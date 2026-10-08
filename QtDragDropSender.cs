using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace YellowInsideLib;

// ──── Qt 메신저 파일 전송 (OLE 드래그앤드롭 시뮬레이션) ─────────────────────
// Qt 채팅창은 WM_DROPFILES를 처리하지 않고 OLE 드롭(IDropTarget)만 받는다.
// 탐색기가 만드는 것과 같은 셸 IDataObject를 만들어 실제 마우스 드래그와 같은 순서로 드롭한다.
//   1. 채팅창 위에 거의 투명한 시작 창을 띄우고 그 위에서 왼쪽 버튼을 누른다 (채팅창에는 클릭이 전달되지 않는다)
//   2. DoDragDrop을 시작한다. 드래그 루프는 마우스 입력이 있을 때만 돌기 때문에 시작 창 안에서 1px씩 움직여 깨운다
//   3. 드롭 위치로 한 번에 이동하고, 채팅창이 드롭을 받겠다고 응답하면 버튼을 뗀다
//   4. 커서를 원래 위치로 돌려놓는다
static class QtDragDropSender
{
    const string SourceWindowClassName = "DcconDragSourceWnd";
    const int SourceWindowSize = 24;
    const byte SourceWindowAlpha = 1;          // 0이면 클릭이 창을 통과하므로 보이지 않는 최소값을 쓴다
    const uint DropEffectCopy = 1;             // DROPEFFECT_COPY
    const int UserInterferenceThreshold = 4;   // 예상 위치에서 이보다 멀어졌으면 사용자가 마우스를 움직인 것으로 본다
    const int ErrorClassAlreadyExists = 1410;
    const string UserMovedMouseReason = "사용자가 마우스를 움직임";
    const string DragEndedEarlyReason = "드래그가 먼저 끝남";

    // 채팅창 메시지 목록 영역의 드롭 후보 위치 (클라이언트 크기 대비 비율, 앞쪽부터 시도)
    static readonly (double X, double Y)[] s_dropPointRatios = [(0.5, 0.45), (0.5, 0.3), (0.7, 0.45), (0.3, 0.45), (0.5, 0.6), (0.7, 0.3), (0.3, 0.3), (0.7, 0.6), (0.3, 0.6)];
    static readonly Guid s_bindHandlerDataObject = new("B8C0BD9F-ED24-455C-83E6-D5390C4FE8C4");   // BHID_DataObject
    static readonly Guid s_dataObjectInterfaceId = new("0000010E-0000-0000-C000-000000000046");   // IID_IDataObject
    static readonly TimeSpan s_sendLockTimeout = TimeSpan.FromSeconds(5);
    static readonly TimeSpan s_dropPointWaitTimeout = TimeSpan.FromMilliseconds(300);
    static readonly TimeSpan s_sourceWindowReadyTimeout = TimeSpan.FromMilliseconds(200);
    static readonly TimeSpan s_buttonPressTimeout = TimeSpan.FromMilliseconds(300);
    static readonly TimeSpan s_dragLoopStartTimeout = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan s_acceptTimeout = TimeSpan.FromMilliseconds(800);
    static readonly TimeSpan s_dragTimeout = TimeSpan.FromSeconds(3);
    static readonly TimeSpan s_dragFinishTimeout = TimeSpan.FromSeconds(3);
    static readonly TimeSpan s_dataObjectGracePeriod = TimeSpan.FromMilliseconds(300);
    static readonly Lock s_sendLock = new();

    static int s_isLeftButtonDown;

    // ── 전송 (OLE는 STA 스레드가 필요하므로 전송마다 전용 스레드에서 실행) ──
    public static Task<bool> SendAsync(IntPtr chatWindowHandle, List<string> filePaths, LogSink? logSink)
    {
        var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => completionSource.TrySetResult(Send(chatWindowHandle, filePaths, logSink)))
        {
            IsBackground = true,
            Name = "DcconQtDragDrop",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completionSource.Task;
    }

    static bool Send(IntPtr chatWindowHandle, List<string> filePaths, LogSink? logSink)
    {
        // 커서와 마우스 버튼은 하나뿐이므로 전송을 한 번에 하나씩만 진행한다
        if (!s_sendLock.TryEnter(s_sendLockTimeout))
        {
            logSink?.Error("[전송] OLE 드래그앤드롭 실패 — 이전 전송이 끝나지 않음");
            return false;
        }

        // 좌표 계산과 커서 이동을 채팅창과 같은 물리 픽셀 기준으로 맞춘다
        using var dpiScope = ThreadDpiAwarenessScope.EnterForWindow(chatWindowHandle);
        var oleInitializeResult = Win32.OleInitialize(IntPtr.Zero);

        // 1ms 단위로 기다릴 수 있도록 전송하는 동안만 타이머 해상도를 올린다
        Win32.TimeBeginPeriod(1);
        try
        {
            if (oleInitializeResult < 0)
            {
                logSink?.Error($"[전송] OLE 초기화 실패 — HRESULT: 0x{oleInitializeResult:X8}");
                return false;
            }

            return SendCore(chatWindowHandle, filePaths, logSink);
        }
        catch (Exception exception)
        {
            logSink?.Error($"[전송] OLE 드래그앤드롭 중 예외 발생 — 대상: 0x{chatWindowHandle:X8}, {exception.GetType().Name}: {exception.Message}");
            return false;
        }
        finally
        {
            ReleaseLeftButton();
            Win32.TimeEndPeriod(1);
            if (oleInitializeResult >= 0) Win32.OleUninitialize();
            s_sendLock.Exit();
        }
    }

    static bool SendCore(IntPtr chatWindowHandle, List<string> filePaths, LogSink? logSink)
    {
        if (!Win32.IsWindow(chatWindowHandle))
        {
            logSink?.Error($"[전송] OLE 드래그앤드롭 실패 — 대상 창이 유효하지 않음: 0x{chatWindowHandle:X8}");
            return false;
        }

        BringToFront(chatWindowHandle, logSink);
        if (FindVisibleDropPoint(chatWindowHandle) is not { } dropPoint)
        {
            logSink?.Error($"[전송] OLE 드래그앤드롭 실패 — 채팅창의 드롭 위치가 모두 다른 창에 가려짐: 0x{chatWindowHandle:X8}");
            return false;
        }

        var sourceCenter = GetSourceWindowCenter(chatWindowHandle, dropPoint);
        var sourceWindowHandle = CreateSourceWindow(sourceCenter, logSink);
        if (sourceWindowHandle == IntPtr.Zero) return false;

        try
        {
            if (!WaitUntil(() => Win32.WindowFromPoint(sourceCenter) == sourceWindowHandle, s_sourceWindowReadyTimeout))
            {
                logSink?.Error("[전송] OLE 드래그앤드롭 실패 — 시작 창이 다른 창에 가려짐, 마우스 버튼을 누르지 않고 중단");
                return false;
            }

            return DragFiles(sourceWindowHandle, sourceCenter, dropPoint, filePaths, logSink);
        }
        finally { Win32.DestroyWindow(sourceWindowHandle); }
    }

    // ── 드래그 실행 (STA 스레드) ─────────────────────────────────────────────
    static unsafe bool DragFiles(IntPtr sourceWindowHandle, Win32.POINT sourceCenter, Win32.POINT dropPoint, List<string> filePaths, LogSink? logSink)
    {
        var dataObject = CreateShellDataObject(filePaths, out var itemIdLists);
        var dropSource = new DragDropSource(s_dragTimeout);
        var dropSourcePointer = (IntPtr)ComInterfaceMarshaller<IDropSource>.ConvertToUnmanaged(dropSource);
        Win32.GetCursorPos(out var originalCursorPosition);
        var isCursorRestored = false;
        var dragFinished = new ManualResetEventSlim();
        var cursorThread = new Thread(() => DriveCursor(dropSource, sourceCenter, dropPoint, originalCursorPosition, dragFinished, logSink))
        {
            IsBackground = true,
            Name = "DcconQtDragCursor",
        };

        void RestoreCursor()
        {
            if (isCursorRestored) return;
            Win32.SetCursorPos(originalCursorPosition.X, originalCursorPosition.Y);
            isCursorRestored = true;
        }

        try
        {
            Win32.SetCursorPos(sourceCenter.X, sourceCenter.Y);
            var stopwatch = Stopwatch.StartNew();
            PressLeftButton();

            // 드래그 루프는 이 스레드의 버튼 상태를 읽으므로, 버튼 입력을 이 스레드에서 먼저 받아 둔다
            if (!PumpUntilMessage(sourceWindowHandle, Win32.WM_LBUTTONDOWN, s_buttonPressTimeout))
            {
                logSink?.Error("[전송] OLE 드래그 시작 실패 — 마우스 버튼 입력이 시작 창에 도착하지 않음");
                return false;
            }

            cursorThread.Start();
            var dragResult = Win32.DoDragDrop(dataObject, dropSourcePointer, DropEffectCopy, out _);
            var dragElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
            dragFinished.Set();
            ReleaseLeftButton();
            Win32.DestroyWindow(sourceWindowHandle);
            cursorThread.Join();
            RestoreCursor();

            if (dragResult != DragDropSource.DragDropDrop)
            {
                logSink?.Warn($"[전송] OLE 드래그앤드롭 취소 — 사유: {dropSource.EndReason ?? "알 수 없음"}, HRESULT: 0x{dragResult:X8}");
                return false;
            }

            // 대상이 드롭이 끝난 뒤에 데이터를 읽는 경우에 대비해 데이터 개체를 잠시 유지하며 메시지를 처리한다
            PumpMessages(s_dataObjectGracePeriod);
            logSink?.Info($"[전송] ✓ OLE 드래그앤드롭 전송 완료 — {filePaths.Count}개 파일, 버튼 누름부터 드롭까지 {dragElapsedMilliseconds}ms");
            return true;
        }
        finally
        {
            dragFinished.Set();
            ReleaseLeftButton();
            if (cursorThread.IsAlive) cursorThread.Join();
            RestoreCursor();
            ComInterfaceMarshaller<IDropSource>.Free((void*)dropSourcePointer);
            Marshal.Release(dataObject);
            FreeItemIdLists(itemIdLists);
            dragFinished.Dispose();
        }
    }

    // ── 커서 이동 (STA 스레드가 DoDragDrop 안에 있는 동안 별도 스레드에서 실행) ──
    static void DriveCursor(DragDropSource dropSource, Win32.POINT sourceCenter, Win32.POINT dropPoint, Win32.POINT originalCursorPosition, ManualResetEventSlim dragFinished, LogSink? logSink)
    {
        try
        {
            if (MoveCursorToDropPoint(dropSource, sourceCenter, dropPoint, dragFinished) is { } cancelReason) dropSource.RequestCancel(cancelReason);
            ReleaseLeftButton();
            if (dragFinished.Wait(s_dragFinishTimeout)) return;

            // 대상 창이 응답하지 않아 DoDragDrop이 끝나지 않으면 커서라도 원래 위치로 돌려놓는다
            logSink?.Error("[전송] OLE 드래그앤드롭이 끝나지 않음 — 대상 창이 응답하지 않을 수 있음");
            Win32.SetCursorPos(originalCursorPosition.X, originalCursorPosition.Y);
        }
        catch (Exception exception)
        {
            dropSource.RequestCancel("커서 이동 중 예외 발생");
            ReleaseLeftButton();
            logSink?.Error($"[전송] OLE 드래그 커서 이동 중 예외 발생 — {exception.GetType().Name}: {exception.Message}");
        }
    }

    // 드롭해도 되면 null, 아니면 취소 사유를 돌려준다
    static string? MoveCursorToDropPoint(DragDropSource dropSource, Win32.POINT sourceCenter, Win32.POINT dropPoint, ManualResetEventSlim dragFinished)
    {
        // DoDragDrop이 마우스를 캡처하기 전의 이동은 시작 창이 받으므로, 루프가 시작될 때까지 1px 이동을 반복한다
        var lastPosition = sourceCenter;
        var nudgeCount = 0;
        var stopwatch = Stopwatch.StartNew();
        while (!dropSource.HasDragLoopStarted)
        {
            if (dragFinished.IsSet) return DragEndedEarlyReason;
            if (stopwatch.Elapsed > s_dragLoopStartTimeout) return "드래그 루프가 시작되지 않음";
            nudgeCount++;
            if (!TryMoveCursor(ref lastPosition, new Win32.POINT { X = sourceCenter.X + nudgeCount % 2, Y = sourceCenter.Y })) return UserMovedMouseReason;
            Thread.Sleep(1);
        }

        // 채팅창으로 한 번에 이동하면 DragEnter와 DragOver가 이어서 호출된다
        var acceptedCountBeforeMove = dropSource.AcceptedFeedbackCount;
        if (!TryMoveCursor(ref lastPosition, dropPoint)) return UserMovedMouseReason;
        stopwatch.Restart();
        while (dropSource.AcceptedFeedbackCount == acceptedCountBeforeMove)
        {
            if (dragFinished.IsSet) return DragEndedEarlyReason;
            if (stopwatch.Elapsed > s_acceptTimeout) return "채팅창이 드롭을 받지 않음";
            Thread.Sleep(1);
        }

        // 버튼을 떼기 직전에도 사용자가 마우스를 움직였는지 확인한다 (그대로 떼면 엉뚱한 곳에 드롭된다)
        return HasCursorMoved(lastPosition) ? UserMovedMouseReason : null;
    }

    static bool TryMoveCursor(ref Win32.POINT lastPosition, Win32.POINT nextPosition)
    {
        if (HasCursorMoved(lastPosition)) return false;
        Win32.SetCursorPos(nextPosition.X, nextPosition.Y);
        lastPosition = nextPosition;
        return true;
    }

    static bool HasCursorMoved(Win32.POINT expectedPosition) => Win32.GetCursorPos(out var currentPosition) && (Math.Abs(currentPosition.X - expectedPosition.X) > UserInterferenceThreshold || Math.Abs(currentPosition.Y - expectedPosition.Y) > UserInterferenceThreshold);

    // ── 드롭 위치 선택 ───────────────────────────────────────────────────────
    // 후보 위치 중 채팅창이 실제로 보이는 곳을 고른다.
    // 팝업 창이 채팅창 활성화로 닫히는 중일 수 있으므로 잠시 기다린다.
    static Win32.POINT? FindVisibleDropPoint(IntPtr chatWindowHandle)
    {
        if (!Win32.GetClientRect(chatWindowHandle, out var clientRect) || clientRect.Width <= 0 || clientRect.Height <= 0) return null;

        Win32.POINT? visibleDropPoint = null;
        WaitUntil(() =>
        {
            visibleDropPoint = FindVisibleDropPointCandidate(chatWindowHandle, clientRect);
            return visibleDropPoint is not null;
        }, s_dropPointWaitTimeout);
        return visibleDropPoint;
    }

    static Win32.POINT? FindVisibleDropPointCandidate(IntPtr chatWindowHandle, Win32.RECT clientRect)
    {
        foreach (var (ratioX, ratioY) in s_dropPointRatios)
        {
            var point = new Win32.POINT { X = (int)(clientRect.Width * ratioX), Y = (int)(clientRect.Height * ratioY) };
            if (!Win32.ClientToScreen(chatWindowHandle, ref point)) return null;
            if (Win32.GetAncestor(Win32.WindowFromPoint(point), Win32.GA_ROOT) == chatWindowHandle) return point;
        }

        return null;
    }

    // 시작 창은 드롭 위치에서 채팅창 가운데 쪽으로 떨어진 곳에 둔다 (최상위 창이라 채팅창과 겹쳐도 된다)
    static Win32.POINT GetSourceWindowCenter(IntPtr chatWindowHandle, Win32.POINT dropPoint)
    {
        Win32.GetWindowRect(chatWindowHandle, out var windowRect);
        var offset = Math.Max(SourceWindowSize * 2, windowRect.Width / 6);
        var direction = dropPoint.X >= windowRect.Left + windowRect.Width / 2 ? -1 : 1;
        return new Win32.POINT { X = dropPoint.X + offset * direction, Y = dropPoint.Y };
    }

    // ── 채팅창 / 시작 창 준비 ────────────────────────────────────────────────
    static void BringToFront(IntPtr chatWindowHandle, LogSink? logSink)
    {
        if (Win32.IsIconic(chatWindowHandle)) Win32.ShowWindow(chatWindowHandle, Win32.SW_RESTORE);
        const uint flags = Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW;
        Win32.SetWindowPos(chatWindowHandle, Win32.HWND_TOPMOST, 0, 0, 0, 0, flags);
        Win32.SetWindowPos(chatWindowHandle, Win32.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
        if (!Win32.SetForegroundWindow(chatWindowHandle)) logSink?.Warn($"[전송] SetForegroundWindow 실패 — 대상: 0x{chatWindowHandle:X8}");
    }

    static IntPtr CreateSourceWindow(Win32.POINT center, LogSink? logSink)
    {
        if (!RegisterSourceWindowClass(logSink)) return IntPtr.Zero;

        const uint extendedStyle = Win32.WS_EX_TOPMOST | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_LAYERED;
        var sourceWindowHandle = Win32.CreateWindowEx(extendedStyle, SourceWindowClassName, "", Win32.WS_POPUP, center.X - SourceWindowSize / 2, center.Y - SourceWindowSize / 2, SourceWindowSize, SourceWindowSize, IntPtr.Zero, IntPtr.Zero, Win32.GetModuleHandle(IntPtr.Zero), IntPtr.Zero);
        if (sourceWindowHandle == IntPtr.Zero)
        {
            logSink?.Error($"[전송] OLE 드래그 시작 창 생성 실패 — Win32 error: {Marshal.GetLastWin32Error()}");
            return IntPtr.Zero;
        }

        Win32.SetLayeredWindowAttributes(sourceWindowHandle, 0, SourceWindowAlpha, Win32.LWA_ALPHA);
        Win32.ShowWindow(sourceWindowHandle, Win32.SW_SHOWNOACTIVATE);
        return sourceWindowHandle;
    }

    // 시작 창은 메시지를 직접 처리할 필요가 없으므로 DefWindowProcW를 창 프로시저로 쓴다
    static unsafe bool RegisterSourceWindowClass(LogSink? logSink)
    {
        fixed (char* classNamePointer = SourceWindowClassName)
        {
            var windowClass = new Win32.WNDCLASSEX
            {
                cbSize = (uint)sizeof(Win32.WNDCLASSEX),
                lpfnWndProc = Win32.GetProcAddress(Win32.GetModuleHandle("user32.dll"), "DefWindowProcW"),
                hInstance = Win32.GetModuleHandle(IntPtr.Zero),
                hbrBackground = Win32.COLOR_WINDOW + 1,
                lpszClassName = classNamePointer,
            };
            if (Win32.RegisterClassEx(in windowClass) != 0) return true;
        }

        var win32Error = Marshal.GetLastWin32Error();
        if (win32Error == ErrorClassAlreadyExists) return true;
        logSink?.Error($"[전송] OLE 드래그 시작 창 클래스 등록 실패 — Win32 error: {win32Error}");
        return false;
    }

    // ── 셸 데이터 개체 ───────────────────────────────────────────────────────
    // 탐색기에서 여러 파일을 끌 때와 같은 IDataObject(CF_HDROP, Shell IDList Array 등)를 만든다
    static unsafe IntPtr CreateShellDataObject(List<string> filePaths, out IntPtr[] itemIdLists)
    {
        itemIdLists = new IntPtr[filePaths.Count];
        try
        {
            for (var index = 0; index < filePaths.Count; index++) Marshal.ThrowExceptionForHR(Win32.SHParseDisplayName(filePaths[index], IntPtr.Zero, out itemIdLists[index], 0, out _));

            IntPtr shellItemArray;
            fixed (IntPtr* itemIdListPointer = itemIdLists) Marshal.ThrowExceptionForHR(Win32.SHCreateShellItemArrayFromIDLists((uint)itemIdLists.Length, itemIdListPointer, out shellItemArray));

            try
            {
                var bindHandlerDataObject = s_bindHandlerDataObject;
                var dataObjectInterfaceId = s_dataObjectInterfaceId;
                IntPtr dataObject;

                // IShellItemArray::BindToHandler (vtable 3번)
                var bindToHandler = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, Guid*, IntPtr*, int>)(*(void***)shellItemArray)[3];
                Marshal.ThrowExceptionForHR(bindToHandler(shellItemArray, IntPtr.Zero, &bindHandlerDataObject, &dataObjectInterfaceId, &dataObject));
                return dataObject;
            }
            finally { Marshal.Release(shellItemArray); }
        }
        catch
        {
            FreeItemIdLists(itemIdLists);
            throw;
        }
    }

    static void FreeItemIdLists(IntPtr[] itemIdLists)
    {
        foreach (var itemIdList in itemIdLists)
        {
            Marshal.FreeCoTaskMem(itemIdList);
        }
    }

    // ── 마우스 버튼 (누른 버튼은 어떤 경로로 끝나든 한 번만 뗀다) ────────────
    static void PressLeftButton()
    {
        Interlocked.Exchange(ref s_isLeftButtonDown, 1);
        SendMouseButton(isButtonDown: true);
    }

    static void ReleaseLeftButton()
    {
        if (Interlocked.Exchange(ref s_isLeftButtonDown, 0) == 0) return;
        SendMouseButton(isButtonDown: false);
    }

    // SendInput은 물리 버튼 기준이므로, 버튼을 바꿔 쓰는 설정이면 오른쪽 버튼 입력이 왼쪽 버튼으로 동작한다
    static unsafe void SendMouseButton(bool isButtonDown)
    {
        var isButtonSwapped = Win32.GetSystemMetrics(Win32.SM_SWAPBUTTON) != 0;
        var flags = isButtonSwapped ? (isButtonDown ? Win32.MOUSEEVENTF_RIGHTDOWN : Win32.MOUSEEVENTF_RIGHTUP) : (isButtonDown ? Win32.MOUSEEVENTF_LEFTDOWN : Win32.MOUSEEVENTF_LEFTUP);
        var input = new Win32.INPUT { Type = Win32.INPUT_MOUSE, Data = new Win32.InputData { Mouse = new Win32.MOUSEINPUT { Flags = flags } } };
        Win32.SendInput(1, &input, sizeof(Win32.INPUT));
    }

    // ── 메시지 처리 ──────────────────────────────────────────────────────────
    static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > timeout) return false;
            PumpMessages(TimeSpan.FromMilliseconds(5));
        }

        return true;
    }

    static bool PumpUntilMessage(IntPtr hwnd, uint messageId, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        var isReceived = false;
        while (stopwatch.Elapsed < timeout)
        {
            while (Win32.PeekMessage(out var message, IntPtr.Zero, 0, 0, Win32.PM_REMOVE))
            {
                if (message.hwnd == hwnd && message.message == messageId) isReceived = true;
                Win32.TranslateMessage(in message);
                Win32.DispatchMessage(in message);
            }

            if (isReceived) return true;
            Thread.Sleep(1);
        }

        return false;
    }

    static void PumpMessages(TimeSpan duration)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < duration)
        {
            while (Win32.PeekMessage(out var message, IntPtr.Zero, 0, 0, Win32.PM_REMOVE))
            {
                Win32.TranslateMessage(in message);
                Win32.DispatchMessage(in message);
            }

            Thread.Sleep(1);
        }
    }
}

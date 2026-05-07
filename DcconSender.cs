using System.Runtime.InteropServices;

namespace YellowInsideLib;

// ──── 디시콘 이미지 자동 전송 (드래그앤드롭 시뮬레이션 + 클립보드 폴백) ─────────
static class DcconSender
{
    const int ClipboardOpenMaxRetry = 5;
    const int ClipboardOpenRetryDelayMilliseconds = 30;
    const int ClipboardFileWaitTimeoutMilliseconds = 1000;
    const int ClipboardChangePollDelayMilliseconds = 20;
    const int PasteBeforeEnterDelayMilliseconds = 10;
    const uint ClipboardDropEffectCopy = 1;

    static readonly uint s_preferredDropEffectClipboardFormat = Win32.RegisterClipboardFormat("Preferred DropEffect");
    static readonly uint s_fileNameWideClipboardFormat = Win32.RegisterClipboardFormat("FileNameW");

    sealed record ClipboardFileWaitContext(
        ManualResetEventSlim ClipboardFileDetectedSignal,
        CancellationTokenSource CancellationTokenSource,
        Task MonitoringTask);

    sealed record ClipboardFileDataEntry(uint Format, IntPtr Handle, string FormatName, bool IsRequired);

    // ── Public API (하위 호환) ────────────────────────────────────────────────

    public static Task<bool> SendDcconAsync(IntPtr chatWindowHandle, string filePath, Action<string>? log = null) =>
        SendDcconAsync(chatWindowHandle, filePath, SendMethod.Auto, new LogSink(InfoLog: log, WarnLog: log, ErrorLog: log));

    public static Task<bool> SendMultipleDcconsAsync(IntPtr chatWindowHandle, IEnumerable<string> filePaths, Action<string>? log = null) =>
        SendMultipleDcconsAsync(chatWindowHandle, filePaths, SendMethod.Auto, new LogSink(InfoLog: log, WarnLog: log, ErrorLog: log));

    // ── Public API (SendMethod 지정) ─────────────────────────────────────────

    public static async Task<bool> SendDcconAsync(IntPtr chatWindowHandle, string filePath, SendMethod sendMethod, LogSink? logSink = null)
    {
        string fullPath = Path.GetFullPath(filePath);
        logSink?.Info($"[전송] 디시콘 전송 시작 — 대상: 0x{chatWindowHandle:X8}, 방식: {sendMethod}, 파일: {Path.GetFileName(filePath)}, 전체 경로: {fullPath}");

        if (!File.Exists(filePath))
        {
            logSink?.Error($"[전송] 디시콘 이미지 파일 없음 — 경로: {fullPath}");
            return false;
        }

        return await SendFilesAsync(chatWindowHandle, [fullPath], sendMethod, logSink);
    }

    public static async Task<bool> SendMultipleDcconsAsync(IntPtr chatWindowHandle, IEnumerable<string> filePaths, SendMethod sendMethod, LogSink? logSink = null)
    {
        var validFullPaths = new List<string>();
        int totalCount = 0;
        int skippedCount = 0;

        foreach (var filePath in filePaths)
        {
            totalCount++;
            if (!File.Exists(filePath))
            {
                skippedCount++;
                logSink?.Error($"[전송] 디시콘 이미지 파일 없음 — 경로: {Path.GetFullPath(filePath)}");
                continue;
            }
            validFullPaths.Add(Path.GetFullPath(filePath));
        }

        logSink?.Info($"[전송] 다중 디시콘 전송 시작 — 대상: 0x{chatWindowHandle:X8}, 방식: {sendMethod}, 전체: {totalCount}개, 유효: {validFullPaths.Count}개, 누락: {skippedCount}개");

        if (validFullPaths.Count == 0)
        {
            logSink?.Error($"[전송] 전송할 유효한 디시콘 파일 없음 — 입력된 {totalCount}개 파일 모두 존재하지 않음");
            return false;
        }

        return await SendFilesAsync(chatWindowHandle, validFullPaths, sendMethod, logSink);
    }

    // ── 전송 방식 분기 ───────────────────────────────────────────────────────

    static async Task<bool> SendFilesAsync(IntPtr chatWindowHandle, List<string> filePaths, SendMethod sendMethod, LogSink? logSink)
    {
        switch (sendMethod)
        {
            case SendMethod.DropFiles:
                return await SendViaDropFilesAsync(chatWindowHandle, filePaths, logSink);

            case SendMethod.Clipboard:
                return await SendViaClipboardAsync(chatWindowHandle, filePaths, logSink);

            case SendMethod.Auto:
                if (!await SendViaDropFilesAsync(chatWindowHandle, filePaths, logSink))
                {
                    logSink?.Info($"[전송] WM_DROPFILES 실패 → 클립보드 방식으로 재시도 — 대상: 0x{chatWindowHandle:X8}");
                    return await SendViaClipboardAsync(chatWindowHandle, filePaths, logSink);
                }
                return true;

            default:
                return false;
        }
    }

    // ── WM_DROPFILES 방식 (기존 로직) ────────────────────────────────────────

    static async Task<bool> SendViaDropFilesAsync(IntPtr chatWindowHandle, List<string> filePaths, LogSink? logSink)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (!Win32.IsWindow(chatWindowHandle))
                {
                    logSink?.Error($"[전송] WM_DROPFILES 전송 실패 — 대상 창이 유효하지 않음: 0x{chatWindowHandle:X8}");
                    return false;
                }

                Win32.ChangeWindowMessageFilter(Win32.WM_DROPFILES, Win32.MSGFLT_ADD);
                Win32.ChangeWindowMessageFilter(Win32.WM_COPYGLOBALDATA, Win32.MSGFLT_ADD);

                IntPtr dropFilesHandle = BuildDropFilesMemory(filePaths);
                if (dropFilesHandle == IntPtr.Zero)
                {
                    logSink?.Error($"[전송] DROPFILES 메모리 할당 실패 — GlobalAlloc 반환값 null, 파일 수: {filePaths.Count}개");
                    return false;
                }

                if (!Win32.PostMessage(chatWindowHandle, Win32.WM_DROPFILES, dropFilesHandle, IntPtr.Zero))
                {
                    int win32Error = Marshal.GetLastWin32Error();
                    Win32.GlobalFree(dropFilesHandle);
                    logSink?.Error($"[전송] WM_DROPFILES 전송 실패 — PostMessage 실패, 대상: 0x{chatWindowHandle:X8} (Win32 error: {win32Error})");
                    return false;
                }

                logSink?.Info($"[전송] ✓ WM_DROPFILES 전송 완료 — {filePaths.Count}개 파일, 대상: 0x{chatWindowHandle:X8}");
                return true;
            }
            catch (Exception exception)
            {
                logSink?.Error($"[전송] WM_DROPFILES 전송 중 예외 발생 — 대상: 0x{chatWindowHandle:X8}, {exception.GetType().Name}: {exception.Message}");
                return false;
            }
        });
    }

    // ── 클립보드 방식 (파일 클립보드 포맷 + Ctrl+V + Enter) ─────────────────

    static async Task<bool> SendViaClipboardAsync(IntPtr chatWindowHandle, List<string> filePaths, LogSink? logSink)
    {
        return await Task.Run(() =>
        {
            List<(uint Format, IntPtr Handle)> savedClipboard = [];
            List<ClipboardFileDataEntry> clipboardFileDataEntries = [];

            try
            {
                logSink?.Info($"[전송] 클립보드 방식 전송 시도 — 대상: 0x{chatWindowHandle:X8}, {filePaths.Count}개 파일");

                if (!Win32.IsWindow(chatWindowHandle))
                {
                    logSink?.Error($"[전송] 클립보드 전송 실패 — 대상 창이 유효하지 않음: 0x{chatWindowHandle:X8}");
                    return false;
                }

                // 1. 기존 클립보드 내용 백업
                savedClipboard = SaveClipboard(logSink);

                // 2. Explorer 파일 복사와 유사한 클립보드 데이터 구성
                clipboardFileDataEntries = BuildClipboardFileDataEntries(filePaths, logSink);
                if (clipboardFileDataEntries.Count == 0)
                {
                    logSink?.Error($"[전송] 클립보드 방식 DROPFILES 메모리 할당 실패 — GlobalAlloc 반환값 null, 파일 수: {filePaths.Count}개");
                    RestoreClipboard(savedClipboard, logSink);
                    return false;
                }

                var clipboardSequenceNumberBeforeClipboardSet = Win32.GetClipboardSequenceNumber();
                var clipboardFileWaitContext = StartClipboardFileWait(clipboardSequenceNumberBeforeClipboardSet);
                try
                {
                    if (!OpenClipboardWithRetry(logSink))
                    {
                        FreeClipboardFileDataEntries(clipboardFileDataEntries);
                        logSink?.Error($"[전송] 클립보드 열기 실패 — {ClipboardOpenMaxRetry}회 재시도 후에도 실패");
                        RestoreClipboard(savedClipboard, logSink);
                        return false;
                    }

                    var clipboardSetSuccessfully = false;
                    try
                    {
                        Win32.EmptyClipboard();
                        clipboardSetSuccessfully = SetClipboardFileData(clipboardFileDataEntries, logSink);
                    }
                    finally
                    {
                        Win32.CloseClipboard();
                    }

                    if (!clipboardSetSuccessfully)
                    {
                        FreeClipboardFileDataEntries(clipboardFileDataEntries);
                        RestoreClipboard(savedClipboard, logSink);
                        return false;
                    }
                    clipboardFileDataEntries = []; // SetClipboardData 성공 → OS가 핸들 소유권을 가져감

                    WaitForClipboardFileReadyOrTimeout(clipboardFileWaitContext, logSink);

                    // 3. 대상 창 활성화 → 붙여넣기
                    logSink?.Info($"[전송] 클립보드 준비 완료, 입력 시뮬레이션 시작 — SetForegroundWindow → Ctrl+V → {PasteBeforeEnterDelayMilliseconds}ms → Enter");

                    if (!Win32.SetForegroundWindow(chatWindowHandle)) logSink?.Warn($"[전송] SetForegroundWindow 실패 — 대상: 0x{chatWindowHandle:X8}, 입력이 다른 창으로 전달될 수 있음");

                    Thread.Sleep(10);
                    SimulateCtrlV();
                    Thread.Sleep(PasteBeforeEnterDelayMilliseconds);
                    SimulateKeyPress(Win32.VK_RETURN);
                    Thread.Sleep(100);
                }
                finally
                {
                    StopClipboardFileWait(clipboardFileWaitContext);
                }

                logSink?.Info($"[전송] ✓ 클립보드 방식 전송 완료 — {filePaths.Count}개 파일, 대상: 0x{chatWindowHandle:X8}");

                // 4. 클립보드 복원
                RestoreClipboard(savedClipboard, logSink);
                savedClipboard = []; // 복원 완료 → 이중 해제 방지
                return true;
            }
            catch (Exception exception)
            {
                logSink?.Error($"[전송] 클립보드 전송 중 예외 발생 — 대상: 0x{chatWindowHandle:X8}, {exception.GetType().Name}: {exception.Message}");
                FreeClipboardFileDataEntries(clipboardFileDataEntries);
                RestoreClipboard(savedClipboard, logSink);
                savedClipboard = [];
                return false;
            }
        });
    }

    // ── 클립보드 열기 재시도 ──────────────────────────────────────────────────

    static bool OpenClipboardWithRetry(LogSink? logSink)
    {
        for (int attempt = 0; attempt < ClipboardOpenMaxRetry; attempt++)
        {
            if (Win32.OpenClipboard(IntPtr.Zero)) return true;

            int win32Error = Marshal.GetLastWin32Error();
            logSink?.Warn($"[전송] 클립보드 열기 실패 (시도 {attempt + 1}/{ClipboardOpenMaxRetry}) — Win32 error: {win32Error}");
            Thread.Sleep(ClipboardOpenRetryDelayMilliseconds * (attempt + 1));
        }
        return false;
    }

    static ClipboardFileWaitContext StartClipboardFileWait(uint initialClipboardSequenceNumber)
    {
        var clipboardFileDetectedSignal = new ManualResetEventSlim(false);
        var cancellationTokenSource = new CancellationTokenSource();
        var monitoringTask = Task.Run(() => MonitorClipboardForFile(initialClipboardSequenceNumber, clipboardFileDetectedSignal, cancellationTokenSource.Token), cancellationTokenSource.Token);
        return new ClipboardFileWaitContext(clipboardFileDetectedSignal, cancellationTokenSource, monitoringTask);
    }

    static void MonitorClipboardForFile(uint initialClipboardSequenceNumber, ManualResetEventSlim clipboardFileDetectedSignal, CancellationToken cancellationToken)
    {
        var lastObservedClipboardSequenceNumber = initialClipboardSequenceNumber;

        while (!cancellationToken.IsCancellationRequested && !clipboardFileDetectedSignal.IsSet)
        {
            var currentClipboardSequenceNumber = Win32.GetClipboardSequenceNumber();
            if (currentClipboardSequenceNumber != lastObservedClipboardSequenceNumber)
            {
                lastObservedClipboardSequenceNumber = currentClipboardSequenceNumber;
                if (HasFileClipboardFormat())
                {
                    clipboardFileDetectedSignal.Set();
                    return;
                }
            }

            cancellationToken.WaitHandle.WaitOne(ClipboardChangePollDelayMilliseconds);
        }
    }

    static void WaitForClipboardFileReadyOrTimeout(ClipboardFileWaitContext clipboardFileWaitContext, LogSink? logSink)
    {
        var waitStartTickCount = Environment.TickCount64;
        if (clipboardFileWaitContext.ClipboardFileDetectedSignal.Wait(ClipboardFileWaitTimeoutMilliseconds))
        {
            logSink?.Info($"[전송] 클립보드 준비 감지 — {Environment.TickCount64 - waitStartTickCount}ms 대기 후 붙여넣기 진행");
            return;
        }

        logSink?.Warn($"[전송] 클립보드 준비 감지 시간 초과 — {ClipboardFileWaitTimeoutMilliseconds}ms 후 강제 붙여넣기 진행");
    }

    static void StopClipboardFileWait(ClipboardFileWaitContext clipboardFileWaitContext)
    {
        clipboardFileWaitContext.CancellationTokenSource.Cancel();

        try
        {
            clipboardFileWaitContext.MonitoringTask.Wait();
        }
        catch (AggregateException aggregateException) when (ContainsOnlyCancellationException(aggregateException)) { }
        finally
        {
            clipboardFileWaitContext.CancellationTokenSource.Dispose();
            clipboardFileWaitContext.ClipboardFileDetectedSignal.Dispose();
        }
    }

    static bool ContainsOnlyCancellationException(AggregateException aggregateException)
    {
        foreach (Exception innerException in aggregateException.InnerExceptions)
        {
            if (innerException is not TaskCanceledException && innerException is not OperationCanceledException)
                return false;
        }
        return true;
    }

    static bool HasFileClipboardFormat() => Win32.IsClipboardFormatAvailable(Win32.CF_HDROP);

    // ── 파일 클립보드 데이터 구성 ────────────────────────────────────────────

    static List<ClipboardFileDataEntry> BuildClipboardFileDataEntries(List<string> filePaths, LogSink? logSink)
    {
        var clipboardFileDataEntries = new List<ClipboardFileDataEntry>();
        var dropFilesHandle = BuildDropFilesMemory(filePaths);
        if (dropFilesHandle == IntPtr.Zero) return clipboardFileDataEntries;

        clipboardFileDataEntries.Add(new ClipboardFileDataEntry(Win32.CF_HDROP, dropFilesHandle, "CF_HDROP", IsRequired: true));
        AddOptionalClipboardFileDataEntry(clipboardFileDataEntries, s_preferredDropEffectClipboardFormat, "Preferred DropEffect", BuildDropEffectMemory, logSink);

        if (filePaths.Count != 1) return clipboardFileDataEntries;

        AddOptionalClipboardFileDataEntry(clipboardFileDataEntries, s_fileNameWideClipboardFormat, "FileNameW", () => BuildUnicodeStringMemory(filePaths[0]), logSink);
        return clipboardFileDataEntries;
    }

    static void AddOptionalClipboardFileDataEntry(
        List<ClipboardFileDataEntry> clipboardFileDataEntries,
        uint clipboardFormat,
        string clipboardFormatName,
        Func<IntPtr> buildClipboardHandle,
        LogSink? logSink)
    {
        if (clipboardFormat == 0)
        {
            logSink?.Warn($"[전송] 선택 클립보드 포맷 등록 실패 — {clipboardFormatName}, 확장자 보존 힌트가 약해질 수 있음");
            return;
        }

        var clipboardHandle = buildClipboardHandle();
        if (clipboardHandle == IntPtr.Zero)
        {
            logSink?.Warn($"[전송] 선택 클립보드 데이터 구성 실패 — {clipboardFormatName}, 확장자 보존 힌트가 약해질 수 있음");
            return;
        }

        clipboardFileDataEntries.Add(new ClipboardFileDataEntry(clipboardFormat, clipboardHandle, clipboardFormatName, IsRequired: false));
    }

    static bool SetClipboardFileData(List<ClipboardFileDataEntry> clipboardFileDataEntries, LogSink? logSink)
    {
        for (var entryIndex = 0; entryIndex < clipboardFileDataEntries.Count; entryIndex++)
        {
            var clipboardFileDataEntry = clipboardFileDataEntries[entryIndex];
            if (clipboardFileDataEntry.Handle == IntPtr.Zero) continue;

            if (Win32.SetClipboardData(clipboardFileDataEntry.Format, clipboardFileDataEntry.Handle) != IntPtr.Zero)
            {
                clipboardFileDataEntries[entryIndex] = clipboardFileDataEntry with { Handle = IntPtr.Zero };
                continue;
            }

            var win32Error = Marshal.GetLastWin32Error();
            Win32.GlobalFree(clipboardFileDataEntry.Handle);
            clipboardFileDataEntries[entryIndex] = clipboardFileDataEntry with { Handle = IntPtr.Zero };

            if (clipboardFileDataEntry.IsRequired)
            {
                logSink?.Error($"[전송] 클립보드 데이터 설정 실패 — SetClipboardData({clipboardFileDataEntry.FormatName}) 반환값 null (Win32 error: {win32Error})");
                return false;
            }

            logSink?.Warn($"[전송] 선택 클립보드 포맷 설정 실패 — {clipboardFileDataEntry.FormatName}, 확장자 보존 힌트가 약해질 수 있음 (Win32 error: {win32Error})");
        }

        return true;
    }

    static void FreeClipboardFileDataEntries(List<ClipboardFileDataEntry> clipboardFileDataEntries)
    {
        for (var entryIndex = 0; entryIndex < clipboardFileDataEntries.Count; entryIndex++)
        {
            var clipboardFileDataEntry = clipboardFileDataEntries[entryIndex];
            if (clipboardFileDataEntry.Handle == IntPtr.Zero) continue;
            Win32.GlobalFree(clipboardFileDataEntry.Handle);
            clipboardFileDataEntries[entryIndex] = clipboardFileDataEntry with { Handle = IntPtr.Zero };
        }
    }

    // ── 클립보드 백업 / 복원 ─────────────────────────────────────────────────

    static unsafe List<(uint Format, IntPtr Handle)> SaveClipboard(LogSink? logSink)
    {
        var entries = new List<(uint Format, IntPtr Handle)>();
        if (!Win32.OpenClipboard(IntPtr.Zero)) return entries;

        try
        {
            uint format = 0;
            while ((format = Win32.EnumClipboardFormats(format)) != 0)
            {
                try
                {
                    IntPtr data = Win32.GetClipboardData(format);
                    if (data == IntPtr.Zero) continue;

                    nuint size = Win32.GlobalSize(data);
                    if (size == 0) continue;

                    IntPtr sourcePointer = Win32.GlobalLock(data);
                    if (sourcePointer == IntPtr.Zero) continue;

                    try
                    {
                        IntPtr copy = Win32.GlobalAlloc(Win32.GHND, size);
                        if (copy == IntPtr.Zero) continue;

                        IntPtr destinationPointer = Win32.GlobalLock(copy);
                        if (destinationPointer == IntPtr.Zero)
                        {
                            Win32.GlobalFree(copy);
                            continue;
                        }

                        try
                        {
                            Buffer.MemoryCopy(
                                sourcePointer.ToPointer(), destinationPointer.ToPointer(),
                                (long)size, (long)size);
                            entries.Add((format, copy));
                        }
                        finally
                        {
                            Win32.GlobalUnlock(copy);
                        }
                    }
                    finally
                    {
                        Win32.GlobalUnlock(data);
                    }
                }
                catch
                {
                    // GDI 핸들 등 HGLOBAL이 아닌 포맷은 건너뜀
                }
            }
        }
        finally
        {
            Win32.CloseClipboard();
        }

        logSink?.Info($"[전송] 클립보드 백업 완료 — {entries.Count}개 포맷 저장");
        return entries;
    }

    static void RestoreClipboard(List<(uint Format, IntPtr Handle)> entries, LogSink? logSink)
    {
        if (entries.Count == 0) return;

        if (!Win32.OpenClipboard(IntPtr.Zero))
        {
            int win32Error = Marshal.GetLastWin32Error();
            logSink?.Warn($"[전송] 클립보드 복원 실패 — OpenClipboard 반환값 false (Win32 error: {win32Error}), 백업 {entries.Count}개 포맷 메모리 해제");
            foreach (var (_, handle) in entries) Win32.GlobalFree(handle);
            return;
        }

        try
        {
            Win32.EmptyClipboard();
            foreach (var (format, handle) in entries)
            {
                if (Win32.SetClipboardData(format, handle) == IntPtr.Zero)
                    Win32.GlobalFree(handle);
                // SetClipboardData 성공 시 OS가 핸들 소유권을 가져감
            }
        }
        finally
        {
            Win32.CloseClipboard();
        }

        logSink?.Info($"[전송] 클립보드 복원 완료 — {entries.Count}개 포맷");
    }

    // ── 입력 시뮬레이션 ──────────────────────────────────────────────────────

    static unsafe void SimulateCtrlV()
    {
        Win32.INPUT* inputs = stackalloc Win32.INPUT[4];
        inputs[0] = CreateKeyInput(Win32.VK_CONTROL, keyUp: false);
        inputs[1] = CreateKeyInput(Win32.VK_V, keyUp: false);
        inputs[2] = CreateKeyInput(Win32.VK_V, keyUp: true);
        inputs[3] = CreateKeyInput(Win32.VK_CONTROL, keyUp: true);
        Win32.SendInput(4, inputs, sizeof(Win32.INPUT));
    }

    static unsafe void SimulateKeyPress(byte virtualKey)
    {
        Win32.INPUT* inputs = stackalloc Win32.INPUT[2];
        inputs[0] = CreateKeyInput(virtualKey, keyUp: false);
        inputs[1] = CreateKeyInput(virtualKey, keyUp: true);
        Win32.SendInput(2, inputs, sizeof(Win32.INPUT));
    }

    static Win32.INPUT CreateKeyInput(byte virtualKey, bool keyUp) => new()
    {
        Type = Win32.INPUT_KEYBOARD,
        Data = new Win32.InputData
        {
            Keyboard = new Win32.KEYBDINPUT
            {
                VirtualKey = virtualKey,
                Flags = keyUp ? Win32.KEYEVENTF_KEYUP : 0,
            },
        },
    };

    // ── DROPFILES 메모리 구성 ────────────────────────────────────────────────

    static unsafe IntPtr BuildDropFilesMemory(List<string> filePaths)
    {
        var headerSize = (uint)sizeof(Win32.DROPFILES);

        var totalPathsByteCount = 0u;
        foreach (var path in filePaths) totalPathsByteCount += (uint)((path.Length + 1) * sizeof(char));
        totalPathsByteCount += sizeof(char); // 이중 null 종료

        var totalSize = headerSize + totalPathsByteCount;

        var handle = Win32.GlobalAlloc(Win32.GHND, totalSize);
        if (handle == IntPtr.Zero) return IntPtr.Zero;

        var lockedPointer = Win32.GlobalLock(handle);
        if (lockedPointer == IntPtr.Zero)
        {
            Win32.GlobalFree(handle);
            return IntPtr.Zero;
        }

        try
        {
            var dropFiles = (Win32.DROPFILES*)lockedPointer;
            dropFiles->pFiles = headerSize;
            dropFiles->pointX = 0;
            dropFiles->pointY = 0;
            dropFiles->fNC = 0;
            dropFiles->fWide = 1;

            var destination = (char*)(lockedPointer + (nint)headerSize);
            foreach (var path in filePaths)
            {
                path.AsSpan().CopyTo(new Span<char>(destination, path.Length));
                destination += path.Length;
                *destination = '\0';
                destination++;
            }
            // GHND = GMEM_ZEROINIT → 이중 null 종료는 자동 처리
        }
        finally
        {
            Win32.GlobalUnlock(handle);
        }

        return handle;
    }

    static unsafe IntPtr BuildDropEffectMemory()
    {
        var handle = Win32.GlobalAlloc(Win32.GHND, (nuint)sizeof(uint));
        if (handle == IntPtr.Zero) return IntPtr.Zero;

        var lockedPointer = Win32.GlobalLock(handle);
        if (lockedPointer == IntPtr.Zero)
        {
            Win32.GlobalFree(handle);
            return IntPtr.Zero;
        }

        try
        {
            *(uint*)lockedPointer = ClipboardDropEffectCopy;
        }
        finally
        {
            Win32.GlobalUnlock(handle);
        }

        return handle;
    }

    static unsafe IntPtr BuildUnicodeStringMemory(string value)
    {
        var byteCount = (nuint)((value.Length + 1) * sizeof(char));
        var handle = Win32.GlobalAlloc(Win32.GHND, byteCount);
        if (handle == IntPtr.Zero) return IntPtr.Zero;

        var lockedPointer = Win32.GlobalLock(handle);
        if (lockedPointer == IntPtr.Zero)
        {
            Win32.GlobalFree(handle);
            return IntPtr.Zero;
        }

        try
        {
            value.AsSpan().CopyTo(new Span<char>(lockedPointer.ToPointer(), value.Length));
        }
        finally
        {
            Win32.GlobalUnlock(handle);
        }

        return handle;
    }
}

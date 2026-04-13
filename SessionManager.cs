using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.InteropServices;

namespace YellowInsideLib;

// ──── 세션 관리자 (싱글톤) ────────────────────────────────────────────────────
public sealed class SessionManager : IDisposable
{
    internal const string ButtonWndClassName = "DcconButtonWnd";

    static SessionManager? s_instance;
    public static SessionManager Instance => s_instance ??= new SessionManager();

    // ── 이벤트 ───────────────────────────────────────────────────────────────
    public event Action<string>? Log;
    public event Action<SessionInfo>? SessionCreated;
    public event Action<SessionInfo>? SessionRemoved;
    public event Action<SessionInfo>? DcconButtonClicked;

    // ── 내부 상태 ────────────────────────────────────────────────────────────
    readonly Lock _lock = new();
    readonly Dictionary<IntPtr, DcconSession> _sessions = [];
    readonly Dictionary<IntPtr, DcconSession> _buttonToSession = [];
    readonly ConcurrentQueue<Action> _pendingActions = new();

    uint    _kakaoTalkPid;
    Image?  _icon;
    uint    _uiThreadId;
    Thread? _uiThread;
    bool    _running;
    ManualResetEventSlim? _startedSignal;

    // GC 방지: 네이티브에서 참조하는 델리게이트는 반드시 필드 유지
    Win32.WndProc? _wndProcDelegate;
    Win32.WinEventProc? _winEventDelegate;
    IntPtr _showDestroyHook;
    IntPtr _locationHook;

    const uint WM_INVOKE = Win32.WM_APP + 1;

    SessionManager() { }

    // ── 서비스 시작 ──────────────────────────────────────────────────────────
    public void Start(string? buttonIconPath = null)
    {
        if (_running) throw new InvalidOperationException("이미 실행 중입니다.");

        bool dpiOk = Win32.SetProcessDpiAwarenessContext(Win32.DPI_CONTEXT_PER_MONITOR_V2);
        RaiseLog($"[DPI]  PerMonitorV2 설정: {(dpiOk ? "OK" : "실패 또는 이미 설정됨")}");

        _kakaoTalkPid = TargetAppHelper.FindProcessId();
        if (_kakaoTalkPid == 0)
            throw new InvalidOperationException("메신저 프로세스를 찾을 수 없습니다.");
        RaiseLog($"[OK]   메신저 PID: {_kakaoTalkPid}");

        _icon = buttonIconPath is not null ? IconHelper.LoadFromFile(buttonIconPath) : null;
        if (_icon != null) RaiseLog("[OK]   아이콘 로드 완료");
        else RaiseLog("[WARN] 아이콘 없음 — 텍스트 폴백 사용");

        _startedSignal = new ManualResetEventSlim(false);
        _uiThread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name         = "DcconUI",
        };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
        _startedSignal.Wait();
        _startedSignal.Dispose();
        _startedSignal = null;

        _running = true;
    }

    // ── 서비스 종료 ──────────────────────────────────────────────────────────
    public void Stop()
    {
        if (!_running) return;
        if (_uiThreadId != 0)
            Win32.PostThreadMessage(_uiThreadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _uiThread?.Join(TimeSpan.FromSeconds(5));
        _running    = false;
        _uiThreadId = 0;
        _uiThread   = null;
    }

    // ── 현재 세션 목록 (버튼 attach 유무 포함) ──────────────────────────────
    public IReadOnlyList<SessionInfo> GetSessions()
    {
        var result = new List<SessionInfo>();
        Win32.EnumWindows((hwnd, _) =>
        {
            if (!IsChatWindow(hwnd)) return true;
            string title = Win32.GetWindowTextString(hwnd);
            bool isAttached;
            lock (_lock) { isAttached = _sessions.ContainsKey(hwnd); }
            result.Add(new SessionInfo(hwnd, title, isAttached));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    // ── Try Attach (버튼 없는 채팅창에 버튼 attach) ─────────────────────────
    public bool TryAttach(IntPtr chatHwnd)
    {
        if (!_running) return false;
        if (Win32.GetCurrentThreadId() == _uiThreadId)
            return TryAttachCore(chatHwnd);

        bool result = false;
        using var done = new ManualResetEventSlim(false);
        _pendingActions.Enqueue(() => { result = TryAttachCore(chatHwnd); done.Set(); });
        Win32.PostThreadMessage(_uiThreadId, WM_INVOKE, IntPtr.Zero, IntPtr.Zero);
        done.Wait();
        return result;
    }

    bool TryAttachCore(IntPtr chatHwnd)
    {
        lock (_lock)
        {
            if (_sessions.ContainsKey(chatHwnd)) return true;
        }
        if (!IsChatWindow(chatHwnd)) return false;
        AttachSession(chatHwnd);
        lock (_lock) { return _sessions.ContainsKey(chatHwnd); }
    }

    // ── 디시콘 전송 ─────────────────────────────────────────────────────────
    public Task SendDcconAsync(IntPtr chatHwnd, string filePath) =>
        DcconSender.SendDcconAsync(chatHwnd, filePath, RaiseLog);

    // ── 로그 이벤트 발생 ─────────────────────────────────────────────────────
    void RaiseLog(string message) => Log?.Invoke(message);

    // ── WndClass 등록 ────────────────────────────────────────────────────────
    unsafe bool RegisterWndClass()
    {
        _wndProcDelegate = ButtonWndProc;
        fixed (char* classNamePointer = ButtonWndClassName)
        {
            var wndClass = new Win32.WNDCLASSEX
            {
                cbSize        = (uint)sizeof(Win32.WNDCLASSEX),
                style         = 0,
                lpfnWndProc   = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                cbClsExtra    = 0,
                cbWndExtra    = 0,
                hInstance     = Win32.GetModuleHandle(IntPtr.Zero),
                hIcon         = IntPtr.Zero,
                hCursor       = Win32.LoadCursor(IntPtr.Zero, Win32.IDC_HAND),
                hbrBackground = IntPtr.Zero,
                lpszMenuName  = IntPtr.Zero,
                lpszClassName = classNamePointer,
                hIconSm       = IntPtr.Zero,
            };
            ushort atom = Win32.RegisterClassEx(in wndClass);
            if (atom == 0)
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 1410)  // ERROR_CLASS_ALREADY_EXISTS
                {
                    RaiseLog($"[ERROR] RegisterClassEx 실패: {error}");
                    return false;
                }
            }
        }
        RaiseLog("[OK]   WndClass 등록 완료");
        return true;
    }

    static void UnregisterWndClass() =>
        Win32.UnregisterClass(ButtonWndClassName, Win32.GetModuleHandle(IntPtr.Zero));

    // ── 채팅창 판별: EVA_Window_Dblclk + 메신저 PID + RICHEDIT50W 보유 ───
    bool IsChatWindow(IntPtr hwnd)
    {
        if (!Win32.IsWindowVisible(hwnd)) return false;
        if (Win32.GetClassNameString(hwnd) != "EVA_Window_Dblclk") return false;
        _ = Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid != _kakaoTalkPid) return false;

        bool hasRichEdit = false;
        Win32.EnumChildWindows(hwnd, (childHwnd, _) =>
        {
            if (Win32.GetClassNameString(childHwnd) == "RICHEDIT50W") { hasRichEdit = true; return false; }
            return true;
        }, IntPtr.Zero);
        return hasRichEdit;
    }

    // ── 시작 시 이미 열린 모든 채팅창에 세션 생성 ────────────────────────────
    void AttachToExistingWindows()
    {
        Win32.EnumWindows((hwnd, _) =>
        {
            if (IsChatWindow(hwnd)) AttachSession(hwnd);
            return true;
        }, IntPtr.Zero);
        int count;
        lock (_lock) { count = _sessions.Count; }
        RaiseLog($"[INFO] 기존 채팅창 {count}개 세션 생성 완료");
    }

    // ── 세션 생성 ────────────────────────────────────────────────────────────
    void AttachSession(IntPtr chatHwnd)
    {
        lock (_lock) { if (_sessions.ContainsKey(chatHwnd)) return; }

        var session = new DcconSession(chatHwnd, RaiseLog) { Clicked = OnSessionButtonClicked };

        if (!session.CreateButton(_icon))
        {
            RaiseLog($"[ERROR] 0x{chatHwnd:X8}: 버튼 생성 실패 ({Marshal.GetLastWin32Error()})");
            return;
        }

        lock (_lock)
        {
            _sessions[chatHwnd] = session;
            _buttonToSession[session.ButtonHwnd] = session;
        }

        var info = CreateSessionInfo(chatHwnd, true);
        RaiseLog($"[OK]   세션 생성: 0x{chatHwnd:X8} '{info.Title}'  버튼=0x{session.ButtonHwnd:X8}");
        SessionCreated?.Invoke(info);
    }

    // ── 세션 제거 ────────────────────────────────────────────────────────────
    void DetachSession(IntPtr chatHwnd)
    {
        DcconSession? session;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(chatHwnd, out session)) return;
            _sessions.Remove(chatHwnd);
            if (session.ButtonHwnd != IntPtr.Zero) _buttonToSession.Remove(session.ButtonHwnd);
        }
        session.DestroyButton();

        int remainingCount;
        lock (_lock) { remainingCount = _sessions.Count; }

        RaiseLog($"[INFO] 세션 제거: 0x{chatHwnd:X8}  남은 세션: {remainingCount}개");
        SessionRemoved?.Invoke(CreateSessionInfo(chatHwnd, false));
    }

    void DetachAll()
    {
        List<IntPtr> keys;
        lock (_lock) { keys = [.. _sessions.Keys]; }
        foreach (var chatHwnd in keys) DetachSession(chatHwnd);
        RaiseLog("[INFO] 모든 세션 제거 완료");
    }

    // ── 버튼 클릭 콜백 ───────────────────────────────────────────────────────
    void OnSessionButtonClicked(DcconSession session)
    {
        var info = CreateSessionInfo(session.ChatHwnd, true);
        RaiseLog($"[EVENT] ★ 디시콘 버튼 클릭!  채팅방: 0x{session.ChatHwnd:X8}  ({DateTime.Now:HH:mm:ss.fff})");
        DcconButtonClicked?.Invoke(info);
    }

    // ── WinEvent 훅 설치/제거 ────────────────────────────────────────────────
    void InstallHooks()
    {
        _winEventDelegate = OnWinEvent;

        // 채팅창 새로 열림(SHOW) / 닫힘(DESTROY) 감지
        _showDestroyHook = Win32.SetWinEventHook(
            Win32.EVENT_OBJECT_DESTROY, Win32.EVENT_OBJECT_SHOW,
            IntPtr.Zero, _winEventDelegate,
            _kakaoTalkPid, 0, Win32.WINEVENT_OUTOFCONTEXT);

        // 채팅창 이동·리사이즈 → 버튼 재배치
        _locationHook = Win32.SetWinEventHook(
            Win32.EVENT_OBJECT_LOCATIONCHANGE, Win32.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _winEventDelegate,
            _kakaoTalkPid, 0, Win32.WINEVENT_OUTOFCONTEXT);

        RaiseLog($"[OK]   훅 설치 showDestroy=0x{_showDestroyHook:X8}  location=0x{_locationHook:X8}");
    }

    void UninstallHooks()
    {
        if (_showDestroyHook != IntPtr.Zero) Win32.UnhookWinEvent(_showDestroyHook);
        if (_locationHook    != IntPtr.Zero) Win32.UnhookWinEvent(_locationHook);
    }

    void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint msEventTime)
    {
        if (hwnd == IntPtr.Zero || idObject != 0) return;  // OBJID_WINDOW = 0

        switch (eventType)
        {
            case Win32.EVENT_OBJECT_SHOW:
                bool exists;
                lock (_lock) { exists = _sessions.ContainsKey(hwnd); }
                if (!exists && IsChatWindow(hwnd)) AttachSession(hwnd);
                break;
            case Win32.EVENT_OBJECT_DESTROY:
                DetachSession(hwnd);
                break;
            case Win32.EVENT_OBJECT_LOCATIONCHANGE:
                DcconSession? session;
                lock (_lock) { _sessions.TryGetValue(hwnd, out session); }
                if (session != null && !Win32.IsIconic(hwnd))
                    session.UpdatePosition(_icon);
                // SHOW 이벤트 타이밍 상 RICHEDIT50W가 아직 없어 세션 미생성된 채팅창 재시도
                else if (session == null && IsChatWindow(hwnd))
                    AttachSession(hwnd);
                break;
        }
    }

    // ── 모든 버튼 창 공유 WndProc ────────────────────────────────────────────
    IntPtr ButtonWndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        DcconSession? session;
        lock (_lock) { _buttonToSession.TryGetValue(hwnd, out session); }
        if (session == null) return Win32.DefWindowProc(hwnd, message, wParam, lParam);

        IntPtr result = session.HandleMessage(hwnd, message, wParam, lParam);
        if (message == Win32.WM_DESTROY)
            lock (_lock) { _buttonToSession.Remove(hwnd); }
        return result;
    }

    // ── 메시지 루프 (UI 스레드) ──────────────────────────────────────────────
    void RunMessageLoop()
    {
        _uiThreadId = Win32.GetCurrentThreadId();

        if (!RegisterWndClass())
        {
            RaiseLog("[ERROR] WndClass 등록 실패. 종료합니다.");
            _startedSignal?.Set();
            return;
        }

        AttachToExistingWindows();
        InstallHooks();
        _startedSignal?.Set();

        RaiseLog("[INFO] 메시지 루프 시작");
        while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_INVOKE)
            {
                while (_pendingActions.TryDequeue(out var action)) action();
                continue;
            }
            Win32.TranslateMessage(in msg);
            Win32.DispatchMessage(in msg);
        }

        UninstallHooks();
        DetachAll();
        UnregisterWndClass();
        RaiseLog("[INFO] 메시지 루프 종료");
    }

    // ── 헬퍼 ─────────────────────────────────────────────────────────────────
    static SessionInfo CreateSessionInfo(IntPtr chatHwnd, bool isButtonAttached)
    {
        string title = Win32.GetWindowTextString(chatHwnd);
        return new SessionInfo(chatHwnd, title, isButtonAttached);
    }

    // ── IDisposable ──────────────────────────────────────────────────────────
    public void Dispose()
    {
        Stop();
        _icon?.Dispose();
    }
}

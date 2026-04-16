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
    public event Action<string>? InfoLog;
    public event Action<string>? WarnLog;
    public event Action<string>? ErrorLog;
    public event Action<SessionInfo>? SessionCreated;
    public event Action<SessionInfo>? SessionRemoved;
    public event Action<SessionInfo>? DcconButtonClicked;
    public event Action? ServiceRestarted;

    // ── 내부 상태 ────────────────────────────────────────────────────────────
    readonly Lock _lock = new();
    readonly LogSink _logSink;
    readonly Dictionary<IntPtr, DcconSession> _sessions = [];
    readonly Dictionary<IntPtr, DcconSession> _buttonToSession = [];
    readonly ConcurrentQueue<Action> _pendingActions = new();

    uint    _kakaoTalkPid;
    Image?  _icon;
    string? _iconPath;
    uint    _uiThreadId;
    Thread? _uiThread;
    bool    _running;
    ManualResetEventSlim? _startedSignal;

    // ── 자동 재시작 관련 ──────────────────────────────────────────────────────
    const int MaxFailuresBeforeRestart = 3;
    const int FailureWindowSeconds = 60;
    int _restarting;  // Interlocked 전용 (0 = 정상, 1 = 재시작 중)
    readonly List<DateTime> _recentFailures = [];
    readonly Lock _failureLock = new();

    // GC 방지: 네이티브에서 참조하는 델리게이트는 반드시 필드 유지
    Win32.WndProc? _wndProcDelegate;
    Win32.WinEventProc? _winEventDelegate;
    IntPtr _showDestroyHook;
    IntPtr _locationHook;
    IntPtr _kakaoWatchTimerId;

    const uint WM_INVOKE            = Win32.WM_APP + 1;
    const uint KakaoWatchIntervalMs = 1000;

    SessionManager()
    {
        _logSink = new LogSink(RaiseInfoLog, RaiseWarnLog, RaiseErrorLog);
    }

    // ── 서비스 시작 ──────────────────────────────────────────────────────────
    public void Start(string? buttonIconPath = null)
    {
        if (_running) throw new InvalidOperationException("이미 실행 중입니다.");

        bool dpiOk = Win32.SetProcessDpiAwarenessContext(Win32.DPI_CONTEXT_PER_MONITOR_V2);
        RaiseInfoLog($"[DPI]  PerMonitorV2 DPI 인식 설정: {(dpiOk ? "성공" : "실패 또는 이미 설정됨")}");
        _kakaoTalkPid = TargetAppHelper.FindProcessId();
        if (_kakaoTalkPid != 0) RaiseInfoLog($"메신저 프로세스 발견 — PID: {_kakaoTalkPid}");
        else RaiseWarnLog("메신저가 실행되어 있지 않습니다 — 실행 후 자동으로 훅을 설치합니다");

        _iconPath = buttonIconPath;
        _icon = buttonIconPath is not null ? IconHelper.LoadFromFile(buttonIconPath) : null;
        if (_icon != null) RaiseInfoLog($"아이콘 로드 완료 — {buttonIconPath}");
        else RaiseWarnLog("아이콘 경로 미지정 — 텍스트 폴백 사용");

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
    public SendMethod SendMethod { get; set; } = SendMethod.Auto;

    public async Task<bool> SendDcconAsync(IntPtr chatHwnd, string filePath)
    {
        bool success = await DcconSender.SendDcconAsync(chatHwnd, filePath, SendMethod, _logSink);
        await HandleSendResultAsync(success);
        return success;
    }

    public async Task<bool> SendMultipleDcconsAsync(IntPtr chatHwnd, IEnumerable<string> filePaths)
    {
        bool success = await DcconSender.SendMultipleDcconsAsync(chatHwnd, filePaths, SendMethod, _logSink);
        await HandleSendResultAsync(success);
        return success;
    }

    // ── 전송 결과 처리 및 자동 재시작 ────────────────────────────────────────

    async Task HandleSendResultAsync(bool success)
    {
        if (success)
        {
            lock (_failureLock) { _recentFailures.Clear(); }
            return;
        }

        bool shouldRestart;
        lock (_failureLock)
        {
            _recentFailures.Add(DateTime.UtcNow);
            var cutoff = DateTime.UtcNow.AddSeconds(-FailureWindowSeconds);
            _recentFailures.RemoveAll(timestamp => timestamp < cutoff);
            shouldRestart = _recentFailures.Count >= MaxFailuresBeforeRestart;
        }

        if (shouldRestart && Interlocked.CompareExchange(ref _restarting, 1, 0) == 0)
        {
            RaiseWarnLog($"[자동복구] {FailureWindowSeconds}초 내 {MaxFailuresBeforeRestart}회 연속 실패 — 서비스 자동 재시작");
            await RestartAsync();
        }
    }

    async Task RestartAsync()
    {
        try
        {
            Stop();
            await Task.Delay(200);
            Start(_iconPath);
            lock (_failureLock) { _recentFailures.Clear(); }
            RaiseInfoLog("[자동복구] ✓ 서비스 재시작 완료");
            ServiceRestarted?.Invoke();
        }
        catch (Exception exception)
        {
            RaiseErrorLog($"[자동복구] 서비스 재시작 실패 — {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _restarting, 0);
        }
    }

    // ── 로그 이벤트 발생 ─────────────────────────────────────────────────────
    void RaiseInfoLog(string message) => InfoLog?.Invoke(message);
    void RaiseWarnLog(string message) => WarnLog?.Invoke(message);
    void RaiseErrorLog(string message) => ErrorLog?.Invoke(message);

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
                    RaiseErrorLog($"RegisterClassEx 실패 — Win32 error: {error}");
                    return false;
                }
            }
        }
        RaiseInfoLog("WndClass 등록 완료");
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
        RaiseInfoLog($"기존 채팅창 {count}개 세션 생성 완료");
    }

    // ── 세션 생성 ────────────────────────────────────────────────────────────
    void AttachSession(IntPtr chatHwnd)
    {
        lock (_lock) { if (_sessions.ContainsKey(chatHwnd)) return; }

        var session = new DcconSession(chatHwnd, _logSink) { Clicked = OnSessionButtonClicked };

        if (!session.CreateButton(_icon))
        {
            int win32Error = Marshal.GetLastWin32Error();
            RaiseErrorLog($"0x{chatHwnd:X8}: 버튼 생성 실패 — Win32 error: {win32Error}");
            return;
        }

        lock (_lock)
        {
            _sessions[chatHwnd] = session;
            _buttonToSession[session.ButtonHwnd] = session;
        }

        var info = CreateSessionInfo(chatHwnd, true);
        RaiseInfoLog($"세션 생성: 0x{chatHwnd:X8} '{info.Title}'  버튼=0x{session.ButtonHwnd:X8}");
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

        RaiseInfoLog($"세션 제거: 0x{chatHwnd:X8}  남은 세션: {remainingCount}개");
        SessionRemoved?.Invoke(CreateSessionInfo(chatHwnd, false));
    }

    void DetachAll()
    {
        List<IntPtr> keys;
        lock (_lock) { keys = [.. _sessions.Keys]; }
        foreach (var chatHwnd in keys) DetachSession(chatHwnd);
        RaiseInfoLog("모든 세션 제거 완료");
    }

    // ── 버튼 클릭 콜백 ───────────────────────────────────────────────────────
    void OnSessionButtonClicked(DcconSession session)
    {
        var info = CreateSessionInfo(session.ChatHwnd, true);
        RaiseInfoLog($"★ 디시콘 버튼 클릭 — 채팅방: 0x{session.ChatHwnd:X8}, 제목: '{info.Title}' ({DateTime.Now:HH:mm:ss.fff})");
        DcconButtonClicked?.Invoke(info);
    }

    // ── 카카오톡 프로세스 감시 (1초 주기 타이머) ─────────────────────────────
    void CheckKakaoTalkProcess()
    {
        uint newPid = TargetAppHelper.FindProcessId();
        if (newPid == _kakaoTalkPid) return;

        if (_kakaoTalkPid != 0)
        {
            UninstallHooks();
            DetachAll();
            _kakaoTalkPid = 0;
            RaiseInfoLog("카카오톡 종료 감지 — 세션 정리 완료");
        }

        if (newPid != 0)
        {
            _kakaoTalkPid = newPid;
            RaiseInfoLog($"카카오톡 재시작 감지 — 새 PID: {_kakaoTalkPid}");
            InstallHooks();
            AttachToExistingWindows();
        }
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

        RaiseInfoLog($"훅 설치 완료 — showDestroy=0x{_showDestroyHook:X8}, location=0x{_locationHook:X8}, 대상 PID: {_kakaoTalkPid}");
    }

    void UninstallHooks()
    {
        if (_showDestroyHook != IntPtr.Zero) { Win32.UnhookWinEvent(_showDestroyHook); _showDestroyHook = IntPtr.Zero; }
        if (_locationHook    != IntPtr.Zero) { Win32.UnhookWinEvent(_locationHook);    _locationHook    = IntPtr.Zero; }
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
            RaiseErrorLog("WndClass 등록 실패 — 메시지 루프를 시작할 수 없습니다");
            _startedSignal?.Set();
            return;
        }

        if (_kakaoTalkPid != 0)
        {
            AttachToExistingWindows();
            InstallHooks();
        }
        _kakaoWatchTimerId = Win32.SetTimer(IntPtr.Zero, IntPtr.Zero, KakaoWatchIntervalMs, IntPtr.Zero);
        _startedSignal?.Set();

        RaiseInfoLog($"메시지 루프 시작 — 스레드: {_uiThreadId}");
        while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_INVOKE)
            {
                while (_pendingActions.TryDequeue(out var action)) action();
                continue;
            }
            if (msg.message == Win32.WM_TIMER && msg.wParam == _kakaoWatchTimerId)
            {
                CheckKakaoTalkProcess();
                continue;
            }
            Win32.TranslateMessage(in msg);
            Win32.DispatchMessage(in msg);
        }

        Win32.KillTimer(IntPtr.Zero, _kakaoWatchTimerId);
        UninstallHooks();
        DetachAll();
        UnregisterWndClass();
        RaiseInfoLog("메시지 루프 종료");
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

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

    uint          _kakaoTalkPid;
    TargetAppKind _targetAppKind;
    Image?  _icon;
    string? _iconPath;
    uint    _uiThreadId;
    Thread? _uiThread;
    bool    _running;
    ManualResetEventSlim? _startedSignal;

    // Qt 클라이언트일 때만 생성 (UIA 측정 담당)
    QtChatWindowTracker? _qtTracker;

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
        IntPtr threadAwarenessContext = Win32.GetThreadDpiAwarenessContext();
        RaiseInfoLog($"[DPI]  현재 스레드 인식: {Win32.DescribeDpiAwarenessContext(threadAwarenessContext)}, 시스템 DPI: {Win32.GetDpiForSystem()}");
        if (Win32.IsGdiScaledAwarenessContext(threadAwarenessContext)) RaiseWarnLog("[DPI]  GDI 스케일링(System (Enhanced)) 모드입니다 — exe 호환성 설정에서 '높은 DPI 설정 재정의'를 해제해야 버튼이 올바르게 그려집니다");
        (_kakaoTalkPid, _targetAppKind) = TargetAppHelper.FindProcess();
        if (_kakaoTalkPid != 0) RaiseInfoLog($"메신저 프로세스 발견 — PID: {_kakaoTalkPid}, 종류: {_targetAppKind}");
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
        if (_uiThreadId != 0) Win32.PostThreadMessage(_uiThreadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
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
            bool isAttached;
            lock (_lock) { isAttached = _sessions.ContainsKey(hwnd); }
            if (!IsChatWindow(hwnd, isAttached)) return true;
            string title = Win32.GetWindowTextString(hwnd);
            result.Add(new SessionInfo(hwnd, title, isAttached));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    // ── Try Attach (버튼 없는 채팅창에 버튼 attach) ─────────────────────────
    public bool TryAttach(IntPtr chatHwnd)
    {
        if (!_running) return false;
        var isUiThread = Win32.GetCurrentThreadId() == _uiThreadId;

        // Qt 채팅창은 UIA 측정이 필요하므로 메시지 루프 스레드 밖에서 먼저 버튼 위치를 측정한다
        var probeResult = !isUiThread && _targetAppKind == TargetAppKind.Qt ? _qtTracker?.ProbeSynchronously(chatHwnd) : null;
        if (isUiThread) return TryAttachCore(chatHwnd, probeResult);

        bool result = false;
        using var done = new ManualResetEventSlim(false);
        PostToUiThread(() =>
        {
            result = TryAttachCore(chatHwnd, probeResult);
            done.Set();
        });
        done.Wait();
        return result;
    }

    bool TryAttachCore(IntPtr chatHwnd, QtChatWindowProbeResult? probeResult)
    {
        lock (_lock)
        {
            if (_sessions.ContainsKey(chatHwnd)) return true;
        }

        if (_targetAppKind == TargetAppKind.Qt)
        {
            if (!QtChatWindow.IsCandidateWindow(chatHwnd, _kakaoTalkPid)) return false;
            if (probeResult is not { Status: QtChatWindowProbeStatus.Succeeded } succeededResult)
            {
                // 동기 측정을 할 수 없었던 경우 비동기 측정 후 자동으로 attach 된다
                _qtTracker?.RequestProbe(chatHwnd);
                return false;
            }
            AttachSession(chatHwnd, new QtButtonLayoutSource(succeededResult.Anchor));
        }
        else
        {
            if (!MfcChatWindow.IsChatWindow(chatHwnd, _kakaoTalkPid)) return false;
            AttachSession(chatHwnd, MfcButtonLayoutSource.Instance);
        }

        lock (_lock) { return _sessions.ContainsKey(chatHwnd); }
    }

    // ── 디시콘 전송 ─────────────────────────────────────────────────────────
    public SendMethod SendMethod { get; set; } = SendMethod.Auto;

    public async Task<bool> SendDcconAsync(IntPtr chatHwnd, string filePath)
    {
        bool success = await DcconSender.SendDcconAsync(chatHwnd, filePath, SendMethod, IsQtMessengerWindow(chatHwnd), _logSink);
        await HandleSendResultAsync(success);
        return success;
    }

    public async Task<bool> SendMultipleDcconsAsync(IntPtr chatHwnd, IEnumerable<string> filePaths)
    {
        bool success = await DcconSender.SendMultipleDcconsAsync(chatHwnd, filePaths, SendMethod, IsQtMessengerWindow(chatHwnd), _logSink);
        await HandleSendResultAsync(success);
        return success;
    }

    // 단축키 팝업은 메신저가 아닌 창을 대상으로 할 수도 있으므로, Qt 메신저 프로세스의 창일 때만 Qt 전송 경로를 쓴다
    bool IsQtMessengerWindow(IntPtr chatHwnd) => _targetAppKind == TargetAppKind.Qt && QtChatWindow.IsCandidateWindow(chatHwnd, _kakaoTalkPid);

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

    // ── 채팅창 판별 (GetSessions 등 다른 스레드에서의 동기 판별) ───────────────
    // Qt는 세션이 없는 창만 UIA 스레드에 확인을 요청하며, 메시지 루프 스레드에서는 기다리지 않는다
    bool IsChatWindow(IntPtr hwnd, bool isAttached)
    {
        if (_targetAppKind == TargetAppKind.Mfc) return MfcChatWindow.IsChatWindow(hwnd, _kakaoTalkPid);
        if (!QtChatWindow.IsCandidateWindow(hwnd, _kakaoTalkPid)) return false;
        if (isAttached) return true;
        if (Win32.GetCurrentThreadId() == _uiThreadId || _qtTracker is not { } qtTracker) return false;
        return qtTracker.IsChatWindowSynchronously(hwnd);
    }

    // ── 채팅창 후보에 세션 생성 시도 (UI 스레드) ─────────────────────────────
    // MFC는 즉시 판별해서 생성하고, Qt는 UIA 측정을 요청한 뒤 결과가 도착하면 생성한다
    void TryAttachWindow(IntPtr hwnd)
    {
        lock (_lock)
        {
            if (_sessions.ContainsKey(hwnd))
            {
                return;
            }
        }

        if (_targetAppKind == TargetAppKind.Qt && QtChatWindow.IsCandidateWindow(hwnd, _kakaoTalkPid)) _qtTracker?.RequestProbe(hwnd);
        else if (_targetAppKind == TargetAppKind.Mfc && MfcChatWindow.IsChatWindow(hwnd, _kakaoTalkPid)) AttachSession(hwnd, MfcButtonLayoutSource.Instance);
    }

    void ScanForChatWindows() => Win32.EnumWindows((hwnd, _) =>
    {
        TryAttachWindow(hwnd);
        return true;
    }, IntPtr.Zero);

    // ── 시작 시 이미 열린 모든 채팅창에 세션 생성 ────────────────────────────
    void AttachToExistingWindows()
    {
        ScanForChatWindows();
        if (_targetAppKind == TargetAppKind.Qt)
        {
            RaiseInfoLog($"기존 Qt 창 {_qtTracker?.PendingProbeCount ?? 0}개 UIA 확인 요청 — 채팅창으로 확인되면 세션 생성");
            return;
        }

        int count;
        lock (_lock) { count = _sessions.Count; }
        RaiseInfoLog($"기존 채팅창 {count}개 세션 생성 완료");
    }

    // ── 세션 생성 ────────────────────────────────────────────────────────────
    void AttachSession(IntPtr chatHwnd, IButtonLayoutSource layoutSource)
    {
        lock (_lock) { if (_sessions.ContainsKey(chatHwnd)) return; }

        var session = new DcconSession(chatHwnd, _icon, layoutSource, _logSink) { Clicked = OnSessionButtonClicked };

        if (!session.CreateButton())
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

    // ── 버튼 위치 갱신 + Qt 앵커 재측정 판단 ─────────────────────────────────
    // Qt는 저장된 앵커로 즉시 재배치하고, 클라이언트 크기가 바뀌었으면 툴바 레이아웃을 다시 측정한다
    void UpdateSessionPosition(DcconSession session)
    {
        session.UpdatePosition();
        if (session.LayoutSource is QtButtonLayoutSource layoutSource && QtChatWindow.HasClientSizeChanged(session.ChatHwnd, layoutSource.Anchor)) _qtTracker?.RequestProbe(session.ChatHwnd);
    }

    // ── 주기적 버튼 좌표 재검증 (DPI 변경·레이아웃 변경 안전망) ────────────────
    void ValidateSessionGeometry()
    {
        List<DcconSession> sessions;
        lock (_lock) sessions = [.._sessions.Values];
        foreach (var session in sessions)
        {
            if (Win32.IsIconic(session.ChatHwnd)) continue;
            UpdateSessionPosition(session);
            session.RefreshBackgroundColor();
            if (session.LayoutSource is QtButtonLayoutSource && _qtTracker is { } qtTracker && qtTracker.IsAnchorRefreshDue(session.ChatHwnd)) qtTracker.RequestProbe(session.ChatHwnd);
        }
    }

    // ── Qt 채팅창 버튼 앵커 측정 완료 (UI 스레드) ────────────────────────────
    void OnQtAnchorMeasured(IntPtr hwnd, QtButtonAnchor anchor)
    {
        DcconSession? session;
        lock (_lock) _sessions.TryGetValue(hwnd, out session);

        if (session?.LayoutSource is QtButtonLayoutSource layoutSource)
        {
            layoutSource.Anchor = anchor;
            session.UpdatePosition();
        }
        else if (session is null && QtChatWindow.IsCandidateWindow(hwnd, _kakaoTalkPid)) AttachSession(hwnd, new QtButtonLayoutSource(anchor));
    }

    // ── Qt 채팅창 추적기 시작/종료 ───────────────────────────────────────────
    void StartQtTrackerIfNeeded()
    {
        if (_targetAppKind != TargetAppKind.Qt || _qtTracker is not null) return;
        _qtTracker = new QtChatWindowTracker(_logSink, PostToUiThread, OnQtAnchorMeasured);
        RaiseInfoLog("[UIA] Qt 채팅창 확인용 UI Automation 스레드 시작");
    }

    void StopQtTracker()
    {
        _qtTracker?.Dispose();
        _qtTracker = null;
    }

    // ── 카카오톡 프로세스 감시 (1초 주기 타이머) ─────────────────────────────
    void CheckKakaoTalkProcess()
    {
        var (newPid, newKind) = TargetAppHelper.FindProcess();
        if (newPid == _kakaoTalkPid) return;

        if (_kakaoTalkPid != 0)
        {
            UninstallHooks();
            DetachAll();
            StopQtTracker();
            _kakaoTalkPid = 0;
            RaiseInfoLog("카카오톡 종료 감지 — 세션 정리 완료");
        }

        if (newPid != 0)
        {
            _kakaoTalkPid = newPid;
            _targetAppKind = newKind;
            RaiseInfoLog($"카카오톡 재시작 감지 — 새 PID: {_kakaoTalkPid}, 종류: {_targetAppKind}");
            StartQtTrackerIfNeeded();
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
                TryAttachWindow(hwnd);
                break;
            case Win32.EVENT_OBJECT_DESTROY:
                DetachSession(hwnd);
                _qtTracker?.Forget(hwnd);
                break;
            case Win32.EVENT_OBJECT_LOCATIONCHANGE:
                DcconSession? session;
                lock (_lock) _sessions.TryGetValue(hwnd, out session);
                // SHOW 이벤트 시점에 RICHEDIT50W(MFC) 또는 입력 툴바(Qt)가 아직 없어 세션 미생성된 채팅창 재시도
                if (session != null && !Win32.IsIconic(hwnd)) UpdateSessionPosition(session);
                else if (session == null) TryAttachWindow(hwnd);
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

    // ── UI 스레드로 작업 전달 ────────────────────────────────────────────────
    void PostToUiThread(Action action)
    {
        _pendingActions.Enqueue(action);
        Win32.PostThreadMessage(_uiThreadId, WM_INVOKE, IntPtr.Zero, IntPtr.Zero);
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
            StartQtTrackerIfNeeded();
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
                ValidateSessionGeometry();
                // Qt 채팅창은 표시 직후 QML 로딩이 끝나지 않아 툴바를 못 찾을 수 있으므로 주기적으로 다시 확인한다
                if (_targetAppKind == TargetAppKind.Qt && _kakaoTalkPid != 0) ScanForChatWindows();
                continue;
            }
            Win32.TranslateMessage(in msg);
            Win32.DispatchMessage(in msg);
        }

        Win32.KillTimer(IntPtr.Zero, _kakaoWatchTimerId);
        UninstallHooks();
        DetachAll();
        StopQtTracker();
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

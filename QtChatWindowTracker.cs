namespace YellowInsideLib;

// ──── Qt 채팅창 추적기 ───────────────────────────────────────────────────────
// UIA 측정은 전용 스레드에서 수행하고, 결과는 메시지 루프 스레드로 되돌려 받아 처리한다.
// Synchronously 접미사가 붙은 메서드를 제외한 모든 멤버는 메시지 루프 스레드에서만 호출한다.
sealed class QtChatWindowTracker(LogSink logSink, Action<Action> postToUiThread, Action<IntPtr, QtButtonAnchor> anchorMeasured) : IDisposable
{
    const int RetryBaseIntervalMilliseconds = 500;
    const int MaxRetryBackoffShift = 5;                 // 실패가 이어지면 최대 16초 간격까지 늘린다
    const int AnchorRefreshIntervalMilliseconds = 5000; // 세션이 있는 창의 툴바 레이아웃 재확인 주기
    const int SynchronousTimeoutMilliseconds = 3000;

    readonly LogSink _logSink = logSink;
    readonly Action<Action> _postToUiThread = postToUiThread;
    readonly Action<IntPtr, QtButtonAnchor> _anchorMeasured = anchorMeasured;
    readonly UiAutomationWorker _worker = new(logSink);
    readonly HashSet<IntPtr> _pendingWindows = [];
    readonly HashSet<IntPtr> _nonChatWindows = [];
    readonly Dictionary<IntPtr, ProbeState> _probeStates = [];
    bool _disposed;

    public int PendingProbeCount => _pendingWindows.Count;

    // ── 측정 요청 (같은 창은 동시에 하나만, 실패가 이어지면 간격을 늘림) ──────
    public void RequestProbe(IntPtr hwnd)
    {
        if (_disposed || _nonChatWindows.Contains(hwnd) || _pendingWindows.Contains(hwnd)) return;

        var now = Environment.TickCount64;
        if (_probeStates.TryGetValue(hwnd, out var state))
        {
            var retryIntervalMilliseconds = (long)RetryBaseIntervalMilliseconds << Math.Min(state.FailureCount, MaxRetryBackoffShift);
            if (now - state.LastRequestTickCount < retryIntervalMilliseconds) return;
        }
        else
        {
            state = new ProbeState();
            _probeStates[hwnd] = state;
        }

        state.LastRequestTickCount = now;
        _pendingWindows.Add(hwnd);
        _ = _worker.InvokeAsync(client => QtChatWindow.Probe(client, hwnd)).ContinueWith(task =>
        {
            var result = task.IsCompletedSuccessfully ? task.Result : QtChatWindowProbeResult.Failed(task.Exception?.InnerException?.Message ?? "UIA 작업 취소");
            _postToUiThread(() => OnProbeCompleted(hwnd, result));
        }, TaskScheduler.Default);
    }

    public bool IsAnchorRefreshDue(IntPtr hwnd) => !_probeStates.TryGetValue(hwnd, out var state) || Environment.TickCount64 - state.LastRequestTickCount >= AnchorRefreshIntervalMilliseconds;

    // ── 창 파괴 시 상태 정리 (HWND 값 재사용 대비) ───────────────────────────
    public void Forget(IntPtr hwnd)
    {
        _nonChatWindows.Remove(hwnd);
        _probeStates.Remove(hwnd);
    }

    // ── 다른 스레드에서의 동기 확인 (메시지 루프 스레드에서는 호출 금지) ───────
    public bool IsChatWindowSynchronously(IntPtr hwnd) => _worker.Invoke(client => QtChatWindow.IsChatWindow(client, hwnd), false, SynchronousTimeoutMilliseconds);

    public QtChatWindowProbeResult? ProbeSynchronously(IntPtr hwnd) => _worker.Invoke<QtChatWindowProbeResult?>(client => QtChatWindow.Probe(client, hwnd), null, SynchronousTimeoutMilliseconds);

    // ── 측정 완료 (메시지 루프 스레드) ───────────────────────────────────────
    void OnProbeCompleted(IntPtr hwnd, QtChatWindowProbeResult result)
    {
        if (_disposed) return;
        _pendingWindows.Remove(hwnd);
        if (!Win32.IsWindow(hwnd) || !_probeStates.TryGetValue(hwnd, out var state)) return;

        switch (result.Status)
        {
            case QtChatWindowProbeStatus.NotChatWindow:
                _nonChatWindows.Add(hwnd);
                _probeStates.Remove(hwnd);
                break;
            case QtChatWindowProbeStatus.Succeeded:
                state.FailureCount = 0;
                state.WarningLogged = false;
                _anchorMeasured(hwnd, result.Anchor);
                break;
            default:
                state.FailureCount++;
                if (!state.WarningLogged)
                {
                    state.WarningLogged = true;
                    _logSink.Warn($"[UIA] 0x{hwnd:X8} 버튼 위치 확인 실패 ({result.Status}) — {result.Detail}, 재시도 예정");
                }
                break;
        }
    }

    // ── IDisposable ──────────────────────────────────────────────────────────
    public void Dispose()
    {
        _disposed = true;
        _worker.Dispose();
        _pendingWindows.Clear();
        _nonChatWindows.Clear();
        _probeStates.Clear();
    }

    sealed class ProbeState
    {
        public long LastRequestTickCount { get; set; }
        public int FailureCount { get; set; }
        public bool WarningLogged { get; set; }
    }
}

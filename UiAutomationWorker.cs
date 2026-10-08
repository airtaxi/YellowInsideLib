using System.Collections.Concurrent;

namespace YellowInsideLib;

// ──── UI Automation 전용 스레드 ───────────────────────────────────────────────
// UIA 호출은 메신저 UI 스레드의 응답을 기다리므로 오래 걸리거나 멈출 수 있다.
// 버튼 창을 소유한 메시지 루프 스레드가 이 대기에 묶이면 입력 큐가 연결된 메신저까지
// 함께 멈출 수 있으므로, UIA는 이 MTA 전용 스레드에서만 호출한다.
sealed class UiAutomationWorker : IDisposable
{
    const uint ConnectionTimeoutMilliseconds = 2000;
    const uint TransactionTimeoutMilliseconds = 5000;

    readonly BlockingCollection<Action<UiAutomationClient?>> _workItems = [];
    readonly LogSink? _logSink;
    int _disposed;

    public UiAutomationWorker(LogSink? logSink)
    {
        _logSink = logSink;
        var thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "DcconUIAutomation",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    // ── 비동기 실행 ──────────────────────────────────────────────────────────
    public Task<TResult> InvokeAsync<TResult>(Func<UiAutomationClient, TResult> work)
    {
        var completionSource = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _workItems.Add(client =>
            {
                if (client is null)
                {
                    completionSource.TrySetException(new InvalidOperationException("UI Automation 초기화에 실패했습니다."));
                    return;
                }

                try { completionSource.TrySetResult(work(client)); }
                catch (Exception exception) { completionSource.TrySetException(exception); }
            });
        }
        catch (InvalidOperationException) { completionSource.TrySetCanceled(); }  // 종료된 뒤 들어온 요청 (ObjectDisposedException 포함)

        return completionSource.Task;
    }

    // ── 동기 실행 (메시지 루프 스레드에서는 호출 금지) ───────────────────────
    public TResult Invoke<TResult>(Func<UiAutomationClient, TResult> work, TResult fallbackValue, int timeoutMilliseconds)
    {
        var task = InvokeAsync(work);
        try { return task.Wait(timeoutMilliseconds) ? task.Result : fallbackValue; }
        catch (AggregateException) { return fallbackValue; }
    }

    // ── 스레드 본체 ──────────────────────────────────────────────────────────
    void Run()
    {
        // UIA 사각형과 Win32 좌표를 모두 물리 픽셀로 받기 위해 PerMonitorV2로 동작한다
        using var dpiScope = ThreadDpiAwarenessScope.Enter(Win32.DPI_CONTEXT_PER_MONITOR_V2);
        var initializeResult = Win32.CoInitializeEx(IntPtr.Zero, Win32.COINIT_MULTITHREADED);
        var client = UiAutomationClient.TryCreate(ConnectionTimeoutMilliseconds, TransactionTimeoutMilliseconds, out var hresult);
        if (client is null) _logSink?.Error($"[UIA] UI Automation 초기화 실패 — HRESULT: 0x{hresult:X8}");

        try
        {
            foreach (var workItem in _workItems.GetConsumingEnumerable())
            {
                workItem(client);
            }
        }
        finally
        {
            client?.Dispose();
            _workItems.Dispose();
            if (initializeResult >= 0) Win32.CoUninitialize();
        }
    }

    // ── IDisposable ──────────────────────────────────────────────────────────
    // 진행 중인 UIA 호출을 기다리지 않고 종료를 요청만 한다 (남은 작업은 스레드가 처리 후 정리)
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _workItems.CompleteAdding();
    }
}

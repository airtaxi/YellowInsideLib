using System.Diagnostics;
using System.Runtime.InteropServices.Marshalling;

namespace YellowInsideLib;

// ──── OLE 드래그 소스 ────────────────────────────────────────────────────────
// 왼쪽 버튼을 뗐을 때 대상이 마지막 DragOver에서 드롭을 받겠다고 응답했으면 드롭하고, 아니면 취소한다.
// 커서를 움직이는 스레드가 취소를 요청하면 다음 호출에서 바로 취소한다.
[GeneratedComClass]
sealed partial class DragDropSource(TimeSpan timeout) : IDropSource
{
    public const int DragDropDrop = 0x00040100;           // DRAGDROP_S_DROP
    const int DragDropCancel = 0x00040101;                // DRAGDROP_S_CANCEL
    const int DragDropUseDefaultCursors = 0x00040102;     // DRAGDROP_S_USEDEFAULTCURSORS
    const uint DropEffectScroll = 0x80000000;             // DROPEFFECT_SCROLL
    const uint MouseKeyLeftButton = 0x0001;               // MK_LBUTTON

    readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    uint _lastFeedbackEffect;
    int _acceptedFeedbackCount;
    volatile bool _hasDragLoopStarted;
    volatile string? _cancelReason;

    public bool HasDragLoopStarted => _hasDragLoopStarted;
    public int AcceptedFeedbackCount => Volatile.Read(ref _acceptedFeedbackCount);
    public string? EndReason { get; private set; }

    public void RequestCancel(string reason) => _cancelReason ??= reason;

    public int QueryContinueDrag(bool escapePressed, uint keyState)
    {
        _hasDragLoopStarted = true;
        if (_cancelReason is { } cancelReason) return End(cancelReason, DragDropCancel);
        if (escapePressed) return End("Esc 키 입력", DragDropCancel);
        if (_stopwatch.Elapsed > timeout) return End("드래그 시간 초과", DragDropCancel);
        if ((keyState & MouseKeyLeftButton) != 0) return 0;  // S_OK: 드래그 계속
        return IsAccepted(_lastFeedbackEffect) ? End("드롭", DragDropDrop) : End("채팅창이 드롭을 받지 않음", DragDropCancel);
    }

    public int GiveFeedback(uint effect)
    {
        _hasDragLoopStarted = true;
        _lastFeedbackEffect = effect;
        if (IsAccepted(effect)) Interlocked.Increment(ref _acceptedFeedbackCount);
        return DragDropUseDefaultCursors;
    }

    static bool IsAccepted(uint effect) => (effect & ~DropEffectScroll) != 0;

    int End(string reason, int result)
    {
        EndReason = reason;
        return result;
    }
}

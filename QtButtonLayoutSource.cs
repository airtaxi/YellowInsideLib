namespace YellowInsideLib;

// ──── Qt 채팅창 버튼 배치 (UIA로 측정한 앵커를 현재 클라이언트 크기에 맞춰 환산) ──────
sealed class QtButtonLayoutSource(QtButtonAnchor anchor) : IButtonLayoutSource
{
    // UIA 측정 결과가 도착하면 메시지 루프 스레드에서 갱신한다
    public QtButtonAnchor Anchor { get; set; } = anchor;

    // Qt Quick 렌더링은 자식 창 위를 덮어 그리지 않으므로 Z 순서 복구가 필요 없다
    public bool RequiresPeriodicZOrderRefresh => false;

    public ButtonLayout? CalculateLayout(IntPtr chatHwnd, LogSink? logSink) => QtChatWindow.CalculateButtonLayout(chatHwnd, Anchor);
}

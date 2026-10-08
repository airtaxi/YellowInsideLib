namespace YellowInsideLib;

// ──── MFC 채팅창 버튼 배치 (RICHEDIT50W 기준 비율 계산) ──────────────────────
sealed class MfcButtonLayoutSource : IButtonLayoutSource
{
    public static MfcButtonLayoutSource Instance { get; } = new();

    // EVA BitBlt이 버튼을 덮어 그리므로 Z 순서를 주기적으로 복구한다
    public bool RequiresPeriodicZOrderRefresh => true;

    public ButtonLayout? CalculateLayout(IntPtr chatHwnd, LogSink? logSink) => MfcChatWindow.CalculateButtonLayout(chatHwnd, logSink);
}

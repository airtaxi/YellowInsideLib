namespace YellowInsideLib;

// ──── 채팅창 종류별 버튼 배치 계산 ───────────────────────────────────────────
interface IButtonLayoutSource
{
    // 대상 창이 버튼 위를 덮어 그리는 경우 주기적으로 Z 순서를 복구해야 한다
    bool RequiresPeriodicZOrderRefresh { get; }

    // 호출 스레드는 채팅창의 DPI 인식 컨텍스트에 들어가 있어야 한다
    ButtonLayout? CalculateLayout(IntPtr chatHwnd, LogSink? logSink);
}

namespace YellowInsideLib;

// ──── Qt 채팅창 디시콘 버튼 앵커 ─────────────────────────────────────────────
// UIA로 측정한 시점의 물리 픽셀 값이다. 입력 툴바가 창 아래쪽에 붙어 있으므로
// 클라이언트 좌하단을 기준으로 저장해 두면 창 크기가 바뀌어도 UIA 없이 재배치할 수 있다.
readonly record struct QtButtonAnchor(int OffsetX, int OffsetFromBottom, int Size, int BackgroundSampleOffsetX, int ClientWidth, int ClientHeight, uint Dpi);

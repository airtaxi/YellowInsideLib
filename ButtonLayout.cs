namespace YellowInsideLib;

// ──── 디시콘 버튼 배치 정보 (채팅창 클라이언트 좌표, 채팅창 DPI 인식 컨텍스트 기준) ────
// IconScale: 버튼 한 변 대비 아이콘이 차지하는 비율
// BackgroundSampleClientPoint: 버튼 배경색으로 사용할 툴바 픽셀 위치
readonly record struct ButtonLayout(int ClientX, int ClientY, int Size, float IconScale, Win32.POINT BackgroundSampleClientPoint);

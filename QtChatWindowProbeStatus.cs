namespace YellowInsideLib;

/// <summary>Qt 창 UIA 확인 결과 상태</summary>
enum QtChatWindowProbeStatus
{
    /// <summary>UIA 호출 실패 또는 시간 초과</summary>
    Failed,

    /// <summary>채팅창이 아닌 Qt 창 (메인 창, 광고 창 등)</summary>
    NotChatWindow,

    /// <summary>채팅창이지만 입력 툴바를 아직 찾지 못함 (QML 로딩 중 등)</summary>
    AnchorNotFound,

    /// <summary>버튼 앵커 측정 완료</summary>
    Succeeded,
}

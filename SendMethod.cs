namespace YellowInsideLib;

/// <summary>디시콘 이미지 전송 방식</summary>
public enum SendMethod
{
    /// <summary>파일 드롭 전송 시도 후 실패 시 클립보드 방식으로 자동 전환</summary>
    Auto,

    /// <summary>파일 드롭 전용 (MFC 메신저: WM_DROPFILES PostMessage, Qt 메신저: OLE 드래그앤드롭)</summary>
    DropFiles,

    /// <summary>클립보드 붙여넣기 (Ctrl+V → Enter) 전용</summary>
    Clipboard,
}

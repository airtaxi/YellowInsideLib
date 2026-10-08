namespace YellowInsideLib;

// ──── Qt 창 UIA 확인 결과 ────────────────────────────────────────────────────
readonly record struct QtChatWindowProbeResult(QtChatWindowProbeStatus Status, QtButtonAnchor Anchor, string Detail)
{
    public static QtChatWindowProbeResult NotChatWindow => new(QtChatWindowProbeStatus.NotChatWindow, default, "");

    public static QtChatWindowProbeResult Succeeded(QtButtonAnchor anchor) => new(QtChatWindowProbeStatus.Succeeded, anchor, "");

    public static QtChatWindowProbeResult AnchorNotFound(string detail) => new(QtChatWindowProbeStatus.AnchorNotFound, default, detail);

    public static QtChatWindowProbeResult Failed(string detail) => new(QtChatWindowProbeStatus.Failed, default, detail);
}

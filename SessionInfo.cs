// ──── 세션 정보 ──────────────────────────────────────────────────────────────
namespace YellowInsideLib;

public record SessionInfo(IntPtr ChatHwnd, string Title, bool IsButtonAttached)
{
    public override string ToString() =>
        $"0x{ChatHwnd:X8} '{Title}' (버튼: {(IsButtonAttached ? "O" : "X")})";
}

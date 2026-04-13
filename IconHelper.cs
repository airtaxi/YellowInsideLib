using System.Drawing;

namespace YellowInsideLib;

// ──── 아이콘 로드 헬퍼 ──────────────────────────────────────────────────────
static class IconHelper
{
    public static Image? LoadFromFile(string filePath)
    {
        try { if (File.Exists(filePath)) return Image.FromFile(filePath); }
        catch { /* ignore */ }
        return null;
    }
}

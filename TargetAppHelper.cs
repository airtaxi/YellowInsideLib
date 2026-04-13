using System.Diagnostics;

namespace YellowInsideLib;

// ──── 메신저 프로세스 헬퍼 ────────────────────────────────────────────────
static class TargetAppHelper
{
    public static uint FindProcessId()
    {
        Process[] processes = Process.GetProcessesByName("KakaoTalk");
        if (processes.Length == 0) return 0;
        return (uint)processes[0].Id;
    }
}

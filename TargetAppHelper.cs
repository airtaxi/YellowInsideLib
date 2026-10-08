using System.Diagnostics;

namespace YellowInsideLib;

// ──── 메신저 프로세스 헬퍼 ────────────────────────────────────────────────
static class TargetAppHelper
{
    const string QtProcessName = "KakaoTalkUI";
    const string MfcProcessName = "KakaoTalk";

    // Qt 클라이언트를 우선 탐색하고, 없으면 MFC 클라이언트를 탐색한다
    public static (uint ProcessId, TargetAppKind Kind) FindProcess()
    {
        var qtProcessId = FindProcessId(QtProcessName);
        if (qtProcessId != 0) return (qtProcessId, TargetAppKind.Qt);
        return (FindProcessId(MfcProcessName), TargetAppKind.Mfc);
    }

    static uint FindProcessId(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        try { return processes.Length == 0 ? 0 : (uint)processes[0].Id; }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}

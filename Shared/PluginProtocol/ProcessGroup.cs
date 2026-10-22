using System.Runtime.InteropServices;

namespace MeowChan.PluginProtocol;

internal static class ProcessGroup
{
    public static void InitializeWorker()
    {
        if (!OperatingSystem.IsWindows()) SetProcessGroup(0, 0);
    }
    public static bool IsWorkerGroup(int processId) => !OperatingSystem.IsWindows() && GetProcessGroup(processId) == processId;
    public static void KillWorkerGroup(int processId, bool previouslyConfirmed = false)
    {
        // Only target a distinct process group led by this owned worker. Never the host group.
        if (!OperatingSystem.IsWindows() && (previouslyConfirmed || IsWorkerGroup(processId)))
            SendSignal(-processId, 9);
    }
    [DllImport("libc", EntryPoint = "setpgid", SetLastError = true)] private static extern int SetProcessGroup(int pid, int pgid);
    [DllImport("libc", EntryPoint = "getpgid", SetLastError = true)] private static extern int GetProcessGroup(int pid);
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)] private static extern int SendSignal(int pid, int signal);
}

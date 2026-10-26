using System.Globalization;
using System.Runtime.InteropServices;

namespace MeowChan.PluginHost.Runtime;

/// <summary>Quota release waits for executable group members; Linux zombies cannot retain workspace resources.</summary>
internal static class WorkerWorkspaceQuota
{
    internal static async Task WaitForGroupExitAsync(int processId, CancellationToken token)
    {
        if (OperatingSystem.IsWindows()) return; // OwnedProcessTree already waited for its whole job.
        string? previousZombies = null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int result = ProbeGroup(-processId, 0), error = Marshal.GetLastPInvokeError();
            string? zombies = null;
            if (result == 0 && OperatingSystem.IsLinux() && ProcVisibilityAllowsZombieProof())
                zombies = ReadLinuxZombieInventory(processId, "/proc");
            if (GroupHasExited(result, error, previousZombies, zombies)) return;
            previousZombies = zombies;
            await Task.Delay(10, token).ConfigureAwait(false);
        }
    }

    internal static bool GroupHasExited(int probeResult, int error, string? previousZombies, string? currentZombies)
    {
        if (probeResult == -1 && error == 3) return true; // ESRCH
        // In particular, EPERM is never overridden by a partial /proc inventory.
        return probeResult == 0 && currentZombies != null && currentZombies == previousZombies;
    }

    private static bool ProcVisibilityAllowsZombieProof()
    {
        try
        {
            return ProcVisibilityAllowsZombieProof(File.ReadAllText("/proc/mounts"),
                File.ReadAllText("/proc/self/stat"), Environment.ProcessId);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    internal static bool ProcVisibilityAllowsZombieProof(string mounts, string selfStat, int selfPid)
    {
        // Reject hidepid (including hidepid=2, which can silently omit directories),
        // and procfs mounted for a different PID namespace. No visible rows is not proof.
        if (!TryReadStat(selfStat, selfPid, out _, out _, out _)) return false;
        bool found = false;
        foreach (string line in mounts.Split('\n'))
        {
            string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4 || fields[1] != "/proc" || fields[2] != "proc") continue;
            found = true;
            if (fields[3].Split(',').Any(option => option.StartsWith("hidepid=", StringComparison.Ordinal)
                && option is not "hidepid=0" and not "hidepid=off")) return false;
        }
        return found;
    }

    internal static string? ReadLinuxZombieInventory(int processGroup, string procRoot)
    {
        try
        {
            List<string> identities = [];
            int inspected = 0;
            foreach (string directory in Directory.EnumerateDirectories(procRoot))
            {
                if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0) continue;
                if (++inspected > 131072) return null;
                string stat;
                try { stat = File.ReadAllText(Path.Combine(directory, "stat")); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                if (!TryReadStat(stat, pid, out int group, out char state, out ulong birth)) return null;
                if (group != processGroup) continue;
                if (state != 'Z') return null; // Includes stopped, traced, uninterruptible and unknown states.
                identities.Add("p" + pid.ToString(CultureInfo.InvariantCulture) + ":" + birth.ToString(CultureInfo.InvariantCulture));
                // A leader can be Z after pthread_exit while sibling threads still
                // execute. Inspect its tasks as well; leader state alone is insufficient.
                bool sawLeader = false;
                foreach (string task in Directory.EnumerateDirectories(Path.Combine(directory, "task")))
                {
                    if (++inspected > 131072) return null;
                    if (!int.TryParse(Path.GetFileName(task), NumberStyles.None, CultureInfo.InvariantCulture, out int tid) || tid <= 0) return null;
                    string taskStat;
                    try { taskStat = File.ReadAllText(Path.Combine(task, "stat")); }
                    catch (FileNotFoundException) { continue; }
                    catch (DirectoryNotFoundException) { continue; }
                    if (!TryReadStat(taskStat, tid, out int taskGroup, out char taskState, out ulong taskBirth)
                        || taskGroup != processGroup || taskState != 'Z') return null;
                    if (tid == pid) { if (taskBirth != birth) return null; sawLeader = true; }
                    identities.Add("t" + tid.ToString(CultureInfo.InvariantCulture) + ":" + taskBirth.ToString(CultureInfo.InvariantCulture));
                }
                if (!sawLeader) return null;
            }
            // Require two complete inventories with identical PID/birth identities.
            // This also avoids declaring a still-existing but invisible group empty.
            identities.Sort(StringComparer.Ordinal);
            return identities.Count == 0 ? null : string.Join(",", identities);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool TryReadStat(string stat, int pid, out int group, out char state, out ulong birth)
    {
        group = 0; state = default; birth = 0;
        if (stat.Length > 8192) return false;
        int opening = stat.IndexOf('('), closing = stat.LastIndexOf(')');
        if (opening < 1 || closing <= opening
            || !int.TryParse(stat.AsSpan(0, opening).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int actualPid)
            || actualPid != pid) return false;
        // comm may contain spaces, newlines and ')' characters. Fields start after its final ')'.
        string[] fields = stat[(closing + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 20 || fields[0].Length != 1
            || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out group) || group <= 0
            || !ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out birth)) return false;
        state = fields[0][0]; return true;
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int ProbeGroup(int processId, int signal);
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Serilog;

namespace MeowChan.Desktop;

/// <summary>
/// Startup cleanup of <c>&lt;profile&gt;/plugin-workers/worker-&lt;guid&gt;</c> workspaces that a forced exit left behind. A worker removes its own
/// workspace when it is disposed; a process that ended without that (watchdog exit, crash, power loss) does not. The profile lease is held by
/// then, so no other host owns a worker of this profile; the sweep still keeps every workspace that is young, that a live process works in,
/// or that cannot be deleted, and it never leaves the profile's <c>plugin-workers</c> directory.
/// </summary>
internal static class PluginWorkerWorkspaceSweep
{
    /// <summary>A workspace younger than this is never swept (documented constant: a worker of a just-ended process may still be shutting down).</summary>
    internal static readonly TimeSpan DefaultMinimumAge = TimeSpan.FromMinutes(10);

    /// <summary>At most this many workspaces are examined per start (documented constant; the rest is swept at the next start).</summary>
    internal const int DefaultMaximumFolders = 256;

    internal readonly record struct Result(int Removed, int KeptYoung, int KeptInUse, int Failed, bool Truncated);

    // Exactly the name WorkerConnection creates: "worker-" and a 32-digit lowercase GUID ("N" format). Anything else is not ours.
    private static bool IsWorkerFolderName(string name) =>
        name.Length == 7 + 32 && name.StartsWith("worker-", StringComparison.Ordinal) && name.AsSpan(7).IndexOfAnyExcept("0123456789abcdef".AsSpan()) < 0;

    internal static Result Sweep(string workersRoot, ILogger? logger = null, TimeSpan? minimumAge = null, int maximumFolders = DefaultMaximumFolders,
        DateTime? nowUtc = null, Func<string, bool>? isInUse = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(workersRoot);
        TimeSpan age = minimumAge ?? DefaultMinimumAge;
        DateTime now = nowUtc ?? DateTime.UtcNow;
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workersRoot));
        int removed = 0, young = 0, busy = 0, failed = 0, examined = 0;
        bool truncated = false;
        try
        {
            DirectoryInfo rootInfo = new(root);
            // A missing directory has nothing to sweep; a link could lead outside the profile.
            if (!rootInfo.Exists || rootInfo.Attributes.HasFlag(FileAttributes.ReparsePoint)) return default;
            Lazy<string[]> liveDirectories = new(() => LiveWorkingDirectories());
            foreach (string entry in Directory.EnumerateDirectories(root).ToArray())
            {
                string name = Path.GetFileName(entry);
                if (!IsWorkerFolderName(name)) continue;
                string full = Path.GetFullPath(entry);
                if (!string.Equals(Path.GetDirectoryName(full), root, StringComparison.Ordinal)) continue;
                DirectoryInfo directory = new(full);
                if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (examined >= Math.Max(1, maximumFolders)) { truncated = true; break; }
                examined++;
                if (now - directory.LastWriteTimeUtc < age) { young++; continue; }
                if (isInUse?.Invoke(full) ?? UsedBy(liveDirectories.Value, full)) { busy++; continue; }
                try { directory.Delete(recursive: true); removed++; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    failed++;
                    logger?.Debug(error, "A stale plugin worker workspace could not be removed and stays for the next start: {Workspace}", name);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger?.Debug(error, "The stale plugin worker workspaces could not be listed.");
        }
        Result result = new(removed, young, busy, failed, truncated);
        if (removed > 0 || failed > 0)
            logger?.Information("Removed {Removed} stale plugin worker workspaces left by an earlier forced exit ({Young} too young, {InUse} in use, {Failed} not removable{More})",
                removed, young, busy, failed, truncated ? ", more remain for the next start" : string.Empty);
        return result;
    }

    private static bool UsedBy(string[] liveDirectories, string workspace)
    {
        string prefix = workspace + Path.DirectorySeparatorChar;
        return liveDirectories.Any(directory => directory.Equals(workspace, StringComparison.Ordinal) || directory.StartsWith(prefix, StringComparison.Ordinal));
    }

    // The working directories of the live processes (Linux only). Windows refuses to delete a directory a process works in, which counts as "failed".
    private static string[] LiveWorkingDirectories()
    {
        if (!OperatingSystem.IsLinux()) return [];
        List<string> found = new();
        try
        {
            foreach (string entry in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(entry), out _)) continue;
                try { if (new FileInfo(Path.Combine(entry, "cwd")).LinkTarget is { } target) found.Add(target); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return found.ToArray();
    }
}

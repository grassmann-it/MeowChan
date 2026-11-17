using System.Diagnostics;

namespace MeowChan.DesktopTests;

/// <summary>Private replay directories inside the already writable evidence bind, never the released profile.
/// The home directory is shared by the three cycles so path-derived defaults (for example DownloadPath) remain
/// comparable; the other per-cycle caches and temporary directories are fresh. sudo's env_reset otherwise supplies
/// /root to a non-root replay child. Forward these exact values through bwrap too: setting only
/// ProcessStartInfo.Environment does not survive privileged namespace setup.</summary>
internal static class Released102CycleEnvironment
{
    internal static IReadOnlyDictionary<string, string> Create(string result)
    {
        string fullResult = Path.GetFullPath(result);
        string root = fullResult + ".environment";
        if (Directory.Exists(root) || File.Exists(root))
            throw new IOException("A replay cycle requires a fresh private environment: " + root);
        Directory.CreateDirectory(root);
        string resultsDirectory = Path.GetDirectoryName(fullResult)
            ?? throw new IOException("A replay cycle result must have a parent directory: " + result);
        string home = Path.Combine(resultsDirectory, "replay-home");
        if (File.Exists(home) || (Directory.Exists(home) && File.GetAttributes(home).HasFlag(FileAttributes.ReparsePoint)))
            throw new IOException("A replay requires a private stable home directory: " + home);
        Directory.CreateDirectory(home);
        Dictionary<string, string> environment = new(StringComparer.Ordinal)
        {
            // Config derives DownloadPath from HOME. Keep HOME stable for all three cycles;
            // otherwise the checker mistakes the harness's cycle-specific sandbox path for
            // a product default drift.
            ["HOME"] = home,
            ["DOTNET_CLI_HOME"] = Path.Combine(root, "dotnet-cli"),
            ["TMPDIR"] = Path.Combine(root, "tmp"),
            ["XDG_CONFIG_HOME"] = Path.Combine(root, "home", ".config"),
            ["XDG_STATE_HOME"] = Path.Combine(root, "home", ".local", "state"),
            ["XDG_CACHE_HOME"] = Path.Combine(root, "home", ".cache"),
        };
        foreach (string directory in environment.Values) Directory.CreateDirectory(directory);
        return environment;
    }

    internal static void Apply(ProcessStartInfo start, IReadOnlyDictionary<string, string> environment, bool bubblewrap)
    {
        foreach ((string key, string value) in environment)
        {
            start.Environment[key] = value;
            if (!bubblewrap) continue;
            start.ArgumentList.Add("--setenv");
            start.ArgumentList.Add(key);
            start.ArgumentList.Add(value);
        }
    }
}

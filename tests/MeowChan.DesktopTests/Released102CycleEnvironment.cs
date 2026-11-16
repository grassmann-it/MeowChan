using System.Diagnostics;

namespace MeowChan.DesktopTests;

/// <summary>Private per-process home inside the already writable evidence bind, never the released profile.
/// sudo's env_reset otherwise supplies /root to a non-root replay child. Forward these exact values through
/// bwrap too: setting only ProcessStartInfo.Environment does not survive privileged namespace setup.</summary>
internal static class Released102CycleEnvironment
{
    internal static IReadOnlyDictionary<string, string> Create(string result)
    {
        string root = Path.GetFullPath(result) + ".environment";
        if (Directory.Exists(root) || File.Exists(root))
            throw new IOException("A replay cycle requires a fresh private environment: " + root);
        Directory.CreateDirectory(root);
        Dictionary<string, string> environment = new(StringComparer.Ordinal)
        {
            ["HOME"] = Path.Combine(root, "home"),
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

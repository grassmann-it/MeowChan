using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MeowChan.Desktop;

/// <summary>Hands a location to the desktop without leaking development runtime paths.</summary>
internal static class ExternalLauncher
{
    internal static Task OpenAsync(string target) => StartAsync(CreateStartInfo(target));

    internal static ProcessStartInfo CreateStartInfo(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (!OperatingSystem.IsLinux())
            return new ProcessStartInfo { FileName = target, UseShellExecute = true };

        ProcessStartInfo info = new ProcessStartInfo
        {
            FileName = "xdg-open",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // Keep URLs (including query strings) and paths as one literal argument.
        info.ArgumentList.Add(target);
        RemoveSnapRuntimePaths(info);
        return info;
    }

    internal static void RemoveSnapRuntimePaths(ProcessStartInfo info)
    {
        string userSnap = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "snap") + "/";
        foreach (string key in new[]
        {
            "GIO_MODULE_DIR", "GIO_EXTRA_MODULES", "GTK_PATH", "GTK_EXE_PREFIX", "GTK_DATA_PREFIX",
            "GTK_IM_MODULE_FILE", "GSETTINGS_SCHEMA_DIR", "LD_LIBRARY_PATH", "XDG_DATA_DIRS"
        })
        {
            if (!info.Environment.TryGetValue(key, out string? value) || string.IsNullOrEmpty(value)) continue;
            string[] paths = value.Split(':');
            string[] systemPaths = paths.Where(path => !path.StartsWith("/snap/", StringComparison.Ordinal)
                && !path.StartsWith(userSnap, StringComparison.Ordinal)).ToArray();
            if (paths.Length == systemPaths.Length) continue;
            // System desktop applications must load their own GTK/GIO libraries. In particular,
            // VS Code's Snap terminal exports a GIO module cache linked to another glibc.
            // Preserve ordinary custom paths and /var/lib/snapd/desktop for app discovery.
            if (systemPaths.Length == 0) info.Environment.Remove(key);
            else info.Environment[key] = string.Join(':', systemPaths);
        }
    }

    internal static async Task StartAsync(ProcessStartInfo info)
    {
        using Process? process = Process.Start(info);
        // Shell handoff can succeed without creating a process (for example when
        // an existing browser handles the URL on Windows).
        if (info.UseShellExecute) return;
        if (process is null)
            throw new InvalidOperationException("The desktop launcher could not be started.");

        // Starting xdg-open is not proof of successful handoff. In particular, a missing
        // default application is reported by its exit code, not by Process.Start.
        // Await asynchronously and never terminate the user's browser/file manager.
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"The desktop launcher exited with code {process.ExitCode}.");
    }
}

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MeowChan.Desktop;

/// <summary>Hands a location to the desktop without leaking development runtime paths.</summary>
internal static class ExternalLauncher
{
    internal static async Task OpenAsync(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (OperatingSystem.IsLinux() && Uri.TryCreate(target, UriKind.Absolute, out Uri? uri)
            && uri.Scheme is "http" or "https")
        {
            string? browser = await TryGetDefaultBrowserAsync().ConfigureAwait(false);
            string? executable = BrowserExecutable(browser);
            if (executable != null && TryFindExecutable(executable) is { } path)
            {
                ProcessStartInfo browserInfo = NewLinuxStartInfo(path);
                browserInfo.ArgumentList.Add("--new-window");
                browserInfo.ArgumentList.Add(target);
                await StartBrowserAsync(browserInfo).ConfigureAwait(false);
                return;
            }
        }
        await StartAsync(CreateStartInfo(target)).ConfigureAwait(false);
    }

    internal static ProcessStartInfo CreateStartInfo(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (!OperatingSystem.IsLinux())
            return new ProcessStartInfo { FileName = target, UseShellExecute = true };

        ProcessStartInfo info = NewLinuxStartInfo("xdg-open");
        // Keep URLs (including query strings) and paths as one literal argument.
        info.ArgumentList.Add(target);
        return info;
    }

    private static ProcessStartInfo NewLinuxStartInfo(string executable)
    {
        ProcessStartInfo info = new() { FileName = executable, UseShellExecute = false, CreateNoWindow = true };
        RemoveSnapRuntimePaths(info);
        return info;
    }

    private static async Task<string?> TryGetDefaultBrowserAsync()
    {
        ProcessStartInfo info = NewLinuxStartInfo("xdg-settings");
        info.ArgumentList.Add("get");
        info.ArgumentList.Add("default-web-browser");
        info.RedirectStandardOutput = true;
        try
        {
            using Process? process = Process.Start(info);
            if (process == null) return null;
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                string output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return process.ExitCode == 0 ? output.Trim() : null;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
                return null;
            }
        }
        catch (Win32Exception) { return null; }
        catch (IOException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static string? BrowserExecutable(string? desktopId) => desktopId?.ToLowerInvariant() switch
    {
        "firefox.desktop" or "firefox_firefox.desktop" => "firefox",
        "chromium.desktop" => "chromium",
        "chromium-browser.desktop" => "chromium-browser",
        "google-chrome.desktop" or "google-chrome-stable.desktop" => "google-chrome",
        "brave-browser.desktop" or "brave-browser-stable.desktop" => "brave-browser",
        "microsoft-edge.desktop" or "microsoft-edge-stable.desktop" => "microsoft-edge",
        "vivaldi.desktop" or "vivaldi-stable.desktop" => "vivaldi",
        _ => null
    };

    private static string? TryFindExecutable(string name)
    {
        if (!OperatingSystem.IsLinux()) return null;
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Take(128))
        {
            if (string.IsNullOrEmpty(directory)) continue;
            string path = Path.Combine(directory, name);
            try
            {
                if (!File.Exists(path)) continue;
                UnixFileMode mode = File.GetUnixFileMode(path);
                if ((mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                    return path;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }

    private static async Task StartBrowserAsync(ProcessStartInfo info)
    {
        using Process? process = Process.Start(info);
        if (process is null)
            throw new InvalidOperationException("The selected browser could not be started.");
        // Browsers may stay alive after opening the URL. Observe quick launcher failures
        // without waiting for or terminating the user's browser process.
        using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(750));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"The selected browser exited with code {process.ExitCode}.");
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

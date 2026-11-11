using System;
using System.IO;
using Serilog;

namespace MeowChan.Desktop;

/// <summary>
/// Local rolling log file of the desktop host. It never depends on the profile: the profile may be locked,
/// missing on first start or selected later, and creating it early would change first-run detection.
/// The file stays on this machine and is never sent anywhere.
/// </summary>
internal static class DesktopLog
{
    internal const string FileName = "meowchan-.log";
    internal const long FileSizeLimitBytes = 8L * 1024 * 1024;
    internal const int RetainedFileCount = 10;
    internal static readonly TimeSpan RetainedFileAge = TimeSpan.FromDays(14);

    /// <summary>The directory that holds the log files, or null when none is writable (console only).</summary>
    internal static string? Directory { get; private set; }

    internal static LoggerConfiguration Configure(LoggerConfiguration configuration, string? directory,
        long fileSizeLimitBytes = FileSizeLimitBytes)
    {
        Directory = directory;
        if (directory == null) return configuration;
        // Shared: a second instance during single-instance handover must not fail on the same file.
        return configuration.WriteTo.File(Path.Combine(directory, FileName), rollingInterval: RollingInterval.Day,
            fileSizeLimitBytes: fileSizeLimitBytes, rollOnFileSizeLimit: true, retainedFileCountLimit: RetainedFileCount,
            retainedFileTimeLimit: RetainedFileAge, shared: true);
    }

    /// <summary>
    /// Creates the per-user log directory. Without one the host logs to the console only: a shared temporary
    /// directory is deliberately not a fallback, because the log contains URLs and local paths.
    /// </summary>
    internal static string? ResolveDirectory()
    {
        try
        {
            if (PlatformDirectory() is not { } directory) return null;
            if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(directory);
            else System.IO.Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return directory;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException) { return null; }
    }

    internal static string? PlatformDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Rooted(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeowChan", "Logs");
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS()) return Rooted(home, "Library", "Logs", "MeowChan");
        string? state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        // The XDG base directory specification ignores relative values.
        return !string.IsNullOrWhiteSpace(state) && Path.IsPathFullyQualified(state)
            ? Path.Combine(state, "MeowChan", "logs") : Rooted(home, ".local", "state", "MeowChan", "logs");
    }

    private static string? Rooted(string root, params string[] parts) =>
        string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) ? null : Path.Combine([root, .. parts]);
}

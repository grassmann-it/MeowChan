using System;
using System.IO;
using System.Text.RegularExpressions;
using MeowChan.Booru;
using Serilog;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;

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

    // Render first: exceptions, nested properties and literal message text can all echo an input URL.
    // This is output redaction only; never use it to change a request, watch identity or provider query.
    // Queries pasted into Add may contain unencoded spaces. Conservatively include the rest of
    // that line, not just a whitespace-delimited URL token (which could leave the secret behind).
    private static readonly Regex HttpUrl = new(@"https?://[^\r\n]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    internal static string RedactText(string text) =>
        HttpUrl.Replace(text, match => DanbooruAddress.RedactForLog(match.Value));

    internal static ITextFormatter ConsoleFormatter { get; } =
        new AddressRedactingFormatter("[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");
    private static ITextFormatter FileFormatter { get; } =
        new AddressRedactingFormatter("{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}");

    private sealed class AddressRedactingFormatter(string template) : ITextFormatter
    {
        private readonly MessageTemplateTextFormatter _inner = new(template, null);

        public void Format(LogEvent logEvent, TextWriter output)
        {
            using StringWriter rendered = new(output.FormatProvider);
            _inner.Format(logEvent, rendered);
            output.Write(RedactText(rendered.ToString()));
        }
    }

    /// <summary>The directory that holds the log files, or null when none is writable (console only).</summary>
    internal static string? Directory { get; private set; }

    internal static LoggerConfiguration Configure(LoggerConfiguration configuration, string? directory,
        long fileSizeLimitBytes = FileSizeLimitBytes)
    {
        Directory = directory;
        if (directory == null) return configuration;
        // Shared: a second instance during single-instance handover must not fail on the same file.
        return configuration.WriteTo.File(FileFormatter, Path.Combine(directory, FileName), rollingInterval: RollingInterval.Day,
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

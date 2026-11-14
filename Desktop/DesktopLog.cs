using System;
using System.IO;
using System.Text;
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
    // A linear scan is deliberate: log text is untrusted and a backtracking regex here would make
    // an error path an avoidable CPU sink. A relevant URL consumes its line remainder because Add
    // queries may contain unencoded spaces; unrelated URL prefixes remain visible.
    internal static string RedactText(string text)
    {
        if (text.Length == 0) return text;

        StringBuilder? redacted = null;
        int copyFrom = 0;
        int searchFrom = 0;
        while (true)
        {
            int start = FindHttpStart(text, searchFrom);
            if (start < 0) break;
            int tokenEnd = FindHttpTokenEnd(text, start);
            if (tokenEnd <= start)
            {
                searchFrom = start + 1;
                continue;
            }

            if (!IsDanbooruUrl(text, start, tokenEnd))
            {
                searchFrom = tokenEnd;
                continue;
            }

            int lineEnd = FindLineEnd(text, start);
            redacted ??= new StringBuilder(text.Length);
            redacted.Append(text, copyFrom, start - copyFrom);
            redacted.Append(DanbooruAddress.RedactForLog(text.Substring(start, tokenEnd - start)));
            copyFrom = lineEnd;
            searchFrom = lineEnd;
        }

        return redacted == null ? text : redacted.Append(text, copyFrom, text.Length - copyFrom).ToString();
    }

    private static int FindHttpStart(string text, int start)
    {
        if (start >= text.Length) return -1;
        int http = text.IndexOf("http://", start, StringComparison.OrdinalIgnoreCase);
        int https = text.IndexOf("https://", start, StringComparison.OrdinalIgnoreCase);
        return http < 0 ? https : https < 0 ? http : Math.Min(http, https);
    }

    private static int FindHttpTokenEnd(string text, int start)
    {
        int end = start;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
        {
            if (end > start && IsHttpStartAt(text, end)) return end;
            end++;
        }
        return end;
    }

    private static bool IsHttpStartAt(string text, int index) =>
        text.AsSpan(index).StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || text.AsSpan(index).StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static int FindLineEnd(string text, int start)
    {
        int end = start;
        while (end < text.Length && text[end] is not ('\r' or '\n')) end++;
        return end;
    }

    private static bool IsDanbooruUrl(string text, int start, int tokenEnd)
    {
        int schemeLength = text.AsSpan(start).StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? 8 : 7;
        int authorityStart = start + schemeLength;
        int authorityEnd = authorityStart;
        while (authorityEnd < tokenEnd && text[authorityEnd] is not ('/' or '?' or '#')) authorityEnd++;
        while (authorityEnd > authorityStart && IsAuthorityPunctuation(text[authorityEnd - 1])) authorityEnd--;
        if (authorityEnd <= authorityStart) return false;

        // Strip userinfo and an optional port without parsing the query/path. This keeps a very
        // long pasted credential bounded to one linear pass and does not mistake a host mention
        // in a foreign URL's path or userinfo for the destination host.
        int hostStart = authorityStart;
        for (int index = authorityEnd - 1; index >= authorityStart; index--)
        {
            if (text[index] == '@')
            {
                hostStart = index + 1;
                break;
            }
        }
        int hostEnd = authorityEnd;
        int port = -1;
        for (int index = hostEnd - 1; index > hostStart; index--)
        {
            if (text[index] == ':')
            {
                port = index;
                break;
            }
        }
        if (port > hostStart) hostEnd = port;
        ReadOnlySpan<char> host = text.AsSpan(hostStart, hostEnd - hostStart);
        return host.Equals(DanbooruPostReference.MetadataHost, StringComparison.OrdinalIgnoreCase)
            || host.Equals(DanbooruPostReference.SafebooruHost, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAuthorityPunctuation(char value) => value is '.' or ',' or ';' or '!' or ')' or ']' or '}';

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

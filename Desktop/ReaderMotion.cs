using System;

namespace MeowChan.Desktop;

/// <summary>
/// The reader's one question to the motion policy: should short fades be skipped? There is no app-wide policy yet, so
/// the environment decides (MEOWCHAN_REDUCED_MOTION=1, or MEOWCHAN_MOTION=off as proposed by the design study); the
/// operating system's setting is not read. Reduced motion keeps the same information as a static state.
/// </summary>
internal static class ReaderMotion
{
    internal static readonly TimeSpan Highlight = TimeSpan.FromMilliseconds(400);

    internal static bool Reduced => IsReduced(Environment.GetEnvironmentVariable);

    internal static bool IsReduced(Func<string, string?> environment) =>
        environment("MEOWCHAN_REDUCED_MOTION") is { } reduced && reduced.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on"
        || string.Equals(environment("MEOWCHAN_MOTION")?.Trim(), "off", StringComparison.OrdinalIgnoreCase);
}

using System;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>
/// The one place where the reader formats a post's date. Relative text is calculated only when a page is rendered;
/// this class owns no timer and therefore never causes a reader list to refresh by itself.
/// </summary>
internal static class ReaderDateText
{
    internal static string Posted(DateTimeOffset? postedAt) => Posted(postedAt, ReaderDateStyle.System);

    internal static string Posted(DateTimeOffset? postedAt, ReaderDateStyle style,
        DateTimeOffset? openingPostAt = null, DateTimeOffset? now = null)
    {
        if (postedAt is not DateTimeOffset posted) return string.Empty;
        DateTimeOffset local = posted.ToLocalTime();
        return style switch
        {
            ReaderDateStyle.Long => local.ToString("F", Texts.Culture),
            ReaderDateStyle.SinceOp when openingPostAt is DateTimeOffset opening =>
                SinceOpening(local, opening.ToLocalTime()),
            ReaderDateStyle.RelativeToNow => Relative(local, (now ?? DateTimeOffset.Now).ToLocalTime()),
            _ => local.ToString("g", Texts.Culture)
        };
    }

    /// <summary>The tooltip is intentionally independent of the selected compact date style.</summary>
    internal static string PostedFull(DateTimeOffset? postedAt) =>
        postedAt?.ToLocalTime().ToString("F", Texts.Culture) ?? string.Empty;

    internal static string SinceOpening(DateTimeOffset postedAt, DateTimeOffset openingPostAt)
    {
        TimeSpan elapsed = postedAt - openingPostAt;
        return Texts.T("reader.date.sinceOp", FormatDuration(elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed));
    }

    internal static string Relative(DateTimeOffset postedAt, DateTimeOffset now)
    {
        TimeSpan elapsed = now - postedAt;
        if (elapsed < TimeSpan.FromSeconds(1)) return Texts.T("reader.date.justNow");
        return Texts.T("reader.date.ago", FormatDuration(elapsed));
    }

    internal static (long Value, string UnitKey) DurationParts(TimeSpan elapsed)
    {
        if (elapsed.TotalDays >= 1)
            return ((long)Math.Floor(elapsed.TotalDays), "reader.date.unit.d");
        if (elapsed.TotalHours >= 1)
            return ((long)Math.Floor(elapsed.TotalHours), "reader.date.unit.h");
        if (elapsed.TotalMinutes >= 1)
            return ((long)Math.Floor(elapsed.TotalMinutes), "reader.date.unit.min");
        return (Math.Max(0, (long)Math.Floor(elapsed.TotalSeconds)), "reader.date.unit.s");
    }

    private static string FormatDuration(TimeSpan elapsed)
    {
        (long value, string unitKey) = DurationParts(elapsed);
        return Texts.T(unitKey, value);
    }
}

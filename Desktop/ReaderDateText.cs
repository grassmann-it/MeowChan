using System;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>
/// The one place where the reader formats a post's date. Today that is the system short format ("g"); the reader date
/// styles (P5) extend this seam. The full form (long date and time with seconds) is the header's tooltip.
/// </summary>
internal static class ReaderDateText
{
    internal static string Posted(DateTimeOffset? postedAt) =>
        postedAt?.ToLocalTime().ToString("g", Texts.Culture) ?? string.Empty;

    internal static string PostedFull(DateTimeOffset? postedAt) =>
        postedAt?.ToLocalTime().ToString("F", Texts.Culture) ?? string.Empty;
}

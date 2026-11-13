using System;
using System.Globalization;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>
/// The one file-size text of the desktop: decimal units (1 KB = 1000 B) that match what file managers and
/// the download limits call a megabyte, in the selected language's number format.
/// </summary>
internal static class SizeText
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    /// <summary>Whole bytes below 1 KB, then KB (a decimal only below 10 KB), then one decimal from MB. Never "0 MB" for a small file.</summary>
    public static string Format(long bytes, CultureInfo? culture = null)
    {
        double value = Math.Max(bytes, 0);
        for (int unit = 0; ; unit++)
        {
            int decimals = unit == 0 ? 0 : unit == 1 ? (value < 10 ? 1 : 0) : 1;
            double rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
            // 999.96 KB would read as "1000 KB"; carry it to the next unit instead.
            if (rounded < 1000 || unit == Units.Length - 1)
                return rounded.ToString(decimals == 0 ? "0" : "0.#", culture ?? Texts.Culture) + " " + Units[unit];
            value /= 1000;
        }
    }
}

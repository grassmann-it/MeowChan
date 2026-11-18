using System;
using System.Globalization;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>
/// The one file-size text of the desktop: binary units (1 KiB = 1024 B, 1 MiB = 1024 KiB, …), the same ones the
/// size limits and the bandwidth setting are written in, with the number in the selected language's format
/// ("1,5 MiB" in German, "1.5 MiB" in English). The unit names are not translated.
/// </summary>
internal static class SizeText
{
    private static readonly string[] Units = { "B", "KiB", "MiB", "GiB", "TiB" };

    /// <summary>Whole bytes below 1 KiB, then KiB (a decimal only below 10 KiB), then one decimal from MiB. Never "0 MiB" for a small file.</summary>
    public static string Format(long bytes, CultureInfo? culture = null)
    {
        double value = Math.Max(bytes, 0);
        for (int unit = 0; ; unit++)
        {
            int decimals = unit == 0 ? 0 : unit == 1 ? (value < 10 ? 1 : 0) : 1;
            double rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
            // 1023.96 KiB would read as "1024 KiB"; carry it to the next unit instead.
            if (rounded < 1024 || unit == Units.Length - 1)
                return rounded.ToString(decimals == 0 ? "0" : "0.#", culture ?? Texts.Culture) + " " + Units[unit];
            value /= 1024;
        }
    }
}

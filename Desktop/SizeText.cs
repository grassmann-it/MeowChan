using System;
using System.Globalization;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>
/// The file-size texts of the desktop, in the selected language's number format. <see cref="Format"/> is the
/// normal one: decimal units (1 KB = 1000 B) that match what file managers and the download limits call a
/// megabyte. <see cref="FormatBinary"/> serves the two technical surfaces (captured attachments, the Danbooru
/// window) that state binary units; which convention those should use is a separate product question.
/// </summary>
internal static class SizeText
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };
    private static readonly string[] BinaryUnits = { "B", "KiB", "MiB", "GiB" };

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

    /// <summary>Binary units (1 KiB = 1024 B): whole bytes below 1 KiB, then one decimal, capped at GiB.</summary>
    public static string FormatBinary(long bytes)
    {
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < BinaryUnits.Length - 1) { value /= 1024; unit++; }
        return (unit == 0 ? value.ToString("0", Texts.Culture) : value.ToString("0.#", Texts.Culture)) + " " + BinaryUnits[unit];
    }
}

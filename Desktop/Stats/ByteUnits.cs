using System;
using System.Globalization;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Stats;

/// <summary>
/// Shared byte sizes and rates in binary units (1 KiB = 1024 B), in the selected language's number format.
/// Sizes retain whole bytes/KiB and one decimal from MiB, carrying at 1024 through TiB.
/// Rates use at most three visible digits and carry at a rounded 1000 independently of sizes.
/// </summary>
internal static class ByteUnits
{
    private static readonly string[] Units = { "B", "KiB", "MiB", "GiB", "TiB" };

    internal static string Size(long bytes, CultureInfo? culture = null) => Format(Math.Max(bytes, 0), culture);

    private static readonly string[] RateUnits = { "B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB", "ZiB", "YiB" };

    /// <summary>
    /// Shared rate text: whole B/s, otherwise at most two decimals below 10, one below 100, none above; no trailing zeros.
    /// A rounded 1000 carries by dividing the unrounded value by 1024 (1000 B/s becomes 0.98 KiB/s).
    /// Negative/non-finite inputs read as zero. Finite overflow beyond the last compact unit reads as ">999 YiB/s".
    /// </summary>
    internal static string Rate(double bytesPerSecond, CultureInfo? culture = null)
    {
        double value = double.IsFinite(bytesPerSecond) && bytesPerSecond > 0 ? bytesPerSecond : 0;
        for (int unit = 0; unit < RateUnits.Length; unit++)
        {
            if (value < 1000)
            {
                int decimals = unit == 0 || value >= 100 ? 0 : value >= 10 ? 1 : 2;
                // Decimal rounding keeps display midpoints such as 9.995 from falling just below the carry in binary.
                decimal rounded = Math.Round((decimal)value, decimals, MidpointRounding.AwayFromZero);
                if (rounded < 1000)
                    return rounded.ToString("0.##", culture ?? Texts.Culture) + " " + RateUnits[unit] + "/s";
            }
            // Carry before formatting: never emit four digits or format an enormous finite double.
            value /= 1024;
        }
        return ">999 " + RateUnits[^1] + "/s";
    }

    private static string Format(double value, CultureInfo? culture)
    {
        if (!double.IsFinite(value) || value < 0) value = 0;
        for (int unit = 0; ; unit++)
        {
            int decimals = unit < 2 ? 0 : 1;
            double rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
            if (rounded < 1024 || unit == Units.Length - 1)
                return rounded.ToString(decimals == 0 ? "0" : "0.0", culture ?? Texts.Culture) + " " + Units[unit];
            value /= 1024;
        }
    }
}

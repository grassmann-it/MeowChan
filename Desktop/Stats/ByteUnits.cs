using System;
using System.Globalization;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Stats;

/// <summary>
/// The one byte and rate text of the statistics: binary units (1 KiB = 1024 B) in the selected language's number format.
/// Whole bytes below 1 KiB, whole KiB below 1 MiB, one decimal from MiB (GiB, TiB only as overflow). A value that would
/// round up to 1024 of its unit is carried into the next unit instead ("1024 KiB" never appears).
/// </summary>
internal static class ByteUnits
{
    private static readonly string[] Units = { "B", "KiB", "MiB", "GiB", "TiB" };

    internal static string Size(long bytes, CultureInfo? culture = null) => Format(Math.Max(bytes, 0), culture);

    /// <summary>The rate text, for example "2,4 MiB/s". Negative and non-finite values read as zero.</summary>
    internal static string Rate(double bytesPerSecond, CultureInfo? culture = null) => Format(bytesPerSecond, culture) + "/s";

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

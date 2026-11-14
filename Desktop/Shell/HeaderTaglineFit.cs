using System;

namespace MeowChan.Desktop;

/// <summary>Which form of the header tagline is shown: the whole sentence, only the quip behind a dash, or nothing.</summary>
internal enum HeaderTaglineStage { None, Quip, Full }

/// <summary>
/// The tagline never shows a cut-off text. Given the width of the star column that holds it and the measured widths of both forms
/// ("lead — quip" and "— quip"), it picks the whole sentence if that fits, else the short form if that fits, else nothing. Pure.
/// </summary>
internal static class HeaderTaglineFit
{
    /// <summary>A form must be at least this much narrower than the column (dip): rounding in layout must never tip a fitting text into an ellipsis.</summary>
    internal const double Tolerance = 1;

    /// <summary>Full when <c>fullWidth &lt;= available - 1</c>, else Quip when <c>quipWidth &lt;= available - 1</c>, else None. An unusable column (zero, negative, NaN, infinite: not laid out yet) shows nothing.</summary>
    internal static HeaderTaglineStage Choose(double available, double fullWidth, double quipWidth)
    {
        if (!double.IsFinite(available) || available <= 0) return HeaderTaglineStage.None;
        if (Fits(fullWidth, available)) return HeaderTaglineStage.Full;
        if (Fits(quipWidth, available)) return HeaderTaglineStage.Quip;
        return HeaderTaglineStage.None;
    }

    // An unmeasured form (zero, NaN, infinite) is not one that fits.
    private static bool Fits(double width, double available) => double.IsFinite(width) && width > 0 && width <= available - Tolerance;
}

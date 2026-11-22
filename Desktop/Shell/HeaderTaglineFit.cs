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
    /// <summary>Minimum clearance in DIP at the trailing clip edge.</summary>
    internal const double Tolerance = 1;

    /// <summary>Pick the longest fitting form with at least one physical pixel to spare for layout and rasterization.</summary>
    internal static HeaderTaglineStage Choose(double available, double fullWidth, double quipWidth, double scale = 1)
    {
        if (!double.IsFinite(available) || available <= 0) return HeaderTaglineStage.None;
        double limit = available - Math.Max(Tolerance, 1 / scale);
        if (Fits(fullWidth, limit)) return HeaderTaglineStage.Full;
        if (Fits(quipWidth, limit)) return HeaderTaglineStage.Quip;
        return HeaderTaglineStage.None;
    }

    // An unmeasured form (zero, NaN, infinite) is not one that fits.
    private static bool Fits(double width, double limit) => double.IsFinite(width) && width > 0 && width <= limit;
}

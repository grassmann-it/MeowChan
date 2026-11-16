using System;
using MeowChan.Desktop.Stats;

namespace MeowChan.Desktop;

/// <summary>
/// Whether the header display (the network and disk values beside the header icons) fits beside the fixed parts of the header
/// (narrow-window sweep, Tom 16.11.2026). The fixed breakpoint of <c>MainWindow.HeaderMonitorShown</c> was sized for the English words:
/// in Czech, German or French ("Stahování / Odesílání", "Download / Schreiben") the display, the icons and the menu needed more than the
/// 760 dip window and ran past the header into the window controls, while the tagline column fell to zero. This model computes the width
/// of the display from the measured words and the widest rate the value columns can show, so the display gives way exactly when it no
/// longer fits in the current language. Pure; the window measures the words and the fixed parts and applies the result.
/// </summary>
/// <remarks>
/// The numbers mirror the header XAML and <c>MainWindow.ApplyHeaderGraphMode</c>: the button's 6 dip side padding, 4 dip between the
/// columns of a value matrix, 8 dip before the second direction of a one-line row, the 56 dip floor of a rate column, the graphs and their
/// gaps. The rate column is reserved for its widest text ("9.99 MiB/s"), not for the current value, so a rising rate never tips a fitting
/// display over the edge and the decision does not flicker with the traffic.
/// </remarks>
internal static class HeaderMonitorFit
{
    internal const double ButtonPadding = 12, ColumnSpacing = 4, SecondPairGap = 8, RateMinimum = 56;
    internal const double RowGraphWidth = 128, BlockGraphWidth = 120, TextGap = 8, BlockGap = 24;

    /// <summary>A rate whose text is among the widest the value columns show: three significant digits, a separator and a binary unit.</summary>
    internal const double WidestRateBytesPerSecond = 9.99 * 1024 * 1024;

    /// <summary>The widest rate text in the current language's number format ("9.99 MiB/s", "9,99 MiB/s").</summary>
    internal static string WidestRateText() => ByteUnits.Rate(WidestRateBytesPerSecond);

    /// <summary>
    /// The width in dip of the display for <paramref name="layout"/>: the words (or icons) of each direction and the reserved rate column.
    /// Zero when the display shows nothing.
    /// </summary>
    internal static double Width(IoStatsDisplay.HeaderLayout layout, double down, double up, double read, double write, double rate)
    {
        if (layout.Hidden || !(layout.NetValue || layout.DiskValue)) return 0;
        double column = Math.Max(RateMinimum, Finite(rate));
        if (layout.TwoLineLabels)
        {
            // Blocks side by side: each a graph (optional) and a two-line matrix of word and rate; 24 dip between two blocks.
            double Block(bool shown, bool graph, double first, double second) =>
                !shown ? 0 : (graph ? BlockGraphWidth + TextGap : 0) + Math.Max(Finite(first), Finite(second)) + ColumnSpacing + column;
            double net = Block(layout.NetValue, layout.NetGraph, down, up), disk = Block(layout.DiskValue, layout.DiskGraph, read, write);
            return ButtonPadding + net + disk + (net > 0 && disk > 0 ? BlockGap : 0);
        }
        // One-line rows: the words and rates of both rows share their columns (SharedSizeGroup), the graphs share the first column.
        double firsts = Math.Max(layout.NetValue ? Finite(down) : 0, layout.DiskValue ? Finite(read) : 0);
        double seconds = Math.Max(layout.NetValue ? Finite(up) : 0, layout.DiskValue ? Finite(write) : 0);
        double graphs = layout.NetGraph || layout.DiskGraph ? RowGraphWidth + TextGap : 0;
        return ButtonPadding + graphs + firsts + ColumnSpacing + column + ColumnSpacing + SecondPairGap + seconds + ColumnSpacing + column;
    }

    /// <summary>
    /// Whether a display of <paramref name="monitor"/> dip fits into a header of <paramref name="room"/> dip whose other parts (branding,
    /// column gaps, icons, menu) take <paramref name="fixedParts"/> dip; <paramref name="gap"/> is the spacing the display adds before the
    /// icons. An unknown room or unmeasured parts (zero, negative, NaN: not laid out yet) do not hide anything.
    /// </summary>
    internal static bool Fits(double room, double fixedParts, double monitor, double gap)
    {
        if (!double.IsFinite(room) || room <= 0 || !double.IsFinite(fixedParts) || fixedParts <= 0 || !double.IsFinite(monitor) || monitor <= 0) return true;
        return fixedParts + monitor + Math.Max(0, Finite(gap)) <= room - HeaderTaglineFit.Tolerance;
    }

    private static double Finite(double value) => double.IsFinite(value) && value > 0 ? value : 0;
}

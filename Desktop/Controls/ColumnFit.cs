using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// "Fit" widths of table columns. The width of a column is the wider of its header (text + sort arrow + header padding) and a content
/// pattern (the widest value the column can show, e.g. five digits or the longest date of the culture) plus the cell padding, clamped by the
/// column's own layout Min/Max. This is layout geometry of the row, not a limit on user data, so there is no setting for it (like
/// MediaToolbarLayout). The pattern is measured once per language, culture and font (the caller caches the result); nothing here runs per
/// row or while scrolling, and no SizeToCells is involved, so widths never jump while the list scrolls.
/// </summary>
internal static class ColumnFit
{
    /// <summary>Sort arrow slot of a column header.</summary>
    internal const double SortArrow = 16;
    /// <summary>Horizontal padding of a column header (WorkspaceStyles: Padding 10,7).</summary>
    internal const double HeaderPadding = 20;
    /// <summary>Horizontal padding of a template column's cell (WorkspaceStyles: DataGridCell Padding 12,0).</summary>
    internal const double CellPadding = 24;
    /// <summary>A text column draws its text with a further 12 dip margin on each side inside the cell.</summary>
    internal const double TextColumnExtraPadding = 24;

    /// <summary>
    /// The fit width: the wider of header and content (each with its padding), rounded up to whole dip, then clamped into
    /// [<paramref name="min"/>, <paramref name="max"/>]. A maximum below the minimum gives the minimum. Non-finite measures count as zero.
    /// </summary>
    internal static double Width(double headerText, double contentText, double min, double max,
        double cellPadding = CellPadding, bool sortable = true)
    {
        double header = Finite(headerText) + (sortable ? SortArrow : 0) + HeaderPadding;
        double content = Finite(contentText) + cellPadding;
        double wanted = Math.Ceiling(Math.Max(header, content));
        return Math.Min(Math.Max(wanted, min), Math.Max(min, max));
    }

    private static double Finite(double value) => double.IsFinite(value) && value > 0 ? value : 0;

    /// <summary>The widest text of <paramref name="samples"/> under <paramref name="measure"/>; 0 without samples.</summary>
    internal static double Widest(IEnumerable<string> samples, Func<string, double> measure)
    {
        double widest = 0;
        foreach (string sample in samples)
            if (!string.IsNullOrEmpty(sample)) widest = Math.Max(widest, Finite(measure(sample)));
        return widest;
    }

    /// <summary>
    /// The pattern of a number column: <c>max(value, 99 999)</c> written with the widest digit, so it never grows while the
    /// numbers stay below five digits and always holds the larger ones.
    /// </summary>
    internal static string NumberPattern(long maxValue)
    {
        int digits = Math.Max(5, Math.Max(0, maxValue).ToString(CultureInfo.InvariantCulture).Length);
        return new string('8', digits);
    }

    /// <summary>
    /// Date patterns of a culture: the general short pattern "g" of the longest date of the year and both ends of the day, so a culture
    /// with a 12-hour clock is measured with PM as well as AM.
    /// </summary>
    internal static string[] DatePatterns(CultureInfo culture) =>
    [
        new DateTime(2026, 12, 28, 23, 58, 0).ToString("g", culture),
        new DateTime(2026, 12, 28, 11, 58, 0).ToString("g", culture)
    ];

    /// <summary>Cache key of one measurement: what changes the text or its font changes the key.</summary>
    internal static string CacheKey(string gridName, string language, string culture, string fontFamily, double bodySize, double headerSize) =>
        string.Join('|', gridName, language, culture, fontFamily, bodySize.ToString("R", CultureInfo.InvariantCulture),
            headerSize.ToString("R", CultureInfo.InvariantCulture));

    /// <summary>
    /// Measures a text with the font of a table (tabular figures like every DataGrid cell). Needs the platform's text engine, which an
    /// application window or a smoke has; a failure returns 0 so the caller falls back to its default width.
    /// </summary>
    internal sealed class Measurer
    {
        private readonly FontFamily _family;
        private readonly FontFeatureCollection? _features;
        private readonly Dictionary<(string Text, double Size, FontWeight Weight), double> _cache = new();

        internal Measurer(FontFamily family)
        {
            _family = family;
            try { _features = FontFeatureCollection.Parse("tnum"); }
            catch (Exception) { _features = null; }
        }

        internal double Measure(string text, double size, FontWeight weight = FontWeight.Normal)
        {
            if (string.IsNullOrEmpty(text) || !double.IsFinite(size) || size <= 0) return 0;
            if (_cache.TryGetValue((text, size, weight), out double known)) return known;
            double width = 0;
            try
            {
                TextBlock probe = new()
                {
                    Text = text, FontFamily = _family, FontSize = size, FontWeight = weight,
                    TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.None
                };
                if (_features != null) probe.FontFeatures = _features;
                probe.Measure(Size.Infinity);
                width = probe.DesiredSize.Width;
            }
            catch (Exception) { width = 0; }
            _cache[(text, size, weight)] = width;
            return width;
        }
    }
}

/// <summary>
/// The text of a table cell that never wraps and trims with an ellipsis; the tooltip carries the full text and exists only while the
/// text is actually shortened (a value that fits needs no tooltip). The state is read after each arrange from the layout that was already
/// built, so nothing is measured a second time.
/// </summary>
internal sealed class TrimTipTextBlock : TextBlock
{
    protected override Type StyleKeyOverride => typeof(TextBlock);

    public TrimTipTextBlock()
    {
        TextWrapping = TextWrapping.NoWrap;
        TextTrimming = TextTrimming.CharacterEllipsis;
    }

    private bool _alwaysTip;

    /// <summary>
    /// Keeps the full text as the tooltip while it fits too (a compact list row's file line, whose tooltip is always its name). Off by default:
    /// the tip exists only while the text is shortened. The text may also wrap (set TextWrapping and MaxLines after construction): a text cut
    /// after its last allowed line counts as shortened.
    /// </summary>
    internal bool AlwaysTip
    {
        get => _alwaysTip;
        set { if (_alwaysTip == value) return; _alwaysTip = value; InvalidateArrange(); }
    }

    /// <summary>Whether the text is shortened in the current layout (its last line collapsed by the ellipsis).</summary>
    internal bool IsTrimmed => !string.IsNullOrEmpty(Text) && TextLayout.TextLines.Any(line => line.HasCollapsed);

    protected override Size ArrangeOverride(Size finalSize)
    {
        Size arranged = base.ArrangeOverride(finalSize);
        bool trimmed = IsTrimmed;
        // Recycled rows change the text, so the tip is compared with the current text, not only with the trimmed state.
        object? tip = trimmed || (_alwaysTip && !string.IsNullOrEmpty(Text)) ? Text : null;
        if (!Equals(ToolTip.GetTip(this), tip)) ToolTip.SetTip(this, tip);
        return arranged;
    }
}

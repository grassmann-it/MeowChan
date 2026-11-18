using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// One row of items that never runs out of its room (narrow-window sweep, Tom 16.11.2026: "headings and texts vanish into nowhere in
/// small windows"). Every visible item keeps its natural width while the row fits. When it does not, the widest items give way first and
/// are cut to one common width (the narrow ones stay whole, nothing drops below <see cref="MinimumShrunkWidth"/>), so a short state
/// such as "Paused" stays readable next to a long counter line instead of being pushed out by it. A text that lost characters ends in its
/// own ellipsis (<c>TextTrimming</c>) and shows its full text as tooltip, unless the code gave it a tooltip of its own.
/// </summary>
/// <remarks>
/// A <see cref="Grid"/> with <c>*,Auto,Auto</c> measures its Auto columns without a limit: a long counter line took all the room, the star
/// column fell to zero and the Auto texts ran past the window edge without an ellipsis. With <see cref="FillFirst"/> the room left over goes
/// to the first item, so the others stand at the right edge (the shell status line: state left, counters right).
/// </remarks>
public sealed class ShrinkRowPanel : Panel
{
    /// <summary>The gap between two visible items, in dip.</summary>
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<ShrinkRowPanel, double>(nameof(Spacing), 8);

    /// <summary>Whether the first visible item takes the room the row does not need (the others then stand at the right edge).</summary>
    public static readonly StyledProperty<bool> FillFirstProperty =
        AvaloniaProperty.Register<ShrinkRowPanel, bool>(nameof(FillFirst));

    /// <summary>The width below which an item is never shrunk any further (an ellipsis and a character or two): a layout floor, not a setting.</summary>
    internal const double MinimumShrunkWidth = 24;

    static ShrinkRowPanel() => AffectsMeasure<ShrinkRowPanel>(SpacingProperty, FillFirstProperty);

    /// <inheritdoc cref="SpacingProperty"/>
    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <inheritdoc cref="FillFirstProperty"/>
    public bool FillFirst
    {
        get => GetValue(FillFirstProperty);
        set => SetValue(FillFirstProperty, value);
    }

    /// <summary>
    /// The widths the items get in <paramref name="available"/> dip: their natural widths when they fit, otherwise one common level for every
    /// item wider than it (the level that makes the row exactly as wide as the room), never below <paramref name="floor"/>. Pure, for tests.
    /// </summary>
    internal static double[] Allocate(IReadOnlyList<double> natural, double available, double spacing, double floor = MinimumShrunkWidth)
    {
        ArgumentNullException.ThrowIfNull(natural);
        double[] widths = natural.Select(width => double.IsFinite(width) && width > 0 ? width : 0).ToArray();
        if (widths.Length == 0 || !double.IsFinite(available)) return widths;
        double room = Math.Max(0, available - Math.Max(0, spacing) * (widths.Length - 1));
        if (widths.Sum() <= room) return widths;
        // Water filling: the narrow items stay whole while the rest shares what is left equally.
        double[] ascending = widths.OrderBy(width => width).ToArray();
        double remaining = room, level = 0;
        for (int index = 0; index < ascending.Length; index++)
        {
            int left = ascending.Length - index;
            if (ascending[index] * left <= remaining) { remaining -= ascending[index]; continue; }
            level = remaining / left;
            break;
        }
        level = Math.Max(level, Math.Max(0, floor));
        for (int index = 0; index < widths.Length; index++) widths[index] = Math.Min(widths[index], level);
        return widths;
    }

    private readonly List<Control> _row = new();
    private readonly Dictionary<TextBlock, string> _tips = new();
    private double[] _natural = [];

    /// <summary>The visible items that lost width in the last arrange (tests and smokes).</summary>
    internal IReadOnlyList<Control> ShrunkItems { get; private set; } = [];

    protected override Size MeasureOverride(Size availableSize)
    {
        _row.Clear();
        foreach (Control child in Children)
            if (child.IsVisible) _row.Add(child);
        Size open = new(double.PositiveInfinity, availableSize.Height);
        foreach (Control child in _row) child.Measure(open);
        _natural = _row.Select(child => child.DesiredSize.Width).ToArray();
        double[] widths = Allocate(_natural, availableSize.Width, Spacing);
        double height = 0;
        for (int index = 0; index < _row.Count; index++)
        {
            if (widths[index] < _natural[index] - 0.01) _row[index].Measure(new Size(widths[index], availableSize.Height));
            height = Math.Max(height, _row[index].DesiredSize.Height);
        }
        double used = widths.Sum() + Spacing * Math.Max(0, widths.Length - 1);
        return new Size(double.IsFinite(availableSize.Width) ? Math.Min(used, availableSize.Width) : used, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double[] widths = Allocate(_natural, finalSize.Width, Spacing);
        double used = widths.Sum() + Spacing * Math.Max(0, widths.Length - 1);
        double extra = FillFirst ? Math.Max(0, finalSize.Width - used) : 0;
        List<Control> shrunk = new();
        double x = 0;
        for (int index = 0; index < _row.Count && index < widths.Length; index++)
        {
            Control child = _row[index];
            bool cut = widths[index] < _natural[index] - 0.5;
            if (cut) shrunk.Add(child);
            double width = widths[index] + (index == 0 ? extra : 0);
            child.Arrange(new Rect(x, 0, Math.Max(0, Math.Min(width, finalSize.Width - x)), finalSize.Height));
            if (child is TextBlock text) UpdateTip(text, cut);
            x += width + Spacing;
        }
        ShrunkItems = shrunk;
        return finalSize;
    }

    // The full text as tooltip while a text is cut, but never over a tooltip the code set itself (the counters' details, a startup error).
    private void UpdateTip(TextBlock text, bool cut)
    {
        object? current = ToolTip.GetTip(text);
        bool ours = _tips.TryGetValue(text, out string? set) && Equals(current, set);
        if (cut && !string.IsNullOrEmpty(text.Text))
        {
            if (current != null && !ours) { _tips.Remove(text); return; }
            if (!Equals(current, text.Text)) ToolTip.SetTip(text, text.Text);
            _tips[text] = text.Text!;
        }
        else if (_tips.Remove(text) && ours) ToolTip.SetTip(text, null);
    }
}

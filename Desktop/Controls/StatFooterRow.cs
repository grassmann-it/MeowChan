using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Media;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Controls;

/// <summary>One stat of a <see cref="StatFooterRow"/>: a stable id, a catalog icon, the short label, the count and its rank (lower stays longer when the row is narrow).</summary>
internal sealed record StatFooterItem(string Id, string Icon, string Label, long Value, int Priority);

/// <summary>
/// The small footer of a list card that shows a few counts (Sources: Threads, Boards, Queued, Active). A rule above it and a little
/// space set it apart from the card text; the look lives in WorkspaceStyles.axaml (<c>controls|StatFooterRow</c>, <c>stat-value</c>,
/// <c>stat-label</c>) on the workspace tokens.
/// <para>Stable by construction: every segment has the same slot width and reserves the width of five tabular characters for its
/// value, so a count that changes from 0 to 10 to 1000 never moves a neighbour or reflows the card. The row never wraps. When the card
/// is too narrow the labels go and the icons stay (the tooltip and the accessible name still say "Threads: 73"); narrower still, only the
/// most important counts stay (<see cref="StatFooterItem.Priority"/>). Which of the three it is depends on the width only, never on the
/// numbers (<see cref="StatFooterLayout"/>). Counts of <see cref="StatFooterLayout.AbbreviateFrom"/> and more are abbreviated, the
/// tooltip keeps the exact number.</para>
/// </summary>
internal sealed class StatFooterRow : Border
{
    private readonly StatFooterPanel _panel = new();

    public StatFooterRow()
    {
        Child = _panel;
        // Each segment carries its own accessible name; the row itself is only a group.
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Content);
    }

    internal IReadOnlyList<StatSegmentView> Segments => _panel.Segments;
    internal StatFooterMode Mode => _panel.Mode;
    internal int VisibleCount => _panel.VisibleCount;

    /// <summary>Shows the items. The same ids keep their segments (only text changes); another set of ids rebuilds them.</summary>
    internal void Update(IReadOnlyList<StatFooterItem> items)
    {
        IReadOnlyList<StatSegmentView> current = _panel.Segments;
        bool same = current.Count == items.Count;
        for (int i = 0; same && i < items.Count; i++) same = current[i].Id == items[i].Id && current[i].Icon == items[i].Icon;
        if (!same)
        {
            _panel.Children.Clear();
            foreach (StatFooterItem item in items) _panel.Children.Add(new StatSegmentView(item.Id, item.Icon));
        }
        for (int i = 0; i < items.Count; i++) _panel.Segments[i].Update(items[i]);
    }
}

/// <summary>
/// One segment: the icon and the value on a line, the label under them. It lays itself out (no wrapping, no value-dependent width):
/// the width it asks for is that of the icon plus the reserved value width, or the label if that is wider.
/// </summary>
internal sealed class StatSegmentView : Panel
{
    /// <summary>Icon size (the 16 dip icon token) and the gap between icon and value (space 4).</summary>
    internal const double IconSize = 16, IconGap = 4;

    private readonly Control _icon;
    private readonly TextBlock _value = Text("stat-value");
    private readonly TextBlock _reserve = Text("stat-value");
    private readonly TextBlock _label = Text("stat-label");
    private bool _showLabel = true;
    private double _valueWidth, _labelWidth, _rowHeight;

    internal StatSegmentView(string id, string icon)
    {
        Id = id; Icon = icon;
        _icon = CommandBindings.NamedIcon(icon, (int)IconSize);
        _icon.Width = _icon.Height = IconSize;
        // The reserve is never seen or read: it only holds the width of the widest value so the column cannot change with the number.
        _reserve.Text = StatFooterLayout.ReservedValueText;
        _reserve.Opacity = 0;
        foreach (Control part in new Control[] { _icon, _value, _reserve, _label })
        {
            AutomationProperties.SetAccessibilityView(part, AccessibilityView.Raw);
            part.IsHitTestVisible = false;
            Children.Add(part);
        }
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Content);
        Focusable = false;
        ClipToBounds = true;
        // The parts do not take the pointer; a transparent fill makes the whole slot hover-able, so the tooltip shows over the icon, the number and the gap.
        Background = Brushes.Transparent;
    }

    internal string Id { get; }
    internal string Icon { get; }
    internal string ValueText => _value.Text ?? string.Empty;
    internal string LabelText => _label.Text ?? string.Empty;
    internal bool LabelShown => _showLabel;
    internal int Priority { get; private set; }

    /// <summary>Whether the label is drawn under the icon and value; set by the row's panel from the width.</summary>
    internal bool ShowLabel
    {
        get => _showLabel;
        set { if (_showLabel == value) return; _showLabel = value; InvalidateMeasure(); }
    }

    internal void Update(StatFooterItem item)
    {
        Priority = item.Priority;
        string shown = StatFooterLayout.Format(item.Value, Texts.Culture);
        string full = Texts.T("stats.footer.tip", item.Label, StatFooterLayout.FormatFull(item.Value, Texts.Culture));
        if (_value.Text != shown) _value.Text = shown;
        if (_label.Text != item.Label) _label.Text = item.Label;
        // The name and the tooltip always carry the label and the exact number, also when the row shows icons and abbreviations only.
        AutomationProperties.SetName(this, full);
        AutomationProperties.SetHelpText(this, full);
        if (!Equals(ToolTip.GetTip(this), full)) ToolTip.SetTip(this, full);
    }

    /// <summary>The widths a slot needs with and without the label. Measures the parts itself, so it works while this segment is hidden.</summary>
    internal (double Full, double IconsOnly) NaturalWidths()
    {
        Size unbounded = new(double.PositiveInfinity, double.PositiveInfinity);
        _icon.Measure(unbounded); _value.Measure(unbounded); _reserve.Measure(unbounded); _label.Measure(unbounded);
        _valueWidth = Math.Max(_value.DesiredSize.Width, _reserve.DesiredSize.Width);
        _labelWidth = _label.DesiredSize.Width;
        _rowHeight = Math.Max(IconSize, Math.Max(_value.DesiredSize.Height, _reserve.DesiredSize.Height));
        double iconsOnly = IconSize + IconGap + _valueWidth;
        return (Math.Max(iconsOnly, _labelWidth), iconsOnly);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        (double full, double iconsOnly) = NaturalWidths();
        double width = double.IsInfinity(availableSize.Width) ? (_showLabel ? full : iconsOnly) : availableSize.Width;
        double height = _rowHeight + (_showLabel ? _label.DesiredSize.Height : 0);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _icon.Arrange(new Rect(0, (_rowHeight - IconSize) / 2, IconSize, IconSize));
        double valueX = IconSize + IconGap;
        double valueWidth = Math.Max(0, Math.Min(_valueWidth, finalSize.Width - valueX));
        Rect valueRect = new(valueX, (_rowHeight - _value.DesiredSize.Height) / 2, valueWidth, _value.DesiredSize.Height);
        _value.Arrange(valueRect);
        _reserve.Arrange(new Rect(valueX, (_rowHeight - _reserve.DesiredSize.Height) / 2, valueWidth, _reserve.DesiredSize.Height));
        // Hidden label: zero-size slot and no opacity, so it neither draws nor takes room, without toggling visibility inside layout.
        _label.Opacity = _showLabel ? 1 : 0;
        _label.Arrange(_showLabel ? new Rect(0, _rowHeight, finalSize.Width, _label.DesiredSize.Height) : default);
        return finalSize;
    }

    private static TextBlock Text(string cssClass)
    {
        TextBlock text = new() { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        text.Classes.Add(cssClass);
        return text;
    }
}

/// <summary>Arranges the segments in equal slots in item order and decides mode and visible segments from the width alone.</summary>
internal sealed class StatFooterPanel : Panel
{
    /// <summary>Gap between two slots (space 8).</summary>
    internal const double SlotGap = 8;

    internal IReadOnlyList<StatSegmentView> Segments => Children.OfType<StatSegmentView>().ToArray();
    internal StatFooterMode Mode { get; private set; } = StatFooterMode.Full;
    internal int VisibleCount { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        StatSegmentView[] segments = Children.OfType<StatSegmentView>().ToArray();
        if (segments.Length == 0) { VisibleCount = 0; return default; }
        double full = 0, iconsOnly = 0;
        foreach (StatSegmentView segment in segments)
        {
            (double f, double i) = segment.NaturalWidths();
            full = Math.Max(full, f); iconsOnly = Math.Max(iconsOnly, i);
        }
        StatFooterLayout.Plan plan = StatFooterLayout.Resolve(availableSize.Width, segments.Length, full, iconsOnly, SlotGap);
        Mode = plan.Mode; VisibleCount = plan.VisibleCount;
        bool[] visible = StatFooterLayout.VisibleSet(segments.Select(segment => segment.Priority).ToArray(), plan.VisibleCount);
        int shown = Math.Max(1, plan.VisibleCount);
        bool unbounded = double.IsInfinity(availableSize.Width);
        double slot = unbounded ? (plan.Mode == StatFooterMode.Full ? full : iconsOnly) : Slot(availableSize.Width, shown);
        double height = 0;
        for (int i = 0; i < segments.Length; i++)
        {
            StatSegmentView segment = segments[i];
            // Visibility changes only when the width crosses a threshold, never because a number changed.
            if (segment.IsVisible != visible[i]) segment.IsVisible = visible[i];
            segment.ShowLabel = plan.Mode == StatFooterMode.Full;
            if (!visible[i]) continue;
            segment.Measure(new Size(slot, double.PositiveInfinity));
            height = Math.Max(height, segment.DesiredSize.Height);
        }
        return new Size(unbounded ? shown * slot + SlotGap * (shown - 1) : availableSize.Width, height);
    }

    /// <summary>The equal slot width, rounded down to a whole dip so layout rounding can never push the last slot past the row.</summary>
    private static double Slot(double width, int shown) => Math.Max(0, Math.Floor((width - SlotGap * (shown - 1)) / shown));

    protected override Size ArrangeOverride(Size finalSize)
    {
        int shown = Math.Max(1, Children.OfType<StatSegmentView>().Count(segment => segment.IsVisible));
        double slot = Slot(finalSize.Width, shown);
        double x = 0;
        foreach (StatSegmentView segment in Children.OfType<StatSegmentView>())
        {
            if (!segment.IsVisible) { segment.Arrange(default); continue; }
            segment.Arrange(new Rect(x, 0, Math.Max(0, slot), finalSize.Height));
            x += slot + SlotGap;
        }
        return finalSize;
    }
}

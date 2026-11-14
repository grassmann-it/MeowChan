using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// The one header or tool row with a way out (Tom's rule, 14.11.2026): back, close, "close details" and every "Back to …" stand at
/// the far right edge of their row, apart from the row's own content and actions. Children marked
/// <see cref="IsEndProperty"/> form the end slot; everything else fills the start area to its left. Between both lie
/// <see cref="EndGap"/> dip of empty room (the 24 dip the quick-tag header set first), so the way out never reads as one more action.
/// </summary>
/// <remarks>
/// <para>Keyboard order (documented choice): the row's own content first, the way out last, in reading order. Tab order
/// follows the children order, so end-slot children are declared after the start content (in XAML and through
/// <see cref="Create"/>); <see cref="EndSlotIsLast"/> is the check the contract tests run. Escape stays with each owner, unchanged:
/// the bar only places the controls, it neither handles keys nor renames anything.</para>
/// <para>The gallery viewer keeps its own overlay placement of the same rule (back alone at the top right, star directly left of
/// it, GalleryViewer.Favorites.cs); this panel is the pattern for every ordinary row.</para>
/// <para>Hidden end children take no room; with no visible end child the row has no gap either. Start children
/// share the start area (one cell, like a single-cell Grid), so a row normally has one start child: a title, a toolbar or a
/// WrapPanel of actions.</para>
/// </remarks>
public sealed class HeaderBar : Panel
{
    /// <summary>The isolating room between the row's content and its way out, in dip (on the 8 dip grid).</summary>
    public const double EndGap = 24;

    /// <summary>The room between two controls of the end slot (e.g. "Back to gallery" and "Close"), in dip.</summary>
    public const double EndSpacing = 8;

    /// <summary>Marks a child as part of the end slot at the right edge.</summary>
    public static readonly AttachedProperty<bool> IsEndProperty =
        AvaloniaProperty.RegisterAttached<HeaderBar, Control, bool>("IsEnd");

    private double? _gapStart;

    static HeaderBar()
    {
        IsEndProperty.Changed.AddClassHandler<Control>((control, _) => (control.Parent as HeaderBar)?.InvalidateMeasure());
    }

    public static bool GetIsEnd(Control control) => control.GetValue(IsEndProperty);

    public static void SetIsEnd(Control control, bool value) => control.SetValue(IsEndProperty, value);

    /// <summary>Where the isolating gap begins (x in dip), or null while the row has no visible end slot or no start content.</summary>
    internal double? GapStart => _gapStart;

    /// <summary>A row with <paramref name="start"/> on the left and <paramref name="end"/> at the right edge, in this order.</summary>
    internal static HeaderBar Create(Control start, params Control[] end)
    {
        HeaderBar bar = new();
        bar.Children.Add(start);
        foreach (Control control in end) { SetIsEnd(control, true); bar.Children.Add(control); }
        return bar;
    }

    /// <summary>The end-slot children in their order (left to right, which is also their tab order).</summary>
    internal IReadOnlyList<Control> EndChildren => Children.Where(GetIsEnd).ToArray();

    /// <summary>True when no start child follows an end child: the way out is last in the row's tab order.</summary>
    internal bool EndSlotIsLast
    {
        get
        {
            bool seenEnd = false;
            foreach (Control child in Children)
            {
                if (GetIsEnd(child)) seenEnd = true;
                else if (seenEnd) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Turns <paramref name="button"/> into the icon-only close of a row or a modal header: the drawn cross (WorkspaceIcons.Close) on
    /// an input-height square, <paramref name="name"/> as accessible name and tooltip (<paramref name="tip"/> when given, e.g.
    /// "Close · Esc"). Calling it again only renames it (language change). The icon is never the only name.
    /// </summary>
    internal static void MakeClose(Button button, string name, string? tip = null)
    {
        if (WorkspaceIcons.KeyOf(button.Content) == WorkspaceIcons.Close && button.Classes.Contains(WorkspaceIcons.SquareClass))
        {
            AutomationProperties.SetName(button, name);
            ToolTip.SetTip(button, tip ?? name);
            return;
        }
        WorkspaceIcons.MakeSquare(button, WorkspaceIcons.Close, name, tip);
    }

    private IEnumerable<Control> Visible(bool end) => Children.Where(child => child.IsVisible && GetIsEnd(child) == end);

    private static double EndWidth(IEnumerable<Control> end)
    {
        double width = 0;
        int count = 0;
        foreach (Control control in end) { width += control.DesiredSize.Width; count++; }
        return count == 0 ? 0 : width + EndSpacing * (count - 1);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Control[] end = Visible(end: true).ToArray();
        double height = 0;
        foreach (Control control in end)
        {
            control.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            height = Math.Max(height, control.DesiredSize.Height);
        }
        double endWidth = EndWidth(end);
        bool hasStart = Visible(end: false).Any();
        double gap = end.Length > 0 && hasStart ? EndGap : 0;
        double startAvailable = Math.Max(0, availableSize.Width - endWidth - gap);
        double startWidth = 0;
        foreach (Control control in Children.Where(child => !GetIsEnd(child)))
        {
            control.Measure(new Size(startAvailable, availableSize.Height));
            startWidth = Math.Max(startWidth, control.DesiredSize.Width);
            height = Math.Max(height, control.DesiredSize.Height);
        }
        return new Size(startWidth + gap + endWidth, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Control[] end = Visible(end: true).ToArray();
        double endWidth = EndWidth(end);
        bool hasStart = Visible(end: false).Any();
        double gap = end.Length > 0 && hasStart ? EndGap : 0;
        double startWidth = Math.Max(0, finalSize.Width - endWidth - gap);
        foreach (Control control in Children.Where(child => !GetIsEnd(child)))
            control.Arrange(new Rect(0, 0, startWidth, finalSize.Height));
        double x = finalSize.Width - endWidth;
        foreach (Control control in end)
        {
            control.Arrange(new Rect(x, 0, control.DesiredSize.Width, finalSize.Height));
            x += control.DesiredSize.Width + EndSpacing;
        }
        _gapStart = gap > 0 ? startWidth : null;
        return finalSize;
    }
}

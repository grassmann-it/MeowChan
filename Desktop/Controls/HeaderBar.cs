using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Metadata;
using Avalonia.VisualTree;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// The one header or tool row with a way out (Tom's rule, 14.11.2026 evening, which reverses the earlier right-edge rule): back,
/// close, "close details" and every "Back to …" stand at the far left of their row, before the title, so they read as "back".
/// Children marked <see cref="IsBackProperty"/> form the back slot; children marked <see cref="IsEndProperty"/> are the row's remaining
/// buttons, right-aligned; everything else is the row's content (title, toolbar) and fills the room between. Between the back slot
/// and the content lie <see cref="BackGap"/> dip of empty room with one thin vertical separator (the Workspace.Border token) in
/// the middle, so the way out never reads as one more action.
/// </summary>
/// <remarks>
/// <para>Keyboard order (documented choice): the back slot first, then the row's own content, the remaining buttons last, in
/// reading order from the left. Tab order follows the children order, so back children are declared first (in XAML and through
/// <see cref="Create"/>); <see cref="BackSlotIsFirst"/> and <see cref="EndSlotIsLast"/> are the checks the contract tests run.
/// Escape stays with each owner, unchanged: the bar only places the controls, it neither handles keys nor renames anything.</para>
/// <para>Narrow rows: below <see cref="CompactWidth"/> dip the bar gets the <c>:compact</c> pseudo-class and the back buttons keep
/// only their icon (the visible label goes; tooltip and accessible name still name the target, e.g. "Back to Archive").</para>
/// <para>The gallery viewer keeps its own overlay placement (GalleryViewer.Favorites.cs, its own package); this panel is the
/// pattern for every ordinary row. Window dialogs keep the caption's × at the right (DialogWindow, OS convention).</para>
/// <para>Hidden back children take no room; with no visible back child the row has no gap and no separator either. Start children
/// share the content area (one cell, like a single-cell Grid), so a row normally has one content child: a title, a toolbar or a
/// WrapPanel of actions.</para>
/// </remarks>
public sealed class HeaderBar : Control
{
    /// <summary>The isolating room between the back slot and the row's content, in dip (on the 8 dip grid). The separator sits in its middle.</summary>
    public const double BackGap = 24;

    /// <summary>The room between two controls of the back slot (e.g. "Back to gallery" and "Close reader"), in dip.</summary>
    public const double BackSpacing = 8;

    /// <summary>The least room between the content and the right-aligned remaining buttons, in dip.</summary>
    public const double EndGap = 16;

    /// <summary>The room between two of the right-aligned remaining buttons, in dip.</summary>
    public const double EndSpacing = 8;

    /// <summary>Below this width (dip) the bar is <c>:compact</c>: back buttons show their icon only.</summary>
    public const double CompactWidth = 360;

    /// <summary>The thickness of the separator after the back slot, in dip.</summary>
    public const double SeparatorThickness = 1;

    /// <summary>Style class every back child carries (set by <see cref="IsBackProperty"/>), the hook of the compact style.</summary>
    public const string BackClass = "header-back";

    /// <summary>Marks a child as part of the back slot at the far left (back, close, "Back to …").</summary>
    public static readonly AttachedProperty<bool> IsBackProperty =
        AvaloniaProperty.RegisterAttached<HeaderBar, Control, bool>("IsBack");

    /// <summary>Marks a child as one of the remaining buttons at the right edge (More, actions).</summary>
    public static readonly AttachedProperty<bool> IsEndProperty =
        AvaloniaProperty.RegisterAttached<HeaderBar, Control, bool>("IsEnd");

    private readonly Avalonia.Controls.Controls _children = new();
    private double? _separatorX;
    private double _separatorHeight;
    private IBrush? _separatorBrush;

    public HeaderBar() => _children.CollectionChanged += OnChildrenChanged;

    /// <summary>The row's controls in tab order: the back slot, the content, the remaining buttons.</summary>
    [Content]
    public Avalonia.Controls.Controls Children => _children;

    static HeaderBar()
    {
        IsBackProperty.Changed.AddClassHandler<Control>((control, args) =>
        {
            control.Classes.Set(BackClass, args.GetNewValue<bool>());
            (control.Parent as HeaderBar)?.InvalidateMeasure();
        });
        IsEndProperty.Changed.AddClassHandler<Control>((control, _) => (control.Parent as HeaderBar)?.InvalidateMeasure());
    }

    public static bool GetIsBack(Control control) => control.GetValue(IsBackProperty);

    public static void SetIsBack(Control control, bool value) => control.SetValue(IsBackProperty, value);

    public static bool GetIsEnd(Control control) => control.GetValue(IsEndProperty);

    public static void SetIsEnd(Control control, bool value) => control.SetValue(IsEndProperty, value);

    /// <summary>Where the separator stands (x in dip, its left edge), or null while the row has no visible back slot or nothing after it.</summary>
    internal double? SeparatorX => _separatorX;

    /// <summary>The height of the separator in dip (the back slot's tallest control), 0 without a separator.</summary>
    internal double SeparatorHeight => _separatorHeight;

    /// <summary>Whether the bar is currently <c>:compact</c> (back buttons icon only).</summary>
    internal bool IsCompact => Classes.Contains(":compact");

    /// <summary>
    /// A row with <paramref name="back"/> at the far left, then <paramref name="content"/>, then <paramref name="end"/> right-aligned,
    /// in this order (which is also the tab order). <paramref name="back"/> may be null for a row without a way out.
    /// </summary>
    internal static HeaderBar Create(Control? back, Control content, params Control[] end)
    {
        HeaderBar bar = new();
        if (back != null) { SetIsBack(back, true); bar.Children.Add(back); }
        bar.Children.Add(content);
        foreach (Control control in end) { SetIsEnd(control, true); bar.Children.Add(control); }
        return bar;
    }

    /// <summary>The back-slot children in their order (left to right, which is also their tab order).</summary>
    internal IReadOnlyList<Control> BackChildren => Children.Where(GetIsBack).ToArray();

    /// <summary>The right-aligned remaining buttons in their order (left to right).</summary>
    internal IReadOnlyList<Control> EndChildren => Children.Where(child => !GetIsBack(child) && GetIsEnd(child)).ToArray();

    /// <summary>True when no content or end child precedes a back child: the way back is first in the row's tab order.</summary>
    internal bool BackSlotIsFirst
    {
        get
        {
            bool seenOther = false;
            foreach (Control child in Children)
            {
                if (GetIsBack(child)) { if (seenOther) return false; }
                else seenOther = true;
            }
            return true;
        }
    }

    /// <summary>True when no content child follows an end child: the remaining buttons close the row's tab order.</summary>
    internal bool EndSlotIsLast
    {
        get
        {
            bool seenEnd = false;
            foreach (Control child in Children)
            {
                if (GetIsBack(child)) continue;
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

    // The same bookkeeping a Panel does (the Panel's own Render is sealed, so the bar draws its separator itself).
    private void OnChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                List<Control> added = e.NewItems!.OfType<Control>().ToList();
                LogicalChildren.InsertRange(e.NewStartingIndex, added);
                VisualChildren.InsertRange(e.NewStartingIndex, added);
                break;
            case NotifyCollectionChangedAction.Remove:
                List<Control> removed = e.OldItems!.OfType<Control>().ToList();
                LogicalChildren.RemoveAll(removed);
                VisualChildren.RemoveAll(removed);
                break;
            case NotifyCollectionChangedAction.Replace:
                for (int i = 0; i < e.NewItems!.Count; i++)
                {
                    int index = e.OldStartingIndex + i;
                    Control child = (Control)e.NewItems[i]!;
                    LogicalChildren[index] = child;
                    VisualChildren[index] = child;
                }
                break;
            case NotifyCollectionChangedAction.Move:
            case NotifyCollectionChangedAction.Reset:
                LogicalChildren.Clear();
                VisualChildren.Clear();
                LogicalChildren.AddRange(_children);
                VisualChildren.AddRange(_children);
                break;
        }
        InvalidateMeasure();
    }

    private enum Slot { Back, Content, End }

    private static Slot SlotOf(Control child) => GetIsBack(child) ? Slot.Back : GetIsEnd(child) ? Slot.End : Slot.Content;

    private Control[] Visible(Slot slot) => Children.Where(child => child.IsVisible && SlotOf(child) == slot).ToArray();

    private static double RowWidth(Control[] controls, double spacing)
    {
        double width = 0;
        foreach (Control control in controls) width += control.DesiredSize.Width;
        return controls.Length == 0 ? 0 : width + spacing * (controls.Length - 1);
    }

    private static double RowHeight(Control[] controls) => controls.Length == 0 ? 0 : controls.Max(control => control.DesiredSize.Height);

    protected override Size MeasureOverride(Size availableSize)
    {
        bool compact = availableSize.Width < CompactWidth;
        if (compact != IsCompact) PseudoClasses.Set(":compact", compact);
        Control[] back = Visible(Slot.Back), end = Visible(Slot.End);
        bool hasContent = Visible(Slot.Content).Length > 0;
        double height = 0;
        foreach (Control control in back.Concat(end))
        {
            control.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            height = Math.Max(height, control.DesiredSize.Height);
        }
        double backWidth = RowWidth(back, BackSpacing), endWidth = RowWidth(end, EndSpacing);
        bool separate = back.Length > 0 && (hasContent || end.Length > 0);
        double backReach = back.Length == 0 ? 0 : backWidth + (separate ? BackGap : 0);
        double endReach = end.Length == 0 ? 0 : endWidth + (hasContent ? EndGap : 0);
        double contentAvailable = Math.Max(0, availableSize.Width - backReach - endReach);
        double contentWidth = 0;
        foreach (Control control in Children.Where(child => SlotOf(child) == Slot.Content))
        {
            control.Measure(new Size(contentAvailable, availableSize.Height));
            contentWidth = Math.Max(contentWidth, control.DesiredSize.Width);
            height = Math.Max(height, control.DesiredSize.Height);
        }
        return new Size(backReach + contentWidth + endReach, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Control[] back = Visible(Slot.Back), end = Visible(Slot.End);
        bool hasContent = Visible(Slot.Content).Length > 0;
        double backWidth = RowWidth(back, BackSpacing), endWidth = RowWidth(end, EndSpacing);
        bool separate = back.Length > 0 && (hasContent || end.Length > 0);
        double backReach = back.Length == 0 ? 0 : backWidth + (separate ? BackGap : 0);
        double endReach = end.Length == 0 ? 0 : endWidth + (hasContent ? EndGap : 0);
        double x = 0;
        foreach (Control control in back)
        {
            control.Arrange(new Rect(x, 0, control.DesiredSize.Width, finalSize.Height));
            x += control.DesiredSize.Width + BackSpacing;
        }
        double contentWidth = Math.Max(0, finalSize.Width - backReach - endReach);
        foreach (Control control in Children.Where(child => SlotOf(child) == Slot.Content))
            control.Arrange(new Rect(backReach, 0, contentWidth, finalSize.Height));
        x = finalSize.Width - endWidth;
        foreach (Control control in end)
        {
            control.Arrange(new Rect(x, 0, control.DesiredSize.Width, finalSize.Height));
            x += control.DesiredSize.Width + EndSpacing;
        }
        // The separator stands in the middle of the gap behind the back slot, as tall as the slot's tallest control.
        double? separatorX = separate ? Math.Round(backWidth + (BackGap - SeparatorThickness) / 2) : null;
        double separatorHeight = separate ? Math.Min(finalSize.Height, RowHeight(back)) : 0;
        if (separatorX != _separatorX || separatorHeight != _separatorHeight)
        {
            _separatorX = separatorX;
            _separatorHeight = separatorHeight;
            InvalidateVisual();
        }
        return finalSize;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActualThemeVariantChanged += OnThemeChanged;
        _separatorBrush = null;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActualThemeVariantChanged -= OnThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _separatorBrush = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (_separatorX is not { } x || _separatorHeight <= 0) return;
        // The token of the app's decorative lines; without the application resources (unit tests) nothing is drawn.
        _separatorBrush ??= this.TryFindResource("Workspace.Border", ActualThemeVariant, out object? found) ? found as IBrush : null;
        if (_separatorBrush != null) context.FillRectangle(_separatorBrush, new Rect(x, 0, SeparatorThickness, _separatorHeight));
    }
}

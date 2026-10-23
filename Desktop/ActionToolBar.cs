using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Ursa.Controls;

namespace MeowChan.Desktop;

/// <summary>
/// Ursa 2.2 toolbar with measured, visibility-aware overflow and keyboard focus repair.
/// Reparents the original controls: commands, disabled reasons and live gestures have
/// exactly one presentation. No command routing or shortcut resolution lives here.
/// </summary>
public sealed class ActionToolBar : ToolBar
{
    protected override Type StyleKeyOverride => typeof(ToolBar);
    internal Panel? OverflowHost;
    internal ToggleButton? More;
    private Popup? _popup;
    private Control? _lastOverflowFocus;
    private int _focusRepairRevision;

    public ActionToolBar()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        ItemsPanel = new FuncTemplate<Panel?>(() => new ActionToolBarPanel());
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (_popup != null) { _popup.Opened -= OnPopupOpened; }
        if (OverflowHost != null)
        {
            OverflowHost.KeyDown -= OnOverflowKeyDown;
            OverflowHost.RemoveHandler(Button.ClickEvent, OnOverflowActionClick);
        }
        base.OnApplyTemplate(e);
        OverflowHost = e.NameScope.Find<Panel>(PART_OverflowPanel);
        More = e.NameScope.Find<ToggleButton>("button");
        if (More?.Parent is Panel panel) _popup = panel.Children.OfType<Popup>().FirstOrDefault();
        if (_popup != null) { _popup.Opened += OnPopupOpened; }
        if (OverflowHost != null)
        {
            KeyboardNavigation.SetTabNavigation(OverflowHost, KeyboardNavigationMode.Cycle);
            OverflowHost.KeyDown += OnOverflowKeyDown;
            OverflowHost.AddHandler(Button.ClickEvent, OnOverflowActionClick);
        }
        RefreshOverflowLabel();
    }

    public void RefreshOverflowLabel()
    {
        if (More == null) return;
        string label = Localization.Texts.T("downloads.more");
        ToolTip.SetTip(More, label);
        AutomationProperties.SetName(More, label);
    }

    internal double MoreWidth()
    {
        if (More == null) return 0;
        // Hidden toggles have zero DesiredSize. Measure their actual content instead;
        // no label-, language-, theme- or scale-specific width constants.
        if (More.Content is Control content)
        {
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return Math.Max(More.MinWidth, content.DesiredSize.Width + More.Padding.Left + More.Padding.Right
                + More.BorderThickness.Left + More.BorderThickness.Right + More.Margin.Left + More.Margin.Right);
        }
        return More.DesiredSize.Width;
    }

    internal void SetOverflow(bool hasItems)
    {
        PseudoClasses.Set(PC_Overflow, hasItems);
        if (!hasItems && More is ToggleButton more && (more.IsChecked == true || more.IsKeyboardFocusWithin))
        {
            more.IsChecked = false;
            Control? target = _lastOverflowFocus is { IsVisible: true, IsEnabled: true } focus ? focus
                : Items.OfType<Control>().FirstOrDefault(c => c.IsVisible && c.IsEnabled);
            Dispatcher.UIThread.Post(() => target?.Focus(), DispatcherPriority.Loaded);
        }
    }

    internal void Moving(Control control, bool toOverflow)
    {
        if (!control.IsKeyboardFocusWithin) return;
        Control focused = TopLevel.GetTopLevel(control)?.FocusManager?.GetFocusedElement() as Control ?? control;
        _lastOverflowFocus = control;
        // Closing the old popup before reparenting prevents a still-open Always
        // overflow from retaining focus in a different popup root. Other actions
        // remain available behind the same More toggle.
        if (!toOverflow && More?.IsChecked == true) More.IsChecked = false;
        Control? target = toOverflow ? More : focused;
        if (target == null) return;
        int revision = ++_focusRepairRevision;
        Dispatcher.UIThread.Post(() =>
        {
            if (revision != _focusRepairRevision || !target.IsEffectivelyVisible || !target.IsEnabled
                || TopLevel.GetTopLevel(target) == null) return;
            object? current = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            // Reparenting may clear focus or return it to More. An explicit focus
            // move to any other control wins over this queued layout repair.
            if (current != null && !ReferenceEquals(current, focused) && !ReferenceEquals(current, More)) return;
            target.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    private void OnPopupOpened(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (More?.IsChecked == true)
                OverflowHost?.Children.FirstOrDefault(c => c.IsVisible && c.IsEnabled)?.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    private void OnOverflowActionClick(object? sender, RoutedEventArgs e)
    {
        // A columns action opens a nested ContextMenu; keep its parent popup.
        // A dialog/gallery action owns its new focus, so closing must not steal it.
        if (e.Source is Button button && button.ContextMenu?.IsOpen != true && More != null)
            More.IsChecked = false;
    }

    private void OnOverflowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || OverflowHost == null) return;
        // This is popup navigation, never an app command/gesture dispatcher.
        if (e.Key == Key.Escape)
        {
            if (More != null) { More.IsChecked = false; More.Focus(); }
            e.Handled = true; return;
        }
        if (e.Key is not (Key.Up or Key.Down or Key.Home or Key.End)) return;
        Control[] items = OverflowHost.Children.Where(c => c.IsVisible && c.IsEnabled && c.Focusable).ToArray();
        if (items.Length == 0) return;
        int current = Array.FindIndex(items, c => c.IsKeyboardFocusWithin);
        int next = e.Key switch { Key.Home => 0, Key.End => items.Length - 1,
            Key.Up => (current - 1 + items.Length) % items.Length, _ => (current + 1) % items.Length };
        items[next].Focus(); e.Handled = true;
    }
}

/// <summary>Uses Ursa Never/AsNeeded/Always; preserves priority/order, including future actions.</summary>
public sealed class ActionToolBarPanel : StackPanel
{
    // StackPanel defaults to Vertical. Measurement below is horizontal; arrange
    // must use that same axis, otherwise only the first action fits the clip.
    static ActionToolBarPanel() => OrientationProperty.OverrideDefaultValue<ActionToolBarPanel>(Orientation.Horizontal);

    private ActionToolBar? Owner => TemplatedParent as ActionToolBar;
    private readonly HashSet<Control> _overflow = new();

    protected override Size MeasureOverride(Size availableSize)
    {
        ActionToolBar? owner = Owner;
        if (owner == null) return base.MeasureOverride(availableSize);
        Control[] items = owner.Items.OfType<Control>().ToArray();
        foreach (Control item in items) item.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double[] widths = items.Select(c => c.IsVisible ? c.DesiredSize.Width : 0).ToArray();
        OverflowMode[] modes = items.Select(ToolBar.GetOverflowMode).ToArray();
        bool[] overflow = ActionOverflowLayout.Resolve(widths, modes, items.Select(c => c.IsVisible).ToArray(), availableSize.Width + (owner.More?.IsVisible == true ? owner.MoreWidth() : 0), owner.MoreWidth());
        _overflow.Clear();
        for (int i = 0; i < items.Length; i++) if (overflow[i]) _overflow.Add(items[i]);
        owner.SetOverflow(_overflow.Count > 0);
        return new Size(items.Where(c => c.IsVisible && !_overflow.Contains(c)).Sum(c => c.DesiredSize.Width),
            items.Where(c => c.IsVisible).Select(c => c.DesiredSize.Height).DefaultIfEmpty().Max());
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        ActionToolBar? owner = Owner;
        if (owner?.OverflowHost is not Panel popup) return base.ArrangeOverride(finalSize);
        Control[] items = owner.Items.OfType<Control>().ToArray();
        int directIndex = 0, popupIndex = 0;
        foreach (Control item in items)
        {
            bool overflow = _overflow.Contains(item);
            Panel destination = overflow ? popup : this;
            int index = overflow ? popupIndex++ : directIndex++;
            if (!destination.Children.Contains(item))
            {
                owner.Moving(item, overflow);
                Children.Remove(item); popup.Children.Remove(item);
                destination.Children.Insert(Math.Min(index, destination.Children.Count), item);
            }
        }
        return base.ArrangeOverride(finalSize);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Return popup children before a template or host is discarded.
        if (Owner?.OverflowHost is Panel popup)
            foreach (Control item in popup.Children.ToArray()) { popup.Children.Remove(item); Children.Add(item); }
        base.OnDetachedFromVisualTree(e);
    }
}

public static class ActionOverflowLayout
{
    public static bool[] Resolve(IReadOnlyList<double> widths, IReadOnlyList<OverflowMode> modes,
        IReadOnlyList<bool> visible, double available, double moreWidth)
    {
        bool[] result = new bool[widths.Count];
        double total = 0, required = 0; bool forced = false;
        for (int i = 0; i < widths.Count; i++)
        {
            if (!visible[i]) continue;
            if (modes[i] == OverflowMode.Always) forced = true;
            else total += widths[i];
            if (modes[i] == OverflowMode.Never) required += widths[i];
        }
        double budget = Math.Max(0, available - (forced || total > available ? moreWidth : 0));
        bool spilling = false;
        for (int i = 0; i < widths.Count; i++)
        {
            if (!visible[i] || modes[i] == OverflowMode.Never) continue;
            if (modes[i] == OverflowMode.Always) { result[i] = true; continue; }
            if (spilling || required + widths[i] > budget) result[i] = spilling = true;
            else required += widths[i];
        }
        return result;
    }
}

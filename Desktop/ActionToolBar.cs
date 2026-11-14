using System;
using System.Collections.Generic;
using System.Collections.Specialized;
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
    private int _overflowCount;
    private Control[] _generatedItems = Array.Empty<Control>();
    internal ActionToolBarPanel? DirectHost;
    /// <summary>Detached parking panel for <see cref="OmittedProperty"/> items: neither in the bar nor in its overflow.</summary>
    internal readonly Panel OmittedHost = new() { IsVisible = false };

    /// <summary>
    /// Work view "K" (WorkViewPresenter): the item is left out of both the bar and its "More" popup because a context menu
    /// or the app menu carries the same command. It stays an item of this toolbar, so its command, disabled reason and
    /// shortcut keep working (CommandBindings.RouteVisibleActions falls back to such reduced items).
    /// </summary>
    public static readonly AttachedProperty<bool> OmittedProperty =
        AvaloniaProperty.RegisterAttached<ActionToolBar, Control, bool>("Omitted");
    public static bool GetOmitted(Control item) => item.GetValue(OmittedProperty);
    public static void SetOmitted(Control item, bool omitted) => item.SetValue(OmittedProperty, omitted);

    /// <summary>
    /// Opt-in: AsNeeded items keep their place on the bar by <see cref="OverflowPriorityProperty"/> (lower stays longer) instead
    /// of by item order, so the visual order and the order things move into "More" are two separate decisions. Without it a
    /// toolbar keeps the historical rule (later items overflow first). Ties keep item order.
    /// </summary>
    public static readonly StyledProperty<bool> PrioritizedOverflowProperty =
        AvaloniaProperty.Register<ActionToolBar, bool>(nameof(PrioritizedOverflow));
    public bool PrioritizedOverflow
    {
        get => GetValue(PrioritizedOverflowProperty);
        set => SetValue(PrioritizedOverflowProperty, value);
    }

    /// <summary>The keep-visible rank of an AsNeeded item on a <see cref="PrioritizedOverflow"/> toolbar (WorkViewEntry.OverflowOrder).</summary>
    public static readonly AttachedProperty<int> OverflowPriorityProperty =
        AvaloniaProperty.RegisterAttached<ActionToolBar, Control, int>("OverflowPriority");
    public static int GetOverflowPriority(Control item) => item.GetValue(OverflowPriorityProperty);
    public static void SetOverflowPriority(Control item, int priority) => item.SetValue(OverflowPriorityProperty, priority);

    /// <summary>The Control items in item order, kept in step with <see cref="ItemsControl.Items"/> (no per-pass LINQ in the panel).</summary>
    internal Control[] GeneratedItems => _generatedItems;

    /// <summary>Items that are currently not in the bar itself: in the overflow popup (open or closed) or omitted.</summary>
    internal IEnumerable<Control> ReducedItems => Items.OfType<Control>()
        .Where(item => OverflowHost?.Children.Contains(item) == true || OmittedHost.Children.Contains(item));


    public ActionToolBar()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        ItemsPanel = new FuncTemplate<Panel?>(() => new ActionToolBarPanel());
        // ItemsSourceView raises CollectionChanged before its generator's internal
        // PostCollectionChanged. Restore the generated index order for that handoff.
        Items.CollectionChanged += OnActionItemsChanged;
    }

    static ActionToolBar()
    {
        // Items keep the toolbar as their logical parent wherever their panel is. The overflow mode counts too: a work-view switch
        // that only turns an item from Always into AsNeeded (Simple → Advanced: full screen) must re-run the bar's measure, else the
        // item stays under More at an unchanged width.
        foreach (AvaloniaProperty property in new AvaloniaProperty[] { OmittedProperty, OverflowPriorityProperty, ToolBar.OverflowModeProperty })
            property.Changed.AddClassHandler<Control>((item, _) =>
            {
                if (item.Parent is not ActionToolBar toolbar) return;
                toolbar.DirectHost?.InvalidateMeasure();
                toolbar.InvalidateMeasure();
            });
        PrioritizedOverflowProperty.Changed.AddClassHandler<ActionToolBar>((toolbar, _) =>
        {
            toolbar.DirectHost?.InvalidateMeasure();
            toolbar.InvalidateMeasure();
        });
    }

    private void OnActionItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Control? focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        bool ownedFocus = focused != null && _generatedItems.Any(item =>
            ReferenceEquals(item, focused) || item.IsKeyboardFocusWithin);
        if (DirectHost is { } direct && OverflowHost is { } popup)
        {
            // PanelContainerGenerator removes/moves by Items index. Leaving the
            // original in Popup.Children makes its generated panel too short.
            foreach (Control item in popup.Children.ToArray()) popup.Children.Remove(item);
            OmittedHost.Children.Clear();
            direct.Children.Clear();
            foreach (Control item in _generatedItems) direct.Children.Add(item);
        }
        foreach (Control item in _generatedItems)
        {
            item.GotFocus -= OnActionGotFocus;
            item.PropertyChanged -= OnActionPropertyChanged;
        }
        _generatedItems = Items.OfType<Control>().ToArray();
        foreach (Control item in _generatedItems)
        {
            item.GotFocus += OnActionGotFocus;
            item.PropertyChanged += OnActionPropertyChanged;
        }
        if (ownedFocus) QueueAvailableActionFocus(focused);
    }

    private void OnActionGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (sender is Control control && OverflowHost?.Children.Contains(control) == true)
            _lastOverflowFocus = control;
    }

    private void OnActionPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsEffectivelyEnabledProperty && sender is Control control
            && !control.IsEffectivelyEnabled && ReferenceEquals(control, _lastOverflowFocus)
            && More?.IsChecked == true)
            QueueAvailableActionFocus(control);
    }

    private void QueueAvailableActionFocus(Control? previous)
    {
        int revision = ++_focusRepairRevision;
        Dispatcher.UIThread.Post(() =>
        {
            if (revision != _focusRepairRevision) return;
            object? current = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            if (current != null && !ReferenceEquals(current, previous) && !ReferenceEquals(current, More)) return;
            bool Usable(Control control) => control.IsEffectivelyVisible && control.IsEffectivelyEnabled
                && control.Focusable && control.Bounds.Width > 0 && control.Bounds.Height > 0
                && TopLevel.GetTopLevel(control) is { IsVisible: true };
            Control? target = previous != null && _generatedItems.Contains(previous) && Usable(previous) ? previous
                : More?.IsChecked == true ? OverflowHost?.Children.FirstOrDefault(Usable) : null;
            target ??= More is { } more && Usable(more) ? more : _generatedItems.FirstOrDefault(Usable);
            target?.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
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

    private ToggleButton? _labelledMore;
    private string? _moreTip;
    private string? _moreName;

    /// <summary>
    /// R7: the overflow button is named "More" and announces how many actions it holds ("More, 5 actions"). A repeated call
    /// with the same texts on the same button writes nothing.
    /// </summary>
    public void RefreshOverflowLabel()
    {
        if (More == null) return;
        string tip = Localization.Texts.T("workview.more");
        string name = OverflowAccessibleName(_overflowCount);
        if (ReferenceEquals(_labelledMore, More) && string.Equals(_moreTip, tip, StringComparison.Ordinal)
            && string.Equals(_moreName, name, StringComparison.Ordinal) && ReferenceEquals(ToolTip.GetTip(More), _moreTip)) return;
        _labelledMore = More;
        _moreTip = tip;
        _moreName = name;
        ToolTip.SetTip(More, tip);
        AutomationProperties.SetName(More, name);
    }

    /// <summary>The accessible name of the overflow button for <paramref name="count"/> reduced actions.</summary>
    internal static string OverflowAccessibleName(int count) => count > 0
        ? Localization.Texts.Count("workview.moreCount", count)
        : Localization.Texts.T("workview.more");

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

    private bool _overflowShown;

    /// <summary>
    /// How many items the panel moved into "More". The pseudo-class acts only when "any" changes: it drives the visibility of
    /// the More button, which the same measure pass already counted, so it is not deferred. The accessible name follows the count.
    /// </summary>
    internal void SetOverflow(int count)
    {
        bool hasItems = count > 0;
        if (_overflowShown != hasItems || PseudoClasses.Contains(PC_Overflow) != hasItems)
        {
            _overflowShown = hasItems;
            PseudoClasses.Set(PC_Overflow, hasItems);
        }
        if (count != _overflowCount)
        {
            _overflowCount = count;
            RefreshOverflowLabel();
        }
        if (!hasItems && More is ToggleButton more && (more.IsChecked == true || more.IsKeyboardFocusWithin))
        {
            more.IsChecked = false;
            QueueAvailableActionFocus(_lastOverflowFocus ?? more);
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
            if (revision != _focusRepairRevision || !target.IsEffectivelyVisible || !target.IsEffectivelyEnabled
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
                OverflowHost?.Children.FirstOrDefault(c => c.IsVisible && c.IsEffectivelyEnabled)?.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    private void OnOverflowActionClick(object? sender, RoutedEventArgs e)
    {
        // A columns action opens a nested ContextMenu; keep its parent popup.
        // A dialog/gallery action owns its new focus, so closing must not steal it.
        // A split button's arrow opens its flyout menu from inside the overflow; keep the parent popup too.
        if (e.Source is Button button && button.ContextMenu?.IsOpen != true && More != null
            && !(button.TemplatedParent is SplitButton && button.Name == "PART_SecondaryButton"))
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
        Control[] items = OverflowHost.Children.Where(c => c.IsVisible && c.IsEffectivelyEnabled && c.Focusable).ToArray();
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
    private readonly HashSet<Control> _omitted = new();
    // Scratch buffers of one measure pass, reused so a pass allocates nothing.
    private double[] _widths = Array.Empty<double>();
    private OverflowMode[] _modes = Array.Empty<OverflowMode>();
    private bool[] _visible = Array.Empty<bool>();
    private bool[] _overflowed = Array.Empty<bool>();
    private int[] _priorities = Array.Empty<int>();
    private int[] _order = Array.Empty<int>();

    private void EnsureScratch(int count)
    {
        if (_widths.Length >= count) return;
        int size = Math.Max(count, 8);
        _widths = new double[size]; _modes = new OverflowMode[size]; _visible = new bool[size];
        _overflowed = new bool[size]; _priorities = new int[size]; _order = new int[size];
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        ActionToolBar? owner = Owner;
        if (owner == null) return base.MeasureOverride(availableSize);
        owner.DirectHost = this;
        Control[] items = owner.GeneratedItems;
        int count = items.Length;
        EnsureScratch(count);
        Size unbounded = new(double.PositiveInfinity, double.PositiveInfinity);
        bool prioritized = owner.PrioritizedOverflow;
        for (int i = 0; i < count; i++)
        {
            Control item = items[i];
            item.Measure(unbounded);
            // An omitted item takes part like an invisible one: no width, never in the overflow.
            bool visible = item.IsVisible && !ActionToolBar.GetOmitted(item);
            _visible[i] = visible;
            _widths[i] = visible ? item.DesiredSize.Width : 0;
            _modes[i] = ToolBar.GetOverflowMode(item);
            if (prioritized) _priorities[i] = ActionToolBar.GetOverflowPriority(item);
        }
        // One measure of the More content per pass: it lives in another subtree and is measured mid-pass.
        double moreWidth = owner.MoreWidth();
        ActionOverflowLayout.Resolve(count, _widths, _modes, _visible,
            availableSize.Width + (owner.More?.IsVisible == true ? moreWidth : 0), moreWidth,
            prioritized ? _priorities : null, _overflowed, _order);
        _overflow.Clear();
        _omitted.Clear();
        double width = 0, height = 0;
        for (int i = 0; i < count; i++)
        {
            Control item = items[i];
            if (_overflowed[i]) _overflow.Add(item);
            if (ActionToolBar.GetOmitted(item)) _omitted.Add(item);
            if (!_visible[i]) continue;
            if (!_overflowed[i]) width += item.DesiredSize.Width;
            height = Math.Max(height, item.DesiredSize.Height);
        }
        owner.SetOverflow(_overflow.Count);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        ActionToolBar? owner = Owner;
        if (owner?.OverflowHost is not Panel popup) return base.ArrangeOverride(finalSize);
        Control[] items = owner.GeneratedItems;
        int directIndex = 0, popupIndex = 0, omittedIndex = 0;
        foreach (Control item in items)
        {
            bool omitted = _omitted.Contains(item);
            bool overflow = !omitted && _overflow.Contains(item);
            Panel destination = omitted ? owner.OmittedHost : overflow ? popup : this;
            int index = omitted ? omittedIndex++ : overflow ? popupIndex++ : directIndex++;
            // Only a real change of place re-parents; an unchanged arrangement touches no collection.
            if (!destination.Children.Contains(item))
            {
                owner.Moving(item, overflow || omitted);
                Children.Remove(item); popup.Children.Remove(item); owner.OmittedHost.Children.Remove(item);
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
        if (Owner is { } owner)
            foreach (Control item in owner.OmittedHost.Children.ToArray()) { owner.OmittedHost.Children.Remove(item); Children.Add(item); }
        base.OnDetachedFromVisualTree(e);
    }
}

public static class ActionOverflowLayout
{
    /// <summary>
    /// Which items go into "More". Never items always stay, Always items always move; AsNeeded items take the remaining
    /// width in item order, or by ascending <paramref name="priorities"/> (ties in item order) when given. The first one
    /// that does not fit and every later one in that order overflow, so what moves into "More" is always the tail.
    /// </summary>
    public static bool[] Resolve(IReadOnlyList<double> widths, IReadOnlyList<OverflowMode> modes,
        IReadOnlyList<bool> visible, double available, double moreWidth, IReadOnlyList<int>? priorities = null)
    {
        int count = widths.Count;
        double[] w = new double[count]; OverflowMode[] m = new OverflowMode[count]; bool[] v = new bool[count];
        int[]? p = priorities == null ? null : new int[count];
        for (int i = 0; i < count; i++)
        {
            w[i] = widths[i]; m[i] = modes[i]; v[i] = visible[i];
            if (p != null) p[i] = priorities![i];
        }
        bool[] result = new bool[count];
        Resolve(count, w, m, v, available, moreWidth, p, result, new int[count]);
        return result;
    }

    /// <summary>
    /// Allocation-free form for the toolbar panel: the first <paramref name="count"/> entries of the arrays are read and
    /// <paramref name="result"/> is overwritten. <paramref name="order"/> is scratch of at least <paramref name="count"/>.
    /// </summary>
    public static void Resolve(int count, double[] widths, OverflowMode[] modes, bool[] visible, double available,
        double moreWidth, int[]? priorities, bool[] result, int[] order)
    {
        double total = 0, required = 0; bool forced = false;
        for (int i = 0; i < count; i++)
        {
            result[i] = false;
            order[i] = i;
            if (!visible[i]) continue;
            if (modes[i] == OverflowMode.Always) forced = true;
            else total += widths[i];
            if (modes[i] == OverflowMode.Never) required += widths[i];
        }
        double budget = Math.Max(0, available - (forced || total > available ? moreWidth : 0));
        if (priorities != null)
        {
            // Stable insertion sort by (priority, item index): ties keep item order, no LINQ, no allocation.
            for (int i = 1; i < count; i++)
            {
                int current = order[i], j = i - 1;
                while (j >= 0 && priorities[order[j]] > priorities[current]) { order[j + 1] = order[j]; j--; }
                order[j + 1] = current;
            }
        }
        bool spilling = false;
        for (int k = 0; k < count; k++)
        {
            int i = order[k];
            if (!visible[i] || modes[i] == OverflowMode.Never) continue;
            if (modes[i] == OverflowMode.Always) { result[i] = true; continue; }
            if (spilling || required + widths[i] > budget) result[i] = spilling = true;
            else required += widths[i];
        }
    }
}

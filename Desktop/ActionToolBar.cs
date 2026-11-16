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
using Avalonia.Media;
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

    /// <summary>
    /// RL1: the group an item belongs to (default 0). On the bar itself, a thin line stands in the gap between two neighbouring
    /// items of different groups; items under "More" and hidden items take no part, so a group that moved away leaves no line
    /// behind. A bar whose items all keep the default group draws none.
    /// </summary>
    public static readonly AttachedProperty<int> GroupProperty =
        AvaloniaProperty.RegisterAttached<ActionToolBar, Control, int>("Group");
    public static int GetGroup(Control item) => item.GetValue(GroupProperty);
    public static void SetGroup(Control item, int group) => item.SetValue(GroupProperty, group);

    /// <summary>
    /// Opt-in for a joined segment group (the List|Table pair): when the group is the last thing standing on the bar and "More" is
    /// shown, its segments share the width up to "More" (the item gap in front of it stays) instead of leaving it empty. Without
    /// "More", with other items after the group, or while the group is under "More" nothing stretches; the items' measured widths
    /// (so the overflow decision) never change, only their arranged slots.
    /// </summary>
    public static readonly AttachedProperty<bool> StretchProperty =
        AvaloniaProperty.RegisterAttached<ActionToolBar, Control, bool>("Stretch");
    public static bool GetStretch(Control item) => item.GetValue(StretchProperty);
    public static void SetStretch(Control item, bool stretch) => item.SetValue(StretchProperty, stretch);

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
        GroupProperty.Changed.AddClassHandler<Control>((item, _) => (item.Parent as Panel)?.InvalidateArrange());
        StretchProperty.Changed.AddClassHandler<Control>((item, _) => (item.Parent as Panel)?.InvalidateArrange());
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
            // The popup was emptied above and the items stand on the bar again (the panel moves them back out on its next arrange).
            ActionToolBarPanel.MarkReduced(item, false);
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
        // Toolbar UX review 15.11.: "⋮" stands one item gap after the last item on the bar, with no line before it. The gap is the
        // toggle's own left margin, so MoreWidth (which counts the margin) reserves it and the bar's items end flush without it.
        if (More != null) More.Margin = MoreMargin(More.Margin);
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

    /// <summary>The margin of the "More" toggle: <see cref="ActionToolBarPanel.ItemGap"/> in front of it, the rest kept.</summary>
    internal static Thickness MoreMargin(Thickness margin) => new(ActionToolBarPanel.ItemGap, margin.Top, margin.Right, margin.Bottom);

    /// <summary>
    /// The width the bar reserves for "More": the toggle itself plus its margins, which hold the item gap in front of it
    /// (<see cref="MoreMargin"/>), so items that move under "More" leave exactly that gap and no more.
    /// </summary>
    internal double MoreWidth()
    {
        if (More == null) return 0;
        // Hidden toggles have zero DesiredSize. Measure their actual content instead;
        // no label-, language-, theme- or scale-specific width constants.
        if (More.Content is Control content)
        {
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return MoreSlot(content.DesiredSize.Width + More.Padding.Left + More.Padding.Right
                + More.BorderThickness.Left + More.BorderThickness.Right, More.MinWidth, More.Margin);
        }
        return More.DesiredSize.Width;
    }

    /// <summary>The slot of "More" on the bar: the toggle (at least its minimum width) and its margins, the leading item gap included.</summary>
    internal static double MoreSlot(double toggleWidth, double minWidth, Thickness margin) =>
        Math.Max(minWidth, toggleWidth) + margin.Left + margin.Right;

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

    /// <summary>
    /// Pseudo-class of an item that is not on the bar itself but under "More" (or left out). An item keeps the toolbar as its logical
    /// parent wherever its panel is, so a style cannot tell the bar from the popup by ancestors: <c>:in-overflow</c> is what a style uses
    /// to look different on the bar than in the popup (<c>Button.bar-button:not(:in-overflow)</c>).
    /// </summary>
    public const string ReducedPseudoClass = ":in-overflow";

    internal static void MarkReduced(Control item, bool reduced) => ((IPseudoClasses)item.Classes).Set(ReducedPseudoClass, reduced);

    // The width an item has while it stands on the bar. Its size depends on :in-overflow (another theme, padding and minimum height
    // on the bar than under "More"), so an item measured under "More" is narrower than it would be back on the bar. Deciding on that
    // narrow width would let it "fit", return, grow and overflow again; the overflow decision therefore always uses this last bar width.
    private static readonly AttachedProperty<double> BarWidthProperty =
        AvaloniaProperty.RegisterAttached<ActionToolBarPanel, Control, double>("BarWidth", double.NaN);

    /// <summary>
    /// The width the overflow decision uses for an item: its measured width while it stands on the bar (<paramref name="reduced"/> false),
    /// else the width it last had on the bar when that is known, else the width measured where it is now. A stale value costs at most one
    /// round trip: back on the bar the item is measured for real and the cache is the true bar width from then on.
    /// </summary>
    internal static double DecisionWidth(bool reduced, double barWidth, double desiredWidth) =>
        reduced && !double.IsNaN(barWidth) && !double.IsInfinity(barWidth) ? barWidth : desiredWidth;

    private static double DecisionWidth(Control item)
    {
        bool reduced = item.Classes.Contains(ReducedPseudoClass);
        double desired = item.DesiredSize.Width;
        if (!reduced) item.SetValue(BarWidthProperty, desired);
        return DecisionWidth(reduced, item.GetValue(BarWidthProperty), desired);
    }

    /// <summary>
    /// Toolbar UX review 15.11.: the gap between two neighbouring items on the bar (Workspace.Space.BarGap). The panel owns it, so no
    /// item or toolbar patches it with a margin; the focus ring (4 outside the control) then stays 4 clear of the neighbour.
    /// </summary>
    public const double ItemGap = 8;

    /// <summary>The gap between two groups (Workspace.Space.BarGroupGap), with the group line centred in it.</summary>
    public const double GroupGap = 16;

    /// <summary>The class of a joined segment (the List/Table toggle): two neighbouring segments stand with no gap.</summary>
    public const string SegmentClass = "segment";

    /// <summary>The width of the line between two groups (device-independent pixels).</summary>
    public const double GroupSeparatorThickness = 1;
    private const double GroupSeparatorHeight = 20;

    public ActionToolBarPanel() =>
        MeowChan.Desktop.Theming.ThemeRuntime.FollowApplied(this, RefreshSeparatorBrush);

    private ActionToolBar? Owner => TemplatedParent as ActionToolBar;
    private readonly List<Rect> _separators = new();
    // The lines are visual children after the items (Panel seals Render, so each line is a small control of its own). Panel keeps its
    // items and their visuals in step by index, so the marks always stand behind the last item and are never part of Children.
    private readonly List<GroupSeparatorMark> _marks = new();
    private readonly HashSet<Control> _overflow = new();
    private readonly HashSet<Control> _omitted = new();
    // The right edge of the items' budget with "More" shown (NaN: unbounded), and the shown items of one arrange pass (scratch).
    private double _stretchRight = double.NaN;
    private readonly List<Control> _shown = new();
    private double[] _stretched = Array.Empty<double>();
    // Scratch buffers of one measure pass, reused so a pass allocates nothing.
    private double[] _widths = Array.Empty<double>();
    private OverflowMode[] _modes = Array.Empty<OverflowMode>();
    private bool[] _visible = Array.Empty<bool>();
    private bool[] _overflowed = Array.Empty<bool>();
    private int[] _priorities = Array.Empty<int>();
    private int[] _order = Array.Empty<int>();
    private double[] _itemWidths = Array.Empty<double>();
    private double[] _gaps = Array.Empty<double>();
    private int[] _groups = Array.Empty<int>();
    private bool[] _segments = Array.Empty<bool>();
    private bool[] _previous = Array.Empty<bool>();

    private void EnsureScratch(int count)
    {
        if (_widths.Length >= count) return;
        int size = Math.Max(count, 8);
        _widths = new double[size]; _modes = new OverflowMode[size]; _visible = new bool[size];
        _overflowed = new bool[size]; _priorities = new int[size]; _order = new int[size];
        _itemWidths = new double[size]; _gaps = new double[size]; _groups = new int[size]; _segments = new bool[size]; _previous = new bool[size];
    }

    private static bool IsSegment(Control item) => item.Classes.Contains(SegmentClass);

    /// <summary>
    /// The gap in front of an item on the bar after the shown item before it: the group gap where a new group starts, none between two
    /// joined segments, the item gap otherwise. The first shown item has none (<see cref="LeadingGap(Control?, Control)"/>).
    /// </summary>
    public static double LeadingGap(bool previousSegment, int previousGroup, bool segment, int group) =>
        StartsNewGroup(previousGroup, group) ? GroupGap : previousSegment && segment ? 0 : ItemGap;

    /// <summary>The gap in front of <paramref name="item"/> on the bar; <paramref name="previous"/> is the shown item before it (null: none).</summary>
    public static double LeadingGap(Control? previous, Control item) => previous == null ? 0
        : LeadingGap(IsSegment(previous), ActionToolBar.GetGroup(previous), IsSegment(item), ActionToolBar.GetGroup(item));

    private static bool Stretches(Control item) => IsSegment(item) && ActionToolBar.GetStretch(item);

    /// <summary>
    /// The index of the first member of the trailing stretch run among the shown items (the last shown item and the segments of its
    /// group directly before it that all carry <see cref="ActionToolBar.StretchProperty"/>), or -1 when the bar does not end in one.
    /// </summary>
    private static int StretchRunStart(List<Control> shown)
    {
        int start = shown.Count - 1;
        if (start < 0 || !Stretches(shown[start])) return -1;
        int group = ActionToolBar.GetGroup(shown[start]);
        while (start > 0 && Stretches(shown[start - 1]) && ActionToolBar.GetGroup(shown[start - 1]) == group) start--;
        return start;
    }

    private static void StretchShown(double span, List<Control> shown, int start, double[] slots)
    {
        int count = shown.Count - start;
        double[] naturals = new double[count], margins = new double[count];
        for (int i = 0; i < count; i++)
        {
            Control member = shown[start + i];
            naturals[i] = member.DesiredSize.Width;
            margins[i] = member.Margin.Left + member.Margin.Right;
        }
        StretchWidths(span, naturals, margins).CopyTo(slots, 0);
    }

    /// <summary>
    /// The slot widths of a joined stretch group that shares <paramref name="span"/> (the slots' sum; the members' natural widths are
    /// <paramref name="naturals"/> and <paramref name="margins"/> their horizontal margins, negative for the overlapping edge of a segment).
    /// The members' visible widths (slot minus margins) become equal; where one member's natural width is wider than that share, or
    /// there is no spare room, every member keeps its natural width and the spare room is split evenly, so nothing is ever clipped.
    /// </summary>
    public static double[] StretchWidths(double span, IReadOnlyList<double> naturals, IReadOnlyList<double> margins)
    {
        int count = naturals.Count;
        double[] slots = new double[count];
        if (count == 0) return slots;
        double sum = 0, marginSum = 0;
        for (int i = 0; i < count; i++) { sum += naturals[i]; marginSum += margins[i]; }
        double extra = span - sum;
        if (!(extra > 0))
        {
            for (int i = 0; i < count; i++) slots[i] = naturals[i];
            return slots;
        }
        double visible = (span - marginSum) / count;
        bool fits = true;
        for (int i = 0; i < count; i++) if (visible + margins[i] < naturals[i]) { fits = false; break; }
        for (int i = 0; i < count; i++) slots[i] = fits ? visible + margins[i] : naturals[i] + extra / count;
        return slots;
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
            _itemWidths[i] = visible ? DecisionWidth(item) : 0;
            _modes[i] = ToolBar.GetOverflowMode(item);
            _groups[i] = ActionToolBar.GetGroup(item);
            _segments[i] = IsSegment(item);
            if (prioritized) _priorities[i] = ActionToolBar.GetOverflowPriority(item);
        }
        // One measure of the More content per pass: it lives in another subtree and is measured mid-pass. It holds the gap before "⋮".
        double moreWidth = owner.MoreWidth();
        double available = availableSize.Width + (owner.More?.IsVisible == true ? moreWidth : 0);
        // Where the items end when "More" is shown: the same budget the overflow decision used (the item gap in front of "More" is part of its slot).
        _stretchRight = double.IsFinite(available) ? available - moreWidth : double.NaN;
        double width = ActionOverflowLayout.ResolveSpaced(count, _itemWidths, _groups, _segments, _modes, _visible,
            available, moreWidth,
            prioritized ? _priorities : null, _overflowed, _gaps, _widths, _previous, _order);
        _overflow.Clear();
        _omitted.Clear();
        double height = 0;
        for (int i = 0; i < count; i++)
        {
            Control item = items[i];
            if (_overflowed[i]) _overflow.Add(item);
            if (ActionToolBar.GetOmitted(item)) _omitted.Add(item);
            if (!_visible[i]) continue;
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
                MarkReduced(item, overflow || omitted);
            }
        }
        // One horizontal row with the bar's own gaps (the measure counted the same ones): the item gap between neighbours, the group gap
        // where a new group starts (the line stands in its middle), none between two joined segments and none at either end.
        // A trailing joined stretch group (ActionToolBar.Stretch) takes the room between the last natural item end and "More".
        _shown.Clear();
        foreach (Control child in Children) if (child.IsVisible) _shown.Add(child);
        int run = _overflow.Count > 0 ? StretchRunStart(_shown) : -1;
        double natural = 0;
        Control? last = null;
        foreach (Control child in _shown) { natural += LeadingGap(last, child) + child.DesiredSize.Width; last = child; }
        double right = double.IsNaN(_stretchRight) ? finalSize.Width : Math.Min(_stretchRight, finalSize.Width);
        int stretchedCount = _shown.Count - run;
        if (run >= 0 && right > natural)
        {
            if (_stretched.Length < stretchedCount) _stretched = new double[Math.Max(stretchedCount, 4)];
            double span = right - natural;
            for (int i = 0; i < stretchedCount; i++) span += _shown[run + i].DesiredSize.Width;
            StretchShown(span, _shown, run, _stretched);
        }
        else run = -1;
        double x = 0;
        Control? previous = null;
        for (int index = 0; index < _shown.Count; index++)
        {
            Control child = _shown[index];
            x += LeadingGap(previous, child);
            double width = run >= 0 && index >= run ? _stretched[index - run] : child.DesiredSize.Width;
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width;
            previous = child;
        }
        _shown.Clear();
        UpdateGroupSeparators();
        return finalSize;
    }

    /// <summary>Whether a line stands between two neighbouring shown items of these groups (the one rule of the bar's separators).</summary>
    public static bool StartsNewGroup(int previousGroup, int group) => previousGroup != group;

    /// <summary>The indexes of the items in <paramref name="groups"/> in front of which a line stands, given which of them are <paramref name="shown"/> on the bar itself.</summary>
    public static IReadOnlyList<int> GroupStarts(IReadOnlyList<int> groups, IReadOnlyList<bool> shown)
    {
        List<int> starts = new();
        int previous = -1;
        for (int index = 0; index < groups.Count; index++)
        {
            if (!shown[index]) continue;
            if (previous >= 0 && StartsNewGroup(groups[previous], groups[index])) starts.Add(index);
            previous = index;
        }
        return starts;
    }

    /// <summary>The lines between groups on the bar now (left edge x, top, width, height in the panel), in order (tests and smokes).</summary>
    internal IReadOnlyList<Rect> GroupSeparators => _separators;

    // The line stands in the middle of the group gap that precedes the first item of the next group (ActionToolBarPanel.GroupGap),
    // centred on the row; only visible items of the bar itself count.
    private void UpdateGroupSeparators()
    {
        List<Rect> lines = new();
        Control? previous = null;
        foreach (Control child in Children)
        {
            if (!child.IsVisible || child.Bounds.Width <= 0) continue;
            if (previous != null && StartsNewGroup(ActionToolBar.GetGroup(previous), ActionToolBar.GetGroup(child)))
            {
                double gap = child.Bounds.X - previous.Bounds.Right;
                double x = Math.Round(previous.Bounds.Right + (gap - GroupSeparatorThickness) / 2);
                double height = Math.Min(GroupSeparatorHeight, Math.Max(child.Bounds.Height, previous.Bounds.Height));
                double top = child.Bounds.Y + (Math.Max(child.Bounds.Height, previous.Bounds.Height) - height) / 2;
                lines.Add(new Rect(x, top, GroupSeparatorThickness, height));
            }
            previous = child;
        }
        if (!lines.SequenceEqual(_separators))
        {
            _separators.Clear();
            _separators.AddRange(lines);
        }
        while (_marks.Count > lines.Count) { VisualChildren.Remove(_marks[^1]); _marks.RemoveAt(_marks.Count - 1); }
        while (_marks.Count < lines.Count)
        {
            GroupSeparatorMark mark = new() { Fill = SeparatorBrush() };
            _marks.Add(mark);
            VisualChildren.Add(mark);
        }
        for (int index = 0; index < lines.Count; index++)
        {
            _marks[index].Measure(lines[index].Size);
            _marks[index].Arrange(lines[index]);
        }
    }

    // The token of the app's decorative lines (as the header row's); without the application resources (unit tests) nothing is drawn.
    private IBrush? SeparatorBrush() =>
        this.TryFindResource("Workspace.Border", ActualThemeVariant, out object? found) ? found as IBrush : null;

    private void RefreshSeparatorBrush()
    {
        IBrush? brush = SeparatorBrush();
        foreach (GroupSeparatorMark mark in _marks) mark.Fill = brush;
    }

    /// <summary>One line between two groups: a filled rectangle that is never hit, focused or announced.</summary>
    private sealed class GroupSeparatorMark : Control
    {
        public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<GroupSeparatorMark, IBrush?>(nameof(Fill));
        static GroupSeparatorMark() => AffectsRender<GroupSeparatorMark>(FillProperty);
        public GroupSeparatorMark() { IsHitTestVisible = false; Focusable = false; }
        public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
        public override void Render(DrawingContext context)
        {
            if (Fill is { } brush) context.FillRectangle(brush, new Rect(Bounds.Size));
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Return popup children before a template or host is discarded.
        if (Owner?.OverflowHost is Panel popup)
            foreach (Control item in popup.Children.ToArray()) { popup.Children.Remove(item); Children.Add(item); MarkReduced(item, false); }
        if (Owner is { } owner)
            foreach (Control item in owner.OmittedHost.Children.ToArray()) { owner.OmittedHost.Children.Remove(item); Children.Add(item); MarkReduced(item, false); }
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
    /// <see cref="Resolve(IReadOnlyList{double}, IReadOnlyList{OverflowMode}, IReadOnlyList{bool}, double, double, IReadOnlyList{int}?)"/>
    /// with the bar's gaps (<see cref="ActionToolBarPanel.LeadingGap(bool, int, bool, int)"/>): which items go into "More" and the width
    /// the items that stay take on the bar, gaps included. <paramref name="widths"/> are the items' own widths.
    /// </summary>
    public static (bool[] Overflow, double Width) ResolveSpaced(IReadOnlyList<double> widths, IReadOnlyList<int> groups, IReadOnlyList<bool> segments,
        IReadOnlyList<OverflowMode> modes, IReadOnlyList<bool> visible, double available, double moreWidth, IReadOnlyList<int>? priorities = null)
    {
        int count = widths.Count;
        double[] w = widths.ToArray(); int[] g = groups.ToArray(); bool[] s = segments.ToArray();
        OverflowMode[] m = modes.ToArray(); bool[] v = visible.ToArray(); int[]? p = priorities?.ToArray();
        bool[] result = new bool[count];
        double width = ResolveSpaced(count, w, g, s, m, v, available, moreWidth, p, result, new double[count], new double[count], new bool[count], new int[count]);
        return (result, width);
    }

    /// <summary>
    /// Allocation-free form of <see cref="ResolveSpaced(IReadOnlyList{double}, IReadOnlyList{int}, IReadOnlyList{bool}, IReadOnlyList{OverflowMode}, IReadOnlyList{bool}, double, double, IReadOnlyList{int}?)"/>
    /// for the toolbar panel; returns the width on the bar. The gap in front of an item depends on the shown item before it, which depends
    /// on what overflows: each pass counts every item with the largest gap it had so far against the items then on the bar, so the widths
    /// only grow, what overflows only grows, and the passes end; a prioritized bar whose middle item moved away is never underestimated.
    /// A final greedy pass then takes back the items that fit with the gaps they really have.
    /// A joined pair (a run of neighbouring visible segments of one group and one overflow mode, <see cref="JoinedSegments"/>) is one
    /// unit for every decision: its width is the sum of its members, its rank the best of theirs, and it stays or moves as a whole, so
    /// the bar never keeps half of a List|Table choice while the other half sits under "More".
    /// <paramref name="gaps"/> ends with the real gap of each item that stays (0 for the others); <paramref name="widths"/>,
    /// <paramref name="previous"/> and <paramref name="order"/> are scratch of at least <paramref name="count"/>.
    /// </summary>
    public static double ResolveSpaced(int count, double[] itemWidths, int[] groups, bool[] segments, OverflowMode[] modes, bool[] visible,
        double available, double moreWidth, int[]? priorities, bool[] result, double[] gaps, double[] widths, bool[] previous, int[] order)
    {
        // First guess: every visible item stands on the bar except the Always ones.
        for (int i = 0; i < count; i++) { previous[i] = visible[i] && modes[i] == OverflowMode.Always; gaps[i] = 0; }
        for (int pass = 0; pass <= count; pass++)
        {
            int last = -1;
            for (int i = 0; i < count; i++)
            {
                if (!visible[i]) { widths[i] = 0; continue; }
                double gap = last < 0 ? 0 : ActionToolBarPanel.LeadingGap(segments[last], groups[last], segments[i], groups[i]);
                gaps[i] = Math.Max(gaps[i], gap);
                widths[i] = itemWidths[i] + gaps[i];
                if (!previous[i]) last = i;
            }
            Resolve(count, widths, modes, visible, available, moreWidth, priorities, result, order, segments, groups);
            bool settled = true;
            for (int i = 0; i < count; i++)
                if (result[i] != previous[i]) { settled = false; previous[i] = result[i]; }
            if (settled) break;
        }
        // The loop above counted every item with the largest gap it ever had, so an item can be under "More" although it fits with the gap
        // it really has (a group start that fits at 8 but not at 16). One greedy pass takes such items back, in the order they would have
        // been kept (first overflowed item first, so what stays under More is still the tail) and stops at the first one that does not fit.
        // Each candidate is counted with the real gaps of the bar it would make, and More is counted while anything remains under it.
        // A joined pair comes back as a whole or not at all (its best-ranked member comes first in the order; the others then stand on
        // the bar already or the pass has stopped).
        for (int k = 0; k < count; k++)
        {
            int i = order[k];
            if (!visible[i] || !result[i] || modes[i] == OverflowMode.Always) continue;
            int start = UnitStart(i, groups, segments, modes, visible), end = UnitEnd(i, count, groups, segments, modes, visible);
            SetUnit(start, end, visible, result, false);
            bool remaining = false;
            for (int j = 0; j < count; j++) if (visible[j] && result[j]) { remaining = true; break; }
            if (BarWidth(count, itemWidths, groups, segments, visible, result) + (remaining ? moreWidth : 0) > available)
            {
                SetUnit(start, end, visible, result, true);
                break;
            }
        }
        double total = 0;
        int shown = -1;
        for (int i = 0; i < count; i++)
        {
            if (!visible[i] || result[i]) { gaps[i] = 0; continue; }
            gaps[i] = shown < 0 ? 0 : ActionToolBarPanel.LeadingGap(segments[shown], groups[shown], segments[i], groups[i]);
            total += itemWidths[i] + gaps[i];
            shown = i;
        }
        return total;
    }

    // The width of the items that stay on the bar with their real gaps (no allocation).
    private static double BarWidth(int count, double[] itemWidths, int[] groups, bool[] segments, bool[] visible, bool[] overflowed)
    {
        double total = 0;
        int shown = -1;
        for (int i = 0; i < count; i++)
        {
            if (!visible[i] || overflowed[i]) continue;
            total += itemWidths[i] + (shown < 0 ? 0 : ActionToolBarPanel.LeadingGap(segments[shown], groups[shown], segments[i], groups[i]));
            shown = i;
        }
        return total;
    }

    /// <summary>
    /// Whether the visible items <paramref name="previous"/> and <paramref name="item"/> (neighbours among the visible items) form one
    /// joined unit: both are segments of one group with one overflow mode. Items with different modes stay independent, because an
    /// explicit Never or Always of one item must not drag its neighbour along.
    /// </summary>
    public static bool JoinedSegments(int previous, int item, int[] groups, bool[] segments, OverflowMode[] modes) =>
        segments[previous] && segments[item] && groups[previous] == groups[item] && modes[previous] == modes[item];

    // The first and last index of the joined unit of item i (i itself when it stands alone); invisible items in between take no part.
    private static int UnitStart(int i, int[] groups, bool[] segments, OverflowMode[] modes, bool[] visible)
    {
        int start = i;
        for (int j = i - 1; j >= 0; j--)
        {
            if (!visible[j]) continue;
            if (!JoinedSegments(j, start, groups, segments, modes)) break;
            start = j;
        }
        return start;
    }

    private static int UnitEnd(int i, int count, int[] groups, bool[] segments, OverflowMode[] modes, bool[] visible)
    {
        int end = i;
        for (int j = i + 1; j < count; j++)
        {
            if (!visible[j]) continue;
            if (!JoinedSegments(end, j, groups, segments, modes)) break;
            end = j;
        }
        return end;
    }

    private static void SetUnit(int start, int end, bool[] visible, bool[] result, bool overflow)
    {
        for (int j = start; j <= end; j++) if (visible[j]) result[j] = overflow;
    }

    /// <summary>
    /// Allocation-free form for the toolbar panel: the first <paramref name="count"/> entries of the arrays are read and
    /// <paramref name="result"/> is overwritten. <paramref name="order"/> is scratch of at least <paramref name="count"/>.
    /// </summary>
    public static void Resolve(int count, double[] widths, OverflowMode[] modes, bool[] visible, double available,
        double moreWidth, int[]? priorities, bool[] result, int[] order) =>
        Resolve(count, widths, modes, visible, available, moreWidth, priorities, result, order, null, null);

    // With segments and groups, a joined unit (JoinedSegments) is decided once, at the place of its best-ranked member (lowest
    // priority, ties in item order: the member the stable sort puts first), with the sum of its members' widths; without them every
    // item is its own unit (the historical rule).
    private static void Resolve(int count, double[] widths, OverflowMode[] modes, bool[] visible, double available,
        double moreWidth, int[]? priorities, bool[] result, int[] order, bool[]? segments, int[]? groups)
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
            int start = i, end = i;
            if (segments != null && groups != null)
            {
                start = UnitStart(i, groups, segments, modes, visible);
                end = UnitEnd(i, count, groups, segments, modes, visible);
            }
            if (start == end)
            {
                if (spilling || required + widths[i] > budget) result[i] = spilling = true;
                else required += widths[i];
                continue;
            }
            int head = -1;
            double unit = 0;
            for (int j = start; j <= end; j++)
            {
                if (!visible[j]) continue;
                unit += widths[j];
                if (head < 0 || (priorities != null && priorities[j] < priorities[head])) head = j;
            }
            // The other members were (or will be) decided with their head.
            if (head != i) continue;
            if (spilling || required + unit > budget) { SetUnit(start, end, visible, result, true); spilling = true; }
            else required += unit;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// The width rules of a popup menu, as plain numbers so they can be tested without a window. A row is a label and an optional shortcut;
/// the shortcut column is one column for all rows (the widest shortcut) and is never given up: when the menu is at its maximum width the
/// labels are trimmed with an ellipsis, the shortcut stays whole. The menu is as wide as its widest row (label, gap, shortcut) between a
/// minimum and a maximum.
/// </summary>
internal static class MenuLayoutPlan
{
    /// <summary>One row: the natural width of its label and of its shortcut text (0 when it has none), and whether it opens a submenu.</summary>
    internal readonly record struct Row(double Label, double Shortcut, bool HasSubmenu = false);

    /// <param name="Minimum">The narrowest menu.</param>
    /// <param name="Maximum">The widest menu; only labels give way to it.</param>
    /// <param name="Gap">The room between the label column and the shortcut column (at least <see cref="MenuLayout.MinimumShortcutGap"/> by the design).</param>
    /// <param name="Leading">Everything before the label: the row's edge and padding, the check and the icon column.</param>
    /// <param name="Trailing">Everything after the last column: the row's padding and edge.</param>
    /// <param name="Chevron">The width of the submenu chevron column, when a row has a submenu.</param>
    internal sealed record Options(double Minimum, double Maximum, double Gap, double Leading, double Trailing, double Chevron);

    /// <param name="Width">The width of the rows (the menu adds its own frame).</param>
    /// <param name="LabelColumn">What the labels may take.</param>
    /// <param name="ShortcutColumn">The widest shortcut, always whole.</param>
    /// <param name="Capped">The maximum width cut the natural width.</param>
    /// <param name="Trimmed">Per row: its label does not fit the label column and is trimmed.</param>
    internal sealed record Result(double Width, double LabelColumn, double ShortcutColumn, bool Capped, IReadOnlyList<bool> Trimmed);

    internal static Result Resolve(IReadOnlyList<Row> rows, Options options)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(options);
        double label = rows.Count == 0 ? 0 : rows.Max(row => row.Label);
        double shortcut = rows.Count == 0 ? 0 : rows.Max(row => row.Shortcut);
        bool anyShortcut = shortcut > 0;
        bool anySubmenu = rows.Any(row => row.HasSubmenu);
        double fixedWidth = options.Leading + options.Trailing + (anyShortcut ? options.Gap + shortcut : 0) + (anySubmenu ? options.Chevron : 0);
        double natural = fixedWidth + label;
        double maximum = Math.Max(options.Maximum, options.Minimum);
        // The shortcut column and the frame come first: a maximum that is narrower than them would clip the shortcut, so it gives way.
        double width = Math.Max(Math.Min(Math.Max(natural, options.Minimum), maximum), fixedWidth);
        double labelColumn = Math.Max(0, width - fixedWidth);
        bool[] trimmed = rows.Select(row => row.Label > labelColumn + 0.5).ToArray();
        return new Result(width, labelColumn, shortcut, natural > width + 0.5, trimmed);
    }
}

/// <summary>
/// Opt-in layout of the popup menus (ContextMenu and MenuFlyoutPresenter; the WorkspaceStyles style turns it on for both): the menu measures
/// to its widest row between the minimum and maximum widths of the menu tokens (MenuFlyoutMinWidth, MenuFlyoutMaxWidth), a label that does
/// not fit is trimmed with an ellipsis and says its full text as a tooltip, and the shortcut column is never clipped. Semi's menu rows put
/// the shortcut in one column shared by the rows of a menu. A popup takes its size from the first measuring of its content and does not follow
/// later changes; at that first measuring the shared column had not reached its final width yet (a row measured early did not know the longest
/// shortcut of a later row), so a long label next to a long shortcut was cut at the right edge of the window. The menu is therefore measured
/// until its shared columns have settled in the moment it is attached, before the popup first looks at it (<see cref="Settle"/>), with the
/// labels set to trim. The shortcut text itself and the gap before it (MenuItemInputGestureTextMargin) come from the theme; the key names are
/// localized elsewhere. After the first layout <see cref="Describe"/> adds the tooltip of every label that is still trimmed (<see cref="MenuLayoutPlan"/>).
/// </summary>
internal sealed class MenuLayout : AvaloniaObject
{
    private MenuLayout() { }

    /// <summary>The smallest room between a label and the shortcut column, in dip (the design asks for at least three 8-dip steps).</summary>
    internal const double MinimumShortcutGap = 24;

    /// <summary>The fallback menu widths (tokens MenuFlyoutMinWidth and MenuFlyoutMaxWidth in WorkspaceStyles) when a theme does not define them.</summary>
    internal const double DefaultMinimumWidth = 224;
    internal const double DefaultMaximumWidth = 416;

    // A layout pass is cheap, but the handler runs on every pass of the popup: a menu is settled after a few of them.
    private const int MaxPasses = 6;

    public static readonly AttachedProperty<bool> IsManagedProperty =
        AvaloniaProperty.RegisterAttached<MenuLayout, Control, bool>("IsManaged");

    public static bool GetIsManaged(Control control) => control.GetValue(IsManagedProperty);
    public static void SetIsManaged(Control control, bool value) => control.SetValue(IsManagedProperty, value);

    /// <summary>
    /// Opt-in for the submenu popup of a menu bar item (the header's "⋯" menu, <c>Menu &gt; MenuItem</c>): it is no <see cref="MenuBase"/>, so
    /// <see cref="IsManagedProperty"/> cannot reach it, and a label that is longer than the menu was cut off at the edge of its column
    /// mid-letter (ST-06). Every time the submenu opens, its rows get the same treatment as in a context menu: a label that does not fit ends
    /// in an ellipsis and says its full text as a tooltip (<see cref="TrimSubmenuRows"/>). A row that opens a submenu of its own takes part
    /// as well. The width of the popup stays the theme's (the menu tokens already cap it); only the labels give way to it.
    /// </summary>
    public static readonly AttachedProperty<bool> IsSubmenuManagedProperty =
        AvaloniaProperty.RegisterAttached<MenuLayout, MenuItem, bool>("IsSubmenuManaged");

    public static bool GetIsSubmenuManaged(MenuItem item) => item.GetValue(IsSubmenuManagedProperty);
    public static void SetIsSubmenuManaged(MenuItem item, bool value) => item.SetValue(IsSubmenuManagedProperty, value);

    private sealed class State
    {
        internal int Passes;
        internal bool Settled;
        // The rows were available when the menu was settled (a flyout presenter gets its rows only with its first layout).
        internal bool Reserved;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MenuBase, State> States = new();

    static MenuLayout()
    {
        IsManagedProperty.Changed.AddClassHandler<MenuBase>(OnManagedChanged);
        IsSubmenuManagedProperty.Changed.AddClassHandler<MenuItem>(OnSubmenuManagedChanged);
    }

    // The submenus that already listen for their opening (the property may be switched off and on again; one listener is enough).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MenuItem, object> ManagedSubmenus = new();
    private static readonly object Marker = new();

    // The tip a row got from the layout (not one of its own): kept to replace it when the label changes with the language, or drop it when
    // the label fits again.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MenuItem, string> OwnTips = new();

    // Looking at the rows is retried a few times: they get their template with the first layout of the popup, after the opening.
    private const int MaxSubmenuPasses = 8;

    private static void OnSubmenuManagedChanged(MenuItem owner, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || !ManagedSubmenus.TryAdd(owner, Marker)) return;
        owner.PropertyChanged += (_, args) =>
        {
            if (args.Property == MenuItem.IsSubMenuOpenProperty && args.NewValue is true) OnSubmenuOpened(owner);
        };
        if (owner.IsSubMenuOpen) OnSubmenuOpened(owner);
    }

    private static void OnSubmenuOpened(MenuItem owner)
    {
        // A row with a submenu of its own is managed too, whatever was added to the menu since the last opening.
        foreach (MenuItem row in owner.Items.OfType<MenuItem>().Where(row => row.Items.Count > 0)) SetIsSubmenuManaged(row, true);
        int pass = 0;
        void Look()
        {
            if (!owner.IsSubMenuOpen) return;
            if (TrimSubmenuRows(owner) || ++pass >= MaxSubmenuPasses) return;
            Dispatcher.UIThread.Post(Look, DispatcherPriority.Loaded);
        }
        // After the first layout and render of the popup, when its rows have their template and size.
        Dispatcher.UIThread.Post(Look, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// One look at the open submenu of <paramref name="owner"/>: every text label is set to trim with an ellipsis, and a label that is wider
    /// than the room its row gives it carries its full text as a tooltip (a row with a tip of its own keeps it). False while no row has been
    /// laid out yet (a later pass looks again).
    /// </summary>
    internal static bool TrimSubmenuRows(MenuItem owner)
    {
        bool laidOut = false;
        foreach (MenuItem row in owner.Items.OfType<MenuItem>().Where(row => row.IsVisible))
        {
            if (Part(row, "PART_HeaderPresenter") is not ContentPresenter { Child: TextBlock { TextWrapping: TextWrapping.NoWrap } block } header
                || header.Bounds.Width <= 0) continue;
            laidOut = true;
            if (block.TextTrimming != TextTrimming.CharacterEllipsis) block.TextTrimming = TextTrimming.CharacterEllipsis;
            bool trimmed = block.Text is { Length: > 0 } && NaturalLabelWidth(header) > header.Bounds.Width + 0.5;
            TipSubmenuRow(row, trimmed ? block.Text : null);
        }
        return laidOut;
    }

    /// <summary>Gives <paramref name="row"/> the tip <paramref name="text"/> (null: the label fits, no tip is needed); see <see cref="TrimSubmenuRows"/>.</summary>
    internal static void TipSubmenuRow(MenuItem row, string? text)
    {
        object? current = ToolTip.GetTip(row);
        bool ours = OwnTips.TryGetValue(row, out string? previous) && Equals(current, previous);
        // A row that brings a tip of its own (every command row: label and shortcut) is left alone.
        if (current != null && !ours) return;
        if (text != null)
        {
            if (ours && previous == text) return;
            ToolTip.SetTip(row, text);
            OwnTips.AddOrUpdate(row, text);
        }
        else if (ours)
        {
            ToolTip.SetTip(row, null);
            OwnTips.Remove(row);
        }
    }

    private static void OnManagedChanged(MenuBase menu, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || States.TryGetValue(menu, out _)) return;
        States.Add(menu, new State());
        bool subscribed = false;
        void OnMenuLayoutUpdated(object? sender, EventArgs args) => OnLayoutUpdated(menu);
        void OnAttached(object? sender, VisualTreeAttachmentEventArgs args)
        {
            if (!subscribed) { menu.LayoutUpdated += OnMenuLayoutUpdated; subscribed = true; }
            Restart(menu);
        }
        void OnDetached(object? sender, VisualTreeAttachmentEventArgs args)
        {
            if (!subscribed) return;
            menu.LayoutUpdated -= OnMenuLayoutUpdated;
            subscribed = false;
        }
        menu.AttachedToVisualTree += OnAttached;
        menu.DetachedFromVisualTree += OnDetached;
        if (menu.IsAttachedToVisualTree()) OnAttached(menu, null!);
    }

    // Every opening starts over: the same menu object may be filled again (ContextMenuBuilder.Populate), so a width from the last opening is dropped.
    private static void Restart(MenuBase menu)
    {
        if (!States.TryGetValue(menu, out State? state)) return;
        state.Passes = 0;
        state.Settled = false;
        (double minimum, double maximum) = Limits(menu);
        menu.MinWidth = minimum;
        menu.MaxWidth = maximum;
        Settle(menu);
        state.Reserved = RowsOf(menu).Length > 0;
    }

    /// <summary>
    /// Measures <paramref name="menu"/> until nothing it measures changes any more: the shared shortcut and icon columns grow while the rows
    /// are measured one after the other and invalidate the rows measured before, and a label that is set to trim changes its row. The popup
    /// then gets the menu's final size at its first look.
    /// </summary>
    internal static void Settle(MenuBase menu)
    {
        for (int pass = 0; pass < MaxPasses; pass++)
        {
            menu.Measure(Size.Infinity);
            bool trimmedNow = false;
            // The rows take the width the menu has: a menu that scrolls sideways (the flyout presenter's ScrollViewer measures its content
            // without a width limit) would let a long label run past the maximum width and be cut off by the popup instead of trimmed.
            if (menu.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(scroller => ReferenceEquals(scroller.FindAncestorOfType<MenuBase>(), menu)) is { } scroll
                && scroll.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled)
            {
                scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                trimmedNow = true;
            }
            foreach (MenuItem item in RowsOf(menu))
                if (Part(item, "PART_HeaderPresenter") is ContentPresenter { Child: TextBlock { TextWrapping: TextWrapping.NoWrap } block } && block.TextTrimming != TextTrimming.CharacterEllipsis)
                {
                    block.TextTrimming = TextTrimming.CharacterEllipsis;
                    trimmedNow = true;
                }
            if (!trimmedNow && menu.IsMeasureValid) break;
        }
        Reserve(menu);
    }

    // The cells of a menu row that line up across the rows: the check, the icon, the shortcut and the submenu chevron (Semi shares one column
    // for each in the rows of a menu).
    private static readonly string[] SharedParts = ["PART_ToggleIconPresenter", "PART_IconPresenter", "PART_InputGestureText", "PART_ExpandIcon"];

    /// <summary>
    /// Gives the menu the width its rows will have once the shared columns are final. Grid applies a shared column's width to the other rows
    /// only in a later layout pass, after the popup took its size from the rows as they measured on their own (each with its own icon,
    /// label and shortcut). So the width is worked out here: each row as it measured, plus what its shared columns will gain.
    /// </summary>
    private static void Reserve(MenuBase menu)
    {
        MenuItem[] rows = RowsOf(menu);
        if (rows.Length == 0) return;
        (double minimum, double maximum) = Limits(menu);
        double[][] own = rows.Select(row => SharedParts.Select(part => Part(row, part)?.DesiredSize.Width ?? 0).ToArray()).ToArray();
        double[] column = SharedParts.Select((_, index) => own.Max(cells => cells[index])).ToArray();
        // A menu that has been arranged before (a flyout presenter that is shown again, rows added after the first layout) has its shared
        // columns at their final width already, and the rows measure with them: nothing is left to add.
        bool final = rows.Any(row => Part(row, "PART_InputGestureText") is { Parent: Grid grid } cell
            && Grid.GetColumn(cell) is var index && index < grid.ColumnDefinitions.Count && grid.ColumnDefinitions[index].ActualWidth > 0);
        double rowWidth = rows.Select((row, index) => row.DesiredSize.Width + (final ? 0 : column.Sum() - own[index].Sum())).Max();
        double frame = Math.Max(0, menu.DesiredSize.Width - rows.Max(row => row.DesiredSize.Width));
        double wanted = Math.Min(Math.Max(frame + rowWidth, minimum), maximum);
        if (menu.DesiredSize.Width >= maximum - 0.5) wanted = maximum;
        if (Math.Abs(menu.MinWidth - wanted) > 0.5) menu.MinWidth = wanted;
    }

    private static MenuItem[] RowsOf(MenuBase menu) => menu.GetVisualDescendants().OfType<MenuItem>()
        .Where(item => item.IsVisible && ReferenceEquals(item.FindAncestorOfType<MenuBase>(), menu)).ToArray();

    private static (double Minimum, double Maximum) Limits(Control menu)
    {
        double minimum = Resource(menu, "MenuFlyoutMinWidth") is { } min and > 0 ? min : DefaultMinimumWidth;
        double maximum = Resource(menu, "MenuFlyoutMaxWidth") is { } max and > 0 ? max : DefaultMaximumWidth;
        return (minimum, Math.Max(minimum, maximum));
    }

    private static double? Resource(Control control, string key) =>
        control.TryFindResource(key, control.ActualThemeVariant, out object? value) && value is double number && !double.IsNaN(number) && !double.IsInfinity(number)
            ? number : null;

    private static void OnLayoutUpdated(MenuBase menu)
    {
        if (!States.TryGetValue(menu, out State? state) || state.Settled || !menu.IsAttachedToVisualTree() || menu.Bounds.Width <= 0) return;
        if (++state.Passes > MaxPasses) { state.Settled = true; return; }
        if (!state.Reserved && RowsOf(menu).Length > 0)
        {
            state.Reserved = true;
            Settle(menu);
        }
        // false: the rows are not laid out yet, a later pass sees them.
        if (Describe(menu)) state.Settled = true;
    }

    /// <summary>Adds the tooltip (the full text) to every label that is trimmed; false when the rows are not laid out yet.</summary>
    internal static bool Describe(MenuBase menu)
    {
        MenuItem[] items = RowsOf(menu);
        (double minimum, double maximum) = Limits(menu);

        List<MenuLayoutPlan.Row> rows = [];
        List<MenuItem> measured = [];
        double leading = 0, trailing = 0, chevron = 0, gap = MinimumShortcutGap;
        foreach (MenuItem item in items)
        {
            Control? header = Part(item, "PART_HeaderPresenter");
            Control? gesture = Part(item, "PART_InputGestureText");
            Control? expand = Part(item, "PART_ExpandIcon");
            // A row that has not been laid out yet (no template, no size) is left for a later pass.
            if (header == null || header.Bounds.Width <= 0 || header.TranslatePoint(default, item) is not { } headerOrigin) continue;
            double shortcut = 0;
            if (gesture is TextBlock { Text: { Length: > 0 } text } block)
            {
                // A shortcut keeps its whole width and the theme's gap in front of it.
                shortcut = TextWidth(block, text) + block.Margin.Right;
                gap = Math.Max(gap, block.Margin.Left);
            }
            bool submenu = expand is { IsVisible: true, Bounds.Width: > 0 };
            if (submenu) chevron = Math.Max(chevron, expand!.DesiredSize.Width);
            leading = Math.Max(leading, headerOrigin.X);
            trailing = Math.Max(trailing, item.Padding.Right + item.BorderThickness.Right);
            rows.Add(new MenuLayoutPlan.Row(NaturalLabelWidth(header), shortcut, submenu));
            measured.Add(item);
        }
        if (rows.Count == 0) return false;

        // The frame of the menu itself (its border and padding around the rows).
        double frame = Math.Max(0, menu.Bounds.Width - measured[0].Bounds.Width);
        MenuLayoutPlan.Result plan = MenuLayoutPlan.Resolve(rows,
            new MenuLayoutPlan.Options(Math.Max(0, minimum - frame), Math.Max(0, maximum - frame), gap, leading, trailing, chevron));
        for (int i = 0; i < measured.Count; i++) Tip(measured[i], plan.Trimmed[i]);
        return true;
    }

    private static Control? Part(MenuItem item, string name) =>
        item.GetVisualDescendants().OfType<Control>().FirstOrDefault(control => control.Name == name);

    private static double NaturalLabelWidth(Control header)
    {
        // A text header is measured by its text (its desired size may already be the trimmed one); anything else (a note that wraps) by its size.
        if (header is ContentPresenter { Child: TextBlock { TextWrapping: TextWrapping.NoWrap } block } && block.Text is { Length: > 0 } text)
            return TextWidth(block, text) + block.Margin.Left + block.Margin.Right;
        return header.DesiredSize.Width;
    }

    internal static double TextWidth(TextBlock block, string text)
    {
        FormattedText formatted = new(text, CultureInfo.CurrentUICulture, block.FlowDirection,
            new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch), block.FontSize, null);
        return Math.Ceiling(formatted.Width);
    }

    private static void Tip(MenuItem item, bool trimmed)
    {
        if (Part(item, "PART_HeaderPresenter") is not ContentPresenter { Child: TextBlock { TextWrapping: TextWrapping.NoWrap } block }) return;
        // A row that shows its text whole needs no tooltip of its own; a trimmed one says it, unless the row brings a tip of its own.
        if (trimmed && ToolTip.GetTip(item) is null && block.Text is { Length: > 0 } text) ToolTip.SetTip(item, text);
    }
}

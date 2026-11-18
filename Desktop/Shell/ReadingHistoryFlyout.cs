using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.MediaActions;

namespace MeowChan.Desktop;

/// <summary>
/// The size rules of the reading history list that opens under the header "Continue reading" button (plain functions: tests and
/// the window use the same ones). A context menu or submenu popup is only as wide as its shortest entry and cannot scroll, so the
/// list is a flyout with its own panel: at least <see cref="MinWidth"/> wide, never wider than <see cref="MaxWidth"/> or the window,
/// and as high as the setting "Threads in the Continue reading menu" says (<see cref="RowHeight"/> per entry) before it scrolls;
/// a small window shortens it further, so it never leaves the window.
/// </summary>
internal static class ReadingHistoryFlyoutLayout
{
    /// <summary>The least width of the list in DIPs (a thread title and the line below it stay readable).</summary>
    internal const double MinWidth = 320;
    /// <summary>The greatest width of the list in DIPs.</summary>
    internal const double MaxWidth = 520;
    /// <summary>The height of one entry (two text lines and their padding).</summary>
    internal const double RowHeight = WorkspaceMetrics.TwoLineRowHeight;
    /// <summary>The height of the row below the list (separator, "Show full history…" and padding).</summary>
    internal const double FooterHeight = 48;
    /// <summary>Space between the list and the window edge, and the height of the title bar and header above the list.</summary>
    internal const double WindowMargin = 16;
    internal const double HeaderReserve = 64;
    /// <summary>The list always has room for this many entries, however small the window is.</summary>
    internal const int LeastVisibleEntries = 2;

    /// <summary>The height at which the entries (without the footer row) start to scroll.</summary>
    internal static double ListMaxHeight(int shownEntries, double windowHeight)
    {
        double wanted = Math.Max(1, shownEntries) * RowHeight;
        double room = windowHeight - HeaderReserve - FooterHeight - 2 * WindowMargin;
        return Math.Max(LeastVisibleEntries * RowHeight, Math.Min(wanted, room));
    }

    /// <summary>The widest the list may be in a window of <paramref name="windowWidth"/> (never below the minimum).</summary>
    internal static double MaxWidthFor(double windowWidth) => Math.Max(MinWidth, Math.Min(MaxWidth, windowWidth - 2 * WindowMargin));
}

/// <summary>What the window shows in the history list at one opening.</summary>
/// <param name="Rows">The listed places, newest first (already limited to the setting).</param>
/// <param name="CanJump">False while another action owns the reader: the rows stay, disabled, with the reason.</param>
/// <param name="HasHistory">True when "Show full history…" has anything to show.</param>
internal sealed record ReadingHistoryFlyoutContent(IReadOnlyList<ReadingHistoryRow> Rows, bool CanJump, bool HasHistory);

/// <summary>
/// The content of the history flyout: one button per remembered place (title on the first line; source, board, thread, post and time on
/// the second; both trimmed, the tooltip has the whole text), a scroll area for them and a pinned "Show full history…" row below.
/// Up, Down, Home and End move through the rows, Escape asks the owner to close.
/// </summary>
internal sealed class ReadingHistoryPanel : StackPanel
{
    internal const string ListName = "ReadingHistoryFlyoutList";
    private readonly ScrollViewer _scroller;
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly Button _showAll;
    private readonly List<Button> _rowButtons = [];
    private string _signature = string.Empty;

    protected override Type StyleKeyOverride => typeof(StackPanel);

    internal ReadingHistoryPanel()
    {
        Name = "ReadingHistoryFlyoutPanel";
        Spacing = 4;
        MinWidth = ReadingHistoryFlyoutLayout.MinWidth;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        _rows.Name = ListName;
        _scroller = new ScrollViewer
        {
            Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Border separator = new()
        {
            Height = 1, Margin = new Thickness(8, 2), IsHitTestVisible = false,
            [!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Workspace.Border")
        };
        _showAll = new Button { Name = "ReadingHistoryShowAll", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
        _showAll.Classes.Add("gallery-overflow-item");
        _showAll.Click += (_, _) => { if (_showAll.IsEnabled) ShowAllChosen?.Invoke(); };
        Children.Add(_scroller);
        Children.Add(separator);
        Children.Add(_showAll);
    }

    /// <summary>A place was chosen.</summary>
    internal event Action<LocalReadingPosition>? PlaceChosen;
    /// <summary>"Show full history…" was chosen.</summary>
    internal event Action? ShowAllChosen;
    /// <summary>Escape was pressed: the owner closes the flyout.</summary>
    internal event Action? CloseRequested;

    /// <summary>The buttons of the places, in order (tests and smokes).</summary>
    internal IReadOnlyList<Button> PlaceButtons => _rowButtons;
    /// <summary>The "Show full history…" button.</summary>
    internal Button ShowAllButton => _showAll;
    /// <summary>The scroll area of the places (its MaxHeight is the size rule).</summary>
    internal ScrollViewer Scroller => _scroller;

    /// <summary>Shows <paramref name="content"/> and sizes the list for a window of the given size.</summary>
    internal void Refresh(ReadingHistoryFlyoutContent content, Size windowSize)
    {
        MaxWidth = ReadingHistoryFlyoutLayout.MaxWidthFor(windowSize.Width);
        _scroller.MaxHeight = ReadingHistoryFlyoutLayout.ListMaxHeight(content.Rows.Count, windowSize.Height);
        AutomationProperties.SetName(_rows, Texts.T("resume.history.menu"));
        string busy = Texts.T("archive.command.busy");
        string signature = string.Join('|', content.Rows.Select(row => row.Id + ":" + row.Title + ":" + row.Detail)) + "#" + content.CanJump;
        if (signature != _signature)
        {
            _signature = signature;
            _rows.Children.Clear();
            _rowButtons.Clear();
            if (content.Rows.Count == 0)
            {
                TextBlock note = new() { Text = Texts.T("resume.history.empty"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 8) };
                note.Classes.Add("workspace-meta");
                _rows.Children.Add(note);
            }
            foreach (ReadingHistoryRow row in content.Rows)
            {
                Button button = CreateRow(row, content.CanJump ? null : busy);
                _rowButtons.Add(button);
                _rows.Children.Add(button);
            }
        }
        string all = CommandCatalog.Label(ReadingHistoryMenu.HistoryCommandId);
        _showAll.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { Centered(CommandBindings.Icon(ReadingHistoryMenu.HistoryCommandId)), new TextBlock { Text = all, VerticalAlignment = VerticalAlignment.Center } }
        };
        _showAll.IsEnabled = content.HasHistory;
        string allTip = content.HasHistory ? all : Texts.T("resume.history.empty");
        AutomationProperties.SetName(_showAll, all);
        ToolTip.SetTip(_showAll, allTip);
        ToolTip.SetShowOnDisabled(_showAll, true);
    }

    private static Control Centered(Control control)
    {
        control.VerticalAlignment = VerticalAlignment.Center;
        return control;
    }

    private Button CreateRow(ReadingHistoryRow row, string? disabledReason)
    {
        TextBlock title = new() { Text = row.Title, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        TextBlock detail = new() { Text = row.Detail, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        detail.Classes.Add("workspace-meta");
        StackPanel lines = new() { Spacing = 0, VerticalAlignment = VerticalAlignment.Center, Children = { title, detail } };
        Grid layout = new() { ColumnDefinitions = new ColumnDefinitions("20,*"), ColumnSpacing = 8 };
        Control icon = Centered(CommandBindings.Icon(ReadingHistoryMenu.ContinueCommandId));
        layout.Children.Add(icon);
        layout.Children.Add(lines);
        Grid.SetColumn(lines, 1);
        Button button = new()
        {
            Name = "ReadingHistoryRow", Tag = row.Id, Content = layout, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 8), IsEnabled = disabledReason == null
        };
        button.Classes.Add("gallery-overflow-item");
        AutomationProperties.SetName(button, row.Title + ", " + row.Detail);
        AutomationProperties.SetHelpText(button, disabledReason ?? row.Tip);
        // The tooltip has the whole text the row had to trim: the thread's title, where it is and the post it continues at.
        ToolTip.SetTip(button, disabledReason ?? row.Tip + "\n" + row.Title + "\n" + row.Detail);
        ToolTip.SetShowOnDisabled(button, true);
        LocalReadingPosition place = row.Place;
        button.Click += (_, _) => { if (button.IsEnabled) PlaceChosen?.Invoke(place); };
        return button;
    }

    private IEnumerable<Button> Usable() =>
        _rowButtons.Concat([_showAll]).Where(button => button.Focusable && button.IsEffectivelyVisible && button.IsEffectivelyEnabled);

    /// <summary>Puts the keyboard focus on the first usable row (a flyout that just opened); false when there is none.</summary>
    internal bool FocusFirst()
    {
        Button? first = Usable().FirstOrDefault();
        first?.Focus(NavigationMethod.Tab);
        return first != null;
    }

    /// <summary>Escape raises <see cref="CloseRequested"/>; Up, Down, Home and End move through the usable rows (cycling). True when the key was used.</summary>
    internal bool HandleMenuKey(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Handled || e.KeyModifiers != KeyModifiers.None) return false;
        if (e.Key == Key.Escape)
        {
            CloseRequested?.Invoke();
            return true;
        }
        if (e.Key is not (Key.Down or Key.Up or Key.Home or Key.End)) return false;
        Button[] rows = Usable().ToArray();
        if (rows.Length == 0) return false;
        int current = Array.FindIndex(rows, row => row.IsKeyboardFocusWithin);
        int next = e.Key switch
        {
            Key.Home => 0,
            Key.End => rows.Length - 1,
            Key.Down => current < 0 ? 0 : (current + 1) % rows.Length,
            _ => current < 0 ? rows.Length - 1 : (current - 1 + rows.Length) % rows.Length
        };
        rows[next].Focus(NavigationMethod.Tab);
        rows[next].BringIntoView();
        return true;
    }
}

/// <summary>
/// Opens the reading history list under the header button: a keyboard-operable flyout (<see cref="ReadingHistoryPanel"/>) instead of a
/// context menu. The triggers (right mouse button, Menu key, Shift+F10 and the configurable context-open command on the controls it is
/// given) and <see cref="Show"/> (the arrow button, a click inside an open thread, the app menu entry) open the same list; it is
/// composed fresh at every opening and the focus returns to the control it came from when it closes.
/// </summary>
internal sealed class ReadingHistoryFlyout : IDisposable
{
    private static readonly TimeSpan PairedRequestWindow = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan PointerToggleWindow = TimeSpan.FromMilliseconds(250);

    private readonly Flyout _flyout = new() { Placement = PlacementMode.BottomEdgeAlignedRight };
    private readonly ReadingHistoryPanel _panel = new();
    private readonly Func<Control?> _anchor;
    private readonly Func<ReadingHistoryFlyoutContent?> _compose;
    private readonly Func<Size> _windowSize;
    private readonly Action? _fallbackFocus;
    private readonly List<Control> _triggers = [];
    private DateTime _lastKeyOpen = DateTime.MinValue;
    private bool _keyboard;
    private bool _hadFocus;
    private Control? _openedOn;
    private DateTime _closedAt = DateTime.MinValue;
    private bool _disposed;

    /// <param name="anchor">The control the list opens under (right edge aligned); null or not on screen opens nothing.</param>
    /// <param name="compose">The content for this opening; null opens nothing.</param>
    /// <param name="windowSize">The size of the window (it limits the list).</param>
    /// <param name="jump">Opens one place (runs after the flyout closed).</param>
    /// <param name="showAll">Opens the window with every remembered thread (runs after the flyout closed).</param>
    /// <param name="fallbackFocus">Where the focus goes when the control the list came from cannot take it any more.</param>
    internal ReadingHistoryFlyout(Func<Control?> anchor, Func<ReadingHistoryFlyoutContent?> compose, Func<Size> windowSize,
        Action<LocalReadingPosition> jump, Action showAll, Action? fallbackFocus = null)
    {
        _anchor = anchor ?? throw new ArgumentNullException(nameof(anchor));
        _compose = compose ?? throw new ArgumentNullException(nameof(compose));
        _windowSize = windowSize ?? throw new ArgumentNullException(nameof(windowSize));
        ArgumentNullException.ThrowIfNull(jump);
        ArgumentNullException.ThrowIfNull(showAll);
        _fallbackFocus = fallbackFocus;
        _flyout.FlyoutPresenterClasses.Add("reading-history");
        _flyout.Content = _panel;
        _panel.PlaceChosen += place => { _flyout.Hide(); Dispatcher.UIThread.Post(() => jump(place), DispatcherPriority.Input); };
        _panel.ShowAllChosen += () => { _flyout.Hide(); Dispatcher.UIThread.Post(showAll, DispatcherPriority.Input); };
        _panel.CloseRequested += () => _flyout.Hide();
        _panel.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            _keyboard = true;
            if (_panel.HandleMenuKey(e)) e.Handled = true;
        }, RoutingStrategies.Tunnel);
        _panel.GotFocus += (_, _) => _hadFocus = true;
        _flyout.Opened += (_, _) => Dispatcher.UIThread.Post(() => _panel.FocusFirst(), DispatcherPriority.Loaded);
        _flyout.Closed += (_, _) => { _closedAt = DateTime.UtcNow; RestoreFocus(); };
    }

    /// <summary>True while the list is open.</summary>
    internal bool IsOpen => _flyout.IsOpen;
    /// <summary>The flyout (smokes and tests).</summary>
    internal Flyout Flyout => _flyout;
    /// <summary>The content of the flyout (smokes and tests).</summary>
    internal ReadingHistoryPanel Panel => _panel;

    /// <summary>
    /// Lets right-click, the Menu key, Shift+F10 and the context-open command on <paramref name="trigger"/> open the list under the anchor
    /// (not at the pointer, so the list always sits in the same place under the button).
    /// </summary>
    internal void AddTrigger(Control trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        _triggers.Add(trigger);
        trigger.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
        trigger.AddHandler(Control.ContextRequestedEvent, OnContextRequested, RoutingStrategies.Bubble);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_disposed || !MediaMenuPresenter.IsContextKey(e)) return;
        _lastKeyOpen = DateTime.UtcNow;
        e.Handled = true;
        Show(keyboard: true);
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_disposed || e.Handled) return;
        e.Handled = true;
        // Avalonia may follow a handled key with a positionless request of its own: it is consumed, not opened again.
        bool positioned = sender is Control origin && e.TryGetPosition(origin, out _);
        if (!positioned && DateTime.UtcNow - _lastKeyOpen < PairedRequestWindow) return;
        Show(keyboard: !positioned);
    }

    /// <summary>Opens the list under the anchor (closes it when it is open). False when it did not open.</summary>
    internal bool Show(bool keyboard = false)
    {
        if (_disposed) return false;
        if (_flyout.IsOpen)
        {
            _flyout.Hide();
            return false;
        }
        // A click on the arrow while the list is open first dismisses it (the press), then arrives as a click: that click closes, it does not reopen.
        if (!keyboard && DateTime.UtcNow - _closedAt < PointerToggleWindow) return false;
        Control? anchor = _anchor();
        if (anchor == null || !anchor.IsAttachedToVisualTree() || !anchor.IsEffectivelyVisible || !anchor.IsEffectivelyEnabled) return false;
        ReadingHistoryFlyoutContent? content = _compose();
        if (content == null) return false;
        _keyboard = keyboard;
        _hadFocus = false;
        _openedOn = anchor;
        _panel.Refresh(content, _windowSize());
        _flyout.ShowAt(anchor);
        return true;
    }

    /// <summary>Closes the list, if open.</summary>
    internal void Close() { if (_flyout.IsOpen) _flyout.Hide(); }

    private void RestoreFocus()
    {
        if (!_hadFocus || _disposed) return;
        _hadFocus = false;
        Control? target = _openedOn;
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || _flyout.IsOpen) return;
            TopLevel? top = target == null ? null : TopLevel.GetTopLevel(target);
            if (top is Window owner && owner.OwnedWindows.Any(window => window.IsVisible)) return; // a window the choice opened owns the focus
            object? focused = top?.FocusManager?.GetFocusedElement();
            if (focused is Control current && current.IsEffectivelyVisible && current.IsEffectivelyEnabled
                && !current.GetSelfAndVisualAncestors().Contains(_panel)) return; // something else took it (the opened page)
            if (target != null && target.IsAttachedToVisualTree() && target.IsEffectivelyVisible && target.IsEffectivelyEnabled)
                target.Focus(_keyboard ? NavigationMethod.Tab : NavigationMethod.Pointer);
            else _fallbackFocus?.Invoke();
        }, DispatcherPriority.Background);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Close();
        _disposed = true;
        foreach (Control trigger in _triggers)
        {
            trigger.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            trigger.RemoveHandler(Control.ContextRequestedEvent, OnContextRequested);
        }
        _triggers.Clear();
    }
}

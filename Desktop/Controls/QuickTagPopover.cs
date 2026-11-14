using System;
using System.Linq;
using System.Threading.Tasks;
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
using MeowChan.Archive;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Controls;

/// <summary>Why a quick tag popover closed.</summary>
internal enum QuickTagPopoverCloseReason
{
    /// <summary>A click outside, Alt+Tab or another light dismissal.</summary>
    Dismissed,
    /// <summary>The × button or Esc.</summary>
    Closed,
    /// <summary>"Advanced…": the host opens the full tag dialog for the same target.</summary>
    Advanced,
    /// <summary>The target is gone: the page changed, the file was replaced, the host's guard no longer holds.</summary>
    Stale,
    /// <summary>Another popover opened.</summary>
    Replaced,
    /// <summary>The anchor left the visual tree (page switch, rebuilt tile).</summary>
    Detached
}

/// <summary>What a host passes besides the target: the suggestion source, the guard and the tag dialog hand-over.</summary>
internal sealed record QuickTagPopoverOptions
{
    /// <summary>Limits of the surface (undo depth, strip size); null uses the defaults.</summary>
    internal QuickTagOptions? Quick { get; init; }
    /// <summary>The suggestion strip; a <see cref="QuickTagContextSource"/> is warmed, repainted when warm and disposed with the popover.</summary>
    internal IQuickTagSuggestionSource? Suggestions { get; init; }
    /// <summary>The Danbooru catalog reader for the source tag group of the field.</summary>
    internal Func<BooruCatalogSnapshot?>? ReadSourceTags { get; init; }
    /// <summary>True while the target is still what is on screen; false closes the popover (<see cref="QuickTagPopoverCloseReason.Stale"/>).</summary>
    internal Func<bool>? IsCurrent { get; init; }
    /// <summary>False hides "Advanced…" (a host without a full dialog for the target).</summary>
    internal bool AdvancedAvailable { get; init; } = true;
    /// <summary>False when the host restores the keyboard focus itself (the media page rebuilds its tiles after a change).</summary>
    internal bool RestoreFocus { get; init; } = true;
    /// <summary>Base name of the controls (<c>QuickTagPopoverInput</c>, …).</summary>
    internal string Name { get; init; } = "QuickTagPopover";
}

/// <summary>
/// The right-click tag popover (TP3 of PLAN-SCHNELL-TAGGEN §1.1 and §3.2): one <see cref="QuickTagSurface"/> in a flyout anchored at
/// the tile or attachment the menu opened on. It stays open while the pointer is inside, closes on a click outside, Esc (after an
/// open suggestion list), the × button, a page change or when the target is lost, and gives the focus back to the origin.
/// "Advanced…" closes it with <see cref="QuickTagPopoverCloseReason.Advanced"/> so the host can open the full dialog. Tab cycles
/// inside; the × is the last stop. The popover owns the session and the surface it is given and disposes both when it closes.
/// A target that cannot be tagged (a Danbooru medium, an ambiguous file) gets <see cref="CreateBlocked"/>: the reason as text.
/// At most one popover is open at a time.
/// </summary>
internal sealed class QuickTagPopover
{
    /// <summary>Width of the popover in dip (plan §3.2).</summary>
    internal const double Width = 360;
    /// <summary>Technical bound of the height; the body of the surface scrolls beyond it. A layout measure, not a user limit.</summary>
    internal const double MaximumHeight = 520;
    private static readonly TimeSpan GuardInterval = TimeSpan.FromMilliseconds(250);

    private readonly Flyout _flyout = new() { Placement = PlacementMode.BottomEdgeAlignedLeft, ShowMode = FlyoutShowMode.Standard };
    private readonly Control _anchor;
    private readonly QuickTagPopoverOptions _options;
    private readonly TaskCompletionSource<QuickTagPopoverCloseReason> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Border _frame = new() { Padding = new Thickness(8), Width = Width, MaxHeight = MaximumHeight };
    private readonly Action? _unsubscribe;
    private readonly QuickTagSession? _session;
    private DispatcherTimer? _guard;
    private Control? _origin;
    private bool _shown;
    private bool _closed;

    private QuickTagPopover(Control anchor, QuickTagPopoverOptions options, QuickTagSurface? surface, QuickTagSession? session,
        Control content, Action? unsubscribe)
    {
        _anchor = anchor;
        _options = options;
        Surface = surface;
        _session = session;
        _unsubscribe = unsubscribe;
        _frame.Child = content;
        _frame.Name = options.Name + "Frame";
        // Tab cycles inside the popover; Shift+Tab from the field reaches the × directly (it is the last tab stop of the surface).
        KeyboardNavigation.SetTabNavigation(_frame, KeyboardNavigationMode.Cycle);
        _frame.AddHandler(InputElement.KeyDownEvent, OnFrameKey, RoutingStrategies.Tunnel);
        _flyout.FlyoutPresenterClasses.Add("quick-tag-popover");
        _flyout.Content = _frame;
        _flyout.Closed += OnFlyoutClosed;
        anchor.DetachedFromVisualTree += OnAnchorDetached;
        if (session != null) session.Updated += OnSessionUpdated;
    }

    /// <summary>The popover open now, if any.</summary>
    internal static QuickTagPopover? Current { get; private set; }

    internal Flyout Flyout => _flyout;
    /// <summary>The content of the flyout: the surface (or the blocked text) in its frame; Tab cycles inside it.</summary>
    internal Border Frame => _frame;
    /// <summary>The tag surface; null for a blocked popover.</summary>
    internal QuickTagSurface? Surface { get; }
    internal Control Anchor => _anchor;
    /// <summary>The control that had the keyboard focus when the popover opened; the focus returns to it.</summary>
    internal Control? Origin => _origin;
    internal bool IsOpen => _shown && !_closed;
    internal bool IsClosed => _closed;
    /// <summary>How it closed; null while open.</summary>
    internal QuickTagPopoverCloseReason? Reason { get; private set; }
    /// <summary>Completes with the close reason (also for a popover replaced by another one).</summary>
    internal Task<QuickTagPopoverCloseReason> Result => _result.Task;
    /// <summary>Raised once, on the UI thread, after the popover closed and its session and surface were disposed.</summary>
    internal event Action<QuickTagPopoverCloseReason>? Closed;

    // ---- creation -----------------------------------------------------------------------------------------------------

    /// <summary>Builds the popover around <paramref name="session"/> without showing it. <paramref name="label"/> is the file name or selection.</summary>
    internal static QuickTagPopover Create(Control anchor, QuickTagSession session, string label, QuickTagPopoverOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(session);
        options ??= new QuickTagPopoverOptions();
        QuickTagSurface surface = new(session, options.Quick, options.Name)
        {
            TargetLabel = label ?? string.Empty,
            ReadSourceTags = options.ReadSourceTags,
            NavigationEnabled = false
        };
        Action? unsubscribe = null;
        if (options.Suggestions is { } source)
        {
            surface.Suggestions = source;
            if (source is QuickTagContextSource context)
            {
                context.Attach(session);
                Action refresh = surface.Refresh;
                context.Warmed += refresh;
                unsubscribe = () => { context.Warmed -= refresh; context.Dispose(); };
                if (session.Snapshot is { } snapshot) context.Prewarm(snapshot);
            }
        }
        surface.AdvancedButton.IsVisible = options.AdvancedAvailable;
        QuickTagPopover popover = new(anchor, options, surface, session, surface, unsubscribe);
        surface.CloseRequested += () => popover.Close(QuickTagPopoverCloseReason.Closed);
        surface.AdvancedRequested += () => popover.Close(QuickTagPopoverCloseReason.Advanced);
        return popover;
    }

    /// <summary>
    /// A popover that only says why the target cannot be tagged (the reason text of the menu, for example a Danbooru medium).
    /// Esc and × close it; it holds no session.
    /// </summary>
    internal static QuickTagPopover CreateBlocked(Control anchor, string label, string reason, QuickTagPopoverOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        options ??= new QuickTagPopoverOptions();
        string title = string.IsNullOrWhiteSpace(label) ? Texts.T("tags.quick.heading") : Texts.T("tags.quick.title", label.Trim());
        TextBlock heading = new() { Text = title, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center, Name = options.Name + "Title" };
        ToolTip.SetTip(heading, title);
        Button close = new() { Name = options.Name + "Close", VerticalAlignment = VerticalAlignment.Center };
        WorkspaceIcons.MakeSquare(close, WorkspaceIcons.Close, Texts.T("tags.quick.close"), Texts.T("tags.quick.closeTip"));
        // Same header as the surface: the title on the left, the × alone at the right edge, 24 dip of empty space between.
        Grid header = new() { ColumnDefinitions = new ColumnDefinitions("*,24,Auto"), Name = options.Name + "Header" };
        header.Children.Add(heading);
        header.Children.Add(close);
        Grid.SetColumn(close, 2);
        TextBlock text = new() { Text = reason, TextWrapping = TextWrapping.Wrap, Classes = { "workspace-help" }, Name = options.Name + "Reason" };
        AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Polite);
        StackPanel content = new() { Spacing = 12, Children = { header, text } };
        AutomationProperties.SetName(content, Texts.T("tags.quick.name", string.IsNullOrWhiteSpace(label) ? Texts.T("tags.quick.heading") : label.Trim()));
        AutomationProperties.SetHelpText(content, reason);
        QuickTagPopover popover = new(anchor, options, null, null, content, null);
        close.Click += (_, _) => popover.Close(QuickTagPopoverCloseReason.Closed);
        popover.BlockedClose = close;
        popover.BlockedReason = reason;
        return popover;
    }

    /// <summary>The × of a blocked popover (the first and only tab stop); null for a popover with a surface.</summary>
    internal Button? BlockedClose { get; private set; }
    /// <summary>The reason a blocked popover states; null for a popover with a surface.</summary>
    internal string? BlockedReason { get; private set; }

    /// <summary>Opens the popover on its anchor, replacing any other one, and returns it.</summary>
    internal QuickTagPopover Show()
    {
        if (_closed || _shown) return this;
        Current?.Close(QuickTagPopoverCloseReason.Replaced);
        _shown = true;
        Current = this;
        _origin = TopLevel.GetTopLevel(_anchor)?.FocusManager?.GetFocusedElement() as Control;
        _flyout.Opened += OnOpened;
        _flyout.ShowAt(_anchor);
        return this;
    }

    /// <summary>Creates and opens a popover for a taggable target.</summary>
    internal static QuickTagPopover Open(Control anchor, QuickTagSession session, string label, QuickTagPopoverOptions? options = null) =>
        Create(anchor, session, label, options).Show();

    /// <summary>Creates and opens the reason popover of a target that cannot be tagged.</summary>
    internal static QuickTagPopover OpenBlocked(Control anchor, string label, string reason, QuickTagPopoverOptions? options = null) =>
        CreateBlocked(anchor, label, reason, options).Show();

    // ---- lifetime -----------------------------------------------------------------------------------------------------

    private void OnOpened(object? sender, EventArgs e)
    {
        _flyout.Opened -= OnOpened;
        if (_closed) return;
        BlockedClose?.Focus(NavigationMethod.Tab);
        if (_options.IsCurrent != null)
        {
            _guard = new DispatcherTimer(GuardInterval, DispatcherPriority.Background, (_, _) => CheckCurrent());
            _guard.Start();
        }
        CheckCurrent();
    }

    private void OnSessionUpdated() => CheckCurrent();

    // The target is the thing on screen when the popover opened. A changed store refreshes the page but not the target, so
    // the guard is the host's page/target check, never "the menu session is still current" (a write invalidates that on purpose).
    private void CheckCurrent()
    {
        if (!_closed && _options.IsCurrent is { } current && !current()) Close(QuickTagPopoverCloseReason.Stale);
    }

    private void OnAnchorDetached(object? sender, VisualTreeAttachmentEventArgs e) => Close(QuickTagPopoverCloseReason.Detached);

    private void OnFlyoutClosed(object? sender, EventArgs e)
    {
        if (!_closed) { _closed = true; Finish(QuickTagPopoverCloseReason.Dismissed); }
    }

    /// <summary>Closes the popover; a second call is ignored. Safe before it was shown (a created popover is released).</summary>
    internal void Close(QuickTagPopoverCloseReason reason = QuickTagPopoverCloseReason.Closed)
    {
        if (_closed) return;
        _closed = true;
        bool wasShown = _shown;
        if (wasShown) _flyout.Hide();
        Finish(reason);
    }

    private void Finish(QuickTagPopoverCloseReason reason)
    {
        if (Reason != null) return;
        Reason = reason;
        _guard?.Stop();
        _guard = null;
        _flyout.Opened -= OnOpened;
        _flyout.Closed -= OnFlyoutClosed;
        _anchor.DetachedFromVisualTree -= OnAnchorDetached;
        _frame.RemoveHandler(InputElement.KeyDownEvent, OnFrameKey);
        if (_session != null) _session.Updated -= OnSessionUpdated;
        if (ReferenceEquals(Current, this)) Current = null;
        _unsubscribe?.Invoke();
        Surface?.Dispose();
        _session?.Dispose();
        if (reason is QuickTagPopoverCloseReason.Dismissed or QuickTagPopoverCloseReason.Closed && _options.RestoreFocus) RestoreFocus();
        Closed?.Invoke(reason);
        _result.TrySetResult(reason);
    }

    // The focus goes back to where it was (the tile): the pattern of the tag dialog of the gallery. Posted so it runs after the
    // flyout has handed the focus back itself; a control that is gone or hidden is skipped.
    private void RestoreFocus()
    {
        Control? origin = _origin, anchor = _anchor;
        Dispatcher.UIThread.Post(() =>
        {
            static bool Usable(Control? control) => control != null && control.IsAttachedToVisualTree() && control.IsEffectivelyVisible && control.IsEffectivelyEnabled;
            if (Current != null) return;
            if (Usable(origin)) origin!.Focus();
            else if (Usable(anchor)) anchor.Focus();
        }, DispatcherPriority.Background);
    }

    // ---- keys ---------------------------------------------------------------------------------------------------------

    private void OnFrameKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (Surface == null)
        {
            // A blocked popover has no list to close first: Esc closes it.
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None) { e.Handled = true; Close(QuickTagPopoverCloseReason.Closed); }
            return;
        }
        if (!ShortcutService.IsCapturing && TryRouteCommand(Surface, e, ShortcutService.Current)) e.Handled = true;
    }

    /// <summary>
    /// The wired quick tag commands (<see cref="QuickTagCommands.Undo"/> and the nine suggestion toggles) by the gesture the user
    /// configured for them. The defaults (Ctrl+Z, Alt+1…9) are also the surface's own keys; this makes a rebinding count inside the
    /// text field too. A Ctrl+Z over typed text stays the field's own undo. True when a command took the key.
    /// </summary>
    internal static bool TryRouteCommand(QuickTagSurface surface, KeyEventArgs e, EffectiveShortcutBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(bindings);
        for (int number = 1; number <= QuickTagCommands.SuggestionCount; number++)
            if (bindings.Matches(e, QuickTagCommands.Suggestion(number)))
                return surface.Execute(new QuickTagKeyRoute(QuickTagKeyAction.ToggleSuggestion, number - 1));
        if (bindings.Matches(e, QuickTagCommands.Undo))
        {
            if (surface.FocusZone() == QuickTagFocus.Field && !string.IsNullOrEmpty(surface.Input.Text)) return false;
            return surface.Execute(new QuickTagKeyRoute(QuickTagKeyAction.Undo));
        }
        return false;
    }
}

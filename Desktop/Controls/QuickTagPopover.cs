using System;
using System.IO;
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

/// <summary>
/// What the favourite and rating row of a popover reads and writes. The host owns the stores and reports a failed write itself; the popover
/// shows <see cref="View"/> again after every request, so a refused write leaves the row on the real state.
/// </summary>
internal sealed record QuickTagPopoverDecision
{
    /// <summary>What the targets carry now (read again after every request).</summary>
    internal required Func<GalleryDecisionView> View { get; init; }
    /// <summary>A star (1 to 5) or the remove button (null): writes the rating of every target.</summary>
    internal required Func<int?, Task> SetRating { get; init; }
    /// <summary>The flag the favourite button asks for; null hides the button (a host without a favourites call for its targets).</summary>
    internal Func<bool, Task>? SetFavorite { get; init; }
    /// <summary>R: the stars take the focus when the popover opens (the checked star, star 1 while unrated); otherwise the tag field does.</summary>
    internal bool FocusRating { get; init; }
    /// <summary>True once a request wrote something (read by the host after the popover closed: the page then refreshes its tiles and captions).</summary>
    internal Func<bool>? HasChanged { get; init; }
    /// <summary>Called once when the popover closed (a decision session the host created for it is disposed here).</summary>
    internal Action? Release { get; init; }
}

/// <summary>How a page asked the shell for the "Rate and tag" popover of one medium.</summary>
/// <param name="FocusRating">The stars take the focus (R, the menu entry, the rating caption); false: the tag field does (Ctrl+Shift+T).</param>
/// <param name="TagsBlocked">The localized reason the medium cannot be tagged (a Danbooru medium, a file shared by several posts), or null; the popover then
/// still offers the stars and says the reason as text.</param>
/// <param name="FocusFallback">Where the keyboard goes after the popover closed when the control it was opened from is gone or hidden (the page's
/// selected post, tile or list); see <see cref="QuickTagPopoverOptions.FocusFallback"/>.</param>
internal sealed record RateAndTagRequest(bool FocusRating = true, string? TagsBlocked = null, Func<bool>? FocusFallback = null);

/// <summary>What a host passes besides the target: the suggestion source, the guard and the tag dialog hand-over.</summary>
internal sealed record QuickTagPopoverOptions
{
    /// <summary>Limits of the surface (undo depth, strip size); null uses the defaults.</summary>
    internal QuickTagOptions? Quick { get; init; }
    /// <summary>The suggestion strip; a <see cref="GalleryTagPanelSuggestions"/> is warmed, repainted when ready and disposed with the popover.</summary>
    internal IQuickTagSuggestionSource? Suggestions { get; init; }
    /// <summary>The session-wide recently used tags; every assignment the popover accepts counts as a use (a removal or an undo does not).</summary>
    internal TagRecentList? Recent { get; init; }
    /// <summary>The profile's stamp set (PT5), the same one the gallery panel shows; null gives the popover an empty stamp.</summary>
    internal QuickTagStamp? Stamp { get; init; }

    /// <summary>A session over <paramref name="target"/> with these limits (undo depth) and this stamp.</summary>
    internal QuickTagSession NewSession(IQuickTagTarget target) =>
        new(target, (Quick ?? QuickTagOptions.Default).Normalized().UndoDepth, Stamp);
    /// <summary>The Danbooru catalog reader for the source tag group of the field.</summary>
    internal Func<BooruCatalogSnapshot?>? ReadSourceTags { get; init; }
    /// <summary>True while the target is still what is on screen; false closes the popover (<see cref="QuickTagPopoverCloseReason.Stale"/>).</summary>
    internal Func<bool>? IsCurrent { get; init; }
    /// <summary>False hides "Advanced…" (a host without a full dialog for the target).</summary>
    internal bool AdvancedAvailable { get; init; } = true;
    /// <summary>False when the host restores the keyboard focus itself (the media page rebuilds its tiles after a change).</summary>
    internal bool RestoreFocus { get; init; } = true;
    /// <summary>
    /// The page's own place for the keyboard when the control the popover was opened from is gone, hidden or was inside another popup (a menu entry):
    /// the selected post card, the selected tile, the list. True when it took the focus. Tried before the anchor, which can be a toolbar button that
    /// keeps neither ↑/↓ nor R for the medium (the focus must never stay null or on a hidden control after the popover closed).
    /// </summary>
    internal Func<bool>? FocusFallback { get; init; }
    /// <summary>Base name of the controls (<c>QuickTagPopoverInput</c>, …).</summary>
    internal string Name { get; init; } = "QuickTagPopover";
    /// <summary>
    /// The execution context of the page that opened the popover ("media", "reader", "gallery"), for the key names in the row's tooltips (stars, remove button): the
    /// popover is a flyout outside every page's key scope, so without it a tip would also name a rating key that gave way to a saved binding on that page. Null names all.
    /// </summary>
    internal string? TooltipKeyContext { get; init; }
    /// <summary>
    /// The favourite and rating row on top of the popover ("Rate and tag…", review 15.11.): the same <see cref="GalleryDecisionRow"/> as the
    /// gallery's Tags segment, bound to what the host reads and writes for the popover's medium or selection. Null: no row (a host that cannot
    /// rate).
    /// </summary>
    internal QuickTagPopoverDecision? Decision { get; init; }

    /// <summary>A host without the profile settings (tests, a page not bound by the shell): the built-in limits and the panel's ranking over <paramref name="context"/>.</summary>
    internal static QuickTagPopoverOptions Defaults(TagContext context) => new()
    {
        Suggestions = new GalleryTagPanelSuggestions { Context = context ?? TagContext.Empty }
    };
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
    /// <summary>
    /// Default width of the popover in dip (plan §3.2 had 360; widened to 440 on 16.11.2026 - "the tagging dialog can be wider"): with the 12 dip frame padding
    /// the content is 416 dip, which gives the suggestion strip about four chips per line and shows a file name of 24 characters in the title. A layout
    /// constant, not a setting; <see cref="WidthFor"/> narrows it where the window is narrower.
    /// </summary>
    internal const double Width = 440;
    /// <summary>The narrowest the popover gets in a very narrow window, in dip: the stars (5 x 24) and the remove button still fit with room to spare.</summary>
    internal const double MinimumWidth = 280;
    /// <summary>Air kept between the popover and each edge of the window it opens in, in dip.</summary>
    internal const double HostMargin = 16;
    /// <summary>
    /// The width the popover takes in a window of <paramref name="hostWidth"/> dip: <see cref="Width"/>, never wider than the window minus
    /// <see cref="HostMargin"/> on both sides and never narrower than <see cref="MinimumWidth"/>. An unknown width (0, not a number) keeps <see cref="Width"/>.
    /// </summary>
    internal static double WidthFor(double hostWidth) =>
        double.IsNaN(hostWidth) || double.IsInfinity(hostWidth) || hostWidth <= 0 ? Width : Math.Min(Width, Math.Max(MinimumWidth, hostWidth - 2 * HostMargin));
    /// <summary>Technical bound of the height; the body of the surface scrolls beyond it. A layout measure, not a user limit.</summary>
    internal const double MaximumHeight = 520;
    private static readonly TimeSpan GuardInterval = TimeSpan.FromMilliseconds(250);

    private readonly Flyout _flyout = new() { Placement = PlacementMode.BottomEdgeAlignedLeft, ShowMode = FlyoutShowMode.Standard };
    private readonly Control _anchor;
    private readonly QuickTagPopoverOptions _options;
    private readonly TaskCompletionSource<QuickTagPopoverCloseReason> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Border _frame = new() { Padding = new Thickness(12), Width = Width, MaxHeight = MaximumHeight };
    private readonly Action? _unsubscribe;
    private readonly QuickTagSession? _session;
    private readonly GalleryDecisionRow? _decisionRow;
    private DispatcherTimer? _guard;
    private bool _decisionRunning;
    private Control? _origin;
    private TopLevel? _keyGuardRoot;
    private bool _shown;
    private bool _closed;

    private QuickTagPopover(Control anchor, QuickTagPopoverOptions options, QuickTagSurface? surface, QuickTagSession? session,
        Control content, Action? unsubscribe, GalleryDecisionRow? decisionRow = null)
    {
        _anchor = anchor;
        _decisionRow = decisionRow;
        _options = options;
        Surface = surface;
        _session = session;
        _unsubscribe = unsubscribe;
        _frame.Child = content;
        _frame.Name = options.Name + "Frame";
        // Tab cycles inside the popover; Shift+Tab from the field reaches the × directly (it is the last tab stop of the surface).
        KeyboardNavigation.SetTabNavigation(_frame, KeyboardNavigationMode.Cycle);
        _frame.AddHandler(InputElement.KeyDownEvent, OnFrameKey, RoutingStrategies.Tunnel);
        // A key nothing inside the popover took ends at its frame: it never travels on to the page behind it (↑/↓ moving the posts, M / U favouring the
        // medium under the popover). Tab stays with the keyboard navigation of the popover's root (the cycle), a lone modifier with the access keys.
        _frame.AddHandler(InputElement.KeyDownEvent, OnFrameKeyUnhandled, RoutingStrategies.Bubble);
        _flyout.FlyoutPresenterClasses.Add("quick-tag-popover");
        _flyout.Content = _frame;
        _flyout.Closed += OnFlyoutClosed;
        // The window may be narrower than the default width (a small window, the narrow reader or gallery): fitted when the flyout opens.
        _flyout.Opening += OnFlyoutOpening;
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
    /// <summary>The favourite and rating row on top; null when the host offered no decision.</summary>
    internal GalleryDecisionRow? DecisionRow => _decisionRow;
    internal bool IsOpen => _shown && !_closed;
    internal bool IsClosed => _closed;
    /// <summary>How it closed; null while open.</summary>
    internal QuickTagPopoverCloseReason? Reason { get; private set; }
    /// <summary>Completes with the close reason (also for a popover replaced by another one).</summary>
    internal Task<QuickTagPopoverCloseReason> Result => _result.Task;
    /// <summary>Raised once, on the UI thread, after the popover closed and its session and surface were disposed.</summary>
    internal event Action<QuickTagPopoverCloseReason>? Closed;

    /// <summary>
    /// Runs <paramref name="release"/> once, synchronously as the popover closes (at once when it already closed). A host's "popover open" or busy flag
    /// ends here, not in the continuation after <see cref="Result"/>: that continuation runs later than the next key, so R pressed right after Esc or ×
    /// found the flag still set and did nothing (audit 16.11.2026).
    /// </summary>
    internal void WhenClosed(Action release)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (Reason != null) { release(); return; }
        Closed += _ => release();
    }

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
            if (source is GalleryTagPanelSuggestions ranking)
            {
                // Ready comes from a worker: the strip is asked again on the UI thread, never after the popover closed.
                bool closed = false;
                Action ready = () => Dispatcher.UIThread.Post(() => { if (!closed) surface.Refresh(); }, DispatcherPriority.Background);
                ranking.Ready += ready;
                unsubscribe = () => { closed = true; ranking.Ready -= ready; ranking.Dispose(); };
                if (session.Snapshot is { } snapshot) ranking.Warm(snapshot);
            }
        }
        if (options.Recent is { } recent)
            session.Applied += change => { if (change.Assigned) recent.Touch(change.Tag); };
        surface.AdvancedButton.IsVisible = options.AdvancedAvailable;
        GalleryDecisionRow? decisionRow = null;
        if (options.Decision is { } decision)
        {
            decisionRow = NewDecisionRow(options, decision);
            decisionRow.FieldFocused = () => surface.Input.IsKeyboardFocusWithin;
            decisionRow.ReturnFocus = () => surface.FocusField();
            surface.DecisionContent = decisionRow;
            // R puts the focus on the stars instead (OnOpened); the field then does not take it when the surface attaches.
            if (decision.FocusRating) surface.FocusOnAttach = false;
        }
        QuickTagPopover popover = new(anchor, options, surface, session, surface, unsubscribe, decisionRow);
        popover.WireDecision(decisionRow);
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
        StackPanel content = new() { Spacing = 12, Children = { header } };
        // A medium that cannot be tagged can still be rated: the row stays on top, the reason below it.
        GalleryDecisionRow? decisionRow = options.Decision is { } decision ? NewDecisionRow(options, decision) : null;
        if (decisionRow != null) content.Children.Add(decisionRow);
        content.Children.Add(text);
        AutomationProperties.SetName(content, Texts.T("tags.quick.name", string.IsNullOrWhiteSpace(label) ? Texts.T("tags.quick.heading") : label.Trim()));
        AutomationProperties.SetHelpText(content, reason);
        QuickTagPopover popover = new(anchor, options, null, null, content, null, decisionRow);
        popover.WireDecision(decisionRow);
        close.Click += (_, _) => popover.Close(QuickTagPopoverCloseReason.Closed);
        popover.BlockedClose = close;
        popover.BlockedReason = reason;
        return popover;
    }

    private static GalleryDecisionRow NewDecisionRow(QuickTagPopoverOptions options, QuickTagPopoverDecision decision)
    {
        GalleryDecisionRow row = new(options.Name + "Decision") { FavoriteVisible = decision.SetFavorite != null, KeyContext = options.TooltipKeyContext };
        row.Show(SafeView(decision));
        // The caption (the rating value) is said when it changes; the gallery panel announces through its status line instead.
        AutomationProperties.SetLiveSetting(row.Summary, AutomationLiveSetting.Polite);
        return row;
    }

    private static GalleryDecisionView SafeView(QuickTagPopoverDecision decision)
    {
        try { return decision.View(); }
        catch (Exception error) when (error is InvalidOperationException or IOException or InvalidDataException or ObjectDisposedException)
        { return GalleryDecisionView.Unavailable; }
    }

    // A request runs one at a time (a second click while a write is under way is ignored: the row shows the real state again); the host reports
    // a failure itself. Afterwards the row shows what the stores carry, unless the popover is gone.
    private void WireDecision(GalleryDecisionRow? row)
    {
        if (row == null || _options.Decision is not { } decision) return;
        row.RatingRequested += value => _ = RunDecisionAsync(row, decision, () => decision.SetRating(value));
        if (decision.SetFavorite is { } setFavorite) row.FavoriteRequested += selected => _ = RunDecisionAsync(row, decision, () => setFavorite(selected));
    }

    private async Task RunDecisionAsync(GalleryDecisionRow row, QuickTagPopoverDecision decision, Func<Task> write)
    {
        if (_closed) return;
        if (_decisionRunning) { row.Show(SafeView(decision)); return; }
        _decisionRunning = true;
        try { await write(); }
        catch (Exception error) when (error is not OutOfMemoryException) { /* the host reports it; the row shows the real state below */ }
        finally
        {
            _decisionRunning = false;
            if (!_closed) row.Show(SafeView(decision));
        }
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
        _origin = OriginOf(TopLevel.GetTopLevel(_anchor), TopLevel.GetTopLevel(_anchor)?.FocusManager?.GetFocusedElement() as Control);
        _flyout.Opened += OnOpened;
        _flyout.ShowAt(_anchor);
        return this;
    }

    /// <summary>
    /// The control the focus returns to: the focused control when the popover opened, unless it sits in another popup (the menu entry "Rate and tag…" has
    /// the focus while its menu closes): that one is gone a moment later, and the fallback chain of <see cref="RestoreFocusNow"/> decides instead.
    /// </summary>
    internal static Control? OriginOf(TopLevel? anchorRoot, Control? focused) =>
        focused == null || anchorRoot == null || focused is TopLevel || !ReferenceEquals(TopLevel.GetTopLevel(focused), anchorRoot) ? null : focused;

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
        AttachKeyGuard();
        if (_options.Decision is { FocusRating: true } && _decisionRow != null)
        {
            // R: the stars take the focus (posted: the flyout has laid its content out by then); the field or × when the row cannot take it.
            GalleryDecisionRow row = _decisionRow;
            Dispatcher.UIThread.Post(() =>
            {
                if (_closed || row.FocusRating()) return;
                if (Surface != null) Surface.FocusField(); else BlockedClose?.Focus(NavigationMethod.Tab);
            }, DispatcherPriority.Input);
        }
        else BlockedClose?.Focus(NavigationMethod.Tab);
        if (_options.IsCurrent != null)
        {
            _guard = new DispatcherTimer(GuardInterval, DispatcherPriority.Background, (_, _) => CheckCurrent());
            _guard.Start();
        }
        CheckCurrent();
    }

    private void OnFlyoutOpening(object? sender, EventArgs e) => _frame.Width = WidthFor(TopLevel.GetTopLevel(_anchor)?.ClientSize.Width ?? 0);

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
        _frame.RemoveHandler(InputElement.KeyDownEvent, OnFrameKeyUnhandled);
        DetachKeyGuard();
        if (_session != null) _session.Updated -= OnSessionUpdated;
        if (ReferenceEquals(Current, this)) Current = null;
        _unsubscribe?.Invoke();
        Surface?.Dispose();
        _session?.Dispose();
        try { _options.Decision?.Release?.Invoke(); }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException) { }
        if (_shown && _options.RestoreFocus && RestoresFocus(reason)) RestoreFocus();
        Closed?.Invoke(reason);
        _result.TrySetResult(reason);
    }

    /// <summary>
    /// Whether a close gives the keyboard back: Esc, ×, a click outside, a lost target and a detached anchor do; "Advanced…" (the dialog takes the
    /// focus) and a replacement (the new popover has it) do not. A stale or detached close only repairs a lost focus (<see cref="FocusIsLost"/>): it never
    /// takes the focus from where the person or the page put it.
    /// </summary>
    internal static bool RestoresFocus(QuickTagPopoverCloseReason reason) =>
        reason is not (QuickTagPopoverCloseReason.Advanced or QuickTagPopoverCloseReason.Replaced);

    // The focus goes back to where it was (the post card, the tile, the row, the gallery picture), at once and again once the flyout has handed the
    // focus back itself (Loaded runs before the next key: ↑/↓ and R pressed right after Esc reach the page, audit 16.11.2026). Before, the focus was
    // put back only at Background priority, after the next key, and only for Esc, × and a click outside; Avalonia's own hand-back put it on the anchor
    // (a toolbar button, the list) or nowhere, so ↑/↓ no longer moved the posts and R found no medium.
    private void RestoreFocus()
    {
        RestoreFocusNow();
        Dispatcher.UIThread.Post(() => RestoreFocusNow(), DispatcherPriority.Loaded);
    }

    /// <summary>Puts the focus back when it was lost by the close; true when it is on a usable control afterwards (also when it already was).</summary>
    internal bool RestoreFocusNow()
    {
        if (Current != null && !ReferenceEquals(Current, this)) return false; // another popover opened: it owns the keyboard
        TopLevel? root = TopLevel.GetTopLevel(_anchor) ?? (_origin == null ? null : TopLevel.GetTopLevel(_origin));
        if (root == null) return false;
        Control? focused = root.FocusManager?.GetFocusedElement() as Control;
        if (!FocusIsLost(focused, _anchor, _frame, Usable)) return true;
        if (Usable(_origin) && !IsInside(_origin!, _frame) && _origin!.Focus()) return true;
        try { if (_options.FocusFallback?.Invoke() == true) return true; }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException) { }
        return NearestFocusable(_anchor, Usable) is { } owner && owner.Focus();
    }

    private static bool Usable(Control? control) =>
        control != null && control.IsAttachedToVisualTree() && control.IsEffectivelyVisible && control.IsEffectivelyEnabled;

    private static bool IsInside(Control control, Control container) =>
        control.GetSelfAndVisualAncestors().Any(visual => ReferenceEquals(visual, container));

    /// <summary>
    /// The focus after a close is lost when it is nowhere, on a control that is gone, hidden or disabled, still inside the closed popover, on the window
    /// itself, or where the flyout's own hand-back put it: the anchor or one of the anchor's ancestors (the posts list, the toolbar). Anywhere else it was
    /// put there on purpose (a click on another post, the page moving it to its rating filter) and stays.
    /// </summary>
    internal static bool FocusIsLost(Control? focused, Control anchor, Control frame, Func<Control?, bool> usable)
    {
        if (focused == null || focused is TopLevel || !usable(focused)) return true;
        if (IsInside(focused, frame)) return true;
        return anchor.GetSelfAndVisualAncestors().Any(visual => ReferenceEquals(visual, focused));
    }

    /// <summary>The anchor, or its nearest ancestor that can take the keyboard (a thumbnail inside a post card gives way to the card's row).</summary>
    internal static Control? NearestFocusable(Control anchor, Func<Control?, bool> usable)
    {
        foreach (Control control in anchor.GetSelfAndVisualAncestors().OfType<Control>())
        {
            if (control is TopLevel) return null;
            if (control.Focusable && usable(control)) return control;
        }
        return null;
    }

    // While the popover is open, a key raised in the window under it (the focus was lost inside the popover, or never reached it) belongs to the
    // popover, never to the page: Esc closes it, Tab brings the focus into it, every other key is consumed (no ↑/↓ moving the posts behind it).
    private void AttachKeyGuard()
    {
        TopLevel? root = TopLevel.GetTopLevel(_anchor);
        if (root == null) return;
        _keyGuardRoot = root;
        root.AddHandler(InputElement.KeyDownEvent, OnGuardedRootKey, RoutingStrategies.Tunnel);
    }

    private void DetachKeyGuard()
    {
        _keyGuardRoot?.RemoveHandler(InputElement.KeyDownEvent, OnGuardedRootKey);
        _keyGuardRoot = null;
    }

    private void OnGuardedRootKey(object? sender, KeyEventArgs e)
    {
        if (_closed || !_shown || _keyGuardRoot == null) return;
        TopLevel root = _keyGuardRoot;
        if (GuardedRootKey(e, visual => ReferenceEquals(TopLevel.GetTopLevel(visual), root), _frame) is not { } action) return;
        e.Handled = true;
        if (action == GuardedKeyAction.Close) Close(QuickTagPopoverCloseReason.Closed);
        else if (action == GuardedKeyAction.FocusInside) FocusInside();
    }

    /// <summary>What the window's tunnel does with a key while the popover is open.</summary>
    internal enum GuardedKeyAction { Consume, Close, FocusInside }

    /// <summary>
    /// The key guard as a pure decision: null leaves the key alone (it was already handled, it is the popover's own key on a route that reaches the
    /// window, it comes from another popup or window, or it is a lone modifier); otherwise Esc closes, Tab moves the focus into the popover and every
    /// other key is consumed.
    /// </summary>
    /// <param name="fromGuardedWindow">True for a source in the window the guard sits on (its own top level).</param>
    internal static GuardedKeyAction? GuardedRootKey(KeyEventArgs e, Func<Visual, bool> fromGuardedWindow, Control frame)
    {
        if (e.Handled || IsModifierKey(e.Key)) return null;
        if (e.Source is Visual source)
        {
            if (source.GetSelfAndVisualAncestors().Any(visual => ReferenceEquals(visual, frame))) return null;
            if (!fromGuardedWindow(source)) return null;
        }
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None) return GuardedKeyAction.Close;
        if (e.Key == Key.Tab) return GuardedKeyAction.FocusInside;
        return GuardedKeyAction.Consume;
    }

    internal static bool IsModifierKey(Key key) => key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    // The keyboard goes back into the popover: the stars for R, else the field, else the ×.
    private void FocusInside()
    {
        if (_closed) return;
        if (_options.Decision is { FocusRating: true } && _decisionRow?.FocusRating() == true) return;
        if (Surface != null) Surface.FocusField();
        else BlockedClose?.Focus(NavigationMethod.Tab);
    }

    // The last stop of a key inside the popover: a plain Esc that nothing inside took (a control outside the surface's own routing) still closes it, so
    // Esc closes from every element; any other key nothing took is consumed here.
    private void OnFrameKeyUnhandled(object? sender, KeyEventArgs e)
    {
        if (!ConsumesUnhandledKey(e)) return;
        e.Handled = true;
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None) Close(QuickTagPopoverCloseReason.Closed);
    }

    /// <summary>True for a key that bubbled out of the popover's content unhandled and must stop at its frame (everything but Tab and a lone modifier).</summary>
    internal static bool ConsumesUnhandledKey(KeyEventArgs e) => !e.Handled && e.Key != Key.Tab && !IsModifierKey(e.Key);

    // ---- keys ---------------------------------------------------------------------------------------------------------

    private void OnFrameKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        // Left, Right, Home and End rove along a focused star group (never read as moving the caret or the list).
        if (_decisionRow is { IsRatingFocused: true } stars && stars.RouteStarKey(e)) { e.Handled = true; return; }
        if (!ShortcutService.IsCapturing && TryRouteDecisionKey(e, ShortcutService.Current)) { e.Handled = true; return; }
        if (Surface == null)
        {
            // A blocked popover has no list to close first: Esc closes it.
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None) { e.Handled = true; Close(QuickTagPopoverCloseReason.Closed); }
            return;
        }
        if (!ShortcutService.IsCapturing && TryRouteCommand(Surface, e, ShortcutService.Current)) e.Handled = true;
    }

    /// <summary>
    /// The keys the row's tooltips name: Shift+1 to Shift+5 rate, Shift+0 removes the rating (as in the gallery's tag panel: not while the field holds
    /// text or its list is open, where a typed "!" or ")" stays typing) and, with the keyboard on the row itself, M adds, U removes and F toggles the
    /// favourite. Without these the popover showed "Rating: 3 of 5 · Shift+3" and ignored the key. True when the row took the key.
    /// </summary>
    internal bool TryRouteDecisionKey(KeyEventArgs e, EffectiveShortcutBindings bindings)
    {
        if (_decisionRow is not { } row || _options.Decision == null) return false;
        if (Surface is { } surface && surface.Input.IsKeyboardFocusWithin && (!string.IsNullOrEmpty(surface.Input.Text) || surface.Input.IsDropDownOpen)) return false;
        for (int value = 1; value <= 5; value++)
            if (bindings.Matches(e, "media.rating.set-" + value)) return row.RequestRating(value);
        if (bindings.Matches(e, "media.rating.clear")) return row.RequestRating(null);
        if (!row.IsKeyboardFocusWithin) return false; // M, U and F are letters: they only mean "favourite" while the keyboard is on the row, never in the field
        FavoriteKeyAction action = FavoriteKeys.Classify(id => bindings.Matches(e, id));
        return action != FavoriteKeyAction.None && row.RequestFavorite(action);
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
        if (bindings.Matches(e, QuickTagCommands.StampApply)) return surface.Execute(new QuickTagKeyRoute(QuickTagKeyAction.ApplyStamp));
        if (bindings.Matches(e, QuickTagCommands.Undo))
        {
            if (surface.FocusZone() == QuickTagFocus.Field && !string.IsNullOrEmpty(surface.Input.Text)) return false;
            return surface.Execute(new QuickTagKeyRoute(QuickTagKeyAction.Undo));
        }
        return false;
    }
}

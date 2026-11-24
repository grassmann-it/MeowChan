using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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
    /// <summary>Explicit rating-only focus opt-in; general R / "Rate and tag" entry uses TagInput so the tag field takes focus.</summary>
    internal QuickTagInitialFocus InitialFocus { get; init; } = QuickTagInitialFocus.TagInput;
    /// <summary>True once a request wrote something (read by the host after the popover closed: the page then refreshes its tiles and captions).</summary>
    internal Func<bool>? HasChanged { get; init; }
    /// <summary>Called once when the popover closed (a decision session the host created for it is disposed here).</summary>
    internal Action? Release { get; init; }
}

/// <summary>Initial keyboard destination, independent of whether a decision row is present.</summary>
internal enum QuickTagInitialFocus { TagInput, Rating }

/// <summary>How a page asked the shell for the "Rate and tag" popover of one medium.</summary>
/// <param name="InitialFocus">Explicit star-focus opt-in. R, menus, rating captions and tagging entry default to the tag field. A blocked target without a field focuses the stars regardless.</param>
/// <param name="TagsBlocked">The localized reason the medium cannot be tagged (a Danbooru medium, a file shared by several posts), or null; the popover then
/// still offers the stars and says the reason as text.</param>
/// <param name="FocusFallback">Where the keyboard goes after the popover closed when the control it was opened from is gone or hidden (the page's
/// selected post, tile or list); see <see cref="QuickTagPopoverOptions.FocusFallback"/>.</param>
/// <param name="SidebarToken">The lifetime of the displayed sidebar target, from resolution through tag writes. Tile and menu requests use the default.</param>
internal sealed record RateAndTagRequest(QuickTagInitialFocus InitialFocus = QuickTagInitialFocus.TagInput, string? TagsBlocked = null,
    Func<bool>? FocusFallback = null, CancellationToken SidebarToken = default);

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
    /// <summary>The narrowest the popover gets in dip, with room for five 40-dip stars and the remove button.</summary>
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

    private readonly Flyout _flyout = new QuickTagFlyout() { Placement = PlacementMode.BottomEdgeAlignedLeft, ShowMode = FlyoutShowMode.Standard };
    private KeyEventArgs? _compositionKey;
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
    internal static QuickTagPopover Create(Control anchor, QuickTagSession session, string label, QuickTagPopoverOptions? options = null,
        Action? creationProbe = null)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(session);
        options ??= new QuickTagPopoverOptions();
        QuickTagSurface? surface = null;
        Action? unsubscribe = null;
        QuickTagPopover? popover = null;
        try
        {
            QuickTagSurface createdSurface = new(session, options.Quick, options.Name)
            {
                ShortcutContext = options.TooltipKeyContext ?? "gallery",
                TargetLabel = label ?? string.Empty,
                ReadSourceTags = options.ReadSourceTags,
                NavigationEnabled = false
            };
            surface = createdSurface;
            creationProbe?.Invoke();
            if (options.Suggestions is { } source)
            {
                createdSurface.Suggestions = source;
                if (source is GalleryTagPanelSuggestions ranking)
                {
                    // Ready comes from a worker: the strip is asked again on the UI thread, never after the popover closed.
                    bool closed = false;
                    Action ready = () => Dispatcher.UIThread.Post(() => { if (!closed) createdSurface.Refresh(); }, DispatcherPriority.Background);
                    ranking.Ready += ready;
                    unsubscribe = () => { closed = true; ranking.Ready -= ready; ranking.Dispose(); };
                    if (session.Snapshot is { } snapshot) ranking.Warm(snapshot);
                }
            }
            if (options.Recent is { } recent)
                session.Applied += change => { if (change.Assigned) recent.Touch(change.Tag); };
            createdSurface.AdvancedButton.IsVisible = options.AdvancedAvailable;
            GalleryDecisionRow? decisionRow = null;
            if (options.Decision is { } decision)
            {
                decisionRow = NewDecisionRow(options, decision);
                decisionRow.FieldFocused = () => createdSurface.Input.IsKeyboardFocusWithin;
                decisionRow.ReturnFocus = () => createdSurface.FocusField();
                createdSurface.DecisionContent = decisionRow;
                // An explicit star-focus request suppresses the surface's automatic field focus; general rate-and-tag entry keeps it.
                if (decision.InitialFocus == QuickTagInitialFocus.Rating) createdSurface.FocusOnAttach = false;
            }
            popover = new(anchor, options, createdSurface, session, createdSurface, unsubscribe, decisionRow);
            popover.WireDecision(decisionRow);
            createdSurface.CloseRequested += () => popover.Close(QuickTagPopoverCloseReason.Closed);
            createdSurface.AdvancedRequested += () => popover.Close(QuickTagPopoverCloseReason.Advanced);
            return popover;
        }
        catch
        {
            if (popover != null) popover.Close();
            else
            {
                unsubscribe?.Invoke();
                surface?.Dispose();
                session.Dispose();
            }
            throw;
        }
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
        TextBlock heading = new() { Text = title, Classes = { "workspace-panel-title" }, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center, Name = options.Name + "Title" };
        ToolTip.SetTip(heading, title);
        PanelCloseButton close = new(options.Name + "Close") { VerticalAlignment = VerticalAlignment.Center };
        close.Localize(Texts.T("tags.quick.close"));
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
        CheckCurrent();
        if (_closed) return;
        if (_decisionRunning) { row.Show(SafeView(decision)); return; }
        _decisionRunning = true;
        row.SetError(null);
        try { await write(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!_closed) row.SetError(Texts.T("gallery.decision.status.failed"));
        }
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
    internal QuickTagPopover Show() => ShowAt((flyout, anchor) => flyout.ShowAt(anchor));

    internal QuickTagPopover ShowAt(Action<Flyout, Control> showAt)
    {
        ArgumentNullException.ThrowIfNull(showAt);
        if (_closed || _shown) return this;
        CheckCurrent();
        if (_closed) return this;
        Current?.Close(QuickTagPopoverCloseReason.Replaced);
        _shown = true;
        Current = this;
        LatchOpeningKeys();
        _origin = OriginOf(TopLevel.GetTopLevel(_anchor), TopLevel.GetTopLevel(_anchor)?.FocusManager?.GetFocusedElement() as Control);
        _flyout.Opened += OnOpened;
        try { showAt(_flyout, _anchor); }
        catch
        {
            try { _flyout.Hide(); } catch { }
            if (!_closed)
            {
                _shown = false;
                _closed = true;
                Finish(QuickTagPopoverCloseReason.Dismissed);
            }
            throw;
        }
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
        // Run after the flyout has attached and laid out its field. Reuse the same focus contract when Tab repairs a lost focus.
        Dispatcher.UIThread.Post(FocusInside, DispatcherPriority.Input);
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
        if (ReferenceEquals(s_latchOwner, this)) { s_latchOwner = null; s_swallowText = false; }
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
        CheckCurrent();
        if (_closed || !_shown || _keyGuardRoot == null) return;
        TopLevel root = _keyGuardRoot;
        if (GuardedRootKey(e, visual => ReferenceEquals(TopLevel.GetTopLevel(visual), root), _frame, Surface != null) is not { } action) return;
        e.Handled = true;
        if (action == GuardedKeyAction.Close) Close(QuickTagPopoverCloseReason.Closed);
        else if (action == GuardedKeyAction.FocusInside) FocusInside();
        else if (action == GuardedKeyAction.TypeIntoField) Surface?.TypeIntoField(TextEntryKeys.TextOf(e)!);
    }

    /// <summary>What the window's tunnel does with a key while the popover is open.</summary>
    internal enum GuardedKeyAction { Consume, Close, FocusInside, TypeIntoField }

    /// <summary>
    /// The key guard as a pure decision: null leaves the key alone (it was already handled, it is the popover's own key on a route that reaches the
    /// window, it comes from another popup or window, it is a lone modifier, or it types into a text box of the window); otherwise Esc closes, Tab moves
    /// the focus into the popover, printable text goes into the popover's field (when it has one) and every other key is consumed.
    /// </summary>
    /// <param name="fromGuardedWindow">True for a source in the window the guard sits on (its own top level).</param>
    internal static GuardedKeyAction? GuardedRootKey(KeyEventArgs e, Func<Visual, bool> fromGuardedWindow, Control frame, bool hasField = false)
    {
        if (e.Handled || IsModifierKey(e.Key)) return null;
        if (e.Source is Visual source)
        {
            if (source.GetSelfAndVisualAncestors().Any(visual => ReferenceEquals(visual, frame))) return null;
            if (!fromGuardedWindow(source)) return null;
        }
        if (TextEntryKeys.TypesText(e))
        {
            // A text box keeps what is typed into it (consuming the KeyDown would swallow the character); other text goes into the popover's field.
            if (TextEntryKeys.IsTextEditor(e.Source)) return null;
            if (hasField && TextEntryKeys.TextOf(e) != null) return GuardedKeyAction.TypeIntoField;
        }
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None) return GuardedKeyAction.Close;
        if (e.Key == Key.Tab) return GuardedKeyAction.FocusInside;
        return GuardedKeyAction.Consume;
    }

    internal static bool IsModifierKey(Key key) => key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    // General entry and focus repair prefer the tag field. An explicit star request, or no field, uses the stars; otherwise ×.
    private void FocusInside()
    {
        if (_closed) return;
        if ((Surface == null || _options.Decision is { InitialFocus: QuickTagInitialFocus.Rating }) && _decisionRow?.FocusRating() == true) return;
        if (Surface != null) Surface.FocusField();
        else BlockedClose?.Focus(NavigationMethod.Tab);
    }

    // A plain Esc left by the content closes the popover. Composition keys and text typed into the field pass through.
    // Text typed on the stars or elsewhere goes into the field. Other unhandled keys stop here.
    private void OnFrameKeyUnhandled(object? sender, KeyEventArgs e)
    {
        // The editor can end preedit during the key route. Honor the tunnel's decision for this same key.
        if (ReferenceEquals(e, _compositionKey)) return;
        switch (UnhandledKey(e, Surface != null))
        {
            case UnhandledKeyAction.Pass:
                return;
            case UnhandledKeyAction.TypeIntoField:
                e.Handled = true;
                Surface!.TypeIntoField(TextEntryKeys.TextOf(e)!);
                return;
            case UnhandledKeyAction.Close:
                e.Handled = true;
                Close(QuickTagPopoverCloseReason.Closed);
                return;
            default:
                e.Handled = true;
                return;
        }
    }

    /// <summary>What the frame does with a key that bubbled out of the popover's content.</summary>
    internal enum UnhandledKeyAction { Pass, Consume, Close, TypeIntoField }

    /// <summary>
    /// The frame's decision for a key that bubbled out of the content: an already handled key, Tab (the cycle) and a lone modifier pass; a key that types
    /// text into the text box it came from passes UNHANDLED (marking it handled would swallow the character: the platform raises TextInput only for an
    /// unhandled KeyDown, Tom 17.11.2026); printable text from any other part (the stars after Tab, a chip, a button) goes into the field when there is one
    /// (TG-02); a plain Esc closes; everything else is consumed, so it never reaches the page behind the popover.
    /// </summary>
    internal static UnhandledKeyAction UnhandledKey(KeyEventArgs e, bool hasField)
    {
        if (e.Handled || e.Key == Key.Tab || IsModifierKey(e.Key)) return UnhandledKeyAction.Pass;
        if (TextEntryKeys.IsTextEditor(e.Source) && QuickTagKeys.FieldOwnsCharacter(e, EffectiveShortcutBindings.CurrentPlatform))
            return UnhandledKeyAction.Pass;
        if (TextEntryKeys.TypesText(e))
        {
            if (hasField && TextEntryKeys.TextOf(e) != null) return UnhandledKeyAction.TypeIntoField;
        }
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None) return UnhandledKeyAction.Close;
        return UnhandledKeyAction.Consume;
    }

    /// <summary>True for a key that bubbled out of the popover's content unhandled and must stop at its frame (everything but Tab, a lone modifier and text typed into a text box).</summary>
    internal static bool ConsumesUnhandledKey(KeyEventArgs e) => UnhandledKey(e, hasField: false) != UnhandledKeyAction.Pass;

    // ---- the key that opened the popover --------------------------------------------------------------------------------
    // R (or Ctrl+Shift+T, or Enter on the menu entry) is still down when the popover opens. A press held past the repeat delay sends further KeyDowns
    // without a KeyUp (X11 detectable autorepeat, Win32, macOS); with the stars focused they are printable text and would land in the field as "rrr"
    // (TG-02 types text from the stars into the field). The keys that were down when the popover opened are latched like HeldKeyLatch does for the
    // favourite keys: their repeats (and a TextInput a platform might still raise for one) are swallowed until the key's KeyUp; a new press after
    // the release types as usual. A key "down" without a KeyDown or repeat for RepeatWindow (its KeyUp was missed, the window lost the activation)
    // does not count as held.

    /// <summary>The longest gap between a key's KeyDown and its next repeat that still counts as held (OS repeat delays are 0.2 to about 1 s).</summary>
    internal static readonly TimeSpan RepeatWindow = TimeSpan.FromMilliseconds(1500);
    private static readonly Dictionary<Key, long> s_down = new();
    private static bool s_tracking;
    private static RoutedEventArgs? s_lastKey;
    private static QuickTagPopover? s_latchOwner;
    private static bool s_swallowText;
    private readonly HashSet<Key> _openingKeys = new();

    /// <summary>
    /// Starts following which keys are down, application-wide (idempotent). The shell calls it when its window is built, so the key that opens the
    /// first popover is already known; <see cref="Show"/> calls it too for other hosts.
    /// </summary>
    internal static void TrackHeldKeys()
    {
        if (s_tracking) return;
        s_tracking = true;
        // At the first element of every route (the window, or the root of a test tree): before any handler of the window can act on a repeat.
        InputElement.KeyDownEvent.AddClassHandler(typeof(Interactive), (_, e) => OnAnyKeyDown((KeyEventArgs)e), RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.KeyUpEvent.AddClassHandler(typeof(Interactive), (_, e) => OnAnyKeyUp((KeyEventArgs)e), RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.TextInputEvent.AddClassHandler(typeof(Interactive), (_, e) => OnAnyTextInput((TextInputEventArgs)e), RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private static void OnAnyKeyDown(KeyEventArgs e)
    {
        if (ReferenceEquals(s_lastKey, e)) return; // once per event, at the first element of its route
        s_lastKey = e;
        long now = Environment.TickCount64;
        bool held = s_down.TryGetValue(e.Key, out long last) && now - last <= (long)RepeatWindow.TotalMilliseconds;
        s_down[e.Key] = now;
        if (s_latchOwner is { } owner && owner.SwallowsOpeningKey(e.Key, held)) { e.Handled = true; s_swallowText = true; }
        else s_swallowText = false;
    }

    private static void OnAnyKeyUp(KeyEventArgs e)
    {
        if (ReferenceEquals(s_lastKey, e)) return;
        s_lastKey = e;
        s_down.Remove(e.Key);
        s_latchOwner?._openingKeys.Remove(e.Key);
        s_swallowText = false;
    }

    private static void OnAnyTextInput(TextInputEventArgs e)
    {
        if (!s_swallowText) return;
        s_swallowText = false;
        e.Handled = true;
    }

    /// <summary>Latches the keys that are down now (recent KeyDown or repeat, no KeyUp yet): their repeats stay out of the popover until they are released.</summary>
    internal void LatchOpeningKeys()
    {
        TrackHeldKeys();
        long now = Environment.TickCount64;
        _openingKeys.Clear();
        foreach ((Key key, long last) in s_down)
            if (now - last <= (long)RepeatWindow.TotalMilliseconds && !IsModifierKey(key)) _openingKeys.Add(key);
        s_latchOwner = this;
        s_swallowText = false;
    }

    /// <summary>The keys latched as held when the popover opened (tests).</summary>
    internal IReadOnlyCollection<Key> OpeningKeys => _openingKeys;

    // A latched key's KeyDown while it is still held is a repeat: swallowed. A KeyDown after a gap longer than the repeat window is a new press (the
    // release was missed): the latch lets go.
    private bool SwallowsOpeningKey(Key key, bool held)
    {
        if (_closed || !_openingKeys.Contains(key)) return false;
        if (held) return true;
        _openingKeys.Remove(key);
        return false;
    }

    // ---- keys ---------------------------------------------------------------------------------------------------------

    private void OnFrameKey(object? sender, KeyEventArgs e)
    {
        _compositionKey = null;
        if (e.Handled) return;
        if (Surface?.FieldHasComposition == true)
        {
            _compositionKey = e;
            return;
        }
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

    internal static bool IsCompositionKey(KeyEventArgs e) => ReferenceEquals(Current?._compositionKey, e);

    private sealed class QuickTagFlyout : Flyout
    {
        protected override Control CreatePresenter() => new QuickTagFlyoutPresenter
        {
            [!ContentControl.ContentProperty] = this[!ContentProperty],
            [!ContentControl.ContentTemplateProperty] = this[!ContentTemplateProperty]
        };
    }

    private sealed class QuickTagFlyoutPresenter : FlyoutPresenter
    {
        protected override Type StyleKeyOverride => typeof(FlyoutPresenter);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // Avalonia's presenter would close on an Escape left unhandled for the input method.
            if (e.Key == Key.Escape && IsCompositionKey(e)) return;
            base.OnKeyDown(e);
        }
    }

    /// <summary>
    /// Rating shortcuts act outside the tag field. The favourite shortcuts require focus on the decision row.
    /// </summary>
    internal bool TryRouteDecisionKey(KeyEventArgs e, EffectiveShortcutBindings bindings)
    {
        if (_decisionRow is not { } row || _options.Decision == null) return false;
        if (Surface?.Input.IsKeyboardFocusWithin == true || TextEntryKeys.IsTextEditor(e.Source)) return false;
        for (int value = 1; value <= 5; value++)
            if (bindings.Matches(e, "media.rating.set-" + value)) return row.RequestRating(value);
        if (bindings.Matches(e, "media.rating.clear")) return row.RequestRating(null);
        if (!row.IsKeyboardFocusWithin) return false; // M, U and F are letters: they only mean "favourite" while the keyboard is on the row, never in the field
        FavoriteKeyAction action = FavoriteKeys.Classify(id => bindings.Matches(e, id));
        return action != FavoriteKeyAction.None && row.RequestFavorite(action);
    }

    /// <summary>
    /// Route the same commands and respect the same text input as the surface, including from controls in the frame around it.
    /// </summary>
    internal static bool TryRouteCommand(QuickTagSurface surface, KeyEventArgs e, EffectiveShortcutBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(bindings);
        if (surface.FieldHasComposition) return false;
        QuickTagFocus focus = TextEntryKeys.IsTextEditor(e.Source) ? QuickTagFocus.Field : surface.FocusZone();
        return surface.Execute(QuickTagKeys.RouteCommand(e, focus, surface.Input.IsDropDownOpen,
            string.IsNullOrEmpty(surface.Input.Text), navigation: false, bindings));
    }
}

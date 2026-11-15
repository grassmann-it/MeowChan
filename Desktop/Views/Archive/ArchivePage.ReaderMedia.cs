using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// Reader media size (R6): larger images in posts without text, the focused card's media enlarged, the hover preview. The three
// switches are quick toggles in the View menu (and settings); the numbers are settings. Everything but the textless width acts live.
public partial class ArchivePage
{
    private ReaderHoverEnlarge? _hoverEnlarge;
    private bool _focusedMediaPresentedElsewhere;

    /// <summary>Persists the quick switches of the View menu (only the changed switches), through the profile's view-preference writer.</summary>
    internal Func<Action<Config>, Task>? SaveReaderMediaSwitches { get; set; }

    internal ReaderMediaPreferences ReaderMedia => _readerDisplayPreferences.Media;

    /// <summary>The hover preview of the reader (smoke seam).</summary>
    internal ReaderHoverEnlarge? HoverEnlarge => _hoverEnlarge;

    /// <summary>
    /// Set by a surface that presents the focused post's media itself (the media sidebar): the card then stays at its ordinary size
    /// for as long as it is true, so the media is never shown twice. See <see cref="ReaderFocusedMedia"/>.
    /// </summary>
    internal bool FocusedMediaPresentedElsewhere
    {
        get => _focusedMediaPresentedElsewhere;
        set
        {
            if (_focusedMediaPresentedElsewhere == value) return;
            _focusedMediaPresentedElsewhere = value;
            ApplyReaderMediaLive();
        }
    }

    /// <summary>Applies the media preferences: the focus and hover options at once, a changed textless setting by rebuilding the posts.</summary>
    internal void ApplyReaderMediaPreferences(ReaderMediaPreferences media) =>
        ApplyReaderDisplayPreferences(ReaderThumbnailWidth, ReaderDateStyle, ReaderPageSize, media: media);

    // The live part: no rows change, only inherited values on the list and the hover popup's settings.
    private void ApplyReaderMediaLive()
    {
        ReaderMediaPreferences media = _readerDisplayPreferences.Media;
        int scale = ReaderFocusedMedia.Choose(media, _focusedMediaPresentedElsewhere) == ReaderFocusedMedia.Presentation.Inline
            ? ReaderDisplayPreferences.FocusedScalePercent(media) : 100;
        ArchivePosts.SetValue(ReaderPostLayout.FocusScalePercentProperty, scale);
        _hoverEnlarge?.Configure(media);
        LayoutMediaSidebar();
        ApplyInlineVideoLive(media); // R10: inline video settings act live too // R8: the sidebar switch, side and width act live too
    }

    private void InitializeReaderMedia()
    {
        Panel? host = QuotePreviewPopup.Parent as Panel ?? ArchiveReaderPane;
        _hoverEnlarge = new ReaderHoverEnlarge(host);
        ArchivePosts.SetValue(ArchiveAttachmentView.HoverEnlargeProperty, _hoverEnlarge);
        InitializeInlineVideo();
        ApplyReaderMediaLive();
        // The focused card grows or shrinks with the selection; keep its top where it was so the list does not jump under the eyes.
        ArchivePosts.SelectionChanged += (_, _) => AnchorFocusedCard();
        // The enlarged media is capped by the list's visible height and kept whole in view (ReaderMediaFit).
        ArchivePosts.SizeChanged += (_, _) => UpdateReaderVisibleHeight();
        ArchivePosts.AddHandler(ScrollViewer.ScrollChangedEvent, (_, e) =>
        {
            if (e is ScrollChangedEventArgs { ViewportDelta.Y: not 0 }) UpdateReaderVisibleHeight();
        }, Avalonia.Interactivity.RoutingStrategies.Bubble);
        ArchivePosts.AddHandler(ArchiveAttachmentView.FocusedFrameChangedEvent, OnFocusedFrameChanged, Avalonia.Interactivity.RoutingStrategies.Bubble);
        UpdateReaderVisibleHeight();
        ArchivePosts.AddHandler(PointerWheelChangedEvent, (_, _) => _hoverEnlarge?.Close(), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        ArchivePosts.DetachedFromVisualTree += (_, _) => _hoverEnlarge?.Close();
        RegisterMediaSwitch("reader.view.textless-large", media => media.TextlessLarge,
            (media, on) => media with { TextlessLarge = on }, (config, on) => config.ReaderTextlessLargeImages = on);
        RegisterMediaSwitch("reader.view.focused-enlarge", media => media.FocusedEnlarge,
            (media, on) => media with { FocusedEnlarge = on }, (config, on) => config.ReaderFocusedMediaEnlarge = on);
        RegisterMediaSwitch("reader.view.hover-enlarge", media => media.HoverEnlarge,
            (media, on) => media with { HoverEnlarge = on }, (config, on) => config.ReaderHoverEnlarge = on);
        InitializeMediaSidebar();
    }

    private void RegisterMediaSwitch(string commandId, Func<ReaderMediaPreferences, bool> read,
        Func<ReaderMediaPreferences, bool, ReaderMediaPreferences> write, Action<Config, bool> store) =>
        RegisterViewOption(commandId, () => read(ReaderMedia), () => ToggleMediaSwitch(read, write, store));

    private void ToggleMediaSwitch(Func<ReaderMediaPreferences, bool> read,
        Func<ReaderMediaPreferences, bool, ReaderMediaPreferences> write, Action<Config, bool> store)
    {
        if (_ended) return;
        bool on = !read(ReaderMedia);
        ApplyReaderMediaPreferences(write(ReaderMedia, on));
        // Only the switch that was flipped is written: another setting changed meanwhile (a width in Settings) is not overwritten.
        if (SaveReaderMediaSwitches is { } save) _ = SaveMediaSwitchAsync(save(config => store(config, on)));
    }

    private async Task SaveMediaSwitchAsync(Task save)
    {
        try { await save; }
        catch (Exception error) { if (!_ended) Message(Texts.T("reader.view.saveFailed", error.Message), true); }
    }

    // The list's scroll viewport (rounded, so a fractional resize does not re-layout every enlarged picture); NaN while it has no size.
    private void UpdateReaderVisibleHeight()
    {
        ScrollViewer? scroll = ArchivePosts.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        double height = scroll is { Viewport.Height: > 0 } ? scroll.Viewport.Height : ArchivePosts.Bounds.Height;
        height = height > 0 ? Math.Round(height) : double.NaN;
        if (ArchivePosts.GetValue(ReaderPostLayout.VisibleHeightProperty).Equals(height)) return;
        ArchivePosts.SetValue(ReaderPostLayout.VisibleHeightProperty, height);
    }

    // After a selection the focused picture may still change its height (the decoded aspect replaces the reserved one); within this
    // window that re-fits the view once more. Later size changes never move the list under a reader who scrolled on, and neither does
    // one after the reader took over (a wheel turn, a press, a paging key) or while a jump still aligns the card (it measures again).
    private const int FocusFitWindowMilliseconds = 1500;
    private bool _focusFitScheduled;

    private bool MayRefitFocusedCard => _readerScrollOwner.MayRefit(Environment.TickCount64, _readerInteractionRevision);

    private void OnFocusedFrameChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!MayRefitFocusedCard || _focusFitScheduled || e.Source is not Visual source
            || ArchivePosts.SelectedItem is not { } item || ArchivePosts.ContainerFromItem(item) is not Visual container
            || !container.IsVisualAncestorOf(source)) return;
        _focusFitScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _focusFitScheduled = false;
            if (MayRefitFocusedCard) HoldFocusedCard();
        }, DispatcherPriority.Loaded);
    }

    // Where the focused card's top edge settled (in the list's scroll viewer), by the jump that aligned it or the anchor of a pointer
    // selection; NaN while unknown. A later size change of its frame holds the card there instead of scrolling it anew.
    private object? _focusedCardItem;
    private double _focusedCardTop = double.NaN;

    /// <summary>Remembers where the selected card's top edge is now (after a jump aligned it).</summary>
    private void RememberFocusedCardTop()
    {
        ListBox list = ArchivePosts;
        _focusedCardItem = null;
        _focusedCardTop = double.NaN;
        if (list.SelectedItem is not { } item || list.ContainerFromItem(item) is not Control container) return;
        list.UpdateLayout();
        if (list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } scroll
            || container.TranslatePoint(default, scroll) is not Point at) return;
        _focusedCardItem = item;
        _focusedCardTop = at.Y;
    }

    // A frame change of the focused card (the decoded aspect replacing the reserved one, a video row): the card keeps its top edge where
    // it settled, and only a medium that would then not be whole in view is brought in (one write, ReaderMediaFit.HoldDelta).
    private void HoldFocusedCard()
    {
        ListBox list = ArchivePosts;
        if (_ended || list.GetValue(ReaderPostLayout.FocusScalePercentProperty) <= 100 || list.SelectedItem is not { } item
            || list.ContainerFromItem(item) is not Control container) return;
        list.UpdateLayout();
        // A card a jump centred is centred again by its new size: the same target as the jump, in one write.
        if (ReferenceEquals(_alignedCardItem, item)) { RealignJumpedCard(item, container); return; }
        double shift = 0;
        if (ReferenceEquals(_focusedCardItem, item) && double.IsFinite(_focusedCardTop)
            && list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroll
            && container.TranslatePoint(default, scroll) is Point at)
            shift = at.Y - _focusedCardTop;
        FitFocusedCard(item, container, shift);
    }

    // The item of the card the latest reader jump aligned while the focused media is enlarged (null otherwise, and after any other selection).
    private object? _alignedCardItem;

    /// <summary>True while the focused card's media is drawn enlarged: a reader jump then centres the card (ReaderMediaFit.JumpOffset).</summary>
    private bool FocusedMediaEnlarged => ArchivePosts.GetValue(ReaderPostLayout.FocusScalePercentProperty) > 100;

    // The jump's target again for the card's current size (a late frame change: the decoded aspect replacing the reserved one). The
    // virtualizer re-estimates the extent around the realized cards when one changes its height, so one write does not always land
    // where the measurement said: it is repeated in this turn (nothing is rendered in between) with the gain the last write showed,
    // a few times at most, until the card is where the jump would put it.
    private void RealignJumpedCard(object item, Control container)
    {
        ListBox list = ArchivePosts;
        if (list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } scroll) return;
        double lead = item is ArchivePostRow { HasUnreadLine: true } ? ReaderUnreadLineHost.Reach : 0;
        double previousWanted = double.NaN, previousWrite = 0, gain = 1;
        for (int iteration = 0; iteration < 6; iteration++)
        {
            if (list.ContainerFromItem(item) is Control current) container = current;
            if (container.TranslatePoint(default, scroll) is not Point top) return;
            double target = ReaderMediaFit.JumpOffset(scroll.Offset.Y, top.Y, container.Bounds.Height, scroll.Viewport.Height,
                scroll.Extent.Height, lead, true);
            double wanted = target - scroll.Offset.Y;
            if (Math.Abs(wanted) < 1) break;
            if (iteration > 0 && Math.Abs(previousWrite) >= 1) gain = Math.Clamp((previousWanted - wanted) / previousWrite, 0.25, 4);
            double before = scroll.Offset.Y;
            scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, before + wanted / gain));
            double applied = scroll.Offset.Y - before; // as coerced by the extent
            if (Math.Abs(applied) < 1) break;
            previousWanted = wanted;
            previousWrite = applied;
            list.UpdateLayout();
        }
        if (list.ContainerFromItem(item) is Control settled && settled.TranslatePoint(default, scroll) is Point at)
        {
            _focusedCardItem = item;
            _focusedCardTop = at.Y;
        }
    }

    // One offset write for the focused card: hold its top edge (the card moved by cardShift since it settled) and bring the whole enlarged
    // medium into view from there; one taller than the viewport (it should not be after the cap) gets its top edge aligned. The intent is
    // ReaderMediaFit.HoldDelta; this only measures (the layout is current) and applies it, and remembers where the card ends up.
    private void FitFocusedCard(object item, Control container, double cardShift)
    {
        ScrollViewer? scroll = ArchivePosts.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scroll == null || container.TranslatePoint(default, scroll) is not Point top) return;
        ArchiveAttachmentView? view = container.GetVisualDescendants().OfType<ArchiveAttachmentView>().FirstOrDefault(candidate => candidate.IsEffectivelyVisible);
        Rect? frame = view?.FrameBoundsIn(scroll);
        double delta = ReaderMediaFit.HoldDelta(cardShift, frame?.Top ?? double.NaN, frame?.Bottom ?? double.NaN, scroll.Viewport.Height);
        double moved = 0;
        if (Math.Abs(delta) >= 1)
        {
            double previous = scroll.Offset.Y;
            scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, previous + delta));
            moved = scroll.Offset.Y - previous; // as coerced by the extent
        }
        _focusedCardItem = item;
        _focusedCardTop = top.Y - moved;
    }

    // Selection changes the focused card's size. Its top edge keeps its place in the viewport: measured before the change is laid out and
    // again once it was, the scroll offset takes up the difference and brings the whole medium into view, in one write (FitFocusedCard).
    // That happens in this very turn: a layout that ran first on its own (a posted pass) would let the presenter's scroll anchoring move
    // the grown card (a card above the held one pushes it out of view) and the frame between would be rendered, a bounce. When the card
    // is not realized yet, or the layout cannot run now (this was called from inside one), the same fit follows on the Loaded pass.
    // Only while the enlargement is in effect; a second pass after other pending layout work holds the card there.
    // A selection made by a reader jump (FocusPostAsync) is left to the jump, which aligns the card's top itself: the anchor is for a
    // selection by the pointer or anything else that does not scroll.
    private void AnchorFocusedCard()
    {
        if (ArchivePosts.GetValue(ReaderPostLayout.FocusScalePercentProperty) <= 100 || ArchivePosts.SelectedItem is not { } item) return;
        ListBox list = ArchivePosts;
        _readerScrollOwner.OpenFitWindow(Environment.TickCount64, _readerInteractionRevision, FocusFitWindowMilliseconds);
        if (ReaderJumpOwnsScroll) return;
        _alignedCardItem = null; // a selection the jump did not make: its top edge is held, not centred
        Point? before = list.ContainerFromItem(item) is Control container ? container.TranslatePoint(default, list) : null;
        bool FitNow()
        {
            if (_ended || !ReferenceEquals(list.SelectedItem, item) || !MayRefitFocusedCard) return true;
            list.UpdateLayout();
            if (list.ContainerFromItem(item) is not Control current || current.TranslatePoint(default, list) is not Point after) return false;
            FitFocusedCard(item, current, before is { } start ? after.Y - start.Y : 0);
            return true;
        }
        if (before == null || !FitNow())
            Dispatcher.UIThread.Post(() => FitNow(), DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(() =>
        {
            if (_ended || !ReferenceEquals(list.SelectedItem, item) || !MayRefitFocusedCard) return;
            HoldFocusedCard();
        }, DispatcherPriority.Background);
    }
}

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
    // window that re-fits the view once more. Later size changes never move the list under a reader who scrolled on.
    private const int FocusFitWindowMilliseconds = 1500;
    private long _focusFitUntil;
    private bool _focusFitScheduled;

    private void OnFocusedFrameChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Environment.TickCount64 > _focusFitUntil || _focusFitScheduled || e.Source is not Visual source
            || ArchivePosts.SelectedItem is not { } item || ArchivePosts.ContainerFromItem(item) is not Visual container
            || !container.IsVisualAncestorOf(source)) return;
        _focusFitScheduled = true;
        Dispatcher.UIThread.Post(() => { _focusFitScheduled = false; EnsureFocusedMediaVisible(); }, DispatcherPriority.Loaded);
    }

    // The whole enlarged medium of the focused post is scrolled into view; one taller than the viewport (it should not be after the
    // cap) gets its top edge aligned. The intent is ReaderMediaFit.ScrollDelta; this only measures and applies it.
    private void EnsureFocusedMediaVisible()
    {
        ListBox list = ArchivePosts;
        if (_ended || list.GetValue(ReaderPostLayout.FocusScalePercentProperty) <= 100 || list.SelectedItem is not { } item
            || list.ContainerFromItem(item) is not Control container) return;
        ScrollViewer? scroll = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        ArchiveAttachmentView? view = container.GetVisualDescendants().OfType<ArchiveAttachmentView>().FirstOrDefault(candidate => candidate.IsEffectivelyVisible);
        if (scroll == null || view?.FrameBoundsIn(scroll) is not { } frame) return;
        double delta = ReaderMediaFit.ScrollDelta(frame.Top, frame.Bottom, scroll.Viewport.Height);
        if (Math.Abs(delta) >= 1)
            scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Offset.Y + delta));
    }

    // Selection changes the focused card's size. Its top edge keeps its place in the viewport: measured before the change is laid out and
    // again once it was, the scroll offset takes up the difference. Only while the enlargement is in effect. Then the whole medium is
    // brought into view (EnsureFocusedMediaVisible), once right after the layout and once after other pending scroll requests.
    private void AnchorFocusedCard()
    {
        if (ArchivePosts.GetValue(ReaderPostLayout.FocusScalePercentProperty) <= 100 || ArchivePosts.SelectedItem is not { } item) return;
        ListBox list = ArchivePosts;
        _focusFitUntil = Environment.TickCount64 + FocusFitWindowMilliseconds;
        Point? before = list.ContainerFromItem(item) is Control container ? container.TranslatePoint(default, list) : null;
        Dispatcher.UIThread.Post(() =>
        {
            if (_ended || !ReferenceEquals(list.SelectedItem, item)) return;
            list.UpdateLayout();
            ScrollViewer? scroll = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (before is { } start && scroll != null && list.ContainerFromItem(item) is Control current && current.TranslatePoint(default, list) is Point after)
            {
                double shift = after.Y - start.Y;
                if (Math.Abs(shift) >= 1)
                    scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Offset.Y + shift));
            }
            EnsureFocusedMediaVisible();
            Dispatcher.UIThread.Post(EnsureFocusedMediaVisible, DispatcherPriority.Background);
        }, DispatcherPriority.Loaded);
    }
}

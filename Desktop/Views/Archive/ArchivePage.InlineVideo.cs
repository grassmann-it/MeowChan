using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;

namespace MeowChan;

// Inline video in the reader (R10). One controller for the page: the posts reach it through an inherited property, the media sidebar
// owns one slot. Off unless chosen in Settings; then no player exists until a video is asked to play, at most the configured number
// are live, one plays. The page pauses them when the gallery covers the reader or the window is minimised, and releases them all when
// the reader closes or moves, the thread changes, its media is about to be deleted or the page ends.
public partial class ArchivePage
{
    private readonly ReaderInlineVideoController _inlineVideo = new();
    private ReaderInlineVideoSlot? _sidebarVideo;
    private DispatcherTimer? _sidebarVideoAutoplay;
    private string? _sidebarVideoAutoplayKey;
    private Window? _inlineVideoWindow;

    /// <summary>The reader's inline players (smoke and test seam).</summary>
    internal ReaderInlineVideoController InlineVideo => _inlineVideo;
    /// <summary>The media sidebar's inline video (smoke seam); null until the large reader was opened once.</summary>
    internal ReaderInlineVideoSlot? SidebarInlineVideo => _sidebarVideo;

    private void InitializeInlineVideo()
    {
        ArchivePosts.SetValue(ArchiveAttachmentView.InlineVideoProperty, _inlineVideo);
        ArchivePosts.SetValue(ArchiveAttachmentView.TileActivationProperty, (Func<ArchiveAttachmentRow, bool>)ShowTileInMediaSidebar);
        _inlineVideo.NavigationKeys = TryNavigateReaderByKey;
        AttachedToVisualTree += (_, _) => WatchInlineVideoWindow(TopLevel.GetTopLevel(this) as Window);
        DetachedFromVisualTree += (_, _) => { WatchInlineVideoWindow(null); ReleaseInlineVideos(); };
    }

    // The live part of the media preferences: settings and whether the focused card may autoplay (not while the sidebar presents it).
    private void ApplyInlineVideoLive(ReaderMediaPreferences media)
    {
        _ = _inlineVideo.ConfigureAsync(media.Video);
        _inlineVideo.AutoplayInPosts = media.Video.InPost && media.Video.AutoplayMuted && !_mediaSidebarShown;
        if (_mediaSidebarShown) OfferSidebarVideo(ArchivePosts.SelectedItem as ArchivePostRow, CurrentSidebarAttachment());
    }

    /// <summary>Every inline player is disposed (reader closed or moved, other thread, media deletion, page end).</summary>
    internal Task ReleaseInlineVideos()
    {
        _sidebarVideoAutoplay?.Stop();
        _sidebarVideoAutoplayKey = null;
        return _inlineVideo.ReleaseAllAsync();
    }

    /// <summary>Every inline video pauses (gallery over the reader, window minimised); the players stay to continue later.</summary>
    internal Task PauseInlineVideos()
    {
        _sidebarVideoAutoplay?.Stop();
        return _inlineVideo.PauseAllAsync();
    }

    private void WatchInlineVideoWindow(Window? window)
    {
        if (ReferenceEquals(window, _inlineVideoWindow)) return;
        if (_inlineVideoWindow != null) _inlineVideoWindow.PropertyChanged -= OnInlineVideoWindowPropertyChanged;
        _inlineVideoWindow = window;
        if (window != null) window.PropertyChanged += OnInlineVideoWindowPropertyChanged;
    }

    private void OnInlineVideoWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty && e.NewValue is WindowState.Minimized) _ = PauseInlineVideos();
    }

    // ---- the reader's video keys --------------------------------------------------------------------------------------------------

    /// <summary>
    /// The video the reader's keys act on for the selected post: the media sidebar's when it is shown and presents a video; otherwise the first
    /// video of the post that can play in it (one that already plays first). Null when nothing can play inline, so the keys keep their old meaning
    /// (Enter and Space open the gallery). The sidebar goes first because it is what the person looks at, and so no second player of the
    /// same file is created beside it.
    /// </summary>
    private ReaderInlineVideoSlot? ReaderVideoTarget(ArchivePostRow post)
    {
        if (!_inlineVideo.Options.Enabled) return null;
        if (_mediaSidebarShown && _sidebarVideo is { IsOffered: true } sidebar) return sidebar;
        if (ArchivePosts.ContainerFromItem(post) is not Control container) return null;
        List<ArchiveAttachmentView> views = container.GetVisualDescendants().OfType<ArchiveAttachmentView>()
            .Where(view => ReaderInlineVideoRules.CanOfferInPost(_inlineVideo.Options, view.DataContext as ArchiveAttachmentRow)).ToList();
        ArchiveAttachmentView? chosen = views.FirstOrDefault(view => view.InlineVideo?.IsLive == true) ?? views.FirstOrDefault();
        return chosen?.InlineVideoForKeys();
    }

    /// <summary>
    /// Enter plays or pauses the selected post's video; the gallery's video keys (play / pause, mute, repeat, seek step, speed, next frame)
    /// act on it too. They are only taken where the focus is on the post's content (never in a text field, a button or a list of its own, which
    /// keep their keys), so Space is the video's only where it cannot be anything else; elsewhere in the reader the keys are what they were.
    /// </summary>
    private bool TryRouteReaderVideoKey(KeyEventArgs e)
    {
        if (e.Handled || !_largeReader || e.Source is not Control source || ArchivePosts.SelectedItem is not ArchivePostRow post) return false;
        if (e.Source is ReaderBodyTextBlock { HasHiddenSpoilers: true }) return false;
        if (!ReaderMediaContextIsCurrent(source, post, needsGallery: false)) return false;
        // Enter follows the choice "Clicking a medium" for the post's first medium (a click on it, from the keyboard).
        if (ReaderVideoKeys.IsEnterToggle(e.Key, e.KeyModifiers) && IsReaderMediaGesture(e, ShortcutService.Current)) return TryActivateFirstMedium(e, post);
        ReaderVideoAction action = ReaderVideoKeys.Classify(id => ShortcutService.Current.Matches(e, id));
        if (action == ReaderVideoAction.None || ReaderVideoTarget(post) is not { } slot) return false;
        e.Handled = true;
        _ = slot.ExecuteAsync(action);
        return true;
    }

    /// <summary>
    /// Enter on the selected post: what a click on its first medium does by the choice "Clicking a medium". In the sidebar it shows that medium there
    /// and plays a video (Enter again, while that video is shown, plays or pauses it); in the post it plays a video or expands an image (again: pauses,
    /// or puts the image back). With the gallery as the choice, or the sidebar closed, it returns false and Enter opens the gallery as it always did.
    /// </summary>
    private bool TryActivateFirstMedium(KeyEventArgs e, ArchivePostRow post)
    {
        if (!post.ShowsMedia || post.Attachments.Count == 0) return false;
        ReaderMediaClickMode target = ReaderMediaClickRules.Resolve(_inlineVideo.Options.Mode, _mediaSidebarShown);
        ArchiveAttachmentRow first = post.Attachments[0];
        if (target == ReaderMediaClickMode.Sidebar)
        {
            e.Handled = true;
            if (ReferenceEquals(CurrentSidebarAttachment(), first) && _sidebarVideo is { IsOffered: true } shown) _ = shown.ToggleAsync();
            else ShowTileInMediaSidebar(first);
            return true;
        }
        if (target == ReaderMediaClickMode.Post && ArchivePosts.ContainerFromItem(post) is Control container
            && container.GetVisualDescendants().OfType<ArchiveAttachmentView>().FirstOrDefault(view => ReferenceEquals(view.DataContext, first)) is { } view)
        {
            e.Handled = true;
            view.ActivateMedium();
            return true;
        }
        return false;
    }

    // The sidebar's own focus (its strip, its header buttons): the video keys act on the video it presents.
    private bool TryRouteSidebarVideoKey(KeyEventArgs e)
    {
        if (e.Handled || !_mediaSidebarShown || _sidebarVideo is not { IsOffered: true } slot || e.Source is Button) return false;
        ReaderVideoAction action = ReaderVideoKeys.Classify(id => CommandBindings.Matches(e, id));
        if (action == ReaderVideoAction.None) return false;
        e.Handled = true;
        _ = slot.ExecuteAsync(action);
        return true;
    }

    // ---- media sidebar ---------------------------------------------------------------------------------------------------------

    private void CreateSidebarVideo(ReaderMediaSidebar sidebar)
    {
        // In the sidebar the slot's activator covers the poster also while idle: the focusable "Play video here" (Enter plays).
        ReaderInlineVideoSlot slot = _sidebarVideo = new ReaderInlineVideoSlot(sidebar.FrameArea, activatorWhenIdle: true);
        sidebar.VideoRow.Children.Add(slot.Below);
    }

    /// <summary>The sidebar shows this attachment (or none): a video offers inline playback; any other medium gives a player back.</summary>
    private void OfferSidebarVideo(ArchivePostRow? post, ArchiveAttachmentRow? row)
    {
        if (_sidebarVideo is not { } slot) return;
        bool offered = _mediaSidebarShown && post != null && ReaderInlineVideoRules.CanOffer(_inlineVideo.Options, row);
        string? key = offered ? _thread?.Summary.Id + "\n" + post!.Id + "\n" + ReaderInlineVideoSlot.KeyOf(row!.Attachment) : null;
        slot.Offer(offered ? _inlineVideo : null, key, row?.ResolveEntry, row?.Attachment.OriginalFileName ?? row?.Attachment.MediaId ?? string.Empty,
            row?.OpenPreview);
        if (!offered) { _sidebarVideoAutoplay?.Stop(); _sidebarVideoAutoplayKey = null; }
        // A click on a video tile of the post asked for it to play here (the sidebar presents it now).
        if (_sidebarPlayRequested) { _sidebarPlayRequested = false; if (offered) _ = slot.PlayAsync(); }
    }

    /// <summary>A click on the sidebar's picture: plays or pauses an inline video; anything else opens the gallery as before.</summary>
    private void OnMediaSidebarPictureActivated()
    {
        if (_sidebarVideo is { IsOffered: true } slot) { _ = slot.ToggleAsync(); return; }
        OpenMediaSidebarMedia();
    }

    // Opt-in muted autoplay of the sidebar's video, once the selection rested (holding ↑/↓ starts nothing).
    private void ScheduleSidebarVideoAutoplay()
    {
        _sidebarVideoAutoplay?.Stop();
        if (_sidebarVideo is not { IsOffered: true, IsLive: false }
            || !ReaderInlineVideoRules.ShouldAutoplay(_inlineVideo.Options, _mediaSidebarShown && !_obscuredByGallery, presentedElsewhere: false)) return;
        _sidebarVideoAutoplayKey = ArchivePosts.SelectedItem is ArchivePostRow post ? post.Id + "\n" + _sidebarIndex : null;
        DispatcherTimer timer = _sidebarVideoAutoplay ??= CreateSidebarTimer(() =>
        {
            string? key = ArchivePosts.SelectedItem is ArchivePostRow current ? current.Id + "\n" + _sidebarIndex : null;
            if (!_ended && _mediaSidebarShown && !_obscuredByGallery && key != null && key == _sidebarVideoAutoplayKey
                && _sidebarVideo is { IsOffered: true, IsLive: false } video)
                _ = video.PlayAsync(muted: true);
        });
        timer.Interval = TimeSpan.FromMilliseconds(ReaderInlineVideoRules.AutoplayDelayMs);
        timer.Start();
    }
}

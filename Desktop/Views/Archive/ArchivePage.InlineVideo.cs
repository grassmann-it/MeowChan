using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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

    /// <summary>The profile's timeline hover-preview cache (the gallery's own); the shell sets it. Videos started after it is set show the preview on their timeline.</summary>
    internal VideoScrubPreviewCache? ScrubPreviewCache { get => _inlineVideo.ScrubCache; set => _inlineVideo.ScrubCache = value; }

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
        CloseQuotePreview(); // its own player and GIF go with it
        _sidebarVideoAutoplay?.Stop();
        _sidebarVideoAutoplayKey = null;
        // The quote preview's player is disposed like the reader's own (shutdown and media deletion wait for its native cleanup too).
        Task preview = _quotePreviewMedia?.Videos.WhenReleasedAsync() ?? Task.CompletedTask;
        return Task.WhenAll(_inlineVideo.ReleaseAllAsync(), preview);
    }

    /// <summary>Every inline video pauses (gallery over the reader, window minimised); the players stay to continue later.</summary>
    internal Task PauseInlineVideos()
    {
        CloseQuotePreview(); // a preview never plays under the gallery or in a minimised window
        _sidebarVideoAutoplay?.Stop();
        return _inlineVideo.PauseAllAsync();
    }

    private void WatchInlineVideoWindow(Window? window)
    {
        if (ReferenceEquals(window, _inlineVideoWindow)) return;
        if (_inlineVideoWindow != null)
        {
            _inlineVideoWindow.PropertyChanged -= OnInlineVideoWindowPropertyChanged;
            _inlineVideoWindow.RemoveHandler(KeyDownEvent, OnViewerFocusLostKeyDown);
        }
        _inlineVideoWindow = window;
        if (window != null)
        {
            window.PropertyChanged += OnInlineVideoWindowPropertyChanged;
            window.AddHandler(KeyDownEvent, OnViewerFocusLostKeyDown, RoutingStrategies.Bubble);
        }
    }

    private void OnInlineVideoWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty && e.NewValue is WindowState.Minimized) _ = PauseInlineVideos();
    }

    // ---- the reader's video keys --------------------------------------------------------------------------------------------------

    /// <summary>
    /// The video the reader's video keys (mute, repeat, seek step, speed, next frame) act on for the selected post: the same medium Space and Enter act on (one resolver,
    /// <see cref="ReaderMediaActivation.Choose"/>), restricted to the videos that can play inline. Null when nothing can play inline, so the keys keep their old meaning.
    /// </summary>
    private ReaderInlineVideoSlot? ReaderVideoTarget(ArchivePostRow post, Control? source = null)
    {
        if (!_inlineVideo.Options.Enabled) return null;
        PostMediaSnapshot? snapshot = SnapshotPostMedia(post, source, videosOnly: true);
        if (snapshot == null || ReaderMediaActivation.Choose(snapshot.Media, snapshot.SidebarIndex, snapshot.SidebarLive, snapshot.SidebarPlaying, snapshot.FocusedIndex) is not { } choice)
            return null;
        if (choice.Live == ReaderMediumPlace.Sidebar || choice.Live == ReaderMediumPlace.None && snapshot.SidebarIndex == choice.Index)
            return _sidebarVideo is { IsOffered: true } sidebar ? sidebar : null;
        return snapshot.Views[choice.Index]?.InlineVideoForKeys();
    }

    /// <summary>
    /// The selected post's medium keys. Enter (the command "Open medium of the post") and Space (play / pause, with the same action as its fallback) act on one medium
    /// by one rule (<see cref="ReaderMediaActivation"/>): a video with a player toggles it; anything else does what a click does by the choice "Clicking a medium".
    /// The gallery's other video keys (mute, repeat, seek step, speed, next frame) act on the same video. They are only taken where the focus is on the post's content (never in a
    /// text field, a button or a list of its own, which keep their keys) and yield to a saved reader or shell binding on the same gesture.
    /// </summary>
    private bool TryRouteReaderVideoKey(KeyEventArgs e)
    {
        if (e.Handled || !_largeReader || e.Source is not Control source || ArchivePosts.SelectedItem is not ArchivePostRow post) return false;
        if (e.Source is ReaderBodyTextBlock { HasHiddenSpoilers: true }) return false;
        if (!ReaderMediaContextIsCurrent(source, post, needsGallery: false)) return false;
        ReaderVideoAction action = ReaderVideoKeys.ClassifyUnclaimed(id => ShortcutService.Current.Matches(e, id));
        if (ReaderMediaActivation.IsMediaKey(e.Key, e.KeyModifiers, ShortcutService.Current.Matches(e, ActivateMediumCommandId), ClaimedByOtherReaderCommand(e))
            || action == ReaderVideoAction.PlayPause)
            return TryActivatePostMedium(e, post, source);
        if (action == ReaderVideoAction.None || ReaderVideoTarget(post, source) is not { } slot) return false;
        e.Handled = true;
        _ = slot.ExecuteAsync(action);
        return true;
    }

    // The sidebar's own focus (its strip, its header buttons): the video keys act on the video it presents.
    private bool TryRouteSidebarVideoKey(KeyEventArgs e)
    {
        if (e.Handled || !_mediaSidebarShown || _sidebarVideo is not { IsOffered: true } slot || e.Source is Button) return false;
        ReaderVideoAction action = ReaderVideoKeys.ClassifyUnclaimed(id => CommandBindings.Matches(e, id));
        if (action == ReaderVideoAction.None) return false;
        e.Handled = true;
        _ = slot.ExecuteAsync(action);
        return true;
    }

    // ---- media sidebar ---------------------------------------------------------------------------------------------------------

    private void CreateSidebarVideo(ReaderMediaSidebar sidebar)
    {
        // In the sidebar the slot's activator covers the poster also while idle: the focusable "Play video here" (Enter plays).
        ReaderInlineVideoSlot slot = _sidebarVideo = new ReaderInlineVideoSlot(sidebar.FrameArea, activatorWhenIdle: true) { PointerPlayFocus = FocusPostsAfterPointerPlay };
        sidebar.VideoRow.Children.Add(slot.Below);
    }

    // After a click on the sidebar's play button the button hides itself and would leave no focus: it goes to the posts, where Up and Down
    // move the reader and the video keys act on the shown video. A menu, dialog or field that took the focus meanwhile keeps it.
    private void FocusPostsAfterPointerPlay()
    {
        if (_ended || !_mediaSidebarShown || _mediaSidebar is not { } sidebar) return;
        object? focused = TopLevel.GetTopLevel(sidebar)?.FocusManager?.GetFocusedElement();
        if (focused is Visual visual && !visual.GetSelfAndVisualAncestors().Any(node => ReferenceEquals(node, sidebar))) return;
        ArchivePosts.Focus(NavigationMethod.Pointer);
    }

    // ---- keys in the media sidebar -----------------------------------------------------------------------------------------------

    /// <summary>
    /// The reader's post keys for a key pressed in the media sidebar (not in the timeline or on the picture, which the video slot routes first): the
    /// large reader moves by the reader's bindings, the archive view moves the selected post one up or down. True when the key did something.
    /// </summary>
    internal bool TryNavigateFromViewer(KeyEventArgs e)
    {
        if (e.Handled || ShortcutService.IsCapturing || _ended) return false;
        if (_largeReader) return TryNavigateReaderByKey(e);
        if (_busy || IsReaderDialogOpen || ArchivePosts.ItemCount == 0) return false;
        int delta = ShortcutService.Current.Matches(e, "reader.previous-post") ? -1 : ShortcutService.Current.Matches(e, "reader.next-post") ? 1 : 0;
        if (delta == 0) return false;
        e.Handled = true;
        int index = Math.Clamp((ArchivePosts.SelectedIndex < 0 ? (delta > 0 ? -1 : ArchivePosts.ItemCount) : ArchivePosts.SelectedIndex) + delta, 0, ArchivePosts.ItemCount - 1);
        if (index != ArchivePosts.SelectedIndex)
        {
            ArchivePosts.SelectedIndex = index;
            ArchivePosts.ScrollIntoView(index);
        }
        return true;
    }

    // Bubble, after the strip and the video had their keys: whatever of the reader's navigation is left reaches the reader from any focus in the sidebar.
    private void OnSidebarNavigationKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || !ReaderVideoKeys.IsNavigationKey(e.Key)) return;
        TryNavigateFromViewer(e);
    }

    // A window-level net for the moment the focus is nowhere (the pressed play button hid itself): the key then has the window for its source and would reach
    // neither the reader's pane nor the sidebar. The sidebar's video keys and the reader's navigation work from there too.
    private void OnViewerFocusLostKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || !ReferenceEquals(e.Source, sender) || _ended || _obscuredByGallery || IsReaderDialogOpen || ShortcutService.IsCapturing) return;
        bool visible = _largeReader ? _largeReaderFrame?.IsEffectivelyVisible == true : _mediaSidebarShown && IsEffectivelyVisible;
        if (!visible) return;
        if (TryRouteSidebarVideoKey(e)) return;
        if (ReaderVideoKeys.IsNavigationKey(e.Key)) TryNavigateFromViewer(e);
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

    // The sidebar's own autoplay (default on; sound as its start-muted switch says) of the shown video, once the selection rested (holding ↑/↓ starts nothing).
    private void ScheduleSidebarVideoAutoplay()
    {
        _sidebarVideoAutoplay?.Stop();
        if (_sidebarVideo is not { IsOffered: true, IsLive: false }
            || !ReaderInlineVideoRules.ShouldAutoplayViewer(_inlineVideo.Options, _mediaSidebarShown && !_obscuredByGallery)) return;
        _sidebarVideoAutoplayKey = ArchivePosts.SelectedItem is ArchivePostRow post ? post.Id + "\n" + _sidebarIndex : null;
        DispatcherTimer timer = _sidebarVideoAutoplay ??= CreateSidebarTimer(() =>
        {
            string? key = ArchivePosts.SelectedItem is ArchivePostRow current ? current.Id + "\n" + _sidebarIndex : null;
            if (!_ended && _mediaSidebarShown && !_obscuredByGallery && key != null && key == _sidebarVideoAutoplayKey
                && _sidebarVideo is { IsOffered: true, IsLive: false } video)
                _ = video.PlayAsync(); // the sidebar's own autoplay is not muted by force: its "start muted" decides
        });
        timer.Interval = TimeSpan.FromMilliseconds(ReaderInlineVideoRules.AutoplayDelayMs);
        timer.Start();
    }
}

using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using MeowChan.Desktop;

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
        AttachedToVisualTree += (_, _) => WatchInlineVideoWindow(TopLevel.GetTopLevel(this) as Window);
        DetachedFromVisualTree += (_, _) => { WatchInlineVideoWindow(null); ReleaseInlineVideos(); };
    }

    // The live part of the media preferences: settings and whether the focused card may autoplay (not while the sidebar presents it).
    private void ApplyInlineVideoLive(ReaderMediaPreferences media)
    {
        _ = _inlineVideo.ConfigureAsync(media.Video);
        _inlineVideo.AutoplayInPosts = media.Video.Enabled && media.Video.AutoplayMuted && !_mediaSidebarShown;
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
        slot.Offer(offered ? _inlineVideo : null, key, row?.ResolveEntry, row?.Attachment.OriginalFileName ?? row?.Attachment.MediaId ?? string.Empty);
        if (!offered) { _sidebarVideoAutoplay?.Stop(); _sidebarVideoAutoplayKey = null; }
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

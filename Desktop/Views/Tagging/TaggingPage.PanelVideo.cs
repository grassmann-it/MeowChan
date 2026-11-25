using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan;

// The video of the tagging sidebar: the same inline player as the reader's media sidebar and the Media page's panel (ReaderInlineVideoSlot, the gallery's
// engine) with the same shared options, never a second definition: "Autoplay videos in the media sidebar", "Loop videos in the media sidebar" and
// "Start videos in the media sidebar muted" (Settings, Reader), the gallery's volume and seek step and the reader's player and file limits. The shell
// passes them in (ConfigurePanelVideo) after every save, as it does for the Media page. A saved video starts by itself once the cursor rested on it,
// and only once per medium; the player pauses when the window is minimised and is given back when the sidebar hides, the page ends or the gallery
// opens (the shell's playback gate). The video keys of the gallery act on it while the focus is in the sidebar (they give way to the page's own keys).
public sealed partial class TaggingPage
{
    private readonly ReaderInlineVideoController _panelVideoController = new();
    private ReaderInlineVideoSlot? _panelVideo;
    private CancellationTokenSource? _panelAutoplay;
    private string? _panelAutoplayDone;
    private Window? _panelWindow;
    private bool _panelVideoConfigured;

    /// <summary>The sidebar's video slot (test seam); null until the sidebar was shown once.</summary>
    internal ReaderInlineVideoSlot? PanelVideo => _panelVideo;
    /// <summary>The sidebar's inline players (test seam).</summary>
    internal ReaderInlineVideoController PanelVideoController => _panelVideoController;
    /// <summary>How the player opens the manifest of its file (test seam; null: the sidebar's own <see cref="OpenPanelManifestAsync"/>).</summary>
    internal Func<string, CancellationToken, Task<MediaAssetSnapshot>>? OpenPanelVideoManifestAsync { get; init; }
    internal PlaybackArbitrationGate? PlaybackGate { get => _panelVideoController.PlaybackGate; set => _panelVideoController.PlaybackGate = value; }

    /// <summary>The shared video options of the media sidebars (the sidebar always plays inline); a change applies to the video shown now.</summary>
    internal void ConfigurePanelVideo(ReaderInlineVideoOptions video)
    {
        _panelVideoConfigured = true;
        _ = _panelVideoController.ConfigureAsync((video with { Enabled = true }).Clamped());
        if (IsPanelShown) OfferPanelVideo(_panelRow);
    }

    /// <summary>The profile's timeline hover-preview cache (the gallery's own); players started after it is set show the preview.</summary>
    internal void ConfigureScrubPreviewCache(VideoScrubPreviewCache? cache) => _panelVideoController.ScrubCache = cache;

    private void InitializePanelVideo(ReaderMediaSidebar panel)
    {
        // Until the shell passes the options in, the profile's saved ones apply (the same settings, read once).
        if (!_panelVideoConfigured) ConfigurePanelVideo(ReaderInlineVideoOptions.From(_host.Config));
        ReaderInlineVideoSlot slot = _panelVideo = new ReaderInlineVideoSlot(panel.FrameArea, activatorWhenIdle: true)
        {
            ShortcutContext = "tagging", OwnerClaims = MediaPanelKeys.TaggingClaims, PointerPlayFocus = FocusListAfterPointerPlay
        };
        panel.VideoRow.Children.Add(slot.Below);
        AttachedToVisualTree += (_, _) => WatchPanelWindow(TopLevel.GetTopLevel(this) as Window);
        DetachedFromVisualTree += (_, _) => WatchPanelWindow(null);
        if (TopLevel.GetTopLevel(this) is Window attached) WatchPanelWindow(attached);
    }

    // After a click on the sidebar's play button the keys go back to the list, unless a menu, dialog or field took the focus meanwhile.
    private void FocusListAfterPointerPlay()
    {
        if (_queueWork.Ended || !IsPanelShown) return;
        object? focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (focused is Visual visual && (_taggingPanel == null || !visual.GetSelfAndVisualAncestors().Any(node => ReferenceEquals(node, _taggingPanel)))) return;
        FocusContent(_panelRow?.Key);
    }

    private void WatchPanelWindow(Window? window)
    {
        if (ReferenceEquals(window, _panelWindow)) return;
        if (_panelWindow != null) _panelWindow.PropertyChanged -= OnPanelWindowPropertyChanged;
        _panelWindow = window;
        if (window != null) window.PropertyChanged += OnPanelWindowPropertyChanged;
    }

    private void OnPanelWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty && e.NewValue is WindowState.Minimized) _ = PausePanelVideo();
    }

    private static string PanelVideoKey(MediaLibraryEntry entry) => string.Join("\n", entry.Thread, entry.Directory, entry.AssetId,
        entry.MediaId, entry.Variant, entry.Sha256, entry.Length);

    /// <summary>The sidebar shows this row (or none): a saved video offers inline playback; anything else gives the player back.</summary>
    private void OfferPanelVideo(TaggingQueueRow? row)
    {
        if (_panelVideo is not { } slot) return;
        MediaLibraryEntry? entry = row == null ? null : EntryOf(row.Item);
        bool offered = IsPanelShown && entry != null && MediaPanelKeys.CanOfferVideo(_panelVideoController.Options.Enabled,
            entry.Directory != null && entry.AssetId != null, entry.Kind == SavedMediaKind.Video);
        slot.Offer(offered ? _panelVideoController : null, offered ? PanelVideoKey(entry!) : null,
            offered ? token => ResolvePanelVideoAsync(entry!, token) : null, row?.Name ?? string.Empty, offered ? OpenPanelGallery : null,
            posterKey: _panelMediaKey);
        SchedulePanelVideoAutoplay(offered ? PanelVideoKey(entry!) : null);
    }

    // The saved file for the player: the manifest of its directory and the asset the queue entry names (the player verifies the bytes itself).
    private async Task<GalleryEntry?> ResolvePanelVideoAsync(MediaLibraryEntry entry, CancellationToken token)
    {
        if (entry.Directory == null) return null;
        MediaAssetSnapshot manifest = await (OpenPanelVideoManifestAsync ?? OpenPanelManifestAsync)(entry.Directory, token);
        MediaAssetReference? asset = MediaTileThumbnails.Matching(manifest, entry);
        return asset == null ? null : new GalleryEntry(new GalleryAssetKey(entry.Thread, manifest.Directory, asset.AssetId), manifest, asset, null, null,
            !asset.IsCurrent, entry.Unassigned);
    }

    // Like the reader's sidebar and the Media page's panel: once the cursor rested (the shared autoplay delay), and only for a medium not started this way before.
    private void SchedulePanelVideoAutoplay(string? key)
    {
        CancelPanelAutoplay();
        if (key == null) { _panelAutoplayDone = null; return; }
        if (key == _panelAutoplayDone || _panelVideo is not { IsOffered: true, IsLive: false }
            || !ReaderInlineVideoRules.ShouldAutoplayViewer(_panelVideoController.Options, IsPanelShown)) return;
        CancellationTokenSource autoplay = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _panelAutoplay = autoplay;
        OpenActions.Start(() => AutoplayPanelVideoAsync(key, autoplay), "tagging.panel.autoplay");
    }

    private async Task AutoplayPanelVideoAsync(string key, CancellationTokenSource autoplay)
    {
        CancellationToken stopped = autoplay.Token;
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(ReaderInlineVideoRules.AutoplayDelayMs), PanelClock, stopped);
            if (!ReferenceEquals(_panelAutoplay, autoplay) || !IsPanelShown || _tagBusy || _panelVideo is not { IsOffered: true, IsLive: false } video
                || !ReaderInlineVideoRules.ShouldAutoplayViewer(_panelVideoController.Options, shown: true)) return;
            _panelAutoplayDone = key;
            await video.PlayAsync();
        }
        catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_panelAutoplay, autoplay))
            {
                _panelAutoplay = null;
                autoplay.Dispose();
            }
        }
    }

    private void CancelPanelAutoplay()
    {
        CancellationTokenSource? autoplay = _panelAutoplay;
        _panelAutoplay = null;
        try { autoplay?.Cancel(); } catch (ObjectDisposedException) { }
        autoplay?.Dispose();
    }

    /// <summary>The sidebar's player pauses (the window is minimised); it stays to continue later.</summary>
    internal Task PausePanelVideo() => _panelVideoController.PauseAllAsync();

    /// <summary>The sidebar's players are given back (the sidebar hides, the page ends, the gallery opens).</summary>
    internal Task ReleasePanelVideos()
    {
        CancelPanelAutoplay();
        return _panelVideoController.ReleaseAllAsync();
    }
}

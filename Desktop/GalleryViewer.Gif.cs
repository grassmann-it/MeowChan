using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

internal sealed partial class GalleryViewer
{
    private GalleryGifPlayback? _gif;
    private Task? _gifOpening;
    private Task _gifRetirement = Task.CompletedTask;
    private object? _retainedGifCleanupFailure;
    private Window? _gifOwner;
    private readonly ToggleButton _gifPlayPause = new()
    { Name = "GalleryGifPlayPause", Classes = { "media-control" }, Width = 32, Height = 32, IsVisible = false };
    internal GifPlaybackEvidence? GifEvidence => _gif?.Evidence;
    internal GalleryGifPlayback? GifPlayback => _gif;
    // Controlled ownership fixtures only; ordinary presentation never supplies either seam.
    internal Func<GalleryEntry, CancellationToken, Task<IGifFrameDecoder?>>? OpenGifDecoderForTests { get; set; }
    internal Func<GalleryGifPlayback, Task>? BeforeGifAdoptionForTests { get; set; }

    private void InitializeGifControls(StackPanel focusTools)
    {
        focusTools.Children.Add(_gifPlayPause);
        CommandBindings.BindExisting(_gifPlayPause, "video.play-pause");
        _gifPlayPause.Click += async (_, _) => await ToggleGifPlaybackAsync();
        AttachedToVisualTree += (_, _) =>
        {
            _gifOwner = TopLevel.GetTopLevel(this) as Window;
            if (_gifOwner != null) { _gifOwner.Activated += OnGifOwnerState; _gifOwner.Deactivated += OnGifOwnerState; }
            UpdateGifPresentation();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (_gifOwner != null) { _gifOwner.Activated -= OnGifOwnerState; _gifOwner.Deactivated -= OnGifOwnerState; }
            _gifOwner = null;
            _gif?.SetActive(false);
        };
    }
    private void OnGifOwnerState(object? sender, EventArgs args) => UpdateGifPresentation();
    private bool CanPresentGif => !_viewerInteractionStopped && _interactionAvailable && !_videoSuspended
        && IsEffectivelyVisible && IsGalleryWindowActive;
    private void UpdateGifPresentation()
    {
        _gif?.SetActive(CanPresentGif);
        RefreshGifTransport();
    }
    private void RefreshGifTransport()
    {
        GifPlaybackEvidence? evidence = GifEvidence;
        _gifPlayPause.IsVisible = evidence is { FrameCount: > 1, Disposed: false } && !_mediaFailed;
        if (!_gifPlayPause.IsVisible) return;
        bool playing = evidence is { UserPaused: false, Ended: false };
        _gifPlayPause.IsChecked = playing;
        _gifPlayPause.Content = CommandBindings.Icon("video.play-pause", 20, playing ? "playing" : "paused");
        string label = Texts.T(playing ? "viewer.video.pause" : "viewer.video.play") + " · GIF";
        string tip = label + (CommandCatalog.ShortcutDisplay("video.play-pause") is string keys ? " · " + keys : "");
        AutomationProperties.SetName(_gifPlayPause, label);
        AutomationProperties.SetHelpText(_gifPlayPause, tip);
        ToolTip.SetTip(_gifPlayPause, tip);
    }
    private async Task ToggleGifPlaybackAsync()
    {
        GalleryGifPlayback? player = _gif;
        if (player == null || _mediaFailed || _viewerInteractionStopped) return;
        await player.ToggleAsync();
        if (!ReferenceEquals(player, _gif)) return;
        if (player.UserPaused) PauseSlideshow();
        RefreshGifTransport();
    }
    private static bool IsGifCandidate(GalleryEntry entry) => string.Equals(
        Path.GetExtension(entry.Asset.OriginalFileName ?? entry.Asset.Path), ".gif", StringComparison.OrdinalIgnoreCase);

    // Called only inside the existing one-active/one-desired image decode queue.
    private Task<bool> TryAdoptGifAsync(GalleryEntry entry, int request, int decodeGeneration, CancellationToken token)
    {
        Task<bool> opening = LoadGifCoreAsync(entry, request, decodeGeneration, token);
        _gifOpening = opening;
        return opening;
    }
    private async Task<bool> LoadGifCoreAsync(GalleryEntry entry, int request, int decodeGeneration, CancellationToken token)
    {
        if (!IsGifCandidate(entry)) return false;
        GalleryGifPlayback? player = await GalleryGifPlayback.OpenAsync(entry, _image, token, OpenGifDecoderForTests);
        if (player == null) return false;
        try
        {
            if (BeforeGifAdoptionForTests is { } before) await before(player);
            if (_viewerInteractionStopped || request != _request || decodeGeneration != _decodeGeneration || token.IsCancellationRequested)
            { await player.DisposeAsync(); return true; }
            _gif = player;
            player.StateChanged += () => { if (ReferenceEquals(_gif, player)) RefreshGifTransport(); };
            player.Failed += error =>
            {
                if (ReferenceEquals(_gif, player) && request == _request && !_viewerInteractionStopped)
                    _ = HandleGifFailureAsync(error, request);
            };
            _info = player.Info;
            _mediaFailed = false;
            _status.Text = string.Empty;
            _status.IsVisible = false;
            _image.IsVisible = true;
            player.Adopt();
            SetImageCommands(true);
            FitIfAutomatic(); ApplyRestoredState(); MarkVisibleImageReady(); UpdateGifPresentation();
            return true;
        }
        catch (GifRetirementException cleanup)
        {
            _retainedGifCleanupFailure = new object[] { player, cleanup.Owner };
            throw new GifRetirementException(_retainedGifCleanupFailure, cleanup);
        }
        catch (Exception adoptionError)
        {
            if (!ReferenceEquals(_gif, player))
                try { await player.DisposeAsync(); }
                catch (Exception cleanup)
                { throw new GifRetirementException(player, new AggregateException(adoptionError, cleanup)); }
            throw;
        }
    }
    private async Task HandleGifFailureAsync(Exception error, int request)
    {
        try { await SetMediaUnavailable(error.Message, request); }
        catch (Exception cleanup)
        {
            // Keep retirement failure reachable by StopAsync; acknowledgement never proves release.
            ReportDiagnostic("gif-cleanup", cleanup.Message, mediaUnavailable: true);
        }
    }
    private Task RetireGifAsync()
    {
        Task? opening = _gifOpening; _gifOpening = null;
        GalleryGifPlayback? player = _gif; _gif = null;
        _gifPlayPause.IsVisible = false;
        if (player == null && opening == null) return _gifRetirement;
        Task retirement = DrainGifAsync(opening, player);
        return _gifRetirement = _gifRetirement.IsCompletedSuccessfully
            ? retirement : Task.WhenAll(_gifRetirement, retirement);
    }
    private async Task DrainGifAsync(Task? opening, GalleryGifPlayback? player)
    {
        // Failed unadopted opens clean their codec before returning the failure. Observe
        // actual completion, not a canceled waiter; stale opens dispose before they finish.
        if (opening != null)
            try { await opening; }
            catch (GifRetirementException cleanup)
            { _retainedGifCleanupFailure = cleanup.Owner; throw; }
            catch (OperationCanceledException) { }
            catch { /* owning load reports decode failure AFTER successful factory cleanup */ }
        if (player == null) return;
        try { await player.DisposeAsync(); }
        catch { _retainedGifCleanupFailure = player; throw; }
    }
}

using System;
using System.Threading.Tasks;
using MeowChan.Desktop.Commands;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Gallery;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

internal sealed partial class GalleryViewer
{
    private GalleryPlaybackPreferences _playbackPreferences = new();
    private GallerySlideshowController _slideshow = null!;
    private readonly DispatcherTimer _interactionTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _updatingPlaybackControls;
    private bool _interactionAvailable = true;
    private bool _viewerInteractionStopped;
    private bool _navigationPending;
    private bool _mediaFailed;
    private int _imageReadyRequest = -1, _videoReadyRequest = -1;
    private bool _lastRunning;
    private bool _slideshowStarted;
    private bool _selectionVideoPaused;
    private SlideshowStatus _shownSlideshowStatus;
    private readonly TextBlock _slideshowStatus = new() { Name = "GallerySlideshowStatus", TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(8, 0, 8, 0), IsVisible = false };
    private string? _playbackLabelsLanguage;
    private long _slideshowGeneration;
    private Action? _videoEndedHandler, _videoSeekHandler, _videoFailedHandler;
    private Action<string>? _videoNoticeHandler;
    private Action<bool>? _videoPlaybackHandler;
    private readonly ComboBox _playbackMode = new() { Name = "GalleryPlaybackModeSelector", MinWidth = 120 };
    private readonly ToggleButton _slideshowToggle = new() { Name = "GallerySlideshowToggle" };
    private readonly ToggleButton _overlayToggle = new() { Name = "GalleryOverlayToggle" };
    private readonly ToggleButton _autoHideToggle = new() { Name = "GalleryAutoHideToggle" };
    private readonly ToggleButton _postOverlayToggle = new() { Name = "GalleryPostOverlayToggle" };
    private readonly TextBlock _overlayFallbackNotice = new() { Name = "GalleryOverlayFallbackNotice",
        IsVisible = false, TextWrapping = TextWrapping.Wrap };

    internal event Action<GalleryPlaybackPreferences>? PlaybackPreferencesChanged;

    internal void ConfigurePlayback(GalleryPlaybackPreferences prefs)
    {
        ArgumentNullException.ThrowIfNull(prefs);
        prefs = prefs.Normalize() with { Mode = Enum.IsDefined(prefs.Mode) ? prefs.Mode : GalleryPlaybackMode.Manual,
            ImageSeconds = Math.Clamp(prefs.ImageSeconds, 1, 300) };
        if (prefs == _playbackPreferences) return;
        bool modeChanged = _playbackPreferences.Mode != prefs.Mode;
        bool durationChanged = _playbackPreferences.ImageSeconds != prefs.ImageSeconds;
        if (modeChanged) { PauseSlideshow(pauseVideo: false); _slideshowStarted = false; } // Switching automation mode is not a native Pause command.
        bool enablingEdgeControls = prefs.OverlayControls && prefs.AutoHideControls
            && (!_playbackPreferences.OverlayControls || !_playbackPreferences.AutoHideControls);
        _playbackPreferences = prefs;
        if (enablingEdgeControls) _overlayHidden = true;
        if (modeChanged) _slideshow.SetMode(prefs.Mode);
        if (durationChanged) _slideshow.SetImageDuration(TimeSpan.FromSeconds(prefs.ImageSeconds));
        _video?.SetLoopEnabled(prefs.Mode == GalleryPlaybackMode.Repeat);
        SetMediaFocus(prefs.MediaFocus, notify: false);
        ApplyOverlayPlacement();
        RefreshPlaybackControls();
        // Applying a saved mode is not a Start request, including after focus returns.
    }

    internal void SetInteractionAvailable(bool available)
    {
        if (_interactionAvailable == available) return;
        _interactionAvailable = available;
        UpdateGifPresentation();
        if (!available)
        {
            HoldSlideshowAdvance();
            _pressedPointers.Clear();
            RevealOverlayControls();
        }
        RefreshPlaybackControls();
    }

    // Config controls selected-video playback, never progression. A running slideshow is a
    // separate explicit request to play its selected videos, even when normal autoplay is off.
    private bool EffectiveVideoAutoplay => GalleryPlaybackPolicy.ShouldPlaySelection(_videoAutoplay, _slideshow.IsRunning, _selectionVideoPaused);

    private void InitializePlaybackControls(WrapPanel toolbar, StackPanel focusTools)
    {
        _slideshow = new GallerySlideshowController(RequestSlideshowNext);
        _slideshow.SetMode(_playbackPreferences.Mode);
        _slideshow.SetImageDuration(TimeSpan.FromSeconds(_playbackPreferences.ImageSeconds));
        StackPanel playback = new() { Name = "GalleryPlaybackTools", Classes = { "workspace-tool-group" } };
        playback.Children.Add(_playbackMode);
        toolbar.Children.Add(playback);
        _secondaryTools.Add(playback);
        // Pause remains reachable even when the optional secondary tools are collapsed.
        focusTools.Children.Add(_slideshowToggle);
        // Independent wrap items also fit the existing narrow window sizes in German.
        foreach (ToggleButton toggle in new[] { _overlayToggle, _postOverlayToggle })
        { toolbar.Children.Add(toggle); _secondaryTools.Add(toggle); }
        _playbackMode.DropDownOpened += (_, _) => { HoldSlideshowAdvance(); RefreshPlaybackControls(); };
        _playbackMode.DropDownClosed += (_, _) => RefreshPlaybackControls();
        _playbackMode.SelectionChanged += (_, _) =>
        {
            if (!_updatingPlaybackControls && _playbackMode.SelectedIndex is >= 0 and <= 2)
                ChangePlaybackPreferences(_playbackPreferences with { Mode = (GalleryPlaybackMode)_playbackMode.SelectedIndex });
        };
        foreach (var (button, id) in PlaybackActions())
        {
            CommandBindings.BindExisting(button, id);
            button.Click += async (_, _) => await ExecutePlaybackActionAsync(id);
        }
        _interactionTimer.Tick += (_, _) => OnViewerInteractionTick();
        RefreshPlaybackControls();
    }

    private (ToggleButton Button, string Id)[] PlaybackActions() => new[]
    {
        (_slideshowToggle, "gallery.slideshow-toggle"), (_overlayToggle, "gallery.overlay-toggle"),
        (_autoHideToggle, "gallery.overlay-autohide-toggle"), (_postOverlayToggle, "gallery.post-overlay-toggle")
    };

    internal bool CanExecutePlaybackAction(string id) => !_viewerInteractionStopped && id switch
    {
        "gallery.slideshow-toggle" => _slideshow.IsRunning || CanRunSlideshow && !_navigationPending,
        "gallery.overlay-toggle" or "gallery.overlay-autohide-toggle" or "gallery.post-overlay-toggle" => true,
        _ => false
    };

    internal async Task ExecutePlaybackActionAsync(string id)
    {
        if (!CanExecutePlaybackAction(id)) { RefreshPlaybackControls(); return; }
        switch (id)
        {
            case "gallery.slideshow-toggle":
                if (_slideshow.IsRunning) PauseSlideshow(pauseVideo: true);
                else
                {
                    if (_playbackPreferences.Mode != GalleryPlaybackMode.Slideshow)
                        ChangePlaybackPreferences(_playbackPreferences with { Mode = GalleryPlaybackMode.Slideshow });
                    StartSlideshowNavigation();
                    if (_slideshow.IsRunning && _gif != null) await _gif.PlayAsync();
                    if (_slideshow.IsRunning && _entry?.Kind == SavedMediaKind.Video && _video != null && _videoReadyRequest == _request)
                        await _video.PlayForSlideshowAsync();
                }
                break;
            case "gallery.overlay-toggle":
            case "gallery.overlay-autohide-toggle": // Compatibility alias, not a second visible setting.
                ChangePlaybackPreferences(_playbackPreferences.WithControlsOverlay(!_playbackPreferences.OverlayControls)); break;
            case "gallery.post-overlay-toggle": ChangePlaybackPreferences(_playbackPreferences with { OverlayPostContext = !_playbackPreferences.OverlayPostContext }); break;
        }
        RefreshPlaybackControls();
    }

    private SlideshowStatus CurrentSlideshowStatus => new SlideshowStatusInput(
        _playbackPreferences.Mode == GalleryPlaybackMode.Slideshow, _slideshow.IsRunning, _slideshowStarted, _slideshow.IsTerminal,
        _entry != null && _entry.Kind != SavedMediaKind.Other, _mediaFailed,
        _navigationPending || _entry != null && (_entry.Kind == SavedMediaKind.Video ? _videoReadyRequest != _request : _imageReadyRequest != _request),
        _contextOpen, _helpOpen, HasGalleryPopup || HasGalleryDialog,
        _interactionAvailable && IsGalleryWindowActive && !_videoSuspended, false).Resolve();

    private string SlideshowStatusText(SlideshowStatus state)
    {
        static string T(string en, string de) => GalleryViewerText.Get(en, de);
        return state switch
        {
            SlideshowStatus.Ready => T("Ready — start slideshow explicitly", "Bereit — Diashow bewusst starten"),
            SlideshowStatus.Running => T("Slideshow running", "Diashow läuft"),
            SlideshowStatus.Loading => T("Waiting for the medium to load", "Wartet auf das Laden des Mediums"),
            SlideshowStatus.Context => T("Paused — close post context to continue", "Pausiert — Beitragskontext zum Fortsetzen schließen"),
            SlideshowStatus.Help => T("Paused — close help to continue", "Pausiert — Hilfe zum Fortsetzen schließen"),
            SlideshowStatus.Menu => T("Paused — close the menu or dialog", "Pausiert — Menü oder Dialog schließen"),
            SlideshowStatus.Inactive => T("Paused — gallery is not active", "Pausiert — Galerie ist nicht aktiv"),
            SlideshowStatus.Ended => T("End of this sequence", "Ende dieser Medienfolge"),
            SlideshowStatus.NoMedia => T("No playable medium selected", "Kein abspielbares Medium ausgewählt"),
            SlideshowStatus.Failed => T("Playback failed — inspect the media error", "Wiedergabe fehlgeschlagen — Medienfehler prüfen"),
            SlideshowStatus.SeekPaused => T("Paused after seeking — resume explicitly", "Nach Spulen pausiert — bewusst fortsetzen"),
            SlideshowStatus.Paused => T("Paused — resume explicitly", "Pausiert — bewusst fortsetzen"),
            _ => string.Empty
        };
    }

    private void RefreshSlideshowStatus()
    {
        _shownSlideshowStatus = CurrentSlideshowStatus;
        string text = _playbackPreferences.Mode == GalleryPlaybackMode.Slideshow ? SlideshowStatusText(_shownSlideshowStatus) : string.Empty;
        if (_slideshowStatus.Text != text) _slideshowStatus.Text = text;
        _slideshowStatus.IsVisible = text.Length > 0;
    }

    private void ChangePlaybackPreferences(GalleryPlaybackPreferences prefs)
    {
        prefs = prefs.Normalize();
        if (_updatingPlaybackControls || prefs == _playbackPreferences) return;
        ConfigurePlayback(prefs);
        RevealOverlayControls(); // Explicit user choice, not incidental pointer movement or media change.
        PlaybackPreferencesChanged?.Invoke(_playbackPreferences);
    }

    private void RefreshPlaybackControls()
    {
        if (_slideshow == null) return;
        _updatingPlaybackControls = true;
        try
        {
            string T(string en, string de) => GalleryViewerText.Get(en, de);
            if (_playbackLabelsLanguage != Texts.Language)
            {
                _playbackLabelsLanguage = Texts.Language;
                _playbackMode.ItemsSource = new[] { T("Manual", "Manuell"), T("Repeat video", "Video wiederholen"), T("Slideshow", "Diashow") };
            }
            if (_playbackMode.SelectedIndex != (int)_playbackPreferences.Mode)
                _playbackMode.SelectedIndex = (int)_playbackPreferences.Mode;
            AutomationProperties.SetName(_playbackMode, T("Playback mode", "Wiedergabemodus"));
            ToolTip.SetTip(_playbackMode, T("Choosing Slideshow does not start it. Use Start slideshow.", "Diashow auswählen startet sie nicht. Mit Diashow starten beginnen."));
            SetToggleLabel(_slideshowToggle, _slideshow.IsRunning ? T("Pause slideshow", "Diashow pausieren") : T("Start slideshow", "Diashow starten"));
            _slideshowToggle.IsChecked = _slideshow.IsRunning;
            _slideshowToggle.IsEnabled = _slideshow.IsRunning || CanRunSlideshow && !_navigationPending;
            SetToggleLabel(_overlayToggle, T("Auto-hide controls overlay", "Controls-Overlay automatisch ausblenden"));
            SetToggleLabel(_autoHideToggle, T("Auto-hide", "Auto-Hide"));
            ToolTip.SetTip(_autoHideToggle, T("Reveal controls at the bottom edge; leave the panel to hide it. Keyboard access remains available.",
                "Controls am unteren Rand einblenden; beim Verlassen einklappen. Tastaturzugriff bleibt verfügbar."));
            SetToggleLabel(_postOverlayToggle, T("Post overlay", "Beitrags-Overlay"));
            _overlayToggle.IsChecked = _playbackPreferences.OverlayControls;
            _autoHideToggle.IsChecked = _playbackPreferences.AutoHideControls;
            _autoHideToggle.IsEnabled = _playbackPreferences.OverlayControls && !_nativeOverlayFallback;
            _postOverlayToggle.IsChecked = _playbackPreferences.OverlayPostContext;
            _overlayFallbackNotice.Text = T("Overlay unavailable for this native video surface. Controls remain docked.",
                "Overlay für diese native Videofläche nicht verfügbar. Controls bleiben angedockt.");
            foreach (var (button, id) in PlaybackActions())
            {
                button.IsEnabled = CanExecutePlaybackAction(id);
                string? reason = id == "gallery.slideshow-toggle" && !button.IsEnabled ? SlideshowStatusText(CurrentSlideshowStatus)
                    : id == "gallery.overlay-autohide-toggle" && !button.IsEnabled
                        ? T("Enable a supported controls overlay first", "Zuerst ein unterstütztes Controls-Overlay aktivieren") : null;
                string label = button.Content?.ToString() ?? CommandCatalog.Label(id);
                string tip = CommandBindings.Tooltip(id, reason, label);
                ToolTip.SetTip(button, tip); AutomationProperties.SetHelpText(button, tip);
            }
            RefreshSlideshowStatus();
            UpdateMediaFocusVisibility();
            _lastRunning = _slideshow.IsRunning;
        }
        finally { _updatingPlaybackControls = false; }
        UpdateViewerInteractionTimer();
    }

    private static void SetToggleLabel(ToggleButton button, string label)
    {
        button.Content = label;
        AutomationProperties.SetName(button, label);
        ToolTip.SetTip(button, label);
    }

    private void OnVideoLoopChanged(bool enabled) => ChangePlaybackPreferences(_playbackPreferences with
    { Mode = enabled ? GalleryPlaybackMode.Repeat : _playbackPreferences.Mode == GalleryPlaybackMode.Repeat
        ? GalleryPlaybackMode.Manual : _playbackPreferences.Mode });

    private void BeginSlideshowMedia(GalleryEntry entry)
    {
        UnbindVideoPlaybackSignals();
        _navigationPending = false; _mediaFailed = false; _selectionVideoPaused = false;
        _imageReadyRequest = _videoReadyRequest = -1;
        _slideshow.BeginMedia(++_slideshowGeneration, entry.Kind == SavedMediaKind.Video, _next.IsEnabled);
        // Readiness may arrive before the next interaction tick while a panel is open.
        // Start held so no hidden/loading interval is charged to the next image.
        _slideshow.SetAdvanceHeld(_slideshowGeneration, true);
        if (entry.Kind == SavedMediaKind.Other) StopSlideshow();
        RefreshPlaybackControls();
    }

    private void MarkVisibleImageReady()
    {
        if (_imageReadyRequest == _request || _info == null || _image.Source == null || !_image.IsEffectivelyVisible
            || _entry?.Kind == SavedMediaKind.Video || _navigationPending || _sessionCancellation.IsCancellationRequested) return;
        _imageReadyRequest = _request;
        _slideshow.ImageReady(_slideshowGeneration);
        RefreshPlaybackControls();
    }

    private void UpdateSlideshowAvailability(bool hasNext)
    {
        if (_slideshow == null || _navigationPending || _entry == null) return;
        // Availability changes do not re-arm an ended medium or restart its image clock.
        _slideshow.UpdateHasNext(_slideshowGeneration, hasNext);
    }

    private void BindVideoPlaybackSignals(int request)
    {
        if (_video == null) return;
        UnbindVideoPlaybackSignals();
        long generation = _slideshowGeneration;
        bool Current() => request == _request && generation == _slideshowGeneration;
        _videoEndedHandler = () =>
        {
            if (!Current() || _entry?.Kind != SavedMediaKind.Video || _navigationPending) return;
            _slideshow.SetAdvanceHeld(generation, IsSlideshowAdvanceBlocked);
            _slideshow.VideoEnded(generation);
            RefreshPlaybackControls();
        };
        _videoPlaybackHandler = playing =>
        {
            if (!Current() || _playbackPreferences.Mode != GalleryPlaybackMode.Slideshow) return;
            if (playing)
            {
                _selectionVideoPaused = false;
                if (CanRunSlideshow && !_navigationPending && !_slideshow.IsRunning) StartSlideshowNavigation();
            }
            else PauseSlideshow(pauseVideo: false);
            RefreshPlaybackControls();
        };
        _videoSeekHandler = () =>
        {
            if (!Current()) return;
            _slideshow.SeekStarted(generation);
            HoldSlideshowAdvance();
            RefreshPlaybackControls();
        };
        _videoFailedHandler = () =>
        {
            if (!Current()) return;
            _mediaFailed = true;
            StopSlideshow();
        };
        _video.PlaybackEnded += _videoEndedHandler;
        _video.UserPlaybackRequested += _videoPlaybackHandler;
        _video.SeekInteractionStarted += _videoSeekHandler;
        _video.PlaybackFailed += _videoFailedHandler;
        _videoNoticeHandler = message =>
        {
            if (Current()) ReportDiagnostic("video", message, mediaUnavailable: true);
        };
        _video.DiagnosticNotice += _videoNoticeHandler;
    }

    private void UnbindVideoPlaybackSignals()
    {
        if (_video == null) return;
        if (_videoEndedHandler != null) _video.PlaybackEnded -= _videoEndedHandler;
        if (_videoPlaybackHandler != null) _video.UserPlaybackRequested -= _videoPlaybackHandler;
        if (_videoSeekHandler != null) _video.SeekInteractionStarted -= _videoSeekHandler;
        if (_videoFailedHandler != null) _video.PlaybackFailed -= _videoFailedHandler;
        if (_videoNoticeHandler != null) _video.DiagnosticNotice -= _videoNoticeHandler;
        _videoEndedHandler = _videoSeekHandler = _videoFailedHandler = null; _videoNoticeHandler = null; _videoPlaybackHandler = null;
    }

    private void StartSlideshowNavigation()
    {
        _selectionVideoPaused = false;
        _slideshowStarted = true;
        _slideshow.SetAdvanceHeld(_slideshowGeneration, IsSlideshowAdvanceBlocked);
        _slideshow.Start();
        if (_slideshow.IsRunning || _entry == null || _navigationPending) return;
        // A completed/consumed medium may be replayed only by this explicit user
        // request. Re-arm with a new identity, not a duplicate old Ended callback.
        _slideshow.BeginMedia(++_slideshowGeneration, _entry.Kind == SavedMediaKind.Video, _next.IsEnabled);
        if (_imageReadyRequest == _request) _slideshow.ImageReady(_slideshowGeneration);
        if (_entry.Kind == SavedMediaKind.Video) BindVideoPlaybackSignals(_request);
        _slideshow.SetAdvanceHeld(_slideshowGeneration, IsSlideshowAdvanceBlocked);
        _slideshow.Start();
    }

    private void RequestSlideshowNext()
    {
        if (!CanRunSlideshow || _navigationPending || !_next.IsEnabled) { StopSlideshow(); return; }
        Move(1);
    }

    // UI interruption API shared with the overlay partial. These methods never issue a native
    // transport command. IsRunning retains explicit progression intent during temporary holds.
    private bool IsSlideshowAdvanceBlocked => !CanRunSlideshow || _navigationPending || _dragging
        || _pressedPointers.Count != 0 || _lastPointer?.Captured != null
        || _video?.IsSeekInteracting == true || _keyboardInteraction && _infoOverlay.IsKeyboardFocusWithin;

    private void HoldSlideshowAdvance()
    {
        _slideshow?.SetAdvanceHeld(_slideshowGeneration, true);
        UpdateViewerInteractionTimer();
    }

    private void UpdateSlideshowInteraction(bool blocked)
    {
        if (_slideshow == null) return;
        _slideshow.SetAdvanceHeld(_slideshowGeneration, blocked || IsSlideshowAdvanceBlocked);
        _slideshow.Tick();
    }

    private void PauseSlideshow(bool pauseVideo = false)
    {
        if (_slideshow == null) return;
        bool wasRunning = _slideshow.IsRunning;
        _slideshow.Pause();
        if (wasRunning && pauseVideo && _entry?.Kind == SavedMediaKind.Video)
        {
            // The explicit pause may precede ShowAsync admission while the previous input
            // drains. Carry it into this selection's initial policy, not the next selection.
            _selectionVideoPaused = true;
            if (_video != null) _ = _video.PauseAsync();
        }
        if (wasRunning) RefreshPlaybackControls();
    }

    private void StopSlideshow()
    {
        _slideshow?.Stop();
        RefreshPlaybackControls();
    }

    private void UpdateViewerInteractionTimer()
    {
        // A manual, docked gallery needs no periodic whole-window popup/focus scan.
        // Optional idle hiding and a running/pending slideshow own the single timer.
        bool waitingForImage = _playbackPreferences.Mode == GalleryPlaybackMode.Slideshow
            && !_mediaFailed && _entry != null && _entry.Kind != SavedMediaKind.Video
            && _entry.Kind != SavedMediaKind.Other && _imageReadyRequest != _request;
        bool hiding = OverlayControlsEffective && _playbackPreferences.AutoHideControls
            && !_overlayHidden && _interactionAvailable && IsGalleryWindowActive;
        bool needed = !_viewerInteractionStopped && _scaleOwner != null && _slideshow != null && (hiding || _slideshow.IsRunning || waitingForImage || _hoverContextOpen);
        if (needed && !_interactionTimer.IsEnabled) _interactionTimer.Start();
        else if (!needed && _interactionTimer.IsEnabled) _interactionTimer.Stop();
    }

    private void StopViewerInteraction()
    {
        _viewerInteractionStopped = true;
        _gif?.SetActive(false);
        _interactionTimer.Stop();
        PauseSlideshow();
        StopSlideshow();
        _pressedPointers.Clear();
        _lastPointer = null;
        _lastIndicatorMotion = null;
        _lastPointerScreen = null; _controlsHot = _postHot = _pointerInControls = _pointerInPost = false;
        if (_hoverContextOpen) SetContextOpen(false, notify: false, hover: true);
        _revealPointerId = null; _swallowRevealTap = false;
        RevealOverlayControls();
    }
}

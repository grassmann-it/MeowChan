using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LibVLCSharp.Avalonia;
using LibVLCSharp.Shared;
using MeowChan.Desktop.Commands;
using MeowChan.Gallery;
using MeowChan.Diagnostics;
using Texts = MeowChan.Desktop.Localization.Texts;

namespace MeowChan.Desktop;

/// <summary>
/// Owns one local, verified media handle. Native calls are serialized off the UI thread;
/// the video surface and the Avalonia transport toolbar occupy separate rows.
/// </summary>
internal sealed partial class GalleryVideoPlayer : UserControl, IAsyncDisposable
{
    private readonly VideoView _video = new() { Name = "GalleryNativeVideo", MinHeight = 100 };
    private readonly Image _softwareVideo = new() { Name = "GallerySoftwareVideo", MinHeight = 100, Stretch = Avalonia.Media.Stretch.Uniform };
    // Status and time belong to the themed transport below the black media viewport.
    private readonly TextBlock _status = new TextBlock { Name = "GalleryVideoStatus", Margin = new Thickness(0, 4, 0, 0), TextWrapping = Avalonia.Media.TextWrapping.Wrap, [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Text") }.MetaText();
    private readonly TextBlock _time = new TextBlock { Name = "GalleryVideoTime", MinWidth = 76, TextAlignment = Avalonia.Media.TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Muted") }.MetaText();
    private readonly Button _playPause = new() { Name = "GalleryVideoPlayPause", Classes = { "media-control", "media-play" }, Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Padding = new Thickness(4) }; // GL5b: 32 like every other control of the bar (was 40)
    private readonly ToggleButton _mute = new() { Name = "GalleryVideoMute", Classes = { "media-control" }, Width = 32, Height = 32, Padding = new Thickness(8) };
    private readonly ToggleButton _loop = new() { Name = "GalleryVideoLoop", Classes = { "media-control" }, Width = 32, Height = 32, Padding = new Thickness(8) };
    // Playback speed: one click steps through Rates, the speed keys (video.speed-down / video.speed-up, [ ] and X C by default) step down/up, Backspace returns to 1×, a right-click offers
    // 0.25×–4× (VideoRates.Choices). Every medium starts at 1×.
    private readonly Button _speed = new() { Name = "GalleryVideoSpeed", Classes = { "media-control" }, MinWidth = 44, Height = 32,
        Padding = new Thickness(8, 0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly GalleryTransportSlider _seek = new() { Name = "GalleryVideoSeek", Classes = { "media-slider" }, Minimum = 0, Maximum = 1000, MinWidth = 80, Height = 32, VerticalAlignment = VerticalAlignment.Center };
    private readonly GalleryVolumeSlider _volume = new() { Name = "GalleryVideoVolume", Classes = { "media-slider" }, Minimum = 0, Maximum = 100, Value = 70, Width = 80, Height = 32, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _root = new() { RowDefinitions = new RowDefinitions("*,Auto") };
    private readonly Border _transportPanel = new() { Name = "GalleryVideoTransport", Classes = { "workspace-media-transport" } };
    private Panel? _externalTransportHost;
    private bool _transportPlacementRetired;
    private Window? _transportWindow;
    private static bool UsesSoftwareVideo => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly VideoPlaybackLifetime _lifetime;
    private GCHandle _failedCleanupRoot;
    private Stream? _failedStream;
    private LibVLC? _engine;
    private Playback? _playback;
    private long _generation => _lifetime.Generation;
    private long _length;
    private bool _updatingSeek;
    private bool IsDisposed => _lifetime.IsDisposing;
    private int _releasedStreams;
    private int _cleanupFailures;
    private bool _autoplay = true;
    private bool _settingVolume;
    private bool _isPlaying;
    private bool _publishingUserTransport;
    private bool _ended;
    private readonly VideoPlaybackIntent _intent = new();
    private readonly VideoVolumeIntent _volumeIntent = new();
    private readonly VideoMuteIntent _muteIntent = new();
    private readonly VideoTransportIntent _transportIntent = new();
    // Accessed only under _operations, unlike UI input revisions.
    private long _appliedVolumeRevision;
    private readonly VideoTimelineInteraction _timeline = new();
    private readonly VideoLatestSeekQueue _seeks;
    private Window? _ownerWindow;
    private IPointer? _seekPointer;
    private int _loopRestarts;
    internal static readonly float[] Rates = VideoRates.Cycle;
    private float _rate = VideoRates.Normal;
    private bool _repeatOwnedBySlideshow;
    private string? _repeatOwnedReason;
    private bool _canSeek;
    private bool _knownSeekable;
    private bool _audioAvailable;
    private int _audioTracks = -1;
    private int _audioRefreshPending;
    private int _unrenderedAudioProbes;
    private bool _audioTrackDisableAttemptedForUnavailableOutput;
    // Time ticks re-probe audio only until the current play epoch has settled it.
    private readonly VideoAudioProbeSchedule _audioProbe = new();

    internal event Action<int>? VolumeChanged;
    /// <summary>Publishes the verified media length for the gallery filmstrip; null retires the previous medium's value.</summary>
    internal event Action<TimeSpan?>? DurationChanged;
    internal event Action<bool>? LoopChanged;
    internal event Action? PlaybackEnded;
    internal event Action? PlaybackFailed;
    internal event Action<string>? DiagnosticNotice;
    internal event Action<string>? CleanupDiagnostic;
    internal event Action<bool>? UserPlaybackRequested;
    internal event Action? SeekInteractionStarted;
    internal bool LoopEnabled => _intent.LoopEnabled;
    /// <summary>The playback speed chosen for the current medium (1 after every new medium).</summary>
    internal float Rate => _rate;
    /// <summary>The shown position in milliseconds (a pending seek's target while it runs).</summary>
    internal long PositionMs => _timeline.DisplayPosition;
    /// <summary>Whether the transport can seek now (a seekable medium with a known length).</summary>
    internal bool CanSeekNow => !IsDisposed && !_lifetime.HasCleanupPending && _seek.IsEnabled && _canSeek && _length > 0;
    internal bool IsSeekInteracting => _timeline.HasGesture || _timeline.HasPendingSeek;
    internal VideoPlaybackMode PlaybackMode => _intent.Current.Mode;
    internal Control TransportControl => _transportPanel;

    // UI-thread API. A dedicated external host owns visibility/auto-hide and routing; it
    // receives the existing transport, never a second set of controls or a native drawable.
    internal void SetExternalTransportHost(Panel? host)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (IsDisposed || _transportPlacementRetired) { RetireTransportPlacement(); return; }
        if (ReferenceEquals(host, _root)) host = null;
        if (host != null && host.GetLogicalAncestors().Contains(_transportPanel))
        {
            throw new ArgumentException("The transport cannot be hosted inside itself.", nameof(host));
        }
        // Repeated layout/refresh placement must not interrupt an active seek.
        // Still submit Move below so an outstanding transfer receives the latest target.
        if (!ReferenceEquals(host, _externalTransportHost)) CancelSeekGesture();
        _externalTransportHost = host;
        // GL5b: a host that marks itself "in-dock" (the docked dock) gives the bar the dock's own 4 dip of height (WorkspaceStyles .in-dock).
        _transportPanel.Classes.Set("in-dock", host != null && host.Classes.Contains("in-dock"));
        Grid.SetRow(_transportPanel, host == null ? 1 : 0);
        Grid.SetRowSpan(SurfaceControl, host == null ? 1 : 2);
        ApplyVolumePlacement();
        // Always submit the latest desired host, even if it is the actual parent:
        // an earlier cross-root request may still be pending. The transfer owns removal.
        GalleryControlTransfer.Move(_transportPanel, host ?? _root,
            () => !_transportPlacementRetired && !IsDisposed);
    }

    // Close placement admission before native disposal (or the viewer's first await).
    // Temporary viewer detachment must not call this permanent cancellation path.
    internal void RetireTransportPlacement()
    {
        Dispatcher.UIThread.VerifyAccess();
        _transportPlacementRetired = true;
        GalleryControlTransfer.Cancel(_transportPanel);
        _externalTransportHost = null;
    }

    private void DetachTransport() => RetireTransportPlacement();

    // May be applied before media is ready; unlike a user toggle this has no enabled-control
    // prerequisite. Setting an unchanged value is always silent, preventing preference echoes.
    internal void SetLoopEnabled(bool enabled, bool notify = false)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (IsDisposed) return;
        if (_intent.SetLoopEnabled(enabled, notify) && !notify) RefreshTransportPresentation();
    }

    // Query after layout so wrapped transport controls, DPI and theme spacing
    // are reflected without duplicating their dimensions in the viewer.
    internal double TransportBottomInset
    {
        get
        {
            if (_externalTransportHost != null) return 0;
            Control surface = SurfaceControl;
            Point? bottom = surface.TranslatePoint(new Point(0, surface.Bounds.Height), this);
            return bottom.HasValue ? Math.Max(0, Bounds.Height - bottom.Value.Y) : 0;
        }
    }

    // Native video occupies its own OS surface. VideoView.Content hosts chrome
    // in LibVLCSharp's owned overlay window above that surface. Install the
    // initial overlay before attaching this player to the visual tree, since
    // VideoView creates that window during attachment. macOS and Linux use an
    // Avalonia Image and keep chrome in the viewer's ordinary visual tree.
    internal void SetNativeOverlay(Control? overlay)
    {
        if (OperatingSystem.IsWindows())
        {
            _video.Content = overlay;
            SynchronizeNativeOverlaySurfaceVisibility();
        }
    }

    private void SynchronizeNativeOverlaySurfaceVisibility() => SynchronizeNativeOverlaySurfaceVisibility(
        _video, UsesSoftwareVideo, IsDisposed, _video.Content != null, IsVisible);

    internal static void SynchronizeNativeOverlaySurfaceVisibility(Control surface, bool softwareVideo,
        bool disposing, bool hasOverlay, bool playerVisible)
    {
        // VideoView 3.10.2 observes its OWN IsVisible to hide/show its floating
        // window, not an ancestor's effective visibility. Keep that notification
        // in sync when our player is hidden for an image. Content=null is the
        // native-transparency fallback: do not hide its usable video surface.
        if (!softwareVideo && !disposing && hasOverlay) surface.IsVisible = playerVisible;
    }

    internal void Configure(bool autoplay, bool startMuted, int volume)
    {
        _autoplay = autoplay;
        _muteIntent.ConfigureStartup(startMuted);
        ConfigureVolume(volume);
    }

    /// <summary>A new volume from the preferences while the player lives: the slider and the playing video follow, nothing is persisted and mute is not touched.</summary>
    internal void ConfigureVolume(int volume)
    {
        int nextVolume = Math.Clamp(volume, 0, 100);
        bool changed = (int)_volume.Value != nextVolume;
        _settingVolume = true;
        try { _volume.Value = nextVolume; }
        finally { _settingVolume = false; }
        if (changed) _ = SetVolumeAsync(nextVolume, userInitiated: false);
    }

    internal Task TogglePlaybackAsync() => SetPlaybackModeAsync(
        !_ended && _intent.Current.Mode == VideoPlaybackMode.Playing ? VideoPlaybackMode.Paused : VideoPlaybackMode.Playing);

    // Compatibility commands remain routable without keeping separate visible buttons.
    private bool PlayCurrent(Playback playback, Func<bool> permitted)
    {
        if (!IsCurrent(playback.Generation) || !permitted()) return false;
        MediaPlayer player = playback.Player;
        if (player.State == VLCState.Ended)
        {
            DisableUnrenderedAudioOutput(playback);
            player.Stop();
        }
        if (!IsCurrent(playback.Generation) || !permitted()) return false;
        if (!player.Play(playback.Media)) throw new InvalidOperationException("The video backend rejected playback.");
        return true;
    }
    internal Task PlayAsync() => SetPlaybackModeAsync(VideoPlaybackMode.Playing);
    internal Task PauseAsync() => SetPlaybackModeAsync(VideoPlaybackMode.Paused);

    /// <summary>The video is playing now (not paused, stopped or at its end).</summary>
    internal bool IsPlayingNow => !_ended && _intent.Current.Mode == VideoPlaybackMode.Playing;

    internal Task StopPlaybackAsync() => SetPlaybackModeAsync(VideoPlaybackMode.Stopped);

    // Slideshow uses a silent, idempotent command: reflecting a user Play must not publish
    // another user request or issue a duplicate native Play for an autoplayed selection.
    internal Task PlayForSlideshowAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        return !_ended && _intent.Current.Mode == VideoPlaybackMode.Playing
            ? Task.CompletedTask : SetPlaybackModeAsync(VideoPlaybackMode.Playing, notify: false);
    }

    private Task SetPlaybackModeAsync(VideoPlaybackMode mode, bool notify = true)
    {
        // Viewer observers manipulate UI state. Keep notification delivery synchronous on the
        // UI thread, including when an existing asynchronous command caller starts off-thread.
        if (!Dispatcher.UIThread.CheckAccess()) return RequestPlaybackOnUiAsync(mode, notify);
        // Publish before waiting for native operations: a pending repeat Stop may be blocked.
        long generation = _generation;
        if (IsDisposed || !IsCurrent(generation) || notify && _publishingUserTransport) return Task.CompletedTask;
        VideoPlaybackIntentStamp command = _transportIntent.Request(generation, mode);
        VideoPlaybackIntentStamp? requested;
        if (notify)
        {
            _publishingUserTransport = true;
            try { requested = _intent.RequestUserPlayback(generation, mode); }
            finally { _publishingUserTransport = false; }
        }
        else requested = _intent.Set(generation, mode);
        if (requested == null) return Task.CompletedTask;
        if (mode == VideoPlaybackMode.Stopped)
        {
            CancelSeekGesture();
            CancelScrubPreviewRequest();
            _ended = false;
        }
        return CommandAsync(playback =>
        {
            if (!_transportIntent.IsCurrent(command)) return;
            MediaPlayer player = playback.Player;
            if (mode == VideoPlaybackMode.Stopped)
            {
                DisableUnrenderedAudioOutput(playback);
                player.Stop();
            }
            else if (mode == VideoPlaybackMode.Paused) player.SetPause(true);
            else if (player.State == VLCState.Paused) player.SetPause(false);
            else if (!player.IsPlaying) PlayCurrent(playback, () => _transportIntent.IsCurrent(command));
        });
    }
    private async Task RequestPlaybackOnUiAsync(VideoPlaybackMode mode, bool notify)
        => await Dispatcher.UIThread.InvokeAsync(() => SetPlaybackModeAsync(mode, notify)).ConfigureAwait(false);

    internal Task ToggleMuteAsync()
    {
        if (!Dispatcher.UIThread.CheckAccess()) return ToggleMuteOnUiAsync();
        if (IsDisposed || !IsCurrent(_generation) || !_audioAvailable || _lifetime.HasCleanupPending)
            return Task.CompletedTask;
        // Accept intent before waiting for a native operation. Navigation may retire this
        // playback before the command executes; the next selection still inherits the choice.
        _muteIntent.Toggle();
        RefreshTransportPresentation();
        return CommandAsync(playback =>
        {
            if (HasUsableAudio(playback)) ApplyMute(playback.Player);
        });
    }
    private async Task ToggleMuteOnUiAsync()
        => await Dispatcher.UIThread.InvokeAsync(() => ToggleMuteAsync()).ConfigureAwait(false);

    internal void ToggleLoop()
    {
        // The visible transport stays disabled while slideshow automation owns
        // repetition, but the explicit keyboard/command intent is still allowed
        // to hand the current video back to the player.
        if (IsDisposed || !_loop.IsEnabled && !_repeatOwnedBySlideshow) return;
        SetLoopEnabled(!LoopEnabled, notify: true);
    }

    /// <summary>While a slideshow runs, its video rule (not the loop button) decides about repetition: the button is then
    /// unavailable and its tooltip says why. An explicit keyboard/command Repeat intent may still take over.</summary>
    internal void SetRepeatOwnedBySlideshow(bool owned, string reason)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_repeatOwnedBySlideshow == owned && _repeatOwnedReason == reason) return;
        _repeatOwnedBySlideshow = owned; _repeatOwnedReason = reason;
        _loop.IsEnabled = _playPause.IsEnabled && !owned;
        // The A–B loop is a repeat too: a running slideshow's video rule owns repetition, so the loop ends and waits.
        if (owned) ClearAbLoop();
        RefreshTransportPresentation();
    }

    /// <summary>Seeks by <paramref name="delta"/> from the shown position, clamped to the medium, through the same latest-seek
    /// path as the timeline (so the slideshow treats it as a seek and not as a natural end).</summary>
    internal bool SeekRelative(TimeSpan delta)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!CanSeekNow) return false;
        long target = Math.Clamp(_timeline.DisplayPosition + (long)delta.TotalMilliseconds, 0, _length);
        double value = Math.Clamp(target * 1000d / _length, _seek.Minimum, _seek.Maximum);
        if (Math.Abs(value - _seek.Value) < 1e-9) return false;
        _seek.Value = value;
        return true;
    }

    /// <summary>Steps the speed one entry of <see cref="Rates"/> down (negative) or up (positive); 0 returns to 1×.</summary>
    internal void StepRate(int direction)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (IsDisposed || !_speed.IsEnabled) return;
        SetRate(VideoRates.Step(_rate, direction));
    }

    private void CycleRate()
    {
        if (IsDisposed || !_speed.IsEnabled) return;
        SetRate(VideoRates.Next(_rate));
    }

    /// <summary>Chooses any speed of <see cref="VideoRates.Choices"/> (the right-click menu and the player menu).</summary>
    internal void ChooseRate(float rate)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (IsDisposed || !_speed.IsEnabled) return;
        SetRate(VideoRates.Normalize(rate));
    }

    private void SetRate(float rate)
    {
        if (VideoRates.Same(rate, _rate)) return;
        _rate = rate;
        RefreshTransportPresentation();
        ApplyRate();
    }

    private void ApplyRate()
    {
        float rate = Rate;
        _ = CommandAsync(playback => playback.Player.SetRate(rate));
    }

    internal static string RateLabel(float rate) => rate.ToString("0.##", Texts.Culture) + MultiplicationSign;

    // The times sign of a rate label ("1.5×") is text next to a number, not an icon, so it is not a drawn catalog icon (P6).
    private const char MultiplicationSign = '\u00D7';

    /// <summary>
    /// Saves the frame shown now as a PNG at <paramref name="path"/> (the native snapshot of the player; on the software
    /// surface of Linux/macOS the presented bitmap when the native snapshot is refused). False when no frame is available.
    /// </summary>
    internal async Task<bool> SaveFrameAsync(string path)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (IsDisposed || _lifetime.HasCleanupPending) return false;
        bool saved = await Task.Run(async () =>
        {
            await _operations.WaitAsync().ConfigureAwait(false);
            try
            {
                Playback? playback = _playback;
                return playback != null && IsCurrent(playback.Generation) && playback.Player.TakeSnapshot(0, path, 0, 0) && File.Exists(path);
            }
            catch (Exception error) when (error is not OutOfMemoryException) { Serilog.Log.Warning(error, "The native video snapshot failed."); return false; }
            finally { _operations.Release(); }
        }).ConfigureAwait(true);
        if (saved || !UsesSoftwareVideo || _softwareVideo.Source is not Avalonia.Media.Imaging.Bitmap shown) return saved;
        // Copied here, encoded and written on a worker: a large frame never stalls the UI thread (logged on failure).
        return await GalleryFramePng.SaveAsync(shown, path).ConfigureAwait(true);
    }

    private sealed record Playback(long Generation, MediaPlayer Player, Media Media, MediaInput? Input, Stream Stream,
        VerifiedVideoFile? NativeFile, SoftwareVideoSurface? Surface, CancellationToken Token);

    // Acceptance observes the real player under its normal native-operation lock.
    // No alternate decoder, dummy video output or synthetic clock is used.
    internal sealed record PlaybackEvidence(string State, long Time, int DecodedFrames, int DisplayedFrames,
        bool HasPlayback, bool HasEngine, bool Disposed, int ReleasedStreams, int CleanupFailures,
        int SurfaceFrames = 0, bool SurfaceNonUniform = false, bool LoopEnabled = false, int LoopRestarts = 0,
        bool Muted = false, int Volume = 0, long Length = 0, bool Seekable = false,
        int AudioTracks = -1, bool AudioAvailable = false, int DecodedAudio = 0, int PlayedAudioBuffers = -1,
        long AudioTicks = 0, long AudioProbeRequests = 0, long AudioProbes = 0, long AudioProbePresentations = 0, bool AudioSettled = false);

    internal Task<PlaybackEvidence> InspectPlaybackAsync(string? snapshotPath = null) => Task.Run(async () =>
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            Playback? playback = _playback;
            if (_lifetime.HasCleanupFailure)
                return new PlaybackEvidence("CleanupFailed", 0, 0, 0, playback != null, _engine != null, IsDisposed,
                    _releasedStreams, _cleanupFailures);
            if (playback == null) return new PlaybackEvidence("Stopped", 0, 0, 0, false, _engine != null, IsDisposed,
                _releasedStreams, _cleanupFailures, LoopEnabled: _intent.LoopEnabled, LoopRestarts: _loopRestarts);
            var stats = playback.Media.Statistics;
            if (snapshotPath != null && !playback.Player.TakeSnapshot(0, snapshotPath, 0, 0))
                throw new InvalidOperationException("The native video output rejected the frame snapshot.");
            int audioTracks = playback.Player.AudioTrackCount, volume = playback.Player.Volume;
            var probes = _audioProbe.Counts;
            return new PlaybackEvidence(playback.Player.State.ToString(), playback.Player.Time,
                stats.DecodedVideo, stats.DisplayedPictures, true, _engine != null, IsDisposed,
                _releasedStreams, _cleanupFailures, playback.Surface?.PresentedFrames ?? 0, playback.Surface?.NonUniform ?? false,
                _intent.LoopEnabled, _loopRestarts, playback.Player.Mute, volume,
                playback.Player.Length, playback.Player.IsSeekable, audioTracks,
                VideoAudioAvailability.IsAvailable(audioTracks, volume, stats.PlayedAudioBuffers), stats.DecodedAudio, stats.PlayedAudioBuffers,
                probes.Ticks, probes.Requests, probes.Probes, probes.Presentations, probes.Settled);
        }
        finally { _operations.Release(); }
    });

    internal GalleryVideoPlayer()
    {
        _lifetime = new VideoPlaybackLifetime(CleanupAsync, ReleaseEngineAsync);
        InitializeScrubPreview();
        GalleryControlTransfer.Track(_transportPanel, () => !_transportPlacementRetired && !IsDisposed);
        _seeks = new VideoLatestSeekQueue(SeekAsync);
        PropertyChanged += (_, change) =>
        {
            if (change.Property == IsVisibleProperty) SynchronizeNativeOverlaySurfaceVisibility();
        };
        _intent.LoopChanged += enabled => { RefreshTransportPresentation(); LoopChanged?.Invoke(enabled); };
        _intent.UserPlaybackRequested += playing => UserPlaybackRequested?.Invoke(playing);
        _intent.PlaybackEnded += () => PlaybackEnded?.Invoke();
        _timeline.SeekInteractionStarted += () =>
        {
            // Invalidate queued terminal callbacks at gesture START, before Slider.Value moves.
            // Re-stamping the same mode never emits a transport request or pauses native video.
            _intent.Set(_generation, _intent.Current.Mode);
            SeekInteractionStarted?.Invoke();
        };
        _transportPanel.AttachedToVisualTree += (_, _) =>
        {
            Window? window = TopLevel.GetTopLevel(_transportPanel) as Window;
            _transportWindow = ReferenceEquals(window, _ownerWindow) ? null : window;
            if (_transportWindow != null) _transportWindow.Deactivated += OnWindowDeactivated;
        };
        _transportPanel.DetachedFromVisualTree += (_, _) =>
        {
            if (_transportWindow != null) _transportWindow.Deactivated -= OnWindowDeactivated;
            _transportWindow = null;
        };
        _lifetime.CleanupAccepted += () => Dispatcher.UIThread.Post(() =>
        {
            if (!_lifetime.HasCleanupPending) return;
            _isPlaying = false; _canSeek = false;
            EnableTransport(false); RefreshTransportPresentation();
        });
        _lifetime.Cleanup.Pending += evidence => Dispatcher.UIThread.Post(() =>
        {
            // A newer show owns its own status. Cleanup diagnostics never make transport usable.
            VideoCleanupEvidence current = _lifetime.Cleanup.Evidence;
            if (!current.Pending || !current.Overdue || current.Request != evidence.Request) return;
            EnableTransport(false);
            _status.Text = UnavailableText;
            // Cleanup retains ownership/admission exactly as before. The report is not
            // forwarded as a new medium's failure or used to reveal its transport.
            CleanupDiagnostic?.Invoke(Texts.T("gallery.video.cleanupPending"));
        });
        CommandBindings.BindExisting(_playPause, "video.play-pause");
        CommandBindings.BindExisting(_mute, "video.mute");
        CommandBindings.BindExisting(_loop, "video.loop");
        ToolTip.SetShowOnDisabled(_loop, true);
        foreach (Slider slider in new Slider[] { _seek, _volume })
        {
            // Retain Semi's slider input/template with a slim media track and
            // a full-height pointer/focus target.
            slider.Resources["SliderTrackWidth"] = 3d;
            slider.Resources["SliderThumbWidth"] = 12d;
            slider.Resources["SliderThumbHeight"] = 12d;
        }
        // One row: ▶ · time · seek (with the A–B marks) · speed · repeat · A·B · next frame · volume · full screen · ⋯.
        StackPanel secondary = _secondary;
        secondary.Children.Add(_speed); secondary.Children.Add(_loop); secondary.Children.Add(_abLoopButton);
        secondary.Children.Add(_frameStep); secondary.Children.Add(_volumeHost);
        secondary.Children.Add(_fullscreen); secondary.Children.Add(_playerMenuButton);
        Grid timeline = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
        timeline.Children.Add(_time);
        Grid seekTrack = new();
        seekTrack.Children.Add(_seek); seekTrack.Children.Add(_seekMarks);
        timeline.Children.Add(seekTrack); Grid.SetColumn(seekTrack, 1);
        InitializeTransportExtras();
        Grid controls = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
        controls.Children.Add(_playPause);
        controls.Children.Add(timeline); Grid.SetColumn(timeline, 1);
        controls.Children.Add(secondary); Grid.SetColumn(secondary, 2);
        // A narrow post-context pane gets two deliberate rows, never a loosely
        // wrapping button pile: the timeline above, ▶ and every other control below.
        bool compact = false;
        _transportPanel.SizeChanged += (_, _) =>
        {
            bool nextCompact = _transportPanel.Bounds.Width < CompactTransportWidth;
            if (compact == nextCompact) return;
            compact = nextCompact;
            controls.RowDefinitions = new RowDefinitions(compact ? "Auto,Auto" : "Auto");
            controls.RowSpacing = compact ? 4 : 0;
            Grid.SetRow(timeline, 0); Grid.SetColumn(timeline, compact ? 0 : 1); Grid.SetColumnSpan(timeline, compact ? 3 : 1);
            Grid.SetRow(_playPause, compact ? 1 : 0);
            Grid.SetRow(secondary, compact ? 1 : 0);
        };
        StackPanel transport = new();
        transport.Children.Add(controls);
        transport.Children.Add(_status);
        _transportPanel.Child = transport;
        // Seek, time, volume and playback form one reserved surface. Its dynamic
        // resources follow open-window theme changes without touching playback.
        InitializeViewTransform();
        _root.Children.Add(SurfaceControl);
        _root.Children.Add(_transportPanel); Grid.SetRow(_transportPanel, 1); Content = _root;
        _status.IsVisible = false;
        _status.PropertyChanged += (_, args) =>
        {
            if (args.Property == TextBlock.TextProperty)
                _status.IsVisible = !string.IsNullOrWhiteSpace(_status.Text);
        };
        EnableTransport(false);
        RefreshTransportPresentation();
        _playPause.Click += async (_, _) => await TogglePlaybackAsync();
        _mute.Click += async (_, _) => await ToggleMuteAsync();
        _loop.Click += (_, _) => ToggleLoop();
        _speed.Click += (_, _) => CycleRate();
        _volume.PropertyChanged += (_, args) =>
        {
            if (args.Property != Slider.ValueProperty || _settingVolume) return;
            // Capture the event value, not a potentially reentrant current slider value.
            int volume = (int)args.GetNewValue<double>();
            _ = SetVolumeAsync(volume, userInitiated: true);
            // Persist input in UI order, never completion order of native worker tasks.
            VolumeChanged?.Invoke(volume);
        };
        _seek.PropertyChanged += (_, args) =>
        {
            if (args.Property != Slider.ValueProperty || _updatingSeek || !_seek.IsEnabled) return;
            if (_timeline.Request(_generation, _seek.Value, _length, _canSeek) is not { } request) return;
            // Seeking supersedes an already queued terminal callback/repeat, without changing
            // whether the user wanted playback or pause.
            _intent.Set(_generation, _intent.Current.Mode);
            SeekInteractionStarted?.Invoke(); // Also protect track clicks/programmatic slider requests.
            _ended = false;
            RenderTime();
            _ = _seeks.Enqueue(request);
        };
        // Timeline capture/latest-seek state remains independent of keyboard commands.
        _seek.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (_seek.IsEnabled && e.GetCurrentPoint(_seek).Properties.IsLeftButtonPressed)
            {
                _seekPointer = e.Pointer;
                _timeline.BeginGesture(pointer: true);
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        _seek.AddHandler(PointerReleasedEvent, (_, _) => EndPointerSeek(), RoutingStrategies.Bubble, handledEventsToo: true);
        _seek.PointerCaptureLost += (_, _) => EndPointerSeek();
        _seek.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && _timeline.HasGesture) CancelSeekGesture();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        _seek.LostFocus += (_, _) => CancelSeekGesture();
        AttachedToVisualTree += (_, _) =>
        {
            _ownerWindow = TopLevel.GetTopLevel(this) as Window;
            if (_transportWindow != null && ReferenceEquals(_transportWindow, _ownerWindow))
            {
                _transportWindow.Deactivated -= OnWindowDeactivated;
                _transportWindow = null;
            }
            if (_ownerWindow != null) _ownerWindow.Deactivated += OnWindowDeactivated;
            AttachOwnerWindowExtras();
            Texts.Changed += OnLanguageChanged; ShortcutService.Changed += RefreshTransportPresentation; RefreshTransportPresentation();
        };
        DetachedFromVisualTree += async (_, _) =>
        {
            if (_ownerWindow != null) _ownerWindow.Deactivated -= OnWindowDeactivated;
            DetachOwnerWindowExtras();
            _ownerWindow = null;
            if (IsDisposed) DetachTransport();
            else SetExternalTransportHost(null);
            Texts.Changed -= OnLanguageChanged; ShortcutService.Changed -= RefreshTransportPresentation; await StopAfterDetachAsync(StopAsync);
        };
    }

    // Nothing awaits a detach handler, so a failed stop would escape as an unhandled UI-thread exception. A failed
    // native cleanup is already retained and reported by the lifetime (CleanupDiagnostic); closing must still finish.
    internal static async Task StopAfterDetachAsync(Func<Task> stop)
    {
        try { await stop(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { Serilog.Log.Warning(error, "Video stop after leaving the visual tree failed; cleanup ownership stays retained."); }
    }

    private void EndPointerSeek() { _seekPointer = null; _timeline.EndGesture(pointer: true); RenderTime(); }
    private void OnWindowDeactivated(object? sender, EventArgs e) => CancelSeekGesture();
    private void CancelSeekGesture()
    {
        _timeline.Cancel();
        long revision = _timeline.Revision;
        void Update()
        {
            if (_timeline.Revision != revision) return;
            if (_seekPointer?.Captured is Visual captured && (ReferenceEquals(captured, _seek) || _seek.IsVisualAncestorOf(captured)))
                _seekPointer.Capture(null);
            _seekPointer = null;
            RenderTime();
        }
        if (Dispatcher.UIThread.CheckAccess()) Update();
        else Dispatcher.UIThread.Post(Update);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(RefreshTransportPresentation); return; }
        RefreshTransportPresentation();
    }
    private void RefreshTransportPresentation()
    {
        void ActionName(Control control, string id, string label)
        {
            string tip = CommandCatalog.ShortcutDisplay(id) is string gesture ? label + " · " + gesture : label;
            AutomationProperties.SetName(control, label);
            AutomationProperties.SetHelpText(control, tip);
            ToolTip.SetTip(control, tip);
        }
        _playPause.Content = TransportIcon("video.play-pause", 20, _isPlaying ? "playing" : "paused");
        ActionName(_playPause, "video.play-pause", Texts.T(_isPlaying ? "viewer.video.pause" : "viewer.video.play"));
        bool muted = _muteIntent.Muted;
        _mute.Content = TransportIcon("video.mute", 18, muted ? "muted" : "audible");
        _mute.IsChecked = muted;
        string audioUnavailable = Texts.T(_audioTracks == 0 ? "viewer.video.noAudioTrack" : "viewer.video.audioUnavailable");
        ActionName(_mute, "video.mute", _audioAvailable ? Texts.T(muted ? "viewer.video.unmute" : "viewer.video.mute") : audioUnavailable);
        _loop.Content = TransportIcon("video.loop", 18);
        _loop.IsChecked = _intent.LoopEnabled;
        ActionName(_loop, "video.loop", _repeatOwnedBySlideshow && _repeatOwnedReason != null ? _repeatOwnedReason
            : Texts.T(_loop.IsChecked == true ? "viewer.video.loop.disable" : "viewer.video.loop.enable"));
        _speed.Content = RateLabel(Rate);
        string speed = Texts.T("viewer.video.speed", RateLabel(Rate));
        string speedKeys = string.Join(" · ", new[] { "video.speed-down", "video.speed-up", "video.speed-reset" }
            .Select(id => CommandCatalog.ShortcutDisplay(id)).Where(gesture => gesture != null));
        string speedTip = speed + " · " + Texts.T("viewer.video.speedHelp") + (speedKeys.Length > 0 ? " · " + speedKeys : string.Empty);
        speedTip += " · " + Texts.T("viewer.video.speedMore");
        AutomationProperties.SetName(_speed, speed);
        AutomationProperties.SetHelpText(_speed, speedTip);
        ToolTip.SetTip(_speed, speedTip);
        RefreshSeekTooltip();
        string volumeLabel = _audioAvailable ? Texts.T("viewer.video.volume") : audioUnavailable;
        AutomationProperties.SetName(_volume, volumeLabel);
        ToolTip.SetTip(_volume, volumeLabel);
        RefreshTransportExtras();
    }
    private static Control TransportIcon(string command, int size, string? state = null)
    {
        Border icon = (Border)CommandBindings.Icon(command, size, state);
        icon.Bind(Border.BackgroundProperty, icon.GetObservable(Avalonia.Controls.Documents.TextElement.ForegroundProperty));
        return icon;
    }
    internal async Task ShowAsync(GalleryEntry entry, CancellationToken cancellationToken)
    {
        VideoPlaybackLifetime.ShowRequest? request = _lifetime.AcceptShow(cancellationToken);
        if (request == null) return;
        // Capture selection policy once. Later config changes are not transport commands;
        // explicit transport intent accepted while loading must survive backend construction.
        _intent.Set(request.Generation, _autoplay ? VideoPlaybackMode.Playing : VideoPlaybackMode.Stopped);
        _transportIntent.Request(request.Generation, _intent.Current.Mode);
        try
        {
            await request.PreviousCleanup.WaitAsync(request.Token).ConfigureAwait(true);
            if (!IsCurrent(request.Generation)) return;
            if (Dispatcher.UIThread.CheckAccess()) await ShowCoreAsync(entry, request).ConfigureAwait(true);
            else
            {
                await Dispatcher.UIThread.InvokeAsync(() => IsCurrent(request.Generation)
                    ? ShowCoreAsync(entry, request) : Task.CompletedTask).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        finally { _lifetime.FinishShow(request); }
    }

    private async Task ShowCoreAsync(GalleryEntry entry, VideoPlaybackLifetime.ShowRequest request)
    {
        long generation = request.Generation;
        CancellationToken token = request.Token;
        PerformanceDiagnostics.Record("video.show", "begin", request.Id, asset: entry.Asset.AssetId, revision: generation);
        _loopRestarts = 0;
        ResetViewTransform();
        _rate = VideoRates.Normal; // A new medium starts at normal speed; its new native player does too.
        ResetTransportExtras(); // Its A–B loop and audio tracks are its own.
        CancelSeekGesture();
        BeginScrubSession(entry, generation);
        _timeline.Reset(generation);
        _isPlaying = false; _ended = false; _canSeek = _knownSeekable = false;
        _muteIntent.BeginMedia();
        _audioAvailable = false; _audioTracks = -1; _unrenderedAudioProbes = 0;
        _audioTrackDisableAttemptedForUnavailableOutput = false; _audioProbe.BeginMedia();
        RefreshTransportPresentation();
        _volumeIntent.Request(generation, (int)_volume.Value);
        EnableTransport(false); _length = 0; DurationChanged?.Invoke(null); UpdateTime(0);
        _status.Text = Texts.T("gallery.video.verifying");
        await Task.Run(async () =>
        {
            using (PerformanceDiagnostics.Measure("video.operations.wait", request.Id, asset: entry.Asset.AssetId, revision: generation))
                await _operations.WaitAsync().ConfigureAwait(false);
            Stream? stream = null; MediaInput? input = null; Media? media = null; MediaPlayer? player = null;
            VerifiedVideoFile? nativeFile = null; SoftwareVideoSurface? surface = null;
            try
            {
                await ClearPlaybackAsync(generation).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!IsCurrent(generation)) return;
                using (PerformanceDiagnostics.Measure("video.verify", request.Id, asset: entry.Asset.AssetId, revision: generation))
                    stream = await entry.Manifest.OpenVerifiedReadAsync(entry.Asset.AssetId, token).ConfigureAwait(false);
                _lifetime.Cleanup.Owns("native operation, verified stream, engine");
                token.ThrowIfCancellationRequested();
                if (!IsCurrent(generation)) return;
                using (PerformanceDiagnostics.Measure("video.native.create", request.Id, asset: entry.Asset.AssetId, revision: generation))
                {
                    NativeVideoRuntime.EnsureInitialized();
                    _engine ??= UsesSoftwareVideo
                        ? new LibVLC("--no-video-title-show", "--no-media-library", "--avcodec-hw=none", "--stats")
                        : new LibVLC("--no-video-title-show", "--no-media-library", "--stats");
                    player = new MediaPlayer(_engine);
                    VideoVolumeRequest initialVolume = _volumeIntent.Current;
                    player.Volume = initialVolume.Volume; ApplyMute(player);
                    _appliedVolumeRevision = initialVolume.Revision;
                    player.EnableKeyInput = false; player.EnableMouseInput = false;
                    if (UsesSoftwareVideo) surface = new SoftwareVideoSurface(_softwareVideo, player);
                    bool isoBaseMedia = VideoStreamDemux.HasIsoBaseMediaHeader(stream);
                    if (VideoNativeInput.UsesFileInput)
                    {
                        nativeFile = await VideoNativeInput.MaterializeAsync(stream, entry.Asset.Path, token).ConfigureAwait(false);
                        media = new Media(_engine, nativeFile.Path, FromType.FromPath);
                    }
                    else
                    {
                        input = new StreamMediaInput(stream);
                        media = new Media(_engine, input);
                    }
                    AddInitialRotation(media);
                    // Keep the verified stream and use a private file on macOS Intel:
                    // LibVLC 3's callback-backed imem access can reject its get/release
                    // table there, while the file input preserves seeking/repeat semantics.
                    if (isoBaseMedia) media.AddOption(":demux=avformat");
                }
                _lifetime.Cleanup.Owns("native operation, player/drawable, callback roots, media/input, verified stream, native file, engine");
                MediaPlayer ownedPlayer = player;
                player.Playing += (_, _) =>
                {
                    // Start, resume, repeat and replay may (re)create the output and reset mute.
                    // An Intel host without a CoreAudio endpoint needs the same defensive
                    // audio-track admission on every play epoch, not only the first medium.
                    _unrenderedAudioProbes = 0;
                    _audioTrackDisableAttemptedForUnavailableOutput = false;
                    _audioProbe.Invalidate();
                    PostIntent(generation, intent =>
                    {
                        if (!_intent.MarkPlaying(intent)) return;
                        _isPlaying = true; _ended = false; _status.Text = string.Empty; EnableTransport(true); RefreshTransportPresentation();
                        _ = RefreshAudioStateAsync(generation, ownedPlayer);
                        // A replay or repeat may re-open the input; keep the chosen speed.
                        if (!VideoRates.Same(_rate, VideoRates.Normal)) ApplyRate();
                        RequestAudioTrackRefresh(generation, ownedPlayer);
                    });
                };
                player.Paused += (_, _) => PostIntent(generation, intent =>
                { if (intent.Mode == VideoPlaybackMode.Paused) { _isPlaying = false; RefreshTransportPresentation(); } });
                player.Stopped += (_, _) => PostIntent(generation, intent =>
                {
                    if (intent.Mode != VideoPlaybackMode.Stopped) return;
                    _isPlaying = false; if (!_ended) UpdateTime(0); RefreshTransportPresentation();
                });
                player.SeekableChanged += (_, args) => Post(generation, () =>
                {
                    _knownSeekable |= args.Seekable != 0;
                    _canSeek = args.Seekable != 0 || (_ended && _knownSeekable);
                    _seek.IsEnabled = _playPause.IsEnabled && _canSeek && _length > 0;
                });
                player.LengthChanged += (_, args) => Post(generation, () =>
                {
                    // The verified local medium has not changed; terminal input teardown may
                    // report zero. Keep its last known duration until the next Show resets it.
                    if (args.Length > 0)
                    {
                        bool changed = _length != args.Length;
                        _length = args.Length;
                        if (changed) DurationChanged?.Invoke(TimeSpan.FromMilliseconds(args.Length));
                    }
                    RenderTime(); _seek.IsEnabled = _playPause.IsEnabled && _canSeek && _length > 0;
                });
                player.TimeChanged += (_, args) =>
                {
                    long revision = _timeline.Revision;
                    Post(generation, () =>
                    {
                        if (!_ended && _timeline.Observe(generation, revision, args.Time))
                        {
                            RenderTime();
                            CheckAbLoop(args.Time);
                        }
                        if (_sourceSize == null) RequestNativeSize(generation, ownedPlayer);
                        if (_audioProbe.Tick()) _ = RefreshAudioStateAsync(generation, ownedPlayer);
                    });
                };
                void TrackChanged()
                {
                    // Invalidate before requesting: a probe in flight may have read the old track list.
                    _audioProbe.Invalidate();
                    _ = RefreshAudioStateAsync(generation, ownedPlayer);
                    RequestAudioTrackRefresh(generation, ownedPlayer);
                }
                player.ESAdded += (_, _) => TrackChanged();
                player.ESDeleted += (_, _) => TrackChanged();
                player.ESSelected += (_, _) => TrackChanged();
                player.EndReached += (_, _) =>
                {
                    // A seek to the terminal position is an interaction, not slideshow progress.
                    bool naturalEnd = !_timeline.HasGesture && !_timeline.HasPendingSeek;
                    PostIntent(generation, intent =>
                    {
                        // Claim once per play epoch, including repeat. Native Stop/duplicate
                        // terminal callbacks cannot later turn a repeat into navigation.
                        if (!_intent.TryAcceptEnd(intent)) return;
                        _ended = _length > 0;
                        if (_ended) { _canSeek = _knownSeekable; UpdateTime(_length); EnableTransport(true); }
                        _isPlaying = false; RefreshTransportPresentation();
                        if (_length <= 0) _ = FailAsync(generation);
                        // A loop whose B lies at the very end meets the natural end first: it returns to A as well.
                        else if (_abLoop.IsActive && JumpToLoopStart()) { }
                        else if (_intent.CanRepeat(intent)) _ = RestartAfterEndAsync(intent, ownedPlayer);
                        else
                        {
                            _status.Text = Texts.T("gallery.video.ended");
                            if (!IsCurrent(generation)) return;
                            if (naturalEnd && !_timeline.HasGesture && !_timeline.HasPendingSeek)
                                _intent.FinishNaturalEnd(intent);
                            else _intent.Set(generation, intent.Mode);
                        }
                    });
                };
                player.EncounteredError += (_, _) => Post(generation, () => _ = FailAsync(generation));
                token.ThrowIfCancellationRequested();
                _playback = new Playback(generation, player, media, input, stream, nativeFile, surface, token);
                player = null; media = null; input = null; stream = null; nativeFile = null; surface = null;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (IsCurrent(generation) && !UsesSoftwareVideo) _video.MediaPlayer = _playback.Player;
                });
                token.ThrowIfCancellationRequested();
                if (!IsCurrent(generation)) { await ClearPlaybackAsync(generation).ConfigureAwait(false); return; }
                VideoPlaybackIntentStamp initialIntent = _intent.Current;
                if (initialIntent.Generation == generation && initialIntent.Mode == VideoPlaybackMode.Playing)
                {
                    using var play = PerformanceDiagnostics.Measure("video.native.play", request.Id, asset: entry.Asset.AssetId, revision: generation);
                    PlayCurrent(_playback, () => _intent.IsCurrent(initialIntent));
                }
                else
                {
                    _playback.Player.Media = _playback.Media;
                    Post(generation, () => { EnableTransport(true); _status.Text = Texts.T("gallery.video.ready"); });
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { await ClearPlaybackAsync(generation).ConfigureAwait(false); }
            catch (Exception exception)
            {
                await ClearPlaybackAsync(generation).ConfigureAwait(false);
                Post(generation, () =>
                {
                    EnableTransport(false); _status.Text = UnavailableText;
                    PlaybackFailed?.Invoke(); DiagnosticNotice?.Invoke(Diagnosis(exception));
                });
            }
            finally
            {
                try
                {
                    // Even a partially constructed player retains its verified input until native release returns.
                    if (player != null)
                    {
                        _playback = new Playback(generation, player, media!, input, stream!, nativeFile, surface, token);
                        await ClearPlaybackAsync(generation).ConfigureAwait(false);
                    }
                    else
                    {
                        try { surface?.Dispose(); media?.Dispose(); input?.Dispose(); nativeFile?.Dispose(); stream?.Dispose(); }
                        catch (Exception exception)
                        {
                            _failedStream = stream;
                            RetainCleanupFailure(exception);
                            throw;
                        }
                    }
                }
                finally
                {
                    PerformanceDiagnostics.Record("video.show", "end", request.Id, asset: entry.Asset.AssetId, revision: generation);
                    _operations.Release();
                }
            }
        }).ConfigureAwait(true);
    }

    private bool IsCurrent(long generation) => _lifetime.IsCurrent(generation);
    private void Post(long generation, Action update) => Dispatcher.UIThread.Post(() => { if (IsCurrent(generation)) update(); });
    private void PostIntent(long generation, Action<VideoPlaybackIntentStamp> update)
    {
        VideoPlaybackIntentStamp intent = _intent.Current;
        Post(generation, () => { if (intent.Generation == generation && _intent.IsCurrent(intent)) update(intent); });
    }
    private async Task FailAsync(long generation)
    {
        Task? stopping = _lifetime.StopCurrent(generation, out long stoppedGeneration);
        if (stopping == null) return;
        // Report current failure before slow native cleanup, but never into a newer Show.
        if (stoppedGeneration == _generation && !IsDisposed)
        {
            EnableTransport(false);
            _status.Text = UnavailableText;
            PlaybackFailed?.Invoke(); DiagnosticNotice?.Invoke(Diagnosis(null));
        }
        await stopping;
        if (stoppedGeneration == _generation && !IsDisposed)
            _status.Text = UnavailableText;
    }
    // EndReached runs on a native callback thread. Restart only on our serialized
    // worker, with fresh checks both before and after Stop can join native threads.
    private async Task RestartAfterEndAsync(VideoPlaybackIntentStamp intent, MediaPlayer expectedPlayer)
    {
        long generation = intent.Generation;
        await Task.Run(async () =>
        {
            await _operations.WaitAsync().ConfigureAwait(false);
            try
            {
                bool CanRestart() => IsCurrent(generation) && _intent.CanRepeat(intent)
                    && ReferenceEquals(_playback?.Player, expectedPlayer);
                if (!CanRestart() || expectedPlayer.State != VLCState.Ended) return;
                long request = _lifetime.RequestId(generation);
                using (PerformanceDiagnostics.Measure("video.native.repeat-stop", request, revision: generation))
                {
                    DisableUnrenderedAudioOutput(_playback!);
                    expectedPlayer.Stop();
                }
                if (!CanRestart() || _intent.BeginRepeat(intent) is not { } repeat) return;
                if (!IsCurrent(generation) || !_intent.CanRepeat(repeat)) return;
                using (PerformanceDiagnostics.Measure("video.native.repeat-play", request, revision: generation))
                    if (!expectedPlayer.Play(_playback!.Media)) throw new InvalidOperationException("The video backend rejected repeat playback.");
                _loopRestarts++;
            }
            catch (Exception) { Post(generation, () => _ = FailAsync(generation)); }
            finally { _operations.Release(); }
        }).ConfigureAwait(true);
    }
    private static string UnavailableText => Texts.T("gallery.mediaUnavailable");

    private static string Diagnosis(Exception? exception)
    {
        string reason = exception is IOException or UnauthorizedAccessException
            ? Texts.T("gallery.video.error.read")
            : Texts.T("gallery.video.error.decode");
        return OperatingSystem.IsLinux() ? reason + " " + Texts.T("gallery.video.error.linuxRuntime") : reason;
    }
    private void UpdateTime(long position)
    {
        _timeline.Observe(_generation, _timeline.Revision, Math.Max(0, position));
        RenderTime();
    }
    internal static string FormatVideoTime(long milliseconds)
    {
        TimeSpan value = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return value.ToString(milliseconds >= 3_600_000 ? @"h\:mm\:ss" : @"m\:ss");
    }

    private void RenderTime()
    {
        long position = _timeline.DisplayPosition;
        _time.Text = FormatVideoTime(position) + " / " + (_length > 0 ? FormatVideoTime(_length) : "–:––");
        _updatingSeek = true;
        try { _seek.Value = _length > 0 ? Math.Clamp(position * 1000d / _length, 0, 1000) : 0; }
        finally { _updatingSeek = false; }
    }

    private async Task SeekAsync(VideoSeekRequest request)
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            Playback? playback = _playback;
            bool Current() => playback != null && playback.Generation == request.Generation
                && IsCurrent(request.Generation) && _timeline.IsLatest(request);
            if (!Current()) return;
            MediaPlayer player = playback!.Player;
            bool terminal = player.State is VLCState.Ended or VLCState.Stopped;
            long length = player.Length;
            if (terminal && length <= 0) length = request.Length;
            if ((!terminal && !player.IsSeekable) || length <= 0)
            {
                CompleteSeek(request, Math.Max(0, player.Time), player.IsPlaying);
                return;
            }
            // VLC cannot seek an ended/stopped input. Re-open it first, then restore the user's
            // current pause intent. The readiness wait does not dispose or release native input.
            if (terminal)
            {
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Playing(object? sender, EventArgs args) => ready.TrySetResult();
                void Error(object? sender, EventArgs args) => ready.TrySetException(new InvalidOperationException("Video seek replay failed."));
                player.Playing += Playing; player.EncounteredError += Error;
                try
                {
                    if (!PlayCurrent(playback, Current)) return;
                    if (!IsCurrent(request.Generation)) return;
                    await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), playback.Token).ConfigureAwait(false);
                }
                finally { player.Playing -= Playing; player.EncounteredError -= Error; }
                if (!IsCurrent(request.Generation)) return;
                if (_intent.Current.Mode == VideoPlaybackMode.Stopped) { player.Stop(); return; }
                if (_intent.Current.Mode == VideoPlaybackMode.Paused) player.SetPause(true);
                if (!Current()) return;
            }
            long currentLength = player.Length;
            long position = Math.Clamp(request.Position, 0, currentLength > 0 ? currentLength : length);
            // VLC queues the setter internally. Do not immediately acknowledge its old clock
            // as the new position. Prefer a matching native observation, with a bounded wait
            // before falling back to an honest readback (never an invented advancing clock).
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void TimeChanged(object? sender, MediaPlayerTimeChangedEventArgs args)
            {
                if (Math.Abs(args.Time - position) <= 250) observed.TrySetResult();
            }
            player.TimeChanged += TimeChanged;
            try
            {
                player.Time = position;
                if (Math.Abs(player.Time - position) > 250)
                {
                    try { await observed.Task.WaitAsync(TimeSpan.FromMilliseconds(500), playback.Token).ConfigureAwait(false); }
                    catch (TimeoutException) { /* Read actual time even if the backend cannot honor the seek. */ }
                }
                CompleteSeek(request, Math.Max(0, player.Time), player.IsPlaying);
            }
            finally { player.TimeChanged -= TimeChanged; }
        }
        catch (Exception)
        {
            if (IsCurrent(request.Generation) && _timeline.IsLatest(request))
                Post(request.Generation, () => { if (_timeline.IsLatest(request)) _ = FailAsync(request.Generation); });
        }
        finally { _operations.Release(); }
    }
    private void CompleteSeek(VideoSeekRequest request, long actualPosition, bool playing)
    {
        VideoPlaybackIntentStamp intent = _intent.Current;
        Post(request.Generation, () =>
        {
            if (!_timeline.Complete(request, actualPosition)) return;
            if (_intent.IsCurrent(intent))
            {
                _isPlaying = playing;
                if (playing) _intent.MarkPlaying(intent);
            }
            _ended = false; _status.Text = string.Empty;
            RenderTime(); RefreshTransportPresentation();
        });
    }
    private void EnableTransport(bool enabled)
    {
        _playPause.IsEnabled = _speed.IsEnabled = enabled;
        _loop.IsEnabled = enabled && !_repeatOwnedBySlideshow;
        _mute.IsEnabled = _volume.IsEnabled = enabled && _audioAvailable;
        _seek.IsEnabled = enabled && _canSeek && _length > 0;
        UpdateTransportExtrasEnabled();
    }

    // A track list/output may become available only after Playing. Query on our
    // native worker after track changes, and retry on time ticks until the play
    // epoch is settled (see VideoAudioProbeSchedule).
    // A nonnegative cached volume does NOT prove an output exists (Windows can
    // return 0/100 while mmdevice reports no default endpoint). Require actual
    // played audio buffers; muted-but-functional outputs still render buffers.
    private async Task RefreshAudioStateAsync(long generation, MediaPlayer expectedPlayer)
    {
        _audioProbe.CountRequest();
        if (Interlocked.CompareExchange(ref _audioRefreshPending, 1, 0) != 0) return;
        try
        {
            await Task.Run(async () =>
            {
                await _operations.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!IsCurrent(generation) || !ReferenceEquals(_playback?.Player, expectedPlayer)) return;
                    // Observe the epoch before native state, so a later track/playing event always wins.
                    long epoch = _audioProbe.Observe();
                    int tracks = expectedPlayer.AudioTrackCount, volume = expectedPlayer.Volume;
                    MediaStats statistics = _playback!.Media.Statistics;
                    int decoded = statistics.DecodedAudio, played = statistics.PlayedAudioBuffers;
                    bool available = VideoAudioAvailability.IsAvailable(tracks, volume, played);
                    if (VideoNativeInput.UsesFileInput && tracks > 0 && decoded > 0 && played == 0)
                    {
                        // Do not let an Intel host with no CoreAudio endpoint keep reopening
                        // the audio output on every play/repeat. This is enabled only after
                        // native decoding is proven and two ordered probes prove no buffers
                        // reached an output; the playback evidence remains auditable.
                        if (_unrenderedAudioProbes < 2) _unrenderedAudioProbes++;
                        if (_unrenderedAudioProbes >= 2 && !_audioTrackDisableAttemptedForUnavailableOutput)
                        {
                            _audioTrackDisableAttemptedForUnavailableOutput = true;
                            try
                            {
                                if (expectedPlayer.SetAudioTrack(-1))
                                {
                                    PerformanceDiagnostics.Record("video.audio", "disable-unrendered-output",
                                        _lifetime.RequestId(generation), revision: generation);
                                }
                            }
                            catch (Exception exception)
                            {
                                Serilog.Log.Debug(exception, "Could not disable the unrendered Intel audio track.");
                            }
                        }
                    }
                    else if (played > 0 || tracks <= 0)
                    {
                        _unrenderedAudioProbes = 0;
                    }
                    // A volume edit can arrive during backend construction, before the audio
                    // output is ready. Apply it on readiness instead of losing the latest value.
                    VideoVolumeRequest requestedVolume = _volumeIntent.Current;
                    if (available && requestedVolume.Generation == generation
                        && requestedVolume.Revision != _appliedVolumeRevision)
                        ApplyVolume(expectedPlayer, requestedVolume);
                    // Audio/output creation may reset native mute. Reconcile from session
                    // intent, never infer a new user choice from a backend readback.
                    if (available) ApplyMute(expectedPlayer);
                    Post(generation, () =>
                    {
                        _audioProbe.Record(epoch, tracks, available, played);
                        // The presentation reads only these two values from a probe. Every other
                        // input (mute, loop, play state, language, shortcuts) refreshes it itself.
                        bool changed = tracks != _audioTracks || available != _audioAvailable;
                        _audioTracks = tracks; _audioAvailable = available;
                        _mute.IsEnabled = _volume.IsEnabled = _playPause.IsEnabled && available;
                        if (changed) { _audioProbe.CountPresentation(); RefreshTransportPresentation(); }
                    });
                }
                catch (Exception) { /* Playback/error lifecycle owns backend failure reporting. */ }
                finally { _operations.Release(); }
            }).ConfigureAwait(false);
        }
        finally { Interlocked.Exchange(ref _audioRefreshPending, 0); }
    }

    internal bool IsTransportInput(object? source) => source is Visual visual
        && visual.GetSelfAndVisualAncestors().Any(control => ReferenceEquals(control, _seek) || ReferenceEquals(control, _volume));

    internal bool MatchesTransportShortcut(KeyEventArgs e, string id) =>
        CommandBindings.MatchesGalleryTransport(e, id, _seek, _volume);

    internal bool CanAdjustVolume => _audioAvailable && _volume.IsEffectivelyEnabled
        && !_lifetime.HasCleanupPending && !IsDisposed;

    internal void AdjustVolume(int direction)
    {
        if (!CanAdjustVolume || direction == 0) return;
        // Same bounds/step and same synchronous ValueChanged/latest-intent/persistence path
        // as pointer input. No play/pause/loop/mute policy or second backend setter here.
        AdjustVolumeBy(Math.Sign(direction) * _volume.SmallChange);
    }

    // The wheel over the volume control takes the same path in larger steps.
    private void AdjustVolumeBy(double delta)
    {
        if (!CanAdjustVolume || delta == 0) return;
        _volume.Value = Math.Clamp(_volume.Value + delta, _volume.Minimum, _volume.Maximum);
    }

    private Task SetVolumeAsync(int volume, bool userInitiated)
    {
        // UI input order, not native completion order, owns the mute decision.
        _muteIntent.OnVolumeChanged(volume, userInitiated);
        RefreshTransportPresentation();
        VideoVolumeRequest request = _volumeIntent.Request(_generation, volume);
        return CommandAsync(playback =>
        {
            if (!_volumeIntent.IsCurrent(request) || playback.Generation != request.Generation) return;
            MediaPlayer player = playback.Player;
            if (!HasUsableAudio(playback)) return;
            ApplyVolume(player, request);
            Post(request.Generation, () =>
            {
                if (!_volumeIntent.IsCurrent(request)) return;
                RefreshTransportPresentation();
            });
        });
    }

    // Called only while the native operation lock owns this exact playback.
    private static bool HasUsableAudio(Playback playback) => VideoAudioAvailability.IsAvailable(
        playback.Player.AudioTrackCount, playback.Player.Volume, playback.Media.Statistics.PlayedAudioBuffers);

    // Caller holds _operations. No play/pause operation and no preference persistence here.
    private void ApplyVolume(MediaPlayer player, VideoVolumeRequest request)
    {
        if (!_volumeIntent.IsCurrent(request)) return;
        player.Volume = request.Volume;
        // Neither Configure nor delayed readiness replay is an implicit unmute.
        // Read the latest intent, not the intent when this volume request was queued.
        ApplyMute(player);
        _appliedVolumeRevision = request.Revision;
    }

    // Caller owns _operations. Native observations never write back into session intent.
    private void ApplyMute(MediaPlayer player)
    {
        bool muted = _muteIntent.Muted;
        if (player.Mute != muted) player.Mute = muted;
    }

    private async Task CommandAsync(Action<Playback> command)
    {
        long generation = _generation;
        await VideoPlaybackCommands.RunAsync(_operations, _lifetime, generation, () => _playback,
            static playback => playback.Generation, command,
            exception => Post(generation, () => _ = FailAsync(generation))).ConfigureAwait(true);
    }

    // Readable without entering the native semaphore, even while native Stop is blocked.
    internal Task StopUnavailableAsync()
    {
        EnableTransport(false);
        _status.Text = UnavailableText;
        return StopAsync(); // Existing generation/capture/ownership coordinator, never a second stop path.
    }

    internal Task StopAsync()
    {
        _intent.Set(_generation, VideoPlaybackMode.Stopped);
        CancelScrubPreviewRequest();
        CancelSeekGesture();
        return _lifetime.StopAsync();
    }

    private async Task CleanupAsync(long generation)
    {
        _lifetime.Cleanup.Phase("operations-wait");
        await _operations.WaitAsync().ConfigureAwait(false);
        try { await ClearPlaybackAsync(generation).ConfigureAwait(false); }
        finally { _operations.Release(); }
    }

    private void DisableUnrenderedAudioOutput(Playback playback)
    {
        if (!VideoNativeInput.UsesFileInput || _audioTrackDisableAttemptedForUnavailableOutput) return;
        try
        {
            MediaStats statistics = playback.Media.Statistics;
            if (playback.Player.AudioTrackCount > 0 && statistics.DecodedAudio > 0 && statistics.PlayedAudioBuffers == 0)
            {
                _audioTrackDisableAttemptedForUnavailableOutput = true;
                if (playback.Player.SetAudioTrack(-1))
                {
                    PerformanceDiagnostics.Record("video.audio", "disable-unrendered-output-before-stop",
                        _lifetime.RequestId(playback.Generation), revision: playback.Generation);
                }
            }
        }
        catch (Exception exception)
        {
            // Cleanup still owns the player and will attempt the native stop. Do not turn
            // an unavailable optional audio output into an untracked ownership loss.
            Serilog.Log.Debug(exception, "Could not disable the unrendered Intel audio track before stop.");
        }
    }

    // The drawable and callback-reachable inputs remain owned until native release actually returns.
    // A throwing release poisons admission and retains this owner, rather than freeing live callbacks.
    private async Task ClearPlaybackAsync(long notNewerThan)
    {
        _lifetime.ThrowIfCleanupFailed();
        Playback? playback = _playback;
        if (playback == null || playback.Generation > notNewerThan) return;
        bool watching = _lifetime.Cleanup.Begin(notNewerThan);
        _lifetime.Cleanup.Owns("native operation, player/drawable, callback roots, media/input, verified stream, native file, engine");
        try
        {
            _lifetime.Cleanup.Phase("native-audio-detach");
            DisableUnrenderedAudioOutput(playback);
            _lifetime.Cleanup.Phase("native-stop");
            playback.Player.Stop();
            // A throwing Stop gives no proof that drawable users joined. Retain the owner and
            // fault the shared drain in that case; do not detach or retry release speculatively.
            _lifetime.Cleanup.Phase("drawable-detach");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ReferenceEquals(_video.MediaPlayer, playback.Player)) _video.MediaPlayer = null;
            });
            _lifetime.Cleanup.Phase("player-dispose");
            playback.Player.Dispose();
            _lifetime.Cleanup.Owns("native operation, callback roots, media/input, verified stream, native file, engine");
            _lifetime.Cleanup.Phase("surface-dispose");
            playback.Surface?.Dispose();
            _lifetime.Cleanup.Owns("native operation, media/input, verified stream, native file, engine");
            _lifetime.Cleanup.Phase("media-dispose");
            playback.Media?.Dispose();
            _lifetime.Cleanup.Owns("native operation, input, native file, verified stream, engine");
            _lifetime.Cleanup.Phase("input-dispose");
            playback.Input?.Dispose();
            _lifetime.Cleanup.Owns("native operation, verified stream, native file, engine");
            _lifetime.Cleanup.Phase("stream-dispose");
            playback.Stream?.Dispose();
            if (playback.Stream != null)
            {
                if (!playback.Stream.CanRead) Interlocked.Increment(ref _releasedStreams);
                else throw new IOException("The verified video stream did not close.");
            }
            _lifetime.Cleanup.Phase("native-file-dispose");
            playback.NativeFile?.Dispose();
            _lifetime.Cleanup.Owns("native operation, engine");
            if (playback.NativeFile != null && _engine != null)
            {
                // The macOS Intel file-backed path uses VLC's FFmpeg demuxer. Reusing its
                // LibVLC instance across repeated stop/play generations can retain decoder
                // thread state after the player/media/file are gone; the next WebM seek then
                // may block in MKV cluster recovery. The player is the only engine owner, so
                // rebuild that engine between generations after native media ownership is gone.
                _lifetime.Cleanup.Phase("engine-dispose-after-file-playback");
                _engine.Dispose();
                _engine = null;
                _lifetime.Cleanup.Owns("native operation, engine");
            }
            _playback = null;
            if (watching) _lifetime.Cleanup.Complete();
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _cleanupFailures);
            RetainCleanupFailure(exception);
            throw;
        }
    }

    private void RetainCleanupFailure(Exception exception)
    {
        _lifetime.FailCleanup(exception);
        if (!_failedCleanupRoot.IsAllocated) _failedCleanupRoot = GCHandle.Alloc(this);
        Dispatcher.UIThread.Post(() => CleanupDiagnostic?.Invoke(Texts.T("gallery.video.cleanupFailed")));
    }

    private async Task ReleaseEngineAsync()
    {
        _lifetime.Cleanup.Owns("native operation, engine");
        _lifetime.Cleanup.Phase("engine-operations-wait");
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            _lifetime.Cleanup.Phase("engine-dispose");
            _engine?.Dispose(); _engine = null;
            // Keep the semaphore valid: late commands still have to acquire and observe stale generation.
        }
        catch (Exception exception)
        {
            RetainCleanupFailure(exception);
            throw;
        }
        finally { _operations.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        _intent.Set(_generation, VideoPlaybackMode.Stopped);
        CancelSeekGesture();
        Task cleanup = _lifetime.DisposeAsync();
        Task scrubCleanup = DisposeScrubPreviewAsync();
        cleanup = Task.WhenAll(cleanup, scrubCleanup);
        if (Dispatcher.UIThread.CheckAccess()) RetireTransportPlacement();
        else Dispatcher.UIThread.Post(RetireTransportPlacement);
        return new(DetachTransportAfterCleanupAsync(cleanup));
    }
    private async Task DetachTransportAfterCleanupAsync(Task cleanup)
    {
        // Keep the drawable and its overlay root alive until native cleanup succeeds.
        // A cleanup failure must not tear down the still-owned native surface.
        await cleanup.ConfigureAwait(false);
        if (Dispatcher.UIThread.CheckAccess()) DetachTransport();
        else await Dispatcher.UIThread.InvokeAsync(DetachTransport);
    }
}

// Only the gallery timeline uses this type. The owning viewer routes effective
// media/volume commands in its existing tunnel handler; if none matched, no native
// timeline key can change Value. Generic/volume slider value keys remain native.
internal class GalleryTransportSlider : Slider
{
    protected override Type StyleKeyOverride => typeof(Slider);
    internal static bool IsValueKey(Key key) => key is Key.Left or Key.Right or Key.Up or Key.Down
        or Key.Home or Key.End or Key.PageUp or Key.PageDown;
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (IsValueKey(e.Key)) { e.Handled = true; return; }
        base.OnKeyDown(e);
    }
}

// Volume arrows are owned by the effective gallery commands too: clearing/remapping
// a binding must not silently recover native horizontal volume editing (or Up/Down).
// Unchanged Home/End/Page value editing remains native to this one volume control.
internal class GalleryVolumeSlider : Slider
{
    protected override Type StyleKeyOverride => typeof(Slider);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down) { e.Handled = true; return; }
        base.OnKeyDown(e);
    }
}

/// <summary>
/// Production command adapter, also exercised with fake native targets. The caller's existing
/// native-operation semaphore protects snapshot selection, authorization and command execution.
/// A newly accepted show can be current while the slot still holds its retiring predecessor.
/// </summary>
internal static class VideoPlaybackCommands
{
    internal static Task RunAsync<TPlayback>(SemaphoreSlim operations, VideoPlaybackLifetime lifetime,
        long generation, Func<TPlayback?> currentPlayback, Func<TPlayback, long> playbackGeneration,
        Action<TPlayback> command, Action<Exception>? onFailure = null) where TPlayback : class
        => Task.Run(async () =>
        {
            await operations.WaitAsync().ConfigureAwait(false);
            try
            {
                TPlayback? playback = currentPlayback();
                if (playback == null || playbackGeneration(playback) != generation || !lifetime.IsCurrent(generation))
                {
                    PerformanceDiagnostics.Record("video.command", "rejected-stale-playback",
                        lifetime.RequestId(generation), revision: generation);
                    return;
                }
                using var measure = PerformanceDiagnostics.Measure("video.command", lifetime.RequestId(generation), revision: generation);
                command(playback);
            }
            catch (Exception exception) when (onFailure != null) { onFailure(exception); }
            finally { operations.Release(); }
        });
}

/// <summary>
/// Headless admission coordinator used by the real player and stalled-native tests.
/// Idle -> accepted show -> cleanup pending -> idle; Dispose closes admission permanently.
/// The state lock never contains native calls, dispatcher waits or cancellation callbacks.
/// </summary>
internal sealed class VideoPlaybackLifetime
{
    private readonly object _state = new();
    private readonly Func<long, Task> _cleanup;
    private readonly Func<Task> _releaseEngine;
    private readonly List<ShowRequest> _shows = new();
    private ShowRequest? _loading;
    private TaskCompletionSource? _stop, _dispose;
    private bool _dirty, _cleanupAgain, _disposing;
    private Exception? _cleanupFailure;
    private Task? _failedDrain;
    private long _generation, _stopTarget, _noOpStops, _fullStops;
    internal VideoCleanupWatch Cleanup { get; }
    internal event Action? CleanupAccepted;
    internal bool HasCleanupPending { get { lock (_state) return _stop != null || _disposing; } }
    internal bool HasCleanupFailure { get { lock (_state) return _cleanupFailure != null; } }

    internal VideoPlaybackLifetime(Func<long, Task> cleanup, Func<Task> releaseEngine, VideoCleanupWatch? cleanupWatch = null)
    { _cleanup = cleanup; _releaseEngine = releaseEngine; Cleanup = cleanupWatch ?? new("video.cleanup"); }
    internal long Generation => Interlocked.Read(ref _generation);
    internal bool IsDisposing { get { lock (_state) return _disposing; } }
    internal long NoOpStops => Interlocked.Read(ref _noOpStops);
    internal long FullStops => Interlocked.Read(ref _fullStops);
    internal long RequestId(long generation)
    { lock (_state) return _loading?.Generation == generation ? _loading.Id : 0; }
    internal bool IsCurrent(long generation)
    {
        lock (_state) return !_disposing && _cleanupFailure == null && generation == _generation
            && _loading?.Token.IsCancellationRequested == false;
    }

    internal sealed class ShowRequest
    {
        internal readonly long Generation, Id = PerformanceDiagnostics.NextId();
        internal readonly CancellationTokenSource Source;
        internal readonly CancellationToken Token;
        internal readonly Task PreviousCleanup;
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenRegistration Cancellation;
        private int _retired;
        internal ShowRequest(long generation, CancellationToken token, Task previousCleanup)
        {
            Generation = generation; PreviousCleanup = previousCleanup;
            Source = CancellationTokenSource.CreateLinkedTokenSource(token); Token = Source.Token;
        }
        internal void Retire()
        {
            if (Interlocked.Exchange(ref _retired, 1) != 0) return;
            _ = RetireAsync();
        }
        private async Task RetireAsync()
        {
            try { await Source.CancelAsync().ConfigureAwait(false); }
            finally
            {
                // A canceled show may still be inside a non-cancelable native call.
                await Completion.Task.ConfigureAwait(false);
                Cancellation.Dispose(); Source.Dispose();
            }
        }
    }

    internal ShowRequest? AcceptShow(CancellationToken token)
    {
        ShowRequest request;
        ShowRequest? previous;
        lock (_state)
        {
            if (_disposing || _cleanupFailure != null || token.IsCancellationRequested) return null;
            request = new ShowRequest(++_generation, token, _stop?.Task ?? Task.CompletedTask);
            _shows.Add(request); _dirty = true;
            previous = _loading; _loading = request;
        }
        // Register outside the lock: an already canceled token invokes synchronously. Retirement
        // cannot dispose this request until FinishShow, which the caller can only run after return.
        request.Cancellation = request.Token.Register(() => { _ = StopAsync(request.Generation); });
        previous?.Retire();
        PerformanceDiagnostics.Record("video.show.accepted", request: request.Id, revision: request.Generation);
        return request;
    }

    internal void FinishShow(ShowRequest request)
    {
        lock (_state) { _shows.Remove(request); request.Completion.TrySetResult(); }
    }

    internal Task StopAsync(long? expectedGeneration = null)
    {
        ShowRequest? loading;
        Task result;
        lock (_state)
        {
            if (expectedGeneration.HasValue && expectedGeneration != _generation) return Task.CompletedTask;
            if (_dispose != null) return _dispose.Task;
            result = RequestStopLocked(out loading);
        }
        loading?.Retire();
        return result;
    }

    internal Task? StopCurrent(long expectedGeneration, out long stoppedGeneration)
    {
        ShowRequest? loading;
        Task result;
        lock (_state)
        {
            stoppedGeneration = _generation;
            if (expectedGeneration != _generation || _dispose != null) return null;
            result = RequestStopLocked(out loading);
            stoppedGeneration = _generation;
        }
        loading?.Retire();
        return result;
    }

    private Task RequestStopLocked(out ShowRequest? loading)
    {
        ++_generation; // This and show admission/idle classification are one atomic transition.
        loading = _loading; _loading = null;
        if (_cleanupFailure != null) return _stop?.Task ?? _failedDrain!;
        if (!_dirty && _shows.Count == 0 && _stop == null)
        {
            ++_noOpStops;
            PerformanceDiagnostics.Record("video.stop", "idle", revision: _generation, value: _noOpStops);
            return Task.CompletedTask;
        }
        _stopTarget = _generation;
        _cleanupAgain |= _dirty;
        _dirty = false;
        if (_stop != null) return _stop.Task;
        ++_fullStops;
        _stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Cleanup.Begin(_generation)) Cleanup.Phase("accepted-shows-wait");
        PerformanceDiagnostics.Record("video.stop", "accepted", revision: _generation, value: _fullStops);
        Task result = _stop.Task;
        _ = Task.Run(DrainAsync);
        return result;
    }

    private async Task DrainAsync()
    {
        try
        {
            while (true)
            {
                long target;
                Task[] shows;
                lock (_state)
                {
                    target = _stopTarget; _cleanupAgain = false;
                    shows = _shows.Where(show => show.Generation <= target).Select(show => show.Completion.Task).ToArray();
                }
                try { CleanupAccepted?.Invoke(); }
                catch (Exception) { PerformanceDiagnostics.Record("video.cleanup.ui", "unavailable", revision: target); }
                // Every accepted show through the stop boundary must finish, including one not yet queued
                // for native execution. New shows wait on this drain and are excluded from this barrier.
                await Task.WhenAll(shows).ConfigureAwait(false);
                ThrowIfCleanupFailed();
                Cleanup.Begin(target);
                await _cleanup(target).ConfigureAwait(false);
                lock (_state)
                {
                    if (_cleanupAgain) continue; // At most one coalesced follow-up, never one task per stop.
                    Cleanup.Complete();
                    _stop!.TrySetResult(); _stop = null;
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            FailCleanup(exception);
            lock (_state) _stop!.TrySetException(exception); // Retain the failed real drain; never retry native release.
        }
    }

    internal void ThrowIfCleanupFailed()
    {
        lock (_state)
            if (_cleanupFailure != null) throw new InvalidOperationException("Native video cleanup failed; ownership is retained.", _cleanupFailure);
    }
    internal void FailCleanup(Exception exception)
    {
        lock (_state)
        {
            if (_cleanupFailure != null) return;
            _cleanupFailure = exception;
            _failedDrain = Task.FromException(exception);
            _ = _failedDrain.Exception; // Fault is also surfaced by every stop/dispose; avoid an unobserved duplicate.
        }
        Cleanup.Fail();
    }

    internal Task DisposeAsync()
    {
        ShowRequest? loading;
        Task stop;
        TaskCompletionSource completion;
        lock (_state)
        {
            if (_dispose != null) return _dispose.Task;
            _disposing = true;
            stop = RequestStopLocked(out loading);
            completion = _dispose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        loading?.Retire();
        _ = Task.Run(async () =>
        {
            try
            {
                await stop.ConfigureAwait(false);
                Cleanup.Begin(Generation);
                await _releaseEngine().ConfigureAwait(false);
                Cleanup.Complete();
                completion.TrySetResult();
            }
            catch (Exception exception) { FailCleanup(exception); completion.TrySetException(exception); }
        });
        return completion.Task;
    }
}

internal sealed record VideoCleanupEvidence(long Request, long Generation, string Phase, string LastCompleted,
    long ElapsedTicks, bool Pending, bool Overdue, bool OwnershipRetained, string RetainedResources);

/// <summary>
/// One bounded deadman per native owner, independent of the UI dispatcher. Its timer uses
/// the ThreadPool; pool-starvation detection belongs to the external PF00 watchdog.
/// It observes pending work and never releases or cancels resources.
/// </summary>
internal sealed class VideoCleanupWatch
{
    private readonly object _state = new();
    private readonly string _prefix;
    private readonly Func<long> _clock;
    private readonly long _deadline;
    private readonly bool _schedule;
    private Timer? _timer;
    private long _request, _generation, _since;
    private string _phase = "idle", _completed = "none", _resources = "accepted show/native operation";
    private bool _pending, _overdue, _retained;
    internal event Action<VideoCleanupEvidence>? Pending;
    internal VideoCleanupWatch(string prefix, Func<long>? clock = null, long? deadline = null, bool schedule = true)
    {
        _prefix = prefix; _clock = clock ?? Stopwatch.GetTimestamp;
        _deadline = deadline ?? 5 * Stopwatch.Frequency; _schedule = schedule;
    }
    internal VideoCleanupEvidence Evidence
    {
        get { lock (_state) return Snapshot(); }
    }
    private VideoCleanupEvidence Snapshot() => new(_request, _generation, _phase, _completed,
        _pending ? Math.Max(0, _clock() - _since) : 0, _pending, _overdue, _retained, _resources);
    internal bool Begin(long generation)
    {
        lock (_state)
        {
            if (_pending) return false;
            _request = PerformanceDiagnostics.NextId(); _generation = generation;
            _pending = _retained = true; _overdue = false;
            _phase = "accepted"; _completed = "none"; _since = _clock();
            if (_resources.Length == 0) _resources = "accepted show/native operation";
            if (_schedule) _timer = new Timer(_ => CheckDeadman(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            PerformanceDiagnostics.Record(_prefix, "accepted", _request, revision: generation);
            return true;
        }
    }
    internal void Owns(string resources) { lock (_state) _resources = resources; }
    internal void Phase(string phase)
    {
        lock (_state)
        {
            if (!_pending) return;
            PerformanceDiagnostics.Record(_prefix + ".phase", _phase + ":returned", _request,
                revision: _generation, value: Math.Max(0, _clock() - _since));
            _completed = _phase; _phase = phase; _since = _clock(); _overdue = false;
            PerformanceDiagnostics.Record(_prefix + ".phase", phase + ":begin", _request, revision: _generation);
        }
    }
    internal void CheckDeadman()
    {
        VideoCleanupEvidence evidence;
        lock (_state)
        {
            if (!_pending || _overdue || _clock() - _since < _deadline) return;
            _overdue = true; evidence = Snapshot();
            PerformanceDiagnostics.Record(_prefix + ".pending", evidence.Phase + "; retained=" + evidence.RetainedResources,
                evidence.Request, revision: evidence.Generation, value: evidence.ElapsedTicks);
        }
        try { Pending?.Invoke(evidence); } catch (Exception) { /* Diagnostics cannot change ownership. */ }
    }
    internal void Complete()
    {
        lock (_state)
        {
            PerformanceDiagnostics.Record(_prefix + ".phase", _phase + ":returned", _request,
                revision: _generation, value: Math.Max(0, _clock() - _since));
            _completed = _phase; _phase = "complete"; _pending = _overdue = _retained = false; _resources = "";
            _timer?.Dispose(); _timer = null;
            PerformanceDiagnostics.Record(_prefix, "released", _request, revision: _generation);
        }
    }
    internal void Fail()
    {
        lock (_state)
        {
            _pending = false; _retained = true; _phase += ":failed";
            _timer?.Dispose(); _timer = null;
            PerformanceDiagnostics.Record(_prefix, "failed:ownership-retained", _request, revision: _generation);
        }
    }
}

/// <summary>Bounded container sniff on the already verified handle, not a filename/codec guess.</summary>
internal static class VideoStreamDemux
{
    internal static bool HasIsoBaseMediaHeader(Stream stream)
    {
        if (!stream.CanSeek) return false;
        long position = stream.Position;
        try
        {
            stream.Position = 0;
            Span<byte> header = stackalloc byte[12];
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length) return false;
            uint boxLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header);
            return boxLength >= 16 && boxLength <= stream.Length
                && header.Slice(4, 4).SequenceEqual("ftyp"u8);
        }
        finally { stream.Position = position; }
    }
}

/// <summary>Track metadata and cached volume alone do not establish a working audio renderer.</summary>
internal static class VideoAudioAvailability
{
    internal static bool IsAvailable(int tracks, int volume, int playedAudioBuffers)
        => tracks > 0 && volume >= 0 && playedAudioBuffers > 0;
}

/// <summary>
/// Decides whether a time tick must re-probe native audio state. Native track and Playing
/// callbacks only call <see cref="Invalidate"/>; all other members run on the UI thread, in
/// probe completion order. An epoch is settled when the media has no audio track, or when two
/// available probes of that epoch saw played buffers grow: the output then rendered after the
/// epoch began, so the probe that saw it already reapplied volume and mute to the live output.
/// Present but silent tracks never settle (an output may still start), so they keep the
/// previous per-tick behavior. A settled epoch with a rendering track still probes every
/// <see cref="HeartbeatTicks"/> ticks: the backend can replace its output (device switch,
/// format change) without a track or Playing event, and a new output may reset mute.
/// </summary>
internal sealed class VideoAudioProbeSchedule
{
    // About two seconds at VLC's usual four time ticks per second.
    internal const int HeartbeatTicks = 8;
    private long _epoch;
    private long _settledAt = -1, _baselineAt = -1;
    private int _baselineBuffers;
    private bool _rendering;
    private int _idleTicks;
    private long _ticks, _requests, _probes, _presentations;

    internal void Invalidate() => Interlocked.Increment(ref _epoch);
    internal long Observe() => Interlocked.Read(ref _epoch);
    internal bool IsSettled => _settledAt == Interlocked.Read(ref _epoch);

    internal void BeginMedia()
    {
        _settledAt = _baselineAt = -1; _rendering = false; _idleTicks = 0;
        Interlocked.Exchange(ref _ticks, 0); Interlocked.Exchange(ref _requests, 0);
        Interlocked.Exchange(ref _probes, 0); Interlocked.Exchange(ref _presentations, 0);
    }

    // Returns whether this time tick must probe.
    internal bool Tick()
    {
        Interlocked.Increment(ref _ticks);
        if (!IsSettled) return true;
        // Trackless media has no output to replace; a track event invalidates it.
        if (!_rendering || ++_idleTicks < HeartbeatTicks) return false;
        _idleTicks = 0;
        return true;
    }

    internal void Record(long epoch, int tracks, bool available, int playedBuffers)
    {
        Interlocked.Increment(ref _probes);
        _settledAt = -1; _idleTicks = 0; _rendering = false;
        if (tracks == 0) { _settledAt = epoch; return; }
        if (!available) { _baselineAt = -1; return; }
        if (_baselineAt == epoch && playedBuffers > _baselineBuffers) { _settledAt = epoch; _rendering = true; return; }
        // First available sample of this epoch, or stale statistics of a replaced input.
        _baselineAt = epoch; _baselineBuffers = playedBuffers;
    }

    internal void CountRequest() => Interlocked.Increment(ref _requests);
    internal void CountPresentation() => Interlocked.Increment(ref _presentations);
    // Evidence only: time ticks, probe requests, completed probes and probe-caused presentations.
    // Settled is read off the UI thread here, so it is a racy snapshot for evidence only.
    internal (long Ticks, long Requests, long Probes, long Presentations, bool Settled) Counts =>
        (Interlocked.Read(ref _ticks), Interlocked.Read(ref _requests), Interlocked.Read(ref _probes),
            Interlocked.Read(ref _presentations), Volatile.Read(ref _settledAt) == Interlocked.Read(ref _epoch));
}

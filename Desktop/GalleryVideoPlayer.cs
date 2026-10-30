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
internal sealed class GalleryVideoPlayer : UserControl, IAsyncDisposable
{
    private readonly VideoView _video = new() { Name = "GalleryNativeVideo", MinHeight = 100 };
    private readonly Image _softwareVideo = new() { Name = "GallerySoftwareVideo", MinHeight = 100, Stretch = Avalonia.Media.Stretch.Uniform };
    // Status and time belong to the themed transport below the black media viewport.
    private readonly TextBlock _status = new() { Name = "GalleryVideoStatus", FontSize = 12, Margin = new Thickness(0, 4, 0, 0), TextWrapping = Avalonia.Media.TextWrapping.Wrap, [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Text") };
    private readonly TextBlock _time = new() { Name = "GalleryVideoTime", FontSize = 12, MinWidth = 76, TextAlignment = Avalonia.Media.TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Muted") };
    private readonly Button _playPause = new() { Name = "GalleryVideoPlayPause", Classes = { "media-control", "media-play" }, Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Padding = new Thickness(10) };
    private readonly ToggleButton _mute = new() { Name = "GalleryVideoMute", Classes = { "media-control" }, Width = 32, Height = 32, Padding = new Thickness(7) };
    private readonly ToggleButton _loop = new() { Name = "GalleryVideoLoop", Classes = { "media-control" }, Width = 32, Height = 32, Padding = new Thickness(7) };
    private readonly GalleryTransportSlider _seek = new() { Name = "GalleryVideoSeek", Classes = { "media-slider" }, Minimum = 0, Maximum = 1000, MinWidth = 80, Height = 32, VerticalAlignment = VerticalAlignment.Center };
    private readonly GalleryVolumeSlider _volume = new() { Name = "GalleryVideoVolume", Classes = { "media-slider" }, Minimum = 0, Maximum = 100, Value = 70, Width = 80, Height = 32, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _root = new() { RowDefinitions = new RowDefinitions("*,Auto") };
    private readonly Border _transportPanel = new() { Name = "GalleryVideoTransport", Classes = { "workspace-media-transport" } };
    private Panel? _externalTransportHost;
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
    private bool _startMuted;
    private bool _settingVolume;
    private bool _isPlaying;
    private bool _publishingUserTransport;
    private bool _ended;
    private bool _muted;
    private readonly VideoPlaybackIntent _intent = new();
    private readonly VideoVolumeIntent _volumeIntent = new();
    private readonly VideoTransportIntent _transportIntent = new();
    // Accessed only under _operations, unlike UI input revisions.
    private long _appliedVolumeRevision;
    private readonly VideoTimelineInteraction _timeline = new();
    private readonly VideoLatestSeekQueue _seeks;
    private Window? _ownerWindow;
    private IPointer? _seekPointer;
    private int _loopRestarts;
    private bool _canSeek;
    private bool _knownSeekable;
    private bool _audioAvailable;
    private int _audioTracks = -1;
    private int _audioRefreshPending;

    internal event Action<int>? VolumeChanged;
    internal event Action<bool>? LoopChanged;
    internal event Action? PlaybackEnded;
    internal event Action? PlaybackFailed;
    internal event Action<string>? DiagnosticNotice;
    internal event Action<string>? CleanupDiagnostic;
    internal event Action<bool>? UserPlaybackRequested;
    internal event Action? SeekInteractionStarted;
    internal bool LoopEnabled => _intent.LoopEnabled;
    internal bool IsSeekInteracting => _timeline.HasGesture || _timeline.HasPendingSeek;
    internal VideoPlaybackMode PlaybackMode => _intent.Current.Mode;
    internal Control TransportControl => _transportPanel;

    // UI-thread API. A dedicated external host owns visibility/auto-hide and routing; it
    // receives the existing transport, never a second set of controls or a native drawable.
    internal void SetExternalTransportHost(Panel? host)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (ReferenceEquals(host, _root)) host = null;
        if (host != null && (IsDisposed || host.GetLogicalAncestors().Contains(_transportPanel)))
        {
            if (IsDisposed) return;
            throw new ArgumentException("The transport cannot be hosted inside itself.", nameof(host));
        }
        if (ReferenceEquals(host, _externalTransportHost)) return;
        CancelSeekGesture();
        Panel oldHost = _externalTransportHost ?? _root;
        oldHost.Children.Remove(_transportPanel);
        _externalTransportHost = host;
        Grid.SetRow(_transportPanel, host == null ? 1 : 0);
        Grid.SetRowSpan(UsesSoftwareVideo ? _softwareVideo : _video, host == null ? 1 : 2);
        (host ?? _root).Children.Add(_transportPanel);
    }

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
            Control surface = UsesSoftwareVideo ? _softwareVideo : _video;
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
        if (OperatingSystem.IsWindows()) _video.Content = overlay;
    }

    internal void Configure(bool autoplay, bool startMuted, int volume)
    {
        _autoplay = autoplay;
        _startMuted = startMuted;
        int nextVolume = Math.Clamp(volume, 0, 100);
        bool changed = (int)_volume.Value != nextVolume;
        _settingVolume = true;
        try { _volume.Value = nextVolume; }
        finally { _settingVolume = false; }
        if (changed) _ = SetVolumeAsync(nextVolume);
    }

    internal Task TogglePlaybackAsync() => SetPlaybackModeAsync(
        !_ended && _intent.Current.Mode == VideoPlaybackMode.Playing ? VideoPlaybackMode.Paused : VideoPlaybackMode.Playing);

    // Compatibility commands remain routable without keeping separate visible buttons.
    private bool PlayCurrent(Playback playback, Func<bool> permitted)
    {
        if (!IsCurrent(playback.Generation) || !permitted()) return false;
        MediaPlayer player = playback.Player;
        if (player.State == VLCState.Ended) player.Stop();
        if (!IsCurrent(playback.Generation) || !permitted()) return false;
        if (!player.Play(playback.Media)) throw new InvalidOperationException("The video backend rejected playback.");
        return true;
    }
    internal Task PlayAsync() => SetPlaybackModeAsync(VideoPlaybackMode.Playing);
    internal Task PauseAsync() => SetPlaybackModeAsync(VideoPlaybackMode.Paused);
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
            _ended = false;
        }
        return CommandAsync(playback =>
        {
            if (!_transportIntent.IsCurrent(command)) return;
            MediaPlayer player = playback.Player;
            if (mode == VideoPlaybackMode.Stopped) player.Stop();
            else if (mode == VideoPlaybackMode.Paused) player.SetPause(true);
            else if (player.State == VLCState.Paused) player.SetPause(false);
            else if (!player.IsPlaying) PlayCurrent(playback, () => _transportIntent.IsCurrent(command));
        });
    }
    private async Task RequestPlaybackOnUiAsync(VideoPlaybackMode mode, bool notify)
        => await Dispatcher.UIThread.InvokeAsync(() => SetPlaybackModeAsync(mode, notify)).ConfigureAwait(false);

    internal Task ToggleMuteAsync() => CommandAsync(playback =>
    {
        MediaPlayer player = playback.Player;
        if (!_audioAvailable || !HasUsableAudio(playback)) return;
        player.Mute = !player.Mute;
        bool muted = player.Mute;
        long generation = playback.Generation;
        Post(generation, () => { _muted = muted; RefreshTransportPresentation(); });
    });
    internal void ToggleLoop()
    {
        if (IsDisposed || !_loop.IsEnabled) return;
        SetLoopEnabled(!LoopEnabled, notify: true);
    }

    private sealed record Playback(long Generation, MediaPlayer Player, Media Media, StreamMediaInput Input, Stream Stream, SoftwareVideoSurface? Surface, CancellationToken Token);

    // Acceptance observes the real player under its normal native-operation lock.
    // No alternate decoder, dummy video output or synthetic clock is used.
    internal sealed record PlaybackEvidence(string State, long Time, int DecodedFrames, int DisplayedFrames,
        bool HasPlayback, bool HasEngine, bool Disposed, int ReleasedStreams, int CleanupFailures,
        int SurfaceFrames = 0, bool SurfaceNonUniform = false, bool LoopEnabled = false, int LoopRestarts = 0,
        bool Muted = false, int Volume = 0, long Length = 0, bool Seekable = false,
        int AudioTracks = -1, bool AudioAvailable = false, int DecodedAudio = 0, int PlayedAudioBuffers = -1);

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
            return new PlaybackEvidence(playback.Player.State.ToString(), playback.Player.Time,
                stats.DecodedVideo, stats.DisplayedPictures, true, _engine != null, IsDisposed,
                _releasedStreams, _cleanupFailures, playback.Surface?.PresentedFrames ?? 0, playback.Surface?.NonUniform ?? false,
                _intent.LoopEnabled, _loopRestarts, playback.Player.Mute, volume,
                playback.Player.Length, playback.Player.IsSeekable, audioTracks,
                VideoAudioAvailability.IsAvailable(audioTracks, volume, stats.PlayedAudioBuffers), stats.DecodedAudio, stats.PlayedAudioBuffers);
        }
        finally { _operations.Release(); }
    });

    internal GalleryVideoPlayer()
    {
        _lifetime = new VideoPlaybackLifetime(CleanupAsync, ReleaseEngineAsync);
        _seeks = new VideoLatestSeekQueue(SeekAsync);
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
            CleanupDiagnostic?.Invoke(Text("The video backend has not finished cleanup; resources remain owned.",
                "Das Videobackend hat das Aufräumen noch nicht beendet; Ressourcen bleiben gebunden."));
        });
        CommandBindings.BindExisting(_playPause, "video.play-pause");
        CommandBindings.BindExisting(_mute, "video.mute");
        CommandBindings.BindExisting(_loop, "video.loop");
        foreach (Slider slider in new Slider[] { _seek, _volume })
        {
            // Retain Semi's slider input/template with a slim media track and
            // a full-height pointer/focus target.
            slider.Resources["SliderTrackWidth"] = 3d;
            slider.Resources["SliderThumbWidth"] = 12d;
            slider.Resources["SliderThumbHeight"] = 12d;
        }
        StackPanel secondary = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        secondary.Children.Add(_mute); secondary.Children.Add(_volume); secondary.Children.Add(_loop);
        Grid timeline = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        timeline.Children.Add(_seek);
        timeline.Children.Add(_time); Grid.SetColumn(_time, 1);
        Grid controls = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
        controls.Children.Add(_playPause);
        controls.Children.Add(timeline); Grid.SetColumn(timeline, 1);
        controls.Children.Add(secondary); Grid.SetColumn(secondary, 2);
        // A narrow post-context pane gets two deliberate rows, never a loosely
        // wrapping button pile. Audio and loop remain reachable at every width.
        bool compact = false;
        _transportPanel.SizeChanged += (_, _) =>
        {
            bool nextCompact = _transportPanel.Bounds.Width < 560;
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
        Control videoSurface = UsesSoftwareVideo ? _softwareVideo : _video;
        _root.Children.Add(videoSurface);
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
        _volume.PropertyChanged += (_, args) =>
        {
            if (args.Property != Slider.ValueProperty || _settingVolume) return;
            // Capture the event value, not a potentially reentrant current slider value.
            int volume = (int)args.GetNewValue<double>();
            _ = SetVolumeAsync(volume);
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
            Texts.Changed += OnLanguageChanged; ShortcutService.Changed += RefreshTransportPresentation; RefreshTransportPresentation();
        };
        DetachedFromVisualTree += async (_, _) =>
        {
            if (_ownerWindow != null) _ownerWindow.Deactivated -= OnWindowDeactivated;
            _ownerWindow = null;
            SetExternalTransportHost(null);
            Texts.Changed -= OnLanguageChanged; ShortcutService.Changed -= RefreshTransportPresentation; await StopAsync();
        };
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

    private static string Text(string en, string de) => GalleryViewerText.Get(en, de);
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
        _mute.Content = TransportIcon("video.mute", 18, _muted ? "muted" : "audible");
        _mute.IsChecked = _muted;
        string audioUnavailable = Texts.T(_audioTracks == 0 ? "viewer.video.noAudioTrack" : "viewer.video.audioUnavailable");
        ActionName(_mute, "video.mute", _audioAvailable ? Texts.T(_muted ? "viewer.video.unmute" : "viewer.video.mute") : audioUnavailable);
        _loop.Content = TransportIcon("video.loop", 18);
        _loop.IsChecked = _intent.LoopEnabled;
        ActionName(_loop, "video.loop", Texts.T(_loop.IsChecked == true ? "viewer.video.loop.disable" : "viewer.video.loop.enable"));
        AutomationProperties.SetName(_seek, Texts.T("viewer.video.position"));
        ToolTip.SetTip(_seek, Texts.T("viewer.video.position"));
        string volumeLabel = _audioAvailable ? Texts.T("viewer.video.volume") : audioUnavailable;
        AutomationProperties.SetName(_volume, volumeLabel);
        ToolTip.SetTip(_volume, volumeLabel);
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
        CancelSeekGesture();
        _timeline.Reset(generation);
        _isPlaying = false; _ended = false; _muted = _startMuted; _canSeek = _knownSeekable = false;
        _audioAvailable = false; _audioTracks = -1;
        RefreshTransportPresentation();
        _volumeIntent.Request(generation, (int)_volume.Value);
        EnableTransport(false); _length = 0; UpdateTime(0);
        _status.Text = Text("Verifying local video…", "Lokales Video wird geprüft…");
        await Task.Run(async () =>
        {
            using (PerformanceDiagnostics.Measure("video.operations.wait", request.Id, asset: entry.Asset.AssetId, revision: generation))
                await _operations.WaitAsync().ConfigureAwait(false);
            Stream? stream = null; StreamMediaInput? input = null; Media? media = null; MediaPlayer? player = null; SoftwareVideoSurface? surface = null;
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
                        ? new LibVLC("--no-video-title-show", "--no-media-library", "--avcodec-hw=none")
                        : new LibVLC("--no-video-title-show", "--no-media-library");
                    player = new MediaPlayer(_engine);
                    VideoVolumeRequest initialVolume = _volumeIntent.Current;
                    player.Volume = initialVolume.Volume; player.Mute = _startMuted;
                    _appliedVolumeRevision = initialVolume.Revision;
                    player.EnableKeyInput = false; player.EnableMouseInput = false;
                    if (UsesSoftwareVideo) surface = new SoftwareVideoSurface(_softwareVideo, player);
                    input = new StreamMediaInput(stream);
                    media = new Media(_engine, input);
                    // VLC 3's MP4 demux can consume a callback-backed, single-chunk
                    // video-only track to EOF while its reported clock stays at 250ms.
                    // Keep the verified handle (never reopen by path); avformat demuxes
                    // the same bytes with progressing native PCR/time and the same decoder.
                    if (VideoStreamDemux.HasIsoBaseMediaHeader(stream)) media.AddOption(":demux=avformat");
                }
                _lifetime.Cleanup.Owns("native operation, player/drawable, callback roots, media/input, verified stream, engine");
                MediaPlayer ownedPlayer = player;
                player.Playing += (_, _) => PostIntent(generation, intent =>
                {
                    if (!_intent.MarkPlaying(intent)) return;
                    _isPlaying = true; _ended = false; _status.Text = string.Empty; EnableTransport(true); RefreshTransportPresentation();
                    _ = RefreshAudioStateAsync(generation, ownedPlayer);
                });
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
                    if (args.Length > 0) _length = args.Length;
                    RenderTime(); _seek.IsEnabled = _playPause.IsEnabled && _canSeek && _length > 0;
                });
                player.TimeChanged += (_, args) =>
                {
                    long revision = _timeline.Revision;
                    Post(generation, () =>
                    {
                        if (!_ended && _timeline.Observe(generation, revision, args.Time))
                            RenderTime();
                        _ = RefreshAudioStateAsync(generation, ownedPlayer);
                    });
                };
                player.ESAdded += (_, _) => _ = RefreshAudioStateAsync(generation, ownedPlayer);
                player.ESDeleted += (_, _) => _ = RefreshAudioStateAsync(generation, ownedPlayer);
                player.ESSelected += (_, _) => _ = RefreshAudioStateAsync(generation, ownedPlayer);
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
                        else if (_intent.CanRepeat(intent)) _ = RestartAfterEndAsync(intent, ownedPlayer);
                        else
                        {
                            _status.Text = Text("Playback ended.", "Wiedergabe beendet.");
                            if (!IsCurrent(generation)) return;
                            if (naturalEnd && !_timeline.HasGesture && !_timeline.HasPendingSeek)
                                _intent.FinishNaturalEnd(intent);
                            else _intent.Set(generation, intent.Mode);
                        }
                    });
                };
                player.EncounteredError += (_, _) => Post(generation, () => _ = FailAsync(generation));
                token.ThrowIfCancellationRequested();
                _playback = new Playback(generation, player, media, input, stream, surface, token);
                player = null; media = null; input = null; stream = null; surface = null;
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
                    Post(generation, () => { EnableTransport(true); _status.Text = Text("Ready to play.", "Bereit zur Wiedergabe."); });
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
                        _playback = new Playback(generation, player, media!, input!, stream!, surface, token);
                        await ClearPlaybackAsync(generation).ConfigureAwait(false);
                    }
                    else
                    {
                        try { surface?.Dispose(); media?.Dispose(); input?.Dispose(); stream?.Dispose(); }
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
                    expectedPlayer.Stop();
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
    private static string UnavailableText => Text("Media unavailable. Details are available in diagnostics.",
        "Medium nicht verfügbar. Details stehen in der Diagnose.");

    private static string Diagnosis(Exception? exception)
    {
        string reason = exception is IOException or UnauthorizedAccessException
            ? Text("The saved video could not be verified or read.", "Das gespeicherte Video konnte nicht geprüft oder gelesen werden.")
            : Text("Video playback is unavailable or this file cannot be decoded.", "Videowiedergabe ist nicht verfügbar oder diese Datei kann nicht dekodiert werden.");
        return OperatingSystem.IsLinux() ? reason + " " + Text(
            "Linux requires LibVLC 3.0.20 or newer with WebM/Matroska, VP8/VP9, Vorbis/Opus and video/audio output plugins. Install your distribution's VLC runtime packages.",
            "Linux benötigt LibVLC ab 3.0.20 mit WebM-/Matroska-, VP8-/VP9-, Vorbis-/Opus- und Video-/Audioausgabe-Plugins. Installiere die VLC-Laufzeitpakete deiner Distribution.") : reason;
    }
    private void UpdateTime(long position)
    {
        _timeline.Observe(_generation, _timeline.Revision, Math.Max(0, position));
        RenderTime();
    }
    private void RenderTime()
    {
        long position = _timeline.DisplayPosition;
        static string Clock(long milliseconds) => TimeSpan.FromMilliseconds(milliseconds).ToString(milliseconds >= 3_600_000 ? @"h\:mm\:ss" : @"m\:ss");
        _time.Text = Clock(position) + " / " + (_length > 0 ? Clock(_length) : "–:––");
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
        _playPause.IsEnabled = _loop.IsEnabled = enabled;
        _mute.IsEnabled = _volume.IsEnabled = enabled && _audioAvailable;
        _seek.IsEnabled = enabled && _canSeek && _length > 0;
    }

    // A track list/output may become available only after Playing. Query on our
    // native worker after track changes, and retry while initial audio is unknown.
    // A nonnegative cached volume does NOT prove an output exists (Windows can
    // return 0/100 while mmdevice reports no default endpoint). Require actual
    // played audio buffers; muted-but-functional outputs still render buffers.
    private async Task RefreshAudioStateAsync(long generation, MediaPlayer expectedPlayer)
    {
        if (Interlocked.CompareExchange(ref _audioRefreshPending, 1, 0) != 0) return;
        try
        {
            await Task.Run(async () =>
            {
                await _operations.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (!IsCurrent(generation) || !ReferenceEquals(_playback?.Player, expectedPlayer)) return;
                    int tracks = expectedPlayer.AudioTrackCount, volume = expectedPlayer.Volume;
                    bool available = VideoAudioAvailability.IsAvailable(tracks, volume, _playback!.Media.Statistics.PlayedAudioBuffers);
                    // A volume edit can arrive during backend construction, before the audio
                    // output is ready. Apply it on readiness instead of losing the latest value.
                    VideoVolumeRequest requestedVolume = _volumeIntent.Current;
                    if (available && requestedVolume.Generation == generation
                        && requestedVolume.Revision != _appliedVolumeRevision)
                        ApplyVolume(expectedPlayer, requestedVolume);
                    bool muted = available && expectedPlayer.Mute;
                    Post(generation, () =>
                    {
                        _audioTracks = tracks; _audioAvailable = available;
                        if (available) _muted = muted;
                        _mute.IsEnabled = _volume.IsEnabled = _playPause.IsEnabled && available;
                        RefreshTransportPresentation();
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
        double step = _volume.SmallChange;
        _volume.Value = Math.Clamp(_volume.Value + Math.Sign(direction) * step, _volume.Minimum, _volume.Maximum);
    }

    private Task SetVolumeAsync(int volume)
    {
        VideoVolumeRequest request = _volumeIntent.Request(_generation, volume);
        return CommandAsync(playback =>
        {
            if (!_volumeIntent.IsCurrent(request) || playback.Generation != request.Generation) return;
            MediaPlayer player = playback.Player;
            if (!HasUsableAudio(playback)) return;
            ApplyVolume(player, request);
            bool muted = player.Mute;
            Post(request.Generation, () =>
            {
                if (!_volumeIntent.IsCurrent(request)) return;
                _muted = muted;
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
        if (request.Volume > 0) player.Mute = false;
        _appliedVolumeRevision = request.Revision;
    }

    private async Task CommandAsync(Action<Playback> command)
    {
        long generation = _generation;
        await VideoPlaybackCommands.RunAsync(_operations, _lifetime, generation, () => _playback,
            static playback => playback.Generation, command,
            exception => Post(generation, () => _ = FailAsync(generation))).ConfigureAwait(true);
    }

    internal long NoOpStopCount => _lifetime.NoOpStops;
    internal long FullStopCount => _lifetime.FullStops;
    // Readable without entering the native semaphore, even while native Stop is blocked.
    internal VideoCleanupEvidence CleanupEvidence => _lifetime.Cleanup.Evidence;
    internal Task StopUnavailableAsync()
    {
        EnableTransport(false);
        _status.Text = UnavailableText;
        return StopAsync(); // Existing generation/capture/ownership coordinator, never a second stop path.
    }

    internal Task StopAsync()
    {
        _intent.Set(_generation, VideoPlaybackMode.Stopped);
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

    // The drawable and callback-reachable inputs remain owned until native release actually returns.
    // A throwing release poisons admission and retains this owner, rather than freeing live callbacks.
    private async Task ClearPlaybackAsync(long notNewerThan)
    {
        _lifetime.ThrowIfCleanupFailed();
        Playback? playback = _playback;
        if (playback == null || playback.Generation > notNewerThan) return;
        bool watching = _lifetime.Cleanup.Begin(notNewerThan);
        _lifetime.Cleanup.Owns("native operation, player/drawable, callback roots, media/input, verified stream, engine");
        try
        {
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
            _lifetime.Cleanup.Owns("native operation, callback roots, media/input, verified stream, engine");
            _lifetime.Cleanup.Phase("surface-dispose");
            playback.Surface?.Dispose();
            _lifetime.Cleanup.Owns("native operation, media/input, verified stream, engine");
            _lifetime.Cleanup.Phase("media-dispose");
            playback.Media?.Dispose();
            _lifetime.Cleanup.Owns("native operation, input, verified stream, engine");
            _lifetime.Cleanup.Phase("input-dispose");
            playback.Input?.Dispose();
            _lifetime.Cleanup.Owns("native operation, verified stream, engine");
            _lifetime.Cleanup.Phase("stream-dispose");
            playback.Stream?.Dispose();
            if (playback.Stream != null)
            {
                if (!playback.Stream.CanRead) Interlocked.Increment(ref _releasedStreams);
                else throw new IOException("The verified video stream did not close.");
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
        Dispatcher.UIThread.Post(() => CleanupDiagnostic?.Invoke(Text(
            "Video cleanup failed; resources remain retained and new playback is blocked.",
            "Video-Aufräumen fehlgeschlagen; Ressourcen bleiben gebunden und neue Wiedergabe ist blockiert.")));
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
        if (_externalTransportHost == null) return new(cleanup);
        return new(RestoreTransportAndDrainAsync(cleanup));
    }
    private async Task RestoreTransportAndDrainAsync(Task cleanup)
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess()) SetExternalTransportHost(null);
            else await Dispatcher.UIThread.InvokeAsync(() => SetExternalTransportHost(null));
        }
        finally { await cleanup.ConfigureAwait(false); }
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

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
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Layout;
using Avalonia.Threading;
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
    private readonly Slider _seek = new() { Name = "GalleryVideoSeek", Classes = { "media-slider" }, Minimum = 0, Maximum = 1000, MinWidth = 80, Height = 32, VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider _volume = new() { Name = "GalleryVideoVolume", Classes = { "media-slider" }, Minimum = 0, Maximum = 100, Value = 70, Width = 80, Height = 32, VerticalAlignment = VerticalAlignment.Center };
    private static bool UsesSoftwareVideo => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly VideoPlaybackLifetime _lifetime;
    private GCHandle _failedCleanupRoot;
    private Stream? _failedStream;
    private LibVLC? _engine;
    private Playback? _playback;
    private long _generation => _lifetime.Generation;
    private long _length;
    private long _position;
    private bool _updatingSeek;
    private bool IsDisposed => _lifetime.IsDisposing;
    private int _releasedStreams;
    private int _cleanupFailures;
    private bool _autoplay = true;
    private bool _startMuted;
    private bool _settingVolume;
    private bool _isPlaying;
    private bool _ended;
    private bool _muted;
    private int _loopEnabled;
    private int _loopRestarts;
    private bool _canSeek;
    private bool _audioAvailable;
    private int _audioTracks = -1;
    private int _audioRefreshPending;

    internal event Action<int>? VolumeChanged;

    // Query after layout so wrapped transport controls, DPI and theme spacing
    // are reflected without duplicating their dimensions in the viewer.
    internal double TransportBottomInset
    {
        get
        {
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
        _settingVolume = true;
        try { _volume.Value = Math.Clamp(volume, 0, 100); }
        finally { _settingVolume = false; }
    }

    internal Task TogglePlaybackAsync() => CommandAsync(playback =>
    {
        MediaPlayer player = playback.Player;
        if (player.IsPlaying) player.SetPause(true);
        else if (player.State == VLCState.Paused) player.SetPause(false);
        else PlayCurrent(playback);
    });

    // Compatibility commands remain routable without keeping separate visible buttons.
    private void PlayCurrent(Playback playback)
    {
        if (!IsCurrent(playback.Generation)) return;
        MediaPlayer player = playback.Player;
        if (player.State == VLCState.Ended) player.Stop();
        if (!IsCurrent(playback.Generation)) return;
        if (!player.Play(playback.Media)) throw new InvalidOperationException("The video backend rejected playback.");
    }
    internal Task PlayAsync() => CommandAsync(playback =>
    {
        if (playback.Player.State == VLCState.Paused) playback.Player.SetPause(false);
        else PlayCurrent(playback);
    });
    internal Task PauseAsync() => CommandAsync(playback => playback.Player.SetPause(true));
    internal Task StopPlaybackAsync()
    {
        _ended = false;
        Interlocked.Exchange(ref _loopEnabled, 0);
        RefreshTransportPresentation();
        return CommandAsync(playback => playback.Player.Stop());
    }
    internal Task ToggleMuteAsync() => CommandAsync(playback =>
    {
        MediaPlayer player = playback.Player;
        if (!_audioAvailable || player.AudioTrackCount <= 0 || player.Volume < 0) return;
        player.Mute = !player.Mute;
        bool muted = player.Mute;
        long generation = playback.Generation;
        Post(generation, () => { _muted = muted; RefreshTransportPresentation(); });
    });
    internal void ToggleLoop()
    {
        if (IsDisposed || !_loop.IsEnabled) return;
        Interlocked.Exchange(ref _loopEnabled, Volatile.Read(ref _loopEnabled) == 0 ? 1 : 0);
        RefreshTransportPresentation();
    }

    private sealed record Playback(long Generation, MediaPlayer Player, Media Media, StreamMediaInput Input, Stream Stream, SoftwareVideoSurface? Surface);

    // Acceptance observes the real player under its normal native-operation lock.
    // No alternate decoder, dummy video output or synthetic clock is used.
    internal sealed record PlaybackEvidence(string State, long Time, int DecodedFrames, int DisplayedFrames,
        bool HasPlayback, bool HasEngine, bool Disposed, int ReleasedStreams, int CleanupFailures,
        int SurfaceFrames = 0, bool SurfaceNonUniform = false, bool LoopEnabled = false, int LoopRestarts = 0,
        bool Muted = false, int Volume = 0, long Length = 0, bool Seekable = false,
        int AudioTracks = -1, bool AudioAvailable = false);

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
                _releasedStreams, _cleanupFailures, LoopEnabled: Volatile.Read(ref _loopEnabled) != 0, LoopRestarts: _loopRestarts);
            var stats = playback.Media.Statistics;
            if (snapshotPath != null && !playback.Player.TakeSnapshot(0, snapshotPath, 0, 0))
                throw new InvalidOperationException("The native video output rejected the frame snapshot.");
            int audioTracks = playback.Player.AudioTrackCount, volume = playback.Player.Volume;
            return new PlaybackEvidence(playback.Player.State.ToString(), playback.Player.Time,
                stats.DecodedVideo, stats.DisplayedPictures, true, _engine != null, IsDisposed,
                _releasedStreams, _cleanupFailures, playback.Surface?.PresentedFrames ?? 0, playback.Surface?.NonUniform ?? false,
                Volatile.Read(ref _loopEnabled) != 0, _loopRestarts, playback.Player.Mute, volume,
                playback.Player.Length, playback.Player.IsSeekable, audioTracks, audioTracks > 0 && volume >= 0);
        }
        finally { _operations.Release(); }
    });

    internal GalleryVideoPlayer()
    {
        _lifetime = new VideoPlaybackLifetime(CleanupAsync, ReleaseEngineAsync);
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
            _status.Text = Text("Waiting for the video backend to finish cleanup…", "Videobackend wird noch aufgeräumt…");
        });
        CommandBindings.BindExisting(_playPause, "video.play-pause");
        CommandBindings.BindExisting(_mute, "video.mute");
        CommandBindings.BindExisting(_loop, "video.loop");
        foreach (Slider slider in new[] { _seek, _volume })
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
        SizeChanged += (_, _) =>
        {
            bool nextCompact = Bounds.Width < 560;
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
        Border transportPanel = new() { Name = "GalleryVideoTransport", Classes = { "workspace-media-transport" }, Child = transport };
        // Seek, time, volume and playback form one reserved surface. Its dynamic
        // resources follow open-window theme changes without touching playback.
        Grid root = new() { RowDefinitions = new RowDefinitions("*,Auto") };
        Control videoSurface = UsesSoftwareVideo ? _softwareVideo : _video;
        root.Children.Add(videoSurface);
        root.Children.Add(transportPanel); Grid.SetRow(transportPanel, 1); Content = root;
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
        _volume.PropertyChanged += async (_, args) =>
        {
            if (args.Property == Slider.ValueProperty)
            {
                int volume = (int)_volume.Value;
                bool userChange = !_settingVolume;
                await CommandAsync(playback =>
                {
                    MediaPlayer player = playback.Player;
                    if (!_audioAvailable || player.AudioTrackCount <= 0 || player.Volume < 0) return;
                    player.Volume = volume; if (volume > 0) player.Mute = false;
                    bool muted = player.Mute;
                    long generation = playback.Generation;
                    Post(generation, () => { _muted = muted; RefreshTransportPresentation(); });
                });
                if (userChange) VolumeChanged?.Invoke(volume);
            }
        };
        _seek.PropertyChanged += async (_, args) =>
        {
            if (args.Property != Slider.ValueProperty || _updatingSeek || _length <= 0) return;
            long position = (long)(_seek.Value * _length / 1000);
            await CommandAsync(playback => { if (playback.Player.IsSeekable) playback.Player.Time = position; });
        };
        // These focused sliders keep their own arrow-key behavior; the gallery owns arrows elsewhere.
        AttachedToVisualTree += (_, _) =>
        { Texts.Changed += OnLanguageChanged; ShortcutService.Changed += RefreshTransportPresentation; RefreshTransportPresentation(); };
        DetachedFromVisualTree += async (_, _) =>
        { Texts.Changed -= OnLanguageChanged; ShortcutService.Changed -= RefreshTransportPresentation; await StopAsync(); };
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
        _loop.IsChecked = Volatile.Read(ref _loopEnabled) != 0;
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
        Interlocked.Exchange(ref _loopEnabled, 0); _loopRestarts = 0;
        _isPlaying = false; _ended = false; _muted = _startMuted; _canSeek = false; _position = 0;
        _audioAvailable = false; _audioTracks = -1;
        RefreshTransportPresentation();
        int volume = (int)_volume.Value;
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
                    player.Volume = volume; player.Mute = _startMuted;
                    player.EnableKeyInput = false; player.EnableMouseInput = false;
                    if (UsesSoftwareVideo) surface = new SoftwareVideoSurface(_softwareVideo, player);
                    input = new StreamMediaInput(stream);
                    media = new Media(_engine, input);
                }
                _lifetime.Cleanup.Owns("native operation, player/drawable, callback roots, media/input, verified stream, engine");
                MediaPlayer ownedPlayer = player;
                player.Playing += (_, _) => Post(generation, () =>
                { _isPlaying = true; _ended = false; _status.Text = string.Empty; EnableTransport(true); RefreshTransportPresentation();
                    _ = RefreshAudioStateAsync(generation, ownedPlayer); });
                player.Paused += (_, _) => Post(generation, () => { _isPlaying = false; RefreshTransportPresentation(); });
                player.Stopped += (_, _) => Post(generation, () => { _isPlaying = false; if (!_ended) UpdateTime(0); RefreshTransportPresentation(); });
                player.SeekableChanged += (_, args) => Post(generation, () =>
                { _canSeek = args.Seekable != 0; _seek.IsEnabled = _playPause.IsEnabled && _canSeek && _length > 0; });
                player.LengthChanged += (_, args) => Post(generation, () =>
                { _length = Math.Max(0, args.Length); UpdateTime(_position); _seek.IsEnabled = _playPause.IsEnabled && _canSeek && _length > 0; });
                player.TimeChanged += (_, args) => Post(generation, () =>
                { if (!_ended) UpdateTime(args.Time); _ = RefreshAudioStateAsync(generation, ownedPlayer); });
                player.ESAdded += (_, _) => _ = RefreshAudioStateAsync(generation, ownedPlayer);
                player.ESDeleted += (_, _) => _ = RefreshAudioStateAsync(generation, ownedPlayer);
                player.ESSelected += (_, _) => _ = RefreshAudioStateAsync(generation, ownedPlayer);
                player.EndReached += (_, _) => Post(generation, () =>
                {
                    // VLC can report a reset time/Stopped after EndReached. Keep
                    // the terminal clock until replay or an explicit stop begins.
                    _ended = _length > 0;
                    if (_ended) UpdateTime(_length);
                    _isPlaying = false; RefreshTransportPresentation();
                    if (_length <= 0) _ = FailAsync(generation);
                    else if (Volatile.Read(ref _loopEnabled) != 0) _ = RestartAfterEndAsync(generation, ownedPlayer);
                    else _status.Text = Text("Playback ended.", "Wiedergabe beendet.");
                });
                player.EncounteredError += (_, _) => Post(generation, () => _ = FailAsync(generation));
                token.ThrowIfCancellationRequested();
                _playback = new Playback(generation, player, media, input, stream, surface);
                player = null; media = null; input = null; stream = null; surface = null;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (IsCurrent(generation) && !UsesSoftwareVideo) _video.MediaPlayer = _playback.Player;
                });
                token.ThrowIfCancellationRequested();
                if (!IsCurrent(generation)) { await ClearPlaybackAsync(generation).ConfigureAwait(false); return; }
                if (_autoplay)
                {
                    using var play = PerformanceDiagnostics.Measure("video.native.play", request.Id, asset: entry.Asset.AssetId, revision: generation);
                    if (!_playback.Player.Play(_playback.Media)) throw new InvalidOperationException("The video backend rejected this media.");
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
                Post(generation, () => { EnableTransport(false); _status.Text = Diagnosis(exception); });
            }
            finally
            {
                try
                {
                    // Even a partially constructed player retains its verified input until native release returns.
                    if (player != null)
                    {
                        _playback = new Playback(generation, player, media!, input!, stream!, surface);
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
    private async Task FailAsync(long generation)
    {
        Task? stopping = _lifetime.StopCurrent(generation, out long stoppedGeneration);
        if (stopping == null) return;
        await stopping;
        if (stoppedGeneration == _generation && !IsDisposed)
            _status.Text = Diagnosis(null);
    }
    // EndReached runs on a native callback thread. Restart only on our serialized
    // worker, with fresh checks both before and after Stop can join native threads.
    private async Task RestartAfterEndAsync(long generation, MediaPlayer expectedPlayer)
    {
        await Task.Run(async () =>
        {
            await _operations.WaitAsync().ConfigureAwait(false);
            try
            {
                bool CanRestart() => IsCurrent(generation) && Volatile.Read(ref _loopEnabled) != 0
                    && ReferenceEquals(_playback?.Player, expectedPlayer);
                if (!CanRestart() || expectedPlayer.State != VLCState.Ended) return;
                long request = _lifetime.RequestId(generation);
                using (PerformanceDiagnostics.Measure("video.native.repeat-stop", request, revision: generation))
                    expectedPlayer.Stop();
                if (!CanRestart()) return;
                using (PerformanceDiagnostics.Measure("video.native.repeat-play", request, revision: generation))
                    if (!expectedPlayer.Play(_playback!.Media)) throw new InvalidOperationException("The video backend rejected repeat playback.");
                _loopRestarts++;
            }
            catch (Exception) { Post(generation, () => _ = FailAsync(generation)); }
            finally { _operations.Release(); }
        }).ConfigureAwait(true);
    }
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
        _position = Math.Max(0, position);
        static string Clock(long milliseconds) => TimeSpan.FromMilliseconds(milliseconds).ToString(milliseconds >= 3_600_000 ? @"h\:mm\:ss" : @"m\:ss");
        _time.Text = Clock(_position) + " / " + (_length > 0 ? Clock(_length) : "–:––");
        // A dragged/focused timeline keeps its own value instead of fighting time notifications.
        if (_seek.IsKeyboardFocusWithin || _seek.IsPointerOver) return;
        _updatingSeek = true;
        try { _seek.Value = _length > 0 ? Math.Clamp(position * 1000d / _length, 0, 1000) : 0; }
        finally { _updatingSeek = false; }
    }
    private void EnableTransport(bool enabled)
    {
        _playPause.IsEnabled = _loop.IsEnabled = enabled;
        _mute.IsEnabled = _volume.IsEnabled = enabled && _audioAvailable;
        _seek.IsEnabled = enabled && _canSeek && _length > 0;
    }

    // A track list/output may become available only after Playing. Query on our
    // native worker after track changes, and retry while initial audio is unknown.
    // VLC returns -1 for volume/mute when there is no usable audio output.
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
                    bool available = tracks > 0 && volume >= 0;
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
    internal Task StopAsync()
    {
        Interlocked.Exchange(ref _loopEnabled, 0);
        return _lifetime.StopAsync();
    }

    private async Task CleanupAsync(long generation)
    {
        Interlocked.Exchange(ref _loopEnabled, 0);
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

    public ValueTask DisposeAsync() => new(_lifetime.DisposeAsync());
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

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using LibVLCSharp.Avalonia;
using LibVLCSharp.Shared;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

/// <summary>
/// Owns one local, verified media handle. Native calls are serialized off the UI thread;
/// the video surface and the Avalonia transport toolbar occupy separate rows.
/// </summary>
internal sealed class GalleryVideoPlayer : UserControl, IAsyncDisposable
{
    private readonly VideoView _video = new() { Name = "GalleryNativeVideo", MinHeight = 100 };
    private readonly Image _macVideo = new() { Name = "GallerySoftwareVideo", MinHeight = 100, Stretch = Avalonia.Media.Stretch.Uniform };
    private readonly TextBlock _status = new() { Name = "GalleryVideoStatus", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock _time = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _play = new() { Name = "GalleryVideoPlay" };
    private readonly Button _pause = new() { Name = "GalleryVideoPause" };
    private readonly Button _stop = new() { Name = "GalleryVideoStop" };
    private readonly Slider _seek = new() { Name = "GalleryVideoSeek", Minimum = 0, Maximum = 1000, MinWidth = 140 };
    private readonly Slider _volume = new() { Name = "GalleryVideoVolume", Minimum = 0, Maximum = 100, Value = 70, Width = 100 };
    private readonly SemaphoreSlim _operations = new(1, 1);
    private CancellationTokenSource? _loading;
    private LibVLC? _engine;
    private Playback? _playback;
    private long _generation;
    private long _length;
    private bool _updatingSeek;
    private int _disposed;
    private int _releasedStreams;
    private int _cleanupFailures;
    private bool _autoplay = true;
    private bool _startMuted;
    private bool _settingVolume;

    internal event Action<int>? VolumeChanged;
    internal void Configure(bool autoplay, bool startMuted, int volume)
    {
        _autoplay = autoplay;
        _startMuted = startMuted;
        _settingVolume = true;
        try { _volume.Value = Math.Clamp(volume, 0, 100); }
        finally { _settingVolume = false; }
    }

    internal Task TogglePlaybackAsync() => CommandAsync(player =>
    {
        if (player.IsPlaying) player.SetPause(true);
        else if (player.State == VLCState.Paused) player.SetPause(false);
        else player.Play();
    });

    private sealed record Playback(long Generation, MediaPlayer Player, Media Media, StreamMediaInput Input, Stream Stream, MacVideoSurface? Surface);

    // Acceptance observes the real player under its normal native-operation lock.
    // No alternate decoder, dummy video output or synthetic clock is used.
    internal sealed record PlaybackEvidence(string State, long Time, int DecodedFrames, int DisplayedFrames,
        bool HasPlayback, bool HasEngine, bool Disposed, int ReleasedStreams, int CleanupFailures,
        int SurfaceFrames = 0, bool SurfaceNonUniform = false);

    internal Task<PlaybackEvidence> InspectPlaybackAsync(string? snapshotPath = null) => Task.Run(async () =>
    {
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            Playback? playback = _playback;
            if (playback == null) return new PlaybackEvidence("Stopped", 0, 0, 0, false, _engine != null, _disposed != 0,
                _releasedStreams, _cleanupFailures);
            var stats = playback.Media.Statistics;
            if (snapshotPath != null && !playback.Player.TakeSnapshot(0, snapshotPath, 0, 0))
                throw new InvalidOperationException("The native video output rejected the frame snapshot.");
            return new PlaybackEvidence(playback.Player.State.ToString(), playback.Player.Time,
                stats.DecodedVideo, stats.DisplayedPictures, true, _engine != null, _disposed != 0,
                _releasedStreams, _cleanupFailures, playback.Surface?.PresentedFrames ?? 0, playback.Surface?.NonUniform ?? false);
        }
        finally { _operations.Release(); }
    });

    internal GalleryVideoPlayer()
    {
        _play.Content = Text("Play", "Abspielen"); _pause.Content = Text("Pause", "Pause"); _stop.Content = Text("Stop", "Stopp");
        AutomationProperties.SetName(_play, Text("Play video", "Video abspielen"));
        AutomationProperties.SetName(_pause, Text("Pause video", "Video pausieren"));
        AutomationProperties.SetName(_stop, Text("Stop video", "Video stoppen"));
        AutomationProperties.SetName(_seek, Text("Video position", "Videoposition"));
        AutomationProperties.SetName(_volume, Text("Volume", "Lautstärke"));
        WrapPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (Control control in new Control[] { _play, _pause, _stop, _time, _volume })
        { control.Margin = new Thickness(4); buttons.Children.Add(control); }
        Grid root = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), RowSpacing = 6 };
        Control videoSurface = OperatingSystem.IsMacOS() ? _macVideo : _video;
        root.Children.Add(_status); root.Children.Add(videoSurface); Grid.SetRow(videoSurface, 1);
        root.Children.Add(_seek); Grid.SetRow(_seek, 2);
        root.Children.Add(buttons); Grid.SetRow(buttons, 3); Content = root;
        EnableTransport(false);
        _play.Click += async (_, _) => await CommandAsync(player => { if (player.State == VLCState.Paused) player.SetPause(false); else player.Play(); });
        _pause.Click += async (_, _) => await CommandAsync(player => player.SetPause(true));
        _stop.Click += async (_, _) =>
        {
            await CommandAsync(player => player.Stop());
            UpdateTime(0);
        };
        _volume.PropertyChanged += async (_, args) =>
        {
            if (args.Property == Slider.ValueProperty)
            {
                int volume = (int)_volume.Value;
                bool userChange = !_settingVolume;
                await CommandAsync(player => { player.Volume = volume; if (volume > 0) player.Mute = false; });
                if (userChange) VolumeChanged?.Invoke(volume);
            }
        };
        _seek.PropertyChanged += async (_, args) =>
        {
            if (args.Property != Slider.ValueProperty || _updatingSeek || _length <= 0) return;
            long position = (long)(_seek.Value * _length / 1000);
            await CommandAsync(player => { if (player.IsSeekable) player.Time = position; });
        };
        // These focused sliders keep their own arrow-key behavior; the gallery owns arrows elsewhere.
        DetachedFromVisualTree += async (_, _) => await StopAsync();
    }

    private static string Text(string en, string de) => GalleryViewerText.Get(en, de);
    internal async Task ShowAsync(GalleryEntry entry, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        long generation = Interlocked.Increment(ref _generation);
        CancellationTokenSource loading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = loading.Token;
        CancellationTokenSource? previous = Interlocked.Exchange(ref _loading, loading);
        previous?.Cancel(); previous?.Dispose();
        token.Register(() => Post(generation, () => _ = StopAsync()));
        int volume = (int)_volume.Value;
        EnableTransport(false); _length = 0; _time.Text = string.Empty;
        _status.Text = Text("Verifying local video…", "Lokales Video wird geprüft…");
        await Task.Run(async () =>
        {
            await _operations.WaitAsync().ConfigureAwait(false);
            Stream? stream = null; StreamMediaInput? input = null; Media? media = null; MediaPlayer? player = null; MacVideoSurface? surface = null;
            try
            {
                await ClearPlaybackAsync(generation).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!IsCurrent(generation)) return;
                stream = await entry.Manifest.OpenVerifiedReadAsync(entry.Asset.AssetId, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                NativeVideoRuntime.EnsureInitialized();
                _engine ??= OperatingSystem.IsMacOS()
                    ? new LibVLC("--no-video-title-show", "--no-media-library", "--avcodec-hw=none")
                    : new LibVLC("--no-video-title-show", "--no-media-library");
                player = new MediaPlayer(_engine) { Volume = volume, Mute = _startMuted, EnableKeyInput = false, EnableMouseInput = false };
                if (OperatingSystem.IsMacOS()) surface = new MacVideoSurface(_macVideo, player);
                input = new StreamMediaInput(stream);
                media = new Media(_engine, input);
                player.Playing += (_, _) => Post(generation, () =>
                { _status.Text = string.Empty; EnableTransport(true); });
                player.LengthChanged += (_, args) => Post(generation, () => _length = Math.Max(0, args.Length));
                player.TimeChanged += (_, args) => Post(generation, () => UpdateTime(args.Time));
                player.EndReached += (_, _) => Post(generation, () =>
                {
                    if (_length <= 0) _ = FailAsync(generation);
                    else _status.Text = Text("Playback ended.", "Wiedergabe beendet.");
                });
                player.EncounteredError += (_, _) => Post(generation, () => _ = FailAsync(generation));
                token.ThrowIfCancellationRequested();
                _playback = new Playback(generation, player, media, input, stream, surface);
                player = null; media = null; input = null; stream = null; surface = null;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (IsCurrent(generation) && !OperatingSystem.IsMacOS()) _video.MediaPlayer = _playback.Player;
                });
                token.ThrowIfCancellationRequested();
                if (!IsCurrent(generation)) { await ClearPlaybackAsync(generation).ConfigureAwait(false); return; }
                if (_autoplay)
                {
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
                player?.Dispose(); surface?.Dispose(); media?.Dispose(); input?.Dispose(); stream?.Dispose();
                _operations.Release();
            }
        }).ConfigureAwait(true);
    }

    private bool IsCurrent(long generation) => generation == Volatile.Read(ref _generation) && Volatile.Read(ref _disposed) == 0;
    private void Post(long generation, Action update) => Dispatcher.UIThread.Post(() => { if (IsCurrent(generation)) update(); });
    private async Task FailAsync(long generation)
    {
        if (!IsCurrent(generation)) return;
        await StopAsync();
        if (generation + 1 == Volatile.Read(ref _generation) && Volatile.Read(ref _disposed) == 0)
            _status.Text = Diagnosis(null);
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
        _time.Text = TimeSpan.FromMilliseconds(Math.Max(0, position)).ToString(@"hh\:mm\:ss") + " / " +
            TimeSpan.FromMilliseconds(_length).ToString(@"hh\:mm\:ss");
        // A dragged/focused timeline keeps its own value instead of fighting time notifications.
        if (_seek.IsKeyboardFocusWithin || _seek.IsPointerOver) return;
        _updatingSeek = true;
        try { _seek.Value = _length > 0 ? Math.Clamp(position * 1000d / _length, 0, 1000) : 0; }
        finally { _updatingSeek = false; }
    }
    private void EnableTransport(bool enabled)
    { _play.IsEnabled = _pause.IsEnabled = _stop.IsEnabled = _seek.IsEnabled = _volume.IsEnabled = enabled; }

    private async Task CommandAsync(Action<MediaPlayer> command)
    {
        long generation = Volatile.Read(ref _generation);
        await Task.Run(async () =>
        {
            await _operations.WaitAsync().ConfigureAwait(false);
            try { if (IsCurrent(generation) && _playback != null) command(_playback.Player); }
            catch (Exception) { Post(generation, () => _ = FailAsync(generation)); }
            finally { _operations.Release(); }
        }).ConfigureAwait(true);
    }

    internal async Task StopAsync()
    {
        long generation = Interlocked.Increment(ref _generation);
        CancellationTokenSource? loading = Interlocked.Exchange(ref _loading, null);
        loading?.Cancel(); loading?.Dispose();
        await Dispatcher.UIThread.InvokeAsync(() => { if (generation == Volatile.Read(ref _generation)) { EnableTransport(false); _video.MediaPlayer = null; } });
        await Task.Run(async () =>
        {
            await _operations.WaitAsync().ConfigureAwait(false);
            try { await ClearPlaybackAsync(generation).ConfigureAwait(false); }
            finally { _operations.Release(); }
        }).ConfigureAwait(true);
    }

    // Caller holds _operations on a worker. Stop may wait for native callbacks, never the UI thread.
    private async Task ClearPlaybackAsync(long notNewerThan)
    {
        Playback? playback = _playback;
        if (playback == null || playback.Generation > notNewerThan) return;
        _playback = null;
        await Dispatcher.UIThread.InvokeAsync(() => { if (ReferenceEquals(_video.MediaPlayer, playback.Player)) _video.MediaPlayer = null; });
        // Cleanup must release the verified file even when a backend operation fails.
        try { playback.Player.Stop(); } catch (Exception) { _cleanupFailures++; }
        try { playback.Player.Dispose(); playback.Surface?.Dispose(); } catch (Exception) { _cleanupFailures++; }
        try { playback.Media.Dispose(); } catch (Exception) { _cleanupFailures++; }
        try { playback.Input.Dispose(); } catch (Exception) { _cleanupFailures++; }
        try
        {
            playback.Stream.Dispose();
            if (!playback.Stream.CanRead) _releasedStreams++;
            else _cleanupFailures++;
        }
        catch (Exception) { _cleanupFailures++; }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAsync();
        await Task.Run(async () =>
        {
            await _operations.WaitAsync().ConfigureAwait(false);
            try { _engine?.Dispose(); _engine = null; }
            finally { _operations.Release(); }
        }).ConfigureAwait(true);
    }
}

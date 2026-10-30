using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Gallery;
using MeowChan.Storage;
using SkiaSharp;

namespace MeowChan;

public partial class MainWindow
{
    // Explicit packaged-app acceptance only. No providers, remote URLs or user profile are used.
    internal async Task CompleteApphostMediaSmokeAsync(string fixture)
    {
        string profile = _session.ProfileDirectory;
        string evidencePath = Path.Combine(profile, "apphost-playback.json");
        string framePath = Path.Combine(profile, "apphost-playback-frame.png");
        List<GalleryVideoPlayer.PlaybackEvidence> samples = new();
        List<string> checks = new();
        bool completed = false;
        string? failure = null;
        GalleryVideoPlayer.PlaybackEvidence? imageCleanup = null, closeCleanup = null, shutdownCleanup = null;
        object? poster = null;
        GalleryVideoPlayer.PlaybackEvidence? repeatAfterSwitch = null, reopenedPlayback = null;
        string? persistedMode = null;
        int? postCloseRestarts = null, postShutdownRestarts = null;
        void SaveEvidence() => File.WriteAllText(evidencePath, JsonSerializer.Serialize(new
        {
            schemaVersion = 2, completed, failure, fixtureSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture))),
            samples, checks, imageCleanup, closeCleanup, shutdownCleanup, poster,
            repeatAfterSwitch, reopenedPlayback, persistedMode, postCloseRestarts, postShutdownRestarts,
            scope = "actual apphost, production verified gallery/native video output; no audio-device, screen-pixel or hardware-acceleration claim"
        }, new JsonSerializerOptions { WriteIndented = true }));
        try
        {
            await _restoreTask;
            RequireMediaSmoke(_restoreCompleted && _session.State == DesktopSessionState.Ready && IsVisible,
                "The actual main window/session did not become ready.");
            string mediaDirectory = Path.Combine(profile, "playback-fixture");
            Directory.CreateDirectory(mediaDirectory);
            byte[] video = await File.ReadAllBytesAsync(fixture);
            string videoName = "clip" + Path.GetExtension(fixture).ToLowerInvariant();
            await File.WriteAllBytesAsync(Path.Combine(mediaDirectory, videoName), video);
            using SKBitmap bitmap = new(96, 64);
            bitmap.Erase(SKColors.CornflowerBlue);
            using SKImage image = SKImage.FromBitmap(bitmap);
            using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
            byte[] picture = png.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(mediaDirectory, "image.png"), picture);
            object Entry(string id, string name, byte[] bytes) => new
            {
                MediaIdentifier = id, SourceUrl = "https://offline.invalid/" + name, FileName = name,
                Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                Md5 = Convert.ToBase64String(MD5.HashData(bytes))
            };
            await File.WriteAllTextAsync(Path.Combine(mediaDirectory, ".meowchan-manifest.json"), JsonSerializer.Serialize(new
            {
                Version = 1, Entries = new Dictionary<string, object>
                {
                    ["image.png"] = Entry("image", "image.png", picture), [videoName] = Entry("video", videoName, video)
                }
            }));
            MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(mediaDirectory);
            RequireMediaSmoke(manifest.Warning == null && manifest.Count == 2, "The real gallery manifest was not readable.");
            GalleryEntry MakeEntry(MediaAssetReference asset) => new(
                new GalleryAssetKey(new ResourceId("smoke", "local", "1"), manifest.Directory, asset.AssetId),
                manifest, asset, null, null, false, true);
            GalleryEntry[] entries = manifest.GetPage().OrderBy(asset => Path.GetFileName(asset.Path) == videoName ? 1 : 0)
                .Select(MakeEntry).ToArray();
            string posterCache = Path.Combine(profile, "cache", "video-posters-v1");
            byte[] posterBytes = await VideoPosterCache.GetPngAsync(manifest, entries[1].Asset, posterCache, default)
                ?? throw new InvalidOperationException("The native video-poster decoder produced no PNG.");
            using (SKBitmap? decodedPoster = SKBitmap.Decode(posterBytes))
            {
                RequireMediaSmoke(decodedPoster != null && decodedPoster.Width is > 0 and <= 960 && decodedPoster.Height is > 0 and <= 640,
                    "The generated video poster is not a bounded image.");
                bool varied = decodedPoster!.Pixels.Distinct().Take(2).Count() == 2;
                RequireMediaSmoke(varied, "The generated video poster contains no varied fixture pixels.");
                string cacheFile = Path.Combine(posterCache, entries[1].Asset.Sha256.ToLowerInvariant() + ".png");
                RequireMediaSmoke(File.Exists(cacheFile) && new FileInfo(cacheFile).Length > 0,
                    "The video poster was not published to the persistent cache.");
                DateTime originalWrite = File.GetLastWriteTimeUtc(cacheFile);
                MediaAssetSnapshot reopened = await MediaAssetReader.OpenAsync(mediaDirectory);
                MediaAssetReference videoAgain = reopened.GetPage().Single(asset => asset.AssetId == entries[1].Asset.AssetId);
                byte[]? cached = await VideoPosterCache.GetPngAsync(reopened, videoAgain, posterCache, default);
                RequireMediaSmoke(cached != null && File.Exists(cacheFile) && posterBytes.SequenceEqual(cached)
                    && originalWrite == File.GetLastWriteTimeUtc(cacheFile),
                    "Reopening the manifest did not reuse the persistent video-poster cache unchanged.");
                await File.WriteAllBytesAsync(Path.Combine(profile, "apphost-playback-poster.png"), posterBytes);
                poster = new { Generated = true, PersistentCacheHit = true, NonUniform = varied, decodedPoster.Width,
                    decodedPoster.Height, Sha256 = Convert.ToHexString(SHA256.HashData(posterBytes)) };
            }
            checks.Add("video-poster-cache"); SaveEvidence();
            await OpenMediaGalleryAsync(entries, entries[0].EntryKey);
            GalleryViewer viewer = _gallery ?? throw new InvalidOperationException("Gallery did not open.");
            await WaitMediaSmokeAsync(() => Task.FromResult(viewer.GetGalleryVisualDescendants().OfType<Image>()
                .Any(control => control.Name == "GalleryImage" && control.IsEffectivelyVisible && control.Source is Bitmap)), "initial image");
            checks.Add("initial-image");
            ClickMediaSmoke(viewer, "GalleryNext");
            await WaitMediaSmokeAsync(() => Task.FromResult(viewer.GetGalleryVisualDescendants().OfType<GalleryVideoPlayer>().Any()), "native video player");
            GalleryVideoPlayer player = viewer.GetGalleryVisualDescendants().OfType<GalleryVideoPlayer>().Single();
            async Task<GalleryVideoPlayer.PlaybackEvidence> PlayingSampleAsync()
            {
                GalleryVideoPlayer.PlaybackEvidence? result = null;
                await WaitMediaSmokeAsync(async () =>
                {
                    result = await player.InspectPlaybackAsync();
                    return result.State == "Playing" && result.Time >= 300 && result.DecodedFrames >= 3 && result.DisplayedFrames >= 2
                        && (OperatingSystem.IsWindows() || (result.SurfaceFrames >= 2 && result.SurfaceNonUniform))
                        && MediaSmokeControl<Button>(viewer, "GalleryVideoPlayPause").IsEffectivelyEnabled
                        && player.IsEffectivelyVisible && player.Bounds.Width > 0 && player.Bounds.Height > 0;
                }, "native decoded/displayed frames and enabled transport");
                samples.Add(result!); SaveEvidence(); return result!;
            }
            var first = await PlayingSampleAsync();
            RequireMediaSmoke(!first.LoopEnabled && first.LoopRestarts == 0, "Repeat must default off.");
            RequireMediaSmoke(viewer.GetGalleryVisualDescendants().OfType<Button>().Count(button => button.Name == "GalleryVideoPlayPause") == 1
                && !viewer.GetGalleryVisualDescendants().OfType<Button>().Any(button => button.Name is "GalleryVideoPlay" or "GalleryVideoPause" or "GalleryVideoStop"),
                "Video transport must expose one playback toggle without a visible Stop.");
            checks.Add("single-stateful-playback-button"); checks.Add("repeat-default-off");
            await WaitMediaSmokeAsync(async () =>
            {
                var current = await player.InspectPlaybackAsync();
                if (current.Time < first.Time + 250 || current.DecodedFrames <= first.DecodedFrames || current.DisplayedFrames <= first.DisplayedFrames) return false;
                if (!OperatingSystem.IsWindows() && current.SurfaceFrames <= first.SurfaceFrames) return false;
                samples.Add(current); return true;
            }, "native clock and frame progression");
            checks.Add("native-time-and-frame-progress");
            await player.InspectPlaybackAsync(framePath);
            await WaitMediaSmokeAsync(() =>
            {
                try
                {
                    if (!File.Exists(framePath) || new FileInfo(framePath).Length == 0) return Task.FromResult(false);
                    using FileStream frame = File.OpenRead(framePath);
                    var info = GalleryImageDecoder.Inspect(frame);
                    return Task.FromResult(info.Width > 0 && info.Height > 0);
                }
                catch (IOException) { return Task.FromResult(false); }
                catch (InvalidDataException) { return Task.FromResult(false); }
            }, "complete native frame snapshot");
            checks.Add("native-output-snapshot");
            ClickMediaSmoke(viewer, "GalleryVideoPlayPause");
            await WaitMediaSmokeAsync(async () => (await player.InspectPlaybackAsync()).State == "Paused", "pause state");
            var paused = await player.InspectPlaybackAsync();
            await Task.Delay(350);
            var stillPaused = await player.InspectPlaybackAsync();
            RequireMediaSmoke(stillPaused.State == "Paused" && Math.Abs(stillPaused.Time - paused.Time) <= 100,
                "Pause did not hold the native playback clock.");
            samples.Add(stillPaused); checks.Add("pause-clock-held");
            await WaitMediaSmokeAsync(() => Task.FromResult(AutomationProperties.GetName(MediaSmokeControl<Button>(viewer, "GalleryVideoPlayPause"))
                == MeowChan.Desktop.Localization.Texts.T("viewer.video.play")), "paused accessible Play action");
            ClickMediaSmoke(viewer, "GalleryVideoPlayPause");
            await PlayingSampleAsync();
            await WaitMediaSmokeAsync(async () => (await player.InspectPlaybackAsync()).Time > stillPaused.Time + 200,
                "resumed native clock");
            checks.Add("play-resumed");
            if (!OperatingSystem.IsWindows())
            {
                for (int restart = 0; restart < 6; restart++)
                {
                    int framesBefore = (await player.InspectPlaybackAsync()).SurfaceFrames;
                    await player.StopPlaybackAsync(); // Retain the real native stop/replay regression without a visible Stop button.
                    await WaitMediaSmokeAsync(async () => (await player.InspectPlaybackAsync()).State == "Stopped", "software-output stop");
                    ClickMediaSmoke(viewer, "GalleryVideoPlayPause");
                    await PlayingSampleAsync();
                    await WaitMediaSmokeAsync(async () => (await player.InspectPlaybackAsync()).SurfaceFrames > framesBefore + 2,
                        "software-output frame progression after restart");
                }
                checks.Add("software-output-six-stop-play-cycles");
            }
            var audio = await player.InspectPlaybackAsync(); samples.Add(audio);
            var muteButton = MediaSmokeControl<Button>(viewer, "GalleryVideoMute");
            var volumeSlider = MediaSmokeControl<Slider>(viewer, "GalleryVideoVolume");
            if (audio.AudioAvailable)
            {
                await WaitMediaSmokeAsync(() => Task.FromResult(muteButton.IsEnabled && volumeSlider.IsEnabled), "audio controls enabled");
                bool initialMute = audio.Muted;
                ClickMediaSmoke(viewer, "GalleryVideoMute");
                await WaitMediaSmokeAsync(async () => (await player.InspectPlaybackAsync()).Muted != initialMute, "mute native state");
                samples.Add(await player.InspectPlaybackAsync());
                ClickMediaSmoke(viewer, "GalleryVideoMute");
                await WaitMediaSmokeAsync(async () => (await player.InspectPlaybackAsync()).Muted == initialMute, "restore native mute state");
                checks.Add("mute-unmute-state");
            }
            else
            {
                await WaitMediaSmokeAsync(() => Task.FromResult(!muteButton.IsEnabled && !volumeSlider.IsEnabled), "unavailable audio controls disabled");
                RequireMediaSmoke(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(muteButton))
                    && !string.IsNullOrWhiteSpace(ToolTip.GetTip(volumeSlider)?.ToString()), "Unavailable audio controls lack an accessible explanation.");
                checks.Add(audio.AudioTracks == 0 ? "no-audio-track-controls" : "audio-output-unavailable-controls");
            }
            checks.Add("audio-controls-availability");
            await WaitMediaSmokeAsync(async () => { var state = await player.InspectPlaybackAsync(); return state.Seekable && state.Length > 0; }, "seekable duration");
            ClickMediaSmoke(viewer, "GalleryVideoLoop");
            RequireMediaSmoke((await player.InspectPlaybackAsync()).LoopEnabled, "Repeat toggle did not activate the current video.");
            MediaSmokeControl<Slider>(viewer, "GalleryVideoSeek").Value = 980;
            await WaitMediaSmokeAsync(async () =>
            { var state = await player.InspectPlaybackAsync(); return state.LoopRestarts >= 1 && state.State == "Playing" && state.Time < state.Length / 2; }, "EndReached repeat restart");
            samples.Add(await player.InspectPlaybackAsync());
            checks.Add("repeat-ended-restarted");
            ClickMediaSmoke(viewer, "GalleryVideoLoop");
            int repeats = (await player.InspectPlaybackAsync()).LoopRestarts;
            MediaSmokeControl<Slider>(viewer, "GalleryVideoSeek").Value = 970;
            await WaitMediaSmokeAsync(async () => (await player.InspectPlaybackAsync()).State == "Ended", "repeat disabled at end");
            await Task.Delay(250);
            var ended = await player.InspectPlaybackAsync(); samples.Add(ended);
            RequireMediaSmoke(!ended.LoopEnabled && ended.LoopRestarts == repeats && ended.State == "Ended", "Disabled repeat restarted the media.");
            checks.Add("repeat-disabled-at-end");
            ClickMediaSmoke(viewer, "GalleryVideoPlayPause"); await PlayingSampleAsync();
            ClickMediaSmoke(viewer, "GalleryVideoLoop");
            ClickMediaSmoke(viewer, "GalleryPrevious");
            await WaitMediaSmokeAsync(async () =>
            {
                imageCleanup = await player.InspectPlaybackAsync();
                return !imageCleanup.HasPlayback && imageCleanup.LoopEnabled && imageCleanup.ReleasedStreams >= 1 && imageCleanup.CleanupFailures == 0
                    && viewer.CaptureState().Key == entries[0].Key
                    && MediaSmokeControl<Button>(viewer, "GalleryActual").IsEffectivelyEnabled;
            }, "video-to-image stream cleanup");
            checks.Add("video-image-stream-released");
            checks.Add("repeat-retained-across-media");
            ClickMediaSmoke(viewer, "GalleryNext");
            await PlayingSampleAsync();
            checks.Add("back-to-video");
            RequireMediaSmoke((await player.InspectPlaybackAsync()).LoopEnabled, "A new media generation lost the chosen repeat preference.");
            MediaSmokeControl<Slider>(viewer, "GalleryVideoSeek").Value = 980;
            await WaitMediaSmokeAsync(async () =>
            {
                repeatAfterSwitch = await player.InspectPlaybackAsync();
                return repeatAfterSwitch.LoopEnabled && repeatAfterSwitch.LoopRestarts >= 1
                    && repeatAfterSwitch.State == "Playing" && repeatAfterSwitch.Time < repeatAfterSwitch.Length / 2;
            }, "inherited repeat actually restarts the next video");
            checks.Add("inherited-repeat-ended-restarted");
            MediaSmokeControl<Slider>(viewer, "GalleryVideoSeek").Value = 980;
            ClickMediaSmoke(viewer, "GalleryClose");
            await GalleryCleanupTask;
            closeCleanup = await player.InspectPlaybackAsync();
            RequireMediaSmoke(closeCleanup.Disposed && !closeCleanup.HasPlayback && !closeCleanup.HasEngine && closeCleanup.LoopEnabled
                && closeCleanup.ReleasedStreams >= 2 && closeCleanup.CleanupFailures == 0,
                "Gallery close did not dispose the native player, engine and verified stream.");
            checks.Add("gallery-close-disposed");
            int afterCloseRepeats = closeCleanup.LoopRestarts;
            await Task.Delay(200);
            postCloseRestarts = (await player.InspectPlaybackAsync()).LoopRestarts;
            RequireMediaSmoke(postCloseRestarts == afterCloseRepeats,
                "A pending EndReached callback restarted a closed video.");
            persistedMode = Config.LoadConfig(profile).GalleryPlaybackMode;
            RequireMediaSmoke(persistedMode == "Repeat", "Gallery close did not persist the chosen repeat mode.");
            checks.Add("repeat-close-no-restart");
            // Also exercise ordinary app shutdown while a fresh native player is actively playing.
            await OpenMediaGalleryAsync(entries, entries[1].EntryKey);
            viewer = _gallery ?? throw new InvalidOperationException("Gallery did not reopen.");
            await WaitMediaSmokeAsync(() => Task.FromResult(viewer.GetGalleryVisualDescendants().OfType<GalleryVideoPlayer>().Any()), "shutdown player");
            player = viewer.GetGalleryVisualDescendants().OfType<GalleryVideoPlayer>().Single();
            reopenedPlayback = await PlayingSampleAsync();
            RequireMediaSmoke(reopenedPlayback.LoopEnabled, "Reopened gallery did not restore the chosen repeat mode.");
            checks.Add("repeat-profile-restored-on-reopen");
            await CloseApplicationAsync();
            shutdownCleanup = await player.InspectPlaybackAsync();
            RequireMediaSmoke(_allowClose && !IsVisible && shutdownCleanup.Disposed && !shutdownCleanup.HasPlayback && !shutdownCleanup.HasEngine
                && shutdownCleanup.ReleasedStreams >= 1 && shutdownCleanup.CleanupFailures == 0,
                "Normal application shutdown did not finish native playback cleanup.");
            checks.Add("playing-shutdown-disposed");
            await Task.Delay(200);
            postShutdownRestarts = (await player.InspectPlaybackAsync()).LoopRestarts;
            RequireMediaSmoke(postShutdownRestarts == shutdownCleanup.LoopRestarts, "Shutdown accepted a stale repeat callback.");
            completed = true;
        }
        catch (Exception exception) { failure = exception.ToString(); throw; }
        finally { SaveEvidence(); }
    }

    private static async Task WaitMediaSmokeAsync(Func<Task<bool>> condition, string stage)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!await condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Apphost playback stage timed out: " + stage);
            await Task.Delay(100);
        }
    }
    private static T MediaSmokeControl<T>(GalleryViewer viewer, string name) where T : Control =>
        viewer.GetGalleryVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void ClickMediaSmoke(GalleryViewer viewer, string name) =>
        MediaSmokeControl<Button>(viewer, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void RequireMediaSmoke(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}

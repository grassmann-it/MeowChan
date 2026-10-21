using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
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
        void SaveEvidence() => File.WriteAllText(evidencePath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, completed, failure, fixtureSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture))),
            samples, checks, imageCleanup, closeCleanup, shutdownCleanup, poster,
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
            await File.WriteAllBytesAsync(Path.Combine(mediaDirectory, "clip.webm"), video);
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
                    ["image.png"] = Entry("image", "image.png", picture), ["clip.webm"] = Entry("video", "clip.webm", video)
                }
            }));
            MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(mediaDirectory);
            RequireMediaSmoke(manifest.Warning == null && manifest.Count == 2, "The real gallery manifest was not readable.");
            GalleryEntry MakeEntry(MediaAssetReference asset) => new(
                new GalleryAssetKey(new ResourceId("smoke", "local", "1"), manifest.Directory, asset.AssetId),
                manifest, asset, null, null, false, true);
            GalleryEntry[] entries = manifest.GetPage().OrderBy(asset => asset.Path.EndsWith(".webm", StringComparison.Ordinal) ? 1 : 0)
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
                        && MediaSmokeControl<Button>(viewer, "GalleryVideoPause").IsEffectivelyEnabled
                        && player.IsEffectivelyVisible && player.Bounds.Width > 0 && player.Bounds.Height > 0;
                }, "native decoded/displayed frames and enabled transport");
                samples.Add(result!); SaveEvidence(); return result!;
            }
            var first = await PlayingSampleAsync();
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
            ClickMediaSmoke(viewer, "GalleryVideoPause");
            await WaitMediaSmokeAsync(async () => (await player.InspectPlaybackAsync()).State == "Paused", "pause state");
            var paused = await player.InspectPlaybackAsync();
            await Task.Delay(350);
            var stillPaused = await player.InspectPlaybackAsync();
            RequireMediaSmoke(stillPaused.State == "Paused" && Math.Abs(stillPaused.Time - paused.Time) <= 100,
                "Pause did not hold the native playback clock.");
            samples.Add(stillPaused); checks.Add("pause-clock-held");
            ClickMediaSmoke(viewer, "GalleryVideoPlay");
            await PlayingSampleAsync();
            await WaitMediaSmokeAsync(async () => (await player.InspectPlaybackAsync()).Time > stillPaused.Time + 200,
                "resumed native clock");
            checks.Add("play-resumed");
            if (!OperatingSystem.IsWindows())
            {
                for (int restart = 0; restart < 6; restart++)
                {
                    int framesBefore = (await player.InspectPlaybackAsync()).SurfaceFrames;
                    ClickMediaSmoke(viewer, "GalleryVideoStop");
                    await WaitMediaSmokeAsync(async () => (await player.InspectPlaybackAsync()).State == "Stopped", "software-output stop");
                    ClickMediaSmoke(viewer, "GalleryVideoPlay");
                    await PlayingSampleAsync();
                    await WaitMediaSmokeAsync(async () => (await player.InspectPlaybackAsync()).SurfaceFrames > framesBefore + 2,
                        "software-output frame progression after restart");
                }
                checks.Add("software-output-six-stop-play-cycles");
            }
            ClickMediaSmoke(viewer, "GalleryPrevious");
            await WaitMediaSmokeAsync(async () =>
            {
                imageCleanup = await player.InspectPlaybackAsync();
                return !imageCleanup.HasPlayback && imageCleanup.ReleasedStreams >= 1 && imageCleanup.CleanupFailures == 0
                    && viewer.CaptureState().Key == entries[0].Key
                    && MediaSmokeControl<Button>(viewer, "GalleryActual").IsEffectivelyEnabled;
            }, "video-to-image stream cleanup");
            checks.Add("video-image-stream-released");
            ClickMediaSmoke(viewer, "GalleryNext");
            await PlayingSampleAsync();
            checks.Add("back-to-video");
            ClickMediaSmoke(viewer, "GalleryClose");
            await GalleryCleanupTask;
            closeCleanup = await player.InspectPlaybackAsync();
            RequireMediaSmoke(closeCleanup.Disposed && !closeCleanup.HasPlayback && !closeCleanup.HasEngine
                && closeCleanup.ReleasedStreams >= 2 && closeCleanup.CleanupFailures == 0,
                "Gallery close did not dispose the native player, engine and verified stream.");
            checks.Add("gallery-close-disposed");
            // Also exercise ordinary app shutdown while a fresh native player is actively playing.
            await OpenMediaGalleryAsync(entries, entries[1].EntryKey);
            viewer = _gallery ?? throw new InvalidOperationException("Gallery did not reopen.");
            await WaitMediaSmokeAsync(() => Task.FromResult(viewer.GetGalleryVisualDescendants().OfType<GalleryVideoPlayer>().Any()), "shutdown player");
            player = viewer.GetGalleryVisualDescendants().OfType<GalleryVideoPlayer>().Single();
            await PlayingSampleAsync();
            await CloseApplicationAsync();
            shutdownCleanup = await player.InspectPlaybackAsync();
            RequireMediaSmoke(_allowClose && !IsVisible && shutdownCleanup.Disposed && !shutdownCleanup.HasPlayback && !shutdownCleanup.HasEngine
                && shutdownCleanup.ReleasedStreams >= 1 && shutdownCleanup.CleanupFailures == 0,
                "Normal application shutdown did not finish native playback cleanup.");
            checks.Add("playing-shutdown-disposed");
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

using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Testing;
using SkiaSharp;

namespace MeowChan.DesktopSmoke;

// Root registers MEOWCHAN_GIF_PLAYBACK_ONLY. No registration/fixture/product edits here.
internal static class GifPlaybackChecks
{
    internal static Task RunAsync(MainWindow owner) => GalleryPlaybackFeatureChecks.RunGifPlaybackChecksAsync(owner);
}

internal static partial class GalleryPlaybackFeatureChecks
{
    private sealed record GifPixels(string Hash, int Width, int Height, string Bounds, SKColor Center);

    internal static async Task RunGifPlaybackChecksAsync(MainWindow owner)
    {
        if (OperatingSystem.IsLinux() ? Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") != "1"
            : Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_NATIVE_ALLOWED") != "1")
            throw new PlatformNotSupportedException("GIF playback requires an authorized disposable native desktop; no personal display fallback.");
        string directory = TemporaryWorkspacePaths.CreateDirectory("meowchan-gif-gui-");
        string language = Texts.Language;
        try
        {
            ShortcutService.Publish(new ShortcutBindingsSection()); // Isolated session only; never save owner settings.
            var data = await GifFixtureBytesAsync();
            GalleryEntry animated = await AssetAsync(directory, "gif-loop", "animated-loop.gif", data["animated-loop.gif"]);
            GalleryEntry finite = await AssetAsync(directory, "gif-finite", "finite-loop.gif", data["finite-loop.gif"]);
            GalleryEntry disposal = await AssetAsync(directory, "gif-disposal", "disposal-transparency.gif", data["disposal-transparency.gif"]);
            GalleryEntry single = await AssetAsync(directory, "gif-single", "single-frame.gif", data["single-frame.gif"]);
            GalleryEntry corrupt = await AssetAsync(directory, "gif-corrupt", "truncated.gif", data["truncated.gif"]);
            GalleryEntry png = await ImageAsync(directory, "gif-successor", SKColors.Purple);
            await CheckGifPixelsAndTransportAsync(owner, animated, png);
            await CheckGifEncodedLoopsAsync(owner, animated, finite, single, disposal);
            await CheckGifSlideshowAsync(owner, animated, png);
            foreach (var video in new[]
            {
                (Path: Environment.GetEnvironmentVariable("MEOWCHAN_VIDEO_FIXTURE") ?? GifFixturePath("tests/Fixtures/gallery-playback-vp8.webm"), Extension: ".webm"),
                (Path: Environment.GetEnvironmentVariable("MEOWCHAN_MP4_FIXTURE") ?? GifFixturePath("tests/Fixtures/release102/playback-h264-silent.mp4"), Extension: ".mp4")
            })
            {
                Require(File.Exists(video.Path) && Path.GetExtension(video.Path).Equals(video.Extension, StringComparison.OrdinalIgnoreCase),
                    "GIF transition requires distinct existing synthetic WebM AND MP4 fixtures: " + video.Path);
                byte[] bytes = await File.ReadAllBytesAsync(video.Path);
                string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                string[] allowed = video.Extension == ".webm"
                    ? ["92f5175697fa441fd6a95270ef307a55c51bc4d30b019f571dc99bf45d2fdddf",
                       "4356f0c4eaa9cbc57cac91481b99dfa233da6c2664c2e4e8ef1504e68c39f7c7",
                       "a88f7ec3fa76c11c38719662ba41d5538a5775cc58afce5bf32e14e0cd5d1ad8"]
                    : ["7c15acb2dc63822b37a3ebd69207b520d2fc8e1bf270f5c53d17bb068c5584be",
                       "677947056fdb71789e9ac9da651f8257adaf1e1b3ec1ccb8dfda48d9f9de0929"];
                Require(allowed.Contains(sha), "GIF successor is not an existing pinned synthetic WebM/MP4 fixture.");
                GalleryEntry clip = await AssetAsync(directory, "gif-follow-" + video.Extension.TrimStart('.'), "follow" + video.Extension, bytes);
                await CheckGifVideoSuccessorAsync(owner, animated, clip, png);
            }
            GalleryEntry tampered = await AssetAsync(directory, "gif-tampered", "tampered.gif", data["animated-loop.gif"]);
            byte[] changed = data["animated-loop.gif"].ToArray(); changed[^1] ^= 0x01;
            await File.WriteAllBytesAsync(tampered.Asset.Path, changed); // Own temporary original, NOT repository fixture/manifest.
            bool rejected = false;
            try { await using Stream ignored = await tampered.Manifest.OpenVerifiedReadAsync(tampered.Asset.AssetId); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "Existing verified media path accepted a same-length GIF checksum mismatch.");
            await CheckGifFailureAsync(owner, corrupt, animated);
            await CheckGifFailureAsync(owner, tampered, animated);
            await CheckGifReaderAssociationAsync(owner, directory, data["animated-loop.gif"], await File.ReadAllBytesAsync(png.Asset.Path));
            Console.WriteLine("SMOKE GIF playback: rendered GalleryImage pixels, native pause/resume, encoded loops/end, disposal, slideshow, WebM/MP4 successors, verified failures and native Reader association passed on this recorded platform only.");
        }
        finally
        {
            Texts.SetLanguage(language);
            ShortcutService.Publish(owner.SessionHost.Config.ShortcutBindings);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string GifFixturePath(string relative)
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (DirectoryInfo? parent = new(start); parent != null; parent = parent.Parent)
            {
                string candidate = Path.Combine(parent.FullName, relative);
                if (File.Exists(candidate)) return candidate;
            }
        throw new FileNotFoundException("Required checked-in synthetic fixture is absent; no download/generation/skip: " + relative);
    }

    private static async Task<Dictionary<string, byte[]>> GifFixtureBytesAsync()
    {
        string manifestPath = GifFixturePath("tests/Fixtures/gif-playback/manifest.json");
        using JsonDocument manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        Dictionary<string, byte[]> result = new(StringComparer.Ordinal);
        foreach (string name in new[] { "animated-loop.gif", "finite-loop.gif", "disposal-transparency.gif", "single-frame.gif", "truncated.gif" })
        {
            JsonElement item = manifest.RootElement.GetProperty("fixtures").EnumerateArray().Single(item => item.GetProperty("file").GetString() == name);
            byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(manifestPath)!, name));
            string sha = Convert.ToHexString(SHA256.HashData(bytes));
            Require(bytes.Length == item.GetProperty("bytes").GetInt32()
                && sha.Equals(item.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase), "GIF source fixture hash mismatch: " + name);
            if (name != "truncated.gif")
            {
                int expectedFrames = name == "single-frame.gif" ? 1 : name == "disposal-transparency.gif" ? 4 : 3;
                int expectedRepeats = name == "single-frame.gif" ? 0 : name == "finite-loop.gif" ? 2 : -1;
                Require(item.GetProperty("frame_count").GetInt32() == expectedFrames
                    && item.GetProperty("repetition_count").GetInt32() == expectedRepeats,
                    "GIF fixture metadata drifted from the explicit playback contract: " + name);
            }
            result.Add(name, bytes);
            Console.WriteLine("SMOKE gif-source " + JsonSerializer.Serialize(new { name, sha256 = sha, bytes = bytes.Length }));
        }
        return result;
    }

    private static GifPlaybackEvidence GifState(GalleryViewer viewer) => viewer.GifEvidence is { } state
        ? state : throw new InvalidOperationException("Verified GIF has no playback evidence.");
    private static GalleryGifPlayback GifOwner(GalleryViewer viewer) => viewer.GifPlayback
        ?? throw new InvalidOperationException("Verified GIF has no lifetime owner.");

    private static async Task<Image> GifStableImageAsync(GalleryViewer viewer)
    {
        Image image = Find<Image>(viewer, "GalleryImage");
        await UntilAsync(() => image.IsEffectivelyVisible && image.Source is Bitmap
            && image.Bounds.Width > 0 && image.Bounds.Height > 0, "GIF image is not actually visible.");
        string? previous = null;
        int same = 0;
        await UntilAsync(async () =>
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            string bounds = image.Bounds.ToString();
            same = bounds == previous ? same + 1 : 0; previous = bounds;
            await Task.Delay(30);
            return same >= 3;
        }, "GIF image layout never stabilized before pixel observation.");
        return image;
    }

    // Render ONLY the actual GalleryImage visual. Animated chrome, counters, cursor,
    // clocks and overlays cannot make this hash change. Hash decoded pixel bytes, not PNG metadata.
    private static GifPixels GifReadPixels(Image image)
    {
        Require(image.IsEffectivelyVisible && image.Source is Bitmap && image.Bounds.Width > 0 && image.Bounds.Height > 0,
            "Attempted pixel observation of a hidden/unready GalleryImage.");
        double scale = TopLevel.GetTopLevel(image)?.RenderScaling ?? throw new InvalidOperationException("Image has no render owner.");
        PixelSize size = PixelSize.FromSize(image.Bounds.Size, scale);
        Require(size.Width is > 0 and <= 4096 && size.Height is > 0 and <= 4096, "GIF pixel capture is unbounded.");
        using RenderTargetBitmap rendered = new(size, new Vector(96 * scale, 96 * scale));
        rendered.Render(image);
        using MemoryStream png = new(); rendered.Save(png, PngBitmapEncoderOptions.Default);
        using SKBitmap pixels = SKBitmap.Decode(png.ToArray()) ?? throw new InvalidDataException("Image visual produced no raster pixels.");
        byte[] raw = new byte[pixels.ByteCount]; Marshal.Copy(pixels.GetPixels(), raw, 0, raw.Length);
        return new(Convert.ToHexString(SHA256.HashData(raw)), pixels.Width, pixels.Height,
            image.Bounds.ToString(), pixels.GetPixel(pixels.Width / 2, pixels.Height / 2));
    }

    private static async Task GifChangingPixelsAsync(GalleryViewer viewer, string stage, bool solidColors = true)
    {
        Image image = await GifStableImageAsync(viewer);
        GifPixels first = GifReadPixels(image);
        HashSet<string> hashes = new(StringComparer.Ordinal) { first.Hash };
        HashSet<SKColor> centers = new() { first.Center };
        bool IsPrimary(SKColor color) => color.Alpha >= 250
            && (color.Red >= 250 && color.Green <= 3 && color.Blue <= 3
                || color.Green >= 250 && color.Red <= 3 && color.Blue <= 3
                || color.Blue >= 250 && color.Red <= 3 && color.Green <= 3);
        Require(!solidColors || IsPrimary(first.Center), "Displayed three-color GIF has no visible opaque primary-color center: " + stage);
        Stopwatch clock = Stopwatch.StartNew();
        do
        {
            await Task.Delay(23);
            GifPixels sample = GifReadPixels(image);
            Require(sample.Width == first.Width && sample.Height == first.Height && sample.Bounds == first.Bounds,
                "Changing geometry, not changing GIF pixels, invalidated the observation: " + stage);
            Require(!solidColors || IsPrimary(sample.Center), "GIF raster became blank/non-fixture pixels: " + stage);
            hashes.Add(sample.Hash); centers.Add(sample.Center);
        } while ((hashes.Count < 2 || solidColors && centers.Count < 2) && clock.Elapsed < TimeSpan.FromSeconds(2));
        Console.WriteLine("SMOKE gif-visible-pixels " + JsonSerializer.Serialize(new
        { stage, visual = "GalleryImage only", first.Width, first.Height, first.Bounds, hashes,
            centerColors = centers.Select(color => color.ToString()).ToArray(), elapsedMs = clock.ElapsedMilliseconds, state = viewer.GifEvidence }));
        Require(hashes.Count >= 2 && (!solidColors || centers.Count >= 2), "GIF remained a static frame for two seconds in the actual GalleryImage: " + stage);
    }

    private static async Task GifStablePixelsAsync(GalleryViewer viewer, string stage, int milliseconds = 320)
    {
        Image image = await GifStableImageAsync(viewer);
        GifPixels first = GifReadPixels(image);
        await HoldAsync(TimeSpan.FromMilliseconds(milliseconds), () =>
        {
            GifPixels current = GifReadPixels(image);
            Require(current == first, "Paused/static/current successor image received late/changing pixels: " + stage);
        });
        Console.WriteLine("SMOKE gif-stable-pixels " + JsonSerializer.Serialize(new { stage, first.Hash, milliseconds, state = viewer.GifEvidence }));
    }

    private static async Task GifCurrentColorAsync(GalleryViewer viewer, string stage)
    {
        Image image = await GifStableImageAsync(viewer);
        GifPlaybackEvidence before = GifState(viewer);
        GifPixels pixels = GifReadPixels(image);
        GifPlaybackEvidence after = GifState(viewer);
        Require(before.FrameIndex == after.FrameIndex && before.FramesPresented == after.FramesPresented,
            "GIF changed frame during a synchronous current-frame observation.");
        SKColor expected = after.FrameIndex switch { 0 => SKColors.Red, 1 => SKColors.Lime, 2 => SKColors.Blue,
            _ => throw new InvalidDataException("Unexpected three-color GIF frame index.") };
        Require(pixels.Center.Alpha >= 250 && Math.Abs(pixels.Center.Red - expected.Red) <= 3
            && Math.Abs(pixels.Center.Green - expected.Green) <= 3 && Math.Abs(pixels.Center.Blue - expected.Blue) <= 3,
            "Current visible GIF pixels do not match its encoded frame; possible stale/old presentation: " + stage);
        Console.WriteLine("SMOKE gif-current-frame " + JsonSerializer.Serialize(new
        { stage, state = after, pixels.Hash, center = pixels.Center.ToString(), expected = expected.ToString() }));
    }

    private static void GifNoAudioOrSeek(GalleryViewer viewer) => Require(!viewer.GetGalleryVisualDescendants().OfType<Control>()
        .Any(control => control.IsEffectivelyVisible && (control.Name is "GalleryVideoSeek" or "GalleryVideoVolume" or "GalleryVideoMute")),
        "GIF exposed video audio/volume/seek controls.");

    private static async Task GifNextAsync(Fixture fixture, MediaNativeInput native, GalleryEntry target)
    {
        GalleryViewer viewer = fixture.Viewer;
        int moves = 0; Exception? failure = null; Task transition = Task.CompletedTask;
        void Move(int delta)
        {
            if (++moves != 1 || delta != 1) { failure = new InvalidOperationException("GIF native Next duplicated/skipped its target."); return; }
            transition = viewer.ShowAsync(target, CancellationToken.None);
        }
        viewer.SetPosition("1 / 2", false, true);
        viewer.MoveRequested += Move;
        try
        {
            viewer.FocusGallerySurface();
            await native.KeyAsync(MediaNativeInput.Right);
            await UntilAsync(() => failure != null || moves > 0 && transition.IsCompleted, "Native media Next did not reach the GIF successor.");
            Require(failure == null && moves == 1, "Invalid native GIF successor transition: " + failure);
            await transition.WaitAsync(Deadline);
            Require(viewer.CaptureState().Key == target.Key, "Native Next selected an unrelated GIF successor.");
        }
        finally { viewer.MoveRequested -= Move; }
    }

    private static async Task CheckGifPixelsAndTransportAsync(MainWindow owner, GalleryEntry animated, GalleryEntry png)
    {
        GalleryGifPlayback retired;
        await using (Fixture fixture = new(owner))
        {
            await fixture.ReadyAsync();
            GalleryViewer viewer = fixture.Viewer;
            viewer.ConfigurePlayback(new(GalleryPlaybackMode.Manual));
            await fixture.ShowAsync(animated);
            Require(GifState(viewer).FrameCount == 3, "Three-color fixture was reduced to a still frame.");
            await GifChangingPixelsAsync(viewer, "initial-three-color-GIF");
            GifNoAudioOrSeek(viewer);
            using MediaNativeInput native = new(fixture.Window);
            viewer.FocusGallerySurface();
            await native.KeyAsync(MediaNativeInput.Space);
            await UntilAsync(() => GifState(viewer).UserPaused && !GifState(viewer).IsRunning, "Native Space did not pause GIF.");
            await GifStablePixelsAsync(viewer, "Space-paused");
            await native.ClickAsync(Find<Button>(viewer, "GalleryGifPlayPause"));
            await UntilAsync(() => !GifState(viewer).UserPaused && GifState(viewer).IsRunning, "GIF button did not resume.");
            await GifChangingPixelsAsync(viewer, "button-resumed");
            await native.ClickAsync(Find<Button>(viewer, "GalleryGifPlayPause"));
            await UntilAsync(() => GifState(viewer).UserPaused && !GifState(viewer).IsRunning, "GIF button did not pause.");
            await GifStablePixelsAsync(viewer, "button-paused");
            viewer.FocusGallerySurface(); await native.KeyAsync(MediaNativeInput.Space);
            await GifChangingPixelsAsync(viewer, "Space-resumed");
            GalleryGifPlayback same = GifOwner(viewer);
            await native.ClickAsync(Find<Button>(viewer, "GalleryActual"));
            Require(viewer.CaptureState().ActualZoom, "GIF lost 1:1 zoom.");
            await native.ClickAsync(Find<Button>(viewer, "GalleryPlus"));
            Require(viewer.CaptureState().ManualZoom, "GIF lost manual zoom.");
            await native.ClickAsync(Find<Button>(viewer, "GalleryFit"));
            Require(!viewer.CaptureState().ManualZoom && ReferenceEquals(same, GifOwner(viewer)), "Fit/zoom replaced or reset the GIF owner.");
            await GifChangingPixelsAsync(viewer, "fit-after-zoom");
            retired = GifOwner(viewer);
            await GifNextAsync(fixture, native, png);
            Require(retired.Evidence.Disposed, "GIF navigation did not actually dispose its old controller.");
            long stopped = retired.Evidence.FramesPresented;
            await GifStablePixelsAsync(viewer, "GIF-to-PNG-no-late-frame");
            Require(retired.Evidence.FramesPresented == stopped && viewer.CaptureState().Key == png.Key, "Retired GIF wrote into its PNG successor.");
            await fixture.ShowAsync(animated);
            retired = GifOwner(viewer);
            await GifChangingPixelsAsync(viewer, "reselected-before-close");
        }
        Require(retired.Evidence.Disposed, "Closing the actual viewer did not dispose GIF playback.");
        long before = retired.Evidence.FramesPresented;
        await using (Fixture reopened = new(owner))
        {
            await reopened.ReadyAsync(); await reopened.ShowAsync(animated);
            await GifCurrentColorAsync(reopened.Viewer, "reopen-first-observable-current-frame");
            Require(!ReferenceEquals(retired, GifOwner(reopened.Viewer)), "Reopened gallery reused its disposed GIF owner.");
            await GifChangingPixelsAsync(reopened.Viewer, "reopen-current-frames");
            using MediaNativeInput native = new(reopened.Window);
            reopened.Viewer.FocusGallerySurface(); await native.KeyAsync(MediaNativeInput.Space);
            await UntilAsync(() => GifState(reopened.Viewer).UserPaused, "Reopened GIF could not pause its current frame.");
            await GifStablePixelsAsync(reopened.Viewer, "reopen-paused-current-frame");
            await GifCurrentColorAsync(reopened.Viewer, "reopen-frozen-current-frame");
            Require(retired.Evidence.Disposed && retired.Evidence.FramesPresented == before, "Closed GIF continued presenting after reopen.");
        }
    }

    private static async Task CheckGifEncodedLoopsAsync(MainWindow owner, GalleryEntry animated, GalleryEntry finite, GalleryEntry single, GalleryEntry disposal)
    {
        foreach (GalleryPlaybackMode mode in new[] { GalleryPlaybackMode.Manual, GalleryPlaybackMode.Repeat })
        {
            await using Fixture fixture = new(owner); await fixture.ReadyAsync();
            GalleryViewer viewer = fixture.Viewer; int moves = 0; viewer.MoveRequested += _ => moves++;
            viewer.ConfigurePlayback(new(mode, 1));
            await fixture.ShowAsync(finite);
            await GifChangingPixelsAsync(viewer, "finite-" + mode);
            await UntilAsync(() => GifState(viewer).Ended, "Encoded finite GIF never ended in " + mode);
            GifPlaybackEvidence ended = GifState(viewer);
            Require(ended.CompletedPasses == 3 && ended.FrameIndex == 2 && !ended.IsRunning && moves == 0,
                "Finite GIF ignored its encoded two repeats or auto-navigated in " + mode);
            await GifStablePixelsAsync(viewer, "finite-end-" + mode);
            await fixture.ShowAsync(animated);
            await GifChangingPixelsAsync(viewer, "infinite-" + mode);
            await UntilAsync(() => GifState(viewer).CompletedPasses >= 4, "Encoded infinite GIF failed to continue beyond finite-repeat count in " + mode);
            Require(GifState(viewer).IsRunning && !GifState(viewer).Ended && moves == 0 && !Running(viewer),
                "GIF encoded looping was confused with Repeat-video/slideshow navigation.");
        }
        await using Fixture staticFixture = new(owner); await staticFixture.ReadyAsync();
        await staticFixture.ShowAsync(single);
        await GifStablePixelsAsync(staticFixture.Viewer, "single-first-frame", 420);
        SKColor first = GifReadPixels(Find<Image>(staticFixture.Viewer, "GalleryImage")).Center;
        Require(first.Alpha >= 250 && first.Red >= 250 && first.Green >= 250 && first.Blue >= 250,
            "Single-frame GIF did not display its encoded white first frame.");
        Require(staticFixture.Viewer.GifEvidence is not { IsRunning: true }, "Single-frame GIF created an active frame timer.");
        await staticFixture.ShowAsync(disposal);
        Require(GifState(staticFixture.Viewer).FrameCount == 4, "Disposal/transparency GIF lost its frame sequence.");
        await GifChangingPixelsAsync(staticFixture.Viewer, "disposal-transparency-visible", solidColors: false);
    }

    private static async Task CheckGifSlideshowAsync(MainWindow owner, GalleryEntry animated, GalleryEntry png)
    {
        await using Fixture fixture = new(owner); await fixture.ReadyAsync();
        GalleryViewer viewer = fixture.Viewer; viewer.ConfigurePlayback(new(GalleryPlaybackMode.Slideshow, 1));
        viewer.SetPosition("1 / 2", false, true);
        int moves = 0; Exception? error = null; Task transition = Task.CompletedTask;
        GalleryGifPlayback? retired = null;
        Stopwatch sinceStart = new(); long atMove = 0;
        viewer.MoveRequested += delta =>
        {
            moves++; atMove = sinceStart.ElapsedMilliseconds;
            if (moves != 1 || delta != 1) { error = new InvalidOperationException("GIF slideshow duplicated/skipped navigation."); return; }
            retired = GifOwner(viewer);
            viewer.SetPosition("2 / 2", true, false);
            transition = viewer.ShowAsync(png, CancellationToken.None);
        };
        await fixture.ShowAsync(animated);
        await GifChangingPixelsAsync(viewer, "remembered-slideshow-GIF");
        await HoldAsync(TimeSpan.FromMilliseconds(1200), () => Require(!Running(viewer) && moves == 0,
            "Selecting remembered Slideshow autostarted navigation on GIF frame/pass completion."));
        using MediaNativeInput native = new(fixture.Window);
        sinceStart.Start(); await native.ClickAsync(Find<Button>(viewer, "GallerySlideshowToggle"));
        // Count from the moment the click can start the timer, not from a GIF loop.
        await HoldAsync(TimeSpan.FromMilliseconds(550), () => Require(moves == 0, "GIF slideshow used frame/pass duration instead of image duration."));
        await UntilAsync(() => error != null || moves == 1 && transition.IsCompleted, "Explicit GIF slideshow did not advance at image duration.");
        Require(error == null && atMove >= 850 && retired?.Evidence.Disposed == true && viewer.CaptureState().Key == png.Key,
            "GIF slideshow advanced too early, failed to dispose or selected wrong successor: " + error);
        await GifStablePixelsAsync(viewer, "slideshow-PNG-no-GIF-late-frames");
        Require(moves == 1, "GIF slideshow generated a stale second Next.");
    }

    private static async Task CheckGifVideoSuccessorAsync(MainWindow owner, GalleryEntry animated, GalleryEntry clip, GalleryEntry png)
    {
        await using Fixture fixture = new(owner); await fixture.ReadyAsync();
        GalleryViewer viewer = fixture.Viewer;
        viewer.Configure(false, true, true, true, 0);
        viewer.ConfigurePlayback(new(GalleryPlaybackMode.Manual));
        await fixture.ShowAsync(animated); await GifChangingPixelsAsync(viewer, "before-" + clip.Asset.OriginalFileName);
        GalleryGifPlayback retired = GifOwner(viewer);
        using MediaNativeInput native = new(fixture.Window);
        await GifNextAsync(fixture, native, clip);
        GalleryVideoPlayer player = viewer.GetGalleryVisualDescendants().OfType<GalleryVideoPlayer>().Single();
        await UntilAsync(async () =>
        {
            var state = await player.InspectPlaybackAsync();
            return state.HasPlayback && state.State == "Playing" && state.Time > 150 && MediaInteractionSmokeHelpers.HasDecodedFrames(state);
        }, "GIF successor did not decode/play actual " + clip.Asset.OriginalFileName);
        Require(retired.Evidence.Disposed && !Find<Image>(viewer, "GalleryImage").IsEffectivelyVisible, "GIF was not retired/hidden for video.");
        long stopped = retired.Evidence.FramesPresented;
        viewer.FocusGallerySurface(); await native.KeyAsync(MediaNativeInput.Space);
        await UntilAsync(async () => (await player.InspectPlaybackAsync()).State == "Paused", "Video Space pause regressed after GIF.");
        await MediaInteractionSmokeHelpers.HoldStateAsync(player, "Paused", "GIF-to-video native Space", 350);
        viewer.FocusGallerySurface(); await native.KeyAsync(MediaNativeInput.Space);
        await UntilAsync(async () => (await player.InspectPlaybackAsync()).State == "Playing", "Video Space resume regressed after GIF.");
        await MediaInteractionSmokeHelpers.HoldStateAsync(player, "Playing", "GIF-to-video native Space", 450);
        Require(retired.Evidence.FramesPresented == stopped, "Old GIF presented into WebM/MP4.");
        await GifNextAsync(fixture, native, png); await GifStablePixelsAsync(viewer, "video-to-PNG");
        Require(retired.Evidence.FramesPresented == stopped && viewer.CaptureState().Key == png.Key, "Video/old GIF wrote into PNG.");
        Console.WriteLine("SMOKE gif-video-successor " + JsonSerializer.Serialize(new { clip.Asset.OriginalFileName, clip.Asset.Sha256, retired = retired.Evidence }));
    }

    private static async Task CheckGifFailureAsync(MainWindow owner, GalleryEntry failed, GalleryEntry animated)
    {
        await using Fixture fixture = new(owner); await fixture.ReadyAsync();
        GalleryViewer viewer = fixture.Viewer; var reports = new List<GalleryDiagnosticNotice>();
        viewer.DiagnosticNotice += reports.Add;
        int moves = 0; viewer.MoveRequested += _ => moves++;
        await fixture.ShowAsync(animated); await GifChangingPixelsAsync(viewer, "before-verified-GIF-failure");
        GalleryGifPlayback retired = GifOwner(viewer);
        await viewer.ShowAsync(failed, CancellationToken.None).WaitAsync(Deadline);
        await UntilAsync(() => reports.Any(report => report.MediaUnavailable && report.Entry?.Key == failed.Key),
            "Actual GIF failure did not reach the central diagnostic bridge for its exact asset.");
        Require(Find<TextBlock>(viewer, "GalleryStatus").IsEffectivelyVisible
            && (!Find<Image>(viewer, "GalleryImage").IsEffectivelyVisible || Find<Image>(viewer, "GalleryImage").Source == null)
            && viewer.GifEvidence is not { IsRunning: true } && !Find<ToggleButton>(viewer, "GallerySlideshowToggle").IsEnabled,
            "Corrupt/unverified GIF displayed a false-ready image/transport.");
        Require(retired.Evidence.Disposed, "GIF failure left its prior animation owner alive.");
        long stopped = retired.Evidence.FramesPresented;
        string status = Find<TextBlock>(viewer, "GalleryStatus").Text ?? "";
        Require(!status.Contains(failed.Manifest.Directory, StringComparison.Ordinal) && !status.Contains("Exception", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(Find<TextBlock>(viewer, "GalleryWarnings").Text)
            && !Find<Control>(viewer, "GalleryFavoriteError").IsEffectivelyVisible,
            "Technical GIF error leaked into a gallery banner instead of central diagnostics.");
        await HoldAsync(TimeSpan.FromMilliseconds(420), () => Require(moves == 0 && !Running(viewer)
            && viewer.GifEvidence is not { IsRunning: true } && retired.Evidence.FramesPresented == stopped,
            "GIF failure auto-skipped or restarted/presented late."));
        Console.WriteLine("SMOKE gif-failure " + JsonSerializer.Serialize(new { failed.Key, reports = reports.Select(r => new { r.Operation, r.MediaUnavailable }) }));
    }

    private static async Task CheckGifReaderAssociationAsync(MainWindow owner, string root, byte[] gif, byte[] png)
    {
        string profile = Path.Combine(root, "reader-profile"); Directory.CreateDirectory(profile);
        string media = Path.Combine(profile, "media");
        new Config { DownloadPath = media, SaveUrlsOnExit = false, AutomaticUpdateChecks = false, CloseToTray = false,
            MinimizeToTray = false, WorkspaceMode = "Advanced", GalleryPlaybackMode = "Manual", GalleryOverlayControls = false,
            GalleryAutoHideControls = false, GalleryContextOpen = false }.SaveConfig(profile);
        Directory.CreateDirectory(media); // Valid profile first; never initialize defaults over existing archive data.
        ResourceId id = new("4chan", "g", "50500");
        var entries = new Dictionary<string, object>();
        foreach (var item in new[] { (Name: "unrelated.png", Id: "reader-png", Bytes: png), (Name: "selected.gif", Id: "reader-gif", Bytes: gif) })
        {
            await File.WriteAllBytesAsync(Path.Combine(media, item.Name), item.Bytes);
            entries[item.Name] = new { MediaIdentifier = item.Id, SourceUrl = "https://offline.invalid/" + item.Name,
                FileName = item.Name, Length = item.Bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(item.Bytes)),
                Md5 = Convert.ToBase64String(MD5.HashData(item.Bytes)), OriginalFileName = item.Name };
        }
        await File.WriteAllTextAsync(Path.Combine(media, ".meowchan-manifest.json"), JsonSerializer.Serialize(new { Version = 1, Entries = entries }));
        CapturedPost[] posts = await StrictMediaFixture.BindAsync(media, id,
        [new("500", 0, PostFieldPresence.All, Html: "<p>Earlier unrelated image</p>", Attachments: [new(0, "reader-png", OriginalFileName: "unrelated.png")]),
         new("501", 1, PostFieldPresence.All, Html: "<p>Selected later animated GIF post</p>", Attachments: [new(0, "reader-gif", OriginalFileName: "selected.gif")])]);
        using (ProfileLease lease = ProfileLease.Acquire(profile))
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease))
            await catalog.CaptureAsync(new(id, "https://offline.invalid/g/thread/50500", "Private GIF Reader", ThreadSourceState.Archived,
                DateTimeOffset.UtcNow, media, posts));
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(media);
        var resolved = MediaAttachmentResolver.Resolve(snapshot, id, "501", posts[1].Attachments!.Single())
            ?? throw new InvalidDataException("Private GIF reader has no exact strict association.");
        GalleryEntryKey expected = new(new(id, snapshot.Directory, resolved.Asset.AssetId), "501", 0);
        await using DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        MainWindow shell = new(host); shell.Show(owner); shell.Activate();
        try
        {
            await host.StartAsync().WaitAsync(Deadline);
            await UntilAsync(() => shell.IsActive && shell.FindControl<Button>("AddUrlButton")!.IsEnabled, "Private GIF Reader session did not become ready.");
            SmokeActionClick.Execute(shell.FindControl<MenuItem>("ArchiveMenuItem")!);
            ArchivePage page = shell.ArchivePage; await UntilAsync(() => !page.IsBusy, "Private archive did not load.");
            await page.LoadThreadAsync(id);
            SmokeActionClick.Execute(page.FindControl<Button>("ArchiveReadPosts")!);
            await UntilAsync(() => shell.FindControl<ContentControl>("ReaderHost")!.IsVisible
                && !page.IsBusy && page.CurrentThread?.Summary.Id == id && page.ReaderShowsPosts
                && page.FindControl<ListBox>("ArchivePosts")!.ItemsSource is IEnumerable<ArchivePostRow> loadedPosts
                && loadedPosts.Any(post => post.Id == "501")
                && typeof(ArchivePage).GetField("_contentCancellation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(page) == null,
                "Large private GIF Reader did not complete its actual content/focus load.");
            await page.MediaLoadTask; await page.ReaderLocalizationTask; await page.ReaderNavigationTask;
            page.ShowPost("501"); // Arrange/realize only. Actual pointer must establish user selection/focus below.
            ListBox list = page.FindControl<ListBox>("ArchivePosts")!;
            ArchivePostRow target = list.ItemsSource!.Cast<ArchivePostRow>().Single(row => row.Id == "501");
            list.ScrollIntoView(target); list.UpdateLayout();
            await UntilAsync(() => list.ContainerFromItem(target) is Control container && container.GetVisualDescendants()
                .OfType<ReaderBodyTextBlock>().Any(body => body.IsEffectivelyVisible && body.Bounds.Width > 20), "Target GIF post did not realize.");
            Control row = list.ContainerFromItem(target)!;
            ReaderBodyTextBlock body = row.GetVisualDescendants().OfType<ReaderBodyTextBlock>().Single();
            body.SelectionStart = body.SelectionEnd = 0;
            using MediaNativeInput native = new(shell);
            Control? pointerSource = null;
            void Pointer(object? sender, PointerPressedEventArgs e)
            {
                pointerSource = e.Source as Control;
                Console.WriteLine("SMOKE gif-reader-pointer " + JsonSerializer.Serialize(new
                {
                    source = pointerSource?.GetType().Name, name = pointerSource?.Name,
                    inBody = pointerSource?.GetSelfAndVisualAncestors().Contains(body) == true,
                    bodyPoint = e.GetPosition(body).ToString(), windowPoint = e.GetPosition(shell).ToString(),
                    selected = (list.SelectedItem as ArchivePostRow)?.Id, active = shell.IsActive
                }));
            }
            object ClickEvidence()
            {
                var pixel = body.PointToScreen(new Point(16, 10));
                var hit = shell.InputHitTest(shell.PointToClient(pixel)) as Control;
                var focus = shell.FocusManager?.GetFocusedElement() as Control;
                return new
                {
                    backend = native.Backend, active = shell.IsActive, scale = shell.RenderScaling,
                    windowPosition = shell.Position.ToString(), windowBounds = shell.Bounds.ToString(),
                    bodyBounds = body.Bounds.ToString(), rowBounds = row.Bounds.ToString(),
                    targetPixel = pixel.ToString(), hit = hit?.GetType().Name, hitName = hit?.Name,
                    hitInBody = hit?.GetSelfAndVisualAncestors().Contains(body) == true,
                    pointer = pointerSource?.GetType().Name, pointerName = pointerSource?.Name,
                    pointerInBody = pointerSource?.GetSelfAndVisualAncestors().Contains(body) == true,
                    target = target.Id, selected = (list.SelectedItem as ArchivePostRow)?.Id,
                    exactSelected = ReferenceEquals(list.SelectedItem, target),
                    focus = focus?.GetType().Name, focusName = focus?.Name,
                    focusInBody = ReferenceEquals(focus, body), focusInRow = ReferenceEquals(focus, row),
                    body.SelectionStart, body.SelectionEnd
                };
            }
            page.FindControl<Grid>("ArchiveReaderPane")!.AddHandler(InputElement.PointerPressedEvent, Pointer, RoutingStrategies.Tunnel, handledEventsToo: true);
            try
            {
                Console.WriteLine("SMOKE gif-reader-click-before " + JsonSerializer.Serialize(ClickEvidence()));
                await native.ClickAsync(body, new Point(16, 10));
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                Console.WriteLine("SMOKE gif-reader-click-after " + JsonSerializer.Serialize(ClickEvidence()));
                Control? focus = shell.FocusManager?.GetFocusedElement() as Control;
                Require(pointerSource?.GetSelfAndVisualAncestors().Contains(body) == true && ReferenceEquals(list.SelectedItem, target)
                    && (ReferenceEquals(focus, body) || ReferenceEquals(focus, row)) && body.SelectionStart == body.SelectionEnd,
                    "Real native GIF body click did not establish the exact later-post selection/focus.");
                await native.KeyAsync(MediaNativeInput.Enter);
                await UntilAsync(() => shell.FindControl<ContentControl>("GalleryHost")!.IsVisible, "Native Reader Enter did not open the GIF gallery.");
                await page.ReaderMediaActivationTask;
                GalleryViewer viewer = (GalleryViewer)shell.FindControl<ContentControl>("GalleryHost")!.Content!;
                GalleryViewerState actual = viewer.CaptureState();
                Require(actual.Key == expected.Asset && actual.PostId == expected.PostId && actual.AttachmentPosition == expected.AttachmentPosition,
                    "Reader opened the earlier PNG or lost the exact GIF GalleryEntryKey.");
                await GifChangingPixelsAsync(viewer, "native-Reader-selected-GIF");
                GalleryGifPlayback playback = GifOwner(viewer);
                await native.ClickAsync(Find<Button>(viewer, "GalleryClose"));
                await UntilAsync(() => !shell.FindControl<ContentControl>("GalleryHost")!.IsVisible, "GIF gallery did not close.");
                await shell.GalleryCleanupTask.WaitAsync(Deadline);
                Require(playback.Evidence.Disposed && (list.SelectedItem as ArchivePostRow)?.Id == "501", "GIF close leaked playback or lost Reader selection.");
                Console.WriteLine("SMOKE gif-reader-key " + JsonSerializer.Serialize(new { expected, actual, native = native.Backend }));
            }
            finally { page.FindControl<Grid>("ArchiveReaderPane")!.RemoveHandler(InputElement.PointerPressedEvent, Pointer); }
        }
        finally
        {
            SmokeActionClick.Execute(shell.FindControl<MenuItem>("ExitMenuItem")!);
            await UntilAsync(() => !shell.IsVisible, "Private GIF shell did not finish actual shutdown.");
            ShortcutService.Publish(owner.SessionHost.Config.ShortcutBindings);
        }
    }
}

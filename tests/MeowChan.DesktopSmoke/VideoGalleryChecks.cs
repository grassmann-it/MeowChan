using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using SkiaSharp;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan.DesktopSmoke;

internal static class VideoGalleryChecks
{
    internal static readonly ResourceId Id = new("4chan", "g", "2000");
    internal static string? FixtureImageSha256 { get; private set; }
    internal static async Task SeedAsync(string profile, string fixture, bool includeSecondVideo = false)
    {
        string media = Path.Combine(profile, "Video media");
        Directory.CreateDirectory(media);
        byte[] video = await File.ReadAllBytesAsync(fixture);
        byte[] image = includeSecondVideo ? InteractionImage()
            : await File.ReadAllBytesAsync(Path.Combine(profile, "Reader media", "reader.png"));
        FixtureImageSha256 = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
        await File.WriteAllBytesAsync(Path.Combine(media, "clip.webm"), video);
        await File.WriteAllBytesAsync(Path.Combine(media, "image.png"), image);
        if (includeSecondVideo) await File.WriteAllBytesAsync(Path.Combine(media, "clip-b.webm"), video);
        // Fresh archive captures receive host revision keys. Seed exact current bindings
        // so the gallery keeps post order instead of sorting unassigned image before video.
        Dictionary<string, object> entries = new();
        Dictionary<string, object> assets = new();
        Dictionary<string, MediaAttachmentBinding> bindings = new();
        CapturedAttachment Entry(string postId, string id, string name, byte[] bytes)
        {
            string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            string md5 = Convert.ToBase64String(MD5.HashData(bytes));
            MediaProviderMetadata provider = new(id, "original", "https://offline.invalid/" + name,
                name, bytes.Length, md5, sha256);
            MediaActualIntegrity actual = new(bytes.Length, md5, sha256);
            string revision = MediaRevisionKeys.CreateAttachmentKey(Id, postId, 0, provider);
            string assetId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new[] { name, id, name, sha256 })))).ToLowerInvariant();
            object entry = new { MediaIdentifier = id, Variant = provider.Variant, SourceUrl = provider.Url,
                FileName = name, Length = bytes.Length, Sha256 = sha256, Md5 = md5, OriginalFileName = name };
            entries[name] = entry;
            assets[assetId] = new { RequestedFileName = name, Entry = entry };
            bindings[revision] = new(new(Id, postId, 0, revision),
                MediaRevisionKeys.CreateAcquisitionKey(Id, provider), assetId, provider, actual,
                new(MediaAcceptanceKind.Strict, false, false, DateTimeOffset.UnixEpoch));
            return new(0, id, provider.Variant, provider.Url, name,
                ExpectedSize: bytes.Length, ExpectedMd5: md5)
                { ExpectedSha256 = sha256, AttachmentRevisionKey = revision };
        }
        CapturedAttachment videoAttachment = Entry("2000", "video", "clip.webm", video);
        CapturedAttachment imageAttachment = Entry("2001", "image", "image.png", image);
        CapturedAttachment? secondVideo = includeSecondVideo ? Entry("2002", "video-b", "clip-b.webm", video) : null;
        await File.WriteAllTextAsync(Path.Combine(media, ".meowchan-manifest.json"), JsonSerializer.Serialize(new {
            Version = 3, Entries = entries, Assets = assets, AttachmentBindings = bindings }));
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        List<CapturedPost> posts = new() {
            new CapturedPost("2000", 0, PostFieldPresence.All, Html: "Saved WebM", Attachments: new[] { videoAttachment }),
            new CapturedPost("2001", 1, PostFieldPresence.All, Html: "Saved image", Attachments: new[] { imageAttachment }) };
        if (secondVideo != null) posts.Add(new CapturedPost("2002", 2, PostFieldPresence.All, Html: "Saved WebM B", Attachments: new[] { secondVideo }));
        await catalog.CaptureAsync(new CapturedThreadSnapshot(Id, "https://boards.4chan.org/g/thread/2000", "Video UX",
            ThreadSourceState.Archived, DateTimeOffset.UtcNow, media, posts));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        ArchivedThreadView thread = await reader.ReadThreadAsync(Id)
            ?? throw new InvalidOperationException("The video fixture archive was not captured.");
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(media);
        GalleryThreadResult selected = GallerySelection.Build(thread, new[] { manifest }, includeOtherMedia: true);
        Require(selected.Entries.Count == (includeSecondVideo ? 3 : 2) && selected.Entries[0].Kind == SavedMediaKind.Video
            && selected.Entries[0].Post?.PostId == "2000" && selected.Entries[1].Post?.PostId == "2001"
            && (!includeSecondVideo || selected.Entries[2].Kind == SavedMediaKind.Video && selected.Entries[2].Post?.PostId == "2002")
            && selected.Entries.All(entry => !entry.Unassigned && entry.AttachmentResolution?.Binding?.Acceptance.Kind == MediaAcceptanceKind.Strict),
            "The seeded video/image fixture must resolve exact strict bindings in captured post order.");
        Console.WriteLine($"SMOKE video fixture: {selected.Entries.Count} exact strict bindings in video/image{(includeSecondVideo ? "/video" : "")} post order.");
    }

    private static byte[] InteractionImage()
    {
        // Tiny synthetic PNG for the bounded normal-runner path; no 52-thread reader seed.
        using SKBitmap bitmap = new(256, 144);
        using SKCanvas canvas = new(bitmap);
        canvas.Clear(SKColors.DarkSlateBlue);
        using SKPaint paint = new() { Color = SKColors.Gold };
        canvas.DrawRect(32, 24, 120, 80, paint);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    internal static async Task RunAsync(MainWindow window)
    {
        await CheckSoftwareSurfaceAsync(window);
        await window.OpenGalleryAsync(Id);
        ContentControl host = window.FindControl<ContentControl>("GalleryHost")!;
        GalleryViewer viewer = (GalleryViewer)host.Content!;
        await Program.WaitUntilAsync(() => viewer.GetGalleryVisualDescendants().OfType<Button>().Any(button => button.Name == "GalleryVideoPlayPause" && button.IsEnabled));
        Slider seek = Control<Slider>(viewer, "GalleryVideoSeek");
        await Program.WaitUntilAsync(() => seek.Value > 10);
        Click(Control<Button>(viewer, "GalleryVideoPlayPause"));
        await Task.Delay(250);
        double paused = seek.Value;
        await Task.Delay(300);
        Require(Math.Abs(seek.Value - paused) < 20, "Video Pause did not stop its playback clock.");
        // Legacy programmatic mechanism check only. Native pointer/focus/key proof lives in
        // MediaInteractionChecks, opt-in through the existing normal runner.
        seek.Focus();
        seek.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
        Require(viewer.CaptureState().PostId == "2000", "Gallery arrow stole the video slider's key.");
        seek.Value = 500;
        Click(Control<Button>(viewer, "GalleryVideoPlayPause"));
        await Task.Delay(300);
        if (Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") == "1")
        {
            ProcessStartInfo info = new("import") { UseShellExecute = false };
            info.ArgumentList.Add("-window"); info.ArgumentList.Add(window.TryGetPlatformHandle()!.Handle.ToString());
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "smoke-gallery-native-video.png"));
            using Process capture = Process.Start(info)!;
            await capture.WaitForExitAsync();
            Require(capture.ExitCode == 0, "Private Xvfb video capture failed.");
        }
        Click(Control<Button>(viewer, "GalleryNext"));
        await Program.WaitUntilAsync(() => viewer.CaptureState().PostId == "2001" && Control<Button>(viewer, "GalleryActual").IsEnabled);
        Click(Control<Button>(viewer, "GalleryPrevious"));
        await Program.WaitUntilAsync(() => Control<Button>(viewer, "GalleryVideoPlayPause").IsEnabled);
        // Earlier galleries persist an open context; toggling blindly can close it while its controls remain.
        if (!viewer.CaptureState().ContextOpen) Click(Control<Button>(viewer, "GalleryToggleContext"));
        await Program.WaitUntilAsync(() => viewer.CaptureState().ContextOpen
            && viewer.GetGalleryVisualDescendants().OfType<Button>().Any(button => button.Name == "GalleryReadPost"
                && button.IsEffectivelyVisible && button.IsEffectivelyEnabled));
        Click(Control<Button>(viewer, "GalleryReadPost"));
        await Program.WaitUntilAsync(() => !host.IsVisible && window.FindControl<ContentControl>("ReaderHost")!.IsVisible);
        await window.GalleryCleanupTask;
        Click(window.ArchivePage.FindControl<Button>("ArchiveGalleryReturn")!);
        await Program.WaitUntilAsync(() => Control<Button>(viewer, "GalleryVideoPlayPause").IsEnabled);
        // Shutdown with a playing native video must await the verified stream and engine cleanup.
        Console.WriteLine("SMOKE production video (programmatic controls, NOT native-input acceptance): verified WebM clock, pause/play/seek, native frame capture, synthetic slider arrows, image switch and reader suspend/resume passed; leaving playback active for awaited shutdown.");
    }

    private static async Task CheckSoftwareSurfaceAsync(Window owner)
    {
        if (OperatingSystem.IsWindows()) return;
        // Exercise the exact production software surface on Mac and Linux test hosts,
        // without changing the production platform selection or faking frame counts.
        string fixture = Environment.GetEnvironmentVariable("MEOWCHAN_VIDEO_FIXTURE")!;
        Image image = new() { Stretch = Avalonia.Media.Stretch.Uniform };
        Window surfaceWindow = new() { Width = 400, Height = 260, Content = image, Title = "Software video surface proof" };
        surfaceWindow.Show(owner);
        NativeVideoRuntime.EnsureInitialized();
        var engine = new LibVLCSharp.Shared.LibVLC("--no-audio", "--avcodec-hw=none", "--no-video-title-show");
        var player = new LibVLCSharp.Shared.MediaPlayer(engine);
        var media = new LibVLCSharp.Shared.Media(engine, fixture, LibVLCSharp.Shared.FromType.FromPath);
        var surface = new SoftwareVideoSurface(image, player);
        try
        {
            Require(player.Play(media), "Software video fixture was rejected.");
            await Program.WaitUntilAsync(() => surface.PresentedFrames >= 3 && surface.NonUniform && media.Statistics.DisplayedPictures >= 2);
            int first = surface.PresentedFrames;
            await Program.WaitUntilAsync(() => surface.PresentedFrames > first + 2);
            player.SetPause(true);
            await Program.WaitUntilAsync(() => player.State == LibVLCSharp.Shared.VLCState.Paused);
            await Task.Delay(200);
            long paused = player.Time;
            await Task.Delay(250);
            Require(Math.Abs(player.Time - paused) <= 100, "Software surface Pause did not hold its native clock.");
            string snapshot = Path.Combine(Path.GetTempPath(), "meowchan-software-video-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                Require(player.TakeSnapshot(0, snapshot, 0, 0), "Software surface rejected native snapshot.");
                await Program.WaitUntilAsync(() => File.Exists(snapshot) && new FileInfo(snapshot).Length > 0);
            }
            finally { File.Delete(snapshot); }
            player.SetPause(false);
            first = surface.PresentedFrames;
            await Program.WaitUntilAsync(() => surface.PresentedFrames > first + 2);
            for (int restart = 0; restart < 6; restart++)
            {
                await Task.Run(() => player.Stop());
                await Program.WaitUntilAsync(() => image.Source == null);
                first = surface.PresentedFrames;
                Require(player.Play(media), "Software surface restart was rejected.");
                await Program.WaitUntilAsync(() => surface.PresentedFrames > first + 2);
            }
            Console.WriteLine($"SMOKE production software surface: real frames={surface.PresentedFrames}, native displayed={media.Statistics.DisplayedPictures}, varied pixels, pause/resume and native snapshot passed.");
        }
        finally
        {
            await Task.Run(() => { player.Stop(); player.Dispose(); surface.Dispose(); media.Dispose(); engine.Dispose(); });
            await Program.WaitUntilAsync(() => image.Source == null);
            surfaceWindow.Close();
        }
    }
    private static T Control<T>(GalleryViewer viewer, string name) where T : Control => viewer.GetGalleryVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

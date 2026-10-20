using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopSmoke;

internal static class VideoGalleryChecks
{
    private static readonly ResourceId Id = new("4chan", "g", "2000");
    internal static async Task SeedAsync(string profile, string fixture)
    {
        string media = Path.Combine(profile, "Video media");
        Directory.CreateDirectory(media);
        byte[] video = await File.ReadAllBytesAsync(fixture);
        byte[] image = await File.ReadAllBytesAsync(Path.Combine(profile, "Reader media", "reader.png"));
        await File.WriteAllBytesAsync(Path.Combine(media, "clip.webm"), video);
        await File.WriteAllBytesAsync(Path.Combine(media, "image.png"), image);
        object Entry(string id, string name, byte[] bytes) => new { MediaIdentifier = id,
            SourceUrl = "https://offline.invalid/" + name, FileName = name, Length = bytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), Md5 = Convert.ToBase64String(MD5.HashData(bytes)) };
        await File.WriteAllTextAsync(Path.Combine(media, ".meowchan-manifest.json"), JsonSerializer.Serialize(new {
            Version = 1, Entries = new Dictionary<string, object> { ["clip.webm"] = Entry("video", "clip.webm", video),
                ["image.png"] = Entry("image", "image.png", image) } }));
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(new CapturedThreadSnapshot(Id, "https://boards.4chan.org/g/thread/2000", "Video UX",
            ThreadSourceState.Archived, DateTimeOffset.UtcNow, media, new[] {
                new CapturedPost("2000", 0, PostFieldPresence.All, Html: "Saved WebM", Attachments: new[] { new CapturedAttachment(0, "video", OriginalFileName: "clip.webm") }),
                new CapturedPost("2001", 1, PostFieldPresence.All, Html: "Saved image", Attachments: new[] { new CapturedAttachment(0, "image", OriginalFileName: "image.png") }) }));
    }
    internal static async Task RunAsync(MainWindow window)
    {
        await window.OpenGalleryAsync(Id);
        ContentControl host = window.FindControl<ContentControl>("GalleryHost")!;
        GalleryViewer viewer = (GalleryViewer)host.Content!;
        await Program.WaitUntilAsync(() => viewer.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "GalleryVideoPause" && button.IsEnabled));
        Slider seek = Control<Slider>(viewer, "GalleryVideoSeek");
        await Program.WaitUntilAsync(() => seek.Value > 10);
        Click(Control<Button>(viewer, "GalleryVideoPause"));
        await Task.Delay(250);
        double paused = seek.Value;
        await Task.Delay(300);
        Require(Math.Abs(seek.Value - paused) < 20, "Video Pause did not stop its playback clock.");
        seek.Focus();
        seek.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
        Require(viewer.CaptureState().PostId == "2000", "Gallery arrow stole the video slider's key.");
        seek.Value = 500;
        Click(Control<Button>(viewer, "GalleryVideoPlay"));
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
        await Program.WaitUntilAsync(() => Control<Button>(viewer, "GalleryVideoPause").IsEnabled);
        Click(Control<Button>(viewer, "GalleryToggleContext"));
        await Program.WaitUntilAsync(() => viewer.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "GalleryReadPost"));
        Click(Control<Button>(viewer, "GalleryReadPost"));
        await Program.WaitUntilAsync(() => !host.IsVisible && window.FindControl<ContentControl>("ReaderHost")!.IsVisible);
        await window.GalleryCleanupTask;
        Click(window.ArchivePage.FindControl<Button>("ArchiveGalleryReturn")!);
        await Program.WaitUntilAsync(() => Control<Button>(viewer, "GalleryVideoPause").IsEnabled);
        // Shutdown with a playing native video must await the verified stream and engine cleanup.
        Console.WriteLine("SMOKE production video: verified WebM clock, pause/play/seek, native frame capture, slider arrows, image switch and reader suspend/resume passed; leaving playback active for awaited shutdown.");
    }
    private static T Control<T>(GalleryViewer viewer, string name) where T : Control => viewer.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

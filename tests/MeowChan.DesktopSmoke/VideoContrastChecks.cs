using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using Texts = MeowChan.Desktop.Localization.Texts;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopSmoke;

internal static class VideoContrastChecks
{
    private static readonly ResourceId Id = new("4chan", "g", "2500");

    internal static async Task SeedAsync(string profile)
    {
        string fixture = Environment.GetEnvironmentVariable("MEOWCHAN_VIDEO_FIXTURE")
            ?? throw new InvalidOperationException("video-contrast requires a short, real WebM in MEOWCHAN_VIDEO_FIXTURE.");
        string directory = Path.Combine(profile, "Video contrast");
        Directory.CreateDirectory(directory);
        Dictionary<string, object> entries = new();
        foreach ((string name, byte[] bytes) in new[] { ("clip.webm", await File.ReadAllBytesAsync(fixture)),
            ("corrupt.webm", "This verified fixture is intentionally not a decodable WebM."u8.ToArray()) })
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, name), bytes);
            entries[name] = new { MediaIdentifier = name, SourceUrl = "https://offline.invalid/" + name,
                FileName = name, Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                Md5 = Convert.ToBase64String(MD5.HashData(bytes)) };
        }
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), JsonSerializer.Serialize(new { Version = 1, Entries = entries }));
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(new CapturedThreadSnapshot(Id, "https://boards.4chan.org/g/thread/2500", "Video contrast",
            ThreadSourceState.Archived, DateTimeOffset.UtcNow, directory, new[] {
                new CapturedPost("2500", 0, PostFieldPresence.All, Html: "Real decoded video", Attachments: new[] { new CapturedAttachment(0, "clip.webm", OriginalFileName: "clip.webm") }),
                new CapturedPost("2501", 1, PostFieldPresence.All, Html: "Verified undecodable video", Attachments: new[] { new CapturedAttachment(0, "corrupt.webm", OriginalFileName: "corrupt.webm") }) }));
    }

    internal static async Task RunAsync(MainWindow window)
    {
        List<string> failures = new();
        Texts.SetLanguage("en");
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            if (Application.Current!.RequestedThemeVariant != theme)
                window.FindControl<Button>("ThemeToggleButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => Application.Current.ActualThemeVariant == theme);
            await window.OpenGalleryAsync(Id);
            ContentControl host = window.FindControl<ContentControl>("GalleryHost")!;
            GalleryViewer viewer = (GalleryViewer)host.Content!;
            await Program.WaitUntilAsync(() => viewer.GetGalleryVisualDescendants().OfType<GalleryVideoPlayer>().Any());
            GalleryVideoPlayer player = viewer.GetGalleryVisualDescendants().OfType<GalleryVideoPlayer>().Single();
            GalleryVideoPlayer.PlaybackEvidence playback;
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            do
            {
                playback = await player.InspectPlaybackAsync();
                if (playback.SurfaceFrames >= 3 && playback.SurfaceNonUniform && playback.Time > 100) break;
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Real video did not decode frames and advance its clock.");
                await Task.Delay(50);
            } while (true);
            Console.WriteLine("SMOKE video playback evidence: " + JsonSerializer.Serialize(playback));
            await Program.WaitUntilAsync(() => player.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text?.Contains(" / ") == true));
            TextBlock time = player.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text?.Contains(" / ") == true);
            Check(window, viewer, time, theme, "time", failures);
            if (theme == ThemeVariant.Light) await GalleryCaptionChecks.RunAsync(window, viewer, video: true);
            TextBlock status = player.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Name == "GalleryVideoStatus");
            await Program.WaitUntilAsync(() => status.Text == "Playback ended.");
            Check(window, viewer, status, theme, "ended", failures);
            await GalleryToolbarContrastChecks.RunAsync(window, viewer, theme, failures);
            Button next = viewer.GetGalleryVisualDescendants().OfType<Button>().Single(button => button.Name == "GalleryNext");
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => status.Text?.StartsWith("Video playback is unavailable", StringComparison.Ordinal) == true);
            Check(window, viewer, status, theme, "error", failures);
            Button close = viewer.GetGalleryVisualDescendants().OfType<Button>().Single(button => button.Name == "GalleryClose");
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => !host.IsVisible);
            await window.GalleryCleanupTask;
        }
        if (failures.Count > 0) throw new InvalidOperationException("Video or gallery toolbar contrast failed: " + string.Join(", ", failures));
    }

    private static void Check(Window window, GalleryViewer viewer, TextBlock text, ThemeVariant theme, string state, List<string> failures)
    {
        window.UpdateLayout();
        Color foreground = (text.Foreground as ISolidColorBrush)?.Color ?? throw new InvalidOperationException("Expected a measurable solid text foreground.");
        Grid viewport = viewer.GetGalleryVisualDescendants().OfType<Grid>().Single(grid => grid.Name == "GalleryViewport");
        Color background = (viewport.Background as ISolidColorBrush)?.Color ?? throw new InvalidOperationException("Expected the production video backdrop.");
        double ratio = Contrast(foreground, background);
        Point origin = text.TranslatePoint(default, viewer) ?? throw new InvalidOperationException("Video text is detached.");
        Rect textBounds = new(origin, text.Bounds.Size);
        bool chromeOverlap = viewer.GetGalleryVisualDescendants().OfType<Button>()
            .Where(button => button.IsEffectivelyVisible && button.Name is "GalleryClose" or "GalleryNext" or "GalleryFavorite" or "GalleryExport")
            .Any(button => button.TranslatePoint(default, viewer) is Point point && new Rect(point, button.Bounds.Size).Intersects(textBounds));
        Console.WriteLine("SMOKE video contrast: " + JsonSerializer.Serialize(new { theme = theme.ToString(), state,
            text.Text, foreground = foreground.ToString(), background = background.ToString(), ratio,
            text.IsEffectivelyVisible, chromeOverlap, bounds = text.Bounds.ToString(), client = window.ClientSize.ToString(), window.RenderScaling }));
        Program.CaptureWindow(window, $"smoke-video-contrast-{theme.ToString().ToLowerInvariant()}-{state}.png");
        if (!text.IsEffectivelyVisible || text.Bounds.Width <= 0 || ratio < 4.5 || chromeOverlap) failures.Add($"{theme}/{state}={ratio:0.###}, chromeOverlap={chromeOverlap}");
    }

    private static double Contrast(Color first, Color second)
    {
        static double Linear(byte channel) { double value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
        static double Luminance(Color color) => .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}

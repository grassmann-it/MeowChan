using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Gallery;
using MeowChan.Storage;
using SkiaSharp;

namespace MeowChan.DesktopSmoke;

/// <summary>Real verified original/fit decode and layout, on the existing isolated harness display.</summary>
internal static class GalleryViewerChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        string directory = Path.Combine(Path.GetTempPath(), "meowchan-gallery-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        object? content = window.Content;
        GalleryViewer viewer = new();
        try
        {
            // Alternating single-pixel stripes beyond 1200px reveal accidental preview upscaling.
            using SKBitmap source = new(2401, 1601);
            using (SKCanvas canvas = new(source))
            {
                canvas.Clear(SKColors.White);
                using SKPaint pen = new() { Color = SKColors.Black, StrokeWidth = 1, IsAntialias = false };
                for (int x = 0; x < source.Width; x += 2) canvas.DrawLine(x + 0.5f, 0, x + 0.5f, source.Height, pen);
            }
            using SKImage image = SKImage.FromBitmap(source);
            using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
            byte[] bytes = data.ToArray();
            // Deliberately nonstandard suffix: decoder must inspect the verified contents.
            string path = Path.Combine(directory, "detail.data");
            await File.WriteAllBytesAsync(path, bytes);
            object entry = new { MediaIdentifier = "1", SourceUrl = "https://offline.invalid/detail", FileName = "detail.data",
                Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), Md5 = Convert.ToBase64String(MD5.HashData(bytes)) };
            await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), JsonSerializer.Serialize(new {
                Version = 1, Entries = new Dictionary<string, object> { ["detail.data"] = entry } }));
            MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory);
            MediaAssetReference asset = manifest.GetPage().Single();
            GalleryEntry selected = new(new GalleryAssetKey(new ResourceId("4chan", "g", "123"), directory, asset.AssetId),
                manifest, asset, new CapturedPost("124", 0, PostFieldPresence.Html, Html: "<script>unsafe()</script><p>Gallery &amp; context</p>"), null, true, false);
            window.Content = viewer;
            viewer.SetPosition("Gallery smoke", false, true);
            await Program.WaitUntilAsync(() => viewer.Bounds.Width > 100 && GalleryControl<Grid>(viewer, "GalleryViewport").Bounds.Height > 100);
            viewer.SetScope("Images of matching threads · 52 threads");
            viewer.SetWarnings("Manifest warning retained");
            await viewer.ShowAsync(selected, CancellationToken.None);
            Require(GalleryControl<TextBlock>(viewer, "GalleryWarnings").Text == "Manifest warning retained", "Loading an image discarded manifest warnings.");
            Require(GalleryControl<TextBlock>(viewer, "GalleryScope").Text!.Contains("52"), "Loading an image discarded the frozen scope.");
            Image displayed = GalleryControl<Image>(viewer, "GalleryImage");
            Require(displayed.Source is Bitmap fit && fit.PixelSize.Width < 2401, "Fit did not use a bounded display-sized decode.");
            Require(!GalleryControl<Border>(viewer, "GalleryContext").IsVisible, "Context must start closed.");
            Click(GalleryControl<Button>(viewer, "GalleryActual"));
            await Program.WaitUntilAsync(() => displayed.Source is Bitmap bitmap && bitmap.PixelSize.Width == 2401);
            double scale = TopLevel.GetTopLevel(viewer)!.RenderScaling;
            Require(Math.Abs(displayed.Width * scale - 2401) < 0.01, "1:1 does not map source pixels to physical display pixels.");
            using MemoryStream original = new();
            ((Bitmap)displayed.Source!).Save(original, PngBitmapEncoderOptions.Default);
            using SKBitmap actual = SKBitmap.Decode(original.ToArray());
            Require(actual.GetPixel(2000, 800) != actual.GetPixel(2001, 800), "Original single-pixel details beyond 1200px were lost.");
            GalleryViewerState state = viewer.CaptureState();
            Click(GalleryControl<Button>(viewer, "GalleryToggleContext"));
            Require(GalleryControl<Border>(viewer, "GalleryContext").IsVisible, "Post context did not open.");
            Program.CaptureWindow(window, "smoke-gallery-original-context.png");
            await viewer.ShowAsync(selected, CancellationToken.None); viewer.RestoreState(state);
            await Program.WaitUntilAsync(() => displayed.Source is Bitmap bitmap && bitmap.PixelSize.Width == 2401);
            Require(viewer.CaptureState().ManualZoom, "Reader return lost manual zoom.");
            // Repeated completed originals measure native decoded-memory behaviour, not only cache counts.
            List<long> resident = new();
            for (int cycle = 0; cycle < 12; cycle++)
            {
                await viewer.ShowAsync(selected, CancellationToken.None);
                Click(GalleryControl<Button>(viewer, "GalleryActual"));
                await Program.WaitUntilAsync(() => displayed.Source is Bitmap bitmap && bitmap.PixelSize.Width == 2401);
                if (cycle % 3 == 2)
                {
                    GC.Collect(); GC.WaitForPendingFinalizers();
                    using Process process = Process.GetCurrentProcess(); process.Refresh(); resident.Add(process.WorkingSet64);
                }
            }
            Console.WriteLine("SMOKE gallery resident-byte samples after 3/6/9/12 original replacements: " + string.Join(", ", resident));
            Require(resident[^1] - resident[1] < 96L * 1024 * 1024, "Repeated original replacement shows excessive retained decoded memory.");
            GalleryControl<Button>(viewer, "GalleryClose").Focus();
            foreach (NavigationDirection direction in new[] { NavigationDirection.Next, NavigationDirection.Previous })
                for (int tab = 0; tab < 30; tab++)
                {
                    window.FocusManager!.TryMoveFocus(direction);
                    Require(window.FocusManager.GetFocusedElement() is Visual focus && (focus == viewer || focus.GetVisualAncestors().Contains(viewer)),
                        "Tab navigation escaped the viewer.");
                }
            Click(GalleryControl<Button>(viewer, "GalleryFit"));
            Require(!viewer.CaptureState().ManualZoom, "Fit did not reset manual zoom.");
            // Cancel 100 superseded verified reads. Only the final generation may publish pixels.
            Task[] switches = Enumerable.Range(0, 100).Select(_ => viewer.ShowAsync(selected, CancellationToken.None)).ToArray();
            await Task.WhenAll(switches).WaitAsync(TimeSpan.FromSeconds(30));
            Require(displayed.Source is Bitmap, "Rapid image changes lost the current image.");
            int moves = 0; viewer.MoveRequested += delta => moves += delta;
            Click(GalleryControl<Button>(viewer, "GalleryPrevious"));
            Require(moves == 0, "Disabled previous navigation crossed the first-image boundary.");
            Click(GalleryControl<Button>(viewer, "GalleryNext"));
            Require(moves == 1, "Next button did not request exactly one image.");
            await File.WriteAllBytesAsync(path, "changed"u8.ToArray());
            await viewer.ShowAsync(selected, CancellationToken.None);
            Require(displayed.Source == null && !string.IsNullOrWhiteSpace(GalleryControl<TextBlock>(viewer, "GalleryStatus").Text),
                "Changed manifest bytes produced a stale or unverified image.");
            await File.WriteAllBytesAsync(path, bytes);
            Task closingLoad = viewer.ShowAsync(selected, CancellationToken.None);
            await viewer.StopAsync(); await closingLoad;
            Require(displayed.Source == null, "A canceled decode published pixels after closing.");
            int readRequests = 0;
            viewer.ReadPostsRequested += () => readRequests++;
            viewer.SetEmpty("No saved images", true);
            Require(GalleryControl<Button>(viewer, "GalleryReadPosts").IsVisible, "Empty gallery lost Read posts action.");
            Click(GalleryControl<Button>(viewer, "GalleryReadPosts"));
            Require(readRequests == 1, "Empty gallery Read posts did not request the reader exactly once.");
            Require(GalleryControl<TextBlock>(viewer, "GalleryWarnings").Text == "Manifest warning retained", "Empty state discarded manifest warnings.");
            Console.WriteLine("SMOKE gallery: verified offline fit/original, >1200px pixel detail, physical 1:1, context, restore and 100 superseded loads passed.");
        }
        finally { await viewer.StopAsync(); window.Content = content; Directory.Delete(directory, true); }
    }
    private static T GalleryControl<T>(GalleryViewer viewer, string name) where T : Control =>
        viewer.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

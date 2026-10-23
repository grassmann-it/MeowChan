using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Gallery;
using MeowChan.Storage;
using MeowChan.Testing;
using SkiaSharp;

namespace MeowChan.DesktopSmoke;

// Deliberately uses the pre-change viewer API so the exact same workload can be
// compiled against the recorded baseline, with decode entry logging added there.
internal static class GalleryFitBenchmark
{
    internal static async Task RunAsync(MainWindow window)
    {
        string directory = Path.Combine(TemporaryWorkspacePaths.TempRoot, "gallery-fit-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        object? originalContent = window.Content;
        GalleryViewer viewer = new();
        try
        {
            using (SKBitmap source = new(6000, 4000))
            using (SKCanvas canvas = new(source))
            {
                canvas.Clear(SKColors.LightSteelBlue);
                using SKPaint paint = new() { Color = SKColors.MidnightBlue, StrokeWidth = 3 };
                for (int x = 0; x < source.Width; x += 50) canvas.DrawLine(x, 0, x, source.Height, paint);
                using SKImage image = SKImage.FromBitmap(source);
                using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
                byte[] bytes = encoded.ToArray();
                await File.WriteAllBytesAsync(Path.Combine(directory, "large.png"), bytes);
                await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), JsonSerializer.Serialize(new
                {
                    Version = 1, Entries = new Dictionary<string, object> { ["large.png"] = new
                    {
                        MediaIdentifier = "large", SourceUrl = "https://offline.invalid/large", FileName = "large.png",
                        Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                        Md5 = Convert.ToBase64String(MD5.HashData(bytes))
                    } }
                }));
            }
            MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory);
            MediaAssetReference asset = manifest.GetPage().Single();
            GalleryEntry entry = new(new GalleryAssetKey(new ResourceId("4chan", "g", "777"), directory, asset.AssetId),
                manifest, asset, null, null, true, false);
            window.Width = 1280; window.Height = 900;
            window.Content = viewer;
            await Program.WaitUntilAsync(() => viewer.GetGalleryVisualDescendants().OfType<Image>().Any(c => c.Name == "GalleryImage"));
            Grid viewport = viewer.GetGalleryVisualDescendants().OfType<Grid>().Single(c => c.Name == "GalleryViewport");
            Image displayed = viewer.GetGalleryVisualDescendants().OfType<Image>().Single(c => c.Name == "GalleryImage");
            List<object> samples = new();
            for (int cycle = 0; cycle < 8; cycle++)
            {
                viewer.Width = 520; viewer.Height = 440;
                await Program.WaitUntilAsync(() => viewport.Bounds.Width is > 100 and < 600);
                await viewer.ShowAsync(entry, CancellationToken.None);
                var initial = ((Bitmap)displayed.Source!).PixelSize;
                Stopwatch response = Stopwatch.StartNew();
                viewer.Width = 1150; viewer.Height = 780;
                await Program.WaitUntilAsync(() => viewport.Bounds.Width > 1000);
                // UI geometry becomes available independently of the background decode.
                double geometryMs = response.Elapsed.TotalMilliseconds;
                double scale = window.RenderScaling;
                int requiredWidth = (int)Math.Floor(Math.Min(viewport.Bounds.Width * scale,
                    viewport.Bounds.Height * scale * 1.5));
                await Program.WaitUntilAsync(() => displayed.Source is Bitmap b && b.PixelSize.Width >= requiredWidth - 1);
                double readyMs = response.Elapsed.TotalMilliseconds;
                var final = ((Bitmap)displayed.Source!).PixelSize;
                // Exercise shrinking without releasing/reopening the current image.
                viewer.Width = 700; viewer.Height = 550;
                await Program.WaitUntilAsync(() => viewport.Bounds.Width < 800);
                await Task.Delay(200);
                using Process process = Process.GetCurrentProcess(); process.Refresh();
                samples.Add(new { cycle, initialWidth = initial.Width, initialHeight = initial.Height,
                    finalWidth = final.Width, finalHeight = final.Height, fitPixels = (long)final.Width * final.Height,
                    geometryMs, readyMs, residentBytes = process.WorkingSet64 });
                await viewer.StopAsync();
            }
            Console.WriteLine("MEDIA_FIT_BENCHMARK " + JsonSerializer.Serialize(new { scale = window.RenderScaling, samples }));
        }
        finally
        {
            await viewer.StopAsync(); window.Content = originalContent; Directory.Delete(directory, true);
        }
    }
}

using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Gallery;
using MeowChan.Storage;
using MeowChan.Testing;
using SkiaSharp;

namespace MeowChan.DesktopSmoke;

// Deterministic stop/input boundary. No engine, native playback or arbitrary timing delay.
internal static class GalleryTransitionReviewChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        bool strict = Environment.GetEnvironmentVariable("MEOWCHAN_GALLERY_TRANSITION_REVIEW") == "1";
        string directory = Path.Combine(TemporaryWorkspacePaths.TempRoot, "gallery-transition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        object? content = window.Content;
        GalleryViewer viewer = new() { Width = 640, Height = 400 };
        GalleryVideoPlayer video = new();
        SemaphoreSlim operations = Field<SemaphoreSlim>(video, "_operations");
        bool held = false;
        Task? showing = null;
        try
        {
            GalleryEntry oldEntry = await FixtureAsync(directory, "old", 1800, 1000, SKColors.DarkRed);
            GalleryEntry nextEntry = await FixtureAsync(directory, "next", 1600, 1200, SKColors.DarkBlue);
            window.Content = viewer;
            await Program.WaitUntilAsync(() => viewer.GetGalleryVisualDescendants().OfType<Grid>()
                .Any(control => control.Name == "GalleryViewport" && control.Bounds.Width > 100 && control.Bounds.Height > 100));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            Image image = viewer.GetGalleryVisualDescendants().OfType<Image>().Single(control => control.Name == "GalleryImage");
            List<(string Asset, string Request)> calls = new();
            Bitmap? nextDecoded = null;
            viewer.DecodeImageAsync = async (entry, request, token) =>
            {
                calls.Add((entry.Key.AssetId, request.GetType().Name));
                var decoded = await GalleryImageDecoder.DecodeAsync(entry, request, token);
                if (entry.EntryKey == nextEntry.EntryKey) nextDecoded = decoded.Bitmap;
                return decoded;
            };
            await viewer.ShowAsync(oldEntry, CancellationToken.None);
            // Model a real previously used video host retained across image navigation.
            // Holding its existing operation lock makes StopAsync await a finite explicit barrier.
            typeof(GalleryViewer).GetField("_video", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewer, video);
            await operations.WaitAsync(); held = true;
            int before = calls.Count;
            showing = viewer.ShowAsync(nextEntry, CancellationToken.None);
            await Program.WaitUntilAsync(() => Field<long>(video, "_generation") > 0);
            bool oldInfoDuringStop = Field<GalleryImageInfo?>(viewer, "_info") != null;
            bool enabledDuringStop = viewer.GetGalleryVisualDescendants().OfType<Button>()
                .Single(button => button.Name == "GalleryActual").IsEffectivelyEnabled;
            viewer.Focus();
            viewer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.D1 });
            int extraCalls = calls.Count - before;
            if (extraCalls > 0)
                await Program.WaitUntilAsync(() => image.Source is Bitmap);
            bool publishedDuringStop = image.Source != null;
            var observed = new {
                oldInfoDuringStop, enabledDuringStop, extraCalls, publishedDuringStop,
                calledAssets = calls.Skip(before).Select(call => call.Asset).ToArray(),
                calledRequests = calls.Skip(before).Select(call => call.Request).ToArray(),
                oldAsset = oldEntry.Key.AssetId, nextAsset = nextEntry.Key.AssetId,
                imageSizeDuringStop = (image.Source as Bitmap)?.PixelSize.ToString(),
                stateDuringStop = viewer.CaptureState(), scale = window.RenderScaling };
            Console.WriteLine("SMOKE gallery-transition observation: " + JsonSerializer.Serialize(observed));
            Program.CaptureWindow(window, "gallery-transition-stop-held-after-actual-key.png");
            operations.Release(); held = false;
            await showing.WaitAsync(TimeSpan.FromSeconds(15));
            bool nextCorrect = viewer.CaptureState().Key == nextEntry.Key && nextDecoded != null
                && ReferenceEquals(image.Source, nextDecoded);
            Console.WriteLine("SMOKE gallery-transition result: " + JsonSerializer.Serialize(new {
                strict, noStaleInputDecode = extraCalls == 0, noStalePublication = !publishedDuringStop,
                imageCommandsDisabled = !enabledDuringStop, nextCorrect,
                finalSourceSize = (image.Source as Bitmap)?.PixelSize.ToString(),
                nextDecodedSize = nextDecoded?.PixelSize.ToString() }));
            Program.CaptureWindow(window, "gallery-transition-next-complete.png");
            if (strict && (extraCalls != 0 || publishedDuringStop || enabledDuringStop || !nextCorrect))
                throw new InvalidOperationException("Image controls retained old decode state while video StopAsync was pending.");
        }
        finally
        {
            if (held) operations.Release();
            if (showing != null) await showing.WaitAsync(TimeSpan.FromSeconds(15));
            await viewer.StopAsync();
            await video.DisposeAsync();
            window.Content = content;
            Directory.Delete(directory, true);
        }
        Console.WriteLine("SMOKE gallery-transition completed; controlled StopAsync barrier, not a native playback claim.");
    }

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    private static async Task<GalleryEntry> FixtureAsync(string parent, string name, int width, int height, SKColor color)
    {
        string directory = Path.Combine(parent, name); Directory.CreateDirectory(directory);
        byte[] bytes;
        using (SKBitmap bitmap = new(width, height))
        {
            using (SKCanvas canvas = new(bitmap)) canvas.Clear(color);
            using SKImage image = SKImage.FromBitmap(bitmap);
            using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
            bytes = data.ToArray();
        }
        string fileName = name + ".png";
        await File.WriteAllBytesAsync(Path.Combine(directory, fileName), bytes);
        object record = new { MediaIdentifier = name, SourceUrl = "https://offline.invalid/" + name, FileName = fileName,
            Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), Md5 = Convert.ToBase64String(MD5.HashData(bytes)) };
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), JsonSerializer.Serialize(new {
            Version = 1, Entries = new Dictionary<string, object> { [fileName] = record } }));
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory);
        MediaAssetReference asset = manifest.GetPage().Single();
        return new GalleryEntry(new GalleryAssetKey(new ResourceId("4chan", "g", "123"), directory, asset.AssetId),
            manifest, asset, new CapturedPost(name, 0, PostFieldPresence.Html, Html: "<p>Transition boundary</p>"), null, false, false);
    }
}

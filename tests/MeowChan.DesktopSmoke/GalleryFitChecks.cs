using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using MeowChan.Desktop.Localization;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Gallery;
using MeowChan.Storage;
using MeowChan.Testing;
using SkiaSharp;

namespace MeowChan.DesktopSmoke;

/// <summary>Native-window Fit checks; every successful decode reads a verified offline manifest.</summary>
internal static class GalleryFitChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        string? requestedLanguage = Environment.GetEnvironmentVariable("MEOWCHAN_MEDIA_TEST_LANGUAGE");
        string? requestedTheme = Environment.GetEnvironmentVariable("MEOWCHAN_MEDIA_TEST_THEME");
        if (!string.IsNullOrEmpty(requestedLanguage)) Texts.SetLanguage(requestedLanguage);
        if (!string.IsNullOrEmpty(requestedTheme))
        {
            ThemeVariant theme = requestedTheme.Equals("dark", StringComparison.OrdinalIgnoreCase) ? ThemeVariant.Dark : ThemeVariant.Light;
            Application.Current!.RequestedThemeVariant = theme;
            await Program.WaitUntilAsync(() => window.ActualThemeVariant == theme);
        }
        CheckNativeFitTargets();
        string directory = Path.Combine(TemporaryWorkspacePaths.TempRoot, "meowchan-fit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        object? previousContent = window.Content;
        GalleryViewer viewer = new();
        DecodeProbe probe = new();
        viewer.DecodeImageAsync = probe.DecodeAsync;
        try
        {
            GalleryEntry large = await FixtureAsync(directory, "large", 6000, 4000, SKColors.DarkOrange);
            GalleryEntry small = await FixtureAsync(directory, "small", 224, 255, SKColors.CornflowerBlue);
            window.Content = viewer;
            await Program.WaitUntilAsync(() => viewer.GetGalleryVisualDescendants().Any(control => control is Image { Name: "GalleryImage" }));
            viewer.SetPosition("Fit integration", false, false);
            viewer.SetWarnings("Retained manifest warning");
            Image image = Control<Image>(viewer, "GalleryImage");
            Grid viewport = Control<Grid>(viewer, "GalleryViewport");
            await ResizeAsync(viewer, 360, 260);
            await viewer.ShowAsync(large, CancellationToken.None);
            await SettledAsync();
            Require(image.Source is Bitmap, "Initial verified Fit did not publish an image.");
            AssertTarget(viewer, image, viewport, 6000, 4000);
            int beforeBurst = probe.Calls.Count;
            await ResizeAsync(viewer, 440, 310); await Task.Delay(25);
            await ResizeAsync(viewer, 540, 370); await Task.Delay(25);
            await ResizeAsync(viewer, 740, 510);
            long finalResize = Stopwatch.GetTimestamp();
            await Program.WaitUntilAsync(() => probe.Calls.Count > beforeBurst && probe.Active == 0);
            await SettledAsync();
            Require(probe.Calls.Count == beforeBurst + 1, "A resize burst decoded intermediate Fit targets.");
            Require(Stopwatch.GetElapsedTime(finalResize, probe.Calls[^1].Started).TotalMilliseconds >= 100,
                "Fit resize decode did not wait for the trailing debounce.");
            AssertTarget(viewer, image, viewport, 6000, 4000);
            Bitmap retained = (Bitmap)image.Source!;
            int beforeShrink = probe.Calls.Count;
            await ResizeAsync(viewer, 400, 280); await SettledAsync();
            await ResizeAsync(viewer, 650, 450); await SettledAsync();
            Require(probe.Calls.Count == beforeShrink && ReferenceEquals(retained, image.Source),
                "Shrink or growth covered by the retained Fit decoded again.");
            Require(probe.Calls.All(call => call.Request is GalleryDecodeRequest.Fit), "Automatic resize requested Original.");

            // Oversized control layout exercises the cap without resizing the native window.
            await ResetAsync(viewer, large);
            int capStart = probe.Calls.Count;
            await ResizeAsync(viewer, 7000, 5000);
            await Program.WaitUntilAsync(() => probe.Calls.Count == capStart + 1 && probe.Active == 0);
            AssertTarget(viewer, image, viewport, 6000, 4000);
            Bitmap capped = (Bitmap)image.Source!;
            Require((long)capped.PixelSize.Width * capped.PixelSize.Height > 15_900_000,
                "Oversized viewport did not reach the 16 MP Fit cap.");
            int cappedCalls = probe.Calls.Count;
            await ResizeAsync(viewer, 7600, 5600); await SettledAsync();
            await ResizeAsync(viewer, 360, 260); await SettledAsync();
            Require(probe.Calls.Count == cappedCalls && ReferenceEquals(capped, image.Source),
                "Growth above the Fit cap or subsequent shrink decoded another buffer.");
            Require(probe.Calls.Skip(capStart).All(call => call.Request is GalleryDecodeRequest.Fit),
                "Capped automatic Fit requested Original.");

            // Start with a context-reduced viewport so closing it really requires more pixels.
            await viewer.StopAsync(); await ResizeAsync(viewer, 1100, 800);
            Click(viewer, "GalleryToggleContext"); viewer.UpdateLayout();
            await Program.WaitUntilAsync(() => Control<Border>(viewer, "GalleryContext").IsVisible && viewport.Bounds.Width < 900);
            int contextStart = probe.Calls.Count;
            await viewer.ShowAsync(large, CancellationToken.None); await SettledAsync();
            AssertTarget(viewer, image, viewport, 6000, 4000);
            double contextViewportWidth = viewport.Bounds.Width;
            int contextInitialCalls = probe.Calls.Count;
            Click(viewer, "GalleryToggleContext"); viewer.UpdateLayout();
            await Program.WaitUntilAsync(() => !Control<Border>(viewer, "GalleryContext").IsVisible && viewport.Bounds.Width > contextViewportWidth + 100);
            await Program.WaitUntilAsync(() => probe.Calls.Count == contextInitialCalls + 1 && probe.Active == 0);
            AssertTarget(viewer, image, viewport, 6000, 4000);
            object? contextFull = image.Source;
            Click(viewer, "GalleryToggleContext"); viewer.UpdateLayout(); await SettledAsync();
            Click(viewer, "GalleryToggleContext"); viewer.UpdateLayout(); await SettledAsync();
            Require(!viewer.CaptureState().ManualZoom && ReferenceEquals(contextFull, image.Source)
                && probe.Calls.Count == contextInitialCalls + 1,
                "Repeated automatic context open/close failed to reuse the larger Fit.");
            Require(probe.Calls.Skip(contextStart).All(call => call.Request is GalleryDecodeRequest.Fit),
                "Automatic context layout requested Original.");

            // A noncancelable old Fit must stay the sole active operation; only the latest pending target runs.
            await ResetAsync(viewer, large);
            DecodeGate latestGate = probe.HoldNext();
            await ResizeAsync(viewer, 500, 350); await latestGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            object? oldImage = image.Source;
            int whileHeld = probe.Calls.Count;
            await ResizeAsync(viewer, 600, 420);
            await ResizeAsync(viewer, 740, 510);
            await SettledAsync();
            Require(probe.Calls.Count == whileHeld && ReferenceEquals(oldImage, image.Source),
                "A pending Fit started concurrently or replaced the retained image before completion.");
            List<object?> publications = new();
            image.PropertyChanged += TrackPublication;
            latestGate.Release.TrySetResult();
            await Program.WaitUntilAsync(() => probe.Calls.Count == whileHeld + 1 && probe.Active == 0);
            await SettledAsync();
            image.PropertyChanged -= TrackPublication;
            Require(!publications.Any(source => ReferenceEquals(source, latestGate.Decoded)), "Superseded Fit published stale pixels.");
            AssertTarget(viewer, image, viewport, 6000, 4000);
            void TrackPublication(object? sender, AvaloniaPropertyChangedEventArgs args)
            { if (args.Property == Image.SourceProperty) publications.Add(image.Source); }

            await ResetAsync(viewer, large);
            DecodeGate switchGate = probe.HoldNext();
            await ResizeAsync(viewer, 620, 430); await switchGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task switchTask = viewer.ShowAsync(small, CancellationToken.None);
            switchGate.Release.TrySetResult();
            await switchTask.WaitAsync(TimeSpan.FromSeconds(5)); await SettledAsync();
            Require(image.Source is Bitmap switched && switched.PixelSize == new PixelSize(224, 255),
                "Image switch published the previous asset's delayed Fit.");
            int beforeSmallOriginal = probe.Calls.Count;
            Click(viewer, "GalleryActual"); await SettledAsync();
            Require(probe.Calls.Count == beforeSmallOriginal, "A complete small Fit was decoded again for 1:1.");
            Require(Math.Abs(image.Width * window.RenderScaling - 224) < 0.01,
                "Small Fit reuse lost physical-pixel 1:1 geometry.");

            await ResetAsync(viewer, large);
            DecodeGate zoomGate = probe.HoldNext();
            await ResizeAsync(viewer, 620, 430); await zoomGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Click(viewer, "GalleryActual"); zoomGate.Release.TrySetResult();
            await Program.WaitUntilAsync(() => image.Source is Bitmap original && original.PixelSize.Width == 6000);
            object? full = image.Source;
            await ResizeAsync(viewer, 740, 510); await SettledAsync();
            Require(ReferenceEquals(full, image.Source) && viewer.CaptureState().ManualZoom,
                "Delayed Fit or resize downgraded Original/manual zoom.");
            Click(viewer, "GalleryFit"); await SettledAsync();
            Require(ReferenceEquals(full, image.Source), "Returning to Fit discarded reusable Original pixels.");

            // Growing Fit past a slight manual zoom must preserve an Original already in flight.
            await ResetAsync(viewer, large);
            DecodeGate originalGate = probe.HoldNext();
            object? beforeOriginal = image.Source;
            Click(viewer, "GalleryPlus");
            await originalGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(viewer.CaptureState().ManualZoom && probe.Calls[^1].Request is GalleryDecodeRequest.Original,
                "A slight zoom did not start the controlled Original decode.");
            double slightZoom = viewer.CaptureState().PhysicalZoom;
            int originalCalls = probe.Calls.Count;
            await ResizeAsync(viewer, 740, 510); await SettledAsync();
            Require(GalleryGeometry.Fit(6000, 4000, viewport.Bounds.Width, viewport.Bounds.Height, window.RenderScaling) > slightZoom,
                "Resize fixture did not grow Fit above the previous slight manual zoom.");
            Require(!originalGate.Cancellation.IsCancellationRequested && probe.Calls.Count == originalCalls && ReferenceEquals(beforeOriginal, image.Source),
                "Automatic Fit canceled or replaced the Original already in flight.");
            originalGate.Release.TrySetResult();
            await Program.WaitUntilAsync(() => ReferenceEquals(image.Source, originalGate.Decoded) && probe.Active == 0);
            await SettledAsync();
            Require(probe.Calls.Count == originalCalls && image.Source is Bitmap enlargedOriginal && enlargedOriginal.PixelSize.Width == 6000,
                "Automatic Fit queued a replacement or downgraded the completed Original.");

            // Failed upgrade keeps the visible buffer and is not retried by unchanged layout churn.
            await ResetAsync(viewer, large);
            object? beforeFailure = image.Source;
            probe.FailNext = true;
            await ResizeAsync(viewer, 620, 430);
            await Program.WaitUntilAsync(() => Control<TextBlock>(viewer, "GalleryStatus").Text?.Contains("controlled Fit failure") == true);
            int failedCalls = probe.Calls.Count;
            Require(ReferenceEquals(beforeFailure, image.Source), "A failed upgrade removed the last usable image.");
            Require(Control<TextBlock>(viewer, "GalleryWarnings").Text == "Retained manifest warning",
                "A failed upgrade discarded manifest warnings.");
            viewer.InvalidateMeasure(); viewer.UpdateLayout(); await SettledAsync();
            Require(probe.Calls.Count == failedCalls, "An unchanged failed Fit target was retried automatically.");
            Click(viewer, "GalleryFit");
            await Program.WaitUntilAsync(() => probe.Calls.Count == failedCalls + 1 && probe.Active == 0);
            Require(string.IsNullOrEmpty(Control<TextBlock>(viewer, "GalleryStatus").Text), "Explicit Fit retry did not clear the warning.");

            await ResetAsync(viewer, large);
            DecodeGate stopGate = probe.HoldNext();
            await ResizeAsync(viewer, 620, 430); await stopGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await viewer.StopAsync(); stopGate.Release.TrySetResult();
            await Program.WaitUntilAsync(() => probe.Active == 0); await SettledAsync();
            Require(image.Source == null, "Closing published a delayed Fit after StopAsync.");

            await ResizeAsync(viewer, 360, 260);
            DecodeGate cancellationGate = probe.HoldNext();
            using (CancellationTokenSource cancellation = new())
            {
                Task canceledShow = viewer.ShowAsync(large, cancellation.Token);
                await cancellationGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancellation.Cancel(); cancellationGate.Release.TrySetResult();
                await canceledShow.WaitAsync(TimeSpan.FromSeconds(5)); await SettledAsync();
                Require(image.Source == null, "Canceled session published a noncancelable decode result.");
            }

            List<long> resident = new(); List<double> latencies = new();
            int perfStart = probe.Calls.Count;
            for (int cycle = 0; cycle < 6; cycle++)
            {
                await ResetAsync(viewer, large);
                object? compact = image.Source;
                PrintState($"cycle-{cycle}-compact", viewer, image, viewport, probe);
                AssertTarget(viewer, image, viewport, 6000, 4000);
                Stopwatch latency = Stopwatch.StartNew();
                await ResizeAsync(viewer, 740, 510);
                PrintState($"cycle-{cycle}-resized", viewer, image, viewport, probe);
                try { await Program.WaitUntilAsync(() => !ReferenceEquals(compact, image.Source) && probe.Active == 0); }
                catch { PrintState($"cycle-{cycle}-timeout", viewer, image, viewport, probe); throw; }
                latencies.Add(latency.Elapsed.TotalMilliseconds);
                await ResizeAsync(viewer, 360, 260); await SettledAsync();
                if (cycle % 2 == 1)
                {
                    GC.Collect(); GC.WaitForPendingFinalizers();
                    using Process process = Process.GetCurrentProcess(); process.Refresh(); resident.Add(process.WorkingSet64);
                }
            }
            Require(probe.Calls.Skip(perfStart).All(call => call.Request is GalleryDecodeRequest.Fit), "Resize performance cycles decoded Original.");
            Require(probe.Calls.Count - perfStart == 12, "Six Fit cycles did not perform exactly six initial and six upgrade decodes.");
            Require(resident[^1] - resident[0] < 128L * 1024 * 1024, "Repeated Fit cycles retained excessive native decoded memory.");
            Require(probe.MaximumActive == 1, "Viewer allowed multiple active decodes.");
            Program.CaptureWindow(window, $"smoke-gallery-fit-{window.RenderScaling:0.0}-{Texts.Language}-{window.ActualThemeVariant}.png");
            Console.WriteLine("SMOKE gallery-fit metrics: " + JsonSerializer.Serialize(new {
                scale = window.RenderScaling, language = MeowChan.Desktop.Localization.Texts.Language,
                theme = window.ActualThemeVariant.ToString(), source = "6000x4000",
                cycles = 6, decodes = probe.Calls.Count - perfStart, maximumActive = probe.MaximumActive,
                upgradeLatencyMilliseconds = latencies, workingSetBytesAfter2_4_6Cycles = resident
            }));
            Console.WriteLine("SMOKE gallery-fit: physical targets, trailing latest resize, retained shrink, single worker, switch/zoom/stop/cancel races, small original reuse, failure/retry and real decode memory cycles passed.");
        }
        finally
        {
            probe.ReleaseAll(); await viewer.StopAsync();
            await Program.WaitUntilAsync(() => probe.Active == 0);
            window.Content = previousContent;
            Directory.Delete(directory, true);
        }
    }

    private static void PrintState(string stage, GalleryViewer viewer, Image image, Grid viewport, DecodeProbe probe)
    {
        Console.WriteLine("SMOKE gallery-fit diagnostic: " + JsonSerializer.Serialize(new {
            stage, viewer = viewer.Bounds.ToString(), viewport = viewport.Bounds.ToString(),
            bitmap = (image.Source as Bitmap)?.PixelSize.ToString(), state = viewer.CaptureState(),
            active = probe.Active, calls = probe.Calls.Count,
            recentRequests = probe.Calls.TakeLast(4).Select(call => call.Request.ToString()).ToArray(),
            status = Control<TextBlock>(viewer, "GalleryStatus").Text
        }));
    }

    private static void CheckNativeFitTargets()
    {
        foreach (var fixture in new[] {
            (Width: 100, Height: 200, ViewWidth: 73d, ViewHeight: 109d),
            (Width: 1, Height: 2000, ViewWidth: 1d, ViewHeight: 500d),
            (Width: 17, Height: 11, ViewWidth: 1d, ViewHeight: 1d),
            (Width: 5001, Height: 4000, ViewWidth: 20000d, ViewHeight: 20000d) })
        {
            using SKBitmap source = new(fixture.Width, fixture.Height);
            using (SKCanvas canvas = new(source)) canvas.Clear(SKColors.MediumPurple);
            using SKImage encodedImage = SKImage.FromBitmap(source);
            using SKData data = encodedImage.Encode(SKEncodedImageFormat.Png, 100);
            using MemoryStream stream = new(data.ToArray());
            GalleryImageInfo info = GalleryImageDecoder.Inspect(stream);
            PixelSize target = GalleryFitTarget.Calculate(info, new(fixture.ViewWidth, fixture.ViewHeight));
            stream.Position = 0;
            using Bitmap decoded = GalleryImageDecoder.DecodeFit(stream, target);
            Require(decoded.PixelSize == target, $"Native Fit {fixture.Width}x{fixture.Height} decoded {decoded.PixelSize}, expected {target}.");
            Require(target.Width >= 1 && target.Height >= 1 && (long)target.Width * target.Height <= GalleryFitTarget.MaximumPixels,
                "Native Fit decoded an invalid or over-cap buffer.");
            Console.WriteLine($"SMOKE gallery-fit native target: source={fixture.Width}x{fixture.Height}; target={target}; actual={decoded.PixelSize}");
        }
    }

    private static async Task ResetAsync(GalleryViewer viewer, GalleryEntry entry)
    {
        await viewer.StopAsync(); await ResizeAsync(viewer, 360, 260);
        await viewer.ShowAsync(entry, CancellationToken.None); await SettledAsync();
    }

    private static async Task ResizeAsync(GalleryViewer viewer, double width, double height)
    {
        viewer.Width = width; viewer.Height = height; viewer.UpdateLayout();
        Grid viewport = Control<Grid>(viewer, "GalleryViewport");
        // Parent Bounds can update before its child layout. Initial Show must read
        // the compact viewport, otherwise its valid retained Fit can cover growth.
        await Program.WaitUntilAsync(() => Math.Abs(viewer.Bounds.Width - width) < 1 && Math.Abs(viewer.Bounds.Height - height) < 1
            && viewport.Bounds.Width >= width - 9 && viewport.Bounds.Width <= width + 1
            && Math.Abs(viewport.Bounds.Height - height) < 1);
    }

    private static Task SettledAsync() => Task.Delay(350);

    private static void AssertTarget(GalleryViewer viewer, Image image, Grid viewport, int width, int height)
    {
        double scale = TopLevel.GetTopLevel(viewer)!.RenderScaling;
        PixelSize target = GalleryFitTarget.Calculate(new(width, height, "Png", false), new(viewport.Bounds.Width * scale, viewport.Bounds.Height * scale));
        Bitmap bitmap = (Bitmap)image.Source!;
        Require(Math.Abs(bitmap.PixelSize.Width - target.Width) <= 1 && Math.Abs(bitmap.PixelSize.Height - target.Height) <= 1,
            $"Fit pixels {bitmap.PixelSize} do not match physical viewport target {target} at scale {scale}.");
        Require((long)bitmap.PixelSize.Width * bitmap.PixelSize.Height <= 16_000_000, "Fit exceeded 16 MP.");
    }

    private static T Control<T>(GalleryViewer viewer, string name) where T : Avalonia.Controls.Control =>
        viewer.GetGalleryVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(GalleryViewer viewer, string name) => Control<Button>(viewer, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static async Task<GalleryEntry> FixtureAsync(string parent, string name, int width, int height, SKColor color)
    {
        string directory = Path.Combine(parent, name); Directory.CreateDirectory(directory);
        byte[] bytes;
        using (SKBitmap bitmap = new(width, height))
        {
            using (SKCanvas canvas = new(bitmap))
            {
                canvas.Clear(color);
                using SKPaint paint = new() { Color = SKColors.White, StrokeWidth = 3 };
                for (int x = 0; x < width; x += 37) canvas.DrawLine(x, 0, width - x, height, paint);
            }
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
            manifest, asset, new CapturedPost(name, 0, PostFieldPresence.Html, Html: "<p>Fit race fixture</p>"), null, false, false);
    }

    private sealed record DecodeCall(GalleryDecodeRequest Request, long Started);
    private sealed class DecodeGate
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Bitmap? Decoded { get; set; }
        internal CancellationToken Cancellation { get; set; }
    }
    private sealed class DecodeProbe
    {
        internal List<DecodeCall> Calls { get; } = new();
        internal int Active { get; private set; }
        internal int MaximumActive { get; private set; }
        internal bool FailNext { get; set; }
        private DecodeGate? _next;
        private readonly List<DecodeGate> _gates = new();
        internal DecodeGate HoldNext() { DecodeGate gate = new(); _gates.Add(gate); _next = gate; return gate; }
        internal void ReleaseAll() { foreach (DecodeGate gate in _gates) gate.Release.TrySetResult(); }
        internal async Task<(Bitmap Bitmap, GalleryImageInfo Info)> DecodeAsync(GalleryEntry entry, GalleryDecodeRequest request, CancellationToken cancellation)
        {
            Calls.Add(new(request, Stopwatch.GetTimestamp())); Active++; MaximumActive = Math.Max(MaximumActive, Active);
            DecodeGate? gate = _next; _next = null;
            bool fail = FailNext; FailNext = false;
            try
            {
                if (fail) throw new IOException("controlled Fit failure");
                // Ignore cancellation only for a controlled codec race, returning ownership to the viewer.
                var decoded = await GalleryImageDecoder.DecodeAsync(entry, request, gate == null ? cancellation : CancellationToken.None);
                if (gate != null)
                {
                    gate.Decoded = decoded.Bitmap; gate.Cancellation = cancellation; gate.Entered.TrySetResult();
                    await gate.Release.Task;
                }
                return decoded;
            }
            finally { Active--; }
        }
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using Ursa.Controls;

namespace MeowChan.ImageViewerComparison;

internal sealed class ComparisonRun(ComparisonWindow window, Options options)
{
    private readonly List<string> _failures = new();
    private readonly List<object> _rows = new();
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly DispatcherTimer _sampler = new() { Interval = TimeSpan.FromMilliseconds(20) };
    private long _peakWorking, _peakPrivate, _emptyWorking, _emptyPrivate;
    private void Check(bool condition, string message) { if (!condition) _failures.Add(message); }
    private void Sample()
    {
        _process.Refresh();
        _peakWorking = Math.Max(_peakWorking, _process.WorkingSet64);
        _peakPrivate = Math.Max(_peakPrivate, _process.PrivateMemorySize64);
    }
    private static string FileHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    internal async Task<int> RunAsync()
    {
        _sampler.Tick += (_, _) => Sample(); _sampler.Start();
        try
        {
            await window.FrameAsync();
            _process.Refresh(); _emptyWorking = _process.WorkingSet64; _emptyPrivate = _process.PrivateMemorySize64;
            var metadata = new
            {
                schema = 1, utc = DateTimeOffset.UtcNow, options, runtime = RuntimeInformation.FrameworkDescription,
                os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                scale = window.RenderScaling, viewport = window.Viewport.Bounds.ToString(),
                backend = options.Backend,
                emptyWorkingBytes = _emptyWorking, emptyPrivateBytes = _emptyPrivate,
                appSha256 = FileHash(typeof(ComparisonRun).Assembly.Location),
                coreSha256 = FileHash(typeof(MeowChan.Storage.MediaAssetReader).Assembly.Location),
                sourceFreezeSha256 = FileHash(Path.Combine(AppContext.BaseDirectory, "source-freeze.json")),
                decoderSource = "../../Desktop/GalleryImageDecoder.cs (Compile Link; see source-freeze.json)",
                ursaAssembly = typeof(ImageViewer).Assembly.GetName().ToString(),
                ursaSha256 = FileHash(typeof(ImageViewer).Assembly.Location),
                fixtureManifestSha256 = FileHash(Path.Combine(options.Fixtures, ".meowchan-manifest.json")),
                note = "First image is a two-animation-frame submission fence, not photon latency. RAM peaks sampled at 20ms plus action/decode boundaries; process includes renderer caches. No profile is opened."
            };
            await WriteAsync("metadata.json", metadata);
            // Same number of frame-fence waits as a task sequence, without image
            // or interactions. Keep idle CPU/RAM evidence separate from A/B.
            for (int cycle = 0; cycle < (options.ChecksOnly ? 1 : 20); cycle++)
            {
                _peakWorking = _peakPrivate = 0;
                TimeSpan idleCpu = _process.TotalProcessorTime;
                Stopwatch idle = Stopwatch.StartNew();
                for (int frame = 0; frame < 12; frame++) await window.FrameAsync();
                Sample();
                _rows.Add(new { kind = "empty-host", cycle, elapsedMs = idle.Elapsed.TotalMilliseconds,
                    cpuMs = (_process.TotalProcessorTime - idleCpu).TotalMilliseconds,
                    peakWorkingBytes = _peakWorking, peakPrivateBytes = _peakPrivate });
            }
            FixtureCase[] good = window.Cases.Where(c => !c.ExpectedError).ToArray();
            foreach (FixtureCase fixture in good)
            {
                // Pass 1: one identical owned bitmap is handed successively to A/B.
                // Render-only trials exclude its shared decode from both timings.
                using (BitmapOwner owner = await window.DecodeAsync(fixture, CancellationToken.None))
                {
                    string bitmapToken = Guid.NewGuid().ToString("N");
                    for (int cycle = options.ChecksOnly ? 0 : -5; cycle < (options.ChecksOnly ? 1 : 20); cycle++)
                        foreach (int variant in Order(cycle))
                            await TrialAsync(fixture, owner, variant, cycle, "shared-bitmap", bitmapToken, 0, null);
                }
                Check(BitmapOwner.Count == 0, fixture.Name + ": owner remains after render-only pair.");
                // Pass 2 includes the exact same verified manifest + production
                // Original decode independently for each A/B timing.
                for (int cycle = options.ChecksOnly ? 0 : -5; cycle < (options.ChecksOnly ? 1 : 20); cycle++)
                {
                    foreach (int variant in Order(cycle))
                    {
                        Stopwatch open = Stopwatch.StartNew();
                        _peakWorking = _peakPrivate = 0;
                        TimeSpan cpu = _process.TotalProcessorTime;
                        Stopwatch decode = Stopwatch.StartNew();
                        using (BitmapOwner owner = await window.DecodeAsync(fixture, CancellationToken.None))
                        {
                            double decodeMs = decode.Elapsed.TotalMilliseconds; Sample();
                            await TrialAsync(fixture, owner, variant, cycle, "with-decode", null, decodeMs, (open, cpu));
                        }
                        Check(BitmapOwner.Count == 0, fixture.Name + ": owner remains after decode trial.");
                    }
                }
            }
            await ErrorChecksAsync();
            await LateCloseAsync(good.Single(c => c.Name == "near-budget.png"));
            foreach (int variant in new[] { 0, 1 })
            {
                for (int cycle = 0; cycle < (options.ChecksOnly ? 0 : 50); cycle++)
                {
                    FixtureCase fixture = good[cycle % good.Length];
                    using (BitmapOwner owner = await window.DecodeAsync(fixture, CancellationToken.None))
                    {
                        window.ShowBorrowed(variant, owner.Bitmap); await window.FrameAsync();
                        window.ClosePreview(); await window.FrameAsync();
                    }
                    Check(window.SourcesClear && BitmapOwner.Count == 0 && BitmapOwner.Bytes == 0,
                        $"{variant}/{cycle}: source/bitmap retained after close.");
                    // Forced-GC residuals are separate from timed trials and are
                    // recorded EVERY cycle, so a growth trend cannot be hidden.
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    await window.FrameAsync(); _process.Refresh();
                    _rows.Add(new { kind = "close-cycle", variant, cycle, fixture = fixture.Name,
                        workingBytes = _process.WorkingSet64, privateBytes = _process.PrivateMemorySize64,
                        heldBitmaps = BitmapOwner.Count, heldBytes = BitmapOwner.Bytes,
                        sourcesClear = window.SourcesClear });
                }
            }
            await WriteAsync("samples.json", _rows);
            await WriteAsync("checks.json", new { passed = _failures.Count == 0, failures = _failures,
                nativeInputAndScreenReader = "OPEN: in-process actions are not OS pointer/keyboard/AT evidence",
                decision = "Product stays on Image/Transform. Run analyze.py and native recipe before suitability decision." });
            Console.WriteLine($"URSA21 evidence={options.Output} failures={_failures.Count}");
            return _failures.Count == 0 ? 0 : 1;
        }
        finally
        {
            _sampler.Stop(); window.ClosePreview();
            // Preserve measurements even if a codec/platform exception aborts the run.
            await WriteAsync("samples.json", _rows);
        }
    }
    private static IEnumerable<int> Order(int cycle) => (cycle & 1) == 0 ? new[] { 0, 1 } : new[] { 1, 0 };
    private async Task TrialAsync(FixtureCase fixture, BitmapOwner owner, int variant, int cycle,
        string pass, string? bitmapToken, double decodeMs, (Stopwatch Clock, TimeSpan Cpu)? start)
    {
        double width = options.Mode == "b" ? 640 : 760, height = options.Mode == "b" ? 480 : 500;
        window.Width = width; window.Height = height; await window.FrameAsync();
        if (start == null) _peakWorking = _peakPrivate = 0;
        TimeSpan cpu = start?.Cpu ?? _process.TotalProcessorTime;
        Stopwatch open = start?.Clock ?? Stopwatch.StartNew();
        Stopwatch render = Stopwatch.StartNew();
        window.ShowBorrowed(variant, owner.Bitmap); await window.FrameAsync();
        double renderMs = render.Elapsed.TotalMilliseconds, firstImageMs = open.Elapsed.TotalMilliseconds;
        Sample();
        Check(window.Viewport.Children.Count == 1 && ReferenceEquals(window.Active!.Source, owner.Bitmap),
            $"{variant}/{fixture.Name}: must render only the supplied bitmap.");
        if (variant == 1)
        {
            ImageViewerPresenter? presenter = window.Active!.Control.GetVisualDescendants().OfType<ImageViewerPresenter>().SingleOrDefault();
            Check(presenter != null, "Ursa's actual themed presenter was not materialized.");
            if (presenter != null)
            {
                Point? center = presenter.TranslatePoint(new Point(presenter.Bounds.Width / 2, presenter.Bounds.Height / 2), window.Viewport);
                Check(center is { } p && Math.Abs(p.X - window.Viewport.Bounds.Width / 2) < 1
                    && Math.Abs(p.Y - window.Viewport.Bounds.Height / 2) < 1,
                    $"Ursa presenter center differs from viewport: {center}, {presenter.Bounds}.");
            }
        }
        List<object> interactions = new();
        List<object> geometry = new();
        void CheckGeometry(string stage, double physicalZoom)
        {
            double expectedWidth = owner.Bitmap.PixelSize.Width * physicalZoom / window.RenderScaling;
            double expectedHeight = owner.Bitmap.PixelSize.Height * physicalZoom / window.RenderScaling;
            double expectedX = (window.Viewport.Bounds.Width - expectedWidth) / 2;
            double expectedY = (window.Viewport.Bounds.Height - expectedHeight) / 2;
            Rect? drawn = null;
            ImageViewerPresenter? presenter = null;
            if (variant == 0)
            {
                Image? image = window.Active!.Control.GetVisualDescendants().OfType<Image>().SingleOrDefault();
                if (image?.TranslatePoint(default, window.Viewport) is { } topLeft)
                    drawn = new Rect(topLeft, image.Bounds.Size);
            }
            else
            {
                presenter = window.Active!.Control.GetVisualDescendants().OfType<ImageViewerPresenter>().SingleOrDefault();
                if (presenter?.Source is { } source)
                {
                    // This is the actual 2.2.0 presenter Render destination, using
                    // its coerced Zoom, not the container's advertised value.
                    double w = source.Size.Width * presenter.Zoom, h = source.Size.Height * presenter.Zoom;
                    Point local = new((presenter.Bounds.Width - w) / 2 + presenter.OffsetX,
                        (presenter.Bounds.Height - h) / 2 + presenter.OffsetY);
                    if (presenter.TranslatePoint(local, window.Viewport) is { } topLeft)
                        drawn = new Rect(topLeft, new Size(w, h));
                }
            }
            bool matches = drawn is { } rect && Math.Abs(rect.X - expectedX) < 1
                && Math.Abs(rect.Y - expectedY) < 1 && Math.Abs(rect.Width - expectedWidth) < 1
                && Math.Abs(rect.Height - expectedHeight) < 1;
            Check(matches, $"{variant}/{fixture.Name}/{stage}: actual drawn image {drawn} differs from expected {expectedX},{expectedY},{expectedWidth},{expectedHeight}.");
            Check(Math.Abs(window.Active!.PhysicalZoom - physicalZoom) < 0.0001,
                $"{variant}/{fixture.Name}/{stage}: container zoom differs from target.");
            if (presenter != null)
                Check(Math.Abs(presenter.Zoom * window.RenderScaling - physicalZoom) < 0.0001,
                    $"{variant}/{fixture.Name}/{stage}: presenter coerces zoom ({presenter.Zoom}) independently of container ({window.Active.PhysicalZoom}).");
            geometry.Add(new { stage, physicalZoom, viewportWidth = window.Viewport.Bounds.Width,
                viewportHeight = window.Viewport.Bounds.Height, expectedX, expectedY, expectedWidth, expectedHeight,
                actualX = drawn?.X, actualY = drawn?.Y, actualWidth = drawn?.Width, actualHeight = drawn?.Height,
                presenterZoom = presenter?.Zoom, presenterMinZoom = presenter?.MinZoom,
                presenterMaxZoom = presenter?.MaxZoom, matches });
        }
        async Task Act(string name, Action action)
        {
            Stopwatch response = Stopwatch.StartNew(); action(); await window.FrameAsync(); Sample();
            interactions.Add(new { name, fenceMs = response.Elapsed.TotalMilliseconds });
        }
        await Act("fit", () => window.Active!.Fit());
        double expectedFit = GalleryGeometry.Fit(owner.Bitmap.PixelSize.Width, owner.Bitmap.PixelSize.Height,
            window.Viewport.Bounds.Width, window.Viewport.Bounds.Height, window.RenderScaling);
        Check(Math.Abs(window.Active!.PhysicalZoom - expectedFit) < 0.0001,
            $"{variant}/{fixture.Name}: fit differs ({window.Active.PhysicalZoom} vs {expectedFit}).");
        CheckGeometry("fit-before-resize", expectedFit);
        await Act("100-percent", () => window.Active!.Actual());
        Check(Math.Abs(window.Active!.PhysicalZoom - 1) < 0.0001, $"{variant}: physical 100% is not 1.");
        CheckGeometry("100-percent", 1);
        await Act("pan", () => window.Active!.PanBy(new Vector(120, 80)));
        await Act("zoom-in-1", () => window.Active!.ZoomBy(1.1));
        await Act("zoom-in-2", () => window.Active!.ZoomBy(1.1));
        double manualZoom = window.Active!.PhysicalZoom;
        await Act("resize", () => { window.Width = width + 160; window.Height = height + 100; });
        Check(Math.Abs(window.Active!.PhysicalZoom - manualZoom) < 0.0001,
            $"{variant}/{fixture.Name}: resize changed manual zoom.");
        // Deliberately excessive pan catches unreachable-image regressions.
        await Act("pan-clamp", () => window.Active!.PanBy(new Vector(1_000_000, -1_000_000)));
        Vector offset = window.Active!.Offset;
        Check(Math.Abs(offset.X) <= Math.Max(0, (owner.Bitmap.PixelSize.Width * window.Active.PhysicalZoom / window.RenderScaling - window.Viewport.Bounds.Width) / 2) + 0.01
            && Math.Abs(offset.Y) <= Math.Max(0, (owner.Bitmap.PixelSize.Height * window.Active.PhysicalZoom / window.RenderScaling - window.Viewport.Bounds.Height) / 2) + 0.01,
            $"{variant}/{fixture.Name}: image can be panned out of reach.");
        await Act("fit-after-resize", () => window.Active!.Fit());
        expectedFit = GalleryGeometry.Fit(owner.Bitmap.PixelSize.Width, owner.Bitmap.PixelSize.Height,
            window.Viewport.Bounds.Width, window.Viewport.Bounds.Height, window.RenderScaling);
        CheckGeometry("fit-after-resize", expectedFit);
        if (cycle == 0 && pass == "shared-bitmap")
            window.Screenshot(Path.Combine(options.Output, $"{fixture.Name}-{variant}-fit.png"));
        await Act("close", window.ClosePreview);
        Check(window.SourcesClear, $"{variant}: source retained after close.");
        Check(window.OpenButton.IsFocused, $"{variant}: close did not restore opener focus.");
        Sample();
        _rows.Add(new { kind = "trial", pass, variant, fixture = fixture.Name, cycle, warmup = cycle < 0,
            bitmapToken, sourceSha256 = fixture.Sha256, encodedBytes = fixture.Length,
            pixelWidth = owner.Bitmap.PixelSize.Width, pixelHeight = owner.Bitmap.PixelSize.Height,
            decodedBytes = owner.ByteCount, decodeMs, renderFenceMs = renderMs, firstImageMs,
            cpuMs = (_process.TotalProcessorTime - cpu).TotalMilliseconds,
            peakWorkingBytes = _peakWorking, peakPrivateBytes = _peakPrivate,
            peakWorkingDelta = _peakWorking - _emptyWorking, peakPrivateDelta = _peakPrivate - _emptyPrivate,
            heldBitmaps = BitmapOwner.Count, heldBytes = BitmapOwner.Bytes, interactions, geometry });
    }
    private async Task ErrorChecksAsync()
    {
        foreach (FixtureCase fixture in window.Cases.Where(c => c.ExpectedError))
            foreach (int variant in new[] { 0, 1 })
            {
                string? message = null;
                try
                {
                    using BitmapOwner unexpected = await window.DecodeAsync(fixture, CancellationToken.None);
                    Check(false, fixture.Name + ": expected production decoder rejection.");
                }
                catch (Exception error)
                {
                    message = error.Message; window.ShowError(error); await window.FrameAsync();
                    Check(window.Status.Text == error.Message, "Error state not visible.");
                }
                window.ClosePreview();
                Check(window.SourcesClear && BitmapOwner.Count == 0, "Error fixture retained a bitmap/source.");
                _rows.Add(new { kind = "expected-error", fixture = fixture.Name, variant, message,
                    sourcesClear = window.SourcesClear, heldBitmaps = BitmapOwner.Count });
            }
    }
    private async Task LateCloseAsync(FixtureCase fixture)
    {
        window.SelectFixture(fixture);
        Task opening = window.OpenSelectedAsync();
        window.ClosePreview(); // cancellation/generation changes while decode awaits
        await opening; await window.FrameAsync();
        Check(window.SourcesClear && BitmapOwner.Count == 0, "Late decode displayed or retained a bitmap after close.");
        _rows.Add(new { kind = "late-close", sourcesClear = window.SourcesClear, heldBitmaps = BitmapOwner.Count });
    }
    private Task WriteAsync(string name, object value) => File.WriteAllTextAsync(Path.Combine(options.Output, name),
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
}

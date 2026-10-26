using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MeowChan.Boards.FourChan;
using MeowChan.Desktop;
using MeowChan.Diagnostics;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using SkiaSharp;

namespace MeowChan.DesktopSmoke;

internal sealed class ShellLoadSmokeApp : App
{
    private readonly HttpClient _client;
    private int _disposed;
    internal ShellLoadTransport Transport { get; } = new();
    internal HeldCommitStore Gate { get; } = new();
    public ShellLoadSmokeApp() => _client = new HttpClient(Transport);
    protected override MainWindow CreateMainWindow()
    {
        // Harness-only cadence control. Reflection keeps these same sources compatible with HEAD.
        string? cadence = Environment.GetEnvironmentVariable("MEOWCHAN_SHELL_LOAD_CADENCE_MS");
        var hook = typeof(MainWindow).GetProperty("SessionProgressIntervalForTests",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        object? previous = hook?.GetValue(null);
        if (cadence != null)
        {
            if (!int.TryParse(cadence, out int milliseconds) || milliseconds is not (20 or 50 or 100))
                throw new ArgumentException("The test cadence must be 20, 50 or 100 ms.");
            if (hook == null) throw new InvalidOperationException("This source variant has no test cadence hook.");
            hook.SetValue(null, TimeSpan.FromMilliseconds(milliseconds));
        }
        try
        {
            return new(new DesktopSessionHost(Config.GetConfigDirectory(),
                new IImageBoard[] { new FourChanImageBoard(_client) },
                workStoreFactory: (directory, lease) => Gate.Wrap(new WorkStateStore(directory, lease))));
        }
        finally { if (hook != null) hook.SetValue(null, previous); }
    }
    internal void DisposeFixture()
    {
        Gate.Release(); Transport.AllowMedia();
        if (Interlocked.Exchange(ref _disposed, 1) == 0) { Gate.StopReleaseThread(); _client.Dispose(); }
    }
}

/// <summary>The gate releaser has its own OS thread and starts before media is allowed. No UI/ThreadPool dependency.</summary>
internal sealed partial class HeldCommitStore : IWorkStateStore
{
    private IWorkStateStore _inner = null!;
    private readonly ManualResetEventSlim _release = new(false), _entered = new(false);
    private Thread? _releaseThread;
    private int _armed;
    private long _successes, _bytes, _heldStarted, _heldReleased;
    internal long Successes => Interlocked.Read(ref _successes);
    internal long CommittedBytes => Interlocked.Read(ref _bytes);
    internal long HeldStarted => Interlocked.Read(ref _heldStarted);
    internal long HeldReleased => Interlocked.Read(ref _heldReleased);
    internal IWorkStateStore Wrap(IWorkStateStore inner) { _inner = inner; return this; }
    internal void Arm(TimeSpan duration)
    {
        if (_releaseThread != null) throw new InvalidOperationException("Only one held commit per fresh process.");
        _releaseThread = new Thread(() =>
        {
            try
            {
                if (!_entered.Wait(TimeSpan.FromSeconds(15))) return;
                _release.Wait(duration);
            }
            finally { Release(); }
        }) { IsBackground = true, Name = "PF00 dedicated durable gate releaser" };
        Volatile.Write(ref _armed, 1);
        _releaseThread.Start();
    }
    internal void Release() => _release.Set();
    internal void StopReleaseThread() { _entered.Set(); _release.Set(); _releaseThread?.Join(TimeSpan.FromSeconds(2)); }
    public IReadOnlyList<WorkResource> LoadResources() => _inner.LoadResources();
    public IReadOnlyList<MediaWork> LoadMedia(string url, long generation) => _inner.LoadMedia(url, generation);
    public WorkResource CommitIntent(WorkResource resource) => _inner.CommitIntent(resource);
    public WorkResource? CommitEnd(string url, long generation, ThreadSourceStateValue source, DateTimeOffset observedAt, string? finalSnapshotJson = null)
        => _inner.CommitEnd(url, generation, source, observedAt, finalSnapshotJson);
    public WorkResource? CommitState(WorkResource resource) => _inner.CommitState(resource);
    public bool CommitMedia(MediaWork media) => CommitMeasured(media, () => _inner.CommitMedia(media));
    private bool CommitMeasured(MediaWork media, Func<bool> commit)
    {
        long request = PerformanceDiagnostics.NextId();
        string? asset = media.CommittedAssetId ?? media.MediaIdentifier;
        using var timing = PerformanceDiagnostics.Measure("fixture.store.commit", request, media.ResourceUrl, asset, revision: media.Generation);
        if (media.Status == MediaWorkStatus.Succeeded && Interlocked.Exchange(ref _armed, 0) == 1)
        {
            Interlocked.Exchange(ref _heldStarted, Stopwatch.GetTimestamp());
            PerformanceDiagnostics.Record("fixture.commit-gate", "entered", request, media.ResourceUrl, asset, revision: media.Generation);
            _entered.Set();
            try
            {
                if (!_release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Independent gate releaser failed.");
            }
            finally
            {
                Interlocked.Exchange(ref _heldReleased, Stopwatch.GetTimestamp());
                PerformanceDiagnostics.Record("fixture.commit-gate", "released", request, media.ResourceUrl, asset, revision: media.Generation);
            }
        }
        bool committed = commit();
        if (committed && media.Status == MediaWorkStatus.Succeeded)
        {
            Interlocked.Increment(ref _successes);
            // Durable success bytes are obtained from the actual committed file, separately from transport bytes.
            long length = new FileInfo(media.DestinationPath).Length;
            Interlocked.Add(ref _bytes, length);
            PerformanceDiagnostics.Record("fixture.commit", "succeeded", request, media.ResourceUrl, asset, revision: media.Generation, value: length);
        }
        return committed;
    }
}

internal static class ShellLoadChecks
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    internal static async Task RunAsync(MainWindow window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        ShellLoadSmokeApp app = Application.Current as ShellLoadSmokeApp ?? throw new InvalidOperationException("Wrong load fixture app.");
        using ShellLoadMeasurement measurement = new(app.Transport, app.Gate);
        ShellLoadSessionObservation session = new(window.SessionHost);
        PerformanceDiagnostics.Record("harness.session-api", session.HasProjection ? "projection" : "release-legacy");
        bool galleryOpen = false;
        try
        {
            if (Environment.GetEnvironmentVariable("MEOWCHAN_SHELL_LOAD_WATCHDOG_STALL") == "1")
            {
                await Task.Delay(200);
                Console.WriteLine("SMOKE watchdog control: deliberately blocking owned UI thread; external watchdog must fail and terminate this child.");
                PerformanceDiagnostics.Record("harness.watchdog-control", "ui-block");
                Dispatcher.UIThread.Post(() => Thread.Sleep(System.Threading.Timeout.Infinite), DispatcherPriority.Normal);
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan);
            }
            TextBox input = window.FindControl<TextBox>("UrlInput")!;
            Button add = window.FindControl<Button>("AddUrlButton")!;
            input.Text = "https://boards.4chan.org/g/thread/123";
            add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => window.UrlList.Count == 1 && add.IsEnabled);
            ThreadRow row = window.UrlList[0];
            string scenario = ShellLoadFixture.Scenario;
            if (scenario != "paused")
                await Program.WaitUntilAsync(() => app.Transport.MetadataRequests > 0);
            if (scenario is "held" or "downloads") await app.Transport.MediaRequested.WaitAsync(Timeout);

            GalleryEntry[] entries = ShellLoadFixture.Entries;
            await window.OpenMediaGalleryAsync(entries, entries[0].EntryKey);
            galleryOpen = true;
            GalleryViewer viewer = (GalleryViewer)window.FindControl<ContentControl>("GalleryHost")!.Content!;
            await Program.WaitUntilAsync(() => Control<Image>(viewer, "GalleryImage").Source is Bitmap);
            // Raster identity controls, before measured interactions, on each frozen target.
            viewer = await VerifyPixelsAsync(window, viewer, measurement, "before");
            int index = 0;
            long epoch = session.Epoch;
            async Task<NavigationSample> Interact()
            {
                if (!window.IsVisible) throw new InvalidOperationException("Window closed before measurement finished.");
                if (scenario == "metadata")
                    await window.SessionHost.RequestCheckAsync(new DiagnosticTarget(DiagnosticTargetKind.Thread, row.WatchId));
                int next = index == entries.Length - 1 ? index - 1 : index + 1;
                GalleryEntry target = entries[next];
                long request = PerformanceDiagnostics.NextId(), accepted = 0;
                long intent = Stopwatch.GetTimestamp();
                PerformanceDiagnostics.Record("gallery.navigation", "intent", request, row.WatchId.ToString(), target.Asset.AssetId, epoch);
                void OnMove(int delta)
                {
                    accepted = Stopwatch.GetTimestamp();
                    PerformanceDiagnostics.Record("gallery.navigation", "accepted", request, row.WatchId.ToString(), target.Asset.AssetId, epoch);
                }
                Image displayed = Control<Image>(viewer, "GalleryImage");
                object? previous = displayed.Source;
                viewer.MoveRequested += OnMove;
                try
                {
                    Button command = Control<Button>(viewer, next > index ? "GalleryNext" : "GalleryPrevious");
                    Require(command.IsEnabled && command.IsEffectivelyVisible, "The target navigation control was not available to the user.");
                    Click(command);
                }
                finally { viewer.MoveRequested -= OnMove; }
                if (accepted == 0) throw new InvalidOperationException("Enabled gallery navigation did not accept input.");
                await Program.WaitUntilAsync(() => viewer.CaptureState().Key == target.Key && displayed.Source is Bitmap
                    && !ReferenceEquals(previous, displayed.Source));
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                long ready = Stopwatch.GetTimestamp();
                bool correct = viewer.CaptureState().Key == target.Key && viewer.CaptureState().PostId == target.Post?.PostId;
                if (!correct) throw new InvalidOperationException("Gallery target identity changed before render readiness.");
                index = next;
                PerformanceDiagnostics.Record("gallery.navigation", "render-ready", request, row.WatchId.ToString(), target.Asset.AssetId, epoch, value: ready - intent);
                // Includes the same cheap projection read the real shell uses while the gallery covers its workspace.
                _ = session.ReadPausedOnUi();
                return new(request, target.Asset.AssetId, intent, accepted, ready, Stopwatch.GetElapsedTime(intent, ready).TotalMilliseconds, correct);
            }

            if (scenario == "held")
            {
                await measurement.MeasureAsync("control-cold", TimeSpan.FromMilliseconds(1000), Interact);
                app.Gate.Arm(TimeSpan.FromMilliseconds(ShellLoadFixture.HoldMilliseconds));
                bool allowed = false;
                ShellPhase held = await measurement.MeasureAsync("held", TimeSpan.FromMilliseconds(ShellLoadFixture.HoldMilliseconds + 1800), () =>
                {
                    if (!allowed) { allowed = true; app.Transport.AllowMedia(); }
                    return Interact();
                });
                long begin = app.Gate.HeldStarted, end = app.Gate.HeldReleased;
                Require(begin > 0 && end > begin, "No bounded held commit was observed.");
                // Event ordering, not just a generous timeout: both a UI answer and real target readiness occur INSIDE the hold.
                Require(measurement.PingSamples.Count(ping => ping.ExecutedAt > begin && ping.ExecutedAt < end) >= 2,
                    "The UI did not answer while persistence was held.");
                // Answers just after gate entry can precede a blocking legacy UI read. Require
                // answers in the hold's interior as well, so that early activity cannot conceal it.
                long interiorBegin = begin + (end - begin) / 3;
                long interiorEnd = end - (end - begin) / 3;
                Require(measurement.PingSamples.Count(ping => ping.ExecutedAt > interiorBegin && ping.ExecutedAt < interiorEnd) >= 2,
                    "The UI did not keep answering in the middle of the held persistence interval.");
                Require(held.Navigation.Any(nav => nav.AcceptedAt > begin && nav.ReadyAt < end),
                    "No gallery navigation finished while persistence was held.");
                Require(held.MaxUiGapMilliseconds < 1000, "UI gap exceeded the provisional 1-second diagnostic trigger.");
                await measurement.MeasureAsync("control-warm", TimeSpan.FromMilliseconds(1000), Interact);
            }
            else
            {
                app.Transport.AllowMedia();
                foreach (string warmth in new[] { "cold", "warm" })
                {
                    // FourChanThread preserves a 10-second minimum between actual checks, including
                    // manually requested checks. Each metadata phase must span that service floor.
                    TimeSpan duration = scenario == "metadata" ? TimeSpan.FromSeconds(12) : TimeSpan.FromMilliseconds(1600);
                    int metadataBefore = app.Transport.MetadataRequests;
                    if (scenario == "metadata")
                        PerformanceDiagnostics.Record("harness.metadata-window", warmth + ".before", value: metadataBefore);
                    ShellPhase phase = await measurement.MeasureAsync(scenario + "-" + warmth, duration, Interact);
                    if (scenario == "metadata")
                    {
                        int metadataAfter = app.Transport.MetadataRequests;
                        PerformanceDiagnostics.Record("harness.metadata-window", warmth + ".after", value: metadataAfter);
                        Require(metadataAfter > metadataBefore && app.Transport.MediaRequests == 0,
                            $"Metadata window {phase.Name} did not perform a new watch check ({metadataBefore}->{metadataAfter}), or unexpectedly downloaded media ({app.Transport.MediaRequests}).");
                    }
                    if (scenario == "downloads") Require(phase.TransferBytes > 0 && phase.CommitCount > 0,
                        "Navigation window did not overlap actual transfer AND durable progress: " + phase.Name);
                    if (scenario == "paused") Require(session.ReadPausedOnUi() && phase.TransferBytes == 0 && phase.CommitCount == 0,
                        "Paused control unexpectedly transferred or committed media.");
                }
            }
            viewer = await VerifyPixelsAsync(window, viewer, measurement, "after");
            if (scenario is "held" or "downloads")
            {
                app.Gate.Release();
                // Keep heartbeat alive while the remainder drains. Every successful download is checked against original fixture bytes.
                Stopwatch drain = Stopwatch.StartNew();
                while (true)
                {
                    DesktopSessionView view = await session.ReadViewAsync();
                    if (app.Gate.Successes == ShellLoadFixture.MediaCount && view.Threads.Single().Snapshot.Downloads.Active == 0) break;
                    if (drain.Elapsed > Timeout) throw new TimeoutException("The finite offline download set did not finish draining.");
                    await Task.Delay(50);
                }
                string root = Config.LoadConfig(Config.GetConfigDirectory()).DownloadPath;
                for (int n = 0; n < ShellLoadFixture.MediaCount; n++)
                {
                    string path = StorageLayout.GetMediaPath(root, "4chan", "g", "123", (777 + n).ToString(), ".png");
                    Require((await File.ReadAllBytesAsync(path)).SequenceEqual(ShellLoadFixture.Payloads[n]), "Published download differs from fixture: " + n);
                }
                Require(app.Gate.CommittedBytes == ShellLoadFixture.Payloads.Sum(bytes => (long)bytes.Length), "Durable byte accounting mismatch.");
            }
            Require(!session.IsStale && (await session.ReadViewAsync()).State == DesktopSessionState.Ready,
                "The load left the session projection stale or not ready.");
            Console.WriteLine($"SMOKE shell-load: {scenario}; phases={measurement.Phases.Count}; commits={app.Gate.Successes}; bytes={app.Transport.Bytes}; identity raster checks={measurement.PixelChecks.Count}.");
        }
        catch (Exception error) { measurement.Failure = error.ToString(); throw; }
        finally
        {
            app.Gate.Release(); app.Transport.AllowMedia();
            try
            {
                if (galleryOpen && window.FindControl<ContentControl>("GalleryHost")!.Content is GalleryViewer viewer)
                    Click(Control<Button>(viewer, "GalleryClose"));
                if (window.IsVisible) await CloseAsync(window);
            }
            finally { app.DisposeFixture(); }
        }
    }

    private static async Task<GalleryViewer> VerifyPixelsAsync(MainWindow window, GalleryViewer viewer, ShellLoadMeasurement measurement, string stage)
    {
        // Initial control checks only the initial asset; final control sweeps every frozen target. This is a separate correctness control, not part of timings.
        for (int index = 0; index < (stage == "before" ? 1 : ShellLoadFixture.Entries.Length); index++)
        {
            GalleryEntry entry = ShellLoadFixture.Entries[index];
            // Open the same real shell navigation surface at the requested frozen identity.
            if (stage != "before") await window.OpenMediaGalleryAsync(ShellLoadFixture.Entries, entry.EntryKey);
            viewer = (GalleryViewer)window.FindControl<ContentControl>("GalleryHost")!.Content!;
            await Program.WaitUntilAsync(() => viewer.CaptureState().Key == entry.Key && Control<Image>(viewer, "GalleryImage").Source is Bitmap);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            Image displayed = Control<Image>(viewer, "GalleryImage");
            Require(displayed.Bounds.Width > 0 && displayed.Bounds.Height > 0, "Rendered target has empty geometry.");
            // Render the viewer and inspect its visible viewport center, independently of Source/reference identity.
            double scale = window.RenderScaling;
            using RenderTargetBitmap raster = new(new PixelSize((int)Math.Ceiling(viewer.Bounds.Width * scale),
                (int)Math.Ceiling(viewer.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
            raster.Render(viewer);
            using MemoryStream encoded = new(); raster.Save(encoded, PngBitmapEncoderOptions.Default);
            using SKBitmap bitmap = SKBitmap.Decode(encoded.ToArray());
            Grid viewport = Control<Grid>(viewer, "GalleryViewport");
            Point center = viewport.TranslatePoint(new Point(viewport.Bounds.Width / 2, viewport.Bounds.Height / 2), viewer)
                ?? throw new InvalidOperationException("Gallery viewport is detached from its render root.");
            SKColor actual = bitmap.GetPixel((int)(center.X * scale), (int)(center.Y * scale)), expected = ShellLoadFixture.Colors[index];
            bool matches = Math.Abs(actual.Red - expected.Red) <= 3 && Math.Abs(actual.Green - expected.Green) <= 3
                && Math.Abs(actual.Blue - expected.Blue) <= 3 && actual.Alpha > 250;
            measurement.PixelChecks.Add(new { stage, asset = entry.Asset.AssetId, index, expected = expected.ToString(), actual = actual.ToString(), matches });
            Require(matches, "Rendered gallery pixels do not identify target " + index);
        }
        return viewer;
    }

    private static T Control<T>(GalleryViewer viewer, string name) where T : Avalonia.Controls.Control =>
        viewer.GetGalleryVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task CloseAsync(MainWindow window)
    {
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnClosed(object? sender, EventArgs args) => closed.TrySetResult();
        window.Closed += OnClosed;
        try
        {
            Func<WindowCloseReason, bool> close = window.PlatformImpl?.Closing ?? throw new InvalidOperationException("Native close unavailable.");
            close(WindowCloseReason.WindowClosing);
            await closed.Task.WaitAsync(Timeout);
        }
        finally { window.Closed -= OnClosed; }
    }
}

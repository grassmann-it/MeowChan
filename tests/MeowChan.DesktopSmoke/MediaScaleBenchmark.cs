using MeowChan.Desktop.Commands;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Diagnostics;
using MeowChan.Gallery;
using MeowChan.Tests.Shared;
using Ursa.Controls;

namespace MeowChan.DesktopSmoke;

// Branch dispatch: MEOWCHAN_MEDIA_SCALE_BENCHMARK=1, tier MEOWCHAN_MEDIA_SCALE=S|M|L, optional report directory
// MEOWCHAN_MEDIA_SCALE_REPORT. A measurement, not a pass/fail check (PERFORMANCE-GROSSE-SAMMLUNG-2026-11-13, 4.2):
// the real MainWindow, host and pages on a synthetic large profile (MediaScaleFixture), managed input only, private
// Xvfb only. Every step records wall time, the synchronous UI-thread part where the step is one call, the UI probe's
// largest gap and P95 lag inside the step, RSS and the peak RSS of the step (VmHWM after clear_refs), Gen2 collections,
// allocations and the PerformanceDiagnostics phases that ended inside it. Only functional preconditions throw.
internal static class MediaScaleBenchmark
{
    private static int AvailableTags(MediaTagDialog dialog) => dialog.TagList.Rows.Count(row => !row.IsHeader);

    private static int AssignedTags(MediaTagDialog dialog) =>
        dialog.TagList.Rows.Count(row => !row.IsHeader && row.State == TagToggleState.All);

    private static readonly TimeSpan Long = TimeSpan.FromMinutes(25);
    private static readonly TimeSpan Short = TimeSpan.FromMinutes(5);
    private static MediaScaleTier _tier = MediaScaleTier.S;
    private static MediaScaleFixtureSummary? _fixture;
    private static long _seedFinishedAt;
    private static double _seedMilliseconds;

    internal static void Configure(Config config)
    {
        config.WorkspaceMode = "Simple";
        config.DownloadsPaused = true;
        config.SaveUrlsOnExit = false;
    }

    internal static async Task SeedAsync(string profile)
    {
        _tier = MediaScaleTier.FromEnvironment();
        Stopwatch watch = Stopwatch.StartNew();
        _fixture = await MediaScaleFixture.WriteAsync(profile, _tier, line => Console.WriteLine("SMOKE " + line));
        _seedMilliseconds = watch.Elapsed.TotalMilliseconds;
        _seedFinishedAt = Stopwatch.GetTimestamp();
    }

    internal static async Task RunAsync(MainWindow window)
    {
        using Recorder recorder = new();
        recorder.Note("peakRssBeforeBenchmarkMiB", Proc("VmHWM") / 1024.0);
        DesktopSessionHost session = window.SessionHost;

        // 1. Start to usable window. The window was shown by the harness; the session starts now.
        await recorder.StepAsync("1 start", "window visible after seed", async () =>
        {
            await Task.CompletedTask;
            return (null, new { msSinceSeedEnd = Stopwatch.GetElapsedTime(_seedFinishedAt).TotalMilliseconds,
                processUptimeMs = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds });
        });
        await recorder.StepAsync("1 start", "session ready + usable", async () =>
        {
            Stopwatch watch = Stopwatch.StartNew();
            await session.StartAsync();
            if (session.State != DesktopSessionState.Ready)
                throw new InvalidOperationException("Session did not reach Ready: " + session.State + " " + session.StartupError);
            double ready = watch.Elapsed.TotalMilliseconds;
            await UntilAsync(() => window.FindControl<Button>("AddUrlButton")!.IsEnabled, Short, "usable window");
            double usable = watch.Elapsed.TotalMilliseconds;
            await UntilAsync(() => session.GetPublishedPrivateTagSnapshot() is { } tags
                && (tags.State == PrivateTagLoadState.Ready || tags.Error != "Private tags are loading."), Long, "private tags");
            if (session.GetPublishedPrivateTagSnapshot() is not { State: PrivateTagLoadState.Ready } loaded)
                throw new InvalidOperationException("The generated private tag file was refused: " + session.GetPublishedPrivateTagSnapshot().Error);
            return (null, new { sessionReadyMs = ready, usableMs = usable, privateTagsReadyMs = watch.Elapsed.TotalMilliseconds,
                tags = loaded.Snapshot!.Tags.Count, msSinceSeedEnd = Stopwatch.GetElapsedTime(_seedFinishedAt).TotalMilliseconds });
        });

        // 2. Media page, cold: no index cache yet, so the first build reads every thread.
        Button mediaNavigation = window.FindControl<Button>("MediaNavigationButton")!;
        await UntilAsync(() => mediaNavigation.IsVisible && mediaNavigation.IsEffectivelyEnabled, Short, "media navigation");
        string cachePath = Path.Combine(session.ProfileDirectory, "cache", MediaLibraryIndexCache.FileName);
        MediaPage page = null!;
        await recorder.StepAsync("2 media cold", "navigate + build + save", async () =>
        {
            double sync = Sync(() => mediaNavigation.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            page = window.MediaPageForTests ?? throw new InvalidOperationException("No media page.");
            return (sync, await BuildDetailsAsync(page, cachePath, null));
        });

        // 3. Media page, warm: a new page (as after a restart) loads the cache synchronously, then rebuilds. Twice.
        Func<MediaLibraryUserData> userData = typeof(MainWindow).GetMethod("ReadMediaLibraryUserData", BindingFlags.NonPublic | BindingFlags.Instance)!
            .CreateDelegate<Func<MediaLibraryUserData>>(window);
        for (int round = 1; round <= 2; round++)
        {
            MediaPage warm = new(session.OpenArchiveReaderAsync, cachePath: cachePath) { ReadUserData = userData, ReadBooruCatalog = session.ReadBooruCatalog };
            Window host = new() { Width = 1280, Height = 900, Content = warm, Title = "Media scale warm page", ShowInTaskbar = false };
            try
            {
                host.Show(window);
                warm.IsAvailable = true;
                await SettleAsync();
                await recorder.StepAsync("3 media warm", "new page #" + round + ": cache load + rebuild + save", async () =>
                {
                    Task activation = Task.CompletedTask;
                    double sync = Sync(() => activation = warm.ActivateAsync());
                    int fromCache = warm.Snapshot?.Entries.Count ?? -1;
                    object details = await BuildDetailsAsync(warm, cachePath, activation);
                    return (sync, new { cachedEntriesShownSynchronously = fromCache, build = details });
                });
            }
            finally { warm.EndLifetime(); host.Close(); }
        }
        window.Activate();
        await SettleAsync();

        MediaLibraryTileRow[] Rows() => (MediaLibraryTileRow[])page.Grid.ItemsSource!;
        object Shown() => new { tiles = page.Tiles.Count, entries = page.ShownEntries.Count };

        // 4. Filter changes: each one runs Present() synchronously.
        CheckBox missing = Find<CheckBox>(page, "MediaShowMissing");
        ComboBox kind = Find<ComboBox>(page, "MediaKindFilter");
        string[] addable = page.AddableTagNames;
        await recorder.UiStepAsync("4 filter", "show missing on", () => missing.IsChecked = true, Shown);
        await recorder.UiStepAsync("4 filter", "show missing off", () => missing.IsChecked = false, Shown);
        await recorder.UiStepAsync("4 filter", "type = images", () => kind.SelectedIndex = 1, Shown);
        await recorder.UiStepAsync("4 filter", "type = any", () => kind.SelectedIndex = 0, Shown);
        await recorder.UiStepAsync("4 filter", "tag include (first of " + addable.Length + ")", () => page.AddTagFilterByName(addable[0], include: true), Shown);
        await recorder.UiStepAsync("4 filter", "reset", () => page.ResetFilters(), Shown);

        // 5. Search typing, one keystroke at a time, then clearing the field.
        TextBox name = Find<TextBox>(page, "MediaNameFilter");
        string query = MediaScaleFixture.NamePrefix + "00";
        for (int length = 1; length <= query.Length; length++)
        {
            string text = query[..length];
            await recorder.UiStepAsync("5 typing", "'" + text + "'", () => name.Text = text, Shown);
        }
        await recorder.UiStepAsync("5 typing", "clear", () => name.Text = string.Empty, Shown);

        // 6. Sort changes.
        ComboBox sort = Find<ComboBox>(page, "MediaSort");
        Button shuffle = Find<Button>(page, "MediaShuffle");
        await recorder.UiStepAsync("6 sort", "newest", () => sort.SelectedIndex = 1, Shown);
        await recorder.UiStepAsync("6 sort", "random", () => sort.SelectedIndex = 2, Shown);
        await recorder.UiStepAsync("6 sort", "shuffle", () => shuffle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), Shown);
        await recorder.UiStepAsync("6 sort", "by thread", () => sort.SelectedIndex = 0, Shown);

        // 7. The shared media menu on an archived and on a Danbooru tile (keyboard-style request on the grid).
        Registry registry = (Registry)typeof(DesktopSessionHost).GetField("_registry", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)!;
        int booruIndex = Array.FindIndex(Rows(), row => row.Entry.BooruPostId != null);
        foreach ((string label, int index) in new[] { ("archived tile", 0), ("Danbooru tile", booruIndex) })
        {
            if (index < 0) { recorder.Note("menu." + label, "no such tile"); continue; }
            await SelectAsync(page, index);
            BooruCatalogStoreCounters before = registry.BooruCatalog?.Counters ?? default;
            int reads = session.BooruCatalogReads;
            await recorder.StepAsync("7 menu", label, async () =>
            {
                Stopwatch watch = Stopwatch.StartNew();
                double sync = Sync(() => page.Grid.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent }));
                await UntilAsync(() => page.MediaMenu?.IsOpen == true, Short, "media menu");
                double open = watch.Elapsed.TotalMilliseconds;
                BooruCatalogStoreCounters after = registry.BooruCatalog?.Counters ?? default;
                return (sync, new { openMs = open, items = page.MediaMenu!.ItemCount, sessionBooruReads = session.BooruCatalogReads - reads,
                    storeFullReads = after.FullReads - before.FullReads, storeCachedReads = after.CachedReads - before.CachedReads });
            });
            page.MediaMenu?.Close();
            await UntilAsync(() => page.MediaMenu?.IsOpen != true, Short, "menu closed");
            await SettleAsync();
        }

        // 8. Tag dialog from the menu: open (one button per available tag), add one existing tag, close (page refresh).
        int bound = Array.FindIndex(Rows(), row => row.Entry.BooruPostId == null && row.State?.CanEditMediaTags == true);
        if (bound < 0) throw new InvalidOperationException("No taggable bound tile in the fixture.");
        await SelectAsync(page, bound);
        page.Grid.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
        await UntilAsync(() => page.MediaMenu?.IsOpen == true, Short, "menu for tags");
        MenuItem edit = page.MediaMenu!.Items.OfType<MenuItem>().Single(item => item.Header as string == CommandCatalog.Label("media.rating.edit"));
        await UntilAsync(() => edit.IsEffectivelyVisible && TopLevel.GetTopLevel(edit) != null, Short, "tag menu item");
        string tagFile = Path.Combine(session.ProfileDirectory, "private-tags.json");
        MediaTagDialog dialog = null!;
        await recorder.StepAsync("8 tags", "open dialog from menu", async () =>
        {
            Stopwatch watch = Stopwatch.StartNew();
            double sync = Sync(() => PointerClick(edit));
            // TP3: the menu entry opens the quick tag popover; the measured dialog is its "Advanced…".
            await UntilAsync(() => window.MediaPageTagPopover is { IsOpen: true, Surface: not null }, Long, "tag popover");
            window.MediaPageTagPopover!.Surface!.AdvancedButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => window.MediaPageTagWindow?.IsVisible == true, Long, "tag dialog");
            dialog = window.MediaPageTagWindow!;
            double visible = watch.Elapsed.TotalMilliseconds;
            await SettleAsync();
            return (sync, new { visibleMs = visible, settledMs = watch.Elapsed.TotalMilliseconds,
                listedTags = AvailableTags(dialog), assigned = AssignedTags(dialog), realizedRows = dialog.TagList.GetRealizedContainers().Count() });
        });
        await recorder.StepAsync("8 tags", "add one existing tag", async () =>
        {
            // The dialog lists every tag once as a toggle row; offering one means pressing Space on an unticked row.
            int assigned = AssignedTags(dialog);
            long revision = dialog.Session.Snapshot?.Revision ?? -1;
            TagToggleRow offered = dialog.TagList.Rows.First(row => !row.IsHeader && row.State == TagToggleState.None);
            string label = offered.Tag?.Name.Display ?? "?";
            dialog.TagList.Focus(offered);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Control target = dialog.TagList.ContainerFromItem(offered) ?? dialog.TagList;
            bool wasBusy = dialog.Session.Busy;
            Stopwatch watch = Stopwatch.StartNew();
            double sync = Sync(() => target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space }));
            // A refused write ends with a message instead of a new revision; both end the step.
            await UntilAsync(() => !dialog.Session.Busy && (dialog.Session.Snapshot?.Revision > revision || dialog.Session.MessageKey != null),
                Short, "tag saved (busy=" + wasBusy + ", button '" + label + "')");
            double saved = watch.Elapsed.TotalMilliseconds;
            bool written = dialog.Session.Snapshot?.Revision > revision;
            if (written) await UntilAsync(() => AssignedTags(dialog) > assigned, Long, "dialog refreshed");
            double refreshed = watch.Elapsed.TotalMilliseconds;
            await SettleAsync();
            return (sync, new { written, message = dialog.Session.MessageKey, tag = label, savedMs = saved, dialogRefreshedMs = refreshed,
                settledMs = watch.Elapsed.TotalMilliseconds, tagFileBytes = new FileInfo(tagFile).Length });
        });
        await recorder.StepAsync("8 tags", "close dialog + page refresh", async () =>
        {
            Stopwatch watch = Stopwatch.StartNew();
            double sync = Sync(() => dialog.Input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape }));
            await UntilAsync(() => window.MediaPageTagWindow == null, Long, "tag dialog closed");
            double closed = watch.Elapsed.TotalMilliseconds;
            await SettleAsync();
            return (sync, new { closedMs = closed, settledMs = watch.Elapsed.TotalMilliseconds });
        });

        // 9. Archive page: first page of the list, its media counts, then three page changes.
        Button archiveNavigation = window.FindControl<Button>("ArchiveNavigationButton")!;
        ArchivePage archive = window.ArchivePage;
        ListBox threads = archive.FindControl<ListBox>("ArchiveThreads")!;
        Pagination pagination = archive.FindControl<Pagination>("ArchiveResultsPagination")!;
        await recorder.StepAsync("9 archive", "open (list + media counts)", async () =>
        {
            Stopwatch watch = Stopwatch.StartNew();
            double sync = Sync(() => archiveNavigation.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            await UntilAsync(() => threads.ItemCount > 0 && !archive.IsBusy && !archive.IsSearchLoading, Long, "archive list");
            double listed = watch.Elapsed.TotalMilliseconds;
            await archive.ArchiveMediaCountTask.WaitAsync(Long);
            return (sync, new { listMs = listed, countsMs = watch.Elapsed.TotalMilliseconds, rows = threads.ItemCount });
        });
        for (int target = 2; target <= 4; target++)
        {
            int next = target;
            await recorder.StepAsync("9 archive", "page " + next, async () =>
            {
                object? before = threads.ItemsSource;
                Stopwatch watch = Stopwatch.StartNew();
                double sync = Sync(() => pagination.CurrentPage = next);
                await UntilAsync(() => !ReferenceEquals(threads.ItemsSource, before) && !archive.IsBusy && !archive.IsSearchLoading
                    && pagination.CurrentPage == next, Long, "archive page");
                double listed = watch.Elapsed.TotalMilliseconds;
                await archive.ArchiveMediaCountTask.WaitAsync(Long);
                return (sync, new { listMs = listed, countsMs = watch.Elapsed.TotalMilliseconds });
            });
        }

        // 3c. Back to the media page in the same process: the page keeps its index but rebuilds and saves again.
        await recorder.StepAsync("3 media warm", "revisit in process: rebuild + save", async () =>
        {
            double sync = Sync(() => mediaNavigation.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            return (sync, await BuildDetailsAsync(page, cachePath, null));
        });

        // 10. Gallery from the media page: first image, ten times next, five times previous; then "newest" first image.
        ContentControl galleryHost = window.FindControl<ContentControl>("GalleryHost")!;
        foreach ((string order, int sortIndex, int nextSteps, int previousSteps) in new[] { ("by thread", 0, 10, 5), ("newest", 1, 0, 0) })
        {
            if (sort.SelectedIndex != sortIndex) { sort.SelectedIndex = sortIndex; await SettleAsync(); }
            await SelectAsync(page, 0);
            GalleryViewer viewer = null!;
            Image image = null!;
            await recorder.StepAsync("10 gallery", order + ": open, first image", async () =>
            {
                Stopwatch watch = Stopwatch.StartNew();
                double sync = Sync(() => page.Grid.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter }));
                await UntilAsync(() => galleryHost.IsVisible && galleryHost.Content is GalleryViewer, Long, "gallery");
                viewer = (GalleryViewer)galleryHost.Content!;
                await UntilAsync(() => viewer.GetGalleryVisualDescendants().OfType<Image>().Any(control => control.Name == "GalleryImage"
                    && control.Source is Bitmap), Long, "first gallery image");
                image = viewer.GetGalleryVisualDescendants().OfType<Image>().Single(control => control.Name == "GalleryImage");
                return (sync, new { firstImageMs = watch.Elapsed.TotalMilliseconds });
            });
            foreach ((string button, int count) in new[] { ("GalleryNext", nextSteps), ("GalleryPrevious", previousSteps) })
                for (int step = 1; step <= count; step++)
                {
                    await recorder.StepAsync("10 gallery", order + ": " + button[7..].ToLowerInvariant() + " #" + step, async () =>
                    {
                        GalleryAssetKey? key = viewer.CaptureState().Key;
                        object? shown = image.Source;
                        Button move = viewer.GetGalleryVisualDescendants().OfType<Button>().Single(control => control.Name == button);
                        Stopwatch watch = Stopwatch.StartNew();
                        double sync = Sync(() => move.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
                        await UntilAsync(() => viewer.CaptureState().Key != key && image.Source is Bitmap current && !ReferenceEquals(current, shown),
                            Short, "gallery " + button);
                        return (sync, new { imageMs = watch.Elapsed.TotalMilliseconds });
                    });
                }
            for (int press = 0; press < 3 && galleryHost.IsVisible; press++)
            {
                viewer.Focus();
                viewer.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                await SettleAsync();
            }
            await UntilAsync(() => !galleryHost.IsVisible, Short, "gallery closed");
            await window.GalleryCleanupTask;
            await SettleAsync();
        }
        if (sort.SelectedIndex != 0) sort.SelectedIndex = 0;

        recorder.Note("peakRssWholeRunMiB", recorder.PeakMiB);
        recorder.Write(_tier, _fixture, _seedMilliseconds);
    }

    // Waits for the page's build (and its cache save) and reports what it did.
    private static async Task<object> BuildDetailsAsync(MediaPage page, string cachePath, Task? activation)
    {
        Stopwatch watch = Stopwatch.StartNew();
        MediaLibrarySnapshot? before = page.Snapshot;
        DateTime cacheBefore = File.Exists(cachePath) ? File.GetLastWriteTimeUtc(cachePath) : DateTime.MinValue;
        TextBlock status = Find<TextBlock>(page, "MediaStatus");
        int ticks = 0;
        void OnStatus(object? sender, AvaloniaPropertyChangedEventArgs e) { if (e.Property == TextBlock.TextProperty) ticks++; }
        status.PropertyChanged += OnStatus;
        try
        {
            await UntilAsync(() => !ReferenceEquals(page.Snapshot, before) && page.Snapshot != null, Long, "index built");
            double built = watch.Elapsed.TotalMilliseconds;
            await (activation ?? page.BuildTask).WaitAsync(Long);
            await page.BuildTask.WaitAsync(Long);
            double done = watch.Elapsed.TotalMilliseconds;
            MediaLibrarySnapshot snapshot = page.Snapshot!;
            if (!snapshot.Complete) throw new InvalidOperationException("Incomplete index: " + string.Join("; ", snapshot.Warnings.Take(5)));
            return new
            {
                indexBuiltMs = built, saveAndPresentMs = done - built, totalMs = done, statusUpdates = ticks,
                entries = snapshot.Entries.Count, threads = snapshot.Threads.Count, tiles = page.Tiles.Count,
                cacheBytes = File.Exists(cachePath) ? new FileInfo(cachePath).Length : -1,
                cacheRewritten = File.Exists(cachePath) && File.GetLastWriteTimeUtc(cachePath) != cacheBefore
            };
        }
        finally { status.PropertyChanged -= OnStatus; }
    }

    private static async Task SelectAsync(MediaPage page, int index)
    {
        page.Grid.SelectedIndex = index;
        page.Grid.ScrollIntoView(index);
        page.Grid.Focus();
        await SettleAsync();
    }

    private static void PointerClick(Control target)
    {
        TopLevel root = TopLevel.GetTopLevel(target)!;
        Point point = new(target.Bounds.Width / 2, target.Bounds.Height / 2);
        using Avalonia.Input.Pointer pointer = new(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, true);
        target.RaiseEvent(new PointerPressedEventArgs(target, pointer, root, point, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
        target.RaiseEvent(new PointerReleasedEventArgs(target, pointer, root, point, 1,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
        pointer.Capture(null);
    }

    private static double Sync(Action action)
    {
        long start = Stopwatch.GetTimestamp();
        action();
        return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    // Two background turns: pending layout and render of the previous change have run.
    private static async Task SettleAsync()
    {
        for (int turn = 0; turn < 2; turn++) await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static async Task UntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > timeout) throw new TimeoutException("media-scale benchmark: " + what + " not reached within " + timeout);
            await Task.Delay(5);
        }
    }

    private static T Find<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? root.GetLogicalDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException("Missing control " + name);

    // kB values of /proc/self/status (Linux only; -1 elsewhere).
    private static long Proc(string field)
    {
        try
        {
            foreach (string line in File.ReadLines("/proc/self/status"))
                if (line.StartsWith(field + ":", StringComparison.Ordinal))
                    return long.Parse(line[(field.Length + 1)..].Trim().Split(' ')[0], CultureInfo.InvariantCulture);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException) { }
        return -1;
    }

    private static bool ResetPeak()
    {
        try { File.WriteAllText("/proc/self/clear_refs", "5"); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string? LoadAverage()
    {
        try { return File.ReadAllText("/proc/loadavg").Trim(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    internal sealed record DiagnosticSummary(int Count, double TotalMs, double MaxMs);
    internal sealed record StepResult(string Phase, string Step, double WallMs, double? SyncMs, double UiMaxGapMs, double UiP95LagMs,
        double UiMaxLagMs, int Pings, double RssMiB, double PeakRssMiB, int Gen2, double AllocatedMiB,
        IReadOnlyDictionary<string, DiagnosticSummary> Diagnostics, object? Details);

    private sealed class Recorder : IDisposable
    {
        private readonly object _sync = new();
        private readonly List<UiPingSample> _pings = new();
        private readonly UiResponsivenessProbe _probe;
        private readonly PerformanceDiagnostics.DiagnosticRun _run;
        private readonly List<StepResult> _steps = new();
        private readonly Dictionary<string, object?> _notes = new(StringComparer.Ordinal);
        private readonly string? _loadAtStart = LoadAverage();
        private readonly bool _peakResettable;
        private double _peakMiB;

        internal Recorder()
        {
            _run = PerformanceDiagnostics.BeginRun(Guid.NewGuid().ToString("N"),
                Environment.GetEnvironmentVariable("MEOWCHAN_PERF_SOURCE_HASH") ?? "assembly-mvid:" + typeof(App).Module.ModuleVersionId,
                "media-scale-" + MediaScaleTier.FromEnvironment().Name, capacity: 262144);
            _probe = new(action => Dispatcher.UIThread.Post(action), TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(30),
                report => Console.WriteLine("SMOKE media-scale: UI unanswered for " + report.UnansweredMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms"),
                onAnswer: sample => { lock (_sync) _pings.Add(sample); });
            _peakResettable = ResetPeak();
            _probe.Start();
        }

        internal double PeakMiB => _peakMiB;

        internal void Note(string key, object? value) => _notes[key] = value;

        // One synchronous UI change, then the time until layout/render settled.
        internal Task UiStepAsync(string phase, string step, Action change, Func<object> details) => StepAsync(phase, step, async () =>
        {
            Stopwatch watch = Stopwatch.StartNew();
            double sync = Sync(change);
            await SettleAsync();
            return (sync, new { settledMs = watch.Elapsed.TotalMilliseconds, shown = details() });
        });

        internal async Task StepAsync(string phase, string step, Func<Task<(double? Sync, object? Details)>> body)
        {
            // Let the previous step's posted work drain so it is not charged to this one.
            await SettleAsync();
            ResetPeak();
            int gen2 = GC.CollectionCount(2);
            long allocated = GC.GetTotalAllocatedBytes(precise: false);
            long start = Stopwatch.GetTimestamp();
            (double? sync, object? details) = await body();
            long end = Stopwatch.GetTimestamp();
            UiPingSample[] pings;
            lock (_sync) pings = _pings.Where(ping => ping.ExecutedAt >= start && ping.ExecutedAt <= end).ToArray();
            long previous = start, gap = 0;
            foreach (UiPingSample ping in pings) { gap = Math.Max(gap, ping.ExecutedAt - previous); previous = ping.ExecutedAt; }
            gap = Math.Max(gap, end - previous);
            double[] lags = pings.Select(ping => ping.LagMilliseconds).Order().ToArray();
            double peak = Proc("VmHWM") / 1024.0;
            _peakMiB = Math.Max(_peakMiB, peak);
            Dictionary<string, DiagnosticSummary> diagnostics = _run.Snapshot().Events
                .Where(item => item.Outcome == "end" && item.Timestamp >= start && item.Timestamp <= end)
                .GroupBy(item => item.Phase, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => new DiagnosticSummary(group.Count(),
                    group.Sum(item => item.Value) * 1000.0 / Stopwatch.Frequency, group.Max(item => item.Value) * 1000.0 / Stopwatch.Frequency),
                    StringComparer.Ordinal);
            StepResult result = new(phase, step, Stopwatch.GetElapsedTime(start, end).TotalMilliseconds, sync,
                gap * 1000.0 / Stopwatch.Frequency, lags.Length == 0 ? 0 : lags[Math.Max(0, (int)Math.Ceiling(lags.Length * .95) - 1)],
                lags.Length == 0 ? 0 : lags[^1], pings.Length, Proc("VmRSS") / 1024.0, peak, GC.CollectionCount(2) - gen2,
                (GC.GetTotalAllocatedBytes(precise: false) - allocated) / 1048576.0, diagnostics, details);
            _steps.Add(result);
            Console.WriteLine("SMOKE media-scale step " + JsonSerializer.Serialize(result));
        }

        internal void Write(MediaScaleTier tier, MediaScaleFixtureSummary? fixture, double seedMilliseconds)
        {
            object report = new
            {
                benchmark = "media-scale", tier = tier.Name, at = DateTimeOffset.UtcNow, loadAverageAtStart = _loadAtStart, loadAverageAtEnd = LoadAverage(),
                processors = Environment.ProcessorCount, runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
                configuration = typeof(App).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration,
                serverGc = System.Runtime.GCSettings.IsServerGC, renderScaling = (Application.Current?.ApplicationLifetime as
                    Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow?.RenderScaling,
                peakResettable = _peakResettable, seedMs = seedMilliseconds, fixture, notes = _notes, probe = _probe.Summarize(),
                diagnosticsDropped = _run.Snapshot().Dropped, steps = _steps
            };
            string json = JsonSerializer.Serialize(report);
            Console.WriteLine("MEDIA_SCALE_BENCHMARK " + json);
            string markdown = Markdown(tier, fixture, seedMilliseconds);
            Console.WriteLine(markdown);
            if (Environment.GetEnvironmentVariable("MEOWCHAN_MEDIA_SCALE_REPORT") is { Length: > 0 } target)
            {
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "media-scale-" + tier.Name + ".json"),
                    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
                File.WriteAllText(Path.Combine(target, "media-scale-" + tier.Name + ".md"), markdown);
            }
        }

        private string Markdown(MediaScaleTier tier, MediaScaleFixtureSummary? fixture, double seedMilliseconds)
        {
            static string F(double? value) => value is { } number ? number.ToString("0", CultureInfo.InvariantCulture) : "–";
            StringBuilder text = new();
            text.AppendLine($"## media-scale {tier.Name}: {tier.Threads} threads, {tier.Media} media, {tier.Tags} private tags (audit: {tier.RequestedTags}; store limit {MeowChan.Tags.PrivateTagStore.MaximumTags}), {tier.BooruPosts} Danbooru posts");
            text.AppendLine();
            text.AppendLine($"Load average start/end: {_loadAtStart} / {LoadAverage()}; {Environment.ProcessorCount} CPUs; seed {F(seedMilliseconds)} ms; "
                + $"private-tags.json {(fixture?.PrivateTagFileBytes ?? 0) / 1048576.0:0.0} MiB; booru-catalog.json {(fixture?.BooruCatalogBytes ?? 0) / 1048576.0:0.0} MiB.");
            text.AppendLine();
            text.AppendLine("| Phase | Step | Wall ms | Sync UI ms | UI max gap ms | UI p95 lag ms | RSS MiB | Peak MiB | Gen2 | Alloc MiB | Details |");
            text.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---|");
            foreach (StepResult step in _steps)
                text.AppendLine($"| {step.Phase} | {step.Step} | {F(step.WallMs)} | {F(step.SyncMs)} | {F(step.UiMaxGapMs)} | {F(step.UiP95LagMs)} | "
                    + $"{F(step.RssMiB)} | {F(step.PeakRssMiB)} | {step.Gen2} | {F(step.AllocatedMiB)} | {JsonSerializer.Serialize(step.Details).Replace("|", "/")} |");
            text.AppendLine();
            text.AppendLine($"Peak RSS whole run: {F(_peakMiB)} MiB (before the benchmark, incl. seed: {_notes.GetValueOrDefault("peakRssBeforeBenchmarkMiB")}).");
            return text.ToString();
        }

        public void Dispose()
        {
            _probe.Dispose();
            _run.Dispose();
        }
    }
}

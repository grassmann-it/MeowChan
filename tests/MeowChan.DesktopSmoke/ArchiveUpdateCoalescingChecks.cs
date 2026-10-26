using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

/// <summary>PF03 integration checks. Root calls RunAsync from the serialized download-session smoke.</summary>
internal static class ArchiveUpdateCoalescingChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        string root = TemporaryWorkspacePaths.CreateDirectory("archive-update-coalescing-");
        ArchivePage? counts = null, readerPage = null;
        Window? countsHost = null, readerHost = null;
        TaskCompletionSource? inventoryRelease = null, summaryRelease = null;
        try
        {
            string profile = Path.Combine(root, "profile"), media = Path.Combine(root, "media");
            Directory.CreateDirectory(media);
            using ProfileLease lease = ProfileLease.Acquire(profile);
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
            ResourceId id = new("fixture", "board", "coalescing");
            const string url = "https://coalescing.invalid/board/thread/1";
            await catalog.RegisterThreadAsync(new(id, url, "Coalescing fixture", media, true));
            CapturedThreadSnapshot Snapshot(int count) => new(id, url, "Coalescing fixture", ThreadSourceState.Active,
                DateTimeOffset.UtcNow, media, Enumerable.Range(1, count).Select(post => new CapturedPost(post.ToString(),
                    post, PostFieldPresence.All, Html: "<p>Offline post " + post + "</p>")).ToArray());
            await catalog.CaptureAsync(Snapshot(2));
            ThreadWorkSnapshot[] work = [];
            void Work(int revision) => work = [new(url, WorkIntent.Watching, ThreadSourceStateValue.Active, null, false,
                [new MediaWork { ResourceUrl = url, Generation = 1, MediaIdentifier = "asset", DestinationPath = media,
                    DownloadUrl = url + "/asset", Status = MediaWorkStatus.Succeeded, Attempts = revision }])];
            counts = new(token => ArchiveReader.OpenAsync(profile, token), (_, _) => Task.CompletedTask, _ => { })
                { IsAvailable = true, GetWorkSnapshots = () => work, ArchiveLayoutPreference = "Table" };
            counts.ReadArchiveMediaInventoryAsync = (_, _) => Task.FromResult(new ArchiveThreadMediaCount(0));
            countsHost = new() { Width = 1200, Height = 760, Content = counts };
            countsHost.Show(owner);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(await counts.ApplySearchSpecAsync(new()), "Initial count query failed.");
            await counts.ArchiveMediaCountTask;
            DataGrid table = counts.FindControl<DataGrid>("ArchiveThreadTable")!;
            ArchiveThreadRow row = table.ItemsSource.Cast<ArchiveThreadRow>().Single();
            table.SelectedItem = row;
            TextBox search = counts.FindControl<TextBox>("ArchiveSearchText")!;
            search.Focus();
            var focused = countsHost.FocusManager?.GetFocusedElement();
            bool appliedStale = false;
            row.PropertyChanged += (_, _) => appliedStale |= row.SavedMediaCount == 999;
            TaskCompletionSource entered = Signal(); inventoryRelease = Signal();
            int inventories = 0;
            counts.ReadArchiveMediaInventoryAsync = async (_, _) =>
            {
                if (++inventories == 1) { entered.SetResult(); await inventoryRelease.Task; return new(999); }
                return new(42);
            };
            long starts = counts.ArchiveMediaCountScanStarts, cancellations = counts.ArchiveMediaCountScanCancellations;
            Work(1); counts.NotifyArchiveMediaCountsChanged(); await entered.Task;
            Task drain = counts.ArchiveMediaCountTask;
            for (int version = 2; version <= 300; version++) { Work(version); counts.NotifyArchiveMediaCountsChanged(); }
            Require(ReferenceEquals(drain, counts.ArchiveMediaCountTask), "Invalidations allocated another scan drain.");
            Require(counts.ArchiveMediaCountScanStarts == starts + 1 && counts.ArchiveMediaCountScanCancellations == cancellations,
                "Visible invalidations cancelled/restarted active inventory.");
            inventoryRelease.SetResult(); await counts.ArchiveMediaCountTask;
            Require(inventories == 2 && counts.ArchiveMediaCountScanStarts == starts + 2 && row.SavedMediaCount == 42 && !appliedStale,
                "The latest burst did not produce exactly one authoritative follow-up.");
            Require(ReferenceEquals(row, table.ItemsSource.Cast<ArchiveThreadRow>().Single())
                && table.SelectedItems.Contains(row) && ReferenceEquals(focused, countsHost.FocusManager?.GetFocusedElement()),
                "Background inventory replaced a row, selection or focus.");

            entered = Signal(); inventoryRelease = Signal(); inventories = 0;
            counts.ReadArchiveMediaInventoryAsync = async (_, _) =>
            {
                if (++inventories == 1) { entered.SetResult(); await inventoryRelease.Task; throw new IOException("late-covered-failure"); }
                return new(77);
            };
            starts = counts.ArchiveMediaCountScanStarts;
            Work(301); counts.NotifyArchiveMediaCountsChanged(); await entered.Task;
            counts.SetObscuredByGallery(true);
            for (int version = 302; version <= 350; version++)
            { counts.SetObscuredByGallery(true); Work(version); counts.NotifyArchiveMediaCountsChanged(); }
            Require(counts.ArchiveMediaCountScanCancellations == cancellations + 1, "Coverage cancelled active inventory more than once.");
            counts.SetObscuredByGallery(false); counts.SetObscuredByGallery(false);
            Require(counts.ArchiveMediaCountScanStarts == starts + 1, "Uncover started inventory before non-cooperative I/O returned.");
            inventoryRelease.SetResult(); await counts.ArchiveMediaCountTask;
            Require(inventories == 2 && counts.ArchiveMediaCountScanStarts == starts + 2 && row.SavedMediaCount == 77
                && !row.MediaStatus.Contains("late-covered-failure", StringComparison.Ordinal),
                "Uncover failed to catch up once or published a cancelled scan's late error.");
            counts.EndLifetime(); await counts.ArchiveMediaCountTask; countsHost.Close(); countsHost = null;

            bool holdSummary = false;
            entered = Signal(); summaryRelease = Signal();
            readerPage = new(async token =>
            {
                if (holdSummary) { holdSummary = false; entered.SetResult(); await summaryRelease.Task; }
                return await ArchiveReader.OpenAsync(profile, token);
            }, (_, _) => Task.CompletedTask, _ => { }) { IsAvailable = true };
            Control pane = readerPage.DetachReader();
            readerHost = new() { Width = 1100, Height = 800, Content = pane };
            readerHost.Show(owner);
            // This fixture owns its clock: avoid the unrelated two-second polling timer.
            (typeof(ArchivePage).GetField("_readerUpdateTimer", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(readerPage) as DispatcherTimer)!.Stop();
            await readerPage.LoadThreadAsync(id);
            await catalog.CaptureAsync(Snapshot(3));
            long checks = readerPage.ReaderSummaryChecks;
            holdSummary = true;
            Task summary = readerPage.CheckReaderUpdatesAsync(); await entered.Task;
            readerPage.SetObscuredByGallery(true);
            for (int i = 0; i < 20; i++) await readerPage.CheckReaderUpdatesAsync();
            Require(readerPage.ReaderSummaryChecks == checks + 1, "A covered reader opened another summary query.");
            readerPage.SetObscuredByGallery(false); readerPage.SetObscuredByGallery(false);
            summaryRelease.SetResult(); await summary;
            Require(readerPage.ReaderSummaryChecks == checks + 2 && readerPage.ReaderSummaryStaleResults == 1,
                "Cover/uncover did not discard the old result and run one latest summary catch-up.");
            Require(readerPage.CurrentThread?.PostCount == 2 && readerPage.FindControl<Button>("ReaderShowNewPosts")!.IsVisible,
                "A passive catch-up replaced the frozen reader or lost the new-post notice.");
            Console.WriteLine("SMOKE archive update coalescing: PASS; visible 300-change burst -> 2 scans/0 cancellations; coverage cancels once; non-cooperative I/O retains slot; latest catch-up; late error rejected; row/selection/focus retained; reader coverage epoch and one catch-up. Offline fixture, no native input claim.");
        }
        finally
        {
            inventoryRelease?.TrySetResult(); summaryRelease?.TrySetResult();
            counts?.EndLifetime(); readerPage?.EndLifetime();
            if (counts != null) await counts.ArchiveMediaCountTask;
            // EndLifetime cancels the reader lifetime; wait for any read continuation before deleting its profile.
            if (readerPage != null) await Program.WaitUntilAsync(() =>
                !(bool)typeof(ArchivePage).GetField("_checkingReaderUpdate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(readerPage)!);
            countsHost?.Close(); readerHost?.Close();
            Directory.Delete(root, recursive: true);
        }
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

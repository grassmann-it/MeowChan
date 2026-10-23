using Avalonia.Controls;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

internal static class ArchiveSearchStateChecks
{
    internal static async Task RunAsync()
    {
        string directory = Path.Combine(TemporaryWorkspacePaths.TempRoot, "archive-search-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        ArchivePage? page = null;
        try
        {
            using ProfileLease lease = ProfileLease.Acquire(directory);
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory, lease);
            List<ThreadWorkSnapshot> work = new();
            List<ResourceId> ids = new();
            for (int i = 0; i < 120; ++i)
            {
                ResourceId id = new(i % 2 == 0 ? "A" : "B", i % 2 == 0 ? "x" : "y", i.ToString());
                ids.Add(id);
                string url = "https://fixture.test/" + i;
                await catalog.RegisterThreadAsync(new ArchiveThreadRegistration(id, url, i.ToString("D3"), directory, true));
                work.Add(new ThreadWorkSnapshot(url, i % 2 == 0 ? WorkIntent.Completed : WorkIntent.Suppressed,
                    ThreadSourceStateValue.Active, null, false, []));
            }
            bool fail = true;
            TaskCompletionSource? gate = null;
            int opens = 0;
            page = new ArchivePage(async token =>
            {
                ++opens;
                if (gate != null) await gate.Task; // deliberately ignores cancellation: publication must guard it
                if (fail) throw new IOException("Injected catalog failure");
                return await ArchiveReader.OpenAsync(directory);
            }, (_, _) => Task.CompletedTask, _ => { }) { IsAvailable = true, GetWorkSnapshots = () => work.ToArray() };
            ArchiveSearchSpec all = new(sources: ["A", "B"], boards: [new("A", "x"), new("B", "y")], sort: ArchiveThreadSort.Title);
            Require(!await page.ApplySearchSpecAsync(all), "First failed search unexpectedly succeeded.");
            Require(page.AppliedSearchSpec == null && !page.FindControl<StackPanel>("ArchiveEmptyState")!.IsVisible,
                "First failure was published as a successful empty result.");
            fail = false;
            Require(await page.ApplySearchSpecAsync(all), "Initial fixture search failed.");
            Require(page.FindControl<ListBox>("ArchiveThreads")!.ItemCount == 50, "Archive page size changed.");
            object applied = page.AppliedSearchSpec!;
            page.FindControl<TextBox>("ArchiveSearchText")!.Text = "unapplied text";
            page.FindControl<ComboBox>("ArchiveSort")!.SelectedIndex = 1;
            page.FindControl<ArchiveDateEditor>("ArchiveStartDate")!.RawText = "not-a-date";
            Require(await page.NavigateResultsPage(3), "Direct page 3 failed.");
            Require(ReferenceEquals(applied, page.AppliedSearchSpec) && page.FindControl<ListBox>("ArchiveThreads")!.ItemCount == 20,
                "Page navigation applied a draft or returned the wrong final page.");
            string oldPage = page.FindControl<TextBlock>("ArchiveThreadPage")!.Text!;
            object oldRows = page.FindControl<ListBox>("ArchiveThreads")!.ItemsSource!;
            fail = true;
            Require(!await page.NavigateResultsPage(1), "Injected paging failure unexpectedly succeeded.");
            Require(ReferenceEquals(oldRows, page.FindControl<ListBox>("ArchiveThreads")!.ItemsSource)
                && page.FindControl<TextBlock>("ArchiveThreadPage")!.Text == oldPage && ReferenceEquals(applied, page.AppliedSearchSpec),
                "Failure changed the successful page snapshot.");
            fail = false;
            Require(await page.RetrySearchAsync(), "Frozen page retry failed.");
            Require(await page.RemoveAppliedFilter("source", "A"), "Applied source removal failed.");
            Require(page.AppliedSearchSpec!.Sources.SequenceEqual(["B"]) && page.AppliedSearchSpec.Sort == ArchiveThreadSort.Title,
                "Chip removal adopted foreign drafts or changed sort.");
            Require(page.FindControl<TextBox>("ArchiveSearchText")!.Text == "unapplied text"
                && page.FindControl<ComboBox>("ArchiveSort")!.SelectedIndex == 1
                && page.FindControl<ArchiveDateEditor>("ArchiveStartDate")!.RawText == "not-a-date",
                "Chip removal discarded an unrelated raw draft.");
            object lastApplied = page.AppliedSearchSpec;
            gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenSource canceled = new();
            Task<bool> late = page.ApplySearchSpecAsync(new ArchiveSearchSpec(text: "missing"), canceled.Token);
            await Program.WaitUntilAsync(() => page.IsSearchLoading);
            canceled.Cancel(); gate.SetResult();
            Require(!await late && ReferenceEquals(lastApplied, page.AppliedSearchSpec), "Canceled late answer replaced the applied result.");
            gate = null;
            // The page-1 fallback must preserve completed membership, including
            // when the remaining catalog is smaller than the requested offset.
            ArchiveSearchSpec completed = new(sources: ["A", "B"], sort: ArchiveThreadSort.Title, workFilter: ArchiveWorkFilter.Completed);
            Require(await page.ApplySearchSpecAsync(completed), "Completed work search failed.");
            await catalog.DeleteThreadsAsync(ids.Where(id => id.Source == "A").Skip(5).ToArray());
            work.Clear(); // Paging retains the successful evaluation's work membership.
            Require(await page.NavigateResultsPage(2), "Out-of-range filtered page fallback failed.");
            var remaining = page.FindControl<ListBox>("ArchiveThreads")!.ItemsSource!.Cast<ArchiveThreadRow>().ToArray();
            Require(remaining.Length == 5 && remaining.All(row => row.Summary.Id.Source == "A")
                && page.AppliedSearchSpec!.WorkFilter == ArchiveWorkFilter.Completed,
                "Page fallback escaped the frozen work filter or lost its eligible members.");
            int before = opens;
            Require(!await page.NavigateResultsPage(int.MaxValue) && before == opens, "Invalid page started a query.");
            Console.WriteLine("SMOKE archive search state: failure retention, draft isolation, direct paging, frozen retry, chip removal, late cancellation and filtered fallback passed.");
        }
        finally
        {
            page?.EndLifetime();
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

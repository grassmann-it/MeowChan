using System.Collections.Concurrent;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Gallery;
using MeowChan.Interfaces;

namespace MeowChan.DesktopSmoke;

// MEOWCHAN_MEDIA_PAGE_ONLY, presentation races: a large library presents in the background (BackgroundPresentEntries = 0 here), and the
// page's PresentGate holds a run open after its rows are computed. Held runs prove what no quick run can: a late, already replaced run
// does not overwrite a newer one, the selection and Ctrl+A made during a run are carried over, and a run that ends after the page was
// closed changes nothing.
internal static partial class MediaPageChecks
{
    // Holds every background presentation until the test releases it by its number (PresentRunCount at its start).
    private sealed class HeldPresents
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _reached = new(), _release = new();
        private volatile bool _open;

        private static TaskCompletionSource Of(ConcurrentDictionary<int, TaskCompletionSource> map, int run) =>
            map.GetOrAdd(run, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        internal Task Gate(int run, CancellationToken token)
        {
            Of(_reached, run).TrySetResult();
            return _open ? Task.CompletedTask : Of(_release, run).Task;
        }

        /// <summary>Completes once run <paramref name="run"/> has computed its rows and waits at the gate.</summary>
        internal Task Reached(int run) => Of(_reached, run).Task.WaitAsync(Deadline);

        internal void Release(int run) => Of(_release, run).TrySetResult();

        internal void ReleaseEverything()
        {
            _open = true;
            foreach (TaskCompletionSource waiting in _release.Values) waiting.TrySetResult();
        }
    }

    private static async Task BackgroundPresentRacesAsync(MainWindow owner, string profile)
    {
        new Config().SaveConfig(profile);
        await SeedAsync(profile);
        await using DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        await host.StartAsync().WaitAsync(Deadline);
        HeldPresents held = new();
        MediaPage page = new(host.OpenArchiveReaderAsync) { BackgroundPresentEntries = 0, PresentGate = held.Gate };
        Window window = new() { Width = 1400, Height = 900, Content = page, Title = "Synthetic media present races", ShowInTaskbar = false };
        try
        {
            window.Show(owner);
            page.IsAvailable = true;
            await page.ActivateAsync().WaitAsync(Deadline);
            ComboBox kind = C<ComboBox>(page, "MediaKindFilter"), sort = C<ComboBox>(page, "MediaSort");
            SearchBox name = C<SearchBox>(page, "MediaNameFilter");

            // Releases every run started so far and waits until no presentation is left running.
            async Task SettleAsync()
            {
                while (true)
                {
                    int runs = page.PresentRunCount;
                    for (int run = 1; run <= runs; run++) held.Release(run);
                    Task current = page.PresentTask;
                    await current.WaitAsync(Deadline);
                    if (runs == page.PresentRunCount && ReferenceEquals(current, page.PresentTask)) return;
                }
            }
            async Task FlushAsync()
            {
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                await Task.Delay(150);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            }
            await SettleAsync();
            Require(page.ShownEntries.Count == 3, "The race fixture must show its three files: " + page.ShownEntries.Count);

            // 1. A late old run must not overwrite a newer result. Run A (type GIF: nothing matches) is held, run B (images) is held
            // after it; B is released and applied, then A is released after it: its rows are discarded.
            int firstRun = page.PresentRunCount, applied = page.PresentCount;
            object? rowsBefore = page.Grid.ItemsSource;
            kind.SelectedIndex = (int)MediaLibraryKindFilter.Gif;
            Task runA = page.PresentTask;
            await held.Reached(firstRun + 1);
            kind.SelectedIndex = (int)MediaLibraryKindFilter.Image;
            Task runB = page.PresentTask;
            await held.Reached(firstRun + 2);
            Require(page.PresentRunCount == firstRun + 2 && !ReferenceEquals(runA, runB), "Two requests must start two runs: " + (page.PresentRunCount - firstRun));
            Require(page.PresentCount == applied && ReferenceEquals(page.Grid.ItemsSource, rowsBefore) && page.ShownEntries.Count == 3,
                "Held runs must not change the grid.");
            held.Release(firstRun + 2);
            await runB.WaitAsync(Deadline);
            object? rowsNewest = page.Grid.ItemsSource;
            Require(page.PresentCount == applied + 1 && page.ShownEntries.Count == 3 && !ReferenceEquals(rowsNewest, rowsBefore),
                "The newest run must be applied: " + (page.PresentCount - applied));
            held.Release(firstRun + 1);
            await runA.WaitAsync(Deadline);
            await FlushAsync();
            Require(page.PresentCount == applied + 1 && ReferenceEquals(page.Grid.ItemsSource, rowsNewest) && page.ShownEntries.Count == 3,
                "A late older run overwrote the newer result: " + page.PresentCount + "/" + page.ShownEntries.Count);

            // 2. A selection made while a run is held is kept (by tile, in the new order); one removed meanwhile stays removed.
            page.Grid.Selection.Clear();
            int run = page.PresentRunCount;
            sort.SelectedIndex = 1; // newest first: the tiles change places
            await held.Reached(run + 1);
            page.Grid.Selection.Select(0);
            MediaLibraryTileKey picked = MediaLibraryTileKey.Of(page.SelectedTiles.Single());
            held.Release(run + 1);
            await page.PresentTask.WaitAsync(Deadline);
            Require(page.SelectedTiles.Count == 1 && MediaLibraryTileKey.Of(page.SelectedTiles[0]) == picked,
                "A tile selected during a run must stay selected after it: " + page.SelectedTiles.Count);
            run = page.PresentRunCount;
            sort.SelectedIndex = 0;
            await held.Reached(run + 1);
            page.Grid.Selection.Clear();
            held.Release(run + 1);
            await page.PresentTask.WaitAsync(Deadline);
            Require(page.SelectedTiles.Count == 0, "A selection cleared during a run must not come back: " + page.SelectedTiles.Count);

            // 3. Ctrl+A during a run selects every tile of the new rows, not only those that were shown when it was pressed.
            name.Text = "two";
            await UntilAsync(() => page.PresentRunCount > run + 1);
            await SettleAsync();
            Require(page.ShownEntries.Count == 1, "The name filter must leave one file: " + page.ShownEntries.Count);
            run = page.PresentRunCount;
            name.Text = string.Empty;
            await UntilAsync(() => page.PresentRunCount == run + 1);
            await held.Reached(run + 1);
            page.Grid.Focus();
            page.Grid.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.A, KeyModifiers = KeyModifiers.Control });
            Require(page.Grid.Selection.Count == 1, "Ctrl+A during the run selects what is shown: " + page.Grid.Selection.Count);
            held.Release(run + 1);
            await page.PresentTask.WaitAsync(Deadline);
            Require(page.ShownEntries.Count == 3 && page.SelectedTiles.Count == 3,
                "Ctrl+A during a run must select all new tiles: " + page.ShownEntries.Count + "/" + page.SelectedTiles.Count);
            page.Grid.Selection.Clear();

            // 4. A new snapshot during a run: the newest run (the one the rebuild starts) is applied, the replaced ones are not.
            run = page.PresentRunCount;
            applied = page.PresentCount;
            kind.SelectedIndex = (int)MediaLibraryKindFilter.Gif;
            await held.Reached(run + 1);
            Task rebuild = page.RebuildAsync();
            kind.SelectedIndex = (int)MediaLibraryKindFilter.Image;
            await rebuild.WaitAsync(Deadline);
            int last = page.PresentRunCount;
            Require(last >= run + 3, "The rebuild must start its own presentation: " + (last - run));
            await held.Reached(last);
            Require(page.PresentCount == applied, "Held runs must not change the grid during a rebuild.");
            object? rowsDuringRebuild = page.Grid.ItemsSource;
            held.Release(last);
            await page.PresentTask.WaitAsync(Deadline);
            Require(page.PresentCount == applied + 1 && page.ShownEntries.Count == 3, "Only the run of the new snapshot must be applied: " + (page.PresentCount - applied) + "/" + page.ShownEntries.Count);
            object? rowsNewSnapshot = page.Grid.ItemsSource;
            await SettleAsync(); // the older runs end late
            await FlushAsync();
            Require(page.PresentCount == applied + 1 && ReferenceEquals(page.Grid.ItemsSource, rowsNewSnapshot) && !ReferenceEquals(rowsNewSnapshot, rowsDuringRebuild),
                "Older runs must not overwrite the one of the new snapshot.");

            // 5. The page ends while a run is held: its result is dropped, nothing is applied to the closed page.
            run = page.PresentRunCount;
            applied = page.PresentCount;
            object? rowsAtEnd = page.Grid.ItemsSource;
            kind.SelectedIndex = (int)MediaLibraryKindFilter.Gif;
            await held.Reached(run + 1);
            Task ending = page.PresentTask;
            page.EndLifetime();
            held.Release(run + 1);
            await ending.WaitAsync(Deadline);
            await FlushAsync();
            Require(page.PresentCount == applied && ReferenceEquals(page.Grid.ItemsSource, rowsAtEnd),
                "A run ending after EndLifetime must not touch the page: " + (page.PresentCount - applied));
            Console.WriteLine("SMOKE media present races [en]: background presentations held open after their rows are computed: a late older run does not overwrite the newer one; a selection made or cleared during a run is carried over; Ctrl+A during a run selects all tiles of the new rows; a new snapshot during a run applies only the newest run; a run ending after EndLifetime changes nothing.");
        }
        finally
        {
            held.ReleaseEverything();
            page.PresentGate = null;
            page.EndLifetime();
            window.Close();
        }
    }
}

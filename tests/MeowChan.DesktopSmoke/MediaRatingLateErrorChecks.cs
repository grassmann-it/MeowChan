using System.Reflection;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Favorites;

namespace MeowChan.DesktopSmoke;

internal static partial class MediaRatingChecks
{
    private static async Task RunTargetedRatingStateChecksAsync()
    {
        List<string> failures = new();
        string selected = Environment.GetEnvironmentVariable("MEOWCHAN_RATINGS_STATE_CASE") ?? "all";
        Require(selected is "all" or "direct" or "menu" or "tile", "Unknown targeted rating state case");
        foreach ((string name, Func<Task> run) in new (string, Func<Task>)[]
        {
            ("direct", RunLateErrorContextAsync),
            ("menu", () => RunLateMediaMenuErrorChecksAsync(MediaRatingFixture.OtherThread)),
            ("tile", RunDetachedTileRatingRefreshAsync)
        })
        {
            if (selected != "all" && selected != name) continue;
            try
            {
                await run();
                Evidence.Add(new { targetedStateCase = name, result = true });
            }
            catch (Exception error)
            {
                failures.Add(name + ": " + error.Message);
                Evidence.Add(new { targetedStateCase = name, result = false, error = error.ToString() });
            }
            SaveEvidence("targeted state cases running; failures retained");
        }
        Require(failures.Count == 0, string.Join("\n", failures));
    }

    private static async Task RunDetachedTileRatingRefreshAsync()
    {
        await _page.LoadThreadAsync(MediaRatingFixture.Thread);
        await _page.MediaLoadTask;
        _page.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex = 1;
        await SelectAsync(Row(2));
        Require(!Tiles.GetSelfAndVisualAncestors().Contains(_page), "Tile refresh regression requires the detached reader");
        FavoriteAssetKey key = KeyOf(Row(2));
        ArchiveMediaTileView CurrentTile() => Tiles.GetVisualDescendants().OfType<ArchiveMediaTileView>().Single(tile =>
            tile.IsEffectivelyVisible && tile.DataContext is ArchiveAssetRow row && KeyOf(row) == key);
        ArchiveMediaTileView before = CurrentTile();
        string? beforeText = Field<TextBlock>(before, "_rating").Text;
        int value = Rating(Row(2)) == 4 ? 2 : 4;
        Log("Change rating with the same detached tile visible", "production archive rating writer; no view switch, no test-side RefreshRating");
        await _page.SetMediaRatingAsync!(key, value);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        ArchiveMediaTileView after = CurrentTile();
        string expected = MediaRatingText.Label + ": " + MediaRatingText.Value(value);
        string? actual = Field<TextBlock>(after, "_rating").Text;
        string? automationName = AutomationProperties.GetName(after);
        bool result = actual == expected && automationName?.Contains(MediaRatingText.Value(value), StringComparison.Ordinal) == true;
        Evidence.Add(new { detachedRatingTileRefresh = true, key, value, beforeText, expected, actual, automationName,
            sameTileInstance = ReferenceEquals(before, after), currentRowRating = ((ArchiveAssetRow)after.DataContext!).Rating,
            hostRating = _window.SessionHost.GetMediaRating(key), result });
        SaveEvidence("running detached tile rating refresh check");
        Require(result, "Detached visible tile text or automation name retained an old rating after successful commit");
        Observe("Detached actual tile publishes committed value without a view switch", Rating(Row(2)) == value);
    }

    // Narrow publication-boundary regression. The delegate injects a delayed I/O
    // failure; existing semantic tests own real host writes and atomic persistence.
    private static async Task RunLateErrorContextAsync()
    {
        Require(_page.CurrentThread?.Summary.Id == MediaRatingFixture.Thread,
            "Late error fixture must start on the exact A source/thread");
        Func<FavoriteAssetKey, int?, Task>? original = _page.SetMediaRatingAsync;
        Require(original != null, "Production rating writer is not connected");
        string before = Digest(RatingPath);
        FavoriteAssetKey expectedKey = KeyOf(Row(2));
        MethodInfo executor = typeof(ArchivePage).GetMethod("ExecuteRatingCommandAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Production focused rating executor is missing");
        foreach (bool navigate in new[] { false, true })
        {
            Log("Load original A content for late error boundary", "production LoadThreadAsync executor");
            await _page.LoadThreadAsync(MediaRatingFixture.Thread);
            await _page.MediaLoadTask;
            _page.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex = 1;
            await Program.WaitUntilAsync(() => _page.CurrentThread?.Summary.Id == MediaRatingFixture.Thread && Tiles.ItemCount == 20);
            await SelectAsync(Row(2));
            TaskCompletionSource<FavoriteAssetKey> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            string cause = navigate ? "Injected delayed A rating failure after source change" : "Injected delayed A rating failure in current context";
            FavoriteAssetKey? received = null;
            int? receivedValue = null;
            _page.SetMediaRatingAsync = async (key, value) =>
            {
                received = key; receivedValue = value; entered.TrySetResult(key);
                await release.Task;
                throw new IOException(cause);
            };
            Task? pending = null;
            try
            {
                Log("Start direct focused rating executor and hold its completion", "fixture reflection invokes production async executor; TCS-delayed IOException at writer delegate");
                pending = (Task)executor.Invoke(_page, new object?[] { 4, false })!;
                FavoriteAssetKey startedKey = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Require(startedKey == expectedKey && receivedValue == 4 && !pending.IsCompleted,
                    "Delayed write must start with frozen A key and requested value");
                if (navigate)
                {
                    Log("Navigate to a different actual source/thread while A write is pending", "production LoadThreadAsync executor and real catalog/media load");
                    await _page.LoadThreadAsync(MediaRatingFixture.OtherThread);
                    await _page.MediaLoadTask;
                    Require(_page.CurrentThread?.Summary.Id == MediaRatingFixture.OtherThread && Tiles.ItemCount == 20,
                        "Actual B thread and media projection did not replace A");
                    Require(Tiles.Items.Cast<ArchiveAssetRow>().All(row => row.Rating == null && !row.RatingUnavailable),
                        "Same media bytes in another source/thread must not inherit A ratings");
                }
                TextBlock error = _page.FindControl<TextBlock>("ArchiveErrorText")!;
                string? beforeRelease = error.Text;
                release.SetResult();
                await pending.WaitAsync(TimeSpan.FromSeconds(5));
                bool correct = navigate ? error.Text == beforeRelease && !(error.Text?.Contains(cause, StringComparison.Ordinal) ?? false)
                    : error.Text?.Contains(cause, StringComparison.Ordinal) == true;
                Evidence.Add(new { lateRatingErrorContext = navigate ? "A-to-B different source/thread" : "current A positive control",
                    startedKey, receivedKey = received, requestedValue = receivedValue, currentThread = _page.CurrentThread?.Summary.Id,
                    beforeRelease, actualError = error.Text, expectedCause = cause, result = correct });
                SaveEvidence("running targeted late rating context checks");
                Require(correct, navigate ? "Late A rating failure leaked into B context" : "Current A rating error was incorrectly suppressed");
                Observe(navigate ? "Late A error cannot replace B feedback; frozen A write identity retained"
                    : "Current A error remains visible after delayed failure", received == expectedKey && Digest(RatingPath) == before);
            }
            finally
            {
                release.TrySetResult();
                if (pending != null) await pending.WaitAsync(TimeSpan.FromSeconds(5));
                _page.SetMediaRatingAsync = original;
            }
        }
        Console.WriteLine("SMOKE targeted rating late error: current-A error visible, A-to-B source/thread transition rejects stale feedback, exact writer key and durable bytes unchanged.");
    }
}

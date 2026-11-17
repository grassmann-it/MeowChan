using System.Text.Json;
using System.Text.RegularExpressions;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Localization;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

public sealed class BooruSelectionContractTests
{
    [Fact]
    public async Task SelectionIsBoundedExplicitAndPageScopedWithVisibleOverflow()
    {
        BooruUiHarness h = new(); h.ServeFullPages();
        using BooruWorkbenchModel m = new(h.Services);
        m.Search.SetInput("cat"); await m.Search.SubmitAsync();
        await m.Search.LoadSelectedAsync();
        Assert.Empty(h.Queue.Loads);
        foreach (var row in m.Search.View.Rows.Take(BooruSearchModel.MaximumSelection))
            m.Search.SetSelected(row.Post.PostId, true);
        string extra = m.Search.View.Rows[BooruSearchModel.MaximumSelection].Post.PostId;
        m.Search.SetSelected(extra, true);
        m.Search.SetSelected("not-shown", true);
        Assert.Equal(BooruSearchModel.MaximumSelection, m.Search.Selected.Count);
        Assert.DoesNotContain(extra, m.Search.Selected);
        Assert.Equal("booru.ui.selection.exceeded", m.Search.View.Notice!.Key);
        Assert.Equal(BooruSearchModel.MaximumSelection, m.Search.View.Notice.Args.Single());
        m.Search.SetSelected("9220", false); m.Search.SetSelected(extra, true);
        Assert.Contains(extra, m.Search.Selected);
        Assert.Empty(h.Queue.Loads);
        string[] selected = m.Search.View.Rows.Where(row => m.Search.Selected.Contains(row.Post.PostId))
            .Select(row => row.Post.PostId).ToArray();
        await m.Search.LoadSelectedAsync();
        Assert.Equal(BooruSearchModel.MaximumSelection, h.Queue.Loads.Count);
        Assert.Equal(selected, h.Queue.Loads.Select(load => load.PostId));
        Assert.Equal(BooruSearchModel.MaximumSelection, m.Search.Batch.Succeeded);
        await m.Search.GoToPageAsync(2);
        Assert.Empty(m.Search.Selected);
        Assert.False(m.Search.CanLoadSelected);
        Assert.False(m.Search.Batch.Started);
    }

    [Fact]
    public async Task PreviewAndUnsentEditsNeverEnterBatchAndRetryTargetsOnlyFailedSubset()
    {
        BooruUiHarness h = new(); h.Serve();
        using BooruWorkbenchModel m = new(h.Services);
        m.Search.SetInput("cat"); await m.Search.SubmitAsync();
        using BooruViewerModel viewer = new(h.Services);
        await viewer.OpenAsync(m.Search.View.Rows[2], BooruLoadOrigin.Search, "cat");
        Assert.Empty(m.Search.Selected);
        m.Search.SetSelected("101", true); m.Search.SetSelected("102", true);
        h.Queue.OnLoad = (post, _) => Task.FromResult(new BooruLoadResult(post.PostId == "102"
            ? BooruLoadStatus.Failed : BooruLoadStatus.Queued, "not_admitted"));
        m.Search.SetInput("dog");
        await m.Search.LoadSelectedAsync();
        Assert.Equal(new[] { "101", "102" }, h.Queue.Loads.Select(item => item.PostId));
        Assert.All(h.Queue.LoadQueries, q => Assert.Equal("cat", q));
        Assert.Equal(new BooruBatchView(Succeeded: 1, Failed: 1, Started: true), m.Search.Batch);
        Assert.True(m.Search.CanRetryBatch);
        m.Search.SetSelected("103", true); // later selection must not widen recovery
        m.Search.Tick();
        Assert.Equal(2, h.Queue.Loads.Count);
        await m.Search.RetryBatchAsync();
        Assert.Equal(new[] { "102" }, h.Queue.Retries);
        Assert.Equal(new BooruBatchView(Succeeded: 1, Started: true), m.Search.Batch);
        Assert.False(m.Search.CanRetryBatch);
        Assert.Single(h.Search.Searches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopSettlesCurrentHandoffThenRetainsOnlyFailedAndUnprocessed(bool independentAbort)
    {
        BooruUiHarness h = new(); h.Serve();
        using BooruWorkbenchModel m = new(h.Services);
        m.Search.SetInput("cat"); await m.Search.SubmitAsync();
        m.Search.SetSelected("101", true); m.Search.SetSelected("102", true); m.Search.SetSelected("103", true);
        TaskCompletionSource<BooruLoadResult> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Queue.OnLoad = (_, _) => answer.Task;
        Task batch = m.Search.LoadSelectedAsync();
        Assert.True(m.Search.Batch.Running);
        Assert.Equal(3, m.Search.Batch.Pending);
        Assert.False(m.Search.CanGoToPage(2));
        Assert.False(m.Search.CanSubmit);
        await m.Search.LoadSelectedAsync(); await m.Search.RetryBatchAsync();
        await m.Search.LoadOriginalAsync("103"); await m.Search.CancelOriginalAsync("101");
        await m.Search.GoToPageAsync(2); await m.Search.SubmitAsync();
        m.Search.SetSelected("103", false);
        Assert.Equal(3, m.Search.Selected.Count);
        Assert.Single(h.Queue.Loads);
        if (independentAbort) answer.SetException(new OperationCanceledException());
        else
        {
            m.Search.CancelBatch();
            Assert.True(m.Search.Batch.Running); // cannot lie about the outstanding admission
            Assert.True(m.Search.Batch.CancelRequested);
            answer.SetResult(new BooruLoadResult(BooruLoadStatus.Queued));
        }
        await batch;
        Assert.False(m.Search.Batch.Running);
        Assert.Equal(independentAbort ? 0 : 1, m.Search.Batch.Succeeded);
        Assert.Equal(independentAbort ? 1 : 0, m.Search.Batch.Failed);
        Assert.Equal(2, m.Search.Batch.Pending);
        Assert.True(m.Search.CanRetryBatch);
        m.Search.Tick(); Assert.Single(h.Queue.Loads);
        h.Queue.OnLoad = null;
        await m.Search.RetryBatchAsync();
        Assert.Equal(independentAbort ? new[] { "101" } : Array.Empty<string>(), h.Queue.Retries);
        Assert.Equal(new[] { "101", "102", "103" }, h.Queue.Loads.Select(item => item.PostId));
        Assert.Equal(independentAbort ? 3 : 2, m.Search.Batch.Succeeded);
        Assert.Equal(0, m.Search.Batch.Failed);
        Assert.Equal(0, m.Search.Batch.Pending);
        Assert.Empty(h.Queue.Cancellations); // accepted jobs were never claimed to be cancelled
        Assert.Single(h.Search.Searches);
    }

    [Theory]
    [InlineData("Offline")]
    [InlineData("exception")]
    [InlineData("Restricted")]
    [InlineData("NotOffered")]
    public async Task FailuresAreCountedAndOnlyUsableOriginalsReachTheQueue(string failure)
    {
        BooruUiHarness h = new();
        BooruOriginalAccess access = failure == "Restricted" ? BooruOriginalAccess.Restricted
            : failure == "NotOffered" ? BooruOriginalAccess.NotOffered : BooruOriginalAccess.Available;
        h.Search.OnSearch = (q, p, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
            BooruUiFixtures.Page(q, p, true, BooruUiFixtures.Post("1", access), BooruUiFixtures.Post("2"))));
        h.Queue.OnLoad = (_, _) => failure == "exception" ? throw new InvalidOperationException()
            : Task.FromResult(new BooruLoadResult(BooruLoadStatus.Offline));
        using BooruWorkbenchModel m = new(h.Services);
        m.Search.SetInput("cat"); await m.Search.SubmitAsync();
        m.Search.SetSelected("1", true); await m.Search.LoadSelectedAsync();
        Assert.Equal(0, m.Search.Batch.Succeeded); Assert.Equal(1, m.Search.Batch.Failed);
        Assert.True(m.Search.CanRetryBatch);
        Assert.Equal(access == BooruOriginalAccess.Available ? 1 : 0, h.Queue.Loads.Count);
        Assert.DoesNotContain(h.Queue.Loads, load => load.PostId == "2");
        h.Queue.OnLoad = null; await m.Search.RetryBatchAsync();
        Assert.Equal(access == BooruOriginalAccess.Available ? new[] { "1" } : Array.Empty<string>(), h.Queue.Retries);
    }

    [Fact]
    public async Task ExistingQueueOrLibraryStateIsNotAdmittedTwiceAndSinglePostUsesOneShot()
    {
        BooruUiHarness h = new();
        using BooruWorkbenchModel m = new(h.Services);
        m.Search.SetMode(BooruInputMode.Post); m.Search.SetInput("123"); await m.Search.SubmitAsync();
        m.Search.SetSelected("123", true); await m.Search.LoadSelectedAsync(); await m.Search.LoadSelectedAsync();
        Assert.Equal(new[] { ("123", BooruLoadOrigin.OneShot) }, h.Queue.Loads);
        Assert.Null(h.Queue.LoadQueries.Single());
        Assert.Equal(1, m.Search.Batch.Succeeded);
        m.Search.SetInput("invalid"); await m.Search.SubmitAsync();
        Assert.Empty(m.Search.Selected); Assert.False(m.Search.CanRetryBatch);
    }

    [Fact]
    public void SelectionControlsAreExplicitAndAllTenPacksHaveUniqueKeysAndMatchingPlaceholders()
    {
        string root = Root();
        string cards = File.ReadAllText(Path.Combine(root, "Desktop/Views/Booru/BooruWorkbenchView.Cards.cs"));
        string search = File.ReadAllText(Path.Combine(root, "Desktop/Views/Booru/BooruWorkbenchView.Search.cs"));
        Assert.Contains("_model.Search.SetSelected(post.PostId, select.IsChecked == true);", cards);
        Assert.Contains("select.IsChecked = _model.Search.Selected.Contains(post.PostId);", cards);
        Assert.Contains("await _model.Search.LoadSelectedAsync();", search);
        Assert.Contains("await _model.Search.RetryBatchAsync();", search);
        Assert.Contains("_model.Search.CancelBatch();", search);
        string[] keys = BooruUiCatalog.Entries.Select(e => e.Key).Where(k => k.StartsWith("booru.ui.selection.")).ToArray();
        Assert.Equal(8, keys.Length);
        string[] packs = Directory.GetFiles(Path.Combine(root, "Desktop/Localization/Packs"), "*.json");
        Assert.Equal(10, packs.Length);
        foreach (string path in packs)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            // Packs place strings in the translations object; locate it without relying on metadata shape.
            var strings = document.RootElement.EnumerateObject().Select(p => p.Value)
                .First(v => v.ValueKind == JsonValueKind.Object && v.TryGetProperty(keys[0], out _));
            string[] names = strings.EnumerateObject().Select(p => p.Name).ToArray();
            Assert.Equal(names.Length, names.Distinct().Count());
            foreach (string key in keys)
            {
                Assert.Contains(key, BooruText.RequiredKeys());
                string translated = strings.GetProperty(key).GetString()!;
                Assert.False(string.IsNullOrWhiteSpace(translated));
                Assert.Equal(Placeholders(BuiltInCatalog.English[key]), Placeholders(translated));
                Assert.Equal(Placeholders(BuiltInCatalog.English[key]), Placeholders(BuiltInCatalog.German[key]));
            }
        }
    }

    private static string[] Placeholders(string text) => Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).Order().ToArray();
    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../.."));
}

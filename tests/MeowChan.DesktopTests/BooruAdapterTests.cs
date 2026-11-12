using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Desktop.Booru;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

/// <summary>The thin adapters only translate results of the existing APIs; they add no behavior.</summary>
public sealed class BooruAdapterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private static BooruPost Sdk(string id, bool original = true, bool restricted = false, bool deleted = false) =>
        new(new SourcePostId("danbooru", id), BooruFieldPresence.All, new Uri("https://danbooru.donmai.us/posts/" + id), Now,
            BooruRating.Questionable, "q", 5, 1, new[] { new BooruTag("cat", BooruTagCategory.General) },
            original ? new[]
            {
                new BooruMediaVariant(BooruMediaRole.Preview, new Uri("https://cdn.donmai.us/180x180/p.jpg")),
                new BooruMediaVariant(BooruMediaRole.Sample, new Uri("https://cdn.donmai.us/sample/s.jpg")),
                new BooruMediaVariant(BooruMediaRole.Original, restricted ? null : new Uri("https://cdn.donmai.us/original/o.png"), "png")
            } : new[] { new BooruMediaVariant(BooruMediaRole.Sample, new Uri("https://cdn.donmai.us/sample/s.jpg")) },
            null, deleted);

    [Fact]
    public void ASampleNeverCountsAsAnOriginalAndRestrictionsAreKept()
    {
        Assert.Equal(BooruOriginalAccess.Available, BooruPostView.From(Sdk("1")).OriginalAccess);
        Assert.Equal(BooruOriginalAccess.NotOffered, BooruPostView.From(Sdk("2", original: false)).OriginalAccess);
        Assert.Equal(BooruOriginalAccess.Restricted, BooruPostView.From(Sdk("3", restricted: true)).OriginalAccess);
        Assert.Equal(BooruOriginalAccess.Restricted, BooruPostView.From(Sdk("4", deleted: true)).OriginalAccess);
    }

    [Fact]
    public void OnlyFlaggedFieldsAreObservations()
    {
        BooruPost bare = new(new SourcePostId("danbooru", "9"), BooruFieldPresence.None, Rating: BooruRating.Explicit, Score: 99);
        BooruPostView view = BooruPostView.From(bare);
        Assert.Equal(BooruRating.Unknown, view.Rating);
        Assert.Null(view.Score);
        Assert.True(view.Tags.IsEmpty);
    }

    [Fact]
    public void SearchResultsMapToPagesDeferralsAndFailures()
    {
        BooruCallResult<BooruResultPage> ok = BooruSdkMapper.Map(new BooruSearchSnapshot(
            new BooruSearchPage("danbooru", "cat", 2, new[] { Sdk("1"), Sdk("2") }, true)));
        Assert.Equal(BooruCallStatus.Ok, ok.Status);
        Assert.Equal(("cat", 2, 2, true), (ok.Value!.Query, ok.Value.Page, ok.Value.Posts.Length, ok.Value.IsEnd));

        BooruCallResult<BooruResultPage> limited = BooruSdkMapper.Map(new BooruSearchDeferred("danbooru", Now.AddMinutes(1), "x", 429));
        Assert.Equal(BooruCallStatus.RateLimited, limited.Status);
        Assert.Equal(Now.AddMinutes(1), limited.RetryAt);
        Assert.Equal(BooruCallStatus.RateLimited, BooruSdkMapper.Map(new BooruSearchDeferred("danbooru", Now, BooruSdkMapper.ThrottledReason)).Status);
        Assert.Equal(BooruCallStatus.Cooldown, BooruSdkMapper.Map(new BooruSearchDeferred("danbooru", Now, "provider-unavailable", 503)).Status);

        foreach ((string code, int? http, BooruCallStatus expected) in new (string, int?, BooruCallStatus)[]
        {
            ("http-network", null, BooruCallStatus.Offline), ("http-timeout", null, BooruCallStatus.Offline),
            ("access-denied", 403, BooruCallStatus.Denied), ("x", 401, BooruCallStatus.Denied),
            ("not-found", 404, BooruCallStatus.NotFound), ("capability", null, BooruCallStatus.Unavailable),
            (BooruSearchService_InvalidQuery, null, BooruCallStatus.InvalidInput), ("something", 500, BooruCallStatus.Failed)
        })
        {
            BooruCallResult<BooruResultPage> mapped = BooruSdkMapper.Map(new BooruSearchFailure("danbooru",
                new FailureDetail(code, "provider text that must not surface", http)));
            Assert.Equal(expected, mapped.Status);
            Assert.Equal(code, mapped.Code);
        }
    }

    private const string BooruSearchService_InvalidQuery = MeowChan.PluginHost.Sources.Booru.BooruSearchService.InvalidQueryCode;

    [Fact]
    public void PostResultsMapToTheOneShotOutcomes()
    {
        Assert.Equal("5", BooruSdkMapper.Map(new BooruPostSnapshot(Sdk("5"))).Value!.PostId);
        Assert.Equal(BooruCallStatus.NotFound, BooruSdkMapper.Map(new BooruPostNotFound(new SourcePostId("danbooru", "5"), 404)).Status);
        Assert.Equal(BooruCallStatus.RateLimited, BooruSdkMapper.Map(new BooruPostDeferred(new SourcePostId("danbooru", "5"), Now, "x", 429)).Status);
        Assert.Equal(BooruCallStatus.Offline, BooruSdkMapper.Map(new BooruPostFailure(new SourcePostId("danbooru", "5"),
            new FailureDetail("http-network", "m"))).Status);
    }

    // ---- catalog reads ----------------------------------------------------------------------------------------------

    private static BooruPostRecord Record(string postId, string? asset, params BooruFinding[] findings) =>
        new("danbooru", postId, new BooruPostClaim("danbooru", postId, BooruPostClaimPresence.None), Now, Now, asset, null, findings);

    [Fact]
    public void LocalStatesComeOnlyFromPostsWithALocalAssetOfThatSource()
    {
        BooruCatalogSnapshot snapshot = new(1, new[]
        {
            Record("1", "asset-a"), Record("2", null),
            new BooruPostRecord("other", "3", new BooruPostClaim("other", "3", BooruPostClaimPresence.None), Now, Now, "asset-c")
        }, Array.Empty<BooruWatcherRecord>());
        var states = BooruCatalogReadMapper.LocalStates(snapshot, "danbooru", new[] { "1", "2", "3", "4" });
        Assert.Equal(BooruLocalState.Loaded, Assert.Single(states).Value);
        Assert.True(states.ContainsKey("1"));
    }

    [Fact]
    public void StoredWatchersAreProjectedWithLowerBoundHitCountsAndRemovedOnesHidden()
    {
        BooruFinding Finding(string id, string watcher) => new(id, BooruFindingKind.Watcher, watcher, "cat", Now);
        BooruCatalogSnapshot snapshot = new(1, new[]
        {
            Record("1", "a", Finding("f1", "w1")), Record("2", null, Finding("f2", "w1")), Record("3", null, Finding("f3", "w2"))
        }, new[]
        {
            new BooruWatcherRecord("w1", "danbooru", "cat", "Active", Now, Now, null, null, "3", 0, true),
            new BooruWatcherRecord("w2", "danbooru", "dog", "Paused", Now),
            new BooruWatcherRecord("w3", "danbooru", "gone", "Removed", Now),
            new BooruWatcherRecord("w4", "danbooru", "weird", "not-a-state", Now),
            new BooruWatcherRecord("w5", "other", "foreign", "Active", Now)
        });
        var views = BooruCatalogReadMapper.Watchers(snapshot, "danbooru");
        Assert.Equal(new[] { "w1", "w2", "w4" }, views.Select(view => view.WatcherId).ToArray());
        Assert.Equal((2, 1), (views[0].HitCount, views[0].QueuedCount));
        Assert.True(views[0].BaselineEstablished);
        Assert.Equal(BooruWatcherStatus.Paused, views[1].Status);
        Assert.Equal(BooruWatcherStatus.Error, views[2].Status); // an unreadable state is shown, never guessed
    }

    [Fact]
    public void TheDomainAggregateProjectsToTheViewWithTheSameState()
    {
        Assert.True(DanbooruWatcher.TryCreate("w1", "cat", Now, out DanbooruWatcher? watcher, out _));
        BooruWatcherView active = BooruCatalogReadMapper.View(watcher!, Now.AddMinutes(30), 0, 0);
        Assert.Equal((BooruWatcherStatus.Active, "cat"), (active.Status, active.Query));
        Assert.False(active.BaselineEstablished);

        DanbooruWatcher paused = DanbooruWatcher.Pause(watcher!).Watcher;
        Assert.Equal(BooruWatcherStatus.Paused, BooruCatalogReadMapper.View(paused, null, 0, 0).Status);
        Assert.Equal(BooruWatcherStatus.Removed,
            BooruCatalogReadMapper.View(DanbooruWatcher.Remove(paused).Watcher, null, 0, 0).Status);
        foreach (DanbooruWatcherState state in Enum.GetValues<DanbooruWatcherState>())
            Assert.Equal(state.ToString(), BooruCatalogReadMapper.FromDomain(state).ToString());
    }

    [Fact]
    public async Task TheLocalStateReaderUsesOneCatalogReadPerQuery()
    {
        int reads = 0;
        BooruCatalogLocalStateReader reader = new(() =>
        {
            reads++;
            return new BooruCatalogSnapshot(1, new[] { Record("1", "asset") }, Array.Empty<BooruWatcherRecord>());
        });
        var states = await reader.GetAsync(new[] { "1", "2" }, CancellationToken.None);
        Assert.Equal(BooruLocalState.Loaded, states["1"]);
        Assert.Equal(1, reads);
        BooruCatalogLocalStateReader missing = new(() => null);
        Assert.Empty(await missing.GetAsync(new[] { "1" }, CancellationToken.None));
    }
}

using System.Collections.Concurrent;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.PluginHost.Http;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

/// <summary>Post links, search provenance and the watcher notification of the Danbooru window.</summary>
public sealed class BooruQuickWinTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    // ---- canonical post link ---------------------------------------------------------------------------------

    [Fact]
    public void OnlyTheCanonicalPostPageIsEverBuiltFromAValidatedId()
    {
        Assert.True(BooruPostLinks.TryCanonical("4242", out string url));
        Assert.Equal("https://danbooru.donmai.us/posts/4242", url);
        Assert.True(BooruPostLinks.TryCanonical(BooruUiFixtures.Post("101"), out string fromPost));
        Assert.Equal("https://danbooru.donmai.us/posts/101", fromPost);
        foreach (string? bad in new[] { null, "", "0", "012", "12a", "../1", "1?x=1", "https://evil.example/posts/1", "1234567890123456789" })
            Assert.False(BooruPostLinks.TryCanonical(bad, out _), bad);
    }

    [Fact]
    public void AProviderClaimedPostUrlNeverChoosesTheLink()
    {
        // Same post id, but a different provider claim for the post page: the canonical link ignores it.
        BooruPostView post = BooruUiFixtures.Post("77");
        Assert.True(BooruPostLinks.TryCanonical(post, out string url));
        Assert.Equal("https://danbooru.donmai.us/posts/77", url);
        Assert.False(BooruPostLinks.TryCanonical(
            BooruPostView.From(new MeowChan.PluginSdk.BooruPost(new MeowChan.PluginSdk.BooruPostId("other", "77"),
                MeowChan.PluginSdk.BooruFieldPresence.None, new Uri("https://evil.example/x"), null,
                MeowChan.PluginSdk.BooruRating.Unknown, null, null, null, null, null, null, false, false)), out _));
    }

    // ---- provenance --------------------------------------------------------------------------------------------

    [Fact]
    public void OnlyACanonicalQueryOfASearchIsKeptAsProvenance()
    {
        Assert.Equal("cat blue_eyes", BooruOriginalQueuePort.Provenance(BooruFindingKind.Search, "cat blue_eyes"));
        Assert.Null(BooruOriginalQueuePort.Provenance(BooruFindingKind.OneShot, "cat blue_eyes"));
        Assert.Null(BooruOriginalQueuePort.Provenance(BooruFindingKind.Search, null));
        Assert.Null(BooruOriginalQueuePort.Provenance(BooruFindingKind.Search, ""));
        Assert.Null(BooruOriginalQueuePort.Provenance(BooruFindingKind.Search, "  cat   blue_eyes "));
        Assert.Null(BooruOriginalQueuePort.Provenance(BooruFindingKind.Search, "cat\nblue_eyes"));
    }

    [Fact]
    public async Task ASearchResultCarriesItsQueryAndAPostLinkCarriesNone()
    {
        BooruUiHarness h = new();
        h.Serve(pages: 1, perPage: 2);
        using BooruWorkbenchModel model = new(h.Services);
        await BooruTestSupport.SearchAsync(model.Search, "cat blue_eyes");
        await BooruTestSupport.SettleAsync(model.Search);
        await model.Search.LoadOriginalAsync(model.Search.View.Rows[0].Post.PostId);
        await BooruTestSupport.SearchAsync(model.Search, "https://danbooru.donmai.us/posts/4242", BooruInputMode.Post);
        await model.Search.LoadOriginalAsync("4242");
        Assert.Equal(new string?[] { "cat blue_eyes", null }, h.Queue.LoadQueries);
        Assert.Equal(new[] { BooruLoadOrigin.Search, BooruLoadOrigin.OneShot }, h.Queue.Loads.Select(load => load.Origin));
    }

    [Fact]
    public async Task TheCatalogKeepsTheSearchQueryOfALoadedOriginal()
    {
        using TempDirectory profile = new();
        using DanbooruStub stub = new();
        stub.Add(new(4003));
        stub.Add(new(4002));
        stub.Publish("cat_ears", 4003, 4002);
        using DanbooruTestBinding binding = new(stub);
        await using DesktopSessionHost host = await BooruCompositionHostTests.StartAsync(profile.Path, stub, () => binding.Binding, paused: true);
        BooruWorkbenchServices services = host.BooruServices!;
        BooruResultPage page = (await services.Search.SearchAsync("cat_ears", 1, BooruSearchModel.PageSize, CancellationToken.None).WaitAsync(Deadline)).Value!;

        Assert.Equal(BooruLoadStatus.Queued,
            (await services.Queue.LoadOriginalAsync(page.Posts[0], BooruLoadOrigin.Search, "cat_ears", CancellationToken.None).WaitAsync(Deadline)).Status);
        // A not canonical text is dropped instead of failing the load or being stored.
        Assert.Equal(BooruLoadStatus.Queued,
            (await services.Queue.LoadOriginalAsync(page.Posts[1], BooruLoadOrigin.Search, "  cat   ears ", CancellationToken.None).WaitAsync(Deadline)).Status);
        BooruCatalogSnapshot catalog = host.BooruComposition!.Catalog.Read();
        Assert.Equal("cat_ears", Assert.Single(Assert.Single(catalog.Posts, post => post.PostId == "4003").Findings).Query);
        Assert.Null(Assert.Single(Assert.Single(catalog.Posts, post => post.PostId == "4002").Findings).Query);
        Assert.Empty(stub.Violations);
    }

    [Fact]
    public async Task PagerLimitsReachTheProviderAndStayWithTheSearchAndItsTabCopy()
    {
        using TempDirectory profile = new();
        using DanbooruStub stub = new();
        stub.Add(new(7003));
        stub.Add(new(7002));
        stub.Add(new(7001));
        stub.Publish("cat_ears", 7003, 7002, 7001);
        using DanbooruTestBinding binding = new(stub);
        await using DesktopSessionHost host = await BooruCompositionHostTests.StartAsync(profile.Path, stub,
            () => binding.Binding, paused: true);
        using BooruWorkbenchModel model = new(host.BooruServices!);
        BooruSearchModel search = model.Search;
        BooruPagerLimits configured = new(2, 1);
        search.ReadPagerLimits = () => configured;
        BooruSearchTabs tabs = new(search);
        search.SetInput("cat_ears");
        await search.SubmitAsync().WaitAsync(Deadline);
        Assert.Equal("7003", Assert.Single(search.View.Rows).Post.PostId);
        Assert.True(search.View.HasNextPage);

        configured = new(1, 2);
        tabs.Add(copyCurrent: true);
        await search.GoToPageAsync(2).WaitAsync(Deadline);
        Assert.Equal("7002", Assert.Single(search.View.Rows).Post.PostId);
        Assert.True(search.View.PageLimitReached);
        Assert.False(search.View.HasNextPage);
        await search.GoToPageAsync(3).WaitAsync(Deadline);
        await search.DownloadAllAsync().WaitAsync(Deadline);
        Assert.Equal(BooruBulkEnd.PageLimit, search.Bulk.End);
        Assert.Equal(2, search.Bulk.PagesRead);
        Assert.Equal(2, search.Bulk.Found);
        Assert.Equal(new BooruPagerLimits(2, 1), search.Bulk.PagerLimits);

        await search.SubmitAsync().WaitAsync(Deadline);
        Assert.Equal(2, search.View.Rows.Length);
        Assert.True(search.View.PageLimitReached);
        Assert.Equal(new[] { "?tags=cat_ears&page=1&limit=1", "?tags=cat_ears&page=2&limit=1",
            "?tags=cat_ears&page=1&limit=1", "?tags=cat_ears&page=2&limit=1", "?tags=cat_ears&page=1&limit=2" },
            stub.Requests.Select(url => new Uri(url)).Where(uri => uri.AbsolutePath == "/posts.json").Select(uri => uri.Query));
        Assert.Empty(stub.Violations);
    }

    // ---- watcher notification ----------------------------------------------------------------------------------

    [Fact]
    public async Task AnAutomaticCheckThatQueuedOriginalsIsReportedOnceAndAManualOneIsNot()
    {
        using TempDirectory profile = new();
        using DanbooruStub stub = new();
        stub.Add(new(5001));
        stub.Publish("cat_ears", 5001);
        using DanbooruTestBinding binding = new(stub);
        // A globally paused session no longer checks watchers. Hold originals so admitted posts stay queued
        // while this test observes the watcher notification and catalog state.
        stub.HoldOriginals();
        await using DesktopSessionHost host = await BooruCompositionHostTests.StartAsync(profile.Path, stub, () => binding.Binding,
            options: new DanbooruWatcherSchedulerOptions { Interval = TimeSpan.FromSeconds(1), CheckStartSpacing = TimeSpan.Zero });
        ConcurrentQueue<BooruWatcherCheckNotice> notices = new();
        host.BooruWatcherQueued += notices.Enqueue;
        IBooruWatcherPort watchers = host.BooruServices!.Watchers;

        Assert.Equal(BooruWatcherCommandStatus.Done, (await watchers.CreateAsync("cat_ears", CancellationToken.None)).Status);
        string id = Assert.Single(await watchers.ListAsync(CancellationToken.None)).WatcherId;
        await BooruCompositionHostTests.WaitAsync(async () => (await watchers.ListAsync(CancellationToken.None)).Single().BaselineEstablished,
            "the baseline check");
        await Task.Delay(200);
        Assert.Empty(notices); // the baseline queues nothing

        stub.Add(new(5003));
        stub.Add(new(5002));
        stub.Publish("cat_ears", 5003, 5002);
        await BooruCompositionHostTests.WaitAsync(() => !notices.IsEmpty, "the notice of the automatic check");
        BooruWatcherCheckNotice notice = Assert.Single(notices);
        Assert.Equal((id, "cat_ears", 2), (notice.WatcherId, notice.Query, notice.Queued));
        await Task.Delay(2500); // further automatic checks find nothing new and announce nothing
        Assert.Single(notices);
        Assert.Equal("5003", Assert.Single(await watchers.ListAsync(CancellationToken.None)).LatestPostId);

        // A check the user started reports in the window itself, not as a notification.
        Assert.Equal(BooruWatcherCommandStatus.Done, (await watchers.PauseAsync(id, CancellationToken.None)).Status);
        stub.Add(new(5004));
        stub.Publish("cat_ears", 5004, 5003, 5002);
        BooruWatcherCommandResult manual = await watchers.CheckNowAsync(id, CancellationToken.None).WaitAsync(Deadline);
        Assert.Equal((BooruWatcherCommandStatus.Done, 1), (manual.Status, manual.Queued));
        await Task.Delay(500);
        Assert.Single(notices);
        Assert.Empty(stub.Violations);
    }

    [Fact]
    public async Task TheHostFollowsTheCurrentPortsForNotices()
    {
        BooruUiHarness first = new();
        BooruUiHarness second = new();
        using TempDirectory profile = new();
        new Config { DownloadPath = Path.Combine(profile.Path, "media"), AutomaticUpdateChecks = false }.SaveConfig(profile.Path);
        await using DesktopSessionHost host = new(profile.Path, Array.Empty<MeowChan.Interfaces.IImageBoard>());
        {
            List<string> seen = new();
            host.BooruWatcherQueued += notice => seen.Add(notice.WatcherId);
            host.BooruServices = first.Services;
            first.Watchers.RaiseCheckQueued(new BooruWatcherCheckNotice("a", "cat", 1));
            host.BooruServices = second.Services;
            first.Watchers.RaiseCheckQueued(new BooruWatcherCheckNotice("stale", "cat", 1));
            second.Watchers.RaiseCheckQueued(new BooruWatcherCheckNotice("b", "cat", 3));
            host.BooruServices = null;
            second.Watchers.RaiseCheckQueued(new BooruWatcherCheckNotice("gone", "cat", 1));
            Assert.Equal(new[] { "a", "b" }, seen);
        }
    }
}

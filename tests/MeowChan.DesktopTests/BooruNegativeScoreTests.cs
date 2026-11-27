using System.Net;
using System.Text;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Http;
using MeowChan.Persistence;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Http;
using MeowChan.PluginHost.Sources.Booru;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

public sealed class BooruNegativeScoreTests
{
    [Fact]
    public async Task WatcherPageWithNegativeScoreSurvivesCatalogRoundTrip()
    {
        DanbooruWatcherSearchOutcome outcome = await SearchAsync("""
            [
              {"id":3,"score":-5,"fav_count":2},
              {"id":2,"score":0},
              {"id":1,"score":12}
            ]
            """);

        DanbooruWatcherSearchSnapshot page = Assert.IsType<DanbooruWatcherSearchSnapshot>(outcome);
        AssertScores(page.Posts);

        using TempDirectory profile = new();
        DateTimeOffset observed = DateTimeOffset.UtcNow;
        using (ProfileLease lease = ProfileLease.Acquire(profile.Path))
        {
            BooruCatalogStore store = new(profile.Path, lease);
            foreach (BooruPostClaim post in page.Posts)
                store.UpsertPost(post, observed);
        }

        using ProfileLease reopenedLease = ProfileLease.Acquire(profile.Path);
        BooruCatalogSnapshot reopened = new BooruCatalogStore(profile.Path, reopenedLease).Read();
        AssertScores(reopened.Posts.Select(record => record.Post).OrderByDescending(post => post.PostId));
    }

    [Fact]
    public async Task CatalogReloadPreservesOmittedScoreAndClearsScoreAfterNegativeObservation()
    {
        DanbooruWatcherSearchSnapshot initial = Assert.IsType<DanbooruWatcherSearchSnapshot>(await SearchAsync("""
            [{"id":2,"score":12},{"id":1,"score":12}]
            """));
        DanbooruWatcherSearchSnapshot update = Assert.IsType<DanbooruWatcherSearchSnapshot>(await SearchAsync("""
            [{"id":2,"score":-5},{"id":1}]
            """));

        using TempDirectory profile = new();
        DateTimeOffset observed = DateTimeOffset.UtcNow;
        using (ProfileLease lease = ProfileLease.Acquire(profile.Path))
        {
            BooruCatalogStore store = new(profile.Path, lease);
            foreach (BooruPostClaim post in initial.Posts)
                store.UpsertPost(post, observed);
            foreach (BooruPostClaim post in update.Posts)
                store.UpsertPost(post, observed.AddSeconds(1));
        }

        using ProfileLease reopenedLease = ProfileLease.Acquire(profile.Path);
        BooruCatalogSnapshot reopened = new BooruCatalogStore(profile.Path, reopenedLease).Read();
        Assert.Collection(reopened.Posts.Select(record => record.Post).OrderByDescending(post => post.PostId),
            negative => Assert.Equal(("2", (int?)null, BooruPostClaimPresence.Score),
                (negative.PostId, negative.Score, negative.Fields)),
            omitted => Assert.Equal(("1", (int?)12, BooruPostClaimPresence.Score),
                (omitted.PostId, omitted.Score, omitted.Fields)));
    }

    [Fact]
    public async Task DanbooruRejectsTheEntirePageWhenAFavoriteCountIsNegative()
    {
        DanbooruWatcherSearchOutcome outcome = await SearchAsync("""
            [
              {"id":2,"score":12,"fav_count":2},
              {"id":1,"score":0,"fav_count":-1}
            ]
            """);

        Assert.Equal("malformed", Assert.IsType<DanbooruWatcherSearchFailed>(outcome).Code);
    }

    private static void AssertScores(IEnumerable<BooruPostClaim> posts)
    {
        Assert.Collection(posts,
            negative =>
            {
                Assert.Equal("3", negative.PostId);
                Assert.Null(negative.Score);
                Assert.Equal(BooruPostClaimPresence.Score | BooruPostClaimPresence.FavoriteCount, negative.Fields);
                Assert.Equal(2, negative.FavoriteCount);
            },
            zero => Assert.Equal(("2", (int?)0, BooruPostClaimPresence.Score), (zero.PostId, zero.Score, zero.Fields)),
            positive => Assert.Equal(("1", (int?)12, BooruPostClaimPresence.Score),
                (positive.PostId, positive.Score, positive.Fields)));
    }

    private static async Task<DanbooruWatcherSearchOutcome> SearchAsync(string response)
    {
        IBooruSourcePlugin plugin = DanbooruTestPlugin.Plugin;
        using HttpClient transport = new(new PageResponse(response));
        using BooruHostHttp http = new(transport, new RequestPolicyState(), plugin.Describe().Sources);
        BooruHostServices services = new(new NoInnerServices(), http);
        await using InProcessDanbooruWorker worker = new(plugin, services);
        IDanbooruWatcherSearchPort port = new BooruWatcherSearchPort(
            new BooruSearchService(services), services, _ => Task.FromResult<IPluginWorkerClient?>(worker));
        using IDanbooruWatcherSearchSession session = port.OpenCheck();
        return await session.SearchAsync("cat", null, CancellationToken.None);
    }

    private sealed class PageResponse(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private sealed class NoInnerServices : IPluginHostServices
    {
        public Task<HostHttpResult> SendHttpAsync(HostHttpRequest request, PluginOperationContext operation,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count,
            PluginOperationContext operation, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Desktop.Booru;
using MeowChan.PluginSdk;
using MeowChan.PluginHost.Http;
using MeowChan.Tests.Shared;
using System.Net;

namespace MeowChan.DesktopTests;

public sealed class BooruContentBoundaryTests
{
    [Fact]
    public async Task ACompletelyHiddenPageKeepsTheNextPageAndBulkQueuesOnlyAllowedPosts()
    {
        BooruUiHarness harness = new();
        BooruWorkspaceContent content = new(Rules(1, BooruPostClaimRating.Sensitive, "blocked_tag"));
        BooruPostView explicitPost = BooruUiFixtures.Post("41", rating: BooruRating.Explicit);
        BooruPostView blacklisted = BooruUiFixtures.Post("42", tags: ["blocked_tag"]);
        BooruPostView unknown = BooruUiFixtures.Post("43", rating: BooruRating.Unknown);
        BooruPostView control = BooruUiFixtures.Post("44");
        harness.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(page == 1
            ? BooruUiFixtures.Page(query, page, false, explicitPost, blacklisted, unknown)
            : BooruUiFixtures.Page(query, page, true, control)));
        using BooruSearchModel model = Create(harness, content);

        await BooruTestSupport.SearchAsync(model, "cat");
        Assert.Empty(model.View.Rows);
        Assert.Equal(3, model.View.HiddenCount);
        Assert.True(model.View.HasNextPage);
        Assert.Equal(0, harness.Previews.Requests);
        await model.GoToPageAsync(2);
        await BooruTestSupport.WaitAsync(() => model.View.Rows[0].Preview == BooruPreviewState.Ready, "the allowed thumbnail");
        Assert.Equal("44", Assert.Single(model.View.Rows).Post.PostId);
        Assert.Equal(1, harness.Previews.Requests);

        await model.DownloadAllAsync();
        Assert.Equal(3, model.Bulk.HiddenCount);
        Assert.Equal("44", Assert.Single(harness.Queue.Loads).PostId);
    }

    [Fact]
    public async Task UnconfirmedChoiceAndSinglePostLookupSendNoMedia()
    {
        BooruUiHarness harness = new();
        BooruPostView post = BooruUiFixtures.Post("41", rating: BooruRating.Explicit, sample: true);
        harness.Search.OnPost = (_, _) => Task.FromResult(BooruCallResult<BooruPostView>.Ok(post));
        BooruWorkspaceContent content = new(Rules(0, null));
        using BooruSearchModel model = Create(harness, content);

        await BooruTestSupport.SearchAsync(model, "41", BooruInputMode.Post);
        Assert.Empty(model.View.Rows);
        content.Publish(Rules(1, BooruPostClaimRating.Sensitive));
        await BooruTestSupport.SearchAsync(model, "41", BooruInputMode.Post);
        await model.LoadOriginalAsync("41");
        await model.RetryOriginalAsync("41");

        Assert.Equal(1, model.View.HiddenCount);
        Assert.Empty(model.View.Rows);
        Assert.Equal(0, harness.Previews.Requests);
        Assert.Empty(harness.Previews.SampleRequests);
        Assert.Empty(harness.Queue.Loads);
        Assert.Empty(harness.Queue.Retries);
    }

    [Fact]
    public void OfflineRestoreFiltersEmbeddedBytesAndRetainsKnownEndPage()
    {
        BooruUiHarness harness = new();
        BooruWorkspaceContent content = new(Rules(1, BooruPostClaimRating.Sensitive));
        using BooruSearchModel model = Create(harness, content);
        BooruSearchTabTarget target = new(null, null, null, "cat", Page: 2, IsEnd: false, LastResultPage: 3,
            Rows: [new(BooruUiFixtures.Post("41", rating: BooruRating.Explicit).Post, [1, 2, 3]),
                new(BooruUiFixtures.Post("42").Post, [4, 5, 6])]);

        BooruSearchView cached = model.ViewFromTarget(target, 10, null);
        Assert.Equal("42", Assert.Single(cached.Rows).Post.PostId);
        Assert.Equal(1, cached.HiddenCount);
        Assert.Equal(3, cached.LastResultPage);
        content.Publish(Rules(2, BooruPostClaimRating.Sensitive, "cat"));
        model.Restore(cached, 1);

        Assert.Empty(model.View.Rows);
        Assert.Equal(2, model.View.HiddenCount);
        Assert.Equal(3, model.View.LastResultPage);
        Assert.True(model.View.HasNextPage);
        Assert.Empty(harness.Search.Searches);
        Assert.Equal(0, harness.Previews.Requests);
    }

    [Fact]
    public async Task TighteningRulesDropsAnInFlightPreviewAndBlocksTheStaleRowActions()
    {
        BooruUiHarness harness = new();
        BooruWorkspaceContent content = new(Rules(1, BooruPostClaimRating.Explicit));
        BooruPostView post = BooruUiFixtures.Post("41", rating: BooruRating.Explicit, sample: true);
        harness.Search.OnPost = (_, _) => Task.FromResult(BooruCallResult<BooruPostView>.Ok(post));
        TaskCompletionSource<byte[]?> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Previews.OnPreviewAsync = (_, _) => pending.Task;
        using BooruSearchModel model = Create(harness, content);

        await BooruTestSupport.SearchAsync(model, "41", BooruInputMode.Post);
        await BooruTestSupport.WaitAsync(() => harness.Previews.Requests == 1, "the in-flight thumbnail");
        BooruResultTarget target = BooruResultTarget.From(model.View, Assert.Single(model.View.Rows));
        content.Publish(Rules(2, BooruPostClaimRating.Sensitive));
        pending.SetResult([1, 2, 3]);
        await model.LoadOriginalAsync("41", target);
        await model.RetryOriginalAsync("41", target);

        Assert.Empty(model.View.Rows);
        Assert.Empty(harness.Queue.Loads);
        Assert.Empty(harness.Queue.Retries);
    }

    [Fact]
    public async Task ProductionPortsRefuseMediaAlreadyInTheirCachesAfterATightening()
    {
        using HttpClient client = new(new PreviewTransport());
        SourceDescription source = DanbooruTestPlugin.Plugin.Describe().Sources.Single();
        using BooruPreviewHttp http = new(client, source);
        BooruWorkspaceContent content = new(Rules(1, BooruPostClaimRating.Explicit));
        BooruHostPreviewPort port = new(http, source.SourceId, content: content);
        BooruPostView post = BooruUiFixtures.Post("41", rating: BooruRating.Explicit, sample: true);

        Assert.NotNull(await port.LoadPreviewAsync(post, CancellationToken.None));
        Assert.NotNull((await port.LoadSampleAsync(post, 1024 * 1024, 1024 * 1024,
            TimeSpan.FromSeconds(5), CancellationToken.None)).Bytes);
        Assert.Equal(2, http.RequestCount);
        content.Publish(Rules(2, BooruPostClaimRating.Sensitive));

        Assert.Null(await port.LoadPreviewAsync(post, CancellationToken.None));
        Assert.Null((await port.LoadSampleAsync(post, 1024 * 1024, 1024 * 1024,
            TimeSpan.FromSeconds(5), CancellationToken.None)).Bytes);
        Assert.Equal(2, http.RequestCount);
    }

    [Fact]
    public void GallerySampleLoaderDoesNotStartForABlockedPost()
    {
        FakeBooruPreviewPort previews = new();
        BooruWorkspaceContent content = new(Rules(1, BooruPostClaimRating.Sensitive));
        BooruGallerySampleLoader loader = new() { AllowsMedia = content.Allows };
        loader.Reset(offered: true);
        Assert.Null(loader.Start(previews, BooruUiFixtures.Post("41", rating: BooruRating.Explicit, sample: true),
            null, TimeSpan.Zero, (bytes, width, pixels) => BooruGalleryPanel.DecodeBitmap(bytes, width, pixels),
            _ => false, () => { }));
        Assert.Empty(previews.SampleRequests);
    }

    private sealed class PreviewTransport : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(BooruUiFixtures.Png(3))
            });
    }

    private static BooruSearchModel Create(BooruUiHarness harness, BooruWorkspaceContent content) =>
        new([new(harness.Search, harness.Queue, harness.Previews, Content: content)], harness.Clock);

    private static BooruContentProfileSnapshot Rules(long revision, BooruPostClaimRating? rating, string blacklist = "")
    {
        BooruContentSettings settings = new(rating, blacklist, []);
        return new(rating is null ? BooruContentProfileState.Missing : BooruContentProfileState.Ready,
            settings, new BooruContentFilter(revision, settings));
    }
}

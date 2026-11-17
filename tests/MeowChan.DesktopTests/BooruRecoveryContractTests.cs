using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Localization;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class BooruRecoveryContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnIndependentlyAbortedSearchOrPostIsVisibleAndRetryableInsteadOfStuckSearching(bool post)
    {
        BooruUiHarness h = new();
        h.Search.OnSearch = (_, _, _) => throw new OperationCanceledException();
        h.Search.OnPost = (_, _) => throw new OperationCanceledException();
        using BooruWorkbenchModel m = new(h.Services);
        m.Search.SetMode(post ? BooruInputMode.Post : BooruInputMode.Tags);
        m.Search.SetInput(post ? "123" : "cat");
        await m.Search.SubmitAsync();
        Assert.False(m.Search.View.IsBusy);
        Assert.True(m.Search.View.CanRetry);
        Assert.Equal(BooruCodes.Canceled, m.Search.View.Code);
        Assert.Equal(Texts.T(BooruCodes.Key(BooruCodes.Canceled)), BooruText.SearchStatus(m.Search.View, false));
        h.Search.OnSearch = null;
        h.Search.OnPost = null;
        m.Search.SetInput(post ? "456" : "dog");
        await m.Search.RetryAsync();
        Assert.Equal(post ? BooruSearchStatus.Results : BooruSearchStatus.Empty, m.Search.View.Status);
        if (post) Assert.Equal(new[] { "123", "123" }, h.Search.PostReads);
        else Assert.Equal(new[] { ("cat", 1), ("cat", 1) }, h.Search.Searches);
        Assert.Empty(h.Queue.Loads);
    }

    [Theory]
    [InlineData("Offline")]
    [InlineData("Failed")]
    [InlineData("Denied")]
    [InlineData("NotFound")]
    [InlineData("Unavailable")]
    [InlineData("RateLimited")]
    [InlineData("Cooldown")]
    public async Task EveryRecoverableProviderFailureCanRepeatTheExecutedRequestWithoutAutomaticRetry(string status)
    {
        BooruUiHarness h = new();
        BooruCallStatus failure = Enum.Parse<BooruCallStatus>(status);
        h.Search.OnSearch = (_, _, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Of(failure,
            "http-network", h.Clock.GetUtcNow() + TimeSpan.FromMinutes(2)));
        using BooruWorkbenchModel m = new(h.Services);
        m.Search.SetInput("cat");
        await m.Search.SubmitAsync();
        Assert.Equal(!m.Search.IsPaused, m.Search.CanRetry);
        Assert.False(string.IsNullOrWhiteSpace(BooruText.SearchStatus(m.Search.View, m.Search.IsPaused)));
        Assert.Single(h.Search.Searches);
        if (m.Search.IsPaused)
        {
            await m.Search.RetryAsync();
            Assert.Single(h.Search.Searches);
            h.Clock.Advance(TimeSpan.FromMinutes(2));
            m.Search.Tick();
            Assert.Single(h.Search.Searches); // expiry never retries itself
        }
        h.Search.OnSearch = null;
        m.Search.SetInput("dog");
        await m.Search.RetryAsync();
        Assert.Equal(new[] { ("cat", 1), ("cat", 1) }, h.Search.Searches);
        Assert.Empty(h.Queue.Loads);
    }

    [Fact]
    public async Task AnAbortedThumbnailBecomesUnavailableAndOnlyItsExplicitRetryFetchesItAgain()
    {
        BooruUiHarness h = new(); h.Serve(perPage: 1);
        h.Previews.OnPreview = _ => throw new OperationCanceledException();
        using BooruWorkbenchModel m = new(h.Services);
        m.Search.SetInput("cat");
        await m.Search.SubmitAsync();
        await AwaitPreview(m.Search, BooruPreviewState.Unavailable);
        Assert.Equal(1, h.Previews.Requests);
        h.Previews.OnPreview = _ => new byte[] { 1, 2, 3 };
        await m.Search.RetryPreviewAsync("not-shown");
        Assert.Equal(1, h.Previews.Requests);
        await m.Search.RetryPreviewAsync("101");
        Assert.Equal(BooruPreviewState.Ready, m.Search.View.Rows.Single().Preview);
        Assert.Equal(2, h.Previews.Requests);
        Assert.Single(h.Search.Searches);
        Assert.Empty(h.Queue.Loads);
    }

    [Fact]
    public async Task FailedAndCancelledViewerImagesRetainPreviewAndRetryOnlyTheSample()
    {
        BooruUiHarness h = new();
        byte[] preview = BooruUiFixtures.Png(1);
        h.Cdn.Script("1", BooruImageRole.Sample, FakeCdnOutcome.Missing());
        using BooruViewerModel v = new(h.Services);
        await v.OpenAsync(new(BooruUiFixtures.Variant("1", BooruFixtureKind.Image), PreviewBytes: preview), BooruLoadOrigin.Search, "cat");
        Assert.Equal(BooruViewerImageState.Unavailable, v.View!.ImageState);
        Assert.True(v.View.CanRetryImage);
        Assert.Same(preview, v.View.ImageBytes);
        h.Cdn.Script("1", BooruImageRole.Sample, FakeCdnOutcome.Image(BooruUiFixtures.Sample(1)));
        h.Cdn.Hold("1", BooruImageRole.Sample);
        Task pending = v.RetryImageAsync();
        v.CancelImage();
        await pending;
        Assert.Equal(BooruViewerImageState.Cancelled, v.View!.ImageState);
        Assert.Same(preview, v.View.ImageBytes);
        await v.RetryImageAsync();
        Assert.Equal(BooruViewerImageState.Ready, v.View!.ImageState);
        Assert.Empty(h.Queue.Loads);
        Assert.Empty(h.Search.Searches);
        Assert.Empty(h.Search.PostReads);
        Assert.All(h.Cdn.Requests, request => Assert.Equal(BooruImageRole.Sample, request.Role));
    }

    [Fact]
    public async Task ViewerDownloadFailureRemainsExplicitAndItsRetryCannotTargetADifferentPost()
    {
        BooruUiHarness h = new();
        h.Queue.OnLoad = (_, _) => Task.FromResult(new BooruLoadResult(BooruLoadStatus.Failed, BooruCodes.NotAdmitted));
        using BooruViewerModel v = new(h.Services);
        await v.OpenAsync(new(BooruUiFixtures.Post("1")), BooruLoadOrigin.OneShot, null);
        await v.DownloadAsync("1");
        Assert.Equal(BooruRowLoad.Failed, v.View!.Row.Load);
        Assert.Equal(BooruCodes.NotAdmitted, v.View.Row.LoadCode);
        Assert.True(v.View.Row.CanRetry);
        await v.RetryDownloadAsync("2");
        Assert.Empty(h.Queue.Retries);
        await v.RetryDownloadAsync("1");
        Assert.Equal(new[] { "1" }, h.Queue.Retries);
        Assert.Equal(BooruRowLoad.Queued, v.View!.Row.Load);
        Assert.Single(h.Queue.Loads);
    }

    private static async Task AwaitPreview(BooruSearchModel model, BooruPreviewState state)
    {
        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed() { if (model.View.Rows.SingleOrDefault()?.Preview == state) ready.TrySetResult(); }
        model.Changed += Changed;
        try { Changed(); await ready.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { model.Changed -= Changed; }
    }
}

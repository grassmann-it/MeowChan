using MeowChan.Desktop.Booru;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

public sealed class BooruViewerContractTests
{
    [Fact]
    public async Task OpeningAndSwitchingRequestsOnlyDistinctSamplesAndDownloadTargetsTheShownResult()
    {
        BooruUiHarness h = new();
        h.ServeFullPages();
        using BooruWorkbenchModel model = new(h.Services);
        model.Search.SetInput("cat");
        await model.Search.SubmitAsync();
        await model.OpenViewerAsync("9220");
        Assert.Equal(BooruViewerImageState.Ready, model.Viewer.View!.ImageState);
        Assert.Equal(new[] { "9220" }, h.Samples.Loaded);
        Assert.Empty(h.Queue.Loads);
        await model.OpenViewerAsync("9218");
        model.Search.SetInput("dog");
        await model.Viewer.DownloadAsync("9220"); // detached/stale button
        await model.Viewer.DownloadAsync("missing");
        Assert.Empty(h.Queue.Loads);
        await model.Viewer.DownloadAsync("9218");
        await model.Viewer.DownloadAsync("9218"); // no double admission
        Assert.Equal(new[] { ("9218", BooruLoadOrigin.Search) }, h.Queue.Loads);
        Assert.Equal(new[] { "cat" }, h.Queue.LoadQueries);
        Assert.Equal(new[] { "9220", "9218" }, h.Samples.Loaded);
        model.Viewer.Close();
        Assert.Empty(h.Queue.Cancellations);
        Assert.Empty(h.Queue.Retries);
        Assert.Single(h.Search.Searches);
        Assert.Empty(h.Search.PostReads);
    }

    [Theory]
    [InlineData("SampleIsOriginal", "OriginalOnly")]
    [InlineData("NoSample", "OriginalOnly")]
    [InlineData("Gif", "OriginalOnly")]
    [InlineData("Video", "NoSample")]
    [InlineData("Ugoira", "NoSample")]
    [InlineData("Deleted", "NoSample")]
    [InlineData("Banned", "NoSample")]
    public async Task MissingAndNonImageSamplesKeepThePreviewWithoutAnyOriginalFallback(string kind, string expected)
    {
        BooruUiHarness h = new();
        byte[] preview = BooruUiFixtures.Png(1);
        BooruResultRowView row = new(BooruUiFixtures.Variant("1", Enum.Parse<BooruFixtureKind>(kind)),
            Preview: BooruPreviewState.Ready, PreviewBytes: preview);
        using BooruViewerModel viewer = new(h.Services);
        await viewer.OpenAsync(row, BooruLoadOrigin.OneShot, "ignored");
        Assert.Equal(Enum.Parse<BooruViewerImageState>(expected), viewer.View!.ImageState);
        Assert.Same(preview, viewer.View.ImageBytes);
        Assert.Null(viewer.View.Query);
        Assert.Equal(0, h.Samples.Requests);
        Assert.Empty(h.Queue.Loads);
        Assert.Empty(h.Cdn.Requests);
        if (kind is "Deleted" or "Banned")
        {
            await viewer.DownloadAsync("1");
            Assert.Empty(h.Queue.Loads);
        }
    }

    [Fact]
    public void EvenAnUnnormalizedRoleAliasCannotExposeAnOriginalAsAPreviewOrSample()
    {
        BooruPost post = BooruUiFixtures.Variant("1", BooruFixtureKind.Image).Post;
        Uri original = post.Media!.Single(media => media.Role == BooruMediaRole.Original).Url!;
        BooruPostView view = BooruPostView.From(post with { Media = new[] {
            post.Media!.Single(media => media.Role == BooruMediaRole.Original),
            new BooruMediaVariant(BooruMediaRole.Sample, original),
            new BooruMediaVariant(BooruMediaRole.Preview, original) } });
        Assert.Null(view.SampleUrl);
        Assert.Null(view.PreviewUrl);
        Assert.Equal(BooruSampleAvailability.OriginalOnly, view.SampleAvailability);
        Assert.False(BooruHostSamplePort.HasImageSample(view));
    }

    [Fact]
    public async Task LateSampleAndDownloadAcknowledgementsCannotRelabelAnotherOpenedResult()
    {
        BooruUiHarness h = new();
        var release = h.Cdn.Hold("1", BooruImageRole.Sample);
        using BooruViewerModel viewer = new(h.Services);
        Task first = viewer.OpenAsync(new(BooruUiFixtures.Variant("1", BooruFixtureKind.Image)), BooruLoadOrigin.Search, "cat");
        await viewer.OpenAsync(new(BooruUiFixtures.Variant("2", BooruFixtureKind.Image)), BooruLoadOrigin.Search, "cat");
        release.TrySetResult();
        await first;
        Assert.Equal("2", viewer.View!.Row.Post.PostId);
        TaskCompletionSource<BooruLoadResult> admitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Queue.OnLoad = (_, _) => admitted.Task;
        Task download = viewer.DownloadAsync("2");
        await viewer.OpenAsync(new(BooruUiFixtures.Variant("3", BooruFixtureKind.Image)), BooruLoadOrigin.Search, "cat");
        admitted.SetResult(new(BooruLoadStatus.Queued));
        await download;
        Assert.Equal("3", viewer.View!.Row.Post.PostId);
        Assert.Equal(BooruRowLoad.Idle, viewer.View.Row.Load);
        Assert.Equal(new[] { ("2", BooruLoadOrigin.Search) }, h.Queue.Loads);
    }
}

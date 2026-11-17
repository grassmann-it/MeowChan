using MeowChan.Booru;
using MeowChan.Desktop.Booru;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

// B4: the post projection (sample address, sample availability, source claim), the queue outcome mapping to the local
// state and the pure remote-item state. Fixtures and fakes only; no request is made anywhere.
public sealed class BooruRemoteStateTests
{
    private static BooruResultRowView Row(BooruRowLoad load, BooruOriginalAccess access = BooruOriginalAccess.Available) =>
        new(BooruUiFixtures.Post("700", access), load);

    // ---- derived remote item state: every load state against every original access ------------------------------

    [Theory]
    // Nothing happened yet: only the thumbnail, or nothing downloadable.
    [InlineData("Idle", "Available", false, false, "RemotePreview")]
    [InlineData("Idle", "Available", true, false, "SampleLoaded")]
    [InlineData("Idle", "NotOffered", false, false, "Unavailable")]
    [InlineData("Idle", "Restricted", false, false, "Unavailable")]
    // A sample never makes an unavailable original available.
    [InlineData("Idle", "Restricted", true, false, "Unavailable")]
    // The explicit action's own answers.
    [InlineData("NoOriginal", "Available", true, false, "Unavailable")]
    [InlineData("Restricted", "Available", false, false, "Unavailable")]
    // In the queue (handing over, queued by this window, queued by anything else, or still transferring).
    [InlineData("Handing", "Available", true, false, "Queued")]
    [InlineData("Queued", "Available", false, false, "Queued")]
    [InlineData("AlreadyQueued", "Available", true, false, "Queued")]
    // Failures keep their own state, also with a sample on screen.
    [InlineData("Failed", "Available", true, false, "Failed")]
    [InlineData("Offline", "Available", false, false, "Failed")]
    // Local: the file wins over everything, a missing file shows as missing, and only then.
    [InlineData("AlreadyLoaded", "Available", false, false, "Downloaded")]
    [InlineData("AlreadyLoaded", "Available", true, false, "Downloaded")]
    [InlineData("AlreadyLoaded", "Restricted", false, false, "Downloaded")]
    [InlineData("AlreadyLoaded", "Available", false, true, "LocalMissing")]
    [InlineData("Queued", "Available", false, true, "Queued")]
    [InlineData("Idle", "Available", false, true, "RemotePreview")]
    public void EveryRowLoadStateMapsToExactlyOneRemoteItemState(string load, string access, bool sampleLoaded,
        bool localMissing, string expected) =>
        Assert.Equal(Enum.Parse<BooruRemoteItemState>(expected),
            BooruRemoteItem.StateOf(Row(Enum.Parse<BooruRowLoad>(load), Enum.Parse<BooruOriginalAccess>(access)), sampleLoaded, localMissing));

    [Fact]
    public void TheTableCoversEveryRowLoadAndEveryStateIsReachable()
    {
        HashSet<BooruRemoteItemState> reached = new();
        foreach (BooruRowLoad load in Enum.GetValues<BooruRowLoad>())
            foreach (BooruOriginalAccess access in Enum.GetValues<BooruOriginalAccess>())
                foreach (bool sample in new[] { false, true })
                    foreach (bool missing in new[] { false, true })
                        reached.Add(BooruRemoteItem.StateOf(Row(load, access), sample, missing));
        Assert.True(Enum.GetValues<BooruRemoteItemState>().ToHashSet().SetEquals(reached), string.Join(", ", reached));
    }

    [Fact]
    public void ThePreviewStateDoesNotDependOnThePreviewBytes()
    {
        foreach (BooruPreviewState preview in Enum.GetValues<BooruPreviewState>())
            Assert.Equal(BooruRemoteItemState.RemotePreview,
                BooruRemoteItem.StateOf(Row(BooruRowLoad.Idle) with { Preview = preview, PreviewBytes = preview == BooruPreviewState.Ready ? new byte[] { 1 } : null }));
    }

    // The whole life of a hit through the real search model and the queue fake: the state follows the row, nothing else.
    [Fact]
    public async Task TheStateFollowsTheRowThroughSampleQueueFailureRetryAndDownload()
    {
        BooruUiHarness h = new();
        h.Serve(pages: 1, perPage: 1);
        using BooruSearchModel model = new(h.Search, h.Queue, h.Previews, h.Clock);
        await BooruTestSupport.SearchAsync(model, "cat");
        await BooruTestSupport.SettleAsync(model);
        BooruRemoteItemState State(bool sample = false, bool missing = false) =>
            BooruRemoteItem.StateOf(model.View.Rows[0], sample, missing);

        Assert.Equal(BooruRemoteItemState.RemotePreview, State());
        Assert.Equal(BooruRemoteItemState.SampleLoaded, State(sample: true));

        await model.LoadOriginalAsync("101");
        Assert.Equal(BooruRemoteItemState.Queued, State(sample: true));

        // The queue reports a failed download: the row fails (explicit retry), the sample on screen does not hide it.
        h.Queue.States["101"] = BooruLocalState.Failed;
        h.Queue.RaiseLocalStatesChanged();
        await BooruTestSupport.WaitAsync(() => model.View.Rows[0].Load == BooruRowLoad.Failed, "failed local state");
        Assert.Equal(BooruRemoteItemState.Failed, State(sample: true));
        Assert.True(model.View.Rows[0].CanRetry);
        Assert.Equal(BooruCodes.NotAdmitted, model.View.Rows[0].LoadCode);

        await model.RetryOriginalAsync("101");
        Assert.Equal(new[] { "101" }, h.Queue.Retries);
        Assert.Equal(BooruRemoteItemState.Queued, State());

        // The file arrives, also for a row that had failed before.
        h.Queue.States["101"] = BooruLocalState.Loaded;
        h.Queue.RaiseLocalStatesChanged();
        await BooruTestSupport.WaitAsync(() => model.View.Rows[0].Load == BooruRowLoad.AlreadyLoaded, "loaded local state");
        Assert.Equal(BooruRemoteItemState.Downloaded, State());
        Assert.Equal(BooruRemoteItemState.LocalMissing, State(missing: true));
    }

    [Fact]
    public async Task AFailedLocalStateOnlyFailsAvailableRowsAndAFailedRowFollowsOnlyACommittedFile()
    {
        BooruUiHarness h = new();
        h.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(query, page, true,
            BooruUiFixtures.Post("1"), BooruUiFixtures.Post("2", BooruOriginalAccess.Restricted))));
        h.Queue.States["1"] = BooruLocalState.Failed;
        h.Queue.States["2"] = BooruLocalState.Failed;
        using BooruSearchModel model = new(h.Search, h.Queue, h.Previews, h.Clock);
        await BooruTestSupport.SearchAsync(model, "cat");
        await BooruTestSupport.WaitAsync(() => model.View.Rows[0].Load == BooruRowLoad.Failed, "failed local state");
        Assert.Equal(BooruRowLoad.Idle, model.View.Rows[1].Load); // nothing to retry without an original

        // A queued or cleared state does not undo a visible failure (that belongs to the explicit retry) ...
        int reads = h.Queue.StateReads;
        h.Queue.States["1"] = BooruLocalState.Queued;
        h.Queue.RaiseLocalStatesChanged();
        await BooruTestSupport.WaitAsync(() => h.Queue.StateReads > reads, "state read");
        await Task.Delay(50);
        Assert.Equal(BooruRowLoad.Failed, model.View.Rows[0].Load);
        // ... a committed file does.
        h.Queue.States["1"] = BooruLocalState.Loaded;
        h.Queue.RaiseLocalStatesChanged();
        await BooruTestSupport.WaitAsync(() => model.View.Rows[0].Load == BooruRowLoad.AlreadyLoaded, "committed file");
    }

    // ---- queue outcome -> local state ------------------------------------------------------------------------------

    [Theory]
    // A committed asset wins over anything the queue says.
    [InlineData(true, false, "DownloadFailed", "Loaded")]
    [InlineData(true, true, null, "Loaded")]
    // Failures beat the stored pending request that a failed download leaves for restart recovery.
    [InlineData(false, true, "DownloadFailed", "Failed")]
    [InlineData(false, true, "LinkFailed", "Failed")]
    [InlineData(false, false, "NotAdmitted", "Failed")]
    // Queued, and interrupted by a queue stop (still pending, re-admitted after a restart).
    [InlineData(false, false, "Queued", "Queued")]
    [InlineData(false, true, "Interrupted", "Queued")]
    [InlineData(false, false, "Interrupted", "Queued")]
    // Without an outcome (a restart) the stored request alone decides.
    [InlineData(false, true, null, "Queued")]
    [InlineData(false, false, null, "NotLoaded")]
    // Cancelled and unavailable are decided by the stored request (which they have already replaced).
    [InlineData(false, false, "Cancelled", "NotLoaded")]
    [InlineData(false, false, "Unavailable", "NotLoaded")]
    [InlineData(false, true, "Cancelled", "Queued")]
    // A link that is not (yet) in the catalog read is not yet a local file.
    [InlineData(false, false, "Linked", "NotLoaded")]
    public void TheQueueOutcomeAndTheStoredRequestGiveOneLocalState(bool asset, bool pending, string? outcome, string expected)
    {
        BooruOriginalDownloadOutcome? snapshot = outcome is null ? null
            : new BooruOriginalDownloadOutcome(Enum.Parse<BooruOriginalDownloadState>(outcome), "1");
        Assert.Equal(Enum.Parse<BooruLocalState>(expected), BooruOriginalQueuePort.LocalState(asset, pending, snapshot));
    }

    [Fact]
    public void EveryDownloadOutcomeMapsToAStateSoNewOutcomesAreNoticed() =>
        // A new outcome must be classified on purpose: this guards the switch's default branch.
        Assert.Equal(8, Enum.GetValues<BooruOriginalDownloadState>().Length);

    // ---- post projection: sample address, availability and the source claim ----------------------------------------

    [Theory]
    [InlineData("Image", "Provided", true)]
    [InlineData("SampleIsOriginal", "OriginalOnly", false)]
    [InlineData("NoSample", "OriginalOnly", false)]
    [InlineData("Gif", "OriginalOnly", false)]
    // A video or ugoira sample is declared but is no still image: provided, and never requested through the image path.
    [InlineData("Video", "Provided", false)]
    [InlineData("Ugoira", "Provided", false)]
    [InlineData("Deleted", "None", false)]
    [InlineData("Banned", "None", false)]
    [InlineData("MissingFile", "None", false)]
    public void TheSampleIsProjectedFromTheMediaVariants(string kindName, string availabilityName, bool imageSample)
    {
        BooruPostView post = BooruUiFixtures.Variant("7001", Enum.Parse<BooruFixtureKind>(kindName));
        BooruSampleAvailability availability = Enum.Parse<BooruSampleAvailability>(availabilityName);
        Assert.Equal(availability, post.SampleAvailability);
        Assert.Equal(availability == BooruSampleAvailability.Provided, post.SampleUrl is not null);
        Assert.Equal(imageSample, BooruHostSamplePort.HasImageSample(post));
        if (post.SampleUrl is { } url) Assert.Equal(url, post.Post.Media!.Single(variant => variant.Role == BooruMediaRole.Sample).Url);
    }

    [Fact]
    public void ASampleThatIsTheOriginalIsExposedAndTheViewerStateStaysThePreview()
    {
        BooruPostView post = BooruUiFixtures.Variant("7002", BooruFixtureKind.SampleIsOriginal);
        Assert.Null(post.SampleUrl);
        Assert.Equal(BooruSampleAvailability.OriginalOnly, post.SampleAvailability);
        Assert.Equal(BooruOriginalAccess.Available, post.OriginalAccess);
        // No larger picture exists without a download: the state is the preview, never a "loaded sample".
        Assert.Equal(BooruRemoteItemState.RemotePreview, BooruRemoteItem.StateOf(new BooruResultRowView(post)));
    }

    [Fact]
    public void AWithdrawnPostWithAnAddressedOriginalHasNoSampleAvailability()
    {
        Assert.Equal(BooruSampleAvailability.None, BooruUiFixtures.Post("7003", deleted: true).SampleAvailability);
        Assert.Equal(BooruSampleAvailability.None, BooruUiFixtures.Post("7004", banned: true).SampleAvailability);
        Assert.Equal(BooruSampleAvailability.OriginalOnly, BooruUiFixtures.Post("7005").SampleAvailability);
    }

    [Fact]
    public void TheSampleExtensionComesFromTheDeclaredOneElseTheAddress()
    {
        Assert.Equal(".jpg", BooruUiFixtures.Variant("7006", BooruFixtureKind.Image).SampleExtension);
        Assert.Equal(".mp4", BooruUiFixtures.Variant("7007", BooruFixtureKind.Video).SampleExtension);
        Assert.Null(BooruUiFixtures.Variant("7008", BooruFixtureKind.NoSample).SampleExtension);
        BooruPostView declared = BooruPostView.From(new BooruPost(new BooruPostId("danbooru", "7009"), BooruFieldPresence.Media,
            Media: [new BooruMediaVariant(BooruMediaRole.Sample, new Uri("https://cdn.donmai.us/sample/x"), "png")]));
        Assert.Equal("png", declared.SampleExtension);
        Assert.True(BooruHostSamplePort.HasImageSample(declared));
    }

    [Fact]
    public void TheSourceClaimIsKeptAsInertTextOnlyWhenPresentAndDeclared()
    {
        Assert.Equal("https://example.invalid/art/7010", BooruUiFixtures.Variant("7010", BooruFixtureKind.Image).SourceClaim);
        Assert.Null(BooruUiFixtures.Post("7011").SourceClaim);
        BooruPost[] posts =
        {
            new(new BooruPostId("danbooru", "1"), BooruFieldPresence.SourceClaim, SourceClaim: "  "),
            new(new BooruPostId("danbooru", "2"), BooruFieldPresence.Media, SourceClaim: null),
            new(new BooruPostId("danbooru", "3"), BooruFieldPresence.SourceClaim, SourceClaim: "pixiv id 42")
        };
        Assert.Equal(new string?[] { null, null, "pixiv id 42" }, posts.Select(post => BooruPostView.From(post).SourceClaim).ToArray());
    }

    // ---- the services bundle -------------------------------------------------------------------------------------------

    [Fact]
    public async Task ABundleWithoutASamplePortAnswersNoSampleAndTheHarnessBundleCarriesTheFake()
    {
        BooruUiHarness h = new();
        BooruWorkbenchServices bare = new(h.Search, h.Queue, h.Watchers, h.Previews);
        Assert.Same(BooruNoSamplePort.Instance, bare.Samples);
        Assert.Null(await bare.Samples.LoadSampleAsync(BooruUiFixtures.Variant("7020", BooruFixtureKind.Image), CancellationToken.None));
        Assert.Same(h.Samples, h.Services.Samples);
        Assert.IsAssignableFrom<IBooruSamplePort>(h.Samples);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            bare.Samples.LoadSampleAsync(BooruUiFixtures.Variant("7021", BooruFixtureKind.Image), cancelled.Token));
        Assert.Equal(0, h.Samples.Requests);
    }
}

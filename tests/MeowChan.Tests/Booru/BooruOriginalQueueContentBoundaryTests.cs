using MeowChan.Archive;
using MeowChan.Booru;
using TempDirectory = MeowChan.Tests.TempDirectory;

namespace MeowChan.Tests.Booru;

public sealed class BooruOriginalQueueAdmissionBoundaryTests
{
    [Fact]
    public async Task StoredBlockedClaimKeepsItsIntentThroughRecoveryAndRetryWithoutNetwork()
    {
        using TempDirectory directory = new();
        using SchedulerCdn cdn = new();
        using HttpClient client = new(cdn, disposeHandler: false);
        await using SchedulerProfile profile = new(directory.Path, withQueue: true, cdn);
        BooruPostClaim post = Rated(cdn.Claim(41), BooruPostClaimRating.Explicit);
        profile.Catalog.RecordObservation(post, DateTimeOffset.UtcNow, BooruFindingKind.OneShot,
            null, null, _ => BooruOriginalIntent.Requested);
        BooruContentFilter filter = Filter(BooruPostClaimRating.Sensitive);
        BooruOriginalQueue originals = new(profile.Queue!, profile.Catalog, profile.OutputRoot, client,
            allowsContent: claim => filter.Evaluate(claim).IsAllowed);

        BooruOriginalAdmission recovered = Assert.Single(await originals.RecoverPendingAsync());
        Assert.Equal(BooruOriginalAdmissionStatus.ContentBlocked, recovered.Status);
        Assert.Equal(BooruOriginalAdmissionStatus.ContentBlocked, (await originals.RetryAsync("41")).Status);
        Assert.Equal(BooruOriginalIntent.Requested, Assert.Single(profile.Catalog.Read().Posts).OriginalIntent);
        Assert.Empty(cdn.Requests);

        filter = Filter(BooruPostClaimRating.Explicit);
        BooruOriginalAdmission retry = await originals.RetryAsync("41");
        Assert.Equal(BooruOriginalDownloadState.Linked, (await retry.Completion.WaitAsync(TestWait.Timeout)).State);
        Assert.Equal(SchedulerCdn.OriginalUrl("41"), Assert.Single(cdn.Requests));
    }

    [Fact]
    public async Task TighteningTheCeilingAfterQueueAdmissionStopsTheDispatch()
    {
        using TempDirectory directory = new();
        using SchedulerCdn cdn = new();
        using HttpClient client = new(cdn, disposeHandler: false);
        await using SchedulerProfile profile = new(directory.Path, withQueue: true, cdn);
        BooruPostClaim post = Rated(cdn.Claim(41), BooruPostClaimRating.Explicit);
        BooruContentFilter filter = Filter(BooruPostClaimRating.Explicit);
        BooruOriginalQueue originals = new(profile.Queue!, profile.Catalog, profile.OutputRoot, client,
            allowsContent: claim => Volatile.Read(ref filter).Evaluate(claim).IsAllowed);

        await profile.Queue!.SetPausedAsync(true);
        BooruOriginalAdmission queued = await originals.AdmitAsync(post, BooruFindingKind.OneShot);
        Assert.Equal(BooruOriginalAdmissionStatus.Queued, queued.Status);
        Volatile.Write(ref filter, Filter(BooruPostClaimRating.General));
        await profile.Queue.SetPausedAsync(false);

        Assert.Equal(BooruOriginalDownloadState.ContentBlocked, (await queued.Completion.WaitAsync(TestWait.Timeout)).State);
        Assert.Empty(cdn.Requests);
        Assert.Single(originals.GetPendingOriginals());
    }

    private static BooruPostClaim Rated(BooruPostClaim claim, BooruPostClaimRating rating) =>
        claim with { Rating = rating, Fields = claim.Fields | BooruPostClaimPresence.Rating };

    private static BooruContentFilter Filter(BooruPostClaimRating rating) => new(1, new(rating, "", []));
}

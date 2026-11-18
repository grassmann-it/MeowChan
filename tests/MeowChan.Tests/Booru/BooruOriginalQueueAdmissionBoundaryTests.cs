using MeowChan.Archive;
using MeowChan.Booru;
using TempDirectory = MeowChan.Tests.TempDirectory;

namespace MeowChan.Tests.Booru;

/// <summary>
/// B4 boundary interleavings around the write-ahead Requested intent and the shared queue offer.
/// The Session below is the existing production DownloadQueue + BooruCatalogStore composition; only the CDN is fake.
/// </summary>
public sealed partial class BooruOriginalQueueTests
{
    [Fact]
    public async Task CancelAfterWriteAheadBeforeOfferLeavesNoGhostJobAndRetryCreatesOneAsset()
    {
        using TempDirectory directory = new();
        FakeCdn cdn = new();
        byte[] bytes = Bytes(501);
        BooruPostClaim post = Post("501", bytes, cdn);
        await using Session session = new(directory.Path, cdn);
        int links = 0;
        session.Booru.LinkingForTests = (_, _) => Interlocked.Increment(ref links);

        TaskCompletionSource<bool> cancelledAtBoundary =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<BooruOriginalDownloadOutcome> cancelBeforeOffer = outcome =>
        {
            if (outcome.PostId != post.PostId || outcome.State != BooruOriginalDownloadState.Queued) return;
            try
            {
                // OutcomeChanged is raised after the write-ahead Requested and before EnqueueAsync is called.
                // Calling CancelAsync synchronously makes the interleaving deterministic without a timing sleep.
                cancelledAtBoundary.TrySetResult(session.Booru.CancelAsync(post.PostId).GetAwaiter().GetResult());
            }
            catch (Exception exception)
            {
                cancelledAtBoundary.TrySetException(exception);
            }
        };
        session.Booru.OutcomeChanged += cancelBeforeOffer;

        BooruOriginalAdmission admission = await session.Booru.AdmitAsync(post, BooruFindingKind.OneShot);
        session.Booru.OutcomeChanged -= cancelBeforeOffer;

        Assert.True(await cancelledAtBoundary.Task.WaitAsync(TestWait.Timeout));
        Assert.Equal(BooruOriginalAdmissionStatus.NotAdmitted, admission.Status);
        Assert.Equal(BooruOriginalDownloadState.Cancelled,
            (await admission.Completion.WaitAsync(TestWait.Timeout)).State);
        BooruPostRecord cancelled = Assert.Single(session.Catalog.Read().Posts);
        Assert.Equal(BooruOriginalIntent.Cancelled, cancelled.OriginalIntent);
        Assert.Null(cancelled.LocalAssetId);
        Assert.Empty(session.Booru.GetPendingOriginals());
        Assert.Equal(0, session.Queue.GetCapacitySnapshot().Active);
        Assert.Equal(0, session.Queue.GetCapacitySnapshot().Pending);
        Assert.Empty(cdn.Requests);

        // The cancelled owner was never offered to the queue. A later explicit retry must activate exactly one
        // fresh owner and publish exactly one verified asset, rather than reviving a ghost or creating a duplicate.
        BooruOriginalAdmission retry = await session.Booru.RetryAsync(post.PostId);
        Assert.Equal(BooruOriginalAdmissionStatus.Queued, retry.Status);
        BooruOriginalDownloadOutcome linked = await retry.Completion.WaitAsync(TestWait.Timeout);
        Assert.Equal(BooruOriginalDownloadState.Linked, linked.State);
        Assert.Equal(1, links);
        Assert.Single(cdn.Requests);
        Assert.Equal(BooruOriginalIntent.Linked, Assert.Single(session.Catalog.Read().Posts).OriginalIntent);
        Assert.Empty(session.Booru.GetPendingOriginals());
    }

    [Fact]
    public async Task PauseAfterWriteAheadBeforeOfferDoesNotCompleteEarlyAndResumesTheSameRequest()
    {
        using TempDirectory directory = new();
        FakeCdn cdn = new();
        byte[] bytes = Bytes(502);
        BooruPostClaim post = Post("502", bytes, cdn);
        await using Session session = new(directory.Path, cdn);

        TaskCompletionSource<bool> pausedAtBoundary =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<BooruOriginalDownloadOutcome> pauseBeforeOffer = outcome =>
        {
            if (outcome.PostId != post.PostId || outcome.State != BooruOriginalDownloadState.Queued) return;
            try
            {
                // The queue is still empty here. Pause it before the admission can issue its offer.
                session.Queue.SetPausedAsync(true).GetAwaiter().GetResult();
                pausedAtBoundary.TrySetResult(true);
            }
            catch (Exception exception)
            {
                pausedAtBoundary.TrySetException(exception);
            }
        };
        session.Booru.OutcomeChanged += pauseBeforeOffer;

        Task<BooruOriginalAdmission> admissionTask = session.Booru.AdmitAsync(post, BooruFindingKind.OneShot);
        await pausedAtBoundary.Task.WaitAsync(TestWait.Timeout);
        // Pause gates dispatch, not the durable offer itself: the admission may be queued, but it must not
        // complete or publish an asset until the shared queue is resumed.
        BooruOriginalAdmission admission = await admissionTask.WaitAsync(TestWait.Timeout);
        Assert.Equal(BooruOriginalAdmissionStatus.Queued, admission.Status);
        Assert.False(admission.Completion.IsCompleted);
        Assert.Empty(cdn.Requests);
        BooruPostRecord pending = Assert.Single(session.Catalog.Read().Posts);
        Assert.Equal(BooruOriginalIntent.Requested, pending.OriginalIntent);
        Assert.Null(pending.LocalAssetId);
        Assert.Equal(BooruOriginalDownloadState.Queued, session.Booru.Snapshot()[post.PostId].State);

        session.Booru.OutcomeChanged -= pauseBeforeOffer;
        await session.Queue.SetPausedAsync(false);
        Assert.Equal(BooruOriginalDownloadState.Linked,
            (await admission.Completion.WaitAsync(TestWait.Timeout)).State);
        Assert.Single(cdn.Requests);
        Assert.Empty(session.Booru.GetPendingOriginals());
    }

    [Fact]
    public async Task ProfileCutAfterWriteAheadRejectsTheOfferButRecoveryKeepsOneIntentAndAsset()
    {
        using TempDirectory directory = new();
        FakeCdn cdn = new();
        byte[] bytes = Bytes(503);
        BooruPostClaim post = Post("503", bytes, cdn);
        TaskCompletionSource<bool> cutAtBoundary =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using (Session session = new(directory.Path, cdn))
        {
            Action<BooruOriginalDownloadOutcome> cutProfileBeforeOffer = outcome =>
            {
                if (outcome.PostId != post.PostId || outcome.State != BooruOriginalDownloadState.Queued) return;
                try
                {
                    // Cut store admission after Requested is durable. The production queue therefore rejects its
                    // offer, while the catalog can no longer be downgraded to NotAdmitted in this process.
                    session.Catalog.ProfileLease.Admission.BeginShutdown();
                    cutAtBoundary.TrySetResult(true);
                }
                catch (Exception exception)
                {
                    cutAtBoundary.TrySetException(exception);
                }
            };
            session.Booru.OutcomeChanged += cutProfileBeforeOffer;

            BooruOriginalAdmission admission = await session.Booru.AdmitAsync(post, BooruFindingKind.OneShot);
            session.Booru.OutcomeChanged -= cutProfileBeforeOffer;

            await cutAtBoundary.Task.WaitAsync(TestWait.Timeout);
            Assert.Equal(BooruOriginalAdmissionStatus.NotAdmitted, admission.Status);
            Assert.Equal(BooruOriginalDownloadState.NotAdmitted,
                (await admission.Completion.WaitAsync(TestWait.Timeout)).State);
            Assert.Equal(BooruOriginalIntent.Requested, Assert.Single(session.Catalog.Read().Posts).OriginalIntent);
            Assert.Single(session.Booru.GetPendingOriginals());
            Assert.Empty(cdn.Requests);
        }

        // The rejected offer was not a user cancellation. A new production queue/catalog composition recovers the
        // still-requested intent once, and the committed asset is linked once without any duplicate activation.
        await using Session restarted = new(directory.Path, cdn);
        int links = 0;
        restarted.Booru.LinkingForTests = (_, _) => Interlocked.Increment(ref links);
        BooruOriginalAdmission recovered = Assert.Single(await restarted.Booru.RecoverPendingAsync());
        Assert.Equal(BooruOriginalAdmissionStatus.Queued, recovered.Status);
        Assert.Equal(BooruOriginalDownloadState.Linked,
            (await recovered.Completion.WaitAsync(TestWait.Timeout)).State);
        Assert.Equal(1, links);
        Assert.Single(cdn.Requests);
        Assert.Equal(BooruOriginalIntent.Linked, Assert.Single(restarted.Catalog.Read().Posts).OriginalIntent);
        Assert.Empty(await restarted.Booru.RecoverPendingAsync());
        Assert.Single(cdn.Requests);
    }
}

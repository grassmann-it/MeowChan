using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Persistence;
using TempDirectory = MeowChan.Tests.TempDirectory;

namespace MeowChan.Tests.Booru;

/// <summary>
/// B4 hardening: restart-safe cancel, stop vs. cancel, Requested only after a real admission and a
/// robust recovery lifecycle. Synthetic fixtures and the in-memory CDN only; no live provider request.
/// </summary>
public sealed partial class BooruOriginalQueueTests
{
    [Fact]
    public async Task StopEndsQueuedPausedOwnersAndKeepsTheirRequestsForRecovery()
    {
        using TempDirectory directory = new();
        FakeCdn cdn = new();
        BooruPostClaim first = Post("101", Bytes(21), cdn);
        BooruPostClaim second = Post("102", Bytes(22), cdn);
        await using (Session session = new(directory.Path, cdn))
        {
            await session.Queue.SetPausedAsync(true);
            BooruOriginalAdmission a = await session.Booru.AdmitAsync(first, BooruFindingKind.OneShot);
            BooruOriginalAdmission b = await session.Booru.AdmitAsync(second, BooruFindingKind.Watcher, "w1", "cat");
            Assert.Equal(BooruOriginalAdmissionStatus.Queued, a.Status);
            Assert.Equal(BooruOriginalAdmissionStatus.Queued, b.Status);

            await session.Queue.StopAllAsync().WaitAsync(TestWait.Timeout);

            // Jobs that never reached a worker are notified too; nothing hangs.
            Assert.Equal(BooruOriginalDownloadState.Interrupted, (await a.Completion.WaitAsync(TestWait.Timeout)).State);
            Assert.Equal(BooruOriginalDownloadState.Interrupted, (await b.Completion.WaitAsync(TestWait.Timeout)).State);
            Assert.Equal(0, session.Booru.ActiveCount);
            Assert.Equal(["101", "102"], session.Booru.GetPendingOriginals().Select(item => item.PostId).Order().ToArray());
        }
        Assert.Empty(cdn.Requests);

        await using Session reopened = new(directory.Path, cdn);
        IReadOnlyList<BooruOriginalAdmission> recovered = await reopened.Booru.RecoverPendingAsync();
        Assert.Equal(2, recovered.Count);
        foreach (BooruOriginalAdmission admission in recovered)
            Assert.Equal(BooruOriginalDownloadState.Linked, (await admission.Completion.WaitAsync(TestWait.Timeout)).State);
        Assert.Equal(2, cdn.Requests.Count);
    }

    [Fact]
    public async Task StopDuringAnAdmissionWaitEndsTheAdmissionWithoutRecordingARequest()
    {
        using TempDirectory directory = new();
        FakeCdn cdn = new();
        BooruPostClaim first = Post("111", Bytes(23), cdn);
        BooruPostClaim second = Post("112", Bytes(24), cdn);
        await using Session session = new(directory.Path, cdn, maxPendingDownloads: 1);
        await session.Queue.SetPausedAsync(true);
        BooruOriginalAdmission held = await session.Booru.AdmitAsync(first, BooruFindingKind.OneShot);
        Task<BooruOriginalAdmission> waiting = session.Booru.AdmitAsync(second, BooruFindingKind.OneShot);
        await TestWait.UntilAsync(() => session.Booru.ActiveCount == 2);
        Assert.False(waiting.IsCompleted);

        await session.Queue.StopAllAsync().WaitAsync(TestWait.Timeout);

        // The external admission task ends instead of waiting forever or faulting.
        BooruOriginalAdmission refused = await waiting.WaitAsync(TestWait.Timeout);
        Assert.Equal(BooruOriginalAdmissionStatus.NotAdmitted, refused.Status);
        Assert.Equal(BooruOriginalDownloadState.NotAdmitted, (await refused.Completion.WaitAsync(TestWait.Timeout)).State);
        Assert.Equal(BooruOriginalDownloadState.Interrupted, (await held.Completion.WaitAsync(TestWait.Timeout)).State);
        Dictionary<string, BooruOriginalIntent> intents = session.Catalog.Read().Posts.ToDictionary(item => item.PostId, item => item.OriginalIntent);
        // Requested only for the job the queue really accepted.
        Assert.Equal(BooruOriginalIntent.Requested, intents["111"]);
        Assert.Equal(BooruOriginalIntent.NotAdmitted, intents["112"]);
        Assert.Equal(0, session.Booru.ActiveCount);
        Assert.Empty(cdn.Requests);
    }

    [Fact]
    public async Task StopDuringRecoveryKeepsEveryRecoveredRequestPending()
    {
        using TempDirectory directory = new();
        FakeCdn cdn = new();
        BooruPostClaim first = Post("121", Bytes(25), cdn);
        BooruPostClaim second = Post("122", Bytes(26), cdn);
        await using (Session session = new(directory.Path, cdn))
        {
            await session.Queue.SetPausedAsync(true);
            await session.Booru.AdmitAsync(first, BooruFindingKind.OneShot);
            await session.Booru.AdmitAsync(second, BooruFindingKind.OneShot);
        }

        await using (Session interrupted = new(directory.Path, cdn, maxPendingDownloads: 1))
        {
            await interrupted.Queue.SetPausedAsync(true);
            Task<IReadOnlyList<BooruOriginalAdmission>> recovery = interrupted.Booru.RecoverPendingAsync();
            await TestWait.UntilAsync(() => interrupted.Booru.ActiveCount == 2);
            await interrupted.Queue.StopAllAsync().WaitAsync(TestWait.Timeout);

            IReadOnlyList<BooruOriginalAdmission> admissions = await recovery.WaitAsync(TestWait.Timeout);
            Assert.Equal(2, admissions.Count);
            foreach (BooruOriginalAdmission admission in admissions)
                Assert.Equal(BooruOriginalDownloadState.Interrupted, (await admission.Completion.WaitAsync(TestWait.Timeout)).State);
            // A stop during startup never downgrades a recovered request to a refusal.
            Assert.Equal(2, interrupted.Booru.GetPendingOriginals().Count);
        }
        Assert.Empty(cdn.Requests);

        await using Session reopened = new(directory.Path, cdn);
        IReadOnlyList<BooruOriginalAdmission> recovered = await reopened.Booru.RecoverPendingAsync();
        Assert.Equal(2, recovered.Count);
        foreach (BooruOriginalAdmission admission in recovered)
            Assert.Equal(BooruOriginalDownloadState.Linked, (await admission.Completion.WaitAsync(TestWait.Timeout)).State);
    }

    [Fact]
    public async Task CancelDuringAnAdmissionWaitIsDurableAndNeverRecovered()
    {
        using TempDirectory directory = new();
        FakeCdn cdn = new();
        BooruPostClaim first = Post("131", Bytes(27), cdn);
        BooruPostClaim second = Post("132", Bytes(28), cdn);
        await using (Session session = new(directory.Path, cdn, maxPendingDownloads: 1))
        {
            await session.Queue.SetPausedAsync(true);
            await session.Booru.AdmitAsync(first, BooruFindingKind.OneShot);
            Task<BooruOriginalAdmission> waiting = session.Booru.AdmitAsync(second, BooruFindingKind.OneShot);
            await TestWait.UntilAsync(() => session.Booru.ActiveCount == 2);

            Assert.True(await session.Booru.CancelAsync("132").WaitAsync(TestWait.Timeout));
            BooruOriginalAdmission refused = await waiting.WaitAsync(TestWait.Timeout);
            Assert.Equal(BooruOriginalAdmissionStatus.NotAdmitted, refused.Status);
            Assert.Equal(BooruOriginalDownloadState.Cancelled, (await refused.Completion.WaitAsync(TestWait.Timeout)).State);
            Assert.Equal(BooruOriginalIntent.Cancelled,
                session.Catalog.Read().Posts.Single(item => item.PostId == "132").OriginalIntent);
        }

        await using Session reopened = new(directory.Path, cdn);
        BooruOriginalAdmission recovered = Assert.Single(await reopened.Booru.RecoverPendingAsync());
        Assert.Equal("131", recovered.PostId);
        await recovered.Completion.WaitAsync(TestWait.Timeout);
        Assert.DoesNotContain(cdn.Requests, url => url.Contains("/132.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancelOvertakingTheQueueAcceptIsNeverOverwrittenByRequested()
    {
        using TempDirectory directory = new();
        FakeCdn cdn = new();
        BooruPostClaim post = Post("141", Bytes(29), cdn);
        await using (Session session = new(directory.Path, cdn))
        {
            await session.Queue.SetPausedAsync(true);
            bool cancelled = false;
            session.Booru.AfterQueueAcceptedForTests = async postId => cancelled = await session.Booru.CancelAsync(postId);

            BooruOriginalAdmission admission = await session.Booru.AdmitAsync(post, BooruFindingKind.OneShot);

            Assert.True(cancelled);
            Assert.Equal(BooruOriginalDownloadState.Cancelled, (await admission.Completion.WaitAsync(TestWait.Timeout)).State);
            Assert.Equal(BooruOriginalIntent.Cancelled, Assert.Single(session.Catalog.Read().Posts).OriginalIntent);
            Assert.Empty(session.Booru.GetPendingOriginals());
        }

        await using Session reopened = new(directory.Path, cdn);
        Assert.Empty(await reopened.Booru.RecoverPendingAsync());
        Assert.Empty(cdn.Requests);
    }

    [Fact]
    public async Task ALinkThatCompletesBeforeRequestedIsRecordedNeitherFaultsNorRegresses()
    {
        using TempDirectory directory = new();
        FakeCdn cdn = new();
        BooruPostClaim post = Post("151", Bytes(30), cdn);
        await using Session session = new(directory.Path, cdn);
        session.Booru.AfterQueueAcceptedForTests = postId => TestWait.UntilAsync(() =>
            session.Booru.Snapshot().TryGetValue(postId, out BooruOriginalDownloadOutcome? outcome)
            && outcome.State == BooruOriginalDownloadState.Linked);

        BooruOriginalAdmission admission = await session.Booru.AdmitAsync(post, BooruFindingKind.OneShot);

        Assert.Equal(BooruOriginalAdmissionStatus.Queued, admission.Status);
        Assert.Equal(BooruOriginalDownloadState.Linked, (await admission.Completion.WaitAsync(TestWait.Timeout)).State);
        BooruPostRecord record = Assert.Single(session.Catalog.Read().Posts);
        Assert.Equal(BooruOriginalIntent.Linked, record.OriginalIntent);
        Assert.NotNull(record.LocalAssetId);
        Assert.False(await session.Booru.CancelAsync("151"));
        Assert.Equal(BooruOriginalIntent.Linked, Assert.Single(session.Catalog.Read().Posts).OriginalIntent);
    }

    [Fact]
    public async Task CancelOfAStoredRequestWithoutARuntimeOwnerIsDurable()
    {
        using TempDirectory directory = new();
        FakeCdn cdn = new();
        BooruPostClaim post = Post("161", Bytes(31), cdn);
        await using (Session session = new(directory.Path, cdn))
        {
            await session.Queue.SetPausedAsync(true);
            await session.Booru.AdmitAsync(post, BooruFindingKind.OneShot);
        }

        await using (Session restarted = new(directory.Path, cdn))
        {
            // Before recovery ran there is no runtime owner, but the stored request is cancelled anyway.
            Assert.Single(restarted.Booru.GetPendingOriginals());
            Assert.True(await restarted.Booru.CancelAsync("161"));
            Assert.Equal(BooruOriginalDownloadState.Cancelled, restarted.Booru.Snapshot()["161"].State);
            Assert.Empty(restarted.Booru.GetPendingOriginals());
            Assert.False(await restarted.Booru.CancelAsync("161"));
        }

        await using Session reopened = new(directory.Path, cdn);
        Assert.Empty(await reopened.Booru.RecoverPendingAsync());
        Assert.Equal(BooruOriginalIntent.Cancelled, Assert.Single(reopened.Catalog.Read().Posts).OriginalIntent);
        Assert.Empty(cdn.Requests);
    }

    [Fact]
    public async Task CancelAfterAFailedTransferStopsTheRequestFromBeingRetriedOnRestart()
    {
        using TempDirectory directory = new();
        byte[] bytes = Bytes(32);
        FakeCdn cdn = new();
        BooruPostClaim post = Post("171", bytes, cdn);
        cdn.Responses[OriginalUrl("171")] = new FakeResponse(bytes.Select(value => (byte)(value ^ 0x33)).ToArray());
        await using (Session session = new(directory.Path, cdn))
        {
            BooruOriginalAdmission admission = await session.Booru.AdmitAsync(post, BooruFindingKind.OneShot);
            Assert.Equal(BooruOriginalDownloadState.DownloadFailed, (await admission.Completion.WaitAsync(TestWait.Timeout)).State);
            Assert.Single(session.Booru.GetPendingOriginals());

            Assert.True(await session.Booru.CancelAsync("171"));
            Assert.Empty(session.Booru.GetPendingOriginals());
        }

        await using Session reopened = new(directory.Path, cdn);
        Assert.Empty(await reopened.Booru.RecoverPendingAsync());
        Assert.Single(cdn.Requests);
    }

    [Fact]
    public async Task RecoverySkipsAPostThatWasCancelledAfterThePendingScan()
    {
        using TempDirectory directory = new();
        FakeCdn cdn = new();
        BooruPostClaim first = Post("181", Bytes(33), cdn);
        BooruPostClaim second = Post("182", Bytes(34), cdn);
        await using (Session session = new(directory.Path, cdn))
        {
            await session.Queue.SetPausedAsync(true);
            await session.Booru.AdmitAsync(first, BooruFindingKind.OneShot);
            await session.Booru.AdmitAsync(second, BooruFindingKind.OneShot);
        }

        await using Session reopened = new(directory.Path, cdn);
        await reopened.Queue.SetPausedAsync(true);
        Assert.Equal(2, reopened.Booru.GetPendingOriginals().Count);
        // While recovery admits the first post, the user cancels the second one (no runtime owner yet).
        bool cancelled = false;
        reopened.Booru.AfterQueueAcceptedForTests = async postId =>
        {
            if (postId == "181") cancelled = await reopened.Booru.CancelAsync("182");
        };

        BooruOriginalAdmission recovered = Assert.Single(await reopened.Booru.RecoverPendingAsync());

        Assert.True(cancelled);
        Assert.Equal("181", recovered.PostId);
        Assert.Equal(BooruOriginalIntent.Cancelled,
            reopened.Catalog.Read().Posts.Single(item => item.PostId == "182").OriginalIntent);
        Assert.Equal(1, reopened.Booru.ActiveCount);
        await reopened.Queue.SetPausedAsync(false);
        Assert.Equal(BooruOriginalDownloadState.Linked, (await recovered.Completion.WaitAsync(TestWait.Timeout)).State);
        Assert.Equal([OriginalUrl("181")], cdn.Requests.ToArray());
    }

    [Fact]
    public async Task AWatcherFindingNeverReAdmitsACancelledOriginalButAnExplicitRequestDoes()
    {
        using TempDirectory directory = new();
        FakeCdn cdn = new();
        BooruPostClaim post = Post("191", Bytes(35), cdn);
        await using Session session = new(directory.Path, cdn);
        await session.Queue.SetPausedAsync(true);
        await session.Booru.AdmitAsync(post, BooruFindingKind.OneShot);
        Assert.True(await session.Booru.CancelAsync("191"));

        BooruOriginalAdmission watcher = await session.Booru.AdmitAsync(post, BooruFindingKind.Watcher, "w1", "cat");
        Assert.Equal(BooruOriginalAdmissionStatus.NotAdmitted, watcher.Status);
        Assert.Equal(BooruOriginalDownloadState.Cancelled, (await watcher.Completion).State);
        BooruPostRecord record = Assert.Single(session.Catalog.Read().Posts);
        Assert.Equal(BooruOriginalIntent.Cancelled, record.OriginalIntent);
        // Provenance is still complete: the watcher finding is stored.
        Assert.Contains(record.Findings, finding => finding.Kind == BooruFindingKind.Watcher);
        Assert.Equal(0, session.Booru.ActiveCount);

        BooruOriginalAdmission explicitRequest = await session.Booru.AdmitAsync(post, BooruFindingKind.OneShot);
        Assert.Equal(BooruOriginalAdmissionStatus.Queued, explicitRequest.Status);
        await session.Queue.SetPausedAsync(false);
        Assert.Equal(BooruOriginalDownloadState.Linked, (await explicitRequest.Completion.WaitAsync(TestWait.Timeout)).State);
    }

    [Fact]
    public async Task CancellationOfAQueuedJobIsNotifiedOutsideTheQueueStateLock()
    {
        using TempDirectory directory = new();
        FakeCdn cdn = new();
        BooruPostClaim post = Post("201", Bytes(36), cdn);
        await using Session session = new(directory.Path, cdn);
        await session.Queue.SetPausedAsync(true);
        BooruOriginalAdmission admission = await session.Booru.AdmitAsync(post, BooruFindingKind.OneShot);
        bool? queueReachable = null;
        session.Booru.OutcomeChanged += outcome =>
        {
            if (outcome.State != BooruOriginalDownloadState.Cancelled) return;
            // Another thread must be able to take the queue state lock while the owner is notified.
            queueReachable ??= Task.Run(() => session.Queue.IsPaused).Wait(TimeSpan.FromSeconds(2));
        };

        Assert.True(await session.Booru.CancelAsync("201").WaitAsync(TestWait.Timeout));

        Assert.Equal(BooruOriginalDownloadState.Cancelled, (await admission.Completion.WaitAsync(TestWait.Timeout)).State);
        Assert.True(queueReachable);
        Assert.Equal(BooruOriginalIntent.Cancelled, Assert.Single(session.Catalog.Read().Posts).OriginalIntent);
    }
    [Fact]
    public async Task AcknowledgementCannotResurrectCancellationAfterACompletedFailure()
    {
        using TempDirectory directory = new();
        FakeCdn cdn = new();
        BooruPostClaim post = Post("211", Bytes(37), cdn);
        // The queue accepts the item, but the transfer fails before the admission acknowledgement is released.
        cdn.Responses[OriginalUrl("211")] = new FakeResponse(Bytes(37).Select(value => (byte)(value ^ 0x51)).ToArray());
        TaskCompletionSource<bool> accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using Session session = new(directory.Path, cdn);
        session.Booru.AfterQueueAcceptedForTests = async _ =>
        {
            accepted.TrySetResult(true);
            await release.Task.WaitAsync(TestWait.Timeout);
        };

        Task<BooruOriginalAdmission> admissionTask = session.Booru.AdmitAsync(post, BooruFindingKind.OneShot);
        await accepted.Task.WaitAsync(TestWait.Timeout);
        await TestWait.UntilAsync(() => session.Booru.Snapshot().TryGetValue("211", out BooruOriginalDownloadOutcome? outcome)
            && outcome.State == BooruOriginalDownloadState.DownloadFailed);
        Assert.True(await session.Booru.CancelAsync("211").WaitAsync(TestWait.Timeout));
        Assert.Equal(BooruOriginalIntent.Cancelled,
            session.Catalog.Read().Posts.Single(item => item.PostId == "211").OriginalIntent);

        release.TrySetResult(true);
        BooruOriginalAdmission admission = await admissionTask.WaitAsync(TestWait.Timeout);
        Assert.Equal(BooruOriginalAdmissionStatus.Queued, admission.Status);
        Assert.Equal(BooruOriginalDownloadState.DownloadFailed,
            (await admission.Completion.WaitAsync(TestWait.Timeout)).State);
        Assert.Equal(BooruOriginalIntent.Cancelled,
            session.Catalog.Read().Posts.Single(item => item.PostId == "211").OriginalIntent);
    }

    [Fact]
    public async Task StoredCancellationCanSynchronouslyStartAnExplicitRetry()
    {
        using TempDirectory directory = new();
        FakeCdn cdn = new();
        BooruPostClaim post = Post("212", Bytes(38), cdn);
        await using (Session initial = new(directory.Path, cdn))
        {
            await initial.Queue.SetPausedAsync(true);
            await initial.Booru.AdmitAsync(post, BooruFindingKind.OneShot);
        }

        await using Session session = new(directory.Path, cdn);
        await session.Queue.SetPausedAsync(true);
        Task<BooruOriginalAdmission>? retryTask = null;
        session.Booru.OutcomeChanged += outcome =>
        {
            if (outcome.PostId == "212" && outcome.State == BooruOriginalDownloadState.Cancelled)
                retryTask = session.Booru.RetryAsync("212");
        };

        Assert.True(await session.Booru.CancelAsync("212").WaitAsync(TestWait.Timeout));
        BooruOriginalAdmission retry = await (retryTask ?? throw new InvalidOperationException("Retry callback was not raised."))
            .WaitAsync(TestWait.Timeout);
        Assert.Equal(BooruOriginalAdmissionStatus.Queued, retry.Status);
        await session.Queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        Assert.Equal(BooruOriginalDownloadState.Interrupted,
            (await retry.Completion.WaitAsync(TestWait.Timeout)).State);
    }

    [Fact]
    public async Task AnAcknowledgementThatCannotEnterTheProfileStillReleasesThePostForALaterRetry()
    {
        using TempDirectory directory = new();
        FakeCdn cdn = new();
        byte[] bytes = Bytes(39);
        BooruPostClaim post = Post("401", bytes, cdn);
        await using Session session = new(directory.Path, cdn);
        await session.Queue.SetPausedAsync(true);
        ProfileAdmission profile = session.Catalog.ProfileLease.Admission;
        using CancellationTokenSource abandonExclusive = new();
        ProfileAdmissionToken? blocker = null;
        Task<ProfileAdmissionToken>? exclusive = null;
        // A private-store commit reserves the exclusive slot (pending behind an active writer) while the queue
        // accept returns, so the acknowledgement cannot enter a store writer.
        session.Booru.AfterQueueAcceptedForTests = _ =>
        {
            blocker = profile.EnterOrdinaryWriter();
            exclusive = profile.EnterExclusiveAsync(abandonExclusive.Token).AsTask();
            return Task.CompletedTask;
        };

        BooruOriginalAdmission admission;
        try
        {
            admission = await session.Booru.AdmitAsync(post, BooruFindingKind.OneShot).WaitAsync(TestWait.Timeout);
            Assert.False(exclusive!.IsCompleted);
        }
        finally
        {
            session.Booru.AfterQueueAcceptedForTests = null;
            abandonExclusive.Cancel();
            if (exclusive is not null)
            {
                try { (await exclusive.WaitAsync(TestWait.Timeout)).Dispose(); }
                catch (OperationCanceledException) { }
            }
            blocker?.Dispose();
        }
        // The queue accepted the job and the write-ahead Requested is durable: this is an admission, not a fault.
        Assert.Equal(BooruOriginalAdmissionStatus.Queued, admission.Status);
        Assert.Equal(BooruOriginalIntent.Requested, Assert.Single(session.Catalog.Read().Posts).OriginalIntent);

        cdn.Responses[OriginalUrl("401")] = new FakeResponse(bytes.Select(value => (byte)(value ^ 0x51)).ToArray());
        await session.Queue.SetPausedAsync(false);
        Assert.Equal(BooruOriginalDownloadState.DownloadFailed, (await admission.Completion.WaitAsync(TestWait.Timeout)).State);

        // Before the fix the accepted owner stayed registered and every retry answered AlreadyQueued until a restart.
        cdn.Responses[OriginalUrl("401")] = new FakeResponse(bytes);
        BooruOriginalAdmission retry = await session.Booru.RetryAsync("401").WaitAsync(TestWait.Timeout);
        Assert.Equal(BooruOriginalAdmissionStatus.Queued, retry.Status);
        Assert.Equal(BooruOriginalDownloadState.Linked, (await retry.Completion.WaitAsync(TestWait.Timeout)).State);
    }

}

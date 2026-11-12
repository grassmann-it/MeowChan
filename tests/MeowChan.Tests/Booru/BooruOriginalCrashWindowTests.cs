using MeowChan.Archive;
using MeowChan.Booru;
using TempDirectory = MeowChan.Tests.TempDirectory;

namespace MeowChan.Tests.Booru;

/// <summary>
/// B4 crash recovery: a process crash is simulated by copying the profile and media directories at an exact
/// point of the admission and reopening the copy, so nothing the live session writes afterwards (stop,
/// refusal, acknowledgement) reaches the "crashed" state. Synthetic fixtures and the in-memory CDN only.
/// </summary>
public sealed partial class BooruOriginalQueueTests
{
    [Theory]
    [InlineData((int)BooruFindingKind.OneShot)]
    [InlineData((int)BooruFindingKind.Search)]
    [InlineData((int)BooruFindingKind.Watcher)]
    public async Task ACrashBetweenTheQueueAcceptAndItsAcknowledgementKeepsTheRequestRecoverable(int findingKind)
    {
        BooruFindingKind kind = (BooruFindingKind)findingKind;
        using TempDirectory directory = new();
        using TempDirectory crashed = new();
        FakeCdn cdn = new();
        BooruPostClaim post = Post("301", Bytes(41), cdn);
        await using (Session session = new(directory.Path, cdn))
        {
            await session.Queue.SetPausedAsync(true);
            session.Booru.AfterQueueAcceptedForTests = _ =>
            {
                CrashCopy(directory.Path, crashed.Path);
                return Task.CompletedTask;
            };
            BooruOriginalAdmission admission = await session.Booru.AdmitAsync(post, kind,
                kind == BooruFindingKind.Watcher ? "w1" : null, "cat");
            Assert.Equal(BooruOriginalAdmissionStatus.Queued, admission.Status);
        }
        Assert.Empty(cdn.Requests);

        await using Session reopened = new(crashed.Path, cdn);
        BooruPostRecord pending = Assert.Single(reopened.Booru.GetPendingOriginals());
        Assert.Equal("301", pending.PostId);
        Assert.Contains(pending.Findings, finding => finding.Kind == kind);
        BooruOriginalAdmission recovered = Assert.Single(await reopened.Booru.RecoverPendingAsync());
        Assert.Equal(BooruOriginalDownloadState.Linked, (await recovered.Completion.WaitAsync(TestWait.Timeout)).State);
        Assert.Equal([OriginalUrl("301")], cdn.Requests.ToArray());
    }

    [Fact]
    public async Task TheVisibleQueuedStateIsDurableBeforeTheQueueIsOffered()
    {
        using TempDirectory directory = new();
        using TempDirectory crashed = new();
        FakeCdn cdn = new();
        BooruPostClaim post = Post("311", Bytes(42), cdn);
        await using (Session session = new(directory.Path, cdn))
        {
            await session.Queue.SetPausedAsync(true);
            bool copied = false;
            session.Booru.OutcomeChanged += outcome =>
            {
                // Raised before EnqueueAsync: the crash happens before the queue even saw the job.
                if (outcome.State != BooruOriginalDownloadState.Queued || copied) return;
                copied = true;
                CrashCopy(directory.Path, crashed.Path);
            };
            await session.Booru.AdmitAsync(post, BooruFindingKind.OneShot);
            Assert.True(copied);
        }

        await using Session reopened = new(crashed.Path, cdn);
        Assert.Equal(BooruOriginalIntent.Requested, Assert.Single(reopened.Catalog.Read().Posts).OriginalIntent);
        BooruOriginalAdmission recovered = Assert.Single(await reopened.Booru.RecoverPendingAsync());
        Assert.Equal(BooruOriginalDownloadState.Linked, (await recovered.Completion.WaitAsync(TestWait.Timeout)).State);
        Assert.Single(cdn.Requests);
    }

    [Fact]
    public async Task ACrashAfterAFastFailureBeforeTheAcknowledgementKeepsTheRequestForRecovery()
    {
        using TempDirectory directory = new();
        using TempDirectory crashed = new();
        byte[] bytes = Bytes(43);
        FakeCdn cdn = new();
        BooruPostClaim post = Post("321", bytes, cdn);
        cdn.Responses[OriginalUrl("321")] = new FakeResponse(bytes.Select(value => (byte)(value ^ 0x5a)).ToArray());
        await using (Session session = new(directory.Path, cdn))
        {
            session.Booru.AfterQueueAcceptedForTests = async postId =>
            {
                // The damaged transfer already failed closed while the acknowledgement is still outstanding.
                await TestWait.UntilAsync(() => session.Booru.Snapshot().TryGetValue(postId, out BooruOriginalDownloadOutcome? outcome)
                    && outcome.State == BooruOriginalDownloadState.DownloadFailed);
                CrashCopy(directory.Path, crashed.Path);
            };
            BooruOriginalAdmission admission = await session.Booru.AdmitAsync(post, BooruFindingKind.OneShot);
            Assert.Equal(BooruOriginalDownloadState.DownloadFailed, (await admission.Completion.WaitAsync(TestWait.Timeout)).State);
        }
        Assert.Single(cdn.Requests);
        Assert.False(File.Exists(Path.Combine(crashed.Path, "media", "danbooru", "321", "321.png")));

        cdn.Responses[OriginalUrl("321")] = new FakeResponse(bytes);
        await using Session reopened = new(crashed.Path, cdn);
        BooruOriginalAdmission recovered = Assert.Single(await reopened.Booru.RecoverPendingAsync());
        Assert.Equal(BooruOriginalDownloadState.Linked, (await recovered.Completion.WaitAsync(TestWait.Timeout)).State);
        Assert.Equal(2, cdn.Requests.Count);
    }

    [Fact]
    public async Task ACrashDuringAnExplicitRetryOfACancelledOriginalKeepsTheRetry()
    {
        using TempDirectory directory = new();
        using TempDirectory crashed = new();
        FakeCdn cdn = new();
        BooruPostClaim post = Post("331", Bytes(44), cdn);
        await using (Session session = new(directory.Path, cdn))
        {
            await session.Queue.SetPausedAsync(true);
            await session.Booru.AdmitAsync(post, BooruFindingKind.OneShot);
            Assert.True(await session.Booru.CancelAsync("331"));
            session.Booru.AfterQueueAcceptedForTests = _ =>
            {
                CrashCopy(directory.Path, crashed.Path);
                return Task.CompletedTask;
            };
            Assert.Equal(BooruOriginalAdmissionStatus.Queued, (await session.Booru.RetryAsync("331")).Status);
        }

        await using Session reopened = new(crashed.Path, cdn);
        // The user's later explicit retry is the durable decision, not the earlier cancel.
        Assert.Equal(BooruOriginalIntent.Requested, Assert.Single(reopened.Catalog.Read().Posts).OriginalIntent);
        BooruOriginalAdmission recovered = Assert.Single(await reopened.Booru.RecoverPendingAsync());
        Assert.Equal(BooruOriginalDownloadState.Linked, (await recovered.Completion.WaitAsync(TestWait.Timeout)).State);
        Assert.Single(cdn.Requests);
    }

    [Fact]
    public async Task AnExplicitRequestAfterACancelSurvivesAQueueStopAndIsRecovered()
    {
        using TempDirectory directory = new();
        FakeCdn cdn = new();
        BooruPostClaim post = Post("341", Bytes(45), cdn);
        await using (Session session = new(directory.Path, cdn))
        {
            await session.Queue.SetPausedAsync(true);
            await session.Booru.AdmitAsync(post, BooruFindingKind.OneShot);
            Assert.True(await session.Booru.CancelAsync("341"));

            BooruOriginalAdmission again = await session.Booru.AdmitAsync(post, BooruFindingKind.OneShot);
            Assert.Equal(BooruOriginalAdmissionStatus.Queued, again.Status);
            // The explicit request overrides the earlier cancel durably, not only in memory.
            Assert.Equal(BooruOriginalIntent.Requested, Assert.Single(session.Catalog.Read().Posts).OriginalIntent);

            await session.Queue.StopAllAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(BooruOriginalDownloadState.Interrupted, (await again.Completion.WaitAsync(TestWait.Timeout)).State);
        }

        await using Session reopened = new(directory.Path, cdn);
        BooruOriginalAdmission recovered = Assert.Single(await reopened.Booru.RecoverPendingAsync());
        Assert.Equal(BooruOriginalDownloadState.Linked, (await recovered.Completion.WaitAsync(TestWait.Timeout)).State);
        Assert.Single(cdn.Requests);
    }

    [Fact]
    public async Task AWatcherFindingForACancelledOriginalWritesNoWriteAheadRequest()
    {
        using TempDirectory directory = new();
        using TempDirectory crashed = new();
        FakeCdn cdn = new();
        BooruPostClaim post = Post("351", Bytes(46), cdn);
        await using (Session session = new(directory.Path, cdn))
        {
            await session.Queue.SetPausedAsync(true);
            await session.Booru.AdmitAsync(post, BooruFindingKind.OneShot);
            Assert.True(await session.Booru.CancelAsync("351"));
            BooruOriginalAdmission watcher = await session.Booru.AdmitAsync(post, BooruFindingKind.Watcher, "w1", "cat");
            Assert.Equal(BooruOriginalAdmissionStatus.NotAdmitted, watcher.Status);
            CrashCopy(directory.Path, crashed.Path);
        }

        await using Session reopened = new(crashed.Path, cdn);
        Assert.Equal(BooruOriginalIntent.Cancelled, Assert.Single(reopened.Catalog.Read().Posts).OriginalIntent);
        Assert.Empty(await reopened.Booru.RecoverPendingAsync());
        Assert.Empty(cdn.Requests);
    }

    [Fact]
    public async Task ARefusedRequestReplacesItsWriteAheadAndIsNeverRecovered()
    {
        using TempDirectory directory = new();
        FakeCdn cdn = new();
        BooruPostClaim first = Post("361", Bytes(47), cdn);
        BooruPostClaim second = Post("362", Bytes(48), cdn);
        await using (Session session = new(directory.Path, cdn, maxPendingDownloads: 1))
        {
            await session.Queue.SetPausedAsync(true);
            await session.Booru.AdmitAsync(first, BooruFindingKind.OneShot);
            Task<BooruOriginalAdmission> waiting = session.Booru.AdmitAsync(second, BooruFindingKind.OneShot);
            await TestWait.UntilAsync(() => session.Booru.ActiveCount == 2);
            // While the second admission waits for capacity its write-ahead request is durable.
            Assert.Equal(BooruOriginalIntent.Requested,
                session.Catalog.Read().Posts.Single(item => item.PostId == "362").OriginalIntent);

            await session.Queue.StopAllAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(BooruOriginalAdmissionStatus.NotAdmitted, (await waiting.WaitAsync(TestWait.Timeout)).Status);
            Assert.Equal(BooruOriginalIntent.NotAdmitted,
                session.Catalog.Read().Posts.Single(item => item.PostId == "362").OriginalIntent);
        }

        await using Session reopened = new(directory.Path, cdn);
        BooruOriginalAdmission recovered = Assert.Single(await reopened.Booru.RecoverPendingAsync());
        Assert.Equal("361", recovered.PostId);
        await recovered.Completion.WaitAsync(TestWait.Timeout);
        Assert.DoesNotContain(cdn.Requests, url => url.Contains("/362.", StringComparison.Ordinal));
    }

    /// <summary>
    /// Copies the durable state as a crash would leave it. The profile lock is not copied: a crashed process
    /// holds no lock, and the copy is reopened by a new lease.
    /// </summary>
    private static void CrashCopy(string source, string target)
    {
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(file);
            if (name is ".meowchan-profile.lock" or ".ochan-profile.lock") continue;
            string destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }
}

using System.Security.Cryptography;
using MeowChan.Admission;
using MeowChan.Persistence;

namespace MeowChan.Tests;

public sealed class WorkStateProducerAdmissionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private const string Url = "https://stub.test/g/thread/42";
    private static readonly DateTimeOffset Observed = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("pending")]
    [InlineData("active")]
    [InlineData("sealed")]
    [InlineData("failed")]
    public async Task EveryDirectWriterAndNoopRefusesClosedAdmissionWithoutChangingFiles(string phase)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore work = new(directory.Path, lease);
        SessionStore session = new(directory.Path, lease);
        BoardAdmissionStore policy = new(directory.Path, lease);
        WorkResource resource = Resource(directory.Path);
        MediaWork media = Media(directory.Path);
        work.CommitIntent(resource);
        Assert.True(work.CommitMedia(media));
        await session.SaveAsync(new());
        policy.Save("https://stub.test/g/", BoardAdmissionPolicy.All);
        IConditionalWorkStateStore conditional = work;
        WorkStateAccess access = WorkStateAccess.For(work);
        ProfileAdmissionToken? held = null;
        Task<ProfileAdmissionToken>? pending = null;
        ProfileAdmissionToken? exclusive = null;
        using CancellationTokenSource cancellation = new();
        try
        {
            if (phase == "failed") lease.Admission.FailStartup();
            else
            {
                lease.Admission.CompleteStartup();
                if (phase == "sealed") lease.Admission.BeginShutdown();
                else
                {
                    held = lease.Admission.EnterOrdinaryWriter();
                    pending = lease.Admission.EnterExclusiveAsync(cancellation.Token).AsTask();
                    Assert.False(pending.IsCompleted);
                    if (phase == "active")
                    {
                        held.Dispose();
                        held = null;
                        exclusive = await pending.WaitAsync(Deadline);
                    }
                }
            }
            Dictionary<string, string> before = Inventory(directory.Path);
            // Even stale-generation and identical-value writes must validate before
            // opening SQLite or returning their otherwise harmless noop result.
            Action[] writers =
            [
                () => _ = new WorkStateStore(directory.Path, lease),
                () => work.CommitIntent(resource with { Generation = 2 }),
                () => work.CommitState(resource),
                () => work.CommitState(resource with { Generation = 99 }),
                () => work.CommitEnd(Url, 99, ThreadSourceStateValue.NotFound, Observed),
                () => work.CommitMedia(media with { Generation = 99 }),
                () => conditional.TryCommitMedia(media, media),
                () => conditional.CommitIntentUnderFence(resource with { Generation = 2 }),
                () => conditional.CommitStateUnderFence(resource),
                () => conditional.CommitEndUnderFence(Url, 99, ThreadSourceStateValue.NotFound, Observed, null),
                () => access.TryCommitMedia(media, media),
                () => access.CommitIntentUnderFence(resource with { Generation = 2 }),
                () => access.CommitStateUnderFence(resource),
                () => access.CommitEndUnderFence(Url, 99, ThreadSourceStateValue.NotFound, Observed, null),
                () => policy.Save("https://stub.test/g/", BoardAdmissionPolicy.All)
            ];
            foreach (Action writer in writers) Assert.Throws<InvalidOperationException>(writer);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.SaveAsync(new()));
            Assert.Equal(resource, Assert.Single(work.LoadResources()));
            Assert.Equal(media, Assert.Single(work.LoadMedia(Url, 1)));
            Assert.Equal(resource, conditional.FindResource(Url));
            Assert.Equal(media, conditional.FindMedia(MediaWorkKey.Of(media)));
            await session.LoadAsync().WaitAsync(Deadline);
            _ = new BoardAdmissionStore(directory.Path, lease).Get("https://stub.test/g/");
            Assert.Equal(before.OrderBy(pair => pair.Key), Inventory(directory.Path).OrderBy(pair => pair.Key));
        }
        finally
        {
            cancellation.Cancel();
            held?.Dispose();
            exclusive?.Dispose();
            if (pending != null)
            {
                try { (await pending.WaitAsync(Deadline)).Dispose(); }
                catch (OperationCanceledException) { }
            }
        }
    }

    [Fact]
    public async Task ExactPreviouslyAcceptedTokenCompletesRealWritesWhileExclusiveIsPendingAndAfterSeal()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore work = new(directory.Path, lease);
        SessionStore session = new(directory.Path, lease);
        BoardAdmissionStore policy = new(directory.Path, lease);
        lease.Admission.CompleteStartup();
        using ProfileAdmissionToken accepted = lease.Admission.EnterOrdinaryWriter();
        using CancellationTokenSource cancellation = new();
        Task<ProfileAdmissionToken> pending = lease.Admission.EnterExclusiveAsync(cancellation.Token).AsTask();
        try
        {
            WorkResource resource = Resource(directory.Path);
            _ = new WorkStateStore(directory.Path, lease, accepted);
            Assert.Equal(resource, work.CommitIntentAdmitted(resource, accepted));
            Assert.True(work.CommitMediaAdmitted(Media(directory.Path), accepted));
            WorkStateAccess access = WorkStateAccess.For(work);
            using (access.Enter(Url))
            {
                Assert.True(access.TryCommitMedia(Media(directory.Path) with { Attempts = 1 }, Media(directory.Path), accepted));
                Assert.NotNull(access.CommitEndUnderFence(Url, 1, ThreadSourceStateValue.NotFound, Observed, null, accepted));
            }
            WorkResource ended = Assert.Single(work.LoadResources());
            Assert.Equal(Observed, ended.EndObservedAt);
            Assert.NotNull(work.CommitStateAdmitted(ended with { CapturePending = false }, accepted));
            await session.SaveAdmittedAsync(new() { ThreadUrls = [Url] }, accepted).WaitAsync(Deadline);
            policy.SaveAdmitted("https://stub.test/g/", BoardAdmissionPolicy.All, accepted);
            Assert.False(pending.IsCompleted);
            lease.Admission.BeginShutdown();
            await Assert.ThrowsAsync<InvalidOperationException>(() => pending.WaitAsync(Deadline));
            Task drain = lease.Admission.DrainAsync();
            Assert.False(drain.IsCompleted);
            // Accepted completion is still authorized after seal; no new Ordinary.
            Assert.True(work.CommitMediaAdmitted(Media(directory.Path) with { Status = MediaWorkStatus.Succeeded }, accepted));
            await session.SaveAdmittedAsync(new() { SuppressedThreadUrls = [Url] }, accepted).WaitAsync(Deadline);
            Assert.False(drain.IsCompleted);
            accepted.Dispose();
            await drain.WaitAsync(Deadline);
            Assert.Throws<InvalidOperationException>(() => work.CommitStateAdmitted(ended, accepted));
        }
        finally
        {
            cancellation.Cancel();
            accepted.Dispose();
            try { (await pending.WaitAsync(Deadline)).Dispose(); }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException) when (lease.Admission.Phase == ProfileAdmissionPhase.Stopping) { }
        }
    }

    [Fact]
    public async Task ForeignUnissuedReleasedAndExclusiveTokensNeverAuthorizeWorkStateOrProjections()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore work = new(directory.Path, lease);
        SessionStore session = new(directory.Path, lease);
        BoardAdmissionStore policy = new(directory.Path, lease);
        using ProfileAdmission foreignOwner = new();
        using ProfileAdmissionToken foreign = foreignOwner.EnterStartupWriter();
        using ProfileAdmissionToken unissued = ProfileAdmissionToken.Create(lease.Admission, ProfileAdmissionKind.Startup);
        using ProfileAdmissionToken released = lease.Admission.EnterStartupWriter();
        released.Dispose();
        lease.Admission.CompleteStartup();
        using ProfileAdmissionToken exclusive = await lease.Admission.EnterExclusiveAsync().AsTask().WaitAsync(Deadline);
        Dictionary<string, string> before = Inventory(directory.Path);
        foreach (ProfileAdmissionToken bad in new[] { foreign, unissued, released, exclusive })
        {
            Assert.Throws<InvalidOperationException>(() => new WorkStateStore(directory.Path, lease, bad));
            Assert.Throws<InvalidOperationException>(() => work.CommitIntentAdmitted(Resource(directory.Path), bad));
            Assert.Throws<InvalidOperationException>(() => WorkStateAccess.For(work).CommitStateUnderFence(Resource(directory.Path), bad));
            Assert.Throws<InvalidOperationException>(() => work.CommitEndUnderFence(
                Url, 99, ThreadSourceStateValue.NotFound, Observed, null, bad));
            Assert.Throws<InvalidOperationException>(() => WorkStateAccess.For(work).CommitEndUnderFence(
                Url, 99, ThreadSourceStateValue.NotFound, Observed, null, bad));
            Assert.Throws<InvalidOperationException>(() => policy.SaveAdmitted("https://stub.test/g/", BoardAdmissionPolicy.All, bad));
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.SaveAdmittedAsync(new(), bad));
        }
        Assert.Equal(before.OrderBy(pair => pair.Key), Inventory(directory.Path).OrderBy(pair => pair.Key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedEndContinuesAfterReleaseDuringPointRead(bool shutdown)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore work = new(directory.Path, lease);
        WorkResource original = work.CommitIntent(Resource(directory.Path));
        WorkStateAccess access = WorkStateAccess.For(work);
        lease.Admission.CompleteStartup();
        using ProfileAdmissionToken accepted = lease.Admission.EnterOrdinaryWriter();
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource<WorkResource?> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread worker = new(() =>
        {
            try
            {
                using IDisposable fence = access.Enter(Url);
                completed.TrySetResult(access.CommitEndUnderFence(Url, 1,
                    ThreadSourceStateValue.NotFound, Observed, "final", accepted));
            }
            catch (Exception error) { completed.TrySetException(error); }
        }) { IsBackground = true };
        Task<ProfileAdmissionToken>? exclusive = null;
        Task? drain = null;
        bool started = false;
        try
        {
            lease.RunExclusive(() =>
            {
                // No competing resource-fence owner: the worker's first blocking
                // wait is FindResource's lease monitor, inside the accepted use.
                long resourcePoints = access.Counts.ResourcePoints;
                worker.Start();
                started = true;
                Assert.True(SpinWait.SpinUntil(() =>
                    access.Counts.ResourcePoints > resourcePoints
                    || completed.Task.IsCompleted, Deadline));
                Assert.False(completed.Task.IsCompleted);
                if (shutdown)
                {
                    lease.Admission.BeginShutdown();
                    drain = lease.Admission.DrainAsync();
                }
                else exclusive = lease.Admission.EnterExclusiveAsync(cancellation.Token).AsTask();
                accepted.Dispose();
                Assert.False((drain ?? exclusive!).IsCompleted);
                // New uses, including direct store entry, must still reject.
                Assert.Throws<InvalidOperationException>(() => work.CommitEndUnderFence(
                    Url, 1, ThreadSourceStateValue.NotFound, Observed, null, accepted));
                Assert.Equal(original, Assert.Single(work.LoadResources()));
                return true;
            });
            WorkResource expected = original with { Source = ThreadSourceStateValue.NotFound,
                EndObservedAt = Observed, CapturePending = true, FinalSnapshotJson = "final" };
            Assert.Equal(expected, await completed.Task.WaitAsync(Deadline));
            Assert.Equal(expected, Assert.Single(work.LoadResources()));
            Assert.Throws<InvalidOperationException>(() => access.CommitEndUnderFence(
                Url, 1, ThreadSourceStateValue.NotFound, Observed, null, accepted));
            if (shutdown) await drain!.WaitAsync(Deadline);
            else (await exclusive!.WaitAsync(Deadline)).Dispose();
        }
        finally
        {
            accepted.Dispose();
            cancellation.Cancel();
            if (started) Assert.True(worker.Join(Deadline));
            if (exclusive != null)
            {
                try { (await exclusive.WaitAsync(Deadline)).Dispose(); }
                catch (OperationCanceledException) { }
            }
        }
    }

    private static Dictionary<string, string> Inventory(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
        .Where(file => Path.GetFileName(file) is not (".meowchan-profile.lock" or ".ochan-profile.lock"))
        .ToDictionary(file => Path.GetRelativePath(directory, file), file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
    private static WorkResource Resource(string root) => new()
    {
        Url = Url, OutputRoot = root, Generation = 1, IsThread = true, Intent = WorkIntent.Watching,
        Source = ThreadSourceStateValue.Active
    };
    private static MediaWork Media(string root) => new()
    {
        ResourceUrl = Url, Generation = 1, MediaIdentifier = "image", DestinationPath = Path.Combine(root, "image.png"),
        DownloadUrl = "https://stub.test/image.png", Status = MediaWorkStatus.Ready
    };
}

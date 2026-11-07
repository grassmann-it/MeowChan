using MeowChan.Downloader;
using MeowChan.Persistence;

namespace MeowChan.Tests;

public sealed class ProfileProducerLifecycleTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private const string Url = "https://stub.test/g/thread/42";

    [Fact]
    public async Task DormantRestoreThenPausedExclusiveRefreshesRemovalOwnerWithoutRestartingMonitor()
    {
        using TempDirectory directory = new();
        using StubImageBoard provider = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore work = new(directory.Path, lease);
        SessionStore session = new(directory.Path, lease);
        WorkResource resource = Resource(directory.Path);
        work.CommitIntent(resource);
        await session.SaveAsync(new() { ThreadUrls = [Url], DownloadDirectories = new() { [Url] = directory.Path } });
        TaskCompletionSource entered = Signal();
        ProbeThread thread = new(provider.Board, new(Url))
        {
            OnCheckWithCancellation = async token =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
        };
        provider.ThreadFactory = _ => thread;
        DownloadQueue queue = new(1, 1024 * 1024);
        await using Registry registry = new([provider], queue, directory.Path, TimeSpan.FromMinutes(1), 60, session, workStore: work);
        registry.PrepareProfileProducers(lease);
        using (ProfileAdmissionToken startup = lease.Admission.EnterStartupWriter())
        {
            await registry.RestoreAdmittedAsync(startup).WaitAsync(Deadline);
            Assert.Equal(ProfileAdmissionPhase.Starting, lease.Admission.Phase);
            Assert.Equal(0, thread.Checks);
            Assert.Single(registry.Threads);
            Assert.Equal(resource, Assert.Single(work.LoadResources()));
        }
        lease.Admission.CompleteStartup();
        registry.InstallReadyProfileEpoch();
        await registry.ActivatePreparedProfileProducersAsync().WaitAsync(Deadline);
        await entered.Task.WaitAsync(Deadline);
        await registry.SetPausedAsync(true).WaitAsync(Deadline);
        int checksBefore = thread.Checks;
        await registry.RunWithProfileExclusiveAsync(admission =>
        {
            Assert.True(lease.Admission.RunWithToken(admission, () => true, ProfileAdmissionKind.Exclusive));
            Assert.Throws<InvalidOperationException>(() => work.CommitState(resource));
            return Task.FromResult(ProfileExclusiveOutcome<bool>.SafeToResume(true));
        }).WaitAsync(Deadline);
        Assert.True(queue.IsPaused);
        Assert.Equal(checksBefore, thread.Checks);
        Assert.Single(registry.Threads);

        thread.NotifyThreadRemoval(false);
        await registry.DrainBackgroundTasksAsync().WaitAsync(Deadline);
        Assert.Empty(registry.Threads);
        WorkResource suppressed = Assert.Single(work.LoadResources());
        Assert.Equal(WorkIntent.Suppressed, suppressed.Intent);
        Assert.True(suppressed.Generation > resource.Generation);
        Assert.Contains(Url, (await session.LoadAsync()).Snapshot.SuppressedThreadUrls);
        Assert.Equal(checksBefore, thread.Checks);
        Assert.True(queue.IsPaused);
        await registry.StopAsync().WaitAsync(Deadline);
        lease.Admission.BeginShutdown();
        await lease.Admission.DrainAsync().WaitAsync(Deadline);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParkedWaiterClosesInsideExclusiveWithoutResume(bool callerCancellation)
    {
        using TempDirectory directory = new();
        using StubImageBoard provider = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore work = new(directory.Path, lease);
        work.CommitIntent(Resource(directory.Path));
        ProbeThread thread = new(provider.Board, new(Url));
        DownloadQueue queue = new(1, 1024 * 1024, maxPendingDownloads: 1);
        queue.SetPaused(true);
        queue.TargetGate.ProbeOverride = (_, _, _, _) => Task.FromResult<string?>(null);
        await using Registry registry = new([provider], queue, directory.Path, TimeSpan.FromMinutes(1), 60,
            persistSession: false, workStore: work);
        registry.PrepareProfileProducers(lease);
        using (ProfileAdmissionToken startup = lease.Admission.EnterStartupWriter())
            await registry.RestoreAdmittedAsync(startup).WaitAsync(Deadline);
        lease.Admission.CompleteStartup();
        registry.InstallReadyProfileEpoch();
        await registry.ActivatePreparedProfileProducersAsync().WaitAsync(Deadline);
        queue.BindWork(thread, Assert.Single(work.LoadResources()));
        DownloadItem Item(string id) => new(new Uri($"https://stub.test/{id}.png"),
            Path.Combine(directory.Path, id + ".png"), provider, thread, id);
        Assert.True(queue.TryEnqueue(Item("pending")));
        TaskCompletionSource targetReady = Signal();
        TaskCompletionSource releaseTarget = Signal();
        TaskCompletionSource parked = Signal();
        TaskCompletionSource exclusiveEntered = Signal();
        TaskCompletionSource releaseExclusive = Signal();
        queue.BeforeFinalAdmission = async _ =>
        {
            targetReady.TrySetResult();
            await releaseTarget.Task.WaitAsync(Deadline);
        };
        using CancellationTokenSource caller = new();
        Task<bool> waiting = queue.EnqueueAsync(Item("waiting"), caller.Token);
        Task owner = Task.CompletedTask;
        try
        {
            await targetReady.Task.WaitAsync(Deadline);
            owner = registry.RunWithProfileExclusiveAsync(async admission =>
            {
                Assert.True(lease.Admission.RunWithToken(admission, () => true, ProfileAdmissionKind.Exclusive));
                exclusiveEntered.TrySetResult();
                await releaseExclusive.Task.WaitAsync(Deadline);
                return ProfileExclusiveOutcome<bool>.SafeToResume(true);
            });
            await exclusiveEntered.Task.WaitAsync(Deadline);
            Assert.False(waiting.IsCompleted);
            queue.BeforeProducerPark = () => parked.TrySetResult();
            releaseTarget.TrySetResult();
            await parked.Task.WaitAsync(Deadline);
            if (callerCancellation)
            {
                caller.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Deadline));
            }
            else
            {
                await queue.CancelDownloadsForThreadAsync(thread).WaitAsync(Deadline);
                Assert.False(await waiting.WaitAsync(Deadline));
            }
            Assert.Equal(0, queue.GetCapacitySnapshot().WaitingAdmission);
            Assert.False(owner.IsCompleted); // Waiter termination did not require a reopen.
            Assert.Throws<InvalidOperationException>(() => lease.Admission.EnterOrdinaryWriter());
            Assert.Equal(0, queue.GetThreadCounts([thread])[thread].Active);
            Assert.False(File.Exists(Path.Combine(directory.Path, "waiting.png")));
            releaseExclusive.TrySetResult();
            await owner.WaitAsync(Deadline);
            Assert.True(queue.IsPaused);
        }
        finally
        {
            queue.BeforeProducerPark = null;
            queue.BeforeFinalAdmission = null;
            caller.Cancel();
            releaseTarget.TrySetResult();
            releaseExclusive.TrySetResult();
            await owner.WaitAsync(Deadline);
            try { await waiting.WaitAsync(Deadline); }
            catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstProducerParkBeforeAdmissionWaiterClosesInsideExclusive(bool callerCancellation)
    {
        using TempDirectory directory = new();
        using StubImageBoard provider = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore work = new(directory.Path, lease);
        WorkResource resource = Resource(directory.Path);
        work.CommitIntent(resource);
        ProbeThread thread = new(provider.Board, new(Url));
        DownloadQueue queue = new(1, 1024 * 1024);
        queue.SetPaused(true);
        int targetChecks = 0;
        queue.TargetGate.ProbeOverride = (_, _, _, _) =>
        {
            Interlocked.Increment(ref targetChecks);
            return Task.FromResult<string?>(null);
        };
        await using Registry registry = new([provider], queue, directory.Path, TimeSpan.FromMinutes(1), 60,
            persistSession: false, workStore: work);
        registry.PrepareProfileProducers(lease);
        using (ProfileAdmissionToken startup = lease.Admission.EnterStartupWriter())
            await registry.RestoreAdmittedAsync(startup).WaitAsync(Deadline);
        lease.Admission.CompleteStartup();
        registry.InstallReadyProfileEpoch();
        await registry.ActivatePreparedProfileProducersAsync().WaitAsync(Deadline);
        queue.BindWork(thread, resource);
        DownloadItem item = new(new Uri("https://stub.test/first-park.png"),
            Path.Combine(directory.Path, "first-park.png"), provider, thread, "first-park");
        TaskCompletionSource exclusiveEntered = Signal();
        TaskCompletionSource releaseExclusive = Signal();
        TaskCompletionSource parked = Signal();
        ProfileAdmissionToken? exactExclusive = null;
        using CancellationTokenSource caller = new();
        Task<bool> waiting = Task.FromResult(false);
        Task owner = registry.RunWithProfileExclusiveAsync(async admission =>
        {
            exactExclusive = admission;
            exclusiveEntered.TrySetResult();
            await releaseExclusive.Task.WaitAsync(Deadline);
            Assert.True(lease.Admission.RunWithToken(admission, () => true, ProfileAdmissionKind.Exclusive));
            return ProfileExclusiveOutcome<bool>.SafeToResume(true);
        });
        try
        {
            await exclusiveEntered.Task.WaitAsync(Deadline);
            queue.BeforeThreadProducerPark = () => parked.TrySetResult();
            // Enqueue starts only after Exclusive. No target check or
            // AdmissionWaiter exists when this first producer park is entered.
            waiting = queue.EnqueueAsync(item, caller.Token);
            await parked.Task.WaitAsync(Deadline);
            Assert.False(waiting.IsCompleted);
            Assert.Equal(0, queue.GetCapacitySnapshot().WaitingAdmission);
            Assert.Equal(0, Volatile.Read(ref targetChecks));
            Assert.Empty(work.LoadMedia(Url, 1));

            if (callerCancellation)
            {
                caller.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Deadline));
            }
            else
            {
                await queue.CancelDownloadsForThreadAsync(thread).WaitAsync(Deadline);
                Assert.False(await waiting.WaitAsync(Deadline));
            }
            Assert.False(owner.IsCompleted); // Neither close needs a producer resume.
            Assert.True(lease.Admission.RunWithToken(exactExclusive!, () => true, ProfileAdmissionKind.Exclusive));
            Assert.Throws<InvalidOperationException>(() => lease.Admission.EnterOrdinaryWriter());
            Assert.Equal((0, 0, 0), queue.GetCapacitySnapshot());
            Assert.Equal(0, Volatile.Read(ref targetChecks));
            Assert.Equal(resource, Assert.Single(work.LoadResources()));
            Assert.Empty(work.LoadMedia(Url, 1));
            Assert.False(File.Exists(item.DestinationPath));

            releaseExclusive.TrySetResult();
            await owner.WaitAsync(Deadline);
            Assert.True(queue.IsPaused);
            using ProfileAdmissionToken reopened = lease.Admission.EnterOrdinaryWriter();
            Assert.True(lease.Admission.RunWithToken(reopened, () => true, ProfileAdmissionKind.Ordinary));
        }
        finally
        {
            queue.BeforeThreadProducerPark = null;
            caller.Cancel();
            releaseExclusive.TrySetResult();
            await owner.WaitAsync(Deadline);
            try { await waiting.WaitAsync(Deadline); }
            catch (OperationCanceledException) { }
        }
        await registry.StopAsync().WaitAsync(Deadline);
        lease.Admission.BeginShutdown();
        await lease.Admission.DrainAsync().WaitAsync(Deadline);
    }

    [Fact]
    public async Task LostResumeRetryPreservesScheduledDescriptorAndUserPause()
    {
        using TempDirectory directory = new();
        using StubImageBoard provider = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore work = new(directory.Path, lease);
        SessionStore session = new(directory.Path, lease);
        WorkResource resource = Resource(directory.Path);
        work.CommitIntent(resource);
        MediaWork waiting = new()
        {
            ResourceUrl = Url, Generation = 1, MediaIdentifier = "future",
            DestinationPath = Path.Combine(directory.Path, "future.png"), DownloadUrl = "https://stub.test/future.png",
            Status = MediaWorkStatus.Waiting, Attempts = 2, ConsecutiveNotFound = 1, HttpStatus = 404,
            LastAttemptAt = DateTimeOffset.UtcNow, DueAt = DateTimeOffset.UtcNow.AddHours(2), ManualAttempt = true
        };
        Assert.True(work.CommitMedia(waiting));
        await session.SaveAsync(new() { ThreadUrls = [Url], DownloadDirectories = new() { [Url] = directory.Path } });
        ProbeThread thread = new(provider.Board, new(Url));
        provider.ThreadFactory = _ => thread;
        DownloadQueue queue = new(1, 1024 * 1024);
        queue.SetPaused(true);
        await using Registry registry = new([provider], queue, directory.Path, TimeSpan.FromMinutes(1), 60, session, workStore: work);
        registry.PrepareProfileProducers(lease);
        using (ProfileAdmissionToken startup = lease.Admission.EnterStartupWriter())
            await registry.RestoreAdmittedAsync(startup).WaitAsync(Deadline);
        lease.Admission.CompleteStartup();
        registry.InstallReadyProfileEpoch();
        await registry.ActivatePreparedProfileProducersAsync().WaitAsync(Deadline);
        ProfileAdmissionToken? competitor = null;
        try
        {
            registry.BeforeProfileProducerResume = async () =>
            {
                // This seam runs after the old exact Exclusive has been released,
                // so the competing consumer deterministically wins fresh admission.
                competitor = await lease.Admission.EnterExclusiveAsync();
            };
            // The synthetic no-op is safe; the failure belongs to fresh-epoch installation, not the delegate.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                registry.RunWithProfileExclusiveAsync(
                    _ => Task.FromResult(ProfileExclusiveOutcome<bool>.SafeToResume(true))).WaitAsync(Deadline));
            Assert.NotNull(competitor);
            Assert.Equal(waiting, Assert.Single(work.LoadMedia(Url, 1)));
            Assert.Equal(0, thread.Checks);
            registry.BeforeProfileProducerResume = null;
            competitor.Dispose();
            competitor = null;
            await registry.RunWithProfileExclusiveAsync(
                _ => Task.FromResult(ProfileExclusiveOutcome<bool>.SafeToResume(true))).WaitAsync(Deadline);
            Assert.True(queue.IsPaused);
            Assert.Equal(0, thread.Checks);
            Assert.Same(thread, Assert.Single(registry.Threads));
            Assert.Equal(resource, Assert.Single(work.LoadResources()));
            Assert.Equal(waiting, Assert.Single(work.LoadMedia(Url, 1)));
            Assert.Equal(waiting, Assert.Single(Assert.Single(registry.GetWorkSnapshots()).Media));
            Assert.False(File.Exists(waiting.DestinationPath));
        }
        finally
        {
            registry.BeforeProfileProducerResume = null;
            competitor?.Dispose();
        }
    }

    [Fact]
    public async Task PermanentIngressCloseCannotBeUndoneByLateInstallOrResume()
    {
        using ProfileAdmission admission = new();
        admission.CompleteStartup();
        using ProfileProducerEpoch epoch = new(admission.EnterOrdinaryWriter());
        DownloadQueue queue = new(1, 1024 * 1024);
        try
        {
            queue.PrepareProfileProducers();
            queue.CloseProfileProducerIngress();
            Assert.Throws<InvalidOperationException>(() => queue.InstallProfileEpoch(epoch));
            Assert.Throws<InvalidOperationException>(queue.ResumeProfileProducers);
        }
        finally
        {
            await queue.StopAllAsync().WaitAsync(Deadline);
            await epoch.Close().WaitAsync(Deadline);
        }
        admission.BeginShutdown();
        Assert.False(admission.DrainAsync().IsCompleted);
        epoch.Dispose();
        await admission.DrainAsync().WaitAsync(Deadline);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static WorkResource Resource(string root) => new()
    {
        Url = Url, OutputRoot = root, Generation = 1, Intent = WorkIntent.Watching,
        IsThread = true, Source = ThreadSourceStateValue.Active
    };
}

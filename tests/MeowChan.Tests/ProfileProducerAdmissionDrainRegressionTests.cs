using System.Reflection;
using MeowChan.Downloader;
using MeowChan.Persistence;

namespace MeowChan.Tests;

/// <summary>Bounded producer-token drain and owner/stop ordering regressions.</summary>
public sealed class ProfileProducerAdmissionDrainRegressionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private const string Url = "https://stub.test/g/thread/42";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProducerAdmissionDrainRequiresTokenReleaseAndPinnedUseExit(bool releaseTokenFirst)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        lease.Admission.CompleteStartup();
        using ProfileAdmissionToken admission = lease.Admission.EnterOrdinaryWriter();
        using ProfileProducerEpoch epoch = new(admission);
        using ProfileProducerEpoch.Use producerUse = epoch.Enter();
        TaskCompletionSource entered = Signal();
        using ManualResetEventSlim release = new();
        // A dedicated worker avoids depending on an available pool thread for a
        // synchronous RunWithToken operation that deliberately waits on a gate.
        Task<bool> pinnedUse = Task.Factory.StartNew(() =>
            lease.Admission.RunWithToken(producerUse.Admission!, () =>
            {
                entered.TrySetResult();
                Assert.True(release.Wait(Deadline), "The synthetic token use was not released.");
                return true;
            }, ProfileAdmissionKind.Ordinary), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await entered.Task.WaitAsync(Deadline);
            Task epochDrain = epoch.Close();
            lease.Admission.BeginShutdown();
            Task admissionDrain = lease.Admission.DrainAsync();
            Assert.False(epochDrain.IsCompleted);
            Assert.False(admissionDrain.IsCompleted);
            Assert.Throws<InvalidOperationException>(lease.Admission.CompleteShutdown);
            Assert.Throws<InvalidOperationException>(() => lease.Admission.EnterOrdinaryWriter());

            // Separate the epoch's descendant count from the admission use pin:
            // even an early epoch release cannot revoke this accepted operation.
            producerUse.Dispose();
            await epochDrain.WaitAsync(Deadline);
            Assert.False(admissionDrain.IsCompleted);
            if (releaseTokenFirst)
            {
                epoch.Dispose();
                admission.Dispose(); // A repeated release must not retire the active pin.
                Assert.False(admissionDrain.IsCompleted);
                Assert.Throws<InvalidOperationException>(lease.Admission.CompleteShutdown);
                int lateUses = 0;
                Assert.Throws<InvalidOperationException>(() => lease.Admission.RunWithToken(admission,
                    () => ++lateUses, ProfileAdmissionKind.Ordinary));
                Assert.Equal(0, lateUses);
                release.Set();
                Assert.True(await pinnedUse.WaitAsync(Deadline));
            }
            else
            {
                release.Set();
                Assert.True(await pinnedUse.WaitAsync(Deadline));
                // Completing a use does not dispose a borrowed, still-owned token.
                Assert.False(admissionDrain.IsCompleted);
                Assert.Throws<InvalidOperationException>(lease.Admission.CompleteShutdown);
                Assert.True(lease.Admission.RunWithToken(admission, () => true, ProfileAdmissionKind.Ordinary));
                epoch.Dispose();
            }
            await admissionDrain.WaitAsync(Deadline);
            lease.Admission.CompleteShutdown();
            Assert.Equal(ProfileAdmissionPhase.Stopped, lease.Admission.Phase);
            Assert.Throws<InvalidOperationException>(() => lease.Admission.RunWithToken(admission,
                () => true, ProfileAdmissionKind.Ordinary));
        }
        finally
        {
            release.Set();
            try { await pinnedUse; }
            finally
            {
                producerUse.Dispose();
                await epoch.Close();
                epoch.Dispose();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DrainedQueueStopWaitsForPendingOwnerAndDoesNotRunOrResumeIt(bool sealWhilePending)
    {
        using TempDirectory directory = new();
        using StubImageBoard provider = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore work = new(directory.Path, lease);
        WorkResource resource = Resource(directory.Path);
        work.CommitIntent(resource);
        DownloadQueue queue = new(1, 1024 * 1024);
        await using Registry registry = new([provider], queue, directory.Path, TimeSpan.FromMinutes(1), 60,
            persistSession: false, workStore: work);
        List<Task> started = [];
        ProfileAdmissionToken? blocker = null;
        TaskCompletionSource finalScanEntered = Signal();
        TaskCompletionSource releaseFinalScan = Signal();
        int finalScans = 0;
        int ownerCalls = 0;
        int resumes = 0;
        Task owner = Task.CompletedTask;
        try
        {
            await InitializeRegistryAsync(registry, lease, started);
            Assert.Empty(registry.Threads);
            Assert.Empty(registry.Boards);
            blocker = lease.Admission.EnterOrdinaryWriter();
            // Queue stop publishes an asynchronous state change. First join its
            // Registry delivery, then the work it registered, before installing
            // the seam intended only for the owner's direct final pass.
            await AwaitStartedAsync(queue.StopAllAsync(), started);
            await AwaitStartedAsync(AwaitQueueRegistryDeliveryIdleAsync(queue, registry), started);
            await AwaitStartedAsync(registry.DrainBackgroundTasksAsync(), started);
            registry.BeforeCompletionScan = async () =>
            {
                Interlocked.Increment(ref finalScans);
                finalScanEntered.TrySetResult();
                await releaseFinalScan.Task.WaitAsync(Deadline);
            };
            registry.BeforeProfileProducerResume = () =>
            {
                Interlocked.Increment(ref resumes);
                return Task.CompletedTask;
            };
            owner = registry.RunWithProfileExclusiveAsync(_ =>
            {
                Interlocked.Increment(ref ownerCalls);
                return Task.CompletedTask;
            });
            // This seam is the owner's final completion pass, after epoch-use and
            // background drain, but before detaching/releasing the old token. It
            // runs outside _gate, so it does not itself prevent Stop's removals.
            await finalScanEntered.Task.WaitAsync(Deadline);
            Assert.Equal(1, Volatile.Read(ref finalScans));
            Assert.False(owner.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => lease.Admission.EnterOrdinaryWriter());

            // No watches exist, and the queue/background work were drained before
            // this owner started. Stop's remaining wait is the owner transition,
            // not monitor I/O or a queue-stop continuation.
            Task stop = registry.StopAsync();
            Assert.Same(stop, registry.StopAsync());
            Assert.True(registry.GetSnapshot().IsStopping);
            Assert.False(stop.IsCompleted);
            Assert.False(owner.IsCompleted);

            Task? admissionDrain = null;
            if (sealWhilePending)
            {
                lease.Admission.BeginShutdown(); // Reject the ungranted reservation, not the blocker.
                admissionDrain = lease.Admission.DrainAsync();
                Assert.False(admissionDrain.IsCompleted);
                Assert.Throws<InvalidOperationException>(lease.Admission.CompleteShutdown);
                Assert.False(owner.IsCompleted); // Rejection cannot skip the owner's final pass.
                Assert.False(stop.IsCompleted);
            }
            releaseFinalScan.TrySetResult();
            if (!sealWhilePending)
            {
                // Observe actual post-scan epoch retirement, not just release of
                // BeforeCompletionScan. Keep the unrelated ordinary token held:
                // Exclusive must still be pending when Stop is checked again.
                await AssertPostScanOwnerStillPendingAsync(registry, lease.Admission, blocker, owner, stop);
                Assert.False(stop.IsCompleted);
                Assert.False(owner.IsCompleted);
                Assert.Equal("Exclusive profile admission is reserved.",
                    Assert.Throws<InvalidOperationException>(() => lease.Admission.EnterOrdinaryWriter()).Message);
                Assert.True(lease.Admission.RunWithToken(blocker, () => true, ProfileAdmissionKind.Ordinary));
                Assert.Equal(0, Volatile.Read(ref ownerCalls));
                Assert.Equal(0, Volatile.Read(ref resumes));
                blocker.Dispose(); // Let grant win, then the owner must reject execution after Stop.
            }
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => owner.WaitAsync(Deadline));
            Assert.Equal(sealWhilePending ? "Profile admission is stopping." : "Registry is stopping.", error.Message);
            await stop.WaitAsync(Deadline);
            Assert.Same(stop, registry.StopAsync());
            Assert.Equal(0, Volatile.Read(ref ownerCalls));
            Assert.Equal(0, Volatile.Read(ref resumes));
            Assert.Equal(1, Volatile.Read(ref finalScans));
            Assert.Equal(resource, Assert.Single(work.LoadResources()));

            if (sealWhilePending)
            {
                // Stop retires its epoch, but cannot silently dispose an unrelated
                // accepted ordinary token. This is the original timeout boundary.
                Assert.False(admissionDrain!.IsCompleted);
                Assert.Throws<InvalidOperationException>(lease.Admission.CompleteShutdown);
                Assert.True(lease.Admission.RunWithToken(blocker, () => true, ProfileAdmissionKind.Ordinary));
                blocker.Dispose();
            }
            else
            {
                lease.Admission.BeginShutdown();
                admissionDrain = lease.Admission.DrainAsync();
                // The owner must also dispose a grant rejected before callback entry.
                Assert.True(admissionDrain.IsCompletedSuccessfully);
            }
            await admissionDrain!.WaitAsync(Deadline);
            lease.Admission.CompleteShutdown();
            Assert.Equal(ProfileAdmissionPhase.Stopped, lease.Admission.Phase);
        }
        finally
        {
            registry.BeforeCompletionScan = null;
            registry.BeforeProfileProducerResume = null;
            releaseFinalScan.TrySetResult();
            blocker?.Dispose();
            await DrainRegistryAsync(registry, lease, started, owner);
        }
    }

    [Fact]
    public async Task StopDoesNotRetireProducerAdmissionUntilCancelledMonitorExits()
    {
        using TempDirectory directory = new();
        using StubImageBoard provider = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore work = new(directory.Path, lease);
        WorkResource resource = Resource(directory.Path);
        work.CommitIntent(resource);
        TaskCompletionSource producerEntered = Signal();
        TaskCompletionSource producerCancelled = Signal();
        TaskCompletionSource releaseProducer = Signal();
        TaskCompletionSource producerReleased = Signal();
        TaskCompletionSource producerExited = Signal();
        ProbeThread thread = new(provider.Board, new(Url))
        {
            OnCheckWithCancellation = async token =>
            {
                producerEntered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    producerCancelled.TrySetResult();
                    await releaseProducer.Task.WaitAsync(Deadline);
                    producerReleased.TrySetResult();
                    throw;
                }
                finally { producerExited.TrySetResult(); }
            }
        };
        provider.ThreadFactory = _ => thread;
        DownloadQueue queue = new(1, 1024 * 1024);
        await using Registry registry = new([provider], queue, directory.Path, TimeSpan.FromMinutes(1), 60,
            persistSession: true, workStore: work);
        List<Task> started = [];
        try
        {
            await InitializeRegistryAsync(registry, lease, started);
            await producerEntered.Task.WaitAsync(Deadline);
            Assert.Same(thread, Assert.Single(registry.Threads));
            lease.Admission.BeginShutdown();
            Task admissionDrain = lease.Admission.DrainAsync();
            Assert.False(admissionDrain.IsCompleted);
            Task stop = registry.StopAsync();
            await producerCancelled.Task.WaitAsync(Deadline);
            // Cancellation was delivered, but the synthetic provider has not yet
            // returned. Neither Stop nor profile drain may report completion.
            Assert.False(producerExited.Task.IsCompleted);
            Assert.False(stop.IsCompleted);
            Assert.False(admissionDrain.IsCompleted);
            Assert.Throws<InvalidOperationException>(lease.Admission.CompleteShutdown);
            releaseProducer.TrySetResult();
            await stop.WaitAsync(Deadline);
            Assert.True(producerExited.Task.IsCompletedSuccessfully);
            Assert.True(producerReleased.Task.IsCompletedSuccessfully);
            Assert.Null(thread.LastCheckFailure);
            await admissionDrain.WaitAsync(Deadline);
            lease.Admission.CompleteShutdown();
            Assert.Equal(ProfileAdmissionPhase.Stopped, lease.Admission.Phase);
            Assert.Equal(1, thread.Checks);
            Assert.Equal(1, thread.MaximumActive);
            Assert.Empty(registry.Threads);
            Assert.Equal(resource, Assert.Single(work.LoadResources()));
        }
        finally
        {
            releaseProducer.TrySetResult();
            await DrainRegistryAsync(registry, lease, started);
        }
    }

    // Registry is already owned by the caller, whose finally covers every setup await.
    private static async Task InitializeRegistryAsync(Registry registry, ProfileLease lease, List<Task> started)
    {
        registry.PrepareProfileProducers(lease);
        await AwaitStartedAsync(RestoreWithStartupAsync(registry, lease), started);
        lease.Admission.CompleteStartup();
        registry.InstallReadyProfileEpoch();
        await AwaitStartedAsync(registry.ActivatePreparedProfileProducersAsync(), started);
        await AwaitStartedAsync(registry.DrainBackgroundTasksAsync(), started);
    }

    private static async Task RestoreWithStartupAsync(Registry registry, ProfileLease lease)
    {
        // The deadline belongs to the observer, not this token's lifetime. A timed-out
        // restore still owns Startup until its actual operation has terminated.
        using ProfileAdmissionToken startup = lease.Admission.EnterStartupWriter();
        await registry.RestoreAdmittedAsync(startup);
    }

    private static Task AwaitStartedAsync(Task operation, List<Task> started)
    {
        started.Add(operation); // Retain the operation, never its timeout wrapper.
        return operation.WaitAsync(Deadline);
    }

    private static async Task DrainRegistryAsync(Registry registry, ProfileLease lease, List<Task> started,
        params Task[] owners)
    {
        // Caller has opened its synthetic gates, released blockers and reset seams.
        // Seal/Stop prevent a late setup continuation or owner from reopening ingress.
        lease.Admission.BeginShutdown();
        Task stop = registry.StopAsync();
        try
        {
            // WhenAll joins every actual operation even if another has faulted.
            // No deadline observer is accepted as evidence of completed teardown.
            await Task.WhenAll(Task.WhenAll(started), Task.WhenAll(owners.Select(ObserveStoppedOwnerAsync)), stop);
        }
        finally { await lease.Admission.DrainAsync(); }
    }

    private static async Task ObserveStoppedOwnerAsync(Task owner)
    {
        try { await owner; }
        catch (InvalidOperationException) { } // Teardown only; normal-path result is asserted in the test.
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task AwaitQueueRegistryDeliveryIdleAsync(DownloadQueue queue, Registry registry)
    {
        // StopAllAsync does not join CoalescedEvent delivery. With this empty,
        // stopped queue no further signals are produced: observe the actual
        // Registry subscription becoming idle, not a timer or a different subscriber.
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object Field(object target, string name) => target.GetType().GetField(name, flags)?.GetValue(target)
            ?? throw new InvalidOperationException($"Required delivery observation field {name} is missing.");
        object changes = Field(queue, "_changes");
        object subscription;
        lock (Field(changes, "_gate"))
        {
            subscription = Assert.Single(((System.Collections.IEnumerable)Field(changes, "_subscriptions"))
                .Cast<object>().Where(candidate =>
                {
                    Delegate handler = Assert.IsAssignableFrom<Delegate>(candidate.GetType()
                        .GetProperty("Handler")!.GetValue(candidate));
                    return ReferenceEquals(handler.Target, registry) && handler.Method.Name == "SignalChanged";
                }));
        }
        using CancellationTokenSource timeout = new(Deadline);
        while (true)
        {
            lock (Field(subscription, "_gate"))
            {
                Assert.False((bool)Field(subscription, "_disposed"));
                if (!(bool)Field(subscription, "_running") && !(bool)Field(subscription, "_pending")) return;
            }
            if (timeout.IsCancellationRequested)
                throw new TimeoutException("The stopped queue's Registry notification did not become idle.");
            await Task.Yield();
        }
    }

    private static async Task AssertPostScanOwnerStillPendingAsync(Registry registry, ProfileAdmission admission,
        ProfileAdmissionToken blocker, Task owner, Task stop)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo Field(Type type, string name) => type.GetField(name, flags)
            ?? throw new InvalidOperationException($"Required drain observation field {type.Name}.{name} is missing.");
        object state = Field(typeof(Registry), "_state").GetValue(registry)!;
        FieldInfo epoch = Field(typeof(Registry), "_producerEpoch");
        SemaphoreSlim transitions = Assert.IsType<SemaphoreSlim>(
            Field(typeof(Registry), "_producerTransitions").GetValue(registry));
        object admissionSync = Field(typeof(ProfileAdmission), "_sync").GetValue(admission)!;
        FieldInfo pending = Field(typeof(ProfileAdmission), "_pending");
        HashSet<ProfileAdmissionToken> active = Assert.IsType<HashSet<ProfileAdmissionToken>>(
            Field(typeof(ProfileAdmission), "_active").GetValue(admission));
        using CancellationTokenSource timeout = new(Deadline);
        while (true)
        {
            bool retired;
            lock (state) retired = epoch.GetValue(registry) is null;
            if (retired) break;
            Assert.False(owner.IsCompleted);
            Assert.False(stop.IsCompleted);
            if (timeout.IsCancellationRequested)
                throw new TimeoutException("The owner did not retire its producer epoch after the final scan.");
            // Yield only to let the owner make progress. Success requires the real
            // retirement state; elapsed time is never accepted as ordering evidence.
            await Task.Yield();
        }
        // Registry clears this epoch only after TryCompleteAsync has returned,
        // queue detachment and old-token disposal. Admission fields are read under
        // their real monitor; reflection never writes or creates a reservation.
        lock (admissionSync)
        {
            object waiter = pending.GetValue(admission)
                ?? throw new InvalidOperationException("The owner's Exclusive reservation is no longer pending.");
            PropertyInfo completion = waiter.GetType().GetProperty("Completion", flags)
                ?? throw new InvalidOperationException("The pending reservation completion is missing.");
            Assert.False(Assert.IsType<TaskCompletionSource<ProfileAdmissionToken>>(
                completion.GetValue(waiter)).Task.IsCompleted);
            Assert.Same(blocker, Assert.Single(active));
        }
        Assert.Equal(0, transitions.CurrentCount);
        // No observation monitor remains held while testing the actual tasks.
        Assert.False(stop.IsCompleted);
        Assert.False(owner.IsCompleted);
    }

    private static WorkResource Resource(string root) => new()
    {
        Url = Url, OutputRoot = root, Generation = 1, Intent = WorkIntent.Watching,
        IsThread = true, Source = ThreadSourceStateValue.Active
    };
}

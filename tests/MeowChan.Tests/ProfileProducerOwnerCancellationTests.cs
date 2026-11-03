using MeowChan.Downloader;
using MeowChan.Persistence;

namespace MeowChan.Tests;

/// <summary>Bounded owner-lane cancellation and resume-failure coverage.</summary>
public sealed class ProfileProducerOwnerCancellationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private const string Url = "https://stub.test/g/thread/42";

    [Fact]
    public async Task OwnerCancellationBeforeExclusiveGrantLeavesAdmissionUsable()
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
        using CancellationTokenSource cancellation = new();
        Task request = Task.CompletedTask;
        try
        {
            await InitializeRegistryAsync(registry, lease, started);
            blocker = lease.Admission.EnterOrdinaryWriter();
            request = registry.RunWithProfileExclusiveAsync(_ => Task.CompletedTask, cancellation.Token);
            await WaitForExclusiveReservation(lease.Admission);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(Deadline));
            blocker.Dispose();
            using (ProfileAdmissionToken ordinary = lease.Admission.EnterOrdinaryWriter())
                Assert.True(lease.Admission.RunWithToken(ordinary, () => true, ProfileAdmissionKind.Ordinary));
            await registry.StopAsync().WaitAsync(Deadline);
            lease.Admission.BeginShutdown();
            await lease.Admission.DrainAsync().WaitAsync(Deadline);
        }
        finally
        {
            cancellation.Cancel();
            blocker?.Dispose();
            await DrainRegistryAsync(registry, lease, started, request);
        }
    }

    [Fact]
    public async Task OwnerCancellationAfterExclusiveGrantDoesNotRevokeAcceptedOperation()
    {
        using TempDirectory directory = new();
        using StubImageBoard provider = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore work = new(directory.Path, lease);
        work.CommitIntent(Resource(directory.Path));
        DownloadQueue queue = new(1, 1024 * 1024);
        await using Registry registry = new([provider], queue, directory.Path, TimeSpan.FromMinutes(1), 60,
            persistSession: false, workStore: work);
        List<Task> started = [];
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource entered = Signal();
        TaskCompletionSource release = Signal();
        Task request = Task.CompletedTask;
        try
        {
            await InitializeRegistryAsync(registry, lease, started);
            request = registry.RunWithProfileExclusiveAsync(async admission =>
            {
                Assert.True(lease.Admission.RunWithToken(admission, () => true, ProfileAdmissionKind.Exclusive));
                entered.TrySetResult();
                await release.Task.WaitAsync(Deadline);
            }, cancellation.Token);
            await entered.Task.WaitAsync(Deadline);
            cancellation.Cancel();
            release.TrySetResult();
            await request.WaitAsync(Deadline);
            using (ProfileAdmissionToken ordinary = lease.Admission.EnterOrdinaryWriter())
                Assert.True(lease.Admission.RunWithToken(ordinary, () => true, ProfileAdmissionKind.Ordinary));
            await registry.StopAsync().WaitAsync(Deadline);
            lease.Admission.BeginShutdown();
            await lease.Admission.DrainAsync().WaitAsync(Deadline);
        }
        finally
        {
            release.TrySetResult();
            cancellation.Cancel();
            await DrainRegistryAsync(registry, lease, started, request);
        }
    }

    [Fact]
    public async Task OwnerResumeFailureLeavesTransitionRetryableAndDoesNotReportSuccess()
    {
        using TempDirectory directory = new();
        using StubImageBoard provider = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore work = new(directory.Path, lease);
        work.CommitIntent(Resource(directory.Path));
        DownloadQueue queue = new(1, 1024 * 1024);
        await using Registry registry = new([provider], queue, directory.Path, TimeSpan.FromMinutes(1), 60,
            persistSession: false, workStore: work);
        List<Task> started = [];
        Task first = Task.CompletedTask;
        Task retry = Task.CompletedTask;
        int failures = 0;
        try
        {
            await InitializeRegistryAsync(registry, lease, started);
            registry.BeforeProfileProducerResume = () =>
            {
                if (Interlocked.Increment(ref failures) == 1)
                    throw new InvalidOperationException("synthetic resume failure");
                return Task.CompletedTask;
            };
            first = registry.RunWithProfileExclusiveAsync(_ => Task.CompletedTask);
            await Assert.ThrowsAsync<InvalidOperationException>(() => first.WaitAsync(Deadline));
            registry.BeforeProfileProducerResume = null;
            retry = registry.RunWithProfileExclusiveAsync(_ => Task.CompletedTask);
            await retry.WaitAsync(Deadline);
            Assert.Equal(1, failures);
            await registry.StopAsync().WaitAsync(Deadline);
            lease.Admission.BeginShutdown();
            await lease.Admission.DrainAsync().WaitAsync(Deadline);
        }
        finally
        {
            registry.BeforeProfileProducerResume = null;
            await DrainRegistryAsync(registry, lease, started, first, retry);
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
        catch (OperationCanceledException) { } // Expected for a cancelled request; never a timeout-as-drain.
        catch (InvalidOperationException) { } // Teardown only; normal-path result is asserted in the test.
    }

    private static async Task WaitForExclusiveReservation(ProfileAdmission admission)
    {
        for (int attempt = 0; attempt < 1000; attempt++)
        {
            try
            {
                using ProfileAdmissionToken ordinary = admission.EnterOrdinaryWriter();
            }
            catch (InvalidOperationException)
            {
                return;
            }
            await Task.Yield();
        }
        throw new TimeoutException("Exclusive reservation did not become observable.");
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static WorkResource Resource(string root) => new()
    {
        Url = Url, OutputRoot = root, Generation = 1, Intent = WorkIntent.Watching,
        IsThread = true, Source = ThreadSourceStateValue.Active
    };
}

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
        await using Registry registry = await StartedRegistry(directory, provider, lease, work);
        using ProfileAdmissionToken blocker = lease.Admission.EnterOrdinaryWriter();
        using CancellationTokenSource cancellation = new();
        Task request = registry.RunWithProfileExclusiveAsync(_ => Task.CompletedTask, cancellation.Token);
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

    [Fact]
    public async Task OwnerCancellationAfterExclusiveGrantDoesNotRevokeAcceptedOperation()
    {
        using TempDirectory directory = new();
        using StubImageBoard provider = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore work = new(directory.Path, lease);
        work.CommitIntent(Resource(directory.Path));
        await using Registry registry = await StartedRegistry(directory, provider, lease, work);
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource entered = Signal();
        TaskCompletionSource release = Signal();
        Task request = registry.RunWithProfileExclusiveAsync(async admission =>
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

    [Fact]
    public async Task OwnerResumeFailureLeavesTransitionRetryableAndDoesNotReportSuccess()
    {
        using TempDirectory directory = new();
        using StubImageBoard provider = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore work = new(directory.Path, lease);
        work.CommitIntent(Resource(directory.Path));
        await using Registry registry = await StartedRegistry(directory, provider, lease, work);
        int failures = 0;
        registry.BeforeProfileProducerResume = () =>
        {
            if (Interlocked.Increment(ref failures) == 1)
                throw new InvalidOperationException("synthetic resume failure");
            return Task.CompletedTask;
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.RunWithProfileExclusiveAsync(_ => Task.CompletedTask).WaitAsync(Deadline));
        registry.BeforeProfileProducerResume = null;
        await registry.RunWithProfileExclusiveAsync(_ => Task.CompletedTask).WaitAsync(Deadline);
        Assert.Equal(1, failures);
        await registry.StopAsync().WaitAsync(Deadline);
        lease.Admission.BeginShutdown();
        await lease.Admission.DrainAsync().WaitAsync(Deadline);
    }

    private static async Task<Registry> StartedRegistry(TempDirectory directory, StubImageBoard provider,
        ProfileLease lease, WorkStateStore work)
    {
        DownloadQueue queue = new(1, 1024 * 1024);
        Registry registry = new([provider], queue, directory.Path, TimeSpan.FromMinutes(1), 60,
            persistSession: false, workStore: work);
        registry.PrepareProfileProducers(lease);
        using (ProfileAdmissionToken startup = lease.Admission.EnterStartupWriter())
            await registry.RestoreAdmittedAsync(startup).WaitAsync(Deadline);
        lease.Admission.CompleteStartup();
        registry.InstallReadyProfileEpoch();
        await registry.ActivatePreparedProfileProducersAsync().WaitAsync(Deadline);
        return registry;
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

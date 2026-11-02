using System.Reflection;
using MeowChan.Persistence;

namespace MeowChan.Tests;

public sealed class ProfileAdmissionTests
{
    [Fact]
    public void NewAdmissionIsStartingAndOnlyAcceptsStartupWriters()
    {
        using ProfileAdmission admission = new();
        Assert.Equal(ProfileAdmissionPhase.Starting, admission.Phase);
        using ProfileAdmissionToken first = admission.EnterStartupWriter();
        using ProfileAdmissionToken second = admission.EnterStartupWriter();
        Assert.Equal(ProfileAdmissionKind.Startup, first.Kind);
        Assert.NotSame(first, second);
        Assert.Throws<InvalidOperationException>(() => admission.EnterOrdinaryWriter());
        Assert.Throws<InvalidOperationException>(() => { _ = admission.EnterExclusiveAsync(); });
        Assert.Throws<InvalidOperationException>(() => admission.CompleteStartup());
        Assert.Equal(ProfileAdmissionPhase.Starting, admission.Phase);
    }

    [Fact]
    public void CompleteStartupRequiresEveryAcceptedStartupTokenToBeReleased()
    {
        using ProfileAdmission admission = new();
        using ProfileAdmissionToken first = admission.EnterStartupWriter();
        using ProfileAdmissionToken second = admission.EnterStartupWriter();
        first.Dispose();
        first.Dispose();
        Assert.Throws<InvalidOperationException>(() => admission.CompleteStartup());
        second.Dispose();
        admission.CompleteStartup();
        Assert.Equal(ProfileAdmissionPhase.Ready, admission.Phase);
        Assert.Throws<InvalidOperationException>(() => admission.CompleteStartup());
        Assert.Throws<InvalidOperationException>(() => admission.EnterStartupWriter());
        Assert.Throws<InvalidOperationException>(() => admission.FailStartup());
        using ProfileAdmissionToken ordinary = admission.EnterOrdinaryWriter();
        Assert.Equal(ProfileAdmissionKind.Ordinary, ordinary.Kind);
    }

    [Fact]
    public async Task FailedStartupIsTerminalForWritersButAcceptedStartupStillDrains()
    {
        using ProfileAdmission admission = new();
        using ProfileAdmissionToken startup = admission.EnterStartupWriter();
        admission.FailStartup();
        admission.FailStartup();
        Assert.Equal(ProfileAdmissionPhase.RecoveryFailed, admission.Phase);
        AssertAllWritersRejected(admission);
        Assert.Throws<InvalidOperationException>(() => admission.CompleteStartup());
        admission.BeginShutdown();
        Task drain = admission.DrainAsync();
        Assert.False(drain.IsCompleted);
        startup.Dispose();
        await drain;
        admission.CompleteShutdown();
        Assert.Equal(ProfileAdmissionPhase.Stopped, admission.Phase);
    }

    [Fact]
    public void PrecancelledStartupDoesNotPreventStartupCompletion()
    {
        using ProfileAdmission admission = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        OperationCanceledException error = Assert.Throws<OperationCanceledException>(
            () => admission.EnterStartupWriter(cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        admission.CompleteStartup();
        Assert.Equal(ProfileAdmissionPhase.Ready, admission.Phase);
    }

    [Fact]
    public async Task PrecancelledReadyEntriesLeaveNoReservation()
    {
        using ProfileAdmission admission = ReadyAdmission();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => admission.EnterOrdinaryWriter(cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => { _ = admission.EnterExclusiveAsync(cancellation.Token); });
        using ProfileAdmissionToken ordinary = admission.EnterOrdinaryWriter();
        ordinary.Dispose();
        using ProfileAdmissionToken exclusive = await admission.EnterExclusiveAsync();
        Assert.Equal(ProfileAdmissionKind.Exclusive, exclusive.Kind);
    }

    [Fact]
    public async Task ExclusiveGrantsImmediatelyWhenReadyAndEmptyAndIsNotReentrant()
    {
        using ProfileAdmission admission = ReadyAdmission();
        ValueTask<ProfileAdmissionToken> entry = admission.EnterExclusiveAsync();
        Assert.True(entry.IsCompletedSuccessfully);
        using ProfileAdmissionToken exclusive = await entry;
        Assert.Equal(ProfileAdmissionKind.Exclusive, exclusive.Kind);
        Assert.Throws<InvalidOperationException>(() => admission.EnterOrdinaryWriter());
        Assert.Throws<InvalidOperationException>(() => { _ = admission.EnterExclusiveAsync(); });
        exclusive.Dispose();
        exclusive.Dispose();
        using ProfileAdmissionToken ordinary = admission.EnterOrdinaryWriter();
    }

    [Fact]
    public async Task PendingExclusiveSealsNewOrdinaryAndWaitsForAllAcceptedWriters()
    {
        using ProfileAdmission admission = ReadyAdmission();
        using ProfileAdmissionToken first = admission.EnterOrdinaryWriter();
        using ProfileAdmissionToken second = admission.EnterOrdinaryWriter();
        Task<ProfileAdmissionToken> pending = admission.EnterExclusiveAsync().AsTask();
        Assert.False(pending.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => admission.EnterOrdinaryWriter());
        Assert.Throws<InvalidOperationException>(() => { _ = admission.EnterExclusiveAsync(); });
        first.Dispose();
        first.Dispose();
        Assert.False(pending.IsCompleted);
        second.Dispose();
        using ProfileAdmissionToken exclusive = await pending;
        Assert.Equal(ProfileAdmissionKind.Exclusive, exclusive.Kind);
        Assert.Throws<InvalidOperationException>(() => admission.EnterOrdinaryWriter());
        exclusive.Dispose();
        using ProfileAdmissionToken next = admission.EnterOrdinaryWriter();
    }

    [Fact]
    public async Task CancellationBeforeGrantRemovesOnlyThatWaiterAndReopensOrdinary()
    {
        using ProfileAdmission admission = ReadyAdmission();
        using ProfileAdmissionToken ordinary = admission.EnterOrdinaryWriter();
        using CancellationTokenSource cancellation = new();
        Task<ProfileAdmissionToken> pending = admission.EnterExclusiveAsync(cancellation.Token).AsTask();
        cancellation.Cancel();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        using ProfileAdmissionToken laterOrdinary = admission.EnterOrdinaryWriter();
        Task<ProfileAdmissionToken> replacement = admission.EnterExclusiveAsync().AsTask();
        ordinary.Dispose();
        Assert.False(replacement.IsCompleted);
        laterOrdinary.Dispose();
        using ProfileAdmissionToken exclusive = await replacement;
        Assert.Equal(ProfileAdmissionKind.Exclusive, exclusive.Kind);
    }

    [Fact]
    public async Task CancellationAfterQueuedGrantDoesNotRevokeOrLeakAcceptedToken()
    {
        using ProfileAdmission admission = ReadyAdmission();
        using ProfileAdmissionToken ordinary = admission.EnterOrdinaryWriter();
        using CancellationTokenSource cancellation = new();
        Task<ProfileAdmissionToken> pending = admission.EnterExclusiveAsync(cancellation.Token).AsTask();
        ordinary.Dispose(); // Grant is linearized synchronously, even before the await resumes.
        cancellation.Cancel();
        admission.BeginShutdown();
        Task drain = admission.DrainAsync();
        Assert.False(drain.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => admission.CompleteShutdown());
        using ProfileAdmissionToken exclusive = await pending;
        exclusive.Dispose();
        await drain;
        admission.CompleteShutdown();
    }

    [Fact]
    public async Task CancellationAfterImmediateGrantDoesNotReleaseExclusive()
    {
        using ProfileAdmission admission = ReadyAdmission();
        using CancellationTokenSource cancellation = new();
        using ProfileAdmissionToken exclusive = await admission.EnterExclusiveAsync(cancellation.Token);
        cancellation.Cancel();
        Assert.Throws<InvalidOperationException>(() => admission.EnterOrdinaryWriter());
        admission.BeginShutdown();
        Task drain = admission.DrainAsync();
        Assert.False(drain.IsCompleted);
        exclusive.Dispose();
        await drain;
        admission.CompleteShutdown();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterWriterAcceptanceDoesNotRevokeWriter(bool startup)
    {
        using ProfileAdmission admission = new();
        if (!startup) admission.CompleteStartup();
        using CancellationTokenSource cancellation = new();
        using ProfileAdmissionToken writer = startup
            ? admission.EnterStartupWriter(cancellation.Token)
            : admission.EnterOrdinaryWriter(cancellation.Token);
        cancellation.Cancel();
        admission.BeginShutdown();
        Task drain = admission.DrainAsync();
        Assert.False(drain.IsCompleted);
        writer.Dispose();
        await drain;
        admission.CompleteShutdown();
    }

    [Fact]
    public async Task ShutdownRejectsPendingExclusiveWithoutCountingItAsAccepted()
    {
        using ProfileAdmission admission = ReadyAdmission();
        using ProfileAdmissionToken ordinary = admission.EnterOrdinaryWriter();
        Task<ProfileAdmissionToken> pending = admission.EnterExclusiveAsync().AsTask();
        admission.BeginShutdown();
        admission.BeginShutdown();
        Assert.Equal(ProfileAdmissionPhase.Stopping, admission.Phase);
        AssertAllWritersRejected(admission);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        Task drain = admission.DrainAsync();
        Assert.False(drain.IsCompleted);
        ordinary.Dispose();
        await drain;
        admission.CompleteShutdown();
        Assert.Equal(ProfileAdmissionPhase.Stopped, admission.Phase);
        AssertAllWritersRejected(admission);
        admission.BeginShutdown();
        Assert.Equal(ProfileAdmissionPhase.Stopped, admission.Phase);
        await admission.DrainAsync();
        Assert.Throws<InvalidOperationException>(() => admission.CompleteShutdown());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownAndCancellationOfWaiterNeverReopenAdmission(bool cancelFirst)
    {
        using ProfileAdmission admission = ReadyAdmission();
        using ProfileAdmissionToken ordinary = admission.EnterOrdinaryWriter();
        using CancellationTokenSource cancellation = new();
        Task<ProfileAdmissionToken> pending = admission.EnterExclusiveAsync(cancellation.Token).AsTask();
        if (cancelFirst) cancellation.Cancel();
        admission.BeginShutdown();
        if (!cancelFirst) cancellation.Cancel();
        if (cancelFirst)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        AssertAllWritersRejected(admission);
        ordinary.Dispose();
        await admission.DrainAsync();
        admission.CompleteShutdown();
    }

    [Fact]
    public async Task CancellingOneDrainDoesNotCancelOtherDrainsOrReleaseTokens()
    {
        using ProfileAdmission admission = ReadyAdmission();
        using ProfileAdmissionToken first = admission.EnterOrdinaryWriter();
        using ProfileAdmissionToken second = admission.EnterOrdinaryWriter();
        admission.BeginShutdown();
        using CancellationTokenSource cancellation = new();
        Task cancelledDrain = admission.DrainAsync(cancellation.Token);
        Task otherDrain = admission.DrainAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledDrain);
        Assert.False(otherDrain.IsCompleted);
        first.Dispose();
        Assert.False(otherDrain.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => admission.CompleteShutdown());
        second.Dispose();
        await otherDrain;
        await admission.DrainAsync();
        admission.CompleteShutdown();
    }

    [Fact]
    public async Task PrecancelledDrainDoesNotChangeAcceptedTokenLifetime()
    {
        using ProfileAdmission admission = ReadyAdmission();
        using ProfileAdmissionToken ordinary = admission.EnterOrdinaryWriter();
        admission.BeginShutdown();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => { _ = admission.DrainAsync(cancellation.Token); });
        Task drain = admission.DrainAsync();
        Assert.False(drain.IsCompleted);
        ordinary.Dispose();
        await drain;
        admission.CompleteShutdown();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DrainAndCompletionRequireShutdownToHaveBegun(bool ready)
    {
        using ProfileAdmission admission = new();
        if (ready) admission.CompleteStartup();
        Assert.Throws<InvalidOperationException>(() => { _ = admission.DrainAsync(); });
        Assert.Throws<InvalidOperationException>(() => admission.CompleteShutdown());
        admission.BeginShutdown();
        Assert.True(admission.DrainAsync().IsCompletedSuccessfully);
        admission.CompleteShutdown();
        Assert.Equal(ProfileAdmissionPhase.Stopped, admission.Phase);
        Assert.Throws<InvalidOperationException>(() => admission.CompleteStartup());
        Assert.Throws<InvalidOperationException>(() => admission.FailStartup());
    }

    [Fact]
    public async Task DisposeSealsPendingWorkButDoesNotPretendAcceptedWorkIsDrained()
    {
        using ProfileAdmission admission = ReadyAdmission();
        using ProfileAdmissionToken ordinary = admission.EnterOrdinaryWriter();
        Task<ProfileAdmissionToken> pending = admission.EnterExclusiveAsync().AsTask();
        ((IDisposable)admission).Dispose();
        ((IDisposable)admission).Dispose();
        Assert.Equal(ProfileAdmissionPhase.Stopping, admission.Phase);
        AssertAllEntriesDisposed(admission);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        Task drain = admission.DrainAsync();
        Assert.False(drain.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => admission.CompleteShutdown());
        ordinary.Dispose();
        await drain;
        admission.CompleteShutdown();
        admission.Close();
        Assert.Equal(ProfileAdmissionPhase.Stopped, admission.Phase);
        AssertAllEntriesDisposed(admission);
    }

    [Fact]
    public void DisposeBeforeStartupBlocksStartupCompletionAndAllEntries()
    {
        using ProfileAdmission admission = new();
        admission.Close();
        AssertAllEntriesDisposed(admission);
        Assert.Throws<ObjectDisposedException>(() => admission.CompleteStartup());
        Assert.Throws<ObjectDisposedException>(() => admission.FailStartup());
        admission.CompleteShutdown();
    }

    [Fact]
    public async Task TokensAreInstanceBoundAndUnknownReleasesCannotDrainAnotherOwner()
    {
        using ProfileAdmission first = ReadyAdmission();
        using ProfileAdmission second = ReadyAdmission();
        using ProfileAdmissionToken firstToken = first.EnterOrdinaryWriter();
        using ProfileAdmissionToken secondToken = second.EnterOrdinaryWriter();
        Task<ProfileAdmissionToken> pending = second.EnterExclusiveAsync().AsTask();
        second.Release(firstToken);
        firstToken.Dispose();
        Assert.False(pending.IsCompleted);
        using ProfileAdmissionToken unissued = ProfileAdmissionToken.Create(second, ProfileAdmissionKind.Ordinary);
        unissued.Dispose();
        Assert.False(pending.IsCompleted);
        secondToken.Dispose();
        using ProfileAdmissionToken exclusive = await pending;
        second.BeginShutdown();
        Task drain = second.DrainAsync();
        secondToken.Dispose();
        Assert.False(drain.IsCompleted);
        exclusive.Dispose();
        await drain;
        second.CompleteShutdown();
        using ProfileAdmissionToken stillIndependent = first.EnterOrdinaryWriter();
    }

    [Fact]
    public void ProfileLeaseOwnsExactlyOneAdmissionForItsLifetime()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);

        Assert.Same(lease.Admission, lease.Admission);
        Assert.Equal(ProfileAdmissionPhase.Starting, lease.Admission.Phase);

        lease.Admission.CompleteStartup();
        lease.Dispose();
        Assert.Equal(ProfileAdmissionPhase.Stopping, lease.Admission.Phase);
        lease.Admission.CompleteShutdown();
    }

    [Fact]
    public void IndependentAndReacquiredProfilesDoNotShareAdmission()
    {
        using TempDirectory firstDirectory = new();
        using TempDirectory secondDirectory = new();
        using ProfileLease first = ProfileLease.Acquire(firstDirectory.Path);
        using ProfileLease second = ProfileLease.Acquire(secondDirectory.Path);

        Assert.NotSame(first.Admission, second.Admission);
        ProfileAdmission oldAdmission = first.Admission;
        first.Dispose();
        second.Dispose();
        oldAdmission.CompleteShutdown();

        using ProfileLease reacquired = ProfileLease.Acquire(firstDirectory.Path);
        Assert.NotSame(oldAdmission, reacquired.Admission);
        Assert.Equal(ProfileAdmissionPhase.Starting, reacquired.Admission.Phase);
    }

    [Fact]
    public async Task LeaseDisposeSealsNewEntriesButDoesNotSynchronouslyDrainAcceptedWork()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        lease.Admission.CompleteStartup();
        using ProfileAdmissionToken writer = lease.Admission.EnterOrdinaryWriter();

        lease.Dispose();
        Assert.Equal(ProfileAdmissionPhase.Stopping, lease.Admission.Phase);
        Assert.Throws<ObjectDisposedException>(() => lease.Admission.EnterOrdinaryWriter());
        Task drain = lease.Admission.DrainAsync();
        Assert.False(drain.IsCompleted);

        writer.Dispose();
        await drain;
        lease.Admission.CompleteShutdown();
    }

    [Fact]
    public async Task LeaseDisposeSealsBeforeWaitingForLeaseMonitor()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        using ManualResetEventSlim releaseLeaseMonitor = new();
        TaskCompletionSource<bool> leaseMonitorHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task holder = Task.Run(() => lease.RunExclusive(() =>
        {
            leaseMonitorHeld.SetResult(true);
            Assert.True(releaseLeaseMonitor.Wait(TimeSpan.FromSeconds(15)));
            return true;
        }));
        Task? disposal = null;
        try
        {
            await leaseMonitorHeld.Task.WaitAsync(TimeSpan.FromSeconds(10));
            disposal = Task.Run(lease.Dispose);
            Assert.True(SpinWait.SpinUntil(
                () => lease.Admission.Phase == ProfileAdmissionPhase.Stopping,
                TimeSpan.FromSeconds(10)), "Admission must seal without acquiring the lease monitor.");
            Assert.False(disposal.IsCompleted);
            Assert.Throws<ObjectDisposedException>(() => lease.Admission.EnterStartupWriter());
        }
        finally
        {
            releaseLeaseMonitor.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(10));
            if (disposal is not null)
                await disposal.WaitAsync(TimeSpan.FromSeconds(10));
        }
        lease.Admission.CompleteShutdown();
    }

    [Fact]
    public void TokenConstructorIsPrivateAndNoPublicAdmissionSurfaceIsIntroduced()
    {
        Assert.False(typeof(ProfileAdmission).IsVisible);
        Assert.False(typeof(ProfileAdmissionToken).IsVisible);
        Assert.False(typeof(ProfileAdmissionPhase).IsVisible);
        Assert.False(typeof(ProfileAdmissionKind).IsVisible);
        ConstructorInfo constructor = Assert.Single(typeof(ProfileAdmissionToken)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        Assert.True(constructor.IsPrivate);
    }

    private static ProfileAdmission ReadyAdmission()
    {
        ProfileAdmission admission = new();
        admission.CompleteStartup();
        return admission;
    }

    private static void AssertAllWritersRejected(ProfileAdmission admission)
    {
        Assert.Throws<InvalidOperationException>(() => admission.EnterStartupWriter());
        Assert.Throws<InvalidOperationException>(() => admission.EnterOrdinaryWriter());
        Assert.Throws<InvalidOperationException>(() => { _ = admission.EnterExclusiveAsync(); });
    }

    private static void AssertAllEntriesDisposed(ProfileAdmission admission)
    {
        Assert.Throws<ObjectDisposedException>(() => admission.EnterStartupWriter());
        Assert.Throws<ObjectDisposedException>(() => admission.EnterOrdinaryWriter());
        Assert.Throws<ObjectDisposedException>(() => { _ = admission.EnterExclusiveAsync(); });
    }
}

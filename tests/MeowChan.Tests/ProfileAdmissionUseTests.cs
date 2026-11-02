using MeowChan.Favorites;
using MeowChan.Persistence;

namespace MeowChan.Tests;

public sealed class ProfileAdmissionUseTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    [Fact]
    public void StoreEntryCountsStartupWithoutPromotingItAndOrdinaryOnlyAfterReady()
    {
        using ProfileAdmission admission = new();
        using (ProfileAdmissionToken startup = admission.EnterStoreWriter())
        {
            Assert.Equal(ProfileAdmissionKind.Startup, startup.Kind);
            Assert.Throws<InvalidOperationException>(admission.CompleteStartup);
            Assert.Equal(7, admission.RunWithToken(startup, () => 7, ProfileAdmissionKind.Startup));
        }
        admission.CompleteStartup();
        using ProfileAdmissionToken ordinary = admission.EnterStoreWriter();
        Assert.Equal(ProfileAdmissionKind.Ordinary, ordinary.Kind);
        Assert.Throws<InvalidOperationException>(() => admission.RunWithToken(ordinary, () => 0, ProfileAdmissionKind.Exclusive));
    }

    [Fact]
    public async Task PendingExclusiveRejectsNewStoreEntryButNotExistingTokenUse()
    {
        using ProfileAdmission admission = new();
        admission.CompleteStartup();
        using ProfileAdmissionToken ordinary = admission.EnterOrdinaryWriter();
        using CancellationTokenSource cancel = new();
        Task<ProfileAdmissionToken> exclusive = admission.EnterExclusiveAsync(cancel.Token).AsTask();
        try
        {
            Assert.False(exclusive.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => admission.EnterStoreWriter());
            Assert.Equal(8, admission.RunWithToken(ordinary, () => 8, ProfileAdmissionKind.Ordinary));
            Assert.False(exclusive.IsCompleted);
            ordinary.Dispose();
            using ProfileAdmissionToken granted = await exclusive.WaitAsync(Deadline);
            Assert.Throws<InvalidOperationException>(() => admission.EnterStoreWriter());
        }
        finally
        {
            cancel.Cancel();
            ordinary.Dispose();
            try { (await exclusive.WaitAsync(Deadline)).Dispose(); }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public void NullForeignUnissuedReleasedAndWrongKindTokensNeverRunTheDelegate()
    {
        using ProfileAdmission admission = new();
        using ProfileAdmission other = new();
        using ProfileAdmissionToken foreign = other.EnterStartupWriter();
        using ProfileAdmissionToken unissued = ProfileAdmissionToken.Create(admission, ProfileAdmissionKind.Startup);
        using ProfileAdmissionToken valid = admission.EnterStartupWriter();
        int calls = 0;
        int Work() => ++calls;
        Assert.Throws<InvalidOperationException>(() => admission.RunWithToken(null!, Work, ProfileAdmissionKind.Startup));
        Assert.Throws<InvalidOperationException>(() => admission.RunWithToken(foreign, Work, ProfileAdmissionKind.Startup));
        Assert.Throws<InvalidOperationException>(() => admission.RunWithToken(unissued, Work, ProfileAdmissionKind.Startup));
        Assert.Throws<InvalidOperationException>(() => admission.RunWithToken(valid, Work, ProfileAdmissionKind.Ordinary));
        Assert.Throws<InvalidOperationException>(() => admission.RunWithToken(valid, Work));
        valid.Dispose();
        Assert.Throws<InvalidOperationException>(() => admission.RunWithToken(valid, Work, ProfileAdmissionKind.Startup));
        Assert.Equal(0, calls);
        admission.CompleteStartup();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeCannotGrantExclusiveOrFinishShutdownWhileActualStoreUseIsPinned(bool shutdown)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        ThreadFavoriteStore store = new(temp.Path, lease);
        lease.Admission.CompleteStartup();
        using ProfileAdmissionToken ordinary = lease.Admission.EnterOrdinaryWriter();
        using ManualResetEventSlim written = new();
        using ManualResetEventSlim release = new();
        Task use = Task.Run(() => lease.Admission.RunWithToken(ordinary, () =>
        {
            Assert.True(store.SetAdmitted(new("source", "board", "42"), true, ordinary).Changed);
            written.Set();
            Assert.True(release.Wait(Deadline));
            return true;
        }, ProfileAdmissionKind.Ordinary));
        Task<ProfileAdmissionToken>? exclusive = null;
        Task? drain = null;
        using CancellationTokenSource cancel = new();
        try
        {
            Assert.True(written.Wait(Deadline), "The real admitted store write did not complete.");
            Assert.Single(store.ReadSnapshot().Threads);
            if (shutdown)
            {
                lease.Admission.BeginShutdown();
                drain = lease.Admission.DrainAsync();
            }
            else exclusive = lease.Admission.EnterExclusiveAsync(cancel.Token).AsTask();
            ordinary.Dispose();
            ordinary.Dispose();
            Assert.False((drain ?? exclusive!).IsCompleted);
            Assert.Throws<InvalidOperationException>(() => lease.Admission.RunWithToken(ordinary,
                () => store.SetAdmitted(new("source", "board", "43"), true, ordinary), ProfileAdmissionKind.Ordinary));
            Assert.Single(store.ReadSnapshot().Threads);
            if (shutdown) Assert.Throws<InvalidOperationException>(lease.Admission.CompleteShutdown);
            release.Set();
            await use.WaitAsync(Deadline);
            if (shutdown)
            {
                await drain!.WaitAsync(Deadline);
                lease.Admission.CompleteShutdown();
            }
            else
            {
                using ProfileAdmissionToken granted = await exclusive!.WaitAsync(Deadline);
                Assert.Equal(ProfileAdmissionKind.Exclusive, granted.Kind);
                ordinary.Dispose();
                Assert.Throws<InvalidOperationException>(() => lease.Admission.EnterStoreWriter());
            }
        }
        finally
        {
            release.Set();
            cancel.Cancel();
            ordinary.Dispose();
            await use.WaitAsync(Deadline);
            if (exclusive is not null)
            {
                try { (await exclusive.WaitAsync(Deadline)).Dispose(); }
                catch (OperationCanceledException) { }
            }
        }
    }

    [Fact]
    public void ExceptionReleasesUseButNeverDisposesBorrowedTokenAndLeaseFenceRemains()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        using ProfileAdmissionToken startup = lease.Admission.EnterStartupWriter();
        Assert.Throws<IOException>(() => lease.RunStoreWrite<int>(startup, () => throw new IOException("fixture")));
        Assert.Throws<InvalidOperationException>(lease.Admission.CompleteStartup);
        Assert.Equal(9, lease.RunStoreWrite(startup, () => 9));
        lease.Dispose();
        bool called = false;
        Assert.Throws<ObjectDisposedException>(() => lease.RunStoreWrite(startup, () => called = true));
        Assert.False(called);
        startup.Dispose();
        Assert.True(lease.Admission.DrainAsync().IsCompletedSuccessfully);
    }

    [Fact]
    public void RecoveryFailureAndShutdownRejectNewStoreWritersButKeepAcceptedUse()
    {
        using ProfileAdmission admission = new();
        using ProfileAdmissionToken startup = admission.EnterStartupWriter();
        admission.FailStartup();
        Assert.Throws<InvalidOperationException>(() => admission.EnterStoreWriter());
        Assert.True(admission.RunWithToken(startup, () => true, ProfileAdmissionKind.Startup));
        admission.BeginShutdown();
        Assert.Throws<InvalidOperationException>(() => admission.EnterStoreWriter());
        Assert.True(admission.RunWithToken(startup, () => true, ProfileAdmissionKind.Startup));
        startup.Dispose();
        admission.CompleteShutdown();
        Assert.Throws<InvalidOperationException>(() => admission.EnterStoreWriter());
    }

    [Fact]
    public void ConsumerDelegateDoesNotHoldTheAdmissionMonitor()
    {
        using ProfileAdmission admission = new();
        admission.CompleteStartup();
        using ProfileAdmissionToken ordinary = admission.EnterOrdinaryWriter();
        Assert.True(admission.RunWithToken(ordinary, () =>
        {
            Task another = Task.Run(() => { using ProfileAdmissionToken accepted = admission.EnterStoreWriter(); });
            Assert.True(another.Wait(Deadline), "Consumer code ran while the admission monitor was held.");
            return true;
        }, ProfileAdmissionKind.Ordinary));
    }

    [Fact]
    public void CancelledStoreEntryAndNestedUseLeaveNoPhantomStartupReservation()
    {
        using ProfileAdmission admission = new();
        Assert.ThrowsAny<OperationCanceledException>(() => admission.EnterStoreWriter(new CancellationToken(true)));
        using (ProfileAdmissionToken startup = admission.EnterStoreWriter())
        {
            Assert.Equal(42, admission.RunWithToken(startup,
                () => admission.RunWithToken(startup, () => 42, ProfileAdmissionKind.Startup), ProfileAdmissionKind.Startup));
        }
        admission.CompleteStartup();
    }
}

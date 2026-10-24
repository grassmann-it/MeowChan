using MeowChan.Desktop;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

public sealed class SourceMediaIntegrityPolicyTests
{
    [Fact]
    public async Task DefaultAndInactiveSavedConsentHaveNoAuthority()
    {
        SourceMediaIntegrityPolicyService policy = new("profile-a", "owner", "source");
        policy.SetActive(true);
        Assert.False(policy.GetSnapshot().AllowProviderSizeMd5Mismatch);
        await Assert.ThrowsAsync<InvalidDataException>(() => policy.AcquireAcceptanceLeaseAsync(policy.GetSnapshot().Revision, default).AsTask());
        SourceMediaIntegrityPolicyService saved = new("profile-a", "owner", "source", new("owner", "source", true, 3));
        Assert.True(saved.GetView().IsGranted);
        Assert.False(saved.GetSnapshot().AllowProviderSizeMd5Mismatch);
        await Assert.ThrowsAsync<InvalidDataException>(() => saved.AcquireAcceptanceLeaseAsync(3, default).AsTask());
    }

    [Fact]
    public async Task FailedPersistenceLeavesConsentAndRevisionUnchanged()
    {
        SourceMediaIntegrityPolicyService policy = Active();
        SourceMediaIntegrityPolicySnapshot before = policy.GetSnapshot();
        await Assert.ThrowsAsync<IOException>(() => policy.SetPermissionAsync(true, before.Revision,
            _ => throw new IOException("read only")));
        Assert.Equal(before, policy.GetSnapshot());
        Assert.False(policy.GetView().IsGranted);
    }

    [Fact]
    public async Task RevokeWaitsForLinearizedCommitAndRejectsOldRevisionAfterAck()
    {
        SourceMediaIntegrityPolicyService policy = await Granted();
        long revision = policy.GetSnapshot().Revision;
        MediaAcceptanceLease lease = await policy.AcquireAcceptanceLeaseAsync(revision, default);
        bool persisted = false;
        Task revoke = policy.SetPermissionAsync(false, revision, _ => persisted = true);
        Assert.False(revoke.IsCompleted);
        Assert.False(persisted);
        await lease.DisposeAsync();
        await revoke.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(persisted);
        Assert.False(policy.GetSnapshot().AllowProviderSizeMd5Mismatch);
        await Assert.ThrowsAsync<InvalidDataException>(() => policy.AcquireAcceptanceLeaseAsync(revision, default).AsTask());
        await lease.DisposeAsync(); // Lease release is idempotent.
    }

    [Fact]
    public async Task LifecycleInvalidationIsSynchronousAndDoesNotDrainEarlierLease()
    {
        SourceMediaIntegrityPolicyService policy = await Granted();
        long oldRevision = policy.GetSnapshot().Revision;
        MediaAcceptanceLease earlier = await policy.AcquireAcceptanceLeaseAsync(oldRevision, default);
        policy.SetActive(false);
        Assert.False(policy.GetSnapshot().AllowProviderSizeMd5Mismatch);
        Assert.True(policy.GetView().IsGranted);
        Task<MediaAcceptanceLease> later = policy.AcquireAcceptanceLeaseAsync(oldRevision, default).AsTask();
        await earlier.DisposeAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => later);
        policy.SetActive(true);
        Assert.True(policy.GetSnapshot().AllowProviderSizeMd5Mismatch);
        await Assert.ThrowsAsync<InvalidDataException>(() => policy.AcquireAcceptanceLeaseAsync(oldRevision, default).AsTask());
    }

    [Fact]
    public async Task ConsentAndTargetsAreIsolatedByProfileOwnerAndSource()
    {
        SourceMediaIntegrityPolicyService granted = await Granted();
        foreach (SourceMediaIntegrityPolicyService other in new[]
        {
            new SourceMediaIntegrityPolicyService("profile-b", "owner", "source"),
            new SourceMediaIntegrityPolicyService("profile-a", "other-owner", "source"),
            new SourceMediaIntegrityPolicyService("profile-a", "owner", "other-source")
        })
        {
            other.SetActive(true);
            Assert.False(other.GetSnapshot().AllowProviderSizeMd5Mismatch);
            await Assert.ThrowsAsync<InvalidDataException>(() => other.AcquireAcceptanceLeaseAsync(other.GetSnapshot().Revision, default).AsTask());
        }
        Assert.Equal("profile-a", granted.GetSnapshot().ProfileId);
        Assert.Throws<InvalidDataException>(() => new SourceMediaIntegrityPolicyService("profile-a", "other-owner", "source", new("owner", "source", true, 4)));
    }

    [Fact]
    public async Task SameOwnerReloadRetainsSavedConsentAndRetirementCannotReactivate()
    {
        SourceMediaIntegrityPermission? saved = null;
        SourceMediaIntegrityPolicyService policy = Active();
        await policy.SetPermissionAsync(true, policy.GetSnapshot().Revision, value => saved = value);
        SourceMediaIntegrityPolicyService reload = new("profile-a", "owner", "source", saved);
        reload.SetActive(true);
        Assert.True(reload.GetSnapshot().AllowProviderSizeMd5Mismatch);
        reload.Retire();
        Assert.False(reload.GetSnapshot().AllowProviderSizeMd5Mismatch);
        reload.SetActive(true);
        Assert.False(reload.GetSnapshot().AllowProviderSizeMd5Mismatch);
        await Assert.ThrowsAsync<InvalidDataException>(() => reload.AcquireAcceptanceLeaseAsync(reload.GetSnapshot().Revision, default).AsTask());
    }

    [Fact]
    public async Task RealCancellationAndStaleUiRevisionNeverPersist()
    {
        SourceMediaIntegrityPolicyService policy = Active();
        long stale = policy.GetSnapshot().Revision;
        policy.SetActive(false); policy.SetActive(true);
        await Assert.ThrowsAsync<InvalidDataException>(() => policy.SetPermissionAsync(true, stale, _ => Assert.Fail("Stale permission was persisted")));
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => policy.SetPermissionAsync(true, policy.GetSnapshot().Revision,
            _ => Assert.Fail("Cancelled permission was persisted"), cancellation.Token));
    }

    [Fact]
    public void ExhaustedRevisionStillAllowsSynchronousInvalidation()
    {
        SourceMediaIntegrityPolicyService policy = new("profile-a", "owner", "source", new("owner", "source", true, long.MaxValue - 1));
        policy.SetActive(true);
        Assert.True(policy.GetSnapshot().AllowProviderSizeMd5Mismatch);
        policy.SetActive(false);
        Assert.False(policy.GetSnapshot().AllowProviderSizeMd5Mismatch);
        Assert.Throws<InvalidDataException>(() => policy.SetActive(true));
        policy.Retire();
    }

    private static SourceMediaIntegrityPolicyService Active()
    {
        SourceMediaIntegrityPolicyService policy = new("profile-a", "owner", "source");
        policy.SetActive(true);
        return policy;
    }

    private static async Task<SourceMediaIntegrityPolicyService> Granted()
    {
        SourceMediaIntegrityPolicyService policy = Active();
        await policy.SetPermissionAsync(true, policy.GetSnapshot().Revision, _ => { });
        return policy;
    }
}

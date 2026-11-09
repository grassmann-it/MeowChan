using System;
using System.IO;
using MeowChan.Transfer;

namespace MeowChan.Persistence;

// Marker retirement is deliberately a store primitive, not an Apply result. The
// caller must already possess the owner-bound PrivateStoreBatch and a valid
// startup/exclusive admission token. A marker is removed only after the exact
// marker-bearing after-image and its backup fence have been observed.
internal enum PrivateStoreMarkerRetirementResult
{
    Retired,
    AlreadyRetired,
}

internal static class PrivateStoreMarkerRetirement
{
    internal static void ValidateRequest(PrivateStoreBatch batch, Guid operationId,
        string contentPlanSha256, NativePrivateApplyStoreKind storeKind,
        NativePrivateApplyStoreImage expectedAfter, NativePrivateApplyStoreImage? expectedBackup)
    {
        if (batch is null || !batch.HasChanges || batch.Fence.Kind != storeKind
            || expectedAfter is null)
            throw PrivateStoreBatchFiles.Invalid();

        var expectedMarker = new PrivateStoreOperationMarker(operationId, contentPlanSha256, storeKind);
        PrivateStoreOperationMarker.Validate(expectedMarker, storeKind);
        if (batch.Marker != expectedMarker
            || !PrivateStoreBatchFiles.Same(batch.Fence.ExpectedAfter, expectedAfter))
            throw PrivateStoreBatchFiles.Invalid();

        // Reuse the same fence validation as restart inspection. It rejects
        // malformed/missing/foreign shape before any store IO or publication.
        _ = PrivateStoreRecoveryInspection.Validate(batch.Fence, storeKind, operationId, contentPlanSha256);

        if (storeKind == NativePrivateApplyStoreKind.PostBookmarksAndNotes)
        {
            if (expectedBackup is not null) throw PrivateStoreBatchFiles.Invalid();
            return;
        }

        NativePrivateApplyStoreImage requiredBackup = batch.Fence.Before.Exists
            ? batch.Fence.Before : batch.Fence.BeforeBackup!;
        if (expectedBackup is null || !PrivateStoreBatchFiles.Same(requiredBackup, expectedBackup))
            throw PrivateStoreBatchFiles.Invalid();
    }

    internal static void EnsureNoPending(ProfileLease lease, string fileName)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.EnsureDirectoryIdentity();
        try
        {
            if (PrivateStoreRecoveryInspection.HasPending(lease.ProfileDirectory, fileName))
                throw PrivateStoreBatchFiles.Invalid();
        }
        catch (InvalidDataException) { throw; }
        catch (IOException)
        {
            throw new IOException("Private store recovery evidence could not be verified.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException("Private store recovery evidence could not be verified.");
        }
    }

    internal static void EnsureReadback(ProfileLease lease)
    {
        // Directory identity is checked after the uncancelled readback by each
        // store. A replaced profile must never be reported as successfully freed.
        lease.EnsureDirectoryIdentity();
    }
}

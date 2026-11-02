using System;
using System.IO;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Favorites;

public sealed partial class ThreadFavoriteStore
{
    // Constructor-free disk sample, independent of any former store/batch owner.
    // ExactBefore is not continuous history or permission to retry/Apply. No
    // migration, restore, temp cleanup, marker retirement or publication occurs.
    internal static PrivateStoreBatchInspection InspectRecoveryFence(ProfileLease lease,
        NativePrivateApplyStoreFence fence, Guid operationId, string contentPlanSha256)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return lease.RunExclusive(() =>
        {
            // Invalid caller data is a failure, not an unreadable disk result.
            PrivateStoreOperationMarker marker = PrivateStoreRecoveryInspection.Validate(
                fence, NativePrivateApplyStoreKind.ThreadFavorites, operationId, contentPlanSha256);
            try
            {
                lease.EnsureDirectoryIdentity();
                try
                {
                    const string fileName = "thread-favorites.json";
                    if (PrivateStoreRecoveryInspection.HasPending(lease.ProfileDirectory, fileName))
                        return PrivateStoreBatchInspection.Unreadable;

                    string path = Path.Combine(lease.ProfileDirectory, fileName);
                    var primary = ReadBatchImage(path);
                    var backup = ReadBatchImage(path + ".bak");
                    if (primary.Document is null && backup.Document is not null)
                        return PrivateStoreBatchInspection.Unreadable;

                    return PrivateStoreRecoveryInspection.Classify(fence, marker,
                        primary.Image, primary.Document?.OperationMarker,
                        backup.Image, backup.Document?.OperationMarker);
                }
                finally
                {
                    // Also fence early returns and failed reads. A renamed or
                    // replaced profile must never look like an absent store.
                    lease.EnsureDirectoryIdentity();
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return PrivateStoreBatchInspection.Unreadable;
            }
        });
    }
}

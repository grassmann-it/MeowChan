using System;
using System.IO;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Favorites;

public sealed partial class FavoriteStore
{
    // Constructor-free restart observation, not a batch capability, consent or
    // import admission. ExactBefore describes this sample, never its history.
    internal static PrivateStoreBatchInspection InspectRecoveryFence(ProfileLease lease,
        NativePrivateApplyStoreFence fence, Guid operationId, string contentPlanSha256)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return lease.RunExclusive(() =>
        {
            // Programmer/contract errors must not become an unreadable disk fact.
            PrivateStoreOperationMarker expectedMarker = PrivateStoreRecoveryInspection.Validate(
                fence, NativePrivateApplyStoreKind.MediaFavorites, operationId, contentPlanSha256);
            try
            {
                lease.EnsureDirectoryIdentity();
                try
                {
                    const string fileName = "media-favorites.json";
                    if (PrivateStoreRecoveryInspection.HasPending(lease.ProfileDirectory, fileName))
                        return PrivateStoreBatchInspection.Unreadable;

                    string path = Path.Combine(lease.ProfileDirectory, fileName);
                    var primary = ReadBatchImage(path);
                    var backup = ReadBatchImage(path + ".bak");
                    // A backup alone is recovery evidence, not an empty baseline.
                    if (primary.Document is null && backup.Document is not null)
                        return PrivateStoreBatchInspection.Unreadable;

                    return PrivateStoreRecoveryInspection.Classify(fence, expectedMarker,
                        primary.Image, primary.Document?.OperationMarker,
                        backup.Image, backup.Document?.OperationMarker);
                }
                finally
                {
                    // Also fence pending/error paths. No sampled fact survives a
                    // lost/replaced physical profile directory after the reads.
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

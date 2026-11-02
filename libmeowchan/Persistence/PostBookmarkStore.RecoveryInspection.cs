using System;
using System.IO;
using MeowChan.Transfer;

namespace MeowChan.Persistence;

internal sealed partial class PostBookmarkStore
{
    // Passive restart inspection: no store constructor (which can clean ordinary
    // pending files), migration, repair or note-history/backup access. Only scalar
    // disk facts escape; ExactBefore is a point sample, not continuous history.
    internal static PrivateStoreBatchInspection InspectRecoveryFence(ProfileLease lease,
        NativePrivateApplyStoreFence fence, Guid operationId, string contentPlanSha256)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return lease.RunExclusive(() =>
        {
            // Keep invalid caller arguments outside the disk-failure classifier.
            PrivateStoreOperationMarker marker = PrivateStoreRecoveryInspection.Validate(
                fence, BookmarkStoreKind, operationId, contentPlanSha256);
            try
            {
                lease.EnsureDirectoryIdentity();
                try
                {
                    const string fileName = "post-bookmarks.json";
                    if (PrivateStoreRecoveryInspection.HasPending(lease.ProfileDirectory, fileName))
                        return PrivateStoreBatchInspection.Unreadable;

                    byte[]? bytes = PrivateStoreBatchFiles.ReadBytes(
                        System.IO.Path.Combine(lease.ProfileDirectory, fileName), MaximumDocumentBytes);
                    ParsedFile document = ParseBookmarkBytes(bytes, exactNotes: true);
                    NativePrivateApplyStoreImage image = bytes is null ? PrivateStoreBatchFiles.Missing()
                        : PrivateStoreBatchFiles.Image(bytes, document.SchemaVersion,
                            document.InstanceId, document.Snapshot.Revision);
                    return PrivateStoreRecoveryInspection.Classify(fence, marker, image, document.OperationMarker);
                }
                finally
                {
                    // Also fence early pending and failed payload reads. No result
                    // may describe a replacement of the physically leased profile.
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

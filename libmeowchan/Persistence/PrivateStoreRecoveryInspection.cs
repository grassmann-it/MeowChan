using System;
using System.IO;
using System.Linq;
using MeowChan.Favorites;
using MeowChan.Transfer;

namespace MeowChan.Persistence;

// Disk facts only. No transient after-images, private values, paths, owner tokens
// or mutation rights escape this boundary. In particular ExactBefore is NOT a
// continuous history proof and can never establish NotStartedProven on restart.
internal static class PrivateStoreRecoveryInspection
{
    internal static PrivateStoreOperationMarker Validate(NativePrivateApplyStoreFence fence,
        NativePrivateApplyStoreKind kind, Guid operationId, string contentPlanSha256)
    {
        var marker = new PrivateStoreOperationMarker(operationId, contentPlanSha256, kind);
        PrivateStoreOperationMarker.Validate(marker, kind);
        if (fence is null || fence.Kind != kind) throw PrivateStoreBatchFiles.Invalid();
        long cap = kind == NativePrivateApplyStoreKind.PostBookmarksAndNotes
            ? PostBookmarkStore.MaximumDocumentBytes : FavoriteStore.MaximumBytes;
        CheckImage(fence.Before, cap); CheckImage(fence.ExpectedAfter, cap);
        if (kind == NativePrivateApplyStoreKind.PostBookmarksAndNotes)
        {
            if (fence.BeforeBackup is not null) throw PrivateStoreBatchFiles.Invalid();
        }
        else
        {
            if (fence.BeforeBackup is null) throw PrivateStoreBatchFiles.Invalid();
            CheckImage(fence.BeforeBackup, cap);
        }
        if (!PrivateStoreBatchFiles.Same(fence.Before, fence.ExpectedAfter))
        {
            var after = fence.ExpectedAfter;
            if (!after.Exists || after.Instance is null || fence.Before.Revision == long.MaxValue
                || after.Revision != fence.Before.Revision + 1
                || fence.Before.Instance is Guid previous && previous != after.Instance)
                throw PrivateStoreBatchFiles.Invalid();
        }
        return marker;
    }

    internal static PrivateStoreBatchInspection Classify(NativePrivateApplyStoreFence fence,
        PrivateStoreOperationMarker expectedMarker, NativePrivateApplyStoreImage primaryImage,
        PrivateStoreOperationMarker? primaryMarker, NativePrivateApplyStoreImage? backupImage = null,
        PrivateStoreOperationMarker? backupMarker = null)
    {
        bool favorites = fence.Kind != NativePrivateApplyStoreKind.PostBookmarksAndNotes;
        if (favorites != (backupImage is not null) || backupMarker is not null)
            return PrivateStoreBatchInspection.Drifted;
        bool changes = !PrivateStoreBatchFiles.Same(fence.Before, fence.ExpectedAfter);
        var afterBackup = fence.Before.Exists ? fence.Before : fence.BeforeBackup;
        if (changes && primaryMarker == expectedMarker
            && PrivateStoreBatchFiles.Same(primaryImage, fence.ExpectedAfter)
            && (!favorites || PrivateStoreBatchFiles.Same(backupImage!, afterBackup!)))
            return PrivateStoreBatchInspection.ExactAfterWithMarker;
        if (primaryMarker is null && PrivateStoreBatchFiles.Same(primaryImage, fence.Before)
            && (!favorites || PrivateStoreBatchFiles.Same(backupImage!, fence.BeforeBackup!)))
            return changes ? PrivateStoreBatchInspection.ExactBefore : PrivateStoreBatchInspection.NoChange;
        return PrivateStoreBatchInspection.Drifted;
    }

    // Do not inspect, normalize or clean uncertain temp files. Even an unknown
    // suffix or directory may be evidence, so fail closed without opening it.
    // Enumerate matching names lazily; never materialize the profile inventory.
    internal static bool HasPending(string profileDirectory, string fileName)
        => Directory.EnumerateFileSystemEntries(profileDirectory, fileName + ".pending-*").Any()
            || Directory.EnumerateFileSystemEntries(profileDirectory, fileName + ".private-pending-*").Any()
            || Directory.EnumerateFileSystemEntries(profileDirectory, "." + fileName + ".*.tmp").Any();

    private static void CheckImage(NativePrivateApplyStoreImage? image, long cap)
    {
        if (image is null) throw PrivateStoreBatchFiles.Invalid();
        if (!image.Exists)
        {
            if (image.Schema != 0 || image.Instance is not null || image.Revision != 0
                || image.ByteLength != 0 || image.Sha256 is not null) throw PrivateStoreBatchFiles.Invalid();
        }
        else if (image.Schema is < 1 or > 255 || image.Instance == Guid.Empty || image.Revision < 0
            || image.ByteLength <= 0 || image.ByteLength > cap || !(image.Sha256 is { Length: 64 } hash
                && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw PrivateStoreBatchFiles.Invalid();
    }
}

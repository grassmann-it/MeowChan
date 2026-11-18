using System;
using System.Threading;
using MeowChan.Persistence;

namespace MeowChan.Transfer;

/// <summary>
/// Explicit post-filter profile backup/restore boundary. The backup is a bounded
/// versioned image, not a directory copy; restore is a guarded full replacement and
/// never a merge or a source/download activation.
/// </summary>
internal static class PostFilterProfileBackup
{
    internal static PostFilterTransferExport Capture(PostFilterStore store,
        CancellationToken cancellationToken = default) => PostFilterProfileTransfer.Export(store, cancellationToken);

    internal static PostFilterTransferImport Restore(PostFilterStore target, ReadOnlyMemory<byte> bytes,
        long expectedRevision, CancellationToken cancellationToken = default) =>
        PostFilterProfileTransfer.Import(target, bytes, expectedRevision, cancellationToken);

    internal static PostFilterTransferImport RestoreAdmitted(PostFilterStore target, ReadOnlyMemory<byte> bytes,
        long expectedRevision, ProfileAdmissionToken admission, CancellationToken cancellationToken = default) =>
        PostFilterProfileTransfer.RestoreBackupAdmitted(target, bytes, expectedRevision, admission, cancellationToken);
}

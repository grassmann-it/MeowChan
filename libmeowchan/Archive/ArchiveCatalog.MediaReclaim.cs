using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.Archive;

internal sealed record MediaReclaimCandidate(Guid OperationId, string OperationsParent, string FullDirectory);
internal sealed record MediaReclaimSummary(int Roots, long Bytes);

public sealed partial class ArchiveCatalog
{
    /// <summary>What a reclaim would free right now: whole Full roots of committed merge operations whose
    /// conflicts are all decided (discarded or promoted, at least one discarded), that are not registered in
    /// any thread and are not aliased by the operation's Safe root. Read-only.</summary>
    internal Task<MediaReclaimSummary> PreviewMediaReclaimAsync(CancellationToken cancellationToken = default)
        => ReadAsync(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
            List<MediaReclaimCandidate> candidates = ArchiveSql.ReadReclaimableMediaRoots(connection, transaction);
            transaction.Commit();
            long bytes = 0;
            int roots = 0;
            foreach (MediaReclaimCandidate item in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long size = MediaStore.MeasureCandidateRoot(item.OperationsParent, item.FullDirectory);
                if (size > 0) { roots++; bytes += size; }
            }
            return new MediaReclaimSummary(roots, bytes);
        }, cancellationToken);

    /// <summary>Deletes the roots <see cref="PreviewMediaReclaimAsync"/> reports. Ledger rows and operation
    /// records are untouched; a missing root already means "reclaimed". Repeatable after a crash.</summary>
    internal Task<MediaReclaimSummary> ReclaimMediaAsync(MediaStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(cancellationToken);
        return CompleteOwnedWriteAsync(admission, async () =>
        {
            List<MediaReclaimCandidate> candidates = await WriteAdmittedAsync(admission,
                (connection, transaction) => ArchiveSql.ReadReclaimableMediaRoots(connection, transaction),
                cancellationToken).ConfigureAwait(false);
            int roots = 0;
            long bytes = 0;
            foreach (MediaReclaimCandidate item in candidates)
            {
                long freed = await store.ReclaimCandidateRootAsync(item.OperationsParent, item.FullDirectory, cancellationToken)
                    .ConfigureAwait(false);
                if (freed > 0) { roots++; bytes += freed; }
            }
            return new MediaReclaimSummary(roots, bytes);
        });
    }
}

internal static partial class ArchiveSql
{
    // Final ledger statuses: Discarded=4, Promoted=5. Open=1 and Kept=2 may still be promoted, so they pin the root.
    internal static List<MediaReclaimCandidate> ReadReclaimableMediaRoots(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand command = Command(connection, transaction, """
            SELECT o.operation_id, o.operations_parent, o.full_directory FROM archive_media_merge_operations o
            WHERE o.state='Committed' AND o.full_directory IS NOT NULL
              AND (o.safe_directory IS NULL OR o.safe_directory<>o.full_directory)
              AND EXISTS (SELECT 1 FROM media_conflict c WHERE c.operation_id=o.operation_id AND c.status=4)
              AND NOT EXISTS (SELECT 1 FROM media_conflict c WHERE c.operation_id=o.operation_id AND c.status IN (1,2))
              AND NOT EXISTS (SELECT 1 FROM archive_media_directories d WHERE d.directory=o.full_directory)
            ORDER BY o.operation_id
            """);
        List<MediaReclaimCandidate> items = new();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
            items.Add(new(Guid.ParseExact(reader.GetString(0), "D"), reader.GetString(1), reader.GetString(2)));
        return items;
    }
}

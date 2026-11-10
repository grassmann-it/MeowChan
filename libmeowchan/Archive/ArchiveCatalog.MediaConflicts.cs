using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Archive;

public sealed partial class ArchiveCatalog
{
    // The ledger is written under the caller's admission, like every other archive write. Recording is
    // idempotent: a replay (or a retried Apply) never reopens a kept, discarded or promoted conflict.
    internal Task<(int Inserted, int AlreadyKnown)> RecordMediaConflictsAdmittedAsync(ProfileAdmissionToken admission,
        NativeMediaConflictInventory inventory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return WriteTransferAdmittedAsync(admission, (connection, transaction) =>
            ArchiveSql.InsertMediaConflicts(connection, transaction, inventory.Provenance, inventory.Entries), cancellationToken);
    }

    /// <summary>User decision on one recorded conflict; ordinary store-writer admission, repeatable.</summary>
    internal Task<ArchiveSql.MediaConflictDecisionOutcome> DecideMediaConflictAsync(string identitySha256,
        NativeMediaConflictResolutionStatus status, DateTimeOffset decidedAtUtc, CancellationToken cancellationToken = default)
    {
        ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(cancellationToken);
        return CompleteOwnedWriteAsync(admission,
            () => DecideMediaConflictAdmittedAsync(admission, identitySha256, status, decidedAtUtc, cancellationToken));
    }

    internal Task<ArchiveSql.MediaConflictDecisionOutcome> DecideMediaConflictAdmittedAsync(ProfileAdmissionToken admission,
        string identitySha256, NativeMediaConflictResolutionStatus status, DateTimeOffset decidedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(identitySha256);
        return WriteAdmittedAsync(admission, (connection, transaction) =>
            ArchiveSql.DecideMediaConflict(connection, transaction, identitySha256, status, decidedAtUtc), cancellationToken);
    }
}

public sealed partial class ArchiveReader
{
    internal Task<(int Total, List<PersistedMediaConflict> Items)> ReadMediaConflictsAsync(int offset, int limit,
        CancellationToken cancellationToken = default)
        => ReadAsync((connection, transaction) => ArchiveSql.ReadMediaConflicts(connection, transaction, offset, limit),
            cancellationToken);
}

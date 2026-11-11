using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Archive;

/// <summary>A native import or merge that stopped before its SQL commit (crash, power loss, full disk).</summary>
internal sealed record InterruptedNativeMediaOperation(bool Merge, Guid OperationId, string RequestSha256,
    ResourceId Resource, NativeMediaImportOperationState State, DateTimeOffset ImportedAtUtc, long CandidateBytes);

public sealed partial class ArchiveCatalog
{
    private const int MaximumInterruptedOperations = 32;
    private const string DiscardPrefix = ".discard-";

    /// <summary>
    /// Preparing/Prepared operations without a receipt whose operations parent is the canonical one for their id.
    /// Nothing of them was published: publication happens only in the SQL commit. Read-only. Committed rows,
    /// receipt mismatches and malformed evidence are never listed and keep blocking startup.
    /// </summary>
    internal Task<IReadOnlyList<InterruptedNativeMediaOperation>> ReadInterruptedNativeMediaOperationsAsync(
        CancellationToken cancellationToken = default)
        => ReadAsync<IReadOnlyList<InterruptedNativeMediaOperation>>(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
            List<InterruptedNativeMediaOperation> rows = ArchiveSql.GetPragma(connection, "user_version", transaction) < 7
                ? new() : ReadInterrupted(connection, transaction);
            transaction.Commit();
            return rows.Select(row => row with { CandidateBytes = MeasureDirectory(InterruptedParent(row)) }).ToArray();
        }, cancellationToken);

    /// <summary>
    /// Confirmed discard of every listed interrupted operation, under the startup (or exclusive) admission before
    /// the recovery guard runs. Per operation: its whole canonical operations parent is renamed to a
    /// <c>.discard-*</c> sibling and deleted, then only its own row is removed if it is still uncommitted and
    /// receipt-less. A crash in between leaves the row with a missing parent; discarding again is idempotent.
    /// Threads, posts, media, favorites, ratings, notes and the conflict ledger are untouched.
    /// </summary>
    internal async Task<int> DiscardInterruptedNativeMediaOperationsAdmittedAsync(ProfileAdmissionToken admission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admission);
        if (admission.Kind == ProfileAdmissionKind.Ordinary)
            throw new InvalidOperationException("Discarding interrupted transfers requires startup or exclusive profile admission.");
        List<InterruptedNativeMediaOperation> rows = await RecoveryWriteAsync(admission,
            (connection, transaction) => ReadInterrupted(connection, transaction), cancellationToken).ConfigureAwait(false);
        int discarded = 0;
        foreach (InterruptedNativeMediaOperation row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RemoveOperationsParent(InterruptedParent(row));
            discarded += await RecoveryWriteAsync(admission, (connection, transaction) =>
                ArchiveSql.DeleteInterruptedMediaOperation(connection, transaction, row), cancellationToken).ConfigureAwait(false);
        }
        return discarded;
    }

    // Startup owns the profile before producers exist; a live Apply owner holds Exclusive.
    private Task<T> RecoveryWriteAsync<T>(ProfileAdmissionToken admission,
        Func<SqliteConnection, SqliteTransaction, T> operation, CancellationToken cancellationToken)
        => admission.Kind == ProfileAdmissionKind.Exclusive
            ? WriteTransferAdmittedAsync(admission, operation, cancellationToken)
            : WriteAdmittedAsync(admission, operation, cancellationToken);

    private List<InterruptedNativeMediaOperation> ReadInterrupted(SqliteConnection connection, SqliteTransaction transaction)
        => ArchiveSql.ReadInterruptedMediaOperations(connection, transaction, merge: false, MaximumInterruptedOperations)
            .Concat(ArchiveSql.ReadInterruptedMediaOperations(connection, transaction, merge: true, MaximumInterruptedOperations))
            .Where(row => PathsMatch(row.Parent, InterruptedParent(row.Operation)))
            .Select(row => row.Operation).Take(MaximumInterruptedOperations).ToList();

    private string InterruptedParent(InterruptedNativeMediaOperation row)
        => Path.Combine(Path.GetDirectoryName(DatabasePath)!, row.Merge ? "merge-media-operations" : "import-media-operations",
            row.OperationId.ToString("N"));

    private static bool PathsMatch(string left, string right)
        => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(right),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static long MeasureDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        ArchiveExportMetadataFile.RejectLinks(directory);
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
    }

    private static void RemoveOperationsParent(string parent)
    {
        string root = Path.GetDirectoryName(parent)!;
        if (Directory.Exists(root))
            foreach (string leftover in Directory.EnumerateDirectories(root, DiscardPrefix + "*"))
                if (new DirectoryInfo(leftover).LinkTarget == null) Directory.Delete(leftover, recursive: true);
        if (File.Exists(parent)) throw new InvalidDataException("The interrupted operations parent is occupied by a file.");
        if (!Directory.Exists(parent)) return;
        ArchiveExportMetadataFile.RejectLinks(parent);
        string parked = Path.Combine(root, DiscardPrefix + Guid.NewGuid().ToString("N"));
        Directory.Move(parent, parked);
        Directory.Delete(parked, recursive: true);
    }
}

internal static partial class ArchiveSql
{
    internal static List<(InterruptedNativeMediaOperation Operation, string Parent)> ReadInterruptedMediaOperations(
        SqliteConnection connection, SqliteTransaction transaction, bool merge, int limit)
    {
        string table = merge ? "archive_media_merge_operations" : "archive_media_import_operations";
        string receipts = merge ? "archive_media_merge_receipts" : "archive_media_import_receipts";
        using SqliteCommand command = Command(connection, transaction, $"""
            SELECT o.operation_id,o.request_sha256,o.source,o.board,o.thread,o.state,o.imported_at,o.operations_parent
            FROM {table} o WHERE o.state IN ('Preparing','Prepared')
              AND NOT EXISTS (SELECT 1 FROM {receipts} r WHERE r.operation_id=o.operation_id)
            ORDER BY o.operation_id LIMIT $limit
            """);
        command.Parameters.AddWithValue("$limit", limit);
        List<(InterruptedNativeMediaOperation, string)> rows = new();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!Guid.TryParseExact(reader.GetString(0), "D", out Guid operation)) continue;
            NativeMediaImportOperationState state = Enum.Parse<NativeMediaImportOperationState>(reader.GetString(5));
            DateTimeOffset at = DateTimeOffset.ParseExact(reader.GetString(6), "O", CultureInfo.InvariantCulture);
            rows.Add((new(merge, operation, reader.GetString(1),
                new ResourceId(reader.GetString(2), reader.GetString(3), reader.GetString(4)), state, at, 0), reader.GetString(7)));
        }
        return rows;
    }

    internal static int DeleteInterruptedMediaOperation(SqliteConnection connection, SqliteTransaction transaction,
        InterruptedNativeMediaOperation row)
    {
        string table = row.Merge ? "archive_media_merge_operations" : "archive_media_import_operations";
        string receipts = row.Merge ? "archive_media_merge_receipts" : "archive_media_import_receipts";
        using SqliteCommand command = Command(connection, transaction, $"""
            DELETE FROM {table} WHERE operation_id=$op AND request_sha256=$request AND state IN ('Preparing','Prepared')
              AND NOT EXISTS (SELECT 1 FROM {receipts} r WHERE r.operation_id=$op)
            """);
        command.Parameters.AddWithValue("$op", row.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$request", row.RequestSha256);
        return command.ExecuteNonQuery();
    }
}

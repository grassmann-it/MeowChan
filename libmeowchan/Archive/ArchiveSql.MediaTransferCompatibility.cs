using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

internal static partial class ArchiveSql
{
    // Storage preparation is deliberately not v2 execution support. Project the version
    // in the SAME row/snapshot as the legacy decoder, never in a separate optimistic read.
    private static string MediaPlanVersionColumn(SqliteConnection connection, SqliteTransaction transaction)
        => GetPragma(connection, "user_version", transaction) >= 10
            ? "plan_version AS media_plan_version" : "1 AS media_plan_version";

    private static void RequireLegacyMediaPlanRow(SqliteDataReader reader)
    {
        if (reader.GetValue(reader.GetOrdinal("media_plan_version")) is not long version || version != 1)
            throw new InvalidDataException("This media transfer plan requires version-aware reconciliation.");
    }

    private static void RequireLegacyMediaOperation(SqliteConnection connection, SqliteTransaction transaction,
        bool merge, Guid operation)
    {
        string table = merge ? "archive_media_merge_operations" : "archive_media_import_operations";
        using SqliteCommand command = Command(connection, transaction,
            $"SELECT {MediaPlanVersionColumn(connection, transaction)} FROM {table} WHERE operation_id=$op");
        command.Parameters.AddWithValue("$op", operation.ToString("D"));
        using SqliteDataReader reader = command.ExecuteReader();
        if (reader.Read()) RequireLegacyMediaPlanRow(reader);
    }

    // Apply helpers need a stronger invariant than reconciliation matching: a missing
    // operation must fail before the first content INSERT, not merely return false later.
    private static void RequireLegacyMediaOperationForApply(SqliteConnection connection, SqliteTransaction transaction,
        bool merge, Guid operation)
    {
        string table = merge ? "archive_media_merge_operations" : "archive_media_import_operations";
        using SqliteCommand command = Command(connection, transaction,
            $"SELECT {MediaPlanVersionColumn(connection, transaction)} FROM {table} WHERE operation_id=$op");
        command.Parameters.AddWithValue("$op", operation.ToString("D"));
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("The media transfer operation is missing.");
        RequireLegacyMediaPlanRow(reader);
    }

    // Strict legacy-format discovery, not the supported-history or startup decision.
    // Individual legacy readers still reject every v2 row; callers that only need
    // compatible historical coexistence use HasIncompatibleMediaTransferHistory.
    internal static bool HasUnsupportedMediaTransferRecords(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (transaction is null)
        {
            using SqliteTransaction snapshot = connection.BeginTransaction(deferred: true);
            return HasUnsupportedMediaTransferRecords(connection, snapshot);
        }
        if (GetPragma(connection, "user_version", transaction) < 10) return false;
        using SqliteCommand command = Command(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM archive_media_import_operations
                WHERE typeof(plan_version) IS NOT 'integer' OR plan_version IS NOT 1)
            OR EXISTS(SELECT 1 FROM archive_media_import_receipts
                WHERE typeof(plan_version) IS NOT 'integer' OR plan_version IS NOT 1)
            OR EXISTS(SELECT 1 FROM archive_media_merge_operations
                WHERE typeof(plan_version) IS NOT 'integer' OR plan_version IS NOT 1)
            OR EXISTS(SELECT 1 FROM archive_media_merge_receipts
                WHERE typeof(plan_version) IS NOT 'integer' OR plan_version IS NOT 1);
            """);
        return (long)command.ExecuteScalar()! != 0;
    }

    private static void RequireLegacyMediaTransferRecords(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (HasIncompatibleMediaTransferHistory(connection, transaction))
            throw new InvalidDataException("This media transfer plan requires version-aware reconciliation.");
    }

    // Unlike startup, a legacy phase may have its own live Preparing/Prepared row.
    // Preserve that v1 behavior, but do not seal an otherwise usable profile merely
    // because a different, fully reconciled v2 import/merge remains in durable history.
    // This is coexistence only: it cannot grant legacy execution of a v2 operation.
    internal static bool HasIncompatibleMediaTransferHistory(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (transaction is null)
        {
            using SqliteTransaction snapshot = connection.BeginTransaction(deferred: true);
            return HasIncompatibleMediaTransferHistory(connection, snapshot);
        }
        if (!HasUnsupportedMediaTransferRecords(connection, transaction)) return false;
        try
        {
            return HasPersistedMediaOperationRecoveryBlocker(connection, transaction, merge: false,
                    CancellationToken.None, nonLegacyOnly: true)
                || HasPersistedMediaReceiptRecoveryBlocker(connection, transaction, merge: false,
                    CancellationToken.None, nonLegacyOnly: true)
                || HasPersistedMediaOperationRecoveryBlocker(connection, transaction, merge: true,
                    CancellationToken.None, nonLegacyOnly: true)
                || HasPersistedMediaReceiptRecoveryBlocker(connection, transaction, merge: true,
                    CancellationToken.None, nonLegacyOnly: true);
        }
        catch (Exception error) when (error is InvalidDataException or JsonException
            or ArgumentException or FormatException or InvalidCastException or OverflowException)
        { return true; }
    }

}

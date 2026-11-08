using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

internal static partial class ArchiveSql
{
    // Shared startup/independent-outcome predicate, not an execution or replay grant.
    // The caller validates the catalog schema and must treat SQL/read failures as
    // recovery-required. Do not call this from a mutation's phase transition: its
    // own Preparing/Prepared row intentionally blocks the whole recovery lane.
    internal static bool HasNativeMediaTransferRecoveryBlocker(SqliteConnection connection,
        SqliteTransaction? transaction, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(connection);
        if (transaction is null)
        {
            using SqliteTransaction snapshot = connection.BeginTransaction(deferred: true);
            return HasNativeMediaTransferRecoveryBlocker(connection, snapshot, ct);
        }
        if (!ReferenceEquals(transaction.Connection, connection))
            throw new InvalidOperationException("Media transfer recovery requires the caller's active transaction.");

        int version = GetPragma(connection, "user_version", transaction);
        ct.ThrowIfCancellationRequested();
        if (version is < 1 or > SchemaVersion) return true;
        if (version < 7) return false; // Media transfer tables do not exist yet.
        if (version < 10)
            return HasLegacyNativeMediaTransferRecoveryBlocker(connection, transaction, version, ct);

        try
        {
            // Do not use the public page readers here: each starts its own snapshot.
            // Page small headers, then use the W32b2 bounded single-row readers in
            // THIS transaction. Only one decoded operation/catalog is retained at a
            // time (32 MiB combined serialized-document bound); no all-history list,
            // catalog parsing, filesystem probe or candidate-stage dependency.
            // Preserve the v1 startup contract after a catalog upgrade too: historical
            // operation payloads are not replayed or reparsed merely to resume startup.
            // The v1 scalar gate still requires its own exact identity/counters receipt.
            // Only non-legacy evidence needs the new typed, bounded v2 validation.
            bool blocked = HasLegacyNativeMediaTransferRecoveryBlocker(connection, transaction, version, ct)
                || HasPersistedMediaOperationRecoveryBlocker(connection, transaction, merge: false, ct, nonLegacyOnly: true)
                || HasPersistedMediaOperationRecoveryBlocker(connection, transaction, merge: true, ct, nonLegacyOnly: true)
                || HasPersistedMediaReceiptRecoveryBlocker(connection, transaction, merge: false, ct, nonLegacyOnly: true)
                || HasPersistedMediaReceiptRecoveryBlocker(connection, transaction, merge: true, ct, nonLegacyOnly: true);
            ct.ThrowIfCancellationRequested();
            return blocked;
        }
        catch (Exception error) when (error is InvalidDataException or JsonException
            or ArgumentException or FormatException or InvalidCastException or OverflowException)
        {
            // Malformed evidence is a blocker, never absence. Cancellation and SQL
            // failures are not swallowed; neither permits a caller to resume writes.
            ct.ThrowIfCancellationRequested();
            return true;
        }
    }

    private readonly record struct MediaTransferRecoveryOperationHeader(
        NativeMediaReceiptIdentity Identity, NativeMediaImportOperationState State);

    private static bool HasPersistedMediaOperationRecoveryBlocker(SqliteConnection connection,
        SqliteTransaction transaction, bool merge, CancellationToken ct, bool nonLegacyOnly = false)
    {
        string table = merge ? "archive_media_merge_operations" : "archive_media_import_operations";
        Guid? after = null;
        while (true)
        {
            List<MediaTransferRecoveryOperationHeader> page = new(32);
            Guid? next = null;
            // The 33rd row is only a bounded lookahead. Do not decode it until the
            // next page, so a single pass never retains more than 32 small headers.
            // A NULL first cursor includes corrupt empty/NULL IDs instead of silently
            // skipping them; only validated canonical IDs become subsequent cursors.
            using (SqliteCommand command = Command(connection, transaction, $"""
                SELECT {PersistedMediaReceiptIdentityProjection},
                    state, typeof(state) AS _type_state, length(CAST(state AS BLOB)) AS _bytes_state
                FROM {table}
                WHERE ($after IS NULL OR operation_id > $after COLLATE BINARY)
                    AND ($nonLegacyOnly=0 OR typeof(plan_version) IS NOT 'integer' OR plan_version IS NOT 1)
                ORDER BY operation_id COLLATE BINARY LIMIT 33
                """))
            {
                command.Parameters.AddWithValue("$after", (object?)after?.ToString("D") ?? DBNull.Value);
                command.Parameters.AddWithValue("$nonLegacyOnly", nonLegacyOnly ? 1 : 0);
                ct.ThrowIfCancellationRequested();
                using SqliteDataReader reader = command.ExecuteReader();
                while (ReadMediaTransferRecoveryRow(reader, ct))
                {
                    if (page.Count == 32)
                    {
                        next = page[^1].Identity.OperationId;
                        break;
                    }
                    NativeMediaReceiptIdentity identity = ReadMediaTransferRecoveryIdentity(reader);
                    NativeMediaImportOperationState state = ReadPersistedMediaState(reader);
                    page.Add(new(identity, state));
                }
            }
            foreach (MediaTransferRecoveryOperationHeader header in page)
            {
                ct.ThrowIfCancellationRequested();
                if (header.State != NativeMediaImportOperationState.Committed) return true;
                if (merge)
                {
                    NativeMediaPersistedMergeOperation? operation = ReadPersistedMediaMergeOperation(
                        connection, transaction, header.Identity.OperationId,
                        header.Identity.RequestSha256, ct);
                    NativeMediaPersistedMergeReceipt? receipt = ReadPersistedMediaMergeReceipt(
                        connection, transaction, header.Identity.OperationId,
                        header.Identity.RequestSha256, header.Identity.PlanVersion, ct);
                    if (operation is null || receipt is null
                        || !PersistedMediaMergeReceiptMatches(operation, receipt)) return true;
                }
                else
                {
                    NativeMediaPersistedImportOperation? operation = ReadPersistedMediaImportOperation(
                        connection, transaction, header.Identity.OperationId,
                        header.Identity.RequestSha256, ct);
                    NativeMediaPersistedImportReceipt? receipt = ReadPersistedMediaImportReceipt(
                        connection, transaction, header.Identity.OperationId,
                        header.Identity.RequestSha256, header.Identity.PlanVersion, ct);
                    // Typed comparison includes request, version, state and EVERY counter.
                    // Typed decoding also enforces v1 zero defaults and v2 selection /
                    // inventory identity; catalog bytes remain opaque persistence evidence.
                    if (operation is null || receipt is null
                        || !PersistedMediaImportReceiptMatches(operation, receipt)) return true;
                }
            }
            if (next is null) return false;
            after = next;
            ct.ThrowIfCancellationRequested();
        }
    }

    private static bool HasPersistedMediaReceiptRecoveryBlocker(SqliteConnection connection,
        SqliteTransaction transaction, bool merge, CancellationToken ct, bool nonLegacyOnly = false)
    {
        string table = merge ? "archive_media_merge_receipts" : "archive_media_import_receipts";
        Guid? after = null;
        while (true)
        {
            List<NativeMediaReceiptIdentity> page = new(32);
            Guid? next = null;
            using (SqliteCommand command = Command(connection, transaction, $"""
                SELECT {PersistedMediaReceiptIdentityProjection}
                FROM {table}
                WHERE ($after IS NULL OR operation_id > $after COLLATE BINARY)
                    AND ($nonLegacyOnly=0 OR typeof(plan_version) IS NOT 'integer' OR plan_version IS NOT 1)
                ORDER BY operation_id COLLATE BINARY LIMIT 33
                """))
            {
                command.Parameters.AddWithValue("$after", (object?)after?.ToString("D") ?? DBNull.Value);
                command.Parameters.AddWithValue("$nonLegacyOnly", nonLegacyOnly ? 1 : 0);
                ct.ThrowIfCancellationRequested();
                using SqliteDataReader reader = command.ExecuteReader();
                while (ReadMediaTransferRecoveryRow(reader, ct))
                {
                    if (page.Count == 32)
                    {
                        next = page[^1].OperationId;
                        break;
                    }
                    page.Add(ReadMediaTransferRecoveryIdentity(reader));
                }
            }
            foreach (NativeMediaReceiptIdentity identity in page)
            {
                ct.ThrowIfCancellationRequested();
                // Check ALL receipts, not just those reached through surviving operations.
                // A valid orphan receipt is historical success, for either supported
                // version/family. Unknown versions and malformed counters still seal the lane.
                // No target, operation or files are needed to attest historical success.
                if (merge)
                {
                    if (ReadPersistedMediaMergeReceipt(connection, transaction, identity.OperationId,
                        identity.RequestSha256, identity.PlanVersion, ct) is null) return true;
                }
                else if (ReadPersistedMediaImportReceipt(connection, transaction, identity.OperationId,
                    identity.RequestSha256, identity.PlanVersion, ct) is null) return true;
            }
            if (next is null) return false;
            after = next;
            ct.ThrowIfCancellationRequested();
        }
    }

    private static NativeMediaReceiptIdentity ReadMediaTransferRecoveryIdentity(SqliteDataReader reader)
    {
        // Bound strings and require their storage classes BEFORE materializing them.
        // Reuse the receipt decoder for canonical GUID/hash and supported-version checks.
        string operation = ReadPersistedMediaText(reader, "operation_id", maximumBytes: 36)!;
        string request = ReadPersistedMediaText(reader, "request_sha256", maximumBytes: 64)!;
        int version = ReadPersistedMediaCount(reader, "plan_version");
        if (!Guid.TryParseExact(operation, "D", out Guid id)) throw InvalidPersistedMedia();
        return ReadPersistedMediaReceiptIdentity(reader, id, request, version);
    }

    private static bool ReadMediaTransferRecoveryRow(SqliteDataReader reader, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        bool present = reader.Read();
        ct.ThrowIfCancellationRequested();
        return present;
    }

    private static bool HasLegacyNativeMediaTransferRecoveryBlocker(SqliteConnection connection,
        SqliteTransaction transaction, int version, CancellationToken ct)
    {
        // Preserve pre-schema10 semantics: no new typed-document/counter rules for
        // old journals, and orphan receipts remain history. Schema7 has no merge tables.
        // Schema10+ contains both versions: filter only legacy operations here and
        // never let a v2 receipt satisfy a v1 operation with matching old counters.
        string legacyOperation = version >= 10 ? "o.plan_version=1 AND " : "";
        string legacyReceipt = version >= 10 ? "AND r.plan_version=1" : "";
        string import = $"""
            SELECT EXISTS (
                SELECT 1 FROM archive_media_import_operations AS o
                WHERE {legacyOperation}(o.state IS NOT 'Committed' OR NOT EXISTS (
                    SELECT 1 FROM archive_media_import_receipts AS r
                    WHERE r.operation_id = o.operation_id COLLATE BINARY
                      AND r.request_sha256 = o.request_sha256 COLLATE BINARY
                      {legacyReceipt}
                      AND r.post_count = o.post_count AND r.attachment_count = o.attachment_count
                ))
            )
            """;
        string merge = $"""
             OR EXISTS (
                SELECT 1 FROM archive_media_merge_operations AS o
                WHERE {legacyOperation}(o.state IS NOT 'Committed' OR NOT EXISTS (
                    SELECT 1 FROM archive_media_merge_receipts AS r
                    WHERE r.operation_id = o.operation_id COLLATE BINARY
                      AND r.request_sha256 = o.request_sha256 COLLATE BINARY
                      {legacyReceipt}
                      AND r.new_posts = o.new_posts AND r.updated_posts = o.updated_posts
                      AND r.new_attachments = o.new_attachments AND r.new_bindings = o.new_bindings
                      AND r.conflicts = o.conflicts
                ))
            )
            """;
        using SqliteCommand command = Command(connection, transaction, import + (version >= 8 ? merge : ""));
        ct.ThrowIfCancellationRequested();
        bool blocked = (long)command.ExecuteScalar()! != 0;
        ct.ThrowIfCancellationRequested();
        return blocked;
    }
}

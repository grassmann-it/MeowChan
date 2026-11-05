using System;
using System.IO;
using System.Text;
using System.Threading;
using MeowChan.Storage;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

internal static partial class ArchiveSql
{
    private static readonly UTF8Encoding LocalReadUtf8 = new(false, true);

    /// <summary>
    /// Binds a read to the compatible v9 target, never to a legacy empty-result convention.
    /// No generation comparison: a historical receipt survives later content changes.
    /// </summary>
    internal static (string Instance, long Generation) RequireLocalTarget(
        SqliteConnection c, SqliteTransaction tx, string expectedInstance)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(tx);
        if (!IsLocalReadUuid(expectedInstance, out _))
            throw new ArgumentException("A canonical nonempty catalog instance is required.", nameof(expectedInstance));
        try
        {
            // Check the header first. In particular v8 must not reach any local-table/fence read.
            if (GetPragma(c, "user_version", tx) != 9 || GetPragma(c, "application_id", tx) != ApplicationId)
                throw new LocalImportReadException(LocalImportError.TargetReplaced);

            // Bound and type-check BEFORE the shared schema validator's older fence decoder.
            // LIMIT 2 also rejects a malformed fence table containing extra singleton rows.
            string instance;
            long generation;
            using (SqliteCommand command = Command(c, tx, """
                SELECT typeof(singleton)='integer',singleton,
                    typeof(instance)='text',length(CAST(instance AS BLOB)),CAST(instance AS BLOB),
                    typeof(generation)='integer',generation
                FROM archive_import_fence LIMIT 2
                """))
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                if (!reader.Read() || reader.GetInt64(0) != 1 || reader.IsDBNull(1) || reader.GetInt64(1) != 1)
                    throw new LocalImportReadException(LocalImportError.TargetReplaced);
                instance = ReadLocalBoundedText(reader, 2, 36, 36, LocalImportError.TargetReplaced);
                if (!IsLocalReadUuid(instance, out _) || reader.GetInt64(5) != 1 || reader.IsDBNull(6))
                    throw new LocalImportReadException(LocalImportError.TargetReplaced);
                generation = reader.GetInt64(6);
                if (generation < 0 || reader.Read())
                    throw new LocalImportReadException(LocalImportError.TargetReplaced);
            }
            // Reuse the one L1 manifest/compatibility validator in precisely the caller's tx.
            ValidateSchema(c, tx);
            if (!string.Equals(instance, expectedInstance, StringComparison.Ordinal))
                throw new LocalImportReadException(LocalImportError.TargetReplaced);
            return (instance, generation);
        }
        catch (SqliteException e) { throw LocalReadSqliteFailure(e, validatingTarget: true); }
        catch (InvalidDataException) { throw new LocalImportReadException(LocalImportError.TargetReplaced); }
        catch (InvalidCastException) { throw new LocalImportReadException(LocalImportError.TargetReplaced); }
        catch (FormatException) { throw new LocalImportReadException(LocalImportError.TargetReplaced); }
        catch (OverflowException) { throw new LocalImportReadException(LocalImportError.TargetReplaced); }
    }

    internal static LocalCollectionImportReceipt? ReadLocalImportReceipt(
        SqliteConnection c, SqliteTransaction tx, LocalImportReceiptKey key,
        LocalImportCodec codec, CancellationToken ct)
    {
        // Caller argument errors stay codec/argument errors and occur before SQL.
        LocalImportCodec.ValidateReceiptKey(key);
        ArgumentNullException.ThrowIfNull(codec);
        return ReadBoundLocalReceipt(c, tx, new(key.CatalogInstance, key.OperationId), key.RequestSha256, codec, ct);
    }

    internal static LocalCollectionImportReceipt? ReadLocalImportReceiptForRecovery(
        SqliteConnection c, SqliteTransaction tx, LocalImportRecoveryKey key,
        LocalImportCodec codec, CancellationToken ct)
    {
        LocalImportCodec.ValidateRecoveryKey(key);
        ArgumentNullException.ThrowIfNull(codec);
        return ReadBoundLocalReceipt(c, tx, key, expectedRequestSha256: null, codec, ct);
    }

    private static LocalCollectionImportReceipt? ReadBoundLocalReceipt(
        SqliteConnection c, SqliteTransaction tx, LocalImportRecoveryKey key,
        string? expectedRequestSha256, LocalImportCodec codec, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            _ = RequireLocalTarget(c, tx, key.CatalogInstance);
            ct.ThrowIfCancellationRequested();
            // Lookup by operation ONLY, never by digest. The BLOB alternative uses the same
            // indexed key bytes to detect a corrupt storage class, not to accept/coerce it.
            using SqliteCommand command = Command(c, tx, """
                SELECT typeof(operation_id)='text',length(CAST(operation_id AS BLOB)),CAST(operation_id AS BLOB),
                    typeof(request_sha256)='text',length(CAST(request_sha256 AS BLOB)),CAST(request_sha256 AS BLOB),
                    typeof(catalog_instance)='text',length(CAST(catalog_instance AS BLOB)),CAST(catalog_instance AS BLOB),
                    typeof(collection_id)='text',length(CAST(collection_id AS BLOB)),CAST(collection_id AS BLOB),
                    typeof(receipt_json)='text',length(CAST(receipt_json AS BLOB)),CAST(receipt_json AS BLOB)
                FROM local_import_receipts
                WHERE operation_id COLLATE BINARY IN ($operation,CAST($operation AS BLOB)) LIMIT 2
                """);
            command.Parameters.AddWithValue("$operation", key.OperationId.ToString("D"));
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                // Only this branch is absence: compatible, same-instance v9 in the same tx.
                return null;
            }
            LocalCollectionImportReceipt receipt = DecodeLocalReceiptRow(reader, codec, ct);
            if (reader.Read()) throw new LocalImportReadException(LocalImportError.IntegrityFailure);
            if (receipt.OperationId != key.OperationId || receipt.CatalogInstance != key.CatalogInstance)
                throw new LocalImportReadException(LocalImportError.IntegrityFailure);
            if (expectedRequestSha256 != null && receipt.RequestSha256 != expectedRequestSha256)
                throw new LocalImportReadException(LocalImportError.OperationConflict);
            ct.ThrowIfCancellationRequested();
            return receipt;
        }
        catch (LocalImportReadException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (SqliteException e)
        {
            ct.ThrowIfCancellationRequested();
            throw LocalReadSqliteFailure(e, validatingTarget: false);
        }
        catch (InvalidCastException)
        {
            ct.ThrowIfCancellationRequested();
            throw new LocalImportReadException(LocalImportError.IntegrityFailure);
        }
    }

    // One shared row decoder for the full receipt key and the digest-less recovery key.
    // First validate the SQL header, then JSON against THAT header, finally the caller key.
    private static LocalCollectionImportReceipt DecodeLocalReceiptRow(
        SqliteDataReader reader, LocalImportCodec codec, CancellationToken ct)
    {
        string operation = ReadLocalBoundedText(reader, 0, 36, 36);
        string hash = ReadLocalBoundedText(reader, 3, 64, 64);
        string instance = ReadLocalBoundedText(reader, 6, 36, 36);
        string collection = ReadLocalBoundedText(reader, 9, 36, 36);
        if (!IsLocalReadUuid(operation, out Guid operationId) || !IsLocalReadUuid(instance, out _)
            || !IsLocalReadUuid(collection, out Guid collectionId) || !IsLocalReadHash(hash))
            throw new LocalImportReadException(LocalImportError.IntegrityFailure);
        byte[] json = ReadLocalBoundedBytes(reader, 12, LocalImportBounds.MaximumReceiptBytes,
            oversize: LocalImportError.BudgetExceeded);
        ct.ThrowIfCancellationRequested();
        LocalCollectionImportReceipt receipt;
        try { receipt = codec.DecodeReceipt(json, new(instance, operationId, hash)); }
        catch (LocalImportCodecException e)
        {
            ct.ThrowIfCancellationRequested();
            // Here these two codes mean JSON disagrees with its redundant SQL header,
            // not that the target changed or that the caller supplied another digest.
            throw new LocalImportReadException(e.Error is LocalImportError.TargetReplaced or LocalImportError.OperationConflict
                ? LocalImportError.IntegrityFailure : e.Error);
        }
        if (receipt.CollectionId.Value != collectionId)
            throw new LocalImportReadException(LocalImportError.IntegrityFailure);
        return receipt;
    }

    // Each projection is (typeof(column)='text', raw UTF-8 length, CAST(column AS BLOB)).
    // Never call GetString on stored TEXT: that can replace invalid UTF-8 before validation.
    private static byte[] ReadLocalBoundedBytes(SqliteDataReader reader, int ordinal, int maximum,
        int? exactLength = null, LocalImportError malformed = LocalImportError.IntegrityFailure,
        LocalImportError oversize = LocalImportError.IntegrityFailure)
    {
        if (reader.GetInt64(ordinal) != 1 || reader.IsDBNull(ordinal + 1) || reader.IsDBNull(ordinal + 2))
            throw new LocalImportReadException(malformed);
        long length = reader.GetInt64(ordinal + 1);
        if (length > maximum) throw new LocalImportReadException(oversize);
        if (length < 1 || (exactLength.HasValue && length != exactLength.Value))
            throw new LocalImportReadException(malformed);
        // Materialize only after SQL type/byte bounds; GetBytes/GetStream can probe
        // origin-column metadata for CAST expressions, which have no source table.
        byte[] bytes = reader.GetFieldValue<byte[]>(ordinal + 2);
        if (bytes.LongLength != length) throw new LocalImportReadException(malformed);
        return bytes;
    }

    private static string ReadLocalBoundedText(SqliteDataReader reader, int ordinal, int maximum, int exactLength,
        LocalImportError malformed = LocalImportError.IntegrityFailure)
    {
        byte[] bytes = ReadLocalBoundedBytes(reader, ordinal, maximum, exactLength, malformed, malformed);
        try { return LocalReadUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw new LocalImportReadException(malformed); }
    }

    private static bool IsLocalReadUuid(string? text, out Guid value)
        => Guid.TryParseExact(text, "D", out value) && value != Guid.Empty && text == value.ToString("D");

    private static bool IsLocalReadHash(string text)
    {
        if (text.Length != 64) return false;
        foreach (char c in text) if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    private static LocalImportReadException LocalReadSqliteFailure(SqliteException e, bool validatingTarget)
        => new(e.SqliteErrorCode switch
        {
            5 or 6 => LocalImportError.Busy,                 // SQLITE_BUSY / SQLITE_LOCKED
            18 => LocalImportError.BudgetExceeded,          // SQLITE_TOOBIG
            1 or 17 or 20 => validatingTarget               // ERROR / SCHEMA / MISMATCH at a known projection
                ? LocalImportError.TargetReplaced : LocalImportError.IntegrityFailure,
            _ => LocalImportError.IoFailure                 // Includes IOERR / CORRUPT / CANTOPEN / NOTADB.
        });
}

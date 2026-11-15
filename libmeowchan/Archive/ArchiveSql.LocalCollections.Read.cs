using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using MeowChan.Storage;
using Microsoft.Data.Sqlite;

namespace MeowChan.Archive;

internal static partial class ArchiveSql
{
    private static readonly UTF8Encoding LocalReadUtf8 = new(false, true);

    /// <summary>
    /// Binds a read to the compatible v9+ target, never to a legacy empty-result convention.
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
            int version = GetPragma(c, "user_version", tx);
            if (version < 9 || version > SchemaVersion || GetPragma(c, "application_id", tx) != ApplicationId)
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
                // Only this branch is absence: a compatible target in the same tx.
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

    // R1 metadata-only reads. R0 above remains an independent, bound receipt lookup.
    internal static LocalCollectionPage ReadLocalCollectionPage(
        SqliteConnection c, SqliteTransaction tx, int offset, int limit, CancellationToken ct)
    {
        LocalImportCodec.ValidatePage(offset, limit);
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(tx);
        ct.ThrowIfCancellationRequested();
        try
        {
            if (ReadLocalMetadataTarget(c, tx, ct) == null)
                return new(0, offset, ImmutableArray<LocalCollectionSummary>.Empty);
            long total;
            using (SqliteCommand count = Command(c, tx, "SELECT COUNT(*) FROM local_collections"))
                total = (long)count.ExecuteScalar()!;
            ct.ThrowIfCancellationRequested();
            var headers = new List<LocalCollectionSummary>(limit);
            using (SqliteCommand command = Command(c, tx, """
                SELECT typeof(collection_id)='text',length(CAST(collection_id AS BLOB)),CAST(collection_id AS BLOB),
                    typeof(title)='text',length(CAST(title AS BLOB)),CAST(title AS BLOB),
                    typeof(revision)='integer',revision
                FROM local_collections ORDER BY collection_id COLLATE BINARY LIMIT $limit OFFSET $offset
                """))
            {
                command.Parameters.AddWithValue("$limit", limit);
                command.Parameters.AddWithValue("$offset", offset);
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    headers.Add(ReadLocalCollectionHeader(reader));
                }
            }
            var page = ImmutableArray.CreateBuilder<LocalCollectionSummary>(headers.Count);
            foreach (LocalCollectionSummary header in headers)
            {
                ct.ThrowIfCancellationRequested();
                // Scalar bounded summary probe only; no claims/slot/payload materialization.
                using SqliteCommand count = Command(c, tx, """
                    SELECT COUNT(*),COALESCE(MAX(CASE WHEN NOT EXISTS (
                        SELECT 1 FROM local_media_revisions r
                        WHERE r.collection_id=$id AND r.item_id=i.item_id
                            AND typeof(r.role)='integer' AND r.role=1
                    ) THEN 1 ELSE 0 END),0)
                    FROM (SELECT item_id FROM local_items WHERE collection_id=$id LIMIT 1001) i
                    """);
                count.Parameters.AddWithValue("$id", header.Id.ToString());
                using SqliteDataReader reader = count.ExecuteReader();
                if (!reader.Read()) throw LocalMetadataInvalid();
                int itemCount = RequireLocalItemCount(reader.GetInt64(0));
                if (reader.GetInt64(1) != 0) throw LocalMetadataInvalid();
                page.Add(header with { ItemCount = itemCount });
            }
            ct.ThrowIfCancellationRequested();
            return new(total, offset, page.MoveToImmutable());
        }
        catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (LocalImportCodecException e) { throw new LocalImportReadException(e.Error); }
        catch (SqliteException e) { throw LocalReadSqliteFailure(e, validatingTarget: false); }
        catch (InvalidDataException) { throw LocalMetadataInvalid(); }
        catch (InvalidCastException) { throw LocalMetadataInvalid(); }
        catch (FormatException) { throw LocalMetadataInvalid(); }
        catch (OverflowException) { throw new LocalImportReadException(LocalImportError.BudgetExceeded); }
    }

    internal static LocalCollectionSnapshot? ReadLocalCollection(
        SqliteConnection c, SqliteTransaction tx, LocalCollectionId id,
        string trustedProfileDirectory, LocalImportCodec codec, CancellationToken ct)
    {
        if (id.Value == Guid.Empty) throw new ArgumentException("A nonempty local collection is required.", nameof(id));
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedProfileDirectory);
        ArgumentNullException.ThrowIfNull(codec);
        ct.ThrowIfCancellationRequested();
        try
        {
            var fence = ReadLocalMetadataTarget(c, tx, ct);
            if (fence == null) return null; // Valid legacy only, never a bound recovery result.
            LocalCollectionSummary header;
            using (SqliteCommand command = Command(c, tx, """
                SELECT typeof(collection_id)='text',length(CAST(collection_id AS BLOB)),CAST(collection_id AS BLOB),
                    typeof(title)='text',length(CAST(title AS BLOB)),CAST(title AS BLOB),
                    typeof(revision)='integer',revision
                FROM local_collections
                WHERE collection_id COLLATE BINARY IN ($id,CAST($id AS BLOB)) LIMIT 2
                """))
            {
                command.Parameters.AddWithValue("$id", id.ToString());
                using SqliteDataReader reader = command.ExecuteReader();
                if (!reader.Read()) { ct.ThrowIfCancellationRequested(); return null; }
                header = ReadLocalCollectionHeader(reader);
                if (header.Id != id || reader.Read()) throw LocalMetadataInvalid();
            }
            ct.ThrowIfCancellationRequested();
            int itemCount;
            using (SqliteCommand count = Command(c, tx, """
                SELECT COUNT(*) FROM (SELECT 1 FROM local_items WHERE collection_id=$id LIMIT 1001)
                """))
            {
                count.Parameters.AddWithValue("$id", id.ToString());
                itemCount = RequireLocalItemCount((long)count.ExecuteScalar()!);
            }
            ct.ThrowIfCancellationRequested();
            List<LocalCollectionReadRow> rows = ReadLocalCollectionRows(c, tx, id, itemCount, ct);
            ct.ThrowIfCancellationRequested();
            ImmutableArray<LocalMediaRevisionSnapshot> items = ReadLocalCollectionClaims(c, tx, id, rows, codec, ct);
            // Reuse L0 for semantic bounds, global item/revision identity uniqueness, exact
            // stored slots and their common operation root; never substitute recomputed slots.
            codec.ValidateSelection(new(header.Title, items.Select(item => new LocalImportInput(
                item.Reference.ItemId.ToString(), item.DisplayName, item.Claims)).ToImmutableArray()));
            ct.ThrowIfCancellationRequested();
            _ = codec.EncodeContent(new(1, rows.Select(row => row.Content).ToImmutableArray()));
            ct.ThrowIfCancellationRequested();
            Guid operation = Guid.ParseExact(rows[0].Content.PayloadRelativeSlot.Split('/')[1], "N");
            var result = new LocalCollectionSnapshot(header with { ItemCount = itemCount }, items,
                fence.Value.Instance, fence.Value.Generation, trustedProfileDirectory, operation,
                rows.Select(row => row.Content.PayloadRelativeSlot).ToImmutableArray());
            ct.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (LocalImportCodecException e) { throw new LocalImportReadException(e.Error); }
        catch (SqliteException e) { throw LocalReadSqliteFailure(e, validatingTarget: false); }
        catch (InvalidDataException) { throw LocalMetadataInvalid(); }
        catch (InvalidCastException) { throw LocalMetadataInvalid(); }
        catch (FormatException) { throw LocalMetadataInvalid(); }
        catch (OverflowException) { throw new LocalImportReadException(LocalImportError.BudgetExceeded); }
    }

    // Null means valid unbound legacy only. No expected instance, no receipt/generation comparison.
    private static (string Instance, long Generation)? ReadLocalMetadataTarget(
        SqliteConnection c, SqliteTransaction tx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int version = GetPragma(c, "user_version", tx);
        if (version < 1 || version > SchemaVersion || GetPragma(c, "application_id", tx) != ApplicationId)
            throw LocalMetadataInvalid();
        if (version < 9)
        {
            ValidateSchema(c, tx); // Existing version-aware checks, no new local table/fence access.
            ct.ThrowIfCancellationRequested();
            return null;
        }
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
            if (!reader.Read() || ReadLocalMetadataInteger(reader, 0) != 1) throw LocalMetadataInvalid();
            instance = ReadLocalBoundedText(reader, 2, 36, 36);
            generation = ReadLocalMetadataInteger(reader, 5);
            if (!IsLocalReadUuid(instance, out _) || generation < 0 || reader.Read()) throw LocalMetadataInvalid();
        }
        ct.ThrowIfCancellationRequested();
        ValidateSchema(c, tx); // Only after bounded raw instance validation in this exact tx.
        ct.ThrowIfCancellationRequested();
        return (instance, generation);
    }

    private static LocalCollectionSummary ReadLocalCollectionHeader(SqliteDataReader reader)
    {
        string rawId = ReadLocalBoundedText(reader, 0, 36, 36);
        if (!IsLocalReadUuid(rawId, out Guid id)) throw LocalMetadataInvalid();
        string title = ReadLocalMetadataText(reader, 3, LocalImportBounds.MaximumTitleBytes, allowEmpty: true);
        if (ReadLocalMetadataInteger(reader, 6) != 1) throw LocalMetadataInvalid();
        // Temporary validation input is not an import selection identity or persisted request.
        new LocalImportCodec().ValidateSelection(new(title,
            ImmutableArray.Create(new LocalImportInput(rawId, null, null))));
        return new(new(id), title, 1, 0);
    }

    private sealed record LocalCollectionReadRow(LocalImportContentItem Content, string? DisplayName, long? ClaimsBytes);

    private static List<LocalCollectionReadRow> ReadLocalCollectionRows(
        SqliteConnection c, SqliteTransaction tx, LocalCollectionId id, int itemCount, CancellationToken ct)
    {
        using SqliteCommand command = Command(c, tx, """
            SELECT typeof(i.item_id)='text',length(CAST(i.item_id AS BLOB)),CAST(i.item_id AS BLOB),
                typeof(i.ordinal)='integer',i.ordinal,
                typeof(r.collection_id)='text',length(CAST(r.collection_id AS BLOB)),CAST(r.collection_id AS BLOB),
                typeof(r.item_id)='text',length(CAST(r.item_id AS BLOB)),CAST(r.item_id AS BLOB),
                typeof(r.revision_id)='text',length(CAST(r.revision_id AS BLOB)),CAST(r.revision_id AS BLOB),
                typeof(r.role)='integer',r.role,typeof(r.length)='integer',r.length,
                typeof(r.sha256)='text',length(CAST(r.sha256 AS BLOB)),CAST(r.sha256 AS BLOB),
                typeof(r.payload_relative_path)='text',length(CAST(r.payload_relative_path AS BLOB)),CAST(r.payload_relative_path AS BLOB),
                typeof(r.display_name)='text',length(CAST(r.display_name AS BLOB)),CAST(r.display_name AS BLOB),
                typeof(i.claims_json)='text',length(CAST(i.claims_json AS BLOB)),i.claims_json IS NULL
            FROM local_items i LEFT JOIN local_media_revisions r
                ON r.collection_id=i.collection_id AND r.item_id=i.item_id
            WHERE i.collection_id=$id
            ORDER BY i.ordinal,i.item_id COLLATE BINARY,r.revision_id COLLATE BINARY LIMIT 1001
            """);
        command.Parameters.AddWithValue("$id", id.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<LocalCollectionReadRow>(itemCount);
        long claimsTotal = 0;
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (rows.Count == LocalImportBounds.MaximumItems)
                throw new LocalImportReadException(LocalImportError.BudgetExceeded);
            string item = ReadLocalBoundedText(reader, 0, 36, 36);
            long ordinal = ReadLocalMetadataInteger(reader, 3);
            string collection = ReadLocalBoundedText(reader, 5, 36, 36);
            string revisionItem = ReadLocalBoundedText(reader, 8, 36, 36);
            string revision = ReadLocalBoundedText(reader, 11, 36, 36);
            if (!IsLocalReadUuid(item, out Guid itemId) || !IsLocalReadUuid(revision, out Guid revisionId)
                || collection != id.ToString() || revisionItem != item || ordinal != rows.Count)
                throw LocalMetadataInvalid();
            long role = ReadLocalMetadataInteger(reader, 14);
            long length = ReadLocalMetadataInteger(reader, 16);
            if (role != (long)LocalMediaRole.Original || length < 0) throw LocalMetadataInvalid();
            string hash = ReadLocalBoundedText(reader, 18, 64, 64);
            if (!IsLocalReadHash(hash)) throw LocalMetadataInvalid();
            string slot = ReadLocalMetadataText(reader, 21, LocalImportBounds.MaximumRelativeSlotBytes);
            string? display = IsLocalMetadataNull(reader, 24) ? null
                : ReadLocalMetadataText(reader, 24, LocalImportBounds.MaximumDisplayNameBytes, allowEmpty: true);
            long? claimsBytes = null;
            if (reader.GetInt64(29) == 1)
            {
                if (reader.GetInt64(27) != 0 || !reader.IsDBNull(28)) throw LocalMetadataInvalid();
            }
            else
            {
                if (reader.GetInt64(27) != 1 || reader.IsDBNull(28)) throw LocalMetadataInvalid();
                long bytes = reader.GetInt64(28);
                if (bytes > LocalImportBounds.MaximumClaimsBytes) throw new LocalImportReadException(LocalImportError.BudgetExceeded);
                if (bytes < 1) throw LocalMetadataInvalid();
                claimsTotal = checked(claimsTotal + bytes);
                if (claimsTotal > LocalImportBounds.MaximumCollectionClaimsBytes)
                    throw new LocalImportReadException(LocalImportError.BudgetExceeded);
                claimsBytes = bytes;
            }
            LocalMediaRevisionReference reference = new(id, new(itemId), new(revisionId));
            rows.Add(new(new(reference, (int)ordinal, LocalMediaRole.Original, length, hash, slot), display, claimsBytes));
        }
        if (rows.Count != itemCount) throw LocalMetadataInvalid();
        return rows;
    }

    private static ImmutableArray<LocalMediaRevisionSnapshot> ReadLocalCollectionClaims(
        SqliteConnection c, SqliteTransaction tx, LocalCollectionId id, List<LocalCollectionReadRow> rows,
        LocalImportCodec codec, CancellationToken ct)
    {
        using SqliteCommand command = Command(c, tx, """
            SELECT typeof(item_id)='text',length(CAST(item_id AS BLOB)),CAST(item_id AS BLOB),
                typeof(claims_json)='text',length(CAST(claims_json AS BLOB)),CAST(claims_json AS BLOB)
            FROM local_items WHERE collection_id=$id ORDER BY ordinal,item_id COLLATE BINARY LIMIT 1001
            """);
        command.Parameters.AddWithValue("$id", id.ToString());
        using SqliteDataReader reader = command.ExecuteReader();
        var items = ImmutableArray.CreateBuilder<LocalMediaRevisionSnapshot>(rows.Count);
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (items.Count >= rows.Count) throw LocalMetadataInvalid();
            LocalCollectionReadRow row = rows[items.Count];
            if (ReadLocalBoundedText(reader, 0, 36, 36) != row.Content.Reference.ItemId.ToString()) throw LocalMetadataInvalid();
            LocalSourceClaims? claims = null;
            if (row.ClaimsBytes == null)
            {
                if (!IsLocalMetadataNull(reader, 3)) throw LocalMetadataInvalid();
            }
            else
            {
                byte[] bytes = ReadLocalMetadataBytes(reader, 3, LocalImportBounds.MaximumClaimsBytes);
                if (bytes.LongLength != row.ClaimsBytes.Value) throw LocalMetadataInvalid();
                ct.ThrowIfCancellationRequested();
                claims = codec.DecodeClaims(bytes);
                ct.ThrowIfCancellationRequested();
            }
            LocalImportContentItem content = row.Content;
            items.Add(new(content.Reference, content.Ordinal, content.Role, content.Length, content.Sha256, row.DisplayName, claims));
        }
        if (items.Count != rows.Count) throw LocalMetadataInvalid();
        return items.MoveToImmutable();
    }

    private static int RequireLocalItemCount(long count)
    {
        if (count > LocalImportBounds.MaximumItems) throw new LocalImportReadException(LocalImportError.BudgetExceeded);
        if (count < 1) throw LocalMetadataInvalid();
        return (int)count;
    }

    private static long ReadLocalMetadataInteger(SqliteDataReader reader, int ordinal)
    {
        if (reader.GetInt64(ordinal) != 1 || reader.IsDBNull(ordinal + 1)) throw LocalMetadataInvalid();
        return reader.GetInt64(ordinal + 1);
    }

    // Only SQL NULL is absence. BLOB/INTEGER/TEXT-null are not silently made optional.
    private static bool IsLocalMetadataNull(SqliteDataReader reader, int ordinal)
        => reader.GetInt64(ordinal) == 0 && reader.IsDBNull(ordinal + 1) && reader.IsDBNull(ordinal + 2);

    private static byte[] ReadLocalMetadataBytes(SqliteDataReader reader, int ordinal, int maximum, bool allowEmpty = false)
    {
        if (reader.GetInt64(ordinal) != 1 || reader.IsDBNull(ordinal + 1) || reader.IsDBNull(ordinal + 2))
            throw LocalMetadataInvalid();
        long length = reader.GetInt64(ordinal + 1);
        if (length > maximum) throw new LocalImportReadException(LocalImportError.BudgetExceeded);
        if (length < (allowEmpty ? 0 : 1)) throw LocalMetadataInvalid();
        // Bounded before allocation; never GetBytes/GetStream on a computed expression.
        byte[] bytes = reader.GetFieldValue<byte[]>(ordinal + 2);
        if (bytes.LongLength != length) throw LocalMetadataInvalid();
        return bytes;
    }

    private static string ReadLocalMetadataText(SqliteDataReader reader, int ordinal, int maximum, bool allowEmpty = false)
    {
        byte[] bytes = ReadLocalMetadataBytes(reader, ordinal, maximum, allowEmpty);
        try { return LocalReadUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw LocalMetadataInvalid(); }
    }

    private static LocalImportReadException LocalMetadataInvalid() => new(LocalImportError.IntegrityFailure);
}

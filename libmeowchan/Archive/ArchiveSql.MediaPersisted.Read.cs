using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Archive;

internal static partial class ArchiveSql
{
    private const long PersistedMediaPageBytes = 32L * 1024 * 1024;
    private const int PersistedMediaDocumentBytes = 8 * 1024 * 1024;
    private const int PersistedMediaCatalogBytes = 16 * 1024 * 1024;

    // SQL evidence only. Keep the original v1 readers and all execution gates separate.
    internal static NativeMediaPersistedImportOperation? ReadPersistedMediaImportOperation(
        SqliteConnection connection, SqliteTransaction? transaction, Guid operation, string request,
        CancellationToken ct = default)
        => ReadPersistedMediaImportOperation(connection, transaction, operation, request, ct, v2Only: false);

    // Explicit v2 single-row lookup. The plan-version boundary stays in SQL so a legacy row
    // with the same operation id can never be decoded through the v2 inspection surface.
    internal static NativeMediaPersistedImportOperation? ReadPersistedMediaImportV2Operation(
        SqliteConnection connection, SqliteTransaction? transaction, Guid operation, string request,
        CancellationToken ct = default)
        => ReadPersistedMediaImportOperation(connection, transaction, operation, request, ct, v2Only: true);

    private static NativeMediaPersistedImportOperation? ReadPersistedMediaImportOperation(
        SqliteConnection connection, SqliteTransaction? transaction, Guid operation, string request,
        CancellationToken ct, bool v2Only)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(connection);
        ValidateMediaImportIdentity(operation, request);
        if (transaction is null)
        {
            using SqliteTransaction snapshot = connection.BeginTransaction(deferred: true);
            return ReadPersistedMediaImportOperation(connection, snapshot, operation, request, ct, v2Only);
        }
        RequirePersistedMediaSchema(connection, transaction);
        string versionFilter = v2Only ? " AND plan_version=2" : string.Empty;
        using SqliteCommand command = Command(connection, transaction,
            $"SELECT {PersistedMediaProjection(false)} FROM archive_media_import_operations WHERE operation_id=$op{versionFilter}");
        command.Parameters.AddWithValue("$op", operation.ToString("D"));
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        ct.ThrowIfCancellationRequested();
        if (PersistedMediaRowBytes(reader, false) > PersistedMediaPageBytes) throw InvalidPersistedMedia();
        NativeMediaPersistedImportOperation found = ReadPersistedMediaImportRow(reader);
        if (found.Identity.OperationId != operation || found.Identity.RequestSha256 != request)
            throw InvalidPersistedMedia();
        return found;
    }

    internal static NativeMediaPersistedPage<NativeMediaPersistedImportOperation> ReadPersistedMediaImportOperationsPage(
        SqliteConnection connection, Guid? after, CancellationToken ct = default)
        => ReadPersistedMediaImportOperationsPage(connection, transaction: null, after, ct, v2Only: false);

    // Explicit v2 paging keeps the version boundary in SQL. It is intentionally a
    // separate entry point so a v2 cursor never skips over v2 rows because of
    // interleaved legacy operations. The caller may supply its own transaction to
    // bind operation rows and receipts to one read snapshot.
    internal static NativeMediaPersistedPage<NativeMediaPersistedImportOperation> ReadPersistedMediaImportV2OperationsPage(
        SqliteConnection connection, SqliteTransaction? transaction, Guid? after, CancellationToken ct = default)
        => ReadPersistedMediaImportOperationsPage(connection, transaction, after, ct, v2Only: true);

    private static NativeMediaPersistedPage<NativeMediaPersistedImportOperation> ReadPersistedMediaImportOperationsPage(
        SqliteConnection connection, SqliteTransaction? transaction, Guid? after, CancellationToken ct, bool v2Only)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(connection);
        if (after == Guid.Empty) throw new ArgumentException("Invalid media operation cursor.", nameof(after));
        if (transaction is null)
        {
            using SqliteTransaction owned = connection.BeginTransaction(deferred: true);
            return ReadPersistedMediaImportOperationsPage(connection, owned, after, ct, v2Only);
        }
        RequirePersistedMediaSchema(connection, transaction);
        string versionFilter = v2Only ? " AND plan_version=2" : string.Empty;
        using SqliteCommand command = Command(connection, transaction,
            $"SELECT {PersistedMediaProjection(false)} FROM archive_media_import_operations "
            + $"WHERE operation_id>$after COLLATE BINARY{versionFilter} ORDER BY operation_id COLLATE BINARY LIMIT 33");
        command.Parameters.AddWithValue("$after", after?.ToString("D") ?? "");
        using SqliteDataReader reader = command.ExecuteReader();
        var items = ImmutableArray.CreateBuilder<NativeMediaPersistedImportOperation>();
        long bytes = 0;
        Guid? next = null;
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (items.Count == 32) { next = items[^1].Identity.OperationId; break; }
            long size = PersistedMediaRowBytes(reader, false);
            if (size > PersistedMediaPageBytes - bytes)
            {
                if (items.Count == 0) throw InvalidPersistedMedia();
                next = items[^1].Identity.OperationId; break;
            }
            bytes += size;
            items.Add(ReadPersistedMediaImportRow(reader));
        }
        ct.ThrowIfCancellationRequested();
        return new(items.ToImmutable(), next);
    }

    private static NativeMediaPersistedImportOperation ReadPersistedMediaImportRow(SqliteDataReader reader)
    {
        NativeMediaPersistedIdentity identity = ReadPersistedMediaIdentity(reader);
        NativeMediaImportOperationState state = ReadPersistedMediaState(reader);
        NativeMediaPersistedRoot full = ReadPersistedMediaRoot(reader, identity, state, merge: false, safe: false);
        ImmutableArray<byte> catalog = ReadPersistedMediaCatalog(reader, optional: identity.PlanVersion == 1);
        var counts = new NativeMediaImportCounts(ReadPersistedMediaCount(reader, "post_count"),
            ReadPersistedMediaCount(reader, "attachment_count"), ReadPersistedMediaCount(reader, "evidence_additions"),
            ReadPersistedMediaCount(reader, "derived_descriptor_additions"), ReadPersistedMediaCount(reader, "contained_derived_additions"),
            ReadPersistedMediaCount(reader, "original_binding_count"));
        if (identity.PlanVersion == 1)
        {
            if (!reader.IsDBNull(reader.GetOrdinal("catalog_bytes")) || counts.AttachmentCount <= 0
                || counts.EvidenceAdditions != 0 || counts.DerivedDescriptorAdditions != 0
                || counts.ContainedDerivedAdditions != 0 || counts.OriginalBindingCount != 0)
                throw InvalidPersistedMedia();
        }
        else
        {
            NativeMediaCandidateInventory inventory = full.Selection!.CreateInventory(identity.OperationId,
                identity.PackageFingerprint, identity.ImportedAtUtc);
            if (counts.EvidenceAdditions != inventory.OriginalEvidence.Length
                || counts.DerivedDescriptorAdditions != inventory.DerivedRepresentations.Length
                || counts.ContainedDerivedAdditions != inventory.DerivedRepresentations.Count(d => d.IncomingStatus == MediaDerivedStatus.Contained)
                || counts.OriginalBindingCount != inventory.Bindings.Length)
                throw InvalidPersistedMedia();
        }
        return new(identity, state, full, catalog, counts);
    }

    private static void RequirePersistedMediaSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        // The four persisted transfer tables keep their Schema10 shape in Schema11;
        // Schema11 adds only the post attachment-presence column. Historical evidence
        // readers therefore support exactly both headers, while future/older headers
        // remain rejected before any row is decoded.
        if (GetPragma(connection, "user_version", transaction) is not (10 or 11))
            throw InvalidPersistedMedia();
    }

    private static string PersistedMediaProjection(bool merge)
    {
        MediaTransferTable10 table = MediaTransferTables10().Single(t => t.Operation && t.Import != merge);
        var names = table.ColumnNames.Split(',').ToList();
        names.Add("plan_version");
        names.AddRange(MediaTransferCounters10(!merge));
        names.AddRange(["full_selection_json", "full_selection_sha256", "full_inventory_sha256", "full_member_count"]);
        if (merge) names.AddRange(["safe_selection_json", "safe_selection_sha256", "safe_inventory_sha256", "safe_member_count"]);
        else names.Add("catalog_bytes");
        // Only fixed internal column identifiers enter this projection, never stored data.
        IEnumerable<string> fields = names.Concat(PersistedMediaVariableColumns(merge).SelectMany(c => new[]
        { $"typeof({c}) AS _type_{c}", $"length(CAST({c} AS BLOB)) AS _bytes_{c}" }));
        return string.Join(",", fields);
    }

    private static IEnumerable<string> PersistedMediaVariableColumns(bool merge)
    {
        string[] common = ["operation_id", "request_sha256", "package_fingerprint", "catalog_instance", "source", "board", "thread",
            "operations_parent", "imported_at", "state", "catalog_bytes", "full_selection_json", "full_selection_sha256", "full_inventory_sha256"];
        return common.Concat(merge
            ? new[] { "roots_json", "full_keys_json", "safe_keys_json", "full_directory", "safe_directory",
                "full_manifest_generation", "safe_manifest_generation", "full_bindings_sha256", "safe_bindings_sha256",
                "safe_selection_json", "safe_selection_sha256", "safe_inventory_sha256" }
            : new[] { "revision_keys_json", "candidate_directory", "manifest_generation", "bindings_sha256" });
    }

    private static long PersistedMediaRowBytes(SqliteDataReader reader, bool merge)
    {
        long total = 0;
        foreach (string column in PersistedMediaVariableColumns(merge))
        {
            bool document = column.EndsWith("_json", StringComparison.Ordinal) || column == "catalog_bytes";
            int limit = column == "catalog_bytes" ? PersistedMediaCatalogBytes
                : document ? PersistedMediaDocumentBytes : 1024 * 1024;
            long size = PersistedMediaValueBytes(reader, column, column == "catalog_bytes" ? "blob" : "text", limit, optional: true);
            // Scalar columns are bounded too, but the page's document budget concerns
            // every JSON/catalog document, including both copies of an aliased selection.
            if (document) total = checked(total + size);
        }
        return total;
    }

    private static long PersistedMediaValueBytes(SqliteDataReader reader, string column, string type, int maximumBytes, bool optional)
    {
        string actual = reader.GetString(reader.GetOrdinal("_type_" + column));
        int lengthOrdinal = reader.GetOrdinal("_bytes_" + column);
        if (actual == "null")
        {
            if (!optional || !reader.IsDBNull(lengthOrdinal)) throw InvalidPersistedMedia();
            return 0;
        }
        if (actual != type || reader.GetValue(lengthOrdinal) is not long size || size < 0 || size > maximumBytes)
            throw InvalidPersistedMedia();
        return size;
    }

    private static string? ReadPersistedMediaText(SqliteDataReader reader, string column, bool optional = false, int maximumBytes = 1048576)
    {
        _ = PersistedMediaValueBytes(reader, column, "text", maximumBytes, optional);
        int ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static int ReadPersistedMediaCount(SqliteDataReader reader, string column)
    {
        if (reader.GetValue(reader.GetOrdinal(column)) is not long value || value < 0 || value > int.MaxValue)
            throw InvalidPersistedMedia();
        return (int)value;
    }

    private static ImmutableArray<byte> ReadPersistedMediaCatalog(SqliteDataReader reader, bool optional)
    {
        _ = PersistedMediaValueBytes(reader, "catalog_bytes", "blob", PersistedMediaCatalogBytes, optional);
        int ordinal = reader.GetOrdinal("catalog_bytes");
        return reader.IsDBNull(ordinal) ? ImmutableArray<byte>.Empty : ImmutableArray.CreateRange((byte[])reader.GetValue(ordinal));
    }

    private static NativeMediaPersistedIdentity ReadPersistedMediaIdentity(SqliteDataReader reader)
    {
        int version = ReadPersistedMediaCount(reader, "plan_version");
        if (version is not (1 or 2)) throw InvalidPersistedMedia();
        string operationText = ReadPersistedMediaText(reader, "operation_id", maximumBytes: 36)!;
        if (!Guid.TryParseExact(operationText, "D", out Guid operation) || operation == Guid.Empty || operationText != operation.ToString("D"))
            throw InvalidPersistedMedia();
        string request = ReadPersistedMediaHash(reader, "request_sha256", optional: false)!;
        string fingerprint = ReadPersistedMediaHash(reader, "package_fingerprint", optional: false)!;
        string instance = ReadPersistedMediaText(reader, "catalog_instance", maximumBytes: 36)!;
        if (!Guid.TryParseExact(instance, "D", out Guid guid) || guid == Guid.Empty || instance != guid.ToString("D"))
            throw InvalidPersistedMedia();
        if (reader.GetValue(reader.GetOrdinal("catalog_generation")) is not long generation || generation < 0)
            throw InvalidPersistedMedia();
        // Archive identities allow 256 UTF-16 code units. A BMP scalar can consume
        // three UTF-8 bytes per code unit; the preallocation bound must not reject
        // otherwise valid identifiers before their existing semantic validation.
        const int maximumIdentifierBytes = 3 * 256;
        var resource = new ResourceId(ReadPersistedMediaText(reader, "source", maximumBytes: maximumIdentifierBytes)!,
            ReadPersistedMediaText(reader, "board", maximumBytes: maximumIdentifierBytes)!,
            ReadPersistedMediaText(reader, "thread", maximumBytes: maximumIdentifierBytes)!);
        try { ArchiveValidation.ValidateId(resource); }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid persisted media transfer evidence.", error); }
        string parent = ReadPersistedMediaText(reader, "operations_parent")!;
        if (string.IsNullOrWhiteSpace(parent)) throw InvalidPersistedMedia();
        DateTimeOffset importedAt;
        try { importedAt = ParseDate(ReadPersistedMediaText(reader, "imported_at", maximumBytes: 64)!); }
        catch (FormatException error) { throw new InvalidDataException("Invalid persisted media transfer evidence.", error); }
        if (importedAt == default) throw InvalidPersistedMedia();
        return new(version, operation, request, fingerprint, instance, generation, resource, parent, importedAt);
    }

    private static NativeMediaImportOperationState ReadPersistedMediaState(SqliteDataReader reader)
        => ParseMediaMergeState(ReadPersistedMediaText(reader, "state", maximumBytes: 16)!);

    private static string? ReadPersistedMediaHash(SqliteDataReader reader, string column, bool optional = true)
    {
        string? hash = ReadPersistedMediaText(reader, column, optional, 64);
        if (hash != null && !IsMediaMergeHash(hash)) throw InvalidPersistedMedia();
        return hash;
    }

    private static NativeMediaPersistedRoot ReadPersistedMediaRoot(SqliteDataReader reader, NativeMediaPersistedIdentity identity,
        NativeMediaImportOperationState state, bool merge, bool safe)
    {
        if (safe && !merge) throw new ArgumentException("Import has no Safe root.", nameof(safe));
        string prefix = safe ? "safe" : "full";
        string keysName = merge ? prefix + "_keys_json" : "revision_keys_json";
        string keysJson = ReadPersistedMediaText(reader, keysName, maximumBytes: PersistedMediaDocumentBytes)!;
        string? bindingHash = ReadPersistedMediaHash(reader, merge ? prefix + "_bindings_sha256" : "bindings_sha256");
        string? directory = ReadPersistedMediaText(reader, merge ? prefix + "_directory" : "candidate_directory", optional: true);
        string? generation = ReadPersistedMediaHash(reader, merge ? prefix + "_manifest_generation" : "manifest_generation");
        string? json = ReadPersistedMediaText(reader, prefix + "_selection_json", optional: true, maximumBytes: PersistedMediaDocumentBytes);
        string? selectionHash = ReadPersistedMediaHash(reader, prefix + "_selection_sha256");
        string? inventoryHash = ReadPersistedMediaHash(reader, prefix + "_inventory_sha256");
        int count = ReadPersistedMediaCount(reader, prefix + "_member_count");
        if (count > NativeMediaCandidateSelection.MaximumMembers) throw InvalidPersistedMedia();
        NativeMediaCandidateSelection? selection = null;
        ImmutableArray<string> keys;
        bool hasMembers;
        if (identity.PlanVersion == 1)
        {
            if (json != null || selectionHash != null || inventoryHash != null || count != 0) throw InvalidPersistedMedia();
            keys = ReadMergeKeys(keysJson, prefix);
            if (!safe && keys.IsEmpty) throw InvalidPersistedMedia();
            hasMembers = !keys.IsEmpty;
        }
        else
        {
            if (keysJson != "[]" || bindingHash != null || !safe && count == 0) throw InvalidPersistedMedia();
            keys = ImmutableArray<string>.Empty;
            hasMembers = count > 0;
            if (!hasMembers)
            {
                if (json != null || selectionHash != null || inventoryHash != null) throw InvalidPersistedMedia();
            }
            else
            {
                if (json == null || selectionHash == null) throw InvalidPersistedMedia();
                selection = NativeMediaCandidateSelection.Parse(json);
                if (selection.Members.Length != count || selection.Sha256 != selectionHash
                    || selection.Members.Any(m => m.Id.Occurrence.Resource != identity.Resource)) throw InvalidPersistedMedia();
                // Includes operation/package/time provenance and all roles. No filesystem I/O.
                string reconstructed = selection.CreateInventory(identity.OperationId, identity.PackageFingerprint, identity.ImportedAtUtc).Sha256;
                if (state != NativeMediaImportOperationState.Preparing && inventoryHash != reconstructed) throw InvalidPersistedMedia();
            }
        }
        if (state == NativeMediaImportOperationState.Preparing || !hasMembers)
        {
            if (directory != null || generation != null || bindingHash != null || inventoryHash != null) throw InvalidPersistedMedia();
        }
        else if (string.IsNullOrWhiteSpace(directory) || generation == null
            || identity.PlanVersion == 1 && bindingHash == null || identity.PlanVersion == 2 && inventoryHash == null)
            throw InvalidPersistedMedia();
        return new(keysJson, keys, bindingHash, selection, selectionHash, count, inventoryHash, directory, generation);
    }

    private static InvalidDataException InvalidPersistedMedia() => new("Invalid persisted media transfer evidence.");
}

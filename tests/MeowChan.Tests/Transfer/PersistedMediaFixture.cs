using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Storage;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests.Transfer;

/// <summary>
/// Synthetic, connection-local persistence evidence for W32b2.2. This fixture creates
/// the real Schema10 tables but never creates a profile, media root or validated package.
/// </summary>
internal sealed class PersistedMediaFixture : IDisposable
{
    internal static readonly ResourceId Resource = new("fixture", "board", "100");
    internal static readonly Guid OperationId = Guid.ParseExact("cd220000-0000-4000-8000-000000000001", "D");
    internal const string Request = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    internal const string Fingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal const string Instance = "cd220000-0000-4000-8000-000000000002";
    internal const long Generation = 7;
    internal static readonly DateTimeOffset ImportedAt = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    internal static readonly NativeMediaCandidateSelection Full = NativeMediaCandidateSelection.Create(
        [Evidence(), Evidence("11")]);

    private const string ManifestGeneration = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string LegacyBindingsSha256 = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";

    internal SqliteConnection Connection { get; }

    internal PersistedMediaFixture()
    {
        Connection = new SqliteConnection("Data Source=:memory:;Pooling=False;Foreign Keys=True");
        try
        {
            Connection.Open();
            using SqliteTransaction transaction = Connection.BeginTransaction(deferred: false);
            ArchiveSql.CreateMediaTransferSchema10(Connection, transaction);
            using SqliteCommand stamp = Connection.CreateCommand();
            stamp.Transaction = transaction;
            stamp.CommandText = "PRAGMA user_version=10";
            stamp.ExecuteNonQuery();
            transaction.Commit();
        }
        catch
        {
            Connection.Dispose();
            throw;
        }
    }

    internal static NativeMediaSelectedMember Evidence(string post = "10") => new(new(
        new(new(Resource, post, 0, null), null, NativeMediaMemberRole.OriginalEvidence),
        new CapturedAttachment(0, null, IsDeleted: true), false, TransferContentStatus.Unknown,
        null, null, null), false);

    /// <summary>
    /// Returns a fresh complete operation row. Full defaults to two real evidence-only
    /// members; Safe defaults to empty in both versions. Mutate the returned dictionary
    /// explicitly for negative tests; this factory does not turn off SQL constraints.
    /// Selection arguments describe v2 rows only.
    /// </summary>
    internal static Dictionary<string, object?> Row(bool merge, int version = 2, string state = "Preparing",
        Guid? operation = null, NativeMediaCandidateSelection? full = null, NativeMediaCandidateSelection? safe = null)
    {
        if (version is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(version));
        if (state is not ("Preparing" or "Prepared" or "Committed"))
            throw new ArgumentOutOfRangeException(nameof(state));
        Guid op = operation ?? OperationId;
        if (op == Guid.Empty) throw new ArgumentException("A fixture operation must be nonempty.", nameof(operation));
        if (version == 1 && (full != null || safe != null))
            throw new ArgumentException("Selection arguments apply only to version-two fixture rows.");
        if (!merge && safe is { IsEmpty: false })
            throw new ArgumentException("An import operation has no Safe selection.", nameof(safe));

        NativeMediaCandidateSelection fullSelection = full ?? Full;
        NativeMediaCandidateSelection safeSelection = safe ?? NativeMediaCandidateSelection.Create([]);
        if (version == 2)
        {
            if (fullSelection.IsEmpty || fullSelection.Members.Any(member => member.Id.Occurrence.Resource != Resource))
                throw new ArgumentException("Full must be nonempty and belong to the fixture resource.", nameof(full));
            fullSelection.ValidateSubset(safeSelection);
        }
        NativeMediaCandidateInventory? fullInventory = version == 2
            ? fullSelection.CreateInventory(op, Fingerprint, ImportedAt) : null;
        NativeMediaCandidateInventory? safeInventory = version == 2 && !safeSelection.IsEmpty
            ? safeSelection.CreateInventory(op, Fingerprint, ImportedAt) : null;
        bool ready = state != "Preparing";
        string parent = "/synthetic/persisted-media/" + (merge ? "merge/" : "import/") + op.ToString("N");
        // Only strings are produced; none of these synthetic directories is touched.
        string[] legacyKeys = version == 1 ? LegacyKeys() : [];
        string fullKeysJson = JsonSerializer.Serialize(legacyKeys);
        var row = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["operation_id"] = op.ToString("D"),
            ["request_sha256"] = Request,
            ["package_fingerprint"] = Fingerprint,
            ["catalog_instance"] = Instance,
            ["catalog_generation"] = Generation,
            ["source"] = Resource.Source,
            ["board"] = Resource.Board,
            ["thread"] = Resource.Thread,
            ["operations_parent"] = parent,
            ["imported_at"] = ImportedAt.ToString("O", CultureInfo.InvariantCulture),
            ["state"] = state,
            ["plan_version"] = (long)version,
            ["full_selection_json"] = version == 2 ? fullSelection.CanonicalJson : null,
            ["full_selection_sha256"] = version == 2 ? fullSelection.Sha256 : null,
            ["full_inventory_sha256"] = ready ? fullInventory?.Sha256 : null,
            ["full_member_count"] = version == 2 ? (long)fullSelection.Members.Length : 0L,
            ["evidence_additions"] = (long)(merge ? safeInventory?.OriginalEvidence.Length ?? 0 : fullInventory?.OriginalEvidence.Length ?? 0),
            ["derived_descriptor_additions"] = (long)(merge ? safeInventory?.DerivedRepresentations.Length ?? 0 : fullInventory?.DerivedRepresentations.Length ?? 0),
            ["contained_derived_additions"] = (long)(merge
                ? safeInventory?.DerivedRepresentations.Count(value => value.IncomingStatus == MediaDerivedStatus.Contained) ?? 0
                : fullInventory?.DerivedRepresentations.Count(value => value.IncomingStatus == MediaDerivedStatus.Contained) ?? 0)
        };
        // Deliberately OPAQUE '{}', NOT a serialized/validated NativeTransferCatalog.
        // Every call gets a separate array so negative tests cannot mutate a shared blob.
        row["catalog_bytes"] = merge || version == 2 ? new byte[] { 0x7b, 0x7d } : null;
        if (merge)
        {
            row["roots_json"] = "[]";
            row["full_keys_json"] = fullKeysJson;
            row["safe_keys_json"] = "[]";
            row["full_directory"] = ready ? parent + "/full" : null;
            row["safe_directory"] = ready && safeInventory != null ? parent + "/safe" : null;
            row["full_manifest_generation"] = ready ? ManifestGeneration : null;
            row["safe_manifest_generation"] = ready && safeInventory != null ? ManifestGeneration : null;
            row["full_bindings_sha256"] = ready && version == 1 ? LegacyBindingsSha256 : null;
            row["safe_bindings_sha256"] = null;
            row["safe_selection_json"] = safeInventory != null ? safeSelection.CanonicalJson : null;
            row["safe_selection_sha256"] = safeInventory != null ? safeSelection.Sha256 : null;
            row["safe_inventory_sha256"] = ready ? safeInventory?.Sha256 : null;
            row["safe_member_count"] = safeInventory != null ? (long)safeSelection.Members.Length : 0L;
            row["new_posts"] = 0L;
            row["updated_posts"] = 0L;
            row["new_attachments"] = 0L;
            row["new_bindings"] = (long)(safeInventory?.Bindings.Length ?? 0);
            row["conflicts"] = 0L;
        }
        else
        {
            row["revision_keys_json"] = fullKeysJson;
            row["candidate_directory"] = ready ? parent + "/full" : null;
            row["manifest_generation"] = ready ? ManifestGeneration : null;
            row["bindings_sha256"] = ready && version == 1 ? LegacyBindingsSha256 : null;
            row["post_count"] = version == 1 ? 2L : (long)fullSelection.Members
                .Select(member => member.Id.Occurrence.PostId).Distinct(StringComparer.Ordinal).Count();
            row["attachment_count"] = version == 1 ? (long)legacyKeys.Length : (long)fullInventory!.OriginalEvidence.Length;
            row["original_binding_count"] = (long)(fullInventory?.Bindings.Length ?? 0);
        }
        return row;
    }

    /// <summary>Copies exactly the content-free receipt columns, including every counter.</summary>
    internal static Dictionary<string, object?> Receipt(bool merge, Dictionary<string, object?> operationRow)
    {
        ArgumentNullException.ThrowIfNull(operationRow);
        string[] columns = merge
            ? ["operation_id", "request_sha256", "plan_version", "new_posts", "updated_posts", "new_attachments",
                "new_bindings", "conflicts", "evidence_additions", "derived_descriptor_additions", "contained_derived_additions"]
            : ["operation_id", "request_sha256", "plan_version", "post_count", "attachment_count",
                "evidence_additions", "derived_descriptor_additions", "contained_derived_additions", "original_binding_count"];
        return columns.ToDictionary(column => column, column => operationRow[column], StringComparer.Ordinal);
    }

    internal void Insert(bool merge, Dictionary<string, object?> row)
        => InsertRow(merge ? "archive_media_merge_operations" : "archive_media_import_operations", row);

    internal void InsertReceipt(bool merge, Dictionary<string, object?> row)
        => InsertRow(merge ? "archive_media_merge_receipts" : "archive_media_import_receipts", row);

    internal void Execute(string sql)
    {
        using SqliteCommand command = Connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private void InsertRow(string table, Dictionary<string, object?> row)
    {
        ArgumentNullException.ThrowIfNull(row);
        using SqliteCommand command = Connection.CreateCommand();
        // Quote identifiers, parameterize values, and leave all CHECK constraints enabled.
        KeyValuePair<string, object?>[] fields = row.ToArray();
        command.CommandText = $"INSERT INTO {table} ({string.Join(",", fields.Select(field => Quote(field.Key)))}) VALUES ("
            + string.Join(",", fields.Select((_, index) => "$p" + index.ToString(CultureInfo.InvariantCulture))) + ")";
        for (int index = 0; index < fields.Length; index++)
            command.Parameters.AddWithValue("$p" + index.ToString(CultureInfo.InvariantCulture), fields[index].Value ?? DBNull.Value);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("The synthetic row was not inserted.");
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string[] LegacyKeys()
    {
        // Extended provider metadata makes these actual att-v2 keys, not format-only placeholders.
        var provider = new MediaProviderMetadata("media", "original", "https://example.invalid/media.jpg", "media.jpg",
            null, null, null) { ExpectedSha1 = Convert.ToBase64String(new byte[20]) };
        return new[] { "10", "11" }.Select(post => MediaRevisionKeys.CreateAttachmentKey(Resource, post, 0, provider))
            .OrderBy(key => key, StringComparer.Ordinal).ToArray();
    }

    public void Dispose() => Connection.Dispose();
}

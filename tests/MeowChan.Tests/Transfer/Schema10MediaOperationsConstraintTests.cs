using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests.Transfer;

// W32b2.1 storage constraints only. No ArchiveCatalog, filesystem, migration, recovery,
// producer or host Apply is exercised. Canonical fixture selections use the real codec;
// SQL acceptance does NOT prove semantic selection/count/catalog/inventory correctness.
// That remains the later bounded v2 decoder's job, not execution authority from a CHECK.
public sealed class Schema10MediaOperationsConstraintTests
{
    private const string ImportOperation = "archive_media_import_operations";
    private const string ImportReceipt = "archive_media_import_receipts";
    private const string MergeOperation = "archive_media_merge_operations";
    private const string MergeReceipt = "archive_media_merge_receipts";
    private const int SelectionLimit = 8 * 1024 * 1024;
    private const int CatalogLimit = 16 * 1024 * 1024;
    private static readonly string[] Tables = [ImportOperation, ImportReceipt, MergeOperation, MergeReceipt];
    private static readonly string[] Operations = [ImportOperation, MergeOperation];
    private static readonly string[] States = ["Preparing", "Prepared", "Committed"];
    private static readonly Guid OperationId = new("cd100000-0000-4000-8000-000000000001");
    private static readonly DateTimeOffset ImportedAt = new(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
    private static readonly ResourceId Resource = new("4chan", "b", "7001");
    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = new('b', 64);
    private static readonly NativeMediaCandidateSelection Full = new([Evidence("10"), Evidence("11")]);
    private static readonly NativeMediaCandidateSelection Safe = Full.Select([Full.Members[0].Id]);

    public static IEnumerable<object[]> VersionMatrix()
    {
        foreach (string table in Tables)
        foreach (int version in new[] { 1, 2 })
        foreach (string state in IsOperation(table) ? States : ["Preparing"])
            yield return [table, version, state];
    }

    [Theory]
    [MemberData(nameof(VersionMatrix))]
    public void Fresh_schema_accepts_both_versions_in_each_supported_state(string table, int version, string state)
    {
        using var db = new Database();
        db.Insert(table, Row(table, version, state));
        Assert.Equal((long)version, db.Scalar($"SELECT plan_version FROM {table}"));
        ArchiveSql.ValidateMediaTransferSchema10(db.Connection, db.Transaction);
        Assert.Equal(Tables.OrderBy(x => x, StringComparer.Ordinal).ToArray(), db.TableNames());
        // Receipts are inserted without any corresponding operation: they remain content-free.
        if (!IsOperation(table))
            Assert.Equal(0L, db.Scalar($"SELECT count(*) FROM {(table == ImportReceipt ? ImportOperation : MergeOperation)}"));
    }

    [Theory]
    [InlineData(ImportOperation)] [InlineData(ImportReceipt)]
    [InlineData(MergeOperation)] [InlineData(MergeReceipt)]
    public void Omitted_new_fields_default_to_uncontaminated_v1(string table)
    {
        using var db = new Database();
        var row = Row(table, 1);
        row.Remove("plan_version");
        foreach (string field in NewFields(table)) row.Remove(field);
        db.Insert(table, row);
        Assert.Equal(1L, db.Scalar($"SELECT plan_version FROM {table}"));
        Assert.Equal("integer", db.Scalar($"SELECT typeof(plan_version) FROM {table}"));
        foreach (string field in NewFields(table))
        {
            Assert.Equal(IsCounter(field) ? (object)0L : DBNull.Value, db.Scalar($"SELECT {field} FROM {table}"));
            Assert.Equal(IsCounter(field) ? "integer" : "null", db.Scalar($"SELECT typeof({field}) FROM {table}"));
        }
    }

    public static IEnumerable<object[]> InvalidVersions()
    {
        foreach (string table in Tables)
        foreach (string fault in new[] { "null", "negative", "zero", "three", "future", "real", "text", "blob" })
            yield return [table, fault];
    }

    [Theory]
    [MemberData(nameof(InvalidVersions))]
    public void Version_is_required_integer_one_or_two(string table, string fault) =>
        Reject(table, Row(table, 1), "plan_version", Value(fault));

    public static IEnumerable<object[]> V1Contamination()
    {
        foreach (string table in Tables)
        foreach (string state in IsOperation(table) ? States : ["Preparing"])
        foreach (string field in NewFields(table))
            yield return [table, state, field];
    }

    [Theory]
    [MemberData(nameof(V1Contamination))]
    public void V1_rejects_every_new_document_digest_catalog_and_nonzero_counter(string table, string state, string field)
    {
        object value = IsCounter(field) ? 1L : field == "catalog_bytes" ? CatalogBytes() :
            field.EndsWith("_json", StringComparison.Ordinal) ? Full.CanonicalJson : HashA;
        var changes = new Dictionary<string, object?> { [field] = value };
        // Avoid obtaining a false positive solely from contained <= descriptors.
        if (field == "contained_derived_additions") changes["derived_descriptor_additions"] = 1L;
        Reject(table, Row(table, 1, state), changes);
    }

    public static IEnumerable<object[]> RequiredNulls()
    {
        foreach (object[] item in VersionMatrix())
        {
            string table = (string)item[0];
            int version = (int)item[1];
            string state = (string)item[2];
            foreach (var field in Row(table, version, state))
                if (field.Value != null)
                    yield return [table, version, state, field.Key];
        }
    }

    [Theory]
    [MemberData(nameof(RequiredNulls))]
    public void Required_fields_cannot_escape_checks_through_SQL_null(string table, int version, string state, string field) =>
        Reject(table, Row(table, version, state), field, null);

    public static IEnumerable<object[]> InvalidNewCounts()
    {
        foreach (string table in Tables)
        foreach (string field in NewFields(table).Where(IsCounter))
        foreach (string fault in new[] { "null", "negative", "over-int32", "real", "text", "blob" })
            yield return [table, field, fault];
    }

    [Theory]
    [MemberData(nameof(InvalidNewCounts))]
    public void New_counts_require_bounded_nonnegative_integers(string table, string field, string fault) =>
        Reject(table, Row(table, 2), field, Value(fault));

    public static IEnumerable<object[]> AdditionCounters()
    {
        foreach (string table in Tables)
        foreach (string field in NewFields(table).Where(f => IsCounter(f) && !f.EndsWith("_member_count", StringComparison.Ordinal)))
            yield return [table, field];
    }

    public static IEnumerable<object[]> InvalidLegacyV2Counts()
    {
        foreach (string table in Tables)
        foreach (string field in IsImport(table) ? new[] { "post_count", "attachment_count" }
                     : ["new_posts", "updated_posts", "new_attachments", "new_bindings", "conflicts"])
        foreach (string fault in new[] { "null", "negative", "over-int32", "real", "text", "blob" })
            yield return [table, field, fault];
    }

    [Theory]
    [MemberData(nameof(InvalidLegacyV2Counts))]
    public void V2_also_bounds_legacy_receipt_and_operation_counters(string table, string field, string fault) =>
        Reject(table, Row(table, 2), field, Value(fault));

    [Theory]
    [MemberData(nameof(AdditionCounters))]
    public void Addition_counter_endpoints_are_inclusive(string table, string field)
    {
        foreach (long value in new[] { 0L, (long)int.MaxValue })
        {
            using var db = new Database();
            var row = Row(table, 2);
            row[field] = value;
            if (field == "contained_derived_additions") row["derived_descriptor_additions"] = value;
            db.Insert(table, row);
            Assert.Equal(value, db.Scalar($"SELECT {field} FROM {table}"));
        }
    }

    [Theory]
    [InlineData(ImportOperation)] [InlineData(ImportReceipt)]
    [InlineData(MergeOperation)] [InlineData(MergeReceipt)]
    public void Contained_derived_cannot_exceed_descriptor_additions(string table) =>
        Reject(table, Row(table, 2), new() { ["derived_descriptor_additions"] = 1L, ["contained_derived_additions"] = 2L });

    [Theory]
    [InlineData(ImportOperation)] [InlineData(MergeOperation)]
    public void V2_full_member_count_is_nonempty_and_at_most_100000(string table)
    {
        foreach (long count in new[] { 0L, 100001L })
            Reject(table, Row(table, 2), "full_member_count", count);
        // Shape endpoints only: SQL must not be mistaken for a decoder counting members.
        foreach (long count in new[] { 1L, 100000L })
        {
            using var db = new Database();
            var row = Row(table, 2); row["full_member_count"] = count;
            db.Insert(table, row);
            Assert.Equal(count, db.Scalar($"SELECT full_member_count FROM {table}"));
        }
    }

    [Fact]
    public void V2_nonempty_safe_count_is_bounded_independently_of_original_bindings()
    {
        Reject(MergeOperation, Row(MergeOperation, 2), "safe_member_count", 100001L);
        using var db = new Database();
        var row = Row(MergeOperation, 2);
        row["full_member_count"] = 100000L; row["safe_member_count"] = 100000L;
        db.Insert(MergeOperation, row);
        Assert.Equal(100000L, db.Scalar($"SELECT safe_member_count FROM {MergeOperation}"));
        Assert.Equal(0L, db.Scalar($"SELECT new_bindings FROM {MergeOperation}"));
    }

    [Theory]
    [InlineData(ImportOperation)] [InlineData(ImportReceipt)]
    public void Attachment_zero_is_v2_only_in_operations_and_receipts(string table)
    {
        foreach (string state in IsOperation(table) ? States : ["Preparing"])
        {
            Reject(table, Row(table, 1, state), "attachment_count", 0L);
            Reject(table, Row(table, 2, state), "attachment_count", -1L);
            using var db = new Database();
            var row = Row(table, 2, state); row["attachment_count"] = 0L;
            db.Insert(table, row);
            Assert.Equal(0L, db.Scalar($"SELECT attachment_count FROM {table}"));
        }
    }

    public static IEnumerable<object[]> LegacyV2Contamination()
    {
        foreach (string table in Operations)
        foreach (string state in States)
        {
            foreach (string field in table == ImportOperation ? new[] { "revision_keys_json" } : ["full_keys_json", "safe_keys_json"])
                foreach (string value in new[] { "[\"legacy-key\"]", "[ ]", " []" })
                    yield return [table, state, field, value];
            foreach (string field in table == ImportOperation ? new[] { "bindings_sha256" } : ["full_bindings_sha256", "safe_bindings_sha256"])
                yield return [table, state, field, HashA];
        }
    }

    [Theory]
    [MemberData(nameof(LegacyV2Contamination))]
    public void V2_requires_literal_empty_legacy_keys_and_null_binding_digests(string table, string state, string field, string value) =>
        Reject(table, Row(table, 2, state), field, value);

    public static IEnumerable<object[]> PreparingReadyFields()
    {
        foreach (string table in Operations)
        foreach (int version in new[] { 1, 2 })
        foreach (var field in Row(table, version, "Prepared"))
            if (Row(table, version)[field.Key] == null && field.Value != null)
                yield return [table, version, field.Key];
    }

    [Theory]
    [MemberData(nameof(PreparingReadyFields))]
    public void Preparing_rejects_each_ready_only_field(string table, int version, string field) =>
        Reject(table, Row(table, version), field, Row(table, version, "Prepared")[field]);

    [Theory]
    [InlineData(ImportOperation, "Prepared")] [InlineData(ImportOperation, "Committed")]
    [InlineData(MergeOperation, "Prepared")] [InlineData(MergeOperation, "Committed")]
    public void V2_cannot_transition_to_ready_state_without_ready_fields(string table, string state) =>
        Reject(table, Row(table, 2), "state", state);

    public static IEnumerable<object[]> InvalidHashes()
    {
        foreach (string table in Operations)
        foreach (string field in NewFields(table).Where(f => f.EndsWith("_sha256", StringComparison.Ordinal))
                     .Concat(table == ImportOperation ? new[] { "manifest_generation" }
                         : ["full_manifest_generation", "safe_manifest_generation"]))
        foreach (string fault in new[] { "empty", "short-hash", "long-hash", "uppercase-hash", "nonhex-hash", "nul-hash", "nul-suffix-hash", "blob-hash" })
            yield return [table, field, fault];
    }

    [Theory]
    [MemberData(nameof(InvalidHashes))]
    public void New_digests_require_lowercase_hex_text_of_exactly_64_characters(string table, string field, string fault) =>
        Reject(table, Row(table, 2, "Prepared"), field, Value(fault));

    public static IEnumerable<object[]> InvalidV2IdentityHashes()
    {
        foreach (string table in Tables)
        foreach (string field in IsOperation(table) ? new[] { "request_sha256", "package_fingerprint" } : ["request_sha256"])
        foreach (string fault in new[] { "short-hash", "long-hash", "uppercase-hash", "nonhex-hash", "nul-hash", "nul-suffix-hash", "blob-hash" })
            yield return [table, field, fault];
    }

    [Theory]
    [MemberData(nameof(InvalidV2IdentityHashes))]
    public void V2_identity_hashes_are_also_lowercase_hex_text(string table, string field, string fault) =>
        Reject(table, Row(table, 2), field, Value(fault));

    public static IEnumerable<object[]> InvalidReadyDirectories()
    {
        foreach (string table in Operations)
        foreach (string state in new[] { "Prepared", "Committed" })
        foreach (string field in table == ImportOperation ? new[] { "candidate_directory" } : ["full_directory", "safe_directory"])
            yield return [table, state, field];
    }

    [Theory]
    [MemberData(nameof(InvalidReadyDirectories))]
    public void V2_ready_directories_require_nonempty_text(string table, string state, string field)
    {
        Reject(table, Row(table, 2, state), field, "");
        Reject(table, Row(table, 2, state), field, Encoding.UTF8.GetBytes("/synthetic/not-text"));
    }

    public static IEnumerable<object[]> TypedDocuments()
    {
        foreach (string table in Operations)
        foreach (string field in NewFields(table).Where(f => f.EndsWith("_json", StringComparison.Ordinal)))
            yield return [table, field];
    }

    [Theory]
    [MemberData(nameof(TypedDocuments))]
    public void Selection_documents_require_nonempty_text_not_blob_even_with_valid_UTF8(string table, string field)
    {
        Reject(table, Row(table, 2), field, "");
        Reject(table, Row(table, 2), field, Encoding.UTF8.GetBytes(Full.CanonicalJson));
    }

    [Theory]
    [InlineData(ImportOperation)] [InlineData(MergeOperation)]
    public void V2_catalog_is_required_blob_not_JSON_text_or_integer(string table)
    {
        Reject(table, Row(table, 2), "catalog_bytes", null);
        Reject(table, Row(table, 2), "catalog_bytes", Encoding.UTF8.GetString(CatalogBytes()));
        Reject(table, Row(table, 2), "catalog_bytes", 123L);
    }

    [Theory]
    [MemberData(nameof(TypedDocuments))]
    public void Selection_limit_counts_UTF8_bytes_not_characters(string table, string field)
    {
        foreach (bool multibyte in new[] { false, true })
        {
            string atLimit = ShapeDocument(SelectionLimit, multibyte);
            string tooLarge = ShapeDocument(SelectionLimit + 1, multibyte);
            Assert.Equal(SelectionLimit, Encoding.UTF8.GetByteCount(atLimit));
            Assert.Equal(SelectionLimit + 1, Encoding.UTF8.GetByteCount(tooLarge));
            if (multibyte) Assert.True(tooLarge.Length < SelectionLimit);
            using (var db = new Database())
            {
                var row = Row(table, 2); row[field] = atLimit;
                db.Insert(table, row);
                Assert.Equal((long)SelectionLimit, db.Scalar($"SELECT length(CAST({field} AS BLOB)) FROM {table}"));
            }
            Reject(table, Row(table, 2), field, tooLarge);
        }
    }

    [Theory]
    [InlineData(ImportOperation)] [InlineData(MergeOperation)]
    public void Catalog_byte_limit_is_inclusive(string table)
    {
        // Byte-budget probes are not semantically decoded catalogs.
        using (var db = new Database())
        {
            var row = Row(table, 2); row["catalog_bytes"] = new byte[CatalogLimit];
            db.Insert(table, row);
            Assert.Equal((long)CatalogLimit, db.Scalar($"SELECT length(catalog_bytes) FROM {table}"));
        }
        Reject(table, Row(table, 2), "catalog_bytes", new byte[CatalogLimit + 1]);
    }

    [Theory]
    [InlineData("Preparing")] [InlineData("Prepared")] [InlineData("Committed")]
    public void Empty_safe_is_zero_count_null_selection_digest_inventory_and_root(string state)
    {
        using (var db = new Database())
        {
            db.Insert(MergeOperation, Row(MergeOperation, 2, state, emptySafe: true));
            Assert.Equal(0L, db.Scalar($"SELECT safe_member_count FROM {MergeOperation}"));
            foreach (string field in SafeFields)
                Assert.Equal(DBNull.Value, db.Scalar($"SELECT {field} FROM {MergeOperation}"));
        }
        foreach (string field in SafeFields)
        {
            object value = field.EndsWith("_json", StringComparison.Ordinal) ? Safe.CanonicalJson :
                field == "safe_directory" ? "/synthetic/schema10/safe" : HashA;
            Reject(MergeOperation, Row(MergeOperation, 2, state, emptySafe: true), field, value);
        }
        // Empty canonical JSON is still a document, not the required absence of Safe.
        Reject(MergeOperation, Row(MergeOperation, 2, state, emptySafe: true), new()
        {
            ["safe_selection_json"] = new NativeMediaCandidateSelection([]).CanonicalJson,
            ["safe_selection_sha256"] = new NativeMediaCandidateSelection([]).Sha256
        });
        Reject(MergeOperation, Row(MergeOperation, 2, state), "safe_member_count", 0L);
    }

    [Theory]
    [InlineData("Preparing")] [InlineData("Prepared")] [InlineData("Committed")]
    public void Nonempty_zero_binding_safe_is_allowed_and_keeps_ready_root_when_required(string state)
    {
        Assert.False(Safe.IsEmpty);
        Assert.Empty(Safe.CreateInventory(OperationId, HashA, ImportedAt).Bindings);
        using var db = new Database();
        db.Insert(MergeOperation, Row(MergeOperation, 2, state));
        Assert.Equal(0L, db.Scalar($"SELECT new_bindings FROM {MergeOperation}"));
        Assert.Equal((long)Safe.Members.Length, db.Scalar($"SELECT safe_member_count FROM {MergeOperation}"));
        Assert.Equal(Safe.CanonicalJson, db.Scalar($"SELECT safe_selection_json FROM {MergeOperation}"));
        Assert.Equal(state == "Preparing" ? (object)DBNull.Value : "/synthetic/schema10/safe",
            db.Scalar($"SELECT safe_directory FROM {MergeOperation}"));
    }

    public static IEnumerable<object[]> AliasMismatches()
    {
        foreach (string state in new[] { "Prepared", "Committed" })
        foreach (string field in new[] { "safe_selection_json", "safe_selection_sha256", "safe_member_count",
                     "safe_inventory_sha256", "safe_manifest_generation", "conflicts" })
            yield return [state, field];
    }

    [Theory]
    [MemberData(nameof(AliasMismatches))]
    public void Same_directory_alias_rejects_any_full_safe_identity_mismatch_or_conflict(string state, string field)
    {
        var row = AliasRow(state);
        object value = field switch
        {
            "safe_selection_json" => Safe.CanonicalJson,
            "safe_member_count" => (long)Safe.Members.Length,
            "conflicts" => 1L,
            _ => HashB
        };
        Assert.NotEqual(row[field], value);
        Reject(MergeOperation, row, field, value);
    }

    [Theory]
    [InlineData("Prepared")] [InlineData("Committed")]
    public void Exact_zero_conflict_full_safe_alias_is_allowed(string state)
    {
        using var db = new Database();
        db.Insert(MergeOperation, AliasRow(state));
        Assert.Equal(1L, db.Scalar($"SELECT count(*) FROM {MergeOperation} WHERE full_directory=safe_directory"));
        ArchiveSql.ValidateMediaTransferSchema10(db.Connection, db.Transaction);
    }

    [Theory]
    [InlineData(ImportOperation)] [InlineData(ImportReceipt)]
    [InlineData(MergeOperation)] [InlineData(MergeReceipt)]
    public void Exact_schema_validation_rejects_one_missing_check_even_when_all_columns_remain(string table)
    {
        using var db = new Database();
        ArchiveSql.ValidateMediaTransferSchema10(db.Connection, db.Transaction);
        string original = Assert.IsType<string>(db.Scalar($"SELECT sql FROM sqlite_master WHERE type='table' AND name='{table}'"));
        string[] columns = db.ColumnNames(table);
        // Recreate only this empty synthetic table. Do not modify sqlite_master or disable checks.
        string damaged = RemoveFirstCheck(original);
        Assert.NotEqual(original, damaged);
        db.Execute($"DROP TABLE {table}");
        db.Execute(damaged);
        Assert.Equal(columns, db.ColumnNames(table));
        Assert.Equal(0L, db.Scalar($"SELECT count(*) FROM {table}"));
        Assert.Throws<InvalidDataException>(() => ArchiveSql.ValidateMediaTransferSchema10(db.Connection, db.Transaction));
    }

    private static readonly string[] SafeFields = ["safe_selection_json", "safe_selection_sha256",
        "safe_inventory_sha256", "safe_directory", "safe_manifest_generation", "safe_bindings_sha256"];

    private static bool IsOperation(string table) => table is ImportOperation or MergeOperation;
    private static bool IsImport(string table) => table is ImportOperation or ImportReceipt;
    private static bool IsCounter(string field) => field.EndsWith("_count", StringComparison.Ordinal)
        || field.EndsWith("_additions", StringComparison.Ordinal);

    private static IEnumerable<string> NewFields(string table)
    {
        yield return "evidence_additions";
        yield return "derived_descriptor_additions";
        yield return "contained_derived_additions";
        if (IsImport(table)) yield return "original_binding_count";
        if (!IsOperation(table)) yield break;
        yield return "full_selection_json";
        yield return "full_selection_sha256";
        yield return "full_inventory_sha256";
        yield return "full_member_count";
        if (IsImport(table)) yield return "catalog_bytes";
        else
        {
            yield return "safe_selection_json";
            yield return "safe_selection_sha256";
            yield return "safe_inventory_sha256";
            yield return "safe_member_count";
        }
    }

    private static Dictionary<string, object?> Row(string table, int version, string state = "Preparing", bool emptySafe = false)
    {
        bool import = IsImport(table), operation = IsOperation(table), ready = state != "Preparing";
        var row = new Dictionary<string, object?>
        {
            ["operation_id"] = OperationId.ToString("D"), ["request_sha256"] = HashB, ["plan_version"] = (long)version
        };
        foreach (string field in NewFields(table)) row[field] = IsCounter(field) ? 0L : null;
        if (import)
        {
            row["post_count"] = 2L; row["attachment_count"] = 2L;
        }
        else
        {
            row["new_posts"] = 0L; row["updated_posts"] = 0L; row["new_attachments"] = 0L;
            row["new_bindings"] = version == 1 ? 1L : 0L; row["conflicts"] = 0L;
        }
        if (version == 2) row["evidence_additions"] = import ? 2L : emptySafe ? 0L : 1L;
        if (!operation) return row;
        row["package_fingerprint"] = HashA;
        row["catalog_instance"] = "cd100000-0000-4000-8000-000000000002";
        row["catalog_generation"] = 0L;
        row["source"] = Resource.Source; row["board"] = Resource.Board; row["thread"] = Resource.Thread;
        row["operations_parent"] = "/synthetic/schema10";
        row["imported_at"] = ImportedAt.ToString("O", CultureInfo.InvariantCulture);
        row["state"] = state;
        if (import)
        {
            row["revision_keys_json"] = version == 1 ? "[\"legacy-key\"]" : "[]";
            row["candidate_directory"] = ready ? "/synthetic/schema10/full" : null;
            row["manifest_generation"] = ready ? HashA : null;
            row["bindings_sha256"] = ready && version == 1 ? HashA : null;
        }
        else
        {
            row["roots_json"] = "[]";
            row["full_keys_json"] = version == 1 ? "[\"legacy-key\"]" : "[]";
            row["safe_keys_json"] = version == 1 ? "[\"legacy-key\"]" : "[]";
            row["catalog_bytes"] = CatalogBytes();
            row["full_directory"] = ready ? "/synthetic/schema10/full" : null;
            row["safe_directory"] = ready && !emptySafe ? "/synthetic/schema10/safe" : null;
            row["full_manifest_generation"] = ready ? HashA : null;
            row["safe_manifest_generation"] = ready && !emptySafe ? HashA : null;
            row["full_bindings_sha256"] = ready && version == 1 ? HashA : null;
            row["safe_bindings_sha256"] = ready && version == 1 && !emptySafe ? HashA : null;
        }
        if (version == 2)
        {
            row["full_selection_json"] = Full.CanonicalJson; row["full_selection_sha256"] = Full.Sha256;
            row["full_member_count"] = (long)Full.Members.Length;
            row["full_inventory_sha256"] = ready ? Full.CreateInventory(OperationId, HashA, ImportedAt).Sha256 : null;
            if (import) row["catalog_bytes"] = CatalogBytes();
            else if (!emptySafe)
            {
                row["safe_selection_json"] = Safe.CanonicalJson; row["safe_selection_sha256"] = Safe.Sha256;
                row["safe_member_count"] = (long)Safe.Members.Length;
                row["safe_inventory_sha256"] = ready ? Safe.CreateInventory(OperationId, HashA, ImportedAt).Sha256 : null;
            }
        }
        return row;
    }

    private static Dictionary<string, object?> AliasRow(string state)
    {
        var row = Row(MergeOperation, 2, state);
        foreach (string suffix in new[] { "directory", "selection_json", "selection_sha256", "member_count", "inventory_sha256", "manifest_generation" })
            row["safe_" + suffix] = row["full_" + suffix];
        row["evidence_additions"] = (long)Full.Members.Length;
        return row;
    }

    private static NativeMediaSelectedMember Evidence(string post) => new(new(
        new(new(Resource, post, 0, null), null, NativeMediaMemberRole.OriginalEvidence),
        new CapturedAttachment(0, null, IsDeleted: true), false, TransferContentStatus.Unknown, null, null, null), false);

    private static byte[] CatalogBytes()
    {
        var posts = Full.Members.Select((member, index) => new TransferPost(
            new CapturedPost(member.Id.Occurrence.PostId, index, PostFieldPresence.Attachments,
                Attachments: [member.Descriptor.Metadata]),
            [new TransferAttachment(member.Descriptor.Metadata, TransferMediaRole.Original,
                TransferContentStatus.Unknown, null, null)])).ToImmutableArray();
        return NativeTransferCatalogSerializer.Serialize(new(ImportedAt,
            [new TransferThread(Resource, null, "synthetic schema10 evidence", ThreadSourceState.NotFound,
                ImportedAt, null, 0, false, posts)]));
    }

    private static object? Value(string fault) => fault switch
    {
        "null" => null, "negative" => -1L, "zero" => 0L, "three" => 3L, "future" => 11L,
        "over-int32" => (long)int.MaxValue + 1, "real" => 1.5, "text" => "not-an-integer", "blob" => new byte[] { 2 },
        "empty" => "", "short-hash" => new string('a', 63), "long-hash" => new string('a', 65),
        "uppercase-hash" => new string('A', 64), "nonhex-hash" => new string('g', 64),
        "nul-hash" => new string('a', 63) + '\0', "nul-suffix-hash" => HashA + "\0extra",
        "blob-hash" => Encoding.UTF8.GetBytes(HashA),
        _ => throw new ArgumentOutOfRangeException(nameof(fault))
    };

    private static string ShapeDocument(int bytes, bool multibyte)
    {
        // A valid JSON byte-budget probe, deliberately not a valid selection document.
        // This test does not claim SQL validates canonicality or semantic membership.
        const string prefix = "{\"padding\":\"", suffix = "\"}";
        int contentBytes = bytes - prefix.Length - suffix.Length;
        return prefix + (multibyte ? new string('\u00e9', contentBytes / 2) + new string('x', contentBytes % 2)
            : new string('x', contentBytes)) + suffix;
    }

    private static void Reject(string table, Dictionary<string, object?> valid, string field, object? value) =>
        Reject(table, valid, new Dictionary<string, object?> { [field] = value });

    private static void Reject(string table, Dictionary<string, object?> valid, Dictionary<string, object?> changes)
    {
        using var db = new Database();
        db.Insert(table, valid); // A positive control prevents unrelated fixture/schema failures passing.
        string before = db.Snapshot(table);
        using (SqliteCommand command = db.Command($"UPDATE {table} SET " +
                   string.Join(",", changes.Keys.Select((key, i) => $"{key}=$p{i}"))))
        {
            int i = 0;
            foreach (object? value in changes.Values) command.Parameters.AddWithValue($"$p{i++}", value ?? DBNull.Value);
            Constraint(() => command.ExecuteNonQuery());
        }
        Assert.Equal(before, db.Snapshot(table));
        db.Execute($"DELETE FROM {table}"); // Synthetic in-memory rows only; never any profile cleanup.
        var invalid = new Dictionary<string, object?>(valid);
        foreach (var change in changes) invalid[change.Key] = change.Value;
        Constraint(() => db.Insert(table, invalid));
        Assert.Equal(0L, db.Scalar($"SELECT count(*) FROM {table}"));
        db.Insert(table, valid);
        Assert.Equal(before, db.Snapshot(table));
    }

    private static void Constraint(Action action)
    {
        SqliteException error = Assert.Throws<SqliteException>(action);
        Assert.Equal(19, error.SqliteErrorCode);
        // CHECK / NOT NULL only: duplicate PK, unknown column, syntax or FK errors cannot pass.
        Assert.Contains(error.SqliteExtendedErrorCode, new[] { 275, 1299 });
    }

    private static string RemoveFirstCheck(string sql)
    {
        int start = sql.IndexOf("CHECK", StringComparison.OrdinalIgnoreCase);
        Assert.True(start >= 0, "Fresh table must contain a CHECK to tamper with.");
        int open = sql.IndexOf('(', start), depth = 0;
        bool quoted = false;
        for (int i = open; i < sql.Length; i++)
        {
            char c = sql[i];
            if (c == '\'')
            {
                if (quoted && i + 1 < sql.Length && sql[i + 1] == '\'') { i++; continue; }
                quoted = !quoted;
            }
            if (quoted) continue;
            if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return sql.Remove(start, i - start + 1);
        }
        throw new InvalidOperationException("Could not locate the complete CHECK expression.");
    }

    private sealed class Database : IDisposable
    {
        internal SqliteConnection Connection { get; }
        internal SqliteTransaction Transaction { get; }
        internal Database()
        {
            Connection = new SqliteConnection("Data Source=:memory:;Pooling=False;Foreign Keys=True");
            try
            {
                Connection.Open();
                Transaction = Connection.BeginTransaction(deferred: false);
                try
                {
                    ArchiveSql.CreateMediaTransferSchema10(Connection, Transaction);
                    ArchiveSql.ValidateMediaTransferSchema10(Connection, Transaction);
                }
                catch { Transaction.Dispose(); throw; }
            }
            catch { Connection.Dispose(); throw; }
        }
        internal SqliteCommand Command(string sql)
        {
            var command = Connection.CreateCommand(); command.Transaction = Transaction; command.CommandText = sql;
            return command;
        }
        internal void Execute(string sql) { using var command = Command(sql); command.ExecuteNonQuery(); }
        internal object? Scalar(string sql) { using var command = Command(sql); return command.ExecuteScalar(); }
        internal void Insert(string table, Dictionary<string, object?> row)
        {
            using var command = Command($"INSERT INTO {table} ({string.Join(",", row.Keys)}) VALUES (" +
                string.Join(",", Enumerable.Range(0, row.Count).Select(i => $"$p{i}")) + ")");
            int i = 0;
            foreach (object? value in row.Values) command.Parameters.AddWithValue($"$p{i++}", value ?? DBNull.Value);
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        internal string[] TableNames()
        {
            using var command = Command("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name");
            using var reader = command.ExecuteReader();
            var names = new List<string>(); while (reader.Read()) names.Add(reader.GetString(0));
            return names.ToArray();
        }
        internal string[] ColumnNames(string table)
        {
            using var command = Command($"PRAGMA table_info({table})");
            using var reader = command.ExecuteReader();
            var names = new List<string>(); while (reader.Read()) names.Add(reader.GetString(1));
            return names.ToArray();
        }
        internal string Snapshot(string table)
        {
            using var command = Command($"SELECT * FROM {table}");
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            var values = Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i) switch
            {
                DBNull => "null", byte[] bytes => "blob:" + Convert.ToHexString(SHA256.HashData(bytes)),
                string text => "text:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))),
                long number => "integer:" + number.ToString(CultureInfo.InvariantCulture),
                double number => "real:" + number.ToString("R", CultureInfo.InvariantCulture),
                _ => throw new InvalidOperationException("Unexpected SQLite value type.")
            }).ToArray();
            Assert.False(reader.Read());
            return string.Join("|", values);
        }
        public void Dispose() { Transaction.Dispose(); Connection.Dispose(); }
    }
}

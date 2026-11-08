using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Storage;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests.Transfer;

// SQL-boundary tests, not another standalone selection-codec suite. Every corrupt
// document/hash is accepted by the real Schema10 with CHECKs enabled, and is then
// rejected by both persisted operation entrypoints. No files or profiles are used.
public sealed class PersistedMediaSelectionAdversarialTests
{
    private static readonly Guid ForeignOperation = Guid.Parse("cd220000-0000-4000-8000-000000000099");
    private static readonly string ForeignFingerprint = new('f', 64);
    private static readonly string OtherSha1 = Convert.ToBase64String(new byte[20]);

    public static IEnumerable<object[]> NoncanonicalCases()
    {
        foreach ((bool merge, bool safe) in SelectionColumns())
        foreach (string mutation in new[]
        {
            "duplicate-root-field", "duplicate-nested-field", "unknown-root-field", "unknown-nested-field",
            "property-order", "member-order", "duplicate-member", "leading-space", "internal-space", "trailing-newline"
        })
            yield return [merge, safe, mutation];
    }

    [Theory]
    [MemberData(nameof(NoncanonicalCases))]
    public void Sql_valid_selection_is_decoded_instead_of_trusted_or_normalized(bool merge, bool safe, string mutation)
    {
        using var fixture = new PersistedMediaFixture();
        NativeMediaCandidateSelection selection = PersistedMediaFixture.Full;
        Dictionary<string, object?> row = PersistedMediaFixture.Row(merge, full: selection, safe: safe ? selection : null);
        string json = CorruptCanonicalWire(selection.CanonicalJson, mutation);
        Assert.NotEqual(selection.CanonicalJson, json);
        SetWire(row, safe, json);
        // Match the digest to the *stored* bytes. A stale hash or a count mismatch
        // must not conceal a missing call to the canonical selection decoder.
        Assert.Equal(Hash(json), row[Prefix(safe) + "_selection_sha256"]);

        AssertStoredThenRejected(fixture, merge, row, selectionDecoder: true);
    }

    public static IEnumerable<object[]> ParentAndContextCases()
    {
        foreach ((bool merge, bool safe) in SelectionColumns())
        foreach (string mutation in new[]
        {
            "missing-parent", "parent-byte-version", "parent-metadata", "child-context", "orphan-context"
        })
            yield return [merge, safe, mutation];
    }

    [Theory]
    [MemberData(nameof(ParentAndContextCases))]
    public void Sql_valid_selection_cannot_bypass_exact_parent_or_context_closure(bool merge, bool safe, string mutation)
    {
        using var fixture = new PersistedMediaFixture();
        NativeMediaCandidateSelection selection = DerivedSelection();
        Dictionary<string, object?> row = PersistedMediaFixture.Row(merge, full: selection, safe: safe ? selection : null);
        JsonObject wire = JsonNode.Parse(selection.CanonicalJson)!.AsObject();
        // The mutation helper itself must not introduce incidental wire changes.
        Assert.Equal(selection.CanonicalJson, wire.ToJsonString());
        JsonArray members = wire["members"]!.AsArray();
        switch (mutation)
        {
            case "missing-parent":
                members.RemoveAt(0);
                break;
            case "parent-byte-version":
                // The exact parent is missing even though occurrence, role and
                // all known digests except the nullable SHA1 still look alike.
                members[1]!["descriptor"]!["id"]!["sourceVersion"]!["sha1"] = null;
                break;
            case "parent-metadata":
                members[1]!["descriptor"]!["metadata"]!["isDeleted"] = true;
                break;
            case "child-context":
                members[1]!["contextOnly"] = true;
                break;
            case "orphan-context":
                members.RemoveAt(1);
                members[0]!["contextOnly"] = true;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        SetWire(row, safe, wire.ToJsonString());

        AssertStoredThenRejected(fixture, merge, row, selectionDecoder: true);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Canonical_document_requires_its_exact_persisted_selection_digest(bool merge, bool safe)
    {
        using var fixture = new PersistedMediaFixture();
        NativeMediaCandidateSelection selection = PersistedMediaFixture.Full;
        Dictionary<string, object?> row = PersistedMediaFixture.Row(merge, full: selection, safe: safe ? selection : null);
        string wrongDigest = Hash(selection.CanonicalJson + " ");
        Assert.NotEqual(selection.Sha256, wrongDigest);
        row[Prefix(safe) + "_selection_sha256"] = wrongDigest;
        Assert.Equal(selection.CanonicalJson, row[Prefix(safe) + "_selection_json"]);

        AssertStoredThenRejected(fixture, merge, row);
    }

    public static IEnumerable<object[]> SafeAlterationCases()
    {
        foreach (string state in new[] { "Preparing", "Prepared", "Committed" })
        foreach (string mutation in new[]
        {
            "role", "status", "retained", "metadata", "measured-sha1", "nullable-sha1-removed", "nullable-sha1-enriched", "context-promotion"
        })
            yield return [state, mutation];
    }

    [Theory]
    [MemberData(nameof(SafeAlterationCases))]
    public void Self_consistent_canonical_safe_document_is_still_bound_to_exact_full_members(string state, string mutation)
    {
        using var fixture = new PersistedMediaFixture();
        NativeMediaCandidateSelection full = DerivedSelection(knownSha1: mutation != "nullable-sha1-enriched");
        NativeMediaMemberId childId = full.Members[1].Id;
        if (mutation == "context-promotion") full = full.Select([childId]);
        NativeMediaCandidateSelection safe = full.Select([childId]);
        Dictionary<string, object?> row = PersistedMediaFixture.Row(true, state: state, full: full, safe: safe);
        NativeMediaSelectedMember parent = safe.Members[0];
        NativeMediaSelectedMember child = safe.Members[1];
        switch (mutation)
        {
            case "role":
                child = child with { Descriptor = child.Descriptor with
                    { Id = child.Id with { Role = NativeMediaMemberRole.VideoPosterPng } } };
                break;
            case "status":
                parent = parent with { Descriptor = parent.Descriptor with { Status = TransferContentStatus.Unavailable } };
                break;
            case "retained":
                parent = parent with { Descriptor = parent.Descriptor with { Retained = true } };
                child = child with { Descriptor = child.Descriptor with { Retained = true } };
                break;
            case "metadata":
                parent = parent with { Descriptor = parent.Descriptor with
                    { Metadata = parent.Descriptor.Metadata with { IsDeleted = true } } };
                child = child with { Descriptor = child.Descriptor with
                    { Metadata = child.Descriptor.Metadata with { IsDeleted = true } } };
                break;
            case "measured-sha1":
                Assert.NotEqual(OtherSha1, child.Descriptor.MeasuredPayload!.Sha1);
                child = child with { Descriptor = child.Descriptor with
                    { MeasuredPayload = child.Descriptor.MeasuredPayload! with { Sha1 = OtherSha1 } } };
                break;
            case "nullable-sha1-removed":
            case "nullable-sha1-enriched":
                string? sha1 = mutation == "nullable-sha1-removed" ? null : child.Descriptor.MeasuredPayload!.Sha1;
                parent = parent with { Descriptor = parent.Descriptor with
                    { Id = parent.Id with { SourceVersion = parent.Id.SourceVersion! with { Sha1 = sha1 } } } };
                child = child with { Descriptor = child.Descriptor with
                    { Id = child.Id with { SourceVersion = child.Id.SourceVersion! with { Sha1 = sha1 } } } };
                break;
            case "context-promotion":
                Assert.True(parent.ContextOnly);
                parent = parent with { ContextOnly = false };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        // Construction and parsing are both valid: this is a semantic substitute,
        // not malformed JSON. Descriptor/context substitutions keep
        // *all* IDs. Role and source-SHA1 are parts of the ID itself, so those
        // cases preserve occurrence but must NOT be treated as an identity alias.
        var altered = new NativeMediaCandidateSelection([parent, child]);
        Assert.Equal(altered.CanonicalJson, NativeMediaCandidateSelection.Parse(altered.CanonicalJson).CanonicalJson);
        Assert.Equal(safe.Members.Select(m => m.Id.Occurrence).ToArray(), altered.Members.Select(m => m.Id.Occurrence).ToArray());
        if (mutation is "status" or "retained" or "metadata" or "measured-sha1" or "context-promotion")
            Assert.Equal(safe.Members.Select(m => m.Id).ToArray(), altered.Members.Select(m => m.Id).ToArray());
        else
            Assert.NotEqual(safe.Members[1].Id, altered.Members[1].Id);
        Assert.NotEqual(safe.CanonicalJson, altered.CanonicalJson);
        SetWire(row, safe: true, altered.CanonicalJson);
        if (state != "Preparing")
            row["safe_inventory_sha256"] = altered.CreateInventory(PersistedMediaFixture.OperationId,
                PersistedMediaFixture.Fingerprint, PersistedMediaFixture.ImportedAt).Sha256;
        // Keep the same counts, and a distinct Safe directory. Both valid
        // inventories have one evidence and one contained derived descriptor,
        // zero original bindings. Hash/counter/SQL-alias failures cannot mask a
        // missing Full.ValidateSubset call, including in Preparing.
        AssertStoredThenRejected(fixture, merge: true, row, selectionDecoder: true);
    }

    public static IEnumerable<object[]> ForeignInventoryCases()
    {
        foreach ((bool merge, bool safe) in SelectionColumns())
        foreach (string state in new[] { "Prepared", "Committed" })
        foreach (string provenance in new[] { "operation", "package", "time" })
            yield return [merge, safe, state, provenance];
    }

    [Theory]
    [MemberData(nameof(ForeignInventoryCases))]
    public void Ready_inventory_digest_is_reconstructed_with_stored_operation_package_and_time(
        bool merge, bool safe, string state, string provenance)
    {
        using var fixture = new PersistedMediaFixture();
        NativeMediaCandidateSelection selection = PersistedMediaFixture.Full;
        Dictionary<string, object?> row = PersistedMediaFixture.Row(merge, state: state,
            full: selection, safe: safe ? selection : null);
        Guid operation = provenance == "operation" ? ForeignOperation : PersistedMediaFixture.OperationId;
        string fingerprint = provenance == "package" ? ForeignFingerprint : PersistedMediaFixture.Fingerprint;
        DateTimeOffset time = provenance == "time" ? PersistedMediaFixture.ImportedAt.AddTicks(1) : PersistedMediaFixture.ImportedAt;
        string foreignHash = selection.CreateInventory(operation, fingerprint, time).Sha256;
        string inventoryColumn = Prefix(safe) + "_inventory_sha256";
        Assert.NotEqual(row[inventoryColumn], foreignHash);
        row[inventoryColumn] = foreignHash;
        // Only the inventory digest changed. Stored identity and canonical
        // selection still agree, but a real inventory of another provenance is
        // not acceptable merely because it is a well-formed lowercase hash.
        Assert.Equal(selection.CanonicalJson, row[Prefix(safe) + "_selection_json"]);
        Assert.Equal(selection.Sha256, row[Prefix(safe) + "_selection_sha256"]);

        AssertStoredThenRejected(fixture, merge, row);
    }

    private static IEnumerable<(bool Merge, bool Safe)> SelectionColumns()
    {
        yield return (false, false); // Import Full.
        yield return (true, false);  // Merge Full.
        yield return (true, true);   // Merge Safe, after valid Full has decoded.
    }

    private static string Prefix(bool safe) => safe ? "safe" : "full";

    private static void SetWire(Dictionary<string, object?> row, bool safe, string json)
    {
        using JsonDocument wire = JsonDocument.Parse(json);
        string prefix = Prefix(safe);
        row[prefix + "_selection_json"] = json;
        row[prefix + "_selection_sha256"] = Hash(json);
        row[prefix + "_member_count"] = (long)wire.RootElement.GetProperty("members").GetArrayLength();
    }

    private static string CorruptCanonicalWire(string canonical, string mutation)
    {
        using JsonDocument wire = JsonDocument.Parse(canonical);
        string[] members = wire.RootElement.GetProperty("members").EnumerateArray().Select(m => m.GetRawText()).ToArray();
        Assert.Equal(2, members.Length);
        return mutation switch
        {
            "duplicate-root-field" => canonical.Replace("\"version\":2", "\"version\":2,\"version\":2", StringComparison.Ordinal),
            "duplicate-nested-field" => canonical.Replace("\"contextOnly\":false", "\"contextOnly\":false,\"contextOnly\":false", StringComparison.Ordinal),
            "unknown-root-field" => canonical.Replace("{\"version\":2,", "{\"unknown\":1,\"version\":2,", StringComparison.Ordinal),
            "unknown-nested-field" => canonical.Replace("\"descriptor\":{", "\"descriptor\":{\"unknown\":1,", StringComparison.Ordinal),
            "property-order" => "{\"members\":" + wire.RootElement.GetProperty("members").GetRawText() + ",\"version\":2}",
            "member-order" => "{\"version\":2,\"members\":[" + members[1] + "," + members[0] + "]}",
            "duplicate-member" => "{\"version\":2,\"members\":[" + string.Join(",", members.Append(members[0])) + "]}",
            "leading-space" => " " + canonical,
            "internal-space" => canonical.Replace("\"version\":2,", "\"version\": 2,", StringComparison.Ordinal),
            "trailing-newline" => canonical + "\n",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
    }

    private static void AssertStoredThenRejected(PersistedMediaFixture fixture, bool merge,
        Dictionary<string, object?> row, bool selectionDecoder = false)
    {
        Assert.Equal(0, ArchiveSql.GetPragma(fixture.Connection, "ignore_check_constraints"));
        fixture.Insert(merge, row); // Never wrap INSERT in Assert.Throws: SQL must accept the row.
        AssertStoredRow(fixture.Connection, merge, row);
        Guid operation = Guid.Parse((string)row["operation_id"]!);
        string request = (string)row["request_sha256"]!;
        InvalidDataException single = Assert.Throws<InvalidDataException>(() =>
        {
            if (merge) _ = ArchiveSql.ReadPersistedMediaMergeOperation(fixture.Connection, null, operation, request);
            else _ = ArchiveSql.ReadPersistedMediaImportOperation(fixture.Connection, null, operation, request);
        });
        AssertStoredRow(fixture.Connection, merge, row);
        InvalidDataException page = Assert.Throws<InvalidDataException>(() =>
        {
            if (merge) _ = ArchiveSql.ReadPersistedMediaMergeOperationsPage(fixture.Connection, null);
            else _ = ArchiveSql.ReadPersistedMediaImportOperationsPage(fixture.Connection, null);
        });
        AssertStoredRow(fixture.Connection, merge, row);
        Assert.Equal(0, ArchiveSql.GetPragma(fixture.Connection, "ignore_check_constraints"));
        foreach (InvalidDataException error in new[] { single, page })
        {
            if (selectionDecoder)
                Assert.Contains(error.Message, new[] { "Invalid passive media member selection.", "Invalid canonical passive media member selection." });
            else
                Assert.Equal("Invalid persisted media transfer evidence.", error.Message);
        }
    }

    private static void AssertStoredRow(SqliteConnection connection, bool merge, Dictionary<string, object?> row)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM " + (merge ? "archive_media_merge_operations" : "archive_media_import_operations");
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        foreach ((string column, object? expected) in row)
        {
            object actual = reader.GetValue(reader.GetOrdinal(column));
            if (expected == null) Assert.Same(DBNull.Value, actual);
            else if (expected is byte[] bytes) Assert.Equal(bytes, Assert.IsType<byte[]>(actual));
            else Assert.Equal(expected, actual);
        }
        Assert.False(reader.Read());
    }

    private static NativeMediaCandidateSelection DerivedSelection(bool knownSha1 = true)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("persisted-selection-adversarial");
        string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string md5 = Convert.ToBase64String(MD5.HashData(bytes));
        string sha1 = Convert.ToBase64String(SHA1.HashData(bytes));
        var provider = new MediaProviderMetadata("fixture-media", "original", "https://i.4cdn.org/b/123.jpg",
            "fixture.jpg", bytes.Length, md5, sha256);
        string key = MediaRevisionKeys.CreateAttachmentKey(PersistedMediaFixture.Resource, "10", 0, provider);
        var metadata = new CapturedAttachment(0, provider.MediaId, "original", provider.Url,
            provider.OriginalFileName, ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
        { ExpectedSha256 = provider.ExpectedSha256, AttachmentRevisionKey = key };
        var version = new TransferOriginalByteVersion(bytes.Length, sha256, md5, knownSha1 ? sha1 : null);
        var parent = new NativeMediaSelectedMember(new(
            new(new(PersistedMediaFixture.Resource, "10", 0, key), version, NativeMediaMemberRole.OriginalEvidence),
            metadata, false, TransferContentStatus.Omitted, null, null, null), false);
        var preview = new NativeMediaSelectedMember(new(parent.Id with { Role = NativeMediaMemberRole.Preview },
            metadata, false, TransferContentStatus.Contained, sha256, null,
            new TransferMeasuredBlob(bytes.Length, sha256, md5, sha1)), false);
        return NativeMediaCandidateSelection.Create([parent, preview]);
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

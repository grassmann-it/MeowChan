using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// W32b2.2 read-only SQL evidence. No catalog validation, filesystem inspection,
// candidate publication, recovery or Apply authority is inferred from these records.
public sealed class PersistedMediaImportReadTests
{
    public static IEnumerable<object[]> VersionsAndStates()
    {
        foreach (int version in new[] { 1, 2 })
        foreach (string state in new[] { "Preparing", "Prepared", "Committed" })
            yield return [version, state];
    }

    [Theory]
    [MemberData(nameof(VersionsAndStates))]
    public void All_versions_and_states_preserve_complete_identity_root_and_counts(int version, string state)
    {
        using var f = new PersistedMediaFixture();
        var row = version == 2
            ? PersistedMediaFixture.Row(false, version, state, full: RichSelection())
            : PersistedMediaFixture.Row(false, version, state);
        row["post_count"] = 23L;
        row["attachment_count"] = 17L;
        if (version == 1)
        {
            // Preserve the stored spelling as well as the decoded keys, not a reserialized DTO.
            string[] legacyKeys = JsonSerializer.Deserialize<string[]>((string)row["revision_keys_json"]!)!;
            row["revision_keys_json"] = "[\n  \"" + legacyKeys[0] + "\",\n  \"" + legacyKeys[1] + "\"\n]";
        }
        else
        {
            // Deliberately not a TransferCatalog. The reader must retain opaque bytes,
            // not invent an envelope/validate or normalize a catalog from the selection.
            row["catalog_bytes"] = new byte[] { 0xff, 0, 0xfe, (byte)'{', (byte)'}', 0x80 };
        }
        f.Insert(false, row);

        NativeMediaPersistedImportOperation actual = Read(f, row);
        AssertComplete(row, actual);
        Assert.Equal(Enum.Parse<NativeMediaImportOperationState>(state), actual.State);
        if (version == 1)
        {
            Assert.Equal(JsonSerializer.Deserialize<string[]>((string)row["revision_keys_json"]!)!,
                actual.Full.LegacyKeys.ToArray());
            Assert.Null(actual.Full.Selection);
            Assert.True(actual.CatalogBytes.IsEmpty);
        }
        else
        {
            NativeMediaCandidateSelection selection = RichSelection();
            Assert.Equal(selection.CanonicalJson, actual.Full.Selection!.CanonicalJson);
            Assert.Equal(selection.Members.ToArray(), actual.Full.Selection.Members.ToArray());
            Assert.Equal(2, actual.Counts.EvidenceAdditions);
            Assert.Equal(2, actual.Counts.DerivedDescriptorAdditions);
            Assert.Equal(1, actual.Counts.ContainedDerivedAdditions);
            Assert.Equal(1, actual.Counts.OriginalBindingCount);
            Assert.True(actual.Full.LegacyKeys.IsEmpty);
        }
        Assert.Equal(23, actual.Counts.PostCount);
        Assert.Equal(17, actual.Counts.AttachmentCount);
    }

    [Theory]
    [InlineData("Preparing")]
    [InlineData("Prepared")]
    [InlineData("Committed")]
    public void V2_evidence_only_import_needs_no_original_binding_or_attachment_payload(string state)
    {
        using var f = new PersistedMediaFixture();
        var selection = new NativeMediaCandidateSelection([PersistedMediaFixture.Evidence("10")]);
        var row = PersistedMediaFixture.Row(false, 2, state, full: selection);
        row["attachment_count"] = 0L;
        f.Insert(false, row);

        NativeMediaPersistedImportOperation actual = Read(f, row);
        AssertComplete(row, actual);
        Assert.Equal(selection.CanonicalJson, actual.Full.Selection!.CanonicalJson);
        Assert.Equal(1, actual.Full.MemberCount);
        Assert.Equal(1, actual.Counts.EvidenceAdditions);
        Assert.Equal(0, actual.Counts.AttachmentCount);
        Assert.Equal(0, actual.Counts.OriginalBindingCount);
        Assert.Equal(0, actual.Counts.DerivedDescriptorAdditions);
        Assert.Equal(0, actual.Counts.ContainedDerivedAdditions);
        Assert.True(actual.Full.Selection.CreateInventory(actual.Identity.OperationId,
            actual.Identity.PackageFingerprint, actual.Identity.ImportedAtUtc).Bindings.IsEmpty);
        // A ready evidence-only root is legitimate; it is not fabricated availability.
        Assert.Equal(state == "Preparing", actual.Full.Directory is null);
    }

    [Fact]
    public void Opaque_catalog_is_an_immutable_snapshot_independent_of_input_and_caller_copies()
    {
        using var f = new PersistedMediaFixture();
        var row = PersistedMediaFixture.Row(false, 2, "Prepared");
        byte[] opaque = [0, 0xff, 0x80, 1, 2, 3];
        byte[] expected = (byte[])opaque.Clone();
        row["catalog_bytes"] = opaque;
        f.Insert(false, row);
        NativeMediaPersistedImportOperation actual = Read(f, row);
        opaque[0] = 99;
        byte[] callerCopy = actual.CatalogBytes.ToArray();
        callerCopy[1] = 42;

        Assert.Equal(expected, actual.CatalogBytes.ToArray());
        Assert.Equal(expected, Read(f, row).CatalogBytes.ToArray());
    }

    [Theory]
    [InlineData("Preparing")]
    [InlineData("Prepared")]
    [InlineData("Committed")]
    public void Schema_valid_wrong_selection_SHA_is_rejected_by_decoder(string state)
    {
        var row = PersistedMediaFixture.Row(false, 2, state);
        row["full_selection_sha256"] = OtherHash((string)row["full_selection_sha256"]!);
        AssertDecoderRejects(row);
    }

    [Theory]
    [InlineData("Preparing")]
    [InlineData("Prepared")]
    [InlineData("Committed")]
    public void Schema_valid_wrong_nonzero_member_count_is_rejected_by_decoder(string state)
    {
        var row = PersistedMediaFixture.Row(false, 2, state);
        row["full_member_count"] = Convert.ToInt64(row["full_member_count"], CultureInfo.InvariantCulture) + 1;
        AssertDecoderRejects(row);
    }

    [Theory]
    [InlineData("Preparing")]
    [InlineData("Prepared")]
    [InlineData("Committed")]
    public void Schema_valid_selection_for_another_resource_is_rejected_by_decoder(string state)
    {
        var row = PersistedMediaFixture.Row(false, 2, state);
        row["thread"] = PersistedMediaFixture.Resource.Thread + "99";
        AssertDecoderRejects(row);
    }

    [Theory]
    [InlineData("Preparing", "whitespace")]
    [InlineData("Prepared", "whitespace")]
    [InlineData("Committed", "whitespace")]
    [InlineData("Preparing", "unknown-field")]
    [InlineData("Prepared", "unknown-field")]
    [InlineData("Committed", "unknown-field")]
    [InlineData("Preparing", "malformed")]
    [InlineData("Prepared", "malformed")]
    [InlineData("Committed", "malformed")]
    public void Schema_valid_text_with_matching_SHA_still_requires_exact_canonical_selection(string state, string fault)
    {
        var row = PersistedMediaFixture.Row(false, 2, state);
        string canonical = (string)row["full_selection_json"]!;
        string invalid = fault switch
        {
            "whitespace" => " " + canonical,
            "unknown-field" => "{\"unknown\":true," + canonical[1..],
            "malformed" => "not a selection",
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
        row["full_selection_json"] = invalid;
        row["full_selection_sha256"] = Hash(Encoding.UTF8.GetBytes(invalid));
        AssertDecoderRejects(row);
    }

    public static IEnumerable<object[]> ReadyInventoryFaults()
    {
        foreach (string state in new[] { "Prepared", "Committed" })
        foreach (string fault in new[] { "arbitrary-hash", "operation", "package", "time", "selection" })
            yield return [state, fault];
    }

    [Theory]
    [MemberData(nameof(ReadyInventoryFaults))]
    public void Ready_inventory_is_reconstructed_with_exact_selection_and_provenance(string state, string fault)
    {
        var row = PersistedMediaFixture.Row(false, 2, state);
        NativeMediaCandidateSelection selection = NativeMediaCandidateSelection.Parse((string)row["full_selection_json"]!);
        Guid operation = Guid.Parse((string)row["operation_id"]!);
        string fingerprint = (string)row["package_fingerprint"]!;
        DateTimeOffset time = DateTimeOffset.Parse((string)row["imported_at"]!, CultureInfo.InvariantCulture);
        string expected = selection.CreateInventory(operation, fingerprint, time).Sha256;
        string wrong = fault switch
        {
            "arbitrary-hash" => OtherHash(expected),
            "operation" => selection.CreateInventory(Guid.Parse("ce000000-0000-4000-8000-000000000099"), fingerprint, time).Sha256,
            "package" => selection.CreateInventory(operation, OtherHash(fingerprint), time).Sha256,
            "time" => selection.CreateInventory(operation, fingerprint, time.AddTicks(1)).Sha256,
            "selection" => new NativeMediaCandidateSelection([PersistedMediaFixture.Evidence("987")])
                .CreateInventory(operation, fingerprint, time).Sha256,
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
        Assert.NotEqual(expected, wrong);
        row["full_inventory_sha256"] = wrong;
        AssertDecoderRejects(row);
    }

    public static IEnumerable<object[]> V2CounterFaults()
    {
        foreach (string state in new[] { "Preparing", "Prepared", "Committed" })
        foreach (string field in new[] { "evidence_additions", "derived_descriptor_additions", "contained_derived_additions", "original_binding_count" })
            yield return [state, field];
    }

    [Theory]
    [MemberData(nameof(V2CounterFaults))]
    public void Every_v2_import_inventory_counter_must_equal_full_inventory_not_a_delta(string state, string field)
    {
        var row = PersistedMediaFixture.Row(false, 2, state, full: RichSelection());
        // Each chosen wrong value still satisfies SQL's Int32 and contained<=descriptor checks.
        row[field] = field == "contained_derived_additions" ? 0L
            : Convert.ToInt64(row[field], CultureInfo.InvariantCulture) + 1;
        AssertDecoderRejects(row);
    }

    public static IEnumerable<object[]> V1Contamination()
    {
        foreach (string state in new[] { "Preparing", "Prepared", "Committed" })
        foreach (string field in new[] { "full_selection_json", "full_selection_sha256", "full_inventory_sha256", "full_member_count",
                     "catalog_bytes", "evidence_additions", "derived_descriptor_additions", "contained_derived_additions", "original_binding_count" })
            yield return [state, field];
    }

    [Theory]
    [MemberData(nameof(V1Contamination))]
    public void Decoder_independently_rejects_v1_contamination_when_CHECKs_are_explicitly_bypassed(string state, string field)
    {
        using var f = new PersistedMediaFixture();
        var row = PersistedMediaFixture.Row(false, 1, state);
        // Positive control: do not pass merely because the baseline fixture is invalid.
        f.Insert(false, row);
        AssertComplete(row, Read(f, row));
        f.Execute("DELETE FROM archive_media_import_operations");
        row[field] = field switch
        {
            "full_selection_json" => PersistedMediaFixture.Full.CanonicalJson,
            "full_selection_sha256" => PersistedMediaFixture.Full.Sha256,
            "full_inventory_sha256" => new string('c', 64),
            "catalog_bytes" => new byte[] { 1, 2, 3 },
            _ => 1L
        };
        if (field == "contained_derived_additions") row["derived_descriptor_additions"] = 1L;
        // These rows normally fail schema10 CHECKs. Bypass *only* to test the decoder;
        // restore enforcement before reading, so SQL rejection cannot satisfy the assertion.
        f.Execute("PRAGMA ignore_check_constraints=ON");
        try { f.Insert(false, row); }
        finally { f.Execute("PRAGMA ignore_check_constraints=OFF"); }
        Assert.Throws<InvalidDataException>(() => Read(f, row));
    }

    [Fact]
    public void Missing_operation_is_null_but_existing_operation_with_wrong_request_is_not_hidden()
    {
        using var f = new PersistedMediaFixture();
        var row = PersistedMediaFixture.Row(false);
        Assert.Null(ArchiveSql.ReadPersistedMediaImportOperation(f.Connection, null,
            Guid.Parse((string)row["operation_id"]!), (string)row["request_sha256"]!));
        f.Insert(false, row);
        Assert.Throws<InvalidDataException>(() => ArchiveSql.ReadPersistedMediaImportOperation(f.Connection, null,
            Guid.Parse((string)row["operation_id"]!), OtherHash((string)row["request_sha256"]!)));
        AssertComplete(row, Read(f, row));
    }

    public static IEnumerable<object[]> ValidUnicodeResourceCases()
    {
        foreach (int version in new[] { 1, 2 })
        foreach (string state in new[] { "Preparing", "Prepared", "Committed" })
        foreach (string component in new[] { "source", "board", "thread" })
        foreach (string shape in new[] { "cjk171", "cjk256", "astral128", "mixed256" })
            yield return [version, state, component, shape];
    }

    [Theory]
    [MemberData(nameof(ValidUnicodeResourceCases))]
    public void Unicode_resource_uses_existing_UTF16_identity_limits_on_single_and_page_reads(
        int version, string state, string component, string shape)
    {
        string value = UnicodeResourceValue(shape);
        Assert.InRange(value.Length, 1, 256);
        Assert.Equal(shape == "cjk171" ? 171 : 256, value.Length);
        Assert.Equal(shape switch { "cjk171" => 513, "cjk256" => 768,
            "astral128" => 512, "mixed256" => 766, _ => throw new ArgumentOutOfRangeException(nameof(shape)) },
            Encoding.UTF8.GetByteCount(value));
        ResourceId resource = ResourceWithComponent(component, value);
        ArchiveValidation.ValidateId(resource); // Existing identity API accepts the positive control.
        using var f = new PersistedMediaFixture();
        var row = UnicodeResourceRow(version, state, resource);
        f.Insert(false, row); // Keep all real Schema10 CHECK constraints enabled.

        NativeMediaPersistedImportOperation single = Read(f, row);
        AssertUnicodeResource(row, single, resource);
        var page = ArchiveSql.ReadPersistedMediaImportOperationsPage(f.Connection, null);
        Assert.Null(page.NextOperationId);
        AssertUnicodeResource(row, Assert.Single(page.Items), resource);
    }

    public static IEnumerable<object[]> OversizedUnicodeResourceCases()
    {
        foreach (int version in new[] { 1, 2 })
        foreach (string state in new[] { "Preparing", "Prepared", "Committed" })
        foreach (string component in new[] { "source", "board", "thread" })
        foreach (string shape in new[] { "ascii257", "cjk257", "astral257" })
            yield return [version, state, component, shape];
    }

    [Theory]
    [MemberData(nameof(OversizedUnicodeResourceCases))]
    public void Widened_UTF8_gate_does_not_relax_257_UTF16_code_unit_rejection(
        int version, string state, string component, string shape)
    {
        using var f = new PersistedMediaFixture();
        ResourceId valid = ResourceWithComponent(component, UnicodeResourceValue("cjk256"));
        var row = UnicodeResourceRow(version, state, valid);
        f.Insert(false, row);
        AssertUnicodeResource(row, Read(f, row), valid);
        AssertUnicodeResource(row,
            Assert.Single(ArchiveSql.ReadPersistedMediaImportOperationsPage(f.Connection, null).Items), valid);

        string invalid = UnicodeResourceValue(shape);
        Assert.Equal(257, invalid.Length);
        Assert.Throws<ArgumentException>(() => ArchiveValidation.ValidateId(ResourceWithComponent(component, invalid)));
        using (var command = f.Connection.CreateCommand())
        {
            // component comes only from the three fixed theory values; never interpolate data.
            _ = ResourceWithComponent(component, invalid);
            command.CommandText = $"UPDATE archive_media_import_operations SET {component}=$value WHERE operation_id=$op";
            command.Parameters.AddWithValue("$value", invalid);
            command.Parameters.AddWithValue("$op", PersistedMediaFixture.OperationId.ToString("D"));
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        // Deliberately corrupt only SQL identity after a coherent positive control.
        // For <=768 bytes require the identity validator's inner exception, so neither
        // a resource/selection mismatch nor a codec failure can falsely satisfy us.
        InvalidDataException single = Assert.Throws<InvalidDataException>(() => Read(f, row));
        InvalidDataException page = Assert.Throws<InvalidDataException>(() =>
            ArchiveSql.ReadPersistedMediaImportOperationsPage(f.Connection, null));
        foreach (InvalidDataException error in new[] { single, page })
        {
            Assert.Equal("Invalid persisted media transfer evidence.", error.Message);
            if (Encoding.UTF8.GetByteCount(invalid) <= 3 * 256)
            {
                ArgumentException identityError = Assert.IsType<ArgumentException>(error.InnerException);
                Assert.Equal(component switch { "source" => "Source", "board" => "Board", "thread" => "Thread",
                    _ => throw new ArgumentOutOfRangeException(nameof(component)) }, identityError.ParamName);
            }
            else Assert.Null(error.InnerException); // 257 CJK exceeds the UTF-8 bound as well.
        }
    }

    private static string UnicodeResourceValue(string shape) => shape switch
    {
        "cjk171" => new string('界', 171),
        "cjk256" => new string('界', 256),
        "astral128" => string.Concat(Enumerable.Repeat("\U0001F600", 128)),
        "mixed256" => new string('界', 254) + "\U0001F600",
        "ascii257" => new string('a', 257),
        "cjk257" => new string('界', 257),
        "astral257" => string.Concat(Enumerable.Repeat("\U0001F600", 128)) + "a",
        _ => throw new ArgumentOutOfRangeException(nameof(shape))
    };

    private static ResourceId ResourceWithComponent(string component, string value) => component switch
    {
        "source" => PersistedMediaFixture.Resource with { Source = value },
        "board" => PersistedMediaFixture.Resource with { Board = value },
        "thread" => PersistedMediaFixture.Resource with { Thread = value },
        _ => throw new ArgumentOutOfRangeException(nameof(component))
    };

    private static Dictionary<string, object?> UnicodeResourceRow(int version, string state, ResourceId resource)
    {
        var row = PersistedMediaFixture.Row(false, version, state);
        row["source"] = resource.Source;
        row["board"] = resource.Board;
        row["thread"] = resource.Thread;
        NativeMediaCandidateSelection selection = RichSelection(resource);
        // Derive actual revision keys from this resource/provider, not format-only hashes.
        string[] keys = selection.Members.Where(member => member.Id.Role == NativeMediaMemberRole.OriginalPayload)
            .Select(member => member.Id.Occurrence.OriginalRevisionKey!).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        if (version == 1)
        {
            row["revision_keys_json"] = JsonSerializer.Serialize(keys);
            row["attachment_count"] = (long)keys.Length;
            return row;
        }
        NativeMediaCandidateInventory inventory = selection.CreateInventory(PersistedMediaFixture.OperationId,
            PersistedMediaFixture.Fingerprint, PersistedMediaFixture.ImportedAt);
        row["full_selection_json"] = selection.CanonicalJson;
        row["full_selection_sha256"] = selection.Sha256;
        row["full_member_count"] = (long)selection.Members.Length;
        row["full_inventory_sha256"] = state == "Preparing" ? null : inventory.Sha256;
        row["evidence_additions"] = (long)inventory.OriginalEvidence.Length;
        row["derived_descriptor_additions"] = (long)inventory.DerivedRepresentations.Length;
        row["contained_derived_additions"] = (long)inventory.DerivedRepresentations.Count(
            derived => derived.IncomingStatus == MediaDerivedStatus.Contained);
        row["attachment_count"] = (long)inventory.OriginalEvidence.Length;
        row["original_binding_count"] = (long)inventory.Bindings.Length;
        return row;
    }

    private static void AssertUnicodeResource(Dictionary<string, object?> row,
        NativeMediaPersistedImportOperation actual, ResourceId resource)
    {
        AssertComplete(row, actual);
        Assert.Equal(resource, actual.Identity.Resource);
        if (actual.Identity.PlanVersion != 2) return;
        Assert.All(actual.Full.Selection!.Members, member => Assert.Equal(resource, member.Id.Occurrence.Resource));
        if (actual.State != NativeMediaImportOperationState.Preparing)
            Assert.Equal(actual.Full.Selection.CreateInventory(actual.Identity.OperationId,
                actual.Identity.PackageFingerprint, actual.Identity.ImportedAtUtc).Sha256, actual.Full.InventorySha256);
    }

    private static void AssertDecoderRejects(Dictionary<string, object?> row)
    {
        using var f = new PersistedMediaFixture();
        // Intentionally outside Assert.Throws: these faults are schema-valid, so an
        // INSERT/CHECK failure is a broken fixture, never a successful decoder test.
        f.Insert(false, row);
        Assert.Throws<InvalidDataException>(() => Read(f, row));
    }

    private static NativeMediaPersistedImportOperation Read(PersistedMediaFixture f, Dictionary<string, object?> row)
        => Assert.IsType<NativeMediaPersistedImportOperation>(ArchiveSql.ReadPersistedMediaImportOperation(
            f.Connection, null, Guid.Parse((string)row["operation_id"]!), (string)row["request_sha256"]!));

    private static void AssertComplete(Dictionary<string, object?> row, NativeMediaPersistedImportOperation actual)
    {
        Assert.Equal(Convert.ToInt32(row["plan_version"], CultureInfo.InvariantCulture), actual.Identity.PlanVersion);
        Assert.Equal(Guid.Parse((string)row["operation_id"]!), actual.Identity.OperationId);
        Assert.Equal(row["request_sha256"], actual.Identity.RequestSha256);
        Assert.Equal(row["package_fingerprint"], actual.Identity.PackageFingerprint);
        Assert.Equal(row["catalog_instance"], actual.Identity.CatalogInstance);
        Assert.Equal(Convert.ToInt64(row["catalog_generation"], CultureInfo.InvariantCulture), actual.Identity.CatalogGeneration);
        Assert.Equal(new ResourceId((string)row["source"]!, (string)row["board"]!, (string)row["thread"]!), actual.Identity.Resource);
        Assert.Equal(row["operations_parent"], actual.Identity.OperationsParent);
        Assert.Equal(DateTimeOffset.Parse((string)row["imported_at"]!, CultureInfo.InvariantCulture), actual.Identity.ImportedAtUtc);
        Assert.Equal(Enum.Parse<NativeMediaImportOperationState>((string)row["state"]!), actual.State);
        Assert.Equal(row["revision_keys_json"], actual.Full.LegacyKeysJson);
        Assert.Equal(JsonSerializer.Deserialize<string[]>((string)row["revision_keys_json"]!)!, actual.Full.LegacyKeys.ToArray());
        Assert.Equal(row["bindings_sha256"], actual.Full.LegacyBindingsSha256);
        Assert.Equal(row["full_selection_json"], actual.Full.Selection?.CanonicalJson);
        Assert.Equal(row["full_selection_sha256"], actual.Full.SelectionSha256);
        Assert.Equal(Convert.ToInt32(row["full_member_count"], CultureInfo.InvariantCulture), actual.Full.MemberCount);
        Assert.Equal(row["full_inventory_sha256"], actual.Full.InventorySha256);
        Assert.Equal(row["candidate_directory"], actual.Full.Directory);
        Assert.Equal(row["manifest_generation"], actual.Full.ManifestGeneration);
        Assert.Equal(row["catalog_bytes"] as byte[] ?? [], actual.CatalogBytes.ToArray());
        Assert.Equal(new NativeMediaImportCounts(
            Convert.ToInt32(row["post_count"], CultureInfo.InvariantCulture),
            Convert.ToInt32(row["attachment_count"], CultureInfo.InvariantCulture),
            Convert.ToInt32(row["evidence_additions"], CultureInfo.InvariantCulture),
            Convert.ToInt32(row["derived_descriptor_additions"], CultureInfo.InvariantCulture),
            Convert.ToInt32(row["contained_derived_additions"], CultureInfo.InvariantCulture),
            Convert.ToInt32(row["original_binding_count"], CultureInfo.InvariantCulture)), actual.Counts);
    }

    private static NativeMediaCandidateSelection RichSelection(ResourceId? resource = null)
    {
        resource ??= PersistedMediaFixture.Resource;
        TransferMeasuredBlob bytes = Measure(Encoding.UTF8.GetBytes("synthetic persisted original"));
        TransferMeasuredBlob previewBytes = Measure(Encoding.UTF8.GetBytes("synthetic persisted preview"));
        var provider = new MediaProviderMetadata("123", "original", "https://i.4cdn.org/b/123.webm", "123.webm",
            bytes.Length, bytes.Md5, bytes.Sha256);
        string key = MediaRevisionKeys.CreateAttachmentKey(resource, "10", 0, provider);
        var metadata = new CapturedAttachment(0, provider.MediaId, "original", provider.Url, provider.OriginalFileName,
            ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
        { ExpectedSha256 = provider.ExpectedSha256, AttachmentRevisionKey = key };
        var deleted = PersistedMediaFixture.Evidence("11");
        deleted = deleted with { Descriptor = deleted.Descriptor with
            { Id = deleted.Id with { Occurrence = deleted.Id.Occurrence with { Resource = resource } } } };
        var id = new NativeMediaMemberId(new(resource, "10", 0, key),
            new(bytes.Length, bytes.Sha256, bytes.Md5, bytes.Sha1), NativeMediaMemberRole.OriginalEvidence);
        var evidence = new NativeMediaSelectedMember(new(id, metadata, false, TransferContentStatus.Contained, null, null, null), false);
        var original = new NativeMediaSelectedMember(new(id with { Role = NativeMediaMemberRole.OriginalPayload },
            metadata, false, TransferContentStatus.Contained, bytes.Sha256, null, bytes), false);
        var preview = new NativeMediaSelectedMember(new(id with { Role = NativeMediaMemberRole.Preview },
            metadata, false, TransferContentStatus.Contained, previewBytes.Sha256, null, previewBytes), false);
        var omittedPoster = new NativeMediaSelectedMember(new(id with { Role = NativeMediaMemberRole.VideoPosterPng },
            metadata, false, TransferContentStatus.Omitted, null, null, null), false);
        return new([evidence, original, preview, omittedPoster, deleted]);
    }

    private static TransferMeasuredBlob Measure(byte[] bytes) => new(bytes.Length, Hash(bytes),
        Convert.ToBase64String(MD5.HashData(bytes)), Convert.ToBase64String(SHA1.HashData(bytes)));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string OtherHash(string value) => value == new string('a', 64) ? new string('b', 64) : new string('a', 64);
}

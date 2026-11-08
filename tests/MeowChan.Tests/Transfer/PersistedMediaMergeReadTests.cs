using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Storage;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests.Transfer;

// W32b2.2 read-only SQL evidence. No filesystem, validated catalog, recovery,
// Apply or public-success claim. Budget and receipt coverage lives elsewhere.
public sealed class PersistedMediaMergeReadTests
{
    private static readonly string[] States = ["Preparing", "Prepared", "Committed"];
    private static readonly byte[] OpaqueCatalog = [0xff, 0x00, 0x7b, 0x01, 0xfe];

    public static IEnumerable<object[]> VersionsAndStates()
    {
        foreach (int version in new[] { 1, 2 })
        foreach (string state in States)
            yield return [version, state];
    }

    [Theory]
    [MemberData(nameof(VersionsAndStates))]
    public void EveryVersionAndStatePreservesCompleteIdentityRootsRawDocumentsAndCounters(int version, string state)
    {
        using PersistedMediaFixture f = new();
        NativeMediaCandidateSelection full = RichSelection();
        var row = version == 1
            ? PersistedMediaFixture.Row(true, version, state)
            : PersistedMediaFixture.Row(true, version, state, full: full, safe: full);
        if (version == 1) SetLegacySafe(row, state, alias: false);
        row["roots_json"] = "[\n { \"Directory\" : \"/synthetic/retained first\", \"Generation\" : \""
            + new string('1', 64) + "\" },\n { \"Generation\":\"" + new string('2', 64)
            + "\", \"Directory\":\"/synthetic/retained second\" }\n]";
        row["catalog_bytes"] = OpaqueCatalog.ToArray();
        row["new_posts"] = 2L;
        row["updated_posts"] = 3L;
        row["new_attachments"] = 5L;
        row["conflicts"] = 7L;
        if (version == 2)
        {
            // Merge counters are deltas, not the totals of either retained root.
            row["new_bindings"] = 0L;
            row["evidence_additions"] = 1L;
            row["derived_descriptor_additions"] = 1L;
            row["contained_derived_additions"] = 0L;
        }
        f.Insert(true, row); // Real schema10 CHECKs stay enabled on every positive path.
        long changes = TotalChanges(f);

        AssertComplete(row, Read(f));
        AssertComplete(row, Assert.Single(ArchiveSql.ReadPersistedMediaMergeOperationsPage(f.Connection, null).Items));
        Assert.Equal(changes, TotalChanges(f));
    }

    [Fact]
    public void OnePagePreservesMixedVersionsAndAllStatesWithoutProjectingAwayEvidence()
    {
        using PersistedMediaFixture f = new();
        var rows = new List<Dictionary<string, object?>>();
        int index = 1;
        foreach (int version in new[] { 1, 2 })
        foreach (string state in States)
        {
            Guid id = Guid.Parse("cd220000-0000-4000-8000-" + (index++).ToString("D12", CultureInfo.InvariantCulture));
            var row = PersistedMediaFixture.Row(true, version, state, operation: id);
            row["new_posts"] = (long)index;
            rows.Add(row);
        }
        foreach (var row in rows.AsEnumerable().Reverse()) f.Insert(true, row);
        long changes = TotalChanges(f);

        var page = ArchiveSql.ReadPersistedMediaMergeOperationsPage(f.Connection, null);
        Assert.Null(page.NextOperationId);
        Assert.Equal(rows.Count, page.Items.Length);
        for (int i = 0; i < rows.Count; i++) AssertComplete(rows[i], page.Items[i]);
        Assert.Equal(changes, TotalChanges(f));
    }

    [Theory]
    [MemberData(nameof(VersionsAndStates))]
    public void EmptySafeHasNoDocumentDigestInventoryDirectoryOrGeneration(int version, string state)
    {
        using PersistedMediaFixture f = new();
        var row = PersistedMediaFixture.Row(true, version, state);
        f.Insert(true, row);

        var result = Read(f);
        AssertComplete(row, result);
        Assert.Equal("[]", result.Safe.LegacyKeysJson);
        Assert.Empty(result.Safe.LegacyKeys);
        Assert.Null(result.Safe.LegacyBindingsSha256);
        Assert.Null(result.Safe.Selection);
        Assert.Null(result.Safe.SelectionSha256);
        Assert.Equal(0, result.Safe.MemberCount);
        Assert.Null(result.Safe.InventorySha256);
        Assert.Null(result.Safe.Directory);
        Assert.Null(result.Safe.ManifestGeneration);
        Assert.Equal(0, result.Counts.NewBindings);
        Assert.Equal(0, result.Counts.EvidenceAdditions);
        Assert.Equal(0, result.Counts.DerivedDescriptorAdditions);
        Assert.Equal(0, result.Counts.ContainedDerivedAdditions);
    }

    [Theory]
    [InlineData("Preparing")]
    [InlineData("Prepared")]
    [InlineData("Committed")]
    public void NonemptyEvidenceOnlySafeHasZeroBindingsButKeepsItsReadyRoot(string state)
    {
        using PersistedMediaFixture f = new();
        NativeMediaCandidateSelection full = PersistedMediaFixture.Full;
        NativeMediaCandidateSelection safe = full.Select([full.Members[0].Id]);
        var row = PersistedMediaFixture.Row(true, state: state, full: full, safe: safe);
        f.Insert(true, row);

        var result = Read(f);
        AssertComplete(row, result);
        Assert.Equal(1, result.Safe.MemberCount);
        Assert.Equal(0, result.Counts.NewBindings);
        Assert.Equal(1, result.Counts.EvidenceAdditions);
        Assert.Empty(safe.CreateInventory(PersistedMediaFixture.OperationId,
            PersistedMediaFixture.Fingerprint, PersistedMediaFixture.ImportedAt).Bindings);
        if (state == "Preparing") Assert.Null(result.Safe.Directory);
        else
        {
            Assert.NotNull(result.Safe.Directory);
            Assert.NotNull(result.Safe.ManifestGeneration);
            Assert.NotNull(result.Safe.InventorySha256);
        }
    }

    [Theory]
    [InlineData("Preparing")]
    [InlineData("Prepared")]
    [InlineData("Committed")]
    public void StrictDerivedSubsetPreservesExactDescriptorsAndOnlyAddsContextParent(string state)
    {
        using PersistedMediaFixture f = new();
        NativeMediaCandidateSelection full = RichSelection();
        NativeMediaSelectedMember preview = full.Members.Single(m => m.Id.Role == NativeMediaMemberRole.Preview);
        NativeMediaCandidateSelection safe = full.Select([preview.Id]);
        Assert.True(safe.Members.Length < full.Members.Length);
        var row = PersistedMediaFixture.Row(true, state: state, full: full, safe: safe);
        row["evidence_additions"] = 0L; // Parent evidence can already be present at the target.
        f.Insert(true, row);

        var result = Read(f);
        AssertComplete(row, result);
        var selected = Assert.IsType<NativeMediaCandidateSelection>(result.Safe.Selection);
        Assert.Equal(2, selected.Members.Length);
        var context = Assert.Single(selected.Members.Where(m => m.ContextOnly));
        var originalParent = full.Members.Single(m => m.Id == context.Id);
        Assert.False(originalParent.ContextOnly);
        Assert.Equal(originalParent.Descriptor, context.Descriptor);
        Assert.Equal(preview, Assert.Single(selected.Members.Where(m => !m.ContextOnly)));
        Assert.Equal(0, result.Counts.NewBindings);
        Assert.Equal(0, result.Counts.EvidenceAdditions);
        Assert.Equal(1, result.Counts.DerivedDescriptorAdditions);
        Assert.Equal(1, result.Counts.ContainedDerivedAdditions);
        if (state != "Preparing") Assert.NotNull(result.Safe.Directory);
    }

    [Theory]
    [InlineData("Preparing")]
    [InlineData("Prepared")]
    [InlineData("Committed")]
    public void MergeMayAddNothingDespiteFullAndSafeContainingAllKindsOfInventory(string state)
    {
        using PersistedMediaFixture f = new();
        NativeMediaCandidateSelection full = RichSelection();
        var inventory = full.CreateInventory(PersistedMediaFixture.OperationId,
            PersistedMediaFixture.Fingerprint, PersistedMediaFixture.ImportedAt);
        Assert.NotEmpty(inventory.Bindings);
        Assert.NotEmpty(inventory.OriginalEvidence);
        Assert.NotEmpty(inventory.DerivedRepresentations);
        var row = PersistedMediaFixture.Row(true, state: state, full: full, safe: full);
        foreach (string column in new[] { "new_bindings", "evidence_additions", "derived_descriptor_additions", "contained_derived_additions" })
            row[column] = 0L;
        f.Insert(true, row);

        var result = Read(f);
        AssertComplete(row, result);
        Assert.Equal(new NativeMediaMergeCounts(0, 0, 0, 0, 0, 0, 0, 0), result.Counts);
        Assert.Equal(full.Members.Length, result.Safe.MemberCount);
        if (state != "Preparing") Assert.NotNull(result.Safe.Directory);
    }

    [Theory]
    [InlineData(1, "Prepared")]
    [InlineData(1, "Committed")]
    [InlineData(2, "Prepared")]
    [InlineData(2, "Committed")]
    public void EqualFullSafeAliasIsAcceptedWithoutLosingEitherRoot(int version, string state)
    {
        using PersistedMediaFixture f = new();
        var row = AliasRow(version, state);
        f.Insert(true, row);

        var result = Read(f);
        AssertComplete(row, result);
        Assert.Equal(result.Full.Directory, result.Safe.Directory);
        Assert.Equal(result.Full.ManifestGeneration, result.Safe.ManifestGeneration);
        Assert.Equal(result.Full.LegacyBindingsSha256, result.Safe.LegacyBindingsSha256);
        Assert.Equal(result.Full.LegacyKeys.ToArray(), result.Safe.LegacyKeys.ToArray());
        Assert.Equal(result.Full.Selection?.CanonicalJson, result.Safe.Selection?.CanonicalJson);
        Assert.Equal(result.Full.InventorySha256, result.Safe.InventorySha256);
        Assert.Equal(0, result.Counts.Conflicts);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void CaseOnlyPathDifferenceUsesTheOperatingSystemsAliasComparison(int version)
    {
        using PersistedMediaFixture f = new();
        var row = AliasRow(version, "Prepared");
        string path = (string)row["full_directory"]!;
        row["safe_directory"] = path.ToUpperInvariant();
        row["safe_manifest_generation"] = new string('9', 64);
        Assert.NotEqual(path, row["safe_directory"]);
        f.Insert(true, row); // SQL's ordinal comparison does not reject this spelling.

        if (OperatingSystem.IsWindows()) Assert.Throws<InvalidDataException>(() => Read(f));
        else AssertComplete(row, Read(f)); // Distinct roots on case-sensitive platforms.
    }

    public static IEnumerable<object[]> AliasCorruptions()
    {
        foreach (string state in new[] { "Prepared", "Committed" })
        {
            foreach (int version in new[] { 1, 2 })
            foreach (string fault in new[] { "generation", "conflicts", "subset" })
                yield return [version, state, fault];
            yield return [1, state, "binding-hash"];
            foreach (string fault in new[] { "selection-hash", "inventory-hash", "member-count" })
                yield return [2, state, fault];
        }
    }

    [Theory]
    [MemberData(nameof(AliasCorruptions))]
    public void AliasedRootRejectsEveryIdentityDisagreementOrConflict(int version, string state, string fault)
    {
        var row = AliasRow(version, state);
        using (PersistedMediaFixture control = new())
        {
            control.Insert(true, row);
            AssertComplete(row, Read(control));
        }
        switch (fault)
        {
            case "generation": row["safe_manifest_generation"] = new string('9', 64); break;
            case "conflicts": row["conflicts"] = 1L; break;
            case "binding-hash": row["safe_bindings_sha256"] = new string('9', 64); break;
            case "selection-hash": row["safe_selection_sha256"] = new string('9', 64); break;
            case "inventory-hash": row["safe_inventory_sha256"] = new string('9', 64); break;
            case "member-count": row["safe_member_count"] = (long)RichSelection().Members.Length + 1; break;
            case "subset" when version == 1:
                string[] keys = JsonSerializer.Deserialize<string[]>((string)row["full_keys_json"]!)!;
                row["safe_keys_json"] = JsonSerializer.Serialize(keys.Take(1));
                row["new_bindings"] = 1L;
                break;
            case "subset":
                NativeMediaCandidateSelection full = RichSelection();
                SetSafeSelection(row, full.Select([full.Members[0].Id]), state);
                row["new_bindings"] = 0L;
                row["evidence_additions"] = 0L;
                row["derived_descriptor_additions"] = 0L;
                row["contained_derived_additions"] = 0L;
                break;
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }
        using PersistedMediaFixture f = new();
        // V1 SQL permits these semantic contradictions; no bypass is necessary.
        // For v2 the same-path checks reject them, so model corruption explicitly
        // and restore enforcement BEFORE invoking either production reader.
        if (version == 1) f.Insert(true, row);
        else
        {
            f.Execute("PRAGMA ignore_check_constraints=ON");
            try { f.Insert(true, row); }
            finally { f.Execute("PRAGMA ignore_check_constraints=OFF"); }
        }
        Assert.Equal(0, ArchiveSql.GetPragma(f.Connection, "ignore_check_constraints"));
        long changes = TotalChanges(f);
        Assert.Throws<InvalidDataException>(() => Read(f));
        Assert.Throws<InvalidDataException>(() => ArchiveSql.ReadPersistedMediaMergeOperationsPage(f.Connection, null));
        Assert.Equal(changes, TotalChanges(f));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("retained")]
    [InlineData("metadata")]
    [InlineData("derived-payload")]
    public void SameMemberIdentityDoesNotPermitDifferentSafeDescriptor(string fault)
    {
        NativeMediaCandidateSelection full = RichSelection();
        NativeMediaSelectedMember parent = full.Members.First(m => m.Id.Role == NativeMediaMemberRole.OriginalEvidence);
        NativeMediaCandidateSelection safe;
        if (fault == "derived-payload")
        {
            var preview = full.Members.Single(m => m.Id.Role == NativeMediaMemberRole.Preview);
            TransferMeasuredBlob different = Measure("changed synthetic preview");
            safe = new([parent, preview with { Descriptor = preview.Descriptor with
            { PayloadSha256 = different.Sha256, MeasuredPayload = different } }]);
        }
        else
        {
            var descriptor = fault switch
            {
                "status" => parent.Descriptor with { Status = TransferContentStatus.Unavailable },
                "retained" => parent.Descriptor with { Retained = true },
                "metadata" => parent.Descriptor with { Metadata = parent.Descriptor.Metadata with { IsDeleted = true } },
                _ => throw new ArgumentOutOfRangeException(nameof(fault))
            };
            safe = new([parent with { Descriptor = descriptor }]);
        }
        // Both documents are independently canonical and have coherent ready hashes.
        // Only their cross-root exact-descriptor relationship is wrong.
        var row = PersistedMediaFixture.Row(true, state: "Prepared", full: full, safe: full);
        SetSafeSelection(row, safe, "Prepared");
        row["new_bindings"] = 0L;
        row["evidence_additions"] = 0L;
        row["derived_descriptor_additions"] = 0L;
        row["contained_derived_additions"] = 0L;
        Assert.All(safe.Members, member => Assert.Contains(member.Id, full.Members.Select(m => m.Id)));
        using PersistedMediaFixture f = new();
        f.Insert(true, row); // No CHECK bypass: valid shape, invalid cross-root semantics.
        Assert.Throws<InvalidDataException>(() => Read(f));
    }

    [Fact]
    public void SafeCannotPromoteAContextOnlyParentToAnExplicitlySelectedOriginal()
    {
        NativeMediaCandidateSelection rich = RichSelection();
        NativeMediaSelectedMember preview = rich.Members.Single(m => m.Id.Role == NativeMediaMemberRole.Preview);
        NativeMediaCandidateSelection full = rich.Select([preview.Id]);
        Assert.Single(full.Members.Where(m => m.ContextOnly));
        var promoted = new NativeMediaCandidateSelection(full.Members.Select(m => m with { ContextOnly = false }));
        var row = PersistedMediaFixture.Row(true, state: "Prepared", full: full, safe: full);
        SetSafeSelection(row, promoted, "Prepared");
        using PersistedMediaFixture f = new();
        f.Insert(true, row);

        Assert.Throws<InvalidDataException>(() => Read(f));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void OpaqueCatalogIsAnImmutableSnapshotNotAValidatedCatalogOrLiveSqlBuffer(int version)
    {
        using PersistedMediaFixture f = new();
        var row = PersistedMediaFixture.Row(true, version, "Prepared");
        byte[] input = OpaqueCatalog.ToArray();
        row["catalog_bytes"] = input;
        f.Insert(true, row);
        var result = Read(f);
        Assert.Equal(OpaqueCatalog, result.CatalogBytes.ToArray());

        input[0] = 0;
        byte[] callerCopy = result.CatalogBytes.ToArray();
        callerCopy[1] = 1;
        f.Execute("UPDATE archive_media_merge_operations SET catalog_bytes=X'123456'");
        Assert.Equal(OpaqueCatalog, result.CatalogBytes.ToArray());
        Assert.Equal(new byte[] { 0x12, 0x34, 0x56 }, Read(f).CatalogBytes.ToArray());
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
        f.Insert(true, row); // Keep all real Schema10 CHECK constraints enabled.

        NativeMediaPersistedMergeOperation single = Read(f);
        AssertUnicodeResource(row, single, resource);
        var page = ArchiveSql.ReadPersistedMediaMergeOperationsPage(f.Connection, null);
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
        f.Insert(true, row);
        AssertUnicodeResource(row, Read(f), valid);
        AssertUnicodeResource(row,
            Assert.Single(ArchiveSql.ReadPersistedMediaMergeOperationsPage(f.Connection, null).Items), valid);

        string invalid = UnicodeResourceValue(shape);
        Assert.Equal(257, invalid.Length);
        Assert.Throws<ArgumentException>(() => ArchiveValidation.ValidateId(ResourceWithComponent(component, invalid)));
        using (var command = f.Connection.CreateCommand())
        {
            // component comes only from the three fixed theory values; never interpolate data.
            _ = ResourceWithComponent(component, invalid);
            command.CommandText = $"UPDATE archive_media_merge_operations SET {component}=$value WHERE operation_id=$op";
            command.Parameters.AddWithValue("$value", invalid);
            command.Parameters.AddWithValue("$op", PersistedMediaFixture.OperationId.ToString("D"));
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        // Deliberately corrupt only SQL identity after a coherent positive control.
        // For <=768 bytes require the identity validator's inner exception, so neither
        // a resource/selection mismatch nor a codec failure can falsely satisfy us.
        InvalidDataException single = Assert.Throws<InvalidDataException>(() => Read(f));
        InvalidDataException page = Assert.Throws<InvalidDataException>(() =>
            ArchiveSql.ReadPersistedMediaMergeOperationsPage(f.Connection, null));
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
        var row = PersistedMediaFixture.Row(true, version, state);
        row["source"] = resource.Source;
        row["board"] = resource.Board;
        row["thread"] = resource.Thread;
        NativeMediaCandidateSelection selection = RichSelection(resource);
        // Derive actual revision keys from this resource/provider, not format-only hashes.
        string[] keys = selection.Members.Where(member => member.Id.Role == NativeMediaMemberRole.OriginalPayload)
            .Select(member => member.Id.Occurrence.OriginalRevisionKey!).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        if (version == 1)
        {
            row["full_keys_json"] = JsonSerializer.Serialize(keys);
            row["safe_keys_json"] = JsonSerializer.Serialize(keys);
            row["new_bindings"] = (long)keys.Length;
            if (state != "Preparing")
            {
                row["safe_directory"] = (string)row["operations_parent"]! + "/safe";
                row["safe_manifest_generation"] = row["full_manifest_generation"];
                row["safe_bindings_sha256"] = row["full_bindings_sha256"];
            }
            return row;
        }
        NativeMediaCandidateInventory inventory = selection.CreateInventory(PersistedMediaFixture.OperationId,
            PersistedMediaFixture.Fingerprint, PersistedMediaFixture.ImportedAt);
        foreach (string prefix in new[] { "full", "safe" })
        {
            row[prefix + "_selection_json"] = selection.CanonicalJson;
            row[prefix + "_selection_sha256"] = selection.Sha256;
            row[prefix + "_member_count"] = (long)selection.Members.Length;
            row[prefix + "_inventory_sha256"] = state == "Preparing" ? null : inventory.Sha256;
        }
        if (state != "Preparing")
        {
            row["safe_directory"] = (string)row["operations_parent"]! + "/safe";
            row["safe_manifest_generation"] = row["full_manifest_generation"];
        }
        row["evidence_additions"] = (long)inventory.OriginalEvidence.Length;
        row["derived_descriptor_additions"] = (long)inventory.DerivedRepresentations.Length;
        row["contained_derived_additions"] = (long)inventory.DerivedRepresentations.Count(
            derived => derived.IncomingStatus == MediaDerivedStatus.Contained);
        row["new_bindings"] = (long)inventory.Bindings.Length;
        return row;
    }

    private static void AssertUnicodeResource(Dictionary<string, object?> row,
        NativeMediaPersistedMergeOperation actual, ResourceId resource)
    {
        AssertComplete(row, actual);
        Assert.Equal(resource, actual.Identity.Resource);
        if (actual.Identity.PlanVersion != 2) return;
        foreach (NativeMediaPersistedRoot root in new[] { actual.Full, actual.Safe })
        {
            Assert.All(root.Selection!.Members, member => Assert.Equal(resource, member.Id.Occurrence.Resource));
            if (actual.State != NativeMediaImportOperationState.Preparing)
                Assert.Equal(root.Selection.CreateInventory(actual.Identity.OperationId,
                    actual.Identity.PackageFingerprint, actual.Identity.ImportedAtUtc).Sha256, root.InventorySha256);
        }
    }

    private static Dictionary<string, object?> AliasRow(int version, string state)
    {
        NativeMediaCandidateSelection full = RichSelection();
        var row = version == 1 ? PersistedMediaFixture.Row(true, version, state)
            : PersistedMediaFixture.Row(true, version, state, full: full, safe: full);
        if (version == 1) SetLegacySafe(row, state, alias: true);
        row["safe_directory"] = row["full_directory"];
        row["safe_manifest_generation"] = row["full_manifest_generation"];
        row["conflicts"] = 0L;
        return row;
    }

    private static void SetLegacySafe(Dictionary<string, object?> row, string state, bool alias)
    {
        string[] keys = JsonSerializer.Deserialize<string[]>((string)row["full_keys_json"]!)!;
        row["full_keys_json"] = "[\n  " + string.Join(",\n  ", keys.Select(key => JsonSerializer.Serialize(key))) + "\n]";
        row["safe_keys_json"] = JsonSerializer.Serialize(alias ? keys : keys.Take(1).ToArray());
        row["new_bindings"] = alias ? (long)keys.Length : 1L;
        if (state != "Preparing")
        {
            row["safe_directory"] = (string)row["operations_parent"]! + "/safe";
            row["safe_manifest_generation"] = row["full_manifest_generation"];
            row["safe_bindings_sha256"] = row["full_bindings_sha256"];
        }
    }

    private static void SetSafeSelection(Dictionary<string, object?> row, NativeMediaCandidateSelection safe, string state)
    {
        row["safe_selection_json"] = safe.CanonicalJson;
        row["safe_selection_sha256"] = safe.Sha256;
        row["safe_member_count"] = (long)safe.Members.Length;
        row["safe_inventory_sha256"] = state == "Preparing" ? null : safe.CreateInventory(
            Guid.Parse((string)row["operation_id"]!), (string)row["package_fingerprint"]!,
            DateTimeOffset.Parse((string)row["imported_at"]!, CultureInfo.InvariantCulture)).Sha256;
    }

    private static NativeMediaPersistedMergeOperation Read(PersistedMediaFixture f)
        => Assert.IsType<NativeMediaPersistedMergeOperation>(ArchiveSql.ReadPersistedMediaMergeOperation(
            f.Connection, null, PersistedMediaFixture.OperationId, PersistedMediaFixture.Request));

    private static void AssertComplete(Dictionary<string, object?> row, NativeMediaPersistedMergeOperation actual)
    {
        Assert.Equal(new NativeMediaPersistedIdentity(Count(row, "plan_version"), Guid.Parse((string)row["operation_id"]!),
            (string)row["request_sha256"]!, (string)row["package_fingerprint"]!, (string)row["catalog_instance"]!,
            Convert.ToInt64(row["catalog_generation"], CultureInfo.InvariantCulture),
            new ResourceId((string)row["source"]!, (string)row["board"]!, (string)row["thread"]!),
            (string)row["operations_parent"]!, DateTimeOffset.Parse((string)row["imported_at"]!, CultureInfo.InvariantCulture)), actual.Identity);
        Assert.Equal(Enum.Parse<NativeMediaImportOperationState>((string)row["state"]!), actual.State);
        Assert.Equal(row["roots_json"], actual.RootsJson);
        Assert.Equal(JsonSerializer.Deserialize<NativeMediaMergeRoot[]>((string)row["roots_json"]!)!, actual.Roots.ToArray());
        AssertRoot(row, "full", actual.Full);
        AssertRoot(row, "safe", actual.Safe);
        Assert.Equal((byte[])row["catalog_bytes"]!, actual.CatalogBytes.ToArray());
        Assert.Equal(new NativeMediaMergeCounts(Count(row, "new_posts"), Count(row, "updated_posts"),
            Count(row, "new_attachments"), Count(row, "new_bindings"), Count(row, "conflicts"),
            Count(row, "evidence_additions"), Count(row, "derived_descriptor_additions"),
            Count(row, "contained_derived_additions")), actual.Counts);
    }

    private static void AssertRoot(Dictionary<string, object?> row, string prefix, NativeMediaPersistedRoot actual)
    {
        Assert.Equal(row[prefix + "_keys_json"], actual.LegacyKeysJson);
        Assert.Equal(JsonSerializer.Deserialize<string[]>((string)row[prefix + "_keys_json"]!)!, actual.LegacyKeys.ToArray());
        Assert.Equal(row[prefix + "_bindings_sha256"], actual.LegacyBindingsSha256);
        Assert.Equal(row[prefix + "_selection_json"], actual.Selection?.CanonicalJson);
        Assert.Equal(row[prefix + "_selection_sha256"], actual.SelectionSha256);
        Assert.Equal(Count(row, prefix + "_member_count"), actual.MemberCount);
        Assert.Equal(row[prefix + "_inventory_sha256"], actual.InventorySha256);
        Assert.Equal(row[prefix + "_directory"], actual.Directory);
        Assert.Equal(row[prefix + "_manifest_generation"], actual.ManifestGeneration);
        if (row[prefix + "_selection_json"] is string json)
            Assert.Equal(NativeMediaCandidateSelection.Parse(json).Members.ToArray(), actual.Selection!.Members.ToArray());
        else Assert.Null(actual.Selection);
    }

    private static int Count(Dictionary<string, object?> row, string column) => Convert.ToInt32(row[column], CultureInfo.InvariantCulture);
    private static long TotalChanges(PersistedMediaFixture f)
    {
        using SqliteCommand command = f.Connection.CreateCommand();
        command.CommandText = "SELECT total_changes()";
        return Assert.IsType<long>(command.ExecuteScalar());
    }

    private static NativeMediaCandidateSelection RichSelection(ResourceId? resource = null)
    {
        resource ??= PersistedMediaFixture.Resource;
        TransferMeasuredBlob source = Measure("synthetic persisted original");
        TransferMeasuredBlob preview = Measure("synthetic persisted preview");
        var provider = new MediaProviderMetadata("media", "original", "https://i.4cdn.org/b/123.webm", "media.webm",
            source.Length, source.Md5, source.Sha256);
        string key = MediaRevisionKeys.CreateAttachmentKey(resource, "10", 0, provider);
        var metadata = new CapturedAttachment(0, provider.MediaId, "original", provider.Url, provider.OriginalFileName,
            ExpectedSize: source.Length, ExpectedMd5: source.Md5)
        { ExpectedSha256 = source.Sha256, AttachmentRevisionKey = key };
        var deleted = PersistedMediaFixture.Evidence("11");
        deleted = deleted with { Descriptor = deleted.Descriptor with
            { Id = deleted.Id with { Occurrence = deleted.Id.Occurrence with { Resource = resource } } } };
        var id = new NativeMediaMemberId(new(resource, "10", 0, key),
            new(source.Length, source.Sha256, source.Md5, null), NativeMediaMemberRole.OriginalEvidence);
        var parent = new NativeMediaSelectedMember(new(id, metadata, false, TransferContentStatus.Contained, null, null, null), false);
        return new([parent,
            new(new(id with { Role = NativeMediaMemberRole.OriginalPayload }, metadata, false,
                TransferContentStatus.Contained, source.Sha256, null, source), false),
            new(new(id with { Role = NativeMediaMemberRole.Preview }, metadata, false,
                TransferContentStatus.Contained, preview.Sha256, null, preview), false),
            new(new(id with { Role = NativeMediaMemberRole.VideoPosterPng }, metadata, false,
                TransferContentStatus.Omitted, null, null, null), false),
            deleted]);
    }

    private static TransferMeasuredBlob Measure(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        return new(bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            Convert.ToBase64String(MD5.HashData(bytes)), Convert.ToBase64String(SHA1.HashData(bytes)));
    }
}

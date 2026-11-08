using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// W32b selection-only contract tests. Targets the internal NativeMediaCandidateSelection,
// NativeMediaMemberId/Descriptor/SelectedMember API present in this checkout. No reflection,
// replacement implementation, disk fixtures, materializer, network or decoder is involved.
// Compile assumption: these internal APIs remain accessible via the existing friend assembly.
public sealed class NativeMediaMemberSelectionTests
{
    private static readonly ResourceId Resource = new("4chan", "b", "7001");
    private static readonly byte[] SourceBytes = Encoding.UTF8.GetBytes("synthetic W32b original");
    private static readonly TransferMeasuredBlob Source = Measure(SourceBytes);
    private static readonly TransferMeasuredBlob Preview = Measure(Encoding.UTF8.GetBytes("synthetic W32b preview"));

    [Fact]
    public void Same_occurrence_and_bytes_keep_all_four_semantic_roles_distinct()
    {
        NativeMediaSelectedMember evidence = Evidence(status: TransferContentStatus.Contained);
        NativeMediaSelectedMember original = Child(evidence, NativeMediaMemberRole.OriginalPayload, Source);
        NativeMediaSelectedMember preview = Child(evidence, NativeMediaMemberRole.Preview, Source);
        // Only a passive descriptor; this test does not claim the bytes are a decoded PNG.
        NativeMediaSelectedMember poster = Child(evidence, NativeMediaMemberRole.VideoPosterPng, Source);
        var selection = new NativeMediaCandidateSelection([poster, original, evidence, preview]);

        Assert.Equal(4, selection.Members.Length);
        Assert.Equal(4, selection.Members.Select(m => m.Id).Distinct().Count());
        Assert.Equal(new[] { NativeMediaMemberRole.OriginalEvidence, NativeMediaMemberRole.OriginalPayload,
                NativeMediaMemberRole.Preview, NativeMediaMemberRole.VideoPosterPng },
            selection.Members.Select(m => m.Id.Role).ToArray());
        Assert.Equal(1, selection.Members.Select(m => m.Id.Occurrence).Distinct().Count());
        AssertRoundTrip(selection);
    }

    [Fact]
    public void Identical_bytes_in_distinct_occurrences_are_not_coalesced()
    {
        NativeMediaSelectedMember first = Evidence(post: "10");
        NativeMediaSelectedMember second = Evidence(post: "11");
        NativeMediaSelectedMember third = Evidence(post: "10", position: 1);
        var selection = new NativeMediaCandidateSelection([third, second, first]);

        Assert.Equal(3, selection.Members.Length);
        Assert.Equal(3, selection.Members.Select(m => m.Id).Distinct().Count());
        Assert.All(selection.Members, m => Assert.Equal(first.Id.SourceVersion, m.Id.SourceVersion));
        Assert.Equal(new[] { first.Id, third.Id, second.Id }, selection.Members.Select(m => m.Id).ToArray());
    }

    [Fact]
    public void Nullable_sha1_is_identity_not_a_wildcard_or_an_enrichment_request()
    {
        NativeMediaSelectedMember unknown = Evidence(sha1: false);
        NativeMediaSelectedMember known = Evidence(sha1: true);
        var full = new NativeMediaCandidateSelection([unknown]);
        var enriched = new NativeMediaCandidateSelection([known]);

        Assert.Equal(unknown.Id.Occurrence, known.Id.Occurrence);
        Assert.NotEqual(unknown.Id, known.Id);
        Assert.NotEqual(full.CanonicalJson, enriched.CanonicalJson);
        Assert.NotEqual(full.Sha256, enriched.Sha256);
        Assert.Null(NativeMediaCandidateSelection.Parse(full.CanonicalJson).Members[0].Id.SourceVersion!.Sha1);
        Assert.Equal(Source.Sha1, NativeMediaCandidateSelection.Parse(enriched.CanonicalJson).Members[0].Id.SourceVersion!.Sha1);
        Assert.Throws<InvalidDataException>(() => full.Select([known.Id]));
        Assert.Throws<InvalidDataException>(() => full.ValidateSubset(enriched));
        Assert.Throws<InvalidDataException>(() => enriched.ValidateSubset(full));
        // Distinct IDs cannot hide two competing parent versions for one occurrence.
        Assert.Throws<InvalidDataException>(() => new NativeMediaCandidateSelection([unknown, known]));
    }

    [Fact]
    public void Derived_parent_must_match_nullable_sha1_exactly()
    {
        NativeMediaSelectedMember parent = Evidence(sha1: false);
        NativeMediaSelectedMember child = Child(parent, NativeMediaMemberRole.Preview, Preview);
        NativeMediaSelectedMember enrichedChild = child with
        {
            Descriptor = child.Descriptor with
            {
                Id = child.Id with { SourceVersion = child.Id.SourceVersion! with { Sha1 = Source.Sha1 } }
            }
        };

        AssertRoundTrip(new NativeMediaCandidateSelection([parent, child]));
        Assert.Throws<InvalidDataException>(() => new NativeMediaCandidateSelection([parent, enrichedChild]));
        Assert.Throws<InvalidDataException>(() => new NativeMediaCandidateSelection([child]));
    }

    [Fact]
    public void Null_source_version_remains_distinct_from_a_known_byte_version()
    {
        NativeMediaSelectedMember known = Evidence();
        NativeMediaSelectedMember unknown = known with
        {
            Descriptor = known.Descriptor with { Id = known.Id with { SourceVersion = null } }
        };
        var selection = new NativeMediaCandidateSelection([unknown]);
        Assert.Null(NativeMediaCandidateSelection.Parse(selection.CanonicalJson).Members[0].Id.SourceVersion);
        Assert.NotEqual(new NativeMediaCandidateSelection([known]).Sha256, selection.Sha256);
        Assert.Throws<InvalidDataException>(() => selection.Select([known.Id]));
        Assert.Throws<InvalidDataException>(() => new NativeMediaCandidateSelection(
            [unknown, Child(unknown, NativeMediaMemberRole.Preview, Preview)]));
    }

    [Fact]
    public void Canonical_json_and_digest_are_independent_of_input_order()
    {
        NativeMediaSelectedMember parent = Evidence(post: "2");
        NativeMediaSelectedMember child = Child(parent, NativeMediaMemberRole.Preview, Preview);
        NativeMediaSelectedMember other = Evidence(post: "10", position: 2);
        NativeMediaSelectedMember[] members = [parent, child, other];
        var expected = new NativeMediaCandidateSelection(members);
        int[][] orders = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];
        foreach (int[] order in orders)
        {
            var actual = NativeMediaCandidateSelection.Create(order.Select(i => members[i]));
            Assert.Equal(expected.CanonicalJson, actual.CanonicalJson);
            Assert.Equal(expected.Sha256, actual.Sha256);
            AssertRoundTrip(actual);
        }
        // Post IDs use ordinal string ordering, not a numeric or culture-dependent comparison.
        Assert.Equal(new[] { other.Id, parent.Id, child.Id }, expected.Members.Select(m => m.Id).ToArray());
    }

    [Fact]
    public void Empty_selection_has_a_fixed_wire_vector_and_hash_of_exact_utf8_bytes()
    {
        var selection = new NativeMediaCandidateSelection([]);
        Assert.True(selection.IsEmpty);
        Assert.Equal("{\"version\":2,\"members\":[]}", selection.CanonicalJson);
        Assert.Equal(Hash(Encoding.UTF8.GetBytes("{\"version\":2,\"members\":[]}")), selection.Sha256);
        AssertRoundTrip(selection);
    }

    [Fact]
    public void Unkeyed_evidence_has_an_independent_nonempty_canonical_wire_vector()
    {
        const string expected = """
            {"version":2,"members":[{"descriptor":{"id":{"occurrence":{"resource":{"source":"4chan","board":"b","thread":"7001"},"postId":"10","position":0,"originalRevisionKey":null},"sourceVersion":null,"role":0},"metadata":{"position":0,"mediaId":null,"variant":"original","url":null,"originalFileName":null,"plannedPath":null,"expectedSize":null,"expectedMd5":null,"isDeleted":true,"expectedSha256":null,"expectedSha1":null,"providerSizeHint":null,"attachmentRevisionKey":null},"retained":false,"status":2,"payloadSha256":null,"history":null,"measuredPayload":null},"contextOnly":false}]}
            """;
        var selection = new NativeMediaCandidateSelection([UnkeyedEvidence()]);
        Assert.Equal(expected, selection.CanonicalJson);
        Assert.Equal(Hash(Encoding.UTF8.GetBytes(expected)), selection.Sha256);
        AssertRoundTrip(selection);
    }

    [Fact]
    public void Derived_only_subset_adds_exact_context_parent_but_not_original_payload()
    {
        NativeMediaSelectedMember parent = Evidence(status: TransferContentStatus.Contained);
        NativeMediaSelectedMember original = Child(parent, NativeMediaMemberRole.OriginalPayload, Source);
        NativeMediaSelectedMember preview = Child(parent, NativeMediaMemberRole.Preview, Preview);
        var full = new NativeMediaCandidateSelection([parent, original, preview]);
        NativeMediaCandidateSelection safe = full.Select([preview.Id]);

        Assert.Equal(2, safe.Members.Length);
        Assert.Equal(parent with { ContextOnly = true }, safe.Members.Single(m => m.Id.Role == NativeMediaMemberRole.OriginalEvidence));
        Assert.Equal(preview, safe.Members.Single(m => m.Id.Role == NativeMediaMemberRole.Preview));
        Assert.DoesNotContain(safe.Members, m => m.Id.Role == NativeMediaMemberRole.OriginalPayload);
        full.ValidateSubset(safe);
        Assert.True(safe.IsSubsetOf(full));
        Assert.False(full.IsSubsetOf(safe));
        AssertRoundTrip(safe);
        Assert.True(full.Select([]).IsEmpty);
    }

    [Fact]
    public void Context_only_parent_cannot_be_promoted_or_selected_as_new_evidence()
    {
        NativeMediaSelectedMember parent = Evidence();
        NativeMediaSelectedMember child = Child(parent, NativeMediaMemberRole.Preview, Preview);
        var full = new NativeMediaCandidateSelection([parent, child]);
        var contextSelection = full.Select([child.Id]);
        Assert.Throws<InvalidDataException>(() => contextSelection.Select([parent.Id]));
        Assert.False(full.IsSubsetOf(contextSelection));
        Assert.Throws<InvalidDataException>(() => new NativeMediaCandidateSelection([parent with { ContextOnly = true }]));
        Assert.Throws<InvalidDataException>(() => new NativeMediaCandidateSelection([parent, child with { ContextOnly = true }]));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("retained")]
    [InlineData("metadata")]
    public void Subset_rejects_changed_descriptor_even_with_the_same_member_id(string change)
    {
        NativeMediaSelectedMember original = Evidence();
        NativeMediaMemberDescriptor descriptor = change switch
        {
            "status" => original.Descriptor with { Status = TransferContentStatus.Unavailable },
            "retained" => original.Descriptor with { Retained = true },
            "metadata" => original.Descriptor with { Metadata = original.Descriptor.Metadata with { IsDeleted = true } },
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        var full = new NativeMediaCandidateSelection([original]);
        var changed = new NativeMediaCandidateSelection([original with { Descriptor = descriptor }]);
        Assert.Equal(full.Members[0].Id, changed.Members[0].Id);
        Assert.NotEqual(full.Sha256, changed.Sha256);
        Assert.False(changed.IsSubsetOf(full));
        Assert.Throws<InvalidDataException>(() => full.ValidateSubset(changed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Subset_rejects_changed_derived_payload_or_measurement(bool measurementOnly)
    {
        NativeMediaSelectedMember parent = Evidence();
        NativeMediaSelectedMember preview = Child(parent, NativeMediaMemberRole.Preview, Preview);
        TransferMeasuredBlob changedBytes = measurementOnly
            ? Preview with { Sha1 = Source.Sha1 }
            : Measure(Encoding.UTF8.GetBytes("different preview"));
        var full = new NativeMediaCandidateSelection([parent, preview]);
        var changed = new NativeMediaCandidateSelection([parent, Child(parent, NativeMediaMemberRole.Preview, changedBytes)]);
        Assert.Equal(full.Members[1].Id, changed.Members[1].Id);
        Assert.NotEqual(full.Sha256, changed.Sha256);
        Assert.Throws<InvalidDataException>(() => full.ValidateSubset(changed));
    }

    [Fact]
    public void Duplicate_and_unknown_member_ids_are_rejected_not_deduplicated()
    {
        NativeMediaSelectedMember parent = Evidence();
        var full = new NativeMediaCandidateSelection([parent]);
        Assert.Throws<InvalidDataException>(() => new NativeMediaCandidateSelection([parent, parent]));
        Assert.Throws<InvalidDataException>(() => new NativeMediaCandidateSelection(
            [parent, parent with { Descriptor = parent.Descriptor with { Status = TransferContentStatus.Unavailable } }]));
        Assert.Throws<InvalidDataException>(() => full.Select([parent.Id, parent.Id]));
        Assert.Throws<InvalidDataException>(() => full.Select([Evidence(post: "999").Id]));
        Assert.Throws<InvalidDataException>(() => full.Select([parent.Id with { Role = NativeMediaMemberRole.Preview }]));
        Assert.Throws<InvalidDataException>(() => full.Select([null!]));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public void Unknown_roles_are_rejected_by_constructor_and_parser(int role)
    {
        NativeMediaSelectedMember parent = Evidence();
        Assert.Throws<InvalidDataException>(() => new NativeMediaCandidateSelection(
            [parent with { Descriptor = parent.Descriptor with { Id = parent.Id with { Role = (NativeMediaMemberRole)role } } }]));
        JsonObject wire = Wire(new NativeMediaCandidateSelection([parent]));
        wire["members"]![0]!["descriptor"]!["id"]!["role"] = role;
        AssertRejected(wire.ToJsonString());
    }

    [Theory]
    [InlineData("root")]
    [InlineData("member")]
    [InlineData("descriptor")]
    [InlineData("id")]
    [InlineData("occurrence")]
    [InlineData("resource")]
    [InlineData("sourceVersion")]
    [InlineData("metadata")]
    [InlineData("measuredPayload")]
    public void Parser_rejects_unknown_fields_at_every_selection_layer(string layer)
    {
        NativeMediaSelectedMember parent = Evidence();
        var full = new NativeMediaCandidateSelection([parent, Child(parent, NativeMediaMemberRole.Preview, Preview)]);
        JsonObject wire = Wire(full);
        JsonObject member = wire["members"]![1]!.AsObject();
        JsonObject descriptor = member["descriptor"]!.AsObject();
        JsonObject id = descriptor["id"]!.AsObject();
        JsonObject target = layer switch
        {
            "root" => wire, "member" => member, "descriptor" => descriptor, "id" => id,
            "occurrence" => id["occurrence"]!.AsObject(),
            "resource" => id["occurrence"]!["resource"]!.AsObject(),
            "sourceVersion" => id["sourceVersion"]!.AsObject(),
            "metadata" => descriptor["metadata"]!.AsObject(),
            "measuredPayload" => descriptor["measuredPayload"]!.AsObject(),
            _ => throw new ArgumentOutOfRangeException(nameof(layer))
        };
        target["unknown"] = 1;
        AssertRejected(wire.ToJsonString());
    }

    [Fact]
    public void Parser_rejects_missing_nullable_sha1_and_empty_sha1_instead_of_coercing_to_null()
    {
        var full = new NativeMediaCandidateSelection([Evidence()]);
        JsonObject missing = Wire(full);
        missing["members"]![0]!["descriptor"]!["id"]!["sourceVersion"]!.AsObject().Remove("sha1");
        AssertRejected(missing.ToJsonString());
        JsonObject empty = Wire(full);
        empty["members"]![0]!["descriptor"]!["id"]!["sourceVersion"]!["sha1"] = "";
        AssertRejected(empty.ToJsonString());
    }

    [Fact]
    public void Parser_rejects_duplicate_members_and_duplicate_nested_properties()
    {
        var full = new NativeMediaCandidateSelection([Evidence()]);
        JsonObject wire = Wire(full);
        wire["members"]!.AsArray().Add(wire["members"]![0]!.DeepClone());
        AssertRejected(wire.ToJsonString());
        AssertRejected(full.CanonicalJson.Replace("\"version\":2", "\"version\":2,\"version\":2", StringComparison.Ordinal));
        AssertRejected(full.CanonicalJson.Replace("\"role\":0", "\"role\":0,\"role\":0", StringComparison.Ordinal));
        AssertRejected(full.CanonicalJson.Replace("\"sha1\":null", "\"sha1\":null,\"sha1\":null", StringComparison.Ordinal));
    }

    [Fact]
    public void Parser_rejects_noncanonical_member_order_instead_of_normalizing_it()
    {
        var full = new NativeMediaCandidateSelection([Evidence(post: "10"), Evidence(post: "11")]);
        JsonObject wire = Wire(full);
        JsonArray members = wire["members"]!.AsArray();
        JsonNode first = members[0]!.DeepClone();
        members[0] = members[1]!.DeepClone();
        members[1] = first;
        AssertRejected(wire.ToJsonString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"version\":1,\"members\":[]}")]
    [InlineData("{\"version\":3,\"members\":[]}")]
    [InlineData("{\"version\":2,\"members\":null}")]
    [InlineData("{\"members\":[],\"version\":2}")]
    [InlineData("{\"version\":2.0,\"members\":[]}")]
    [InlineData("{\"version\":2e0,\"members\":[]}")]
    [InlineData("{\"version\":2,\"members\":[],}")]
    [InlineData(" {\"version\":2,\"members\":[]}")]
    [InlineData("{\"version\":2,\"members\":[]}\n")]
    [InlineData("{\"version\":2,\"members\":[]}garbage")]
    [InlineData("{\"version\":2,\"members\":[]}{}")]
    [InlineData("{\"version\":2,/*comment*/\"members\":[]}")]
    public void Parser_rejects_malformed_or_alternate_wire_spellings(string json) => AssertRejected(json);

    [Fact]
    public void Parser_preserves_identifier_boundary_and_rejects_one_character_over()
    {
        var valid = new NativeMediaCandidateSelection([UnkeyedEvidence(new string('p', 256))]);
        AssertRoundTrip(valid);
        JsonObject wire = Wire(valid);
        wire["members"]![0]!["descriptor"]!["id"]!["occurrence"]!["postId"] = new string('p', 257);
        AssertRejected(wire.ToJsonString());
    }

    [Fact]
    public void Parser_rejects_input_over_utf8_byte_budget_even_when_characters_fit()
    {
        const string prefix = "{\"version\":2,\"members\":[],\"unknown\":\"";
        string json = prefix + new string('\u00e9', NativeMediaCandidateSelection.MaximumJsonBytes / 2) + "\"}";
        Assert.True(json.Length < NativeMediaCandidateSelection.MaximumJsonBytes);
        Assert.True(Encoding.UTF8.GetByteCount(json) > NativeMediaCandidateSelection.MaximumJsonBytes);
        AssertRejected(json);
        AssertRejected(new string(' ', NativeMediaCandidateSelection.MaximumJsonBytes + 1));
    }

    [Fact]
    public void Parser_rejects_over_budget_member_array_and_excessive_depth()
    {
        string json = "{\"version\":2,\"members\":[" +
            string.Join(",", Enumerable.Repeat("null", NativeMediaCandidateSelection.MaximumMembers + 1)) + "]}";
        Assert.True(Encoding.UTF8.GetByteCount(json) < NativeMediaCandidateSelection.MaximumJsonBytes);
        AssertRejected(json);
        AssertRejected("{\"version\":2,\"members\":" + new string('[', 32) + "0" + new string(']', 32) + "}");
    }

    [Theory]
    [InlineData("\\uD800")]
    [InlineData("\\uDC00")]
    [InlineData("\\uD800x")]
    public void Parser_rejects_escaped_lone_surrogates(string escape)
    {
        string json = new NativeMediaCandidateSelection([UnkeyedEvidence()]).CanonicalJson;
        AssertRejected(json.Replace("\"postId\":\"10\"", "\"postId\":\"" + escape + "\"", StringComparison.Ordinal));
    }

    private static NativeMediaSelectedMember Evidence(string post = "10", int position = 0,
        bool sha1 = false, TransferContentStatus status = TransferContentStatus.Omitted)
    {
        var provider = new MediaProviderMetadata("123", "original", "https://i.4cdn.org/b/123.webm", "123.webm",
            Source.Length, Source.Md5, Source.Sha256);
        string key = MediaRevisionKeys.CreateAttachmentKey(Resource, post, position, provider);
        var metadata = new CapturedAttachment(position, provider.MediaId, "original", provider.Url, provider.OriginalFileName,
            ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
        { ExpectedSha256 = provider.ExpectedSha256, AttachmentRevisionKey = key };
        var version = new TransferOriginalByteVersion(Source.Length, Source.Sha256, Source.Md5, sha1 ? Source.Sha1 : null);
        var id = new NativeMediaMemberId(new(Resource, post, position, key), version, NativeMediaMemberRole.OriginalEvidence);
        return new(new(id, metadata, false, status, null, null, null), false);
    }

    private static NativeMediaSelectedMember UnkeyedEvidence(string post = "10") => new(new(
        new(new(Resource, post, 0, null), null, NativeMediaMemberRole.OriginalEvidence),
        new CapturedAttachment(0, null, IsDeleted: true), false, TransferContentStatus.Unknown, null, null, null), false);

    private static NativeMediaSelectedMember Child(NativeMediaSelectedMember parent, NativeMediaMemberRole role,
        TransferMeasuredBlob bytes) => new(new(parent.Id with { Role = role }, parent.Descriptor.Metadata,
        parent.Descriptor.Retained, TransferContentStatus.Contained, bytes.Sha256, null, bytes), false);

    private static TransferMeasuredBlob Measure(byte[] bytes) => new(bytes.Length, Hash(bytes),
        Convert.ToBase64String(MD5.HashData(bytes)), Convert.ToBase64String(SHA1.HashData(bytes)));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static JsonObject Wire(NativeMediaCandidateSelection selection) => JsonNode.Parse(selection.CanonicalJson)!.AsObject();
    private static void AssertRejected(string json) => Assert.Throws<InvalidDataException>(() => NativeMediaCandidateSelection.Parse(json));

    private static void AssertRoundTrip(NativeMediaCandidateSelection selection)
    {
        NativeMediaCandidateSelection parsed = NativeMediaCandidateSelection.Parse(selection.CanonicalJson);
        Assert.Equal(selection.CanonicalJson, parsed.CanonicalJson);
        Assert.Equal(selection.Sha256, parsed.Sha256);
        Assert.Equal(Hash(Encoding.UTF8.GetBytes(selection.CanonicalJson)), selection.Sha256);
        Assert.Equal(selection.Members.ToArray(), parsed.Members.ToArray());
    }
}

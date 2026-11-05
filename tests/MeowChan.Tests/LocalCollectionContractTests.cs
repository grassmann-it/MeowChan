using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Storage;

namespace MeowChan.Tests;

// Synthetic in-memory contract cases only. No catalog/profile/SQL/FS/Host fixtures.
// Added under the L0 lease; not executed as part of that lease.
public sealed class LocalCollectionContractTests
{
    private const string Instance = "12345678-90ab-cdef-1234-567890abcdef";
    private static readonly Guid Operation = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly LocalCollectionId Collection = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly LocalImportCodec Codec = new();
    private static readonly LocalImportLimits Limits = new(1024 * 1024, LocalImportBounds.MaximumOperationMetadataBytes);
    private static readonly string Hash = new('a', 64);

    private static LocalImportSelectionDescriptor Selection(int count = 1, LocalSourceClaims? claims = null)
        => new(1, Instance, Operation, Collection, "synthetic collection",
            Enumerable.Range(1, count).Select(i => new LocalSelectedItem(
                new(Collection, new(Guid.Parse($"33333333-3333-3333-3333-{i:x12}")),
                    new(Guid.Parse($"44444444-4444-4444-4444-{i:x12}"))),
                i - 1, "input-" + i.ToString(CultureInfo.InvariantCulture), null, claims)).ToImmutableArray());
    private static LocalImportContent Content(LocalImportSelectionDescriptor selection, long length = 0)
        => new(1, selection.Items.Select(x => new LocalImportContentItem(x.Reference, x.Ordinal,
            LocalMediaRole.Original, length, Hash, LocalImportCodec.PayloadRelativeSlot(selection.OperationId, x.Reference.RevisionId))).ToImmutableArray());
    private static LocalSourceClaims Claims(LocalFieldPresence presence = LocalFieldPresence.Absent)
        => new(1, "synthetic-profile", 1, null, null, presence, [], []);
    private static LocalCollectionImportReceipt Receipt(LocalImportSelectionDescriptor selection, LocalImportContent content)
        => new(1, selection.CatalogInstance, selection.OperationId, Codec.ComputeRequestSha256(selection, content),
            selection.CollectionId, 1, new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero),
            content.Items.Select(x => new LocalRegisteredRevision(x.Reference, x.Ordinal, x.Role, x.Length, x.Sha256)).ToImmutableArray());
    private static LocalImportReceiptKey Key(LocalCollectionImportReceipt receipt)
        => new(receipt.CatalogInstance, receipt.OperationId, receipt.RequestSha256);
    private static LocalImportSelectionDescriptor First(LocalImportSelectionDescriptor value, Func<LocalSelectedItem, LocalSelectedItem> change)
        => value with { Items = value.Items.SetItem(0, change(value.Items[0])) };
    private static void Reject(Action action, LocalImportError error = LocalImportError.InvalidSelection)
    {
        var exception = Assert.Throws<LocalImportCodecException>(action);
        Assert.Equal(error, exception.Error);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void LocalIdsAreDistinctTypesAndDefaultIsRejectedAtCodecBoundary()
    {
        Assert.Throws<ArgumentException>(() => new LocalCollectionId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new LocalItemId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new LocalRevisionId(Guid.Empty));
        Assert.Equal(Collection.Value.ToString("D"), Collection.ToString());
        var selection = Selection();
        Reject(() => Codec.EncodeSelection(selection with { CollectionId = default }));
        Reject(() => Codec.EncodeSelection(First(selection, x => x with { Reference = default })));
        Reject(() => Codec.EncodeSelection(selection with { OperationId = Guid.Empty }));
        LocalImportCodec.ValidateRecoveryKey(new(Instance, Operation));
        Reject(() => LocalImportCodec.ValidateRecoveryKey(default));
        Reject(() => LocalImportCodec.ValidateReceiptKey(default));
    }

    [Fact]
    public void OriginalOnlyAndNumericEnumValuesAreFixed()
    {
        Assert.Equal(1, (int)LocalMediaRole.Original);
        Assert.Equal(new[] { 1, 2, 3, 4 }, Enum.GetValues<LocalClaimOmissionReason>().Select(x => (int)x));
        var content = Content(Selection());
        foreach (int unsupported in new[] { 0, 2, -1, int.MaxValue })
            Reject(() => Codec.EncodeContent(content with { Items = [content.Items[0] with { Role = (LocalMediaRole)unsupported }] }),
                LocalImportError.UnsupportedRole);
    }

    [Fact]
    public void EqualBytesKeepIndependentFullReferencesAndOrderedItems()
    {
        var selection = Selection(2);
        var content = Content(selection);
        Assert.Equal(content.Items[0].Sha256, content.Items[1].Sha256);
        Assert.NotEqual(content.Items[0].Reference, content.Items[1].Reference);
        Assert.Equal(new[] { 0, 1 }, Codec.DecodeContent(Codec.EncodeContent(content)).Items.Select(x => x.Ordinal));
        var copy = selection.Items.ToArray();
        var immutable = copy.ToImmutableArray();
        copy[0] = copy[0] with { InputKey = "changed outside immutable array" };
        var roundtrip = Codec.DecodeSelection(Codec.EncodeSelection(selection with { Items = immutable }));
        Assert.Equal("input-1", roundtrip.Items[0].InputKey);
        Assert.Equal(2, roundtrip.Items.Length);
    }

    [Fact]
    public void NullClaimsAbsentTagsAndPresentEmptyAreDifferentWithoutNormalization()
    {
        var noClaims = Selection();
        var absent = Selection(claims: Claims());
        var present = Selection(claims: Claims(LocalFieldPresence.Present));
        string Digest(LocalImportSelectionDescriptor x) => Codec.ComputeRequestSha256(x, Content(x));
        Assert.Equal(3, new[] { Digest(noClaims), Digest(absent), Digest(present) }.Distinct().Count());
        var claims = Claims(LocalFieldPresence.Present) with
        { ClaimId = "", SourceTags = ["z", "z", "", "é", "e\u0301", "🐱"], Omissions = [new("privateCategories", LocalClaimOmissionReason.NotSelected)] };
        var decoded = Codec.DecodeClaims(Codec.EncodeClaims(claims));
        Assert.Equal(claims.SourceTags.ToArray(), decoded.SourceTags.ToArray());
        Assert.Equal("", decoded.ClaimId);
        Assert.Equal(claims.Omissions.ToArray(), decoded.Omissions.ToArray());
        Reject(() => Codec.EncodeClaims(claims with { TagsPresence = LocalFieldPresence.Absent }));
        Reject(() => Codec.EncodeClaims(claims with { TagsPresence = (LocalFieldPresence)2 }));
        Reject(() => Codec.EncodeClaims(claims with { SourceTags = default }));
    }

    [Fact]
    public void UrlsNeedExplicitProfileClassificationAndNeverUserInfoOrAcquisitionFallback()
    {
        const string url = "https://public.example.test/item/1";
        var claims = Claims() with { PublicSourceUrl = url };
        Reject(() => Codec.EncodeClaims(claims));
        var classified = new LocalImportCodec((profile, version, candidate)
            => profile == "synthetic-profile" && version == 1 && candidate == url);
        Assert.Equal(url, classified.DecodeClaims(classified.EncodeClaims(claims)).PublicSourceUrl);
        Reject(() => classified.EncodeClaims(claims with { ProfileVersion = 2 }));
        Reject(() => classified.EncodeClaims(claims with { PublicSourceUrl = url + "?bearer=private" }));
        var alwaysTrue = new LocalImportCodec((_, _, _) => true);
        Reject(() => alwaysTrue.EncodeClaims(claims with { PublicSourceUrl = "https://secret@public.example.test/item/1" }));
        Reject(() => alwaysTrue.EncodeClaims(claims with { PublicSourceUrl = "file:///synthetic" }));
        Reject(() => alwaysTrue.EncodeClaims(claims with { PublicSourceUrl = " " + url }));
        Reject(() => Codec.EncodeClaims(Claims() with { Omissions = [new("privateCategories", LocalClaimOmissionReason.UnsafeValue)] }));
        Reject(() => Codec.EncodeClaims(claims with { PublicSourceUrl = null,
            Omissions = [new("untrusted diagnostic secret", LocalClaimOmissionReason.UnsafeValue)] }));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("\\")]
    [InlineData("/home/synthetic/private/photo.jpg")]
    [InlineData("folder/photo.jpg")]
    [InlineData("./photo.jpg")]
    [InlineData("../photo.jpg")]
    [InlineData(@"C:\Users\synthetic\private\photo.jpg")]
    [InlineData("C:/Users/synthetic/private/photo.jpg")]
    [InlineData(@"\photo.jpg")]
    [InlineData(@"folder\photo.jpg")]
    [InlineData(@"\\server\share\photo.jpg")]
    [InlineData("//server/share/photo.jpg")]
    [InlineData(@"\\?\C:\photo.jpg")]
    [InlineData(@"\\.\C:\photo.jpg")]
    [InlineData("C:photo.jpg")]
    [InlineData("c:photo.jpg")]
    [InlineData("C:")]
    [InlineData("z:")]
    public void DisplayNamesRejectSourcePathsAtEverySelectionBoundary(string displayName)
    {
        var selection = Selection();
        var invalid = First(selection, x => x with { DisplayName = displayName });
        Reject(() => Codec.EncodeSelection(invalid));
        Reject(() => Codec.ValidateSelection(new(selection.Title, [new("input-1", displayName, null)])));

        // Start with valid wire, then inject a path without using the rejecting encoder.
        JsonObject root = JsonNode.Parse(Codec.EncodeSelection(selection))!.AsObject();
        root["items"]![0]!["displayName"] = displayName;
        byte[] wire = Encoding.UTF8.GetBytes(root.ToJsonString());
        Reject(() => Codec.DecodeSelection(wire));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("photo.jpg")]
    [InlineData("猫 café e\u0301 🐱.jpg")]
    [InlineData("é.jpg")]
    [InlineData("e\u0301.jpg")]
    [InlineData(" photo.jpg ")]
    public void DisplayNamesPreserveAllowedValuesExactly(string? displayName)
    {
        var selection = First(Selection(), x => x with { DisplayName = displayName });
        Codec.ValidateSelection(new(selection.Title, [new("input-1", displayName, null)]));
        byte[] wire = Codec.EncodeSelection(selection);
        var decoded = Codec.DecodeSelection(wire);
        Assert.Equal(displayName, decoded.Items[0].DisplayName);
        Assert.Equal(wire, Codec.EncodeSelection(decoded));
    }

    [Fact]
    public void UrlClassificationIsRequiredAgainWhenDecodingClaimsAndNestedSelections()
    {
        const string url = "https://public.example.test/item/1";
        var claims = Claims() with { PublicSourceUrl = url };
        var classified = new LocalImportCodec((profile, version, candidate)
            => profile == "synthetic-profile" && version == 1 && candidate == url);
        var selection = Selection(claims: claims);
        var content = Content(selection);
        byte[] claimsWire = classified.EncodeClaims(claims);
        byte[] selectionWire = classified.EncodeSelection(selection);
        var decoded = classified.DecodeSelection(selectionWire);
        Assert.Equal(url, decoded.Items[0].Claims!.PublicSourceUrl);
        Assert.Equal(classified.ComputeRequestSha256(selection, content), classified.ComputeRequestSha256(decoded, content));
        foreach (var rejecting in new[] { Codec, new LocalImportCodec((_, _, _) => false) })
        {
            Reject(() => rejecting.DecodeClaims(claimsWire));
            Reject(() => rejecting.DecodeSelection(selectionWire));
            Reject(() => rejecting.ComputeRequestSha256(decoded, content));
        }
    }

    [Fact]
    public void JsonWhitespaceAndFieldOrderDoNotAffectCanonicalRequestDigest()
    {
        var selection = Selection(claims: Claims(LocalFieldPresence.Present) with { SourceTags = ["first", "second"] });
        var content = Content(selection);
        byte[] encoded = Codec.EncodeSelection(selection);
        JsonObject root = JsonNode.Parse(encoded)!.AsObject();
        var reordered = new JsonObject();
        foreach (var member in root.Reverse()) reordered.Add(member.Key, member.Value?.DeepClone());
        var decoded = Codec.DecodeSelection(Encoding.UTF8.GetBytes(reordered.ToJsonString(new() { WriteIndented = true })));
        Assert.Equal(encoded, Codec.EncodeSelection(decoded));
        Assert.Equal(Codec.ComputeRequestSha256(selection, content), Codec.ComputeRequestSha256(decoded, content));
        Assert.Equal(Codec.EncodeContent(content), Codec.EncodeContent(Codec.DecodeContent(Codec.EncodeContent(content))));
    }

    [Theory]
    [InlineData("\"version\":1", "\"version\":1,\"extra\":0")]
    [InlineData("\"version\":1", "\"version\":1,\"version\":1")]
    [InlineData("\"version\":1", "\"version\":1,\"\\u0076ersion\":1")]
    [InlineData("\"version\":1", "\"version\":2")]
    [InlineData("\"title\":\"synthetic collection\",", "")]
    [InlineData("\"claims\":null", "\"claims\":{}")]
    [InlineData("\"ordinal\":0", "\"ordinal\":0.0")]
    [InlineData("\"ordinal\":0", "\"ordinal\":0e0")]
    [InlineData("\"ordinal\":0", "\"ordinal\":\"0\"")]
    [InlineData("\"inputKey\":\"input-1\"", "\"inputKey\":null")]
    public void SelectionRejectsUnknownDuplicateMissingWrongKindOrVersion(string oldText, string newText)
    {
        string json = Encoding.UTF8.GetString(Codec.EncodeSelection(Selection()));
        Assert.Contains(oldText, json);
        Reject(() => Codec.DecodeSelection(Encoding.UTF8.GetBytes(json.Replace(oldText, newText, StringComparison.Ordinal))));
    }

    [Fact]
    public void InvalidRawOrEscapedUnicodeBomCommentsDepthAndTrailingTokensFailClosed()
    {
        byte[] valid = Codec.EncodeSelection(Selection());
        string json = Encoding.UTF8.GetString(valid);
        foreach (string badTitle in new[] { "\\ud800", "\\udc00", "\\ud800x", "\\ud800\\u0041" })
            Reject(() => Codec.DecodeSelection(Encoding.UTF8.GetBytes(json.Replace("synthetic collection", badTitle, StringComparison.Ordinal))));
        var cat = Codec.DecodeSelection(Encoding.UTF8.GetBytes(json.Replace("synthetic collection", "\\ud83d\\udc31", StringComparison.Ordinal)));
        Assert.Equal("🐱", cat.Title);
        Reject(() => Codec.EncodeSelection(Selection() with { Title = "bad\ud800" }));
        Reject(() => Codec.EncodeClaims(Claims(LocalFieldPresence.Present) with { SourceTags = ["bad\udc00"] }));
        Reject(() => Codec.DecodeSelection(new byte[] { 0xef, 0xbb, 0xbf }.Concat(valid).ToArray()));
        Reject(() => Codec.DecodeSelection(new byte[] { 0xc0, 0xaf }));
        Reject(() => Codec.DecodeSelection(Encoding.UTF8.GetBytes(json + "{}")));
        Reject(() => Codec.DecodeSelection(Encoding.UTF8.GetBytes("/*comment*/" + json)));
        Reject(() => Codec.DecodeSelection(Encoding.UTF8.GetBytes(json[..^1] + ",}")));
        Reject(() => Codec.DecodeSelection(Encoding.UTF8.GetBytes(new string('[', 17) + "0" + new string(']', 17))));
        Assert.Equal("literal \\ud800", Codec.DecodeSelection(Codec.EncodeSelection(Selection() with { Title = "literal \\ud800" })).Title);
    }

    [Fact]
    public void AllCodecsEnforceExactNestedFieldsEnumsAndCanonicalIdsHashes()
    {
        var selection = Selection(claims: Claims());
        string wire = Encoding.UTF8.GetString(Codec.EncodeSelection(selection));
        Reject(() => Codec.DecodeSelection(Encoding.UTF8.GetBytes(wire.Replace(Instance, Instance.ToUpperInvariant(), StringComparison.Ordinal))));
        string item = selection.Items[0].Reference.ItemId.ToString();
        Reject(() => Codec.DecodeSelection(Encoding.UTF8.GetBytes(wire.Replace(item, item.Replace("-", ""), StringComparison.Ordinal))));
        string claimWire = Encoding.UTF8.GetString(Codec.EncodeClaims(Claims()));
        Reject(() => Codec.DecodeClaims(Encoding.UTF8.GetBytes(claimWire.Replace("\"profileVersion\":1", "\"profileVersion\":0", StringComparison.Ordinal))));
        Reject(() => Codec.DecodeClaims(Encoding.UTF8.GetBytes(claimWire.Replace("\"tagsPresence\":0", "\"tagsPresence\":8", StringComparison.Ordinal))));
        Reject(() => Codec.EncodeClaims(Claims() with { Omissions = [new("claimId", (LocalClaimOmissionReason)0)] }));
        var content = Content(selection);
        Reject(() => Codec.EncodeContent(content with { Items = [content.Items[0] with { Sha256 = Hash.ToUpperInvariant() }] }));
        Reject(() => Codec.EncodeContent(content with { Items = [content.Items[0] with { Length = -1 }] }));
        string contentWire = Encoding.UTF8.GetString(Codec.EncodeContent(content));
        Reject(() => Codec.DecodeContent(Encoding.UTF8.GetBytes(contentWire.Replace("\"length\":0", "\"length\":0e0", StringComparison.Ordinal))));
        Reject(() => Codec.DecodeContent(Encoding.UTF8.GetBytes(contentWire.Replace("\"sha256\":", "\"unknown\":1,\"sha256\":", StringComparison.Ordinal))));
        var receipt = Receipt(selection, content);
        string receiptWire = Encoding.UTF8.GetString(Codec.EncodeReceipt(receipt));
        Reject(() => Codec.DecodeReceipt(Encoding.UTF8.GetBytes(receiptWire.Replace("\"role\":1", "\"role\":2", StringComparison.Ordinal)), Key(receipt)), LocalImportError.UnsupportedRole);
        Reject(() => Codec.DecodeReceipt(Encoding.UTF8.GetBytes(receiptWire.Replace("\"collectionRevision\":1", "\"collectionRevision\":2", StringComparison.Ordinal)), Key(receipt)));
    }

    [Fact]
    public void SelectionRejectsDuplicateKeysIdsGapsAndForeignCollection()
    {
        var selection = Selection(2);
        Reject(() => Codec.EncodeSelection(First(selection, x => x with { InputKey = selection.Items[1].InputKey })));
        Reject(() => Codec.EncodeSelection(First(selection, x => x with { Reference = selection.Items[1].Reference })));
        Reject(() => Codec.EncodeSelection(First(selection, x => x with { Reference = x.Reference with { RevisionId = selection.Items[1].Reference.RevisionId } })));
        Reject(() => Codec.EncodeSelection(First(selection, x => x with { Ordinal = 1 })));
        Reject(() => Codec.EncodeSelection(First(selection, x => x with { Reference = x.Reference with
        { RevisionId = new(selection.Items[1].Reference.ItemId.Value) } })));
        Reject(() => Codec.EncodeSelection(First(selection, x => x with
        { Reference = x.Reference with { CollectionId = new(Guid.Parse("55555555-5555-5555-5555-555555555555")) } })));
        Reject(() => Codec.EncodeSelection(selection with { Items = [] }));
        Reject(() => Codec.EncodeSelection(selection with { Items = default }));
    }

    [Fact]
    public void SlotsAreDeterministicNoPathsAreOpenedAndForeignTraversalAliasesFail()
    {
        var selection = Selection();
        var content = Content(selection);
        string slot = content.Items[0].PayloadRelativeSlot;
        Assert.Equal($"local-import-operations/{Operation:N}/payloads/{selection.Items[0].Reference.RevisionId.Value:N}.original.bin", slot);
        foreach (string bad in new[] { "/" + slot, "../" + slot, slot.Replace("/payloads/", "/stage/", StringComparison.Ordinal),
            slot.Replace("/payloads/", "/payloads/../payloads/", StringComparison.Ordinal), slot.ToUpperInvariant(), slot.Replace('/', '\\') })
            Reject(() => Codec.EncodeContent(content with { Items = [content.Items[0] with { PayloadRelativeSlot = bad }] }), LocalImportError.InvalidPayloadBinding);
        var otherOperation = selection with { OperationId = Guid.Parse("99999999-9999-9999-9999-999999999999") };
        Reject(() => Codec.ComputeRequestSha256(otherOperation, content), LocalImportError.InvalidPayloadBinding);
    }

    [Fact]
    public void EveryRelevantFrozenValueChangesDigestAndJsonFormattingDoesNot()
    {
        var selection = Selection(claims: Claims(LocalFieldPresence.Present) with { SourceTags = ["a", "b"] });
        var content = Content(selection, 1);
        string baseline = Codec.ComputeRequestSha256(selection, content);
        foreach (var changed in new[]
        {
            selection with { Title = selection.Title + "!" },
            First(selection, x => x with { InputKey = "other" }),
            First(selection, x => x with { DisplayName = "" }),
            First(selection, x => x with { Claims = null }),
            First(selection, x => x with { Claims = x.Claims! with { ProfileVersion = 2 } }),
            First(selection, x => x with { Claims = x.Claims! with { ProfileId = "other-profile" } }),
            First(selection, x => x with { Claims = x.Claims! with { ClaimId = "" } }),
            First(selection, x => x with { Claims = x.Claims! with { SourceTags = ["b", "a"] } }),
            First(selection, x => x with { Claims = x.Claims! with { Omissions = [new("claimId", LocalClaimOmissionReason.NotSelected)] } }),
            selection with { CatalogInstance = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa" }
        }) Assert.NotEqual(baseline, Codec.ComputeRequestSha256(changed, content));
        Assert.NotEqual(baseline, Codec.ComputeRequestSha256(selection, content with { Items = [content.Items[0] with { Length = 2 }] }));
        Assert.NotEqual(baseline, Codec.ComputeRequestSha256(selection, content with { Items = [content.Items[0] with { Sha256 = new string('b', 64) }] }));
        var changedReference = First(selection, x => x with { Reference = x.Reference with
        { RevisionId = new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")) } });
        Assert.NotEqual(baseline, Codec.ComputeRequestSha256(changedReference, Content(changedReference, 1)));
        var changedItem = First(selection, x => x with { Reference = x.Reference with
        { ItemId = new(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")) } });
        Assert.NotEqual(baseline, Codec.ComputeRequestSha256(changedItem, Content(changedItem, 1)));
        var changedCollection = selection with { CollectionId = new(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc")) };
        changedCollection = changedCollection with { Items = changedCollection.Items.Select(x => x with
        { Reference = x.Reference with { CollectionId = changedCollection.CollectionId } }).ToImmutableArray() };
        Assert.NotEqual(baseline, Codec.ComputeRequestSha256(changedCollection, Content(changedCollection, 1)));
        var changedOperation = selection with { OperationId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd") };
        Assert.NotEqual(baseline, Codec.ComputeRequestSha256(changedOperation, Content(changedOperation, 1)));
        var multiple = Selection(2);
        var reordered = multiple with { Items = multiple.Items.Reverse().Select((x, i) => x with { Ordinal = i }).ToImmutableArray() };
        Assert.NotEqual(Codec.ComputeRequestSha256(multiple, Content(multiple)), Codec.ComputeRequestSha256(reordered, Content(reordered)));
        Assert.NotEqual(Codec.ComputeRequestSha256(selection with { Title = "é" }, content),
            Codec.ComputeRequestSha256(selection with { Title = "e\u0301" }, content));
    }

    [Fact]
    public void RequestAndBindingDigestsMatchIndependentBinaryVectors()
    {
        var selection = Selection();
        var content = Content(selection);
        LocalSelectedItem item = selection.Items[0];
        using var request = new MemoryStream();
        request.Write(Encoding.UTF8.GetBytes("meowchan.local-import.request.v1\0"));
        VectorText(request, Instance); VectorText(request, Operation.ToString("D")); VectorText(request, Collection.ToString());
        VectorText(request, "synthetic collection"); VectorInt(request, 1);
        VectorInt(request, 0); VectorText(request, item.Reference.ItemId.ToString()); VectorText(request, item.Reference.RevisionId.ToString());
        VectorText(request, "input-1"); VectorInt(request, 1);
        request.Write(new byte[8]); // Int64 length=0, big-endian
        request.Write(Enumerable.Repeat((byte)0xaa, 32).ToArray());
        VectorText(request, content.Items[0].PayloadRelativeSlot);
        request.WriteByte(0); request.WriteByte(0); // display/claims absent
        Assert.Equal(VectorHash(request), Codec.ComputeRequestSha256(selection, content));
        foreach (LocalImportBindingKind kind in Enum.GetValues<LocalImportBindingKind>())
        {
            using var binding = new MemoryStream();
            binding.Write(Encoding.UTF8.GetBytes(kind == LocalImportBindingKind.Stage
                ? "meowchan.local-import.stage-bindings.v1\0" : "meowchan.local-import.candidate-bindings.v1\0"));
            VectorInt(binding, 1); VectorText(binding, Collection.ToString());
            VectorText(binding, item.Reference.ItemId.ToString()); VectorText(binding, item.Reference.RevisionId.ToString());
            VectorInt(binding, 1); binding.Write(new byte[8]); binding.Write(Enumerable.Repeat((byte)0xaa, 32).ToArray());
            VectorText(binding, kind == LocalImportBindingKind.Stage
                ? $"local-import-operations/{Operation:N}/stage/{item.Reference.RevisionId.Value:N}.original.bin" : content.Items[0].PayloadRelativeSlot);
            Assert.Equal(VectorHash(binding), Codec.ComputeBindingsSha256(selection, content, kind));
        }
        Assert.NotEqual(Codec.ComputeBindingsSha256(selection, content, LocalImportBindingKind.Stage),
            Codec.ComputeBindingsSha256(selection, content, LocalImportBindingKind.Candidate));
        Reject(() => Codec.ComputeBindingsSha256(selection, content, (LocalImportBindingKind)0));
    }
    [Fact]
    public void NonnullClaimsDigestMatchesIndependentUtf8PresenceAndInt64Vector()
    {
        const string url = "https://public.example.test/item/7";
        var claims = Claims(LocalFieldPresence.Present) with
        {
            ProfileId = "synthetic-猫", ProfileVersion = 7, ClaimId = "項目-é", PublicSourceUrl = url,
            SourceTags = ["é", "e\u0301", "", "猫", "é"],
            Omissions = [new("privateCategories", LocalClaimOmissionReason.NotSelected),
                new("acquisitionUrl", LocalClaimOmissionReason.UnsafeValue)]
        };
        var classified = new LocalImportCodec((profile, version, candidate)
            => profile == "synthetic-猫" && version == 7 && candidate == url);
        var selection = First(Selection(claims: claims) with { Title = "集合-é" },
            x => x with { DisplayName = "猫 e\u0301.jpg" });
        var content = Content(selection, 0x0102030405060708L);
        var reference = selection.Items[0].Reference;
        using var request = new MemoryStream();
        request.Write(Encoding.UTF8.GetBytes("meowchan.local-import.request.v1\0"));
        VectorText(request, Instance); VectorText(request, Operation.ToString("D")); VectorText(request, Collection.ToString());
        VectorText(request, "集合-é"); VectorInt(request, 1);
        VectorInt(request, 0); VectorText(request, reference.ItemId.ToString()); VectorText(request, reference.RevisionId.ToString());
        VectorText(request, "input-1"); VectorInt(request, 1); // Original
        request.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }); // Int64 length, big-endian
        request.Write(Enumerable.Repeat((byte)0xaa, 32).ToArray());
        VectorText(request, $"local-import-operations/{Operation:N}/payloads/{reference.RevisionId.Value:N}.original.bin");
        request.WriteByte(1); VectorText(request, "猫 e\u0301.jpg"); // display present
        request.WriteByte(1); // claims present
        VectorInt(request, 1); VectorText(request, "synthetic-猫"); VectorInt(request, 7);
        request.WriteByte(1); VectorText(request, "項目-é"); // ClaimId present
        request.WriteByte(1); VectorText(request, url); // PublicSourceUrl present
        VectorInt(request, 1); VectorInt(request, 5); // Present tags, ordered and not deduplicated
        VectorText(request, "é"); VectorText(request, "e\u0301"); VectorText(request, "");
        VectorText(request, "猫"); VectorText(request, "é");
        VectorInt(request, 2); VectorText(request, "privateCategories"); VectorInt(request, 2);
        VectorText(request, "acquisitionUrl"); VectorInt(request, 3);
        string expected = VectorHash(request);
        Assert.Equal(expected, classified.ComputeRequestSha256(selection, content));
        Assert.Equal(expected, classified.ComputeRequestSha256(
            classified.DecodeSelection(classified.EncodeSelection(selection)),
            classified.DecodeContent(classified.EncodeContent(content))));
    }
    private static void VectorInt(MemoryStream stream, int number)
    { for (int shift = 24; shift >= 0; shift -= 8) stream.WriteByte((byte)((uint)number >> shift)); }
    private static void VectorText(MemoryStream stream, string value)
    { byte[] bytes = new UTF8Encoding(false, true).GetBytes(value); VectorInt(stream, bytes.Length); stream.Write(bytes); }
    private static string VectorHash(MemoryStream stream) => Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();

    [Fact]
    public void ScalarsUseUtf8BytesBoundsNotUtf16OrCharacterCount()
    {
        _ = Codec.EncodeSelection(Selection() with { Title = new string('é', LocalImportBounds.MaximumTitleBytes / 2) });
        Reject(() => Codec.EncodeSelection(Selection() with { Title = new string('é', LocalImportBounds.MaximumTitleBytes / 2) + "x" }), LocalImportError.BudgetExceeded);
        Reject(() => Codec.EncodeSelection(Selection() with { Title = "line\nbreak" }));
        var item = Selection();
        _ = Codec.EncodeSelection(First(item, x => x with { InputKey = new string('x', LocalImportBounds.MaximumInputKeyBytes), DisplayName = new string('x', LocalImportBounds.MaximumDisplayNameBytes) }));
        Reject(() => Codec.EncodeSelection(First(item, x => x with { InputKey = new string('x', LocalImportBounds.MaximumInputKeyBytes + 1) })), LocalImportError.BudgetExceeded);
        Reject(() => Codec.EncodeSelection(First(item, x => x with { DisplayName = new string('x', LocalImportBounds.MaximumDisplayNameBytes + 1) })), LocalImportError.BudgetExceeded);
        var classified = new LocalImportCodec((_, _, _) => true);
        const string urlPrefix = "https://public.example.test/";
        string maxUrl = urlPrefix + new string('x', LocalImportBounds.MaximumUrlBytes - urlPrefix.Length);
        _ = classified.EncodeClaims(Claims() with { PublicSourceUrl = maxUrl });
        Reject(() => classified.EncodeClaims(Claims() with { PublicSourceUrl = maxUrl + "x" }), LocalImportError.BudgetExceeded);
        _ = Codec.EncodeClaims(Claims() with { ProfileId = new string('x', 128), ClaimId = new string('x', 1024) });
        Reject(() => Codec.EncodeClaims(Claims() with { ProfileId = new string('x', 129) }), LocalImportError.BudgetExceeded);
        Reject(() => Codec.EncodeClaims(Claims() with { ClaimId = new string('x', 1025) }), LocalImportError.BudgetExceeded);
        _ = Codec.EncodeClaims(Claims(LocalFieldPresence.Present) with { SourceTags = [new string('x', 1024)] });
        Reject(() => Codec.EncodeClaims(Claims(LocalFieldPresence.Present) with { SourceTags = [new string('x', 1025)] }), LocalImportError.BudgetExceeded);
        _ = Codec.EncodeClaims(Claims(LocalFieldPresence.Present) with { SourceTags = Enumerable.Repeat("", 4096).ToImmutableArray() });
        Reject(() => Codec.EncodeClaims(Claims(LocalFieldPresence.Present) with { SourceTags = Enumerable.Repeat("", 4097).ToImmutableArray() }), LocalImportError.BudgetExceeded);
    }

    // Fill an exact serialized claims-byte budget using ASCII, whose JSON form is
    // independent of encoder escape preferences. No production preflight math reused.
    private static LocalSourceClaims ClaimsAtByteCap(int target = LocalImportBounds.MaximumClaimsBytes)
    {
        var empty = Claims(LocalFieldPresence.Present);
        int overhead = Codec.EncodeClaims(empty).Length;
        int fullCount = (target - overhead - 3) / 1027;
        var tags = Enumerable.Repeat(new string('x', 1024), fullCount).ToList();
        int current = Codec.EncodeClaims(empty with { SourceTags = tags.ToImmutableArray() }).Length;
        tags.Add(new string('y', target - current - 3));
        return empty with { SourceTags = tags.ToImmutableArray() };
    }

    [Fact]
    public void ClaimsAndCollectionCapsIncludeWrappersAndAreSimultaneous()
    {
        LocalSourceClaims exact = ClaimsAtByteCap();
        Assert.Equal(LocalImportBounds.MaximumClaimsBytes, Codec.EncodeClaims(exact).Length);
        Reject(() => Codec.EncodeClaims(exact with { ClaimId = "one-more" }), LocalImportError.BudgetExceeded);
        _ = Codec.EncodeSelection(Selection(16, exact));
        // Individual caps fitting does not promise all maxima simultaneously fit
        // the operation reservation, especially the temporary metadata publication.
        Reject(() => Codec.Preflight(Selection(16, exact), Limits), LocalImportError.BudgetExceeded);
        Reject(() => Codec.EncodeSelection(Selection(17, exact)), LocalImportError.BudgetExceeded);
        int minimalBytes = Codec.EncodeClaims(Claims(LocalFieldPresence.Present)).Length;
        var plusOne = Selection(17, exact);
        plusOne = plusOne with { Items = plusOne.Items
            .SetItem(15, plusOne.Items[15] with { Claims = ClaimsAtByteCap(LocalImportBounds.MaximumClaimsBytes - minimalBytes + 1) })
            .SetItem(16, plusOne.Items[16] with { Claims = Claims(LocalFieldPresence.Present) }) };
        Reject(() => Codec.EncodeSelection(plusOne), LocalImportError.BudgetExceeded);
        Reject(() => Codec.DecodeSelection(new byte[LocalImportBounds.MaximumSelectionBytes + 1]), LocalImportError.BudgetExceeded);
        Reject(() => Codec.DecodeContent(new byte[LocalImportBounds.MaximumContentBytes + 1]), LocalImportError.BudgetExceeded);
        Reject(() => Codec.DecodeClaims(new byte[LocalImportBounds.MaximumClaimsBytes + 1]), LocalImportError.BudgetExceeded);
    }

    [Fact]
    public void CountPayloadSumAndReservationOverflowFailBeforeAnyStageExists()
    {
        var maximum = Selection(1000);
        _ = Codec.EncodeSelection(maximum);
        _ = Codec.EncodeContent(Content(maximum));
        Reject(() => Codec.EncodeSelection(Selection(1001)), LocalImportError.BudgetExceeded);
        var two = Content(Selection(2), long.MaxValue);
        Reject(() => Codec.EncodeContent(two), LocalImportError.BudgetExceeded);
        Reject(() => Codec.Preflight(Selection(), new(long.MaxValue, 32 * 1024 * 1024)), LocalImportError.BudgetExceeded);
        Reject(() => Codec.Preflight(Selection(), new(0, 32 * 1024 * 1024)));
        Reject(() => Codec.Preflight(Selection(), new(1, 0)));
        Reject(() => Codec.Preflight(Selection(), new(1, 32L * 1024 * 1024 + 1)), LocalImportError.BudgetExceeded);
        var budget = Codec.Preflight(Selection(), Limits);
        _ = Codec.Preflight(Selection(), Limits with { ReservedMetadataBytes = budget.RequiredMetadataBytes });
        Reject(() => Codec.Preflight(Selection(), Limits with { ReservedMetadataBytes = budget.RequiredMetadataBytes - 1 }), LocalImportError.BudgetExceeded);
        Assert.Equal(checked(2 * Limits.MaxSelectedPayloadBytes + Limits.ReservedMetadataBytes), budget.RequiredFileBytes);
        Assert.True(budget.ReadyJournalBytes > budget.MaximumContentBytes);
        Assert.True(budget.RequiredMetadataBytes > budget.SelectionBytes + budget.MaximumContentBytes + 2 * budget.ReadyJournalBytes + budget.ReceiptBytes);
    }

    [Fact]
    public void ThousandItemCompactReceiptsFitAndKeepEveryFullReferenceWithoutLiveContent()
    {
        var selection = Selection(1000);
        var receipt = Receipt(selection, Content(selection));
        byte[] wire = Codec.EncodeReceipt(receipt);
        Assert.True(wire.Length <= LocalImportBounds.MaximumReceiptBytes);
        var budget = Codec.Preflight(selection, Limits);
        Assert.True(wire.LongLength <= budget.ReceiptBytes);
        var decoded = Codec.DecodeReceipt(wire, Key(receipt)); // no inventory/content/FS arguments
        Assert.Equal(receipt.Revisions.ToArray(), decoded.Revisions.ToArray());
        Assert.Equal(wire, Codec.EncodeReceipt(decoded));
        string text = Encoding.UTF8.GetString(wire);
        Assert.DoesNotContain("claims", text);
        Assert.DoesNotContain("title", text);
        Assert.DoesNotContain("payloadRelativeSlot", text);
        Assert.DoesNotContain("input-", text);
    }

    [Fact]
    public void ReceiptHeaderBindingFailuresAreNotAbsenceAndDiagnosticsAreCodeOnly()
    {
        var selection = Selection();
        var receipt = Receipt(selection, Content(selection));
        byte[] wire = Codec.EncodeReceipt(receipt);
        Reject(() => Codec.DecodeReceipt(wire, Key(receipt) with { CatalogInstance = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa" }), LocalImportError.TargetReplaced);
        Reject(() => Codec.DecodeReceipt(wire, Key(receipt) with { OperationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa") }), LocalImportError.OperationConflict);
        Reject(() => Codec.DecodeReceipt(wire, Key(receipt) with { RequestSha256 = new string('b', 64) }), LocalImportError.OperationConflict);
        Reject(() => Codec.DecodeReceipt(new byte[LocalImportBounds.MaximumReceiptBytes + 1], Key(receipt)), LocalImportError.BudgetExceeded);
        var error = Assert.Throws<LocalImportCodecException>(() => Codec.DecodeSelection(Encoding.UTF8.GetBytes("{\"secret-synthetic-value\":1}")));
        Assert.DoesNotContain("secret-synthetic-value", error.ToString());
        Assert.Null(error.InnerException);
        Reject(() => Codec.EncodeReceipt(receipt with { RegisteredAtUtc = receipt.RegisteredAtUtc.ToOffset(TimeSpan.FromHours(1)) }));
    }

    private static LocalImportOperationDetail Detail(LocalImportOperationState state)
    {
        var selection = Selection();
        var content = Content(selection, 4);
        bool captured = state != LocalImportOperationState.Capturing;
        string? request = captured ? Codec.ComputeRequestSha256(selection, content) : null;
        var summary = new LocalImportOperationSummary(Operation, Collection, Instance, request, state, 1, 1, captured ? 4 : null);
        return new(summary, Limits, 0, selection.Title, selection.Items.Select((x, i) => new LocalFrozenItem(
            x.Reference, x.Ordinal, x.InputKey, x.DisplayName, x.Claims, captured ? content.Items[i].Length : null,
            captured ? content.Items[i].Sha256 : null)).ToImmutableArray(),
            captured ? Codec.ComputeBindingsSha256(selection, content, LocalImportBindingKind.Stage) : null,
            state is LocalImportOperationState.Prepared or LocalImportOperationState.Committed
                ? Codec.ComputeBindingsSha256(selection, content, LocalImportBindingKind.Candidate) : null);
    }

    [Fact]
    public void CapturingRequiresNullMeasurementsSealedStatesRequireConsistentDigests()
    {
        foreach (LocalImportOperationState state in Enum.GetValues<LocalImportOperationState>()) Codec.ValidateInventory(Detail(state));
        var capturing = Detail(LocalImportOperationState.Capturing);
        Reject(() => Codec.ValidateInventory(capturing with { Summary = capturing.Summary with { TotalLength = 0 } }), LocalImportError.InvalidInventory);
        Reject(() => Codec.ValidateInventory(capturing with { Items = [capturing.Items[0] with { Length = 0 }] }), LocalImportError.InvalidInventory);
        var frozen = Detail(LocalImportOperationState.Frozen);
        Reject(() => Codec.ValidateInventory(frozen with { Items = [frozen.Items[0] with { Length = null }] }), LocalImportError.InvalidInventory);
        Reject(() => Codec.ValidateInventory(frozen with { Summary = frozen.Summary with { RequestSha256 = new string('b', 64) } }), LocalImportError.InvalidInventory);
        Reject(() => Codec.ValidateInventory(frozen with { CandidateBindingsSha256 = new string('b', 64) }), LocalImportError.InvalidInventory);
        Reject(() => Codec.ValidateInventory(frozen with { Limits = Limits with { MaxSelectedPayloadBytes = 3 } }), LocalImportError.BudgetExceeded);
        Reject(() => Codec.ValidateInventory(frozen with { Summary = frozen.Summary with { State = (LocalImportOperationState)9 } }), LocalImportError.InvalidInventory);
        var replanValues = frozen with { ExpectedCatalogGeneration = 42, Summary = frozen.Summary with { Attempt = 2 } };
        Codec.ValidateInventory(replanValues);
        Assert.Equal(frozen.Summary.RequestSha256, replanValues.Summary.RequestSha256);
    }

    [Fact]
    public void PassiveInventoryPagesUseCanonicalOrdinalKeysetOrderNotGuidComparison()
    {
        var summary = Detail(LocalImportOperationState.Capturing).Summary;
        var rows = Enumerable.Range(1, 32).Select(i => summary with { OperationId = Guid.Parse($"{i:x8}-1111-1111-1111-111111111111") }).ToImmutableArray();
        Codec.ValidateInventoryPage(new(rows, rows[^1].OperationId));
        Codec.ValidateInventoryPage(new([], null));
        Reject(() => Codec.ValidateInventoryPage(new(rows.Add(summary), null)), LocalImportError.InvalidInventory);
        Reject(() => Codec.ValidateInventoryPage(new(rows.Reverse().ToImmutableArray(), null)), LocalImportError.InvalidInventory);
        Reject(() => Codec.ValidateInventoryPage(new(rows, rows[0].OperationId)), LocalImportError.InvalidInventory);
        Reject(() => Codec.ValidateInventoryPage(new([], Operation)), LocalImportError.InvalidInventory);
    }

    [Fact]
    public void AdmissionAndPageArithmeticHasBoundaryPlusOneCasesButNoAtomicSqlClaim()
    {
        long before = LocalImportBounds.MaximumInventoryMetadataBytes - Limits.ReservedMetadataBytes;
        LocalImportCodec.ValidateAdmission(4095, 31, before, Limits);
        Reject(() => LocalImportCodec.ValidateAdmission(4096, 31, before, Limits), LocalImportError.InventoryFull);
        Reject(() => LocalImportCodec.ValidateAdmission(4095, 32, before, Limits), LocalImportError.InventoryFull);
        Reject(() => LocalImportCodec.ValidateAdmission(4095, 31, before + 1, Limits), LocalImportError.InventoryFull);
        Reject(() => LocalImportCodec.ValidateAdmission(-1, 0, 0, Limits), LocalImportError.InvalidInventory);
        LocalImportCodec.ValidatePage(int.MaxValue, 500);
        Reject(() => LocalImportCodec.ValidatePage(0, 501));
        Reject(() => LocalImportCodec.ValidatePage(-1, 1));
        Reject(() => LocalImportCodec.ValidatePage(0, 0));
    }

    [Fact]
    public void ResultArmsCannotRepresentValueAndErrorOrLegacyNullAsNegativeCommit()
    {
        var value = new object();
        Assert.Same(value, LocalImportResult<object>.Success(value).Value);
        Assert.Null(LocalImportResult<object>.Failure(LocalImportError.StalePlan).Value);
        Assert.Throws<ArgumentOutOfRangeException>(() => LocalImportResult<object>.Failure(LocalImportError.None));
        Assert.Throws<ArgumentNullException>(() => LocalImportResult<object>.Success(null!));
        var receipt = Receipt(Selection(), Content(Selection()));
        var committed = LocalImportCommitResult.Committed(receipt, true);
        Assert.Same(receipt, committed.Receipt);
        Assert.True(committed.IsReplay);
        Assert.Equal(LocalImportError.None, committed.Error);
        var negative = LocalImportCommitResult.NotCommitted(LocalImportError.StalePlan);
        Assert.Null(negative.Receipt);
        Assert.False(negative.IsReplay);
        var unknown = LocalImportCommitResult.Unknown();
        Assert.Equal(LocalImportCommitStatus.Unknown, unknown.Status);
        Assert.Equal(LocalImportError.UnknownOutcome, unknown.Error);
        Assert.Throws<ArgumentException>(() => LocalImportCommitResult.NotCommitted(LocalImportError.TargetReplaced));
        Assert.Throws<ArgumentException>(() => LocalImportCommitResult.NotCommitted(LocalImportError.UnknownOutcome));
    }
}

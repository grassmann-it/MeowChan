using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using MeowChan.Archive;
using MeowChan.Storage;

namespace MeowChan.Tests;

// In-memory synthetic vectors only; no payload of the declared length is allocated.
public sealed class LocalImportCodecGoldenVectorTests
{
    private const string Instance = "12345678-90ab-cdef-1234-567890abcdef";
    private const string Operation = "10203040-5060-7080-90a0-b0c0d0e0f001";
    private const string Collection = "20304050-6070-8090-a0b0-c0d0e0f00102";
    private const string Item = "30405060-7080-90a0-b0c0-d0e0f0010203";
    private const string Revision = "40506070-8090-a0b0-c0d0-e0f001020304";
    private const string Root = "local-import-operations/102030405060708090a0b0c0d0e0f001/";
    private const string Leaf = "405060708090a0b0c0d0e0f001020304.original.bin";
    private const string Hash = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";
    private const long Length = 0x0102030405060708L;
    private const string Profile = "synthetic-public-v1";
    private const string Url = "https://codec.example.invalid/public/42";

    private static bool Trusted(string profile, int version, string url)
        => profile == Profile && version == 7 && url == Url;

    private static LocalSourceClaims Claims(int presence, bool tags, bool url)
        => new(1, Profile, 7, "識別🐱", url ? Url : null, (LocalFieldPresence)presence,
            tags ? ImmutableArray.Create("猫", "é", "猫") : ImmutableArray<string>.Empty,
            [new("privateCategories", LocalClaimOmissionReason.Unsupported),
             new("acquisitionUrl", LocalClaimOmissionReason.UnsafeValue),
             new("credentials", LocalClaimOmissionReason.NotSelected),
             new("claimId", LocalClaimOmissionReason.ExceedsBound)]);

    private static LocalImportSelectionDescriptor Selection(LocalSourceClaims claims)
    {
        var collection = new LocalCollectionId(Guid.Parse(Collection));
        return new(1, Instance, Guid.Parse(Operation), collection, "収集 café 🐱",
            [new(new(collection, new(Guid.Parse(Item)), new(Guid.Parse(Revision))),
                0, "入力-é", "猫-é.png", claims)]);
    }

    private static LocalImportContent Content(LocalImportSelectionDescriptor selection)
        => new(1, [new(selection.Items[0].Reference, 0, LocalMediaRole.Original,
            Length, Hash, Root + "payloads/" + Leaf)]);

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(1, false, true)]
    [InlineData(1, true, true)]
    public void NonemptyRequestAndBothBindingsMatchIndependentContractBytes(int presence, bool tags, bool url)
    {
        var codec = new LocalImportCodec(Trusted);
        var selection = Selection(Claims(presence, tags, url));
        var content = Content(selection);
        // Expected bytes below never consume a product encoder, slot helper or computed digest.
        string expectedRequest = Sha(RequestBytes(presence, tags, url));
        Assert.Equal(expectedRequest, codec.ComputeRequestSha256(selection, content));
        Assert.Equal(Sha(BindingBytes(false)), codec.ComputeBindingsSha256(selection, content, LocalImportBindingKind.Stage));
        Assert.Equal(Sha(BindingBytes(true)), codec.ComputeBindingsSha256(selection, content, LocalImportBindingKind.Candidate));
        Assert.NotEqual(Sha(BindingBytes(false)), Sha(BindingBytes(true)));
    }

    [Fact]
    public void AbsentAndPresentEmptyAreDistinctRequestVectorsButNotPayloadBindings()
    {
        var codec = new LocalImportCodec(Trusted);
        var absent = Selection(Claims(0, false, true));
        var present = Selection(Claims(1, false, true));
        var content = Content(absent);
        Assert.Equal(Sha(RequestBytes(0, false, true)), codec.ComputeRequestSha256(absent, content));
        Assert.Equal(Sha(RequestBytes(1, false, true)), codec.ComputeRequestSha256(present, content));
        Assert.NotEqual(Sha(RequestBytes(0, false, true)), Sha(RequestBytes(1, false, true)));
        foreach (var kind in new[] { LocalImportBindingKind.Stage, LocalImportBindingKind.Candidate })
            Assert.Equal(codec.ComputeBindingsSha256(absent, content, kind), codec.ComputeBindingsSha256(present, content, kind));
    }

    [Fact]
    public void IndependentWireRequiresExactClassifierForClaimsSelectionAndRequest()
    {
        // Hand-authored wire, not output from EncodeClaims/EncodeSelection.
        byte[] claimsWire = Encoding.UTF8.GetBytes(ClaimsJson);
        byte[] selectionWire = Encoding.UTF8.GetBytes(SelectionJson(ClaimsJson));
        var allowed = new LocalImportCodec(Trusted);
        var claims = allowed.DecodeClaims(claimsWire);
        Assert.Equal(Url, claims.PublicSourceUrl);
        Assert.Equal(new[] { "猫", "é", "猫" }, claims.SourceTags.ToArray());
        Assert.Equal(4, claims.Omissions.Length);
        var selection = allowed.DecodeSelection(selectionWire);
        Assert.Equal("猫-é.png", selection.Items[0].DisplayName);
        Assert.Equal("識別🐱", selection.Items[0].Claims!.ClaimId);
        Assert.Equal(Sha(RequestBytes(1, true, true)), allowed.ComputeRequestSha256(selection, Content(selection)));

        foreach (var denied in new[] { new LocalImportCodec(), new LocalImportCodec((_, _, _) => false) })
        {
            RejectWithoutRawUrl(() => denied.DecodeClaims(claimsWire));
            RejectWithoutRawUrl(() => denied.DecodeSelection(selectionWire));
            RejectWithoutRawUrl(() => denied.ComputeRequestSha256(selection, Content(selection)));
        }
        // Even the permitting callback accepts only the exact synthetic profile/version/URL tuple.
        foreach (string changed in new[] {
            ClaimsJson.Replace(Profile, "other-profile", StringComparison.Ordinal),
            ClaimsJson.Replace("\"profileVersion\":7", "\"profileVersion\":8", StringComparison.Ordinal),
            ClaimsJson.Replace(Url, Url + "?token=synthetic-secret", StringComparison.Ordinal) })
        {
            RejectWithoutRawUrl(() => allowed.DecodeClaims(Encoding.UTF8.GetBytes(changed)));
            RejectWithoutRawUrl(() => allowed.DecodeSelection(Encoding.UTF8.GetBytes(SelectionJson(changed))));
        }
        // A missing optional URL requires no classifier; this is not a missing Claims object.
        string noUrl = ClaimsJson.Replace('"' + Url + '"', "null", StringComparison.Ordinal);
        Assert.Null(new LocalImportCodec().DecodeClaims(Encoding.UTF8.GetBytes(noUrl)).PublicSourceUrl);
        Assert.NotNull(new LocalImportCodec().DecodeSelection(Encoding.UTF8.GetBytes(SelectionJson(noUrl))).Items[0].Claims);
    }

    private static void RejectWithoutRawUrl(Action action)
    {
        var error = Assert.Throws<LocalImportCodecException>(action);
        Assert.Equal(LocalImportError.InvalidSelection, error.Error);
        Assert.Equal("Local import codec rejected data: InvalidSelection", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Url, error.ToString());
        Assert.DoesNotContain("synthetic-secret", error.ToString());
    }

    private const string ClaimsJson = """
        {"version":1,"profileId":"synthetic-public-v1","profileVersion":7,"claimId":"識別🐱","publicSourceUrl":"https://codec.example.invalid/public/42","tagsPresence":1,"sourceTags":["猫","é","猫"],"omissions":[{"fieldCode":"privateCategories","reason":1},{"fieldCode":"acquisitionUrl","reason":3},{"fieldCode":"credentials","reason":2},{"fieldCode":"claimId","reason":4}]}
        """;

    private static string SelectionJson(string claims) => $$"""
        {"version":1,"catalogInstance":"{{Instance}}","operationId":"{{Operation}}","collectionId":"{{Collection}}","title":"収集 café 🐱","items":[{"itemId":"{{Item}}","revisionId":"{{Revision}}","ordinal":0,"inputKey":"入力-é","displayName":"猫-é.png","claims":{{claims}}}]}
        """;

    // Independent, literal field sequence from core-v1 §4. Integer emission uses shifts,
    // not the product's BinaryPrimitives/IncrementalHash Digest implementation.
    private static byte[] RequestBytes(int presence, bool tags, bool url)
    {
        using var bytes = new MemoryStream();
        Domain(bytes, "meowchan.local-import.request.v1");
        Text(bytes, Instance); Text(bytes, Operation); Text(bytes, Collection); Text(bytes, "収集 café 🐱");
        Integer(bytes, 1, 4); Integer(bytes, 0, 4); Text(bytes, Item); Text(bytes, Revision);
        Text(bytes, "入力-é"); Integer(bytes, 1, 4); Measurement(bytes); Text(bytes, Root + "payloads/" + Leaf);
        bytes.WriteByte(1); Text(bytes, "猫-é.png"); bytes.WriteByte(1); // display and Claims present
        Integer(bytes, 1, 4); Text(bytes, Profile); Integer(bytes, 7, 4);
        bytes.WriteByte(1); Text(bytes, "識別🐱"); bytes.WriteByte(url ? (byte)1 : (byte)0);
        if (url) Text(bytes, Url);
        Integer(bytes, (ulong)presence, 4); Integer(bytes, tags ? 3UL : 0UL, 4);
        if (tags) { Text(bytes, "猫"); Text(bytes, "é"); Text(bytes, "猫"); }
        Integer(bytes, 4, 4);
        Text(bytes, "privateCategories"); Integer(bytes, 1, 4);
        Text(bytes, "acquisitionUrl"); Integer(bytes, 3, 4);
        Text(bytes, "credentials"); Integer(bytes, 2, 4);
        Text(bytes, "claimId"); Integer(bytes, 4, 4);
        return bytes.ToArray();
    }

    private static byte[] BindingBytes(bool candidate)
    {
        using var bytes = new MemoryStream();
        Domain(bytes, candidate ? "meowchan.local-import.candidate-bindings.v1" : "meowchan.local-import.stage-bindings.v1");
        Integer(bytes, 1, 4); Text(bytes, Collection); Text(bytes, Item); Text(bytes, Revision);
        Integer(bytes, 1, 4); Measurement(bytes);
        Text(bytes, Root + (candidate ? "payloads/" : "stage/") + Leaf);
        return bytes.ToArray();
    }

    private static void Measurement(Stream bytes)
    {
        // Fixed big-endian Int64 0x0102030405060708 and raw SHA bytes 00..1f, not hex text.
        bytes.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        for (int i = 0; i < 32; i++) bytes.WriteByte((byte)i);
    }
    private static void Domain(Stream bytes, string domain)
    { bytes.Write(Encoding.UTF8.GetBytes(domain)); bytes.WriteByte(0); }
    private static void Text(Stream bytes, string value)
    { byte[] text = Encoding.UTF8.GetBytes(value); Integer(bytes, (ulong)text.Length, 4); bytes.Write(text); }
    private static void Integer(Stream bytes, ulong value, int width)
    { for (int shift = (width - 1) * 8; shift >= 0; shift -= 8) bytes.WriteByte((byte)(value >> shift)); }
    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

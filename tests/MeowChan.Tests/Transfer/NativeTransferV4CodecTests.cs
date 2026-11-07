using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// AP01 public representation/codec tests only. No stage/materializer, profile, network or decoder.
// Synthetic source bindings are passive claims, not proof that a fixture video was decoded.
public sealed class NativeTransferV4CodecTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid PackageId = Guid.Parse("260c4540-6bdd-4dba-bb2f-b6c8830b8741");
    private static readonly ResourceId Id = new("4chan", "b", "7001");
    private static readonly byte[] Original = Encoding.UTF8.GetBytes("synthetic original byte-version, not a decoder fixture");
    private static readonly byte[] Preview = Encoding.UTF8.GetBytes("synthetic ordinary preview, no PNG promise");
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    // zlib representation of the two bytes [filter=0, gray=0] for a 1x1 grayscale PNG.
    private static readonly byte[] IdatData = [0x78, 0x9c, 0x63, 0x60, 0x00, 0x00, 0x00, 0x02, 0x00, 0x01];
    private static readonly byte[] Poster = Png(Chunk("IHDR", Header()), Chunk("IDAT", IdatData), Chunk("IEND", []));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static TransferOriginalByteVersion Version(byte[] bytes, bool sha1 = true) => new(bytes.Length, Hash(bytes),
        Convert.ToBase64String(MD5.HashData(bytes)), sha1 ? Convert.ToBase64String(SHA1.HashData(bytes)) : null);

    private static TransferAttachment Media(string post = "15", string mediaId = "111", int position = 0,
        TransferContentStatus status = TransferContentStatus.Omitted, bool sha1 = true)
    {
        MediaProviderMetadata provider = new(mediaId, "original", $"https://i.4cdn.org/b/{mediaId}.webm", "original.webm",
            Original.Length, Version(Original).Md5, Hash(Original));
        CapturedAttachment metadata = new(position, mediaId, "original", provider.Url, provider.OriginalFileName,
            ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
        {
            ExpectedSha256 = provider.ExpectedSha256,
            AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(Id, post, position, provider)
        };
        return new(metadata, TransferMediaRole.Original, status,
            status == TransferContentStatus.Contained ? Hash(Original) : null, null)
        { OriginalByteVersion = Version(Original, sha1) };
    }
    private static TransferAttachment WithDerived(TransferAttachment a, byte[]? preview = null, byte[]? poster = null)
    {
        ImmutableArray<TransferDerivedRepresentation>.Builder items = ImmutableArray.CreateBuilder<TransferDerivedRepresentation>();
        if (preview != null) items.Add(new(TransferDerivedRepresentationKind.Preview, a.Metadata.AttachmentRevisionKey!,
            a.OriginalByteVersion!, TransferContentStatus.Contained, Hash(preview)));
        if (poster != null) items.Add(new(TransferDerivedRepresentationKind.VideoPosterPng, a.Metadata.AttachmentRevisionKey!,
            a.OriginalByteVersion!, TransferContentStatus.Contained, Hash(poster)));
        return a with { DerivedRepresentations = items.ToImmutable() };
    }
    private static TransferPost Post(params TransferAttachment[] media) =>
        new(new CapturedPost("15", 1, PostFieldPresence.All, Attachments: media.Select(a => a.Metadata).ToImmutableArray()),
            media.ToImmutableArray());
    private static TransferThread Thread(ImmutableArray<TransferPost> posts, params TransferRetainedBinding[] retained) =>
        new(Id, null, "synthetic", ThreadSourceState.NotFound, At, null, 0, false, posts)
        { RetainedBindings = retained.ToImmutableArray() };
    private static NativeTransferCatalog Catalog(TransferAttachment a, bool retained = false) => new(At,
        [retained ? Thread([], new TransferRetainedBinding("999", a)) : Thread([Post(a)])]);
    private static JsonObject Wire(NativeTransferCatalog catalog) => JsonNode.Parse(NativeTransferCatalogSerializer.Serialize(catalog))!.AsObject();
    private static JsonObject Current(JsonObject root) => root["threads"]![0]!["posts"]![0]!["attachments"]![0]!.AsObject();
    private static JsonObject Retained(JsonObject root) => root["threads"]![0]!["retainedBindings"]![0]!["attachment"]!.AsObject();
    private static byte[] Bytes(JsonNode root) => JsonSerializer.SerializeToUtf8Bytes(root);
    private static JsonObject Blob(byte[] bytes) => new() { ["length"] = bytes.Length, ["sha256"] = Hash(bytes) };

    // Independent ZIP/manifest composition permits malformed catalog/PNG admission tests;
    // it does not use the production serializer to construct a deliberately rejected field.
    private static byte[] Package(byte[] catalog, byte[][] payloads, string schema = "chan-v4", string[]? features = null,
        byte[]? privateBytes = null)
    {
        byte[][] unique = payloads.DistinctBy(Hash).ToArray();
        features ??= privateBytes == null ? [schema] : [schema, "meowchan-private-v1"];
        JsonObject manifest = new()
        {
            ["format"] = "meowchan-transfer", ["envelopeVersion"] = privateBytes == null ? 1 : 2,
            ["packageId"] = PackageId.ToString("D"), ["contentSchema"] = schema,
            ["requiredFeatures"] = new JsonArray(features.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
            ["catalog"] = Blob(catalog), ["payloads"] = new JsonArray(unique.Select(p => (JsonNode?)Blob(p)).ToArray())
        };
        if (privateBytes != null) manifest["privateMetadata"] = Blob(privateBytes);
        using MemoryStream output = new();
        using (ZipArchive zip = new(output, ZipArchiveMode.Create, true))
        {
            Add("manifest.json", Bytes(manifest)); Add("catalog.json", catalog);
            if (privateBytes != null) Add("private.json", privateBytes);
            foreach (byte[] payload in unique) Add("payload/" + Hash(payload), payload);
            void Add(string name, byte[] content)
            {
                ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                entry.ExternalAttributes = 0;
                using Stream target = entry.Open(); target.Write(content);
            }
        }
        return output.ToArray();
    }
    private static async Task<TransferPackageInspection> Inspect(byte[] package)
    { using MemoryStream source = new(package, false); return await NativeTransferPackageValidator.InspectAsync(source); }
    private static async Task<NativeTransferPackage> Accept(JsonObject root, params byte[][] payloads)
    {
        TransferPackageInspection result = await Inspect(Package(Bytes(root), payloads));
        Assert.True(result.IsPackageValid, result.ToString()); return result.Package!;
    }
    private static async Task Reject(JsonObject root, TransferCatalogError expected, params byte[][] payloads)
    {
        TransferPackageInspection result = await Inspect(Package(Bytes(root), payloads));
        Assert.False(result.IsPackageValid); Assert.Null(result.Package);
        Assert.Equal(TransferEnvelopeError.None, result.EnvelopeError); Assert.Equal(expected, result.CatalogError);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task OmittedOriginalRetainsExactKnownVersionAndSeparatelyBoundPreviews(bool retained, bool sha1)
    {
        TransferAttachment a = WithDerived(Media(post: retained ? "999" : "15", sha1: sha1), Preview, Poster);
        NativeTransferCatalog input = Catalog(a, retained);
        using MemoryStream output = new();
        TransferPayloadSource[] sources = new[] { Preview, Poster }.Select(p => new TransferPayloadSource(new(p.Length, Hash(p)),
            _ => Task.FromResult<Stream>(new MemoryStream(p, false)))).ToArray();
        TransferWriteResult written = await NativeTransferPackageWriter.WriteAsync(output, input, sources, PackageId);
        Assert.True(written.IsComplete, written.ToString()); Assert.True(written.Package!.IsChanV4);
        Assert.Equal("chan-v4", written.Package.Envelope.ContentSchema);
        Assert.Equal(2, written.Package.Envelope.Payloads.Length);
        Assert.DoesNotContain(written.Package.Envelope.Payloads, b => b.Sha256 == Hash(Original));
        TransferThread thread = Assert.Single(written.Package.Catalog.Threads);
        TransferAttachment parsed = retained ? Assert.Single(thread.RetainedBindings).Attachment : Assert.Single(thread.Posts[0].Attachments);
        if (retained) { Assert.Empty(thread.Posts); Assert.Equal("999", thread.RetainedBindings[0].PostId); }
        Assert.Equal(TransferContentStatus.Omitted, parsed.Status); Assert.Null(parsed.PayloadSha256); Assert.Null(parsed.History);
        Assert.Equal(a.Metadata, parsed.Metadata); Assert.Equal(a.OriginalByteVersion, parsed.OriginalByteVersion);
        Assert.Equal(a.DerivedRepresentations.ToArray(), parsed.DerivedRepresentations.ToArray());
        Assert.Equal(NativeTransferCatalogSerializer.Serialize(input), NativeTransferCatalogSerializer.Serialize(written.Package.Catalog));
        JsonObject wire = Wire(input);
        JsonObject attachment = retained ? Retained(wire) : Current(wire);
        Assert.Equal(new[] { "length", "sha256", "md5", "sha1" }, attachment["originalByteVersion"]!.AsObject().Select(p => p.Key));
        Assert.Equal(new[] { "kind", "sourceRevisionKey", "sourceByteVersion", "status", "payloadSha256" },
            attachment["derivedRepresentations"]![0]!.AsObject().Select(p => p.Key));
        Assert.Equal(!retained, attachment.ContainsKey("isDeleted"));
        foreach (string forbidden in new[] { "profileId", "operationId", "policyRevision", "tags", "plannedPath", "acceptedAtUtc" })
            Assert.DoesNotContain(forbidden, wire.ToJsonString());
    }

    [Theory]
    [InlineData("contained")] [InlineData("omitted")] [InlineData("unknown")]
    [InlineData("unavailable")] [InlineData("failed")] [InlineData("cancelled")]
    public async Task OriginalSixStatesAreIndependentOfDerivedAvailability(string originalStatus)
    {
        JsonObject root = Wire(Catalog(WithDerived(Media(), Preview)));
        JsonObject a = Current(root); a["status"] = originalStatus;
        bool contained = originalStatus == "contained";
        if (contained) a["payloadSha256"] = Hash(Original);
        NativeTransferPackage parsed = await Accept(root, contained ? [Original, Preview] : [Preview]);
        TransferAttachment item = parsed.Catalog.Threads[0].Posts[0].Attachments[0];
        Assert.Equal(originalStatus, item.Status.ToString().ToLowerInvariant());
        Assert.Equal(Version(Original), item.OriginalByteVersion); Assert.Null(item.History);
        Assert.Equal(TransferContentStatus.Contained, Assert.Single(item.DerivedRepresentations).Status);
    }

    [Theory]
    [InlineData(false, "omitted")] [InlineData(false, "unavailable")]
    [InlineData(true, "omitted")] [InlineData(true, "unavailable")]
    public async Task AbsentDerivedDoesNotDemandPhantomPayloadAndAbsentOriginalNeedsNoBytes(bool retained, string status)
    {
        JsonObject root = Wire(Catalog(WithDerived(Media(post: retained ? "999" : "15"), Preview, Poster), retained));
        JsonObject a = retained ? Retained(root) : Current(root);
        foreach (JsonNode? item in a["derivedRepresentations"]!.AsArray())
        { item!["status"] = status; item["payloadSha256"] = null; }
        NativeTransferPackage result = await Accept(root);
        Assert.Empty(result.Envelope.Payloads); Assert.True(result.IsChanV4);
    }

    [Fact]
    public async Task CurrentAndRetainedCanShareBytesButNotIdentityOrInventAHistoryPost()
    {
        TransferAttachment a = WithDerived(Media(), Preview, Poster);
        TransferAttachment b = WithDerived(Media(post: "999", mediaId: "112", position: 0), Preview, Poster);
        NativeTransferCatalog input = new(At, [Thread([Post(a)], new TransferRetainedBinding("999", b))]);
        NativeTransferPackage parsed = await Accept(Wire(input), Preview, Poster);
        Assert.Equal(2, parsed.Envelope.Payloads.Length);
        TransferThread thread = Assert.Single(parsed.Catalog.Threads);
        Assert.Equal("15", Assert.Single(thread.Posts).Metadata.PostId);
        Assert.Equal("999", Assert.Single(thread.RetainedBindings).PostId);
        Assert.NotEqual(thread.Posts[0].Attachments[0].Metadata.AttachmentRevisionKey,
            thread.RetainedBindings[0].Attachment.Metadata.AttachmentRevisionKey);
        Assert.Equal(a.OriginalByteVersion, b.OriginalByteVersion);
    }

    [Fact]
    public async Task SameNameMediaIdAndBytesAtDifferentPositionsRemainDifferentOriginalTargets()
    {
        TransferAttachment first = WithDerived(Media(position: 0), Preview);
        TransferAttachment second = WithDerived(Media(position: 1), Preview);
        NativeTransferCatalog input = new(At, [Thread([Post(first, second)])]);
        NativeTransferPackage parsed = await Accept(Wire(input), Preview);
        TransferAttachment[] items = parsed.Catalog.Threads[0].Posts[0].Attachments.ToArray();
        Assert.Equal(2, items.Length); Assert.Single(parsed.Envelope.Payloads);
        Assert.Equal(items[0].Metadata.MediaId, items[1].Metadata.MediaId);
        Assert.Equal(items[0].Metadata.OriginalFileName, items[1].Metadata.OriginalFileName);
        Assert.Equal(items[0].OriginalByteVersion, items[1].OriginalByteVersion);
        Assert.NotEqual(items[0].Metadata.AttachmentRevisionKey, items[1].Metadata.AttachmentRevisionKey);
        Assert.NotEqual(items[0].DerivedRepresentations[0].SourceRevisionKey, items[1].DerivedRepresentations[0].SourceRevisionKey);
    }

    [Fact]
    public async Task OmittedRecordedLengthIsInt64EvidenceNotAnAllocationOrPhantomPayload()
    {
        TransferAttachment a = Media();
        a = a with { OriginalByteVersion = a.OriginalByteVersion! with { Length = long.MaxValue } };
        NativeTransferPackage parsed = await Accept(Wire(Catalog(a)));
        Assert.Empty(parsed.Envelope.Payloads);
        Assert.Equal(long.MaxValue, parsed.Catalog.Threads[0].Posts[0].Attachments[0].OriginalByteVersion!.Length);
    }

    [Fact]
    public async Task EqualHashOriginalAndPreviewStillRequireARepresentationAwareConsumer()
    {
        TransferAttachment a = WithDerived(Media(status: TransferContentStatus.Contained), Original);
        NativeTransferPackage parsed = await Accept(Wire(Catalog(a)), Original);
        Assert.True(parsed.IsChanV4); Assert.Single(parsed.Envelope.Payloads);
        TransferAttachment item = parsed.Catalog.Threads[0].Posts[0].Attachments[0];
        Assert.Equal(item.PayloadSha256, item.DerivedRepresentations[0].PayloadSha256);
        Assert.Equal(TransferMediaRole.Original, item.Role); Assert.Single(item.DerivedRepresentations);
    }

    [Fact]
    public async Task EqualHashOriginalPreviewAndPosterShareBytesButKeepAllThreeReferences()
    {
        TransferAttachment a = Media(status: TransferContentStatus.Contained) with
        { OriginalByteVersion = Version(Poster), PayloadSha256 = Hash(Poster) };
        a = WithDerived(a, Poster, Poster);
        NativeTransferPackage parsed = await Accept(Wire(Catalog(a)), Poster);
        Assert.True(parsed.IsChanV4); Assert.Single(parsed.Envelope.Payloads);
        TransferAttachment item = Assert.Single(parsed.Catalog.Threads[0].Posts[0].Attachments);
        Assert.Equal(TransferContentStatus.Contained, item.Status);
        Assert.Equal(Version(Poster), item.OriginalByteVersion);
        Assert.Equal(2, item.DerivedRepresentations.Length);
        Assert.All(item.DerivedRepresentations, d =>
        {
            Assert.Equal(item.PayloadSha256, d.PayloadSha256);
            Assert.Equal(item.Metadata.AttachmentRevisionKey, d.SourceRevisionKey);
            Assert.Equal(item.OriginalByteVersion, d.SourceByteVersion);
        });
        Assert.Equal(TransferDerivedRepresentationKind.Preview, item.DerivedRepresentations[0].Kind);
        Assert.Equal(TransferDerivedRepresentationKind.VideoPosterPng, item.DerivedRepresentations[1].Kind);
        Assert.Null(item.History); // provider expectations do not authenticate these synthetic source claims
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ContainedOriginalChecksAllRecordedActualEvidenceWithoutEnrichingNullSha1(bool sha1)
    {
        TransferAttachment a = WithDerived(Media(status: TransferContentStatus.Contained, sha1: sha1), Preview);
        NativeTransferPackage parsed = await Accept(Wire(Catalog(a)), Original, Preview);
        Assert.Equal(Version(Original, sha1), parsed.Catalog.Threads[0].Posts[0].Attachments[0].OriginalByteVersion);
        Assert.Equal(Version(Original).Sha1, parsed.Envelope.MeasuredPayloads[Hash(Original)].Sha1);
    }

    [Theory]
    [InlineData("length")] [InlineData("sha256")] [InlineData("md5")] [InlineData("sha1")]
    public async Task ActualMismatchCannotBeDowngradedToPreviewSuccess(string field)
    {
        JsonObject root = Wire(Catalog(WithDerived(Media(status: TransferContentStatus.Contained), Preview)));
        JsonObject a = Current(root); JsonObject evidence = a["originalByteVersion"]!.AsObject();
        evidence[field] = field switch
        {
            "length" => JsonValue.Create(Original.Length + 1), "sha256" => JsonValue.Create(Hash(Preview)),
            "md5" => JsonValue.Create(Version(Preview).Md5), _ => JsonValue.Create(Version(Preview).Sha1)
        };
        // Keep source linkage valid so the failure is measured ORIGINAL evidence, not a dangling derivative.
        a["derivedRepresentations"]![0]!["sourceByteVersion"] = evidence.DeepClone();
        await Reject(root, TransferCatalogError.InvalidReference, Original, Preview);
    }

    [Fact]
    public async Task ProviderExpectationIsNotActualEvidenceAndContradictoryHistoryIsNeverErased()
    {
        TransferAttachment a = Media(status: TransferContentStatus.Contained);
        MediaProviderMetadata provider = new(a.Metadata.MediaId!, "original", a.Metadata.Url!, a.Metadata.OriginalFileName,
            Original.Length, a.Metadata.ExpectedMd5, new string('0', 64));
        a = a with { Metadata = a.Metadata with { ExpectedSha256 = provider.ExpectedSha256,
            AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(Id, "15", 0, provider) } };
        JsonObject root = Wire(Catalog(WithDerived(a, Preview)));
        NativeTransferPackage parsed = await Accept(root, Original, Preview);
        Assert.Equal(Hash(Original), parsed.Catalog.Threads[0].Posts[0].Attachments[0].OriginalByteVersion!.Sha256);
        Current(root)["history"] = new JsonObject { ["kind"] = "strict", ["acceptedAtUtc"] = "2026-11-06T10:00:00.0000000Z",
            ["sizeMismatch"] = false, ["md5Mismatch"] = false };
        await Reject(root, TransferCatalogError.InvalidHistory, Original, Preview);
        Assert.NotNull(Current(root)["history"]);
    }

    [Theory]
    [InlineData("missingVersion")] [InlineData("missingDerived")] [InlineData("missingDigest")]
    [InlineData("extraVersion")] [InlineData("extraDerived")] [InlineData("extraSource")]
    [InlineData("nullDerived")] [InlineData("nullEntry")] [InlineData("nullSource")]
    [InlineData("unknownKind")] [InlineData("invalidStatus")] [InlineData("previewParent")]
    [InlineData("noOriginalEvidence")] [InlineData("negativeLength")] [InlineData("fractionLength")]
    [InlineData("overflowLength")] [InlineData("uppercaseHash")] [InlineData("nullMd5")]
    [InlineData("spacedMd5")] [InlineData("noncanonicalMd5")] [InlineData("badSha1")]
    public async Task StrictV4FieldsTypesRolesAndCanonicalEvidenceReject(string mutation)
    {
        JsonObject root = Wire(Catalog(WithDerived(Media(), Preview)));
        JsonObject a = Current(root), v = a["originalByteVersion"]!.AsObject(), d = a["derivedRepresentations"]![0]!.AsObject();
        switch (mutation)
        {
            case "missingVersion": a.Remove("originalByteVersion"); break;
            case "missingDerived": a.Remove("derivedRepresentations"); break;
            case "missingDigest": v.Remove("sha1"); break;
            case "extraVersion": v["path"] = "/DO-NOT-LEAK"; break;
            case "extraDerived": d["history"] = null; break;
            case "extraSource": d["sourceByteVersion"]!["authority"] = "DO-NOT-LEAK"; break;
            case "nullDerived": a["derivedRepresentations"] = null; break;
            case "nullEntry": a["derivedRepresentations"]![0] = null; break;
            case "nullSource": d["sourceByteVersion"] = null; break;
            case "unknownKind": d["kind"] = "original"; break;
            case "invalidStatus": d["status"] = "failed"; break;
            case "previewParent": a["role"] = "preview"; break;
            case "noOriginalEvidence": a["originalByteVersion"] = null; break;
            case "negativeLength": v["length"] = -1; break;
            case "fractionLength": v["length"] = 1.25; break;
            case "overflowLength": v["length"] = JsonNode.Parse("9223372036854775808"); break;
            case "uppercaseHash": v["sha256"] = new string('A', 64); break;
            case "nullMd5": v["md5"] = null; break;
            case "spacedMd5": v["md5"] = " " + Version(Original).Md5; break;
            case "noncanonicalMd5": v["md5"] = "AAAAAAAAAAAAAAAAAAAAAB=="; break;
            case "badSha1": v["sha1"] = "not-a-sha1"; break;
        }
        await Reject(root, TransferCatalogError.InvalidField, Preview);
    }

    [Theory]
    [InlineData("sourceKey")] [InlineData("sourceLength")] [InlineData("sourceSha1Null")]
    [InlineData("payloadMissing")] [InlineData("containedNull")] [InlineData("omittedPayload")]
    [InlineData("originalMissing")] [InlineData("absentHistory")]
    public async Task SourceAndAvailabilityAreBoundExactlyWithoutNameOrHashFallback(string mutation)
    {
        JsonObject root = Wire(Catalog(WithDerived(Media(), Preview)));
        JsonObject a = Current(root), d = a["derivedRepresentations"]![0]!.AsObject();
        switch (mutation)
        {
            case "sourceKey": d["sourceRevisionKey"] = Media(post: "999").Metadata.AttachmentRevisionKey; break;
            case "sourceLength": d["sourceByteVersion"]!["length"] = Original.Length + 1; break;
            case "sourceSha1Null": d["sourceByteVersion"]!["sha1"] = null; break;
            case "payloadMissing": d["payloadSha256"] = Hash(Original); break;
            case "containedNull": d["payloadSha256"] = null; break;
            case "omittedPayload": d["status"] = "omitted"; break;
            case "originalMissing": a["status"] = "contained"; a["payloadSha256"] = Hash(Original); break;
            case "absentHistory": a["history"] = new JsonObject { ["kind"] = "strict",
                ["acceptedAtUtc"] = "2026-11-06T10:00:00.0000000Z", ["sizeMismatch"] = false, ["md5Mismatch"] = false }; break;
        }
        await Reject(root, TransferCatalogError.InvalidReference, Preview);
    }

    [Fact]
    public async Task DerivedSourceCannotEnrichAnUnknownHistoricalSha1()
    {
        JsonObject root = Wire(Catalog(WithDerived(Media(sha1: false), Preview)));
        Current(root)["derivedRepresentations"]![0]!["sourceByteVersion"]!["sha1"] = Version(Original).Sha1;
        await Reject(root, TransferCatalogError.InvalidReference, Preview);
        Assert.Null(Current(root)["originalByteVersion"]!["sha1"]);
    }

    [Theory]
    [InlineData("unkeyed")] [InlineData("wrongRevision")] [InlineData("variant")]
    public async Task NoInventedOrRekeyedOriginal(string mutation)
    {
        JsonObject root = Wire(Catalog(Media()));
        JsonObject a = Current(root);
        if (mutation == "unkeyed") a["revisionKey"] = null;
        else if (mutation == "variant") a["variant"] = "preview";
        else a["revisionKey"] = "att-v1:" + new string('0', 64);
        await Reject(root, TransferCatalogError.InvalidRevision);
    }

    [Theory]
    [InlineData("samePositionSibling")] [InlineData("duplicateKind")]
    [InlineData("reverseKinds")] [InlineData("currentRetainedDuplicate")]
    public async Task IdentityUniquenessIsNotWeakenedForRepresentations(string mutation)
    {
        JsonObject root = Wire(Catalog(WithDerived(Media(), Preview, Poster)));
        JsonObject a = Current(root); JsonArray derived = a["derivedRepresentations"]!.AsArray();
        if (mutation == "duplicateKind") derived[1] = derived[0]!.DeepClone();
        else if (mutation == "reverseKinds")
        { JsonNode p = derived[0]!.DeepClone(); derived[0] = derived[1]!.DeepClone(); derived[1] = p; }
        else if (mutation == "samePositionSibling")
        {
            JsonObject sibling = a.DeepClone().AsObject(); sibling["role"] = "preview"; sibling["revisionKey"] = null;
            sibling["originalByteVersion"] = null; sibling["derivedRepresentations"] = new JsonArray();
            root["threads"]![0]!["posts"]![0]!["attachments"]!.AsArray().Add(sibling);
        }
        else
        {
            JsonObject duplicate = a.DeepClone().AsObject(); duplicate.Remove("isDeleted");
            root["threads"]![0]!["retainedBindings"]!.AsArray().Add(new JsonObject { ["postId"] = "15", ["attachment"] = duplicate });
        }
        await Reject(root, TransferCatalogError.DuplicateIdentity, Preview, Poster);
    }

    [Theory]
    [InlineData("unknown")] [InlineData("failed")] [InlineData("cancelled")]
    public async Task RetainedAndDerivedKeepTheThreeStatusSubset(string status)
    {
        JsonObject root = Wire(Catalog(Media(post: "999"), retained: true)); Retained(root)["status"] = status;
        await Reject(root, TransferCatalogError.InvalidField);
        root = Wire(Catalog(WithDerived(Media(), Preview))); Current(root)["derivedRepresentations"]![0]!["status"] = status;
        await Reject(root, TransferCatalogError.InvalidField, Preview);
    }

    [Fact]
    public async Task ExtraPayloadAndMoreThanTwoNestedRepresentationsReject()
    {
        JsonObject root = Wire(Catalog(WithDerived(Media(), Preview, Poster)));
        await Reject(root, TransferCatalogError.InvalidReference, Preview, Poster, Original);
        Current(root)["derivedRepresentations"]!.AsArray().Add(Current(root)["derivedRepresentations"]![0]!.DeepClone());
        await Reject(root, TransferCatalogError.BudgetExceeded, Preview, Poster);
    }

    [Theory]
    [InlineData("originalByteVersion")] [InlineData("sourceRevisionKey")] [InlineData("sha256")]
    public async Task EscapedDuplicateKeysRejectRatherThanLastWriteWins(string field)
    {
        string text = Wire(Catalog(WithDerived(Media(), Preview))).ToJsonString();
        string escaped = "\\u" + ((int)field[0]).ToString("x4") + field[1..];
        text = text.Replace("\"" + field + "\":", "\"" + escaped + "\":null,\"" + field + "\":", StringComparison.Ordinal);
        TransferPackageInspection result = await Inspect(Package(Encoding.UTF8.GetBytes(text), [Preview]));
        Assert.Equal(TransferCatalogError.InvalidField, result.CatalogError); Assert.Null(result.Package);
    }

    [Theory]
    [InlineData("chan-v1")] [InlineData("chan-v2")] [InlineData("chan-v3")]
    public async Task OldVersionsNeverAdmitV4FieldsAndEmptyV4CanCanonicallyFallBack(string schema)
    {
        TransferAttachment a = Media(status: TransferContentStatus.Contained) with { OriginalByteVersion = null,
            History = schema == "chan-v3" ? null : new("strict", At, false, false) };
        NativeTransferCatalog input = schema == "chan-v2"
            ? new(At, [Thread([Post(a)], new TransferRetainedBinding("999", Media(post: "999") with { OriginalByteVersion = null }))]) : Catalog(a);
        byte[] oldBytes = NativeTransferCatalogSerializer.Serialize(input);
        Assert.Equal(schema, NativeTransferCatalogSerializer.ContentSchema(input));
        Assert.DoesNotContain("originalByteVersion", Encoding.UTF8.GetString(oldBytes));
        TransferPackageInspection old = await Inspect(Package(oldBytes, [Original], schema));
        Assert.True(old.IsPackageValid, old.ToString()); Assert.False(old.Package!.IsChanV4);
        Assert.Equal(oldBytes, NativeTransferCatalogSerializer.Serialize(old.Package.Catalog));

        JsonObject root = JsonNode.Parse(oldBytes)!.AsObject(); root["schema"] = "chan-v4";
        foreach (JsonNode? t in root["threads"]!.AsArray())
        {
            t!["retainedBindings"] ??= new JsonArray();
            foreach (JsonNode? post in t["posts"]!.AsArray())
                foreach (JsonNode? item in post!["attachments"]!.AsArray()) EmptyExtensions(item!);
            foreach (JsonNode? retained in t["retainedBindings"]!.AsArray()) EmptyExtensions(retained!["attachment"]!);
        }
        NativeTransferPackage admitted = await Accept(root, Original);
        Assert.True(admitted.IsChanV4); // consumer guard must not use canonical serializer schema!
        Assert.Equal(oldBytes, NativeTransferCatalogSerializer.Serialize(admitted.Catalog));
        root["schema"] = schema;
        if (schema == "chan-v1") foreach (JsonNode? t in root["threads"]!.AsArray()) t!.AsObject().Remove("retainedBindings");
        TransferPackageInspection wrong = await Inspect(Package(Bytes(root), [Original], schema));
        Assert.Equal(TransferCatalogError.InvalidField, wrong.CatalogError); Assert.Null(wrong.Package);
        static void EmptyExtensions(JsonNode a) { a["originalByteVersion"] = null; a["derivedRepresentations"] = new JsonArray(); }
    }

    [Fact]
    public async Task V4RequiresEmptyThreadRetainedFieldAndDoesNotUpgradeLegacyPreviewObservations()
    {
        TransferAttachment legacy = Media(mediaId: "112", position: 1) with
        { Role = TransferMediaRole.Preview, OriginalByteVersion = null };
        NativeTransferCatalog input = new(At,
            [Thread([Post(Media(), legacy)]), Thread([]) with { Id = new("4chan", "b", "7002") }]);
        JsonObject root = Wire(input);
        NativeTransferPackage parsed = await Accept(root);
        Assert.Equal(TransferMediaRole.Preview, parsed.Catalog.Threads[0].Posts[0].Attachments[1].Role);
        Assert.Null(parsed.Catalog.Threads[0].Posts[0].Attachments[1].OriginalByteVersion);
        Assert.Empty(root["threads"]![1]!["retainedBindings"]!.AsArray());
        root["threads"]![1]!.AsObject().Remove("retainedBindings");
        await Reject(root, TransferCatalogError.InvalidField);
    }

    [Theory]
    [InlineData("schemaMismatch")] [InlineData("featureMismatch")] [InlineData("duplicateFeature")] [InlineData("unknownVersion")]
    public async Task EnvelopeAndCatalogDeclareExactlyTheUnderstoodVersion(string mutation)
    {
        byte[] catalog = Bytes(Wire(Catalog(Media())));
        string schema = mutation == "schemaMismatch" ? "chan-v3" : mutation == "unknownVersion" ? "chan-v999" : "chan-v4";
        string[] features = mutation == "featureMismatch" ? ["chan-v3"] : mutation == "duplicateFeature" ? [schema, schema] : [schema];
        TransferPackageInspection result = await Inspect(Package(catalog, [], schema, features));
        Assert.False(result.IsPackageValid); Assert.Null(result.Package);
        Assert.Equal(mutation switch
        {
            "schemaMismatch" => TransferEnvelopeError.None, "unknownVersion" => TransferEnvelopeError.UnsupportedVersion,
            _ => TransferEnvelopeError.UnsupportedFeature
        }, result.EnvelopeError);
        Assert.Equal(mutation == "schemaMismatch" ? TransferCatalogError.UnsupportedSchema : TransferCatalogError.None, result.CatalogError);
    }

    [Fact]
    public async Task UnknownOriginalEvidenceAndNullAttachmentPresenceAreNotReconstructed()
    {
        TransferAttachment known = Media();
        TransferAttachment unknown = Media(mediaId: "112", position: 1) with { OriginalByteVersion = null,
            Metadata = Media(mediaId: "112", position: 1).Metadata with { AttachmentRevisionKey = null } };
        TransferPost nullPost = new(new CapturedPost("16", 2, PostFieldPresence.All, Attachments: null), []) { AttachmentsWereNull = true };
        TransferPost absentPost = new(new CapturedPost("17", 3, PostFieldPresence.None), []);
        TransferPost emptyPost = new(new CapturedPost("18", 4, PostFieldPresence.Attachments, Attachments: []), []);
        NativeTransferCatalog input = new(At, [Thread([Post(known, unknown), nullPost, absentPost, emptyPost])]);
        NativeTransferPackage result = await Accept(Wire(input));
        Assert.Null(result.Catalog.Threads[0].Posts[0].Attachments[1].OriginalByteVersion);
        Assert.Null(result.Catalog.Threads[0].Posts[0].Attachments[1].Metadata.AttachmentRevisionKey);
        Assert.True(result.Catalog.Threads[0].Posts[1].AttachmentsWereNull);
        Assert.Equal(NativeTransferCatalogSerializer.Serialize(input), NativeTransferCatalogSerializer.Serialize(result.Catalog));
    }

    [Fact]
    public void SerializerCountsEveryDerivedAliasBeforeSerializationOrOpeningPayloads()
    {
        TransferAttachment a = WithDerived(Media(), Preview, Poster);
        TransferAttachment plain = Media() with { OriginalByteVersion = null };
        TransferAttachment[] items = [.. Enumerable.Repeat(a, 33333), plain]; // 100000 references, regardless of equal hashes
        NativeTransferCatalog boundary = new(At, [Thread([Post(items)])]);
        Assert.Equal("chan-v4", NativeTransferCatalogSerializer.ContentSchema(boundary));
        NativeTransferCatalog over = new(At, [Thread([Post([.. items, plain])])]);
        Assert.Throws<InvalidDataException>(() => NativeTransferCatalogSerializer.ContentSchema(over));
        Assert.Throws<InvalidDataException>(() => NativeTransferCatalogSerializer.Serialize(over));
    }

    [Fact]
    public async Task OversizedNestedMetadataStopsBeforeAnyPayloadRead()
    {
        JsonObject root = Wire(Catalog(WithDerived(Media(), Preview, Poster)));
        JsonArray derived = Current(root)["derivedRepresentations"]!.AsArray();
        derived.Add(derived[0]!.DeepClone());
        byte[] package = Package(Bytes(root), [Preview, Poster]);
        IReadOnlyList<TransferZipEntry> directory;
        using (MemoryStream source = new(package, false)) directory = await NativeTransferZip.ReadDirectoryAsync(source, new(), default);
        foreach (TransferZipEntry entry in directory.Where(e => e.Name.StartsWith("payload/", StringComparison.Ordinal)))
        {
            using PayloadReadStream source = new(package, entry);
            TransferPackageInspection result = await NativeTransferPackageValidator.InspectAsync(source);
            Assert.Equal(TransferCatalogError.BudgetExceeded, result.CatalogError);
            Assert.Equal(0, source.PayloadBytesRead); Assert.Null(result.Package);
        }
    }

    [Fact]
    public void MetadataBudgetPreflightCountsCurrentRetainedAndNestedReferencesTogether()
    {
        // Only the shape/count preflight is exercised here. Null nested entries deliberately
        // have no semantic admission; no fake envelope measurements or catalog success result.
        JsonObject root = Wire(Catalog(Media()));
        JsonObject a = Current(root).DeepClone().AsObject();
        a["originalByteVersion"] = null; a["revisionKey"] = null;
        a["url"] = null; a["originalFileName"] = null; a["expectedSize"] = null;
        a["expectedMd5"] = null; a["expectedSha256"] = null;
        a["derivedRepresentations"] = new JsonArray((JsonNode?)null, (JsonNode?)null);
        string row = a.ToJsonString();
        root["threads"]![0]!["posts"]![0]!["attachments"] = new JsonArray();
        string skeleton = root.ToJsonString();
        string atBoundary = skeleton.Replace("\"attachments\":[]", "\"attachments\":[" +
            string.Join(",", Enumerable.Repeat(row, 33333)) + "]", StringComparison.Ordinal);
        Assert.Equal(TransferCatalogError.None, NativeTransferCatalogValidator.PreflightV4ReferenceBudget(Encoding.UTF8.GetBytes(atBoundary), default));
        JsonObject retained = a.DeepClone().AsObject(); retained.Remove("isDeleted");
        string over = atBoundary.Replace("\"retainedBindings\":[]", "\"retainedBindings\":[" +
            new JsonObject { ["postId"] = "999", ["attachment"] = retained }.ToJsonString() + "]", StringComparison.Ordinal);
        byte[] bytes = Encoding.UTF8.GetBytes(over);
        Assert.True(bytes.Length < 16 * 1024 * 1024);
        Assert.Equal(TransferCatalogError.BudgetExceeded, NativeTransferCatalogValidator.PreflightV4ReferenceBudget(bytes, default));
    }

    [Fact]
    public async Task SerializerRejectsUninitializedOrInconsistentRepresentationsBeforePayloadIo()
    {
        TransferAttachment a = WithDerived(Media(), Preview);
        TransferAttachment[] invalid =
        [
            a with { DerivedRepresentations = default },
            a with { OriginalByteVersion = null },
            a with { Role = TransferMediaRole.Preview },
            a with { DerivedRepresentations = [a.DerivedRepresentations[0], a.DerivedRepresentations[0]] },
            a with { DerivedRepresentations = [a.DerivedRepresentations[0] with { SourceRevisionKey = "invented" }] },
            a with { DerivedRepresentations = [a.DerivedRepresentations[0] with { SourceByteVersion = Version(Preview) }] },
            a with { OriginalByteVersion = a.OriginalByteVersion! with { Md5 = "AAAAAAAAAAAAAAAAAAAAAB==" } },
            WithDerived(Media(status: TransferContentStatus.Contained) with { PayloadSha256 = Hash(Preview) }, Preview)
        ];
        foreach (TransferAttachment item in invalid)
        {
            int opens = 0;
            TransferPayloadSource source = new(new(Preview.Length, Hash(Preview)), _ =>
            { opens++; return Task.FromResult<Stream>(new MemoryStream(Preview, false)); });
            using MemoryStream output = new();
            TransferWriteResult result = await NativeTransferPackageWriter.WriteAsync(output, Catalog(item), [source], PackageId);
            Assert.Equal(TransferWriteError.InvalidCatalog, result.Error); Assert.Equal(0, opens); Assert.Equal(0, output.Length);
        }
    }

    [Theory]
    [InlineData("signatureOnly")] [InlineData("badSignature")] [InlineData("missingHeader")]
    [InlineData("duplicateHeader")] [InlineData("zeroWidth")] [InlineData("hugeHeight")]
    [InlineData("badDepth")] [InlineData("badColor")] [InlineData("compression")]
    [InlineData("filter")] [InlineData("interlace")] [InlineData("chunkOverflow")]
    [InlineData("badType")] [InlineData("reservedTypeBit")] [InlineData("unknownCritical")]
    [InlineData("missingData")] [InlineData("emptyData")] [InlineData("missingEnd")]
    [InlineData("endPayload")] [InlineData("trailingBytes")] [InlineData("truncated")]
    [InlineData("nonconsecutiveData")] [InlineData("paletteRequired")] [InlineData("paletteForbidden")]
    [InlineData("paletteMalformed")] [InlineData("paletteDuplicate")] [InlineData("paletteLate")]
    [InlineData("paletteTooLargeForDepth")] [InlineData("headerCrc")] [InlineData("dataCrc")]
    [InlineData("ancillaryCrc")] [InlineData("endCrc")]
    public async Task PosterRequiresPngCriticalStructureAndEveryChunkCrc(string mutation)
    {
        byte[] bytes = BadPng(mutation);
        JsonObject root = Wire(Catalog(WithDerived(Media(), poster: bytes)));
        byte[] package = Package(Bytes(root), [bytes]);
        using (MemoryStream envelopeSource = new(package, false))
            Assert.True((await NativeTransferEnvelopeValidator.InspectAsync(envelopeSource)).IsEnvelopeValid);
        // Envelope integrity is real but insufficient for the declared PNG role.
        TransferPackageInspection result = await Inspect(package);
        Assert.Equal(TransferEnvelopeError.None, result.EnvelopeError);
        Assert.Equal(TransferCatalogError.InvalidDerivedPayload, result.CatalogError); Assert.Null(result.Package);
        Current(root)["derivedRepresentations"]![0]!["kind"] = "preview";
        await Accept(root, bytes); // no PNG/decoder promise on ordinary preview payloads
    }

    [Theory]
    [InlineData(1)] [InlineData(3)] [InlineData(7)] [InlineData(65536)]
    public async Task PngParserHandlesSplitTokensLargeAncillaryDataAndRestoresCallerPosition(int maximumRead)
    {
        byte[] png = Png(Chunk("IHDR", Header()), Chunk("teST", new byte[70000]),
            Chunk("IDAT", IdatData[..5]), Chunk("IDAT", IdatData[5..]), Chunk("IEND", []));
        byte[] package = Package(Bytes(Wire(Catalog(WithDerived(Media(), poster: png)))), [png]);
        using ShortReadStream source = new(package, maximumRead) { Position = 9 };
        TransferPackageInspection result = await NativeTransferPackageValidator.InspectAsync(source);
        Assert.True(result.IsPackageValid, result.ToString()); Assert.Equal(9, source.Position); Assert.True(source.CanRead);
    }

    [Fact]
    public async Task ValidIndexedPaletteAndInterlacedHeaderAreStructuralNotDecodedClaims()
    {
        byte[] png = Png(Chunk("IHDR", Header(depth: 1, color: 3, interlace: 1)), Chunk("PLTE", [0, 0, 0, 255, 255, 255]),
            Chunk("IDAT", IdatData), Chunk("IEND", []));
        await Accept(Wire(Catalog(WithDerived(Media(), poster: png))), png);
    }

    [Fact]
    public async Task PosterValidationUsesExactlyTheAlreadyBudgetedPayloadRead()
    {
        byte[] package = Package(Bytes(Wire(Catalog(WithDerived(Media(), Preview, Poster)))), [Preview, Poster]);
        IReadOnlyList<TransferZipEntry> directory;
        using (MemoryStream source = new(package, false)) directory = await NativeTransferZip.ReadDirectoryAsync(source, new(), default);
        TransferZipEntry posterEntry = directory.Single(e => e.Name == "payload/" + Hash(Poster));
        using PayloadReadStream monitored = new(package, posterEntry);
        TransferPackageInspection result = await NativeTransferPackageValidator.InspectAsync(monitored);
        Assert.True(result.IsPackageValid, result.ToString()); Assert.Equal(Poster.Length, monitored.PayloadBytesRead);
        Assert.Equal(0, monitored.Position);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task InvalidPngStillRequiresTheEntireMeasuredPayloadAndPreservesEnvelopeFailures(bool corruptZipBytes)
    {
        // Invalid at the signature, with enough remaining bytes to require multiple reads.
        // PNG rejection must not truncate measurement or hide a later ZIP CRC failure.
        byte[] invalid = new byte[140000];
        byte[] package = Package(Bytes(Wire(Catalog(WithDerived(Media(), poster: invalid)))), [invalid]);
        IReadOnlyList<TransferZipEntry> directory;
        using (MemoryStream source = new(package, false)) directory = await NativeTransferZip.ReadDirectoryAsync(source, new(), default);
        TransferZipEntry entry = directory.Single(e => e.Name == "payload/" + Hash(invalid));
        if (corruptZipBytes) package[checked((int)(entry.DataOffset + entry.Length - 1))] ^= 1;
        using PayloadReadStream monitored = new(package, entry) { Position = 13 };
        TransferPackageInspection result = await NativeTransferPackageValidator.InspectAsync(monitored);
        Assert.Null(result.Package);
        Assert.Equal(invalid.Length, monitored.PayloadBytesRead); Assert.Equal(13, monitored.Position);
        Assert.Equal(corruptZipBytes ? TransferEnvelopeError.InvalidZip : TransferEnvelopeError.None, result.EnvelopeError);
        Assert.Equal(corruptZipBytes ? TransferCatalogError.None : TransferCatalogError.InvalidDerivedPayload, result.CatalogError);
    }

    [Fact]
    public async Task PayloadLimitsAndCancellationRemainRealNotPngSuccessFallbacks()
    {
        NativeTransferCatalog catalog = Catalog(WithDerived(Media(), poster: Poster));
        byte[] package = Package(Bytes(Wire(catalog)), [Poster]);
        using (MemoryStream source = new(package, false))
        {
            TransferPackageInspection limited = await NativeTransferPackageValidator.InspectAsync(source,
                limits: new() { PayloadBytes = Poster.Length - 1 });
            Assert.Equal(TransferEnvelopeError.PayloadLimit, limited.EnvelopeError); Assert.Null(limited.Package);
        }
        using CancellationTokenSource before = new(); before.Cancel();
        Assert.Throws<OperationCanceledException>(() => NativeTransferCatalogSerializer.Serialize(catalog, before.Token));
        using (MemoryStream source = new(package, false))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeTransferPackageValidator.InspectAsync(source, before.Token));
        IReadOnlyList<TransferZipEntry> directory;
        using (MemoryStream source = new(package, false)) directory = await NativeTransferZip.ReadDirectoryAsync(source, new(), default);
        using CancellationTokenSource during = new();
        using PayloadReadStream monitored = new(package, directory.Single(e => e.Name.StartsWith("payload/", StringComparison.Ordinal)), during)
        { Position = 11 };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeTransferPackageValidator.InspectAsync(monitored, during.Token));
        Assert.True(monitored.PayloadBytesRead > 0); Assert.Equal(11, monitored.Position);
    }

    [Fact]
    public async Task PrivateV1CompositionWorksButAbsentOriginalEvidenceDoesNotBroadenFavoriteBinding()
    {
        JsonObject identity = new() { ["source"] = Id.Source, ["board"] = Id.Board, ["thread"] = Id.Thread };
        JsonObject privateRoot = new()
        {
            ["schema"] = "meowchan-private-v1", ["threads"] = new JsonArray(identity.DeepClone()),
            ["threadFavorites"] = new JsonArray(identity.DeepClone()), ["mediaFavorites"] = null, ["postBookmarksAndNotes"] = null
        };
        byte[] catalog = Bytes(Wire(Catalog(Media())));
        TransferPackageInspection good = await Inspect(Package(catalog, [], privateBytes: Bytes(privateRoot)));
        Assert.True(good.IsPackageValid, good.ToString()); Assert.True(good.Package!.IsChanV4);
        Assert.Equal(2, good.Package.Envelope.EnvelopeVersion); Assert.NotNull(good.Package.PrivateMetadata);
        privateRoot["mediaFavorites"] = new JsonArray(new JsonObject { ["thread"] = identity.DeepClone(),
            ["mediaId"] = "111", ["variant"] = "original", ["sha256"] = Hash(Original) });
        TransferPackageInspection missing = await Inspect(Package(catalog, [], privateBytes: Bytes(privateRoot)));
        Assert.Equal(NativePrivateMetadataCodecError.OutOfScope, missing.PrivateMetadataError); Assert.Null(missing.Package);
        byte[] aliasCatalog = Bytes(Wire(Catalog(WithDerived(Media(), Original))));
        TransferPackageInspection alias = await Inspect(Package(aliasCatalog, [Original], privateBytes: Bytes(privateRoot)));
        Assert.Equal(NativePrivateMetadataCodecError.OutOfScope, alias.PrivateMetadataError); Assert.Null(alias.Package);
        TransferPackageInspection feature = await Inspect(Package(catalog, [], features: ["chan-v4"], privateBytes: Bytes(privateRoot)));
        Assert.Equal(TransferEnvelopeError.UnsupportedFeature, feature.EnvelopeError);
    }

    private static byte[] Header(uint width = 1, uint height = 1, byte depth = 8, byte color = 0,
        byte compression = 0, byte filter = 0, byte interlace = 0)
    {
        byte[] result = new byte[13]; BinaryPrimitives.WriteUInt32BigEndian(result, width);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), height);
        result[8] = depth; result[9] = color; result[10] = compression; result[11] = filter; result[12] = interlace;
        return result;
    }
    private static byte[] Chunk(string type, byte[] data)
    {
        byte[] bytes = new byte[data.Length + 12]; BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)data.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(bytes, 4); data.CopyTo(bytes, 8);
        // Independent bitwise implementation; do not derive expected CRCs using production code.
        uint crc = uint.MaxValue;
        foreach (byte b in bytes.AsSpan(4, 4 + data.Length))
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
        }
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(bytes.Length - 4), ~crc); return bytes;
    }
    private static byte[] Png(params byte[][] chunks) => [.. PngSignature, .. chunks.SelectMany(b => b)];
    private static byte[] BadPng(string mutation)
    {
        byte[] header = Chunk("IHDR", Header()), data = Chunk("IDAT", IdatData), end = Chunk("IEND", []);
        byte[] palette = Chunk("PLTE", [0, 0, 0]);
        switch (mutation)
        {
            case "signatureOnly": return [.. PngSignature];
            case "badSignature": return [0, .. Poster[1..]];
            case "missingHeader": return Png(data, end);
            case "duplicateHeader": return Png(header, header, data, end);
            case "zeroWidth": return Png(Chunk("IHDR", Header(width: 0)), data, end);
            case "hugeHeight": return Png(Chunk("IHDR", Header(height: uint.MaxValue)), data, end);
            case "badDepth": return Png(Chunk("IHDR", Header(depth: 3)), data, end);
            case "badColor": return Png(Chunk("IHDR", Header(color: 1)), data, end);
            case "compression": return Png(Chunk("IHDR", Header(compression: 1)), data, end);
            case "filter": return Png(Chunk("IHDR", Header(filter: 1)), data, end);
            case "interlace": return Png(Chunk("IHDR", Header(interlace: 2)), data, end);
            case "chunkOverflow": BinaryPrimitives.WriteUInt32BigEndian(data, uint.MaxValue); return Png(header, data, end);
            case "badType": return Png(header, Chunk("t1ST", []), data, end);
            case "reservedTypeBit": return Png(header, Chunk("test", []), data, end);
            case "unknownCritical": return Png(header, Chunk("ABCD", []), data, end);
            case "missingData": return Png(header, end);
            case "emptyData": return Png(header, Chunk("IDAT", []), end);
            case "missingEnd": return Png(header, data);
            case "endPayload": return Png(header, data, Chunk("IEND", [1]));
            case "trailingBytes": return [.. Poster, 0];
            case "truncated": return Poster[..^1];
            case "nonconsecutiveData": return Png(header, data, Chunk("teST", []), data, end);
            case "paletteRequired": return Png(Chunk("IHDR", Header(color: 3)), data, end);
            case "paletteForbidden": return Png(header, palette, data, end);
            case "paletteMalformed": return Png(Chunk("IHDR", Header(color: 3)), Chunk("PLTE", [1]), data, end);
            case "paletteDuplicate": return Png(Chunk("IHDR", Header(color: 3)), palette, palette, data, end);
            case "paletteLate": return Png(Chunk("IHDR", Header(color: 2)), data, palette, end);
            case "paletteTooLargeForDepth": return Png(Chunk("IHDR", Header(depth: 1, color: 3)), Chunk("PLTE", new byte[9]), data, end);
            case "headerCrc": header[^1] ^= 1; return Png(header, data, end);
            case "dataCrc": data[^1] ^= 1; return Png(header, data, end);
            case "ancillaryCrc": byte[] ancillary = Chunk("teST", [1, 2]); ancillary[^1] ^= 1; return Png(header, ancillary, data, end);
            case "endCrc": end[^1] ^= 1; return Png(header, data, end);
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
    }
    private sealed class ShortReadStream(byte[] bytes, int maxRead) : MemoryStream(bytes, false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, maxRead)], cancellationToken);
    }
    private sealed class PayloadReadStream(byte[] bytes, TransferZipEntry payload, CancellationTokenSource? cancel = null) : MemoryStream(bytes, false)
    {
        internal long PayloadBytesRead { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            long start = Position;
            int read = await base.ReadAsync(buffer, cancellationToken);
            long overlap = Math.Max(0, Math.Min(start + read, payload.DataOffset + payload.Length) - Math.Max(start, payload.DataOffset));
            PayloadBytesRead += overlap;
            if (PayloadBytesRead > payload.Length) throw new IOException("Unexpected second payload read.");
            if (overlap != 0) cancel?.Cancel();
            return read;
        }
    }
}

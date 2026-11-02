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

// Independent codec/package tests for the bounded retained-binding extension. All bytes are synthetic.
public sealed class NativeTransferRetainedCodecTests
{
    private static readonly DateTimeOffset When = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly ResourceId Id = new("4chan", "b", "7001");
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("synthetic retained revision payload");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static TransferAttachment Media(string mediaId, string postId, int position, bool mismatch = false,
        bool contained = true)
    {
        string url = $"https://i.4cdn.org/b/{mediaId}.jpg";
        string actualMd5 = Convert.ToBase64String(MD5.HashData(Payload));
        string actualSha1 = Convert.ToBase64String(SHA1.HashData(Payload));
        MediaProviderSizeHint hint = new("1.50", "KiB", MediaSizePrecision.Truncated);
        MediaProviderMetadata provider = new(mediaId, "original", url, mediaId + ".jpg", Payload.Length,
            mismatch ? Convert.ToBase64String(new byte[16]) : actualMd5, Hash(Payload))
        { ExpectedSha1 = actualSha1, ProviderSizeHint = hint };
        CapturedAttachment metadata = new(position, mediaId, "original", url, provider.OriginalFileName,
            ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
        {
            ExpectedSha1 = provider.ExpectedSha1, ExpectedSha256 = provider.ExpectedSha256,
            ProviderSizeHint = hint, AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(Id, postId, position, provider)
        };
        return new(metadata, TransferMediaRole.Original,
            contained ? TransferContentStatus.Contained : TransferContentStatus.Unavailable,
            contained ? Hash(Payload) : null,
            contained ? new(mismatch ? "provider-mismatch" : "strict", When, false, mismatch) : null);
    }

    private static TransferPost CurrentPost() => Media("111", "15", 0) is { } media
        ? new(new CapturedPost("15", 1, PostFieldPresence.All, Attachments: [media.Metadata]), [media])
        : throw new InvalidOperationException();

    private static TransferThread Thread(ImmutableArray<TransferPost> posts,
        params TransferRetainedBinding[] retained) => new(Id, null, "synthetic", ThreadSourceState.NotFound,
        When, null, 0, false, posts) { RetainedBindings = retained.ToImmutableArray() };

    private static NativeTransferCatalog V2() => new(When,
    [
        Thread([CurrentPost()], new("999", Media("112", "999", 7)),
            new("999", Media("113", "999", 7, mismatch: true))),
        Thread([]) with { Id = new("4chan", "b", "7002") }
    ]);

    private static NativeTransferCatalog V1() => new(When, [Thread([CurrentPost()])]);

    private static byte[] V2Bytes() => NativeTransferCatalogSerializer.Serialize(V2());

    private static TransferCatalogInspection Inspect(byte[] bytes, string? schema = null, params byte[][] payloads)
    {
        schema ??= JsonNode.Parse(bytes)!["schema"]!.GetValue<string>();
        NativeTransferEnvelope envelope = new(Guid.NewGuid().ToString("D"),
            schema, "unused", new(bytes.Length, Hash(bytes)),
            payloads.Select(Hash).Distinct(StringComparer.Ordinal).Select(hash => new TransferBlobDescriptor(Payload.Length, hash)).ToImmutableArray())
        {
            CatalogBytes = bytes.ToImmutableArray(),
            MeasuredPayloads = payloads.Select(Hash).Distinct(StringComparer.Ordinal).ToImmutableDictionary(hash => hash,
                hash => new TransferMeasuredBlob(Payload.Length, hash, Convert.ToBase64String(MD5.HashData(Payload)),
                    Convert.ToBase64String(SHA1.HashData(Payload))), StringComparer.Ordinal)
        };
        return NativeTransferCatalogValidator.Inspect(bytes, envelope);
    }

    private static NativeTransferCatalog RoundTrip(NativeTransferCatalog input, params byte[][] payloads)
    {
        byte[] bytes = NativeTransferCatalogSerializer.Serialize(input);
        TransferCatalogInspection result = Inspect(bytes, payloads: payloads);
        Assert.True(result.IsCatalogValid, result.Error.ToString());
        Assert.Equal(bytes, NativeTransferCatalogSerializer.Serialize(result.Catalog!));
        return result.Catalog!;
    }

    private static async Task<TransferPackageInspection> InspectPackageAsync(byte[] catalogBytes, byte[][] payloads,
        string? manifestSchema = null, string[]? features = null)
    {
        JsonNode root = JsonNode.Parse(catalogBytes)!;
        string schema = manifestSchema ?? root["schema"]!.GetValue<string>();
        features ??= [schema];
        JsonObject Blob(byte[] bytes) => new() { ["length"] = bytes.Length, ["sha256"] = Hash(bytes) };
        JsonObject manifest = new()
        {
            ["format"] = "meowchan-transfer", ["envelopeVersion"] = 1, ["packageId"] = Guid.NewGuid().ToString("D"),
            ["contentSchema"] = schema, ["requiredFeatures"] = new JsonArray(features.Select(f => (JsonNode?)f).ToArray()),
            ["catalog"] = Blob(catalogBytes),
            ["payloads"] = new JsonArray(payloads.Select(Hash).Distinct(StringComparer.Ordinal)
                .Select(hash => (JsonNode?)new JsonObject { ["length"] = Payload.Length, ["sha256"] = hash }).ToArray())
        };
        using MemoryStream stream = new();
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create, true))
        {
            Add("manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest));
            Add("catalog.json", catalogBytes);
            foreach (string hash in payloads.Select(Hash).Distinct(StringComparer.Ordinal)) Add("payload/" + hash, Payload);
            void Add(string name, byte[] content)
            {
                using Stream entry = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                entry.Write(content);
            }
        }
        stream.Position = 0;
        return await NativeTransferPackageValidator.InspectAsync(stream);
    }

    private static void Reject(NativeTransferCatalog catalog) => Assert.Throws<InvalidDataException>(
        () => NativeTransferCatalogSerializer.Serialize(catalog));

    [Fact]
    public void RetainedV2RoundTripsRepeatedPositionsAndEqualByteDistinctRevisions()
    {
        NativeTransferCatalog input = V2(), parsed = RoundTrip(input, Payload);
        byte[] wire = NativeTransferCatalogSerializer.Serialize(input);
        Assert.Equal("chan-v2", JsonNode.Parse(wire)!["schema"]!.GetValue<string>());
        Assert.Equal("chan-v2", NativeTransferCatalogSerializer.ContentSchema(input));

        JsonArray first = (JsonArray)JsonNode.Parse(wire)!["threads"]![0]!["retainedBindings"]!;
        JsonArray sibling = (JsonArray)JsonNode.Parse(wire)!["threads"]![1]!["retainedBindings"]!;
        Assert.Equal(2, first.Count);
        Assert.Empty(sibling);
        Assert.Equal(new[] { "postId", "attachment" }, first[0]!.AsObject().Select(p => p.Key));
        Assert.Equal(new[] { "position", "mediaId", "variant", "url", "originalFileName", "expectedSize", "expectedMd5",
            "expectedSha1", "expectedSha256", "sizeHint", "revisionKey", "role", "status", "payloadSha256", "history" },
            first[0]!["attachment"]!.AsObject().Select(p => p.Key));
        Assert.Equal(new[] { "kind", "acceptedAtUtc", "sizeMismatch", "md5Mismatch" },
            first[0]!["attachment"]!["history"]!.AsObject().Select(p => p.Key));
        Assert.DoesNotContain("isDeleted", first[0]!["attachment"]!.ToJsonString());
        Assert.DoesNotContain("plannedPath", first[0]!["attachment"]!.ToJsonString());
        Assert.DoesNotContain("policyRevision", first[0]!["attachment"]!.ToJsonString());

        TransferThread thread = parsed.Threads[0];
        Assert.Equal(new[] { "15" }, thread.Posts.Select(p => p.Metadata.PostId));
        Assert.Equal(new[] { 7, 7 }, thread.RetainedBindings.Select(b => b.Attachment.Metadata.Position));
        Assert.Equal(new[] { "112", "113" }, thread.RetainedBindings.Select(b => b.Attachment.Metadata.MediaId));
        Assert.All(thread.RetainedBindings, b => Assert.Equal(Hash(Payload), b.Attachment.PayloadSha256));
        Assert.Equal("strict", thread.RetainedBindings[0].Attachment.History!.Kind);
        Assert.Equal("provider-mismatch", thread.RetainedBindings[1].Attachment.History!.Kind);
        Assert.True(thread.RetainedBindings[1].Attachment.History!.Md5Mismatch);
        Assert.All(thread.RetainedBindings.Select(b => b.Attachment), a =>
        {
            Assert.False(a.Metadata.IsDeleted); Assert.Null(a.Metadata.PlannedPath);
            Assert.Equal("original", a.Metadata.Variant); Assert.Equal(TransferMediaRole.Original, a.Role);
            Assert.StartsWith("att-v2:", a.Metadata.AttachmentRevisionKey);
        });
        Assert.Equal(input.Threads[0].RetainedBindings.ToArray(), thread.RetainedBindings.ToArray());
        Assert.Empty(parsed.Threads[1].RetainedBindings);
    }

    [Fact]
    public void AllEmptyIncomingV2IsAdmittedThenCanonicallyDowngradedToSemanticV1()
    {
        byte[] v1 = NativeTransferCatalogSerializer.Serialize(V1());
        JsonNode root = JsonNode.Parse(v1)!;
        root["schema"] = "chan-v2";
        foreach (JsonNode? thread in root["threads"]!.AsArray()) thread!.AsObject()["retainedBindings"] = new JsonArray();
        byte[] incoming = JsonSerializer.SerializeToUtf8Bytes(root);
        TransferCatalogInspection result = Inspect(incoming, "chan-v2", Payload);
        Assert.True(result.IsCatalogValid, result.Error.ToString());
        byte[] canonical = NativeTransferCatalogSerializer.Serialize(result.Catalog!);
        Assert.Equal("chan-v1", JsonNode.Parse(canonical)!["schema"]!.GetValue<string>());
        Assert.DoesNotContain("retainedBindings", Encoding.UTF8.GetString(canonical));
        Assert.Equal(v1, canonical);
        Assert.NotEqual(incoming, canonical);
    }

    [Fact]
    public void InternallyEmptyRetainedSelectionAlsoEmitsV1()
    {
        NativeTransferCatalog input = V1();
        Assert.Equal("chan-v1", NativeTransferCatalogSerializer.ContentSchema(input));
        byte[] wire = NativeTransferCatalogSerializer.Serialize(input);
        Assert.DoesNotContain("retainedBindings", Encoding.UTF8.GetString(wire));
        Assert.Equal(wire, NativeTransferCatalogSerializer.Serialize(RoundTrip(input, Payload)));
    }

    [Fact]
    public async Task RetainedUnavailableAndOmittedHaveNoPayloadOrHistory()
    {
        TransferAttachment unavailable = Media("114", "998", 3, contained: true) with
        { Status = TransferContentStatus.Unavailable, PayloadSha256 = null, History = null };
        TransferAttachment omitted = Media("115", "997", 3, contained: true) with
        { Status = TransferContentStatus.Omitted, PayloadSha256 = null, History = null };
        NativeTransferCatalog input = new(When, [Thread([], new("998", unavailable), new("997", omitted))]);
        NativeTransferCatalog parsed = RoundTrip(input);
        Assert.Equal(new[] { TransferContentStatus.Unavailable, TransferContentStatus.Omitted },
            parsed.Threads[0].RetainedBindings.Select(b => b.Attachment.Status));
        Assert.All(parsed.Threads[0].RetainedBindings.Select(b => b.Attachment),
            a => { Assert.Null(a.PayloadSha256); Assert.Null(a.History); });
        using MemoryStream stage = new();
        Assert.True((await NativeTransferPackageWriter.WriteAsync(stage, input, [], Guid.NewGuid())).IsComplete);
    }

    [Fact]
    public async Task ActualPackageWriterDeclaresV2AndUsesOneMeasuredPayloadForDistinctReferences()
    {
        using MemoryStream stage = new();
        TransferPayloadSource source = new(new(Payload.Length, Hash(Payload)),
            _ => Task.FromResult<Stream>(new MemoryStream(Payload, false)));
        TransferWriteResult result = await NativeTransferPackageWriter.WriteAsync(stage, V2(), [source], Guid.NewGuid());
        Assert.True(result.IsComplete, result.Error.ToString());
        Assert.Equal("chan-v2", result.Package!.Envelope.ContentSchema);
        Assert.Equal("chan-v2", NativeTransferCatalogSerializer.ContentSchema(result.Package.Catalog));
        Assert.Single(result.Package.Envelope.Payloads);
        Assert.Equal(V2Bytes(), NativeTransferCatalogSerializer.Serialize(result.Package.Catalog));

        stage.Position = 0;
        using ZipArchive zip = new(stage, ZipArchiveMode.Read);
        using Stream manifestStream = zip.GetEntry("manifest.json")!.Open();
        using MemoryStream manifestBytes = new();
        manifestStream.CopyTo(manifestBytes);
        JsonNode node = JsonNode.Parse(manifestBytes.ToArray())!;
        Assert.Equal("chan-v2", node["contentSchema"]!.GetValue<string>());
        Assert.Equal(new[] { "chan-v2" }, node["requiredFeatures"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Theory]
    [InlineData("missingNestedField", (int)TransferCatalogError.InvalidField)]
    [InlineData("extraDeletionField", (int)TransferCatalogError.InvalidField)]
    [InlineData("extraNestedHintField", (int)TransferCatalogError.InvalidField)]
    [InlineData("missingHistoryDate", (int)TransferCatalogError.InvalidField)]
    [InlineData("missingSiblingRetained", (int)TransferCatalogError.InvalidField)]
    [InlineData("duplicateRetained", (int)TransferCatalogError.DuplicateIdentity)]
    [InlineData("duplicateCurrentRetained", (int)TransferCatalogError.DuplicateIdentity)]
    [InlineData("invalidPayloadReference", (int)TransferCatalogError.InvalidReference)]
    [InlineData("unsafeUrl", (int)TransferCatalogError.UnsafeUrl)]
    [InlineData("wrongRevision", (int)TransferCatalogError.InvalidRevision)]
    [InlineData("wrongHistoryKind", (int)TransferCatalogError.InvalidHistory)]
    public async Task IndependentJsonMutationsRejectAtTheExactBoundary(string mode, int expected)
    {
        JsonNode root = JsonNode.Parse(V2Bytes())!;
        JsonObject first = (JsonObject)root["threads"]![0]!;
        JsonArray retained = (JsonArray)first["retainedBindings"]!;
        JsonObject attachment = (JsonObject)retained[0]!["attachment"]!;
        switch (mode)
        {
            case "missingNestedField": attachment.Remove("mediaId"); break;
            case "extraDeletionField": attachment.Add("isDeleted", false); break;
            case "extraNestedHintField": attachment["sizeHint"]!.AsObject().Add("policyRevision", 7); break;
            case "missingHistoryDate": attachment["history"]!.AsObject().Remove("acceptedAtUtc"); break;
            case "missingSiblingRetained": ((JsonObject)root["threads"]![1]!).Remove("retainedBindings"); break;
            case "duplicateRetained": retained.Add(JsonNode.Parse(retained[0]!.ToJsonString())); break;
            case "duplicateCurrentRetained":
                JsonObject current = (JsonObject)first["posts"]![0]!["attachments"]![0]!.DeepClone();
                current.Remove("isDeleted");
                retained.Add(new JsonObject { ["postId"] = "15", ["attachment"] = JsonNode.Parse(current.ToJsonString()) });
                break;
            case "invalidPayloadReference": attachment["payloadSha256"] = new string('0', 64); break;
            case "unsafeUrl":
                string unsafeUrl = "https://user:secret@i.4cdn.org/b/112.jpg";
                attachment["url"] = unsafeUrl;
                attachment["revisionKey"] = MediaRevisionKeys.CreateAttachmentKey(Id, "999", 7,
                    new("112", "original", unsafeUrl, "112.jpg", Payload.Length,
                        Convert.ToBase64String(MD5.HashData(Payload)), Hash(Payload))
                    { ExpectedSha1 = Convert.ToBase64String(SHA1.HashData(Payload)), ProviderSizeHint = new("1.50", "KiB", MediaSizePrecision.Truncated) });
                break;
            case "wrongRevision": attachment["mediaId"] = "changed-without-rekey"; break;
            case "wrongHistoryKind": attachment["history"]!["kind"] = "provider-mismatch"; break;
        }
        TransferPackageInspection result = await InspectPackageAsync(JsonSerializer.SerializeToUtf8Bytes(root), [Payload]);
        Assert.False(result.IsPackageValid);
        Assert.Equal(TransferEnvelopeError.None, result.EnvelopeError);
        Assert.Equal((TransferCatalogError)expected, result.CatalogError);
    }

    [Theory]
    [InlineData("manifestV1WithV2Catalog", "chan-v1", new[] { "chan-v1" }, false, (int)TransferEnvelopeError.None, (int)TransferCatalogError.UnsupportedSchema)]
    [InlineData("featureMismatch", "chan-v2", new[] { "chan-v1" }, false, (int)TransferEnvelopeError.UnsupportedFeature, (int)TransferCatalogError.None)]
    [InlineData("featureNotSingleton", "chan-v2", new[] { "chan-v2", "chan-v2" }, false, (int)TransferEnvelopeError.UnsupportedFeature, (int)TransferCatalogError.None)]
    [InlineData("unknownSchema", "chan-v4", new[] { "chan-v4" }, false, (int)TransferEnvelopeError.UnsupportedVersion, (int)TransferCatalogError.None)]
    [InlineData("catalogV1WithV2Envelope", "chan-v2", new[] { "chan-v2" }, true, (int)TransferEnvelopeError.None, (int)TransferCatalogError.UnsupportedSchema)]
    public async Task CatalogSchemaAndSingletonRequiredFeatureMustMatch(string mode, string manifestSchema,
        string[] features, bool rewriteCatalogV1, int envelopeError, int catalogError)
    {
        _ = mode; // xUnit supplies a diagnostic name; each mutation is independent below.
        byte[] catalog = V2Bytes();
        if (rewriteCatalogV1)
        {
            JsonNode root = JsonNode.Parse(catalog)!;
            root["schema"] = "chan-v1";
            catalog = JsonSerializer.SerializeToUtf8Bytes(root);
        }
        TransferPackageInspection result = await InspectPackageAsync(catalog, [Payload], manifestSchema, features);
        Assert.Equal((TransferEnvelopeError)envelopeError, result.EnvelopeError);
        Assert.Equal((TransferCatalogError)catalogError, result.CatalogError);
        Assert.False(result.IsPackageValid);
    }

    [Fact]
    public async Task ActualWriterStillEmitsV1WithoutRetainedBindings()
    {
        using MemoryStream stage = new();
        TransferPayloadSource source = new(new(Payload.Length, Hash(Payload)),
            _ => Task.FromResult<Stream>(new MemoryStream(Payload, false)));
        TransferWriteResult result = await NativeTransferPackageWriter.WriteAsync(stage, V1(), [source], Guid.NewGuid());
        Assert.True(result.IsComplete, result.Error.ToString());
        Assert.Equal("chan-v1", result.Package!.Envelope.ContentSchema);
        stage.Position = 0;
        using ZipArchive zip = new(stage, ZipArchiveMode.Read);
        using Stream catalog = zip.GetEntry("catalog.json")!.Open();
        Assert.DoesNotContain("retainedBindings", Encoding.UTF8.GetString(ReadAll(catalog)));
        static byte[] ReadAll(Stream stream) { using MemoryStream copy = new(); stream.CopyTo(copy); return copy.ToArray(); }
    }

    [Theory]
    [InlineData("plannedPath")] [InlineData("deleted")] [InlineData("mediaId")]
    [InlineData("url")] [InlineData("revision")] [InlineData("role")]
    [InlineData("variant")] [InlineData("status")] [InlineData("postId")]
    public void RetainedSurrogateMustStayTheBoundedOriginalMetadataShape(string mode)
    {
        TransferAttachment media = Media("112", "999", 7);
        TransferAttachment changed = mode switch
        {
            "plannedPath" => media with { Metadata = media.Metadata with { PlannedPath = "/SYNTHETIC-SECRET" } },
            "deleted" => media with { Metadata = media.Metadata with { IsDeleted = true } },
            "mediaId" => media with { Metadata = media.Metadata with { MediaId = null } },
            "url" => media with { Metadata = media.Metadata with { Url = null } },
            "revision" => media with { Metadata = media.Metadata with { AttachmentRevisionKey = null } },
            "role" => media with { Role = TransferMediaRole.Preview },
            "variant" => media with { Metadata = media.Metadata with { Variant = "preview" } },
            "status" => media with { Status = TransferContentStatus.Unknown },
            _ => media
        };
        Reject(new(When, [Thread([], new TransferRetainedBinding(mode == "postId" ? null! : "999", changed))]));
    }

    [Fact]
    public void RetainedCollectionMustBeInitializedAndStayWithinAttachmentBudget()
    {
        TransferThread uninitialized = Thread([]);
        Assert.Throws<InvalidDataException>(() => NativeTransferCatalogSerializer.ContentSchema(
            new(When, [uninitialized with { RetainedBindings = default }])));
        TransferRetainedBinding binding = new("999", Media("116", "999", 0));
        Reject(new(When, [Thread([], Enumerable.Repeat(binding, 100_001).ToArray())]));
    }
}

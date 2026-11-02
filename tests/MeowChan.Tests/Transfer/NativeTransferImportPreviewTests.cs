using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Memory-only synthetic package fixtures; no profiles, filesystem, providers or plugin host.
public sealed class NativeTransferImportPreviewTests
{
    private static readonly DateTimeOffset When = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly ResourceId Id = new("4chan", "b", "7001");
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("synthetic preview inventory");
    private static readonly string Sha256 = Hash(Payload);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static TransferAttachment Media(int position, TransferContentStatus status = TransferContentStatus.Contained,
        TransferMediaRole role = TransferMediaRole.Original) => new(
        new(position, "111", "original", "https://i.4cdn.org/b/111.jpg", "synthetic.jpg", ExpectedSize: 987654321),
        role, status, status == TransferContentStatus.Contained ? Sha256 : null, null);

    private static TransferAttachment Retained(int position, TransferContentStatus status, bool mismatch = false)
    {
        string md5 = Convert.ToBase64String(MD5.HashData(Payload));
        MediaProviderMetadata provider = new("111", "original", "https://i.4cdn.org/b/111.jpg", "synthetic.jpg",
            Payload.Length + (mismatch ? 1 : 0), md5, Sha256);
        CapturedAttachment metadata = new(position, provider.MediaId, provider.Variant, provider.Url,
            provider.OriginalFileName, ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
        {
            ExpectedSha256 = Sha256,
            AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(Id, "absent-historical-post", position, provider)
        };
        return new(metadata, TransferMediaRole.Original, status,
            status == TransferContentStatus.Contained ? Sha256 : null,
            status == TransferContentStatus.Contained ? new(mismatch ? "provider-mismatch" : "strict", When, mismatch, false) : null);
    }

    private static TransferPost Post(params TransferAttachment[] media) => new(
        new("15", long.MaxValue, PostFieldPresence.All, Subject: "", Html: "<b>inert</b>",
            Attachments: media.Select(m => m.Metadata).ToImmutableArray()), media.ToImmutableArray());
    private static TransferThread Thread(params TransferPost[] posts) => new(Id, null, "synthetic",
        ThreadSourceState.NotFound, When, null, null, null, posts.ToImmutableArray());
    private static NativeTransferCatalog Catalog(params TransferThread[] threads) => new(When, threads.ToImmutableArray());

    private static async Task<NativeTransferPackage> Validated(NativeTransferCatalog catalog, bool payload = true)
    {
        using MemoryStream stage = new();
        TransferPayloadSource[] sources = payload
            ? [new(new(Payload.Length, Sha256), _ => Task.FromResult<Stream>(new MemoryStream(Payload, false)))] : [];
        TransferWriteResult written = await NativeTransferPackageWriter.WriteAsync(stage, catalog, sources,
            Guid.Parse("5d7750f7-1c3a-4093-b770-7c854fcfb117"));
        Assert.True(written.IsComplete, written.Error.ToString());
        // Full validator, not serializer or Catalogvalidator success alone, is the entry precondition.
        TransferPackageInspection inspection = await NativeTransferPackageValidator.InspectAsync(stage);
        Assert.True(inspection.IsPackageValid, inspection.ToString());
        return inspection.Package!;
    }
    private static TransferImportPreview Preview(NativeTransferPackage package)
    {
        TransferImportPreviewInspection result = NativeTransferImportPreview.Create(package);
        Assert.True(result.HasPreview, result.Error.ToString());
        return result.Preview!;
    }
    private static void Invalid(NativeTransferPackage? package)
    {
        TransferImportPreviewInspection result = NativeTransferImportPreview.Create(package);
        Assert.False(result.HasPreview);
        Assert.Null(result.Preview);
        Assert.Equal(TransferImportPreviewError.InvalidPackage, result.Error);
        Assert.DoesNotContain("SECRET", result.ToString());
    }

    [Fact]
    public async Task AllSixStatusesStaySeparateForBothCurrentRoles()
    {
        TransferAttachment[] attachments = Enum.GetValues<TransferContentStatus>()
            .SelectMany((status, i) => new[] { Media(i * 2, status), Media(i * 2 + 1, status, TransferMediaRole.Preview) }).ToArray();
        NativeTransferPackage package = await Validated(Catalog(Thread(Post(attachments))));
        TransferImportPreview result = Preview(package);
        TransferImportStatusCounts six = new(1, 1, 1, 1, 1, 1);
        Assert.Equal(six, result.CurrentOriginals);
        Assert.Equal(six, result.CurrentPreviews);
        Assert.Equal(6, result.CurrentOriginals.Total);
        Assert.Equal(0, result.RetainedBindings.Total);
        Assert.Equal(1, result.Posts);
        TransferImportThreadPreview thread = Assert.Single(result.Threads);
        Assert.Equal(Id, thread.Id);
        Assert.Equal(six, thread.CurrentOriginals);
        Assert.Equal(six, thread.CurrentPreviews);
        Assert.Equal(1, result.UniquePayloads);
        Assert.Equal((long)Payload.Length, result.UniquePayloadBytes); // NOT provider ExpectedSize.
    }

    [Fact]
    public async Task EqualPayloadAcrossThreadsRolesAndRetainedBindingsCountsBytesOnceNotOccurrences()
    {
        TransferThread first = Thread(Post(Media(0), Media(1, role: TransferMediaRole.Preview))) with
        {
            RetainedBindings =
            [
                new("absent-historical-post", Retained(7, TransferContentStatus.Contained)),
                new("absent-historical-post", Retained(8, TransferContentStatus.Contained, mismatch: true)),
                new("absent-historical-post", Retained(9, TransferContentStatus.Unavailable)),
                new("absent-historical-post", Retained(10, TransferContentStatus.Omitted))
            ]
        };
        NativeTransferPackage package = await Validated(Catalog(first, Thread(Post(Media(0))) with { Id = new("4chan", "b", "7002") }));
        Assert.Equal("chan-v2", package.Envelope.ContentSchema);
        TransferImportPreview result = Preview(package);
        Assert.Equal(2, result.Threads.Length);
        Assert.Equal(2, result.Posts); // No synthetic post for retained postId.
        Assert.Equal(new TransferImportStatusCounts(2, 0, 0, 0, 0, 0), result.CurrentOriginals);
        Assert.Equal(new TransferImportStatusCounts(1, 0, 0, 0, 0, 0), result.CurrentPreviews);
        Assert.Equal(new TransferImportStatusCounts(2, 1, 0, 1, 0, 0), result.RetainedBindings);
        Assert.Equal(result.RetainedBindings, result.Threads[0].RetainedBindings);
        Assert.Equal(0, result.Threads[1].RetainedBindings.Total);
        Assert.Equal(1, result.UniquePayloads);
        Assert.Equal((long)Payload.Length, result.UniquePayloadBytes);
    }

    [Fact]
    public async Task PreviewOnlyDoesNotClaimOriginalSuccessOrDiscoverMissingOriginals()
    {
        NativeTransferPackage package = await Validated(Catalog(Thread(Post(Media(0, TransferContentStatus.Omitted),
            Media(1, role: TransferMediaRole.Preview)))));
        TransferImportPreview result = Preview(package);
        Assert.Equal(0, result.CurrentOriginals.Contained);
        Assert.Equal(1, result.CurrentOriginals.Omitted);
        Assert.Equal(1, result.CurrentPreviews.Contained);
        NativeTransferPackage onlyPreview = await Validated(Catalog(Thread(Post(Media(0, role: TransferMediaRole.Preview)))));
        Assert.Equal(0, Preview(onlyPreview).CurrentOriginals.Total); // No invented unknown original.
    }

    [Fact]
    public async Task EmptyMetadataOnlyAndNullPresenceRemainHonestZeroInventories()
    {
        Assert.Empty(Preview(await Validated(Catalog(), false)).Threads);
        TransferPost nullPost = new(new("null", 0, PostFieldPresence.Attachments, Attachments: null), []) { AttachmentsWereNull = true };
        TransferPost missingPost = new(new("missing", 1, PostFieldPresence.None, Attachments: ImmutableArray<CapturedAttachment>.Empty), []);
        TransferPost emptyPost = Post() with { Metadata = Post().Metadata with { PostId = "empty" } };
        NativeTransferPackage package = await Validated(Catalog(Thread(nullPost, missingPost, emptyPost),
            Thread() with { Id = new("unknown-source", "opaque", "999") }), false);
        TransferImportPreview result = Preview(package);
        Assert.Equal(3, result.Posts);
        Assert.Equal(2, result.Threads.Length);
        Assert.Equal(0, result.UniquePayloads);
        Assert.Equal(0L, result.UniquePayloadBytes);
        Assert.Equal(0, result.CurrentOriginals.Total);
        Assert.Equal(0, result.CurrentPreviews.Total);
        Assert.True(package.Catalog.Threads[0].Posts[0].AttachmentsWereNull);
        Assert.Null(package.Catalog.Threads[0].Posts[0].Metadata.Attachments);
    }

    [Fact]
    public async Task MultipleUniquePayloadsAreMeasuredOnceEach()
    {
        byte[] other = Encoding.UTF8.GetBytes("different synthetic payload");
        string otherHash = Hash(other);
        TransferAttachment second = Media(1) with { PayloadSha256 = otherHash };
        using MemoryStream stage = new();
        TransferWriteResult written = await NativeTransferPackageWriter.WriteAsync(stage, Catalog(Thread(Post(Media(0), second))),
            new[] { Payload, other }.Select(bytes => new TransferPayloadSource(new(bytes.Length, Hash(bytes)),
                _ => Task.FromResult<Stream>(new MemoryStream(bytes, false)))).ToArray(), Guid.NewGuid());
        Assert.True(written.IsComplete, written.Error.ToString());
        TransferImportPreview result = Preview(written.Package!);
        Assert.Equal(2, result.UniquePayloads);
        Assert.Equal((long)Payload.Length + other.Length, result.UniquePayloadBytes);
    }

    [Fact]
    public async Task InspectionIsDeterministicAndDoesNotMutatePassiveSourceOrCreateAuthority()
    {
        NativeTransferPackage package = await Validated(Catalog(Thread(Post(Media(0))) with
        { RetainedBindings = [new("absent-historical-post", Retained(7, TransferContentStatus.Contained, mismatch: true))] }));
        byte[] before = NativeTransferCatalogSerializer.Serialize(package.Catalog);
        TransferImportPreview first = Preview(package), second = Preview(package);
        Assert.Equal(first.Posts, second.Posts);
        Assert.Equal(first.CurrentOriginals, second.CurrentOriginals);
        Assert.Equal(first.RetainedBindings, second.RetainedBindings);
        Assert.Equal(first.UniquePayloadBytes, second.UniquePayloadBytes);
        Assert.Equal(first.Threads.ToArray(), second.Threads.ToArray());
        Assert.Equal(before, NativeTransferCatalogSerializer.Serialize(package.Catalog));
        Assert.Equal(ThreadSourceState.NotFound, package.Catalog.Threads[0].SourceState);
        Assert.Null(package.Catalog.Threads[0].CaptureRevision);
        Assert.Null(package.Catalog.Threads[0].DecimalPostOrder);
        Assert.Null(package.Catalog.Threads[0].Posts[0].Attachments[0].History);
        Assert.Equal("provider-mismatch", package.Catalog.Threads[0].RetainedBindings[0].Attachment.History!.Kind);
        // Inventory contains no history/grant/source activation/commit decision projection.
        Assert.DoesNotContain("provider-mismatch", first.ToString());
        Assert.DoesNotContain("History", first.ToString());
        Assert.DoesNotContain("Grant", first.ToString());
    }

    [Theory]
    [InlineData("missingCatalogBytes")] [InlineData("catalogHash")] [InlineData("badBytes")]
    [InlineData("schema")] [InlineData("fingerprint")] [InlineData("packageId")]
    [InlineData("duplicatePayload")] [InlineData("missingMeasured")] [InlineData("extraMeasured")]
    [InlineData("measuredHash")] [InlineData("measuredLength")] [InlineData("measuredMd5")]
    [InlineData("measuredSha1")] [InlineData("negativeLength")] [InlineData("largePayload")]
    [InlineData("largeCatalog")] [InlineData("nullEnvelope")] [InlineData("nullCatalog")]
    [InlineData("defaultPayloads")] [InlineData("nullDescriptor")] [InlineData("tooManyPayloads")]
    public async Task ForgedOrInconsistentEnvelopeFailsClosedWithoutRawDiagnostics(string mode)
    {
        NativeTransferPackage package = await Validated(Catalog(Thread(Post(Media(0)))));
        NativeTransferEnvelope e = package.Envelope;
        TransferMeasuredBlob m = e.MeasuredPayloads[Sha256];
        e = mode switch
        {
            "missingCatalogBytes" => e with { CatalogBytes = [] },
            "catalogHash" => e with { Catalog = e.Catalog with { Sha256 = new string('0', 64) } },
            "badBytes" => e with { CatalogBytes = e.CatalogBytes.SetItem(0, (byte)'!') },
            "schema" => e with { ContentSchema = "chan-v3" },
            "fingerprint" => e with { Fingerprint = "SECRET" },
            "packageId" => e with { PackageId = "SECRET" },
            "duplicatePayload" => e with { Payloads = [e.Payloads[0], e.Payloads[0]], MeasuredPayloads = e.MeasuredPayloads.Add(new string('0', 64), m) },
            "missingMeasured" => e with { MeasuredPayloads = ImmutableDictionary<string, TransferMeasuredBlob>.Empty },
            "extraMeasured" => e with { MeasuredPayloads = e.MeasuredPayloads.Add(new string('0', 64), m) },
            "measuredHash" => e with { MeasuredPayloads = e.MeasuredPayloads.SetItem(Sha256, m with { Sha256 = new string('0', 64) }) },
            "measuredLength" => e with { MeasuredPayloads = e.MeasuredPayloads.SetItem(Sha256, m with { Length = m.Length + 1 }) },
            "measuredMd5" => e with { MeasuredPayloads = e.MeasuredPayloads.SetItem(Sha256, m with { Md5 = "SECRET" }) },
            "measuredSha1" => e with { MeasuredPayloads = e.MeasuredPayloads.SetItem(Sha256, m with { Sha1 = "SECRET" }) },
            "negativeLength" => e with { Payloads = [e.Payloads[0] with { Length = -1 }] },
            "largePayload" => e with { Payloads = [e.Payloads[0] with { Length = 1024L * 1024 * 1024 + 1 }] },
            "largeCatalog" => e with { Catalog = e.Catalog with { Length = 16 * 1024 * 1024 + 1 } },
            "defaultPayloads" => e with { Payloads = default },
            "nullDescriptor" => e with { Payloads = [null!] },
            "tooManyPayloads" => e with { Payloads = Enumerable.Repeat(e.Payloads[0], 9999).ToImmutableArray() },
            _ => e
        };
        Invalid(package with { Envelope = mode == "nullEnvelope" ? null! : e, Catalog = mode == "nullCatalog" ? null! : package.Catalog });
    }

    [Theory]
    [InlineData("status")] [InlineData("payload")] [InlineData("path")] [InlineData("metadataProjection")]
    [InlineData("nullFlag")] [InlineData("threadId")] [InlineData("postFields")] [InlineData("nullPost")]
    [InlineData("defaultAttachments")] [InlineData("defaultRetained")] [InlineData("defaultThreads")]
    [InlineData("largeThreads")] [InlineData("largePosts")] [InlineData("largeAttachments")] [InlineData("largeRetained")]
    public async Task DirectCatalogDtoMustMatchCapturedValidatedBytesIncludingNonWireProperties(string mode)
    {
        NativeTransferPackage package = await Validated(Catalog(Thread(Post(Media(0)))));
        NativeTransferCatalog c = package.Catalog;
        TransferThread t = c.Threads[0];
        TransferPost p = t.Posts[0];
        TransferAttachment a = p.Attachments[0];
        TransferPost changed = mode switch
        {
            "status" => p with { Attachments = [a with { Status = (TransferContentStatus)999 }] },
            "payload" => p with { Attachments = [a with { PayloadSha256 = new string('0', 64) }] },
            "path" => p with { Attachments = [a with { Metadata = a.Metadata with { PlannedPath = "/SECRET" } }] },
            "metadataProjection" => p with { Metadata = p.Metadata with { Attachments = new[] { a.Metadata } } },
            "nullFlag" => p with { AttachmentsWereNull = true },
            "postFields" => p with { Metadata = p.Metadata with { Fields = PostFieldPresence.None } },
            "defaultAttachments" => p with { Attachments = default },
            "largeAttachments" => p with { Attachments = Enumerable.Repeat(a, 100001).ToImmutableArray() },
            "nullPost" => null!,
            _ => p
        };
        t = t with { Posts = [changed] };
        if (mode == "threadId") t = t with { Id = new("SECRET", "b", "1") };
        if (mode == "defaultRetained") t = t with { RetainedBindings = default };
        if (mode == "largePosts") t = t with { Posts = Enumerable.Repeat(p, 100001).ToImmutableArray() };
        if (mode == "largeRetained") t = t with { RetainedBindings = Enumerable.Repeat(new TransferRetainedBinding("15", a), 100001).ToImmutableArray() };
        c = c with { Threads = mode == "largeThreads" ? Enumerable.Repeat(t, 1001).ToImmutableArray() : [t] };
        if (mode == "defaultThreads") c = c with { Threads = default };
        Invalid(package with { Catalog = c });
    }

    [Fact]
    public async Task CatalogValidatorIsReusedForPrivacyAndRetainedInventoryCannotBeHidden()
    {
        NativeTransferPackage package = await Validated(Catalog(Thread(Post(Media(0)))));
        // Bind the modified bytes honestly so rejection must come from existing semantic admission,
        // not merely from the catalog hash guard. The supplied DTO itself remains unchanged.
        foreach (string mode in new[] { "activeHtml", "credentialUrl", "unknownStatus", "privateField", "schema" })
        {
            JsonNode root = JsonNode.Parse(package.Envelope.CatalogBytes.AsSpan())!;
            JsonNode post = root["threads"]![0]!["posts"]![0]!;
            switch (mode)
            {
                case "activeHtml": post["html"] = "<script>SECRET</script>"; break;
                case "credentialUrl": post["attachments"]![0]!["url"] = "https://user:SECRET@i.4cdn.org/b/111.jpg"; break;
                case "unknownStatus": post["attachments"]![0]!["status"] = "future"; break;
                case "privateField": root["grant"] = "SECRET"; break;
                case "schema": root["schema"] = "chan-v3"; break;
            }
            byte[] bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
            Invalid(package with { Envelope = package.Envelope with { CatalogBytes = bytes.ToImmutableArray(), Catalog = new(bytes.Length, Hash(bytes)) } });
        }
        NativeTransferPackage retained = await Validated(Catalog(Thread() with
        { RetainedBindings = [new("absent-historical-post", Retained(7, TransferContentStatus.Contained))] }));
        TransferImportPreview retainedOnly = Preview(retained);
        Assert.Equal(0, retainedOnly.Posts);
        Assert.Equal(0, retainedOnly.CurrentOriginals.Total);
        Assert.Equal(1, retainedOnly.RetainedBindings.Contained);
        Assert.Equal((long)Payload.Length, retainedOnly.UniquePayloadBytes);
        TransferThread t = retained.Catalog.Threads[0];
        Invalid(retained with { Catalog = retained.Catalog with { Threads = [t with { RetainedBindings = [] }] } });
    }

    [Fact]
    public async Task AllEmptyChanV2IsAcceptedWithoutCanonicalSerializationChangingSchema()
    {
        NativeTransferPackage package = await Validated(Catalog(Thread()), false);
        JsonNode root = JsonNode.Parse(package.Envelope.CatalogBytes.AsSpan())!;
        root["schema"] = "chan-v2";
        root["threads"]![0]!["retainedBindings"] = new JsonArray();
        byte[] bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
        // The writer canonically selects v1 here. Use a real, fully inspected stored ZIP to
        // exercise legal noncanonical v2 rather than pretending DTO construction validated bytes.
        JsonObject manifest = new()
        {
            ["format"] = "meowchan-transfer", ["envelopeVersion"] = 1,
            ["packageId"] = package.Envelope.PackageId, ["contentSchema"] = "chan-v2",
            ["requiredFeatures"] = new JsonArray("chan-v2"),
            ["catalog"] = new JsonObject { ["length"] = bytes.Length, ["sha256"] = Hash(bytes) },
            ["payloads"] = new JsonArray()
        };
        using MemoryStream stage = new();
        using (ZipArchive zip = new(stage, ZipArchiveMode.Create, true))
        {
            using (Stream entry = zip.CreateEntry("manifest.json", CompressionLevel.NoCompression).Open())
                entry.Write(Encoding.UTF8.GetBytes(manifest.ToJsonString()));
            using (Stream entry = zip.CreateEntry("catalog.json", CompressionLevel.NoCompression).Open())
                entry.Write(bytes);
        }
        TransferPackageInspection inspected = await NativeTransferPackageValidator.InspectAsync(stage);
        Assert.True(inspected.IsPackageValid, inspected.ToString());
        Assert.Equal(0, Preview(inspected.Package!).Threads[0].RetainedBindings.Total);
        Assert.Equal("chan-v2", inspected.Package!.Envelope.ContentSchema);
    }

    [Fact]
    public async Task CancellationIsOperationCancellationNotAPartialOrContainedStatus()
    {
        NativeTransferPackage package = await Validated(Catalog(Thread(Post(Media(0)))));
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        OperationCanceledException error = Assert.Throws<OperationCanceledException>(() => NativeTransferImportPreview.Create(package, cancelled.Token));
        Assert.Equal(cancelled.Token, error.CancellationToken);
        Assert.Throws<OperationCanceledException>(() => NativeTransferImportPreview.Create(null, cancelled.Token));
        Assert.Equal(1, Preview(package).CurrentOriginals.Contained);
    }

    [Fact]
    public async Task MaximumThreadInventoryIsBoundedAndEveryEmptyThreadRemainsVisible()
    {
        TransferThread[] threads = Enumerable.Range(0, 1000)
            .Select(i => Thread() with { Id = new("unloaded-source", "opaque-board", i.ToString(System.Globalization.CultureInfo.InvariantCulture)) }).ToArray();
        TransferImportPreview result = Preview(await Validated(Catalog(threads), false));
        Assert.Equal(1000, result.Threads.Length);
        Assert.Equal(0, result.Posts);
        Assert.Equal(0L, result.UniquePayloadBytes);
        Assert.All(result.Threads, t => Assert.Equal(0, t.CurrentOriginals.Total + t.CurrentPreviews.Total + t.RetainedBindings.Total));
    }

    [Fact]
    public async Task CoherentForgedMeasurementDemonstratesWhyFullInspectionOriginIsAPrecondition()
    {
        NativeTransferPackage package = await Validated(Catalog(Thread(Post(Media(0)))));
        // This legacy-unbound occurrence carries no history. A fabricated MD5 is syntactically
        // valid and catalog-consistent, but did NOT come from the payload. DTOs cannot prove that.
        TransferMeasuredBlob fabricated = package.Envelope.MeasuredPayloads[Sha256] with
            { Md5 = Convert.ToBase64String(new byte[16]) };
        NativeTransferPackage forged = package with { Envelope = package.Envelope with
            { MeasuredPayloads = package.Envelope.MeasuredPayloads.SetItem(Sha256, fabricated) } };
        TransferImportPreview inventory = Preview(forged);
        Assert.Equal(Preview(package).UniquePayloadBytes, inventory.UniquePayloadBytes);
        Assert.Equal(1, inventory.CurrentOriginals.Contained);
        Assert.DoesNotContain(fabricated.Md5, inventory.ToString());
        // Success means only an inventory under the documented caller precondition. No import
        // permission, measured-byte certificate or active acceptance/grant is emitted.
    }

    [Fact]
    public void UninspectedDirectDtosCannotUseThePreviewAsAdmission()
    {
        Invalid(null);
        NativeTransferCatalog catalog = Catalog(Thread(Post(Media(0))));
        Invalid(new(new(Guid.NewGuid().ToString("D"), "chan-v1", new string('a', 64), new(0, new string('a', 64)), []), catalog));
        // No check on DTOs can prove that a forged, internally consistent measured digest was ever
        // read from bytes. Full PackageValidator origin remains a documented caller precondition;
        // even a successful preview never authorizes a future commit or active mismatch policy.
    }
}

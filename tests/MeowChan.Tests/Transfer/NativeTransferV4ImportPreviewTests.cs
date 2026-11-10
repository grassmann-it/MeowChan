using System.Buffers.Binary;
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

// Memory-only public inventory fixtures. No profiles, stores, host, decoder or apply authority.
public sealed class NativeTransferV4ImportPreviewTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);
    private static readonly ResourceId Id = new("4chan", "b", "7001");
    private static readonly byte[] Original = Encoding.UTF8.GetBytes("synthetic original; not proof of playable video");
    private static readonly byte[] PreviewBytes = Encoding.UTF8.GetBytes("synthetic derived preview");
    private static readonly byte[] Poster = CreatePoster();
    private static readonly TransferImportStatusCounts Zero = new(0, 0, 0, 0, 0, 0);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static TransferOriginalByteVersion Version(byte[] bytes) => new(bytes.Length, Hash(bytes),
        Convert.ToBase64String(MD5.HashData(bytes)), Convert.ToBase64String(SHA1.HashData(bytes)));

    private static TransferAttachment Media(ResourceId? thread = null, string post = "15", int position = 0,
        TransferContentStatus status = TransferContentStatus.Omitted, byte[]? original = null)
    {
        original ??= Original;
        MediaProviderMetadata provider = new("111", "original", "https://i.4cdn.org/b/111.webm", "synthetic.webm",
            987654321, Version(original).Md5, Hash(original));
        CapturedAttachment metadata = new(position, provider.MediaId, provider.Variant, provider.Url,
            provider.OriginalFileName, ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
        {
            ExpectedSha256 = provider.ExpectedSha256,
            AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(thread ?? Id, post, position, provider)
        };
        return new(metadata, TransferMediaRole.Original, status,
            status == TransferContentStatus.Contained ? Hash(original) : null, null)
        { OriginalByteVersion = Version(original) };
    }

    private static TransferAttachment Derived(TransferAttachment a,
        TransferContentStatus preview = TransferContentStatus.Contained,
        TransferContentStatus poster = TransferContentStatus.Contained, byte[]? previewBytes = null)
    {
        return a with { DerivedRepresentations =
        [
            new(TransferDerivedRepresentationKind.Preview, a.Metadata.AttachmentRevisionKey!, a.OriginalByteVersion!,
                preview, preview == TransferContentStatus.Contained ? Hash(previewBytes ?? PreviewBytes) : null),
            new(TransferDerivedRepresentationKind.VideoPosterPng, a.Metadata.AttachmentRevisionKey!, a.OriginalByteVersion!,
                poster, poster == TransferContentStatus.Contained ? Hash(Poster) : null)
        ] };
    }

    private static TransferPost Post(params TransferAttachment[] attachments) => new(
        new("15", long.MaxValue, PostFieldPresence.All, Subject: "", Html: "<b>inert</b>",
            Attachments: attachments.Select(a => a.Metadata).ToImmutableArray()), attachments.ToImmutableArray());
    private static TransferThread Thread(ResourceId? id = null, params TransferPost[] posts) => new(id ?? Id,
        null, "synthetic inventory", ThreadSourceState.NotFound, At, null, null, null, posts.ToImmutableArray());
    private static NativeTransferCatalog Catalog(params TransferThread[] threads) => new(At, threads.ToImmutableArray());
    private static NativeTransferCatalog Single(TransferAttachment a, bool retained = false) => Catalog(retained
        ? Thread() with { RetainedBindings = [new("999", a)] } : Thread(posts: [Post(a)]));

    private static async Task<NativeTransferPackage> Validated(NativeTransferCatalog catalog, byte[][]? payloads = null,
        NativePrivateMetadataProjection? privateMetadata = null)
    {
        using MemoryStream bytes = new();
        TransferPayloadSource[] sources = (payloads ?? []).DistinctBy(Hash).Select(p => new TransferPayloadSource(
            new(p.Length, Hash(p)), _ => Task.FromResult<Stream>(new MemoryStream(p, false)))).ToArray();
        TransferWriteResult written = await NativeTransferPackageWriter.WriteAsync(bytes, catalog, sources,
            Guid.Parse("ce0df0bc-2145-4a4f-9ba1-f9548875e347"), privateMetadata: privateMetadata);
        Assert.True(written.IsComplete, written.ToString());
        // Full byte/ZIP/PNG inspection, not just serializer success or direct DTOs.
        bytes.Position = 7;
        TransferPackageInspection result = await NativeTransferPackageValidator.InspectAsync(bytes);
        Assert.True(result.IsPackageValid, result.ToString());
        Assert.Equal(7L, bytes.Position);
        return result.Package!;
    }

    private static TransferImportPreview Inventory(NativeTransferPackage package)
    {
        TransferImportPreviewInspection result = NativeTransferImportPreview.Create(package);
        Assert.True(result.HasPreview, result.Error.ToString());
        return result.Preview!;
    }
    private static void Invalid(NativeTransferPackage package)
    {
        TransferImportPreviewInspection result = NativeTransferImportPreview.Create(package);
        Assert.False(result.HasPreview);
        Assert.Null(result.Preview);
        Assert.Equal(TransferImportPreviewError.InvalidPackage, result.Error);
        Assert.DoesNotContain("SECRET", result.ToString());
    }

    [Fact]
    public async Task CurrentRetainedAndLegacyPreviewRolesHaveDistinctCountsAcrossThreads()
    {
        ResourceId second = new("4chan", "b", "7002");
        TransferAttachment legacyPreview = new(new(1, "112", "preview", "https://i.4cdn.org/b/112s.jpg", "preview.jpg"),
            TransferMediaRole.Preview, TransferContentStatus.Contained, Hash(PreviewBytes), null);
        NativeTransferPackage package = await Validated(Catalog(
            Thread(posts: [Post(Derived(Media()), legacyPreview)]) with
            { RetainedBindings = [new("999", Derived(Media(post: "999")))] },
            Thread(second) with { RetainedBindings = [new("999", Derived(Media(second, "999")))] }),
            [PreviewBytes, Poster]);
        Assert.True(package.IsChanV4);
        byte[] before = NativeTransferCatalogSerializer.Serialize(package.Catalog);
        TransferImportPreview result = Inventory(package);
        Assert.Equal(2, result.Threads.Length);
        Assert.Equal(1, result.Posts); // Historical 999 is NOT manufactured as a post.
        Assert.Equal(new TransferImportStatusCounts(0, 1, 0, 0, 0, 0), result.CurrentOriginals);
        Assert.Equal(new TransferImportStatusCounts(1, 0, 0, 0, 0, 0), result.CurrentPreviews);
        Assert.Equal(new TransferImportStatusCounts(0, 2, 0, 0, 0, 0), result.RetainedBindings);
        Assert.Equal(new TransferImportStatusCounts(3, 0, 0, 0, 0, 0), result.DerivedPreviews);
        Assert.Equal(result.DerivedPreviews, result.VideoPosters);
        Assert.Equal(2, result.Threads[0].DerivedPreviews.Contained);
        Assert.Equal(1, result.Threads[1].DerivedPreviews.Contained);
        Assert.Equal(result.Threads[0].DerivedPreviews, result.Threads[0].VideoPosters);
        Assert.Equal(result.Threads[1].DerivedPreviews, result.Threads[1].VideoPosters);
        Assert.Equal(0, result.Threads[1].Posts);
        Assert.Equal(Zero, result.Threads[1].CurrentOriginals);
        Assert.Equal(Zero, result.Threads[1].CurrentPreviews);
        Assert.Equal(2, result.UniquePayloads);
        Assert.Equal((long)PreviewBytes.Length + Poster.Length, result.UniquePayloadBytes);
        Assert.Equal(package.Envelope.MeasuredPayloads.Values.Sum(p => p.Length), result.UniquePayloadBytes);
        Assert.Equal(before, NativeTransferCatalogSerializer.Serialize(package.Catalog));
        Assert.Equal(result.Threads.ToArray(), Inventory(package).Threads.ToArray());
        Assert.All(package.Catalog.Threads, t => Assert.Equal(ThreadSourceState.NotFound, t.SourceState));
        Assert.DoesNotContain("Grant", result.ToString());
        Assert.DoesNotContain("Playable", result.ToString());
    }

    [Theory]
    [InlineData(false, (int)TransferContentStatus.Contained, (int)TransferContentStatus.Unavailable)]
    [InlineData(false, (int)TransferContentStatus.Omitted, (int)TransferContentStatus.Contained)]
    [InlineData(false, (int)TransferContentStatus.Unavailable, (int)TransferContentStatus.Omitted)]
    [InlineData(true, (int)TransferContentStatus.Contained, (int)TransferContentStatus.Omitted)]
    [InlineData(true, (int)TransferContentStatus.Omitted, (int)TransferContentStatus.Unavailable)]
    [InlineData(true, (int)TransferContentStatus.Unavailable, (int)TransferContentStatus.Contained)]
    public async Task DerivedStatusesAreSeparateAndNeverPromoteAbsentOriginals(bool retained,
        int previewValue, int posterValue)
    {
        TransferContentStatus preview = (TransferContentStatus)previewValue;
        TransferContentStatus poster = (TransferContentStatus)posterValue;
        TransferAttachment a = Derived(Media(post: retained ? "999" : "15"), preview, poster);
        byte[][] payloads = preview == TransferContentStatus.Contained ? [PreviewBytes]
            : poster == TransferContentStatus.Contained ? [Poster] : [];
        NativeTransferPackage package = await Validated(Single(a, retained), payloads);
        TransferImportPreview result = Inventory(package);
        TransferImportThreadPreview thread = Assert.Single(result.Threads);
        Assert.Equal(Count(preview), result.DerivedPreviews);
        Assert.Equal(Count(poster), result.VideoPosters);
        Assert.Equal(result.DerivedPreviews, thread.DerivedPreviews);
        Assert.Equal(result.VideoPosters, thread.VideoPosters);
        Assert.Equal(Zero, result.CurrentPreviews);
        Assert.Equal(0, result.CurrentOriginals.Contained);
        Assert.Equal(retained ? Zero : Count(TransferContentStatus.Omitted), result.CurrentOriginals);
        Assert.Equal(retained ? Count(TransferContentStatus.Omitted) : Zero, result.RetainedBindings);
        Assert.Equal(retained ? 0 : 1, result.Posts);
        Assert.Equal(payloads.Length, result.UniquePayloads);
        Assert.Equal(payloads.Sum(p => (long)p.Length), result.UniquePayloadBytes);
    }

    [Theory]
    [InlineData((int)TransferContentStatus.Contained)] [InlineData((int)TransferContentStatus.Omitted)]
    [InlineData((int)TransferContentStatus.Unknown)] [InlineData((int)TransferContentStatus.Unavailable)]
    [InlineData((int)TransferContentStatus.Failed)] [InlineData((int)TransferContentStatus.Cancelled)]
    public async Task AllOriginalStatusesRemainIndependentOfContainedDerivedBytes(int statusValue)
    {
        TransferContentStatus status = (TransferContentStatus)statusValue;
        NativeTransferPackage package = await Validated(Single(Derived(Media(status: status))),
            status == TransferContentStatus.Contained ? [Original, PreviewBytes, Poster] : [PreviewBytes, Poster]);
        TransferImportPreview result = Inventory(package);
        Assert.Equal(Count(status), result.CurrentOriginals);
        Assert.Equal(Count(TransferContentStatus.Contained), result.DerivedPreviews);
        Assert.Equal(result.DerivedPreviews, result.VideoPosters);
        Assert.Equal(Zero, result.CurrentPreviews);
        Assert.Null(package.Catalog.Threads[0].Posts[0].Attachments[0].History);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task EqualHashOriginalPreviewPosterAndRetainedAliasesCountOneMeasuredPayload(bool contained)
    {
        TransferContentStatus status = contained ? TransferContentStatus.Contained : TransferContentStatus.Omitted;
        TransferAttachment a = Derived(Media(status: status, original: Poster), previewBytes: Poster);
        TransferAttachment b = Derived(Media(post: "999", status: status, original: Poster), previewBytes: Poster);
        NativeTransferPackage package = await Validated(Catalog(Thread(posts: [Post(a)]) with
            { RetainedBindings = [new("999", b)] }), [Poster]);
        TransferImportPreview result = Inventory(package);
        Assert.Equal(Count(status), result.CurrentOriginals);
        Assert.Equal(Count(status), result.RetainedBindings);
        Assert.Equal(Zero, result.CurrentPreviews);
        Assert.Equal(new TransferImportStatusCounts(2, 0, 0, 0, 0, 0), result.DerivedPreviews);
        Assert.Equal(result.DerivedPreviews, result.VideoPosters);
        Assert.Equal(1, result.Posts);
        Assert.Equal(1, result.UniquePayloads);
        Assert.Equal((long)Poster.Length, result.UniquePayloadBytes); // Not ExpectedSize; not multiplied by five/six references.
        Assert.NotEqual(a.Metadata.AttachmentRevisionKey, b.Metadata.AttachmentRevisionKey);
        Assert.Equal(a.OriginalByteVersion, b.OriginalByteVersion);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task IndependentlyAllocatedNestedValuesAreEqualIncludingNullableSourceSha1(bool sha1)
    {
        TransferAttachment a = Media();
        if (!sha1) a = a with { OriginalByteVersion = a.OriginalByteVersion! with { Sha1 = null } };
        TransferAttachment b = Media(post: "999");
        if (!sha1) b = b with { OriginalByteVersion = b.OriginalByteVersion! with { Sha1 = null } };
        NativeTransferPackage package = await Validated(Catalog(Thread(posts: [Post(Derived(a))]) with
            { RetainedBindings = [new("999", Derived(b))] }), [PreviewBytes, Poster]);
        TransferAttachment Clone(TransferAttachment attachment) => attachment with
        {
            Metadata = attachment.Metadata with { },
            OriginalByteVersion = attachment.OriginalByteVersion! with { },
            DerivedRepresentations = attachment.DerivedRepresentations.Select(d => d with
                { SourceByteVersion = d.SourceByteVersion with { } }).ToImmutableArray()
        };
        TransferThread t = package.Catalog.Threads[0];
        TransferPost p = t.Posts[0];
        TransferAttachment cloned = Clone(p.Attachments[0]);
        Assert.NotEqual(p.Attachments[0].DerivedRepresentations, cloned.DerivedRepresentations);
        NativeTransferCatalog clone = package.Catalog with { Threads = [t with
        {
            Posts = [p with { Metadata = p.Metadata with { Attachments = ImmutableArray.Create(cloned.Metadata) }, Attachments = [cloned] }],
            RetainedBindings = t.RetainedBindings.Select(r => r with { Attachment = Clone(r.Attachment) }).ToImmutableArray()
        }] };
        TransferImportPreview expected = Inventory(package), result = Inventory(package with { Catalog = clone });
        Assert.Equal(expected.Threads.ToArray(), result.Threads.ToArray());
        Assert.Equal(expected.DerivedPreviews, result.DerivedPreviews);
        Assert.Equal(expected.VideoPosters, result.VideoPosters);
        Assert.Equal(expected.UniquePayloadBytes, result.UniquePayloadBytes);
    }

    public static IEnumerable<object[]> DtoMutations()
    {
        string[] mutations = ["kind", "revision", "sourceLength", "sourceHash", "sourceMd5", "sourceSha1", "sourceSha1Null",
            "nullSource", "status", "payload", "nullEntry", "defaultArray", "emptyArray", "order", "largeArray",
            "originalLength", "originalHash", "originalMd5", "originalSha1", "originalSha1Null", "noOriginal",
            "metadataPath", "role", "originalStatus", "originalPayload", "history", "nullAttachment"];
        foreach (bool retained in new[] { false, true })
            foreach (string mutation in mutations)
            {
                yield return [retained, 0, mutation];
                if (mutation is "kind" or "revision" or "sourceLength" or "sourceHash" or "sourceMd5"
                    or "sourceSha1" or "sourceSha1Null" or "nullSource" or "status" or "payload" or "nullEntry")
                    yield return [retained, 1, mutation];
            }
    }

    [Theory]
    [MemberData(nameof(DtoMutations))]
    public async Task EveryNestedDtoValueMustMatchCapturedCatalogForCurrentAndRetained(bool retained, int index, string mutation)
    {
        NativeTransferPackage package = await Validated(Single(Derived(Media(post: retained ? "999" : "15")), retained),
            [PreviewBytes, Poster]);
        TransferThread t = package.Catalog.Threads[0];
        TransferAttachment a = retained ? t.RetainedBindings[0].Attachment : t.Posts[0].Attachments[0];
        TransferDerivedRepresentation d = a.DerivedRepresentations[index];
        TransferOriginalByteVersion version = d.SourceByteVersion;
        TransferDerivedRepresentation changed = mutation switch
        {
            "kind" => d with { Kind = index == 0 ? TransferDerivedRepresentationKind.VideoPosterPng : TransferDerivedRepresentationKind.Preview },
            "revision" => d with { SourceRevisionKey = "SECRET" },
            "sourceLength" => d with { SourceByteVersion = version with { Length = version.Length + 1 } },
            "sourceHash" => d with { SourceByteVersion = version with { Sha256 = Hash(PreviewBytes) } },
            "sourceMd5" => d with { SourceByteVersion = version with { Md5 = Version(PreviewBytes).Md5 } },
            "sourceSha1" => d with { SourceByteVersion = version with { Sha1 = Version(PreviewBytes).Sha1 } },
            "sourceSha1Null" => d with { SourceByteVersion = version with { Sha1 = null } },
            "nullSource" => d with { SourceByteVersion = null! },
            "status" => d with { Status = TransferContentStatus.Omitted, PayloadSha256 = null },
            "payload" => d with { PayloadSha256 = Hash(index == 0 ? Poster : PreviewBytes) },
            "nullEntry" => null!,
            _ => d
        };
        TransferAttachment bad = a with { DerivedRepresentations = a.DerivedRepresentations.SetItem(index, changed) };
        bad = mutation switch
        {
            "defaultArray" => bad with { DerivedRepresentations = default },
            "emptyArray" => bad with { DerivedRepresentations = [] },
            "order" => bad with { DerivedRepresentations = [a.DerivedRepresentations[1], d] },
            "largeArray" => bad with { DerivedRepresentations = Enumerable.Repeat(d, 100001).ToImmutableArray() },
            "originalLength" => bad with { OriginalByteVersion = version with { Length = version.Length + 1 } },
            "originalHash" => bad with { OriginalByteVersion = version with { Sha256 = Hash(PreviewBytes) } },
            "originalMd5" => bad with { OriginalByteVersion = version with { Md5 = Version(PreviewBytes).Md5 } },
            "originalSha1" => bad with { OriginalByteVersion = version with { Sha1 = Version(PreviewBytes).Sha1 } },
            "originalSha1Null" => bad with { OriginalByteVersion = version with { Sha1 = null } },
            "noOriginal" => bad with { OriginalByteVersion = null },
            "metadataPath" => bad with { Metadata = bad.Metadata with { PlannedPath = "/SECRET" } },
            "role" => bad with { Role = TransferMediaRole.Preview },
            "originalStatus" => bad with { Status = TransferContentStatus.Unavailable },
            "originalPayload" => bad with { PayloadSha256 = Hash(Poster) },
            "history" => bad with { History = new("strict", At, false, false) },
            "nullAttachment" => null!,
            _ => bad
        };
        t = retained ? t with { RetainedBindings = [t.RetainedBindings[0] with { Attachment = bad }] }
            : t with { Posts = [t.Posts[0] with { Attachments = [bad] }] };
        Invalid(package with { Catalog = package.Catalog with { Threads = [t] } });
        Assert.Equal(1, Inventory(package).DerivedPreviews.Contained); // No mutable cache/poisoned source.
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CoherentlyChangedDtoSourceEvidenceStillCannotReplaceCapturedValues(bool retained)
    {
        NativeTransferPackage package = await Validated(Single(Derived(Media(post: retained ? "999" : "15")), retained),
            [PreviewBytes, Poster]);
        TransferThread t = package.Catalog.Threads[0];
        TransferAttachment a = retained ? t.RetainedBindings[0].Attachment : t.Posts[0].Attachments[0];
        TransferOriginalByteVersion different = a.OriginalByteVersion! with { Length = long.MaxValue };
        TransferAttachment changed = a with
        {
            OriginalByteVersion = different,
            DerivedRepresentations = a.DerivedRepresentations.Select(d => d with { SourceByteVersion = different }).ToImmutableArray()
        };
        // Internally consistent source linkage is not a capability to replace the captured evidence.
        Assert.Equal(TransferCatalogError.None, NativeTransferCatalogValidator.ValidateRepresentations(changed));
        t = retained ? t with { RetainedBindings = [t.RetainedBindings[0] with { Attachment = changed }] }
            : t with { Posts = [t.Posts[0] with { Attachments = [changed] }] };
        Invalid(package with { Catalog = package.Catalog with { Threads = [t] } });
    }

    [Theory]
    [InlineData("metadataProjection")] [InlineData("retainedPostId")] [InlineData("dropRetained")]
    [InlineData("defaultRetained")] [InlineData("largePosts")] [InlineData("largeThreads")]
    [InlineData("largeAttachments")] [InlineData("largeRetained")]
    public async Task BoundedOuterComparisonStillRejectsAlteredAndUntrustedCollections(string mutation)
    {
        NativeTransferPackage package = await Validated(Catalog(Thread(posts: [Post(Derived(Media()))]) with
            { RetainedBindings = [new("999", Derived(Media(post: "999")))] }), [PreviewBytes, Poster]);
        TransferThread t = package.Catalog.Threads[0];
        TransferPost p = t.Posts[0];
        t = mutation switch
        {
            "metadataProjection" => t with { Posts = [p with { Metadata = p.Metadata with { Attachments = new ThrowOnReadList() } }] },
            "retainedPostId" => t with { RetainedBindings = [t.RetainedBindings[0] with { PostId = "SECRET" }] },
            "dropRetained" => t with { RetainedBindings = [] },
            "defaultRetained" => t with { RetainedBindings = default },
            "largePosts" => t with { Posts = Enumerable.Repeat(p, 100001).ToImmutableArray() },
            "largeAttachments" => t with { Posts = [p with { Attachments = Enumerable.Repeat(p.Attachments[0], 100001).ToImmutableArray() }] },
            "largeRetained" => t with { RetainedBindings = Enumerable.Repeat(t.RetainedBindings[0], 100001).ToImmutableArray() },
            _ => t
        };
        Invalid(package with { Catalog = package.Catalog with { Threads = mutation == "largeThreads"
            ? Enumerable.Repeat(t, 1001).ToImmutableArray() : [t] } });
    }

    [Theory]
    [InlineData("derivedStatus")] [InlineData("sourceRevision")] [InlineData("privateField")]
    [InlineData("activeHtml")] [InlineData("credentialUrl")] [InlineData("schema")]
    [InlineData("duplicateKind")] [InlineData("tooManyDerived")] [InlineData("originalEvidence")]
    public async Task ReboundCatalogBytesStillPassThroughFullSemanticValidator(string mutation)
    {
        NativeTransferPackage package = await Validated(Single(Derived(Media())), [PreviewBytes, Poster]);
        JsonNode root = JsonNode.Parse(package.Envelope.CatalogBytes.AsSpan())!;
        JsonNode post = root["threads"]![0]!["posts"]![0]!;
        JsonNode a = post["attachments"]![0]!;
        switch (mutation)
        {
            case "derivedStatus": a["derivedRepresentations"]![0]!["status"] = "failed"; break;
            case "sourceRevision": a["derivedRepresentations"]![0]!["sourceRevisionKey"] = "SECRET"; break;
            case "privateField": root["grant"] = "SECRET"; break;
            case "activeHtml": post["html"] = "<script>SECRET</script>"; break;
            case "credentialUrl": a["url"] = "https://user:SECRET@i.4cdn.org/b/111.webm"; break;
            case "schema": root["schema"] = "chan-v3"; break;
            case "duplicateKind": a["derivedRepresentations"]![1]!["kind"] = "preview"; break;
            case "tooManyDerived": a["derivedRepresentations"]!.AsArray().Add(a["derivedRepresentations"]![0]!.DeepClone()); break;
            case "originalEvidence": a["originalByteVersion"]!["length"] = -1; break;
        }
        byte[] bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
        NativeTransferEnvelope envelope = package.Envelope with { CatalogBytes = bytes.ToImmutableArray(), Catalog = new(bytes.Length, Hash(bytes)) };
        Assert.False(NativeTransferCatalogValidator.Inspect(bytes, envelope).IsCatalogValid);
        Invalid(package with { Envelope = envelope });
    }

    [Theory]
    [InlineData("missingMeasured")] [InlineData("measuredLength")] [InlineData("duplicatePayload")]
    [InlineData("largePayload")] [InlineData("largeCatalog")] [InlineData("totalPayloadCap")]
    public async Task V4DoesNotBypassMeasurementOrEnvelopeCaps(string mutation)
    {
        NativeTransferPackage package = await Validated(Single(Derived(Media())), [PreviewBytes, Poster]);
        NativeTransferEnvelope e = package.Envelope;
        string hash = Hash(Poster);
        e = mutation switch
        {
            "missingMeasured" => e with { MeasuredPayloads = e.MeasuredPayloads.Remove(hash) },
            "measuredLength" => e with { MeasuredPayloads = e.MeasuredPayloads.SetItem(hash,
                e.MeasuredPayloads[hash] with { Length = Poster.Length + 1 }) },
            "duplicatePayload" => e with { Payloads = [e.Payloads[0], e.Payloads[0]] },
            "largePayload" => e with { Payloads = e.Payloads.SetItem(0, e.Payloads[0] with { Length = 1024L * 1024 * 1024 + 1 }) },
            "largeCatalog" => e with { Catalog = e.Catalog with { Length = 16 * 1024 * 1024 + 1 } },
            "totalPayloadCap" => e with { Payloads = e.Payloads.Select(p => p with { Length = 1024L * 1024 * 1024 }).ToImmutableArray() },
            _ => e
        };
        Invalid(package with { Envelope = e });
    }

    [Fact]
    public async Task ValidPrivateV4ExposesContentFreeOfferButForgedPrivateSignalsRemainRejected()
    {
        NativePrivateMetadataProjection privateMetadata = new([Id], new(true, false, false), [Id], [], []);
        NativeTransferPackage privatePackage = await Validated(Single(Media()), privateMetadata: privateMetadata);
        Assert.True(privatePackage.IsChanV4);
        Assert.NotNull(privatePackage.PrivateMetadata);
        Assert.NotNull(privatePackage.Envelope.PrivateMetadata);
        TransferImportPreviewInspection privatePreview = NativeTransferImportPreview.Create(privatePackage);
        Assert.True(privatePreview.HasPreview);
        Assert.True(privatePreview.Preview!.PrivateOffer.Available);
        Assert.True(privatePreview.Preview.PrivateOffer.ThreadFavoritesAvailable);
        Assert.False(privatePreview.Preview.PrivateOffer.MediaFavoritesAvailable);
        Assert.False(privatePreview.Preview.PrivateOffer.PostBookmarksAndNotesAvailable);
        Assert.Equal(default, privatePreview.Preview.PrivateOffer.Selected);
        Invalid(privatePackage with { PrivateMetadata = null });
        NativeTransferPackage publicPackage = await Validated(Single(Media()));
        Assert.Equal(1, Inventory(publicPackage).Posts);
        Invalid(publicPackage with { PrivateMetadata = privateMetadata });
        Invalid(publicPackage with { Envelope = publicPackage.Envelope with { EnvelopeVersion = 2 } });
        Invalid(publicPackage with { Envelope = publicPackage.Envelope with { PrivateMetadata = privatePackage.Envelope.PrivateMetadata } });
        Invalid(publicPackage with { Envelope = publicPackage.Envelope with { PrivateMetadataBytes = privatePackage.Envelope.PrivateMetadataBytes } });
        Invalid(publicPackage with { Envelope = publicPackage.Envelope with { PrivateMetadataBytes = default } });
    }

    [Fact]
    public async Task KnownVersionOnlyAndUnavailableRetainedOriginalDoNotInventCurrentPostsOrPreviews()
    {
        TransferAttachment a = Media(post: "999", status: TransferContentStatus.Unavailable);
        a = a with { OriginalByteVersion = a.OriginalByteVersion! with { Length = long.MaxValue } };
        NativeTransferPackage package = await Validated(Single(a, retained: true));
        Assert.True(package.IsChanV4);
        TransferImportPreview result = Inventory(package);
        Assert.Equal(0, result.Posts);
        Assert.Equal(Zero, result.CurrentOriginals);
        Assert.Equal(Zero, result.CurrentPreviews);
        Assert.Equal(Count(TransferContentStatus.Unavailable), result.RetainedBindings);
        Assert.Equal(Zero, result.DerivedPreviews);
        Assert.Equal(Zero, result.VideoPosters);
        Assert.Equal(0, result.UniquePayloads);
        Assert.Equal(0L, result.UniquePayloadBytes);
    }

    [Fact]
    public async Task MaximumV4ThreadInventoryIncludesEmptyThreadsWithoutInventingRepresentations()
    {
        TransferThread[] threads = Enumerable.Range(0, 1000).Select(i => Thread(new("opaque", "b",
            i.ToString(System.Globalization.CultureInfo.InvariantCulture)))).ToArray();
        ResourceId firstId = threads[0].Id;
        threads[0] = threads[0] with { Posts = [Post(Media(firstId))] };
        NativeTransferPackage package = await Validated(Catalog(threads));
        Assert.True(package.IsChanV4);
        TransferImportPreview result = Inventory(package);
        Assert.Equal(1000, result.Threads.Length);
        Assert.Equal(1, result.Posts);
        Assert.Equal(Count(TransferContentStatus.Omitted), result.CurrentOriginals);
        Assert.Equal(Zero, result.CurrentPreviews);
        Assert.Equal(Zero, result.DerivedPreviews);
        Assert.Equal(Zero, result.VideoPosters);
        Assert.Equal(0, result.UniquePayloads);
        Assert.Equal(0L, result.UniquePayloadBytes);
        Assert.All(result.Threads.Skip(1), t =>
        {
            Assert.Equal(0, t.Posts);
            Assert.Equal(Zero, t.CurrentOriginals);
            Assert.Equal(Zero, t.DerivedPreviews);
            Assert.Equal(Zero, t.VideoPosters);
        });
    }

    [Theory]
    [InlineData("chan-v1")] [InlineData("chan-v2")] [InlineData("chan-v3")]
    public async Task DownlevelWriterFixturesKeepOldCountsAndZeroDefaultDerivedProperties(string schema)
    {
        TransferAttachment a = Media(post: schema == "chan-v2" ? "999" : "15",
            status: schema == "chan-v3" ? TransferContentStatus.Contained : TransferContentStatus.Omitted)
            with { OriginalByteVersion = null };
        if (schema == "chan-v1") a = a with { Metadata = a.Metadata with { AttachmentRevisionKey = null } };
        NativeTransferPackage package = await Validated(Single(a, retained: schema == "chan-v2"),
            schema == "chan-v3" ? [Original] : []);
        Assert.Equal(schema, package.Envelope.ContentSchema);
        TransferImportPreview result = Inventory(package);
        Assert.Equal(Zero, result.DerivedPreviews);
        Assert.Equal(Zero, result.VideoPosters);
        TransferImportThreadPreview thread = Assert.Single(result.Threads);
        Assert.Equal(Zero, thread.DerivedPreviews);
        Assert.Equal(Zero, thread.VideoPosters);
        Assert.Equal(schema == "chan-v2" ? 0 : 1, result.Posts);
        Assert.Equal(schema == "chan-v2" ? Zero : Count(a.Status), result.CurrentOriginals);
        Assert.Equal(schema == "chan-v2" ? Count(a.Status) : Zero, result.RetainedBindings);
        Assert.Equal(Zero, result.CurrentPreviews);
        Assert.Equal(schema == "chan-v3" ? 1 : 0, result.UniquePayloads);
        Assert.Equal(schema == "chan-v3" ? (long)Original.Length : 0L, result.UniquePayloadBytes);
        // Positional constructors still work; new counters are initialized without callers supplying them.
        TransferImportThreadPreview oldThread = new(Id, 0, Zero, Zero, Zero);
        TransferImportPreview oldAggregate = new([oldThread], 0, Zero, Zero, Zero, 0, 0);
        Assert.Equal(Zero, oldThread.DerivedPreviews);
        Assert.Equal(Zero, oldThread.VideoPosters);
        Assert.Equal(Zero, oldAggregate.DerivedPreviews);
        Assert.Equal(Zero, oldAggregate.VideoPosters);
    }

    [Fact]
    public async Task LegalTextOnlyWireV4DoesNotInventRepresentationsOrEraseNullPresence()
    {
        TransferPost nullPost = new(new("null", 0, PostFieldPresence.Attachments, Attachments: null), []) { AttachmentsWereNull = true };
        TransferPost emptyPost = new(new("empty", 1, PostFieldPresence.Attachments, Attachments: ImmutableArray<CapturedAttachment>.Empty), []);
        NativeTransferPackage old = await Validated(Catalog(Thread(posts: [nullPost, emptyPost]), Thread(new("opaque", "b", "2"))));
        JsonNode catalog = JsonNode.Parse(old.Envelope.CatalogBytes.AsSpan())!;
        catalog["schema"] = "chan-v4";
        foreach (JsonNode? t in catalog["threads"]!.AsArray()) t!["retainedBindings"] = new JsonArray();
        byte[] content = Encoding.UTF8.GetBytes(catalog.ToJsonString());
        JsonObject manifest = new()
        {
            ["format"] = "meowchan-transfer", ["envelopeVersion"] = 1, ["packageId"] = old.Envelope.PackageId,
            ["contentSchema"] = "chan-v4", ["requiredFeatures"] = new JsonArray("chan-v4"),
            ["catalog"] = new JsonObject { ["length"] = content.Length, ["sha256"] = Hash(content) }, ["payloads"] = new JsonArray()
        };
        using MemoryStream bytes = new();
        using (ZipArchive zip = new(bytes, ZipArchiveMode.Create, true))
        {
            using (Stream entry = zip.CreateEntry("manifest.json", CompressionLevel.NoCompression).Open())
                entry.Write(Encoding.UTF8.GetBytes(manifest.ToJsonString()));
            using (Stream entry = zip.CreateEntry("catalog.json", CompressionLevel.NoCompression).Open()) entry.Write(content);
        }
        TransferPackageInspection inspected = await NativeTransferPackageValidator.InspectAsync(bytes);
        Assert.True(inspected.IsPackageValid, inspected.ToString());
        Assert.True(inspected.Package!.IsChanV4);
        Assert.Equal("chan-v1", NativeTransferCatalogSerializer.ContentSchema(inspected.Package.Catalog));
        TransferImportPreview result = Inventory(inspected.Package);
        Assert.Equal(2, result.Posts);
        Assert.Equal(2, result.Threads.Length);
        Assert.Equal(0, result.Threads[1].Posts);
        Assert.Equal(Zero, result.DerivedPreviews);
        Assert.Equal(Zero, result.VideoPosters);
        Assert.Equal(0, result.UniquePayloads);
        Assert.Equal(0L, result.UniquePayloadBytes);
        Assert.True(inspected.Package.Catalog.Threads[0].Posts[0].AttachmentsWereNull);
        Assert.Null(inspected.Package.Catalog.Threads[0].Posts[0].Metadata.Attachments);
        Assert.False(inspected.Package.Catalog.Threads[0].Posts[1].AttachmentsWereNull);
    }

    [Fact]
    public async Task CancellationIsNotReportedAsDerivedCancellationOrPartialInventory()
    {
        NativeTransferPackage package = await Validated(Single(Derived(Media())), [PreviewBytes, Poster]);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        OperationCanceledException error = Assert.Throws<OperationCanceledException>(
            () => NativeTransferImportPreview.Create(package, cancelled.Token));
        Assert.Equal(cancelled.Token, error.CancellationToken);
        Assert.Throws<OperationCanceledException>(() => NativeTransferImportPreview.Create(null, cancelled.Token));
        Assert.Equal(0, Inventory(package).DerivedPreviews.Cancelled);
    }

    private static TransferImportStatusCounts Count(TransferContentStatus status) => new(
        status == TransferContentStatus.Contained ? 1 : 0, status == TransferContentStatus.Omitted ? 1 : 0,
        status == TransferContentStatus.Unknown ? 1 : 0, status == TransferContentStatus.Unavailable ? 1 : 0,
        status == TransferContentStatus.Failed ? 1 : 0, status == TransferContentStatus.Cancelled ? 1 : 0);

    private sealed class ThrowOnReadList : IReadOnlyList<CapturedAttachment>
    {
        public int Count => throw new InvalidOperationException("Must not inspect arbitrary DTO collections.");
        public CapturedAttachment this[int index] => throw new InvalidOperationException();
        public IEnumerator<CapturedAttachment> GetEnumerator() => throw new InvalidOperationException();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => throw new InvalidOperationException();
    }

    // Structurally valid 1x1 grayscale PNG, independent CRC writer; no video derivation is claimed.
    private static byte[] CreatePoster()
    {
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 1);
        header[8] = 8;
        byte[] Chunk(string type, byte[] data)
        {
            byte[] result = new byte[data.Length + 12];
            BinaryPrimitives.WriteUInt32BigEndian(result, (uint)data.Length);
            Encoding.ASCII.GetBytes(type).CopyTo(result, 4);
            data.CopyTo(result, 8);
            uint crc = uint.MaxValue;
            foreach (byte value in result.AsSpan(4, data.Length + 4))
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u);
            }
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(result.Length - 4), ~crc);
            return result;
        }
        return [137, 80, 78, 71, 13, 10, 26, 10, .. Chunk("IHDR", header),
            .. Chunk("IDAT", [0x78, 0x9c, 0x63, 0x60, 0x00, 0x00, 0x00, 0x02, 0x00, 0x01]), .. Chunk("IEND", [])];
    }
}

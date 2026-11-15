using System.Collections.Immutable;
using System.Globalization;
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

// All fixtures are synthetic; no stores, profile data, plugins or filesystem are consulted.
public sealed class NativeTransferCatalogSerializerTests
{
    private const int MiB = 1024 * 1024;
    private const string WireDate = "2026-11-01T09:00:00.1234567Z";
    private static readonly DateTimeOffset When = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero).AddTicks(1234567);
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("synthetic transfer codec fixture");
    private static readonly ResourceId Id = new("4chan", "b", "7001");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static TransferAttachment Media() => new(new CapturedAttachment(0, "111"),
        TransferMediaRole.Preview, TransferContentStatus.Unknown, null, null);
    private static TransferPost Post(params TransferAttachment[] attachments) => new(
        new CapturedPost("15", long.MaxValue, PostFieldPresence.All, Subject: "", Html: "",
            Attachments: attachments.Select(a => a.Metadata).ToImmutableArray()), attachments.ToImmutableArray());
    private static TransferThread Thread(params TransferPost[] posts) => new(Id, null, "synthetic", ThreadSourceState.NotFound,
        When, null, 0, false, posts.ToImmutableArray());
    private static NativeTransferCatalog Catalog(params TransferPost[] posts) => new(When, [Thread(posts)]);
    private static NativeTransferCatalog WithPost(TransferPost post) => Catalog(post);
    private static NativeTransferCatalog WithMedia(TransferAttachment attachment) => Catalog(Post(attachment));

    // Caller-built envelope with independently measured bytes, not serializer-generated integrity claims.
    private static TransferCatalogInspection Inspect(byte[] bytes, params byte[][] payloads)
    {
        NativeTransferEnvelope envelope = new("5d7750f7-1c3a-4093-b770-7c854fcfb117", "chan-v1", "unused-by-catalog-validator",
            new(bytes.Length, Hash(bytes)), payloads.Select(p => new TransferBlobDescriptor(p.Length, Hash(p))).ToImmutableArray())
        {
            CatalogBytes = bytes.ToImmutableArray(),
            MeasuredPayloads = payloads.ToImmutableDictionary(Hash, p => new TransferMeasuredBlob(p.Length, Hash(p),
                Convert.ToBase64String(MD5.HashData(p)), Convert.ToBase64String(SHA1.HashData(p))), StringComparer.Ordinal)
        };
        return NativeTransferCatalogValidator.Inspect(bytes, envelope);
    }
    private static NativeTransferCatalog RoundTrip(NativeTransferCatalog input, params byte[][] payloads)
    {
        byte[] bytes = NativeTransferCatalogSerializer.Serialize(input);
        TransferCatalogInspection result = Inspect(bytes, payloads);
        Assert.True(result.IsCatalogValid, result.Error.ToString());
        Assert.Equal(bytes, NativeTransferCatalogSerializer.Serialize(result.Catalog!));
        return result.Catalog!;
    }
    private static void Reject(NativeTransferCatalog input)
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => NativeTransferCatalogSerializer.Serialize(input));
        Assert.Equal("Unsupported transfer catalog projection.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("SYNTHETIC-SECRET", error.ToString());
    }

    [Fact]
    public void OptionalImageDimensionsSurviveThePublicCatalogManifestRoundTrip()
    {
        TransferAttachment input = new(new CapturedAttachment(0, "111") { Width = 640, Height = 480 },
            TransferMediaRole.Preview, TransferContentStatus.Unknown, null, null);
        TransferAttachment output = Assert.Single(RoundTrip(Catalog(Post(input))).Threads.Single().Posts.Single().Attachments);
        Assert.Equal(640, output.Metadata.Width);
        Assert.Equal(480, output.Metadata.Height);
    }

    [Fact]
    public void PrecancelledTokenPropagatesBeforeValidationOrWriting()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        OperationCanceledException error = Assert.Throws<OperationCanceledException>(
            () => NativeTransferCatalogSerializer.Serialize(null!, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        error = Assert.Throws<OperationCanceledException>(
            () => NativeTransferCatalogSerializer.Serialize(Catalog(Post(Media())), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public void NoncancelledOptionalTokenDoesNotChangeGoldenOrBoundCatalogBytes()
    {
        using CancellationTokenSource cancellation = new();
        foreach (NativeTransferCatalog input in new[] { Catalog(Post(Media())), Bound(true) })
        {
            byte[] expected = NativeTransferCatalogSerializer.Serialize(input);
            Assert.Equal(expected, NativeTransferCatalogSerializer.Serialize(input, default));
            Assert.Equal(expected, NativeTransferCatalogSerializer.Serialize(input, cancellation.Token));
        }
    }

    [Fact]
    public void CancellationDuringProjectionStopsBeforeReadingTheNextAttachment()
    {
        using CancellationTokenSource cancellation = new();
        TransferAttachment first = Media(), second = Media() with { Metadata = Media().Metadata with { Position = 1 } };
        TransferPost post = Post(first, second);
        CancelOnReadAttachments metadata = new([first.Metadata, second.Metadata], cancellation);
        post = post with { Metadata = post.Metadata with { Attachments = metadata } };
        Assert.False(cancellation.IsCancellationRequested);
        OperationCanceledException error = Assert.Throws<OperationCanceledException>(
            () => NativeTransferCatalogSerializer.Serialize(Catalog(post), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        // The first metadata entry is read for null and equality; entry 2 must never be read.
        Assert.Equal(2, metadata.ReadCount);
    }

    // Deterministic test-only cancellation trigger through the existing IReadOnlyList DTO surface.
    private sealed class CancelOnReadAttachments(ImmutableArray<CapturedAttachment> items,
        CancellationTokenSource cancellation) : IReadOnlyList<CapturedAttachment>
    {
        public int Count => items.Length;
        internal int ReadCount { get; private set; }
        public CapturedAttachment this[int index]
        {
            get
            {
                ReadCount++;
                if (index == 0) cancellation.Cancel();
                return items[index];
            }
        }
        public IEnumerator<CapturedAttachment> GetEnumerator() => ((IEnumerable<CapturedAttachment>)items).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void GoldenBytesHaveExplicitOrderNullsAndNoBomOrWhitespace()
    {
        const string golden = """
            {"schema":"chan-v1","exportedAtUtc":"2026-11-01T09:00:00.1234567Z","threads":[{"id":{"source":"4chan","board":"b","thread":"7001"},"pageUrl":null,"title":"synthetic","sourceState":"not-found","firstSeenAtUtc":"2026-11-01T09:00:00.1234567Z","capturedAtUtc":null,"captureRevision":0,"decimalPostOrder":false,"posts":[{"id":"15","ordinal":9223372036854775807,"fields":63,"postedAtUtc":null,"subject":"","author":null,"html":"","isDeleted":false,"attachments":[{"position":0,"mediaId":"111","variant":"original","url":null,"originalFileName":null,"expectedSize":null,"expectedMd5":null,"expectedSha1":null,"expectedSha256":null,"sizeHint":null,"isDeleted":false,"revisionKey":null,"role":"preview","status":"unknown","payloadSha256":null,"history":null}]}]}]}
            """;
        byte[] bytes = NativeTransferCatalogSerializer.Serialize(Catalog(Post(Media())));
        Assert.Equal(Encoding.UTF8.GetBytes(golden), bytes);
        Assert.Equal(bytes, NativeTransferCatalogSerializer.Serialize(Catalog(Post(Media()))));
        RoundTrip(Catalog(Post(Media())));
    }

    [Fact]
    public void EmptyCatalogHasExactGoldenBytes()
    {
        Assert.Equal(Encoding.UTF8.GetBytes("{\"schema\":\"chan-v1\",\"exportedAtUtc\":\"" + WireDate + "\",\"threads\":[]}"),
            NativeTransferCatalogSerializer.Serialize(new(When, [])));
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("tr-TR")]
    [InlineData("ar-SA")]
    public void DateNumbersAndBytesAreCultureIndependent(string culture)
    {
        NativeTransferCatalog input = Catalog(Post(Media()));
        byte[] expected = NativeTransferCatalogSerializer.Serialize(input);
        CultureInfo oldCulture = CultureInfo.CurrentCulture, oldUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal(expected, NativeTransferCatalogSerializer.Serialize(input));
        }
        finally { CultureInfo.CurrentCulture = oldCulture; CultureInfo.CurrentUICulture = oldUi; }
    }

    [Theory]
    [InlineData(0)] // absent bit and null metadata: canonical []
    [InlineData(1)] // present null metadata without parser flag: null
    [InlineData(2)] // present observed empty collection: []
    [InlineData(3)] // parser-projected present null: null
    public void NullAttachmentCollectionIsNotSilentlyConvertedToEmpty(int mode)
    {
        PostFieldPresence fields = mode == 0 ? PostFieldPresence.None : PostFieldPresence.Attachments;
        CapturedPost metadata = new("15", 0, fields, Attachments: mode == 2 ? ImmutableArray<CapturedAttachment>.Empty : null);
        TransferPost post = new(metadata, []) { AttachmentsWereNull = mode == 3 };
        byte[] bytes = NativeTransferCatalogSerializer.Serialize(Catalog(post));
        using JsonDocument json = JsonDocument.Parse(bytes);
        JsonElement wire = json.RootElement.GetProperty("threads")[0].GetProperty("posts")[0].GetProperty("attachments");
        Assert.Equal(mode is 1 or 3 ? JsonValueKind.Null : JsonValueKind.Array, wire.ValueKind);
        NativeTransferCatalog parsed = RoundTrip(Catalog(post));
        Assert.Equal(mode is 1 or 3, parsed.Threads[0].Posts[0].AttachmentsWereNull);
        Assert.Equal(mode is 1 or 3, parsed.Threads[0].Posts[0].Metadata.Attachments is null);
    }

    [Fact]
    public void PresentNullEmptyAndExactInertTextAreSeparateObservations()
    {
        TransferPost absent = new(new("2", 0, PostFieldPresence.None), []);
        TransferPost nulls = new(new("3", 1, PostFieldPresence.All), []);
        TransferPost empty = new(new("4", 2, PostFieldPresence.All, Subject: "", Author: "", Html: "",
            Attachments: ImmutableArray<CapturedAttachment>.Empty), []);
        const string html = "<span class='quote'>synthetic &amp; exact</span><br>";
        TransferPost text = new(new("5", 3, PostFieldPresence.All, When, "e\u0301", "\u00e9", html, true,
            ImmutableArray<CapturedAttachment>.Empty), []);
        NativeTransferCatalog parsed = RoundTrip(Catalog(absent, nulls, empty, text));
        Assert.Equal(PostFieldPresence.None, parsed.Threads[0].Posts[0].Metadata.Fields);
        Assert.Equal(PostFieldPresence.All, parsed.Threads[0].Posts[1].Metadata.Fields);
        Assert.Null(parsed.Threads[0].Posts[1].Metadata.Subject);
        Assert.Null(parsed.Threads[0].Posts[1].Metadata.Author);
        Assert.Null(parsed.Threads[0].Posts[1].Metadata.Html);
        Assert.Equal("", parsed.Threads[0].Posts[2].Metadata.Subject);
        Assert.Equal("", parsed.Threads[0].Posts[2].Metadata.Author);
        Assert.Equal("", parsed.Threads[0].Posts[2].Metadata.Html);
        Assert.Equal(text.Metadata.Subject, parsed.Threads[0].Posts[3].Metadata.Subject);
        Assert.Equal(text.Metadata.Author, parsed.Threads[0].Posts[3].Metadata.Author);
        Assert.Equal(html, parsed.Threads[0].Posts[3].Metadata.Html);
    }

    private static NativeTransferCatalog Bound(bool v2, bool mismatch = false)
    {
        string md5 = " \t" + Convert.ToBase64String(MD5.HashData(Payload)).Insert(4, "\r\n") + " ";
        string? sha1 = v2 ? " " + Convert.ToBase64String(SHA1.HashData(Payload)).Insert(4, " \n") + "\t" : null;
        MediaProviderSizeHint? hint = v2 ? new("1.50", "KiB", MediaSizePrecision.Truncated) : null;
        MediaProviderMetadata provider = new("111", "original", "https://i.4cdn.org/b/111.jpg", "synthetic.jpg",
            Payload.Length + (mismatch ? 1 : 0), md5, Hash(Payload).ToUpperInvariant())
            { ExpectedSha1 = sha1, ProviderSizeHint = hint };
        CapturedAttachment metadata = new(0, provider.MediaId, provider.Variant, provider.Url, provider.OriginalFileName,
            PlannedPath: "/SYNTHETIC-SECRET/local-only.jpg", ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
        {
            ExpectedSha1 = sha1, ExpectedSha256 = provider.ExpectedSha256, ProviderSizeHint = hint,
            AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(Id, "15", 0, provider)
        };
        return WithMedia(new(metadata, TransferMediaRole.Original, TransferContentStatus.Contained, Hash(Payload),
            new(mismatch ? "provider-mismatch" : "strict", When, mismatch, false)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RawDigestSpellingsAndExistingV1V2KeysSurviveExactly(bool v2)
    {
        NativeTransferCatalog input = Bound(v2);
        TransferAttachment original = input.Threads[0].Posts[0].Attachments[0];
        TransferAttachment parsed = RoundTrip(input, Payload).Threads[0].Posts[0].Attachments[0];
        Assert.StartsWith(v2 ? "att-v2:" : "att-v1:", parsed.Metadata.AttachmentRevisionKey);
        Assert.Equal(original.Metadata.ExpectedMd5, parsed.Metadata.ExpectedMd5);
        Assert.Equal(original.Metadata.ExpectedSha1, parsed.Metadata.ExpectedSha1);
        Assert.Equal(original.Metadata.ExpectedSha256, parsed.Metadata.ExpectedSha256);
        Assert.Equal(original.Metadata.AttachmentRevisionKey, parsed.Metadata.AttachmentRevisionKey);
        Assert.Equal(original.Metadata.ProviderSizeHint, parsed.Metadata.ProviderSizeHint);
        Assert.Equal(original.History, parsed.History);
        Assert.Null(parsed.Metadata.PlannedPath);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void RawSizeHintAndZeroExpectedSizeAreNotReinterpreted(int precision)
    {
        TransferAttachment media = Media() with { Metadata = Media().Metadata with
            { ExpectedSize = 0, ProviderSizeHint = new(" 1.50 ", "KiB", (MediaSizePrecision)precision) } };
        CapturedAttachment parsed = RoundTrip(WithMedia(media)).Threads[0].Posts[0].Attachments[0].Metadata;
        Assert.Equal(0L, parsed.ExpectedSize);
        Assert.Equal(media.Metadata.ProviderSizeHint, parsed.ProviderSizeHint);
        Assert.Null(parsed.AttachmentRevisionKey);
    }

    [Fact]
    public void OnlyPassiveAllowlistedFieldsAreWritten()
    {
        byte[] bytes = NativeTransferCatalogSerializer.Serialize(Bound(true));
        string text = Encoding.UTF8.GetString(bytes);
        foreach (string forbidden in new[] { "SYNTHETIC-SECRET", "plannedPath", "PlannedPath", "profileId", "pluginOwnerId",
            "policyRevision", "allowProviderSizeMd5Mismatch", "captureEnabled", "favorites", "privateTags", "grants", "watch", "retry" })
            Assert.DoesNotContain(forbidden, text);
        using JsonDocument json = JsonDocument.Parse(bytes);
        JsonElement media = json.RootElement.GetProperty("threads")[0].GetProperty("posts")[0].GetProperty("attachments")[0];
        Assert.Equal(new[] { "kind", "acceptedAtUtc", "sizeMismatch", "md5Mismatch" },
            media.GetProperty("history").EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(new[] { "rawValue", "unitLabel", "precision" },
            media.GetProperty("sizeHint").EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void HistoricalMismatchStaysPassiveAndMeasuredValidatorStillDecides()
    {
        NativeTransferCatalog input = Bound(true, mismatch: true);
        Assert.Equal("provider-mismatch", RoundTrip(input, Payload).Threads[0].Posts[0].Attachments[0].History!.Kind);
        TransferAttachment media = input.Threads[0].Posts[0].Attachments[0];
        // Internally coherent but wrong measured flag: serialization is not admission.
        input = WithMedia(media with { History = media.History! with { SizeMismatch = false, Md5Mismatch = true } });
        byte[] bytes = NativeTransferCatalogSerializer.Serialize(input);
        Assert.Equal(TransferCatalogError.InvalidHistory, Inspect(bytes, Payload).Error);
    }

    [Fact]
    public async Task PackageRoundTripUsesHandbuiltStoredEnvelopeAndActualPayloadMeasurement()
    {
        byte[] bytes = NativeTransferCatalogSerializer.Serialize(Bound(true));
        JsonObject Blob(byte[] b) => new() { ["length"] = b.Length, ["sha256"] = Hash(b) };
        JsonObject manifest = new()
        {
            ["format"] = "meowchan-transfer", ["envelopeVersion"] = 1,
            ["packageId"] = "5d7750f7-1c3a-4093-b770-7c854fcfb117", ["contentSchema"] = "chan-v1",
            ["requiredFeatures"] = new JsonArray("chan-v1"), ["catalog"] = Blob(bytes), ["payloads"] = new JsonArray(Blob(Payload))
        };
        using MemoryStream stream = new();
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create, true))
        {
            Add("manifest.json", Encoding.UTF8.GetBytes(manifest.ToJsonString()));
            Add("catalog.json", bytes); Add("payload/" + Hash(Payload), Payload);
            void Add(string name, byte[] content)
            {
                using Stream target = zip.CreateEntry(name, CompressionLevel.NoCompression).Open(); target.Write(content);
            }
        }
        stream.Position = 7;
        TransferPackageInspection result = await NativeTransferPackageValidator.InspectAsync(stream);
        Assert.True(result.IsPackageValid, $"{result.EnvelopeError}/{result.CatalogError}");
        Assert.Equal(7, stream.Position);
        Assert.Equal(bytes, NativeTransferCatalogSerializer.Serialize(result.Package!.Catalog));
        TransferMeasuredBlob measured = Assert.Single(result.Package.Envelope.MeasuredPayloads).Value;
        Assert.Equal(Payload.Length, measured.Length);
        Assert.Equal(Convert.ToBase64String(SHA1.HashData(Payload)), measured.Sha1);
    }

    [Fact]
    public void StatesRolesStatusesArrayOrderAndDeletionMarkersAreNotInferredOrSorted()
    {
        TransferContentStatus[] statuses = [TransferContentStatus.Cancelled, TransferContentStatus.Unknown, TransferContentStatus.Contained,
            TransferContentStatus.Failed, TransferContentStatus.Omitted, TransferContentStatus.Unavailable];
        TransferAttachment[] media = statuses.Select((s, i) => new TransferAttachment(
            new CapturedAttachment(10 - i, s == TransferContentStatus.Unavailable ? null : "111", IsDeleted: s == TransferContentStatus.Unavailable),
            i % 2 == 0 ? TransferMediaRole.Preview : TransferMediaRole.Original, s, s == TransferContentStatus.Contained ? Hash(Payload) : null, null)).ToArray();
        TransferPost a = Post(media), b = Post() with { Metadata = Post().Metadata with { PostId = "2", Ordinal = 0 } };
        TransferThread[] threads = new[] { ThreadSourceState.NotFound, ThreadSourceState.Active, ThreadSourceState.Archived }
            .Select((state, i) => Thread(a, b) with { Id = new("4chan", "b", (9 - i).ToString(CultureInfo.InvariantCulture)), SourceState = state }).ToArray();
        NativeTransferCatalog parsed = RoundTrip(new(When, threads.ToImmutableArray()), Payload);
        Assert.Equal(new[] { "9", "8", "7" }, parsed.Threads.Select(t => t.Id.Thread).ToArray());
        Assert.Equal(threads.Select(t => t.SourceState), parsed.Threads.Select(t => t.SourceState));
        foreach (TransferThread thread in parsed.Threads)
        {
            Assert.Equal(new[] { "15", "2" }, thread.Posts.Select(p => p.Metadata.PostId).ToArray());
            Assert.Equal(statuses, thread.Posts[0].Attachments.Select(m => m.Status).ToArray());
            Assert.Equal(media.Select(m => m.Role), thread.Posts[0].Attachments.Select(m => m.Role));
            Assert.Equal(new[] { 10, 9, 8, 7, 6, 5 }, thread.Posts[0].Attachments.Select(m => m.Metadata.Position).ToArray());
            Assert.True(thread.Posts[0].Attachments[5].Metadata.IsDeleted);
            Assert.Null(thread.Posts[0].Attachments[5].Metadata.MediaId);
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void NullableTimesRevisionAndOrderingContractStayDistinct(int mode)
    {
        TransferPost post = Post() with { Metadata = Post().Metadata with { PostedAt = mode == 0 ? null : When, Subject = mode == 0 ? null : "", IsDeleted = mode == 2 } };
        TransferThread thread = Thread(post) with
        {
            CapturedAtUtc = mode == 0 ? null : When.ToOffset(TimeSpan.FromHours(2)),
            CaptureRevision = mode == 0 ? null : mode == 1 ? 0 : long.MaxValue,
            DecimalPostOrder = mode == 0 ? null : mode == 1 ? false : true
        };
        NativeTransferCatalog parsed = RoundTrip(new(When.ToOffset(TimeSpan.FromHours(-3)), [thread]));
        Assert.Equal(thread.CapturedAtUtc, parsed.Threads[0].CapturedAtUtc);
        Assert.Equal(thread.CaptureRevision, parsed.Threads[0].CaptureRevision);
        Assert.Equal(thread.DecimalPostOrder, parsed.Threads[0].DecimalPostOrder);
        Assert.Equal(post.Metadata.PostedAt, parsed.Threads[0].Posts[0].Metadata.PostedAt);
        Assert.Equal(post.Metadata.Subject, parsed.Threads[0].Posts[0].Metadata.Subject);
        Assert.Equal(post.Metadata.IsDeleted, parsed.Threads[0].Posts[0].Metadata.IsDeleted);
    }

    [Theory]
    [InlineData("state")] [InlineData("role")] [InlineData("status")]
    [InlineData("precision")] [InlineData("fields")]
    public void UnsupportedEnumValuesAreSanitized(string which)
    {
        TransferAttachment media = Media(); TransferPost post = Post(media);
        NativeTransferCatalog input = which switch
        {
            "state" => new(When, [Thread(post) with { SourceState = (ThreadSourceState)999 }]),
            "role" => WithMedia(media with { Role = (TransferMediaRole)999 }),
            "status" => WithMedia(media with { Status = (TransferContentStatus)(-1) }),
            "precision" => WithMedia(media with { Metadata = media.Metadata with { ProviderSizeHint = new("1", "KiB", (MediaSizePrecision)999) } }),
            _ => WithPost(post with { Metadata = post.Metadata with { Fields = (PostFieldPresence)64 } })
        };
        Reject(input);
    }

    [Theory]
    [InlineData("catalog")] [InlineData("threads")] [InlineData("thread")] [InlineData("identity")]
    [InlineData("source")] [InlineData("board")] [InlineData("threadId")] [InlineData("title")]
    [InlineData("posts")] [InlineData("post")] [InlineData("postMetadata")] [InlineData("postId")]
    [InlineData("attachments")] [InlineData("attachment")] [InlineData("attachmentMetadata")]
    [InlineData("capturedDefault")] [InlineData("variant")] [InlineData("hintRaw")] [InlineData("hintUnit")]
    public void NullAndDefaultRequiredValuesRejectWithoutDumpingDtos(string which)
    {
        TransferAttachment media = Media(); TransferPost post = Post(media); TransferThread thread = Thread(post);
        NativeTransferCatalog input = which switch
        {
            "catalog" => null!, "threads" => new(When, default), "thread" => new(When, [null!]),
            "identity" => new(When, [thread with { Id = null! }]),
            "source" => new(When, [thread with { Id = Id with { Source = null! } }]),
            "board" => new(When, [thread with { Id = Id with { Board = null! } }]),
            "threadId" => new(When, [thread with { Id = Id with { Thread = null } }]),
            "title" => new(When, [thread with { Title = null! }]), "posts" => new(When, [thread with { Posts = default }]),
            "post" => Catalog((TransferPost)null!), "postMetadata" => WithPost(post with { Metadata = null! }),
            "postId" => WithPost(post with { Metadata = post.Metadata with { PostId = null! } }),
            "attachments" => WithPost(post with { Attachments = default }),
            "attachment" => WithPost(post with { Attachments = [null!] }),
            "attachmentMetadata" => WithMedia(media with { Metadata = null! }),
            "capturedDefault" => WithPost(post with { Metadata = post.Metadata with { Attachments = default(ImmutableArray<CapturedAttachment>) } }),
            "variant" => WithMedia(media with { Metadata = media.Metadata with { Variant = null! } }),
            "hintRaw" => WithMedia(media with { Metadata = media.Metadata with { ProviderSizeHint = new(null!, "KiB") } }),
            _ => WithMedia(media with { Metadata = media.Metadata with { ProviderSizeHint = new("1", null!) } })
        };
        Reject(input);
    }

    [Theory]
    [InlineData("count")] [InlineData("value")] [InlineData("order")] [InlineData("metadataNull")]
    [InlineData("flag")] [InlineData("absentFlag")] [InlineData("absentCollection")]
    public void DualAttachmentProjectionMismatchIsNeverSilentlyDropped(string which)
    {
        TransferAttachment media = Media(), another = Media() with { Metadata = Media().Metadata with { Position = 1 } };
        TransferPost post = Post(media, another);
        post = which switch
        {
            "count" => post with { Attachments = [media] },
            "value" => post with { Attachments = [media with { Metadata = media.Metadata with { Url = "SYNTHETIC-SECRET" } }, another] },
            "order" => post with { Attachments = [another, media] },
            "metadataNull" => post with { Metadata = post.Metadata with { Attachments = null } },
            "flag" => post with { AttachmentsWereNull = true },
            "absentFlag" => new(new("15", 0, PostFieldPresence.None), []) { AttachmentsWereNull = true },
            _ => post with { Metadata = post.Metadata with { Fields = PostFieldPresence.None, Subject = null, Html = null } }
        };
        Reject(WithPost(post));
    }

    [Theory]
    [InlineData("posted")] [InlineData("subject")] [InlineData("author")] [InlineData("html")] [InlineData("deleted")]
    public void AbsentPresenceBitsCannotHideValues(string which)
    {
        CapturedPost metadata = new("15", 0, PostFieldPresence.None);
        metadata = which switch
        {
            "posted" => metadata with { PostedAt = When }, "subject" => metadata with { Subject = "SYNTHETIC-SECRET" },
            "author" => metadata with { Author = "" }, "html" => metadata with { Html = "" }, _ => metadata with { IsDeleted = true }
        };
        Reject(WithPost(new(metadata, [])));
    }

    [Theory]
    [InlineData("position")] [InlineData("size")] [InlineData("ordinal")] [InlineData("revision")]
    [InlineData("exportDate")] [InlineData("firstDate")] [InlineData("captureDate")] [InlineData("postDate")]
    public void NegativeNumbersAndDefaultDatesAreNotRewritten(string which)
    {
        TransferAttachment media = Media(); TransferPost post = Post(media);
        NativeTransferCatalog input = which switch
        {
            "position" => WithMedia(media with { Metadata = media.Metadata with { Position = -1 } }),
            "size" => WithMedia(media with { Metadata = media.Metadata with { ExpectedSize = -1 } }),
            "ordinal" => WithPost(post with { Metadata = post.Metadata with { Ordinal = -1 } }),
            "revision" => new(When, [Thread(post) with { CaptureRevision = -1 }]),
            "exportDate" => new(default, [Thread(post)]), "firstDate" => new(When, [Thread(post) with { FirstSeenAtUtc = default }]),
            "captureDate" => new(When, [Thread(post) with { CapturedAtUtc = default(DateTimeOffset) }]),
            _ => WithPost(post with { Metadata = post.Metadata with { PostedAt = default(DateTimeOffset) } })
        };
        Reject(input);
    }

    [Fact]
    public void InvalidUnicodeAndUtf8FieldBudgetFailClosed()
    {
        Reject(new(When, [Thread() with { Title = "SYNTHETIC-SECRET" + '\ud800' }]));
        Reject(new(When, [Thread() with { Title = new string('x', MiB + 1) }]));
        Reject(new(When, [Thread() with { Title = new string('\u00e9', MiB / 2 + 1) }]));
        RoundTrip(new(When, [Thread() with { Title = new string('\u00e9', MiB / 2) }]));
    }

    [Fact]
    public void MaximumEscapingExpansionStillFitsTheBoundedReservation()
    {
        int overhead = NativeTransferCatalogSerializer.Serialize(new(When, [Thread() with { Title = "" }])).Length;
        NativeTransferCatalog input = new(When, [Thread() with { Title = new string('\0', MiB) }]);
        byte[] bytes = NativeTransferCatalogSerializer.Serialize(input);
        Assert.Equal(6 * MiB + overhead, bytes.Length);
        Assert.Equal(input.Threads[0].Title, Inspect(bytes).Catalog!.Threads[0].Title);
    }

    [Fact]
    public void ExactCatalogByteCapIsInclusiveAndNextByteRejectsBeforeUnboundedMaterialization()
    {
        TransferThread[] threads = Enumerable.Range(0, 16).Select(i => Thread() with
            { Id = new("4chan", "b", (7001 + i).ToString(CultureInfo.InvariantCulture)), Title = "" }).ToArray();
        int overhead = NativeTransferCatalogSerializer.Serialize(new(When, threads.ToImmutableArray())).Length;
        string full = new('x', MiB);
        for (int i = 0; i < 15; i++) threads[i] = threads[i] with { Title = full };
        threads[15] = threads[15] with { Title = new string('x', MiB - overhead) };
        byte[] bytes = NativeTransferCatalogSerializer.Serialize(new(When, threads.ToImmutableArray()));
        Assert.Equal(16 * MiB, bytes.Length);
        threads[15] = threads[15] with { Title = threads[15].Title + "x" };
        Reject(new(When, threads.ToImmutableArray()));
    }

    [Fact]
    public void StructuralBudgetsRejectCallerConstructedCollections()
    {
        Reject(new(When, Enumerable.Repeat(Thread(), 1001).ToImmutableArray()));
        TransferPost post = Post();
        Reject(new(When, [Thread() with { Posts = Enumerable.Repeat(post, 100001).ToImmutableArray() }]));
        TransferThread first = Thread() with { Posts = Enumerable.Repeat(post, 50000).ToImmutableArray() };
        TransferThread second = Thread() with { Posts = Enumerable.Repeat(post, 50001).ToImmutableArray() };
        Reject(new(When, [first, second]));
        TransferAttachment media = Media();
        Reject(WithPost(new(new("15", 0, PostFieldPresence.Attachments), Enumerable.Repeat(media, 100001).ToImmutableArray())));
    }

    [Theory]
    [InlineData("missingPayload")] [InlineData("unboundHistory")]
    [InlineData("noncontainedHistory")] [InlineData("noncontainedPayload")] [InlineData("unknownKind")]
    [InlineData("strictFlags")] [InlineData("mismatchFlags")] [InlineData("historyDate")] [InlineData("mediaId")]
    public void InconsistentContentAndHistoricalProjectionIsRejected(string which)
    {
        TransferAttachment media = Bound(true).Threads[0].Posts[0].Attachments[0];
        media = which switch
        {
            "missingPayload" => media with { PayloadSha256 = null },
            "unboundHistory" => media with { Metadata = media.Metadata with { AttachmentRevisionKey = null } },
            "noncontainedHistory" => media with { Status = TransferContentStatus.Omitted, PayloadSha256 = null },
            "noncontainedPayload" => media with { Status = TransferContentStatus.Cancelled, History = null },
            "unknownKind" => media with { History = media.History! with { Kind = "SYNTHETIC-SECRET" } },
            "strictFlags" => media with { History = media.History! with { SizeMismatch = true } },
            "mismatchFlags" => media with { History = media.History! with { Kind = "provider-mismatch" } },
            "historyDate" => media with { History = media.History! with { AcceptedAtUtc = default } },
            _ => media with { Metadata = media.Metadata with { MediaId = null } }
        };
        Reject(WithMedia(media));
    }

    [Fact]
    public void SerializerDoesNotTurnUnsafeInputsOrForgedRevisionClaimsIntoAdmission()
    {
        TransferAttachment media = Bound(true).Threads[0].Posts[0].Attachments[0];
        NativeTransferCatalog unsafeUrl = WithMedia(media with { Metadata = media.Metadata with { Url = "https://i.4cdn.org/b/111.jpg?token=SYNTHETIC-SECRET" } });
        Assert.Equal(TransferCatalogError.UnsafeUrl, Inspect(NativeTransferCatalogSerializer.Serialize(unsafeUrl), Payload).Error);
        NativeTransferCatalog wrongKey = WithMedia(media with { Metadata = media.Metadata with { AttachmentRevisionKey = "att-v2:unchanged-not-rekeyed" } });
        Assert.Equal(TransferCatalogError.InvalidRevision, Inspect(NativeTransferCatalogSerializer.Serialize(wrongKey), Payload).Error);
    }
}

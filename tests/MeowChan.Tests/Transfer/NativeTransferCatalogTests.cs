using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

public sealed class NativeTransferCatalogTests
{
    private const string Date = "2026-11-01T09:00:00.0000000Z";
    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("wholly synthetic bytes, not personal media");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static JsonObject Blob(byte[] bytes) => new() { ["length"] = bytes.Length, ["sha256"] = Hash(bytes) };
    private static JsonObject Catalog() => new()
    {
        ["schema"] = "chan-v1", ["exportedAtUtc"] = Date,
        ["threads"] = new JsonArray(new JsonObject
        {
            ["id"] = new JsonObject { ["source"] = "4chan", ["board"] = "b", ["thread"] = "7001" },
            ["pageUrl"] = "https://boards.4chan.org/b/thread/7001", ["title"] = "synthetic title", ["sourceState"] = "not-found",
            ["firstSeenAtUtc"] = Date, ["capturedAtUtc"] = Date, ["captureRevision"] = 15,
            ["decimalPostOrder"] = true,
            ["posts"] = new JsonArray(new JsonObject
            {
                ["id"] = "15", ["ordinal"] = long.MaxValue, ["fields"] = (int)PostFieldPresence.All,
                ["postedAtUtc"] = null, ["subject"] = "", ["author"] = null,
                ["html"] = "<span class=\"quote\">&gt;synthetic</span><br><a href=\"#p20\" class=\"quotelink\">&gt;&gt;20</a>",
                ["isDeleted"] = false, ["attachments"] = new JsonArray(Attachment())
            })
        })
    };
    private static JsonObject Attachment() => new()
    {
        ["position"] = 0, ["mediaId"] = "111", ["variant"] = "original", ["url"] = "https://i.4cdn.org/b/111.jpg",
        ["originalFileName"] = "synthetic.jpg", ["expectedSize"] = (long)Bytes.Length,
        ["expectedMd5"] = Convert.ToBase64String(MD5.HashData(Bytes)),
        ["expectedSha1"] = null, ["expectedSha256"] = Hash(Bytes), ["sizeHint"] = null,
        ["isDeleted"] = false, ["revisionKey"] = null, ["role"] = "original", ["status"] = "contained",
        ["payloadSha256"] = Hash(Bytes), ["history"] = null
    };
    private static JsonObject Thread(JsonObject catalog) => catalog["threads"]![0]!.AsObject();
    private static JsonObject Post(JsonObject catalog) => Thread(catalog)["posts"]![0]!.AsObject();
    private static JsonObject Media(JsonObject catalog) => Post(catalog)["attachments"]![0]!.AsObject();
    private static void Bind(JsonObject catalog, bool v2 = false)
    {
        JsonObject media = Media(catalog);
        if (v2) media["expectedSha1"] = Convert.ToBase64String(SHA1.HashData(Bytes));
        MediaProviderMetadata provider = new("111", "original", media["url"]!.GetValue<string>(), "synthetic.jpg",
            media["expectedSize"]?.GetValue<long>(), media["expectedMd5"]?.GetValue<string>(), media["expectedSha256"]?.GetValue<string>())
            { ExpectedSha1 = media["expectedSha1"]?.GetValue<string>() };
        media["revisionKey"] = MediaRevisionKeys.CreateAttachmentKey(new("4chan", "b", "7001"), "15", 0, provider);
        media["history"] = new JsonObject { ["kind"] = "strict", ["acceptedAtUtc"] = Date, ["sizeMismatch"] = false, ["md5Mismatch"] = false };
    }
    private static MemoryStream Package(byte[] catalog, bool withPayload = true)
    {
        JsonObject manifest = new()
        {
            ["format"] = "meowchan-transfer", ["envelopeVersion"] = 1,
            ["packageId"] = "5d7750f7-1c3a-4093-b770-7c854fcfb117", ["contentSchema"] = "chan-v1",
            ["requiredFeatures"] = new JsonArray("chan-v1"), ["catalog"] = Blob(catalog),
            ["payloads"] = withPayload ? new JsonArray(Blob(Bytes)) : new JsonArray()
        };
        MemoryStream stream = new();
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create, true))
        {
            Add("manifest.json", Encoding.UTF8.GetBytes(manifest.ToJsonString()));
            Add("catalog.json", catalog);
            if (withPayload) Add("payload/" + Hash(Bytes), Bytes);
            void Add(string name, byte[] content)
            {
                using Stream target = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                target.Write(content);
            }
        }
        stream.Position = 7;
        return stream;
    }
    private static async Task<TransferPackageInspection> Inspect(JsonObject catalog, bool withPayload = true)
    {
        using MemoryStream package = Package(Encoding.UTF8.GetBytes(catalog.ToJsonString()), withPayload);
        byte[] before = package.ToArray();
        TransferPackageInspection result = await NativeTransferPackageValidator.InspectAsync(package);
        Assert.Equal(7, package.Position); Assert.True(package.CanRead); Assert.Equal(before, package.ToArray());
        return result;
    }
    private static async Task Error(JsonObject catalog, TransferCatalogError expected, bool withPayload = true)
    {
        TransferPackageInspection result = await Inspect(catalog, withPayload);
        Assert.False(result.IsPackageValid); Assert.Null(result.Package);
        Assert.Equal(TransferEnvelopeError.None, result.EnvelopeError); Assert.Equal(expected, result.CatalogError);
        Assert.DoesNotContain("SECRET", result.ToString());
    }
    [Fact]
    public async Task LegacyNullPresence404AndInt64OrderingArePreservedWithoutNewAuthority()
    {
        TransferPackageInspection result = await Inspect(Catalog());
        Assert.True(result.IsPackageValid);
        TransferThread thread = Assert.Single(result.Package!.Catalog.Threads);
        Assert.Equal(ThreadSourceState.NotFound, thread.SourceState);
        Assert.True(thread.DecimalPostOrder); Assert.Equal(15, thread.CaptureRevision);
        TransferPost post = Assert.Single(thread.Posts);
        Assert.Null(post.Metadata.PostedAt); Assert.Null(post.Metadata.Author); Assert.Equal("", post.Metadata.Subject);
        Assert.Equal(long.MaxValue, post.Metadata.Ordinal);
        TransferAttachment attachment = Assert.Single(post.Attachments);
        Assert.Null(attachment.Metadata.AttachmentRevisionKey); Assert.Null(attachment.History);
        Assert.Null(attachment.Metadata.PlannedPath);
        TransferMeasuredBlob measured = Assert.Single(result.Package.Envelope.MeasuredPayloads).Value;
        Assert.Equal(Convert.ToBase64String(MD5.HashData(Bytes)), measured.Md5);
        Assert.Equal(Convert.ToBase64String(SHA1.HashData(Bytes)), measured.Sha1);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ExactV1V2RawRevisionWithPassiveStrictHistory(bool v2)
    {
        JsonObject catalog = Catalog(); Bind(catalog, v2);
        TransferPackageInspection result = await Inspect(catalog);
        Assert.True(result.IsPackageValid);
        TransferAttachment item = result.Package!.Catalog.Threads[0].Posts[0].Attachments[0];
        Assert.StartsWith(v2 ? "att-v2:" : "att-v1:", item.Metadata.AttachmentRevisionKey);
        Assert.Equal("strict", item.History!.Kind);
        Media(catalog)["originalFileName"] = "different.jpg";
        await Error(catalog, TransferCatalogError.InvalidRevision);
    }
    [Fact]
    public async Task HistoricalMismatchFlagsAreMeasuredButNeverCarryCurrentPermission()
    {
        JsonObject catalog = Catalog(); Media(catalog)["expectedSize"] = (long)Bytes.Length + 1; Bind(catalog);
        await Error(catalog, TransferCatalogError.InvalidHistory);
        JsonObject history = Media(catalog)["history"]!.AsObject(); history["kind"] = "provider-mismatch"; history["sizeMismatch"] = true;
        Assert.True((await Inspect(catalog)).IsPackageValid);
        history["profileId"] = "SECRET";
        await Error(catalog, TransferCatalogError.InvalidField);
    }
    [Fact]
    public async Task ShaMismatchCannotBeBlessedByHistoricalException()
    {
        JsonObject catalog = Catalog(); Media(catalog)["expectedSha256"] = new string('0', 64); Bind(catalog);
        Media(catalog)["history"]!["kind"] = "provider-mismatch";
        await Error(catalog, TransferCatalogError.InvalidHistory);
    }
    [Fact]
    public async Task DuplicateThreadPostAndPositionAreRejected()
    {
        JsonObject catalog = Catalog(); catalog["threads"]!.AsArray().Add(Thread(catalog).DeepClone());
        await Error(catalog, TransferCatalogError.DuplicateIdentity);
        catalog = Catalog(); Thread(catalog)["posts"]!.AsArray().Add(Post(catalog).DeepClone());
        await Error(catalog, TransferCatalogError.DuplicateIdentity);
        catalog = Catalog(); Post(catalog)["attachments"]!.AsArray().Add(Media(catalog).DeepClone());
        await Error(catalog, TransferCatalogError.DuplicateIdentity);
    }
    [Fact]
    public async Task SharedBytesDoNotUnifyDistinctOccurrences()
    {
        JsonObject catalog = Catalog(); JsonObject another = Media(catalog).DeepClone().AsObject(); another["position"] = 1;
        Post(catalog)["attachments"]!.AsArray().Add(another);
        TransferPackageInspection result = await Inspect(catalog);
        Assert.True(result.IsPackageValid);
        Assert.Equal(2, result.Package!.Catalog.Threads[0].Posts[0].Attachments.Length);
        Assert.Single(result.Package.Envelope.Payloads);
    }
    [Theory]
    [InlineData("https://SECRET@i.4cdn.org/b/111.jpg")]
    [InlineData("https://i.4cdn.org/b/111.jpg?token=SECRET")]
    [InlineData("https://i.4cdn.org:443/b/111.jpg")]
    [InlineData("https://i.4cdn.org/b/%31.jpg")]
    [InlineData("https://i.4cdn.org.evil.invalid/b/111.jpg")]
    [InlineData("file:///SECRET")]
    [InlineData("https://private.invalid/SECRET")]
    public async Task AcquisitionReferencesAreNotNormalizedOrRedactedIntoDifferentRevisions(string url)
    {
        JsonObject catalog = Catalog(); Media(catalog)["url"] = url;
        await Error(catalog, TransferCatalogError.UnsafeUrl);
    }
    [Fact]
    public async Task PublicPageMustActuallyMatchDeclaredThread()
    {
        JsonObject catalog = Catalog(); Thread(catalog)["pageUrl"] = "https://boards.4chan.org/b/thread/7002";
        await Error(catalog, TransferCatalogError.UnsafeUrl);
        Thread(catalog)["pageUrl"] = null;
        Assert.True((await Inspect(catalog)).IsPackageValid);
    }
    [Theory]
    [InlineData("<script>SECRET</script>")]
    [InlineData("<!-- SECRET -->")]
    [InlineData("<img src=\"https://i.4cdn.org/b/111.jpg\">")]
    [InlineData("<a href=\"https://boards.4chan.org/b/thread/7001?token=SECRET\">x</a>")]
    [InlineData("<a href=\"javascript:SECRET\">x</a>")]
    [InlineData("<a href=\"#p15\" onclick=\"SECRET\">x</a>")]
    [InlineData("<span data-secret=\"SECRET\">x</span>")]
    [InlineData("<a href=\"#p15\" href=\"#p16\">x</a>")]
    [InlineData("<a href=\"&#x68;ttps://SECRET@host.invalid/\">x</a>")]
    [InlineData("<b")]
    public async Task UnclassifiedOrActiveMarkupIsRejectedWithoutLoggingBody(string html)
    {
        JsonObject catalog = Catalog(); Post(catalog)["html"] = html;
        await Error(catalog, TransferCatalogError.UnsafeHtml);
    }
    [Theory]
    [InlineData("<b>synthetic</b><br />")]
    [InlineData("<a href=\"https://boards.4chan.org/b/thread/7001#p15\">&gt;&gt;15</a>")]
    [InlineData("<span class='quote spoiler'>inert</span>")]
    [InlineData("&lt;script&gt;visible text&lt;/script&gt;")]
    public async Task KnownFormattingStaysByteExactAndPassive(string html)
    {
        JsonObject catalog = Catalog(); Post(catalog)["html"] = html;
        TransferPackageInspection result = await Inspect(catalog);
        Assert.True(result.IsPackageValid); Assert.Equal(html, result.Package!.Catalog.Threads[0].Posts[0].Metadata.Html);
    }
    [Fact]
    public async Task PresenceAbsentIsDifferentFromPresentNullOrEmpty()
    {
        JsonObject catalog = Catalog(); Post(catalog)["fields"] = 0;
        await Error(catalog, TransferCatalogError.InvalidPresence);
        Post(catalog)["subject"] = null; Post(catalog)["html"] = null; Post(catalog)["attachments"] = new JsonArray();
        Assert.True((await Inspect(catalog, false)).IsPackageValid);
        Post(catalog)["fields"] = 64;
        await Error(catalog, TransferCatalogError.InvalidPresence, false);
    }
    [Fact]
    public async Task ReferencesAndStatusAreExactAndEveryPayloadIsAccountedFor()
    {
        JsonObject catalog = Catalog(); Media(catalog)["payloadSha256"] = new string('0', 64);
        await Error(catalog, TransferCatalogError.InvalidReference);
        catalog = Catalog(); Media(catalog)["status"] = "omitted";
        await Error(catalog, TransferCatalogError.InvalidReference);
        Media(catalog)["payloadSha256"] = null;
        await Error(catalog, TransferCatalogError.InvalidReference); // now the actual ZIP payload is orphaned
        Assert.True((await Inspect(catalog, false)).IsPackageValid);
        Media(catalog)["status"] = "preview-only";
        await Error(catalog, TransferCatalogError.InvalidField, false);
    }
    [Theory]
    [InlineData("CON.jpg")] [InlineData("../SECRET")] [InlineData("C:SECRET")] [InlineData("dir\\SECRET")]
    public async Task FilenameIsNeverAnImportPath(string name)
    {
        JsonObject catalog = Catalog(); Media(catalog)["originalFileName"] = name;
        await Error(catalog, TransferCatalogError.InvalidField);
    }
    [Fact]
    public async Task UnknownPrivateAndAuthorityFieldsDoNotEnterTheCatalog()
    {
        foreach (string field in new[] { "favorites", "privateTags", "grants", "captureEnabled", "sql", "watch", "profilePath" })
        {
            JsonObject catalog = Catalog(); Thread(catalog)[field] = "SECRET";
            await Error(catalog, TransferCatalogError.InvalidField);
        }
        JsonObject unsupported = Catalog(); unsupported["schema"] = "chan-v999";
        await Error(unsupported, TransferCatalogError.UnsupportedSchema);
    }
    [Theory]
    [InlineData("\\uD800")] [InlineData("\\uDC00")] [InlineData("\\uD800\\u0041")]
    public async Task InvalidUnicodeIsASanitizedRejectionNotAnEscapingException(string malformed)
    {
        string json = Catalog().ToJsonString().Replace("synthetic title", malformed, StringComparison.Ordinal);
        using MemoryStream package = Package(Encoding.UTF8.GetBytes(json));
        TransferPackageInspection result = await NativeTransferPackageValidator.InspectAsync(package);
        Assert.False(result.IsPackageValid); Assert.Equal(TransferCatalogError.InvalidJson, result.CatalogError);
    }
    [Fact]
    public async Task DuplicateRootFieldsAndUnexpectedDatesAreRejected()
    {
        string json = Catalog().ToJsonString().Replace("\"schema\":\"chan-v1\"", "\"schema\":\"chan-v1\",\"schema\":\"chan-v1\"", StringComparison.Ordinal);
        using MemoryStream package = Package(Encoding.UTF8.GetBytes(json));
        Assert.Equal(TransferCatalogError.InvalidField, (await NativeTransferPackageValidator.InspectAsync(package)).CatalogError);
        JsonObject catalog = Catalog(); catalog["exportedAtUtc"] = "2026-11-01T09:00:00+01:00";
        await Error(catalog, TransferCatalogError.InvalidField);
    }
    [Fact]
    public async Task CancellationNeverProducesAnAdmittedPackage()
    {
        using MemoryStream package = Package(Encoding.UTF8.GetBytes(Catalog().ToJsonString()));
        using CancellationTokenSource cancel = new(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeTransferPackageValidator.InspectAsync(package, cancel.Token));
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task SupplementaryFormatScalarDoesNotHideInIdentityOrFilename(bool identifier)
    {
        JsonObject catalog = Catalog();
        if (identifier) Post(catalog)["id"] = "x\U000E0001";
        else Media(catalog)["originalFileName"] = "x\U000E0001.jpg";
        await Error(catalog, TransferCatalogError.InvalidField);
    }
    [Fact]
    public async Task PresentNullAttachmentCollectionRemainsDistinctFromPresentEmpty()
    {
        JsonObject catalog = Catalog(); Post(catalog)["attachments"] = null;
        TransferPackageInspection result = await Inspect(catalog, false);
        Assert.True(result.IsPackageValid);
        Assert.Null(result.Package!.Catalog.Threads[0].Posts[0].Metadata.Attachments);
        Post(catalog)["attachments"] = new JsonArray();
        result = await Inspect(catalog, false);
        Assert.True(result.IsPackageValid);
        Assert.NotNull(result.Package!.Catalog.Threads[0].Posts[0].Metadata.Attachments);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ValidRawDigestWhitespacePreservesExactHistoricalRevision(bool v2)
    {
        JsonObject catalog = Catalog();
        Media(catalog)["expectedMd5"] = " " + Convert.ToBase64String(MD5.HashData(Bytes)) + " ";
        Bind(catalog, v2);
        if (v2)
        {
            Media(catalog)["expectedSha1"] = " " + Convert.ToBase64String(SHA1.HashData(Bytes)) + " ";
            MediaProviderMetadata provider = new("111", "original", "https://i.4cdn.org/b/111.jpg", "synthetic.jpg", Bytes.Length,
                Media(catalog)["expectedMd5"]!.GetValue<string>(), Hash(Bytes)) { ExpectedSha1 = Media(catalog)["expectedSha1"]!.GetValue<string>() };
            Media(catalog)["revisionKey"] = MediaRevisionKeys.CreateAttachmentKey(new("4chan", "b", "7001"), "15", 0, provider);
        }
        TransferPackageInspection result = await Inspect(catalog);
        Assert.True(result.IsPackageValid);
        CapturedAttachment retained = result.Package!.Catalog.Threads[0].Posts[0].Attachments[0].Metadata;
        Assert.Equal(Media(catalog)["expectedMd5"]!.GetValue<string>(), retained.ExpectedMd5);
        Assert.Equal(Media(catalog)["expectedSha1"]?.GetValue<string>(), retained.ExpectedSha1);
        Assert.Equal(Media(catalog)["revisionKey"]!.GetValue<string>(), retained.AttachmentRevisionKey);
    }
    [Theory]
    [InlineData("COM¹.jpg")] [InlineData("LPT².jpg")] [InlineData("COM³")]
    public async Task SuperscriptDeviceNamesAreNotAcceptedAsPortableFileMetadata(string name)
    {
        JsonObject catalog = Catalog(); Media(catalog)["originalFileName"] = name;
        await Error(catalog, TransferCatalogError.InvalidField);
    }

    [Fact]
    public async Task ExistingEightKunPublicProducerReferencesStayExactAndPassive()
    {
        JsonObject catalog = Catalog();
        Thread(catalog)["id"]!["source"] = "8kun";
        Thread(catalog)["pageUrl"] = "https://8kun.top/b/res/7001.html";
        string url = "https://nerv.8kun.top/file_store/Synthetic_111-AbC.jpg";
        Media(catalog)["url"] = url;
        TransferPackageInspection result = await Inspect(catalog);
        Assert.True(result.IsPackageValid);
        Assert.Equal(url, result.Package!.Catalog.Threads[0].Posts[0].Attachments[0].Metadata.Url);
        foreach (string invalid in new[] { url + "?token=SECRET", url.Replace("nerv.8kun.top", "nerv.8kun.top.evil.invalid", StringComparison.Ordinal),
            "https://nerv.8kun.top/file_store/../SECRET.jpg", "https://nerv.8kun.top:443/file_store/111.jpg" })
        {
            Media(catalog)["url"] = invalid;
            await Error(catalog, TransferCatalogError.UnsafeUrl);
        }
    }

}

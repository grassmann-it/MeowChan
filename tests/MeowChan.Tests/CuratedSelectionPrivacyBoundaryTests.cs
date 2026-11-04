using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Favorites;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Tests;

// Cross-boundary tests, not export UI/capability tests. Everything is synthetic and
// in memory; inert manifest paths and snapshot owners are never dereferenced.
public sealed class CuratedSelectionPrivacyBoundaryTests
{
    private static readonly ResourceId Thread = new("4chan", "b", "100");
    private static readonly DateTimeOffset At = new(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid PackageId = Guid.Parse("86069931-b16a-4e73-a6ef-82d884cfb6ac");
    private static readonly byte[] Bytes = [11, 22, 33];
    private const string PrivateName = "SYNTHETIC-PRIVATE-CURATED-NAME";
    private const string PrivateNote = "SYNTHETIC-PRIVATE-CURATED-NOTE";
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static FavoriteAssetKey Key => new(Thread, "1000", "original", Hash(Bytes));

    private sealed record Fixture(NativeTransferCatalog Catalog, CuratedExportSelection Selection);

    private static Fixture Create(bool originalInManifest = true, bool originalContained = true,
        bool retainedThumbnail = false, bool selectOriginal = true)
    {
        (MediaAssetReference Asset, MediaAttachmentBinding Binding, CapturedAttachment Metadata) Row(string variant, int position, char id)
        {
            var provider = new MediaProviderMetadata("1000", variant, "https://i.4cdn.org/b/1000.png",
                "public.png", Bytes.Length, null, Hash(Bytes));
            var metadata = new CapturedAttachment(position, provider.MediaId, variant, provider.Url,
                provider.OriginalFileName, ExpectedSize: Bytes.Length)
            { ExpectedSha256 = Hash(Bytes), AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(Thread, "101", position, provider) };
            var asset = new MediaAssetReference(new string(id, 64), "1000", variant, "public.png",
                "/synthetic/never-opened/" + variant, Bytes.Length, Hash(Bytes), provider.Url, provider.OriginalFileName, false)
            { Md5 = Convert.ToBase64String(MD5.HashData(Bytes)) };
            var binding = new MediaAttachmentBinding(new(Thread, "101", position, metadata.AttachmentRevisionKey!),
                MediaRevisionKeys.CreateAcquisitionKey(Thread, provider), asset.AssetId, provider,
                new(Bytes.Length, asset.Md5, Hash(Bytes)), new(MediaAcceptanceKind.Strict, false, false, At));
            return (asset, binding, metadata);
        }

        var original = Row("original", 0, '1');
        // Deliberately identical bytes, media ID and filename: only variant/occurrence differ.
        var thumbnail = Row("thumbnail", 1, '2');
        var assets = originalInManifest ? new[] { original.Asset, thumbnail.Asset } : new[] { thumbnail.Asset };
        var bindings = originalInManifest ? new[] { original.Binding, thumbnail.Binding } : new[] { thumbnail.Binding };
        var manifest = new MediaAssetSnapshot("/synthetic/never-opened", MediaManifestStatus.Available,
            2, new string('d', 64), assets, attachmentBindings: bindings.ToDictionary(b => b.Reference.RevisionKey));
        var favorite = new FavoriteEntry(Key, Bytes.Length, PrivateName);
        var selection = CuratedExportSelection.Freeze(
            new FavoriteStoreSnapshot(null!, [favorite], new string('f', 64), PackageId, 7),
            CuratedExportScope.Thread, CuratedExportMode.Files, false, [Thread], selectOriginal ? [Key] : [],
            [new CuratedExportManifest(Thread, manifest)], []);
        TransferAttachment Attachment(CapturedAttachment metadata, TransferMediaRole role, bool contained) =>
            new(metadata, role, contained ? TransferContentStatus.Contained : TransferContentStatus.Unavailable,
                contained ? Hash(Bytes) : null, null);
        TransferAttachment originalAttachment = Attachment(original.Metadata, TransferMediaRole.Original, originalContained);
        TransferAttachment thumbnailAttachment = Attachment(thumbnail.Metadata, TransferMediaRole.Preview, true);
        ImmutableArray<TransferAttachment> current = retainedThumbnail ? [originalAttachment] : [originalAttachment, thumbnailAttachment];
        var post = new TransferPost(new CapturedPost("101", 101, PostFieldPresence.All, At,
            Html: "<p>synthetic public context</p>", Attachments: current.Select(a => a.Metadata).ToArray()), current);
        var thread = new TransferThread(Thread, null, "Synthetic", ThreadSourceState.NotFound, At, At, 1, true, [post])
        { RetainedBindings = retainedThumbnail ? [new("101", thumbnailAttachment)] : [] };
        return new(new NativeTransferCatalog(At, [thread]), selection);
    }

    private static NativePrivateMetadataProjection OfferedPrivate() => new([Thread], new(true, true, true),
        [Thread], [Key], [new PostBookmark(new(Thread, "101"), PrivateNote)]);

    private static IEnumerable<TransferAttachment> Attachments(NativeTransferCatalog catalog) =>
        catalog.Threads.SelectMany(t => t.Posts.SelectMany(p => p.Attachments)
            .Concat(t.RetainedBindings.Select(r => r.Attachment)));

    private static async Task<MemoryStream> WriteAsync(NativeTransferCatalog catalog,
        NativePrivateMetadataProjection? privateMetadata = null)
    {
        MemoryStream stream = new();
        TransferPayloadSource[] payloads = Attachments(catalog).Any(a => a.Status == TransferContentStatus.Contained)
            ? [new(new(Bytes.Length, Hash(Bytes)), _ => Task.FromResult<Stream>(new MemoryStream(Bytes, writable: false)))] : [];
        TransferWriteResult written = await NativeTransferPackageWriter.WriteAsync(stream, catalog, payloads,
            PackageId, privateMetadata: privateMetadata);
        Assert.True(written.IsComplete, written.Error.ToString());
        return stream;
    }

    private static async Task<JsonObject> ReadJsonAsync(ZipArchive zip, string entry)
    {
        using StreamReader reader = new(zip.GetEntry(entry)!.Open());
        return JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CuratedFavoriteSelectionDoesNotOptIntoPrivatePackageMetadata(bool selectOriginal)
    {
        Fixture fixture = Create(selectOriginal: selectOriginal);
        NativeTransferCatalog projected = NativeTransferCuratedProjection.Project(fixture.Catalog, fixture.Selection);
        Assert.Single(fixture.Selection.FavoriteEntries);
        Assert.Equal(selectOriginal ? 1 : 0, fixture.Selection.Originals.Length);
        using MemoryStream stream = await WriteAsync(projected); // Deliberately omit privateMetadata.
        TransferPackageInspection inspected = await NativeTransferPackageValidator.InspectAsync(stream);
        Assert.True(inspected.IsPackageValid, inspected.CatalogError.ToString());
        Assert.Null(inspected.Package!.PrivateMetadata);
        Assert.Null(inspected.Package.Envelope.PrivateMetadata);
        using ZipArchive zip = new(stream, ZipArchiveMode.Read, leaveOpen: true);
        Assert.Null(zip.GetEntry("private.json"));
        Assert.Equal(selectOriginal ? 1 : 0, zip.Entries.Count(e => e.FullName.StartsWith("payload/", StringComparison.Ordinal)));
        JsonObject manifest = await ReadJsonAsync(zip, "manifest.json");
        Assert.Equal(1, manifest["envelopeVersion"]!.GetValue<int>());
        Assert.False(manifest.ContainsKey("privateMetadata"));
        string publicJson = (await ReadJsonAsync(zip, "catalog.json")).ToJsonString();
        Assert.DoesNotContain(PrivateName, publicJson);
        Assert.DoesNotContain(PrivateNote, publicJson);
        Assert.False(manifest.ContainsKey("ratings"));
        Assert.False(manifest.ContainsKey("tags"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExplicitPrivateCategoryDoesNotFollowCuratedFavoritesIntoOtherCategories(int category)
    {
        Fixture fixture = Create();
        NativeTransferCatalog projected = NativeTransferCuratedProjection.Project(fixture.Catalog, fixture.Selection);
        var selection = new NativePrivateMetadataSelection(category == 0, category == 1, category == 2);
        using MemoryStream stream = await WriteAsync(projected, OfferedPrivate().Select(selection));
        TransferPackageInspection inspected = await NativeTransferPackageValidator.InspectAsync(stream);
        Assert.True(inspected.IsPackageValid);
        var received = Assert.IsType<NativePrivateMetadataProjection>(inspected.Package!.PrivateMetadata);
        Assert.Equal(selection, received.Selection);
        Assert.Equal(category == 0 ? 1 : 0, received.ThreadFavorites.Length);
        Assert.Equal(category == 1 ? 1 : 0, received.MediaFavorites.Length);
        Assert.Equal(category == 2 ? 1 : 0, received.PostBookmarksAndNotes.Length);
        NativePrivateMetadataProjection unselected = received.Select();
        Assert.Equal(default(NativePrivateMetadataSelection), unselected.Selection);
        Assert.Empty(unselected.ThreadFavorites);
        Assert.Empty(unselected.MediaFavorites);
        Assert.Empty(unselected.PostBookmarksAndNotes);
        using ZipArchive zip = new(stream, ZipArchiveMode.Read, leaveOpen: true);
        JsonObject wire = await ReadJsonAsync(zip, "private.json");
        Assert.Equal(category != 0, wire["threadFavorites"] is null);
        Assert.Equal(category != 1, wire["mediaFavorites"] is null);
        Assert.Equal(category != 2, wire["postBookmarksAndNotes"] is null);
        Assert.False(wire.ContainsKey("ratings"));
        Assert.False(wire.ContainsKey("tags"));
        if (category != 2) Assert.DoesNotContain(PrivateNote, wire.ToJsonString());
        Assert.DoesNotContain(PrivateName, wire.ToJsonString());
        Assert.DoesNotContain(PrivateNote, (await ReadJsonAsync(zip, "catalog.json")).ToJsonString());
    }

    [Theory]
    [InlineData("ratings")]
    [InlineData("tags")]
    public void UnsupportedPrivateCategoriesCannotBeSmuggledAlongsideExplicitFavorites(string field)
    {
        // These categories have no native selection contract. Do not invent one.
        byte[] valid = NativePrivateMetadataCodec.Serialize(OfferedPrivate().Select(new(MediaFavorites: true)));
        Assert.True(NativePrivateMetadataCodec.Inspect(valid).IsValid);
        JsonObject wire = JsonNode.Parse(valid)!.AsObject();
        wire[field] = new JsonArray(new JsonObject { ["value"] = "SYNTHETIC-UNSELECTED-PRIVATE-VALUE" });
        NativePrivateMetadataInspection rejected = NativePrivateMetadataCodec.Inspect(Encoding.UTF8.GetBytes(wire.ToJsonString()));
        Assert.False(rejected.IsValid);
        Assert.Null(rejected.Projection);
        Assert.Equal(NativePrivateMetadataCodecError.InvalidField, rejected.Error);
        Assert.DoesNotContain("SYNTHETIC-UNSELECTED-PRIVATE-VALUE", rejected.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsupportedRetainedPreviewIsRejectedBeforeItCanSupplyOriginalAuthority(bool originalInManifest)
    {
        // Retained wire currently supports originals only, not previews. Preserve
        // that boundary instead of treating a malformed retained thumbnail as a
        // valid catalog or expanding the product contract for this fixture.
        Fixture fixture = Create(originalInManifest: originalInManifest, originalContained: false, retainedThumbnail: true);
        TransferAttachment retained = Assert.Single(Assert.Single(fixture.Catalog.Threads).RetainedBindings).Attachment;
        Assert.Equal(TransferMediaRole.Preview, retained.Role);
        Assert.Equal("thumbnail", retained.Metadata.Variant);
        var ledger = fixture.Selection.CreateLedger();
        var before = Assert.Single(ledger.Originals);
        Assert.Throws<InvalidDataException>(() => NativeTransferCuratedProjection.Project(fixture.Catalog, fixture.Selection));
        Assert.Equal(before, Assert.Single(ledger.Originals));
        Assert.False(ledger.AllOriginalsVerified);
        Assert.Equal(originalInManifest ? CuratedOriginalVerification.Required : CuratedOriginalVerification.NotApplicable,
            Assert.Single(ledger.Originals).Verification);
    }

    [Fact]
    public async Task ThumbnailOnlyManifestCannotSupplyOriginalSelectionOrPayloadAuthority()
    {
        Fixture fixture = Create(originalInManifest: false, originalContained: false);
        Assert.Equal(CuratedExportStatus.OriginalMissing, Assert.Single(fixture.Selection.Originals).Status);
        Assert.Null(Assert.Single(fixture.Selection.Originals).Asset);
        Assert.Empty(Assert.Single(fixture.Selection.Originals).Occurrences);
        CuratedExportLedger ledger = fixture.Selection.CreateLedger();
        Assert.False(ledger.AllOriginalsVerified);
        Assert.Equal(CuratedOriginalVerification.NotApplicable, Assert.Single(ledger.Originals).Verification);
        var error = Assert.Throws<CuratedExportValidationException>(() => ledger.RecordOriginal(Key, CuratedExportStatus.OriginalIncluded));
        Assert.Equal(CuratedExportValidationError.InvalidLedgerUpdate, error.Error);
        NativeTransferCatalog projected = NativeTransferCuratedProjection.Project(fixture.Catalog, fixture.Selection);
        Assert.Equal(2, Attachments(projected).Count());
        Assert.All(Attachments(projected), a =>
        {
            Assert.Equal(TransferContentStatus.Omitted, a.Status);
            Assert.Null(a.PayloadSha256);
            Assert.Null(a.History);
        });
        using MemoryStream stream = await WriteAsync(projected);
        TransferPackageInspection inspected = await NativeTransferPackageValidator.InspectAsync(stream);
        Assert.True(inspected.IsPackageValid);
        Assert.Null(inspected.Package!.PrivateMetadata);
        using ZipArchive zip = new(stream, ZipArchiveMode.Read, leaveOpen: true);
        Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith("payload/", StringComparison.Ordinal));
        Assert.False(ledger.AllOriginalsVerified);
    }

    [Fact]
    public async Task ThumbnailBytesCannotVerifySelectedButUnavailableOriginal()
    {
        Fixture fixture = Create(originalContained: false);
        CuratedExportLedger ledger = fixture.Selection.CreateLedger();
        Assert.Equal(CuratedExportStatus.OriginalIncluded, Assert.Single(fixture.Selection.Originals).Status);
        Assert.Equal(CuratedOriginalVerification.Required, Assert.Single(ledger.Originals).Verification);
        NativeTransferCatalog projected = NativeTransferCuratedProjection.Project(fixture.Catalog, fixture.Selection);
        Assert.Equal(TransferContentStatus.Unavailable,
            Assert.Single(Attachments(projected), a => a.Role == TransferMediaRole.Original).Status);
        Assert.Equal(TransferContentStatus.Omitted,
            Assert.Single(Attachments(projected), a => a.Role == TransferMediaRole.Preview).Status);
        Assert.All(Attachments(projected), a => Assert.Null(a.PayloadSha256));
        using MemoryStream stream = await WriteAsync(projected);
        Assert.True((await NativeTransferPackageValidator.InspectAsync(stream)).IsPackageValid);
        using ZipArchive zip = new(stream, ZipArchiveMode.Read, leaveOpen: true);
        Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith("payload/", StringComparison.Ordinal));
        Assert.Equal(CuratedOriginalVerification.Required, Assert.Single(ledger.Originals).Verification);
        Assert.False(ledger.AllOriginalsVerified);
    }
}

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Favorites;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

public sealed class NativeTransferCuratedV4ProjectionTests
{
    private static readonly ResourceId Thread = new("4chan", "b", "100");
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-03T00:00:00Z");
    private static readonly byte[] First = [1, 2, 3], Second = [4, 5, 6];
    private const string PrivateName = "SYNTHETIC-PRIVATE-FAVORITE-NAME";
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static CapturedAttachment Metadata(string media, int position, byte[] bytes, string post = "101")
    {
        var provider = new MediaProviderMetadata(media, "original", $"https://i.4cdn.org/b/{media}.png",
            media + ".png", bytes.Length, null, Hash(bytes));
        return new(position, media, "original", provider.Url, provider.OriginalFileName, ExpectedSize: bytes.Length)
        { ExpectedSha256 = Hash(bytes), AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(Thread, post, position, provider) };
    }

    private static (NativeTransferCatalog Catalog, CuratedExportSelection Selection) Fixture(bool previews = false, bool empty = false,
        CuratedExportMode mode = CuratedExportMode.Files, bool manual = false)
    {
        CapturedAttachment first = Metadata("1000", 0, First), second = Metadata("1001", 1, Second);
        MediaAssetReference asset = new(new string('1', 64), first.MediaId!, "original", "1000.png", "/never/opened",
            First.Length, Hash(First), first.Url, first.OriginalFileName, false)
        { Md5 = Convert.ToBase64String(MD5.HashData(First)) };
        var provider = new MediaProviderMetadata(first.MediaId!, first.Variant, first.Url,
            first.OriginalFileName, first.ExpectedSize, null, first.ExpectedSha256);
        MediaAttachmentBinding binding = new(new(Thread, "101", 0, first.AttachmentRevisionKey!),
            MediaRevisionKeys.CreateAcquisitionKey(Thread, provider), asset.AssetId, provider,
            new(First.Length, Convert.ToBase64String(MD5.HashData(First)), Hash(First)),
            new(MediaAcceptanceKind.Strict, false, false, At));
        var manifest = new CuratedExportManifest(Thread, new MediaAssetSnapshot("/synthetic/root",
            MediaManifestStatus.Available, 2, new string('d', 64), [asset], attachmentBindings:
            new Dictionary<string, MediaAttachmentBinding> { [binding.Reference.RevisionKey] = binding }));
        var favorite = new FavoriteEntry(new(Thread, first.MediaId!, "original", Hash(First)), First.Length, PrivateName);
        var unchosen = new FavoriteEntry(new(Thread, second.MediaId!, "original", Hash(Second)), Second.Length, PrivateName + "-UNSELECTED");
        var selection = CuratedExportSelection.Freeze(
            new FavoriteStoreSnapshot(null!, [favorite, unchosen], new string('f', 64), Guid.NewGuid(), 7),
            CuratedExportScope.Thread, mode, previews, [Thread], empty ? [] : [favorite.Key], [manifest], []);
        if (manual)
            selection = CuratedExportSelection.FreezeManual(mode, previews,
                [FavoriteExportAsset.From(Thread, manifest.Snapshot, asset)], [manifest], []);
        TransferAttachment Attachment(CapturedAttachment m, byte[] bytes) => new(m, TransferMediaRole.Original,
            TransferContentStatus.Contained, Hash(bytes), new("strict", At, false, false));
        CapturedPost captured = new("101", 101, PostFieldPresence.All, At, Html: "<p>public</p>", Attachments: [first, second]);
        TransferPost post = new(captured, [Attachment(first, First), Attachment(second, Second)]);
        TransferPost absent = new(new("102", 102, PostFieldPresence.Attachments, Attachments: null), []) { AttachmentsWereNull = true };
        return (new(At, [new(Thread, "https://boards.4chan.org/b/thread/100", "Synthetic",
            ThreadSourceState.NotFound, At, At, 11, true, [post, absent])]), selection);
    }

    private static TransferAttachment V4(TransferAttachment a) => a with
    {
        OriginalByteVersion = new(First.Length, Hash(First), Convert.ToBase64String(MD5.HashData(First)), null),
        DerivedRepresentations = [new(TransferDerivedRepresentationKind.Preview,
            a.Metadata.AttachmentRevisionKey!,
            new(First.Length, Hash(First), Convert.ToBase64String(MD5.HashData(First)), null),
            TransferContentStatus.Contained, Hash(Second))]
    };

    private static NativeTransferCatalog Place(NativeTransferCatalog c, TransferAttachment a, bool retained) =>
        c with { Threads = [c.Threads[0] with
        {
            Posts = retained ? [] : [c.Threads[0].Posts[0] with
            {
                Metadata = c.Threads[0].Posts[0].Metadata with { Attachments = [a.Metadata] },
                Attachments = [a]
            }],
            RetainedBindings = retained ? [new("101", a)] : []
        }] };

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FavoriteAndManualSelectionsPreserveExactCurrentAndRetainedEvidence(bool retained, bool manual)
    {
        var (catalog, selection) = Fixture(manual: manual);
        var original = V4(catalog.Threads[0].Posts[0].Attachments[0]);
        var input = Place(catalog, original, retained);
        byte[] before = NativeTransferCatalogSerializer.Serialize(input);
        var output = NativeTransferCuratedProjection.Project(input, selection);
        var actual = retained ? Assert.Single(output.Threads[0].RetainedBindings).Attachment
            : Assert.Single(output.Threads[0].Posts[0].Attachments);
        Assert.Same(original.Metadata, actual.Metadata);
        Assert.Same(original.OriginalByteVersion, actual.OriginalByteVersion);
        Assert.Equal(TransferContentStatus.Contained, actual.Status);
        Assert.Equal(original.PayloadSha256, actual.PayloadSha256);
        var derived = Assert.Single(actual.DerivedRepresentations);
        Assert.Equal(TransferContentStatus.Omitted, derived.Status);
        Assert.Null(derived.PayloadSha256);
        Assert.Equal(original.Metadata.AttachmentRevisionKey, derived.SourceRevisionKey);
        Assert.Equal(original.OriginalByteVersion, derived.SourceByteVersion);
        Assert.Equal(before, NativeTransferCatalogSerializer.Serialize(input));
        Assert.DoesNotContain(PrivateName, Encoding.UTF8.GetString(NativeTransferCatalogSerializer.Serialize(output)));
        if (retained) Assert.Empty(output.Threads[0].Posts);
        if (manual) Assert.Empty(selection.FavoriteEntries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnselectedOmittedOriginalKeepsKnownIdentityWithoutBorrowingDerivedBytes(bool retained)
    {
        var (catalog, selection) = Fixture(empty: true);
        var original = V4(catalog.Threads[0].Posts[0].Attachments[0]);
        var output = NativeTransferCuratedProjection.Project(Place(catalog, original, retained), selection);
        var actual = retained ? output.Threads[0].RetainedBindings[0].Attachment
            : output.Threads[0].Posts[0].Attachments[0];
        Assert.Equal(TransferContentStatus.Omitted, actual.Status);
        Assert.Null(actual.PayloadSha256);
        Assert.Null(actual.History);
        Assert.Equal(original.OriginalByteVersion, actual.OriginalByteVersion);
        Assert.Equal(original.Metadata, actual.Metadata);
        Assert.NotEqual(actual.OriginalByteVersion!.Sha256, original.DerivedRepresentations[0].PayloadSha256);
    }

    [Theory]
    [InlineData((int)TransferContentStatus.Omitted)]
    [InlineData((int)TransferContentStatus.Unavailable)]
    public void SelectedAbsentOriginalCannotRetargetToAnotherKnownByteVersion(int value)
    {
        var status = (TransferContentStatus)value;
        var (catalog, selection) = Fixture();
        var original = V4(catalog.Threads[0].Posts[0].Attachments[0]) with
        { Status = status, PayloadSha256 = null, History = null, DerivedRepresentations = [] };
        var good = Place(catalog, original, false);
        Assert.Equal(status, NativeTransferCuratedProjection.Project(good, selection).Threads[0].Posts[0].Attachments[0].Status);
        var changed = original with { OriginalByteVersion = original.OriginalByteVersion! with { Sha256 = Hash(Second) } };
        Assert.Throws<InvalidDataException>(() => NativeTransferCuratedProjection.Project(Place(catalog, changed, false), selection));
    }

    [Fact]
    public void InvalidDerivedBindingIsRejectedEvenWhenEverythingWouldBeOmitted()
    {
        var (catalog, selection) = Fixture(empty: true);
        var a = V4(catalog.Threads[0].Posts[0].Attachments[0]);
        Assert.NotNull(NativeTransferCuratedProjection.Project(Place(catalog, a, false), selection));
        a = a with { DerivedRepresentations = [a.DerivedRepresentations[0] with { SourceRevisionKey = new string('e', 64) }] };
        Assert.Throws<InvalidDataException>(() => NativeTransferCuratedProjection.Project(Place(catalog, a, false), selection));
    }

    [Fact]
    public void PreviewRequestWithNoProducerStillFailsRatherThanReportingFulfillment()
    {
        var (catalog, selection) = Fixture(previews: true);
        var a = V4(catalog.Threads[0].Posts[0].Attachments[0]);
        Assert.Throws<InvalidDataException>(() => NativeTransferCuratedProjection.Project(Place(catalog, a, false), selection));
    }
}

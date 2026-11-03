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

public sealed class NativeTransferCuratedProjectionTests
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
        CuratedExportMode mode = CuratedExportMode.Files)
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
        TransferAttachment Attachment(CapturedAttachment m, byte[] bytes) => new(m, TransferMediaRole.Original,
            TransferContentStatus.Contained, Hash(bytes), new("strict", At, false, false));
        CapturedPost captured = new("101", 101, PostFieldPresence.All, At, Html: "<p>public</p>", Attachments: [first, second]);
        TransferPost post = new(captured, [Attachment(first, First), Attachment(second, Second)]);
        TransferPost absent = new(new("102", 102, PostFieldPresence.Attachments, Attachments: null), []) { AttachmentsWereNull = true };
        return (new(At, [new(Thread, "https://boards.4chan.org/b/thread/100", "Synthetic",
            ThreadSourceState.NotFound, At, At, 11, true, [post, absent])]), selection);
    }

    // Actual synthetic byte measurements, not fabricated measurement receipts. No
    // file or network handles are opened. The validator binds every contained row.
    private static byte[] AssertCatalogValid(NativeTransferCatalog catalog)
    {
        byte[] bytes = NativeTransferCatalogSerializer.Serialize(catalog);
        var hashes = catalog.Threads.SelectMany(t => t.Posts.SelectMany(p => p.Attachments)
                .Concat(t.RetainedBindings.Select(r => r.Attachment)))
            .Where(a => a.Status == TransferContentStatus.Contained).Select(a => a.PayloadSha256!).ToHashSet();
        var measured = new[] { First, Second }.Where(b => hashes.Contains(Hash(b)))
            .ToImmutableDictionary(Hash, b => new TransferMeasuredBlob(b.Length, Hash(b),
                Convert.ToBase64String(MD5.HashData(b)), Convert.ToBase64String(SHA1.HashData(b))));
        var envelope = new NativeTransferEnvelope("29106c80-d16d-4d1c-a345-861b6c85681b",
            NativeTransferCatalogSerializer.ContentSchema(catalog), "", new(bytes.Length, Hash(bytes)),
            measured.Values.Select(b => new TransferBlobDescriptor(b.Length, b.Sha256)).ToImmutableArray())
        { MeasuredPayloads = measured };
        TransferCatalogInspection inspected = NativeTransferCatalogValidator.Inspect(bytes, envelope);
        Assert.True(inspected.IsCatalogValid, inspected.Error.ToString());
        using JsonDocument json = JsonDocument.Parse(bytes);
        Assert.False(json.RootElement.TryGetProperty("privateMetadata", out _));
        Assert.DoesNotContain(PrivateName, Encoding.UTF8.GetString(bytes));
        return bytes;
    }

    [Fact]
    public void PreservesAllPublicOccurrencesAndNullPresenceWhileOmittingUnchosenBinary()
    {
        var (input, selection) = Fixture();
        byte[] before = AssertCatalogValid(input);
        NativeTransferCatalog output = NativeTransferCuratedProjection.Project(input, selection);
        AssertCatalogValid(output);
        Assert.Equal(before, AssertCatalogValid(input));
        Assert.Equal(input.Threads[0].Posts.Length, output.Threads[0].Posts.Length);
        TransferPost post = output.Threads[0].Posts[0];
        Assert.Same(input.Threads[0].Posts[0].Metadata, post.Metadata);
        Assert.Equal(2, post.Attachments.Length);
        Assert.Equal(TransferContentStatus.Contained, post.Attachments[0].Status);
        Assert.Equal(TransferContentStatus.Omitted, post.Attachments[1].Status);
        Assert.Null(post.Attachments[1].PayloadSha256);
        Assert.Null(post.Attachments[1].History);
        Assert.Equal(input.Threads[0].Posts[0].Attachments[1].Metadata, post.Attachments[1].Metadata);
        Assert.True(output.Threads[0].Posts[1].AttachmentsWereNull);
        Assert.Null(output.Threads[0].Posts[1].Metadata.Attachments);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RejectsUnsupportedModeOrPreviewRequestWithoutChangingTheCatalog(bool html, bool previews)
    {
        var (input, selection) = Fixture(previews: previews, mode: html ? CuratedExportMode.Html : CuratedExportMode.Files);
        byte[] before = AssertCatalogValid(input);
        Assert.Throws<InvalidDataException>(() => NativeTransferCuratedProjection.Project(input, selection));
        Assert.Equal(before, AssertCatalogValid(input));
    }

    [Fact]
    public void EmptySelectionPreservesContextWithAllBinariesOmitted()
    {
        var (input, selection) = Fixture(empty: true);
        AssertCatalogValid(input);
        var output = NativeTransferCuratedProjection.Project(input, selection);
        AssertCatalogValid(output);
        Assert.All(output.Threads[0].Posts[0].Attachments, a => Assert.Equal(TransferContentStatus.Omitted, a.Status));
    }

    private static NativeTransferCatalog ReplaceFirst(NativeTransferCatalog catalog, TransferAttachment attachment) =>
        catalog with { Threads = [catalog.Threads[0] with { Posts = [catalog.Threads[0].Posts[0] with
        { Attachments = [attachment, catalog.Threads[0].Posts[0].Attachments[1]] }, catalog.Threads[0].Posts[1]] }] };

    [Fact]
    public void SelectedUnavailableRemainsHonest()
    {
        var (source, selection) = Fixture();
        var input = ReplaceFirst(source, source.Threads[0].Posts[0].Attachments[0] with
        { Status = TransferContentStatus.Unavailable, PayloadSha256 = null, History = null });
        AssertCatalogValid(input);
        var output = NativeTransferCuratedProjection.Project(input, selection);
        AssertCatalogValid(output);
        Assert.Equal(TransferContentStatus.Unavailable, output.Threads[0].Posts[0].Attachments[0].Status);
        Assert.Null(output.Threads[0].Posts[0].Attachments[0].PayloadSha256);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetainedOccurrencesPreserveContextAndDoNotInventCapturedPosts(bool unavailable)
    {
        var (source, selection) = Fixture();
        var attachments = source.Threads[0].Posts[0].Attachments;
        var first = unavailable ? attachments[0] with
            { Status = TransferContentStatus.Unavailable, PayloadSha256 = null, History = null } : attachments[0];
        var input = source with { Threads = [source.Threads[0] with { Posts = [],
            RetainedBindings = [new("101", first), new("101", attachments[1])] }] };
        AssertCatalogValid(input);
        var output = NativeTransferCuratedProjection.Project(input, selection);
        AssertCatalogValid(output);
        Assert.Empty(output.Threads[0].Posts);
        Assert.Equal(2, output.Threads[0].RetainedBindings.Length);
        Assert.Equal(first, output.Threads[0].RetainedBindings[0].Attachment);
        Assert.Equal(TransferContentStatus.Omitted, output.Threads[0].RetainedBindings[1].Attachment.Status);
        Assert.Null(output.Threads[0].RetainedBindings[1].Attachment.PayloadSha256);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectedMismatchedHashFailsClosedForCurrentAndRetained(bool retained)
    {
        var (source, selection) = Fixture();
        // v3 unattested observation is legal in the source even though it is not
        // the selected favorite's bytes. The consumer must not silently omit it.
        var changed = source.Threads[0].Posts[0].Attachments[0] with { PayloadSha256 = Hash(Second), History = null };
        var input = retained ? source with { Threads = [source.Threads[0] with
            { Posts = [], RetainedBindings = [new("101", changed)] }] } : ReplaceFirst(source, changed);
        AssertCatalogValid(input);
        Assert.Throws<InvalidDataException>(() => NativeTransferCuratedProjection.Project(input, selection));
    }

    [Fact]
    public void ForgedRevisionAndUnsafeSourceReferenceAreRejectedBeforeFiltering()
    {
        var (source, selection) = Fixture();
        var forged = source.Threads[0].Posts[0].Attachments[1] with
        { Metadata = source.Threads[0].Posts[0].Attachments[1].Metadata with { AttachmentRevisionKey = new string('e', 64) } };
        var post = source.Threads[0].Posts[0];
        var bad = source with { Threads = [source.Threads[0] with { Posts = [post with
        { Metadata = post.Metadata with { Attachments = [post.Attachments[0].Metadata, forged.Metadata] },
            Attachments = [post.Attachments[0], forged] }] }] };
        Assert.Throws<InvalidDataException>(() => NativeTransferCuratedProjection.Project(bad, selection));
        var unsafeSource = source with { Threads = [source.Threads[0] with { PageUrl = "https://example.invalid/?token=secret" }] };
        Assert.Throws<InvalidDataException>(() => NativeTransferCuratedProjection.Project(unsafeSource, selection));
    }
}

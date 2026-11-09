using System;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using MeowChan.Favorites;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Independently written P07 sidecar checks. All values are synthetic and these tests
// intentionally stop at the pure codec: no store, journal, profile, file, or build access.
public sealed class NativePrivateApplyRequestCodecAdversarialTests
{
    private static readonly ResourceId Thread = new("zsynthetic-source", "zboard", "0001");
    private static readonly FavoriteAssetKey Media = new(Thread, "z-media-0001", "original", Hash('c'));
    private static readonly PostBookmark Bookmark = new(new(Thread, "0001"), "z incoming note");
    private static readonly Guid OperationId = Guid.Parse("6f13d7ba-64a3-4a41-8b31-2b8af5b17a75");
    private static readonly Guid ProfileIncarnation = Guid.Parse("52a91d51-b292-43aa-8762-c895830ba71e");
    private static readonly Guid StableInstance = Guid.Parse("12345678-90ab-cdef-1234-567890abcdef");
    private static readonly NativePrivateMetadataSelection All = new(true, true, true);

    private static string Hash(char value) => new(value, 64);

    private static NativePrivateMetadataSelection Selection(int bits) => new(
        (bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0);

    private static NativePrivateMetadataProjection Projection(
        NativePrivateMetadataSelection selection, string note = "z incoming note")
        => new([Thread], selection,
            selection.ThreadFavorites ? [Thread] : [],
            selection.MediaFavorites ? [Media] : [],
            selection.PostBookmarksAndNotes
                ? [new PostBookmark(new(Thread, "0001"), note)] : []);

    private static NativePrivateMetadataComparisonResult Comparison(
        NativePrivateMetadataProjection selected,
        NativePrivateMetadataDisposition disposition = NativePrivateMetadataDisposition.Add)
        => new(selected.Selection, selected,
            selected.ThreadFavorites.Select(target => new NativePrivateThreadFavoriteComparison(target, disposition)).ToImmutableArray(),
            selected.MediaFavorites.Select(target => new NativePrivateMediaFavoriteComparison(target, disposition)).ToImmutableArray(),
            selected.PostBookmarksAndNotes.Select(row => new NativePrivateBookmarkComparison(row.Key, row.Note, disposition)).ToImmutableArray());

    private static NativePrivateApplyRequestBinding Binding() => new(
        4096, Hash('1'), Hash('2'), 512, Hash('3'), ProfileIncarnation, Hash('4'));

    private static NativePrivateApplyStoreImage Image(
        int schema = 2, Guid? instance = null, long revision = 19, long byteLength = 321,
        string? sha256 = null) => new(true, schema, instance ?? StableInstance, revision, byteLength, sha256 ?? Hash('5'));

    private static NativePrivateApplyStoreImage MissingImage() => new(false, 0, null, 0, 0, null);

    private static NativePrivateApplyStoreFence NoChangeFence(NativePrivateApplyStoreKind kind)
    {
        NativePrivateApplyStoreImage image = Image();
        return new(kind, image, image, kind == NativePrivateApplyStoreKind.PostBookmarksAndNotes ? null : MissingImage());
    }

    private static ImmutableArray<NativePrivateApplyStoreFence> NoChangeFences(
        NativePrivateMetadataSelection selection)
    {
        ImmutableArray<NativePrivateApplyStoreKind>.Builder builder = ImmutableArray.CreateBuilder<NativePrivateApplyStoreKind>();
        if (selection.ThreadFavorites) builder.Add(NativePrivateApplyStoreKind.ThreadFavorites);
        if (selection.MediaFavorites) builder.Add(NativePrivateApplyStoreKind.MediaFavorites);
        if (selection.PostBookmarksAndNotes) builder.Add(NativePrivateApplyStoreKind.PostBookmarksAndNotes);
        return builder.Select(NoChangeFence).ToImmutableArray();
    }

    private static NativePrivateApplyRequest Create(
        NativePrivateMetadataSelection selection, string note = "z incoming note") =>
        NativePrivateApplyRequest.Create(OperationId, Comparison(Projection(selection, note)), Binding(),
            NoChangeFences(selection));

    private static byte[] Encode(NativePrivateApplyRequest request) =>
        NativePrivateApplyRequestCodec.Serialize(request);

    private static NativePrivateApplyRequest Decode(byte[] wire) =>
        NativePrivateApplyRequestCodec.Deserialize(wire);

    private static JsonObject Json() => JsonNode.Parse(Encoding.UTF8.GetString(Encode(Create(new(true)))))!.AsObject();

    private static void Reject(JsonObject document) => Reject(document.ToJsonString());

    private static void Reject(string text) =>
        Assert.Throws<InvalidDataException>(() => Decode(Encoding.UTF8.GetBytes(text)));

    private static void AssertImage(NativePrivateApplyStoreImage actual,
        NativePrivateApplyStoreImage expected)
    {
        Assert.Equal(expected.Exists, actual.Exists);
        Assert.Equal(expected.Schema, actual.Schema);
        Assert.Equal(expected.Instance, actual.Instance);
        Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.ByteLength, actual.ByteLength);
        Assert.Equal(expected.Sha256, actual.Sha256);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void EveryExplicitSelectionRoundTripsAndCarriesOnlyItsStoreFence(int bits)
    {
        NativePrivateMetadataSelection selection = Selection(bits);
        NativePrivateApplyRequest request = Create(selection);
        byte[] wire = Encode(request);
        NativePrivateApplyRequest decoded = Decode(wire);

        Assert.Equal(wire, Encode(decoded));
        Assert.Equal(selection, decoded.Selection);
        Assert.Equal(selection.ThreadFavorites ? 1 : 0, decoded.ThreadFavorites.Length);
        Assert.Equal(selection.MediaFavorites ? 1 : 0, decoded.MediaFavorites.Length);
        Assert.Equal(selection.PostBookmarksAndNotes ? 1 : 0, decoded.PostBookmarksAndNotes.Length);
        Assert.Equal(
            new[] { selection.ThreadFavorites, selection.MediaFavorites, selection.PostBookmarksAndNotes }.Count(flag => flag),
            decoded.StoreFences.Length);
        Assert.Equal(request.ContentPlanSha256, decoded.ContentPlanSha256);
        Assert.Equal(request.RequestSha256, decoded.RequestSha256);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("  leading and trailing\n\twhitespace  ")]
    [InlineData("pr\u00e9composed caf\u00e9")]
    [InlineData("decomposed cafe\u0301")]
    [InlineData("astral \ud83d\udc31 and combining marks z\u030a")]
    public void ExactIncomingNoteScalarIsPreservedAcrossTheSidecar(string note)
    {
        NativePrivateMetadataSelection selection = new(PostBookmarksAndNotes: true);
        NativePrivateApplyRequest request = Create(selection, note);
        NativePrivateApplyRequest decoded = Decode(Encode(request));

        Assert.Equal(note, decoded.SelectedIncoming.PostBookmarksAndNotes[0].Note);
        Assert.Equal(note, decoded.PostBookmarksAndNotes[0].IncomingNote);
        Assert.Equal(request.SelectedPayloadSha256, decoded.SelectedPayloadSha256);
        Assert.Equal(request.ContentPlanSha256, decoded.ContentPlanSha256);
    }

    [Fact]
    public void LegacyV1BeforeAndV2ExpectedAfterMutationFencesRoundTripExactly()
    {
        NativePrivateMetadataProjection selected = Projection(All);
        NativePrivateMetadataComparisonResult rows = Comparison(selected, NativePrivateMetadataDisposition.Add);
        NativePrivateApplyStoreImage threadBefore = new(true, 1, null, 40, 400, Hash('a'));
        NativePrivateApplyStoreImage threadAfter = new(true, 2,
            Guid.Parse("00000000-1111-2222-3333-444444444444"), 41, 401, Hash('b'));
        NativePrivateApplyStoreImage threadBackup = new(true, 1,
            Guid.Parse("99999999-8888-7777-6666-555555555555"), 7, 70, Hash('c'));
        NativePrivateApplyStoreImage mediaBefore = new(true, 1, null, 52, 520, Hash('d'));
        NativePrivateApplyStoreImage mediaAfter = new(true, 2,
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), 53, 521, Hash('e'));
        NativePrivateApplyStoreImage mediaBackup = new(true, 3,
            Guid.Parse("ffffffff-eeee-dddd-cccc-bbbbbbbbbbbb"), 8, 80, Hash('f'));
        NativePrivateApplyStoreImage bookmarkBefore = new(true, 1, null, 64, 640, Hash('6'));
        NativePrivateApplyStoreImage bookmarkAfter = new(true, 2,
            Guid.Parse("1a2a3a4a-5a6a-7a8a-9aab-acacacacacac"), 65, 641, Hash('7'));

        var request = NativePrivateApplyRequest.Create(OperationId, rows, Binding(),
        [
            new NativePrivateApplyStoreFence(NativePrivateApplyStoreKind.ThreadFavorites,
                threadBefore, threadAfter, threadBackup),
            new NativePrivateApplyStoreFence(NativePrivateApplyStoreKind.MediaFavorites,
                mediaBefore, mediaAfter, mediaBackup),
            new NativePrivateApplyStoreFence(NativePrivateApplyStoreKind.PostBookmarksAndNotes,
                bookmarkBefore, bookmarkAfter, null),
        ]);
        NativePrivateApplyRequest decoded = Decode(Encode(request));

        Assert.Collection(decoded.StoreFences,
            fence =>
            {
                Assert.Equal(NativePrivateApplyStoreKind.ThreadFavorites, fence.Kind);
                AssertImage(threadBefore, fence.Before);
                AssertImage(threadAfter, fence.ExpectedAfter);
                Assert.NotNull(fence.BeforeBackup);
                AssertImage(threadBackup, fence.BeforeBackup!);
            },
            fence =>
            {
                Assert.Equal(NativePrivateApplyStoreKind.MediaFavorites, fence.Kind);
                AssertImage(mediaBefore, fence.Before);
                AssertImage(mediaAfter, fence.ExpectedAfter);
                Assert.NotNull(fence.BeforeBackup);
                AssertImage(mediaBackup, fence.BeforeBackup!);
            },
            fence =>
            {
                Assert.Equal(NativePrivateApplyStoreKind.PostBookmarksAndNotes, fence.Kind);
                AssertImage(bookmarkBefore, fence.Before);
                AssertImage(bookmarkAfter, fence.ExpectedAfter);
                Assert.Null(fence.BeforeBackup);
            });
        Assert.Equal(request.ContentPlanSha256, decoded.ContentPlanSha256);
        Assert.Equal(request.RequestSha256, decoded.RequestSha256);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void DistinctFavoriteBackupFencesSurviveANoChangeApplyClaim(int category)
    {
        NativePrivateApplyStoreKind kind = (NativePrivateApplyStoreKind)category;
        NativePrivateMetadataSelection selection = kind == NativePrivateApplyStoreKind.ThreadFavorites
            ? new(ThreadFavorites: true) : new(MediaFavorites: true);
        NativePrivateApplyStoreImage before = Image(schema: 2, revision: 91, byteLength: 910, sha256: Hash('8'));
        NativePrivateApplyStoreImage backup = Image(schema: 3, revision: 92, byteLength: 920, sha256: Hash('9'));
        var request = NativePrivateApplyRequest.Create(OperationId, Comparison(Projection(selection)), Binding(),
            [new NativePrivateApplyStoreFence(kind, before, before, backup)]);
        NativePrivateApplyRequest decoded = Decode(Encode(request));

        NativePrivateApplyStoreFence fence = Assert.Single(decoded.StoreFences);
        Assert.Equal(kind, fence.Kind);
        AssertImage(before, fence.Before);
        AssertImage(before, fence.ExpectedAfter);
        AssertImage(backup, fence.BeforeBackup!);
    }

    [Fact]
    public void BookmarkFenceKeepsItsExplicitNullBackupAndDoesNotInventHistory()
    {
        NativePrivateMetadataSelection selection = new(PostBookmarksAndNotes: true);
        NativePrivateApplyRequest decoded = Decode(Encode(Create(selection)));
        NativePrivateApplyStoreFence fence = Assert.Single(decoded.StoreFences);

        Assert.Equal(NativePrivateApplyStoreKind.PostBookmarksAndNotes, fence.Kind);
        Assert.Null(fence.BeforeBackup);
        string json = Encoding.UTF8.GetString(Encode(decoded));
        Assert.Contains("\"beforeBackup\":null", json);
    }

    [Fact]
    public void EveryAllowedDispositionStaysAttachedToItsCanonicalTarget()
    {
        ResourceId one = new("zsynthetic-source", "zboard", "01");
        ResourceId two = one with { Thread = "02" };
        ResourceId three = one with { Thread = "03" };
        ResourceId four = one with { Thread = "04" };
        ImmutableArray<ResourceId> threads = [four, three, two, one];
        ImmutableArray<ResourceId> threadRows = [three, two, one];
        ImmutableArray<FavoriteAssetKey> mediaRows = [
            new(three, "z-media-03", "original", Hash('3')),
            new(two, "z-media-02", "original", Hash('2')),
            new(one, "z-media-01", "original", Hash('1'))];
        ImmutableArray<PostBookmark> notes = [
            new(new(four, "04"), "missing note"),
            new(new(three, "03"), "conflict note"),
            new(new(two, "02"), "identical note"),
            new(new(one, "01"), "add note")];
        var selected = new NativePrivateMetadataProjection(threads, All, threadRows, mediaRows, notes);
        var comparison = new NativePrivateMetadataComparisonResult(All, selected,
        [
            new NativePrivateThreadFavoriteComparison(three, NativePrivateMetadataDisposition.MissingTarget),
            new NativePrivateThreadFavoriteComparison(two, NativePrivateMetadataDisposition.Identical),
            new NativePrivateThreadFavoriteComparison(one, NativePrivateMetadataDisposition.Add),
        ],
        [
            new NativePrivateMediaFavoriteComparison(mediaRows[0], NativePrivateMetadataDisposition.MissingTarget),
            new NativePrivateMediaFavoriteComparison(mediaRows[1], NativePrivateMetadataDisposition.Identical),
            new NativePrivateMediaFavoriteComparison(mediaRows[2], NativePrivateMetadataDisposition.Add),
        ],
        [
            new NativePrivateBookmarkComparison(notes[0].Key, notes[0].Note, NativePrivateMetadataDisposition.MissingTarget),
            new NativePrivateBookmarkComparison(notes[1].Key, notes[1].Note, NativePrivateMetadataDisposition.Conflict),
            new NativePrivateBookmarkComparison(notes[2].Key, notes[2].Note, NativePrivateMetadataDisposition.Identical),
            new NativePrivateBookmarkComparison(notes[3].Key, notes[3].Note, NativePrivateMetadataDisposition.Add),
        ]);
        var request = NativePrivateApplyRequest.Create(OperationId, comparison, Binding(), NoChangeFences(All));
        NativePrivateApplyRequest decoded = Decode(Encode(request));

        Assert.Equal(new[] { one, two, three }, decoded.ThreadFavorites.Select(row => row.Target));
        Assert.Equal(NativePrivateMetadataDisposition.Add, decoded.ThreadFavorites[0].Disposition);
        Assert.Equal(NativePrivateMetadataDisposition.Identical, decoded.ThreadFavorites[1].Disposition);
        Assert.Equal(NativePrivateMetadataDisposition.MissingTarget, decoded.ThreadFavorites[2].Disposition);

        Assert.Equal(new[] { one, two, three }, decoded.MediaFavorites.Select(row => row.Target.Thread));
        Assert.Equal(NativePrivateMetadataDisposition.Add, decoded.MediaFavorites[0].Disposition);
        Assert.Equal(NativePrivateMetadataDisposition.Identical, decoded.MediaFavorites[1].Disposition);
        Assert.Equal(NativePrivateMetadataDisposition.MissingTarget, decoded.MediaFavorites[2].Disposition);

        Assert.Equal(new[] { one, two, three, four }, decoded.PostBookmarksAndNotes.Select(row => row.Target.Thread));
        Assert.Equal(NativePrivateMetadataDisposition.Add, decoded.PostBookmarksAndNotes[0].Disposition);
        Assert.Equal(NativePrivateMetadataDisposition.Identical, decoded.PostBookmarksAndNotes[1].Disposition);
        Assert.Equal(NativePrivateMetadataDisposition.Conflict, decoded.PostBookmarksAndNotes[2].Disposition);
        Assert.Equal(NativePrivateMetadataDisposition.MissingTarget, decoded.PostBookmarksAndNotes[3].Disposition);
        Assert.Equal("add note", decoded.PostBookmarksAndNotes[0].IncomingNote);
        Assert.Equal("conflict note", decoded.PostBookmarksAndNotes[2].IncomingNote);
    }

    [Theory]
    [InlineData("threadDispositions")]
    [InlineData("mediaDispositions")]
    [InlineData("bookmarkDispositions")]
    public void RealignedDispositionArraysCannotBorrowTheOriginalRequestIdentity(string arrayName)
    {
        NativePrivateApplyRequest request = EveryDispositionRequest();
        JsonObject document = JsonNode.Parse(Encoding.UTF8.GetString(Encode(request)))!.AsObject();
        JsonArray values = document[arrayName]!.AsArray();
        (values[0], values[1]) = (values[1]!.DeepClone(), values[0]!.DeepClone());

        Reject(document);
    }

    [Theory]
    [InlineData("schema-zero")]
    [InlineData("schema-string")]
    [InlineData("operation-hyphen-guid")]
    [InlineData("profile-compact-guid")]
    [InlineData("zero-package-length")]
    [InlineData("oversized-private-length")]
    [InlineData("uppercase-binding-hash")]
    [InlineData("invalid-store-kind")]
    [InlineData("negative-image-revision")]
    [InlineData("favorite-backup-number")]
    public void ScalarIdentityBindingAndFenceTamperingFailsClosed(string mutation)
    {
        JsonObject document = Json();
        switch (mutation)
        {
            case "schema-zero":
                document["schema"] = 0;
                break;
            case "schema-string":
                document["schema"] = "1";
                break;
            case "operation-hyphen-guid":
                document["operationId"] = OperationId.ToString("D");
                break;
            case "profile-compact-guid":
                document["binding"]!["profileIncarnation"] = ProfileIncarnation.ToString("N");
                break;
            case "zero-package-length":
                document["binding"]!["packageLength"] = 0;
                break;
            case "oversized-private-length":
                document["binding"]!["privateLength"] = (long)(NativePrivateMetadataCodec.MaximumDocumentBytes + 1);
                break;
            case "uppercase-binding-hash":
                document["binding"]!["packageSha256"] = new string('A', 64);
                break;
            case "invalid-store-kind":
                document["storeFences"]![0]!["kind"] = 99;
                break;
            case "negative-image-revision":
                document["storeFences"]![0]!["before"]!["revision"] = -1;
                break;
            case "favorite-backup-number":
                document["storeFences"]![0]!["beforeBackup"] = 1;
                break;
        }
        Reject(document);
    }

    [Fact]
    public void DuplicatePrimitivePropertiesAreRejectedAtEveryNestingLevel()
    {
        string wire = Encoding.UTF8.GetString(Encode(Create(new(true))));
        Reject(wire.Replace("\"schema\":2,", "\"schema\":2,\"schema\":2,"));
        Reject(wire.Replace("\"packageLength\":4096,", "\"packageLength\":4096,\"packageLength\":4096,"));
        Reject(wire.Replace("\"exists\":true,", "\"exists\":true,\"exists\":true,"));
    }

    [Theory]
    [InlineData("root-array")]
    [InlineData("payload-number")]
    [InlineData("dispositions-object")]
    [InlineData("fences-object")]
    [InlineData("image-string")]
    [InlineData("bool-number")]
    public void WrongNestingAndPrimitiveTypesFailBeforeRequestAcceptance(string mutation)
    {
        JsonObject document = Json();
        switch (mutation)
        {
            case "root-array":
                Reject("[]");
                return;
            case "payload-number":
                document["payload"] = 7;
                break;
            case "dispositions-object":
                document["threadDispositions"] = new JsonObject { ["synthetic"] = 1 };
                break;
            case "fences-object":
                document["storeFences"] = new JsonObject { ["synthetic"] = 1 };
                break;
            case "image-string":
                document["storeFences"]![0]!["before"] = "image";
                break;
            case "bool-number":
                document["storeFences"]![0]!["before"]!["exists"] = 1;
                break;
        }
        Reject(document);
    }

    [Fact]
    public void PrecancelledSidecarCallsRemainCancellationNotInvalidData()
    {
        using CancellationTokenSource source = new();
        source.Cancel();

        Assert.Throws<OperationCanceledException>(() => NativePrivateApplyRequestCodec.Serialize(Create(new(true)), source.Token));
        Assert.Throws<OperationCanceledException>(() => NativePrivateApplyRequestCodec.Deserialize(ReadOnlyMemory<byte>.Empty, source.Token));
    }

    private static NativePrivateApplyRequest EveryDispositionRequest()
    {
        ResourceId one = new("zsynthetic-source", "zboard", "01");
        ResourceId two = one with { Thread = "02" };
        ResourceId three = one with { Thread = "03" };
        ResourceId four = one with { Thread = "04" };
        ImmutableArray<ResourceId> threads = [one, two, three, four];
        ImmutableArray<ResourceId> threadFavorites = [one, two, three];
        ImmutableArray<FavoriteAssetKey> media = [
            new(one, "z-media-01", "original", Hash('1')),
            new(two, "z-media-02", "original", Hash('2')),
            new(three, "z-media-03", "original", Hash('3'))];
        ImmutableArray<PostBookmark> notes = [
            new(new(one, "01"), "add note"), new(new(two, "02"), "identical note"),
            new(new(three, "03"), "conflict note"), new(new(four, "04"), "missing note")];
        var projection = new NativePrivateMetadataProjection(threads, All, threadFavorites, media, notes);
        var comparison = new NativePrivateMetadataComparisonResult(All, projection,
        [
            new NativePrivateThreadFavoriteComparison(one, NativePrivateMetadataDisposition.Add),
            new NativePrivateThreadFavoriteComparison(two, NativePrivateMetadataDisposition.Identical),
            new NativePrivateThreadFavoriteComparison(three, NativePrivateMetadataDisposition.MissingTarget),
        ],
        [
            new NativePrivateMediaFavoriteComparison(media[0], NativePrivateMetadataDisposition.Add),
            new NativePrivateMediaFavoriteComparison(media[1], NativePrivateMetadataDisposition.Identical),
            new NativePrivateMediaFavoriteComparison(media[2], NativePrivateMetadataDisposition.MissingTarget),
        ],
        [
            new NativePrivateBookmarkComparison(notes[0].Key, notes[0].Note, NativePrivateMetadataDisposition.Add),
            new NativePrivateBookmarkComparison(notes[1].Key, notes[1].Note, NativePrivateMetadataDisposition.Identical),
            new NativePrivateBookmarkComparison(notes[2].Key, notes[2].Note, NativePrivateMetadataDisposition.Conflict),
            new NativePrivateBookmarkComparison(notes[3].Key, notes[3].Note, NativePrivateMetadataDisposition.MissingTarget),
        ]);
        return NativePrivateApplyRequest.Create(OperationId, comparison, Binding(), NoChangeFences(All));
    }
}

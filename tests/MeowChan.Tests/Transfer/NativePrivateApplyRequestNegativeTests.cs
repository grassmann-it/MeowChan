using System.Collections.Immutable;
using System.IO;
using MeowChan;
using MeowChan.Favorites;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// P05 request-contract negatives. These tests use only synthetic, in-memory DTOs;
// they do not open stores, profiles, media, build runners, or RUNSTATE.
public sealed class NativePrivateApplyRequestNegativeTests
{
    private static readonly ResourceId Thread = new("synthetic", "board", "100");
    private static readonly FavoriteAssetKey Media = new(Thread, "m-1", "original", Hash);
    private static readonly PostBookmark Bookmark = new(new(Thread, "post-1"), "synthetic incoming note");
    private static readonly Guid Instance = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Operation = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Secret = "PRIVATE-SYNTHETIC-NOTE-DO-NOT-ECHO";

    private static NativePrivateApplyRequestBinding Binding(
        long packageLength = 1024, string packageSha = Hash, string publicSha = Hash,
        long privateLength = 512, string privateSha = Hash, Guid? profile = null,
        string targetFence = Hash)
        => new(packageLength, packageSha, publicSha, privateLength, privateSha,
            profile ?? Instance, targetFence);

    private static NativePrivateApplyStoreImage Image(
        bool exists = true, int schema = 1, Guid? instance = null, long revision = 4,
        long bytes = 100, string? sha = Hash)
        => new(exists, schema, exists ? instance ?? Instance : instance, revision, bytes, sha);

    private static NativePrivateApplyStoreImage Missing(
        int schema = 0, Guid? instance = null, long revision = 0, long bytes = 0,
        string? sha = null)
        => new(false, schema, instance, revision, bytes, sha);

    private static NativePrivateMetadataProjection Projection(
        NativePrivateMetadataSelection selection,
        ImmutableArray<ResourceId>? threads = null,
        ImmutableArray<ResourceId>? threadFavorites = null,
        ImmutableArray<FavoriteAssetKey>? media = null,
        ImmutableArray<PostBookmark>? bookmarks = null)
        => new(threads ?? [Thread], selection,
            threadFavorites ?? [], media ?? [], bookmarks ?? []);

    private static NativePrivateMetadataComparisonResult Comparison(
        NativePrivateMetadataSelection selection,
        NativePrivateMetadataProjection? projection = null,
        ImmutableArray<NativePrivateThreadFavoriteComparison>? threadRows = null,
        ImmutableArray<NativePrivateMediaFavoriteComparison>? mediaRows = null,
        ImmutableArray<NativePrivateBookmarkComparison>? bookmarkRows = null)
    {
        NativePrivateMetadataProjection incoming = projection ?? Projection(selection,
            threadFavorites: selection.ThreadFavorites ? [Thread] : [],
            media: selection.MediaFavorites ? [Media] : [],
            bookmarks: selection.PostBookmarksAndNotes ? [Bookmark] : []);
        ImmutableArray<NativePrivateThreadFavoriteComparison> threads = threadRows
            ?? (selection.ThreadFavorites
                ? [new NativePrivateThreadFavoriteComparison(Thread, NativePrivateMetadataDisposition.Add)] : []);
        ImmutableArray<NativePrivateMediaFavoriteComparison> media = mediaRows
            ?? (selection.MediaFavorites
                ? [new NativePrivateMediaFavoriteComparison(Media, NativePrivateMetadataDisposition.Add)] : []);
        ImmutableArray<NativePrivateBookmarkComparison> bookmarks = bookmarkRows
            ?? (selection.PostBookmarksAndNotes
                ? [new NativePrivateBookmarkComparison(Bookmark.Key, Bookmark.Note,
                    NativePrivateMetadataDisposition.Add)] : []);
        return new(selection, incoming, threads, media, bookmarks);
    }

    private static NativePrivateApplyStoreFence Fence(
        NativePrivateApplyStoreKind kind,
        NativePrivateApplyStoreImage? before = null,
        NativePrivateApplyStoreImage? after = null,
        NativePrivateApplyStoreImage? backup = null)
        => new(kind, before ?? Image(), after ?? before ?? Image(), backup ?? Image());

    private static ImmutableArray<NativePrivateApplyStoreFence> Fences(
        NativePrivateMetadataSelection selection,
        NativePrivateApplyStoreImage? threadBefore = null,
        NativePrivateApplyStoreImage? threadAfter = null,
        NativePrivateApplyStoreImage? threadBackup = null,
        NativePrivateApplyStoreImage? mediaBefore = null,
        NativePrivateApplyStoreImage? mediaAfter = null,
        NativePrivateApplyStoreImage? mediaBackup = null,
        NativePrivateApplyStoreImage? bookmarkBefore = null,
        NativePrivateApplyStoreImage? bookmarkAfter = null,
        NativePrivateApplyStoreImage? bookmarkBackup = null)
    {
        var result = ImmutableArray.CreateBuilder<NativePrivateApplyStoreFence>();
        if (selection.ThreadFavorites)
            result.Add(Fence(NativePrivateApplyStoreKind.ThreadFavorites, threadBefore, threadAfter, threadBackup));
        if (selection.MediaFavorites)
            result.Add(Fence(NativePrivateApplyStoreKind.MediaFavorites, mediaBefore, mediaAfter, mediaBackup));
        if (selection.PostBookmarksAndNotes)
            result.Add(new(NativePrivateApplyStoreKind.PostBookmarksAndNotes,
                bookmarkBefore ?? Image(), bookmarkAfter ?? bookmarkBefore ?? Image(), bookmarkBackup));
        return result.ToImmutable();
    }

    private static NativePrivateApplyRequest Create(
        NativePrivateMetadataSelection selection,
        NativePrivateMetadataComparisonResult? comparison = null,
        NativePrivateApplyRequestBinding? binding = null,
        ImmutableArray<NativePrivateApplyStoreFence>? fences = null,
        Guid? operation = null)
        => NativePrivateApplyRequest.Create(operation ?? Operation,
            comparison ?? Comparison(selection), binding ?? Binding(),
            fences ?? Fences(selection));

    private static void Reject(
        NativePrivateMetadataSelection selection,
        NativePrivateMetadataComparisonResult? comparison = null,
        NativePrivateApplyRequestBinding? binding = null,
        ImmutableArray<NativePrivateApplyStoreFence>? fences = null,
        Guid? operation = null)
    {
        AssertInvalid(() => Create(selection, comparison, binding, fences, operation));
    }

    private static void AssertInvalid(Action action)
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(action);
        Assert.Equal("The private apply request is invalid.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Thread.Source, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyOperationIdIsRejected()
        => Reject(new(ThreadFavorites: true), operation: Guid.Empty);

    [Theory]
    [InlineData(0, 512)]
    [InlineData(-1, 512)]
    [InlineData(2_147_483_649L, 512)]
    [InlineData(1024, 0)]
    [InlineData(1024, -1)]
    [InlineData(512, 513)]
    public void BindingLengthsMustBePositiveBoundedAndOrdered(long packageLength, long privateLength)
        => Reject(new(ThreadFavorites: true), binding: Binding(packageLength: packageLength,
            privateLength: privateLength));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void BindingHashesMustBeLowercaseSha256(string? invalidHash)
    {
        Reject(new(ThreadFavorites: true), binding: Binding(packageSha: invalidHash!));
        Reject(new(ThreadFavorites: true), binding: Binding(publicSha: invalidHash!));
        Reject(new(ThreadFavorites: true), binding: Binding(privateSha: invalidHash!));
        Reject(new(ThreadFavorites: true), binding: Binding(targetFence: invalidHash!));
    }

    [Fact]
    public void NullBindingAndEmptyProfileIncarnationAreRejected()
    {
        AssertInvalid(() => NativePrivateApplyRequest.Create(Operation, Comparison(default), null!, []));
        AssertInvalid(() => NativePrivateApplyRequest.Create(Operation, null!, Binding(), []));
        AssertInvalid(() => NativePrivateApplyRequest.Create(Operation,
            new(default, null!, [], [], []), Binding(), []));
        Reject(new(ThreadFavorites: true), binding: Binding(profile: Guid.Empty));
    }

    [Fact]
    public void UnselectedPayloadAndRowsCannotLeakIntoTheRequest()
    {
        NativePrivateMetadataSelection selection = new(ThreadFavorites: true);
        NativePrivateMetadataProjection leakedPayload = Projection(selection, media: [Media]);
        Reject(selection, Comparison(selection, leakedPayload));

        NativePrivateMetadataComparisonResult leakedRows = Comparison(selection,
            threadRows: [new(Thread, NativePrivateMetadataDisposition.Add)],
            mediaRows: [new(Media, NativePrivateMetadataDisposition.Add)]);
        Reject(selection, leakedRows);
    }

    [Fact]
    public void RowIdentityAndIncomingNoteMustMatchSelectedPayload()
    {
        NativePrivateMetadataSelection selection = new(PostBookmarksAndNotes: true);
        NativePrivateMetadataProjection projection = Projection(selection, bookmarks: [Bookmark]);
        Reject(selection, Comparison(selection, projection,
            bookmarkRows: [new(new(Thread, "other-post"), Bookmark.Note, NativePrivateMetadataDisposition.Add)]));
        Reject(selection, Comparison(selection, projection,
            bookmarkRows: [new(Bookmark.Key, Secret, NativePrivateMetadataDisposition.Add)]));
    }

    [Fact]
    public void DuplicateRowsAndUndefinedDispositionsAreRejected()
    {
        NativePrivateMetadataSelection selection = new(ThreadFavorites: true);
        NativePrivateMetadataProjection projection = Projection(selection,
            threadFavorites: [Thread, Thread with { Thread = "200" }]);
        Reject(selection, Comparison(selection, projection,
            threadRows: [new(Thread, NativePrivateMetadataDisposition.Add),
                new(Thread, NativePrivateMetadataDisposition.Add)]));

        Reject(selection, Comparison(selection, Projection(selection, threadFavorites: [Thread]),
            threadRows: [new(Thread, (NativePrivateMetadataDisposition)99)]));
        Reject(selection, Comparison(selection, Projection(selection, threadFavorites: [Thread]),
            threadRows: [new(Thread, NativePrivateMetadataDisposition.Conflict)]));
    }

    [Fact]
    public void NullRowsAreRejectedWithoutDiagnosticDetails()
    {
        NativePrivateMetadataSelection selection = new(ThreadFavorites: true);
        ImmutableArray<NativePrivateThreadFavoriteComparison> rows =
            ImmutableArray.CreateRange<NativePrivateThreadFavoriteComparison>([null!]);
        Reject(selection, Comparison(selection, Projection(selection, threadFavorites: [Thread]), rows));
    }

    [Fact]
    public void StoreFenceCountDefaultAndOrderAreStrict()
    {
        NativePrivateMetadataSelection all = new(true, true, true);
        Reject(all, fences: default(ImmutableArray<NativePrivateApplyStoreFence>));
        Reject(all, fences: ImmutableArray.Create(
            Fence(NativePrivateApplyStoreKind.ThreadFavorites),
            Fence(NativePrivateApplyStoreKind.MediaFavorites),
            Fence(NativePrivateApplyStoreKind.PostBookmarksAndNotes),
            Fence(NativePrivateApplyStoreKind.PostBookmarksAndNotes)));
        Reject(all, fences: [
            Fence(NativePrivateApplyStoreKind.MediaFavorites),
            Fence(NativePrivateApplyStoreKind.ThreadFavorites),
            Fence(NativePrivateApplyStoreKind.PostBookmarksAndNotes)]);
    }

    [Fact]
    public void EverySelectedEmptyCategoryStillNeedsAnExplicitFence()
    {
        NativePrivateMetadataSelection all = new(true, true, true);
        Reject(all, fences: []);
        Reject(all, fences: [Fence(NativePrivateApplyStoreKind.ThreadFavorites)]);
        Reject(new(MediaFavorites: true), fences: []);
    }

    [Fact]
    public void MissingImageHasOnlyTheExplicitZeroSentinelShape()
    {
        NativePrivateMetadataSelection selection = new(ThreadFavorites: true);
        Reject(selection, fences: Fences(selection,
            threadBefore: Missing(schema: 1), threadAfter: Image(), threadBackup: Image()));
        Reject(selection, fences: Fences(selection,
            threadBefore: Missing(instance: Instance), threadAfter: Image(), threadBackup: Image()));
        Reject(selection, fences: Fences(selection,
            threadBefore: Missing(revision: 1), threadAfter: Image(), threadBackup: Image()));
        Reject(selection, fences: Fences(selection,
            threadBefore: Missing(bytes: 1), threadAfter: Image(), threadBackup: Image()));
        Reject(selection, fences: Fences(selection,
            threadBefore: Missing(sha: Hash), threadAfter: Image(), threadBackup: Image()));
    }

    [Fact]
    public void BookmarkBackupsAreForbiddenAndFavoriteBackupsAreRequired()
    {
        NativePrivateMetadataSelection bookmarks = new(PostBookmarksAndNotes: true);
        Reject(bookmarks, fences: Fences(bookmarks, bookmarkBackup: Image()));

        NativePrivateMetadataSelection threadFavorites = new(ThreadFavorites: true);
        Reject(threadFavorites, fences: [new(NativePrivateApplyStoreKind.ThreadFavorites,
            Image(), Image(), null)]);
        NativePrivateMetadataSelection mediaFavorites = new(MediaFavorites: true);
        Reject(mediaFavorites, fences: [new(NativePrivateApplyStoreKind.MediaFavorites,
            Image(), Image(), null)]);
        Reject(bookmarks, fences: Fences(bookmarks, bookmarkBackup: Missing()));
    }

    public static IEnumerable<object[]> NoAdditionCases()
    {
        for (int category = 0; category < 3; category++)
        {
            yield return [category, "empty"];
            yield return [category, "identical"];
            yield return [category, "missing"];
            if (category == 2) yield return [category, "conflict"];
        }
    }

    [Theory]
    [MemberData(nameof(NoAdditionCases))]
    public void EveryCategoryWithoutAddRowsRequiresExactlyUnchangedAfter(int category, string state)
    {
        NativePrivateMetadataSelection selection = SelectionFor(category);
        NativePrivateMetadataDisposition disposition = state switch
        {
            "identical" => NativePrivateMetadataDisposition.Identical,
            "missing" => NativePrivateMetadataDisposition.MissingTarget,
            "conflict" => NativePrivateMetadataDisposition.Conflict,
            _ => NativePrivateMetadataDisposition.Add
        };
        bool empty = state == "empty";
        NativePrivateMetadataProjection projection = Projection(selection,
            threadFavorites: category == 0 && !empty ? [Thread] : [],
            media: category == 1 && !empty ? [Media] : [],
            bookmarks: category == 2 && !empty ? [Bookmark] : []);
        NativePrivateMetadataComparisonResult comparison = Comparison(selection, projection,
            threadRows: category == 0 && !empty ? [new(Thread, disposition)] : [],
            mediaRows: category == 1 && !empty ? [new(Media, disposition)] : [],
            bookmarkRows: category == 2 && !empty ? [new(Bookmark.Key, Bookmark.Note, disposition)] : []);
        NativePrivateApplyStoreImage before = Image();
        NativePrivateApplyStoreImage changed = Image(revision: 5, sha: new string('b', 64));
        NativePrivateApplyStoreKind kind = (NativePrivateApplyStoreKind)category;
        NativePrivateApplyStoreImage? backup = category == 2 ? null : Missing();
        Reject(selection, comparison, fences: [new(kind, before, changed, backup)]);
        // Independent scalar equality, not just object reference equality.
        NativePrivateApplyRequest unchanged = Create(selection, comparison,
            fences: [new(kind, before, Image(), backup)]);
        Assert.Equal(selection, unchanged.Selection);
    }

    private static NativePrivateMetadataSelection SelectionFor(int category)
        => new(category == 0, category == 1, category == 2);

    [Fact]
    public void ChangedAfterImageMustRemainPresentAndAdvanceRevisionExactlyOnce()
    {
        NativePrivateMetadataSelection selection = new(ThreadFavorites: true);
        NativePrivateApplyStoreImage before = Image(revision: 8);
        NativePrivateApplyStoreImage backup = Image();
        Reject(selection, fences: Fences(selection, threadBefore: before,
            threadAfter: Missing(), threadBackup: backup));
        Reject(selection, fences: Fences(selection, threadBefore: before,
            threadAfter: Image(revision: 10), threadBackup: backup));
        Reject(selection, fences: Fences(selection, threadBefore: before,
            threadAfter: Image(instance: Guid.Parse("33333333-3333-3333-3333-333333333333"), revision: 9),
            threadBackup: backup));
        Reject(selection, fences: Fences(selection, threadBefore: Image(revision: long.MaxValue),
            threadAfter: Image(revision: long.MaxValue, sha: new string('b', 64)), threadBackup: backup));
    }

    [Fact]
    public void AddWithAnUnchangedImageIsOnlyAPassiveClaimAndRemainsAllowed()
    {
        NativePrivateMetadataSelection selection = new(ThreadFavorites: true);
        NativePrivateApplyRequest request = Create(selection, fences: Fences(selection,
            threadBefore: Missing(), threadAfter: Missing(), threadBackup: Missing()));
        Assert.Equal(NativePrivateMetadataDisposition.Add, Assert.Single(request.ThreadFavorites).Disposition);
        Assert.False(Assert.Single(request.StoreFences).ExpectedAfter.Exists);
    }

    [Fact]
    public void CodecRejectsStrictNotesUnicodeLineEndingsAndEntryBudgets()
    {
        NativePrivateMetadataSelection selection = new(PostBookmarksAndNotes: true);
        static NativePrivateMetadataComparisonResult WithNote(string note)
        {
            PostBookmark bookmark = new(new(Thread, "post-1"), note);
            NativePrivateMetadataSelection selection = new(PostBookmarksAndNotes: true);
            NativePrivateMetadataProjection projection = Projection(selection, bookmarks: [bookmark]);
            return Comparison(selection, projection,
                bookmarkRows: [new(bookmark.Key, note, NativePrivateMetadataDisposition.Add)]);
        }

        Reject(selection, WithNote("line\rbreak"));
        Reject(selection, WithNote("line\r\nbreak"));
        Reject(selection, WithNote("bad\0control"));
        Reject(selection, WithNote("x\ud800"));
        Reject(selection, WithNote(new string('x', PostBookmarkStore.MaximumNoteCodepoints + 1)));
        Reject(selection, WithNote(string.Concat(Enumerable.Repeat("😺", PostBookmarkStore.MaximumNoteCodepoints + 1))));

        ImmutableArray<ResourceId> tooManyThreads = Enumerable.Range(0, NativePrivateMetadataCodec.MaximumThreads + 1)
            .Select(i => Thread with { Thread = i.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            .ToImmutableArray();
        Reject(new(), Comparison(new(), Projection(new(), threads: tooManyThreads)));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void UnselectedCategoriesRejectBothPayloadAndRows(int category)
    {
        NativePrivateMetadataSelection none = default;
        NativePrivateMetadataProjection leaked = Projection(none,
            threadFavorites: category == 0 ? [Thread] : [],
            media: category == 1 ? [Media] : [],
            bookmarks: category == 2 ? [new(Bookmark.Key, Secret)] : []);
        Reject(none, Comparison(none, leaked));
        Reject(none, Comparison(none,
            threadRows: category == 0 ? [new(Thread, NativePrivateMetadataDisposition.Add)] : [],
            mediaRows: category == 1 ? [new(Media, NativePrivateMetadataDisposition.Add)] : [],
            bookmarkRows: category == 2 ? [new(Bookmark.Key, Secret, NativePrivateMetadataDisposition.Add)] : []));
        // A comparison's category flags cannot disagree with its selected projection.
        Reject(none, Comparison(none, Projection(SelectionFor(category))));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void EverySelectedEmptyCategoryNeedsItsOwnFence(int category)
    {
        NativePrivateMetadataSelection selection = SelectionFor(category);
        NativePrivateMetadataComparisonResult empty = Comparison(selection,
            Projection(selection), [], [], []);
        Reject(selection, empty, fences: []);
        Assert.Single(Create(selection, empty).StoreFences);
    }

    [Fact]
    public void DefaultRowArraysAndDefaultProjectionArraysAreRejected()
    {
        NativePrivateMetadataSelection selection = new(true, true, true);
        Reject(selection, Comparison(selection, threadRows: default(ImmutableArray<NativePrivateThreadFavoriteComparison>)));
        Reject(selection, Comparison(selection, mediaRows: default(ImmutableArray<NativePrivateMediaFavoriteComparison>)));
        Reject(selection, Comparison(selection, bookmarkRows: default(ImmutableArray<NativePrivateBookmarkComparison>)));
        Reject(selection, Comparison(selection, Projection(selection, threads: default(ImmutableArray<ResourceId>))));
        Reject(selection, Comparison(selection, Projection(selection, threadFavorites: default(ImmutableArray<ResourceId>))));
        Reject(selection, Comparison(selection, Projection(selection, media: default(ImmutableArray<FavoriteAssetKey>))));
        Reject(selection, Comparison(selection, Projection(selection, bookmarks: default(ImmutableArray<PostBookmark>))));
    }

    [Fact]
    public void DuplicateMediaAndBookmarkRowsCannotReplaceMissingRows()
    {
        NativePrivateMetadataSelection mediaSelection = new(MediaFavorites: true);
        NativePrivateMetadataProjection mediaPayload = Projection(mediaSelection,
            media: [Media, Media with { MediaIdentifier = "m-2" }]);
        Reject(mediaSelection, Comparison(mediaSelection, mediaPayload,
            mediaRows: [new(Media, NativePrivateMetadataDisposition.Add), new(Media, NativePrivateMetadataDisposition.Add)]));
        NativePrivateMetadataSelection noteSelection = new(PostBookmarksAndNotes: true);
        NativePrivateMetadataProjection notePayload = Projection(noteSelection,
            bookmarks: [Bookmark, new(new(Thread, "post-2"), "other synthetic note")]);
        Reject(noteSelection, Comparison(noteSelection, notePayload,
            bookmarkRows: [new(Bookmark.Key, Bookmark.Note, NativePrivateMetadataDisposition.Add),
                new(Bookmark.Key, Bookmark.Note, NativePrivateMetadataDisposition.Add)]));
    }

    [Theory]
    [InlineData("thread-source")] [InlineData("thread-board")] [InlineData("thread-id")]
    [InlineData("media-id")] [InlineData("variant")] [InlineData("sha")]
    public void FullMediaIdentityMustMatchRatherThanJustHashOrMediaId(string component)
    {
        NativePrivateMetadataSelection selection = new(MediaFavorites: true);
        FavoriteAssetKey wrong = component switch
        {
            "thread-source" => Media with { Thread = Thread with { Source = "SYNTHETIC" } },
            "thread-board" => Media with { Thread = Thread with { Board = "BOARD" } },
            "thread-id" => Media with { Thread = Thread with { Thread = "200" } },
            "media-id" => Media with { MediaIdentifier = "m-2" },
            "variant" => Media with { Variant = "preview" },
            _ => Media with { Sha256 = new string('b', 64) }
        };
        Reject(selection, Comparison(selection, mediaRows: [new(wrong, NativePrivateMetadataDisposition.Add)]));
    }

    [Fact]
    public void NullTargetsNullNotesAndMediaConflictOrUndefinedDispositionsAreRejected()
    {
        NativePrivateMetadataSelection threads = new(ThreadFavorites: true);
        Reject(threads, Comparison(threads, threadRows: [new(null!, NativePrivateMetadataDisposition.Add)]));
        NativePrivateMetadataSelection media = new(MediaFavorites: true);
        Reject(media, Comparison(media, mediaRows: [null!]));
        Reject(media, Comparison(media, mediaRows: [new(null!, NativePrivateMetadataDisposition.Add)]));
        Reject(media, Comparison(media, mediaRows: [new(Media, NativePrivateMetadataDisposition.Conflict)]));
        Reject(media, Comparison(media, mediaRows: [new(Media, (NativePrivateMetadataDisposition)(-1))]));
        NativePrivateMetadataSelection notes = new(PostBookmarksAndNotes: true);
        Reject(notes, Comparison(notes, bookmarkRows: [null!]));
        Reject(notes, Comparison(notes, bookmarkRows: [new(null!, Bookmark.Note, NativePrivateMetadataDisposition.Add)]));
        Reject(notes, Comparison(notes, bookmarkRows: [new(Bookmark.Key, null!, NativePrivateMetadataDisposition.Add)]));
        Reject(notes, Comparison(notes, bookmarkRows: [new(Bookmark.Key, Bookmark.Note, (NativePrivateMetadataDisposition)99)]));
    }

    [Fact]
    public void FenceKindNullAndDuplicateOrUnselectedFencesAreRejected()
    {
        NativePrivateMetadataSelection threads = new(ThreadFavorites: true);
        Reject(threads, fences: [null!]);
        Reject(threads, fences: [Fence((NativePrivateApplyStoreKind)99)]);
        Reject(threads, fences: [Fence(NativePrivateApplyStoreKind.ThreadFavorites), Fence(NativePrivateApplyStoreKind.ThreadFavorites)]);
        Reject(default, fences: [Fence(NativePrivateApplyStoreKind.ThreadFavorites)]);
        Reject(default, fences: default(ImmutableArray<NativePrivateApplyStoreFence>));
        Reject(threads, fences: [new(NativePrivateApplyStoreKind.ThreadFavorites, null!, Image(), Missing())]);
        Reject(threads, fences: [new(NativePrivateApplyStoreKind.ThreadFavorites, Image(), null!, Missing())]);
    }

    public static IEnumerable<object[]> InvalidStoreImageCases()
    {
        string[] fields = ["schema-low", "schema-high", "empty-instance", "negative-revision",
            "empty-length", "negative-length", "over-cap", "null-sha", "empty-sha", "short-sha",
            "uppercase-sha", "nonhex-sha", "missing-schema", "missing-instance", "missing-revision",
            "missing-length", "missing-sha"];
        for (int category = 0; category < 3; category++)
            for (int slot = 0; slot < (category == 2 ? 2 : 3); slot++)
                foreach (string field in fields)
                    yield return [category, slot, field];
    }

    [Theory]
    [MemberData(nameof(InvalidStoreImageCases))]
    public void BeforeAfterAndFavoriteBackupImagesAreIndividuallyValidated(int category, int slot, string field)
    {
        NativePrivateMetadataSelection selection = SelectionFor(category);
        long cap = category == 2 ? PostBookmarkStore.MaximumDocumentBytes : ProfileStoreLimits.FavoriteFileCeilingBytes;
        NativePrivateApplyStoreImage invalid = field switch
        {
            "schema-low" => Image(schema: 0),
            "schema-high" => Image(schema: 256),
            "empty-instance" => Image(instance: Guid.Empty),
            "negative-revision" => Image(revision: -1),
            "empty-length" => Image(bytes: 0),
            "negative-length" => Image(bytes: -1),
            "over-cap" => Image(bytes: cap + 1),
            "null-sha" => Image(sha: null),
            "empty-sha" => Image(sha: ""),
            "short-sha" => Image(sha: "short"),
            "uppercase-sha" => Image(sha: new string('A', 64)),
            "nonhex-sha" => Image(sha: new string('g', 64)),
            "missing-schema" => Missing(schema: 1),
            "missing-instance" => Missing(instance: Guid.Empty),
            "missing-revision" => Missing(revision: 1),
            "missing-length" => Missing(bytes: 1),
            _ => Missing(sha: Hash)
        };
        NativePrivateApplyStoreImage before = slot == 0 ? invalid : Image();
        NativePrivateApplyStoreImage after = slot == 1 ? invalid : Image(revision: 5);
        NativePrivateApplyStoreImage? backup = category == 2 ? null : slot == 2 ? invalid : Missing();
        Reject(selection, fences: [new((NativePrivateApplyStoreKind)category, before, after, backup)]);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void AChangedImageRequiresANonemptyInstanceEvenForLegacyBeforeImages(int category)
    {
        NativePrivateMetadataSelection selection = SelectionFor(category);
        NativePrivateApplyStoreImage legacyBefore = new(true, 1, null, 4, 100, Hash);
        NativePrivateApplyStoreImage afterWithoutInstance = new(true, 1, null, 5, 100, new string('b', 64));
        NativePrivateApplyStoreImage? backup = category == 2 ? null : Missing();
        Reject(selection, fences: [new((NativePrivateApplyStoreKind)category, legacyBefore, afterWithoutInstance, backup)]);
        // No-write legacy images may lack an instance. The guard is about changes.
        Assert.Single(Create(selection, fences: [new((NativePrivateApplyStoreKind)category,
            legacyBefore, new(true, 1, null, 4, 100, Hash), backup)]).StoreFences);
    }

    [Fact]
    public void NoAdditionGuardIsPerCategoryNotSatisfiedByAnotherStoresAddRow()
    {
        NativePrivateMetadataSelection all = new(true, true, true);
        NativePrivateMetadataComparisonResult comparison = Comparison(all,
            bookmarkRows: [new(Bookmark.Key, Bookmark.Note, NativePrivateMetadataDisposition.Conflict)]);
        Reject(all, comparison, fences: Fences(all, bookmarkAfter: Image(revision: 5)));
    }

    [Fact]
    public void CodecDocumentAndEntryBudgetsAlsoApplyBeforeRequestHashing()
    {
        Reject(default, binding: Binding(packageLength: 2L * NativePrivateMetadataCodec.MaximumDocumentBytes,
            privateLength: NativePrivateMetadataCodec.MaximumDocumentBytes + 1L));
        NativePrivateMetadataSelection notes = new(PostBookmarksAndNotes: true);
        ImmutableArray<PostBookmark> tooMany = Enumerable.Range(0, NativePrivateMetadataCodec.MaximumBookmarks + 1)
            .Select(i => new PostBookmark(new(Thread, i.ToString(System.Globalization.CultureInfo.InvariantCulture)), ""))
            .ToImmutableArray();
        Reject(notes, Comparison(notes, Projection(notes, bookmarks: tooMany), bookmarkRows: []));
        NativePrivateMetadataSelection media = new(MediaFavorites: true);
        ImmutableArray<FavoriteAssetKey> tooManyMedia = Enumerable.Range(0, NativePrivateMetadataCodec.MaximumMediaFavorites + 1)
            .Select(i => Media with { MediaIdentifier = i.ToString(System.Globalization.CultureInfo.InvariantCulture) }).ToImmutableArray();
        Reject(media, Comparison(media, Projection(media, media: tooManyMedia), mediaRows: []));
        string longValidNote = new('x', PostBookmarkStore.MaximumNoteCodepoints);
        ImmutableArray<PostBookmark> tooManyBytes = Enumerable.Range(0, 2200)
            .Select(i => new PostBookmark(new(Thread, i.ToString(System.Globalization.CultureInfo.InvariantCulture)), longValidNote))
            .ToImmutableArray();
        // Individually valid notes, within entry count, but over the aggregate wire budget.
        Reject(notes, Comparison(notes, Projection(notes, bookmarks: tooManyBytes), bookmarkRows: []));
    }

    [Fact]
    public void CancellationIsObservedBeforeAnyRequestIsReturned()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => NativePrivateApplyRequest.Create(
            Operation, Comparison(new(ThreadFavorites: true)), Binding(),
            Fences(new(ThreadFavorites: true)), cts.Token));
    }

    [Fact]
    public void InvalidRequestsAndRowsHaveOpaqueToStringValues()
    {
        NativePrivateMetadataSelection selection = new(PostBookmarksAndNotes: true);
        NativePrivateMetadataComparisonResult comparison = Comparison(selection,
            Projection(selection, bookmarks: [new(new(Thread, "post-1"), Secret)]),
            bookmarkRows: [new(new(Thread, "post-1"), Secret, NativePrivateMetadataDisposition.Add)]);
        NativePrivateApplyRequest request = Create(selection, comparison);
        Assert.DoesNotContain(Secret, request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Thread.Source, request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, comparison.PostBookmarksAndNotes[0].ToString(), StringComparison.Ordinal);
        Reject(selection, comparison, binding: Binding(packageSha: "not-a-sha"));
    }
}

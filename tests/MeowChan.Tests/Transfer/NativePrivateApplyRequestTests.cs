using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MeowChan.Archive;
using MeowChan.Favorites;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Pure request identity tests: synthetic values only, no profile/store/file access.
public sealed class NativePrivateApplyRequestTests
{
    private static readonly ResourceId Thread = new("synthetic-source", "synthetic-board", "100");
    private static readonly ResourceId OtherThread = Thread with { Thread = "200" };
    private static readonly Guid Operation = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Profile = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid Instance = Guid.Parse("12345678-1234-1234-1234-123456789abc");
    private static readonly Guid OtherInstance = Guid.Parse("87654321-4321-4321-4321-cba987654321");
    private static readonly NativePrivateMetadataSelection All = new(true, true, true);
    private static readonly DateTimeOffset When = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
    private static string Hash(char value) => new(value, 64);

    private static NativePrivateApplyRequestBinding Binding() =>
        new(1024, Hash('1'), Hash('2'), 256, Hash('3'), Profile, Hash('4'));

    private static NativePrivateApplyStoreImage Image(int schema = 2, Guid? instance = null,
        long revision = 4, long byteLength = 100, string? sha256 = null) =>
        new(true, schema, instance ?? Instance, revision, byteLength, sha256 ?? Hash('a'));

    private static NativePrivateApplyStoreImage Missing() => new(false, 0, null, 0, 0, null);

    // All default steps are explicit no-change claims, including selected-empty.
    private static ImmutableArray<NativePrivateApplyStoreFence> Fences(NativePrivateMetadataSelection selection)
    {
        var result = ImmutableArray.CreateBuilder<NativePrivateApplyStoreFence>();
        foreach (NativePrivateApplyStoreKind kind in Enum.GetValues<NativePrivateApplyStoreKind>())
        {
            bool chosen = kind switch
            {
                NativePrivateApplyStoreKind.ThreadFavorites => selection.ThreadFavorites,
                NativePrivateApplyStoreKind.MediaFavorites => selection.MediaFavorites,
                _ => selection.PostBookmarksAndNotes
            };
            if (!chosen) continue;
            NativePrivateApplyStoreImage before = Image();
            result.Add(new(kind, before, before,
                kind == NativePrivateApplyStoreKind.PostBookmarksAndNotes ? null : Missing()));
        }
        return result.ToImmutable();
    }

    private static NativePrivateMetadataComparisonResult Rows(NativePrivateMetadataProjection selected,
        NativePrivateMetadataDisposition disposition = NativePrivateMetadataDisposition.Add) =>
        new(selected.Selection, selected,
            selected.ThreadFavorites.Select(t => new NativePrivateThreadFavoriteComparison(t, disposition)).ToImmutableArray(),
            selected.MediaFavorites.Select(m => new NativePrivateMediaFavoriteComparison(m, disposition)).ToImmutableArray(),
            selected.PostBookmarksAndNotes.Select(n => new NativePrivateBookmarkComparison(n.Key, n.Note, disposition)).ToImmutableArray());

    private static NativePrivateMetadataProjection Single(string note = "synthetic incoming note",
        ResourceId? thread = null, string media = "synthetic-media", string variant = "original", string post = "1")
    {
        thread ??= Thread;
        return new([thread], All, [thread], [new(thread, media, variant, Hash('5'))], [new(new(thread, post), note)]);
    }

    private static NativePrivateApplyRequest Request(NativePrivateMetadataComparisonResult comparison,
        NativePrivateApplyRequestBinding? binding = null,
        ImmutableArray<NativePrivateApplyStoreFence>? fences = null, Guid? operation = null) =>
        NativePrivateApplyRequest.Create(operation ?? Operation, comparison, binding ?? Binding(),
            fences ?? Fences(comparison.Selection));

    private static ImmutableArray<T> Permute<T>(ImmutableArray<T> input, int seed) =>
        input.Select((value, index) => (value, rank: (index + seed) % Math.Max(1, input.Length)))
            .OrderByDescending(pair => pair.rank).Select(pair => pair.value).ToImmutableArray();

    private static NativePrivateMetadataComparisonResult Permute(NativePrivateMetadataComparisonResult input, int seed)
    {
        var source = input.SelectedIncoming;
        var shuffled = new NativePrivateMetadataProjection(Permute(source.Threads, seed), source.Selection,
            Permute(source.ThreadFavorites, seed + 1), Permute(source.MediaFavorites, seed + 2),
            Permute(source.PostBookmarksAndNotes, seed + 3));
        return new(input.Selection, shuffled, Permute(input.ThreadFavorites, seed + 4),
            Permute(input.MediaFavorites, seed + 5), Permute(input.PostBookmarksAndNotes, seed + 6));
    }

    private static IOrderedEnumerable<ResourceId> OrderedThreads(IEnumerable<ResourceId> ids) =>
        ids.OrderBy(t => t.Source, StringComparer.Ordinal).ThenBy(t => t.Board, StringComparer.Ordinal)
            .ThenBy(t => t.Thread, StringComparer.Ordinal);

    private static IOrderedEnumerable<FavoriteAssetKey> OrderedMedia(IEnumerable<FavoriteAssetKey> keys) =>
        keys.OrderBy(k => k.Thread.Source, StringComparer.Ordinal).ThenBy(k => k.Thread.Board, StringComparer.Ordinal)
            .ThenBy(k => k.Thread.Thread, StringComparer.Ordinal).ThenBy(k => k.MediaIdentifier, StringComparer.Ordinal)
            .ThenBy(k => k.Variant, StringComparer.Ordinal).ThenBy(k => k.Sha256, StringComparer.Ordinal);

    private static IOrderedEnumerable<PostBookmark> OrderedNotes(IEnumerable<PostBookmark> notes) =>
        notes.OrderBy(n => n.Key.Thread.Source, StringComparer.Ordinal).ThenBy(n => n.Key.Thread.Board, StringComparer.Ordinal)
            .ThenBy(n => n.Key.Thread.Thread, StringComparer.Ordinal).ThenBy(n => n.Key.Post, StringComparer.Ordinal);

    [Theory]
    [InlineData("")] [InlineData("en-US")] [InlineData("de-DE")]
    [InlineData("tr-TR")] [InlineData("sv-SE")]
    public void CanonicalOrdinalOrderingAndBothDigestsIgnoreIndependentInputPermutationsAndCulture(string culture)
    {
        ImmutableArray<ResourceId> threads = [
            new("i", "z", "2"), new("I", "z", "2"), new("\u0131", "z", "2"),
            new("\u0130", "z", "2"), new("I", "\u00e4", "2"), new("I", "a", "2"),
            new("I", "a", "10"), new("I", "a", "01")];
        var media = threads.SelectMany(t => new FavoriteAssetKey[] {
            new(t, "i", "z", Hash('a')), new(t, "I", "z", Hash('a')),
            new(t, "I", "a", Hash('b')), new(t, "I", "a", Hash('a')) }).ToImmutableArray();
        var notes = threads.SelectMany(t => new PostBookmark[] {
            new(new(t, "i"), "tail\n "), new(new(t, "I"), ""),
            new(new(t, "10"), "cafe\u0301"), new(new(t, "2"), "caf\u00e9") }).ToImmutableArray();
        var projection = new NativePrivateMetadataProjection(threads, All, threads, media, notes);
        var comparison = Rows(projection);
        NativePrivateApplyRequest baseline = Request(comparison);
        CultureInfo prior = CultureInfo.CurrentCulture, priorUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            for (int seed = 0; seed < 8; seed++)
            {
                NativePrivateApplyRequest actual = Request(Permute(comparison, seed));
                Assert.Equal(OrderedThreads(threads), actual.SelectedIncoming.Threads);
                Assert.Equal(OrderedThreads(threads), actual.SelectedIncoming.ThreadFavorites);
                Assert.Equal(OrderedMedia(media), actual.SelectedIncoming.MediaFavorites);
                Assert.Equal(OrderedNotes(notes), actual.SelectedIncoming.PostBookmarksAndNotes);
                Assert.Equal(OrderedThreads(threads), actual.ThreadFavorites.Select(r => r.Target));
                Assert.Equal(OrderedMedia(media), actual.MediaFavorites.Select(r => r.Target));
                Assert.Equal(OrderedNotes(notes).Select(n => n.Key), actual.PostBookmarksAndNotes.Select(r => r.Target));
                Assert.Equal(OrderedNotes(notes).Select(n => n.Note), actual.PostBookmarksAndNotes.Select(r => r.IncomingNote));
                Assert.Equal(baseline.SelectedPayloadSha256, actual.SelectedPayloadSha256);
                Assert.Equal(baseline.SelectedPayloadLength, actual.SelectedPayloadLength);
                Assert.Equal(baseline.ContentPlanSha256, actual.ContentPlanSha256);
                Assert.Equal(baseline.RequestSha256, actual.RequestSha256);
                AssertPayloadDigest(actual);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = prior;
            CultureInfo.CurrentUICulture = priorUi;
        }
    }

    [Theory]
    [InlineData("")] [InlineData(" ")] [InlineData("  keep whitespace  ")]
    [InlineData("one\n two\n")] [InlineData("caf\u00e9")] [InlineData("cafe\u0301")]
    [InlineData("synthetic \ud83d\udc31 note")]
    public void ExactSelectedIncomingNotesAndAllDispositionsSurviveIncludingConflictAndMissing(string note)
    {
        ImmutableArray<ResourceId> scope = [Thread, OtherThread, Thread with { Thread = "300" }];
        var media = scope.Select(t => new FavoriteAssetKey(t, "synthetic-media", "original", Hash('5'))).ToImmutableArray();
        var bookmarks = new[] { "add", "equal", "conflict", "missing" }
            .Select(p => new PostBookmark(new(Thread, p), note)).ToImmutableArray();
        var source = new NativePrivateMetadataProjection(scope, All, scope, media, bookmarks);
        NativePrivateMetadataDisposition[] marks = [NativePrivateMetadataDisposition.Add,
            NativePrivateMetadataDisposition.Identical, NativePrivateMetadataDisposition.MissingTarget];
        NativePrivateMetadataDisposition[] notes = [NativePrivateMetadataDisposition.Add,
            NativePrivateMetadataDisposition.Identical, NativePrivateMetadataDisposition.Conflict,
            NativePrivateMetadataDisposition.MissingTarget];
        var comparison = new NativePrivateMetadataComparisonResult(All, source,
            scope.Select((t, i) => new NativePrivateThreadFavoriteComparison(t, marks[i])).ToImmutableArray(),
            media.Select((m, i) => new NativePrivateMediaFavoriteComparison(m, marks[i])).ToImmutableArray(),
            bookmarks.Select((n, i) => new NativePrivateBookmarkComparison(n.Key, n.Note, notes[i])).ToImmutableArray());
        NativePrivateApplyRequest request = Request(comparison);
        foreach (var row in comparison.ThreadFavorites)
            Assert.Equal(row.Disposition, Assert.Single(request.ThreadFavorites, r => r.Target == row.Target).Disposition);
        foreach (var row in comparison.MediaFavorites)
            Assert.Equal(row.Disposition, Assert.Single(request.MediaFavorites, r => r.Target == row.Target).Disposition);
        foreach (var row in comparison.PostBookmarksAndNotes)
        {
            var retained = Assert.Single(request.PostBookmarksAndNotes, r => r.Target == row.Target);
            Assert.Equal(row.Disposition, retained.Disposition);
            Assert.Equal(note, retained.IncomingNote);
            Assert.Equal(note, Assert.Single(request.SelectedIncoming.PostBookmarksAndNotes, n => n.Key == row.Target).Note);
        }
        AssertPayloadDigest(request);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void ReceivingSelectionRetainsNoTargetOrUnselectedValuesOrOriginalContainers(int bits)
    {
        NativePrivateMetadataSelection selection = Selection(bits);
        const string incomingMedia = "synthetic-incoming-media-marker";
        const string incomingPost = "synthetic-incoming-post-marker";
        const string incomingNote = "synthetic-incoming-note-marker";
        const string targetMedia = "synthetic-target-only-media-marker";
        const string targetPost = "synthetic-target-only-post-marker";
        const string targetNote = "synthetic-target-only-note-marker";
        const string oldTargetNote = "synthetic-old-target-note-marker";
        var incoming = new NativePrivateMetadataProjection([Thread, OtherThread], All, [Thread],
            [new(Thread, incomingMedia, "original", Hash('5'))], [new(new(Thread, incomingPost), incomingNote)]);
        var target = new NativePrivateMetadataProjection([OtherThread, Thread], All, [OtherThread],
            [new(Thread, targetMedia, "original", Hash('6'))],
            [new(new(Thread, targetPost), targetNote), new(new(Thread, incomingPost), oldTargetNote)]);
        NativeTransferCatalog catalog = Catalog(incomingPost);
        byte[] incomingBefore = NativePrivateMetadataCodec.Serialize(incoming);
        byte[] targetBefore = NativePrivateMetadataCodec.Serialize(target);
        byte[] catalogBefore = NativeTransferCatalogSerializer.Serialize(catalog);
        var comparison = NativePrivateMetadataComparison.Analyze(incoming, catalog, target, selection);
        var request = Request(comparison);
        List<object> graph = Reachable(request);
        foreach (object forbidden in new object[] { incoming, target, catalog, comparison, comparison.SelectedIncoming })
            Assert.DoesNotContain(graph, item => ReferenceEquals(item, forbidden));
        string[] strings = graph.OfType<string>().ToArray();
        foreach (string forbidden in new[] { targetMedia, targetPost, targetNote, oldTargetNote })
            Assert.DoesNotContain(forbidden, strings);
        Assert.Equal(selection, request.Selection);
        Assert.Equal(selection.ThreadFavorites ? 1 : 0, request.ThreadFavorites.Length);
        Assert.Equal(selection.MediaFavorites ? 1 : 0, request.MediaFavorites.Length);
        Assert.Equal(selection.PostBookmarksAndNotes ? 1 : 0, request.PostBookmarksAndNotes.Length);
        if (selection.MediaFavorites) Assert.Contains(incomingMedia, strings);
        else Assert.DoesNotContain(incomingMedia, strings);
        if (selection.PostBookmarksAndNotes)
        {
            Assert.Contains(incomingPost, strings);
            Assert.Contains(incomingNote, strings);
            Assert.Equal(NativePrivateMetadataDisposition.Conflict, Assert.Single(request.PostBookmarksAndNotes).Disposition);
        }
        else
        {
            Assert.DoesNotContain(incomingPost, strings);
            Assert.DoesNotContain(incomingNote, strings);
        }
        Assert.Equal(incomingBefore, NativePrivateMetadataCodec.Serialize(incoming));
        Assert.Equal(targetBefore, NativePrivateMetadataCodec.Serialize(target));
        Assert.Equal(catalogBefore, NativeTransferCatalogSerializer.Serialize(catalog));
    }

    [Fact]
    public void DefaultOffAndEverySelectedEmptyCombinationHaveDistinctIdentityWithoutMarkingOrDeleting()
    {
        HashSet<string> payloads = new(StringComparer.Ordinal), plans = new(StringComparer.Ordinal), requests = new(StringComparer.Ordinal);
        for (int bits = 0; bits < 8; bits++)
        {
            NativePrivateMetadataSelection selection = Selection(bits);
            var source = new NativePrivateMetadataProjection([Thread], selection, [], [], []);
            var request = Request(Rows(source));
            Assert.Equal(selection, request.Selection);
            Assert.Empty(request.ThreadFavorites);
            Assert.Empty(request.MediaFavorites);
            Assert.Empty(request.PostBookmarksAndNotes);
            Assert.Empty(request.SelectedIncoming.ThreadFavorites);
            Assert.Empty(request.SelectedIncoming.MediaFavorites);
            Assert.Empty(request.SelectedIncoming.PostBookmarksAndNotes);
            Assert.Equal(new[] { selection.ThreadFavorites, selection.MediaFavorites, selection.PostBookmarksAndNotes }.Count(v => v),
                request.StoreFences.Length);
            Assert.All(request.StoreFences, fence => Assert.Same(fence.Before, fence.ExpectedAfter));
            Assert.True(payloads.Add(request.SelectedPayloadSha256));
            Assert.True(plans.Add(request.ContentPlanSha256));
            Assert.True(requests.Add(request.RequestSha256));
        }
    }

    [Fact]
    public void OperationIdIsPreservedButExcludedFromBothLogicalDigests()
    {
        var comparison = Rows(Single());
        Guid otherOperation = Guid.Parse("99999999-8888-7777-6666-555555555555");
        var first = Request(comparison);
        var second = Request(comparison, operation: otherOperation);
        Assert.Equal(Operation, first.OperationId);
        Assert.Equal(otherOperation, second.OperationId);
        Assert.NotEqual(first.OperationId, second.OperationId);
        Assert.Equal(first.SelectedPayloadSha256, second.SelectedPayloadSha256);
        Assert.Equal(first.ContentPlanSha256, second.ContentPlanSha256);
        Assert.Equal(first.RequestSha256, second.RequestSha256);
    }

    [Theory]
    [InlineData("packageLength")] [InlineData("packageHash")] [InlineData("catalogHash")]
    [InlineData("privateLength")] [InlineData("privateHash")] [InlineData("profile")]
    [InlineData("targetFence")]
    public void EveryScalarBindingFenceChangesBothPlanAndFinalDigestButNotSelectedPayload(string field)
    {
        var comparison = Rows(Single());
        var baseline = Request(comparison);
        var changedBinding = new NativePrivateApplyRequestBinding(field == "packageLength" ? 1025 : 1024,
            field == "packageHash" ? Hash('9') : Hash('1'), field == "catalogHash" ? Hash('9') : Hash('2'),
            field == "privateLength" ? 257 : 256, field == "privateHash" ? Hash('9') : Hash('3'),
            field == "profile" ? OtherInstance : Profile, field == "targetFence" ? Hash('9') : Hash('4'));
        var changed = Request(comparison, changedBinding);
        Assert.Equal(baseline.SelectedPayloadSha256, changed.SelectedPayloadSha256);
        Assert.NotEqual(baseline.ContentPlanSha256, changed.ContentPlanSha256);
        Assert.NotEqual(baseline.RequestSha256, changed.RequestSha256);
    }

    [Theory]
    [InlineData("schema")] [InlineData("instance")] [InlineData("legacy-instance")]
    [InlineData("revision")] [InlineData("length")] [InlineData("hash")] [InlineData("missing")]
    public void EveryBeforeImageScalarOrExistenceChangesContentPlanAndFinalIdentity(string field)
    {
        var comparison = Rows(Single());
        var baseline = Request(comparison);
        NativePrivateApplyStoreImage before = ChangedImage(field);
        var fences = Fences(All).SetItem(0, new(NativePrivateApplyStoreKind.ThreadFavorites, before, before, Missing()));
        var changed = Request(comparison, fences: fences);
        Assert.Equal(baseline.SelectedPayloadSha256, changed.SelectedPayloadSha256);
        Assert.NotEqual(baseline.ContentPlanSha256, changed.ContentPlanSha256);
        Assert.NotEqual(baseline.RequestSha256, changed.RequestSha256);
    }

    [Theory]
    [InlineData("schema")] [InlineData("instance")] [InlineData("legacy-instance")]
    [InlineData("revision")] [InlineData("length")] [InlineData("hash")] [InlineData("missing")]
    public void FavoriteBackupFenceIsPartOfContentPlanEvenWhenOnlyBackupChanges(string field)
    {
        var comparison = Rows(Single());
        var originalFences = Fences(All);
        var firstFence = originalFences[0];
        originalFences = originalFences.SetItem(0, new(firstFence.Kind, firstFence.Before, firstFence.ExpectedAfter, Image()));
        var baseline = Request(comparison, fences: originalFences);
        var changedFences = originalFences.SetItem(0, new(firstFence.Kind, firstFence.Before, firstFence.ExpectedAfter, ChangedImage(field)));
        var changed = Request(comparison, fences: changedFences);
        Assert.Equal(baseline.SelectedPayloadSha256, changed.SelectedPayloadSha256);
        Assert.NotEqual(baseline.ContentPlanSha256, changed.ContentPlanSha256);
        Assert.NotEqual(baseline.RequestSha256, changed.RequestSha256);
    }

    [Theory]
    [InlineData(0, "schema")] [InlineData(0, "length")] [InlineData(0, "hash")]
    [InlineData(1, "schema")] [InlineData(1, "length")] [InlineData(1, "hash")]
    [InlineData(2, "schema")] [InlineData(2, "length")] [InlineData(2, "hash")]
    public void ExpectedAfterFingerprintChangesOnlyFinalDigestAvoidingAtomicMarkerHashCycle(int store, string field)
    {
        var comparison = Rows(Single()); // Every selected store has at least one Add row.
        var fences = Fences(All);
        var step = fences[store];
        NativePrivateApplyStoreImage after = Image(revision: 5, byteLength: 120, sha256: Hash('b'));
        fences = fences.SetItem(store, new(step.Kind, step.Before, after, step.BeforeBackup));
        var baseline = Request(comparison, fences: fences);
        var changedAfter = Image(schema: field == "schema" ? 3 : 2, revision: 5,
            byteLength: field == "length" ? 121 : 120, sha256: field == "hash" ? Hash('c') : Hash('b'));
        var changed = Request(comparison, fences: fences.SetItem(store,
            new(step.Kind, step.Before, changedAfter, step.BeforeBackup)));
        Assert.Equal(baseline.SelectedPayloadSha256, changed.SelectedPayloadSha256);
        Assert.Equal(baseline.ContentPlanSha256, changed.ContentPlanSha256);
        Assert.NotEqual(baseline.RequestSha256, changed.RequestSha256);
        // A future atomic marker can bind OperationId + ContentPlanSha256 + store kind;
        // the final request hash must never feed back into the after-image's marker.
        Assert.Equal(baseline.OperationId, changed.OperationId);
        Assert.Equal(baseline.StoreFences[store].Kind, changed.StoreFences[store].Kind);
    }

    [Theory]
    [InlineData("", " ")] [InlineData("note", " note")] [InlineData("note", "note ")]
    [InlineData("line\nnext", "line\n next")] [InlineData("caf\u00e9", "cafe\u0301")]
    [InlineData("note", "Note")]
    public void ExactNoteChangesAreBoundWithoutTrimmingCaseFoldingOrUnicodeNormalization(string first, string second)
    {
        var baseline = Request(Rows(Single(first)));
        var changed = Request(Rows(Single(second)));
        Assert.Equal(first, Assert.Single(baseline.SelectedIncoming.PostBookmarksAndNotes).Note);
        Assert.Equal(second, Assert.Single(changed.PostBookmarksAndNotes).IncomingNote);
        Assert.NotEqual(baseline.SelectedPayloadSha256, changed.SelectedPayloadSha256);
        Assert.NotEqual(baseline.ContentPlanSha256, changed.ContentPlanSha256);
        Assert.NotEqual(baseline.RequestSha256, changed.RequestSha256);
    }

    [Theory]
    [InlineData("thread", 1)] [InlineData("thread", 3)]
    [InlineData("media", 1)] [InlineData("media", 3)]
    [InlineData("note", 1)] [InlineData("note", 2)] [InlineData("note", 3)]
    public void ClassificationChangesPlanAndFinalDigestWithoutChangingSelectedValues(string category, int disposition)
    {
        var comparison = Rows(Single());
        var changedComparison = new NativePrivateMetadataComparisonResult(All, comparison.SelectedIncoming,
            category == "thread" ? [new(Thread, (NativePrivateMetadataDisposition)disposition)] : comparison.ThreadFavorites,
            category == "media" ? [new(comparison.MediaFavorites[0].Target, (NativePrivateMetadataDisposition)disposition)] : comparison.MediaFavorites,
            category == "note" ? [new(comparison.PostBookmarksAndNotes[0].Target,
                comparison.PostBookmarksAndNotes[0].IncomingNote, (NativePrivateMetadataDisposition)disposition)] : comparison.PostBookmarksAndNotes);
        var baseline = Request(comparison);
        var changed = Request(changedComparison);
        Assert.Equal(baseline.SelectedPayloadSha256, changed.SelectedPayloadSha256);
        Assert.NotEqual(baseline.ContentPlanSha256, changed.ContentPlanSha256);
        Assert.NotEqual(baseline.RequestSha256, changed.RequestSha256);
    }

    [Theory]
    [InlineData("|")] [InlineData(":")] [InlineData("/")]
    public void DelimiterConcatenationCannotAliasDistinctThreadMediaOrBookmarkComponentKeys(string delimiter)
    {
        ResourceId a = new("source" + delimiter + "board", "tail", "100");
        ResourceId b = new("source", "board" + delimiter + "tail", "100");
        Assert.Equal(string.Join(delimiter, a.Source, a.Board, a.Thread), string.Join(delimiter, b.Source, b.Board, b.Thread));
        AssertDistinct(Request(Rows(Single(thread: a))), Request(Rows(Single(thread: b))));

        var mediaA = Single(media: "media" + delimiter + "variant", variant: "original");
        var mediaB = Single(media: "media", variant: "variant" + delimiter + "original");
        FavoriteAssetKey keyA = mediaA.MediaFavorites[0], keyB = mediaB.MediaFavorites[0];
        Assert.Equal(keyA.MediaIdentifier + delimiter + keyA.Variant, keyB.MediaIdentifier + delimiter + keyB.Variant);
        AssertDistinct(Request(Rows(mediaA)), Request(Rows(mediaB)));

        // Keep the identical scope and select only bookmarks, so only the typed
        // thread/post boundary distinguishes these otherwise concatenation-equal keys.
        ResourceId firstThread = Thread with { Thread = "100" + delimiter + "200" };
        ResourceId secondThread = Thread with { Thread = "100" };
        var chosen = new NativePrivateMetadataSelection(PostBookmarksAndNotes: true);
        var firstNotes = new NativePrivateMetadataProjection([firstThread, secondThread], chosen, [], [],
            [new(new(firstThread, "300"), "same synthetic note")]);
        var secondNotes = new NativePrivateMetadataProjection([firstThread, secondThread], chosen, [], [],
            [new(new(secondThread, "200" + delimiter + "300"), "same synthetic note")]);
        var firstKey = firstNotes.PostBookmarksAndNotes[0].Key;
        var secondKey = secondNotes.PostBookmarksAndNotes[0].Key;
        Assert.Equal(firstKey.Thread.Thread + delimiter + firstKey.Post, secondKey.Thread.Thread + delimiter + secondKey.Post);
        AssertDistinct(Request(Rows(firstNotes)), Request(Rows(secondNotes)));
    }

    [Fact]
    public void RequestConstructionDoesNotMutateInputOrderingRowsBindingOrScalarStoreFences()
    {
        var source = new NativePrivateMetadataProjection([OtherThread, Thread], All, [OtherThread, Thread],
            [new(OtherThread, "z", "original", Hash('6')), new(Thread, "a", "original", Hash('5'))],
            [new(new(OtherThread, "2"), " second\n"), new(new(Thread, "1"), "first")]);
        var comparison = Rows(source);
        byte[] payloadBefore = NativePrivateMetadataCodec.Serialize(source);
        var threadRows = comparison.ThreadFavorites.ToArray();
        var mediaRows = comparison.MediaFavorites.ToArray();
        var noteRows = comparison.PostBookmarksAndNotes.ToArray();
        var binding = Binding();
        var fences = Fences(All);
        string[] fenceBefore = fences.Select(FenceText).ToArray();
        var request = Request(comparison, binding, fences);
        Assert.Equal(payloadBefore, NativePrivateMetadataCodec.Serialize(source));
        Assert.Same(source, comparison.SelectedIncoming);
        Assert.Equal(new[] { OtherThread, Thread }, source.Threads);
        Assert.Equal(threadRows, comparison.ThreadFavorites);
        Assert.Equal(mediaRows, comparison.MediaFavorites);
        Assert.Equal(noteRows, comparison.PostBookmarksAndNotes);
        Assert.Equal(fenceBefore, fences.Select(FenceText));
        Assert.Equal(1024L, binding.PackageLength);
        Assert.Equal(Hash('1'), binding.PackageSha256);
        Assert.Equal(Hash('2'), binding.PublicCatalogSha256);
        Assert.Equal(256L, binding.PrivateLength);
        Assert.Equal(Hash('3'), binding.PrivateSha256);
        Assert.Equal(Profile, binding.ProfileIncarnation);
        Assert.Equal(Hash('4'), binding.TargetFenceSha256);
        Assert.Equal(new[] { Thread, OtherThread }, request.SelectedIncoming.Threads);
        Assert.NotSame(source, request.SelectedIncoming);
        AssertPayloadDigest(request);
    }

    private static NativePrivateMetadataSelection Selection(int bits) =>
        new((bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0);

    private static NativePrivateApplyStoreImage ChangedImage(string field) => field switch
    {
        "schema" => Image(schema: 3),
        "instance" => Image(instance: OtherInstance),
        "legacy-instance" => new(true, 2, null, 4, 100, Hash('a')),
        "revision" => Image(revision: 5),
        "length" => Image(byteLength: 101),
        "hash" => Image(sha256: Hash('b')),
        "missing" => Missing(),
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

    private static NativeTransferCatalog Catalog(string post)
    {
        var captured = new CapturedPost(post, 1, PostFieldPresence.All, When, "", null,
            "synthetic public post", false, []);
        return new(When, [new(Thread, null, "synthetic public thread", ThreadSourceState.NotFound,
            When, When, 1, true, [new(captured, [])])]);
    }

    private static void AssertDistinct(NativePrivateApplyRequest first, NativePrivateApplyRequest second)
    {
        Assert.NotEqual(first.SelectedPayloadSha256, second.SelectedPayloadSha256);
        Assert.NotEqual(first.ContentPlanSha256, second.ContentPlanSha256);
        Assert.NotEqual(first.RequestSha256, second.RequestSha256);
    }

    private static void AssertPayloadDigest(NativePrivateApplyRequest request)
    {
        byte[] payload = NativePrivateMetadataCodec.Serialize(request.SelectedIncoming);
        Assert.Equal(payload.Length, request.SelectedPayloadLength);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(), request.SelectedPayloadSha256);
        Assert.Matches("^[0-9a-f]{64}$", request.ContentPlanSha256);
        Assert.Matches("^[0-9a-f]{64}$", request.RequestSha256);
    }

    private static string FenceText(NativePrivateApplyStoreFence fence)
    {
        static string ImageText(NativePrivateApplyStoreImage image) => FormattableString.Invariant(
            $"{image.Exists};{image.Schema};{image.Instance:D};{image.Revision};{image.ByteLength};{image.Sha256}");
        return $"{fence.Kind}:{ImageText(fence.Before)}:{ImageText(fence.ExpectedAfter)}:{(fence.BeforeBackup is null ? "no-backup" : ImageText(fence.BeforeBackup))}";
    }

    // Inspect only instance fields and enumerable elements; never call product
    // getters, ToString, providers or stores. This detects hidden retained DTOs,
    // not merely a selected-only public facade hiding an unfiltered input field.
    private static List<object> Reachable(object root)
    {
        List<object> result = [];
        HashSet<object> seen = new(ReferenceEqualityComparer.Instance);
        Stack<object> pending = new();
        pending.Push(root);
        while (pending.TryPop(out object? value))
        {
            if (!seen.Add(value)) continue;
            result.Add(value);
            if (value is string) continue;
            if (value is IEnumerable enumerable)
            {
                foreach (object? child in enumerable)
                    if (child is not null) pending.Push(child);
                continue;
            }
            if (value.GetType().Assembly != typeof(NativePrivateApplyRequest).Assembly) continue;
            for (Type? type = value.GetType(); type is not null; type = type.BaseType)
                foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (field.GetValue(value) is { } child) pending.Push(child);
        }
        return result;
    }
}

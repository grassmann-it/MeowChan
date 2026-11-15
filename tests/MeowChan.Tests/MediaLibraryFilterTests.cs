using MeowChan.Archive;
using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Ratings;
using MeowChan.Storage;
using MeowChan.Tags;
using MeowChan.Transfer;

namespace MeowChan.Tests;

public sealed class MediaLibraryFilterTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid Rpt = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Cute = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid Bad = Guid.Parse("00000000-0000-0000-0000-0000000000a3");

    private static string Hash(char c) => new(c, 64);

    private static MediaLibraryEntry Entry(string thread, string post, char hash, string name = "f.png",
        SavedMediaKind kind = SavedMediaKind.Image, string source = "s", string board = "b", bool missing = false,
        bool unassigned = false) =>
        new(new ResourceId(source, board, thread), "T" + thread, 1, Day, Day,
            unassigned ? null : post, unassigned ? null : long.Parse(post), null, unassigned ? null : 0,
            unassigned ? null : "att-v1:" + Hash(hash),
            missing ? null : "/dir/" + thread, missing ? null : "asset-" + post, missing ? null : Hash(hash), missing ? null : 1,
            "m" + post, "original", name, kind, missing ? MediaLibraryPresence.Missing : MediaLibraryPresence.Present, unassigned, false);

    private static PrivateTagDefinition Tag(Guid id, string name)
    {
        Assert.True(PrivateTagName.TryCreate(name, out PrivateTagName? tagName, out _));
        return new PrivateTagDefinition(new TagId(id), tagName!);
    }

    private static PrivateMediaTagAssignment MediaTag(Guid id, MediaLibraryEntry entry) =>
        new(new TagId(id), MediaLibraryFilterEngine.TagTarget(entry)!);

    private static MediaLibraryUserData Data(IEnumerable<PrivateThreadTagAssignment>? threads = null,
        IEnumerable<PrivateMediaTagAssignment>? media = null, IEnumerable<MediaLibraryEntry>? favorites = null,
        IEnumerable<(MediaLibraryEntry Entry, int Value)>? ratings = null) =>
        new(new PrivateTagStoreSnapshot(1, "fp", [Tag(Rpt, "rpt"), Tag(Cute, "cute"), Tag(Bad, "bad")], threads ?? [], media ?? []),
            (favorites ?? []).Select(entry => new FavoriteEntry(MediaLibraryFilterEngine.AssetKey(entry)!, 1, entry.Name)).ToArray(),
            (ratings ?? []).Select(pair => new MediaRatingEntry(MediaLibraryFilterEngine.AssetKey(pair.Entry)!, pair.Value)).ToArray());

    private static string[] Posts(IEnumerable<MediaLibraryEntry> entries) =>
        entries.Select(entry => entry.Thread.Thread + ":" + (entry.PostId ?? "-")).ToArray();

    [Fact]
    public void ThreadTagsReachEveryFileOfTheThreadAndMediaTagsOnlyTheirFile()
    {
        MediaLibraryEntry a1 = Entry("a", "1", 'a'), a2 = Entry("a", "2", 'b'), b1 = Entry("b", "1", 'c');
        MediaLibraryUserData data = Data(threads: [new(new TagId(Rpt), a1.Thread)], media: [MediaTag(Cute, b1)]);
        MediaLibraryEntry[] all = [a1, a2, b1];

        Assert.Equal(new[] { "a:1", "a:2" }, Posts(MediaLibraryFilterEngine.Apply(all, new(tags: new ArchiveTagFilter([Rpt], null)), data)));
        Assert.Equal(new[] { "b:1" }, Posts(MediaLibraryFilterEngine.Apply(all, new(tags: new ArchiveTagFilter([Cute], null)), data)));
        Assert.Equal(new[] { "b:1" }, Posts(MediaLibraryFilterEngine.Apply(all, new(tags: new ArchiveTagFilter(null, [Rpt])), data)));
    }

    [Fact]
    public void IncludeAllAnyAndExcludeCombine()
    {
        MediaLibraryEntry both = Entry("a", "1", 'a'), onlyRpt = Entry("a", "2", 'b'), onlyCute = Entry("b", "1", 'c'),
            cuteAndBad = Entry("b", "2", 'd');
        MediaLibraryUserData data = Data(threads: [new(new TagId(Rpt), both.Thread)],
            media: [MediaTag(Cute, both), MediaTag(Cute, onlyCute), MediaTag(Cute, cuteAndBad), MediaTag(Bad, cuteAndBad)]);
        MediaLibraryEntry[] all = [both, onlyRpt, onlyCute, cuteAndBad];

        Assert.Equal(new[] { "a:1" }, Posts(MediaLibraryFilterEngine.Apply(all, new(tags: new ArchiveTagFilter([Rpt, Cute], null)), data)));
        Assert.Equal(new[] { "a:1", "a:2", "b:1" }, Posts(MediaLibraryFilterEngine.Apply(all,
            new(tags: new ArchiveTagFilter([Rpt, Cute], [Bad], ArchiveTagMatch.Any)), data)));
    }

    [Fact]
    public void DeletedTagOrUnavailableStoresRefuseInsteadOfWidening()
    {
        MediaLibraryEntry entry = Entry("a", "1", 'a');
        Guid gone = Guid.NewGuid();
        var unknown = Assert.Throws<MediaLibraryFilterException>(() =>
            MediaLibraryFilterEngine.Apply([entry], new(tags: new ArchiveTagFilter(null, [gone])), Data()));
        Assert.Equal(MediaLibraryFilterError.UnknownTag, unknown.Error);
        Assert.Equal(MediaLibraryFilterError.TagsUnavailable, Assert.Throws<MediaLibraryFilterException>(() =>
            MediaLibraryFilterEngine.Apply([entry], new(tags: new ArchiveTagFilter([Rpt], null)), MediaLibraryUserData.Unavailable)).Error);
        Assert.Equal(MediaLibraryFilterError.FavoritesUnavailable, Assert.Throws<MediaLibraryFilterException>(() =>
            MediaLibraryFilterEngine.Apply([entry], new(favoritesOnly: true), MediaLibraryUserData.Unavailable)).Error);
        Assert.Equal(MediaLibraryFilterError.RatingsUnavailable, Assert.Throws<MediaLibraryFilterException>(() =>
            MediaLibraryFilterEngine.Apply([entry], new(minimumRating: 3), MediaLibraryUserData.Unavailable)).Error);
        // Filters that need no store still work while a store is unavailable.
        Assert.Single(MediaLibraryFilterEngine.Apply([entry], new(nameContains: "F."), MediaLibraryUserData.Unavailable));
    }

    [Fact]
    public void FavoritesAndMinimumRatingUseTheExactSavedBytes()
    {
        MediaLibraryEntry fav = Entry("a", "1", 'a'), rated = Entry("a", "2", 'b'), low = Entry("a", "3", 'c'),
            missing = Entry("a", "4", 'd', missing: true);
        // Same post, other bytes: a favorite of different bytes does not count.
        MediaLibraryEntry changed = fav with { Sha256 = Hash('e'), AssetId = "asset-new" };
        MediaLibraryUserData data = Data(favorites: [fav], ratings: [(rated, 4), (low, 2), (fav, 5)]);
        MediaLibraryEntry[] all = [fav, rated, low, missing, changed];

        Assert.Equal(new[] { "a:1" }, Posts(MediaLibraryFilterEngine.Apply(all, new(favoritesOnly: true), data)));
        Assert.Equal(new[] { "a:1", "a:2" }, Posts(MediaLibraryFilterEngine.Apply(all, new(minimumRating: 4), data)));
        Assert.Equal(new[] { "a:1" }, Posts(MediaLibraryFilterEngine.Apply(all, new(favoritesOnly: true, minimumRating: 5), data)));
    }

    [Fact]
    public void TypeSourceBoardNameAndThreadFilters()
    {
        MediaLibraryEntry png = Entry("a", "1", 'a', name: "Cat.PNG"), gif = Entry("a", "2", 'b', name: "dance.GIF"),
            video = Entry("b", "1", 'c', name: "clip.webm", kind: SavedMediaKind.Video, board: "wsg"),
            pdf = Entry("c", "1", 'd', name: "paper.pdf", kind: SavedMediaKind.Other, source: "t"),
            unknown = Entry("c", "2", 'e', name: "blob", kind: SavedMediaKind.Unknown, source: "t");
        MediaLibraryEntry[] all = [png, gif, video, pdf, unknown];
        MediaLibraryUserData data = MediaLibraryUserData.Unavailable;

        Assert.Equal(new[] { "a:1" }, Posts(MediaLibraryFilterEngine.Apply(all, new(kind: MediaLibraryKindFilter.Image), data)));
        Assert.Equal(new[] { "a:2" }, Posts(MediaLibraryFilterEngine.Apply(all, new(kind: MediaLibraryKindFilter.Gif), data)));
        Assert.Equal(new[] { "b:1" }, Posts(MediaLibraryFilterEngine.Apply(all, new(kind: MediaLibraryKindFilter.Video), data)));
        Assert.Equal(new[] { "c:1", "c:2" }, Posts(MediaLibraryFilterEngine.Apply(all, new(kind: MediaLibraryKindFilter.Other), data)));
        Assert.Equal(new[] { "c:1", "c:2" }, Posts(MediaLibraryFilterEngine.Apply(all, new(source: "t"), data)));
        Assert.Equal(new[] { "b:1" }, Posts(MediaLibraryFilterEngine.Apply(all, new(source: "s", board: "wsg"), data)));
        Assert.Equal(new[] { "a:1" }, Posts(MediaLibraryFilterEngine.Apply(all, new(nameContains: "  cat "), data)));
        Assert.Equal(new[] { "a:1", "a:2" }, Posts(MediaLibraryFilterEngine.Apply(all, new(thread: png.Thread), data)));
        Assert.Throws<ArgumentException>(() => new MediaLibraryFilter(board: "b"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaLibraryFilter(minimumRating: 6));
        Assert.True(new MediaLibraryFilter(nameContains: "   ").IsEmpty);
    }

    [Fact]
    public void ThreadSetKeepsOnlyTheMediaOfItsThreadsAndAnEmptySetShowsNothingWhileNoSetShowsAll()
    {
        MediaLibraryEntry a1 = Entry("a", "1", 'a'), a2 = Entry("a", "2", 'b'), b1 = Entry("b", "1", 'c'), c1 = Entry("c", "1", 'd', source: "t"),
            d1 = Entry("d", "1", 'e', board: "x");
        MediaLibraryEntry[] all = [a1, a2, b1, c1, d1];
        MediaLibraryUserData data = MediaLibraryUserData.Unavailable;

        Assert.Equal(new[] { "a:1", "a:2", "c:1" }, Posts(MediaLibraryFilterEngine.Apply(all, new(threads: [a1.Thread, c1.Thread, new ResourceId("s", "b", "gone")]), data)));
        // The set is exact: the same thread number on another board or source is another thread.
        Assert.Empty(MediaLibraryFilterEngine.Apply(all, new(threads: [new ResourceId("s", "x", "a"), new ResourceId("t", "b", "a")]), data));
        // An empty set is a restriction that nothing passes; no set at all (null) is no restriction.
        MediaLibraryFilter none = new(threads: []);
        Assert.False(none.IsEmpty);
        Assert.Empty(MediaLibraryFilterEngine.Apply(all, none, data));
        Assert.Equal(5, MediaLibraryFilterEngine.Apply(all, new(threads: null), data).Count);
        Assert.True(new MediaLibraryFilter(threads: null).IsEmpty);
        Assert.NotEqual(none, MediaLibraryFilter.None);
        // It narrows together with the other parts and the single thread.
        Assert.Equal(new[] { "a:1", "a:2" }, Posts(MediaLibraryFilterEngine.Apply(all, new(threads: [a1.Thread, b1.Thread], thread: a1.Thread), data)));
        Assert.Empty(MediaLibraryFilterEngine.Apply(all, new(threads: [a1.Thread], thread: b1.Thread), data));
        Assert.Equal(new[] { "c:1" }, Posts(MediaLibraryFilterEngine.Apply(all, new(threads: [a1.Thread, c1.Thread, d1.Thread], source: "t"), data)));
        Assert.Equal(new[] { "b:1" }, Posts(MediaLibraryFilterEngine.Apply(all, new(threads: [a1.Thread, b1.Thread], nameContains: "Tb"), data)));
    }

    [Fact]
    public void ThreadSetsCompareByContentAndStayAFastLookupForTensOfThousandsOfThreads()
    {
        ResourceId[] many = [.. Enumerable.Range(0, 50_000).Select(index => new ResourceId("s", "b", index.ToString()))];
        MediaLibraryFilter first = new(threads: many), reversed = new(threads: many.Reverse());
        Assert.Equal(first, reversed);
        Assert.Equal(first.GetHashCode(), reversed.GetHashCode());
        Assert.NotEqual(first, new MediaLibraryFilter(threads: many.Skip(1)));
        Assert.NotEqual(first, MediaLibraryFilter.None);
        MediaLibraryEntry[] entries = [.. Enumerable.Range(0, 20_000).Select(index => Entry((index * 5).ToString(), "1", 'a'))];
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<MediaLibraryEntry> passing = MediaLibraryFilterEngine.Apply(entries, first, MediaLibraryUserData.Unavailable);
        watch.Stop();
        Assert.Equal(Enumerable.Range(0, 20_000).Count(index => index * 5 < 50_000), passing.Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "A set lookup per entry took " + watch.Elapsed);
    }

    [Fact]
    public void KindsAreASetWhereEmptyMeansEveryTypeAndWithoutVideoIsTheOtherTypes()
    {
        MediaLibraryEntry png = Entry("a", "1", 'a', name: "Cat.PNG"), gif = Entry("a", "2", 'b', name: "dance.GIF"),
            video = Entry("b", "1", 'c', name: "clip.webm", kind: SavedMediaKind.Video), pdf = Entry("c", "1", 'd', name: "paper.pdf", kind: SavedMediaKind.Other);
        MediaLibraryEntry[] all = [png, gif, video, pdf];
        MediaLibraryUserData data = MediaLibraryUserData.Unavailable;

        // Empty, null, Any and all four types restrict nothing, and are the empty filter.
        foreach (MediaLibraryFilter every in new MediaLibraryFilter[] { new(kinds: []), new(kinds: null), new(kind: MediaLibraryKindFilter.Any),
            new(kinds: [MediaLibraryKindFilter.Image, MediaLibraryKindFilter.Gif, MediaLibraryKindFilter.Video, MediaLibraryKindFilter.Other]),
            new(kinds: [MediaLibraryKindFilter.Video, MediaLibraryKindFilter.Any]) })
        {
            Assert.True(every.IsEmpty);
            Assert.Empty(every.Kinds);
            Assert.Equal(4, MediaLibraryFilterEngine.Apply(all, every, data).Count);
            Assert.Equal(MediaLibraryFilter.None, every);
        }
        Assert.Equal(new[] { "a:1", "a:2" }, Posts(MediaLibraryFilterEngine.Apply(all, new(kinds: [MediaLibraryKindFilter.Image, MediaLibraryKindFilter.Gif]), data)));
        Assert.Equal(new[] { "a:1", "b:1" }, Posts(MediaLibraryFilterEngine.Apply(all, new(kinds: [MediaLibraryKindFilter.Image, MediaLibraryKindFilter.Video]), data)));
        // "Without type video".
        MediaLibraryFilter withoutVideo = new(kinds: MediaLibraryFilter.AllKindsExcept(MediaLibraryKindFilter.Video));
        Assert.False(withoutVideo.IsEmpty);
        Assert.Equal(new[] { "a:1", "a:2", "c:1" }, Posts(MediaLibraryFilterEngine.Apply(all, withoutVideo, data)));
        Assert.Equal(new[] { "c:1" }, Posts(MediaLibraryFilterEngine.Apply(all, new(kinds: MediaLibraryFilter.AllKindsExcept(MediaLibraryKindFilter.Image, MediaLibraryKindFilter.Gif, MediaLibraryKindFilter.Video)), data)));
        // The single kind and the set narrow together; types that exclude each other are refused (empty would mean "all").
        Assert.Equal(new[] { "a:2" }, Posts(MediaLibraryFilterEngine.Apply(all, new(kind: MediaLibraryKindFilter.Gif, kinds: [MediaLibraryKindFilter.Gif, MediaLibraryKindFilter.Video]), data)));
        Assert.Throws<ArgumentException>(() => new MediaLibraryFilter(kind: MediaLibraryKindFilter.Gif, kinds: [MediaLibraryKindFilter.Video]));
        Assert.Throws<ArgumentException>(() => MediaLibraryFilter.AllKindsExcept(MediaLibraryKindFilter.Image, MediaLibraryKindFilter.Gif,
            MediaLibraryKindFilter.Video, MediaLibraryKindFilter.Other));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaLibraryFilter(kinds: [(MediaLibraryKindFilter)9]));
    }

    [Fact]
    public void ASoundFilterNeedsVideosToStayInTheTypeSet()
    {
        Assert.False(new MediaLibraryFilter(sound: MediaLibrarySoundFilter.WithSound, kinds: [MediaLibraryKindFilter.Video]).IsEmpty);
        Assert.False(new MediaLibraryFilter(sound: MediaLibrarySoundFilter.WithSound, kinds: [MediaLibraryKindFilter.Image, MediaLibraryKindFilter.Video]).IsEmpty);
        Assert.Throws<ArgumentException>(() => new MediaLibraryFilter(sound: MediaLibrarySoundFilter.WithSound,
            kinds: MediaLibraryFilter.AllKindsExcept(MediaLibraryKindFilter.Video)));
        Assert.Throws<ArgumentException>(() => new MediaLibraryFilter(sound: MediaLibrarySoundFilter.WithoutSound, kinds: [MediaLibraryKindFilter.Gif]));
    }

    [Fact]
    public void ASourceOrABoardCanBeExcludedAlsoNextToAnIncludedBoard()
    {
        MediaLibraryEntry a = Entry("1", "1", 'a', source: "4chan", board: "g"), b = Entry("2", "1", 'b', source: "4chan", board: "wsg"),
            c = Entry("3", "1", 'c', source: "danbooru", board: "posts"), d = Entry("4", "1", 'd', source: "other", board: "g");
        MediaLibraryEntry[] all = [a, b, c, d];
        MediaLibraryUserData data = MediaLibraryUserData.Unavailable;

        Assert.Equal(new[] { "1:1", "2:1", "4:1" }, Posts(MediaLibraryFilterEngine.Apply(all, new(excludeSources: ["danbooru"]), data)));
        // Excluding a board is per source: /g/ of 4chan goes, /g/ of another source stays.
        Assert.Equal(new[] { "2:1", "3:1", "4:1" }, Posts(MediaLibraryFilterEngine.Apply(all, new(excludeBoards: [new MediaLibraryBoardKey("4chan", "g")]), data)));
        // A source excluded and a board of another source included: both narrow.
        Assert.Equal(new[] { "4:1" }, Posts(MediaLibraryFilterEngine.Apply(all, new(source: "other", board: "g", excludeSources: ["4chan"]), data)));
        Assert.Equal(new[] { "2:1" }, Posts(MediaLibraryFilterEngine.Apply(all, new(source: "4chan", excludeBoards: [new MediaLibraryBoardKey("4chan", "g")]), data)));
        // The same source included and excluded shows nothing (every part narrows).
        Assert.Empty(MediaLibraryFilterEngine.Apply(all, new(source: "4chan", excludeSources: ["4chan"]), data));
        Assert.Equal(new[] { "1:1", "2:1", "4:1" }, Posts(MediaLibraryFilterEngine.Apply(all, new(excludeSources: ["danbooru", "", null!], excludeBoards: [new MediaLibraryBoardKey("", "x")]), data)));
        // Source names compare exactly (ordinal), not by case.
        Assert.Equal(4, MediaLibraryFilterEngine.Apply(all, new(excludeSources: ["DANBOORU"]), data).Count);
        Assert.False(new MediaLibraryFilter(excludeSources: ["s"]).IsEmpty);
        Assert.False(new MediaLibraryFilter(excludeBoards: [new MediaLibraryBoardKey("s", "b")]).IsEmpty);
        Assert.True(new MediaLibraryFilter(excludeSources: [], excludeBoards: []).IsEmpty);
    }

    [Fact]
    public void FiltersCompareByTheContentOfTheirSets()
    {
        MediaLibraryFilter first = new(kinds: [MediaLibraryKindFilter.Image, MediaLibraryKindFilter.Gif], excludeSources: ["a", "b"],
            excludeBoards: [new MediaLibraryBoardKey("s", "x"), new MediaLibraryBoardKey("s", "y")]);
        MediaLibraryFilter same = new(kinds: [MediaLibraryKindFilter.Gif, MediaLibraryKindFilter.Image, MediaLibraryKindFilter.Gif], excludeSources: ["b", "a"],
            excludeBoards: [new MediaLibraryBoardKey("s", "y"), new MediaLibraryBoardKey("s", "x")]);
        Assert.Equal(first, same);
        Assert.Equal(first.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(first, new MediaLibraryFilter(kinds: [MediaLibraryKindFilter.Image], excludeSources: ["a", "b"],
            excludeBoards: [new MediaLibraryBoardKey("s", "x"), new MediaLibraryBoardKey("s", "y")]));
        Assert.NotEqual(first, new MediaLibraryFilter(kinds: first.Kinds, excludeSources: ["a"], excludeBoards: first.ExcludeBoards));
        Assert.NotEqual(first, new MediaLibraryFilter(kinds: first.Kinds, excludeSources: first.ExcludeSources));
        // The old single-kind call and the set say the same.
        Assert.Equal(new MediaLibraryFilter(kind: MediaLibraryKindFilter.Video), new MediaLibraryFilter(kinds: [MediaLibraryKindFilter.Video]));
    }

    [Fact]
    public void MissingAndUnboundOccurrencesKeepThreadTagsButCarryNoMediaData()
    {
        MediaLibraryEntry missing = Entry("a", "1", 'a', missing: true), loose = Entry("a", "9", 'b', unassigned: true);
        Assert.Null(MediaLibraryFilterEngine.TagTarget(missing));
        Assert.Null(MediaLibraryFilterEngine.TagTarget(loose));
        Assert.Null(MediaLibraryFilterEngine.AssetKey(missing));
        Assert.NotNull(MediaLibraryFilterEngine.AssetKey(loose));
        MediaLibraryUserData data = Data(threads: [new(new TagId(Rpt), missing.Thread)]);

        Assert.Equal(2, MediaLibraryFilterEngine.Apply([missing, loose], new(tags: new ArchiveTagFilter([Rpt], null)), data).Count);
    }

    [Fact]
    public void FilterTargetMatchesTheGalleryTagTarget()
    {
        MediaLibraryEntry entry = Entry("a", "1", 'a');
        PrivateMediaTagTarget target = MediaLibraryFilterEngine.TagTarget(entry)!;
        Assert.True(PrivateMediaTagTarget.TryCreate(new AttachmentRevisionReference(entry.Thread, "1", 0, "att-v1:" + Hash('a')),
            "m1", "original", TransferMediaRole.Original, Hash('a'), out PrivateMediaTagTarget? expected, out _));
        Assert.Equal(expected, target);
    }

    [Fact]
    public void EmptyFilterReturnsTheInputAndSameSourcesDetectsChanges()
    {
        MediaLibraryEntry[] all = [Entry("a", "1", 'a')];
        Assert.Same(all, MediaLibraryFilterEngine.Apply(all, MediaLibraryFilter.None, MediaLibraryUserData.Unavailable));
        MediaLibraryUserData first = Data();
        Assert.True(first.SameSourcesAs(new MediaLibraryUserData(first.TagSnapshot, first.FavoriteSource, first.RatingSource)));
        Assert.False(first.SameSourcesAs(Data()));
    }

    // N1: the identity the Booru mode's tag counts are keyed on follows the tags only; a favorite or a rating must not look like new tag data.
    [Fact]
    public void TagSourceKeyFollowsTheTagSnapshotAndNotFavoritesOrRatings()
    {
        MediaLibraryEntry entry = Entry("a", "1", 'a');
        MediaLibraryUserData first = Data();
        MediaLibraryUserData starred = new(first.TagSnapshot, [new FavoriteEntry(MediaLibraryFilterEngine.AssetKey(entry)!, 1, entry.Name)],
            [new MediaRatingEntry(MediaLibraryFilterEngine.AssetKey(entry)!, 4)]);
        Assert.False(first.SameSourcesAs(starred));
        Assert.Same(first.TagSourceKey, starred.TagSourceKey);
        Assert.NotSame(first.TagSourceKey, Data().TagSourceKey);
        Assert.Null(MediaLibraryUserData.Unavailable.TagSourceKey);
    }

    // T1: "without media tags" is the start of tagging. It reads the media tags the tag filter reads, only for files that can carry
    // them, and combines with every other part of the filter. A thread tag does not count: the file still waits for its own tag,
    // while the tag filter keeps finding it through that thread tag.
    [Fact]
    public void WithoutMediaTagsKeepsTaggableFilesWithoutMediaTagsAlsoInTaggedThreadsAndCombines()
    {
        MediaLibraryEntry threadTagged = Entry("a", "1", 'a'), mediaTagged = Entry("b", "1", 'b'), plain = Entry("b", "2", 'c', name: "plain.png"),
            plainFavorite = Entry("b", "3", 'd'), missing = Entry("b", "4", 'e', missing: true),
            // An occurrence without a bound version and without a manifest slot has no tag subject (as downloaded provider media):
            // nothing can be tagged there, so it is never offered as untagged.
            noSubject = Entry("b", "5", 'f', unassigned: true);
        MediaLibraryUserData data = Data(threads: [new(new TagId(Rpt), threadTagged.Thread)], media: [MediaTag(Cute, mediaTagged)], favorites: [plainFavorite]);
        MediaLibraryEntry[] all = [threadTagged, mediaTagged, plain, plainFavorite, missing, noSubject];
        Assert.Null(MediaTagSubjects.Of(noSubject));

        Assert.False(new MediaLibraryFilter(withoutMediaTags: true).IsEmpty);
        Assert.Equal(new[] { "a:1", "b:2", "b:3" }, Posts(MediaLibraryFilterEngine.Apply(all, new(withoutMediaTags: true), data)));
        // Both at once: the file in the tagged thread is untagged as a medium and still matches its thread's tag.
        Assert.Equal(new[] { "a:1" }, Posts(MediaLibraryFilterEngine.Apply(all, new(tags: new ArchiveTagFilter([Rpt], null)), data)));
        Assert.Equal(new[] { "a:1" }, Posts(MediaLibraryFilterEngine.Apply(all, new(tags: new ArchiveTagFilter([Rpt], null), withoutMediaTags: true), data)));
        Assert.Equal(new[] { "b:2", "b:3" }, Posts(MediaLibraryFilterEngine.Apply(all, new(tags: new ArchiveTagFilter(null, [Rpt]), withoutMediaTags: true), data)));
        Assert.Equal(new[] { "b:3" }, Posts(MediaLibraryFilterEngine.Apply(all, new(withoutMediaTags: true, favoritesOnly: true), data)));
        Assert.Equal(new[] { "b:2" }, Posts(MediaLibraryFilterEngine.Apply(all, new(withoutMediaTags: true, nameContains: "plain"), data)));
        // An included media tag and "without media tags" contradict each other: nothing, never the wider list.
        Assert.Empty(MediaLibraryFilterEngine.Apply(all, new(tags: new ArchiveTagFilter([Cute], null), withoutMediaTags: true), data));
        Assert.All(all, entry => Assert.Equal(entry == threadTagged || entry == plain || entry == plainFavorite, data.LacksMediaTags(entry)));
        // Unavailable tags are unknown, never "no tags".
        Assert.Equal(MediaLibraryFilterError.TagsUnavailable, Assert.Throws<MediaLibraryFilterException>(() =>
            MediaLibraryFilterEngine.Apply(all, new(withoutMediaTags: true), new MediaLibraryUserData(null, [], []))).Error);
        Assert.Null(MediaLibraryUntagged.Of(all, MediaLibraryUserData.Unavailable));
    }

    // Review 15.11.2026: free text is not limited to the file name. "/kot/" is a pseudo-tag people write into thread titles: it is found
    // literally (slashes included) in the title; every word may match a different field, each word must match one of them.
    [Fact]
    public void FreeTextMatchesFileNameThreadTitleBoardAndSource()
    {
        MediaLibraryEntry kot = Entry("a", "1", 'a', name: "img.png") with { ThreadTitle = "/kot/ general: cats" },
            kotVideo = Entry("a", "2", 'b', name: "clip.webm", kind: SavedMediaKind.Video) with { ThreadTitle = "/kot/ general: cats" },
            wsg = Entry("b", "1", 'c', name: "dance.gif", board: "wsg") with { ThreadTitle = "Dance thread" },
            other = Entry("c", "1", 'd', name: "kotlin.png", source: "8kun") with { ThreadTitle = "Programming" },
            slashless = Entry("d", "1", 'e', name: "x.png") with { ThreadTitle = "kot without slashes" };
        MediaLibraryEntry[] all = [kot, kotVideo, wsg, other, slashless];
        MediaLibraryUserData data = MediaLibraryUserData.Unavailable;
        string[] Find(string text, MediaLibraryKindFilter kind = MediaLibraryKindFilter.Any) =>
            Posts(MediaLibraryFilterEngine.Apply(all, new(nameContains: text, kind: kind), data));

        Assert.Equal(new[] { "a:1", "a:2" }, Find("/kot/")); // the title, literally: "kotlin.png" and "kot without slashes" do not match
        Assert.Equal(new[] { "a:1", "a:2" }, Find("/KOT/")); // case is ignored
        Assert.Equal(new[] { "a:2" }, Find("/kot/", MediaLibraryKindFilter.Video)); // "/kot/ type:video"
        Assert.Equal(new[] { "a:1", "a:2", "c:1", "d:1" }, Find("kot")); // title or file name
        Assert.Equal(new[] { "b:1" }, Find("wsg")); // the board
        Assert.Equal(new[] { "c:1" }, Find("8kun")); // the source
        Assert.Equal(new[] { "a:2" }, Find("/kot/ clip")); // words AND, each in any field (title + file name)
        Assert.Equal(new[] { "b:1" }, Find("wsg dance.gif"));
        Assert.Empty(Find("/kot/ dance"));
        Assert.Equal(new[] { "a:1", "a:2" }, Find("\"general: cats\"")); // a quoted phrase is one word, spaces included
        Assert.Empty(Find("\"cats general\""));
        Assert.Equal(new[] { "a:1", "a:2" }, Find("cats general")); // unquoted, the order of the words does not matter
        Assert.Equal(new[] { "/kot/", "general: cats", "a\"b" }, new MediaLibraryFilter(nameContains: "/kot/ \"general: cats\" \"a\\\"b\" /KOT/").TextWords);
    }

    // Review 15.11.2026, finding 10: "" is a text of two characters but of no word, so the filter is not active.
    [Fact]
    public void AnEmptyPhraseOrWhiteSpaceIsNoTextAndEqualsNoFilter()
    {
        foreach (string text in new[] { "", " ", "\"\"", "\"\" \"\"", "  \"\"  " })
        {
            MediaLibraryFilter filter = new(nameContains: text);
            Assert.True(filter.IsEmpty, text);
            Assert.Null(filter.NameContains);
            Assert.Empty(filter.TextWords);
            Assert.Equal(MediaLibraryFilter.None, filter);
            Assert.False(MediaLibraryFilter.HasText(text));
        }
        Assert.True(MediaLibraryFilter.HasText(" cat "));
        Assert.True(MediaLibraryFilter.HasText("\" \"")); // a phrase of white space is a word
        Assert.False(new MediaLibraryFilter(nameContains: "cat").IsEmpty);
        MediaLibraryEntry[] all = [Entry("a", "1", 'a'), Entry("a", "2", 'b')];
        Assert.Equal(2, MediaLibraryFilterEngine.Apply(all, new(nameContains: "\"\""), MediaLibraryUserData.Unavailable).Count);
    }

    // Review 15.11.2026, finding 11: board and source name themselves as a whole; "chan" is no longer every 4chan file.
    [Fact]
    public void BoardAndSourceMatchAsWholeValuesWhileNameAndTitleStaySubstrings()
    {
        MediaLibraryEntry g = Entry("a", "1", 'a', name: "a.webm", source: "4chan", board: "g") with { ThreadTitle = "General" },
            b = Entry("b", "1", 'b', name: "b.webm", source: "4chan", board: "b") with { ThreadTitle = "Random" },
            kot = Entry("c", "1", 'c', name: "c.webm", source: "8kun", board: "kot") with { ThreadTitle = "/kot/ talk" },
            plain = Entry("d", "1", 'd', name: "chanel.webm", source: "8kun", board: "x") with { ThreadTitle = "4 items" };
        MediaLibraryEntry[] all = [g, b, kot, plain];
        string[] Find(string text) => Posts(MediaLibraryFilterEngine.Apply(all, new(nameContains: text), MediaLibraryUserData.Unavailable));

        Assert.Equal(new[] { "a:1", "b:1" }, Find("4chan")); // the source as a whole, any case
        Assert.Equal(new[] { "a:1", "b:1" }, Find("4CHAN"));
        Assert.Equal(new[] { "d:1" }, Find("chan")); // not the source "4chan"; only the file name "chanel.webm" holds it
        Assert.Equal(new[] { "d:1" }, Find("4")); // the title "4 items"; not the source "4chan"
        Assert.Equal(new[] { "a:1" }, Find("g")); // the board g (and "General"); no other board, source, title or name holds a g
        Assert.Equal(new[] { "a:1" }, Find("/g/ general")); // "/g/" names the board; the other word is in the title
        Assert.Equal(new[] { "a:1" }, Find("/G/"));
        Assert.Equal(new[] { "c:1" }, Find("kot")); // board kot, and "/kot/" in the title
        Assert.Equal(new[] { "c:1" }, Find("/kot/")); // the board "kot" without its slashes, and the title literally
        Assert.Equal(new[] { "d:1" }, Find("items"));
        Assert.Empty(Find("//")); // slashes alone name no board
    }

    // ---- where the text was found (the page's breakdown row) -----------------------------------------------------

    private static int CountIn(IEnumerable<MediaTextTally> tally, MediaTextFields field) => tally.Count(item => (item.Fields & field) != 0);

    private static (MediaLibraryEntry[] All, MediaLibraryUserData Data) TallyLibrary()
    {
        MediaLibraryEntry name = Entry("a", "1", 'a', name: "cute.png") with { ThreadTitle = "Plain" },
            title = Entry("b", "1", 'b', name: "x.png") with { ThreadTitle = "Cute thread" },
            tag = Entry("c", "1", 'c', name: "y.png") with { ThreadTitle = "Other" },
            all = Entry("d", "1", 'd', name: "cute.webm", kind: SavedMediaKind.Video) with { ThreadTitle = "cute thread" },
            spread = Entry("e", "1", 'e', name: "cute.gif") with { ThreadTitle = "thread story" },
            none = Entry("f", "1", 'f', name: "z.png") with { ThreadTitle = "Nothing" };
        // The tag "cute" is on the file of c, and on the thread d (so it counts for every file of that thread).
        return ([name, title, tag, all, spread, none], Data(threads: [new(new TagId(Cute), all.Thread)], media: [MediaTag(Cute, tag)]));
    }

    [Fact]
    public void TextTallyCountsEveryFieldInWhichAllWordsHit()
    {
        (MediaLibraryEntry[] library, MediaLibraryUserData data) = TallyLibrary();
        MediaLibraryFilterResult one = MediaLibraryFilterEngine.Evaluate(library, new(nameContains: "cute"), data);
        IReadOnlyList<MediaTextTally> tally = one.TextTally!;
        Assert.Equal(new[] { "a:1", "b:1", "c:1", "d:1", "e:1" }, Posts(tally.Select(item => item.Entry)));
        Assert.Equal(Posts(one.Passing), Posts(tally.Select(item => item.Entry))); // the default scope is "All"
        Assert.Equal(new[] { MediaTextFields.Name, MediaTextFields.Title, MediaTextFields.Tag, MediaTextFields.Name | MediaTextFields.Title | MediaTextFields.Tag, MediaTextFields.Name },
            tally.Select(item => item.Fields));
        Assert.Equal(3, CountIn(tally, MediaTextFields.Name));
        Assert.Equal(2, CountIn(tally, MediaTextFields.Title));
        Assert.Equal(2, CountIn(tally, MediaTextFields.Tag)); // the tag on a file, and the tag of a thread for its files
        Assert.Equal(0, CountIn(tally, MediaTextFields.Board));
        Assert.Equal(0, CountIn(tally, MediaTextFields.Source));

        // Two words: each must hit, in the same field to count for that field.
        IReadOnlyList<MediaTextTally> two = MediaLibraryFilterEngine.Evaluate(library, new(nameContains: "cute thread"), data).TextTally!;
        Assert.Equal(new[] { "b:1", "d:1", "e:1" }, Posts(two.Select(item => item.Entry)));
        Assert.Equal(new[] { MediaTextFields.Title, MediaTextFields.Title, MediaTextFields.None }, two.Select(item => item.Fields)); // e: "cute" in the name, "thread" in the title
        Assert.Equal(0, CountIn(two, MediaTextFields.Name));
        Assert.Equal(2, CountIn(two, MediaTextFields.Title));
    }

    [Fact]
    public void WordsSpreadOverSeveralFieldsAreInEveryFieldCountOfNothingButInTheTotal()
    {
        (MediaLibraryEntry[] library, MediaLibraryUserData data) = TallyLibrary();
        MediaLibraryEntry spread = library[4];
        IReadOnlyList<MediaTextTally> tally = MediaLibraryFilterEngine.Evaluate([spread], new(nameContains: "cute story"), data).TextTally!;
        Assert.Equal(spread, Assert.Single(tally).Entry);
        Assert.Equal(MediaTextFields.None, tally[0].Fields);
        foreach (MediaTextFields field in new[] { MediaTextFields.Name, MediaTextFields.Title, MediaTextFields.Board, MediaTextFields.Source, MediaTextFields.Tag })
        {
            Assert.Equal(0, CountIn(tally, field));
            // Chosen alone, that field finds nothing; the total ("All") finds the file.
            Assert.Empty(MediaLibraryFilterEngine.Apply([spread], new(nameContains: "cute story", textFields: field), data));
        }
        Assert.Single(MediaLibraryFilterEngine.Apply([spread], new(nameContains: "cute story"), data));
        // Name and title together are enough for the two words.
        Assert.Single(MediaLibraryFilterEngine.Apply([spread], new(nameContains: "cute story", textFields: MediaTextFields.Name | MediaTextFields.Title), data));
    }

    [Fact]
    public void AScopeNarrowsWhatPassesButNotTheTally()
    {
        (MediaLibraryEntry[] library, MediaLibraryUserData data) = TallyLibrary();
        MediaLibraryFilterResult all = MediaLibraryFilterEngine.Evaluate(library, new(nameContains: "cute"), data);
        string[] total = Posts(all.TextTally!.Select(item => item.Entry));
        foreach ((MediaTextFields scope, string[] expected) in new (MediaTextFields, string[])[]
        {
            (MediaTextFields.Name, ["a:1", "d:1", "e:1"]), (MediaTextFields.Title, ["b:1", "d:1"]), (MediaTextFields.Tag, ["c:1", "d:1"]),
            (MediaTextFields.Name | MediaTextFields.Tag, ["a:1", "c:1", "d:1", "e:1"]), (MediaTextFields.Board, []), (MediaTextFields.Source, [])
        })
        {
            MediaLibraryFilterResult narrowed = MediaLibraryFilterEngine.Evaluate(library, new(nameContains: "cute", textFields: scope), data);
            Assert.Equal(expected, Posts(narrowed.Passing));
            Assert.Equal(total, Posts(narrowed.TextTally!.Select(item => item.Entry))); // the numbers do not depend on the scope
            Assert.Equal(all.TextTally!.Select(item => item.Fields), narrowed.TextTally!.Select(item => item.Fields));
            // "Chosen alone, a part shows exactly its number" holds for every single field.
            if (scope is MediaTextFields.Name or MediaTextFields.Title or MediaTextFields.Tag or MediaTextFields.Board or MediaTextFields.Source)
                Assert.Equal(CountIn(all.TextTally!, scope), narrowed.Passing.Count);
        }
        // No text, no tally.
        Assert.Null(MediaLibraryFilterEngine.Evaluate(library, new(kind: MediaLibraryKindFilter.Video), data).TextTally);
        Assert.Null(MediaLibraryFilterEngine.Evaluate(library, MediaLibraryFilter.None, data).TextTally);
    }

    [Fact]
    public void TheTallyHoldsOnlyWhatPassesEveryOtherPartOfTheFilter()
    {
        (MediaLibraryEntry[] library, MediaLibraryUserData data) = TallyLibrary();
        // A type filter: only the video of "d" is left of the five hits.
        IReadOnlyList<MediaTextTally> videos = MediaLibraryFilterEngine.Evaluate(library, new(nameContains: "cute", kind: MediaLibraryKindFilter.Video), data).TextTally!;
        Assert.Equal(new[] { "d:1" }, Posts(videos.Select(item => item.Entry)));
        // A tag filter counts as the grid does: thread tag Cute reaches "d" only (the file tag is on "c").
        IReadOnlyList<MediaTextTally> tagged = MediaLibraryFilterEngine.Evaluate(library, new(tags: new ArchiveTagFilter([Cute], null), nameContains: "cute"), data).TextTally!;
        Assert.Equal(new[] { "c:1", "d:1" }, Posts(tagged.Select(item => item.Entry)));
        // A video whose sound is unknown is in no answer, so it is not in the tally either.
        IReadOnlyList<MediaTextTally> sound = MediaLibraryFilterEngine.Evaluate(library, new(nameContains: "cute", sound: MediaLibrarySoundFilter.WithSound), data).TextTally!;
        Assert.Empty(sound);
    }

    [Fact]
    public void BoardAndSourceAreFieldsOfTheTextAndNeedTheWholeValue()
    {
        MediaLibraryEntry g = Entry("a", "1", 'a', name: "x.webm", source: "4chan", board: "g") with { ThreadTitle = "Lamp" };
        IReadOnlyList<MediaTextTally> board = MediaLibraryFilterEngine.Evaluate([g], new(nameContains: "/g/"), MediaLibraryUserData.Unavailable).TextTally!;
        Assert.Equal(MediaTextFields.Board, Assert.Single(board).Fields);
        IReadOnlyList<MediaTextTally> source = MediaLibraryFilterEngine.Evaluate([g], new(nameContains: "4CHAN"), MediaLibraryUserData.Unavailable).TextTally!;
        Assert.Equal(MediaTextFields.Source, Assert.Single(source).Fields);
        Assert.Empty(MediaLibraryFilterEngine.Evaluate([g], new(nameContains: "chan"), MediaLibraryUserData.Unavailable).TextTally!);
        Assert.Single(MediaLibraryFilterEngine.Apply([g], new(nameContains: "4chan", textFields: MediaTextFields.Source), MediaLibraryUserData.Unavailable));
        Assert.Empty(MediaLibraryFilterEngine.Apply([g], new(nameContains: "4chan", textFields: MediaTextFields.Name | MediaTextFields.Title | MediaTextFields.Board), MediaLibraryUserData.Unavailable));
    }

    [Fact]
    public void WithoutTheTagIndexTheDefaultScopeSkipsTheTagFieldButInTagFails()
    {
        (MediaLibraryEntry[] library, _) = TallyLibrary();
        MediaLibraryUserData none = MediaLibraryUserData.Unavailable;
        // "All": the four other fields answer; the tag-only file ("c") is simply not found, nothing throws.
        MediaLibraryFilterResult result = MediaLibraryFilterEngine.Evaluate(library, new(nameContains: "cute"), none);
        Assert.Equal(new[] { "a:1", "b:1", "d:1", "e:1" }, Posts(result.Passing));
        Assert.Equal(0, CountIn(result.TextTally!, MediaTextFields.Tag));
        // Naming the tags as the place to look is refused, as a tag filter is.
        Assert.Equal(MediaLibraryFilterError.TagsUnavailable, Assert.Throws<MediaLibraryFilterException>(() =>
            MediaLibraryFilterEngine.Apply(library, new(nameContains: "cute", textFields: MediaTextFields.Tag), none)).Error);
        Assert.Equal(MediaLibraryFilterError.TagsUnavailable, Assert.Throws<MediaLibraryFilterException>(() =>
            MediaLibraryFilterEngine.Apply(library, new(nameContains: "cute", textFields: MediaTextFields.Name | MediaTextFields.Tag), none)).Error);
        Assert.Equal(new[] { "a:1", "d:1", "e:1" }, Posts(MediaLibraryFilterEngine.Apply(library, new(nameContains: "cute", textFields: MediaTextFields.Name), none)));
        // Without any word the scope means nothing, so it cannot fail either.
        Assert.Equal(library.Length, MediaLibraryFilterEngine.Apply(library, new(textFields: MediaTextFields.Tag), none).Count);
    }

    [Fact]
    public void ThreadFieldsAreSearchedOncePerThreadAndEvaluation()
    {
        MediaLibraryEntry[] files = [.. Enumerable.Range(1, 40).Select(index => Entry("a", index.ToString(), (char)('a' + index % 20), name: "f" + index + ".png") with { ThreadTitle = "Cute thread" }),
            Entry("b", "1", 'z', name: "g.png") with { ThreadTitle = "Cute thread" }];
        int before = MediaLibraryFilterEngine.ThreadSearchCount;
        MediaLibraryFilterResult result = MediaLibraryFilterEngine.Evaluate(files, new(nameContains: "cute thread f"), MediaLibraryUserData.Unavailable);
        Assert.Equal(40, result.Passing.Count); // "f" hits the file names of the first thread only
        Assert.Equal(2, MediaLibraryFilterEngine.ThreadSearchCount - before); // two threads, three words, 41 files
    }

    [Fact]
    public void TheTextScopeIsPartOfTheFilterAndDefaultsToEveryField()
    {
        Assert.Equal(MediaTextFields.All, new MediaLibraryFilter(nameContains: "cat").TextFields);
        Assert.Equal(new MediaLibraryFilter(nameContains: "cat"), new MediaLibraryFilter(nameContains: "cat", textFields: MediaTextFields.All));
        MediaLibraryFilter tags = new(nameContains: "cat", textFields: MediaTextFields.Tag), titles = new(nameContains: "cat", textFields: MediaTextFields.Title);
        Assert.NotEqual(new MediaLibraryFilter(nameContains: "cat"), tags);
        Assert.NotEqual(tags, titles);
        Assert.NotEqual(tags.GetHashCode(), titles.GetHashCode());
        Assert.Equal(tags, new MediaLibraryFilter(nameContains: " cat ", textFields: MediaTextFields.Tag));
        Assert.Equal(tags.GetHashCode(), new MediaLibraryFilter(nameContains: " cat ", textFields: MediaTextFields.Tag).GetHashCode());
        // Without a word the scope is forgotten: the filter is empty and equal to no filter.
        MediaLibraryFilter empty = new(nameContains: "\"\"", textFields: MediaTextFields.Tag);
        Assert.Equal(MediaTextFields.All, empty.TextFields);
        Assert.True(empty.IsEmpty);
        Assert.Equal(MediaLibraryFilter.None, new MediaLibraryFilter(textFields: MediaTextFields.Name));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaLibraryFilter(nameContains: "cat", textFields: MediaTextFields.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaLibraryFilter(nameContains: "cat", textFields: (MediaTextFields)64));
    }

    [Fact]
    public void FreeTextThreadAnswersAreNotReusedForAnotherTitleOfTheSameThread()
    {
        // One thread seen with two titles (e.g. a partial index): each occurrence is judged by its own title.
        MediaLibraryEntry before = Entry("a", "1", 'a') with { ThreadTitle = "Old title" }, after = Entry("a", "2", 'b') with { ThreadTitle = "/kot/ new" };
        Assert.Equal(new[] { "a:2" }, Posts(MediaLibraryFilterEngine.Apply([before, after], new(nameContains: "/kot/"), MediaLibraryUserData.Unavailable)));
        Assert.Equal(new[] { "a:1" }, Posts(MediaLibraryFilterEngine.Apply([after, before], new(nameContains: "old"), MediaLibraryUserData.Unavailable)));
    }

    [Fact]
    public void UntaggedCountIsTheTileCountTheFilterWouldShowAndIsBuiltOncePerIndexAndTagState()
    {
        // Equal bytes in two threads: one copy tagged, the other not. The tile counts once, like the grid shows it.
        MediaLibraryEntry tagged = Entry("a", "1", 'a'), twin = Entry("b", "1", 'a'), plain = Entry("b", "2", 'c'),
            other = Entry("c", "1", 'd', name: "other.gif"), done = Entry("c", "2", 'e');
        MediaLibraryUserData data = Data(media: [MediaTag(Cute, tagged), MediaTag(Rpt, done)]);
        MediaLibraryEntry[] all = [tagged, twin, plain, other, done];
        MediaLibraryUntagged untagged = MediaLibraryUntagged.Of(all, data)!;

        foreach (MediaLibraryFilter filter in new MediaLibraryFilter[] { MediaLibraryFilter.None, new(kind: MediaLibraryKindFilter.Gif), new(thread: twin.Thread) })
        {
            IReadOnlyList<MediaLibraryEntry> passing = MediaLibraryFilterEngine.Apply(all, filter, data);
            MediaLibraryFilter narrowed = new(filter.Tags, MediaLibraryKindFilter.Any, filter.FavoritesOnly, filter.MinimumRating, filter.Source, filter.Board,
                filter.NameContains, filter.Thread, filter.Sound, withoutMediaTags: true, kinds: filter.Kinds,
                excludeSources: filter.ExcludeSources, excludeBoards: filter.ExcludeBoards);
            int shown = MediaLibraryGrouping.Build(all, MediaLibraryFilterEngine.Apply(all, narrowed, data), MediaLibrarySort.ByThread, includeMissing: false).Count;
            Assert.Equal(shown, untagged.CountTiles(passing));
        }
        Assert.Equal(3, untagged.CountTiles(all));
        Assert.True(untagged.IsFor(all, data));
        Assert.False(untagged.IsFor(all, Data()));
        Assert.False(untagged.IsFor(all.ToArray(), data));
    }

    [Fact]
    public void CarriedTagsListMediaTagsFirstThenThreadTagsEachOnce()
    {
        MediaLibraryEntry entry = Entry("a", "1", 'a');
        MediaLibraryUserData data = Data(threads: [new(new TagId(Rpt), entry.Thread), new(new TagId(Cute), entry.Thread)],
            media: [MediaTag(Bad, entry), MediaTag(Cute, entry)]);
        MediaLibraryEntryState state = MediaLibraryEntryState.Of(entry, data);
        // Vocabulary order inside each part: rpt, cute, bad.
        Assert.Equal(new[] { "cute", "bad", "rpt" }, state.CarriedTags.Select(tag => tag.Name.Display));
        Assert.Equal(data.TagsOf(entry).Order(), state.CarriedTags.Select(tag => tag.Id.Value).Order());
        Assert.Empty(MediaLibraryEntryState.Of(entry, MediaLibraryUserData.Unavailable).CarriedTags);
    }
}

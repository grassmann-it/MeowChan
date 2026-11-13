using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Ratings;
using MeowChan.Tags;

namespace MeowChan.Tests;

/// <summary>The tag count and rating sorts of the media page: keys per occurrence, the group maximum, unknown data, and cache validity.</summary>
public sealed class MediaLibraryUserOrderTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid Rpt = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Cute = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid Bad = Guid.Parse("00000000-0000-0000-0000-0000000000a3");

    private static string Hash(char c) => new(c, 64);

    // Captured hour = the number in the thread name, so a higher number is the newer thread.
    private static MediaLibraryEntry Entry(string thread, char hash, int captured = 1, bool missing = false) =>
        new(new ResourceId("s", "b", thread), "T" + thread, 1, Day, Day.AddHours(captured), "1", 1, null, 0, "att-v1:" + Hash(hash),
            missing ? null : "/dir/" + thread, missing ? null : "asset-" + thread, missing ? null : Hash(hash), missing ? null : 1,
            "m" + thread, "original", "f.png", SavedMediaKind.Image,
            missing ? MediaLibraryPresence.Missing : MediaLibraryPresence.Present, false, false);

    private static PrivateTagDefinition Tag(Guid id, string name)
    {
        Assert.True(PrivateTagName.TryCreate(name, out PrivateTagName? tagName, out _));
        return new PrivateTagDefinition(new TagId(id), tagName!);
    }

    private static MediaLibraryUserData Data(IEnumerable<PrivateThreadTagAssignment>? threads = null,
        IEnumerable<PrivateMediaTagAssignment>? media = null, IEnumerable<(MediaLibraryEntry Entry, int Value)>? ratings = null,
        long revision = 1, string fingerprint = "fp") =>
        new(new PrivateTagStoreSnapshot(revision, fingerprint, [Tag(Rpt, "rpt"), Tag(Cute, "cute"), Tag(Bad, "bad")], threads ?? [], media ?? []),
            new FavoriteEntry[] { },
            (ratings ?? []).Select(pair => new MediaRatingEntry(MediaLibraryFilterEngine.AssetKey(pair.Entry)!, pair.Value)).ToArray());

    private static PrivateThreadTagAssignment ThreadTag(Guid tag, MediaLibraryEntry entry) => new(new TagId(tag), entry.Thread);

    private static PrivateMediaTagAssignment MediaTag(Guid tag, MediaLibraryEntry entry) => new(new TagId(tag), MediaLibraryFilterEngine.TagTarget(entry)!);

    private static string[] Threads(IEnumerable<MediaLibraryEntry> entries) => entries.Select(entry => entry.Thread.Thread!).ToArray();

    [Fact]
    public void TagCountIsOwnAndThreadTagsCountedOnceAndAGroupTakesTheMaximum()
    {
        MediaLibraryEntry a = Entry("a", 'a'), b = Entry("b", 'a'), c = Entry("c", 'c'), d = Entry("d", 'd');
        MediaLibraryEntry[] all = [a, b, c, d];
        MediaLibraryUserData data = Data(
            threads: [ThreadTag(Rpt, a), ThreadTag(Rpt, b), ThreadTag(Rpt, d), ThreadTag(Cute, d)],
            media: [MediaTag(Cute, a), MediaTag(Bad, a), MediaTag(Rpt, b)]);

        MediaLibraryUserOrder order = MediaLibraryUserOrder.Of(all, data);

        // a: two media tags and the thread's tag = 3. b: the same tag on file and thread counts once = 1, but it shares bytes with a, so its tile counts 3.
        Assert.Equal(3, order.TagCountOf(a));
        Assert.Equal(3, order.TagCountOf(b));
        Assert.Equal(0, order.TagCountOf(c)); // known and zero: tags are available, this file just has none
        Assert.Equal(2, order.TagCountOf(d));
        Assert.True(order.TagsKnown);
        Assert.Equal((long?)1, order.TagRevision);
    }

    [Fact]
    public void UnavailableTagsAndRatingsAreUnknownNotZeroAndNeverThrow()
    {
        MediaLibraryEntry a = Entry("a", 'a');
        MediaLibraryUserOrder order = MediaLibraryUserOrder.Of([a], MediaLibraryUserData.Unavailable);

        Assert.False(order.TagsKnown);
        Assert.False(order.RatingsKnown);
        Assert.Null(order.TagRevision);
        Assert.Equal(MediaLibraryUserOrder.Unknown, order.TagCountOf(a));
        Assert.Equal(MediaLibraryUserOrder.Unknown, order.RatingOf(a));
    }

    [Fact]
    public void ARatingIsTheGroupMaximumAndUnratedMissingAndForeignEntriesAreUnknown()
    {
        MediaLibraryEntry a = Entry("a", 'a'), b = Entry("b", 'a'), c = Entry("c", 'c'), d = Entry("d", 'd'), gone = Entry("gone", 'e', missing: true);
        MediaLibraryEntry foreign = Entry("foreign", 'f');
        MediaLibraryEntry[] all = [a, b, c, d, gone];
        MediaLibraryUserOrder order = MediaLibraryUserOrder.Of(all, Data(ratings: [(a, 2), (b, 5), (d, 1)]));

        Assert.True(order.RatingsKnown);
        Assert.Equal(5, order.RatingOf(a));
        Assert.Equal(5, order.RatingOf(b)); // equal bytes: the tile counts its best rating
        Assert.Equal(MediaLibraryUserOrder.Unknown, order.RatingOf(c)); // unrated
        Assert.Equal(1, order.RatingOf(d));
        Assert.Equal(MediaLibraryUserOrder.Unknown, order.RatingOf(gone)); // no file, nothing to rate
        Assert.Equal(MediaLibraryUserOrder.Unknown, order.RatingOf(foreign));
        Assert.Equal(MediaLibraryUserOrder.Unknown, order.TagCountOf(foreign));
    }

    [Fact]
    public void OnlyTagCountAndRatingHaveUserKeys()
    {
        MediaLibraryEntry a = Entry("a", 'a');
        MediaLibraryUserOrder order = MediaLibraryUserOrder.Of([a], Data());
        Assert.Throws<ArgumentOutOfRangeException>(() => order.KeyOf(MediaLibrarySort.Name, a));
        Assert.Equal(0, order.KeyAt(MediaLibrarySort.TagCount, 0));
    }

    [Fact]
    public void ItIsValidForTheSameEntriesAndTagStateAndForNothingElse()
    {
        MediaLibraryEntry a = Entry("a", 'a');
        MediaLibraryEntry[] all = [a];
        MediaLibraryUserData data = Data(ratings: [(a, 3)]);
        MediaLibraryUserOrder order = MediaLibraryUserOrder.Of(all, data);

        Assert.True(order.IsFor(all, data));
        Assert.False(order.IsFor(all.ToArray(), data)); // another list is another index
        // The same store state read again (equal revision and fingerprint) keeps the order; a newer revision or other content does not.
        Assert.True(order.IsFor(all, new MediaLibraryUserData(Data(ratings: [(a, 3)]).TagSnapshot, [], data.RatingSource)));
        Assert.False(order.IsFor(all, new MediaLibraryUserData(Data(revision: 2).TagSnapshot, [], data.RatingSource)));
        Assert.False(order.IsFor(all, new MediaLibraryUserData(Data(fingerprint: "other").TagSnapshot, [], data.RatingSource)));
        Assert.False(order.IsFor(all, Data(ratings: [(a, 3)]))); // another rating list
        Assert.False(order.IsFor(all, MediaLibraryUserData.Unavailable));
        Assert.True(MediaLibraryUserOrder.Of(all, MediaLibraryUserData.Unavailable).IsFor(all, MediaLibraryUserData.Unavailable));
    }

    [Fact]
    public void TagCountSortsMostFirstTiesInThreadOrderAndReversedIsTheMirror()
    {
        MediaLibraryEntry t1 = Entry("t1", 'a', 1), t2 = Entry("t2", 'b', 2), t3 = Entry("t3", 'c', 3), t4 = Entry("t4", 'd', 4);
        MediaLibraryEntry[] all = [t1, t2, t3, t4];
        MediaLibraryUserOrder order = MediaLibraryUserOrder.Of(all, Data(
            threads: [ThreadTag(Rpt, t2), ThreadTag(Cute, t2), ThreadTag(Rpt, t3), ThreadTag(Rpt, t4), ThreadTag(Bad, t4)]));

        // By thread: t4, t3, t2, t1. Counts: t4 2, t3 1, t2 2, t1 0.
        Assert.Equal(new[] { "t4", "t2", "t3", "t1" }, Threads(MediaLibraryView.Select(all, MediaLibrarySort.TagCount, true, userOrder: order)));
        Assert.Equal(new[] { "t1", "t3", "t2", "t4" },
            Threads(MediaLibraryView.Select(all, MediaLibrarySort.TagCount, true, direction: MediaLibrarySortDirection.Reversed, userOrder: order)));
    }

    [Fact]
    public void RatingSortsBestFirstAndUnratedStayLastInBothDirections()
    {
        MediaLibraryEntry r1 = Entry("r1", 'a', 1), r2 = Entry("r2", 'b', 2), r3 = Entry("r3", 'c', 3), r4 = Entry("r4", 'd', 4);
        MediaLibraryEntry[] all = [r1, r2, r3, r4];
        MediaLibraryUserOrder order = MediaLibraryUserOrder.Of(all, Data(ratings: [(r1, 3), (r3, 5), (r4, 3)]));

        // By thread: r4, r3, r2, r1. Ratings: r4 3, r3 5, r2 none, r1 3.
        Assert.Equal(new[] { "r3", "r4", "r1", "r2" }, Threads(MediaLibraryView.Select(all, MediaLibrarySort.Rating, true, userOrder: order)));
        Assert.Equal(new[] { "r1", "r4", "r3", "r2" },
            Threads(MediaLibraryView.Select(all, MediaLibrarySort.Rating, true, direction: MediaLibrarySortDirection.Reversed, userOrder: order)));
    }

    [Fact]
    public void UnavailableTagsKeepTheByThreadOrderInBothDirections()
    {
        MediaLibraryEntry[] all = [Entry("t1", 'a', 1), Entry("t2", 'b', 2), Entry("t3", 'c', 3)];
        MediaLibraryUserOrder order = MediaLibraryUserOrder.Of(all, MediaLibraryUserData.Unavailable);
        foreach (MediaLibrarySort sort in new[] { MediaLibrarySort.TagCount, MediaLibrarySort.Rating })
            foreach (MediaLibrarySortDirection direction in Enum.GetValues<MediaLibrarySortDirection>())
                Assert.Equal(new[] { "t3", "t2", "t1" }, Threads(MediaLibraryView.Select(all, sort, true, direction: direction, userOrder: order)));
    }

    [Fact]
    public void ATileIsPlacedByTheMaximumOfAllItsOccurrencesEvenWhenOnlyAnUntaggedOneStaysAfterTheFilter()
    {
        // x: a (no tag) and b (three tags) share bytes; y: c has two tags. The tile of x counts 3 whichever occurrence passed.
        MediaLibraryEntry a = Entry("a", 'a', 3), b = Entry("b", 'a', 1), c = Entry("c", 'c', 2);
        MediaLibraryEntry[] all = [a, b, c];
        MediaLibraryUserOrder order = MediaLibraryUserOrder.Of(all, Data(
            threads: [ThreadTag(Rpt, b), ThreadTag(Cute, b), ThreadTag(Bad, b), ThreadTag(Rpt, c), ThreadTag(Cute, c)]));

        IReadOnlyList<MediaLibraryTile> onlyUntagged = MediaLibraryGrouping.Build(all, [a, c], MediaLibrarySort.TagCount, false, userOrder: order);
        Assert.Equal(new[] { "a", "c" }, Threads(onlyUntagged.Select(tile => tile.Entry)));
        Assert.Equal(new[] { 2, 1 }, onlyUntagged.Select(tile => tile.Occurrences.Count));

        IReadOnlyList<MediaLibraryTile> fewest = MediaLibraryGrouping.Build(all, all, MediaLibrarySort.TagCount, false,
            direction: MediaLibrarySortDirection.Reversed, userOrder: order);
        Assert.Equal(new[] { "c", "b" }, Threads(fewest.Select(tile => tile.Entry))); // the group's keys are equal: mirrored thread order shows b
    }

    [Fact]
    public void TheIndexAndTheGeneralPathAgreeOnUserSortsOfHandMadeData()
    {
        MediaLibraryEntry a = Entry("a", 'a', 3), b = Entry("b", 'a', 1), c = Entry("c", 'c', 2), d = Entry("d", 'd', 4);
        MediaLibraryEntry[] all = [a, b, c, d];
        MediaLibraryUserOrder order = MediaLibraryUserOrder.Of(all, Data(
            threads: [ThreadTag(Rpt, b), ThreadTag(Cute, c)], ratings: [(a, 4), (c, 2)]));
        MediaLibraryPresentIndex index = new(all);
        foreach (MediaLibrarySort sort in new[] { MediaLibrarySort.TagCount, MediaLibrarySort.Rating })
            foreach (MediaLibrarySortDirection direction in Enum.GetValues<MediaLibrarySortDirection>())
            {
                IReadOnlyList<MediaLibraryTile> expected = MediaLibraryGrouping.Build(all, all, sort, true, 0, null, direction, order);
                IReadOnlyList<MediaLibraryTile> actual = index.Build(null, sort, true, 0, null, default, direction, order);
                Assert.Equal(expected.Select(tile => tile.Entry.Thread.Thread), actual.Select(tile => tile.Entry.Thread.Thread));
            }
    }
}

using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Ratings;
using MeowChan.Tags;

namespace MeowChan.Tests;

/// <summary>
/// Regression tests for the media sorts on synthetic data with many equal sort keys: the index path and the general path agree for any
/// order of <c>passing</c> when the by-thread keys are a total order, no tile is lost or added by a sort or a direction, and the user
/// order follows its rating and tag sources. Fully tied entries (equal in every key) are the unchanged legacy contract, pinned separately.
/// </summary>
public sealed class MediaLibrarySortDeterminismTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TagId One = new(Guid.Parse("00000000-0000-0000-0000-0000000000c1"));
    private static readonly TagId Two = new(Guid.Parse("00000000-0000-0000-0000-0000000000c2"));

    // Three capture times for every thread, three names that differ by case (some entries have none), three sizes, nine byte contents spread over all threads,
    // missing files, files without hash and loose files, and a unique (thread, post, position, asset) for every entry: a total order.
    private static MediaLibraryEntry[] TiedLibrary(int threads = 24, int perThread = 14)
    {
        List<MediaLibraryEntry> entries = new();
        for (int t = 0; t < threads; t++)
        {
            ResourceId id = new(t % 5 == 0 ? "other" : "s", t % 2 == 0 ? "a" : "b", "t" + t.ToString("D3"));
            DateTimeOffset captured = Day.AddHours(t % 3);
            for (int i = 0; i < perThread; i++)
            {
                bool missing = i % 5 == 4, loose = !missing && i % 7 == 6, hashless = !missing && i % 13 == 12;
                int shared = (t * 3 + i) % 9;
                string? sha = missing || hashless ? null : shared.ToString("x").PadLeft(64, '0');
                long? length = missing || hashless ? null : 1 + shared % 3;
                string? name = i % 11 == 5 || hashless ? null : (i % 2 == 0 ? "Name" : "name") + (i % 3);
                long? ordinal = loose ? null : i / 2 + 1;
                int? position = loose ? null : i % 2;
                entries.Add(new MediaLibraryEntry(id, "T" + t, 1, Day, captured, loose ? null : ordinal.ToString(), ordinal,
                    t % 4 == 0 ? Day.AddMinutes(i % 3) : null, position, null,
                    missing ? null : "/d/" + t, missing ? null : "asset-" + t + "-" + i, sha, length, "m" + i, "original", name,
                    SavedMediaKind.Image, missing ? MediaLibraryPresence.Missing : MediaLibraryPresence.Present, loose, false));
            }
        }
        return entries.ToArray();
    }

    private static PrivateTagDefinition Definition(TagId id, string name)
    {
        Assert.True(PrivateTagName.TryCreate(name, out PrivateTagName? parsed, out _));
        return new PrivateTagDefinition(id, parsed!);
    }

    private static PrivateTagStoreSnapshot TagSnapshot(MediaLibraryEntry[] all, long revision, Func<int, bool> first, Func<int, bool> second)
    {
        ResourceId[] threads = all.Select(entry => entry.Thread).Distinct().ToArray();
        List<PrivateThreadTagAssignment> assigned = new();
        for (int i = 0; i < threads.Length; i++)
        {
            if (first(i)) assigned.Add(new PrivateThreadTagAssignment(One, threads[i]));
            if (second(i)) assigned.Add(new PrivateThreadTagAssignment(Two, threads[i]));
        }
        return new PrivateTagStoreSnapshot(revision, "fp" + revision, [Definition(One, "one"), Definition(Two, "two")], assigned, []);
    }

    private static MediaRatingEntry[] RatingList(MediaLibraryEntry[] all, int remainder, Func<int, MediaLibraryEntry, int> value)
    {
        List<MediaRatingEntry> ratings = new();
        for (int i = 0; i < all.Length; i++)
            if (i % 3 == remainder && MediaLibraryFilterEngine.AssetKey(all[i]) is { } key) ratings.Add(new MediaRatingEntry(key, value(i, all[i])));
        return ratings.ToArray();
    }

    // State A and state B differ in tags (revision, assignments) and in ratings (which entries, which values).
    private static MediaLibraryUserData StateA(MediaLibraryEntry[] all) =>
        new(TagSnapshot(all, 1, i => i % 3 == 0, i => i % 4 == 0), Array.Empty<FavoriteEntry>(), RatingList(all, 0, (i, _) => i % 2 == 0 ? 5 : 1));

    private static MediaLibraryUserData StateB(MediaLibraryEntry[] all) =>
        new(TagSnapshot(all, 2, i => i % 3 == 1, i => i % 5 == 0), Array.Empty<FavoriteEntry>(), RatingList(all, 1, (i, _) => 2 + i % 3));

    private static IReadOnlyList<MediaLibraryEntry> Shuffled(IReadOnlyList<MediaLibraryEntry> source, int seed)
    {
        MediaLibraryEntry[] copy = source.ToArray();
        Random random = new(seed);
        for (int i = copy.Length - 1; i > 0; i--) { int j = random.Next(i + 1); (copy[i], copy[j]) = (copy[j], copy[i]); }
        return copy;
    }

    private static IReadOnlyList<MediaLibraryEntry>[] Permutations(IReadOnlyList<MediaLibraryEntry> source) =>
        [source, source.Reverse().ToArray(), Shuffled(source, 1), Shuffled(source, 2)];

    private static void AssertSame(IReadOnlyList<MediaLibraryTile> expected, IReadOnlyList<MediaLibraryTile> actual, string context)
    {
        Assert.True(expected.Count == actual.Count, context + ": " + expected.Count + " tiles expected, " + actual.Count + " built");
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.True(ReferenceEquals(expected[i].Entry, actual[i].Entry), context + ": tile " + i + " shows another occurrence");
            Assert.Equal(expected[i].Occurrences.Count, actual[i].Occurrences.Count);
            for (int j = 0; j < expected[i].Occurrences.Count; j++) Assert.Same(expected[i].Occurrences[j], actual[i].Occurrences[j]);
        }
    }

    // What a tile is made of, independent of which occurrence it shows and where it stands.
    private static string[] Signatures(IEnumerable<MediaLibraryTile> tiles) => tiles
        .Select(tile => string.Join("|", tile.Occurrences.Select(entry => entry.Thread.Thread + ":" + entry.PostId + ":" + entry.Position + ":" + entry.AssetId).Order(StringComparer.Ordinal)))
        .Order(StringComparer.Ordinal).ToArray();

    public static IEnumerable<object[]> SortsDirectionsAndMissing() =>
        from sort in Enum.GetValues<MediaLibrarySort>()
        from direction in Enum.GetValues<MediaLibrarySortDirection>()
        from includeMissing in new[] { false, true }
        select new object[] { sort, direction, includeMissing };

    [Fact]
    public void TheFixtureHasATotalByThreadAndNewestOrderAndManyEqualSortKeys()
    {
        MediaLibraryEntry[] all = TiedLibrary();
        Assert.Equal(all.Length, all.Select(entry => (entry.Thread, entry.PostOrdinal, entry.Position, entry.AssetId)).Distinct().Count());
        Assert.True(all.Select(entry => entry.Name).Distinct().Count() <= 7);
        Assert.True(all.Select(entry => entry.Length).Distinct().Count() <= 4);
        Assert.Equal(3, all.Select(entry => entry.LastCapturedAt).Distinct().Count());
        MediaLibraryPresentIndex index = new(all);
        Assert.True(index.CountTiles(true) < all.Length); // equal bytes really merge
    }

    [Theory]
    [MemberData(nameof(SortsDirectionsAndMissing))]
    public void AnyOrderOfPassingGivesTheSameTilesOnBothPathsWhenTheKeysAreATotalOrder(MediaLibrarySort sort, MediaLibrarySortDirection direction, bool includeMissing)
    {
        MediaLibraryEntry[] all = TiedLibrary();
        MediaLibraryPresentIndex index = new(all);
        MediaLibraryUserOrder userOrder = MediaLibraryUserOrder.Of(all, StateA(all));
        Func<MediaLibraryEntry, bool> favorite = entry => entry.AssetId != null && entry.AssetId.EndsWith('3');
        IReadOnlyList<MediaLibraryEntry>[] passings =
        [
            all,
            all.Where((_, position) => position % 2 == 0).ToArray(),
            all.Where(entry => entry.Name != null && entry.Name.StartsWith("name", StringComparison.Ordinal)).ToArray()
        ];
        foreach (IReadOnlyList<MediaLibraryEntry> passing in passings)
        {
            IReadOnlyList<MediaLibraryTile> expected = MediaLibraryGrouping.Build(all, passing, sort, includeMissing, 5, favorite, direction, userOrder);
            foreach (IReadOnlyList<MediaLibraryEntry> permuted in Permutations(passing))
            {
                string context = sort + "/" + direction + "/missing=" + includeMissing + "/passing=" + passing.Count;
                AssertSame(expected, MediaLibraryGrouping.Build(all, permuted, sort, includeMissing, 5, favorite, direction, userOrder), "general " + context);
                AssertSame(expected, index.Build(permuted, sort, includeMissing, 5, favorite, CancellationToken.None, direction, userOrder), "index " + context);
            }
        }
    }

    [Theory]
    [MemberData(nameof(SortsDirectionsAndMissing))]
    public void NoSortAndNoDirectionAddsOrLosesATileForAnyLibraryShape(MediaLibrarySort sort, MediaLibrarySortDirection direction, bool includeMissing)
    {
        MediaLibraryEntry[] tied = TiedLibrary();
        MediaLibraryEntry[] allMissing = tied.Select(entry => entry with
            { Presence = MediaLibraryPresence.Missing, Directory = null, AssetId = null, Sha256 = null, Length = null }).ToArray();
        MediaLibraryEntry[] noMetadata = tied.Select(entry => entry with { Name = null, Length = null, Sha256 = null, PostedAt = null }).ToArray();
        MediaLibraryEntry[] empty = Array.Empty<MediaLibraryEntry>();
        foreach ((string label, MediaLibraryEntry[] all) in new[] { ("tied", tied), ("allMissing", allMissing), ("noMetadata", noMetadata), ("empty", empty) })
            foreach (MediaLibraryUserData data in new[] { StateA(all), MediaLibraryUserData.Unavailable })
            {
                MediaLibraryPresentIndex index = new(all);
                MediaLibraryUserOrder userOrder = MediaLibraryUserOrder.Of(all, data);
                IReadOnlyList<MediaLibraryTile> baseline = MediaLibraryGrouping.Build(all, all, MediaLibrarySort.ByThread, includeMissing);
                string[] expected = Signatures(baseline);
                string context = label + "/" + sort + "/" + direction + "/missing=" + includeMissing + "/tags=" + data.TagSnapshot?.Revision;

                IReadOnlyList<MediaLibraryTile> general = MediaLibraryGrouping.Build(all, all, sort, includeMissing, 3, null, direction, userOrder);
                IReadOnlyList<MediaLibraryTile> indexed = index.Build(null, sort, includeMissing, 3, null, CancellationToken.None, direction, userOrder);
                Assert.True(expected.SequenceEqual(Signatures(general)), "general " + context);
                Assert.True(expected.SequenceEqual(Signatures(indexed)), "index " + context);
                Assert.Equal(MediaLibraryGrouping.CountTiles(all, includeMissing), indexed.Count);
                Assert.Equal(index.CountTiles(includeMissing), indexed.Count);
                // Every shown occurrence is one of its tile's occurrences, and no occurrence is shown twice.
                Assert.All(indexed, tile => Assert.Contains(tile.Entry, tile.Occurrences));
                Assert.Equal(indexed.Count, indexed.Select(tile => tile.Entry).Distinct(ReferenceEqualityComparer.Instance).Count());
                // Nothing passes: nothing is shown, whatever the sort.
                Assert.Empty(MediaLibraryGrouping.Build(all, empty, sort, includeMissing, 3, null, direction, userOrder));
                Assert.Empty(index.Build(empty, sort, includeMissing, 3, null, CancellationToken.None, direction, userOrder));
            }
    }

    [Fact]
    public void ReversedKeepsEveryTileAndTheTilesWithoutAKeyStayLast()
    {
        MediaLibraryEntry[] all = TiedLibrary();
        MediaLibraryPresentIndex index = new(all);
        MediaLibraryUserOrder userOrder = MediaLibraryUserOrder.Of(all, StateA(all));
        foreach (MediaLibrarySort sort in new[] { MediaLibrarySort.Name, MediaLibrarySort.Size, MediaLibrarySort.Rating })
        {
            IReadOnlyList<MediaLibraryTile> forward = index.Build(null, sort, true, 0, null, default, MediaLibrarySortDirection.Default, userOrder);
            IReadOnlyList<MediaLibraryTile> backward = index.Build(null, sort, true, 0, null, default, MediaLibrarySortDirection.Reversed, userOrder);
            Func<MediaLibraryTile, bool> hasKey = sort switch
            {
                MediaLibrarySort.Name => tile => !string.IsNullOrEmpty(tile.Entry.Name),
                MediaLibrarySort.Size => tile => tile.Entry.Length != null,
                _ => tile => userOrder.RatingOf(tile.Entry) >= 0
            };
            // The keyless tiles are the same tiles in the same order at the end of both lists.
            MediaLibraryTile[] forwardTail = forward.Reverse().TakeWhile(tile => !hasKey(tile)).Reverse().ToArray();
            MediaLibraryTile[] backwardTail = backward.Reverse().TakeWhile(tile => !hasKey(tile)).Reverse().ToArray();
            Assert.NotEmpty(forwardTail);
            Assert.Equal(forwardTail.Length, backwardTail.Length);
            for (int i = 0; i < forwardTail.Length; i++) Assert.Same(forwardTail[i].Entry, backwardTail[i].Entry);
            Assert.Equal(forward.Count, backward.Count);
        }
    }

    [Fact]
    public void ARatingOrTagChangeIsSeenByTheNextUserOrderAndTheIndexNeverServesAStaleOrder()
    {
        MediaLibraryEntry[] all = TiedLibrary();
        MediaLibraryPresentIndex index = new(all);
        MediaLibraryUserData a = StateA(all), b = StateB(all);
        MediaLibraryUserOrder orderA = MediaLibraryUserOrder.Of(all, a), orderB = MediaLibraryUserOrder.Of(all, b);
        Assert.True(orderA.IsFor(all, a));
        Assert.False(orderA.IsFor(all, b));

        foreach (MediaLibrarySort sort in new[] { MediaLibrarySort.TagCount, MediaLibrarySort.Rating })
            foreach (MediaLibrarySortDirection direction in Enum.GetValues<MediaLibrarySortDirection>())
            {
                IReadOnlyList<MediaLibraryTile> expectedA = MediaLibraryGrouping.Build(all, all, sort, true, 0, null, direction, orderA);
                IReadOnlyList<MediaLibraryTile> expectedB = MediaLibraryGrouping.Build(all, all, sort, true, 0, null, direction, orderB);
                Assert.False(expectedA.Select(tile => tile.Entry).SequenceEqual(expectedB.Select(tile => tile.Entry)), "the two states must really sort differently");
                foreach (MediaLibraryUserOrder current in new[] { orderA, orderB, orderA, orderB, orderB, orderA })
                    AssertSame(ReferenceEquals(current, orderA) ? expectedA : expectedB,
                        index.Build(null, sort, true, 0, null, default, direction, current), sort + "/" + direction);
            }
    }

    [Fact]
    public void RatingsAndTagsInvalidateIndependentlyAndEachSortFollowsOnlyItsOwnSource()
    {
        MediaLibraryEntry[] all = TiedLibrary();
        MediaLibraryUserData a = StateA(all), b = StateB(all);
        // Same tags as A with the ratings of B, and the tags of B with the ratings of A.
        MediaLibraryUserData ratingsChanged = new(a.TagSnapshot, Array.Empty<FavoriteEntry>(), b.RatingSource);
        MediaLibraryUserData tagsChanged = new(b.TagSnapshot, Array.Empty<FavoriteEntry>(), a.RatingSource);
        MediaLibraryUserOrder orderA = MediaLibraryUserOrder.Of(all, a);

        Assert.False(orderA.IsFor(all, ratingsChanged));
        Assert.False(orderA.IsFor(all, tagsChanged));
        MediaLibraryUserOrder viaRatings = MediaLibraryUserOrder.Of(all, ratingsChanged), viaTags = MediaLibraryUserOrder.Of(all, tagsChanged);
        MediaLibraryEntry[] Order(MediaLibraryUserOrder order, MediaLibrarySort sort) =>
            MediaLibraryGrouping.Build(all, all, sort, true, 0, null, MediaLibrarySortDirection.Default, order).Select(tile => tile.Entry).ToArray();

        Assert.Equal(Order(orderA, MediaLibrarySort.TagCount), Order(viaRatings, MediaLibrarySort.TagCount)); // tags unchanged
        Assert.NotEqual(Order(orderA, MediaLibrarySort.Rating), Order(viaRatings, MediaLibrarySort.Rating));
        Assert.Equal(Order(orderA, MediaLibrarySort.Rating), Order(viaTags, MediaLibrarySort.Rating)); // ratings unchanged
        Assert.NotEqual(Order(orderA, MediaLibrarySort.TagCount), Order(viaTags, MediaLibrarySort.TagCount));
    }

    [Fact]
    public void AnEqualRevisionAndFingerprintOfAnotherSizeIsNotTheSameTagState()
    {
        MediaLibraryEntry[] all = TiedLibrary(4, 4);
        PrivateTagStoreSnapshot Snapshot(int tagged) => new(1, "fp", [Definition(One, "one")],
            all.Select(entry => entry.Thread).Distinct().Take(tagged).Select(thread => new PrivateThreadTagAssignment(One, thread)).ToArray(), []);
        MediaLibraryUserData Data(PrivateTagStoreSnapshot snapshot) => new(snapshot, Array.Empty<FavoriteEntry>(), null);

        MediaLibraryUserOrder order = MediaLibraryUserOrder.Of(all, Data(Snapshot(2)));
        Assert.True(order.IsFor(all, Data(Snapshot(2)))); // the same state read again
        Assert.False(order.IsFor(all, Data(Snapshot(3)))); // a placeholder fingerprint must not hide a change
    }

    [Fact]
    public void AGroupIsPlacedByItsBestRatingWhateverTheFilterKeepsAndWhenTheRatingChangesTheNextBuildFollows()
    {
        MediaLibraryEntry Make(string thread, int captured, char hash) => new(new ResourceId("s", "b", thread), "T" + thread, 1, Day, Day.AddHours(captured),
            "1", 1, null, 0, null, "/d/" + thread, "asset-" + thread, new string(hash, 64), 1, "m" + thread, "original", "f.png",
            SavedMediaKind.Image, MediaLibraryPresence.Present, false, false);
        MediaLibraryEntry a = Make("a", 1, 'a'), b = Make("b", 2, 'a'), c = Make("c", 3, 'b'), d = Make("d", 4, 'c');
        MediaLibraryEntry[] all = [a, b, c, d];
        MediaLibraryUserData Data(params (MediaLibraryEntry Entry, int Value)[] ratings) => new(null, Array.Empty<FavoriteEntry>(),
            ratings.Select(pair => new MediaRatingEntry(MediaLibraryFilterEngine.AssetKey(pair.Entry)!, pair.Value)).ToArray());
        string[] Threads(IEnumerable<MediaLibraryTile> tiles) => tiles.Select(tile => tile.Entry.Thread.Thread!).ToArray();
        MediaLibraryPresentIndex index = new(all);

        // a and b are the same bytes (rated 1 and 5); c is rated 3; d is unrated. By thread: d, c, b, a.
        MediaLibraryUserOrder first = MediaLibraryUserOrder.Of(all, Data((a, 1), (b, 5), (c, 3)));
        Assert.Equal(new[] { "b", "c", "d" }, Threads(index.Build(null, MediaLibrarySort.Rating, false, 0, null, default, MediaLibrarySortDirection.Default, first)));
        Assert.Equal(new[] { "c", "a", "d" }, Threads(index.Build(null, MediaLibrarySort.Rating, false, 0, null, default, MediaLibrarySortDirection.Reversed, first)));
        // The filter keeps only a (rated 1): its tile still counts the 5 of b.
        MediaLibraryEntry[] withoutB = [a, c, d];
        Assert.Equal(new[] { "a", "c", "d" }, Threads(index.Build(withoutB, MediaLibrarySort.Rating, false, 0, null, default, MediaLibrarySortDirection.Default, first)));
        Assert.Equal(new[] { "a", "c", "d" }, Threads(MediaLibraryGrouping.Build(all, withoutB, MediaLibrarySort.Rating, false, 0, null, MediaLibrarySortDirection.Default, first)));

        // b loses its rating: the group is now 1 and c (3) goes first. The same index, a new user order.
        MediaLibraryUserData after = Data((a, 1), (c, 3));
        Assert.False(first.IsFor(all, after));
        MediaLibraryUserOrder second = MediaLibraryUserOrder.Of(all, after);
        Assert.Equal(new[] { "c", "b", "d" }, Threads(index.Build(null, MediaLibrarySort.Rating, false, 0, null, default, MediaLibrarySortDirection.Default, second)));
        Assert.Equal(new[] { "c", "b", "d" }, Threads(MediaLibraryGrouping.Build(all, all, MediaLibrarySort.Rating, false, 0, null, MediaLibrarySortDirection.Default, second)));
    }

    [Fact]
    public void ReferencesCountsAnEntryTheCallerLeftOutOfAllTheSameWhicheverEntryWasAskedFirst()
    {
        MediaLibraryEntry Make(string thread, int captured, char hash, string name = "f.png") => new(new ResourceId("s", "b", thread), "T" + thread, 1, Day,
            Day.AddHours(captured), "1", 1, null, 0, null, "/d/" + thread, "asset-" + thread, new string(hash, 64), 1, "m" + thread, "original", name,
            SavedMediaKind.Image, MediaLibraryPresence.Present, false, false);
        // x: a1, a2, a3 (3 in all); y: y1..y4 (4 in all). The foreign entry has the bytes of x but is not part of all: its tile lists 4.
        MediaLibraryEntry a1 = Make("a1", 1, 'a'), a2 = Make("a2", 2, 'a'), a3 = Make("a3", 3, 'a');
        MediaLibraryEntry[] y = Enumerable.Range(1, 4).Select(index => Make("y" + index, 6 + index, 'b')).ToArray();
        MediaLibraryEntry[] all = [a1, a2, a3, .. y];
        MediaLibraryEntry foreign = a1 with { Name = "other.png" };
        MediaLibraryEntry[] passing = [foreign, a2, y[0]];
        Func<MediaLibraryEntry, int> counter = MediaLibraryGrouping.OccurrenceCounter(all);

        Assert.Equal(3, counter(a2));
        Assert.Equal(4, counter(foreign));
        Assert.Equal(3, counter(a2)); // asked again after the foreign entry: unchanged
        Assert.Equal(4, counter(y[0]));
        Func<MediaLibraryEntry, int> foreignFirst = MediaLibraryGrouping.OccurrenceCounter(all);
        Assert.Equal(4, foreignFirst(foreign));
        Assert.Equal(3, foreignFirst(a2)); // the extra occurrence belongs to the foreign entry, not to its bytes key

        // Equal counts (foreign 4, y1 4) fall back to thread order: y1 is the newer thread, then the foreign one, then a2 (3).
        foreach (IReadOnlyList<MediaLibraryEntry> permuted in new IReadOnlyList<MediaLibraryEntry>[]
            { passing, [a2, foreign, y[0]], [y[0], a2, foreign], [a2, y[0], foreign], [foreign, y[0], a2], [y[0], foreign, a2] })
        {
            Assert.Equal(new[] { "y1", "a1", "a2" }, MediaLibraryView.Select(permuted, MediaLibrarySort.References, true,
                references: MediaLibraryGrouping.OccurrenceCounter(all)).Select(entry => entry.Thread.Thread!));
            IReadOnlyList<MediaLibraryTile> tiles = MediaLibraryGrouping.Build(all, permuted, MediaLibrarySort.References, true);
            // The foreign entry and a2 are one bytes group, so one tile: the foreign one stands first and lists four occurrences.
            Assert.Equal(new[] { "y1", "a1" }, tiles.Select(tile => tile.Entry.Thread.Thread!));
            Assert.Same(foreign, tiles[1].Entry);
            Assert.Equal(new[] { 4, 4 }, tiles.Select(tile => tile.Occurrences.Count));
        }
    }

    [Fact]
    public void EntriesEqualInEveryKeyKeepTheOrderOfTheIndexOnBothPathsAndTheGeneralPathStaysStableByInput()
    {
        // The legacy contract, unchanged by the new sorts: entries equal in every sort key (here two lost loose files of one thread with
        // the same name) are ordered by input order in the general path (a stable sort) and by index position in the index. They agree for
        // a passing in index order, which is what the page's filter gives; a permuted passing with such ties is outside the contract.
        MediaLibraryEntry Lost(int hours) => new(new ResourceId("s", "b", "t"), "T", 1, Day, Day.AddHours(hours), null, null, null, null, null,
            null, null, null, null, "m" + hours, "original", "same.png", SavedMediaKind.Image, MediaLibraryPresence.Missing, true, false);
        MediaLibraryEntry x = Lost(1) with { LastCapturedAt = Day }, y = Lost(2) with { LastCapturedAt = Day };
        MediaLibraryEntry[] all = [x, y];
        MediaLibraryPresentIndex index = new(all);

        foreach (MediaLibrarySort sort in Enum.GetValues<MediaLibrarySort>())
            foreach (MediaLibrarySortDirection direction in Enum.GetValues<MediaLibrarySortDirection>())
            {
                IReadOnlyList<MediaLibraryTile> general = MediaLibraryGrouping.Build(all, all, sort, true, 0, null, direction);
                IReadOnlyList<MediaLibraryTile> indexed = index.Build(null, sort, true, 0, null, default, direction);
                Assert.Equal(2, general.Count);
                Assert.Equal(2, indexed.Count);
                AssertSame(general, indexed, sort + "/" + direction);
            }

        Assert.Equal(new[] { x, y }, MediaLibraryView.Select(all, MediaLibrarySort.ByThread, true));
        Assert.Equal(new[] { y, x }, MediaLibraryView.Select(new[] { y, x }, MediaLibrarySort.ByThread, true));
    }

    [Fact]
    public void AnEmptyIndexAndAnEmptyUserOrderAreFineForEverySort()
    {
        MediaLibraryEntry[] none = Array.Empty<MediaLibraryEntry>();
        MediaLibraryPresentIndex index = new(none);
        MediaLibraryUserOrder order = MediaLibraryUserOrder.Of(none, MediaLibraryUserData.Unavailable);
        Assert.False(order.TagsKnown);
        Assert.Equal(0, index.CountTiles(true));
        foreach (MediaLibrarySort sort in Enum.GetValues<MediaLibrarySort>())
            foreach (MediaLibrarySortDirection direction in Enum.GetValues<MediaLibrarySortDirection>())
            {
                Assert.Empty(index.Build(null, sort, true, 0, null, default, direction, order));
                Assert.Empty(index.Build(none, sort, false, 0, null, default, direction));
                Assert.Empty(MediaLibraryGrouping.Build(none, none, sort, true, 0, null, direction, order));
                Assert.Empty(MediaLibraryView.Select(none, sort, true, 0, direction, null, order));
            }
    }

    [Fact]
    public void AUserOrderOfAnotherIndexIsOnlyRefusedByTheSortsThatReadIt()
    {
        MediaLibraryEntry[] all = TiedLibrary(4, 4);
        MediaLibraryPresentIndex index = new(all);
        MediaLibraryUserOrder foreign = MediaLibraryUserOrder.Of(all.ToArray(), StateA(all));
        foreach (MediaLibrarySort sort in new[] { MediaLibrarySort.TagCount, MediaLibrarySort.Rating })
            Assert.Throws<ArgumentException>(() => index.Build(null, sort, true, 0, null, default, MediaLibrarySortDirection.Default, foreign));
        foreach (MediaLibrarySort sort in new[] { MediaLibrarySort.ByThread, MediaLibrarySort.Name, MediaLibrarySort.Size, MediaLibrarySort.References })
            AssertSame(index.Build(null, sort, true), index.Build(null, sort, true, 0, null, default, MediaLibrarySortDirection.Default, foreign), sort.ToString());
    }
}

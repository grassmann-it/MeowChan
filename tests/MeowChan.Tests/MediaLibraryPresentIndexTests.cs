using System.Diagnostics;
using System.Globalization;
using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Ratings;
using MeowChan.Tags;
using Xunit.Abstractions;

namespace MeowChan.Tests;

/// <summary>
/// The per-index presentation data (precomputed orders and groups) must give exactly what the general path gives: the page uses it
/// for large libraries, and a different order or grouping would be a wrong page, not a slow one.
/// </summary>
public sealed class MediaLibraryPresentIndexTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Day = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    // A library with every shape the keys look at: shared timestamps, equal bytes across threads, missing files, files without hash,
    // posts and loose files, repeated names, another board and source.
    private static MediaLibraryEntry[] Library(int threads, int perThread, int seed = 1)
    {
        Random random = new(seed);
        List<MediaLibraryEntry> entries = new();
        for (int t = 0; t < threads; t++)
        {
            ResourceId id = new(t % 7 == 0 ? "other" : "s", t % 3 == 0 ? "a" : "b", "t" + (t * 7 % threads).ToString("D4"));
            DateTimeOffset captured = Day.AddHours(random.Next(0, 6)); // many threads share a capture time
            for (int i = 0; i < perThread; i++)
            {
                bool missing = random.Next(10) == 0, loose = random.Next(8) == 0, hashless = random.Next(15) == 0;
                int shared = random.Next(0, Math.Max(2, threads * perThread / 6)); // equal bytes in several places
                string? sha = missing || hashless ? null : shared.ToString("x").PadLeft(64, '0');
                entries.Add(new MediaLibraryEntry(id, "T" + t, 1, Day, captured, loose ? null : (i / 2 + 1).ToString(), loose ? null : i / 2 + 1,
                    random.Next(3) == 0 ? Day.AddMinutes(random.Next(0, 40)) : null, loose ? null : i % 2, null,
                    missing ? null : "/d/" + t, missing ? null : "asset-" + t + "-" + i, sha, missing || hashless ? null : 1 + shared % 3,
                    "m" + i, "original", "f" + random.Next(0, 5) + ".png", SavedMediaKind.Image,
                    missing ? MediaLibraryPresence.Missing : MediaLibraryPresence.Present, loose, false));
            }
        }
        return entries.ToArray();
    }

    private static void AssertSame(IReadOnlyList<MediaLibraryTile> expected, IReadOnlyList<MediaLibraryTile> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Same(expected[i].Entry, actual[i].Entry);
            Assert.Equal(expected[i].Occurrences.Count, actual[i].Occurrences.Count);
            for (int j = 0; j < expected[i].Occurrences.Count; j++) Assert.Same(expected[i].Occurrences[j], actual[i].Occurrences[j]);
            Assert.Equal(expected[i].Favorite, actual[i].Favorite);
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void BuildGivesTheTilesOfTheGeneralPathForEveryFilterShape(int sortValue, bool includeMissing)
    {
        MediaLibrarySort sort = (MediaLibrarySort)sortValue;
        MediaLibraryEntry[] all = Library(40, 12);
        MediaLibraryPresentIndex index = new(all);
        Func<MediaLibraryEntry, bool> favorite = entry => entry.AssetId != null && entry.AssetId.EndsWith('3');
        IReadOnlyList<MediaLibraryEntry>[] passings =
        [
            all, // nothing filtered
            all.Where(entry => entry.Name == "f1.png").ToArray(),
            all.Where(entry => entry.Thread.Source == "other").ToArray(),
            all.Where((_, position) => position % 5 == 0).Reverse().ToArray(), // another input order must not matter
            Array.Empty<MediaLibraryEntry>()
        ];
        foreach (IReadOnlyList<MediaLibraryEntry> passing in passings)
        {
            AssertSame(MediaLibraryGrouping.Build(all, passing, sort, includeMissing, 11, favorite),
                index.Build(ReferenceEquals(passing, all) ? null : passing, sort, includeMissing, 11, favorite));
            AssertSame(MediaLibraryGrouping.Build(all, passing, sort, includeMissing, 11, favorite),
                index.Build(passing, sort, includeMissing, 11, favorite));
        }
        Assert.Equal(MediaLibraryGrouping.CountTiles(all, includeMissing), index.CountTiles(includeMissing));
    }

    // Tags on every third and fourth thread, a rating on about half of the saved files: tag counts and ratings of every shape, with equal
    // bytes in several threads so the group maximum matters.
    private static MediaLibraryUserData UserData(MediaLibraryEntry[] all, long revision = 1, int ratingShift = 0)
    {
        PrivateTagName.TryCreate("one", out PrivateTagName? one, out _);
        PrivateTagName.TryCreate("two", out PrivateTagName? two, out _);
        TagId first = new(Guid.Parse("00000000-0000-0000-0000-0000000000b1")), second = new(Guid.Parse("00000000-0000-0000-0000-0000000000b2"));
        ResourceId[] threads = all.Select(entry => entry.Thread).Distinct().ToArray();
        List<PrivateThreadTagAssignment> assigned = new();
        for (int i = 0; i < threads.Length; i++)
        {
            if (i % 3 == 0) assigned.Add(new PrivateThreadTagAssignment(first, threads[i]));
            if (i % 4 == 0) assigned.Add(new PrivateThreadTagAssignment(second, threads[i]));
        }
        List<MediaRatingEntry> ratings = new();
        for (int i = 0; i < all.Length; i++)
            if ((i + ratingShift) % 2 == 0 && MediaLibraryFilterEngine.AssetKey(all[i]) is { } key) ratings.Add(new MediaRatingEntry(key, 1 + i % 5));
        return new MediaLibraryUserData(new PrivateTagStoreSnapshot(revision, "fp" + revision,
            [new PrivateTagDefinition(first, one!), new PrivateTagDefinition(second, two!)], assigned, []), Array.Empty<FavoriteEntry>(), ratings.ToArray());
    }

    public static IEnumerable<object[]> SortsDirectionsAndMissing() =>
        from sort in Enum.GetValues<MediaLibrarySort>()
        from direction in Enum.GetValues<MediaLibrarySortDirection>()
        from includeMissing in new[] { false, true }
        select new object[] { sort, direction, includeMissing };

    [Theory]
    [MemberData(nameof(SortsDirectionsAndMissing))]
    public void EverySortInEveryDirectionGivesTheTilesOfTheGeneralPath(MediaLibrarySort sort, MediaLibrarySortDirection direction, bool includeMissing)
    {
        MediaLibraryEntry[] all = Library(40, 12, seed: 4);
        MediaLibraryPresentIndex index = new(all);
        MediaLibraryUserOrder userOrder = MediaLibraryUserOrder.Of(all, UserData(all));
        Func<MediaLibraryEntry, bool> favorite = entry => entry.AssetId != null && entry.AssetId.EndsWith('3');
        IReadOnlyList<MediaLibraryEntry>[] passings =
        [
            all,
            all.Where(entry => entry.Name == "f1.png").ToArray(),
            all.Where((_, position) => position % 5 == 0).ToArray(),
            Array.Empty<MediaLibraryEntry>()
        ];
        foreach (IReadOnlyList<MediaLibraryEntry> passing in passings)
        {
            IReadOnlyList<MediaLibraryTile> expected = MediaLibraryGrouping.Build(all, passing, sort, includeMissing, 11, favorite, direction, userOrder);
            AssertSame(expected, index.Build(ReferenceEquals(passing, all) ? null : passing, sort, includeMissing, 11, favorite,
                CancellationToken.None, direction, userOrder));
            AssertSame(expected, index.Build(passing, sort, includeMissing, 11, favorite, CancellationToken.None, direction, userOrder));
            // The same index serves the next call from its cache and still gives the same tiles.
            AssertSame(expected, index.Build(passing, sort, includeMissing, 11, favorite, CancellationToken.None, direction, userOrder));
        }
    }

    [Theory]
    [InlineData(MediaLibrarySort.Name)]
    [InlineData(MediaLibrarySort.Size)]
    [InlineData(MediaLibrarySort.References)]
    [InlineData(MediaLibrarySort.TagCount)]
    [InlineData(MediaLibrarySort.Rating)]
    public void AReversedDirectionIsTheMirrorOfTheKeyedTilesAndKeepsTheUnkeyedOnesLast(MediaLibrarySort sort)
    {
        MediaLibraryEntry[] all = Library(40, 12, seed: 8);
        MediaLibraryPresentIndex index = new(all);
        MediaLibraryUserOrder userOrder = MediaLibraryUserOrder.Of(all, UserData(all));
        IReadOnlyList<MediaLibraryTile> forward = index.Build(null, sort, true, 0, null, default, MediaLibrarySortDirection.Default, userOrder);
        IReadOnlyList<MediaLibraryTile> backward = index.Build(null, sort, true, 0, null, default, MediaLibrarySortDirection.Reversed, userOrder);
        Assert.Equal(forward.Count, backward.Count);
        Assert.Equal(forward.Select(tile => tile.Occurrences.Count).Sum(), backward.Select(tile => tile.Occurrences.Count).Sum());
        // Every tile stays one tile; only the places change, and a sort with several distinct keys really does move them.
        Assert.Equal(forward.SelectMany(tile => tile.Occurrences).Count(), backward.SelectMany(tile => tile.Occurrences).Count());
        Assert.NotEqual(forward.Select(tile => tile.Entry), backward.Select(tile => tile.Entry));
    }

    [Fact]
    public void TheNewKeysAreSortedAsTheDocumentedDefaultDirections()
    {
        MediaLibraryEntry[] all = Library(40, 12, seed: 2);
        MediaLibraryPresentIndex index = new(all);
        Func<MediaLibrarySort, IReadOnlyList<MediaLibraryTile>> build = sort => index.Build(null, sort, false);
        long?[] sizes = build(MediaLibrarySort.Size).Select(tile => tile.Entry.Length).ToArray();
        Assert.Equal(sizes.OrderByDescending(size => size), sizes);
        int[] references = build(MediaLibrarySort.References).Select(tile => tile.Occurrences.Count).ToArray();
        Assert.Equal(references.OrderByDescending(count => count), references);
        string?[] names = build(MediaLibrarySort.Name).Select(tile => tile.Entry.Name).ToArray();
        Assert.Equal(names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase), names);
    }

    [Fact]
    public void UserSortsWithoutAUserOrderKeepTheByThreadOrderAndAForeignUserOrderIsRefused()
    {
        MediaLibraryEntry[] all = Library(20, 8);
        MediaLibraryPresentIndex index = new(all);
        foreach (MediaLibrarySort sort in new[] { MediaLibrarySort.TagCount, MediaLibrarySort.Rating })
            foreach (MediaLibrarySortDirection direction in Enum.GetValues<MediaLibrarySortDirection>())
                AssertSame(MediaLibraryGrouping.Build(all, all, MediaLibrarySort.ByThread, true),
                    index.Build(null, sort, true, 0, null, default, direction));
        MediaLibraryUserOrder foreign = MediaLibraryUserOrder.Of(all.ToArray(), UserData(all));
        Assert.Throws<ArgumentException>(() => index.Build(null, MediaLibrarySort.TagCount, true, 0, null, default, MediaLibrarySortDirection.Default, foreign));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Build(null, (MediaLibrarySort)42, true));
    }

    [Fact]
    public void ANewUserOrderOfNewDataSortsAgainAndAnUnchangedOneIsReused()
    {
        MediaLibraryEntry[] all = Library(40, 12, seed: 6);
        MediaLibraryPresentIndex index = new(all);
        MediaLibraryUserOrder first = MediaLibraryUserOrder.Of(all, UserData(all, revision: 1, ratingShift: 0));
        MediaLibraryUserOrder second = MediaLibraryUserOrder.Of(all, UserData(all, revision: 2, ratingShift: 1));
        foreach (MediaLibrarySort sort in new[] { MediaLibrarySort.TagCount, MediaLibrarySort.Rating })
            foreach (MediaLibraryUserOrder order in new[] { first, second, first, second })
                AssertSame(MediaLibraryGrouping.Build(all, all, sort, true, 0, null, MediaLibrarySortDirection.Default, order),
                    index.Build(null, sort, true, 0, null, default, MediaLibrarySortDirection.Default, order));
    }

    [Fact]
    public void RunsOnSeveralThreadsWithDifferentSortsAndDirectionsOnOneIndexEachGiveTheirOwnResult()
    {
        MediaLibraryEntry[] all = Library(40, 12, seed: 3);
        MediaLibraryPresentIndex index = new(all);
        MediaLibraryUserOrder userOrder = MediaLibraryUserOrder.Of(all, UserData(all));
        (MediaLibrarySort Sort, MediaLibrarySortDirection Direction)[] runs =
        [
            (MediaLibrarySort.Name, MediaLibrarySortDirection.Default), (MediaLibrarySort.Name, MediaLibrarySortDirection.Reversed),
            (MediaLibrarySort.Size, MediaLibrarySortDirection.Reversed), (MediaLibrarySort.References, MediaLibrarySortDirection.Default),
            (MediaLibrarySort.TagCount, MediaLibrarySortDirection.Reversed), (MediaLibrarySort.Rating, MediaLibrarySortDirection.Default)
        ];
        IReadOnlyList<MediaLibraryTile>[] expected = runs.Select(run =>
            MediaLibraryGrouping.Build(all, all, run.Sort, true, 0, null, run.Direction, userOrder)).ToArray();
        using Barrier start = new(runs.Length);
        Task<IReadOnlyList<MediaLibraryTile>[]>[] workers = Enumerable.Range(0, runs.Length).Select(worker => Task.Run(() =>
        {
            start.SignalAndWait();
            List<IReadOnlyList<MediaLibraryTile>> results = new();
            for (int round = 0; round < 10; round++)
                results.Add(index.Build(null, runs[worker].Sort, true, 0, null, default, runs[worker].Direction, userOrder));
            return results.ToArray();
        })).ToArray();
        Task.WaitAll(workers);
        for (int worker = 0; worker < runs.Length; worker++)
            foreach (IReadOnlyList<MediaLibraryTile> built in workers[worker].Result) AssertSame(expected[worker], built);
    }

    [Fact]
    public void ARandomOrderFollowsItsSeedAndTheOtherSortsKeepTheirOrderAfterwards()
    {
        MediaLibraryEntry[] all = Library(30, 10, seed: 5);
        MediaLibraryPresentIndex index = new(all);
        foreach (int seed in new[] { 3, 4, 3 })
            AssertSame(MediaLibraryGrouping.Build(all, all, MediaLibrarySort.Random, true, seed), index.Build(null, MediaLibrarySort.Random, true, seed));
        AssertSame(MediaLibraryGrouping.Build(all, all, MediaLibrarySort.Newest, true), index.Build(null, MediaLibrarySort.Newest, true));
        AssertSame(MediaLibraryGrouping.Build(all, all, MediaLibrarySort.ByThread, false), index.Build(null, MediaLibrarySort.ByThread, false));
    }

    [Fact]
    public void RunsOnTwoThreadsWithDifferentSeedsAndSortsOnOneIndexEachGiveTheirOwnResult()
    {
        // A replaced presentation may still be running on the index when the next one starts (see MediaPage.Present).
        MediaLibraryEntry[] all = Library(40, 12, seed: 9);
        MediaLibraryPresentIndex index = new(all);
        (MediaLibrarySort Sort, int Seed)[] runs = [(MediaLibrarySort.Random, 1), (MediaLibrarySort.Random, 2), (MediaLibrarySort.Newest, 0), (MediaLibrarySort.ByThread, 0)];
        IReadOnlyList<MediaLibraryTile>[] expected = runs.Select(run => MediaLibraryGrouping.Build(all, all, run.Sort, true, run.Seed)).ToArray();
        using Barrier start = new(runs.Length);
        Task<IReadOnlyList<MediaLibraryTile>[]>[] workers = Enumerable.Range(0, runs.Length).Select(worker => Task.Run(() =>
        {
            start.SignalAndWait();
            List<IReadOnlyList<MediaLibraryTile>> results = new();
            for (int round = 0; round < 20; round++) results.Add(index.Build(null, runs[worker].Sort, true, runs[worker].Seed));
            return results.ToArray();
        })).ToArray();
        Task.WaitAll(workers);
        for (int worker = 0; worker < runs.Length; worker++)
            foreach (IReadOnlyList<MediaLibraryTile> built in workers[worker].Result) AssertSame(expected[worker], built);
    }

    [Fact]
    public void FavoritesThatCannotBeReadMakeEveryTileUnknownLikeTheGeneralPath()
    {
        MediaLibraryEntry[] all = Library(10, 6);
        Func<MediaLibraryEntry, bool> unreadable = _ => throw new MediaLibraryFilterException(MediaLibraryFilterError.FavoritesUnavailable);
        AssertSame(MediaLibraryGrouping.Build(all, all, MediaLibrarySort.ByThread, true, 0, unreadable),
            new MediaLibraryPresentIndex(all).Build(null, MediaLibrarySort.ByThread, true, 0, unreadable));
    }

    [Fact]
    public void AnEntryOutsideTheIndexIsHandledByTheGeneralPath()
    {
        MediaLibraryEntry[] all = Library(10, 6);
        MediaLibraryEntry[] passing = [all[3], all[1] with { Name = "elsewhere.png" }];
        AssertSame(MediaLibraryGrouping.Build(all, passing, MediaLibrarySort.ByThread, true),
            new MediaLibraryPresentIndex(all).Build(passing, MediaLibrarySort.ByThread, true));
    }

    [Fact]
    public void BuildStopsWhenCancelled()
    {
        MediaLibraryEntry[] all = Library(60, 400); // more entries than one cancellation check covers
        using CancellationTokenSource cancel = new();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => new MediaLibraryPresentIndex(all).Build(null, MediaLibrarySort.ByThread, true, 0, null, cancel.Token));
    }

    [Fact]
    public void TheShuffleKeyIsTheHashOfTheJoinedIdentityAsBefore()
    {
        // The key was once computed over string.Join("\u001f", ...); the allocation-free hash must not change any order.
        foreach (MediaLibraryEntry entry in Library(12, 6, seed: 9))
        {
            string identity = string.Join("\u001f", entry.Thread.Source, entry.Thread.Board, entry.Thread.Thread,
                entry.PostId, entry.Position, entry.Directory, entry.AssetId, entry.MediaId);
            ulong hash = 14695981039346656037UL ^ 77u;
            foreach (char character in identity) { hash ^= character; hash *= 1099511628211UL; }
            hash ^= hash >> 33; hash *= 0xff51afd7ed558ccdUL; hash ^= hash >> 33;
            Assert.Equal(hash, MediaLibraryView.StableKey(entry, 77));
        }
    }

    [Fact]
    public void TheGalleryWindowFromTheSavedPositionsIsTheOneOfTheGeneralPath()
    {
        MediaLibraryEntry[] shown = Library(8, 20, seed: 4);
        int[] present = MediaLibraryView.PresentIndex(shown);
        Assert.Equal(shown.Count(entry => entry.Presence == MediaLibraryPresence.Present), present.Length);
        foreach (int maximum in new[] { 1, 7, 50, 500 })
            for (int index = -1; index <= shown.Length; index += 3)
            {
                (IReadOnlyList<MediaLibraryEntry> window, int start) = MediaLibraryView.GalleryWindow(shown, present, index, maximum);
                // The reference answer: the saved entries around the chosen one, as before.
                List<int> saved = Enumerable.Range(0, shown.Length).Where(i => shown[i].Presence == MediaLibraryPresence.Present).ToList();
                if (index < 0 || index >= shown.Length || shown[index].Presence != MediaLibraryPresence.Present)
                {
                    Assert.Equal(-1, start);
                    continue;
                }
                int first = Math.Clamp(saved.IndexOf(index) - maximum / 2, 0, Math.Max(0, saved.Count - maximum));
                int[] taken = saved.Skip(first).Take(maximum).ToArray();
                Assert.Equal(taken.Select(i => shown[i]), window);
                Assert.Equal(Array.IndexOf(taken, index), start);
            }
    }

    [Fact]
    public void PickKeepsPageOrderForAscendingAndForScrambledPositions()
    {
        MediaLibraryEntry[] all = Library(5, 5);
        IReadOnlyList<MediaLibraryTile> tiles = new MediaLibraryPresentIndex(all).Build(null, MediaLibrarySort.ByThread, true);
        Assert.Equal(new[] { 1, 3, 4 }.Select(i => tiles[i]), MediaLibrarySelection.Pick(tiles, [1, 3, 4, 999]));
        Assert.Equal(new[] { 1, 3, 4 }.Select(i => tiles[i]), MediaLibrarySelection.Pick(tiles, [4, 1, 3, 1, -2]));
        Assert.Empty(MediaLibrarySelection.Pick(tiles, []));
    }

    // Before/after for the audit's 200,000-entry case: MEOWCHAN_MEDIA_SCALE_MICRO=1 dotnet test --filter Category=MediaScaleBenchmark
    [Fact]
    [Trait("Category", "MediaScaleBenchmark")]
    public void LargeLibraryBeforeAndAfter()
    {
        if (Environment.GetEnvironmentVariable("MEOWCHAN_MEDIA_SCALE_MICRO") != "1") return;
        int threads = 2000, perThread = int.TryParse(Environment.GetEnvironmentVariable("MEOWCHAN_PRESENT_PER_THREAD"), out int n) ? n : 100;
        MediaLibraryEntry[] all = Library(threads, perThread);
        Func<MediaLibraryEntry, bool> favorite = _ => false;
        double Time(Action action) { Stopwatch watch = Stopwatch.StartNew(); action(); return watch.Elapsed.TotalMilliseconds; }
        IReadOnlyList<MediaLibraryEntry> named = all.Where(entry => entry.Name == "f1.png").ToArray();
        string F(double ms) => ms.ToString("0", CultureInfo.InvariantCulture) + " ms";
        MediaLibraryPresentIndex? index = null;
        double indexBuild = Time(() => index = new MediaLibraryPresentIndex(all));
        output.WriteLine("MEDIA_PRESENT_BENCH entries=" + all.Length + " indexBuildOncePerSnapshot=" + F(indexBuild));
        foreach (MediaLibrarySort sort in Enum.GetValues<MediaLibrarySort>())
            foreach ((string name, IReadOnlyList<MediaLibraryEntry> passing) in new[] { ("all", (IReadOnlyList<MediaLibraryEntry>)all), ("named", named) })
            {
                double before = Time(() => MediaLibraryGrouping.Build(all, passing, sort, false, 17, favorite));
                double after = Time(() => index!.Build(passing, sort, false, 17, favorite)); // first use of a sort includes its order
                double again = Time(() => index!.Build(passing, sort, false, 17, favorite));
                output.WriteLine("MEDIA_PRESENT_BENCH sort=" + sort + " filter=" + name + " general=" + F(before) + " index(first)=" + F(after) + " index(next)=" + F(again));
            }
        output.WriteLine("MEDIA_PRESENT_BENCH countTiles general=" + F(Time(() => MediaLibraryGrouping.CountTiles(all, false))) + " index=" + F(Time(() => index!.CountTiles(false))));
    }
}

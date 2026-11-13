using System.Diagnostics;
using System.Globalization;
using MeowChan.Gallery;
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

using MeowChan.Gallery;

namespace MeowChan.Tests;

public sealed class MediaLibraryViewTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static MediaLibraryEntry Entry(string thread, int captured, long? ordinal, int? posted = null,
        bool missing = false, string? asset = null, int position = 0) =>
        new(new ResourceId("s", "b", thread), "T" + thread, 1, Day, Day.AddHours(captured),
            ordinal?.ToString(), ordinal, posted is int hours ? Day.AddHours(hours) : null, ordinal == null ? null : position, null,
            missing ? null : "/dir/" + thread, missing ? null : asset ?? thread + "-" + ordinal + "-" + position,
            missing ? null : new string('a', 64), missing ? null : 1, "m", "original", "f.png", SavedMediaKind.Image,
            missing ? MediaLibraryPresence.Missing : MediaLibraryPresence.Present, ordinal == null, false);

    [Fact]
    public void ByThreadKeepsThreadsTogetherNewestThreadFirstPostsInOrderLooseFilesLast()
    {
        MediaLibraryEntry[] input =
        [
            Entry("old", 1, 2), Entry("new", 5, null, asset: "loose"), Entry("new", 5, 3), Entry("old", 1, 1),
            Entry("new", 5, 1, position: 1), Entry("new", 5, 1, position: 0)
        ];

        string[] order = MediaLibraryView.Select(input, MediaLibrarySort.ByThread, includeMissing: true)
            .Select(entry => entry.Thread.Thread + ":" + (entry.PostId ?? "-") + ":" + entry.Position).ToArray();

        Assert.Equal(new[] { "new:1:0", "new:1:1", "new:3:0", "new:-:", "old:1:0", "old:2:0" }, order);
    }

    [Fact]
    public void NewestUsesPostTimeThenCaptureTime()
    {
        MediaLibraryEntry[] input = [Entry("a", 1, 1, posted: 10), Entry("b", 20, 1), Entry("c", 1, 1, posted: 30)];
        Assert.Equal(new[] { "c", "b", "a" }, MediaLibraryView.Select(input, MediaLibrarySort.Newest, true).Select(entry => entry.Thread.Thread!));
    }

    [Fact]
    public void RandomIsStableForASeedIndependentOfInputOrderAndChangesWithTheSeed()
    {
        MediaLibraryEntry[] input = Enumerable.Range(0, 40).Select(index => Entry("t" + index, index, 1)).ToArray();
        IReadOnlyList<MediaLibraryEntry> first = MediaLibraryView.Select(input, MediaLibrarySort.Random, true, seed: 7);
        IReadOnlyList<MediaLibraryEntry> again = MediaLibraryView.Select(input.Reverse(), MediaLibrarySort.Random, true, seed: 7);
        IReadOnlyList<MediaLibraryEntry> other = MediaLibraryView.Select(input, MediaLibrarySort.Random, true, seed: 8);

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        Assert.Equal(input.ToHashSet(), first.ToHashSet());
        Assert.NotEqual(MediaLibraryView.Select(input, MediaLibrarySort.ByThread, true), first);
    }

    [Fact]
    public void MissingEntriesAppearOnlyWhenRequested()
    {
        MediaLibraryEntry[] input = [Entry("a", 1, 1), Entry("a", 1, 2, missing: true)];
        Assert.Single(MediaLibraryView.Select(input, MediaLibrarySort.ByThread, includeMissing: false));
        Assert.Equal(2, MediaLibraryView.Select(input, MediaLibrarySort.ByThread, includeMissing: true).Count);
    }

    [Fact]
    public void GalleryWindowCentersOnTheChosenEntrySkipsMissingAndRespectsTheBound()
    {
        MediaLibraryEntry[] shown = Enumerable.Range(0, 30)
            .Select(index => Entry("t", 1, index, missing: index % 3 == 2)).ToArray();

        (IReadOnlyList<MediaLibraryEntry> window, int start) = MediaLibraryView.GalleryWindow(shown, 15, maximum: 6);
        Assert.Equal(6, window.Count);
        Assert.Same(shown[15], window[start]);
        Assert.All(window, entry => Assert.Equal(MediaLibraryPresence.Present, entry.Presence));
        Assert.Equal(window.OrderBy(entry => entry.PostOrdinal), window);

        (IReadOnlyList<MediaLibraryEntry> head, int headStart) = MediaLibraryView.GalleryWindow(shown, 0, maximum: 6);
        Assert.Equal((6, 0), (head.Count, headStart));
        (IReadOnlyList<MediaLibraryEntry> tail, int tailStart) = MediaLibraryView.GalleryWindow(shown, 28, maximum: 6);
        Assert.Same(shown[28], tail[tailStart]);
        Assert.Equal(6, tail.Count);

        Assert.Equal(-1, MediaLibraryView.GalleryWindow(shown, 2).Start); // a missing entry has no file to show
        Assert.Equal(-1, MediaLibraryView.GalleryWindow(shown, 99).Start);
        Assert.Empty(MediaLibraryView.GalleryWindow(Array.Empty<MediaLibraryEntry>(), 0).Window);
    }

    // One present entry per thread; captured hour = thread number, so the by-thread order is t5, t4, t3, t2, t1.
    private static MediaLibraryEntry One(int thread, string? name = "f.png", long? length = 1, string? hash = null) =>
        Entry("t" + thread, thread, 1) with { Name = name, Length = length, Sha256 = hash ?? new string('a', 64) };

    private static string[] Threads(IEnumerable<MediaLibraryEntry> entries) => entries.Select(entry => entry.Thread.Thread!).ToArray();

    [Fact]
    public void TheOriginalSortValuesKeepTheirNumbersAndTheNewOnesFollow()
    {
        Assert.Equal(new[] { 0, 1, 2 }, new[] { MediaLibrarySort.ByThread, MediaLibrarySort.Newest, MediaLibrarySort.Random }.Select(sort => (int)sort));
        Assert.Equal(new[] { 3, 4, 5, 6, 7, 8 }, new[]
        {
            MediaLibrarySort.Captured, MediaLibrarySort.Name, MediaLibrarySort.Size, MediaLibrarySort.References, MediaLibrarySort.TagCount, MediaLibrarySort.Rating
        }.Select(sort => (int)sort));
        Assert.Equal(0, (int)MediaLibrarySortDirection.Default);
    }

    [Fact]
    public void EverySortHasADefaultDirectionAndOnlyTheShuffleHasNoDirection()
    {
        foreach (MediaLibrarySort sort in Enum.GetValues<MediaLibrarySort>())
            Assert.Equal(sort != MediaLibrarySort.Random, MediaLibraryView.HasDirection(sort));
        Assert.False(MediaLibraryView.IsDescendingByDefault(MediaLibrarySort.Name));
        Assert.False(MediaLibraryView.IsDescendingByDefault(MediaLibrarySort.Random));
        foreach (MediaLibrarySort sort in new[] { MediaLibrarySort.ByThread, MediaLibrarySort.Newest, MediaLibrarySort.Captured,
            MediaLibrarySort.Size, MediaLibrarySort.References, MediaLibrarySort.TagCount, MediaLibrarySort.Rating })
            Assert.True(MediaLibraryView.IsDescendingByDefault(sort));
    }

    [Fact]
    public void AnUndefinedSortIsRefusedAndTheDefaultDirectionChangesNothing()
    {
        MediaLibraryEntry[] input = [One(1), One(2)];
        Assert.Throws<ArgumentOutOfRangeException>(() => MediaLibraryView.Select(input, (MediaLibrarySort)99, true));
        foreach (MediaLibrarySort sort in Enum.GetValues<MediaLibrarySort>())
            Assert.Equal(MediaLibraryView.Select(input, sort, true, 3), MediaLibraryView.Select(input, sort, true, 3, MediaLibrarySortDirection.Default));
    }

    [Fact]
    public void ReversedIsTheExactMirrorOfByThreadNewestAndCapturedAndCapturedIsByThread()
    {
        MediaLibraryEntry[] input =
        [
            Entry("old", 1, 2), Entry("new", 5, null, asset: "loose"), Entry("new", 5, 3), Entry("old", 1, 1),
            Entry("new", 5, 1, position: 1), Entry("new", 5, 1, position: 0), Entry("mid", 3, 1, posted: 40)
        ];
        foreach (MediaLibrarySort sort in new[] { MediaLibrarySort.ByThread, MediaLibrarySort.Newest, MediaLibrarySort.Captured })
        {
            IReadOnlyList<MediaLibraryEntry> forward = MediaLibraryView.Select(input, sort, true);
            Assert.Equal(Enumerable.Reverse(forward), MediaLibraryView.Select(input, sort, true, 0, MediaLibrarySortDirection.Reversed));
        }
        Assert.Equal(MediaLibraryView.Select(input, MediaLibrarySort.ByThread, true), MediaLibraryView.Select(input, MediaLibrarySort.Captured, true));
    }

    [Fact]
    public void NameSortsCaseInsensitiveThenOrdinalAndNamelessEntriesStayLastInBothDirections()
    {
        MediaLibraryEntry[] input = [One(1, "b.png"), One(2, "A.png"), One(3, "a.png"), One(4, null), One(5, "")];
        // By thread: t5, t4, t3, t2, t1. "A.png" and "a.png" are equal ignoring case; ordinal puts the capital first.
        Assert.Equal(new[] { "t2", "t3", "t1", "t5", "t4" }, Threads(MediaLibraryView.Select(input, MediaLibrarySort.Name, true)));
        Assert.Equal(new[] { "t1", "t3", "t2", "t5", "t4" },
            Threads(MediaLibraryView.Select(input, MediaLibrarySort.Name, true, 0, MediaLibrarySortDirection.Reversed)));
        // A total order does not depend on the order the input came in.
        Assert.Equal(MediaLibraryView.Select(input, MediaLibrarySort.Name, true), MediaLibraryView.Select(Enumerable.Reverse(input), MediaLibrarySort.Name, true));
    }

    [Fact]
    public void SizeSortsLargestFirstNeverSavedLastAndReversedIsSmallestFirstWithTheSameLastOnes()
    {
        MediaLibraryEntry[] input = [One(1, length: 5), One(2, length: 20), One(3, length: 20), One(4, length: null), One(5, length: 1)];
        Assert.Equal(new[] { "t3", "t2", "t1", "t5", "t4" }, Threads(MediaLibraryView.Select(input, MediaLibrarySort.Size, true))); // equal sizes: thread order
        Assert.Equal(new[] { "t5", "t1", "t2", "t3", "t4" },
            Threads(MediaLibraryView.Select(input, MediaLibrarySort.Size, true, 0, MediaLibrarySortDirection.Reversed)));
    }

    [Fact]
    public void EqualKeysFollowThreadOrderAndAReversedDirectionMirrorsThemToo()
    {
        MediaLibraryEntry[] input = Enumerable.Range(1, 6).Select(thread => One(thread)).ToArray(); // same name, same size
        string[] byThread = Threads(MediaLibraryView.Select(input, MediaLibrarySort.ByThread, true));
        foreach (MediaLibrarySort sort in new[] { MediaLibrarySort.Name, MediaLibrarySort.Size, MediaLibrarySort.References })
        {
            Assert.Equal(byThread, Threads(MediaLibraryView.Select(input, sort, true)));
            Assert.Equal(Enumerable.Reverse(byThread), Threads(MediaLibraryView.Select(input, sort, true, 0, MediaLibrarySortDirection.Reversed)));
        }
    }

    [Fact]
    public void ReferencesCountsOccurrencesOfTheSameBytesAndHashlessEntriesCountOne()
    {
        string x = new('a', 64), y = new('b', 64), z = new('c', 64);
        // by thread: tD, tC, tB2, tB1, tA3, tA2, tA1
        MediaLibraryEntry[] input =
        [
            Entry("tA1", 1, 1) with { Sha256 = x }, Entry("tA2", 2, 1) with { Sha256 = x }, Entry("tA3", 3, 1) with { Sha256 = x },
            Entry("tB1", 4, 1) with { Sha256 = y }, Entry("tB2", 5, 1) with { Sha256 = y }, Entry("tC", 6, 1) with { Sha256 = z },
            Entry("tD", 7, 1) with { Sha256 = null, Length = null }
        ];
        Assert.Equal(new[] { "tA3", "tA2", "tA1", "tB2", "tB1", "tD", "tC" }, Threads(MediaLibraryView.Select(input, MediaLibrarySort.References, true)));
        Assert.Equal(new[] { "tC", "tD", "tB1", "tB2", "tA1", "tA2", "tA3" },
            Threads(MediaLibraryView.Select(input, MediaLibrarySort.References, true, 0, MediaLibrarySortDirection.Reversed)));
        // Without a counter the bytes are counted among the entries given; a caller that knows more (the unfiltered index) passes its own.
        MediaLibraryEntry[] subset = [input[0], input[3]];
        Assert.Equal(new[] { "tB1", "tA1" }, Threads(MediaLibraryView.Select(subset, MediaLibrarySort.References, true)));
        Assert.Equal(new[] { "tA1", "tB1" }, Threads(MediaLibraryView.Select(subset, MediaLibrarySort.References, true,
            references: entry => entry.Thread.Thread == "tA1" ? 3 : 2)));
    }

    [Fact]
    public void ARandomOrderHasNoDirection()
    {
        MediaLibraryEntry[] input = Enumerable.Range(1, 30).Select(thread => One(thread)).ToArray();
        Assert.Equal(MediaLibraryView.Select(input, MediaLibrarySort.Random, true, 4),
            MediaLibraryView.Select(input, MediaLibrarySort.Random, true, 4, MediaLibrarySortDirection.Reversed));
    }

    [Fact]
    public void UserSortsWithoutUserDataKeepTheByThreadOrderInBothDirections()
    {
        MediaLibraryEntry[] input = [One(1), One(2), One(3)];
        foreach (MediaLibrarySort sort in new[] { MediaLibrarySort.TagCount, MediaLibrarySort.Rating })
            foreach (MediaLibrarySortDirection direction in Enum.GetValues<MediaLibrarySortDirection>())
                Assert.Equal(new[] { "t3", "t2", "t1" }, Threads(MediaLibraryView.Select(input, sort, true, 0, direction)));
    }

    [Fact]
    public void MissingEntriesStayOutOfEveryNewSortUnlessRequested()
    {
        MediaLibraryEntry[] input = [Entry("a", 1, 1), Entry("a", 1, 2, missing: true)];
        foreach (MediaLibrarySort sort in Enum.GetValues<MediaLibrarySort>())
        {
            Assert.Single(MediaLibraryView.Select(input, sort, includeMissing: false));
            Assert.Equal(2, MediaLibraryView.Select(input, sort, includeMissing: true).Count);
        }
    }
}

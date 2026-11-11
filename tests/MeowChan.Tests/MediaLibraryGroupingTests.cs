using MeowChan.Archive;
using MeowChan.Gallery;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.Tests;

public sealed class MediaLibraryGroupingTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid Rpt = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Cute = Guid.Parse("00000000-0000-0000-0000-0000000000a2");

    private static string Hash(char c) => new(c, 64);

    private static MediaLibraryEntry Entry(string thread, string post, char hash, int captured = 0, bool missing = false) =>
        new(new ResourceId("s", "b", thread), "T" + thread, 1, Day, Day.AddHours(captured),
            post, long.Parse(post), null, 0, "att-v1:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(thread + post))).ToLowerInvariant(),
            missing ? null : "/dir/" + thread, missing ? null : "asset-" + thread + post, missing ? null : Hash(hash), missing ? null : 1,
            "m" + post, "original", "f" + post + ".png", SavedMediaKind.Image,
            missing ? MediaLibraryPresence.Missing : MediaLibraryPresence.Present, false, false);

    private static PrivateTagDefinition Tag(Guid id, string name)
    {
        Assert.True(PrivateTagName.TryCreate(name, out PrivateTagName? tagName, out _));
        return new PrivateTagDefinition(new TagId(id), tagName!);
    }

    private static MediaLibraryUserData Data(IEnumerable<PrivateThreadTagAssignment>? threads = null, IEnumerable<PrivateMediaTagAssignment>? media = null) =>
        new(new PrivateTagStoreSnapshot(1, "fp", [Tag(Rpt, "rpt"), Tag(Cute, "cute")], threads ?? [], media ?? []), [], []);

    private static string[] Threads(IEnumerable<MediaLibraryEntry> entries) => entries.Select(entry => entry.Thread.Thread!).ToArray();

    [Fact]
    public void SameBytesInThreeThreadsAreOneTileWithThreeOccurrencesAndSeparateTags()
    {
        MediaLibraryEntry a = Entry("a", "1", 'a', 1), b = Entry("b", "1", 'a', 3), c = Entry("c", "1", 'a', 2), other = Entry("a", "2", 'b', 1);
        MediaLibraryEntry[] all = [a, b, c, other];
        // A tag on one occurrence's file and a tag on another's thread: nothing is merged or copied.
        MediaLibraryUserData data = Data(threads: [new(new TagId(Rpt), c.Thread)],
            media: [new(new TagId(Cute), MediaLibraryFilterEngine.TagTarget(b)!)]);

        IReadOnlyList<MediaLibraryTile> tiles = MediaLibraryGrouping.Build(all, all, MediaLibrarySort.ByThread, includeMissing: false);

        Assert.Equal(2, tiles.Count);
        MediaLibraryTile group = tiles.Single(tile => tile.IsGroup);
        Assert.Equal(3, group.Occurrences.Count);
        Assert.Equal(3, group.ThreadCount);
        Assert.Equal(new[] { "b", "c", "a" }, Threads(group.Occurrences)); // thread order: latest capture first
        Assert.Contains(group.Entry, group.Occurrences);
        Assert.Equal(new HashSet<Guid>(), data.TagsOf(a));
        Assert.Equal(new HashSet<Guid> { Cute }, data.TagsOf(b));
        Assert.Equal(new HashSet<Guid> { Rpt }, data.TagsOf(c));
        Assert.Single(tiles, tile => !tile.IsGroup && tile.Entry == other);
    }

    [Fact]
    public void OccurrencesWithoutAHashAreNeverGrouped()
    {
        // Two captured but never saved attachments and one saved file: the missing ones stay single tiles.
        MediaLibraryEntry[] all = [Entry("a", "1", 'a', missing: true), Entry("b", "1", 'a', missing: true), Entry("c", "1", 'a'), Entry("d", "1", 'a')];

        IReadOnlyList<MediaLibraryTile> tiles = MediaLibraryGrouping.Build(all, all, MediaLibrarySort.ByThread, includeMissing: true);

        Assert.Equal(3, tiles.Count);
        Assert.Equal(new[] { 1, 1, 2 }, tiles.Select(tile => tile.Occurrences.Count).Order());
        Assert.All(tiles.Where(tile => tile.Entry.Presence == MediaLibraryPresence.Missing), tile => Assert.Single(tile.Occurrences));
        Assert.Single(MediaLibraryGrouping.Build(all, all, MediaLibrarySort.ByThread, includeMissing: false));
        Assert.Equal(3, MediaLibraryGrouping.CountTiles(all, includeMissing: true));
        Assert.Equal(1, MediaLibraryGrouping.CountTiles(all, includeMissing: false));
    }

    [Fact]
    public void AGroupMatchesWhenOneOccurrenceMatchesAndStillKnowsAllOccurrences()
    {
        MediaLibraryEntry a = Entry("a", "1", 'a'), b = Entry("b", "1", 'a'), c = Entry("c", "1", 'b');
        MediaLibraryEntry[] all = [a, b, c];
        MediaLibraryUserData data = Data(threads: [new(new TagId(Rpt), b.Thread)]);

        IReadOnlyList<MediaLibraryEntry> passing = MediaLibraryFilterEngine.Apply(all, new(tags: new ArchiveTagFilter([Rpt], null)), data);
        IReadOnlyList<MediaLibraryTile> tiles = MediaLibraryGrouping.Build(all, passing, MediaLibrarySort.ByThread, includeMissing: false);

        MediaLibraryTile tile = Assert.Single(tiles);
        Assert.Equal(b, tile.Entry); // the shown occurrence is the one that matched
        Assert.Equal(2, tile.Occurrences.Count);
        Assert.Contains(a, tile.Occurrences);
        // An exclude filter hides only occurrences; the other occurrence keeps the tile alive.
        passing = MediaLibraryFilterEngine.Apply(all, new(tags: new ArchiveTagFilter(null, [Rpt])), data);
        tiles = MediaLibraryGrouping.Build(all, passing, MediaLibrarySort.ByThread, includeMissing: false);
        Assert.Equal(new[] { "a", "c" }, Threads(tiles.Select(item => item.Entry)).Order());
        Assert.Equal(2, tiles.Single(item => item.Entry == a).Occurrences.Count);
        // Nothing passing means no tile, however many occurrences exist.
        Assert.Empty(MediaLibraryGrouping.Build(all, Array.Empty<MediaLibraryEntry>(), MediaLibrarySort.ByThread, includeMissing: false));
    }

    [Fact]
    public void ThreadCountIsDistinctThreadsOverAllOccurrencesEvenWhenTwoShareAThread()
    {
        MediaLibraryEntry a1 = Entry("a", "1", 'a'), a2 = Entry("a", "2", 'a'), b = Entry("b", "1", 'a');
        MediaLibraryEntry[] all = [a1, a2, b];

        MediaLibraryTile whole = Assert.Single(MediaLibraryGrouping.Build(all, all, MediaLibrarySort.ByThread, false));
        Assert.Equal(3, whole.Occurrences.Count);
        Assert.Equal(2, whole.ThreadCount);

        // Filtered to one thread, the hint still counts every thread the bytes occur in.
        MediaLibraryFilter onlyA = new(thread: a1.Thread);
        MediaLibraryTile filtered = Assert.Single(MediaLibraryGrouping.Build(all,
            MediaLibraryFilterEngine.Apply(all, onlyA, MediaLibraryUserData.Unavailable), MediaLibrarySort.ByThread, false));
        Assert.Equal(2, filtered.ThreadCount);
        Assert.Equal(a1, filtered.Entry);

        MediaLibraryTile sameThread = Assert.Single(MediaLibraryGrouping.Build([a1, a2], [a1, a2], MediaLibrarySort.ByThread, false));
        Assert.Equal((2, 1), (sameThread.Occurrences.Count, sameThread.ThreadCount));
    }

    [Fact]
    public void SortingAndRandomWorkOnTilesWithOneTilePerContent()
    {
        MediaLibraryEntry[] all = Enumerable.Range(0, 30).Select(index => Entry("t" + index, "1", (char)('0' + index % 10), index)).ToArray();

        IReadOnlyList<MediaLibraryTile> first = MediaLibraryGrouping.Build(all, all, MediaLibrarySort.Random, false, seed: 5);
        IReadOnlyList<MediaLibraryTile> again = MediaLibraryGrouping.Build(all.Reverse(), all.Reverse(), MediaLibrarySort.Random, false, seed: 5);
        IReadOnlyList<MediaLibraryTile> other = MediaLibraryGrouping.Build(all, all, MediaLibrarySort.Random, false, seed: 6);

        Assert.Equal(10, first.Count);
        Assert.All(first, tile => Assert.Equal(3, tile.Occurrences.Count));
        Assert.Equal(first.Select(tile => tile.Entry), again.Select(tile => tile.Entry));
        Assert.NotEqual(first.Select(tile => tile.Entry), other.Select(tile => tile.Entry));

        // By thread, a tile sits where its first occurrence in that order sits: the latest captured thread.
        IReadOnlyList<MediaLibraryTile> byThread = MediaLibraryGrouping.Build(all, all, MediaLibrarySort.ByThread, false);
        Assert.Equal("t29", byThread[0].Entry.Thread.Thread);
        Assert.Equal(new[] { "t29", "t28", "t27" }, Threads(byThread.Take(3).Select(tile => tile.Entry)));
        Assert.Equal(10, MediaLibraryGrouping.CountTiles(all, false));
        // The window for the gallery is one entry per tile.
        (IReadOnlyList<MediaLibraryEntry> window, int start) = MediaLibraryView.GalleryWindow(byThread.Select(tile => tile.Entry).ToArray(), 4);
        Assert.Equal(10, window.Count);
        Assert.Same(byThread[4].Entry, window[start]);
    }
}

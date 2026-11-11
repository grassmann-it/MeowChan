using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.Tests;

// Pure rules behind the way back from "show in thread" to the Medien page: tiles are found again by identity.
public sealed class MediaLibraryReturnTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static string Hash(char c) => new(c, 64);

    private static MediaLibraryEntry Entry(string thread, string post, char hash, int captured = 0, bool missing = false) =>
        new(new ResourceId("s", "b", thread), "T" + thread, 1, Day, Day.AddHours(captured),
            post, long.Parse(post), null, 0,
            "att-v1:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(thread + post))).ToLowerInvariant(),
            missing ? null : "/dir/" + thread, missing ? null : "asset-" + thread + post, missing ? null : Hash(hash), missing ? null : 3,
            "m" + post, "original", "f" + thread + post + ".png", SavedMediaKind.Image,
            missing ? MediaLibraryPresence.Missing : MediaLibraryPresence.Present, false, false);

    private static IReadOnlyList<MediaLibraryTile> Tiles(IReadOnlyList<MediaLibraryEntry> all, IEnumerable<MediaLibraryEntry>? passing = null,
        MediaLibrarySort sort = MediaLibrarySort.ByThread) => MediaLibraryGrouping.Build(all, passing ?? all, sort, includeMissing: true);

    [Fact]
    public void TilesAreFoundAgainByIdentityAfterTheyMoved()
    {
        MediaLibraryEntry a = Entry("a", "1", 'a', 3), b = Entry("b", "1", 'b', 2), c = Entry("c", "1", 'c', 1), d = Entry("d", "1", 'd', 0);
        MediaLibraryEntry[] all = [a, b, c, d];
        IReadOnlyList<MediaLibraryTile> before = Tiles(all);
        MediaLibraryReturnPoint point = MediaLibraryReturn.Capture(c, [before[1], before[2]], before[1], -12);

        // A rebuild put a new file in front: every tile moved one position.
        MediaLibraryEntry first = Entry("0", "1", '0', 9);
        IReadOnlyList<MediaLibraryTile> after = Tiles([first, .. all]);
        MediaLibraryReturnMatch match = MediaLibraryReturn.Locate(point, after);

        Assert.Equal(c, after[match.Target].Entry);
        Assert.Equal(new[] { "b", "c" }, match.Selected.Select(index => after[index].Entry.Thread.Thread!));
        Assert.Equal(b, after[match.Anchor].Entry);
        Assert.Equal(-12, match.AnchorOffset);
    }

    [Fact]
    public void AnOccurrenceOfEqualBytesLeadsBackToItsOneTile()
    {
        MediaLibraryEntry shown = Entry("a", "1", 'e', 3), other = Entry("b", "1", 'e', 1), solo = Entry("c", "1", 'f', 2);
        IReadOnlyList<MediaLibraryTile> tiles = Tiles([shown, other, solo]);
        MediaLibraryTile group = tiles.Single(tile => tile.IsGroup);
        Assert.Equal(shown, group.Entry);

        // The occurrences window opened the other occurrence's thread.
        MediaLibraryReturnPoint point = MediaLibraryReturn.Capture(other, [group], group, 0);
        MediaLibraryReturnMatch match = MediaLibraryReturn.Locate(point, tiles);

        Assert.Same(group, tiles[match.Target]);
        Assert.Equal(other, point.Occurrence); // the occurrence is kept as opened, not merged into the shown one
        Assert.Equal(MediaLibraryTileKey.Of(group), MediaLibraryTileKey.Of(other));
    }

    [Fact]
    public void AGoneTileIsReportedAndNeverReplacedByItsNeighbor()
    {
        MediaLibraryEntry a = Entry("a", "1", 'a', 3), b = Entry("b", "1", 'b', 2), c = Entry("c", "1", 'c', 1);
        MediaLibraryEntry[] all = [a, b, c];
        IReadOnlyList<MediaLibraryTile> before = Tiles(all);
        MediaLibraryReturnPoint point = MediaLibraryReturn.Capture(b, [before[1]], before[0], -5);

        IReadOnlyList<MediaLibraryTile> after = Tiles(all, passing: [a, c]); // b no longer matches, c now stands at its position
        MediaLibraryReturnMatch match = MediaLibraryReturn.Locate(point, after);

        Assert.Equal(-1, match.Target);
        Assert.Empty(match.Selected);
        Assert.Equal(0, match.Anchor); // the view still goes back to where it stood
        Assert.Equal(-5, match.AnchorOffset);
    }

    [Fact]
    public void WithoutItsTopTileTheViewGoesToTheOpenedTileOrStays()
    {
        MediaLibraryEntry a = Entry("a", "1", 'a', 3), b = Entry("b", "1", 'b', 2), c = Entry("c", "1", 'c', 1);
        MediaLibraryEntry[] all = [a, b, c];
        IReadOnlyList<MediaLibraryTile> before = Tiles(all);

        MediaLibraryReturnMatch toTarget = MediaLibraryReturn.Locate(MediaLibraryReturn.Capture(c, [], before[0], -40), Tiles(all, passing: [b, c]));
        Assert.Equal(1, toTarget.Target);
        Assert.Equal(1, toTarget.Anchor);
        Assert.Equal(0, toTarget.AnchorOffset);

        MediaLibraryReturnMatch stays = MediaLibraryReturn.Locate(MediaLibraryReturn.Capture(c, [], before[0], -40), Tiles(all, passing: [b]));
        Assert.Equal((-1, -1), (stays.Target, stays.Anchor));

        MediaLibraryReturnMatch noAnchor = MediaLibraryReturn.Locate(MediaLibraryReturn.Capture(a, [], null, 0), before);
        Assert.Equal((0, 0), (noAnchor.Target, noAnchor.Anchor));
    }

    [Fact]
    public void AnUnsavedAttachmentIsItsOwnTile()
    {
        MediaLibraryEntry missing = Entry("a", "2", 'x', 1, missing: true), saved = Entry("a", "1", 'a', 2);
        IReadOnlyList<MediaLibraryTile> tiles = Tiles([saved, missing]);
        MediaLibraryReturnMatch match = MediaLibraryReturn.Locate(MediaLibraryReturn.Capture(missing, [], null, 0), tiles);
        Assert.Equal(missing, tiles[match.Target].Entry);
    }
}

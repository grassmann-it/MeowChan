using MeowChan.Archive;
using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.Tests;

/// <summary>Byte identity (hash case, length) and the favorite state of grouped Medien tiles.</summary>
public sealed class MediaLibraryGroupFavoriteTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid Rpt = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    private static string Hash(char c) => new(c, 64);

    private static MediaLibraryEntry Entry(string thread, string post, char hash, string name = "f.png", long length = 1,
        bool missing = false, bool noHash = false) =>
        new(new ResourceId("s", "b", thread), "T" + thread, 1, Day, Day,
            post, long.Parse(post), null, 0, "att-v1:" + hash + thread + post,
            missing ? null : "/dir/" + thread, missing ? null : "asset-" + thread + post, missing || noHash ? null : Hash(hash),
            missing ? null : length, "m" + post, "original", name, SavedMediaKind.Image,
            missing ? MediaLibraryPresence.Missing : MediaLibraryPresence.Present, false, false);

    private static MediaLibraryUserData Data(IEnumerable<MediaLibraryEntry> favorites,
        IEnumerable<PrivateThreadTagAssignment>? threads = null)
    {
        Assert.True(PrivateTagName.TryCreate("rpt", out PrivateTagName? name, out _));
        return new(new PrivateTagStoreSnapshot(1, "fp", [new PrivateTagDefinition(new TagId(Rpt), name!)], threads ?? [], []),
            favorites.Select(entry => new FavoriteEntry(MediaLibraryFilterEngine.AssetKey(entry)!, 1, entry.Name)).ToArray(), []);
    }

    private static IReadOnlyList<MediaLibraryTile> Tiles(IReadOnlyList<MediaLibraryEntry> all, IEnumerable<MediaLibraryEntry>? passing = null,
        Func<MediaLibraryEntry, bool>? isFavorite = null) =>
        MediaLibraryGrouping.Build(all, passing ?? all, MediaLibrarySort.ByThread, includeMissing: true, isFavorite: isFavorite);

    [Fact]
    public void DifferentBytesAreNeverMerged()
    {
        MediaLibraryEntry first = Entry("1", "1", 'a', "same.png"), revision = Entry("2", "1", 'b', "same.png"),
            sameHashOtherLength = Entry("3", "1", 'a', "same.png", length: 2),
            noHash1 = Entry("4", "1", 'a', noHash: true), noHash2 = Entry("5", "1", 'a', noHash: true),
            missing1 = Entry("6", "1", 'a', missing: true), missing2 = Entry("7", "1", 'a', missing: true);
        MediaLibraryEntry[] all = [first, revision, sameHashOtherLength, noHash1, noHash2, missing1, missing2];
        IReadOnlyList<MediaLibraryTile> tiles = Tiles(all);
        Assert.Equal(7, tiles.Count);
        Assert.All(tiles, tile => Assert.Single(tile.Occurrences));
        Assert.Equal(7, MediaLibraryGrouping.CountTiles(all, includeMissing: true));
    }

    [Fact]
    public void HashCaseDoesNotSplitAGroupButLengthDoes()
    {
        MediaLibraryEntry lower = Entry("1", "1", 'a'), upper = Entry("2", "1", 'a') with { Sha256 = Hash('a').ToUpperInvariant() };
        MediaLibraryTile group = Assert.Single(Tiles([lower, upper]));
        Assert.Equal(2, group.Occurrences.Count);
        Assert.Equal(1, MediaLibraryGrouping.CountTiles([lower, upper], true));
        Assert.Equal(2, Tiles([lower, Entry("3", "1", 'a', length: 2)]).Count);
        // The tile identity for the selection follows the same rule: hash case is irrelevant, length is not.
        Assert.Equal(MediaLibraryTileKey.Of(Tiles([lower])[0]), MediaLibraryTileKey.Of(Tiles([upper])[0]));
        Assert.NotEqual(MediaLibraryTileKey.Of(Tiles([lower])[0]), MediaLibraryTileKey.Of(Tiles([Entry("3", "1", 'a', length: 2)])[0]));
    }

    [Fact]
    public void FavoriteStateIsAllSomeOrNoneAcrossTheOccurrences()
    {
        MediaLibraryEntry a1 = Entry("1", "1", 'a'), a2 = Entry("2", "1", 'a'), b1 = Entry("3", "1", 'b'), b2 = Entry("4", "1", 'b'),
            c1 = Entry("5", "1", 'c'), c2 = Entry("6", "1", 'c'), solo = Entry("7", "1", 'd'), soloFav = Entry("8", "1", 'e');
        MediaLibraryEntry[] all = [a1, a2, b1, b2, c1, c2, solo, soloFav];
        MediaLibraryUserData data = Data([a1, a2, b1, soloFav]);
        IReadOnlyList<MediaLibraryTile> tiles = Tiles(all, isFavorite: data.IsFavorite);
        Assert.Equal(MediaLibraryFavoriteState.All, tiles.Single(t => t.Entry == a1 || t.Entry == a2).Favorite);
        Assert.Equal(MediaLibraryFavoriteState.Some, tiles.Single(t => t.Entry == b1 || t.Entry == b2).Favorite);
        Assert.Equal(MediaLibraryFavoriteState.None, tiles.Single(t => t.Entry == c1 || t.Entry == c2).Favorite);
        Assert.Equal(MediaLibraryFavoriteState.None, tiles.Single(t => t.Entry == solo).Favorite);
        Assert.Equal(MediaLibraryFavoriteState.All, tiles.Single(t => t.Entry == soloFav).Favorite);
    }

    [Fact]
    public void FavoriteStateIgnoresWhatTheFilterHid()
    {
        // b2 does not pass the filter, but it is saved and not a favorite: the tile must say "some", not "all".
        MediaLibraryEntry b1 = Entry("3", "1", 'b'), b2 = Entry("4", "1", 'b');
        MediaLibraryUserData data = Data([b1]);
        MediaLibraryTile tile = Assert.Single(Tiles([b1, b2], [b1], data.IsFavorite));
        Assert.Equal(MediaLibraryFavoriteState.Some, tile.Favorite);
        Assert.Equal(b1, tile.Entry);
    }

    [Fact]
    public void UnavailableFavoritesGiveUnknownInsteadOfNone()
    {
        MediaLibraryEntry a1 = Entry("1", "1", 'a'), a2 = Entry("2", "1", 'a'), lone = Entry("3", "1", 'z'), missing = Entry("4", "1", 'm', missing: true);
        Assert.All(Tiles([a1, a2, lone, missing], isFavorite: MediaLibraryUserData.Unavailable.IsFavorite),
            tile => Assert.Equal(MediaLibraryFavoriteState.Unknown, tile.Favorite));
        // Without any reader the state is unknown too.
        Assert.All(Tiles([a1, a2, lone]), tile => Assert.Equal(MediaLibraryFavoriteState.Unknown, tile.Favorite));
    }
}

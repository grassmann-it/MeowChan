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
}

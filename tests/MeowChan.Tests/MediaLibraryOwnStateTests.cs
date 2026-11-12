using MeowChan.Archive;
using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Ratings;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.Tests;

// OR-02: the media page, the gallery and the archive talk about the same media version, and the page shows the
// profile's own tags, favorites and ratings from the same stores without merging them.
public sealed class MediaLibraryOwnStateTests
{
    private static readonly ResourceId A = MediaLibraryIndexTests.A;
    private static readonly ResourceId B = MediaLibraryIndexTests.B;

    private static Task<IReadOnlyList<GalleryEntry>> ResolveAsync(string profile, IReadOnlyList<MediaLibraryEntry> entries) =>
        MediaLibraryGallery.ResolveAsync(entries, t => ArchiveReader.OpenAsync(profile, t), MediaAssetReader.OpenAsync);

    private static MediaLibraryUserData Data(PrivateTagStoreSnapshot? tags, IEnumerable<MediaLibraryEntry>? favorites = null,
        IEnumerable<(MediaLibraryEntry Entry, int Value)>? ratings = null) =>
        new(tags, favorites?.Select(entry => new FavoriteEntry(MediaLibraryFilterEngine.AssetKey(entry)!, 1, entry.Name)).ToArray(),
            ratings?.Select(pair => new MediaRatingEntry(MediaLibraryFilterEngine.AssetKey(pair.Entry)!, pair.Value)).ToArray());

    [Fact]
    public async Task OneMediumKeepsTheSameVersionAndIdentityOnMediaPageGalleryAndArchive()
    {
        using TempDirectory temp = new();
        await MediaLibraryIndexTests.SeedAsync(temp.Path);
        MediaLibrarySnapshot snapshot = await MediaLibraryIndexTests.BuildAsync(temp.Path);
        MediaLibraryEntry[] present = snapshot.Entries.Where(entry => entry.Presence == MediaLibraryPresence.Present).ToArray();

        IReadOnlyList<GalleryEntry> gallery = await ResolveAsync(temp.Path, present);

        Assert.Equal(present.Length, gallery.Count);
        // The archive reads a thread through the same selection the gallery uses; compare against it directly.
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(temp.Path, default);
        foreach ((MediaLibraryEntry entry, GalleryEntry opened) in present.Zip(gallery))
        {
            ArchivedThreadView view = (await reader.ReadThreadAsync(entry.Thread, default))!;
            List<MediaAssetSnapshot> manifests = new();
            foreach (string directory in await reader.GetMediaDirectoriesAsync(entry.Thread, default))
                manifests.Add(await MediaAssetReader.OpenAsync(directory));
            GalleryEntry archive = GallerySelection.Build(view, manifests, allSavedVersions: false, includeOtherMedia: true).Entries
                .Single(item => item.Asset.AssetId == entry.AssetId && item.Post?.PostId == entry.PostId);

            Assert.Equal(FavoriteAssetKey.From(opened.Key.Thread, opened.Asset), MediaLibraryFilterEngine.AssetKey(entry));
            Assert.Equal(MediaLibraryFilterEngine.AssetKey(entry), FavoriteAssetKey.From(archive.Key.Thread, archive.Asset));
            Assert.Equal(archive.Asset.Sha256.ToLowerInvariant(), entry.Sha256);
            PrivateMediaTagTarget? galleryTarget = GalleryMediaTagTarget.Resolve(opened, out _);
            Assert.Equal(galleryTarget, MediaLibraryFilterEngine.TagTarget(entry));
            Assert.Equal(galleryTarget, GalleryMediaTagTarget.Resolve(archive, out _));
            Assert.Equal(entry.Unassigned, galleryTarget == null);
        }
        Assert.Contains(present, entry => MediaLibraryFilterEngine.TagTarget(entry) != null);
    }

    [Fact]
    public async Task ATagChangeShowsConsistentlyAfterReloadAndNeverLeaksBetweenThreadAndMedia()
    {
        using TempDirectory temp = new();
        await MediaLibraryIndexTests.SeedAsync(temp.Path);
        MediaLibrarySnapshot snapshot = await MediaLibraryIndexTests.BuildAsync(temp.Path);
        MediaLibraryEntry a = snapshot.Entries.Single(entry => entry.Thread == A && entry.PostId == "100");
        MediaLibraryEntry b = snapshot.Entries.Single(entry => entry.Thread == B);
        MediaLibraryEntry loose = snapshot.Entries.Single(entry => entry.Unassigned);
        // Same bytes in both threads: a media tag belongs to the exact version, not to the content.
        Assert.Equal(a.Sha256, b.Sha256);

        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            PrivateTagStore store = new(temp.Path, lease);
            TagId cat = store.Define("cat").Tag, rpt = store.Define("rpt").Tag;
            store.SetMedia(cat, GalleryTarget(await ResolveAsync(temp.Path, [a])), true);
            store.SetThread(rpt, B, true);
        }

        // A new store instance stands for a reload from disk.
        PrivateTagStoreSnapshot reloaded;
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path)) reloaded = new PrivateTagStore(temp.Path, lease).ReadSnapshot();
        MediaLibraryUserData data = Data(reloaded, favorites: [], ratings: []);

        MediaLibraryEntryState stateA = MediaLibraryEntryState.Of(a, data), stateB = MediaLibraryEntryState.Of(b, data);
        Assert.Equal(new[] { "cat" }, stateA.MediaTags);
        Assert.Empty(stateA.ThreadTags);
        Assert.Empty(stateB.MediaTags); // The other copy of the same bytes is not tagged.
        Assert.Equal(new[] { "rpt" }, stateB.ThreadTags); // A thread tag stays a thread tag; no inheritance into media tags.
        Assert.True(stateA.CanEditMediaTags);

        // AP-01b replaces the former "no captured post version: cannot be tagged": a loose saved file is taggable as
        // its own local occurrence, starts without tags and never shows the tags of equal bytes or of its thread.
        MediaLibraryEntryState stateLoose = MediaLibraryEntryState.Of(loose, data);
        Assert.True(stateLoose.TagsKnown);
        Assert.True(stateLoose.CanEditMediaTags);
        Assert.IsType<MediaTagSubject.Local>(stateLoose.TagSubject);
        Assert.Empty(stateLoose.MediaTags);
    }

    private static PrivateMediaTagTarget GalleryTarget(IReadOnlyList<GalleryEntry> entries)
    {
        PrivateMediaTagTarget? target = GalleryMediaTagTarget.Resolve(Assert.Single(entries), out _);
        return Assert.IsType<PrivateMediaTagTarget>(target);
    }

    [Fact]
    public async Task FavoritesRatingsAndUnknownStatesStaySeparate()
    {
        using TempDirectory temp = new();
        await MediaLibraryIndexTests.SeedAsync(temp.Path);
        MediaLibrarySnapshot snapshot = await MediaLibraryIndexTests.BuildAsync(temp.Path);
        MediaLibraryEntry a = snapshot.Entries.Single(entry => entry.Thread == A && entry.PostId == "100");
        MediaLibraryEntry b = snapshot.Entries.Single(entry => entry.Thread == B);
        MediaLibraryEntry missing = snapshot.Entries.Single(entry => entry.Presence == MediaLibraryPresence.Missing && entry.PostId == "101");
        PrivateTagStoreSnapshot noTags = new(0, "fp", [], [], []);

        // Favorite without rating, and rating without favorite: neither implies the other.
        MediaLibraryEntryState favoriteOnly = MediaLibraryEntryState.Of(a, Data(noTags, favorites: [a], ratings: []));
        Assert.Equal((MediaLibraryFlag.Yes, true, (int?)null), (favoriteOnly.Favorite, favoriteOnly.RatingKnown, favoriteOnly.UserRating));
        MediaLibraryEntryState ratedOnly = MediaLibraryEntryState.Of(b, Data(noTags, favorites: [a], ratings: [(b, 4)]));
        Assert.Equal((MediaLibraryFlag.No, true, (int?)4), (ratedOnly.Favorite, ratedOnly.RatingKnown, ratedOnly.UserRating));

        // Unavailable stores are unknown, not "no" and not "unrated".
        MediaLibraryEntryState unknown = MediaLibraryEntryState.Of(a, Data(null));
        Assert.Equal((MediaLibraryFlag.Unknown, false, (int?)null, false), (unknown.Favorite, unknown.RatingKnown, unknown.UserRating, unknown.TagsKnown));
        Assert.False(unknown.CanEditMediaTags);

        // A file that was never saved cannot carry a favorite or rating; that is unknown as well.
        MediaLibraryEntryState notSaved = MediaLibraryEntryState.Of(missing, Data(noTags, favorites: [], ratings: []));
        Assert.Equal((MediaLibraryFlag.Unknown, false), (notSaved.Favorite, notSaved.RatingKnown));
    }

    [Fact]
    public async Task AGroupedTileShowsTheShownOccurrencesOwnStateAndTheGroupFavoriteSeparately()
    {
        using TempDirectory temp = new();
        await MediaLibraryIndexTests.SeedAsync(temp.Path);
        MediaLibrarySnapshot snapshot = await MediaLibraryIndexTests.BuildAsync(temp.Path);
        MediaLibraryEntry a = snapshot.Entries.Single(entry => entry.Thread == A && entry.PostId == "100");
        MediaLibraryEntry b = snapshot.Entries.Single(entry => entry.Thread == B);
        PrivateTagStoreSnapshot noTags = new(0, "fp", [], [], []);
        MediaLibraryUserData data = Data(noTags, favorites: [a], ratings: []);

        MediaLibraryTile tile = MediaLibraryGrouping.Build(snapshot.Entries, snapshot.Entries, MediaLibrarySort.ByThread, false, isFavorite: data.IsFavorite)
            .Single(item => item.IsGroup);

        Assert.Equal(2, tile.Occurrences.Count);
        Assert.Equal(MediaLibraryFavoriteState.Some, tile.Favorite);
        // The occurrence's own answer is not the group's answer.
        Assert.Equal(MediaLibraryFlag.Yes, MediaLibraryEntryState.Of(a, data).Favorite);
        Assert.Equal(MediaLibraryFlag.No, MediaLibraryEntryState.Of(b, data).Favorite);
    }
}

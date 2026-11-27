using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.FolderSources;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan.Tests;

public sealed class MediaContentBoundaryTests
{
    [Theory]
    [InlineData("g", (int)BooruPostClaimRating.General, (int)BooruContentReason.Allowed)]
    [InlineData("e", (int)BooruPostClaimRating.Explicit, (int)BooruContentReason.AboveRatingCeiling)]
    [InlineData("unrated", (int)BooruPostClaimRating.General, (int)BooruContentReason.UnknownRating)]
    [InlineData("s", (int)BooruPostClaimRating.General, (int)BooruContentReason.UnknownRating)]
    public void LocalRatingDecisionMatchesTheProviderDecision(string raw, int rating, int expected)
    {
        BooruPostClaim claim = new("danbooru", "123", BooruPostClaimPresence.Rating, Rating: (BooruPostClaimRating)rating, RawRating: raw);
        BooruContentProfileSnapshot profile = Profile(BooruPostClaimRating.Sensitive);
        MediaContentBoundary boundary = new(MediaContentBoundary.RatingFilter(profile)!, () => Catalog(claim));

        Assert.Equal((BooruContentReason)expected, profile.Filter.Evaluate(claim).Reason);
        Assert.Equal((BooruContentReason)expected, boundary.ReasonFor(claim.Identity));
    }

    [Fact]
    public void AHiddenOccurrenceCannotEnterAnEqualBytesTileOrItsGalleryWindow()
    {
        BooruPostClaim explicitPost = new("danbooru", "123", BooruPostClaimPresence.Rating, Rating: BooruPostClaimRating.Explicit, RawRating: "e");
        BooruPostClaim unclassified = new("danbooru", "124", BooruPostClaimPresence.None);
        MediaLibraryEntry blocked = Entry(BooruMediaIdentity.Thread("123"), "explicit") with { BooruPostId = "123" };
        MediaLibraryEntry unknown = Entry(BooruMediaIdentity.Thread("124"), "unknown") with { BooruPostId = "124" };
        MediaLibraryEntry chan = Entry(new ResourceId("4chan", "g", "42"), "chan");
        MediaLibraryEntry folder = Entry(FolderSourceIdentity.Thread(Guid.NewGuid()), "folder") with
        {
            FolderItemId = Guid.NewGuid().ToString("N"), Sha256 = null
        };
        MediaLibraryEntry[] all = [blocked, unknown, chan, folder];
        Assert.Null(MediaContentBoundary.RatingFilter(Profile(null)));

        MediaContentBoundary boundary = new(MediaContentBoundary.RatingFilter(Profile(BooruPostClaimRating.General))!,
            () => Catalog(explicitPost, unclassified));
        (IReadOnlyList<MediaLibraryEntry> visible, int hidden) = boundary.Apply(all);
        Assert.Equal(2, hidden);
        Assert.Equal(new[] { chan, folder }, visible);
        MediaLibraryPresentIndex index = new(visible);
        var tiles = index.Build(null, MediaLibrarySort.Random, includeMissing: false);
        Assert.All(tiles, tile => Assert.Single(tile.Occurrences));
        var window = MediaLibraryView.GalleryWindow(tiles.Select(tile => tile.Entry).ToArray(), 0);
        Assert.Equal(2, window.Window.Count);
        Assert.Contains(chan, window.Window);
        Assert.Contains(folder, window.Window);

        MediaContentBoundary raised = new(MediaContentBoundary.RatingFilter(Profile(BooruPostClaimRating.Explicit))!,
            () => Catalog(explicitPost, unclassified));
        Assert.Equal(new[] { blocked, chan, folder }, raised.Apply(all).Entries);
        Assert.Equal(BooruContentReason.UnknownRating, raised.ReasonFor(BooruPostKey.FromLegacy("danbooru", "999")));
    }

    private static BooruContentProfileSnapshot Profile(BooruPostClaimRating? ceiling)
    {
        BooruContentSettings settings = new(ceiling, string.Empty, []);
        return new(ceiling.HasValue ? BooruContentProfileState.Ready : BooruContentProfileState.Missing, settings, new(1, settings));
    }

    private static BooruCatalogSnapshot Catalog(params BooruPostClaim[] claims) =>
        new(1, claims.Select(claim => new BooruPostRecord(claim.SourceId, claim.PostId, claim, DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch)).ToArray(), []);

    private static MediaLibraryEntry Entry(ResourceId thread, string id) =>
        new(thread, id, 0, DateTimeOffset.UnixEpoch, null, null, null, null, null, null, "/fixture",
            id, new string('a', 64), 1, id, "original", id + ".png", SavedMediaKind.Image, MediaLibraryPresence.Present, true, false);
}

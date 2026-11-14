using MeowChan.Archive;
using MeowChan.Gallery;

namespace MeowChan.DesktopTests;

// Real local-admission functions; no provider, profile, renderer or window is constructed.
public sealed class BooruLocalMediaAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactPresentOriginalIsUsableEvenWhenAnotherPartOfTheIndexIsIncomplete(bool complete)
    {
        MediaLibrarySnapshot index = new([Entry("123")], [], [], complete);
        Assert.Equal(MediaPage.BooruOriginalAvailability.Present, MediaPage.SavedBooruOriginalAvailability(index, "123"));
    }

    [Fact]
    public void OnlyACompleteIndexCanEstablishThatAnOriginalIsMissing()
    {
        MediaLibrarySnapshot complete = new([Entry("1234")], [], [], true);
        MediaLibrarySnapshot partial = new([Entry("1234")], [], ["synthetic unreadable manifest"], false);
        Assert.Equal(MediaPage.BooruOriginalAvailability.Missing, MediaPage.SavedBooruOriginalAvailability(complete, "123"));
        Assert.Equal(MediaPage.BooruOriginalAvailability.Unavailable, MediaPage.SavedBooruOriginalAvailability(partial, "123"));
        Assert.Equal(MediaPage.BooruOriginalAvailability.Unavailable, MediaPage.SavedBooruOriginalAvailability(null, "123"));
    }

    [Fact]
    public void APartialIndexWithOnlyANonOriginalOrMissingFileDoesNotEstablishAbsence()
    {
        foreach (MediaLibraryEntry entry in new[] { Entry("123") with { Variant = "preview" },
                     Entry("123") with { Presence = MediaLibraryPresence.Missing } })
        {
            MediaLibrarySnapshot partial = new([entry], [], [], false);
            Assert.Equal(MediaPage.BooruOriginalAvailability.Unavailable, MediaPage.SavedBooruOriginalAvailability(partial, "123"));
        }
    }

    [Fact]
    public void CatalogNavigationReadsOnceAndOnlyKnownAbsenceReturnsFalse()
    {
        int reads = 0;
        var now = DateTimeOffset.UnixEpoch;
        BooruCatalogSnapshot catalog = new(1, [new BooruPostRecord("danbooru", "123",
            new BooruPostClaim("danbooru", "123", BooruPostClaimPresence.None), now, now, "asset")], []);
        Assert.True(MainWindow.CatalogHasSavedBooruPostForNavigation(() => { reads++; return catalog; }, "123"));
        Assert.Equal(1, reads);
        Assert.False(MainWindow.CatalogHasSavedBooruPostForNavigation(() => catalog, "1234"));
        Assert.False(MainWindow.CatalogHasSavedBooruPostForNavigation(() => new BooruCatalogSnapshot(1, [], []), "123"));
    }

    [Fact]
    public void UnreadableOrUnavailableCatalogDoesNotBecomeMissing()
    {
        Assert.Throws<InvalidOperationException>(() => MainWindow.CatalogHasSavedBooruPostForNavigation(() => null, "123"));
        foreach (Exception error in new Exception[] { new IOException("synthetic read failure"), new InvalidDataException("bad catalog"),
                     new UnauthorizedAccessException("denied"), new ObjectDisposedException("retired catalog") })
        {
            Exception? observed = Record.Exception(() => MainWindow.CatalogHasSavedBooruPostForNavigation(() => throw error, "123"));
            Assert.Same(error, observed); // The active workbench catches an action failure, never a false Missing.
        }
    }

    private static MediaLibraryEntry Entry(string post) => new(BooruMediaIdentity.Thread(post), "Post " + post, 0,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, null, null, null, null, "/synthetic/not-opened",
        "asset-" + post, new string('a', 64), 7, BooruMediaIdentity.MediaIdentifier(post), "original", "md5-original.jpg",
        SavedMediaKind.Image, MediaLibraryPresence.Present, true, false) { BooruPostId = post };
}

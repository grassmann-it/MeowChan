using MeowChan;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

// The thread Media tab's multi-selection (T3): the pure rules that hand the selected files to the media page's own
// helpers (tag dialog candidates, gallery entries, export) and the focus/lead choice of single-file actions.
[Collection("Localization")]
public sealed class ArchiveMediaSelectionTests
{
    private static readonly ResourceId Thread = new("fixture", "g", "42");
    private static readonly ArchiveThreadSummary Summary = new(Thread, "https://fixture.invalid/g/thread/42", "Fixture thread",
        "/not-opened/42", ThreadSourceState.Archived, true, DateTimeOffset.UnixEpoch, null, 4, 1);

    private static ArchiveAssetRow Row(string name, char hash)
    {
        string directory = Path.Combine(Path.GetTempPath(), "archive-media-selection-fixture");
        string sha = new(hash, 64);
        MediaAssetReference asset = new("asset-" + name, name, "original", name, Path.Combine(directory, name), 3, sha,
            "https://offline.invalid/" + name, OriginalFileName: name, IsCurrent: true);
        return new ArchiveAssetRow(new MediaAssetSnapshot(directory, MediaManifestStatus.Available, 3, "fixture", [asset]), asset);
    }

    private static GalleryEntry Entry(ArchiveAssetRow row, string? post = null) => new(ArchiveMediaSelection.KeyOf(Summary, row), row.Manifest, row.Asset,
        post == null ? null : new CapturedPost(post, long.Parse(post), PostFieldPresence.All), null, false, post == null) { ThreadUrl = Summary.Url };

    [Fact]
    public void SelectionComesBackInShownOrderEachOnceAndOnlyWhileShown()
    {
        ArchiveAssetRow a = Row("a.png", 'a'), b = Row("b.png", 'b'), c = Row("c.png", 'c'), hidden = Row("h.png", 'd');
        IReadOnlyList<ArchiveAssetRow> picked = ArchiveMediaSelection.InShownOrder([a, b, c], [c, hidden, a, c]);
        Assert.Equal([a, c], picked);
        Assert.Empty(ArchiveMediaSelection.InShownOrder([a, b, c], [hidden]));
        Assert.Empty(ArchiveMediaSelection.InShownOrder([a, b, c], []));
    }

    [Fact]
    public void MenuOffersSelectionActionsOnlyForAFileThatIsPartOfSeveral()
    {
        ArchiveAssetRow a = Row("a.png", 'a'), b = Row("b.png", 'b'), other = Row("o.png", 'o');
        Assert.True(ArchiveMediaSelection.IsSeveral([a, b], a));
        Assert.False(ArchiveMediaSelection.IsSeveral([a, b], other));
        Assert.False(ArchiveMediaSelection.IsSeveral([a], a));
        Assert.False(ArchiveMediaSelection.IsSeveral([a, b], null));
    }

    [Fact]
    public void SingleFileActionsUseTheFocusedFileWhileItIsSelectedThenTheListsPrimaryThenTheFirst()
    {
        ArchiveAssetRow a = Row("a.png", 'a'), b = Row("b.png", 'b'), c = Row("c.png", 'c');
        Assert.Same(b, ArchiveMediaSelection.Lead([a, b, c], b, a));
        Assert.Same(a, ArchiveMediaSelection.Lead([a, b], Row("x.png", 'x'), a));
        Assert.Same(a, ArchiveMediaSelection.Lead([a, b], null, null));
        Assert.Null(ArchiveMediaSelection.Lead([], b, a));
    }

    [Fact]
    public void TilesCarryEveryOccurrenceOfAFileWithThePostBoundOneShown()
    {
        ArchiveAssetRow a = Row("a.png", 'a'), b = Row("b.png", 'b');
        // a is shown by two posts and as a loose view; b only loose.
        GalleryEntry[] entries = [Entry(a), Entry(a, "7"), Entry(a, "9"), Entry(b)];
        IReadOnlyList<MediaLibraryTile> tiles = ArchiveMediaSelection.Tiles(Summary, [a, b], entries);
        Assert.Equal(2, tiles.Count);
        Assert.Equal("7", tiles[0].Entry.PostId);
        Assert.Equal(3, tiles[0].Occurrences.Count);
        Assert.Single(tiles[1].Occurrences);
        // The tag dialog lists every occurrence of both files, each once.
        Assert.Equal(4, MediaLibraryTagging.Candidates(tiles).Count);
        Assert.All(tiles.SelectMany(tile => tile.Occurrences), entry => Assert.Equal(MediaLibraryPresence.Present, entry.Presence));
    }

    [Fact]
    public void AFileTheGalleryBuildDidNotReturnIsStillOfferedButNeverTaggable()
    {
        ArchiveAssetRow a = Row("a.png", 'a');
        IReadOnlyList<MediaLibraryTile> tiles = ArchiveMediaSelection.Tiles(Summary, [a], []);
        MediaTagCandidate candidate = Assert.Single(MediaLibraryTagging.Candidates(tiles));
        Assert.Null(candidate.Subject);
        Assert.Single(ArchiveMediaSelection.GalleryEntries(Summary, [a], []));
    }

    [Fact]
    public void SelectionGalleryHasOneEntryPerFileInRowOrderStartingWithTheFirst()
    {
        ArchiveAssetRow a = Row("a.png", 'a'), b = Row("b.png", 'b');
        GalleryEntry[] entries = [Entry(b), Entry(a), Entry(a, "7")];
        IReadOnlyList<GalleryEntry> gallery = ArchiveMediaSelection.GalleryEntries(Summary, [a, b], entries);
        Assert.Equal(2, gallery.Count);
        Assert.Equal(a.Asset.AssetId, gallery[0].Asset.AssetId);
        Assert.Equal("7", gallery[0].Post?.PostId);
        Assert.Equal(b.Asset.AssetId, gallery[1].Asset.AssetId);
    }

    [Fact]
    public void StatusShowsFromTwoSelectedFilesOnInBothLanguages()
    {
        Assert.False(ArchiveMediaSelection.ShowsStatus(0));
        Assert.False(ArchiveMediaSelection.ShowsStatus(1));
        Assert.True(ArchiveMediaSelection.ShowsStatus(2));
        string before = Texts.Language;
        try
        {
            Texts.SetLanguage("de");
            Assert.Equal("3 ausgewählt", ArchiveMediaSelection.Status(3));
            Texts.SetLanguage("en");
            Assert.Equal("3 selected", ArchiveMediaSelection.Status(3));
        }
        finally { Texts.SetLanguage(before); }
    }
}

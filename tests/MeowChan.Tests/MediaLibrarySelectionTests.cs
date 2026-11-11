using MeowChan.Archive;
using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.Tests;

// Pure rules behind multi-select on the Medien page: what stays selected, and what the three actions reach.
public sealed class MediaLibrarySelectionTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static string Hash(char c) => new(c, 64);

    private static MediaLibraryEntry Entry(string thread, string post, char hash, int captured = 0, bool missing = false,
        string variant = "original", bool unassigned = false) =>
        new(new ResourceId("s", "b", thread), "T" + thread, 1, Day, Day.AddHours(captured),
            unassigned ? null : post, long.Parse(post), null, 0,
            unassigned ? null : "att-v1:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(thread + post))).ToLowerInvariant(),
            missing ? null : "/dir/" + thread, missing ? null : "asset-" + thread + post, missing ? null : Hash(hash), missing ? null : 3,
            "m" + post, variant, "f" + post + ".png", SavedMediaKind.Image,
            missing ? MediaLibraryPresence.Missing : MediaLibraryPresence.Present, unassigned, false);

    private static IReadOnlyList<MediaLibraryTile> Tiles(IReadOnlyList<MediaLibraryEntry> all, IEnumerable<MediaLibraryEntry>? passing = null,
        MediaLibrarySort sort = MediaLibrarySort.ByThread, bool includeMissing = true) =>
        MediaLibraryGrouping.Build(all, passing ?? all, sort, includeMissing);

    private static string[] Threads(IEnumerable<MediaLibraryTile> tiles) => tiles.Select(tile => tile.Entry.Thread.Thread!).ToArray();

    [Fact]
    public void ASelectedTileStaysSelectedWhileShownAndDropsWhenAFilterHidesIt()
    {
        MediaLibraryEntry a = Entry("a", "1", 'a', 3), b = Entry("b", "1", 'b', 2), c = Entry("c", "1", 'c', 1);
        MediaLibraryEntry[] all = [a, b, c];
        IReadOnlyList<MediaLibraryTile> before = Tiles(all);
        MediaLibraryTileKey[] selected = [MediaLibraryTileKey.Of(before[0]), MediaLibraryTileKey.Of(before[2])]; // a and c

        IReadOnlyList<MediaLibraryTile> filtered = Tiles(all, passing: [b, c]); // a is hidden now

        IReadOnlyList<int> kept = MediaLibrarySelection.Restore(selected, filtered);
        Assert.Equal(new[] { 1 }, kept); // only c is left, at its new position
        Assert.Equal(new[] { "c" }, Threads(MediaLibrarySelection.Pick(filtered, kept)));
        Assert.Empty(MediaLibrarySelection.Restore([], filtered));
    }

    [Fact]
    public void SelectionFollowsTheBytesWhenAnotherOccurrenceBecomesTheShownOne()
    {
        MediaLibraryEntry a = Entry("a", "1", 'e', 3), b = Entry("b", "1", 'e', 1), other = Entry("c", "1", 'f', 2);
        MediaLibraryEntry[] all = [a, b, other];
        IReadOnlyList<MediaLibraryTile> before = Tiles(all);
        MediaLibraryTile group = before.Single(tile => tile.IsGroup);
        Assert.Equal(a, group.Entry);

        IReadOnlyList<MediaLibraryTile> onlyB = Tiles(all, passing: [b]); // the filter now shows the group through b

        Assert.Equal(b, onlyB.Single().Entry);
        Assert.Equal(new[] { 0 }, MediaLibrarySelection.Restore([MediaLibraryTileKey.Of(group)], onlyB));
    }

    [Fact]
    public void UnsavedTilesAreKeyedByTheirOwnEntryAndNeverMergeByHash()
    {
        MediaLibraryEntry m1 = Entry("a", "1", 'a', missing: true), m2 = Entry("b", "1", 'a', missing: true);
        IReadOnlyList<MediaLibraryTile> tiles = Tiles([m1, m2]);

        Assert.NotEqual(MediaLibraryTileKey.Of(tiles[0]), MediaLibraryTileKey.Of(tiles[1]));
        Assert.Equal(new[] { 1 }, MediaLibrarySelection.Restore([MediaLibraryTileKey.Of(tiles[1])], tiles));
    }

    [Fact]
    public void PickGivesPageOrderAndIgnoresPositionsThatAreGone()
    {
        IReadOnlyList<MediaLibraryTile> tiles = Tiles([Entry("a", "1", 'a', 3), Entry("b", "1", 'b', 2), Entry("c", "1", 'c', 1)]);

        Assert.Equal(new[] { "a", "c" }, Threads(MediaLibrarySelection.Pick(tiles, [2, 0, 0, 7, -1])));
    }

    [Fact]
    public void TheGalleryGetsOneEntryPerTileAndCountsTilesWithoutAFile()
    {
        MediaLibraryEntry a = Entry("a", "1", 'e', 3), b = Entry("b", "1", 'e', 1), other = Entry("c", "1", 'f', 2), gone = Entry("d", "1", 'd', missing: true);
        IReadOnlyList<MediaLibraryTile> tiles = Tiles([a, b, other, gone]); // a and b are one tile

        (IReadOnlyList<MediaLibraryEntry> entries, int notSaved) = MediaLibrarySelection.GalleryEntries(tiles);

        Assert.Equal(3, tiles.Count);
        Assert.Equal(new[] { a, other }, entries); // the shown occurrence of the group, not both
        Assert.Equal(1, notSaved);
    }

    [Fact]
    public void CandidatesListEveryOccurrenceOfEveryTileAndNameWhyOneCannotBeTagged()
    {
        MediaLibraryEntry a = Entry("a", "1", 'e', 3), b = Entry("b", "1", 'e', 1), loose = Entry("c", "1", 'f', 2, unassigned: true), gone = Entry("d", "1", 'd', missing: true);
        IReadOnlyList<MediaLibraryTile> tiles = Tiles([a, b, loose, gone]);

        IReadOnlyList<MediaTagCandidate> candidates = MediaLibraryTagging.Candidates(tiles);

        Assert.Equal(4, candidates.Count);
        Assert.Equal(new[] { MediaTagBlock.None, MediaTagBlock.None, MediaTagBlock.NoPostVersion, MediaTagBlock.NotSaved }, candidates.Select(c => c.Block));
        Assert.All(candidates.Where(c => c.Block == MediaTagBlock.None), c => Assert.NotNull(c.Target));
        Assert.All(candidates.Where(c => c.Block != MediaTagBlock.None), c => Assert.Null(c.Target));
    }

    [Fact]
    public void GroupedTileWithOneOccurrenceDeselectedIsTaggedAndUntaggedOnTheRestOnly()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaLibraryEntry a = Entry("a", "1", 'e', 3), b = Entry("b", "1", 'e', 2), c = Entry("c", "1", 'e', 1), single = Entry("d", "1", 'f', 0);
        IReadOnlyList<MediaLibraryTile> tiles = Tiles([a, b, c, single]);
        IReadOnlyList<MediaTagCandidate> candidates = MediaLibraryTagging.Candidates(tiles);
        TagId cat = store.Define("cat").Tag, rpt = store.Define("rpt").Tag;
        store.SetThread(rpt, c.Thread, true); // a thread tag must stay untouched by everything below
        MediaTagCandidate deselected = candidates.Single(x => x.Entry == b);
        MediaTagCandidate[] chosen = candidates.Where(x => x != deselected).ToArray();

        IReadOnlyList<PrivateMediaTagTarget> toAdd = MediaLibraryTagging.TargetsToAdd(store.ReadSnapshot(), cat, chosen);
        PrivateTagCommit added = store.SetMediaBatch(cat, toAdd, true);

        Assert.Equal(3, toAdd.Count);
        Assert.Equal(cat, Assert.Single(added.Snapshot.TagsOf(MediaLibraryFilterEngine.TagTarget(a)!)).Id);
        Assert.Empty(added.Snapshot.TagsOf(MediaLibraryFilterEngine.TagTarget(b)!));
        Assert.Equal(cat, Assert.Single(added.Snapshot.TagsOf(MediaLibraryFilterEngine.TagTarget(c)!)).Id);
        Assert.Equal(cat, Assert.Single(added.Snapshot.TagsOf(MediaLibraryFilterEngine.TagTarget(single)!)).Id);
        Assert.Equal(rpt, Assert.Single(added.Snapshot.TagsOf(c.Thread)).Id);

        // Choosing the already tagged ones again offers nothing; removing reaches only chosen occurrences that carry the tag.
        Assert.Empty(MediaLibraryTagging.TargetsToAdd(added.Snapshot, cat, chosen));
        store.SetMedia(cat, MediaLibraryFilterEngine.TagTarget(b)!, true); // b gets the tag on its own later
        IReadOnlyList<PrivateMediaTagTarget> toRemove = MediaLibraryTagging.TargetsToRemove(store.ReadSnapshot(), cat, chosen.Where(x => x.Entry != c));
        PrivateTagCommit removed = store.SetMediaBatch(cat, toRemove, false);

        Assert.Equal(2, toRemove.Count); // a and single; b was deselected, c was not chosen for removal
        Assert.Empty(removed.Snapshot.TagsOf(MediaLibraryFilterEngine.TagTarget(a)!));
        Assert.Empty(removed.Snapshot.TagsOf(MediaLibraryFilterEngine.TagTarget(single)!));
        Assert.Equal(cat, Assert.Single(removed.Snapshot.TagsOf(MediaLibraryFilterEngine.TagTarget(b)!)).Id);
        Assert.Equal(cat, Assert.Single(removed.Snapshot.TagsOf(MediaLibraryFilterEngine.TagTarget(c)!)).Id);
        Assert.Equal(rpt, Assert.Single(removed.Snapshot.TagsOf(c.Thread)).Id);
    }

    // A manifest view with the given saved files; the export reads assets from it and never touches the disk.
    private static MediaAssetSnapshot Manifest(string directory, params MediaLibraryEntry[] files)
    {
        MediaAssetReference[] assets = files.Select(entry => new MediaAssetReference(entry.AssetId!, entry.Name!, entry.Variant, entry.Name!,
            Path.Combine(directory, entry.Name!), entry.Length!.Value, entry.Sha256!, "https://offline.invalid/" + entry.Name, entry.Name!, true)
        { MediaIdentifier = entry.MediaId! }).ToArray();
        return new MediaAssetSnapshot(directory, MediaManifestStatus.Available, 3, "fixture", assets);
    }

    [Fact]
    public async Task ExportOffersOneOriginalPerTileFromTheShownOccurrence()
    {
        MediaLibraryEntry a = Entry("a", "1", 'e', 3), b = Entry("b", "1", 'e', 1), other = Entry("c", "2", 'f', 2);
        IReadOnlyList<MediaLibraryTile> tiles = Tiles([a, b, other]);
        Dictionary<string, MediaAssetSnapshot> manifests = new()
        {
            [a.Directory!] = Manifest(a.Directory!, a), [b.Directory!] = Manifest(b.Directory!, b), [other.Directory!] = Manifest(other.Directory!, other)
        };
        List<string> opened = new();

        MediaLibraryExportPlan plan = await MediaLibraryExport.BuildAsync(tiles, (directory, _) => { opened.Add(directory); return Task.FromResult(manifests[directory]); });

        Assert.Equal(0, plan.Unavailable);
        Assert.Equal(2, plan.Assets.Count); // three occurrences, two tiles
        Assert.Equal(new[] { a.Thread, other.Thread }, plan.Assets.Select(asset => asset.Key.Thread));
        Assert.Equal(new[] { Hash('e'), Hash('f') }, plan.Assets.Select(asset => asset.Key.Sha256));
        Assert.DoesNotContain(b.Directory!, opened); // the duplicate's folder is never read
    }

    [Fact]
    public async Task ExportCountsTilesThatHaveNoOriginalToOfferInsteadOfDroppingThemSilently()
    {
        MediaLibraryEntry ok = Entry("a", "1", 'e'), gone = Entry("b", "1", 'd', missing: true), preview = Entry("c", "1", '1', variant: "preview"),
            changed = Entry("d", "1", '2'), unreadable = Entry("e", "1", '3');
        IReadOnlyList<MediaLibraryTile> tiles = Tiles([ok, gone, preview, changed, unreadable]);
        MediaAssetSnapshot differing = Manifest(changed.Directory!, changed with { Sha256 = Hash('4') }); // the bytes changed since indexing

        MediaLibraryExportPlan plan = await MediaLibraryExport.BuildAsync(tiles, (directory, _) =>
            directory == ok.Directory ? Task.FromResult(Manifest(directory, ok))
            : directory == preview.Directory ? Task.FromResult(Manifest(directory, preview))
            : directory == changed.Directory ? Task.FromResult(differing)
            : throw new IOException("unreadable"));

        Assert.Equal(new[] { ok.Thread }, plan.Assets.Select(asset => asset.Key.Thread));
        Assert.Equal(4, plan.Unavailable); // not saved, preview only, changed bytes, unreadable folder
    }
}

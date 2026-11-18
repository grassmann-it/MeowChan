using System.Diagnostics;
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
        Assert.All(candidates.Where(c => c.Block == MediaTagBlock.None), c => Assert.NotNull(c.Subject));
        Assert.All(candidates.Where(c => c.Block != MediaTagBlock.None), c => Assert.Null(c.Subject));
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

        IReadOnlyList<MediaTagSubject> toAdd = MediaLibraryTagging.TargetsToAdd(store.ReadSnapshot(), cat, chosen);
        PrivateTagCommit added = store.SetMediaSubjects(cat, toAdd.ToArray(), true);

        Assert.Equal(3, toAdd.Count);
        Assert.Equal(cat, Assert.Single(added.Snapshot.TagsOf(MediaLibraryFilterEngine.TagTarget(a)!)).Id);
        Assert.Empty(added.Snapshot.TagsOf(MediaLibraryFilterEngine.TagTarget(b)!));
        Assert.Equal(cat, Assert.Single(added.Snapshot.TagsOf(MediaLibraryFilterEngine.TagTarget(c)!)).Id);
        Assert.Equal(cat, Assert.Single(added.Snapshot.TagsOf(MediaLibraryFilterEngine.TagTarget(single)!)).Id);
        Assert.Equal(rpt, Assert.Single(added.Snapshot.TagsOf(c.Thread)).Id);

        // Choosing the already tagged ones again offers nothing; removing reaches only chosen occurrences that carry the tag.
        Assert.Empty(MediaLibraryTagging.TargetsToAdd(added.Snapshot, cat, chosen));
        store.SetMedia(cat, MediaLibraryFilterEngine.TagTarget(b)!, true); // b gets the tag on its own later
        IReadOnlyList<MediaTagSubject> toRemove = MediaLibraryTagging.TargetsToRemove(store.ReadSnapshot(), cat, chosen.Where(x => x.Entry != c));
        PrivateTagCommit removed = store.SetMediaSubjects(cat, toRemove.ToArray(), false);

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

    // Facts a Present original must carry before any manifest is opened: each broken entry is one tile with nothing to offer.
    private static MediaLibraryEntry[] BrokenOriginals() =>
    [
        Entry("n1", "1", 'a') with { Directory = null },
        Entry("n2", "1", 'b') with { Directory = "" },
        Entry("n3", "1", 'c') with { Directory = "  " },
        Entry("n4", "1", 'd') with { AssetId = null },
        Entry("n5", "1", 'e') with { AssetId = "" },
        Entry("n6", "1", '1') with { AssetId = " \t" },
        Entry("n7", "1", '2') with { MediaId = null },
        Entry("n8", "1", '3') with { MediaId = " " },
        Entry("n9", "1", '4') with { Sha256 = null, Length = null },
        Entry("n10", "1", '5') with { Sha256 = "" },
        Entry("n11", "1", '6') with { Variant = "preview" },
        Entry("n12", "1", '7') with { Variant = "Original" }, // the variant is an exact name, not a case-insensitive one
        Entry("n13", "1", '8', missing: true) with { Directory = "/dir/n13", AssetId = "asset-n13", Sha256 = Hash('8'), MediaId = "m1" } // flagged Missing
    ];

    [Fact]
    public async Task MeasureAndPlanAgreeOnEntriesWithInvalidOriginalFactsAndNeverPassBlankKeysToTheManifestReader()
    {
        MediaLibraryEntry good = Entry("ok", "1", '9');
        MediaLibraryEntry[] broken = BrokenOriginals();
        IReadOnlyList<MediaLibraryTile> tiles = Tiles([good, .. broken]);
        Assert.Equal(broken.Length + 1, tiles.Count); // every entry is its own tile: nothing is grouped away
        List<string?> opened = new();

        MediaLibraryExportSize size = MediaLibraryExport.Measure(tiles);
        MediaLibraryExportPlan plan = await MediaLibraryExport.BuildAsync(tiles, (directory, _) =>
        {
            opened.Add(directory);
            return Task.FromResult(Manifest(directory, good));
        });

        Assert.Equal(new MediaLibraryExportSize(1, 3, broken.Length), size);
        Assert.Equal(size.Files, plan.Assets.Count);
        Assert.Equal(size.NotOffered, plan.Unavailable);
        Assert.Equal(new[] { good.Thread }, plan.Assets.Select(asset => asset.Key.Thread));
        Assert.Equal(new[] { good.Directory }, opened); // the one valid original; no null, blank or foreign folder reached the reader
        Assert.DoesNotContain(opened, directory => string.IsNullOrWhiteSpace(directory));
    }

    [Fact]
    public async Task AnInvalidOriginalNeverDrawsAnotherFileOfTheSameBytesOrFolder()
    {
        // Same bytes in two threads are one tile; the shown occurrence is the first (by thread order). When its own facts are
        // invalid the plan must not fall back to the twin's perfectly good file.
        MediaLibraryEntry shown = Entry("a", "1", 'e') with { AssetId = null };
        MediaLibraryEntry twin = Entry("b", "1", 'e');
        IReadOnlyList<MediaLibraryTile> tiles = Tiles([shown, twin]);
        Assert.Equal(shown, Assert.Single(tiles).Entry);
        List<string> opened = new();

        MediaLibraryExportSize size = MediaLibraryExport.Measure(tiles);
        MediaLibraryExportPlan plan = await MediaLibraryExport.BuildAsync(tiles, (directory, _) => { opened.Add(directory); return Task.FromResult(Manifest(directory, twin)); });

        Assert.Equal(new MediaLibraryExportSize(0, 0, 1), size);
        Assert.Empty(plan.Assets);
        Assert.Equal(1, plan.Unavailable);
        Assert.Empty(opened);
    }

    [Fact]
    public async Task AManifestThatDoesNotHoldTheIndexedOriginalIsUnavailableAndNothingElseIsChosen()
    {
        MediaLibraryEntry entry = Entry("a", "1", 'a'), mediaMoved = Entry("b", "1", 'b'), otherHash = Entry("c", "1", 'c'),
            otherVariant = Entry("d", "1", 'd'), assetGone = Entry("e", "1", 'e'), caseOnly = Entry("f", "1", 'f');
        MediaLibraryEntry foreign = Entry("z", "9", 'z'); // a perfectly valid original of another tile, in the same folder
        IReadOnlyList<MediaLibraryTile> tiles = Tiles([entry, mediaMoved, otherHash, otherVariant, assetGone, caseOnly]);
        MediaAssetSnapshot Of(MediaLibraryEntry shown, MediaLibraryEntry holds) => Manifest(shown.Directory!, holds with { Name = shown.Name });

        MediaLibraryExportPlan plan = await MediaLibraryExport.BuildAsync(tiles, (directory, _) => Task.FromResult(
            directory == entry.Directory ? Of(entry, entry)
            : directory == mediaMoved.Directory ? Of(mediaMoved, mediaMoved with { MediaId = "other" })
            : directory == otherHash.Directory ? Of(otherHash, otherHash with { Sha256 = Hash('0') })
            : directory == otherVariant.Directory ? Of(otherVariant, otherVariant with { Variant = "preview" })
            : directory == assetGone.Directory ? Of(assetGone, foreign) // holds only the foreign asset
            : Of(caseOnly, caseOnly with { Sha256 = Hash('f').ToUpperInvariant() }))); // a hash differing only in case is the same hash

        Assert.Equal(new[] { entry.Thread, caseOnly.Thread }, plan.Assets.Select(asset => asset.Key.Thread));
        Assert.Equal(4, plan.Unavailable);
        Assert.DoesNotContain(plan.Assets, asset => asset.Key.Sha256 == Hash('z'));
    }

    [Fact]
    public async Task ACancelledPlanOpensNoManifestAndACancelRaisedWhileOpeningIsNotSwallowed()
    {
        MediaLibraryEntry a = Entry("a", "1", 'a'), b = Entry("b", "1", 'b');
        IReadOnlyList<MediaLibraryTile> tiles = Tiles([a, b]);
        int opens = 0;
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MediaLibraryExport.BuildAsync(tiles, (directory, _) => { opens++; return Task.FromResult(Manifest(directory, a)); }, cancelled.Token));
        Assert.Equal(0, opens);

        // The cancel arrives while the first manifest is read: the plan stops there instead of counting a failed tile and going on.
        using CancellationTokenSource during = new();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MediaLibraryExport.BuildAsync(tiles, (directory, token) =>
        {
            opens++;
            during.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Manifest(directory, a));
        }, during.Token));
        Assert.Equal(1, opens);
    }

    // "Export all": the page hands every shown tile to the same plan the selection uses.
    [Fact]
    public async Task ExportAllOffersExactlyTheShownTilesOfTheFilterAndLeavesHiddenOnesOut()
    {
        MediaLibraryEntry a = Entry("a", "1", 'a', 4), b = Entry("b", "1", 'b', 3), dupX = Entry("c", "1", 'e', 2), dupY = Entry("d", "1", 'e', 1),
            hidden = Entry("h", "1", 'h', 5), gone = Entry("g", "1", 'd', missing: true), preview = Entry("p", "1", '1', variant: "preview");
        MediaLibraryEntry[] all = [a, b, dupX, dupY, hidden, gone, preview];
        // The filter lets everything through except "hidden" and a; the duplicate tile stays through either of its occurrences.
        IReadOnlyList<MediaLibraryTile> shown = Tiles(all, passing: [b, dupY, gone, preview]);
        Dictionary<string, MediaAssetSnapshot> manifests = all.Where(entry => entry.Directory != null).ToDictionary(entry => entry.Directory!, entry => Manifest(entry.Directory!, entry));
        List<string> opened = new();

        MediaLibraryExportSize size = MediaLibraryExport.Measure(shown);
        MediaLibraryExportPlan plan = await MediaLibraryExport.BuildAsync(shown, (directory, _) => { opened.Add(directory); return Task.FromResult(manifests[directory]); });

        Assert.Equal(new MediaLibraryExportSize(2, 6, 2), size); // b and the pair; the unsaved and the preview-only tile have nothing to offer
        Assert.Equal(size.Files, plan.Assets.Count);
        Assert.Equal(size.NotOffered, plan.Unavailable);
        Assert.Equal(new[] { Hash('b'), Hash('e') }, plan.Assets.Select(asset => asset.Key.Sha256).Order());
        Assert.Equal(1, plan.Assets.Count(asset => asset.Key.Sha256 == Hash('e'))); // one per tile, never one per duplicate
        Assert.DoesNotContain(hidden.Directory!, opened);
        Assert.DoesNotContain(a.Directory!, opened);
        Assert.DoesNotContain(plan.Assets, asset => asset.Key.Sha256 == Hash('h') || asset.Key.Sha256 == Hash('a'));
    }

    [Fact]
    public void ExportAllMeasuresFromTheIndexAloneAndAsksAboveFiveHundredFilesOrTwoGibibytes()
    {
        MediaLibraryEntry Sized(int n, long length) => Entry("t" + n, "1", 'a') with { Sha256 = n.ToString("x").PadLeft(64, '0'), Length = length };
        IReadOnlyList<MediaLibraryTile> TilesOf(int count, long each) => Tiles(Enumerable.Range(0, count).Select(n => Sized(n, each)).ToArray());

        Assert.False(MediaLibraryExport.Measure(TilesOf(500, 1)).Exceeds()); // 500 files exactly: no question
        Assert.True(MediaLibraryExport.Measure(TilesOf(501, 1)).Exceeds());
        long twoGiB = 2L * 1024 * 1024 * 1024;
        Assert.False(MediaLibraryExport.Measure(TilesOf(2, twoGiB / 2)).Exceeds()); // 2 GiB exactly: no question
        Assert.True(MediaLibraryExport.Measure(TilesOf(2, twoGiB / 2 + 1)).Exceeds());
        Assert.Equal(new MediaLibraryExportSize(3, 3 * twoGiB, 0), MediaLibraryExport.Measure(TilesOf(3, twoGiB))); // sums as long
        Assert.True(new MediaLibraryExportSize(3, 10, 0).Exceeds(maxFiles: 2)); // a lowered limit, as the smoke test uses it
        Assert.True(new MediaLibraryExportSize(3, 10, 0).Exceeds(maxBytes: 9));
        Assert.False(new MediaLibraryExportSize(3, 10, 0).Exceeds(maxFiles: 3, maxBytes: 10));
        Assert.Equal(new MediaLibraryExportSize(0, 0, 0), MediaLibraryExport.Measure([]));
    }

    [Fact]
    public async Task ExportPlanForTenThousandTilesBuildsWithinBounds()
    {
        const int threads = 200, perThread = 50;
        MediaLibraryEntry[] all = Enumerable.Range(0, threads * perThread).Select(n =>
            Entry("t" + n / perThread, (n % perThread + 1).ToString(), 'a') with { Sha256 = n.ToString("x").PadLeft(64, '0') }).ToArray();
        IReadOnlyList<MediaLibraryTile> shown = Tiles(all);
        Dictionary<string, MediaAssetSnapshot> manifests = all.GroupBy(entry => entry.Directory!).ToDictionary(group => group.Key, group => Manifest(group.Key, group.ToArray()));
        Assert.Equal(threads * perThread, shown.Count);

        // The cost bound is checked on the fastest of a few runs: a scheduling or GC pause on a loaded machine hits one run,
        // an accidentally quadratic plan hits all of them.
        TimeSpan fastest = TimeSpan.MaxValue;
        MediaLibraryExportSize size = default!;
        MediaLibraryExportPlan plan = default!;
        for (int run = 0; run < 3; run++)
        {
            Stopwatch watch = Stopwatch.StartNew();
            size = MediaLibraryExport.Measure(shown);
            plan = await MediaLibraryExport.BuildAsync(shown, (directory, _) => Task.FromResult(manifests[directory]));
            watch.Stop();
            if (watch.Elapsed < fastest) fastest = watch.Elapsed;
        }

        Assert.Equal(threads * perThread, size.Files);
        Assert.Equal(threads * perThread * 3L, size.Bytes);
        Assert.Equal(threads * perThread, plan.Assets.Count);
        Assert.Equal(0, plan.Unavailable);
        Assert.True(fastest < TimeSpan.FromSeconds(2), "Measuring and planning 10 000 tiles took " + fastest);
    }
}

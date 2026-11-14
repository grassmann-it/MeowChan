using System.Diagnostics;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using Xunit.Abstractions;

namespace MeowChan.DesktopTests;

// MP4: the tag counts of the Booru mode (MediaTagCountIndex). Pure: own tags and source tags are handed in as functions, so the counts,
// the reuse of resolved occurrences across filter changes and index rebuilds, and the time of a recount over 50,000 tiles and 1,000 tags
// are checked without a profile.
[Collection("Localization")]
public sealed class MediaTagCountsTests(ITestOutputHelper output) : IDisposable
{
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    internal static MediaLibraryEntry Entry(int index, string? booruPost = null) => new(
        booruPost == null ? new ResourceId("4chan", "g", "t" + (index % 97)) : BooruMediaIdentity.Thread(booruPost), "Thread", 1,
        DateTimeOffset.UnixEpoch, null, booruPost == null ? "p" + index : null, booruPost == null ? index : null, null, 0, null,
        "/synthetic/dir" + (index % 97), "asset" + index, new string((char)('a' + index % 6), 64), 10, "m" + index, "original", "f" + index + ".jpg",
        SavedMediaKind.Image, MediaLibraryPresence.Present, booruPost != null, false) { BooruPostId = booruPost };

    private static Guid Id(int n) => new(n, 0, 0, new byte[8]);

    private static MediaTagCountIndex Index(IReadOnlyList<MediaLibraryEntry> all, Dictionary<MediaLibraryEntry, Guid[]> own,
        Dictionary<MediaLibraryEntry, string[]>? source = null, object? ownSource = null, object? sourceSource = null, MediaTagCountIndex? previous = null) =>
        MediaTagCountIndex.Create(all, ownSource ?? own, entry => own.TryGetValue(entry, out Guid[]? tags) ? tags : [], id => "tag" + id.ToString("N")[..8],
            source == null ? null : sourceSource ?? source, source == null ? null : entry => source.TryGetValue(entry, out string[]? tags) ? tags : [], previous);

    [Fact]
    public void EveryShownTileCountsOncePerTagOwnAndSourceTagsApart()
    {
        MediaLibraryEntry a = Entry(1), b = Entry(2), c = Entry(3, booruPost: "42"), hidden = Entry(4);
        Dictionary<MediaLibraryEntry, Guid[]> own = new(ReferenceEqualityComparer.Instance)
        {
            [a] = [Id(1), Id(2)], [b] = [Id(1)], [hidden] = [Id(1), Id(3)]
        };
        Dictionary<MediaLibraryEntry, string[]> source = new(ReferenceEqualityComparer.Instance) { [c] = ["cat_ears", "blue_eyes"] };
        MediaTagCountResult result = Index([a, b, c, hidden], own, source).Count([a, b, c]);
        Assert.Equal(3, result.Media);
        Assert.Equal(1, result.SourceMedia);
        Assert.True(result.OwnAvailable && result.SourceAvailable);
        Assert.Equal([(Id(1).ToString("D"), 2), (Id(2).ToString("D"), 1)], result.Own.Select(count => (count.Key, count.Count)).Order());
        // A tag only hidden tiles carry is not in the results.
        Assert.DoesNotContain(result.Own, count => count.Key == Id(3).ToString("D"));
        Assert.Equal(["blue_eyes", "cat_ears"], result.Source.Select(count => count.Name).Order());
        Assert.All(result.Source, count => Assert.Equal((MediaTagGroup.Source, 1), (count.Group, count.Count)));
    }

    [Fact]
    public void WithoutTagDataOrCatalogTheGroupSaysItIsUnavailableInsteadOfCountingLess()
    {
        MediaLibraryEntry a = Entry(1), booru = Entry(2, booruPost: "7");
        MediaTagCountIndex noOwn = MediaTagCountIndex.Create([a, booru], null, null, _ => null, null, null, null);
        MediaTagCountResult result = noOwn.Count([a, booru]);
        Assert.False(result.OwnAvailable);
        Assert.False(result.SourceAvailable); // provider media are shown, but there is no catalog to read their tags from
        Assert.True(noOwn.Count([a]).SourceAvailable); // no provider media: nothing is missing
        // Tag data that went away while counting (the reader throws) makes the group unavailable as well.
        MediaTagCountIndex failing = MediaTagCountIndex.Create([a], new object(), _ => throw new MediaLibraryFilterException(MediaLibraryFilterError.TagsUnavailable),
            _ => null, null, null, null);
        Assert.False(failing.Count([a]).OwnAvailable);
    }

    [Fact]
    public void AFilterChangeOnlyAddsUpAgainAndANewIndexKeepsTheOccurrencesItShares()
    {
        MediaLibraryEntry[] all = Enumerable.Range(0, 100).Select(i => Entry(i)).ToArray();
        int reads = 0;
        Dictionary<MediaLibraryEntry, Guid[]> own = all.ToDictionary(entry => entry, entry => new[] { Id(entry.Position!.Value % 5) }, (IEqualityComparer<MediaLibraryEntry>)ReferenceEqualityComparer.Instance);
        object tags = new();
        MediaTagCountIndex Make(IReadOnlyList<MediaLibraryEntry> entries, object source, MediaTagCountIndex? previous) => MediaTagCountIndex.Create(entries, source,
            entry => { reads++; return own.TryGetValue(entry, out Guid[]? found) ? found : []; }, _ => "t", null, null, previous);
        MediaTagCountIndex first = Make(all, tags, null);
        Assert.Equal(100, first.Count(all).Own.Sum(count => count.Count));
        Assert.Equal(100, reads);
        // The same index and tag data: the same object, and a narrower result set reads nothing again.
        Assert.Same(first, Make(all, tags, first));
        Assert.Equal(20, first.Count(all.Where((_, i) => i % 5 == 0).ToArray()).Own.Single().Count);
        Assert.Equal(100, reads);
        // A rebuilt index (new list, 90 of the old occurrences plus 10 new): the 90 are taken over, only the new ones are read.
        MediaLibraryEntry[] fresh = Enumerable.Range(100, 10).Select(i => Entry(i)).ToArray();
        foreach (MediaLibraryEntry entry in fresh) own[entry] = [Id(9)];
        MediaLibraryEntry[] rebuilt = all.Skip(10).Concat(fresh).ToArray();
        MediaTagCountIndex second = Make(rebuilt, tags, first);
        Assert.NotSame(first, second);
        Assert.Equal(90, second.Reused);
        MediaTagCountResult counted = second.Count(rebuilt);
        Assert.Equal(110, reads);
        Assert.Equal(10, counted.Own.Single(count => count.Key == Id(9).ToString("D")).Count);
        // Other tag data: every occurrence is read again.
        MediaTagCountIndex third = Make(rebuilt, new object(), second);
        Assert.Equal(0, third.Reused);
        third.Count(rebuilt);
        Assert.Equal(210, reads);
    }

    [Fact]
    public void ACancelledCountStopsAndTheNextOneStillCountsEverything()
    {
        MediaLibraryEntry[] all = Enumerable.Range(0, 10_000).Select(i => Entry(i)).ToArray();
        Dictionary<MediaLibraryEntry, Guid[]> own = all.ToDictionary(entry => entry, _ => new[] { Id(1) }, (IEqualityComparer<MediaLibraryEntry>)ReferenceEqualityComparer.Instance);
        MediaTagCountIndex index = Index(all, own);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => index.Count(all, cancelled.Token));
        Assert.Equal(10_000, index.Count(all).Own.Single().Count);
    }

    [Fact]
    public void ARecountOverFiftyThousandTilesAndAThousandTagsStaysWithinTheBudget()
    {
        // 50,000 occurrences (5,000 of them provider media), 1,000 own tags (8 per occurrence) and 300 source tags (12 per provider medium).
        const int Entries = 50_000, OwnTags = 1_000;
        MediaLibraryEntry[] all = Enumerable.Range(0, Entries).Select(i => Entry(i, i % 10 == 0 ? "post" + i : null)).ToArray();
        Guid[] ids = Enumerable.Range(0, OwnTags).Select(Id).ToArray();
        Dictionary<MediaLibraryEntry, Guid[]> own = new((IEqualityComparer<MediaLibraryEntry>)ReferenceEqualityComparer.Instance);
        Dictionary<MediaLibraryEntry, string[]> source = new((IEqualityComparer<MediaLibraryEntry>)ReferenceEqualityComparer.Instance);
        for (int i = 0; i < all.Length; i++)
        {
            own[all[i]] = Enumerable.Range(0, 8).Select(k => ids[(i * 7 + k * 131) % OwnTags]).Distinct().ToArray();
            if (all[i].BooruPostId != null) source[all[i]] = Enumerable.Range(0, 12).Select(k => "tag_" + ((i + k * 17) % 300)).Distinct().ToArray();
        }
        MediaTagCountIndex index = Index(all, own, source);
        Stopwatch first = Stopwatch.StartNew();
        MediaTagCountResult whole = index.Count(all);
        first.Stop();
        Assert.Equal(OwnTags, whole.Own.Count);
        Assert.Equal(300, whole.Source.Count);
        // Filter changes: every other tile, a tenth, everything again. The best of three runs per change is the budget's measure
        // (a test host shares the machine), and every one of them must stay at or under 100 ms.
        MediaLibraryEntry[][] changes = [all.Where((_, i) => i % 2 == 0).ToArray(), all.Where((_, i) => i % 10 == 3).ToArray(), all];
        foreach (MediaLibraryEntry[] shown in changes)
        {
            double best = double.MaxValue;
            for (int run = 0; run < 3; run++)
            {
                Stopwatch watch = Stopwatch.StartNew();
                MediaTagCountResult result = index.Count(shown);
                watch.Stop();
                Assert.Equal(shown.Length, result.Media);
                best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
            }
            output.WriteLine($"recount of {shown.Length} tiles: {best:F1} ms (first count with resolution {first.Elapsed.TotalMilliseconds:F1} ms)");
            Assert.True(best <= 100, $"A recount of {shown.Length} tiles took {best:F1} ms; the budget is 100 ms.");
        }
    }
}

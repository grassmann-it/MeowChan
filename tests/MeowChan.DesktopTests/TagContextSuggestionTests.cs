using System.Diagnostics;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Storage;
using MeowChan.Tags;
using MeowChan.Transfer;
using Xunit.Abstractions;

namespace MeowChan.DesktopTests;

public sealed class TagContextSuggestionTests
{
    private readonly ITestOutputHelper _output;

    public TagContextSuggestionTests(ITestOutputHelper output) => _output = output;

    private static PrivateTagDefinition Tag(string name)
    {
        Assert.True(PrivateTagName.TryCreate(name, out PrivateTagName? parsed, out _));
        return new PrivateTagDefinition(new TagId(Guid.NewGuid()), parsed!);
    }

    private static ResourceId Thread(string id) => new("source", "board", id);
    private static string Hex(char value) => new(value, 64);

    private static PrivateMediaTagTarget Media(string thread, string post, int position, string sha, char revision = 'a', string mediaId = "m")
    {
        Assert.True(PrivateMediaTagTarget.TryCreate(new AttachmentRevisionReference(Thread(thread), post, position, "att-v2:" + Hex(revision)),
            mediaId, "original", TransferMediaRole.Original, sha, out PrivateMediaTagTarget? target, out _));
        return target!;
    }

    private static PrivateTagStoreSnapshot Snap(IEnumerable<PrivateTagDefinition> tags,
        IEnumerable<(PrivateTagDefinition Tag, string Thread)>? threads = null,
        IEnumerable<(PrivateTagDefinition Tag, PrivateMediaTagTarget Target)>? media = null,
        LocalMediaRegistry? registry = null,
        IEnumerable<(PrivateTagDefinition Tag, LocalMediaTagTarget Target)>? local = null) => new(1, "fp", tags,
        (threads ?? Array.Empty<(PrivateTagDefinition, string)>()).Select(item => new PrivateThreadTagAssignment(item.Tag.Id, Thread(item.Thread))),
        (media ?? Array.Empty<(PrivateTagDefinition, PrivateMediaTagTarget)>()).Select(item => new PrivateMediaTagAssignment(item.Tag.Id, item.Target)),
        registry, (local ?? Array.Empty<(PrivateTagDefinition, LocalMediaTagTarget)>()).Select(item => new PrivateLocalMediaTagAssignment(item.Tag.Id, item.Target)));

    private static double Freq(int uses) => Math.Min(3, Math.Log2(uses + 1));

    private static RankedTagSuggestion Find(IReadOnlyList<RankedTagSuggestion> list, string name) =>
        list.Single(item => item.Name.Display == name);

    private static string[] Names(IEnumerable<RankedTagSuggestion> items) => items.Select(item => item.Name.Display).ToArray();

    // ------------------------------------------------------------------ ranking per source

    [Fact]
    public void EverySourceContributesItsPlanPointsAndReason()
    {
        PrivateTagDefinition same = Tag("same"), thr = Tag("thr"), tm = Tag("tm"), nb = Tag("nb"), rec = Tag("rec"),
            srcown = Tag("srcown"), blueSky = Tag("blue_sky");
        string sha = Hex('1');
        PrivateMediaTagTarget current = Media("cur", "1", 0, Hex('9'));
        PrivateMediaTagTarget neighbour = Media("elsewhere2", "1", 0, Hex('8'));
        PrivateTagStoreSnapshot snapshot = Snap(new[] { same, thr, tm, nb, rec, srcown, blueSky },
            threads: new[] { (thr, "cur") },
            media: new[]
            {
                (same, Media("elsewhere", "1", 0, sha)),   // same bytes, other thread
                (tm, Media("cur", "2", 0, Hex('7'))),      // other file of this thread
                (nb, neighbour)
            });
        TagContext context = new()
        {
            Thread = Thread("cur"),
            Sha256 = sha,
            FileName = "blue_sky_01.jpg",
            TitleTexts = new string?[] { "Look /rpt/ fine" },
            Neighbours = TagNeighbourhood.From(new MediaTagSubject?[] { null, new MediaTagSubject.Bound(current), new MediaTagSubject.Bound(neighbour) }, 1),
            SourceTags = new[] { "srcown", "newsrc" },
            Recent = new[] { rec.Id }
        };

        IReadOnlyList<RankedTagSuggestion> ranked = TagContextSuggestions.Rank(snapshot, context);

        Assert.Equal(9, ranked.Count);
        Assert.Equal(6 + Freq(1), Find(ranked, "same").Score);
        Assert.Equal(TagSuggestionReason.SameFile, Find(ranked, "same").PrimaryReason);
        Assert.Equal(5 + Freq(1), Find(ranked, "thr").Score);
        Assert.Equal(TagSuggestionReason.ThreadTag, Find(ranked, "thr").PrimaryReason);
        Assert.Equal(4 + Freq(1), Find(ranked, "tm").Score);
        Assert.Equal(TagSuggestionReason.ThreadMedia, Find(ranked, "tm").PrimaryReason);
        Assert.Equal(3 + Freq(1), Find(ranked, "nb").Score);
        Assert.Equal(TagSuggestionReason.Neighbour, Find(ranked, "nb").PrimaryReason);
        Assert.Equal(4, Find(ranked, "rec").Score);
        Assert.Equal(TagSuggestionReason.Recent, Find(ranked, "rec").PrimaryReason);
        Assert.Equal(4, Find(ranked, "srcown").Score);
        Assert.Equal(TagSuggestionReason.SourceTag, Find(ranked, "srcown").PrimaryReason);
        Assert.Equal(3, Find(ranked, "blue_sky").Score);
        Assert.Equal(TagSuggestionReason.FileName, Find(ranked, "blue_sky").PrimaryReason);

        RankedTagSuggestion source = Find(ranked, "newsrc");
        Assert.Equal((TagSuggestionOrigin.Source, (TagId?)null, 2.0), (source.Origin, source.Id, source.Score));
        RankedTagSuggestion marker = Find(ranked, "/rpt/");
        Assert.Equal((TagSuggestionOrigin.New, (TagId?)null, 2.0, TagSuggestionReason.SlashMarker), (marker.Origin, marker.Id, marker.Score, marker.PrimaryReason));

        // Strongest first, own tags carry their id, and the whole list follows the plan's order.
        Assert.Equal(new[] { "same", "thr", "tm" }, Names(ranked.Take(3)));
        Assert.Equal(new[] { "nb", "rec", "srcown" }, Names(ranked.Skip(3).Take(3))); // 4 points: more uses, then name
        Assert.Equal(same.Id, Find(ranked, "same").Id);
        Assert.All(ranked.Zip(ranked.Skip(1)), pair => Assert.True(TagContextSuggestions.Compare(pair.First, pair.Second) <= 0));
    }

    [Fact]
    public void SourcesAddUpButEachCountsOnlyItsBestContribution()
    {
        PrivateTagDefinition tag = Tag("multi"), plain = Tag("plain");
        string sha = Hex('1');
        PrivateTagStoreSnapshot snapshot = Snap(new[] { tag, plain },
            threads: new[] { (tag, "cur") },
            media: new[]
            {
                (tag, Media("cur", "2", 0, Hex('7'))), (tag, Media("cur", "3", 0, Hex('7'))), (tag, Media("cur", "4", 0, Hex('7'))),
                (tag, Media("cur", "5", 0, Hex('7'))), (tag, Media("cur", "6", 0, Hex('7'))), (tag, Media("cur", "7", 0, sha))
            });
        MediaTagSubject near = new MediaTagSubject.Bound(Media("cur", "2", 0, Hex('7')));
        TagContext context = new()
        {
            Thread = Thread("cur"), Sha256 = sha, Recent = new[] { tag.Id },
            Neighbours = TagNeighbourhood.From(new MediaTagSubject?[] { null, near, near }, 0)
        };

        RankedTagSuggestion ranked = TagContextSuggestions.Rank(snapshot, context).Single(item => item.Id == tag.Id);

        // Thread tag (5) and thread media (4 + min(2, 6 files / 3)) are one source: the better 6 counts, not 11; the
        // neighbour appears twice and counts 3 once; recent rank 1 is 4; the same bytes are 6; plus base noise for 7 uses.
        int uses = TagSuggestionIndex.UsesOf(snapshot)[tag.Id];
        Assert.Equal(7, uses); // one thread plus six files
        Assert.Equal(6 + 6 + 3 + 4 + Freq(uses), ranked.Score);
        Assert.Equal(new[] { TagSuggestionReason.SameFile, TagSuggestionReason.ThreadMedia, TagSuggestionReason.Recent, TagSuggestionReason.Neighbour, TagSuggestionReason.Frequent },
            ranked.Reasons.ToArray());
        Assert.Equal(5, ranked.Contributions.Count);
        // A tag nobody relates to never joins the empty-field suggestions with a zero score.
        Assert.DoesNotContain(TagContextSuggestions.Rank(snapshot, TagContext.Empty), item => item.Id == plain.Id);
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(2, 4)]
    [InlineData(3, 5)]
    [InlineData(5, 5)]
    [InlineData(6, 6)]
    [InlineData(9, 6)]
    [InlineData(30, 6)]
    public void ThreadMediaScoresFourPlusOnePerThreeFilesUpToSix(int files, int expected)
    {
        PrivateTagDefinition tag = Tag("t");
        PrivateTagStoreSnapshot snapshot = Snap(new[] { tag }, media: Enumerable.Range(0, files).Select(i => (tag, Media("cur", i.ToString(), 0, Hex('7')))));

        RankedTagSuggestion ranked = TagContextSuggestions.Rank(snapshot, new TagContext { Thread = Thread("cur") }).Single();

        Assert.Equal(expected, ranked.Contributions.Single(item => item.Reason == TagSuggestionReason.ThreadMedia).Points);
    }

    [Fact]
    public void ARevisionKeyOfTheSameFileCountsOnceForTheThread()
    {
        PrivateTagDefinition tag = Tag("t");
        // Same file under an earlier and a later revision key: one file, so no bonus; a second file adds one.
        PrivateTagStoreSnapshot one = Snap(new[] { tag }, media: new[] { (tag, Media("cur", "1", 0, Hex('7'), 'a')), (tag, Media("cur", "1", 0, Hex('7'), 'b')) });
        PrivateTagStoreSnapshot two = Snap(new[] { tag }, media: new[] { (tag, Media("cur", "1", 0, Hex('7'), 'a')), (tag, Media("cur", "2", 0, Hex('7'), 'a')) });

        Assert.Equal(1, TagContextIndex.For(one).ThreadMediaTags(Thread("cur"))[tag.Id]);
        Assert.Equal(2, TagContextIndex.For(two).ThreadMediaTags(Thread("cur"))[tag.Id]);
    }

    [Theory]
    [InlineData(1, 4)] [InlineData(2, 4)] [InlineData(3, 4)] [InlineData(4, 3)] [InlineData(10, 3)] [InlineData(11, 1)] [InlineData(25, 1)]
    public void RecentRankScoresFourThreeThenOne(int rank, double points)
    {
        PrivateTagDefinition[] tags = Enumerable.Range(0, 25).Select(i => Tag("recent" + i.ToString("00"))).ToArray();
        PrivateTagStoreSnapshot snapshot = Snap(tags);
        TagContext context = new() { Recent = tags.Select(tag => tag.Id).ToArray() };

        IReadOnlyList<RankedTagSuggestion> ranked = TagContextSuggestions.Rank(snapshot, context, options: new TagContextOptions(suggestionCount: 18), limit: 25);

        Assert.Equal(points, ranked.Single(item => item.Id == tags[rank - 1].Id).Score);
    }

    [Fact]
    public void RecentCountLimitsTheRankAndZeroSwitchesTheSourceOff()
    {
        PrivateTagDefinition[] tags = Enumerable.Range(0, 5).Select(i => Tag("r" + i)).ToArray();
        PrivateTagStoreSnapshot snapshot = Snap(tags);
        TagContext context = new() { Recent = tags.Select(tag => tag.Id).ToArray() };

        Assert.Empty(TagContextSuggestions.Rank(snapshot, context, options: new TagContextOptions(recentCount: 0)));
        // 3 are kept of 5; negative values are clamped to the minimum 0, huge ones to the maximum.
        Assert.Equal(new[] { "r0", "r1" }, Names(TagContextSuggestions.Rank(snapshot, context, options: new TagContextOptions(recentCount: 2))));
        Assert.Empty(TagContextSuggestions.Rank(snapshot, context, options: new TagContextOptions(recentCount: -5)));
        Assert.Equal(5, TagContextSuggestions.Rank(snapshot, context, options: new TagContextOptions(recentCount: 100_000), limit: 18).Count);
    }

    [Fact]
    public void NeighboursScoreThreeWithinFiveEntriesAndTwoFartherAndTheScanIsBounded()
    {
        PrivateTagDefinition near = Tag("near"), far = Tag("far"), beyond = Tag("beyond");
        MediaTagSubject[] subjects = Enumerable.Range(0, 301).Select(i => (MediaTagSubject)new MediaTagSubject.Bound(Media("list", i.ToString(), 0, Hex('7')))).ToArray();
        PrivateTagStoreSnapshot snapshot = Snap(new[] { near, far, beyond }, media: new[]
        {
            (near, ((MediaTagSubject.Bound)subjects[155]).Target),     // 5 after the current (150)
            (far, ((MediaTagSubject.Bound)subjects[110]).Target),      // 40 before
            (beyond, ((MediaTagSubject.Bound)subjects[290]).Target)    // 140 after: outside the smallest scan (100 entries)
        });
        TagContext context = new() { Neighbours = TagNeighbourhood.From(subjects, 150) };
        TagContextOptions smallest = new(neighbourScan: 1);

        Assert.Equal(TagContextOptions.MinimumNeighbourScan, smallest.NeighbourScan);
        IReadOnlyList<RankedTagSuggestion> ranked = TagContextSuggestions.Rank(snapshot, context, options: smallest);

        Assert.Equal(3 + Freq(1), Find(ranked, "near").Score);
        Assert.Equal(2 + Freq(1), Find(ranked, "far").Score);
        // Outside the scan it only keeps the base noise of its use; no neighbour reason.
        Assert.Equal(Freq(1), Find(ranked, "beyond").Score);
        Assert.DoesNotContain(TagSuggestionReason.Neighbour, Find(ranked, "beyond").Reasons);
        // A larger scan reaches it, and it is a "far" neighbour.
        IReadOnlyList<RankedTagSuggestion> wide = TagContextSuggestions.Rank(snapshot, context, options: new TagContextOptions(neighbourScan: 1000));
        Assert.Equal(2 + Freq(1), Find(wide, "beyond").Score);
    }

    [Fact]
    public void NeighbourhoodEdgesAndMissingSubjectsAreSafe()
    {
        PrivateTagDefinition tag = Tag("t");
        PrivateMediaTagTarget target = Media("list", "1", 0, Hex('7'));
        PrivateTagStoreSnapshot snapshot = Snap(new[] { tag }, media: new[] { (tag, target) });
        MediaTagSubject?[] list = { null, new MediaTagSubject.Bound(target), null };

        static bool Neighbours(IReadOnlyList<RankedTagSuggestion> ranked) => ranked.Any(item => item.Reasons.Contains(TagSuggestionReason.Neighbour));
        bool Reaches(TagNeighbourhood neighbourhood) => Neighbours(TagContextSuggestions.Rank(snapshot, new TagContext { Neighbours = neighbourhood }));

        // Index out of range is clamped; a single entry or an empty list has no neighbours; null subjects are skipped.
        Assert.True(Reaches(TagNeighbourhood.From(list, 99)));
        Assert.True(Reaches(TagNeighbourhood.From(list, -7)));
        Assert.False(Reaches(TagNeighbourhood.From(new MediaTagSubject?[] { new MediaTagSubject.Bound(target) }, 0)));
        Assert.False(Reaches(TagNeighbourhood.From(Array.Empty<MediaTagSubject?>(), 0)));
        Assert.False(Reaches(TagNeighbourhood.None));
    }

    [Fact]
    public void AssignedTagsAreNeverSuggested()
    {
        PrivateTagDefinition mine = Tag("mine"), other = Tag("other");
        string sha = Hex('1');
        PrivateTagStoreSnapshot snapshot = Snap(new[] { mine, other }, threads: new[] { (mine, "cur"), (other, "cur") },
            media: new[] { (mine, Media("x", "1", 0, sha)), (other, Media("x", "2", 0, sha)) });
        TagContext context = new() { Thread = Thread("cur"), Sha256 = sha, Recent = new[] { mine.Id, other.Id }, FileName = "mine.png", SourceTags = new[] { "mine" } };

        Assert.Equal(new[] { "other" }, Names(TagContextSuggestions.Rank(snapshot, context, new HashSet<TagId> { mine.Id })));
        Assert.Empty(TagContextSuggestions.Rank(snapshot, context, new HashSet<TagId> { mine.Id, other.Id }));
        Assert.Empty(TagContextSuggestions.RankTyped(snapshot, "mi", context, new HashSet<TagId> { mine.Id }).Own);
    }

    [Fact]
    public void LocalFilesCountForSameBytesThreadAndNeighbours()
    {
        PrivateTagDefinition loc = Tag("loc");
        string sha = Hex('7');
        LocalMediaOccurrenceId occurrenceId = new(Guid.NewGuid());
        LocalMediaRevisionId revisionId = new(Guid.NewGuid());
        LocalMediaLocator locator = new(Thread("lt"), "dir", "pic.png");
        LocalMediaRegistry registry = new(new[]
        {
            new LocalMediaOccurrence(occurrenceId, locator, new[] { new LocalMediaRevision(revisionId, Hex('d'), 10, sha) })
        });
        Assert.True(LocalMediaObservation.TryCreate(Thread("lt"), "dir", "pic.png", Hex('d'), 10, sha, out LocalMediaObservation? observation));
        PrivateTagStoreSnapshot snapshot = Snap(new[] { loc }, registry: registry, local: new[] { (loc, new LocalMediaTagTarget(occurrenceId, revisionId)) });
        MediaTagSubject unresolved = new MediaTagSubject.Local(observation!, null);

        // The registry resolves the observation to the tagged target; the same bytes and the owning thread both point at it.
        Assert.Equal(new[] { loc.Id }, TagContextIndex.For(snapshot).TagsOf(unresolved));
        RankedTagSuggestion byContext = TagContextSuggestions.Rank(snapshot, new TagContext { Thread = Thread("lt"), Sha256 = sha }).Single();
        Assert.Equal(6 + 4 + Freq(1), byContext.Score);
        Assert.Equal(new[] { TagSuggestionReason.SameFile, TagSuggestionReason.ThreadMedia, TagSuggestionReason.Frequent }, byContext.Reasons.ToArray());

        RankedTagSuggestion byNeighbour = TagContextSuggestions.Rank(snapshot,
            new TagContext { Neighbours = TagNeighbourhood.From(new MediaTagSubject?[] { null, unresolved }, 0) }).Single();
        Assert.Equal(3 + Freq(1), byNeighbour.Score);
        // A different revision of the file (other asset) is not the tagged one: nothing is carried over.
        Assert.True(LocalMediaObservation.TryCreate(Thread("lt"), "dir", "pic.png", Hex('e'), 10, Hex('6'), out LocalMediaObservation? other));
        Assert.Empty(TagContextIndex.For(snapshot).TagsOf(new MediaTagSubject.Local(other!, null)));
        Assert.Empty(TagContextIndex.For(snapshot).TagsOf(null));
    }

    // R18 (review 14.11. night): a loose file counts once per tag in the thread, however many of its revisions carry the tag.
    [Fact]
    public void ALooseFileWithTwoTaggedRevisionsCountsOnceForTheThread()
    {
        PrivateTagDefinition loc = Tag("loc");
        LocalMediaOccurrenceId occurrenceId = new(Guid.NewGuid()), otherId = new(Guid.NewGuid());
        LocalMediaRevisionId first = new(Guid.NewGuid()), second = new(Guid.NewGuid()), third = new(Guid.NewGuid());
        LocalMediaRegistry registry = new(new[]
        {
            new LocalMediaOccurrence(occurrenceId, new LocalMediaLocator(Thread("lt"), "dir", "pic.png"),
                new[] { new LocalMediaRevision(first, Hex('d'), 10, Hex('1')), new LocalMediaRevision(second, Hex('e'), 11, Hex('2')) }),
            new LocalMediaOccurrence(otherId, new LocalMediaLocator(Thread("lt"), "dir", "other.png"),
                new[] { new LocalMediaRevision(third, Hex('f'), 12, Hex('3')) })
        });
        PrivateTagStoreSnapshot snapshot = Snap(new[] { loc }, registry: registry, local: new[]
        {
            (loc, new LocalMediaTagTarget(occurrenceId, first)), (loc, new LocalMediaTagTarget(occurrenceId, second)),
            (loc, new LocalMediaTagTarget(otherId, third))
        });

        Assert.Equal(2, TagContextIndex.For(snapshot).ThreadMediaTags(Thread("lt"))[loc.Id]); // two files, not three revisions
        // Both revisions still answer for their own bytes.
        Assert.Equal(new[] { loc.Id }, TagContextIndex.For(snapshot).TagsOfHash(Hex('1')));
        Assert.Equal(new[] { loc.Id }, TagContextIndex.For(snapshot).TagsOfHash(Hex('2')));
    }

    // ------------------------------------------------------------------ word matching

    [Fact]
    public void WordsOnlyMatchExistingTagsAndNeverCreateOne()
    {
        PrivateTagDefinition sky = Tag("sky"), skyline = Tag("skyline"), unrelated = Tag("unrelated");
        PrivateTagStoreSnapshot snapshot = Snap(new[] { sky, skyline, unrelated });

        IReadOnlyList<RankedTagSuggestion> ranked = TagContextSuggestions.Rank(snapshot, new TagContext { FileName = "unknownword_sky_IMG.JPEG" });

        // "sky" is an exact word (3), "skyline" starts with it (2); the unknown words create nothing.
        Assert.Equal(new[] { "sky", "skyline" }, Names(ranked));
        Assert.Equal(new[] { 3.0, 2.0 }, ranked.Select(item => item.Score));
        Assert.All(ranked, item => Assert.Equal(TagSuggestionOrigin.Own, item.Origin));
        // No tags at all: nothing to match and nothing invented.
        Assert.Empty(TagContextSuggestions.Rank(Snap(Array.Empty<PrivateTagDefinition>()), new TagContext { FileName = "sky.png", TitleTexts = new string?[] { "sky" } }));
    }

    [Fact]
    public void FileNameAndTitleAreSeparateSourcesThatAdd()
    {
        PrivateTagDefinition cat = Tag("Cat");
        PrivateTagStoreSnapshot snapshot = Snap(new[] { cat });

        RankedTagSuggestion ranked = TagContextSuggestions.Rank(snapshot, new TagContext { FileName = "cat.png", TitleTexts = new string?[] { "my CAT thread", null, "" } }).Single();

        Assert.Equal(new[] { TagSuggestionReason.FileName, TagSuggestionReason.ThreadTitle }, ranked.Reasons.ToArray());
        Assert.Equal(6, ranked.Score);
    }

    [Fact]
    public void FilenameTokenLimitZeroSwitchesWordMatchingOffButMarkersStay()
    {
        PrivateTagDefinition cat = Tag("cat"), rpt = Tag("/rpt/");
        PrivateTagStoreSnapshot snapshot = Snap(new[] { cat, rpt });
        TagContext context = new() { FileName = "cat.png", TitleTexts = new string?[] { "cat /rpt/" } };

        Assert.Equal(new[] { "/rpt/" }, Names(TagContextSuggestions.Rank(snapshot, context, options: new TagContextOptions(filenameTokens: 0))));
        Assert.Equal(new[] { "cat", "/rpt/" }.OrderBy(x => x), Names(TagContextSuggestions.Rank(snapshot, context)).OrderBy(x => x));
        // An existing slash tag counts like an exact word (3), not like a new marker (2).
        Assert.Equal(3, Find(TagContextSuggestions.Rank(snapshot, context, options: new TagContextOptions(filenameTokens: 0)), "/rpt/").Score);
    }

    [Fact]
    public void JoinedWordsMatchMultiWordTagsAndCjkRunsContainTagNames()
    {
        PrivateTagDefinition blueSky = Tag("blue sky"), photo = Tag("写真"), cat = Tag("猫"), whole = Tag("猫の写真"), kanji = Tag("猫の");
        PrivateTagStoreSnapshot snapshot = Snap(new[] { blueSky, photo, cat, whole, kanji });

        IReadOnlyList<RankedTagSuggestion> english = TagContextSuggestions.Rank(snapshot, new TagContext { FileName = "blue-sky.png" });
        Assert.Equal(new[] { "blue sky" }, Names(english));
        Assert.Equal(3, english[0].Score);

        IReadOnlyList<RankedTagSuggestion> cjk = TagContextSuggestions.Rank(snapshot, new TagContext { FileName = "猫の写真.jpg" });
        Assert.Equal(3, Find(cjk, "猫の写真").Score);        // the whole run is the tag
        Assert.Equal(2, Find(cjk, "写真").Score);            // contained in the run
        Assert.Equal(2, Find(cjk, "猫の").Score);            // a prefix of the run, and contained: 2 points once
        Assert.DoesNotContain(cjk, item => item.Name.Display == "猫"); // a single character inside a run is too weak
        // A lone character as the whole word is exact.
        Assert.Equal(3, Find(TagContextSuggestions.Rank(snapshot, new TagContext { FileName = "猫.jpg" }), "猫").Score);
    }

    [Fact]
    public void WordsAlsoMatchProviderSourceTagsWithoutDuplicatingOwnTags()
    {
        PrivateTagDefinition own = Tag("blue_hair");
        PrivateTagStoreSnapshot snapshot = Snap(new[] { own });
        TagSuggestionIndex sources = TagSuggestionIndex.FromSourceTags(new[] { ("blue_eyes", 5), ("blue_hair", 9), ("blue_sky", 1) });

        IReadOnlyList<RankedTagSuggestion> ranked = TagContextSuggestions.Rank(snapshot, new TagContext { FileName = "blue_eyes_final.png" }, sources: sources);

        RankedTagSuggestion eyes = Find(ranked, "blue_eyes");
        Assert.Equal((TagSuggestionOrigin.Source, (TagId?)null, 3.0), (eyes.Origin, eyes.Id, eyes.Score));
        Assert.Equal(2, Find(ranked, "blue_sky").Score);
        // The source "blue_hair" is the own tag: it appears once, as the own tag.
        RankedTagSuggestion hair = Find(ranked, "blue_hair");
        Assert.Equal((TagSuggestionOrigin.Own, (TagId?)own.Id), (hair.Origin, hair.Id));
        Assert.Equal(3, ranked.Count);
    }

    // ------------------------------------------------------------------ typed text

    [Fact]
    public void TypedTextKeepsTheGroupsAndLetsContextDecideInsideAGroup()
    {
        PrivateTagDefinition exact = Tag("cat"), few = Tag("catA"), many = Tag("catB"), word = Tag("big_cat");
        PrivateMediaTagTarget neighbour = Media("list", "2", 0, Hex('7'));
        PrivateTagStoreSnapshot snapshot = Snap(new[] { exact, few, many, word },
            threads: Enumerable.Range(0, 9).Select(i => (many, "t" + i)).ToArray(),
            media: new[] { (few, neighbour) });
        TagContext context = new() { Neighbours = TagNeighbourhood.From(new MediaTagSubject?[] { null, new MediaTagSubject.Bound(neighbour) }, 0) };

        // Without context nothing changes against the plain index: more uses first.
        TypedTagSuggestions plain = TagContextSuggestions.RankTyped(snapshot, "cat");
        Assert.Equal(Names(TagSuggestionIndex.For(snapshot).Match("cat").Select(item => new RankedTagSuggestion(item.Name, item.Id, TagSuggestionOrigin.Own, item.Uses, 0, Array.Empty<TagSuggestionContribution>()))),
            Names(plain.Own));
        Assert.Equal(new[] { "cat", "catB", "catA", "big_cat" }, Names(plain.Own));

        // The neighbour tag leads its group, the exact name still leads everything.
        TypedTagSuggestions typed = TagContextSuggestions.RankTyped(snapshot, "cat", context);
        Assert.Equal(new[] { "cat", "catA", "catB", "big_cat" }, Names(typed.Own));
        Assert.Equal(new TagMatchKind?[] { TagMatchKind.Exact, TagMatchKind.Prefix, TagMatchKind.Prefix, TagMatchKind.WordPrefix }, typed.Own.Select(item => item.TypedMatch));
        Assert.Equal(TagSuggestionReason.Neighbour, Find(typed.Own, "catA").PrimaryReason);
        Assert.Empty(typed.Source);
        // Blank text and oversized text list nothing; the limit cuts after ordering.
        Assert.Empty(TagContextSuggestions.RankTyped(snapshot, "  ", context).Own);
        Assert.Empty(TagContextSuggestions.RankTyped(snapshot, new string('a', PrivateTagName.MaximumUtf16CodeUnits + 1), context).Own);
        Assert.Equal(new[] { "cat", "catA" }, Names(TagContextSuggestions.RankTyped(snapshot, "cat", context, ownLimit: 2).Own));
    }

    [Fact]
    public void TypedSourceTagsAreOrderedByContextToo()
    {
        PrivateTagStoreSnapshot snapshot = Snap(Array.Empty<PrivateTagDefinition>());
        TagSuggestionIndex sources = TagSuggestionIndex.FromSourceTags(new[] { ("blue_eyes", 50), ("blue_hair", 2), ("bluebird", 9) });

        TypedTagSuggestions typed = TagContextSuggestions.RankTyped(snapshot, "blue", new TagContext { SourceTags = new[] { "blue_hair" } }, sources: sources);

        // Prefix group: the tag on this very medium first, then by posts; all are source tags that would create an own tag.
        Assert.Equal(new[] { "blue_hair", "blue_eyes", "bluebird" }, Names(typed.Source));
        Assert.All(typed.Source, item => Assert.Equal(TagSuggestionOrigin.Source, item.Origin));
        Assert.Empty(typed.Own);
    }

    // R05 (review 14.11. night): typed source matches stop early instead of copying the whole catalog per keystroke, and the
    // result is exactly the one of an unbounded match (context candidates included, wherever they rank in the catalog).
    [Fact]
    public void TypedSourceTagsStopEarlyInALargeCatalogAndMatchTheUnboundedRanking()
    {
        PrivateTagDefinition own = Tag("a_own");
        PrivateTagStoreSnapshot snapshot = Snap(new[] { own });
        TagSuggestionIndex sources = TagSuggestionIndex.FromSourceTags(Enumerable.Range(0, 50_000)
            .Select(i => ("a" + i.ToString("00000"), i % 97 + 1)).Append(("x_a_word", 3)).Append(("a_own", 500)));
        TagContext context = new() { SourceTags = new[] { "a49999", "a12345", "x_a_word", "a_own" }, FileName = "a00007.png" };

        TypedTagSuggestions bounded = default!, unbounded = default!;
        for (int round = 0; round < 2; round++) // the second round measures with every cache warm
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            bounded = TagContextSuggestions.RankTyped(snapshot, "a", context, sources: sources);
            long boundedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread();
            unbounded = TagContextSuggestions.RankTyped(snapshot, "a", context, sources: sources, sourceLimit: 100_000);
            long unboundedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            if (round == 1) Assert.True(boundedBytes * 8 < unboundedBytes, $"bounded {boundedBytes} B, unbounded {unboundedBytes} B");
        }

        Assert.Equal(TagSuggestionIndex.SourceLimit, bounded.Source.Count);
        Assert.Equal(unbounded.Source.Take(TagSuggestionIndex.SourceLimit).Select(item => (item.Name.Display, item.Score, item.Uses, item.TypedMatch)),
            bounded.Source.Select(item => (item.Name.Display, item.Score, item.Uses, item.TypedMatch)));
        // The context tags lead the prefix group although the catalog ranks them far behind; an own tag is never a source row.
        Assert.Contains("a49999", Names(bounded.Source));
        Assert.Contains("a12345", Names(bounded.Source));
        Assert.DoesNotContain(bounded.Source, item => item.Name.Display == "a_own");
        Assert.Equal(new[] { "a_own" }, Names(bounded.Own));
        // With a limit of one the strongest context tag still wins over the most used catalog names.
        Assert.Equal(Names(unbounded.Source.Take(1)), Names(TagContextSuggestions.RankTyped(snapshot, "a", context, sources: sources, sourceLimit: 1).Source));
    }

    [Fact]
    public void MatchKeyListsANameExactlyAsMatchDoes()
    {
        TagSuggestionIndex sources = TagSuggestionIndex.FromSourceTags(new[] { ("blue_eyes", 5), ("light_blue", 2), ("blue", 1), ("sky", 4) });
        foreach (string text in new[] { "blue", "BLUE", "eyes", "ue", "lu" })
            foreach (TagSuggestion item in sources.Match(text))
                Assert.Equal(item, sources.MatchKey(text, item.Name.Key));
        Assert.Null(sources.MatchKey("blue", "SKY"));
        Assert.Null(sources.MatchKey("blue", "MISSING"));
        Assert.Null(sources.MatchKey("  ", "BLUE"));
    }

    // ------------------------------------------------------------------ order, limits, empty data

    [Fact]
    public void TiesBreakByUsesThenNameAndTheOrderIsDeterministic()
    {
        PrivateTagDefinition zebra = Tag("zebra"), apple = Tag("Apple"), banana = Tag("banana"), heavy = Tag("heavy");
        PrivateTagStoreSnapshot snapshot = Snap(new[] { zebra, apple, banana, heavy },
            threads: new[] { (heavy, "1"), (heavy, "2"), (heavy, "3"), (heavy, "4"), (heavy, "5"), (heavy, "6"), (heavy, "7"), (heavy, "8") });
        TagContext context = new() { Recent = new[] { zebra.Id, apple.Id, banana.Id, heavy.Id } };

        // Recent ranks 1–3 are 4 points each, rank 4 is 3 plus the capped noise (3) = 6: heavy leads on score; the rest tie
        // on score and uses and fall back to the name, case-insensitively.
        IReadOnlyList<RankedTagSuggestion> first = TagContextSuggestions.Rank(snapshot, context);
        Assert.Equal(new[] { "heavy", "Apple", "banana", "zebra" }, Names(first));
        for (int run = 0; run < 5; run++) Assert.Equal(Names(first), Names(TagContextSuggestions.Rank(snapshot, context)));
        // Equal score and equal uses: the name decides, whatever the vocabulary order was.
        PrivateTagStoreSnapshot reversed = Snap(new[] { heavy, banana, apple, zebra }, threads: new[] { (heavy, "1") });
        Assert.Equal(new[] { "Apple", "banana", "zebra" }, Names(TagContextSuggestions.Rank(reversed, context).Where(item => item.Id != heavy.Id)));
    }

    [Fact]
    public void TheSuggestionCountIsClampedAndLimitOverridesIt()
    {
        PrivateTagDefinition[] tags = Enumerable.Range(0, 40).Select(i => Tag("tag" + i.ToString("00"))).ToArray();
        PrivateTagStoreSnapshot snapshot = Snap(tags);
        TagContext context = new() { Recent = tags.Select(tag => tag.Id).ToArray() };

        Assert.Equal(TagContextOptions.DefaultSuggestionCount, TagContextSuggestions.Rank(snapshot, context).Count);
        Assert.Equal(TagContextOptions.MinimumSuggestionCount, TagContextSuggestions.Rank(snapshot, context, options: new TagContextOptions(suggestionCount: 0)).Count);
        Assert.Equal(TagContextOptions.MaximumSuggestionCount, TagContextSuggestions.Rank(snapshot, context, options: new TagContextOptions(suggestionCount: 500)).Count);
        Assert.Equal(25, TagContextSuggestions.Rank(snapshot, context, limit: 25).Count);
        Assert.Empty(TagContextSuggestions.Rank(snapshot, context, limit: 0));
        Assert.Empty(TagContextSuggestions.Rank(snapshot, context, limit: -3));
    }

    [Fact]
    public void OptionsClampEveryLimitIntoItsDocumentedRange()
    {
        TagContextOptions defaults = TagContextOptions.Default;
        Assert.Equal((9, 50, 2000, 8, 24), (defaults.SuggestionCount, defaults.RecentCount, defaults.NeighbourScan, defaults.FilenameTokens, defaults.SourceTagsPerMedium));

        TagContextOptions low = new(int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue);
        Assert.Equal((3, 0, 100, 0, 0), (low.SuggestionCount, low.RecentCount, low.NeighbourScan, low.FilenameTokens, low.SourceTagsPerMedium));
        TagContextOptions high = new(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue);
        Assert.Equal((18, 500, 20_000, 32, 200), (high.SuggestionCount, high.RecentCount, high.NeighbourScan, high.FilenameTokens, high.SourceTagsPerMedium));
    }

    // R19 (review 14.11. night): one source of truth for the suggestion count of the surface and of the context ranking.
    [Fact]
    public void TheSuggestionCountRangeIsTheQuickTagSettingsRange()
    {
        Assert.Equal((QuickTagOptions.DefaultSuggestionCount, QuickTagOptions.MinimumSuggestionCount, QuickTagOptions.MaximumSuggestionCount),
            (TagContextOptions.DefaultSuggestionCount, TagContextOptions.MinimumSuggestionCount, TagContextOptions.MaximumSuggestionCount));
        Assert.Equal(QuickTagOptions.Default.SuggestionCount, TagContextOptions.Default.SuggestionCount);
    }

    [Fact]
    public void SourceTagsPerMediumIsBoundedAndSkipsInvalidNames()
    {
        PrivateTagStoreSnapshot snapshot = Snap(Array.Empty<PrivateTagDefinition>());
        string[] names = Enumerable.Range(0, 300).Select(i => "src" + i.ToString("000")).Prepend("bad\tname").Prepend(new string('x', 5000)).ToArray();

        IReadOnlyList<RankedTagSuggestion> ranked = TagContextSuggestions.Rank(snapshot, new TagContext { SourceTags = names },
            options: new TagContextOptions(suggestionCount: 18, sourceTagsPerMedium: 200), limit: 1000);

        // 200 entries are looked at, two of them invalid: 198 names are candidates, in order, none beyond the cap.
        Assert.Equal(198, ranked.Count);
        Assert.Contains(ranked, item => item.Name.Display == "src197");
        Assert.DoesNotContain(ranked, item => item.Name.Display == "src198");
        Assert.Empty(TagContextSuggestions.Rank(snapshot, new TagContext { SourceTags = names }, options: new TagContextOptions(sourceTagsPerMedium: 0)));
    }

    [Fact]
    public void EmptyDataGivesAnEmptyListAndNullsThrowOnlyWhereRequired()
    {
        PrivateTagStoreSnapshot empty = Snap(Array.Empty<PrivateTagDefinition>());

        Assert.Empty(TagContextSuggestions.Rank(empty));
        Assert.Empty(TagContextSuggestions.Rank(empty, TagContext.Empty, new HashSet<TagId>(), TagContextOptions.Default));
        Assert.Empty(TagContextSuggestions.RankTyped(empty, "x").Own);
        Assert.Empty(TagContextSuggestions.RankTyped(empty, null).Source);
        // A vocabulary without any assignment or context gives nothing either (no zero-score noise).
        Assert.Empty(TagContextSuggestions.Rank(Snap(new[] { Tag("a"), Tag("b") })));
        Assert.Throws<ArgumentNullException>(() => TagContextSuggestions.Rank(null!));
        Assert.Throws<ArgumentNullException>(() => TagContextIndex.For(null!));
        // Odd inputs inside the context do not throw.
        PrivateTagStoreSnapshot snapshot = Snap(new[] { Tag("a") });
        TagContext odd = new() { Sha256 = "not-a-hash", FileName = "\0\0", TitleTexts = new string?[] { null }, SourceTags = new[] { "", " " }, Recent = new[] { new TagId(Guid.NewGuid()) } };
        Assert.Empty(TagContextSuggestions.Rank(snapshot, odd));
    }

    [Fact]
    public void ReasonKeysAreStableAndDistinct()
    {
        string[] keys = Enum.GetValues<TagSuggestionReason>().Select(TagSuggestionReasons.LocalizationKey).ToArray();

        Assert.Equal(Enum.GetValues<TagSuggestionReason>().Length, keys.Distinct().Count());
        Assert.All(keys, key => Assert.StartsWith("tags.quick.reason.", key));
        Assert.Equal("tags.quick.reason.sameFile", TagSuggestionReasons.LocalizationKey(TagSuggestionReason.SameFile));
        Assert.Throws<ArgumentOutOfRangeException>(() => TagSuggestionReasons.LocalizationKey((TagSuggestionReason)99));
    }

    // ------------------------------------------------------------------ threading and cancellation

    [Fact]
    public async Task RankingIsPureAndSafeToRunOnSeveralThreadsAtOnce()
    {
        PrivateTagDefinition[] tags = Enumerable.Range(0, 60).Select(i => Tag("t" + i.ToString("00"))).ToArray();
        PrivateTagStoreSnapshot snapshot = Snap(tags, threads: tags.Select((tag, i) => (tag, "th" + (i % 5))).ToArray(),
            media: tags.Select((tag, i) => (tag, Media("th" + (i % 5), i.ToString(), 0, Hex('7')))).ToArray());
        TagContext context = new() { Thread = Thread("th1"), Recent = tags.Take(5).Select(tag => tag.Id).ToArray(), FileName = "t07_t08.png" };

        string[] expected = Names(TagContextSuggestions.Rank(snapshot, context));
        string[][] results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => Names(TagContextSuggestions.Rank(snapshot, context)))));

        Assert.All(results, result => Assert.Equal(expected, result));
        Assert.Same(TagContextIndex.For(snapshot), TagContextIndex.For(snapshot));
    }

    [Fact]
    public void ACancelledTokenStopsTheRankingWithoutAPartialResult()
    {
        PrivateTagStoreSnapshot snapshot = Snap(new[] { Tag("a") });
        using CancellationTokenSource source = new();
        source.Cancel();

        Assert.Throws<OperationCanceledException>(() => TagContextSuggestions.Rank(snapshot, new TagContext { Thread = Thread("x") }, cancellation: source.Token));
        Assert.Throws<OperationCanceledException>(() => TagContextSuggestions.RankTyped(snapshot, "a", cancellation: source.Token));
        Assert.Throws<OperationCanceledException>(() => TagContextTokenizer.Tokenize("abc def", 4, cancellation: source.Token));
    }

    [Fact]
    public void ACancelledIndexBuildIsNotCachedAndTheNextBuildSucceeds()
    {
        PrivateTagDefinition tag = Tag("t");
        PrivateTagStoreSnapshot snapshot = Snap(new[] { tag }, media: Enumerable.Range(0, 3000).Select(i => (tag, Media("th", i.ToString(), 0, Hex('7')))));
        using CancellationTokenSource source = new();
        source.Cancel();

        Assert.Throws<OperationCanceledException>(() => TagContextIndex.For(snapshot, source.Token));
        Assert.Equal(3000, TagContextIndex.For(snapshot).ThreadMediaTags(Thread("th"))[tag.Id]);
    }

    // ------------------------------------------------------------------ the index itself

    [Fact]
    public void TheIndexAnswersLikeTheSnapshotWithoutScanning()
    {
        PrivateTagDefinition a = Tag("a"), b = Tag("b");
        PrivateMediaTagTarget one = Media("th", "1", 0, Hex('c')), two = Media("th", "2", 0, Hex('2')), three = Media("th", "3", 0, Hex('3'));
        PrivateTagStoreSnapshot snapshot = Snap(new[] { a, b }, threads: new[] { (a, "th") },
            media: new[] { (a, one), (b, one), (b, two) });
        TagContextIndex index = TagContextIndex.For(snapshot);

        Assert.Same(index, TagContextIndex.For(snapshot));
        Assert.NotSame(index, TagContextIndex.For(Snap(new[] { a })));
        foreach (PrivateMediaTagTarget target in new[] { one, two, three })
            Assert.Equal(snapshot.TagsOf(target).Select(item => item.Id).OrderBy(id => id.Value),
                index.TagsOf(new MediaTagSubject.Bound(target)).OrderBy(id => id.Value));
        Assert.Equal(new[] { a.Id }, index.ThreadTags(Thread("th")));
        Assert.Empty(index.ThreadTags(Thread("other")));
        Assert.Empty(index.ThreadTags(null));
        Assert.Equal(new[] { a.Id, b.Id }.OrderBy(id => id.Value), index.TagsOfHash(Hex('c')).OrderBy(id => id.Value));
        Assert.Equal(index.TagsOfHash(Hex('c')), index.TagsOfHash(new string('c', 64).ToUpperInvariant()));
        Assert.Empty(index.TagsOfHash("short"));
        Assert.Empty(index.TagsOfHash(null));
        Assert.Equal(a.Id, index.FindByKey(a.Name.Key)!.Id);
        Assert.Null(index.FindByKey("MISSING"));
        Assert.True(index.ApproximateEntries > 0);
    }

    [Fact]
    public void NameLookupFindsExactAndPrefixNamesInBoundedTime()
    {
        TagNameLookup lookup = new(new[] { "apple", "apricot", "app", "banana", "apple" }.Select(name =>
        {
            Assert.True(PrivateTagName.TryCreate(name, out PrivateTagName? parsed, out _));
            return new TagNameEntry(parsed!.Key, parsed, null, 0);
        }));

        Assert.Equal(4, lookup.Count);
        Assert.True(lookup.TryExact("APP", out TagNameEntry exact));
        Assert.Equal("app", exact.Name.Display);
        Assert.False(lookup.TryExact("AP", out _));
        List<TagNameEntry> hits = new();
        lookup.Prefix("AP", 100, hits);
        Assert.Equal(new[] { "app", "apple", "apricot" }, hits.Select(item => item.Name.Display));
        hits.Clear();
        lookup.Prefix("APP", 100, hits); // the exact name itself is not a prefix hit
        Assert.Equal(new[] { "apple" }, hits.Select(item => item.Name.Display));
        hits.Clear();
        lookup.Prefix("AP", 1, hits);
        Assert.Single(hits);
        hits.Clear();
        lookup.Prefix("", 10, hits);
        lookup.Prefix("ZZ", 10, hits);
        Assert.Empty(hits);
    }

    // ------------------------------------------------------------------ recent list

    [Fact]
    public void TheRecentListKeepsMostRecentFirstWithoutDuplicatesWithinItsCapacity()
    {
        TagRecentList recent = new(3);
        TagId a = new(Guid.NewGuid()), b = new(Guid.NewGuid()), c = new(Guid.NewGuid()), d = new(Guid.NewGuid());

        recent.Touch(new[] { a, b });
        Assert.Equal(new[] { b, a }, recent.Snapshot());
        recent.Touch(a);
        Assert.Equal(new[] { a, b }, recent.Snapshot());
        recent.Touch(new[] { c, d });
        Assert.Equal(new[] { d, c, a }, recent.Snapshot());
        IReadOnlyList<TagId> before = recent.Snapshot();
        recent.Touch(b);
        Assert.Equal(new[] { d, c, a }, before); // an earlier snapshot never changes
        recent.Remove(c);
        Assert.Equal(new[] { b, d }, recent.Snapshot());
        recent.Capacity = 1;
        Assert.Equal(new[] { b }, recent.Snapshot());
        recent.Capacity = 100_000;
        Assert.Equal(TagContextOptions.MaximumRecentCount, recent.Capacity);
        recent.Capacity = -1;
        Assert.Equal(0, recent.Capacity);
        recent.Touch(a);
        Assert.Empty(recent.Snapshot());
        Assert.Throws<ArgumentNullException>(() => recent.Touch((IEnumerable<TagId>)null!));
    }

    [Fact]
    public async Task TheRecentListIsSafeUnderConcurrentUse()
    {
        TagRecentList recent = new(50);
        TagId[] ids = Enumerable.Range(0, 200).Select(_ => new TagId(Guid.NewGuid())).ToArray();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 500; i++)
            {
                recent.Touch(ids[(i * 7 + worker) % ids.Length]);
                IReadOnlyList<TagId> snapshot = recent.Snapshot();
                Assert.True(snapshot.Count <= 50 && snapshot.Distinct().Count() == snapshot.Count);
            }
        })));

        Assert.Equal(50, recent.Snapshot().Count);
    }

    // ------------------------------------------------------------------ tokenizer

    private static string[] Words(string? text, int max = 8, bool file = false) => TagContextTokenizer.Tokenize(text, max, file).Words.ToArray();

    [Fact]
    public void TokenizerSplitsAtSeparatorsDropsShortNumericAndExtension()
    {
        Assert.Equal(new[] { "IMG", "Cool", "Cat", "final" }, Words("IMG_20240101_Cool-Cat (final)[x].JPG", file: true));
        Assert.Empty(Words("1699999999123.jpg", file: true));                // 4chan timestamp
        Assert.Empty(Words("ab.cd (e)", file: true));                        // shorter than three characters
        Assert.Equal(new[] { "Mr", "Smith" }.Where(word => word.Length >= 3), Words("Mr.Smith", file: true)); // not a media extension
        Assert.Equal(new[] { "archive", "tar" }, Words("archive.tar.gz", file: true));
        Assert.Equal(new[] { "blue" }, Words("C:\\pics\\blue.png", file: true));
        Assert.Equal(new[] { "blue" }, Words("/home/x/y/blue.png", file: true));
        // Without the file-name rule a title keeps the extension word and the directory words.
        Assert.Equal(new[] { "blue", "png" }, Words("blue.png"));
        Assert.Equal(new[] { "cat" }, Words("cat_CAT_Cat"));                 // each word once, case-insensitively
        Assert.Equal(new[] { "2girls", "4koma" }, Words("2girls 4koma 1234"));
        Assert.Empty(Words("１２３４ ５６７８"));                              // full-width digits are numbers too
    }

    [Fact]
    public void TokenizerHandlesUnicodeCombiningMarksCjkAndBrokenText()
    {
        Assert.Equal(new[] { "Привет", "мир", "котик" }, Words("Привет-мир_котик.png", file: true));
        Assert.Equal(new[] { "café", "lait" }, Words("cafe\u0301_au_lait.png", file: true));   // NFC, mark stays with its letter
        Assert.Equal(new[] { "猫の写真" }, Words("猫の写真.jpg", file: true));                  // one run, no spaces
        Assert.Equal(new[] { "猫" }, Words("猫.jpg", file: true));                             // a single CJK character is a word
        Assert.Equal(new[] { "犬", "猫" }, Words("犬 猫"));
        Assert.Equal(new[] { "고양이", "사진" }, Words("고양이_사진.png", file: true));
        Assert.Equal(new[] { "猫cat" }, Words("猫cat"));                                      // mixed scripts: ordinary word rules
        Assert.Equal(new[] { "cat" }, Words("🐱🐱🐱 cat 😀"));                                   // emoji separate
        Assert.Equal(new[] { "cde" }, Words("ab\uD800cde"));                                  // a lone surrogate separates, never throws
        Assert.Equal(new[] { "𠮷野家" }, Words("𠮷野家.png", file: true));                       // astral Han stays whole
        Assert.Empty(Words("\u0301\u0301 ..", file: true));                                    // stray marks and punctuation only
    }

    [Fact]
    public void TokenizerPhrasesJoinNeighbouringWordsAndLimitsApply()
    {
        TagTokens tokens = TagContextTokenizer.Tokenize("end_of_world.png", 8, isFileName: true);

        Assert.Equal(new[] { "end", "world" }, tokens.Words);
        Assert.Equal(new[] { "end_of", "of_world" }, tokens.Phrases);      // short words still join their neighbours
        Assert.Empty(TagContextTokenizer.Tokenize("猫 犬", 8).Phrases);        // never joined across written-together scripts

        string many = string.Join(' ', Enumerable.Range(0, 40).Select(i => "word" + (char)('a' + i % 26) + (char)('a' + i / 26)));
        TagTokens limited = TagContextTokenizer.Tokenize(many, 5);
        Assert.Equal(5, limited.Words.Count);
        Assert.Equal(5, limited.Phrases.Count);
        Assert.Same(TagTokens.Empty, TagContextTokenizer.Tokenize(many, 0));
        Assert.Same(TagTokens.Empty, TagContextTokenizer.Tokenize(null, 5));
        Assert.Same(TagTokens.Empty, TagContextTokenizer.Tokenize("", 5));
        Assert.Same(TagTokens.Empty, TagContextTokenizer.Tokenize("abc", -1));
        // Text past the scan ceiling is not read; a word longer than 64 characters is skipped.
        Assert.Empty(Words(new string(' ', TagContextTokenizer.MaximumScannedCharacters) + "hidden"));
        Assert.Equal(new[] { "short" }, Words(new string('x', TagContextTokenizer.MaximumWordScalars + 1) + " short"));
        Assert.Equal(new string('x', TagContextTokenizer.MaximumWordScalars), Words(new string('x', TagContextTokenizer.MaximumWordScalars)).Single());
    }

    [Fact]
    public void TokenizerIsCjkRecognisesWrittenTogetherScriptsOnly()
    {
        Assert.True(TagContextTokenizer.IsCjk("猫の写真"));
        Assert.True(TagContextTokenizer.IsCjk("カタカナー"));
        Assert.True(TagContextTokenizer.IsCjk("한글"));
        Assert.False(TagContextTokenizer.IsCjk("猫cat"));
        Assert.False(TagContextTokenizer.IsCjk(""));
        Assert.False(TagContextTokenizer.IsCjk("привет"));
    }

    // ------------------------------------------------------------------ budget

    [Fact]
    public void BudgetIndexBuildAndRankingStayFarInsideGenerousCiBounds()
    {
        // The plan's reference sizes: 1,024 tags, 50,000 media assignments, a gallery list of 5,000.
        PrivateTagDefinition[] tags = Enumerable.Range(0, 1024).Select(i => Tag("tag_" + i.ToString("0000"))).ToArray();
        PrivateMediaTagTarget[] targets = Enumerable.Range(0, 50_000).Select(i => Media("th" + (i % 500), (i / 500).ToString(), 0, i.ToString("x64"))).ToArray();
        List<(PrivateTagDefinition, PrivateMediaTagTarget)> media = new(50_000);
        for (int i = 0; i < targets.Length; i++) media.Add((tags[(i * 31) % tags.Length], targets[i]));
        PrivateTagStoreSnapshot snapshot = Snap(tags, threads: Enumerable.Range(0, 2000).Select(i => (tags[i % tags.Length], "th" + (i % 500))).ToArray(), media: media);
        MediaTagSubject?[] gallery = targets.Take(5000).Select(target => (MediaTagSubject?)new MediaTagSubject.Bound(target)).ToArray();
        TagContext context = new()
        {
            Thread = Thread("th7"), Sha256 = 777.ToString("x64"), FileName = "tag_0012_tag_0013_IMG_20240101.jpg",
            TitleTexts = new string?[] { "Wallpaper thread /wg/ tag_0100 and more words in the title" },
            Neighbours = TagNeighbourhood.From(gallery, 2500),
            Recent = tags.Take(50).Select(tag => tag.Id).ToArray(),
            SourceTags = Enumerable.Range(0, 24).Select(i => "tag_" + (i * 7).ToString("0000")).ToArray()
        };
        HashSet<TagId> assigned = new() { tags[3].Id };

        Stopwatch watch = Stopwatch.StartNew();
        TagContextIndex index = TagContextIndex.For(snapshot);
        long buildMs = watch.ElapsedMilliseconds;
        _ = TagSuggestionIndex.UsesOf(snapshot);
        _ = TagSuggestionIndex.For(snapshot);
        watch.Restart();
        const int Runs = 20;
        IReadOnlyList<RankedTagSuggestion> last = Array.Empty<RankedTagSuggestion>();
        for (int i = 0; i < Runs; i++) last = TagContextSuggestions.Rank(snapshot, context, assigned);
        double rankMs = watch.Elapsed.TotalMilliseconds / Runs;
        watch.Restart();
        TypedTagSuggestions typed = default!;
        for (int i = 0; i < Runs; i++) typed = TagContextSuggestions.RankTyped(snapshot, "tag_00", context, assigned);
        double typedMs = watch.Elapsed.TotalMilliseconds / Runs;
        _output.WriteLine($"index build {buildMs} ms ({index.ApproximateEntries} entries); rank {rankMs:F2} ms; typed {typedMs:F2} ms");

        Assert.Equal(TagContextOptions.DefaultSuggestionCount, last.Count);
        Assert.Equal(TagSuggestionIndex.OwnLimit, typed.Own.Count);
        Assert.DoesNotContain(last, item => item.Id == tags[3].Id);
        // The plan budgets ~30 ms for the build and ≤ 10 ms per medium change; CI machines are slow and shared, so the
        // bounds below are one to two orders of magnitude looser and only catch accidental O(n²) work.
        Assert.True(buildMs < 5000, $"index build took {buildMs} ms");
        Assert.True(rankMs < 250, $"rank took {rankMs:F1} ms");
        Assert.True(typedMs < 250, $"typed rank took {typedMs:F1} ms");
    }
}

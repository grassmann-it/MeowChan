using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// MP4: the rows of the Booru mode's tag list (MediaTagList): groups, order, top-N with "Show all", the search box, the tags the filter
// holds, the gestures (click, Alt+click, Enter, Shift+Enter) and what a screen reader hears. Pure: no controls.
[Collection("Localization")]
public sealed class MediaTagListTests : IDisposable
{
    public MediaTagListTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static readonly Guid Cats = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Dogs = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid Birds = Guid.Parse("00000000-0000-0000-0000-0000000000a3");
    private static readonly Guid Gone = Guid.Parse("00000000-0000-0000-0000-0000000000a4");

    private static MediaTagCount Own(Guid id, string name, int count) => new(MediaTagGroup.Own, id.ToString("D"), name, count);
    private static MediaTagCount Source(string name, int count) => new(MediaTagGroup.Source, name, name, count);

    private static MediaTagCountResult Counts(int sourceMedia = 0, params MediaTagCount[] counts) =>
        new(counts.Where(count => count.Group == MediaTagGroup.Own).ToArray(), counts.Where(count => count.Group == MediaTagGroup.Source).ToArray(),
            10, sourceMedia, true, true);

    private static MediaTagListOptions Options(MediaTagListSort sort = MediaTagListSort.Count, string? query = null, int top = 30,
        MediaTagGroup[]? showAll = null, MediaTagGroup[]? collapsed = null) =>
        new(sort, query, top, (showAll ?? []).ToHashSet(), (collapsed ?? []).ToHashSet());

    private static string? Name(Guid id) => id == Cats ? "cats" : id == Dogs ? "dogs" : id == Birds ? "birds" : null;

    private static string[] Tags(IReadOnlyList<MediaTagListItem> rows, MediaTagGroup group) =>
        rows.Where(row => row.Kind == MediaTagListKind.Tag && row.Group == group).Select(row => row.Name).ToArray();

    [Fact]
    public void OwnTagsComeFirstMostFrequentFirstWithTiesByNameAndSourceTagsOnlyWithProviderMedia()
    {
        MediaTagCountResult counts = Counts(0, Own(Cats, "cats", 5), Own(Dogs, "dogs", 9), Own(Birds, "birds", 5));
        IReadOnlyList<MediaTagListItem> rows = MediaTagList.Arrange(counts, [], Options(), Name);
        Assert.Equal(["dogs", "birds", "cats"], Tags(rows, MediaTagGroup.Own));
        Assert.Equal(MediaTagListKind.Header, rows[0].Kind);
        Assert.Equal("Own tags (3)", rows[0].Name);
        Assert.DoesNotContain(rows, row => row.Group == MediaTagGroup.Source); // no provider media, no source tag in the filter
        Assert.Equal(["birds", "cats", "dogs"], Tags(MediaTagList.Arrange(counts, [], Options(MediaTagListSort.Name), Name), MediaTagGroup.Own));
        IReadOnlyList<MediaTagListItem> withSource = MediaTagList.Arrange(Counts(2, Own(Cats, "cats", 1), Source("cat_ears", 2)), [], Options(), Name);
        Assert.Equal("Danbooru tags (1)", withSource.Single(row => row is { Kind: MediaTagListKind.Header, Group: MediaTagGroup.Source }).Name);
        Assert.Equal(["cat_ears"], Tags(withSource, MediaTagGroup.Source));
        Assert.Equal(MediaFilterTerm.SourceTag("cat_ears"), withSource.Single(row => row.Name == "cat_ears").Term);
        Assert.Equal(MediaFilterTerm.Tag(Cats), withSource.Single(row => row.Name == "cats").Term);
    }

    [Fact]
    public void TheTopNIsTheSettingAndShowAllListsTheRestUntilShowFewer()
    {
        MediaTagCount[] many = Enumerable.Range(0, 12).Select(i => Source("tag_" + i.ToString("D2"), 100 - i)).ToArray();
        MediaTagCountResult counts = Counts(5, many);
        IReadOnlyList<MediaTagListItem> top = MediaTagList.Arrange(counts, [], Options(top: 5), Name);
        Assert.Equal(["tag_00", "tag_01", "tag_02", "tag_03", "tag_04"], Tags(top, MediaTagGroup.Source));
        MediaTagListItem more = top.Single(row => row is { Kind: MediaTagListKind.More, Group: MediaTagGroup.Source });
        Assert.Equal(("Show all (12)", 7, false), (more.Name, more.Hidden, more.ShowingAll));
        IReadOnlyList<MediaTagListItem> all = MediaTagList.Arrange(counts, [], Options(top: 5, showAll: [MediaTagGroup.Source]), Name);
        Assert.Equal(12, Tags(all, MediaTagGroup.Source).Length);
        Assert.Equal("Show fewer", all.Single(row => row.Kind == MediaTagListKind.More).Name);
        // Fewer tags than the top: no switch at all.
        Assert.DoesNotContain(MediaTagList.Arrange(counts, [], Options(top: 30), Name), row => row.Kind == MediaTagListKind.More);
    }

    [Fact]
    public void TheSearchBoxFindsAnyPartOfANameIgnoringCaseAndUnderscoresAndSaysWhenNothingMatches()
    {
        MediaTagCountResult counts = Counts(3, Source("blue_eyes", 3), Source("cat_ears", 2), Own(Cats, "Cats", 1));
        IReadOnlyList<MediaTagListItem> rows = MediaTagList.Arrange(counts, [], Options(query: "BLUE EY"), Name);
        Assert.Equal(["blue_eyes"], Tags(rows, MediaTagGroup.Source));
        Assert.Empty(Tags(rows, MediaTagGroup.Own));
        Assert.Contains(rows, row => row is { Kind: MediaTagListKind.Note, Group: MediaTagGroup.Own } && row.Name == "No tag matches “BLUE EY”.");
        Assert.Equal(["Cats", "cat_ears"], MediaTagList.Arrange(counts, [], Options(query: "cat"), Name).Where(row => row.Kind == MediaTagListKind.Tag).Select(row => row.Name));
        Assert.True(MediaTagList.Matches("long_hair", "g h"));
        Assert.False(MediaTagList.Matches("long_hair", "short"));
    }

    [Fact]
    public void TheTagsOfTheFilterStayOnTopWithTheirStateEvenWithoutCountOrSearchMatch()
    {
        MediaTagCountResult counts = Counts(1, Own(Cats, "cats", 4), Own(Dogs, "dogs", 9), Source("solo", 1));
        MediaFilterTerm[] terms = [MediaFilterTerm.Tag(Cats), MediaFilterTerm.Tag(Birds, exclude: true), MediaFilterTerm.Tag(Gone), MediaFilterTerm.SourceTag("comic", exclude: true)];
        IReadOnlyList<MediaTagListItem> rows = MediaTagList.Arrange(counts, terms, Options(query: "zzz"), Name);
        MediaTagListItem[] own = rows.Where(row => row is { Kind: MediaTagListKind.Tag, Group: MediaTagGroup.Own }).ToArray();
        Assert.Equal([("cats", 4, 1), ("(deleted tag)", 0, 1), ("birds", 0, -1)], own.Select(row => (row.Name, row.Count, row.Polarity)));
        MediaTagListItem comic = rows.Single(row => row is { Kind: MediaTagListKind.Tag, Group: MediaTagGroup.Source });
        Assert.Equal(("comic", 0, -1), (comic.Name, comic.Count, comic.Polarity));
        // The row's term is the included value, whatever the filter holds: the gesture decides the polarity.
        Assert.False(own[2].Term!.Exclude);
        Assert.Equal("cats, 4 media, included", own[0].AccessibleName);
        Assert.Equal("birds, 0 media, excluded", own[2].AccessibleName);
        Assert.Equal("solo, 1 medium", MediaTagList.Arrange(counts, [], Options(), Name).Single(row => row.Name == "solo").AccessibleName);
    }

    [Fact]
    public void ACollapsedGroupShowsItsHeadingOnlyAndSaysSo()
    {
        MediaTagCountResult counts = Counts(1, Own(Cats, "cats", 4), Source("solo", 1));
        IReadOnlyList<MediaTagListItem> rows = MediaTagList.Arrange(counts, [], Options(collapsed: [MediaTagGroup.Own]), Name);
        Assert.Empty(Tags(rows, MediaTagGroup.Own));
        MediaTagListItem header = rows.First();
        Assert.True(header.Collapsed);
        Assert.Equal("Own tags (1), collapsed", header.AccessibleName);
        Assert.Equal(["solo"], Tags(rows, MediaTagGroup.Source));
        Assert.Equal("Danbooru tags (1), expanded", rows.Single(row => row is { Kind: MediaTagListKind.Header, Group: MediaTagGroup.Source }).AccessibleName);
    }

    [Fact]
    public void AnUnavailableGroupOrAnEmptyResultSaysSoInsteadOfShowingNothing()
    {
        MediaTagCountResult unavailable = new([], [], 3, 2, OwnAvailable: false, SourceAvailable: false);
        IReadOnlyList<MediaTagListItem> rows = MediaTagList.Arrange(unavailable, [], Options(), Name);
        Assert.Equal(["Own tags cannot be read right now.", "The Danbooru catalog cannot be read right now."],
            rows.Where(row => row.Kind == MediaTagListKind.Note).Select(row => row.Name));
        Assert.Contains(MediaTagList.Arrange(null, [], Options(), Name), row => row.Kind == MediaTagListKind.Note && row.Name == "No tags in these results.");
    }

    // Gesture 0 = click / Enter (include), 1 = Alt+click / Shift+Enter (exclude); change 1 include, 2 exclude, 3 remove.
    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(0, 1, 2)]
    [InlineData(1, 0, 3)]
    [InlineData(1, 1, 2)]
    [InlineData(-1, 0, 1)]
    [InlineData(-1, 1, 3)]
    public void AClickIncludesOrTakesOutAndAltClickExcludesOrTakesOut(int polarity, int gesture, int expected) =>
        Assert.Equal((MediaTagListChange)expected, MediaTagList.Resolve(polarity, (MediaTagListGesture)gesture));

    [Fact]
    public void EveryLanguageNamesTheRowsAndThePlural()
    {
        foreach (string code in new[] { "en", "de" }.Concat(Texts.BundledPackCodes()))
        {
            Texts.SetLanguage(code);
            MediaTagListItem one = new(MediaTagListKind.Tag, MediaTagGroup.Source, "solo", "solo", 1);
            MediaTagListItem many = new(MediaTagListKind.Tag, MediaTagGroup.Source, "solo", "solo", 5, Polarity: 1);
            Assert.Contains("solo", one.AccessibleName);
            Assert.Contains("1", one.AccessibleName);
            Assert.Contains("5", many.AccessibleName);
            Assert.EndsWith(Texts.T("media.tags.state.included"), many.AccessibleName);
            foreach (string key in new[] { "media.booruMode", "media.booruMode.tip", "media.tags.title", "media.tags.hint", "media.tile.queued", "media.status.queue",
                         "media.queue.walkNow", "media.toolbar.taggingPage", "settings.limits.mediaTagListTop" })
                Assert.NotEqual(key, Texts.T(key));
        }
    }
}

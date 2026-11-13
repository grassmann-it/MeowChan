using Avalonia.Input;
using MeowChan.Archive;
using MeowChan.Gallery;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class TagSuggestionTests : IDisposable
{
    public TagSuggestionTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static PrivateTagDefinition Tag(string name)
    {
        Assert.True(PrivateTagName.TryCreate(name, out PrivateTagName? parsed, out _));
        return new PrivateTagDefinition(new TagId(Guid.NewGuid()), parsed!);
    }

    private static string[] Names(IEnumerable<TagSuggestion> items) => items.Select(item => item.Name.Display).ToArray();

    [Fact]
    public void ExactBeatsPrefixBeatsWordStartBeatsSubstringAndUsesThenAlphabetOrderEachGroup()
    {
        var index = new TagSuggestionIndex(new[]
        {
            (Tag("catgirl"), 1), (Tag("Cat"), 1), (Tag("cats_misc"), 7),
            (Tag("big_cat"), 9), (Tag("/cat/"), 2), (Tag("bobcat"), 30), (Tag("dog"), 99)
        });

        IReadOnlyList<TagSuggestion> found = index.Match("cat");

        Assert.Equal(new[] { "Cat", "cats_misc", "catgirl", "big_cat", "/cat/", "bobcat" }, Names(found));
        Assert.Equal(new[] { TagMatchKind.Exact, TagMatchKind.Prefix, TagMatchKind.Prefix, TagMatchKind.WordPrefix,
            TagMatchKind.WordPrefix, TagMatchKind.Substring }, found.Select(item => item.Kind));
        Assert.Equal(7, found[1].Uses);
        // The exact name survives a cut that prefix matches would otherwise fill.
        Assert.Equal(new[] { "Cat", "cats_misc" }, Names(index.Match("CAT", limit: 2)));
        // A later occurrence at a word start still counts as a word start ("bobcat_cat").
        Assert.Equal(TagMatchKind.WordPrefix, new TagSuggestionIndex(new[] { (Tag("bobcat_cat"), 0) }).Match("cat").Single().Kind);
    }

    [Fact]
    public void MatchingUsesTheStoreComparisonKeyAndSkipsAssignedTags()
    {
        PrivateTagDefinition cafe = Tag("Café"), upper = Tag("RED"), other = Tag("blue");
        var index = new TagSuggestionIndex(new[] { (cafe, 0), (upper, 0), (other, 0) });

        // Case, edge whitespace and Unicode composition never hide a tag.
        Assert.Equal(new[] { "Café" }, Names(index.Match("  café ")));
        Assert.Equal(new[] { "RED" }, Names(index.Match("re")));
        Assert.Equal(TagSuggestionIndex.QueryKey("Café"), cafe.Name.Key);
        // Blank or oversized input matches nothing instead of listing everything.
        Assert.Empty(index.Match("   "));
        Assert.Empty(index.Match(null));
        Assert.Empty(index.Match(new string('a', PrivateTagName.MaximumUtf16CodeUnits + 1)));
        // Tags the target already carries are not offered again.
        Assert.Empty(index.Match("red", new HashSet<TagId> { upper.Id }));
        Assert.Equal(new[] { "blue" }, Names(index.Match("e", new HashSet<TagId> { upper.Id, cafe.Id })));
    }

    [Fact]
    public void SnapshotIndexIsBuiltOnceAndCountsThreadsAndDistinctMedia()
    {
        PrivateTagDefinition often = Tag("often"), rare = Tag("rare"), unused = Tag("rarely_used");
        ResourceId one = new("s", "b", "1"), two = new("s", "b", "2");
        var snapshot = new PrivateTagStoreSnapshot(3, "fp", new[] { unused, rare, often },
            new[] { new PrivateThreadTagAssignment(often.Id, one), new PrivateThreadTagAssignment(often.Id, two), new PrivateThreadTagAssignment(rare.Id, one) },
            Array.Empty<PrivateMediaTagAssignment>());

        TagSuggestionIndex index = TagSuggestionIndex.For(snapshot);

        Assert.Same(index, TagSuggestionIndex.For(snapshot));
        Assert.Equal(3, index.Count);
        Assert.Equal(new[] { ("rare", 1), ("rarely_used", 0) }, index.Match("rar").Select(item => (item.Name.Display, item.Uses)));
        Assert.Equal(2, index.Match("of").Single().Uses);
        // A tag change publishes a new snapshot, which gets its own index: nothing stale is served.
        var changed = new PrivateTagStoreSnapshot(4, "fp2", new[] { often }, Array.Empty<PrivateThreadTagAssignment>(), Array.Empty<PrivateMediaTagAssignment>());
        Assert.NotSame(index, TagSuggestionIndex.For(changed));
        Assert.Equal(0, TagSuggestionIndex.For(changed).Match("often").Single().Uses);
    }

    [Fact]
    public void CommaAddsTheDraftUnlessAnExistingNameContinuesWithIt()
    {
        var index = new TagSuggestionIndex(new[] { (Tag("Smith, John"), 1), (Tag("art"), 1) });

        Assert.True(index.CommitsOnComma("art"));
        Assert.True(index.CommitsOnComma("new name"));
        Assert.False(index.CommitsOnComma("smith"));
        Assert.False(index.CommitsOnComma(" SMITH "));
        Assert.False(index.CommitsOnComma("   "));
        Assert.False(index.CommitsOnComma(null));
    }

    [Theory]
    [InlineData(Key.Escape, false, false, "CloseOwner")]
    [InlineData(Key.Escape, true, false, "CloseList")]
    [InlineData(Key.Escape, true, true, "CloseList")]
    [InlineData(Key.Enter, false, false, "CommitTyped")]
    [InlineData(Key.Enter, true, false, "CommitTyped")]
    [InlineData(Key.Enter, true, true, "AcceptSuggestion")]
    [InlineData(Key.Tab, true, true, "None")]
    [InlineData(Key.Down, true, false, "None")]
    public void KeysCloseTheListBeforeTheOwnerAndEnterNeverAddsAnUnchosenSuggestion(Key key, bool open, bool highlighted, string expected)
    {
        Assert.Equal(Enum.Parse<TagFieldKey>(expected), TagSuggestionInput.Route(key, KeyModifiers.None, open, highlighted));
        // Modified keys (Ctrl+Enter, Shift+Escape) stay with the editor and the dialog's own shortcuts.
        Assert.Equal(TagFieldKey.None, TagSuggestionInput.Route(key, KeyModifiers.Control, open, highlighted));
    }

    [Fact]
    public void TabCompletesTheHighlightedOrFirstSuggestionButNeverTrapsFocus()
    {
        IdentitySuggestion first = new("cats", null, "cats"), second = new("catgirl", null, "catgirl");
        IdentitySuggestion[] shown = { first, second };

        Assert.Same(second, IdentitySuggestions.TabCompletion("cat", second, shown));
        Assert.Same(first, IdentitySuggestions.TabCompletion("cat", null, shown));
        // An automatic exact highlight (the "New tag" row equals the draft) is not a choice: the first row completes.
        IdentitySuggestion create = new("cat", TagSuggestionIndex.CreateGroup, "New tag");
        Assert.Same(first, IdentitySuggestions.TabCompletion("cat", create, new[] { first, create }));
        // Already complete or nothing shown: Tab keeps moving focus.
        Assert.Null(IdentitySuggestions.TabCompletion("cats", null, shown));
        Assert.Null(IdentitySuggestions.TabCompletion("cat", null, Array.Empty<IdentitySuggestion>()));
    }

    [Fact]
    public void RowsShowTheUseCountAndNameItForScreenReadersInEachLanguage()
    {
        PrivateTagDefinition used = Tag("my_tag"), fresh = Tag("fresh");
        IdentitySuggestion row = TagSuggestionIndex.Present(new TagSuggestion(used.Name, used.Id, 3, TagMatchKind.Prefix));
        IdentitySuggestion plain = TagSuggestionIndex.Present(new TagSuggestion(fresh.Name, fresh.Id, 0, TagMatchKind.Substring));

        // The value is the exact display name: accepting it reuses the tag, it never adds the count.
        Assert.Equal(("my_tag", "my_tag (3)", "my_tag, number of uses: 3"), (row.Value, row.Label, row.Description));
        Assert.Equal(("fresh", "fresh", "fresh, not used yet"), (plain.Value, plain.Label, plain.Description));
        Assert.Contains("13", Texts.T("tags.suggest.count", 5, TagSuggestionIndex.VisibleLimit));

        Texts.SetLanguage("de");
        Assert.Equal("my_tag, Anzahl Verwendungen: 3", TagSuggestionIndex.Present(new TagSuggestion(used.Name, used.Id, 3, TagMatchKind.Prefix)).Description);
        string german = Texts.T("tags.suggest.used", "my_tag", 3);
        Texts.SetLanguage("en");
        string englishHint = Texts.T("tags.suggest.hint");
        Assert.NotEqual(german, Texts.T("tags.suggest.used", "my_tag", 3));
        foreach (string language in new[] { "es", "fr", "ru" })
        {
            Texts.SetLanguage(language);
            Assert.NotEqual(englishHint, Texts.T("tags.suggest.hint"));
            Assert.Contains("3", TagSuggestionIndex.Present(new TagSuggestion(used.Name, used.Id, 3, TagMatchKind.Prefix)).Description);
        }
    }

    private static BooruPostRecord Post(string source, string id, params string[] tags) =>
        new(source, id, new BooruPostClaim(source, id, BooruPostClaimPresence.None,
            Tags: tags.Select(tag => new BooruPostClaimTag(tag, BooruPostClaimTagCategory.General)).ToArray()),
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    [Fact]
    public void SourceTagsCountSavedDanbooruPostsAndSkipNamesThatCannotBeOwnTags()
    {
        var catalog = new BooruCatalogSnapshot(BooruCatalogSnapshot.CurrentSchemaVersion, new[]
        {
            Post(BooruMediaIdentity.Source, "1", "long_hair", "smile", "smile"),
            Post(BooruMediaIdentity.Source, "2", "long_hair", "Long_Hair", "bad\tname"),
            Post("other", "3", "long_hair", "elsewhere")
        }, Array.Empty<BooruWatcherRecord>());

        TagSuggestionIndex sources = TagSuggestionIndex.FromCatalog(catalog);

        // One count per post; a duplicate within a post counts once; another provider is not read.
        Assert.Equal(new[] { ("long_hair", 3), ("smile", 1) }, sources.Match("long", limit: 10).Concat(sources.Match("smi"))
            .Select(item => (item.Name.Display, item.Uses)));
        Assert.Empty(sources.Match("elsewhere"));
        Assert.Empty(sources.Match("bad"));
        Assert.All(sources.Match("i"), item => Assert.Null(item.Id));
    }

    [Fact]
    public void OwnTagsComeFirstAndSourceTagsFormALabelledGroupBelowThem()
    {
        PrivateTagDefinition hair = Tag("Long_Hair"), assigned = Tag("hairpin");
        var own = new TagSuggestionIndex(new[] { (hair, 1), (assigned, 5), (Tag("short_hair"), 0) });
        var sources = TagSuggestionIndex.FromSourceTags(new[] { ("long_hair", 900), ("hair_ornament", 400), ("blue_hair", 50), ("hairband", 3) });

        IdentitySuggestion[] rows = TagSuggestionIndex.Combine(own, sources, "hair", new HashSet<TagId> { assigned.Id });

        // Own first even though the source tags are used far more; a source tag that already is an own tag (any
        // case) is not offered twice; an own tag on the target is not offered at all; "New tag" comes last.
        Assert.Equal(new[] { "Long_Hair", "short_hair", "hair_ornament", "hairband", "blue_hair", "hair" }, rows.Select(row => row.Value));
        Assert.Equal(new string?[] { null, null, TagSuggestionIndex.SourceGroup, TagSuggestionIndex.SourceGroup, TagSuggestionIndex.SourceGroup,
            TagSuggestionIndex.CreateGroup }, rows.Select(row => row.Source));
        Assert.Equal(new string?[] { null, null, Texts.T("tags.suggest.sourceGroup"), null, null, null }, rows.Select(row => row.GroupHeader));
        Assert.Equal("New tag: \u201chair\u201d", rows[^1].Label);
        Assert.Equal("hair_ornament · Danbooru (400)", rows[2].Label);
        Assert.Contains("creates an own tag", rows[2].Description);
        Assert.DoesNotContain("Danbooru", rows[0].Label);
        // Without source tags there is no group, and nothing changes for own tags.
        Assert.Equal(rows.Take(2).Append(rows[^1]), TagSuggestionIndex.Combine(own, null, "hair", new HashSet<TagId> { assigned.Id }));
    }

    [Fact]
    public void NewTagRowOnlyForAValidNameThatIsNoTagYetAndAnEmptyFieldOnlyListsOnRequest()
    {
        PrivateTagDefinition on = Tag("Blue"), off = Tag("blur");
        var own = new TagSuggestionIndex(new[] { (on, 9), (off, 3), (Tag("sky"), 1) });
        var sources = TagSuggestionIndex.FromSourceTags(new[] { ("blue_eyes", 5) });
        HashSet<TagId> assigned = new() { on.Id };
        string[] Values(string? text, bool browse = false) => TagSuggestionIndex.Combine(own, sources, text, assigned, browse).Select(row => row.Value).ToArray();

        Assert.Equal(new[] { "blur", "blue_eyes", "bl" }, Values(" bl "));
        // Already an own tag (even the assigned one) or exactly a source tag: nothing to create.
        Assert.Equal(new[] { "blue_eyes" }, Values("BLUE"));
        Assert.Equal(new[] { "blur" }, Values("BLUR"));
        Assert.Equal(new[] { "blue_eyes" }, Values("blue_EYES"));
        // An invalid name never gets a create row (the field keeps it for correction).
        Assert.Empty(Values("bad\tname"));
        // Empty: nothing on focus; Down shows the most used own tags that the target lacks.
        Assert.Empty(Values(""));
        Assert.Equal(new[] { "blur", "sky" }, Values("", browse: true));
    }

    [Fact]
    public void LargeCatalogsStayCappedPerGroupAndKeepTheBestMatches()
    {
        var own = new TagSuggestionIndex(Enumerable.Range(0, 1024).Select(n => (Tag("tag" + n.ToString("D4")), n)));
        var sources = TagSuggestionIndex.FromSourceTags(Enumerable.Range(0, 20_000).Select(n => ("tag_source_" + n.ToString("D5"), n)));

        IdentitySuggestion[] rows = TagSuggestionIndex.Combine(own, sources, "TAG", null);

        Assert.Equal(TagSuggestionIndex.VisibleLimit, rows.Length);
        Assert.Equal(Enumerable.Range(0, TagSuggestionIndex.OwnLimit).Select(n => "tag" + (1023 - n).ToString("D4")), rows.Take(TagSuggestionIndex.OwnLimit).Select(row => row.Value));
        Assert.Equal(Enumerable.Range(0, TagSuggestionIndex.SourceLimit).Select(n => "tag_source_" + (19_999 - n).ToString("D5")),
            rows.Skip(TagSuggestionIndex.OwnLimit).Take(TagSuggestionIndex.SourceLimit).Select(row => row.Value));
        Assert.Equal(TagSuggestionIndex.CreateGroup, rows[^1].Source);
        // A later group is still found when the first one is small: word starts after a full prefix scan.
        Assert.Equal("tag_source_00042", sources.Match("00042", limit: 1).Single().Name.Display);
    }

    [Fact]
    public void AllThreeTagFieldsUseTheSharedSuggestionField()
    {
        foreach (string file in new[] { "Desktop/Views/Collections/MediaTagDialog.cs", "Desktop/Views/Media/MediaSelectionTagDialog.cs", "Desktop/Views/Archive/ArchivePage.Tags.cs" })
        {
            Assert.Contains("TagSuggestionInput.Attach(", Source(file));
            Assert.Contains("SourceTagSuggestions", Source(file));
        }
        Assert.Contains("<desktop:IdentitySuggestionBox x:Name=\"ReaderTagInput\"", Source("Desktop/Views/Archive/ArchivePage.axaml"));
    }

    private static string Source(string relative, [System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../..", relative)));
}

using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

// M2 (partial): the popover that adds a tag to a filter, and the tag chip with its include/exclude toggle.
[Collection("Localization")]
public sealed class TagFilterPickerTests : IDisposable
{
    public TagFilterPickerTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static PrivateTagDefinition Tag(string name)
    {
        Assert.True(PrivateTagName.TryCreate(name, out PrivateTagName? parsed, out _));
        return new PrivateTagDefinition(new TagId(Guid.NewGuid()), parsed!);
    }

    private static PrivateTagStoreSnapshot Snapshot(params PrivateTagDefinition[] tags) =>
        new(1, "fp", tags, Array.Empty<PrivateThreadTagAssignment>(), Array.Empty<PrivateMediaTagAssignment>());

    [Theory]
    [InlineData("surface", false, "surface", false)]
    [InlineData("surface", true, "surface", true)]
    [InlineData("-wip", false, "wip", true)]
    [InlineData("  - wip", false, "wip", true)]
    [InlineData("-", false, "", true)]
    [InlineData("a-b", false, "a-b", false)]
    [InlineData(null, false, "", false)]
    public void AMinusAtTheStartMeansExcludeAndIsNotPartOfTheName(string? text, bool addAsExclude, string query, bool exclude)
    {
        Assert.Equal((query, exclude), TagFilterPicker.ParseQuery(text, addAsExclude));
    }

    [Fact]
    public void AnEmptyQueryOffersTheMostUsedTagsAndNeverOnesAlreadyInTheFilter()
    {
        PrivateTagDefinition red = Tag("red"), blue = Tag("blue"), green = Tag("green");
        PrivateTagStoreSnapshot snapshot = Snapshot(red, blue, green);
        IReadOnlyList<TagSuggestion> all = TagFilterPicker.Choices(snapshot, new HashSet<TagId>(), "");
        Assert.Equal(new[] { "blue", "green", "red" }, all.Select(item => item.Name.Display).Order());
        IReadOnlyList<TagSuggestion> left = TagFilterPicker.Choices(snapshot, new HashSet<TagId> { red.Id, blue.Id }, null);
        Assert.Equal(new[] { "green" }, left.Select(item => item.Name.Display));
    }

    [Fact]
    public void ATypedQueryRanksPrefixBeforeAnywhereAndSkipsUsedTags()
    {
        PrivateTagDefinition cat = Tag("cat"), bobcat = Tag("bobcat"), catgirl = Tag("catgirl"), dog = Tag("dog");
        PrivateTagStoreSnapshot snapshot = Snapshot(cat, bobcat, catgirl, dog);
        Assert.Equal(new[] { "cat", "catgirl", "bobcat" }, TagFilterPicker.Choices(snapshot, new HashSet<TagId>(), "CAT").Select(item => item.Name.Display));
        Assert.Equal(new[] { "catgirl", "bobcat" }, TagFilterPicker.Choices(snapshot, new HashSet<TagId> { cat.Id }, "cat").Select(item => item.Name.Display));
        Assert.Empty(TagFilterPicker.Choices(snapshot, new HashSet<TagId>(), "zzz"));
    }

    [Fact]
    public void NoSnapshotMeansNoChoices()
    {
        Assert.Empty(TagFilterPicker.Choices(null, new HashSet<TagId>(), "cat"));
    }

    [Fact]
    public void TheButtonIsDisabledWithAReasonWhenNoTagIsLeftToAdd()
    {
        PrivateTagDefinition red = Tag("red");
        PrivateTagStoreSnapshot? snapshot = null;
        HashSet<TagId> used = new();
        TagFilterPicker picker = new() { Snapshot = () => snapshot, Used = () => used };
        picker.Refresh();
        Assert.False(picker.IsEnabled);
        Assert.Contains(Texts.T("tags.filter.unavailable"), (string)Avalonia.Controls.ToolTip.GetTip(picker)!);
        snapshot = Snapshot();
        picker.Refresh();
        Assert.False(picker.IsEnabled);
        Assert.Contains(Texts.T("tags.filter.none"), (string)Avalonia.Controls.ToolTip.GetTip(picker)!);
        snapshot = Snapshot(red);
        picker.Refresh();
        Assert.True(picker.IsEnabled);
        Assert.Equal(Texts.T("tags.filter.add"), Avalonia.Controls.ToolTip.GetTip(picker));
        used.Add(red.Id);
        picker.Refresh();
        Assert.False(picker.IsEnabled);
        Assert.Contains(Texts.T("tags.filter.add.allUsed"), (string)Avalonia.Controls.ToolTip.GetTip(picker)!);
    }

    [Fact]
    public void ATagChipSaysItsStateAndOffersTheOtherOneAsAToggle()
    {
        Guid id = Guid.NewGuid();
        int removed = 0, toggled = 0;
        Ursa.Controls.ClosableTag included = ArchivePage.CreateTagFilterChip("Tag: surface", "surface", id, exclude: false, () => removed++, () => toggled++);
        Ursa.Controls.ClosableTag excluded = ArchivePage.CreateTagFilterChip("Without tag: wip", "wip", id, exclude: true, () => removed++, () => toggled++);
        Assert.Contains("chip-filter", included.Classes);
        Assert.DoesNotContain("chip-exclude", included.Classes);
        Assert.Contains("chip-exclude", excluded.Classes);
        Assert.Equal("Tag: surface", ArchivePage.FilterTagLabel(included));
        Assert.Equal("Without tag: wip", ArchivePage.FilterTagLabel(excluded));
        Assert.Equal(id, included.Tag);
        Avalonia.Controls.Button toggleInclude = (Avalonia.Controls.Button)((Avalonia.Controls.StackPanel)included.Content!).Children[0];
        Avalonia.Controls.Button toggleExclude = (Avalonia.Controls.Button)((Avalonia.Controls.StackPanel)excluded.Content!).Children[0];
        Assert.Equal(Texts.T("tags.filter.toExclude", "surface"), Avalonia.Automation.AutomationProperties.GetName(toggleInclude));
        Assert.Equal(Texts.T("tags.filter.toInclude", "wip"), Avalonia.Automation.AutomationProperties.GetName(toggleExclude));
        toggleInclude.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.Equal((0, 1), (removed, toggled));
        included.Command!.Execute(null); // the chip's own close mark removes
        Assert.Equal((1, 1), (removed, toggled));
    }

    [Theory]
    [InlineData("en"), InlineData("de"), InlineData("es"), InlineData("fr"), InlineData("ru"), InlineData("cs"), InlineData("pl"), InlineData("ja")]
    public void ThePopoverAndToggleTextsAreTranslatedWithTheirPlaceholders(string language)
    {
        Texts.SetLanguage(language);
        foreach (string key in new[] { "tags.filter.add", "tags.filter.add.search", "tags.filter.add.mode", "tags.filter.add.hint", "tags.filter.add.allUsed" })
            Assert.False(string.IsNullOrWhiteSpace(Texts.T(key)) || Texts.T(key) == key, language + " " + key);
        Assert.Contains("cat", Texts.T("tags.filter.add.noMatch", "cat"));
        Assert.Contains("cat", Texts.T("tags.filter.toExclude", "cat"));
        Assert.Contains("cat", Texts.T("tags.filter.toInclude", "cat"));
        Assert.Contains("5", Texts.Count("archive.filters.countName", 5));
        Assert.Contains("1", Texts.Count("archive.filters.countName", 1));
        if (language == "de") Assert.Equal("Filter und Sortierung, 3 aktiv", Texts.Count("archive.filters.countName", 3));
        if (language == "en") Assert.Equal("Filters and sorting, 1 active", Texts.Count("archive.filters.countName", 1));
    }
}

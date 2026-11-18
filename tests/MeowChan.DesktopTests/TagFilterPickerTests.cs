using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Tags;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;

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
    public void TypedTextThatNamesAnAddableTagIsAnUnfinishedEntryOnlyWhenThePageKeepsIt()
    {
        PrivateTagDefinition red = Tag("red"), blue = Tag("blue");
        HashSet<TagId> used = new();
        int changes = 0;
        TagFilterPicker picker = new() { Snapshot = () => Snapshot(red, blue), Used = () => used };
        picker.PendingChanged += (_, _) => changes++;
        picker.Search.SetTextSilently("re");
        Assert.False(picker.HasPending); // Media applies at once and keeps nothing
        picker.KeepQueryOnDismiss = true;
        Assert.True(picker.HasPending);
        picker.Search.SetTextSilently("-re");
        Assert.True(picker.HasPending); // the dash only says "exclude"
        picker.Search.SetTextSilently("zzz");
        Assert.False(picker.HasPending); // names no tag: nothing to finish
        picker.Search.SetTextSilently("-");
        Assert.False(picker.HasPending);
        picker.Search.SetTextSilently("red");
        used.Add(red.Id);
        Assert.False(picker.HasPending); // already in the filter
        used.Clear();
        Assert.True(picker.HasPending);
        picker.ClearPending();
        Assert.False(picker.HasPending);
        Assert.False(picker.Search.HasText);
        Assert.Equal(1, changes);
        picker.ClearPending(); // nothing left to clear: no further change
        Assert.Equal(1, changes);
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

    // ---- M2: the full picker ("+ Filter") over every group --------------------------------------------------------

    private static FilterTermChoice Choice(MediaFilterTerm term, string label) => new(term.Field, label, term);

    private static IReadOnlyList<FilterTermChoice> Others() =>
    [
        Choice(MediaFilterTerm.Type(MeowChan.Gallery.MediaLibraryKindFilter.Image), "Images"),
        Choice(MediaFilterTerm.Type(MeowChan.Gallery.MediaLibraryKindFilter.Video), "Videos"),
        Choice(MediaFilterTerm.Source("4chan"), "4chan"),
        Choice(MediaFilterTerm.Board("4chan", "g"), "/g/ (4chan)"),
        Choice(MediaFilterTerm.Board("4chan", "vg"), "/vg/ (4chan)"),
        Choice(MediaFilterTerm.Rating(3), "At least 3 of 5"),
        Choice(MediaFilterTerm.Flag(MediaFilterField.Favorite), "Favorites only"),
    ];

    [Fact]
    public void TypingSearchesEveryGroupAndRanksPrefixBeforeWordStartBeforeAnywhere()
    {
        Assert.Equal(new[] { "Videos" }, FilterTermPicker.Match(Others(), "vid").Select(choice => choice.Label));
        // "g": within the board group /g/ (a word starts with it) before /vg/ (contains it); the groups keep their order (types first).
        Assert.Equal(new[] { "Images", "/g/ (4chan)", "/vg/ (4chan)" }, FilterTermPicker.Match(Others(), "g").Select(choice => choice.Label));
        // A group's heading finds the whole group: "type" lists every type.
        Assert.Equal(new[] { "Images", "Videos" }, FilterTermPicker.Match(Others(), "type").Select(choice => choice.Label));
        Assert.Equal(new[] { "4chan", "/g/ (4chan)", "/vg/ (4chan)" }, FilterTermPicker.Match(Others(), "4CH").Select(choice => choice.Label));
        Assert.Equal(Others().Count, FilterTermPicker.Match(Others(), "  ").Count);
        Assert.Empty(FilterTermPicker.Match(Others(), "zzz"));
    }

    [Fact]
    public void TheListShowsAHeadingPerGroupInTheFixedGroupOrder()
    {
        PrivateTagDefinition red = Tag("red");
        IReadOnlyList<FilterTermChoice> choices = [Choice(MediaFilterTerm.Rating(3), "At least 3 of 5"), Choice(MediaFilterTerm.Tag(red.Id.Value), "red"),
            Choice(MediaFilterTerm.Type(MeowChan.Gallery.MediaLibraryKindFilter.Gif), "GIFs"), Choice(MediaFilterTerm.Flag(MediaFilterField.Missing), "Missing")];
        IReadOnlyList<object> items = FilterTermPicker.Arrange(FilterTermPicker.Match(choices, null), headings: true);
        Assert.Equal(new[] { Texts.T("media.filter.group.tag"), "red", Texts.T("media.filter.kind"), "GIFs", Texts.T("media.filter.rating"), "At least 3 of 5",
            Texts.T("media.filter.group.flags"), "Missing" }, items.Select(item => item.ToString()));
        Assert.All(items.Where((_, index) => index % 2 == 0), item => Assert.IsType<FilterTermGroup>(item));
        Assert.Equal(4, FilterTermPicker.Arrange(choices, headings: false).Count); // the archive's tags-only form has no headings
    }

    private static (FilterTermPicker Picker, List<MediaFilterTerm> Picked) FullPicker(params PrivateTagDefinition[] tags)
    {
        List<MediaFilterTerm> picked = new();
        FilterTermPicker picker = new() { Snapshot = () => Snapshot(tags), Others = Others, PickedTerm = term => { picked.Add(term); return null; } };
        picker.Refresh();
        return (picker, picked);
    }

    private static void Type(FilterTermPicker picker, string text, KeyModifiers modifiers = KeyModifiers.None)
    {
        picker.Search.Text = text; picker.Search.FlushQuery();
        picker.Search.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = modifiers });
    }

    [Fact]
    public void EnterIncludesShiftEnterAndADashExcludeAndFieldsThatCannotExcludeSaySo()
    {
        PrivateTagDefinition wip = Tag("wip");
        (FilterTermPicker picker, List<MediaFilterTerm> picked) = FullPicker(wip);
        Assert.True(picker.IsEnabled);
        Assert.Equal(Texts.T("media.filter.add"), AutomationProperties.GetName(picker));
        Assert.Contains("chip-available", picker.Classes); // "+ Filter" is an offered chip
        Assert.Equal("FilterTermPickerSearch", picker.Search.Name);
        Type(picker, "vid");
        Type(picker, "/g/", KeyModifiers.Shift);
        Type(picker, "-wip");
        Assert.Equal(new[] { MediaFilterTerm.Type(MeowChan.Gallery.MediaLibraryKindFilter.Video), MediaFilterTerm.Board("4chan", "g", exclude: true),
            MediaFilterTerm.Tag(wip.Id.Value, exclude: true) }, picked);
        // While the entry would exclude, fields that cannot are not offered; Shift+Enter on one is refused with a reason.
        picker.Search.Text = "-fav"; picker.Search.FlushQuery();
        Assert.Empty(picker.ShownChoices);
        Type(picker, "fav", KeyModifiers.Shift);
        Assert.Equal(3, picked.Count);
        TextBlock error = (TextBlock)((StackPanel)((Border)((Flyout)picker.Flyout!).Content!).Child!).Children.Single(child => child.Name == "FilterTermPickerError");
        Assert.True(error.IsVisible);
        Assert.Equal(Texts.T("media.filter.add.cannotExclude", "Favorites only"), error.Text);
    }

    [Fact]
    public void ACommaAddsTheEntryAndContinuesUnlessItContinuesATagName()
    {
        PrivateTagDefinition plain = Tag("cat"), comma = Tag("a,b");
        (FilterTermPicker picker, List<MediaFilterTerm> picked) = FullPicker(plain, comma);
        void TypeComma() => picker.Search.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "," });
        picker.Search.Text = "cat"; picker.Search.FlushQuery();
        TypeComma();
        Assert.Equal(new[] { MediaFilterTerm.Tag(plain.Id.Value) }, picked);
        Assert.True(FilterTermPicker.CommaContinuesATagName(Snapshot(plain, comma), "A"));
        Assert.False(FilterTermPicker.CommaContinuesATagName(Snapshot(plain, comma), "cat"));
        picker.Search.Text = "a"; picker.Search.FlushQuery();
        TypeComma(); // "a," starts "a,b": the comma is typed, nothing is added
        Assert.Single(picked);
        // The archive's tags-only form takes no comma at all (its tags.filter.* behaviour is unchanged).
        int tagPicks = 0;
        TagFilterPicker tags = new() { Snapshot = () => Snapshot(plain), Picked = (_, _) => { tagPicks++; return null; } };
        tags.Search.Text = "cat"; tags.Search.FlushQuery();
        tags.Search.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "," });
        Assert.Equal(0, tagPicks);
        Assert.Equal("TagFilterPickerSearch", tags.Search.Name);
        Assert.True(tags.TagsOnly);
    }

    [Fact]
    public void OpeningForOneFieldListsOnlyItsGroupAndTheButtonSaysWhenNothingIsLeft()
    {
        FilterTermPicker empty = new() { Snapshot = () => Snapshot(), Others = () => Array.Empty<FilterTermChoice>() };
        empty.Refresh();
        Assert.False(empty.IsEnabled);
        Assert.Contains(Texts.T("media.filter.add.allUsed"), (string)Avalonia.Controls.ToolTip.GetTip(empty)!);
        Assert.Null(empty.OnlyField);
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

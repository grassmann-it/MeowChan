using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

// T1/T2: own tags on media tiles are filter chips like the thread label beside them, and the media status line offers
// "N without tags". No window is opened: the tile is built and driven through its controls and routed events.
[Collection("Localization")]
public sealed class MediaTileTagChipTests : IDisposable
{
    private static readonly DateTimeOffset Day = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    public MediaTileTagChipTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static MediaLibraryEntry Entry(string thread, char hash) =>
        new(new ResourceId("s", "b", thread), "T" + thread, 1, Day, Day, "1", 1, null, 0, "att-v1:" + new string(hash, 64),
            "/dir/" + thread, "asset-" + thread, new string(hash, 64), 1, "m" + thread, "original", "f.png", SavedMediaKind.Image,
            MediaLibraryPresence.Present, false, false);

    private static PrivateTagDefinition Tag(int id, string name)
    {
        Assert.True(PrivateTagName.TryCreate(name, out PrivateTagName? tagName, out _));
        return new PrivateTagDefinition(new TagId(new Guid(id, 0, 0, new byte[8])), tagName!);
    }

    private static MediaLibraryTileRow Row(MediaLibraryTile tile, params PrivateTagDefinition[] tags) => new(tile, 0, _ =>
        new MediaLibraryEntryState(MediaLibraryFlag.No, true, 4, true, tags.Select(tag => tag.Name.Display).ToArray(), Array.Empty<string>(), null)
        { CarriedTags = tags });

    private static Button[] Chips(Control view) => view.GetLogicalDescendants().OfType<Button>().Where(button => button.Name == "MediaTileTag").ToArray();
    private static TextBlock More(Control view) => view.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "MediaTileMoreTags");

    [Fact]
    public void ChipsNameTheirTagFilterOnClickOrEnterAndSummarizeTheRest()
    {
        MediaLibraryEntry entry = Entry("1", 'a');
        PrivateTagDefinition cat = Tag(1, "cat"), dog = Tag(2, "dog"), owl = Tag(3, "owl");
        List<Guid> filtered = new();
        MediaLibraryTileView view = new() { TagFilter = filtered.Add, DataContext = Row(new MediaLibraryTile(entry, [entry]), cat, dog, owl) };

        Button[] visible = Chips(view).Where(chip => chip.IsVisible).ToArray();
        Assert.Equal(new[] { "cat", "dog" }, visible.Select(chip => ((TextBlock)chip.Content!).Text));
        Assert.All(visible, chip =>
        {
            // Same link role as the thread label; reachable and named for keyboard and screen reader users.
            Assert.Contains("link", chip.Classes);
            Assert.True(chip.Focusable && chip.IsTabStop);
            string hint = Texts.T("media.tile.tagHint", ((TextBlock)chip.Content!).Text);
            Assert.Equal(hint, ToolTip.GetTip(chip));
            Assert.Equal(hint, AutomationProperties.GetName(chip));
        });
        Assert.True(More(view).IsVisible);
        Assert.Equal("+1", More(view).Text);
        Assert.Equal(Texts.T("media.tile.moreTags", "owl"), ToolTip.GetTip(More(view)));

        RoutedEventArgs click = new(Button.ClickEvent);
        visible[1].RaiseEvent(click);
        Assert.True(click.Handled); // The tile itself is not activated by the chip.
        visible[0].RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.Equal(new[] { dog.Id.Value, cat.Id.Value }, filtered);
        // The tile line no longer counts tags; the chips show them.
        Assert.DoesNotContain("tags", ((MediaLibraryTileRow)view.DataContext!).DetailWithState);
    }

    [Fact]
    public void AGroupedTileSharesTheRowWithItsFavoriteAndAnUntaggedTileShowsNoChips()
    {
        MediaLibraryEntry first = Entry("1", 'a'), second = Entry("2", 'a');
        PrivateTagDefinition cat = Tag(1, "cat"), dog = Tag(2, "dog");
        MediaLibraryTileView view = new() { DataContext = Row(new MediaLibraryTile(first, [first, second], MediaLibraryFavoriteState.None), cat, dog) };
        Assert.Equal(new[] { "cat" }, Chips(view).Where(chip => chip.IsVisible).Select(chip => ((TextBlock)chip.Content!).Text));
        Assert.Equal("+1", More(view).Text);
        Assert.True(view.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "MediaTileFavorite").IsVisible);

        view.DataContext = Row(new MediaLibraryTile(first, [first]));
        Assert.All(Chips(view), chip => Assert.False(chip.IsVisible));
        Assert.False(More(view).IsVisible);
        Assert.Null(ToolTip.GetTip(Chips(view)[0]));
    }

    [Theory]
    [InlineData("en"), InlineData("de"), InlineData("es"), InlineData("fr"), InlineData("ru")]
    public void TaggingEntryTextsAreTranslatedWithTheirPlaceholders(string language)
    {
        Texts.SetLanguage(language);
        foreach (string key in new[] { "media.filter.withoutTags", "media.filter.withoutTagsHint" })
            Assert.False(string.IsNullOrWhiteSpace(Texts.T(key)) || Texts.T(key) == key, key);
        Assert.Contains("12", Texts.T("media.untagged.count", 12));
        Assert.Contains("12", Texts.T("media.untagged.hint", 12));
        Assert.Contains("kitten", Texts.T("media.tile.tagHint", "kitten"));
        Assert.Contains("kitten", Texts.T("media.tile.moreTags", "kitten"));
        if (language == "en") return;
        string translated = Texts.T("media.filter.withoutTags");
        Texts.SetLanguage("en");
        Assert.NotEqual(Texts.T("media.filter.withoutTags"), translated);
    }
}

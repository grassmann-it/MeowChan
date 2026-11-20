using MeowChan.Desktop.Controls;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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

    private static MediaLibraryTileRow Row(MediaLibraryTile tile, params PrivateTagDefinition[] tags) => Row(tile, tags, Array.Empty<PrivateTagDefinition>());

    // Media tags first, then the thread's tags the file does not carry itself (MediaLibraryEntryState.CarriedTags).
    private static MediaLibraryTileRow Row(MediaLibraryTile tile, PrivateTagDefinition[] media, PrivateTagDefinition[] thread) => new(tile, 0, _ =>
        new MediaLibraryEntryState(MediaLibraryFlag.No, true, 4, true, media.Select(tag => tag.Name.Display).ToArray(), thread.Select(tag => tag.Name.Display).ToArray(), null)
        { CarriedTags = media.Concat(thread).ToArray() });
    private static string Label(Button chip) => MediaLibraryTileView.ChipLabel(chip).Text ?? string.Empty;

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
        Assert.Equal(new[] { "cat", "dog" }, visible.Select(Label));
        Assert.All(visible, chip =>
        {
            // C1: a filled compact own-tag chip; reachable and named for keyboard and screen reader users.
            Assert.Equal(new[] { "chip", "chip-own", "chip-compact" }, chip.Classes.Where(name => name.StartsWith("chip", StringComparison.Ordinal)));
            Assert.DoesNotContain("link", chip.Classes);
            Assert.False(MediaLibraryTileView.ChipThreadMark(chip).IsVisible);
            Assert.True(chip.Focusable && chip.IsTabStop);
            string hint = Texts.T("media.tile.tagHint", Label(chip));
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
    public void AGroupedTileSharesTheRowWithItsFavoriteStateAndAnUntaggedTileShowsNoChips()
    {
        MediaLibraryEntry first = Entry("1", 'a'), second = Entry("2", 'a');
        PrivateTagDefinition cat = Tag(1, "cat"), dog = Tag(2, "dog");
        MediaLibraryTileView view = new() { DataContext = Row(new MediaLibraryTile(first, [first, second], MediaLibraryFavoriteState.None), cat, dog) };
        Assert.Equal(new[] { "cat" }, Chips(view).Where(chip => chip.IsVisible).Select(Label));
        Assert.Equal("+1", More(view).Text);
        TextBlock favorite = view.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "MediaTileFavorite");
        Assert.False(favorite.IsVisible); // "Favorite: none" stays out of the quiet short form ...
        Assert.Contains(Texts.T("media.group.favorite.none"), AutomationProperties.GetName(view)); // ... but not out of the spoken name
        foreach (MediaLibraryFavoriteState state in new[] { MediaLibraryFavoriteState.Some, MediaLibraryFavoriteState.All, MediaLibraryFavoriteState.Unknown })
        {
            view.DataContext = Row(new MediaLibraryTile(first, [first, second], state), cat, dog);
            Assert.True(favorite.IsVisible);
            Assert.Equal(MediaLibraryStateText.Favorite(state), favorite.Text);
        }

        view.DataContext = Row(new MediaLibraryTile(first, [first]));
        Assert.All(Chips(view), chip => Assert.False(chip.IsVisible));
        Assert.False(More(view).IsVisible);
        Assert.Null(ToolTip.GetTip(Chips(view)[0]));
    }

    [Fact]
    public void AThreadTagChipCarriesTheThreadMarkAndSaysSoWhileAMediaTagDoesNot()
    {
        MediaLibraryEntry entry = Entry("1", 'a');
        PrivateTagDefinition cat = Tag(1, "cat"), rpt = Tag(2, "rpt");
        List<Guid> filtered = new();
        MediaLibraryTileView view = new() { TagFilter = filtered.Add, DataContext = Row(new MediaLibraryTile(entry, [entry]), [cat], [rpt]) };
        Button[] visible = Chips(view).Where(chip => chip.IsVisible).ToArray();
        Assert.Equal(new[] { "cat", "rpt" }, visible.Select(Label));
        // Both are own tags (same filled form); the thread tag adds a shape mark and the word "Thread" in its name.
        Assert.All(visible, chip => Assert.Contains("chip-own", chip.Classes));
        Assert.False(MediaLibraryTileView.ChipThreadMark(visible[0]).IsVisible);
        Assert.DoesNotContain("chip-thread", visible[0].Classes);
        Assert.True(MediaLibraryTileView.ChipThreadMark(visible[1]).IsVisible);
        Assert.Contains("chip-thread", visible[1].Classes);
        Assert.Equal(Texts.T("media.tile.tagHint", "cat"), AutomationProperties.GetName(visible[0]));
        Assert.Equal(Texts.T("media.tile.threadTagHint", "rpt"), AutomationProperties.GetName(visible[1]));
        Assert.Equal(Texts.T("media.tile.threadTagHint", "rpt"), ToolTip.GetTip(visible[1]));
        // Behaviour unchanged: the thread tag chip filters by its tag like any other chip.
        visible[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(new[] { rpt.Id.Value }, filtered);

        // A recycled tile drops the mark when the next row's chip is a media tag.
        view.DataContext = Row(new MediaLibraryTile(entry, [entry]), [rpt], []);
        Assert.False(MediaLibraryTileView.ChipThreadMark(Chips(view)[0]).IsVisible);
        Assert.DoesNotContain("chip-thread", Chips(view)[0].Classes);
    }

    // I7: fixed preview corners. Type badge top right, "not saved"/"no media tags yet" mark bottom left, favourite star bottom right.
    private static T Named<T>(Control view, string name) where T : Control => view.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);

    private static MediaLibraryTileRow StateRow(MediaLibraryTile tile, MediaLibraryFlag favorite, bool editable, params string[] mediaTags) => new(tile, 0, entry =>
        new MediaLibraryEntryState(favorite, true, null, true, mediaTags, Array.Empty<string>(), editable ? MediaTagSubjects.Of(entry) : null));

    [Fact]
    public void BadgesNameTypeUntaggedMissingAndFavouriteFromTheRowAlone()
    {
        MediaLibraryEntry gif = Entry("1", 'a') with { Name = "loop.GIF" };
        Assert.NotNull(MediaTagSubjects.Of(gif));
        List<MediaLibraryEntry> toggled = new();
        MediaLibraryTileView view = new() { FavoriteToggle = entry => { toggled.Add(entry); return Task.CompletedTask; },
            DataContext = StateRow(new MediaLibraryTile(gif, [gif]), MediaLibraryFlag.No, editable: true) };
        ToggleButton star = Named<ToggleButton>(view, "MediaTileFavoriteToggle");
        MediaBadgeView badges = view.Badges;
        Assert.Equal(MediaBadgeKind.Gif, badges.State.Kind);
        Assert.Equal(0, badges.State.TagCount); // an editable occurrence without media tags
        Assert.False(badges.State.Favorite || badges.State.Missing);
        Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(badges));
        // The tile's own name carries what the badges show.
        string name = AutomationProperties.GetName(view)!;
        Assert.Contains(Texts.T("media.badge.gif"), name);
        Assert.Contains(Texts.T("media.badge.untagged"), name);
        Assert.Equal(view.BadgeDescription, AutomationProperties.GetHelpText(view));
        // The star: a named, focusable toggle; clicking runs the page's favourite action for the shown occurrence, not the tile.
        Assert.True(star.IsVisible && star.Focusable);
        Assert.False(star.IsChecked);
        Assert.Contains("tile-favorite", star.Classes);
        Assert.Contains(MediaLibraryTileView.QuietFavoriteClass, star.Classes); // the badge shows the state at rest
        Assert.False(MeowChan.Desktop.Controls.FavoriteStar.MarkOf(star.Content));
        Assert.Equal(Texts.T("viewer.favorite.add"), AutomationProperties.GetName(star));
        RoutedEventArgs click = new(Button.ClickEvent);
        star.RaiseEvent(click);
        Assert.True(click.Handled);
        Assert.Equal(new[] { gif }, toggled);

        // A favourite with a media tag: star set, no untagged mark; a video names its type.
        MediaLibraryEntry video = Entry("2", 'b') with { Name = "clip.webm", Kind = SavedMediaKind.Video };
        view.DataContext = StateRow(new MediaLibraryTile(video, [video]), MediaLibraryFlag.Yes, editable: true, "cat");
        Assert.Equal(MediaBadgeKind.Video, badges.State.Kind);
        Assert.True(badges.State.Favorite);
        Assert.Null(badges.State.TagCount); // tagged, but StateRow carries no tag chips: neither a count nor the untagged mark
        Assert.Contains(Texts.T("media.badge.favorite"), AutomationProperties.GetName(view)!);
        Assert.True(star.IsChecked);
        Assert.True(MeowChan.Desktop.Controls.FavoriteStar.MarkOf(star.Content));
        Assert.Equal(Texts.T("viewer.favorite.remove"), AutomationProperties.GetName(star));

        // A still image without a tag subject, favourites unavailable: no type badge, no mark, no star.
        MediaLibraryEntry still = Entry("3", 'c');
        view.DataContext = StateRow(new MediaLibraryTile(still, [still]), MediaLibraryFlag.Unknown, editable: false);
        Assert.False(star.IsVisible);
        Assert.Equal(default, badges.State with { Kind = MediaBadgeKind.Image });
        Assert.Null(badges.State.TagCount);

        // A missing file shows the "file missing" badge instead.
        MediaLibraryEntry missing = Entry("4", 'd') with { Presence = MediaLibraryPresence.Missing };
        view.DataContext = new MediaLibraryTileRow(new MediaLibraryTile(missing, [missing]), 0);
        Assert.True(badges.State.Missing);
        Assert.Contains(Texts.T("media.badge.missing"), AutomationProperties.GetName(view)!);
    }

    [Fact]
    public void GroupedTilesAndUnwiredPagesShowNoFavouriteStar()
    {
        MediaLibraryEntry first = Entry("1", 'a'), second = Entry("2", 'a');
        MediaLibraryTileView view = new() { FavoriteToggle = _ => Task.CompletedTask,
            DataContext = StateRow(new MediaLibraryTile(first, [first, second], MediaLibraryFavoriteState.None), MediaLibraryFlag.No, editable: true) };
        Assert.False(Named<ToggleButton>(view, "MediaTileFavoriteToggle").IsVisible);
        MediaLibraryTileView unwired = new() { FavoriteToggle = _ => Task.CompletedTask, CanToggleFavorite = () => false,
            DataContext = StateRow(new MediaLibraryTile(first, [first]), MediaLibraryFlag.No, editable: true) };
        Assert.False(Named<ToggleButton>(unwired, "MediaTileFavoriteToggle").IsVisible);
    }

    [Theory]
    [InlineData("en"), InlineData("de"), InlineData("es"), InlineData("fr"), InlineData("ru")]
    public void TaggingEntryTextsAreTranslatedWithTheirPlaceholders(string language)
    {
        Texts.SetLanguage(language);
        foreach (string key in new[] { "media.filter.withoutTags", "media.filter.withoutTagsHint" })
            Assert.False(string.IsNullOrWhiteSpace(Texts.T(key)) || Texts.T(key) == key, key);
        Assert.Contains("12", Texts.T("media.untagged.count", 12));
        Assert.Contains("12", Texts.Count("media.untagged.hint", 12));
        Assert.Contains("kitten", Texts.T("media.tile.tagHint", "kitten"));
        Assert.Contains("kitten", Texts.T("media.tile.threadTagHint", "kitten"));
        Assert.Contains("kitten", Texts.T("media.tile.moreTags", "kitten"));
        if (language == "en") return;
        string translated = Texts.T("media.filter.withoutTags");
        Texts.SetLanguage("en");
        Assert.NotEqual(Texts.T("media.filter.withoutTags"), translated);
    }
}

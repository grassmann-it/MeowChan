using Avalonia.Controls;
using Avalonia.Input;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using Ursa.Controls;

namespace MeowChan.DesktopTests;

/// <summary>
/// The context menu of the filter chips, one composition for the media page and the archive: which entries each kind of chip offers,
/// what they do, "Show only this", and that the chip's menu host takes the Menu key and Shift+F10.
/// </summary>
[Collection("Localization")]
public sealed class MediaChipContextMenuTests : IDisposable
{
    public MediaChipContextMenuTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static readonly Guid Alpha = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid Beta = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    private static MediaFilterChipActions Actions(List<string>? log, string? queueBlocked = null) => new(
        ShowOnly: term => log?.Add("only:" + term.Field), Edit: field => log?.Add("edit:" + field), CopyTagName: name => log?.Add("name:" + name),
        CopyFilters: () => log?.Add("filters"), QueueAll: () => log?.Add("queue"), QueueBlocked: () => queueBlocked,
        ResetAll: () => log?.Add("reset"), IsSuppressed: () => false);

    // Composed the way the media page composes a chip's menu: the switch exists for the fields that can exclude.
    private static ContextMenuModel Menu(MediaFilterTerm? term, MediaFilterTerm[] terms, string? tagName = null, List<string>? log = null,
        string? queueBlocked = null, bool available = true)
    {
        FilterChipMenuOptions options = MediaFilterChipMenu.Options(term, terms, tagName, Actions(log, queueBlocked));
        bool negatable = term != null && MediaFilterTerm.CanExclude(term.Field);
        return FilterChipMenu.Compose("chip", tagName ?? "value", term?.Exclude == true, negatable ? () => log?.Add("toggle") : null,
            () => log?.Add("remove"), options, available).ToModel();
    }

    private static string[] Ids(ContextMenuModel model) => model.Entries.Select(entry => entry.CommandId!).ToArray();

    private static MediaFilterTerm Tag(Guid id, bool exclude = false) => MediaFilterTerm.Tag(id, exclude);

    [Fact]
    public void AnIncludedTagChipOffersTheSwitchShowOnlyEditBothCopiesTheQueueRemoveAndResetAll()
    {
        ContextMenuModel model = Menu(Tag(Alpha), [Tag(Alpha), Tag(Beta)], "alpha");
        Assert.Equal(["tags.filter-toggle", "tags.filter-only", "media.filter.edit", "tags.copy-name", "media.filter.copy-query", "media.queue.all",
            "archive.clear-filter", "media.filter.reset-all"], Ids(model));
        Assert.Equal(4, model.Sections.Count);
        Assert.Equal(["archive.clear-filter", "media.filter.reset-all"], model.Sections[^1].Select(entry => entry.CommandId));
        Assert.Equal("Switch to exclude: alpha", model.Entries.First().Label);
        Assert.Equal("Show only this", model.Entries.ElementAt(1).Label);
    }

    [Fact]
    public void ALoneTagHasNothingToSingleOutAndAnExcludedOneSaysItWillIncludeAndNeverSinglesOut()
    {
        Assert.DoesNotContain("tags.filter-only", Ids(Menu(Tag(Alpha), [Tag(Alpha)], "alpha")));
        ContextMenuModel excluded = Menu(Tag(Alpha, exclude: true), [Tag(Alpha, exclude: true), Tag(Beta)], "alpha");
        Assert.DoesNotContain("tags.filter-only", Ids(excluded));
        Assert.Equal("Switch to include: alpha", excluded.Entries.First().Label);
    }

    [Fact]
    public void ATypeSourceOrBoardChipSwitchesAndSinglesOutWhenAnotherValueOfItsFieldIsInTheFilterButCopiesNoTagName()
    {
        MediaFilterTerm video = MediaFilterTerm.Type(MeowChan.Gallery.MediaLibraryKindFilter.Video), image = MediaFilterTerm.Type(MeowChan.Gallery.MediaLibraryKindFilter.Image);
        string[] expected = ["tags.filter-toggle", "tags.filter-only", "media.filter.edit", "media.filter.copy-query", "media.queue.all", "archive.clear-filter", "media.filter.reset-all"];
        Assert.Equal(expected, Ids(Menu(video, [video, image])));
        MediaFilterTerm source = MediaFilterTerm.Source("4chan"), other = MediaFilterTerm.Source("lainchan", exclude: true);
        Assert.Equal(["tags.filter-toggle", "tags.filter-only", "media.filter.edit", "media.filter.copy-query", "media.queue.all", "archive.clear-filter", "media.filter.reset-all"],
            Ids(Menu(source, [source, other])));
        MediaFilterTerm board = MediaFilterTerm.Board("4chan", "g"), second = MediaFilterTerm.Board("4chan", "a", exclude: true);
        Assert.Contains("tags.filter-only", Ids(Menu(board, [board, second])));
        // Alone, the value has nothing to single out.
        Assert.DoesNotContain("tags.filter-only", Ids(Menu(video, [video])));
    }

    [Theory]
    [InlineData("Rating", true), InlineData("Favorite", true), InlineData("Missing", true), InlineData("Untagged", true), InlineData("Sound", true),
        InlineData("Thread", false), InlineData("Name", false)]
    public void AChipOfAFieldThatCannotExcludeHasNoSwitchAndNoShowOnlyAndEditOnlyWhereThePickerHasTheField(string fieldName, bool edit)
    {
        MediaFilterField field = Enum.Parse<MediaFilterField>(fieldName);
        MediaFilterTerm term = field switch
        {
            MediaFilterField.Rating => MediaFilterTerm.Rating(3),
            MediaFilterField.Sound => MediaFilterTerm.Sound(MeowChan.Gallery.MediaLibrarySoundFilter.WithSound),
            MediaFilterField.Name => MediaFilterTerm.Name("r-6"),
            MediaFilterField.Thread => new MediaFilterTerm(MediaFilterField.Thread, "123"),
            _ => MediaFilterTerm.Flag(field)
        };
        string[] ids = Ids(Menu(term, [term]));
        Assert.DoesNotContain("tags.filter-toggle", ids);
        Assert.DoesNotContain("tags.filter-only", ids);
        Assert.DoesNotContain("tags.copy-name", ids);
        Assert.Equal(edit, ids.Contains("media.filter.edit"));
        Assert.Equal(["media.filter.copy-query", "media.queue.all", "archive.clear-filter", "media.filter.reset-all"], ids.Where(id => id != "media.filter.edit"));
    }

    [Fact]
    public void TheTagModeChipIsNoTermAndOnlyCopiesQueuesRemovesAndResets()
    {
        Assert.Equal(["media.filter.copy-query", "media.queue.all", "archive.clear-filter", "media.filter.reset-all"], Ids(Menu(null, [Tag(Alpha), Tag(Beta)])));
    }

    [Fact]
    public void EveryMenuStaysWithinNineEntriesUsesOnlyCatalogCommandsAndHasUniqueEntries()
    {
        MediaFilterTerm video = MediaFilterTerm.Type(MeowChan.Gallery.MediaLibraryKindFilter.Video);
        foreach (ContextMenuModel model in new[]
        {
            Menu(Tag(Alpha), [Tag(Alpha), Tag(Beta)], "alpha"), Menu(Tag(Alpha, true), [Tag(Alpha, true)], "alpha"), Menu(video, [video]),
            Menu(MediaFilterTerm.Rating(2), [MediaFilterTerm.Rating(2)]), Menu(null, [])
        })
        {
            Assert.InRange(model.Entries.Count(), 1, ContextMenuModel.MaxEntriesPerSection);
            Assert.Empty(model.OversizedSections);
            Assert.Equal(model.Entries.Count(), model.Entries.Select(entry => entry.Id).Distinct().Count());
            Assert.All(model.Entries, entry =>
            {
                Assert.True(CommandCatalog.TryGet(entry.CommandId!, out _), entry.CommandId);
                Assert.False(string.IsNullOrWhiteSpace(entry.Label));
            });
            // The destructive entries come last.
            Assert.Equal(["archive.clear-filter", "media.filter.reset-all"], model.Sections[^1].Select(entry => entry.CommandId));
        }
    }

    [Fact]
    public void EachEntryReachesItsOwnAction()
    {
        List<string> log = [];
        foreach (ContextMenuEntry entry in Menu(Tag(Alpha), [Tag(Alpha), Tag(Beta)], "alpha", log).Entries) entry.Invoke!();
        Assert.Equal(["toggle", "only:Tag", "edit:Tag", "name:alpha", "filters", "queue", "remove", "reset"], log);
    }

    [Fact]
    public void WhileTheFilterCannotChangeTheChangingEntriesSayWhyAndTheOthersStayAvailable()
    {
        ContextMenuModel model = Menu(Tag(Alpha), [Tag(Alpha), Tag(Beta)], "alpha", available: false);
        foreach (ContextMenuEntry entry in model.Entries)
        {
            bool changes = entry.CommandId is "tags.filter-toggle" or "tags.filter-only" or "archive.clear-filter" or "media.filter.reset-all";
            Assert.Equal(!changes, entry.Available);
            if (changes) Assert.Equal("Currently unavailable for this target.", entry.Reason);
        }
    }

    [Fact]
    public void TheQueueEntryIsDisabledWithTheReasonWhenNothingIsShownOrNoQueueIsWired()
    {
        ContextMenuEntry queue = Menu(Tag(Alpha), [Tag(Alpha)], "alpha", queueBlocked: "Nothing here.").Entries.Single(entry => entry.CommandId == "media.queue.all");
        Assert.False(queue.Available);
        Assert.Equal("Nothing here.", queue.Reason);
        Assert.Equal(Texts.T("media.queue.all"), queue.Label);
        Assert.True(Menu(Tag(Alpha), [Tag(Alpha)], "alpha").Entries.Single(entry => entry.CommandId == "media.queue.all").Available);
    }

    [Fact]
    public void TheRowMenuCopiesTheFilterAndResetsIt()
    {
        List<string> log = [];
        ContextMenuModel row = FilterChipMenu.ComposeRow("Active filters", () => log.Add("filters"), () => log.Add("reset")).ToModel();
        Assert.Equal(["media.filter.copy-query", "media.filter.reset-all"], Ids(row));
        Assert.Equal(2, row.Sections.Count);
        foreach (ContextMenuEntry entry in row.Entries) entry.Invoke!();
        Assert.Equal(["filters", "reset"], log);
    }

    // ---- Show only this -----------------------------------------------------------------------------------------------

    [Fact]
    public void ShowOnlyThisDropsEveryOtherTagIncludedOrExcludedAndKeepsTheRestOfTheFilter()
    {
        MediaFilterTerm video = MediaFilterTerm.Type(MeowChan.Gallery.MediaLibraryKindFilter.Video);
        MediaFilterTerm[] terms = [Tag(Alpha), Tag(Beta), Tag(Guid.NewGuid(), exclude: true), video, MediaFilterTerm.Rating(3)];
        Assert.Equal([Tag(Alpha), video, MediaFilterTerm.Rating(3)], MediaFilterTermSet.OnlyThis(terms, Tag(Alpha)));
        Assert.True(MediaFilterTermSet.CanShowOnly(terms, Tag(Beta)));
        Assert.False(MediaFilterTermSet.CanShowOnly(terms, MediaFilterTerm.Rating(3)));
        Assert.False(MediaFilterTermSet.CanShowOnly(terms, video));
        Assert.False(MediaFilterTermSet.CanShowOnly([Tag(Alpha)], Tag(Alpha)));
    }

    [Fact]
    public void ShowOnlyAnImageTypeAlsoDropsTheSoundBecauseASoundNeedsVideos()
    {
        MediaFilterTerm image = MediaFilterTerm.Type(MeowChan.Gallery.MediaLibraryKindFilter.Image), video = MediaFilterTerm.Type(MeowChan.Gallery.MediaLibraryKindFilter.Video);
        MediaFilterTerm sound = MediaFilterTerm.Sound(MeowChan.Gallery.MediaLibrarySoundFilter.WithSound);
        MediaFilterTerm[] terms = [image, video, sound];
        Assert.Equal([image], MediaFilterTermSet.OnlyThis(terms, image));
        Assert.Equal([video, sound], MediaFilterTermSet.OnlyThis(terms, video));
    }

    [Fact]
    public void ShowOnlyASourceDropsTheExcludedSourcesAndNeverSinglesOutAnExcludedChip()
    {
        MediaFilterTerm a = MediaFilterTerm.Source("4chan"), b = MediaFilterTerm.Source("lainchan", exclude: true);
        Assert.Equal([a], MediaFilterTermSet.OnlyThis([a, b], a));
        Assert.False(MediaFilterTermSet.CanShowOnly([a, b], b));
        // A term the set does not hold changes nothing.
        MediaFilterTerm[] set = [a];
        Assert.Same(set, MediaFilterTermSet.OnlyThis(set, b));
    }

    // ---- opening it ---------------------------------------------------------------------------------------------------

    private static KeyEventArgs KeyDown(Control source, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        new() { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = source };

    [Fact]
    public void TheMenuKeyAndShiftF10OpenTheChipMenuAndOtherKeysDoNotTouchIt()
    {
        ClosableTag chip = WorkspaceChips.CreateTermChip("Tag: alpha", negatable: true, exclude: false, () => { }, () => { }, "alpha", menu: FilterChipMenuOptions.Plain);
        Assert.NotNull(WorkspaceChips.MenuHostOf(chip));
        foreach ((Key key, KeyModifiers modifiers) in new[] { (Key.Apps, KeyModifiers.None), (Key.F10, KeyModifiers.Shift) })
        {
            KeyEventArgs e = KeyDown(chip, key, modifiers);
            chip.RaiseEvent(e);
            Assert.True(e.Handled, key + " must be taken by the chip's menu host");
        }
        // F10 alone, Shift+Apps and letters are not the context key; the chip's own keys keep working (Enter switches, Delete removes).
        foreach ((Key key, KeyModifiers modifiers) in new[] { (Key.F10, KeyModifiers.None), (Key.A, KeyModifiers.None), (Key.F9, KeyModifiers.Shift) })
        {
            KeyEventArgs e = KeyDown(chip, key, modifiers);
            chip.RaiseEvent(e);
            Assert.False(e.Handled, key + " must not open the menu");
        }
    }

    [Fact]
    public void ThePlainArchiveChipAndTheChipRowShareTheSameHostAndAChipWithoutAMenuTakesNoKeys()
    {
        ClosableTag plain = ArchivePage.CreateFilterTag("Source: 4chan", () => { });
        KeyEventArgs ignored = KeyDown(plain, Key.Apps);
        plain.RaiseEvent(ignored);
        Assert.False(ignored.Handled);
        // Attached through the one entry point, it takes the key like any other chip.
        ContextMenuHost host = WorkspaceChips.AttachMenu(plain, () => FilterChipMenu.Compose("Source: 4chan", null, false, null, () => { }, FilterChipMenuOptions.Plain));
        Assert.Same(host, WorkspaceChips.MenuHostOf(plain));
        KeyEventArgs taken = KeyDown(plain, Key.F10, KeyModifiers.Shift);
        plain.RaiseEvent(taken);
        Assert.True(taken.Handled);
        Assert.Equal(["archive.clear-filter"], WorkspaceChips.MenuModelOf(plain)!.Entries.Select(entry => entry.CommandId));
        // A second attach replaces the first: one menu per chip.
        ContextMenuHost again = WorkspaceChips.AttachMenu(plain, () => null);
        Assert.NotSame(host, again);
        Assert.Null(WorkspaceChips.MenuModelOf(plain));
    }

    // ---- the archive shares the chip model ------------------------------------------------------------------------------

    [Fact]
    public void AnArchiveTagChipHasOneCopyTagNameTheTextCopyAndNoResetAllUntilItIsAnAppliedFilter()
    {
        List<string> log = [];
        FilterChipMenuOptions draft = new() { TagName = "alpha", CopyText = () => log.Add("text"), CopyName = () => log.Add("name") };
        ClosableTag chip = ArchivePage.CreateTagFilterChip("Tag: alpha", "alpha", Alpha, exclude: false, () => { }, () => { }, draft);
        ContextMenuModel draftModel = WorkspaceChips.MenuModelOf(chip)!;
        Assert.Equal(["tags.filter-toggle", "archive.copy-filter", "tags.copy-name", "archive.clear-filter"], Ids(draftModel));
        Assert.Equal(1, Ids(draftModel).Count(id => id == "tags.copy-name"));

        FilterChipMenuOptions applied = draft with { ResetAll = () => log.Add("reset"), IsAvailable = () => false };
        ClosableTag appliedChip = ArchivePage.CreateTagFilterChip("Tag: alpha", "alpha", Alpha, exclude: false, () => { }, () => { }, applied);
        ContextMenuModel appliedModel = WorkspaceChips.MenuModelOf(appliedChip)!;
        Assert.Equal(["tags.filter-toggle", "archive.copy-filter", "tags.copy-name", "archive.clear-filter", "media.filter.reset-all"], Ids(appliedModel));
        Assert.False(appliedModel.Entries.Single(entry => entry.CommandId == "media.filter.reset-all").Available);
        Assert.True(appliedModel.Entries.Single(entry => entry.CommandId == "archive.copy-filter").Available);
    }

    [Fact]
    public void AChipWhosePageIsGoneOpensNothing()
    {
        bool gone = false;
        ClosableTag chip = WorkspaceChips.CreateTermChip("Tag: alpha", negatable: true, exclude: false, () => { }, () => { }, "alpha",
            menu: new FilterChipMenuOptions { IsSuppressed = () => gone });
        Assert.NotNull(WorkspaceChips.MenuModelOf(chip));
        gone = true;
        Assert.Null(WorkspaceChips.MenuModelOf(chip));
    }

    // ---- texts ----------------------------------------------------------------------------------------------------------

    public static TheoryData<string> Languages() => new() { "en", "de", "es", "fr", "ru", "zh-Hans", "ja", "ko", "pt-BR", "cs", "it", "pl" };

    [Theory]
    [MemberData(nameof(Languages))]
    public void EveryEntryOfTheMenuHasALabelInEveryLanguage(string language)
    {
        Texts.SetLanguage(language);
        foreach (string id in new[] { "tags.filter-toggle", "tags.filter-only", "media.filter.edit", "tags.copy-name", "media.filter.copy-query", "media.queue.all",
            "archive.clear-filter", "media.filter.reset-all", "archive.copy-filter" })
        {
            string label = CommandCatalog.Label(id);
            Assert.False(string.IsNullOrWhiteSpace(label) || label == CommandCatalog.Get(id).LabelKey, language + ": " + id);
            if (language != "en") Assert.True(label != CommandCatalog.Get(id).LabelEn || id is "media.filter.edit", language + ": " + id + " is still English");
        }
        Assert.False(Texts.T("tags.filter.toExclude", "x").Contains("{0}"));
    }
}

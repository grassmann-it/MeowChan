using System.Runtime.CompilerServices;
using System.Text.Json;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>
/// QW-A (review of 15.11.2026, items 1, 6, 7, 10, 13): the ways into things that used to hide. Pure rules are tested as functions
/// (the header book button's visibility and click routing, the history list's size, the header menu groups, the footer fit); the
/// pieces that need a window (the empty Media page, the menus' entries, the flyout) are checked on the catalog, the markup and the
/// source, and by the smokes media-page, footer-navigation, context-menus-header, reading-position and reader-ux.
/// </summary>
[Collection("Localization")]
public sealed class ShellReachabilityTests : IDisposable
{
    public ShellReachabilityTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
    private static string Source(string relative) => File.ReadAllText(Root(relative));

    // ---- #10: the Continue reading button ----

    [Theory]
    [InlineData(true, 1, false, true)]    // an entry in the loaded history
    [InlineData(true, 7, true, true)]
    [InlineData(true, 0, true, true)]     // the position is known before the first snapshot arrives
    [InlineData(true, 0, false, false)]   // no history: nothing to continue
    [InlineData(false, 5, true, false)]   // remembering is switched off: the button is gone
    [InlineData(false, 0, false, false)]
    public void TheBookIsShownWheneverTheHistoryHasAnEntryAndRememberingIsOn(bool remember, int entries, bool stored, bool expected) =>
        Assert.Equal(expected, ReadingResumeRouting.Visible(remember, entries, stored));

    [Fact]
    public void TheVisibilityRuleDoesNotNameAWorkViewAPageOrAnOpenThread()
    {
        // Pure: the same arguments give the same answer (nothing else is read), and the window's refresh no longer hides it over a thread.
        string resume = Source("Desktop/Shell/MainWindow.Resume.cs");
        Assert.Contains("ReadingResumeRouting.Visible(", resume);
        Assert.DoesNotContain("ReadingResumeContextVisible", resume);
        string policy = Source("Desktop/WorkViews/WorkViewPolicy.Shell.cs");
        Assert.Matches(@"Same\(""shell\.header\.continue-reading"", WorkViewPage\.Shell, WorkViewKind\.Action, Direct, ""navigation\.continue-reading""\)", policy);
        Assert.DoesNotContain("only while a reading position exists", policy);
    }

    [Theory]
    [InlineData(false, false, "resume.tip.jump")]
    [InlineData(true, true, "resume.tip.history")]
    public void ALeftClickJumpsOutsideAThreadAndOpensTheHistoryInsideOne(bool threadOpen, bool opensHistory, string tipKey)
    {
        Assert.Equal(opensHistory ? ReadingResumeRouting.ClickAction.OpenHistory : ReadingResumeRouting.ClickAction.JumpToLast, ReadingResumeRouting.Click(threadOpen));
        Assert.Equal(tipKey, ReadingResumeRouting.TipKey(threadOpen));
        // The tooltip states the click action in every language.
        Assert.NotEqual(tipKey, Texts.T(tipKey));
    }

    [Fact]
    public void ThePlacesOpenUnlessTheGalleryOrAnotherActionHasTheReader()
    {
        Assert.True(ReadingResumeRouting.PlacesOpenable(galleryOpen: false, otherActionBusy: false));
        Assert.True(ReadingResumeRouting.PlacesOpenable(galleryOpen: true, otherActionBusy: false));
        Assert.False(ReadingResumeRouting.PlacesOpenable(galleryOpen: false, otherActionBusy: true));
    }

    [Fact]
    public void TheWindowRoutesButtonShortcutAndAppMenuThroughTheOneRuleAndTheArrowAndRightClickAlwaysOpenTheHistory()
    {
        string resume = Source("Desktop/Shell/MainWindow.Resume.cs");
        Assert.Contains("ReadingResumeRouting.Click(ReadingThreadOpen)", resume);
        Assert.Matches(@"ContinueReadingButton\.Click \+= \(_, _\) => ActivateReadingResume\(", resume);
        Assert.Matches(@"_resumeReadingMenu\.Click \+= \(_, _\) => ActivateReadingResume\(", resume);
        Assert.Contains("() => ActivateReadingResume(keyboard: true)", resume); // the shortcut
        Assert.Contains("ContinueReadingHistoryButton.Click += (_, _) => ShowReadingHistoryList(", resume); // the arrow: always the list
        Assert.Contains("AddTrigger(ContinueReadingButton)", resume);           // right click, Menu key, Shift+F10
        Assert.Contains("AddTrigger(ContinueReadingHistoryButton)", resume);
        Assert.Contains("ActivateReadingResume(keyboard: true); return;", Source("Desktop/Shell/MainWindow.MacMenu.cs"));
        // The tooltip names the click that is current.
        Assert.Contains("ReadingResumeRouting.TipKey(ReadingThreadOpen)", resume);
        // The arrow is a button next to the book in the header, hidden with it.
        string markup = Source("Desktop/Shell/MainWindow.axaml");
        Assert.Contains("x:Name=\"ContinueReadingHistoryButton\"", markup);
        Assert.True(markup.IndexOf("ContinueReadingHistoryButton", StringComparison.Ordinal) > markup.IndexOf("x:Name=\"ContinueReadingButton\"", StringComparison.Ordinal));
        Assert.True(markup.IndexOf("ContinueReadingHistoryButton", StringComparison.Ordinal) < markup.IndexOf("x:Name=\"PauseButton\"", StringComparison.Ordinal));
        // The history opens places while a thread or gallery is open; the selected place replaces it and the gallery is closed first.
        Assert.Contains("PlacesOpenable(GalleryOverlayVisible", resume);
        Assert.Contains("if (GalleryOverlayVisible) CloseGallery(restoreOrigin: false);", resume);
    }

    // ---- #10: the size of the history list ----

    [Fact]
    public void TheHistoryListIsAtLeast320DipWideAndNeverWiderThanItsMaximumOrTheWindow()
    {
        Assert.True(ReadingHistoryFlyoutLayout.MinWidth >= 320);
        Assert.True(ReadingHistoryFlyoutLayout.MaxWidth > ReadingHistoryFlyoutLayout.MinWidth);
        Assert.Equal(ReadingHistoryFlyoutLayout.MaxWidth, ReadingHistoryFlyoutLayout.MaxWidthFor(1920));
        Assert.Equal(ReadingHistoryFlyoutLayout.MinWidth, ReadingHistoryFlyoutLayout.MaxWidthFor(300));   // a tiny window never goes below the minimum
        Assert.Equal(500 - 2 * ReadingHistoryFlyoutLayout.WindowMargin, ReadingHistoryFlyoutLayout.MaxWidthFor(500)); // inside the window
    }

    [Theory]
    [InlineData(10, 1200)]
    [InlineData(3, 1200)]
    [InlineData(25, 2000)]
    public void TheListIsAsHighAsTheSettingsRowsBeforeItScrolls(int entries, double windowHeight) =>
        Assert.Equal(entries * ReadingHistoryFlyoutLayout.RowHeight, ReadingHistoryFlyoutLayout.ListMaxHeight(entries, windowHeight));

    [Fact]
    public void ASmallWindowShortensTheListButNeverBelowTwoRowsAndItNeverExceedsTheRows()
    {
        double room = 500 - ReadingHistoryFlyoutLayout.HeaderReserve - ReadingHistoryFlyoutLayout.FooterHeight - 2 * ReadingHistoryFlyoutLayout.WindowMargin;
        Assert.Equal(room, ReadingHistoryFlyoutLayout.ListMaxHeight(25, 500));
        Assert.Equal(2 * ReadingHistoryFlyoutLayout.RowHeight, ReadingHistoryFlyoutLayout.ListMaxHeight(25, 100));
        Assert.Equal(2 * ReadingHistoryFlyoutLayout.RowHeight, ReadingHistoryFlyoutLayout.ListMaxHeight(2, 100));
        Assert.Equal(2 * ReadingHistoryFlyoutLayout.RowHeight, ReadingHistoryFlyoutLayout.ListMaxHeight(0, 5000)); // an empty history keeps the two-row minimum for its note
    }

    [Fact]
    public void TheListIsAFlyoutWithItsOwnStyleSoItIsNotATinyPopup()
    {
        string styles = Source("Desktop/WorkspaceStyles.axaml");
        int at = styles.IndexOf("FlyoutPresenter.reading-history", StringComparison.Ordinal);
        Assert.True(at >= 0);
        string rule = styles[at..styles.IndexOf("</Style>", at, StringComparison.Ordinal)];
        Assert.Contains("Property=\"MinWidth\" Value=\"320\"", rule);
        string flyout = Source("Desktop/Shell/ReadingHistoryFlyout.cs");
        Assert.Contains("FlyoutPresenterClasses.Add(\"reading-history\")", flyout);
        Assert.Contains("TextTrimming.CharacterEllipsis", flyout);                // entries are trimmed ...
        Assert.Contains("ToolTip.SetTip(button", flyout);                         // ... with the whole text in the tooltip
        Assert.Contains("PlacementMode.BottomEdgeAlignedRight", flyout);          // under the button, inside the window
        Assert.Contains("VerticalScrollBarVisibility = ScrollBarVisibility.Auto", flyout); // scrolls after the setting's rows
        // The submenu of the app menu is gone: it was a popup as narrow as its shortest entry. The entry opens the same list.
        string resume = Source("Desktop/Shell/MainWindow.Resume.cs");
        Assert.DoesNotContain("SubmenuOpened", resume);
        Assert.DoesNotContain("ContextMenuHost", resume);
        Assert.Contains("ShowReadingHistoryList(keyboard: false)", resume);
    }

    // ---- #13: the header menu groups ----

    private static readonly string[] AllEntries =
        ["resume", "history", "postBookmarks", "readerGroups", "import", "booru", "profile", "pause", "theme", "fullscreen", "view", "settings", "shortcuts", "updates", "about", "exit"];

    private static string Shape(IEnumerable<HeaderMenuEntry> entries) => string.Join(" ", entries.Select(entry => entry.Kind switch
    {
        HeaderMenuEntryKind.Separator => "|",
        HeaderMenuEntryKind.Label => "[" + entry.Key + "]",
        _ => entry.Key
    }));

    [Fact]
    public void TheHeaderMenuHasTheThreeLabelledGroupsAndExitApart()
    {
        Assert.Equal("[menu.group.read] resume history postBookmarks readerGroups | [menu.group.collection] import booru profile | "
            + "[menu.group.app] pause theme fullscreen view settings shortcuts updates about | exit",
            Shape(HeaderMenuGroups.Arrange(AllEntries)));
        // The order of the input does not matter (the parts of the window insert themselves in their own order).
        Assert.Equal(Shape(HeaderMenuGroups.Arrange(AllEntries)), Shape(HeaderMenuGroups.Arrange(AllEntries.Reverse())));
    }

    [Fact]
    public void AGroupWithoutEntriesLeavesNoLabelAndNoSeparatorAndAnUnknownEntryIsNotLost()
    {
        Assert.Equal("[menu.group.read] resume | [menu.group.collection] import profile other:0 | [menu.group.app] pause | exit",
            Shape(HeaderMenuGroups.Arrange(["resume", "import", "profile", "pause", "exit", "other:0"])));
        Assert.Equal("[menu.group.app] settings", Shape(HeaderMenuGroups.Arrange(["settings"])));
        Assert.Empty(HeaderMenuGroups.Arrange([]));
        Assert.Equal(HeaderMenuGroup.Collection, HeaderMenuGroups.GroupOf("something-new"));
    }

    [Fact]
    public void TheTaggingQueueIsNotAHeaderMenuEntryAnymoreButTheCommandStays()
    {
        Assert.DoesNotContain(HeaderMenuGroups.Groups, group => HeaderMenuGroups.Keys(group).Any(key => key.Contains("queue", StringComparison.OrdinalIgnoreCase)));
        string tagQueue = Source("Desktop/Shell/MainWindow.TagQueue.cs");
        Assert.DoesNotContain("_tagQueueMenu", tagQueue);
        Assert.DoesNotContain("WorkspaceMenuItem", tagQueue);
        Assert.Contains("new CommandRegistration(\"media.queue.open\"", tagQueue); // shortcut and Media page still reach it
        Assert.DoesNotContain("app-menu", CommandCatalog.Get("media.queue.open").Contexts);
        Assert.Contains("media-page", CommandCatalog.Get("media.queue.open").Contexts);
    }

    [Fact]
    public void EveryGroupLabelIsATextInAllLanguagesAndTheWindowArrangesAfterEveryPartInserted()
    {
        foreach (HeaderMenuGroup group in HeaderMenuGroups.Groups)
        {
            string? key = HeaderMenuGroups.LabelKey(group);
            if (key == null) { Assert.Equal(HeaderMenuGroup.Exit, group); continue; }
            Assert.NotEqual(key, Texts.T(key));
        }
        string ctor = Source("Desktop/Shell/MainWindow.axaml.cs");
        Assert.True(ctor.IndexOf("ArrangeHeaderMenu();", StringComparison.Ordinal) > ctor.IndexOf("InitializeProfileMenu();", StringComparison.Ordinal));
        Assert.True(ctor.IndexOf("ArrangeHeaderMenu();", StringComparison.Ordinal) > ctor.IndexOf("InitializeWorkspace()", StringComparison.Ordinal));
        string window = Source("Desktop/Shell/MainWindow.HeaderMenuGroups.cs");
        Assert.Contains("Classes.Add(\"localized\")", window); // a language change relabels the group labels (LocalizeWorkspace)
        Assert.Contains("IsEnabled = false", window);
    }

    // ---- #6: the footer fits ----

    private static FooterFitButton Page(string id, double label = 100, double icon = 32, bool direct = true, bool available = true) => new(id, label, icon, direct, available);

    private static FooterFitResult Plan(double available, params FooterFitButton[] pages) =>
        FooterFitPlanner.Plan(available, 4, 9, pages, "navigation.downloads", Page("navigation.settings", 100, 32), Page("navigation.tools-menu", 80, 32), flags => flags.Count(flag => flag) > 0 ? 1 : 0);

    private static FooterFitButton[] DefaultPages() =>
    [
        Page("navigation.downloads"), Page("navigation.archive"), Page("navigation.media"), Page("navigation.tagging"), Page("navigation.rules"), Page("navigation.status")
    ];

    [Fact]
    public void AWideFooterKeepsEveryButtonWithIconAndText()
    {
        FooterFitResult wide = Plan(2000, DefaultPages());
        Assert.False(wide.Compact);
        Assert.Empty(wide.MovedToMore);
    }

    [Fact]
    public void ANarrowFooterTurnsThePageButtonsIntoIconsBeforeAnyPageMovesToMore()
    {
        // Seven labelled buttons (6 pages + Settings) need about 700 + gaps; icons need about 7 * 32 + gaps.
        FooterFitResult narrow = Plan(520, DefaultPages());
        Assert.True(narrow.PagesIconOnly);
        Assert.Empty(narrow.MovedToMore);
        Assert.False(narrow.SettingsIconOnly || narrow.DownloadsIconOnly || narrow.ToolsIconOnly);
    }

    [Fact]
    public void AVeryNarrowFooterMovesTheRightmostPagesToMoreThenShrinksSettingsDownloadsAndMore()
    {
        FooterFitResult tight = Plan(240, DefaultPages());
        Assert.True(tight.PagesIconOnly);
        Assert.NotEmpty(tight.MovedToMore);
        Assert.DoesNotContain("navigation.downloads", tight.MovedToMore);
        // The rightmost page leaves first: what moved is a suffix of the order.
        string[] order = DefaultPages().Select(page => page.Id).Where(id => id != "navigation.downloads").ToArray();
        string[] expectedMoved = order.Skip(order.Length - tight.MovedToMore.Count).ToArray();
        Assert.True(expectedMoved.ToHashSet().SetEquals(tight.MovedToMore));

        FooterFitResult tiny = Plan(110, DefaultPages());
        Assert.True(tiny.SettingsIconOnly && tiny.DownloadsIconOnly && tiny.ToolsIconOnly);
        Assert.Equal(order.Length, tiny.MovedToMore.Count);
    }

    [Fact]
    public void MoreKeepsItsLabelUntilTheLastFallbackAndAPageTheLayoutHidesIsNeverCountedAsMoved()
    {
        FooterFitResult result = Plan(300, DefaultPages());
        Assert.False(result.ToolsIconOnly);
        FooterFitButton[] pages = [Page("navigation.downloads"), Page("navigation.archive"), Page("navigation.media", direct: false), Page("navigation.booru", available: false)];
        FooterFitResult hidden = Plan(2000, pages);
        Assert.Empty(hidden.MovedToMore); // hidden by the layout or unavailable: not the planner's doing
    }

    [Fact]
    public void TheWindowAppliesThePlannerAndGivesEveryNarrowButtonItsNameAndTooltipFromTheCommand()
    {
        string navigation = Source("Desktop/Shell/MainWindow.Navigation.cs");
        Assert.Contains("FooterFitPlanner.Plan(", navigation);
        Assert.Contains("iconOnly: fit.PagesIconOnly, iconWithLabel: !fit.PagesIconOnly", navigation);
        Assert.DoesNotContain("double RequiredWidth()", navigation);
        // CommandBindings.Apply with iconOnly sets the command's tooltip and AutomationName (CommandBindingsTests covers the adapter).
        string bindings = Source("Desktop/Commands/CommandBindings.cs");
        Assert.Contains("AutomationProperties.SetName(button, label);", bindings);
    }

    // ---- #1 / #7 / #10: commands, icons and texts of the new entries ----

    [Theory]
    [InlineData("thread.show-in-media", "grid", new[] { "downloads-thread-menu", "archive-card-menu", "archive-table-menu", "reader-thread-menu" })]
    [InlineData("media.link-folder", "folder", new[] { "media-empty-state" })]
    [InlineData("media.folders", "folder", new[] { "media-page" })]
    public void TheNewCommandsAreCatalogEntriesWithAnIconAndTheirPlaces(string id, string icon, string[] contexts)
    {
        CommandDefinition command = CommandCatalog.Get(id);
        Assert.Equal(icon, command.Icon);
        Assert.True(File.Exists(Root("Assets/icons/feather/masks/" + icon + ".png")));
        foreach (string context in contexts) Assert.Contains(context, command.Contexts);
        Assert.False(string.IsNullOrWhiteSpace(CommandCatalog.Label(id)));
        Assert.NotEqual(command.LabelKey, CommandCatalog.Label(id));
    }

    [Fact]
    public void ShowInMediaIsOneCommandInTheDownloadsArchiveAndReaderThreadMenus()
    {
        Assert.Contains("\"thread.show-in-media\"", Source("Desktop/Shell/MainWindow.Menus.cs"));
        Assert.Contains("\"thread.show-in-media\"", Source("Desktop/Views/Archive/ArchivePage.Commands.cs"));
        Assert.Contains("thread.show-in-media", Source("Desktop/Views/Archive/ArchivePage.ThreadMenu.cs"));
        Assert.Single(CommandCatalog.All, entry => entry.Id == "thread.show-in-media");
    }

    private static readonly string[] NewKeys =
    [
        "command.thread.show-in-media", "command.media.link-folder", "command.media.folders", "media.empty.copyOrLink", "media.queueAll.hint",
        "media.queueAll.reason.none", "reader.thread.postBookmarks", "resume.tip.jump", "resume.tip.history", "resume.history.open",
        "menu.group.read", "menu.group.collection", "menu.group.app"
    ];

    [Fact]
    public void EveryNewTextHasEnglishGermanAndARealTranslationInEveryBundledPack()
    {
        foreach ((string key, string english, string german) in ShellReachabilityCatalog.Entries)
        {
            Assert.Contains(key, NewKeys);
            Assert.NotEqual(english, german == "App" ? "" : german); // "App" is the same word in both languages
            Assert.DoesNotContain("→", english + german); // no arrow or glyph characters: the arrow is a drawn icon
        }
        Assert.Equal(NewKeys.Order(), ShellReachabilityCatalog.Entries.Select(entry => entry.Key).Order());
        string packs = Root("Desktop/Localization/Packs");
        foreach (string file in Directory.EnumerateFiles(packs, "*.json"))
        {
            using JsonDocument pack = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement strings = pack.RootElement.GetProperty("strings");
            foreach (string key in NewKeys)
            {
                Assert.True(strings.TryGetProperty(key, out JsonElement value), Path.GetFileName(file) + " lacks " + key);
                string english = ShellReachabilityCatalog.Entries.Single(entry => entry.Key == key).English;
                // Short group names can be the same word as the English one (App, Collection); every sentence must be translated.
                if (!key.StartsWith("menu.group.", StringComparison.Ordinal)) Assert.NotEqual(english, value.GetString());
                Assert.False(string.IsNullOrWhiteSpace(value.GetString()));
            }
        }
    }

    [Fact]
    public void GermanUsesDuAndTheClickTipsNameBothClicks()
    {
        Texts.SetLanguage("de");
        Assert.Contains("Klick:", Texts.T("resume.tip.jump"));
        Assert.Contains("Rechtsklick", Texts.T("resume.tip.jump"));
        Assert.Contains("du", Texts.T("resume.tip.history"));
        Assert.DoesNotContain(" Sie ", Texts.T("media.empty.copyOrLink") + Texts.T("media.queueAll.hint"));
    }
}

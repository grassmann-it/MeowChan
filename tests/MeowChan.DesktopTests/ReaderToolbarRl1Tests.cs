using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using Ursa.Controls;

namespace MeowChan.DesktopTests;

/// <summary>
/// RL1 (15.11.2026): the reader toolbar in groups with Read as the eye, "View" and the quick settings as one gear popover, full screen
/// under More. The page needs an Avalonia application, so the contract is checked on the catalog, the XAML, the policy, the popover's
/// layout and the pure rules; the look and the behaviour (separators, the dot, the popover rows) are the reader-toolbar smokes.
/// </summary>
[Collection("Localization")]
public sealed class ReaderToolbarRl1Tests : IDisposable
{
    public ReaderToolbarRl1Tests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string Root(string path, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", path));

    private static XElement[] ToolbarItems() => XDocument.Load(Root("Desktop/Views/Archive/ArchivePage.axaml")).Descendants()
        .Single(element => element.Name.LocalName == "ActionToolBar" && (string?)element.Attribute(X + "Name") == "ReaderToolbar").Elements().Where(element => !element.Name.LocalName.Contains('.')).ToArray();

    private static string Name(XElement item) => (string)item.Attribute(X + "Name")!;
    private static int Group(XElement item) => int.Parse((string?)item.Attributes().SingleOrDefault(attribute => attribute.Name.LocalName == "ActionToolBar.Group")?.Value ?? "0");

    // ---- the bar --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheBarStandsInItsGroupsInTheOrderOfTheSpec()
    {
        XElement[] items = ToolbarItems();
        string[] visible = items.Select(Name).Where(name => name is not ("ReaderUnreadReturn" or "ReaderSkipUnavailableGroupMember" or "ArchiveHistoryBack" or "ArchiveReaderFullscreen")).ToArray();
        // Thread mark | Read, Gallery, Find | Tags, Export | Settings, Thread | Media sidebar icon (right edge) (the states and the way back sit behind the star, and last).
        Assert.Equal(["ArchiveCurrentThreadFavorite", "ArchiveReadPosts", "ArchiveOpenGallery", "ReaderFindOpen", "ReaderTagsOpen", "ExportArchiveButton",
            "ReaderSettingsOpen", "ReaderThreadOpen", "ReaderMediaSidebarToggle"], visible);
        string[] all = items.Select(Name).ToArray();
        // The returns of a state stay right behind the star, in its group, so a state never sits between two tools.
        Assert.Equal(["ArchiveCurrentThreadFavorite", "ReaderUnreadReturn", "ReaderSkipUnavailableGroupMember"], all.Take(3));
        Assert.All(items.Take(3), item => Assert.Equal(0, Group(item)));
        Dictionary<string, int> groups = items.ToDictionary(Name, Group);
        Assert.Equal(1, groups["ArchiveReadPosts"]); Assert.Equal(1, groups["ArchiveOpenGallery"]); Assert.Equal(1, groups["ReaderFindOpen"]);
        Assert.Equal(2, groups["ReaderTagsOpen"]); Assert.Equal(2, groups["ExportArchiveButton"]); 
        Assert.Equal(3, groups["ReaderSettingsOpen"]); Assert.Equal(3, groups["ReaderThreadOpen"]); Assert.Equal(4, groups["ReaderMediaSidebarToggle"]);
        // Four lines between the five groups of the default bar; the history return and full screen are a fifth group at the end.
        Assert.Equal(4, GroupsOf(visible, groups).Zip(GroupsOf(visible, groups).Skip(1)).Count(pair => pair.First != pair.Second));
        Assert.Equal(["ArchiveHistoryBack", "ArchiveReaderFullscreen"], all.TakeLast(2));
        Assert.All(items.TakeLast(2), item => Assert.Equal(4, Group(item)));
    }

    private static int[] GroupsOf(string[] names, Dictionary<string, int> groups) => names.Select(name => groups[name]).ToArray();

    [Fact]
    public void TheSeparatorRuleDrawsALineOnlyBetweenShownItemsOfDifferentGroups()
    {
        int[] groups = [0, 0, 1, 1, 1, 2, 2, 3, 4];
        Assert.Equal([2, 5, 7, 8], ActionToolBarPanel.GroupStarts(groups, groups.Select(_ => true).ToArray()));
        // A group that moved under More leaves no line behind: without Find and the tags the lines stand between the groups still shown.
        bool[] shown = [true, true, true, true, false, false, false, true, false];
        Assert.Equal([2, 7], ActionToolBarPanel.GroupStarts(groups, shown));
        // Nothing hidden before the first shown item gives a line in front of it; one group alone has none.
        Assert.Empty(ActionToolBarPanel.GroupStarts([0, 0, 0], [true, true, true]));
        Assert.Equal([2], ActionToolBarPanel.GroupStarts([0, 0, 1], [false, true, true]));
        // A toolbar that never sets a group (every other bar) has none.
        Assert.Equal(0, ActionToolBar.GetGroup(new Avalonia.Controls.Button()));
    }

    [Fact]
    public void LargeReaderCarriesTheOpenElsewhereIconAndTheListsReadKeepsTheEye()
    {
        // Tom 15.11. (supersedes RL1 "Read as the eye"): the button that opens the large reader says so and shows that it opens the thread
        // elsewhere. The icon catalog has no expand arrow ("maximize" is full screen's frame on the same bar), so it is external-link.
        Assert.Equal("external-link", CommandCatalog.Get("reader.expand").Icon);
        Assert.NotEqual(CommandCatalog.Get("window.fullscreen").Icon, CommandCatalog.Get("reader.expand").Icon);
        Assert.Equal("eye", CommandCatalog.Get("thread.read").Icon);
        Assert.Equal("external-link", CommandCatalog.Get("location.open-source").Icon);
        Assert.Equal("tag", CommandCatalog.Get("thread.tags").Icon);
        Assert.Equal("settings", CommandCatalog.Get("reader.quick-settings").Icon);
        string source = File.ReadAllText(Root("Desktop/Views/Archive/ArchivePage.axaml.cs"));
        Assert.Contains("CommandBindings.ShowIconBeforeText(ReaderThreadOpen, \"file-text\");", source);
        Assert.Contains("CommandBindings.Apply(ArchiveReadPosts, \"reader.expand\", NoThreadSelectedReason, iconWithLabel: true);", source);
        Assert.Contains("const string icon = \"settings\";", File.ReadAllText(Root("Desktop/Views/Archive/ArchivePage.QuickSettings.cs")));
        // "file-text" means Thread everywhere; the eye stays the list's "Read posts".
        string[] eye = CommandCatalog.All.Where(entry => entry.Icon == "eye").Select(entry => entry.Id).ToArray();
        Assert.DoesNotContain("reader.expand", eye);
        Assert.Contains("thread.read", eye);
    }

    [Fact]
    public void ReadAndTheListsPostsReadAreOneThingInTheLabelsOfBothLanguages()
    {
        Assert.Equal("Large reader", CommandCatalog.Label("reader.expand"));
        Assert.Equal("Large reader", Texts.T("command.reader.expand.compact"));
        Assert.Equal("Thread tags…", CommandCatalog.Label("thread.tags"));
        Assert.Equal("Find", Texts.T("readerFind.open"));
        Assert.Equal("Settings", Texts.T("reader.settings.button"));
        Assert.Equal("A view filter is on", Texts.T("reader.settings.active"));
        Texts.SetLanguage("de");
        Assert.Equal("Großer Reader", CommandCatalog.Label("reader.expand"));
        Assert.Equal("Großer Reader", Texts.T("command.reader.expand.compact"));
        Assert.Equal("Thread-Tags…", CommandCatalog.Label("thread.tags"));
        Assert.Equal("Suchen", Texts.T("readerFind.open"));
        Assert.Equal("Einstellungen", Texts.T("reader.settings.button"));
        Assert.Equal("Ein Ansichtsfilter ist aktiv", Texts.T("reader.settings.active"));
        Assert.Equal("Ansicht (nur diese Sitzung)", Texts.T("quicksettings.reader.section.view"));
        Assert.Equal("Medien", Texts.T("quicksettings.reader.section.media"));
        Assert.Equal("Text und Layout", Texts.T("quicksettings.reader.section.text"));
        Assert.Equal("Thread", Texts.T("quicksettings.reader.section.thread"));
        Assert.Equal("In der Threadliste: Enter", CommandCatalog.ScopeHint("reader.expand"));
        Assert.Equal("reader.view.menu", Texts.T("reader.view.menu")); // the key is gone: the lookup answers with the key itself
    }

    [Fact]
    public void TheNewAndChangedTextsAreRealTranslationsInAllTwelveLanguagesAndTheViewMenuKeyIsGone()
    {
        string[] keys = ["command.reader.expand", "command.thread.tags", "readerFind.open", "reader.settings.button", "reader.settings.active",
            "quicksettings.reader.section.view", "quicksettings.reader.section.media", "quicksettings.reader.section.text", "quicksettings.reader.section.thread",
            "command.scope.reader.expand", "reader.thread.bookmarksUnavailable"];
        Assert.False(BuiltInCatalog.English.ContainsKey("reader.view.menu"));
        foreach (string code in new[] { "es", "fr", "ru", "zh-Hans", "ja", "ko", "pt-BR", "cs", "it", "pl" })
        {
            using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(Texts.BundledPackResourcePrefix + code + ".json")!;
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement strings = document.RootElement.GetProperty("strings");
            Assert.False(strings.TryGetProperty("reader.view.menu", out _), code);
            foreach (string key in keys)
            {
                Assert.True(strings.TryGetProperty(key, out JsonElement value), code + " lacks " + key);
                string text = value.GetString()!;
                Assert.False(string.IsNullOrWhiteSpace(text), code + " " + key);
                // Loan words that are the same in English (Italian "Thread", "Media"; Polish "Media") are the packs' own terms elsewhere too.
                bool loanWord = key is "quicksettings.reader.section.media" && code is "it" or "pl" || key is "quicksettings.reader.section.thread" && code == "it";
                if (!loanWord) Assert.NotEqual(BuiltInCatalog.English[key], text);
            }
        }
        // The glossary (F6): the thread's tags are "Thread tags" in every language, not the bare word that also names a media's tags.
        Assert.All(new[] { "es", "fr", "ru", "zh-Hans", "ja", "ko", "pt-BR", "cs", "it", "pl" }, code =>
        {
            using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(Texts.BundledPackResourcePrefix + code + ".json")!;
            using JsonDocument document = JsonDocument.Parse(stream);
            Assert.NotEqual(document.RootElement.GetProperty("strings").GetProperty("tags.open").GetString(),
                document.RootElement.GetProperty("strings").GetProperty("command.thread.tags").GetString());
        });
    }

    // ---- work views ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheGearIsTheViewsOldRowAndFullScreenEndsTheOrderAndOverflowsOnlyWhenItDoesNotFit()
    {
        Assert.Throws<KeyNotFoundException>(() => WorkViewPolicy.Get("reader.toolbar.view"));
        WorkViewEntry gear = WorkViewPolicy.Get("reader.toolbar.settings");
        Assert.Equal((Presence.Overflow, Presence.Direct, Presence.Direct), (gear.Barebones, gear.Simple, gear.Advanced));
        Assert.Null(gear.CommandId);
        WorkViewEntry full = WorkViewPolicy.Get("reader.toolbar.fullscreen");
        // Review 15.11.: on the bar while it fits (Simple, Advanced), the first to move under More; Barebones keeps it under More.
        Assert.Equal((Presence.Overflow, Presence.Direct, Presence.Direct), (full.Barebones, full.Simple, full.Advanced));
        Assert.Equal("window.fullscreen", full.CommandId);
        Assert.Equal("reader.toolbar.fullscreen", WorkViewPolicy.ReaderToolbarKeepVisibleOrder[^1]);
        foreach (WorkView view in WorkViewSetting.All)
            Assert.Equal(view == WorkView.Barebones ? OverflowMode.Always : OverflowMode.AsNeeded,
                WorkViewPresenter.ToolBarMode(WorkViewPolicy.Resolve("reader.toolbar.fullscreen", view)));
        // The order a narrow window moves things under More, from the end (owner 14.11., the gear in View's place).
        Assert.Equal(["reader.toolbar.gallery", "reader.toolbar.read", "reader.toolbar.media-sidebar", "reader.toolbar.find", "reader.toolbar.tags", "reader.toolbar.export",
            "reader.toolbar.settings", "reader.toolbar.thread", "reader.toolbar.history-back", "reader.toolbar.fullscreen"], WorkViewPolicy.ReaderToolbarKeepVisibleOrder);
        // Every bar item of the XAML has its row (the id registry is checked against the page by the work-view tests).
        string[] ids = ToolbarItems().Select(item => item.Attributes().Single(attribute => attribute.Name.LocalName == "WorkViewElement.Id").Value).ToArray();
        Assert.Contains("reader.toolbar.settings", ids);
        Assert.DoesNotContain("reader.toolbar.view", ids);
    }

    // ---- the popover ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void ThePopoverHasTheSixSectionsInTheirOrderAndNothingOfTheOldViewMenuIsLost()
    {
        IReadOnlyList<QuickSettingsEntry> layout = ReaderQuickSettings.Layout;
        string[] sections = layout.OfType<QuickSettingsSection>().Select(section => section.TitleKey).ToArray();
        Assert.Equal(["quicksettings.reader.section.view", "quicksettings.reader.section.media", "quicksettings.reader.section.postVideo",
            "quicksettings.reader.section.sidebarVideo", "quicksettings.reader.section.text", "quicksettings.reader.section.thread"], sections);
        Assert.IsType<QuickSettingsSection>(layout[0]);
        // Section "view (this session only)": the three switches and the post filters action.
        Assert.Equal(["reader.view.media-only", "reader.view.hide-media", "reader.view.conversation", "reader.post-filters"],
            layout.Skip(1).TakeWhile(entry => entry is QuickSettingsSessionEntry).Cast<QuickSettingsSessionEntry>().Select(entry => entry.CommandId));
        Assert.True(layout.OfType<QuickSettingsSessionEntry>().Single(entry => entry.CommandId == "reader.post-filters").IsAction);
        // Section "thread" holds "Show new posts automatically".
        Assert.Equal("watch.follow-live", ((QuickSettingsSessionEntry)layout[^1]).CommandId);

        // Every option the View menu had is reachable here: as a session row, or as the setting (with its command's key) that the option toggles.
        string[] viewOptions = ["reader.view.hide-media", "reader.view.media-only", "reader.view.conversation", "reader.view.textless-large",
            "reader.view.focused-enlarge", "reader.view.hover-enlarge", "reader.sidebar.toggle", "watch.follow-live", "reader.post-filters", "reader.quick-settings"];
        string[] sessionRows = layout.OfType<QuickSettingsSessionEntry>().Select(entry => entry.CommandId).ToArray();
        string[] shortcutRows = layout.OfType<QuickSettingsFieldEntry>().Select(entry => entry.ShortcutCommandId).OfType<string>().ToArray();
        foreach (string option in viewOptions.Where(option => option != "reader.quick-settings"))
            Assert.True(sessionRows.Contains(option) || shortcutRows.Contains(option), option);
        // The settings those three switches were are the ones of the media section; the sidebar row shows Ctrl+Shift+M.
        Assert.Equal("reader.sidebar.toggle", layout.OfType<QuickSettingsFieldEntry>().Single(entry => entry.Field.Id == SettingsFieldId.ReaderMediaSidebar).ShortcutCommandId);
        Assert.Equal("Primary+Shift+M", CommandCatalog.Get("reader.sidebar.toggle").Shortcut);
        Assert.Equal("Primary+Shift+L", CommandCatalog.Get("watch.follow-live").Shortcut);
    }

    [Fact]
    public void ConditionalRowsFollowTheOnOffSettingTheyDependOnAndTheClickOnAMediumComesFirstInTheMediaSection()
    {
        IReadOnlyList<QuickSettingsEntry> layout = ReaderQuickSettings.Layout;
        QuickSettingsFieldEntry[] conditional = layout.OfType<QuickSettingsFieldEntry>().Where(entry => entry.WhenFieldId != null).ToArray();
        Assert.Equal([(SettingsFieldId.ReaderMediaSidebarScope, SettingsFieldId.ReaderMediaSidebar),
            (SettingsFieldId.ReaderMediaSidebarSide, SettingsFieldId.ReaderMediaSidebar),
            (SettingsFieldId.ReaderTextlessMediaWidth, SettingsFieldId.ReaderTextlessLargeImages),
            (SettingsFieldId.ReaderFocusedMediaScalePercent, SettingsFieldId.ReaderFocusedMediaEnlarge)],
            conditional.Select(entry => (entry.Field.Id, entry.WhenFieldId!)));
        Assert.All(conditional, entry => Assert.Equal(QuickSettingKind.Toggle, ReaderQuickSettings.Fields.Single(field => field.Id == entry.WhenFieldId).Kind));
        // The first row of the media section is "Clicking a medium" (bound to the temporary field until the inline video package is merged).
        QuickSettingsEntry first = layout.SkipWhile(entry => entry is not QuickSettingsSection { TitleKey: "quicksettings.reader.section.media" }).Skip(1).First();
        Assert.Equal(ReaderQuickSettings.MediaClickFieldId, ((QuickSettingsFieldEntry)first).Field.Id);
        // A condition on a setting that comes later, or on one that is no switch, is refused when the layout is made.
        QuickSettingField toggle = ReaderQuickSettings.Fields.First(field => field.Kind == QuickSettingKind.Toggle);
        QuickSettingField number = ReaderQuickSettings.Fields.First(field => field.Kind == QuickSettingKind.Integer);
        Assert.Throws<ArgumentException>(() => QuickSettingsSpec.WithLayout("x", "t", "a", [new QuickSettingsFieldEntry(number), new QuickSettingsFieldEntry(toggle, number.Id)]));
        Assert.Throws<ArgumentException>(() => QuickSettingsSpec.WithLayout("x", "t", "a", [new QuickSettingsFieldEntry(toggle, toggle.Id)]));
        Assert.Throws<ArgumentException>(() => QuickSettingsSpec.WithLayout("x", "t", "a", [new QuickSettingsFieldEntry(toggle), new QuickSettingsFieldEntry(toggle)]));
        QuickSettingsSpec spec = QuickSettingsSpec.WithLayout("x", "t", "a", [new QuickSettingsSection("s"), new QuickSettingsFieldEntry(toggle), new QuickSettingsFieldEntry(number, toggle.Id)]);
        Assert.Equal([toggle.Id, number.Id], spec.Fields.Select(field => field.Id));
    }

    [Fact]
    public void EveryRowKeyOfThePopoverExistsInBothLanguagesAndEveryCommandOfARowIsARealCommand()
    {
        foreach (QuickSettingsSection section in ReaderQuickSettings.Layout.OfType<QuickSettingsSection>())
            Assert.NotEqual(section.TitleKey, Texts.T(section.TitleKey));
        foreach (QuickSettingsSessionEntry entry in ReaderQuickSettings.Layout.OfType<QuickSettingsSessionEntry>())
        {
            Assert.Contains("reader-settings", CommandCatalog.Get(entry.CommandId).Contexts);
            Assert.False(string.IsNullOrWhiteSpace(CommandCatalog.Label(entry.CommandId)));
        }
        foreach (string id in ReaderQuickSettings.Layout.OfType<QuickSettingsFieldEntry>().Select(entry => entry.ShortcutCommandId).OfType<string>())
            Assert.Contains("reader-settings", CommandCatalog.Get(id).Contexts);
        Texts.SetLanguage("de");
        foreach (QuickSettingsSection section in ReaderQuickSettings.Layout.OfType<QuickSettingsSection>())
            Assert.NotEqual(section.TitleKey, Texts.T(section.TitleKey));
    }

    [Fact]
    public void TheViewShortcutsStillWorkWithoutAMenuBecauseTheKeyHandlerStaysOnThePane()
    {
        // Ctrl+Shift+M (sidebar) goes through the option's key handler on the reader pane; Ctrl+Shift+L is routed by the shell.
        string view = File.ReadAllText(Root("Desktop/Views/Archive/ArchivePage.ReaderView.cs"));
        Assert.Contains("ArchiveReaderPane.AddHandler(KeyDownEvent, OnReaderViewKeyDown, RoutingStrategies.Tunnel);", view);
        Assert.Contains("CommandRouter.IsRoutingContextActive(context, e.Source)", view);
        Assert.Contains("CommandBindings.Matches(e, option.CommandId)", view);
        Assert.Contains("internal bool ToggleReaderViewOption(string commandId)", view);
        Assert.Contains("RegisterViewOption(\"reader.view.media-only\"", view);
        Assert.Contains("live.ToggleFollowLiveByShortcut", File.ReadAllText(Root("Desktop/Shell/MainWindow.Shortcuts.cs")));
        Assert.Equal("Primary+Shift+M", CommandCatalog.Get("reader.sidebar.toggle").Shortcut);
        // The popover is the gear's: it opens under it and falls back to More.
        string settings = File.ReadAllText(Root("Desktop/Views/Archive/ArchivePage.QuickSettings.cs"));
        Assert.Contains("ReaderSettingsOpen.IsEffectivelyVisible ? ReaderSettingsOpen", settings);
        Assert.Contains("Click=\"OnReaderSettingsClick\"", File.ReadAllText(Root("Desktop/Views/Archive/ArchivePage.axaml")));
    }

    [Fact]
    public void AViewFilterOfTheSessionMarksTheGearAndTheDefaultViewDoesNot()
    {
        ReaderViewState state = new();
        Assert.True(state.IsDefault);
        state.MediaOnly = true;
        Assert.False(state.IsDefault);
        state.MediaOnly = false;
        state.HideMedia = true;
        Assert.False(state.IsDefault);
        state.HideMedia = false;
        state.Conversation = true;
        Assert.False(state.IsDefault);
        state.Conversation = false;
        Assert.True(state.IsDefault);
    }
}

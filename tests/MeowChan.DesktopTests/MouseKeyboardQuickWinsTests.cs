using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using MeowChan;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Views.Shortcuts;

namespace MeowChan.DesktopTests;

// Mouse and keyboard quick wins (audit V4, V5, V6, V7, V13, V15, V16). The decisions are plain objects (steps, type-ahead, the middle-click press, the strip's
// notch, the footer move, the table menu's composition); the wiring on the pages is a GUI control, so its source is the contract, as in the other page tests.
[Collection("Localization")]
public sealed class MouseKeyboardQuickWinsTests : IDisposable
{
    public MouseKeyboardQuickWinsTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static string Read(string relative, [CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative)));

    // ---- V4: the reader's font size -------------------------------------------------------------------------------------------

    [Fact]
    public void TheFontSizeStepsWithinTheSettingsOwnThreeValuesAndStaysAtTheEnds()
    {
        Assert.Equal(new[] { "Small", "Normal", "Large" }, ReaderFontSizeSteps.Order);
        Assert.Equal("Large", ReaderFontSizeSteps.Step("Normal", 1));
        Assert.Equal("Small", ReaderFontSizeSteps.Step("Normal", -1));
        Assert.Equal("Large", ReaderFontSizeSteps.Step("Large", 1));
        Assert.Equal("Small", ReaderFontSizeSteps.Step("Small", -3));
        // Several notches at once still move one step; no step stays.
        Assert.Equal("Large", ReaderFontSizeSteps.Step("Normal", 5));
        Assert.Equal("Normal", ReaderFontSizeSteps.Step("Normal", 0));
        // A value that is none of the three counts as the default, like the configuration reads it.
        Assert.Equal("Large", ReaderFontSizeSteps.Step("huge", 1));
        Assert.Equal("Large", ReaderFontSizeSteps.Step(null, 1));
        Assert.Equal("Normal", ReaderFontSizeSteps.Default);
        Assert.Equal(new Config().ReadingFontSize, ReaderFontSizeSteps.Default);
    }

    [Fact]
    public void TheFontSizeFieldIsTheSettingsOwnFieldAndOnlyAcceptsItsValues()
    {
        Assert.Equal(SettingsFieldId.ReadingFontSize, ReaderFontSizeSteps.Field.Id);
        Assert.True(ReaderFontSizeSteps.Field.TryCreateChange("Large", out Action<Config>? change));
        Config config = new();
        change(config);
        Assert.Equal("Large", config.ReadingFontSize);
        Assert.False(ReaderFontSizeSteps.Field.TryCreateChange("Huge", out _));
    }

    [Fact]
    public void CtrlWheelAndCtrlPlusMinusZeroAreWiredInTheirOwnPartialAndNeverScroll()
    {
        string zoom = Read("Desktop/Views/Archive/ArchivePage.ReaderFontZoom.cs");
        Assert.Contains("ArchiveReaderPane.AddHandler(PointerWheelChangedEvent, OnReaderFontWheel, RoutingStrategies.Tunnel);", zoom);
        Assert.Contains("ItemActivation.IsZoomWheel(e.KeyModifiers)", zoom);
        // The wheel is handled before any step is known: Ctrl+wheel never reaches the reader's scroll viewer.
        Assert.True(zoom.IndexOf("e.Handled = true;", StringComparison.Ordinal) < zoom.IndexOf("_readerFontWheel.Add", StringComparison.Ordinal));
        Assert.Contains("CommandBindings.Matches(e, ReaderFontLargerCommandId)", zoom);
        Assert.Contains("CommandRouter.IsNativeInput(e.Source, e.Key)", zoom);
        Assert.Contains("ReaderFontSizeSteps.Field", zoom);
        Assert.Contains("AttachReaderFontZoom();", Read("Desktop/Views/Archive/ArchivePage.axaml.cs"));
        // The reader's scroll code stays untouched by this package.
        Assert.DoesNotContain("ReaderFont", Read("Desktop/Views/Archive/ArchivePage.Reader.cs"));
    }

    // ---- V13 and the reader keys: the revision-20 defaults ----------------------------------------------------------------------

    [Theory]
    [InlineData(ShortcutPlatform.Windows)]
    [InlineData(ShortcutPlatform.Linux)]
    [InlineData(ShortcutPlatform.MacOS)]
    public void TheRevision20DefaultsAreConflictFreeAndYieldToSavedBindings(ShortcutPlatform platform)
    {
        Assert.True(EffectiveShortcutBindings.DefaultsRevision >= 20);
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(null, platform);
        Assert.Empty(bindings.Issues);
        string Keys(string id) => string.Join(" ", bindings.For(id).Select(g => string.Join("+", g.Modifiers.Append(g.Key))));
        Assert.Equal("Primary+OemPlus Primary+Add", Keys("reader.font-size.larger"));
        Assert.Equal("Primary+OemMinus Primary+Subtract", Keys("reader.font-size.smaller"));
        Assert.Equal("Primary+D0 Primary+NumPad0", Keys("reader.font-size.reset"));
        Assert.Equal(platform == ShortcutPlatform.MacOS ? "Primary+R F5" : "F5", Keys("plugins.refresh"));
        foreach (string id in new[] { "reader.font-size.larger", "reader.font-size.smaller", "reader.font-size.reset", "plugins.refresh" })
        {
            Assert.Contains(id, EffectiveShortcutBindings.YieldingDefaultCommandIds);
            Assert.True(EffectiveShortcutBindings.YieldsOn(id, platform), id);
        }
        // Each acts only where it is: the reader's text, the Plugins page.
        Assert.Equal(new[] { "reader" }, EffectiveShortcutBindings.ExecutionContexts("reader.font-size.larger"));
        Assert.Equal(new[] { "plugins" }, EffectiveShortcutBindings.ExecutionContexts("plugins.refresh"));
    }

    [Theory]
    [InlineData(ShortcutPlatform.Windows)]
    [InlineData(ShortcutPlatform.MacOS)]
    public void ASavedBindingOfAnotherCommandOnTheNewKeyWinsAndTheNewDefaultStaysUnbound(ShortcutPlatform platform)
    {
        // "plugins.save" lives in the same context as plugins.refresh; "reader.font-size.larger" in the reader text's.
        EffectiveShortcutBindings plugins = EffectiveShortcutBindings.Build(new ShortcutBindingsSection().Set("plugins.folder", [ShortcutGesture.Parse("F5")]), platform);
        Assert.Empty(plugins.For("plugins.refresh").Where(g => g.Key == "F5"));
        Assert.Equal("F5", Assert.Single(plugins.For("plugins.folder")).Key);
        Assert.DoesNotContain(plugins.Issues, issue => issue.BlocksSave);
        Assert.Contains(plugins.Issues, issue => issue.CommandId == "plugins.refresh" && issue.OtherCommandId == "plugins.folder" && !issue.BlocksSave);

        EffectiveShortcutBindings reader = EffectiveShortcutBindings.Build(new ShortcutBindingsSection().Set("reader.font-size.larger", [ShortcutGesture.Parse("Primary+0")]), platform);
        Assert.Empty(reader.For("reader.font-size.reset").Where(g => g.Key == "D0"));
        Assert.Single(reader.For("reader.font-size.larger"));
        Assert.DoesNotContain(reader.Issues, issue => issue.BlocksSave);
        // A binding saved for the command itself is kept.
        EffectiveShortcutBindings own = EffectiveShortcutBindings.Build(new ShortcutBindingsSection().Set("plugins.refresh", [ShortcutGesture.Parse("F9")]), platform);
        Assert.Equal(new[] { "F9" }, own.For("plugins.refresh").Select(g => g.Key));
    }

    [Fact]
    public void ThePluginsRefreshKeyRunsThroughTheButtonTheCommandIsBoundTo()
    {
        // The window routes a visible command button's key (RouteVisibleActions); the page binds plugins.refresh to its refresh button.
        Assert.Equal("F5", CommandCatalog.Get("plugins.refresh").Shortcut);
        Assert.Contains("(\"PluginsRefresh\",\"plugins.refresh\")", Read("Desktop/Views/Plugins/PluginsPage.axaml.cs"));
    }

    // ---- V5: the middle click ----------------------------------------------------------------------------------------------------

    [Fact]
    public void AMiddleClickIsAPressAndAReleaseOnTheSameItemAndNothingElse()
    {
        object first = new(), second = new();
        MiddleClickTracker tracker = new();
        Assert.False(tracker.Released(first), "a release without a press");
        tracker.Pressed(first);
        Assert.True(tracker.Released(first));
        Assert.False(tracker.Released(first), "the press is used up");
        tracker.Pressed(first);
        Assert.False(tracker.Released(second), "a drag onto another item");
        tracker.Pressed(null);
        Assert.False(tracker.Released(null), "no item under the pointer");
        tracker.Pressed(first);
        tracker.Cancel();
        Assert.False(tracker.Released(first));
    }

    [Fact]
    public void AMiddleClickOnAnInteractivePartOrEmptySpaceHasNoItem()
    {
        Button star = new();
        TextBlock label = new();
        ListBoxItem item = new() { DataContext = "row" };
        StackPanel surface = new() { Children = { item, star, label } };
        Assert.Equal("row", MiddleClickTracker.RowAt<string>(item, surface));
        Assert.Null(MiddleClickTracker.RowAt<string>(star, surface));
        Assert.Null(MiddleClickTracker.RowAt<string>(surface, surface));
        Assert.Null(MiddleClickTracker.RowAt<string>(null, surface));
    }

    [Fact]
    public void TheMiddleClickRunsTheMenusCommandOnEachSurface()
    {
        // Downloads (threads and boards): the menu's "Open source in browser" target, under the key commands' rules.
        string downloads = Read("Desktop/Shell/MainWindow.MouseKeyboard.cs");
        Assert.Contains("MiddleClickTracker.IsRelease(e)", downloads);
        Assert.Contains("OpenTarget(target);", downloads);
        Assert.Contains("DownloadListAcceptsQuickInput()", downloads);
        Assert.Contains("InitializeDownloadMiddleClick();", Read("Desktop/Shell/MainWindow.Selection.cs"));
        // Archive rows: the thread menu's rule (a shell action and an address), else the same message as the key; tiles: the media menu's own entry.
        string archive = Read("Desktop/Views/Archive/ArchivePage.MouseKeyboard.cs");
        Assert.Contains("list.AddHandler(PointerReleasedEvent, OnThreadMiddleReleased", archive);
        Assert.Contains("SelectionLeadAction == null", archive);
        Assert.Contains("RunArchiveLeadAction(row, \"browser\");", archive);
        Assert.Contains("archive.command.noSource", archive);
        Assert.Contains("CommandBindings.ActionId(item) == MediaFolderCommandId", archive);
        Assert.Equal("media.show-in-folder", ArchivePage.MediaFolderCommandId);
        // Media page tiles: the same entry as the menu and its shortcut, for the clicked tile, without touching the selection.
        string media = Read("Desktop/Views/Media/MediaPage.Interaction.cs");
        Assert.Contains("RunMediaCommandOnTileAsync(FolderCommandId, row)", media);
        Assert.Contains("row.Entry.Presence != MediaLibraryPresence.Present", media);
        Assert.Equal("media.show-in-folder", MediaPage.FolderCommandId);
        Assert.Contains("AttachMouseKeyboardQuickWins();", Read("Desktop/Views/Archive/ArchivePage.axaml.cs"));
    }

    // ---- V6: the strip's wheel ---------------------------------------------------------------------------------------------------

    [Fact]
    public void TheStripWheelLooksBackOnWheelUpAndForwardOnWheelDownByTwoTilesAndFallsBackToAPadsSideSwipe()
    {
        Assert.Equal(1, StripWheel.Notches(new Vector(0, 1)));
        Assert.Equal(-2, StripWheel.Notches(new Vector(5, -2)));
        Assert.Equal(0.5, StripWheel.Notches(new Vector(0.5, 0)));
        Assert.Equal(0, StripWheel.Notches(default));
        // Wheel down (negative) scrolls forward by two tiles of 68; wheel up goes back.
        Assert.Equal(100 + 2 * 68, StripWheel.NextOffset(100, new Vector(0, -1), 68));
        Assert.Equal(100 - 2 * 68, StripWheel.NextOffset(100, new Vector(0, 1), 68));
        Assert.Equal(StripWheel.NotchTiles, 2);
    }

    [Fact]
    public void TheSidebarStripAndTheFilmstripShareTheNotch()
    {
        Assert.Contains("StripWheel.Attach(_strip, ReaderMediaMultiRules.ThumbnailWidth + ReaderMediaMultiRules.ThumbnailSpacing);", Read("Desktop/Views/Archive/ReaderMediaSidebar.cs"));
        string filmstrip = Read("Desktop/Controls/GalleryFilmstrip.cs");
        Assert.Contains("StripWheel.NextOffset(_offset, e.Delta, GalleryFilmstripPlan.Pitch(_tile, _gap))", filmstrip);
        Assert.DoesNotContain("ScrollNotchTiles", filmstrip);
        // A strip with nothing to scroll leaves the wheel to the posts behind it; Ctrl+wheel is not a scroll.
        string wheel = Read("Desktop/Controls/StripWheel.cs");
        Assert.Contains("e.KeyModifiers != KeyModifiers.None", wheel);
        Assert.Contains("viewer.Extent.Width <= viewer.Viewport.Width + 0.5", wheel);
    }

    // ---- V7: type-ahead -----------------------------------------------------------------------------------------------------------

    private static readonly string?[] Names = { "Alpha", "Beta", "alpine", "Bravo", null, "Charlie", "  Delta" };
    private static readonly DateTime Start = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TheFirstLetterJumpsToTheNextRowWithItAndWrapsAround()
    {
        TypeAheadSearch search = new();
        Assert.Equal(0, search.Next(Names, -1, "a", Start));
        search.Reset();
        Assert.Equal(2, search.Next(Names, 0, "a", Start));
        search.Reset();
        Assert.Equal(0, search.Next(Names, 2, "a", Start));
        search.Reset();
        Assert.Equal(5, search.Next(Names, 0, "C", Start));
        search.Reset();
        Assert.Equal(-1, search.Next(Names, 0, "x", Start));
        Assert.Equal(-1, search.Next(Array.Empty<string?>(), -1, "a", Start));
    }

    [Fact]
    public void MoreLettersNarrowTheMatchDownAndTheCurrentRowStaysWhenItStillMatches()
    {
        TypeAheadSearch search = new();
        Assert.Equal(1, search.Next(Names, -1, "b", Start));
        Assert.Equal(3, search.Next(Names, 1, "r", Start.AddMilliseconds(200)));
        Assert.Equal("br", search.Typed);
        search.Reset();
        Assert.Equal(0, search.Next(Names, -1, "a", Start));
        Assert.Equal(0, search.Next(Names, 0, "l", Start.AddMilliseconds(100)));
        Assert.Equal(0, search.Next(Names, 0, "p", Start.AddMilliseconds(200)));
    }

    [Fact]
    public void TheSameLetterAgainStepsThroughTheRowsWithThatLetter()
    {
        TypeAheadSearch search = new();
        Assert.Equal(0, search.Next(Names, -1, "a", Start));
        Assert.Equal(2, search.Next(Names, 0, "a", Start.AddMilliseconds(100)));
        Assert.Equal(0, search.Next(Names, 2, "a", Start.AddMilliseconds(200)));
        Assert.True(TypeAheadSearch.IsOneLetterRepeated("aaa"));
        Assert.True(TypeAheadSearch.IsOneLetterRepeated("aA"));
        Assert.False(TypeAheadSearch.IsOneLetterRepeated("a"));
        Assert.False(TypeAheadSearch.IsOneLetterRepeated("ab"));
    }

    [Fact]
    public void APauseStartsANewWordAndLeadingBlanksOfALabelDoNotCount()
    {
        TypeAheadSearch search = new();
        Assert.Equal(1, search.Next(Names, -1, "b", Start));
        // After the documented pause "r" is a new word and finds no row starting with "r".
        Assert.Equal(TimeSpan.FromSeconds(1), TypeAheadSearch.ResetAfter);
        Assert.Equal(-1, search.Next(Names, 1, "r", Start + TypeAheadSearch.ResetAfter + TimeSpan.FromMilliseconds(1)));
        Assert.Equal("r", search.Typed);
        search.Reset();
        Assert.Equal(6, search.Next(Names, 0, "d", Start));
    }

    [Fact]
    public void OnlyPrintableTextCountsAndASpaceCannotStartAWord()
    {
        TypeAheadSearch search = new();
        Assert.True(search.Accepts("a"));
        Assert.True(search.Accepts("ä"));
        Assert.False(search.Accepts(null));
        Assert.False(search.Accepts(""));
        Assert.False(search.Accepts("\t"));
        Assert.False(search.Accepts(" "), "Space alone is a command key, not a word");
        search.Next(Names, -1, "a", Start);
        Assert.True(search.Accepts(" "), "inside a word a space is a letter");
    }

    [Fact]
    public void TypeAheadIsWiredOnTheThreeSurfacesWithTheirOwnGuards()
    {
        Assert.Contains("TypeAheadSearch.AttachDataGrid(grid, DownloadRowLabel,", Read("Desktop/Shell/MainWindow.MouseKeyboard.cs"));
        Assert.Contains("InitializeDownloadTypeAhead();", Read("Desktop/Shell/MainWindow.Selection.cs"));
        Assert.Contains("TypeAheadSearch.AttachDataGrid(ArchiveThreadTable,", Read("Desktop/Views/Archive/ArchivePage.MouseKeyboard.cs"));
        string plugins = Read("Desktop/Views/Plugins/PluginsPage.TypeAhead.cs");
        Assert.Contains("TypeAheadSearch.Attach(list,", plugins);
        Assert.Contains("!_busy && !IsDiscardConfirmationOpen", plugins);
        Assert.Contains("AttachTypeAhead();", Read("Desktop/Views/Plugins/PluginsPage.axaml.cs"));
        Assert.Null(MainWindow.DownloadRowLabel(new FakeRowMarker()));
    }

    private sealed class FakeRowMarker { }

    // ---- V15: Alt+Up / Alt+Down in the footer list --------------------------------------------------------------------------------

    [Fact]
    public void OnlyAltWithUpOrDownMovesAFooterRow()
    {
        Assert.Equal(-1, FooterLayoutEditor.MoveDirection(Key.Up, KeyModifiers.Alt));
        Assert.Equal(1, FooterLayoutEditor.MoveDirection(Key.Down, KeyModifiers.Alt));
        Assert.Equal(0, FooterLayoutEditor.MoveDirection(Key.Up, KeyModifiers.None));
        Assert.Equal(0, FooterLayoutEditor.MoveDirection(Key.Down, KeyModifiers.Alt | KeyModifiers.Shift));
        Assert.Equal(0, FooterLayoutEditor.MoveDirection(Key.Left, KeyModifiers.Alt));
    }

    [Fact]
    public void AltUpAndDownMoveTheRowOfTheFocusedControlLikeItsButtons()
    {
        OwnedUiDispatcher.Run(() =>
        {
            var editor = new FooterLayoutEditor();
            string page = "navigation.media";
            int before = editor.Layout.IndexOf(page);
            Assert.True(before > 0);
            // From the switch of the row: it moves up one place, the focus stays on its switch.
            Assert.True(editor.TryMoveRowOf(editor.SwitchOf(page), -1));
            Assert.Equal(before - 1, editor.Layout.IndexOf(page));
            Assert.Same(editor.SwitchOf(page), editor.FocusTarget);
            // The same move as the button's: the layout equals the one the Click would have made.
            var viaButton = new FooterLayoutEditor();
            viaButton.MoveButtonOf(page, up: true)!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(viaButton.Layout.Order, editor.Layout.Order);
            // From the down button: one place down again, the focus on the down button.
            Assert.True(editor.TryMoveRowOf(editor.MoveButtonOf(page, up: false), 1));
            Assert.Equal(before, editor.Layout.IndexOf(page));
            Assert.Same(editor.MoveButtonOf(page, up: false), editor.FocusTarget);
            // At the first place nothing moves (and the key is still the row's); a control outside the rows is not handled.
            string first = editor.Layout.Order[0];
            string[] order = editor.Layout.Order.ToArray();
            Assert.True(editor.TryMoveRowOf(editor.SwitchOf(first), -1));
            Assert.Equal(order, editor.Layout.Order);
            Assert.False(editor.TryMoveRowOf(editor.ResetButton, -1));
            Assert.False(editor.TryMoveRowOf(null, 1));
            Assert.False(editor.TryMoveRowOf(editor.SwitchOf(page), 0));
        });
    }

    [Fact]
    public void TheMoveButtonsTipNamesTheKeysAndTheKeyIsWiredOnTheRows()
    {
        OwnedUiDispatcher.Run(() =>
        {
            var editor = new FooterLayoutEditor();
            string up = ToolTip.GetTip(editor.MoveButtonOf("navigation.media", up: true)!) as string ?? "";
            string down = ToolTip.GetTip(editor.MoveButtonOf("navigation.media", up: false)!) as string ?? "";
            Assert.EndsWith(ShortcutGesture.Parse("Alt+Up").Display(EffectiveShortcutBindings.CurrentPlatform), up);
            Assert.EndsWith(ShortcutGesture.Parse("Alt+Down").Display(EffectiveShortcutBindings.CurrentPlatform), down);
            // The accessible name stays the plain label.
            Assert.Equal(Texts.T("settings.footer.moveUp", CommandCatalog.Label("navigation.media")),
                Avalonia.Automation.AutomationProperties.GetName(editor.MoveButtonOf("navigation.media", up: true)!));
        });
        Assert.Contains("_rows.AddHandler(KeyDownEvent, OnRowsKeyDown", Read("Desktop/Views/Settings/FooterLayoutEditor.cs"));
    }

    // ---- V16: the shortcuts table's context menu ------------------------------------------------------------------------------------

    [Fact]
    public void TheShortcutTableMenuOffersTheEditBarsActionsWithTheEditBarsLabels()
    {
        var editor = new ShortcutBindingsEditor();
        editor.ApplyContext(ShortcutHelpContext.Settings);
        ListBoxItem row = editor.ActionList.Items.OfType<ListBoxItem>().First(item => item.Tag as string == "gallery.fit");
        ContextMenuModel model = editor.ComposeRowMenu(row)!.ToModel();
        Assert.Same(row, editor.ActionList.SelectedItem);
        Assert.Equal("gallery.fit", editor.SelectedCommandId);
        Assert.Equal(new[] { Texts.T("shortcuts.window.recordReplace"), Texts.T("settings.shortcutsEditor.recordAdditional"),
            Texts.T("settings.shortcutsEditor.restoreDefault"), Texts.T("settings.shortcutsEditor.clearBinding") }, model.Entries.Select(entry => entry.Label));
        Assert.Equal(2, model.Sections.Count);
        Assert.All(model.Entries, entry => Assert.True(entry.Available, entry.Id));
    }

    [Fact]
    public void TheMenuRunsTheSessionsActionsAndClearAndAddFollowTheBindings()
    {
        var editor = new ShortcutBindingsEditor();
        editor.ApplyContext(ShortcutHelpContext.Settings);
        ListBoxItem row = editor.ActionList.Items.OfType<ListBoxItem>().First(item => item.Tag as string == "gallery.fit");
        ContextMenuModel model = editor.ComposeRowMenu(row)!.ToModel();
        ContextMenuEntry Entry(string id) => model.Entries.Single(entry => entry.Id == id);
        // Clear binding is the edit bar's: the command ends up unbound; Restore default brings the default back.
        Assert.NotEmpty(editor.Session.Requested("gallery.fit"));
        Entry("shortcuts.clear").Invoke!();
        Assert.Empty(editor.Session.Requested("gallery.fit"));
        // The list is rebuilt by a change of the bindings: the menu is composed on the live row, as a click would.
        ListBoxItem liveRow = editor.ActionList.Items.OfType<ListBoxItem>().First(item => item.Tag as string == "gallery.fit");
        ContextMenuModel afterClear = editor.ComposeRowMenu(liveRow)!.ToModel();
        Assert.False(afterClear.Entries.Single(entry => entry.Id == "shortcuts.clear").Available, "nothing left to clear");
        Assert.True(afterClear.Entries.Single(entry => entry.Id == "shortcuts.record-additional").Available);
        Entry("shortcuts.restore-default").Invoke!();
        Assert.NotEmpty(editor.Session.Requested("gallery.fit"));
        // Record starts a recording exactly like the edit bar's button (and a menu in the middle of one opens nowhere).
        try
        {
            Entry("shortcuts.record").Invoke!();
            Assert.True(editor.IsRecording);
            Assert.Null(editor.ComposeRowMenu(row));
        }
        finally { editor.Detach(); }
    }

    [Fact]
    public void ASectionRowAndNoRowHaveNoMenuAndTheMenuHostIsOnTheTable()
    {
        var editor = new ShortcutBindingsEditor();
        editor.ApplyContext(ShortcutHelpContext.Settings);
        ListBoxItem header = editor.ActionList.Items.OfType<ListBoxItem>().First(item => item.Tag is not string);
        Assert.Null(editor.ComposeRowMenu(header));
        Assert.Null(editor.ComposeRowMenu(null));
        Assert.NotNull(editor.RowMenuHost);
        Assert.Contains("InitializeRowMenu();", Read("Desktop/Views/Shortcuts/ShortcutBindingsEditor.cs"));
    }

    // ---- the language packs --------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheNewCommandsHaveALabelInEveryLanguagePack()
    {
        foreach (string pack in Directory.GetFiles(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "../../Desktop/Localization/Packs")), "*.json"))
        {
            string text = File.ReadAllText(pack);
            foreach (string key in new[] { "command.reader.font-size.larger", "command.reader.font-size.smaller", "command.reader.font-size.reset" })
                Assert.Contains("\"" + key + "\"", text);
        }
        foreach (string id in new[] { "reader.font-size.larger", "reader.font-size.smaller", "reader.font-size.reset" })
        {
            Assert.False(string.IsNullOrWhiteSpace(CommandCatalog.Label(id)));
            Texts.SetLanguage("de");
            Assert.False(string.IsNullOrWhiteSpace(CommandCatalog.Label(id)));
            Texts.SetLanguage("en");
        }
    }

    private static string ThisFile([CallerFilePath] string path = "") => path;
}

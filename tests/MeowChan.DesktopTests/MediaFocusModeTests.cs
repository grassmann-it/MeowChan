using Avalonia.Input;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// M4: the focus mode of the media page. The page needs Avalonia's platform (icons), which this test host does not start, so the
// behaviour of the real controls (hidden rows, the focus row, the exit button, Escape on the grid, the announcement) is driven in the
// media-page smoke (FocusModeAsync). Here: the pure rules (Escape levels, the sentences), the command and its shortcut, the texts of
// every bundled language, and the source contracts that pin what the mode may hide (the grid and the selection bar stay) and that it is
// never saved.
[Collection("Localization")]
public sealed class MediaFocusModeTests : IDisposable
{
    public MediaFocusModeTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static string Source(string relative, [System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative)));

    private const string Focus = "Desktop/Views/Media/MediaPage.Focus.cs";

    // ---- Escape levels ----

    [Theory]
    [InlineData(true, false, 0, "ExitFocus")]
    [InlineData(true, true, 3, "ExitFocus")]   // the mode ends before the question and the selection
    [InlineData(true, false, 5, "ExitFocus")]
    [InlineData(false, true, 0, "DismissExportAll")]
    [InlineData(false, true, 4, "DismissExportAll")] // the question before the selection, as before M4
    [InlineData(false, false, 2, "ClearSelection")]
    [InlineData(false, false, 0, "None")]
    public void EscapeEndsTheFocusModeFirstThenTheQuestionThenTheSelection(bool focus, bool ask, int selected, string expected) =>
        Assert.Equal(Enum.Parse<MediaEscapeStep>(expected), MediaFocusMode.Escape(focus, panelFocused: false, ask, selected));

    [Fact]
    public void EscapeWalksTheLevelsOneAtATimeUntilNothingIsLeft()
    {
        bool focus = true, ask = true; int selected = 3;
        List<MediaEscapeStep> steps = new();
        for (MediaEscapeStep step = MediaFocusMode.Escape(focus, false, ask, selected); step != MediaEscapeStep.None; step = MediaFocusMode.Escape(focus, false, ask, selected))
        {
            steps.Add(step);
            if (step == MediaEscapeStep.ExitFocus) focus = false;
            else if (step == MediaEscapeStep.DismissExportAll) ask = false;
            else selected = 0;
        }
        Assert.Equal([MediaEscapeStep.ExitFocus, MediaEscapeStep.DismissExportAll, MediaEscapeStep.ClearSelection], steps);
    }

    [Fact]
    public void ThePageAsksTheRuleForItsEscapeKeyAndAlsoEndsTheModeFromAnywhereOnThePage()
    {
        string selection = Source("Desktop/Views/Media/MediaPage.Selection.cs"), focus = Source(Focus);
        Assert.Contains("MediaFocusMode.Escape(_focusMode, panelFocused: false, _exportAllAsk.IsVisible, _grid.Selection.Count)", selection);
        Assert.Contains("case MediaEscapeStep.ExitFocus: SetFocusMode(false); break;", selection);
        // The page-level handler: Escape on the exit button (or any other control of the page) that nothing handled ends the mode.
        Assert.Contains("AddHandler(KeyDownEvent, (_, e) => { if (!e.Handled) OnFocusEscape(e); }, RoutingStrategies.Bubble);", focus);
        Assert.Contains("e.KeyModifiers != KeyModifiers.None", focus);
    }

    // ---- sentences ----

    [Fact]
    public void TheFocusLineIsTheLabelBehindTheStatusAndTheAnnouncementsDiffer()
    {
        Assert.Equal("Focus", MediaFocusMode.Line(null));
        Assert.Equal("Focus", MediaFocusMode.Line("  "));
        Assert.Equal("Focus · 20 of 32 media", MediaFocusMode.Line("20 of 32 media"));
        Assert.Equal("Focus mode on", MediaFocusMode.Announcement(true));
        Assert.Equal("Focus mode off", MediaFocusMode.Announcement(false));
        Texts.SetLanguage("de");
        Assert.Equal("Fokus · 20 von 32 Medien", MediaFocusMode.Line("20 von 32 Medien"));
        Assert.Equal("Fokusmodus an", MediaFocusMode.Announcement(true));
        Assert.Equal("Fokusmodus aus", MediaFocusMode.Announcement(false));
        Assert.Equal("Fokus beenden", Texts.T("media.focus.exit"));
    }

    [Fact]
    public void TheFilterListIsOneChipLabelPerLine()
    {
        Assert.Equal("Tag: surface\nWithout Type: Video", MediaFocusMode.FilterList(["+tag:1\u0001Tag: surface", "-type:video\u0001Without Type: Video"]));
        Assert.Equal(string.Empty, MediaFocusMode.FilterList([]));
        Assert.Equal("no separator", MediaFocusMode.FilterList(["no separator"]));
    }

    public static IEnumerable<object[]> Languages() => new[] { "en", "de" }.Concat(Texts.BundledPackCodes()).Select(code => new object[] { code });

    [Theory]
    [MemberData(nameof(Languages))]
    public void EveryLanguageTranslatesTheFocusTexts(string language)
    {
        Texts.SetLanguage(language);
        string[] keys = ["command.media.focus-mode", "media.focus.label", "media.focus.exit", "media.focus.exit.tip", "media.focus.on", "media.focus.off"];
        foreach (string key in keys)
            Assert.False(string.IsNullOrWhiteSpace(Texts.T(key)) || Texts.T(key) == key, language + ": " + key);
        Assert.NotEqual(Texts.T("media.focus.on"), Texts.T("media.focus.off"));
        Assert.DoesNotContain("×", Texts.T("media.focus.exit")); // an icon plus a short text, not a cross in the text
        // The exit says which key does it; the label of the command is what the button, the menu and the shortcut list show.
        Assert.Contains("Esc", Texts.T("media.focus.exit.tip").Replace("Échap", "Esc"));
        Assert.Equal(Texts.T("command.media.focus-mode"), CommandCatalog.Label("media.focus-mode"));
    }

    [Fact]
    public void TheTextsAreTheSameInBothBuiltInLanguagesAsTheCommandCatalogSays()
    {
        CommandDefinition command = CommandCatalog.Get("media.focus-mode");
        Assert.Equal("Focus mode", command.LabelEn);
        Assert.Equal("Fokusmodus", command.LabelDe);
        Assert.Equal("Focus mode", command.Label);
        Texts.SetLanguage("de");
        Assert.Equal("Fokusmodus", command.Label);
    }

    // ---- command and shortcut ----

    [Theory]
    [InlineData(ShortcutPlatform.Linux, "Shift+F")]
    [InlineData(ShortcutPlatform.Windows, "Shift+F")]
    [InlineData(ShortcutPlatform.MacOS, "⇧F")]
    public void TheCommandIsShiftFOnTheMediaPageAndLeavesTheFavouritesKeyAlone(ShortcutPlatform platform, string display)
    {
        CommandDefinition command = CommandCatalog.Get("media.focus-mode");
        Assert.Equal(CommandScope.Media, command.Scope);
        Assert.Equal(new[] { "media" }, command.ExecutionContexts);
        Assert.True(command.IsImplemented);
        Assert.Equal("maximize", command.Icon);
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(platform: platform);
        Assert.Empty(bindings.Issues);
        Assert.Equal(display, bindings.Display("media.focus-mode"));
        KeyEventArgs shiftF = new() { Key = Key.F, KeyModifiers = KeyModifiers.Shift }, plainF = new() { Key = Key.F, KeyModifiers = KeyModifiers.None };
        Assert.True(bindings.Matches(shiftF, "media.focus-mode"));
        Assert.False(bindings.Matches(plainF, "media.focus-mode"));
        // F (the favourite) is the other command of the same key and never fires on Shift+F.
        Assert.Equal("F", CommandCatalog.Get("favorites.toggle").Shortcut);
        Assert.True(bindings.Matches(plainF, "favorites.toggle"));
        Assert.False(bindings.Matches(shiftF, "favorites.toggle"));
        Assert.Contains("media.focus-mode", EffectiveShortcutBindings.YieldingDefaultCommandIds);
    }

    [Fact]
    public void ShiftFIsNotUsedByAnyOtherCommandThatCanRunInTheSameContext()
    {
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(platform: ShortcutPlatform.Linux);
        KeyEventArgs shiftF = new() { Key = Key.F, KeyModifiers = KeyModifiers.Shift };
        string[] owners = CommandCatalog.All.Where(command => bindings.Matches(shiftF, command.Id)).Select(command => command.Id).ToArray();
        Assert.Equal(["media.focus-mode"], owners);
    }

    [Fact]
    public void TheShellRoutesTheShortcutToTheMediaPageOnlyAndNotIntoTextFields()
    {
        string shell = Source("Desktop/Shell/MainWindow.Shortcuts.cs");
        int start = shell.IndexOf("new CommandRegistration(MediaFocusMode.CommandId, media,", StringComparison.Ordinal);
        Assert.True(start > 0);
        string registration = shell[start..shell.IndexOf("Priority: 100));", start, StringComparison.Ordinal)];
        Assert.Contains("_selectedPage == WorkspacePage.Media && !_largeReaderOpen && _gallery == null", registration);
        Assert.Contains("media.CanToggleFocusMode, media.ToggleFocusModeByShortcut", registration);
        // A capital F typed into the search field stays a letter: no AllowInTextInput.
        Assert.DoesNotContain("AllowInTextInput", registration);
    }

    // ---- what the mode hides, what stays ----

    [Fact]
    public void TheModeHidesTheToolbarChipsHintStatusAndFilterSectionAndKeepsTheGridTheSelectionBarAndTheError()
    {
        string focus = Source(Focus), page = Source("Desktop/Views/Media/MediaPage.cs");
        Assert.Contains("Control[] top = { _toolbar!, _searchCarryOffer, (Control)_filterChips.Parent! };", focus);
        Assert.Contains("Control[] bottom = { _queryHint, (Control)_status.Parent!, _filterSurface! };", focus);
        // The two groups are the only things hidden; the filter error stays between them, the selection bar and the grid are not in them.
        Assert.Contains("_topRows!.IsVisible = _bottomRows!.IsVisible = !on;", focus);
        Assert.DoesNotContain("_filterError.IsVisible", focus);
        Assert.DoesNotContain("BuildSelectionBar", focus);
        Assert.DoesNotContain("_grid.IsVisible", focus);
        Assert.DoesNotContain("_selectionRow", focus);
        Assert.Contains("_headerBody.Children.Add(BuildExportAllAsk()); _headerBody.Children.Add(BuildSelectionBar());", page);
        // The groups wrap the rows in their old order and spacing, so the header looks as before while the mode is off.
        Assert.Contains("Spacing = _headerBody.Spacing", focus);
        // The filter model, the chips and the term set are not touched by the mode.
        foreach (string unchanged in new[] { "MediaPage.Filters.cs", "MediaPage.FilterChips.cs", "MediaPage.FilterSurface.cs", "MediaPage.Query.cs" })
            Assert.DoesNotContain("_focusMode", Source("Desktop/Views/Media/" + unchanged));
    }

    [Fact]
    public void TheFocusRowIs32DipWithTheExitAloneAtTheLeftAndTheCountOnlyWhileAFilterIsActive()
    {
        string focus = Source(Focus);
        Assert.Equal(32, MediaFocusMode.FocusRowHeight);
        Assert.Contains("Height = MediaFocusMode.FocusRowHeight", focus);
        Assert.Contains("new ColumnDefinitions(\"Auto,Auto,*\")", focus);
        Assert.Contains("Avalonia.Controls.Grid.SetColumn(_focusExit, 0);", focus); // the way out first: far left, first in the tab order
        Assert.Contains("_focusExit.HorizontalAlignment = HorizontalAlignment.Left;", focus);
        Assert.Contains("WorkspaceIcons.Create(WorkspaceIcons.ChevronLeft), _focusExitLabel", focus);
        Assert.Contains("FocusExitGap = 24", focus);
        Assert.Contains("\"Workspace.Border\"", focus); // the thin separator token of the HeaderBar back slot
        Assert.Contains("_focusExitLabel.IsVisible = e.NewSize.Width >= FocusExitCompactWidth", focus);
        Assert.True(focus.IndexOf("_focusRow.Children.Add(_focusExit)", StringComparison.Ordinal) < focus.IndexOf("_focusRow.Children.Add(left)", StringComparison.Ordinal));
        Assert.Contains("_focusSeparator.IsVisible = _focusFilters.IsVisible = count > 0;", focus);
        Assert.Contains("ToolTip.SetTip(_focusFilters, MediaFocusMode.FilterList(_renderedChips));", focus);
        // The counter is the page's status line behind a label, refreshed with it (presentation and progress ticks).
        Assert.Contains("MediaFocusMode.Line(_status.Text)", focus);
        Assert.Contains("RefreshFocusRow();", Source("Desktop/Views/Media/MediaPage.cs"));
        // The toolbar button uses the Maximize icon prepared in M1 and the catalog command's label and shortcut.
        Assert.Contains("WorkspaceIcons.MakeSquare(_focus, WorkspaceIcons.Maximize, Texts.T(\"command.media.focus-mode\"), CommandBindings.Tooltip(MediaFocusMode.CommandId));", focus);
    }

    [Fact]
    public void TheToolbarButtonSitsBetweenRefreshAndMoreAndWorkViewsKnowIt()
    {
        string toolbar = Source("Desktop/Views/Media/MediaPage.Toolbar.cs"), view = Source("Desktop/Views/Media/MediaPage.WorkView.cs");
        Assert.Contains("{ _filters, _shuffle, _sort, _sortDirection, _refresh, _layoutButton, _focus, _sidebarToggles, _exportAll, _manageRatings, _booruOpen,", toolbar);
        Assert.Contains("WorkViewElement.SetId(_focus, \"media.toolbar.focus\");", view);
        Assert.Contains("WorkViewGate.Apply(_focus, view);", view);
        Assert.Contains("Overflowed(_layoutButton, _focus, _refresh, _shuffle, _exportAll, _manageRatings, _booruOpen, _queueWalk, _taggingPageOpen, _queueAll, _mediaFolders)", view);
        // A narrow page moves it under More first (MediaToolbarLayout.Demotable); the shortcut keeps working.
        Assert.True(MediaToolbarLayout.IsMovedUnderMore(MediaToolbarLayout.Level.Reduced, "media.toolbar.focus"));
        Assert.Equal("media.toolbar.focus", MediaToolbarLayout.MovedUnderMore(MediaToolbarLayout.Level.Compact)[0]);
    }

    // ---- state ----

    [Fact]
    public void StartingAndEndingTheModeFlipsOneFlagAnnouncesBothWaysAndKeepsItPerPage()
    {
        string focus = Source(Focus);
        Assert.Contains("if (_lifetime.IsCancellationRequested || on == _focusMode || on && !CanToggleFocusMode) return;", focus);
        Assert.Contains("_focusMode = on;", focus);
        Assert.Contains("if (announce) _focusAnnouncement.Text = MediaFocusMode.Announcement(on);", focus);
        Assert.Contains("void ToggleFocusModeByShortcut() => SetFocusMode(!_focusMode);", focus);
        Assert.Contains("_focus.Click += (_, _) => SetFocusMode(!_focusMode);", focus);
        Assert.Contains("_focusExit.Click += (_, _) => SetFocusMode(false);", focus);
        // The announcement is a polite live region that is in the tree at no size, so it is there while the row is hidden.
        Assert.Contains("AutomationProperties.SetLiveSetting(_focusAnnouncement, AutomationLiveSetting.Polite);", focus);
        Assert.Contains("body.Children.Add(_focusAnnouncement);", focus);
        // An empty library has no header: the mode ends with it (and cannot start there).
        Assert.Contains("bool CanToggleFocusMode => !_lifetime.IsCancellationRequested && (_focusMode || _headerBody.IsVisible);", focus.Replace("internal ", ""));
        Assert.Contains("EndFocusWithoutHeader();", Source("Desktop/Views/Media/MediaPage.cs"));
        // Asking for the search field shows the rows it sits in.
        Assert.Contains("SetFocusMode(false);", Source("Desktop/Views/Media/MediaPage.Filters.cs"));
    }

    [Fact]
    public void TheModeIsAWorkingStateAndNeverSaved()
    {
        string focus = Source(Focus), helper = Source("Desktop/Views/Media/MediaFocusMode.cs");
        foreach (string persistence in new[] { "Settings", "Config", "Save", "Persist", "Profile", "File.", "Preferences" })
        {
            Assert.DoesNotContain(persistence, focus);
            Assert.DoesNotContain(persistence, helper);
        }
        Assert.Contains("private bool _focusMode;", focus); // a field of the page: a new page starts with the header shown
        // Nothing outside the media page and the shell's shortcut line reads or writes the state.
        string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "../..", "Desktop"));
        string[] users = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("SetFocusMode", StringComparison.Ordinal) || File.ReadAllText(path).Contains("IsFocusMode", StringComparison.Ordinal))
            .Select(path => Path.GetFileName(path)).Order().ToArray();
        Assert.Equal(["MediaPage.Filters.cs", "MediaPage.Focus.cs", "MediaPage.Selection.cs"], users);
    }

    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}

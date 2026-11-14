using System.Xml.Linq;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// The media page's focus mode as a real full-screen grid, and the visible label of the order box. The controls need Avalonia's platform, which
// this test host does not start, so what they do (title, subtitle and counts hidden, the footer gone, the header floating behind a hover
// strip, the label next to the order box) is driven in the media-page smoke (FocusModeAsync). Here: the setting (default, round trip, copies),
// its row on the Reading and media page, its texts in every language, and the source contracts that pin what the shell hides and when it
// gives the chrome back.
[Collection("Localization")]
public sealed class MediaFocusFullscreenTests : IDisposable
{
    public MediaFocusFullscreenTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static string Source(string relative, [System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative)));

    private const string Focus = "Desktop/Views/Media/MediaPage.Focus.cs", Chrome = "Desktop/Shell/MainWindow.MediaFocusChrome.cs";

    private sealed class Profile : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("meowchan-focus-chrome-").FullName;
        public void Dispose() => Directory.Delete(Path, true);
    }

    // ---- the setting ----

    [Fact]
    public void TheHeaderIsHiddenInFocusModeByDefaultAndTheChoiceRoundTripsThroughTheProfileAndEveryCopy()
    {
        using Profile profile = new();
        Config config = Config.LoadConfig(profile.Path);
        Assert.True(config.MediaFocusHidesHeader);
        Assert.True(new Config().Clone().MediaFocusHidesHeader);
        config.MediaFocusHidesHeader = false;
        config.SaveConfig(profile.Path);
        Config loaded = Config.LoadConfig(profile.Path);
        Assert.False(loaded.MediaFocusHidesHeader);
        Assert.False(loaded.Clone().MediaFocusHidesHeader);
        Config copy = new();
        copy.CopyFrom(loaded);
        Assert.False(copy.MediaFocusHidesHeader);
        Config settings = new();
        settings.CopySettingsFieldsFrom(loaded);
        Assert.False(settings.MediaFocusHidesHeader);
        Assert.False(SettingsTransfer.CreatePortableSnapshot(loaded).MediaFocusHidesHeader);
        Assert.True(SettingsTransfer.CreatePortableSnapshot(new Config()).MediaFocusHidesHeader);
    }

    [Fact]
    public void AMalformedSavedValueFallsBackToOnWithoutDiscardingTheOtherSettings()
    {
        using Profile profile = new();
        File.WriteAllText(Path.Combine(profile.Path, "settings.json"), "{\"RecheckTimer\":120,\"MediaFocusHidesHeader\":[]}");
        Config loaded = Config.LoadConfig(profile.Path);
        Assert.Null(loaded.LoadWarning);
        Assert.Equal(120, loaded.RecheckTimer);
        Assert.True(loaded.MediaFocusHidesHeader);
    }

    [Fact]
    public void TheSettingIsACheckBoxOfTheReadingAndMediaGroupWithAHelpTextAndAResetAndAnAlias()
    {
        XDocument markup = XDocument.Load(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "../..", "Desktop/Views/Settings/SettingsEditor.axaml")));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement input = Assert.Single(markup.Descendants(), element => (string?)element.Attribute(x + "Name") == "MediaFocusHidesHeaderInput");
        Assert.Equal("CheckBox", input.Name.LocalName);
        Assert.Equal("{Binding MediaFocusHidesHeader, Mode=TwoWay}", (string?)input.Attribute("IsChecked"));
        Assert.Contains("MediaFocusHidesHeaderHint", (string?)input.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.HelpText").Value);
        XElement group = Assert.Single(markup.Descendants(), element => (string?)element.Attribute(x + "Name") == "SettingsReadingMediaGroup");
        Assert.Contains(input.Ancestors(), ancestor => ReferenceEquals(ancestor, group));
        Assert.Contains("Flag(MediaFocusHidesHeaderInput, () => MediaFocusHidesHeader, value => MediaFocusHidesHeader = value, config => config.MediaFocusHidesHeader),",
            Source("Desktop/Views/Settings/SettingsEditor.RowMenu.cs"));
        Assert.Contains("[\"MediaFocusHidesHeader\"] =", Source("Desktop/Views/Settings/SettingsSearchAliases.cs"));
        string viewer = Source("Desktop/Views/Settings/SettingsEditor.Viewer.cs");
        Assert.Contains("MediaFocusHidesHeader = config.MediaFocusHidesHeader;", viewer);       // start, discard, reset
        Assert.Contains("candidate.MediaFocusHidesHeader = MediaFocusHidesHeader;", viewer);     // save
    }

    // ---- texts ----

    public static IEnumerable<object[]> Languages() => new[] { "en", "de" }.Concat(Texts.BundledPackCodes()).Select(code => new object[] { code });

    [Theory]
    [MemberData(nameof(Languages))]
    public void EveryLanguageTranslatesTheNewFocusAndSortTexts(string language)
    {
        Texts.SetLanguage(language);
        foreach (string key in new[] { "media.focus.headerStrip", "settings.mediaFocusHidesHeader", "settings.mediaFocusHidesHeaderHint", "media.sort" })
            Assert.False(string.IsNullOrWhiteSpace(Texts.T(key)) || Texts.T(key) == key, language + ": " + key);
        // The hint names the way back, like the exit button's tooltip does.
        Assert.Contains("Esc", Texts.T("settings.mediaFocusHidesHeaderHint").Replace("Échap", "Esc"));
        Assert.Contains("×", Texts.T("settings.mediaFocusHidesHeaderHint"));
        // The label is one short word (it sits next to a combo box in the toolbar row).
        Assert.True(Texts.T("media.sort").Length <= 14, language + ": " + Texts.T("media.sort"));
        if (language != "en")
        {
            Texts.SetLanguage("en");
            string[] english = new[] { "media.focus.headerStrip", "settings.mediaFocusHidesHeader", "settings.mediaFocusHidesHeaderHint", "media.sort" }.Select(key => Texts.T(key)).ToArray();
            Texts.SetLanguage(language);
            string[] own = new[] { "media.focus.headerStrip", "settings.mediaFocusHidesHeader", "settings.mediaFocusHidesHeaderHint", "media.sort" }.Select(key => Texts.T(key)).ToArray();
            for (int index = 0; index < english.Length; index++) Assert.NotEqual(english[index], own[index]);
        }
    }

    [Fact]
    public void TheSortLabelIsSortInEnglishAndSortierungInGerman()
    {
        Assert.Equal("Sort", Texts.T("media.sort"));
        Texts.SetLanguage("de");
        Assert.Equal("Sortierung", Texts.T("media.sort"));
    }

    // ---- the label of the order box ----

    [Fact]
    public void TheOrderBoxHasAVisibleMutedLabelBeforeItWithTheSameAccessibleName()
    {
        string toolbar = Source("Desktop/Views/Media/MediaPage.Toolbar.cs"), sorting = Source("Desktop/Views/Media/MediaPage.Sorting.cs"), page = Source("Desktop/Views/Media/MediaPage.cs");
        Assert.Contains("{ _shuffle, _sortLabel, _sort, _sortDirection,", toolbar);
        Assert.Contains("Name = \"MediaSortLabel\", Classes = { \"muted\" }", sorting);
        Assert.Contains("_sortLabel.Text = Texts.T(\"media.sort\");", sorting);
        Assert.Contains("AutomationProperties.SetName(_sort, _sortLabel.Text);", sorting);
        // The label follows the box's visibility (and goes with the Compact stage, N6) and is not read twice by a screen reader.
        Assert.Contains("_sortLabel.IsVisible = _sort.IsVisible && MediaToolbarLayout.ShowsSortLabel(_toolbarLevel);", toolbar);
        Assert.Contains("e.Property == Visual.IsVisibleProperty) SyncSortLabel();", toolbar);
        Assert.Contains("AutomationProperties.SetAccessibilityView(_sortLabel, AccessibilityView.Raw);", toolbar);
        // The name is set in one place (on every language change), not twice.
        Assert.DoesNotContain("AutomationProperties.SetName(_sort, Texts.T(\"media.sort\"));", page);
    }

    // ---- what the page hides ----

    [Fact]
    public void TheFocusModeAlsoHidesTheTitleAndSubtitleKeepsTheCountsInTheFocusRowAndTellsTheShell()
    {
        string focus = Source(Focus);
        Assert.Contains("_title.IsVisible = _subtitle.IsVisible = !on;", focus);
        Assert.Contains("new Control[] { _topRows!, _bottomRows!, _title, _subtitle }", focus);
        // The counts line is the focus row's text and, whole, its tooltip.
        Assert.Contains("ToolTip.SetTip(_focusText, string.IsNullOrWhiteSpace(_status.Text) ? null : _status.Text);", focus);
        Assert.Contains("MediaFocusMode.Line(_status.Text)", focus);
        // The shell is told after the state changed, in both directions.
        Assert.Contains("internal event Action<bool>? FocusModeChanged;", focus);
        Assert.Contains("FocusModeChanged?.Invoke(on);", focus);
        Assert.True(focus.IndexOf("_focusMode = on;", StringComparison.Ordinal) < focus.IndexOf("FocusModeChanged?.Invoke(on);", StringComparison.Ordinal));
        // The grid and the selection bar still stay (the pinned contract of MediaFocusModeTests); the focus row stays too.
        Assert.DoesNotContain("_grid.IsVisible", focus);
        Assert.DoesNotContain("_selectionRow", focus);
        Assert.Contains("_focusRow.IsVisible = on;", focus);
        Assert.Contains("_focusMode = on;", focus);
    }

    // ---- what the shell hides ----

    [Fact]
    public void TheShellHidesTheFooterAlwaysAndTheHeaderOnlyWhenTheSettingAsksForIt()
    {
        string chrome = Source(Chrome);
        // The conditions: the mode is on, the Media page is shown, nothing covers it.
        Assert.Contains("bool hide = _mediaFocusActive && !_isClosing && _selectedPage == WorkspacePage.Media && !_largeReaderOpen && _gallery == null;", chrome);
        Assert.Contains("_mediaFocusFooterHidden = hide;", chrome);
        Assert.Contains("_mediaFocusHeaderHidden = hide && _config.MediaFocusHidesHeader;", chrome);
        // The footer: the status strip and the page navigation lose their rows as well as their visibility.
        Assert.Contains("WorkspaceStatusBar.IsVisible = WorkspaceNavigation.IsVisible = !hide;", chrome);
        Assert.Contains("WorkspaceRoot.RowDefinitions[2].Height = new GridLength(hide ? 0 : StatusStripHeight);", chrome);
        Assert.Contains("WorkspaceRoot.RowDefinitions[3].Height = new GridLength(hide ? 0 : NavigationHeight);", chrome);
        // The numbers are those of the XAML rows.
        string markup = Source("Desktop/Shell/MainWindow.axaml");
        Assert.Contains("<RowDefinition Height=\"26\" />\n            <RowDefinition Height=\"52\" />", markup.Replace("\r\n", "\n"));
        Assert.Contains("StatusStripHeight = 26, NavigationHeight = 52", chrome);
    }

    [Fact]
    public void TheHeaderFloatsOverThePageBehindAHoverStripSoTheWindowControlsStayReachable()
    {
        string chrome = Source(Chrome), markup = Source("Desktop/Shell/MainWindow.axaml");
        Assert.Contains("WorkspaceRoot.RowDefinitions[0].Height = floating ? new GridLength(0) : WorkspaceMetrics.TitleBarRow;", chrome);
        Assert.Contains("Grid.SetRowSpan(frame, floating ? 2 : 1);", chrome);
        Assert.Contains("frame.VerticalAlignment = floating ? VerticalAlignment.Top : VerticalAlignment.Stretch;", chrome);
        Assert.Contains("frame.Height = floating ? WorkspaceMetrics.TitleBarHeight : double.NaN;", chrome);
        Assert.Contains("frame.IsVisible = !_galleryImmersive && (!floating || _mediaFocusHeaderRevealed);", chrome);
        Assert.Contains("strip.PointerEntered += (_, _) => RevealMediaFocusHeader(true);", chrome);
        // The strip is a thin hit target at the top edge, over the page, below the modal overlays, and hidden until it is needed.
        Assert.Contains("x:Name=\"MediaFocusHeaderStrip\" Grid.RowSpan=\"2\" Height=\"6\" VerticalAlignment=\"Top\" IsVisible=\"False\" ZIndex=\"119\"", markup);
        Assert.Contains("Background=\"Transparent\"", markup[markup.IndexOf("MediaFocusHeaderStrip", StringComparison.Ordinal)..]);
        // It does not go away under a pointer, an open menu or the keyboard focus.
        Assert.Contains("WorkspaceTitleBarFrame.IsPointerOver || WorkspaceMenuItem.IsSubMenuOpen", chrome);
        Assert.Contains("WorkspaceTitleBarFrame.IsKeyboardFocusWithin", chrome);
    }

    [Fact]
    public void LeavingTheModeThePageOrTheSettingGivesTheChromeBackAndTheGalleryKeepsItsOwnFullscreenRule()
    {
        string media = Source("Desktop/Shell/MainWindow.Media.cs"), navigation = Source("Desktop/Shell/MainWindow.Navigation.cs"),
            presentation = Source("Desktop/Shell/MainWindow.Presentation.cs"), gallery = Source("Desktop/Shell/MainWindow.Gallery.cs");
        Assert.Contains("_mediaPage.FocusModeChanged += on => { _mediaFocusActive = on; UpdateMediaFocusChrome(); };", media);
        // Every navigation re-applies the rule (the page keeps its mode while another page is shown).
        Assert.Contains("_selectedPage = page;\n        UpdateMediaFocusChrome();", navigation.Replace("\r\n", "\n"));
        // A saved setting applies to a mode that is already on.
        Assert.Contains("UpdateMediaFocusChrome();", presentation);
        // The gallery's immersive rule and the focus rule are applied together by one method; the gallery covering the page gives the chrome back.
        Assert.Contains("_galleryImmersive = immersive;", gallery);
        Assert.Contains("UpdateMediaFocusChrome();", gallery);
        Assert.DoesNotContain("WorkspaceTitleBarFrame.IsVisible = !immersive;", gallery);
        Assert.Contains("Grid.SetRowSpan(host, immersive ? 4 : 3);", gallery);
    }

    [Fact]
    public void TheShellCodeDoesNotTouchThePagesOwnStateOrTheFilterAndChipCode()
    {
        string chrome = Source(Chrome);
        Assert.DoesNotContain("IsFocusMode", chrome);
        Assert.DoesNotContain("SetFocusMode", chrome);
        foreach (string chipCode in new[] { "MediaPage.FilterChips.cs", "MediaPage.Filters.cs", "MediaPage.FilterSurface.cs" })
            Assert.DoesNotContain("_sortLabel", Source("Desktop/Views/Media/" + chipCode));
        // The state is a working state: it is not saved, and neither is the header's floating state.
        foreach (string word in new[] { "SaveConfig", "SaveViewPreferences", "Persist" })
            Assert.DoesNotContain(word, chrome);
    }

    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}

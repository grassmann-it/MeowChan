using System.Reflection;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Motion;
using MeowChan.Desktop.Updates;
using MeowChan.Gallery;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

// Focus mode settings, localizations and layout. The headless window exercises the floating header's pointer and activation route.
[Collection("Localization")]
public sealed class MediaFocusFullscreenTests : IDisposable
{
    public MediaFocusFullscreenTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static string Source(string relative, [System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative)));

    private const string Focus = "Desktop/Views/Media/MediaPage.Focus.cs", Chrome = "Desktop/Shell/MainWindow.MediaFocusChrome.cs";
    private static readonly string[] CaptionButtonNames = ["WindowMinimizeButton", "WindowMaximizeButton", "WindowCloseButton"];

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
    public void TheSettingIsASwitchOfTheReadingAndMediaGroupWithAHelpTextAndAResetAndAnAlias()
    {
        XDocument markup = XDocument.Load(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "../..", "Desktop/Views/Settings/SettingsEditor.axaml")));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement input = Assert.Single(markup.Descendants(), element => (string?)element.Attribute(x + "Name") == "MediaFocusHidesHeaderInput");
        Assert.Equal("ToggleSwitch", input.Name.LocalName);
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
        Assert.Contains(Texts.T("media.focus.exit"), Texts.T("settings.mediaFocusHidesHeaderHint")); // it quotes the button's label
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

    // UX review 17.11.2026: the "Sort" label is a muted prefix inside the closed box (PrefixedComboBox), not a separate text in front of it.
    [Fact]
    public void TheOrderBoxShowsAMutedSortPrefixInsideItWithTheSameAccessibleName()
    {
        string toolbar = Source("Desktop/Views/Media/MediaPage.Toolbar.cs"), sorting = Source("Desktop/Views/Media/MediaPage.Sorting.cs"),
            page = Source("Desktop/Views/Media/MediaPage.cs"), box = Source("Desktop/Controls/PrefixedComboBox.cs");
        Assert.Contains("{ _filters, _shuffle, _sort, _sortDirection,", toolbar);
        Assert.Contains("private readonly PrefixedComboBox _sort = new() { Name = \"MediaSort\"", page);
        Assert.Contains("_sort.Prefix = Texts.T(\"media.sort\");", sorting);
        Assert.Contains("AutomationProperties.SetName(_sort, _sort.Prefix);", sorting);
        Assert.DoesNotContain("MediaSortLabel", sorting + toolbar + page);
        // The prefix goes with the Compact stage (N6) and whenever the row has no room for it; it is muted and not read twice by a screen reader.
        Assert.Contains("MediaToolbarLayout.ShowsSortPrefix(_toolbarLevel,", toolbar);
        Assert.Contains(": MediaToolbarLayout.ShowsSortLabel(_toolbarLevel);", toolbar);
        Assert.Contains("new DynamicResourceExtension(\"Workspace.Muted\")", box);
        Assert.Contains("AutomationProperties.SetAccessibilityView(_prefix, AccessibilityView.Raw);", box);
        Assert.Contains("SelectionBoxItemTemplate = new FuncDataTemplate<object?>", box); // the closed box only; the list shows the values
        // The name is set in one place (on every language change), not twice.
        Assert.DoesNotContain("AutomationProperties.SetName(_sort, Texts.T(\"media.sort\"));", page);
    }

    // ---- what the page hides ----

    [Fact]
    public void TheFocusModeAlsoHidesTheTitleAndSubtitleKeepsTheCountsInTheFocusRowAndTellsTheShell()
    {
        string focus = Source(Focus);
        Assert.Contains("_title.IsVisible = !on;", focus); // the subtitle follows the layout plan (MP5), which hides it in focus mode
        Assert.Contains("ApplyLayoutPlan();", focus);
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
    public void TheHeaderFloatsOverThePageBehindAHoverStripSoTheWindowControlsStayReachable() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using Profile profile = new();
        new Config { DownloadPath = Path.Combine(profile.Path, "Downloads"), FirstRunSetupVersion = 1 }.SaveConfig(profile.Path);
        BooruComponentDispatcher.Wait(Exercise());

        async Task<bool> Exercise()
        {
            await using DesktopSessionHost session = new(profile.Path, Array.Empty<MeowChan.Interfaces.IImageBoard>(),
                updateSessionFactory: (path, config) => new UpdateSession(path, config, disableEnvironment: () => "1"));
            await using MainWindow window = new(session) { Width = 1000, Height = 700, Classes = { MotionPolicy.OffClass } };
            const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
            window.Show();
            window.PlatformImpl!.Activated?.Invoke();
            // Model the focus-mode notification on Media without waiting for the session startup gate.
            typeof(MainWindow).GetField("_selectedPage", privateInstance)!.SetValue(window, MainWindow.WorkspacePage.Media);
            typeof(MainWindow).GetField("_mediaFocusActive", privateInstance)!.SetValue(window, true);
            typeof(MainWindow).GetMethod("UpdateMediaFocusChrome", privateInstance)!.Invoke(window, null);
            Dispatcher.UIThread.RunJobs();
            Border strip = window.FindControl<Border>("MediaFocusHeaderStrip")!;
            Border frame = window.FindControl<Border>("WorkspaceTitleBarFrame")!;
            Grid root = window.FindControl<Grid>("WorkspaceRoot")!;
            Assert.True(window.IsActive && window.MediaFocusHeaderHidden);
            AssertFooter(window, visible: false);
            Assert.True(strip.IsVisible);
            Assert.Equal(6, strip.Bounds.Height);
            Assert.Equal(new GridLength(0), root.RowDefinitions[0].Height);
            Assert.False(frame.IsVisible);

            Point edge = strip.TranslatePoint(new Point(strip.Bounds.Width / 2, strip.Bounds.Height / 2), window)!.Value;
            window.MouseMove(edge);
            Assert.True(window.MediaFocusHeaderRevealed);
            Assert.True(frame.IsVisible);
            Assert.False(strip.IsVisible);
            Assert.Equal(2, Grid.GetRowSpan(frame));
            Assert.Equal(WorkspaceMetrics.TitleBarHeight, frame.Height);

            window.PlatformImpl.Deactivated?.Invoke();
            Assert.False(window.IsActive);
            Assert.False(window.MediaFocusHeaderRevealed);
            Assert.False(frame.IsVisible);
            Assert.True(strip.IsVisible);
            window.WindowState = WindowState.FullScreen;
            window.WindowState = WindowState.Normal;
            AssertFooter(window, visible: false);
            typeof(MainWindow).GetField("_mediaFocusActive", privateInstance)!.SetValue(window, false);
            typeof(MainWindow).GetMethod("UpdateMediaFocusChrome", privateInstance)!.Invoke(window, null);
            AssertFooter(window, visible: true);
            return true;
        }
    });

    [Fact]
    public void LeavingTheModeThePageOrTheSettingReappliesTheChromeAndFullscreenUsesTheSameTitleBar()
    {
        string media = Source("Desktop/Shell/MainWindow.Media.cs"), navigation = Source("Desktop/Shell/MainWindow.Navigation.cs"),
            presentation = Source("Desktop/Shell/MainWindow.Presentation.cs"), gallery = Source("Desktop/Shell/MainWindow.Gallery.cs");
        Assert.Contains("_mediaPage.FocusModeChanged += on => { _mediaFocusActive = on; UpdateMediaFocusChrome(); };", media);
        // Every navigation re-applies the rule (the page keeps its mode while another page is shown).
        Assert.Contains("_selectedPage = page;\n        UpdateMediaFocusChrome();", navigation.Replace("\r\n", "\n"));
        // A saved setting applies to a mode that is already on.
        Assert.Contains("UpdateMediaFocusChrome();", presentation);
        // A gallery covering Media recomputes media focus. Window fullscreen controls the shared title bar independently.
        Assert.Contains("UpdateMediaFocusChrome();", gallery);
        Assert.DoesNotContain("WorkspaceTitleBarFrame", gallery);
        Assert.Contains("_mediaFocusHeaderHidden || WindowState == WindowState.FullScreen", Source(Chrome));
    }

    [Fact]
    public void FullscreenHidesTheFooterAcrossWorkspacePagesWithoutDisablingPageShortcuts() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using Profile profile = new();
        new Config
        {
            DownloadPath = Path.Combine(profile.Path, "Downloads"), FirstRunSetupVersion = 1,
            WorkspaceMode = "Advanced", WindowWidth = 1200, WindowHeight = 700,
            MediaFocusHidesHeader = false,
            ShortcutBindings = new ShortcutBindingsSection().Set("navigation.media", [ShortcutGesture.Parse("Alt+3")])
        }.SaveConfig(profile.Path);
        BooruComponentDispatcher.Wait(Exercise());

        async Task<bool> Exercise()
        {
            await using DesktopSessionHost session = new(profile.Path, Array.Empty<IImageBoard>(),
                updateSessionFactory: (path, config) => new UpdateSession(path, config, disableEnvironment: () => "1"));
            await using MainWindow window = new(session) { Classes = { MotionPolicy.OffClass } };
            window.Show();
            window.PlatformImpl!.Activated?.Invoke();
            await window.StartupTimingRestoreTask;
            ComboBox mode = window.FindControl<ComboBox>("WorkspaceModeComboBox")!;
            Border navigation = window.FindControl<Border>("WorkspaceNavigation")!;
            Border status = window.FindControl<Border>("WorkspaceStatusBar")!;
            Button downloads = window.FindControl<Button>("DownloadsNavigationButton")!;
            Border title = window.FindControl<Border>("WorkspaceTitleBarFrame")!;
            Assert.True(mode.Focus());
            mode.IsDropDownOpen = true;
            Assert.True(mode.IsDropDownOpen);
            // Exercise the state-change route used by native fullscreen without a shell command.
            window.WindowState = WindowState.FullScreen;
            AssertFooter(window, visible: false);
            Assert.False(mode.IsDropDownOpen);
            Assert.False(title.IsVisible);
            Control pageFocus = Assert.IsAssignableFrom<Control>(window.FocusManager!.GetFocusedElement());
            Assert.True(pageFocus.IsEffectivelyVisible);
            Assert.False(navigation.IsKeyboardFocusWithin);
            Assert.False(downloads.Focus());
            Assert.False(mode.Focus());

            // Start Tab and Shift+Tab from real content, with the footer and both rows collapsed.
            TextBox url = window.FindControl<TextBox>("UrlInput")!;
            foreach (RawInputModifiers modifiers in new[] { RawInputModifiers.None, RawInputModifiers.Shift })
            {
                Assert.True(url.Focus());
                window.KeyPress(Key.Tab, modifiers, PhysicalKey.Tab, "\t");
                Assert.NotSame(url, Assert.IsAssignableFrom<Control>(window.FocusManager.GetFocusedElement()));
                Assert.False(navigation.IsKeyboardFocusWithin);
                Assert.False(status.IsKeyboardFocusWithin);
            }

            Assert.True(pageFocus.Focus());
            Press(pageFocus, Key.LeftAlt, KeyModifiers.Alt);
            Assert.True(Press(pageFocus, Key.D3, KeyModifiers.Alt));
            var release = new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.LeftAlt };
            window.RaiseEvent(release);
            Assert.False(release.Handled);
            Assert.Equal(MainWindow.WorkspacePage.Media, window.SelectedPage);
            Assert.False(title.IsVisible);
            AssertFooter(window, visible: false);
            Assert.True(Press(window, Key.Escape));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(WindowState.Normal, window.WindowState);
            AssertFooter(window, visible: true);
            Assert.True(mode.Focus());

            foreach (MainWindow.WorkspacePage page in Enum.GetValues<MainWindow.WorkspacePage>()
                .Where(page => page != MainWindow.WorkspacePage.Booru)) // The real Booru route is covered by booru-workbench.
            {
                string command = "navigation." + page.ToString().ToLowerInvariant();
                KeyGesture gesture = CommandBindings.Gesture(command)!;
                Assert.True(Press(window, gesture.Key, gesture.KeyModifiers), command);
                Assert.Equal(page, window.SelectedPage);
                AssertFooter(window, visible: true);
                Assert.True(Press(window, Key.F11));
                Assert.Equal(WindowState.FullScreen, window.WindowState);
                AssertFooter(window, visible: false);
                Assert.True(Press(window, Key.F11));
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(WindowState.Normal, window.WindowState);
                AssertFooter(window, visible: true);
                Assert.Equal(page, window.SelectedPage);
            }
            return true;
        }
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ReaderFullscreenRevealsTheTitleBarWithoutMovingTheReaderOrLosingFocus(bool sidebarShown, bool fromArchive) =>
        BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using Profile profile = new();
        new Config
        {
            DownloadPath = Path.Combine(profile.Path, "Downloads"), FirstRunSetupVersion = 1,
            WorkspaceMode = "Advanced", WindowWidth = 1200, WindowHeight = 700,
            ReaderMediaSidebar = sidebarShown, ReaderMediaSidebarMinimumWidth = Config.ReaderMediaSidebarMinimumWidthMinimum
        }.SaveConfig(profile.Path);
        BooruComponentDispatcher.Wait(Exercise());

        async Task<bool> Exercise()
        {
            await using DesktopSessionHost session = new(profile.Path, Array.Empty<IImageBoard>(),
                updateSessionFactory: (path, config) => new UpdateSession(path, config, disableEnvironment: () => "1"));
            await using MainWindow window = new(session) { Classes = { MotionPolicy.OffClass } };
            window.Show();
            window.PlatformImpl!.Activated?.Invoke();
            await window.StartupTimingRestoreTask;
            if (fromArchive)
                window.FindControl<Button>("ArchiveNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(fromArchive ? MainWindow.WorkspacePage.Archive : MainWindow.WorkspacePage.Downloads, window.SelectedPage);
            typeof(MainWindow).GetMethod("OpenLargeReader", BindingFlags.Instance | BindingFlags.NonPublic,
                [typeof(Control), typeof(IReadOnlyList<ResourceId>)])!.Invoke(window, [null, Array.Empty<ResourceId>()]);
            Layout();

            ContentControl reader = window.FindControl<ContentControl>("ReaderHost")!;
            Grid pane = window.ArchivePage.FindControl<Grid>("ArchiveReaderPane")!;
            Button fullscreen = window.ArchivePage.FindControl<Button>("ArchiveReaderFullscreen")!;
            Button readerFocus = window.ArchivePage.FindControl<Button>("ArchiveCloseReader")!;
            Border strip = window.FindControl<Border>("MediaFocusHeaderStrip")!;
            Border frame = window.FindControl<Border>("WorkspaceTitleBarFrame")!;
            Grid root = window.FindControl<Grid>("WorkspaceRoot")!;
            Assert.Equal(sidebarShown, window.ArchivePage.MediaSidebar?.IsEffectivelyVisible == true);
            Assert.True(readerFocus.Focus());
            fullscreen.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout();
            Assert.Equal(WindowState.FullScreen, window.WindowState);
            AssertFooter(window, visible: false);
            Assert.Equal(new GridLength(0), root.RowDefinitions[0].Height);
            Assert.False(frame.IsVisible);
            Assert.False(frame.IsHitTestVisible);
            Assert.Equal(6, strip.Bounds.Height);
            Rect readerBounds = reader.Bounds, paneBounds = pane.Bounds;
            Rect? sidebarBounds = window.ArchivePage.MediaSidebar?.Bounds;

            window.MouseMove(strip.TranslatePoint(new Point(strip.Bounds.Width / 2, 3), window)!.Value);
            Layout();
            Assert.True(frame.IsVisible && frame.IsHitTestVisible);
            AssertFooter(window, visible: false);
            Assert.False(strip.IsVisible);
            Assert.Same(readerFocus, window.FocusManager!.GetFocusedElement());
            bool ownCaptionButtons = WindowChrome.ShowsOwnCaptionButtons(WindowChrome.Mode);
            Button shellFullscreen = window.FindControl<Button>("FullscreenButton")!;
            foreach (Button button in CaptionButtonNames.Select(name => window.FindControl<Button>(name)!).Prepend(shellFullscreen))
            {
                // The app's fullscreen command is available with both native and custom captions.
                if (!ownCaptionButtons && button != shellFullscreen)
                {
                    Assert.False(button.IsEffectivelyVisible);
                    continue;
                }
                Point point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                window.MouseMove(point);
                Assert.True(button.IsEffectivelyVisible && button.IsEffectivelyEnabled && button.IsPointerOver, button.Name);
            }
            window.MouseMove(new Point(300, 250));
            Layout();
            Assert.False(frame.IsVisible);
            Assert.True(strip.IsVisible);
            Assert.Equal(readerBounds, reader.Bounds);
            Assert.Equal(paneBounds, pane.Bounds);
            Assert.Equal(sidebarBounds, window.ArchivePage.MediaSidebar?.Bounds);

            Assert.True(Press(readerFocus, Key.F6));
            Layout();
            Button brand = window.FindControl<Button>("BrandAboutButton")!;
            Assert.Same(brand, window.FocusManager.GetFocusedElement());
            Button headerAction = ownCaptionButtons ? window.FindControl<Button>("WindowCloseButton")! : shellFullscreen;
            Assert.True(headerAction.Focus(NavigationMethod.Tab));
            Layout();
            Assert.True(frame.IsVisible);
            Assert.True(Press(headerAction, Key.F6));
            Layout();
            Assert.Same(readerFocus, window.FocusManager.GetFocusedElement());
            Assert.False(frame.IsVisible);

            Press(readerFocus, Key.LeftAlt, KeyModifiers.Alt);
            var release = new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.LeftAlt };
            readerFocus.RaiseEvent(release);
            Assert.True(release.Handled);
            Assert.Same(brand, window.FocusManager.GetFocusedElement());
            Assert.True(Press(brand, Key.Escape));
            Layout();
            Assert.Equal(WindowState.Normal, window.WindowState);
            AssertFooter(window, visible: true);
            Assert.Equal(WorkspaceMetrics.TitleBarRow, root.RowDefinitions[0].Height);
            Assert.Equal(1, Grid.GetRowSpan(frame));
            Assert.True(frame.IsVisible && frame.IsHitTestVisible && reader.IsVisible);
            Assert.False(strip.IsVisible);
            Assert.Same(readerFocus, window.FocusManager.GetFocusedElement());
            Assert.Equal(sidebarShown, window.ArchivePage.MediaSidebar?.IsEffectivelyVisible == true);

            Assert.True(Press(readerFocus, Key.F11));
            Assert.Equal(WindowState.FullScreen, window.WindowState);
            Assert.True(Press(readerFocus, Key.F11));
            Layout();
            Assert.Equal(WindowState.Normal, window.WindowState);
            return true;

            void Layout()
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            }
        }
    });

    [Fact]
    public void GalleryFullscreenSharesTheRevealWithoutMovingTheGallery() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using Profile profile = new();
        new Config { DownloadPath = Path.Combine(profile.Path, "Downloads"), FirstRunSetupVersion = 1 }.SaveConfig(profile.Path);
        BooruComponentDispatcher.Wait(Exercise());

        async Task<bool> Exercise()
        {
            await using DesktopSessionHost session = new(profile.Path, Array.Empty<IImageBoard>(),
                updateSessionFactory: (path, config) => new UpdateSession(path, config, disableEnvironment: () => "1"));
            await using MainWindow window = new(session) { Classes = { MotionPolicy.OffClass } };
            window.Show();
            window.PlatformImpl!.Activated?.Invoke();
            await window.StartupTimingRestoreTask;
            window.FindControl<Button>("MediaNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(MainWindow.WorkspacePage.Media, window.SelectedPage);
            GalleryEntryKey unavailable = new(new(new ResourceId("offline", "gallery", "1"), profile.Path, "missing"), null, null);
            await window.OpenMediaGalleryAsync(Array.Empty<GalleryEntry>(), unavailable);
            ContentControl host = window.FindControl<ContentControl>("GalleryHost")!;
            GalleryViewer gallery = Assert.IsType<GalleryViewer>(host.Content);
            Layout();
            gallery.GetGalleryVisualDescendants().OfType<Button>().Single(button => button.Name == "GalleryFullscreen")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout();
            Assert.Equal(WindowState.FullScreen, window.WindowState);
            AssertFooter(window, visible: false);
            Assert.True(host.IsVisible);
            Assert.Equal(0, host.Bounds.Y);
            Rect galleryBounds = host.Bounds;
            Border frame = window.FindControl<Border>("WorkspaceTitleBarFrame")!;
            Border strip = window.FindControl<Border>("MediaFocusHeaderStrip")!;
            Assert.False(frame.IsVisible);
            window.MouseMove(strip.TranslatePoint(new Point(strip.Bounds.Width / 2, 3), window)!.Value);
            Layout();
            Assert.True(frame.IsVisible);
            Assert.Equal(galleryBounds, host.Bounds);
            window.MouseMove(new Point(300, 250));
            Assert.False(frame.IsVisible);
            Assert.Equal(galleryBounds, host.Bounds);
            Assert.True(Press(gallery, Key.Escape));
            Assert.Equal(WindowState.Normal, window.WindowState);
            AssertFooter(window, visible: true);
            Assert.True(host.IsVisible && frame.IsVisible);
            Assert.False(strip.IsVisible);
            return true;

            void Layout()
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            }
        }
    });

    private static void AssertFooter(MainWindow window, bool visible)
    {
        Grid root = window.FindControl<Grid>("WorkspaceRoot")!;
        Assert.Equal(visible, window.FindControl<Border>("WorkspaceStatusBar")!.IsEffectivelyVisible);
        Assert.Equal(visible, window.FindControl<Border>("WorkspaceNavigation")!.IsEffectivelyVisible);
        Assert.Equal(new GridLength(visible ? 26 : 0), root.RowDefinitions[2].Height);
        Assert.Equal(new GridLength(visible ? 52 : 0), root.RowDefinitions[3].Height);
    }

    private static bool Press(Control target, Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers };
        target.RaiseEvent(args);
        return args.Handled;
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

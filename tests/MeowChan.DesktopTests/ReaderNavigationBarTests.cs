using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Motion;

namespace MeowChan.DesktopTests;

// The reader navigation capsule, its settings and translated text. The hosted test drives the modes and compact form.
[Collection("Localization")]
public sealed class ReaderNavigationBarTests : IDisposable
{
    public ReaderNavigationBarTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static string Source(string relative, [System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative)));

    private static XDocument Markup(string relative) => XDocument.Parse(Source(relative));

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string[] NavigationArrowIds =
        ["ReaderPreviousThread", "ReaderNextThread", "ReaderPreviousPost", "ReaderNextPost"];

    [Fact]
    public void SecondaryMediaGeometryTreatsMissingOrDetachedControlsAsNotVisible()
    {
        Assert.False(MeowChan.DesktopSmoke.SmokeVisualTree.IsAttachedAndEffectivelyVisible(null));
        Assert.False(MeowChan.DesktopSmoke.SmokeVisualTree.IsAttachedAndEffectivelyVisible(new Control()));
    }

    private static XElement Named(XDocument markup, string name) =>
        Assert.Single(markup.Descendants(), element => (string?)element.Attribute(X + "Name") == name);

    // ---- the settings ----

    [Fact]
    public void TheBarIsAutomaticWithPositionByDefault()
    {
        Config config = new();
        Assert.Equal("Auto", config.ReaderNavigationMode);
        Assert.True(config.ReaderNavigationShowPosition);
        Config loaded = JsonSerializer.Deserialize("{}", ConfigJsonContext.Default.Config)!;
        Assert.Equal("Auto", loaded.ReaderNavigationMode);
        Assert.True(loaded.ReaderNavigationShowPosition);
    }

    [Theory]
    [InlineData("Auto", "Auto")]
    [InlineData("Always", "Always")]
    [InlineData("Off", "Off")]
    [InlineData("always", "Always")]
    [InlineData(" OFF ", "Off")]
    [InlineData("auto", "Auto")]
    [InlineData("", "Auto")]
    [InlineData("Sometimes", "Auto")]
    [InlineData("0", "Auto")]
    [InlineData(null, "Auto")]
    public void AnUnknownModeReadsAsAuto(string? stored, string expected)
    {
        Assert.Equal(expected, new Config { ReaderNavigationMode = stored! }.ReaderNavigationMode);
        Assert.Equal(expected, Config.NormalizeReaderNavigationMode(stored));
        Config loaded = JsonSerializer.Deserialize("{\"ReaderNavigationMode\":" + JsonSerializer.Serialize(stored) + "}", ConfigJsonContext.Default.Config)!;
        Assert.Equal(expected, loaded.ReaderNavigationMode);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("{\"nested\":[1,2]}")]
    public void ADamagedValueOfAnotherTypeReadsAsAutoAndKeepsTheOtherSettings(string raw)
    {
        Config loaded = JsonSerializer.Deserialize("{\"ReaderNavigationMode\":" + raw + ",\"ControlsIdleHideMs\":1500,\"ReaderNavigationShowPosition\":" + raw + "}",
            ConfigJsonContext.Default.Config)!;
        Assert.Equal("Auto", loaded.ReaderNavigationMode);
        Assert.True(loaded.ReaderNavigationShowPosition);
        Assert.Equal(1500, loaded.ControlsIdleHideMs);
    }

    [Theory]
    [InlineData("Always", false)]
    [InlineData("Off", true)]
    [InlineData("Auto", false)]
    public void TheChoicesRoundTripThroughTheProfileAndEveryCopy(string mode, bool showPosition)
    {
        using TempDirectory directory = new();
        Config original = new() { ReaderNavigationMode = mode, ReaderNavigationShowPosition = showPosition, DownloadPath = Path.Combine(directory.Path, "media") };
        Config clone = original.Clone();
        Config settingsCopy = new();
        settingsCopy.CopySettingsFieldsFrom(clone);
        Config fullCopy = new();
        fullCopy.CopyFrom(settingsCopy);
        fullCopy.SaveConfig(directory.Path);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        foreach (Config candidate in new[] { original, clone, settingsCopy, fullCopy, loaded })
        {
            Assert.Equal(mode, candidate.ReaderNavigationMode);
            Assert.Equal(showPosition, candidate.ReaderNavigationShowPosition);
            candidate.Validate();
        }
        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.Path, "settings.json")));
        Assert.Equal(mode, saved.RootElement.GetProperty(nameof(Config.ReaderNavigationMode)).GetString());
        Assert.Equal(showPosition, saved.RootElement.GetProperty(nameof(Config.ReaderNavigationShowPosition)).GetBoolean());
    }

    [Fact]
    public void TheSettingsTransferCarriesBothChoices()
    {
        Config source = new() { ReaderNavigationMode = "Always", ReaderNavigationShowPosition = false };
        Config snapshot = SettingsTransfer.CreatePortableSnapshot(source);
        Assert.Equal("Always", snapshot.ReaderNavigationMode);
        Assert.False(snapshot.ReaderNavigationShowPosition);
        Config defaults = SettingsTransfer.CreatePortableSnapshot(new Config());
        Assert.Equal("Auto", defaults.ReaderNavigationMode);
        Assert.True(defaults.ReaderNavigationShowPosition);
        JsonObject exported = SettingsTransfer.Export(source);
        Assert.Equal("Always", (string?)exported[nameof(Config.ReaderNavigationMode)]);
        Assert.False((bool?)exported[nameof(Config.ReaderNavigationShowPosition)]);
    }

    [Fact]
    public void BothChoicesAreUniformRowsOfTheReadingAndMediaGroupBesideTheIdleTimeWithHelpTextsResetsAndAliases()
    {
        XDocument markup = Markup("Desktop/Views/Settings/SettingsEditor.axaml");
        XElement group = Named(markup, "SettingsReadingMediaGroup");
        XElement mode = Named(markup, "ReaderNavigationModeInput"), position = Named(markup, "ReaderNavigationShowPositionInput"), idle = Named(markup, "ControlsIdleHideMsInput");
        Assert.Equal("ComboBox", mode.Name.LocalName);
        Assert.Equal("{Binding SelectedReaderNavigationMode, Mode=TwoWay}", (string?)mode.Attribute("SelectedItem"));
        Assert.Equal("{Binding ReaderNavigationModeChoices}", (string?)mode.Attribute("ItemsSource"));
        Assert.Equal("ToggleSwitch", position.Name.LocalName);
        Assert.Equal("{Binding ReaderNavigationShowPosition, Mode=TwoWay}", (string?)position.Attribute("IsChecked"));
        foreach (XElement input in new[] { mode, position })
        {
            Assert.Contains(input.Ancestors(), ancestor => ReferenceEquals(ancestor, group));
            XElement row = Assert.Single(input.Ancestors().Where(ancestor => ancestor.Name.LocalName == "Grid" && ((string?)ancestor.Attribute("Classes"))?.Contains("settings-row") == true).Take(1));
            Assert.Equal("*,280", (string?)row.Attribute("ColumnDefinitions"));
            Assert.Equal("settings-divider", (string?)row.Parent!.Attribute("Classes"));
            Assert.Contains("Hint", input.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.HelpText").Value);
        }
        // Beside the idle time: the same container, directly after its row.
        XElement idleRow = idle.Ancestors().First(ancestor => (string?)ancestor.Attribute("Classes") == "settings-divider");
        Assert.Same(idleRow.Parent, mode.Ancestors().First(ancestor => (string?)ancestor.Attribute("Classes") == "settings-divider").Parent);
        Assert.Equal(new[] { "ReaderNavigationModeInput", "ReaderNavigationShowPositionInput" },
            idleRow.ElementsAfterSelf().Take(2).Select(row => row.Descendants().Select(e => (string?)e.Attribute(X + "Name")).First(name => name?.EndsWith("Input", StringComparison.Ordinal) == true)).ToArray());
        string rowMenu = Source("Desktop/Views/Settings/SettingsEditor.RowMenu.cs");
        Assert.Contains("Flag(ReaderNavigationShowPositionInput, () => ReaderNavigationShowPosition, value => ReaderNavigationShowPosition = value, config => config.ReaderNavigationShowPosition),", rowMenu);
        Assert.Contains("Choice(ReaderNavigationModeInput, () => SelectedReaderNavigationMode?.Value,", rowMenu);
        string aliases = Source("Desktop/Views/Settings/SettingsSearchAliases.cs");
        Assert.Contains("[\"ReaderNavigationMode\"] =", aliases);
        Assert.Contains("[\"ReaderNavigationShowPosition\"] =", aliases);
        string viewer = Source("Desktop/Views/Settings/SettingsEditor.Viewer.cs");
        Assert.Contains("RefreshReaderNavigationModeChoices(config.ReaderNavigationMode);", viewer);        // start, discard, reset
        Assert.Contains("ReaderNavigationShowPosition = config.ReaderNavigationShowPosition;", viewer);
        Assert.Contains("candidate.ReaderNavigationMode = SelectedReaderNavigationMode?.Value ?? _config.ReaderNavigationMode;", viewer);   // save
        Assert.Contains("candidate.ReaderNavigationShowPosition = ReaderNavigationShowPosition;", viewer);
        Assert.Contains("nameof(SelectedReaderNavigationMode)", Source("Desktop/Views/Settings/SettingsEditor.axaml.cs")); // a language refresh is no edit
    }

    [Fact]
    public void TheShellHandsBothValuesToThePageWhereItHandsOverTheIdleTime()
    {
        foreach (string file in new[] { "Desktop/Shell/MainWindow.Navigation.cs", "Desktop/Shell/MainWindow.GalleryPreferences.cs" })
        {
            string text = Source(file);
            int idle = text.IndexOf("_archivePage.ReaderNavigationIdleDelayMs = _config.ControlsIdleHideMs;", StringComparison.Ordinal);
            Assert.True(idle >= 0, file);
            Assert.Contains("_archivePage.ReaderNavigationMode = _config.ReaderNavigationMode;", text[idle..]);
            Assert.Contains("_archivePage.ReaderNavigationShowPosition = _config.ReaderNavigationShowPosition;", text[idle..]);
        }
    }

    // ---- markup ----

    [Fact]
    public void TheBarIsOneCapsuleOfFourArrowsInTabOrderWithTwoCountersAndASeparator()
    {
        XDocument markup = Markup("Desktop/Views/Archive/ArchivePage.axaml");
        XElement root = Named(markup, "ReaderNavigation");
        Assert.Equal("WrapPanel", root.Name.LocalName);
        Assert.Equal("False", (string?)root.Attribute("IsVisible"));
        XElement surface = Named(markup, "ReaderNavigationHitSurface");
        Assert.Same(root, surface.Parent);
        XElement bar = Named(markup, "ReaderNavigationBar");
        Assert.Same(surface, bar.Parent);
        Assert.Contains("workspace-overlay", (string?)bar.Attribute("Classes"));
        Assert.NotNull(root.Attribute("Background"));
        Assert.Equal("3", (string?)bar.Attribute("Padding"));
        // Not transparent to the pointer: the whole capsule takes clicks (a click in a gap must not reach the post below).
        Assert.Null(bar.Attribute("IsHitTestVisible"));
        // The two literals of the markup are the layout constants.
        Assert.Equal(ReaderNavigationLayout.BarMaxWidth, double.Parse((string)bar.Attribute("MaxWidth")!, System.Globalization.CultureInfo.InvariantCulture));
        string[] inOrder = bar.Descendants().Select(element => (string?)element.Attribute(X + "Name")).Where(name => name != null).ToArray()!;
        Assert.Equal(new[] { "ReaderThreadGroup", "ReaderPreviousThread", "ReaderThreadPosition", "ReaderNextThread", "ReaderNavigationSeparator",
            "ReaderPostGroup", "ReaderPreviousPost", "ReaderPostPosition", "ReaderNextPost" }, inOrder);
        foreach (string counter in new[] { "ReaderThreadPosition", "ReaderPostPosition" })
        {
            XElement text = Named(markup, counter);
            Assert.Equal("TextBlock", text.Name.LocalName);
            Assert.Contains("tnum", (string?)text.Attribute("Classes"));
            Assert.Contains("workspace-meta", (string?)text.Attribute("Classes"));
            Assert.Equal("NoWrap", (string?)text.Attribute("TextWrapping"));
            Assert.Equal(ReaderNavigationLayout.CounterMinWidth, double.Parse((string)text.Attribute("MinWidth")!, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Null(text.Attribute("LiveSetting"));
            Assert.DoesNotContain(text.Attributes(), attribute => attribute.Name.LocalName == "AutomationProperties.LiveSetting");
        }
        XElement separator = Named(markup, "ReaderNavigationSeparator");
        Assert.Equal("1", (string?)separator.Attribute("Width"));
        Assert.Equal("24", (string?)separator.Attribute("Height"));
        Assert.Equal("{DynamicResource Workspace.ControlBorder}", (string?)separator.Attribute("Background"));
        foreach (string name in NavigationArrowIds)
        {
            XElement button = Named(markup, name);
            Assert.Equal("Button", button.Name.LocalName);
            Assert.Equal("OnReaderNavigationClick", (string?)button.Attribute("Click"));
            Assert.Contains("ghost", (string?)button.Attribute("Classes"));
            Assert.Equal("True", (string?)button.Attribute("ToolTip.ShowOnDisabled"));
        }
        // Only the four controls (and the counters, which are not focusable) are in the bar: no 3x3 grid is left.
        Assert.DoesNotContain(bar.Descendants(), element => element.Name.LocalName == "Grid");
    }

    [Fact]
    public async Task TheHostedBarFollowsTheModesAndTheCompactRule()
    {
        using ReaderArchiveTestFixture archive = new();
        ArchivedThreadView thread = await archive.CaptureAsync(new ResourceId("offline", "reader", "1"), "Navigation fixture",
            [new("1", 0, PostFieldPresence.All, Html: "First post"), new("2", 1, PostFieldPresence.All, Html: "Second post")]);
        archive.Run(page =>
        {
            Window window = new() { Content = page, Width = 1100, Height = 750, Classes = { MotionPolicy.OffClass } };
            try
            {
                window.Show();
                ReaderArchiveTestFixture.Load(page, thread);
                page.ApplyReaderMediaPreferences(ReaderMediaPreferences.From(new Config { ReaderMediaSidebar = false }));
                page.SetReaderScope([thread.Summary.Id]);
                page.ReaderNavigationMode = Config.ReaderNavigationModeAlways;
                Assert.False(Find<WrapPanel>(page, "ReaderNavigation").IsEffectivelyVisible);

                window.Content = page.DetachReader();
                window.PlatformImpl!.Activated?.Invoke();
                Settle();
                var navigation = Find<WrapPanel>(window, "ReaderNavigation");
                var bar = Find<Border>(window, "ReaderNavigationBar");
                var threadPosition = Find<TextBlock>(window, "ReaderThreadPosition");
                var postPosition = Find<TextBlock>(window, "ReaderPostPosition");
                Assert.True(bar.IsEffectivelyVisible && navigation.IsHitTestVisible);
                Assert.Equal("Reader navigation", AutomationProperties.GetName(bar));
                AssertCounter(threadPosition, "1 / 1", "Thread 1 of 1");
                AssertCounter(postPosition, "1 / 2", "Post 1 of 2");

                var nextPost = Find<Button>(window, "ReaderNextPost");
                Point next = nextPost.TranslatePoint(new Rect(nextPost.Bounds.Size).Center, window)!.Value;
                window.MouseDown(next, MouseButton.Left);
                window.MouseUp(next, MouseButton.Left);
                BooruComponentDispatcher.Wait(page.ReaderNavigationTask);
                Assert.Equal("2", page.CurrentReaderPostId);
                Assert.True(Find<ListBox>(window, "ArchivePosts").IsKeyboardFocusWithin);
                window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
                BooruComponentDispatcher.Wait(page.ReaderNavigationTask);
                Assert.Equal("1", page.CurrentReaderPostId);
                foreach (string id in NavigationArrowIds)
                {
                    Button arrow = Find<Button>(window, id);
                    Assert.False(arrow.IsTabStop);
                    Assert.False(arrow.Focus());
                }

                page.ReaderNavigationShowPosition = false;
                Settle();
                Assert.False(threadPosition.IsEffectivelyVisible || postPosition.IsEffectivelyVisible);
                page.ReaderNavigationShowPosition = true;
                Settle();
                Assert.True(threadPosition.IsEffectivelyVisible && postPosition.IsEffectivelyVisible);
                BooruReviewTestPlatform.Resize(window, 620);
                Assert.True(bar.IsEffectivelyVisible);
                Assert.False(threadPosition.IsEffectivelyVisible || postPosition.IsEffectivelyVisible);
                BooruReviewTestPlatform.Resize(window, 1100);
                Assert.True(threadPosition.IsEffectivelyVisible && postPosition.IsEffectivelyVisible);

                var tabs = Find<TabControl>(window, "ArchiveContentTabs");
                object? posts = tabs.SelectedItem;
                tabs.SelectedItem = Find<TabItem>(window, "ArchiveMediaTab");
                Settle();
                Assert.False(bar.IsEffectivelyVisible || navigation.IsHitTestVisible);
                tabs.SelectedItem = posts;
                Settle();
                Assert.True(bar.IsEffectivelyVisible && navigation.IsHitTestVisible);

                // NAVBAR-06: Always stays visible and interactive after the idle delay with the pointer away.
                page.ReaderNavigationIdleDelayMs = ReaderNavigationLayout.IdleHideDelayMsMinimum;
                window.MouseMove(new Point(60, 60));
                page.NotifyReaderPointerMoved(new Point(60, 60));
                BooruComponentDispatcher.Wait(Task.Delay(2 * page.ReaderNavigationIdleDelayMs, BooruComponentDispatcher.Cancellation));
                Settle();
                Assert.True(bar.IsEffectivelyVisible && navigation.IsHitTestVisible);
                Assert.Equal(1, navigation.Opacity);

                page.ReaderNavigationMode = Config.ReaderNavigationModeOff;
                Settle();
                Assert.False(bar.IsEffectivelyVisible || navigation.IsHitTestVisible);
                Assert.True(page.TryNavigateReaderByKey(new KeyEventArgs { Key = Key.Down, PhysicalKey = PhysicalKey.ArrowDown }));
                BooruComponentDispatcher.Wait(page.ReaderNavigationTask);
                Assert.Equal("2", page.CurrentReaderPostId);
                Assert.False(bar.IsEffectivelyVisible || navigation.IsHitTestVisible);

                page.ReaderNavigationMode = Config.ReaderNavigationModeAuto;
                Assert.True(navigation.IsHitTestVisible);
                BooruComponentDispatcher.WaitUntil(() => !navigation.IsHitTestVisible, "idle reader navigation to stop taking pointer input");
                Assert.Equal(0, navigation.Opacity);
                page.ShowReaderNavigation();
                Settle();
                Assert.True(bar.IsEffectivelyVisible && navigation.IsHitTestVisible);
                AssertCounter(postPosition, "2 / 2", "Post 2 of 2");
            }
            finally { window.Close(); }

            void Settle()
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            }
        }, hosted: true);

        static T Find<T>(Visual root, string id) where T : Control =>
            Assert.Single(root.GetVisualDescendants().OfType<T>(), control => AutomationProperties.GetAutomationId(control) == id);

        static void AssertCounter(TextBlock counter, string text, string sentence)
        {
            Assert.True(counter.IsEffectivelyVisible);
            Assert.Equal(text, counter.Text);
            Assert.Equal(sentence, AutomationProperties.GetName(counter));
            Assert.Equal(sentence, ToolTip.GetTip(counter));
            Assert.Equal(AutomationLiveSetting.Off, AutomationProperties.GetLiveSetting(counter));
        }
    }

    [Fact]
    public void TheFadeStyleStaysAndThe108DipCrossIsGone()
    {
        string styles = Source("Desktop/WorkspaceStyles.axaml");
        Assert.Contains("WrapPanel.reader-cross", styles);
        Assert.DoesNotContain("arrow cross", styles);
        Assert.DoesNotContain("NavigationSize", Source("Desktop/ReaderNavigationLayout.cs"));
    }

    // ---- texts ----

    private static readonly string[] BarKeys =
    {
        "reader.nav.name", "reader.nav.threadPosition", "reader.nav.postPosition", "settings.reader.navigation", "settings.reader.navigation.hint",
        "settings.reader.navigation.auto", "settings.reader.navigation.always", "settings.reader.navigation.off",
        "settings.reader.navigationPosition", "settings.reader.navigationPosition.hint", "settings.controlsIdleHideHint"
    };

    public static IEnumerable<object[]> Languages() => new[] { "en", "de" }.Concat(Texts.BundledPackCodes()).Select(code => new object[] { code });

    [Fact]
    public void AllTwelveLanguagesAreCovered() => Assert.Equal(12, Languages().Count());

    [Theory]
    [MemberData(nameof(Languages))]
    public void EveryLanguageTranslatesTheBarTextsWithTheirPlaceholders(string language)
    {
        Texts.SetLanguage(language);
        foreach (string key in BarKeys)
            Assert.False(string.IsNullOrWhiteSpace(Texts.T(key)) || Texts.T(key) == key, language + ": " + key);
        foreach (string key in new[] { "reader.nav.threadPosition", "reader.nav.postPosition" })
        {
            string sentence = Texts.T(key, "N3", "M27");
            Assert.Contains("N3", sentence);
            Assert.Contains("M27", sentence);
            Assert.DoesNotContain("{", sentence);
        }
        Assert.DoesNotContain("arrow cross", Texts.T("settings.controlsIdleHideHint", 2000, 500, 30000), StringComparison.OrdinalIgnoreCase);
        // The three options are three different words.
        Assert.Equal(3, new[] { "auto", "always", "off" }.Select(option => Texts.T("settings.reader.navigation." + option)).Distinct().Count());
        if (language is not ("en" or "de"))
            foreach (string key in new[] { "reader.nav.name", "settings.reader.navigation.auto", "settings.reader.navigation.always", "settings.reader.navigation.off", "settings.reader.navigationPosition" })
                Assert.NotEqual(BuiltInCatalog.English[key], Texts.T(key));
    }

    [Fact]
    public void TheEnglishAndGermanTextsAreThoseOfTheSpecAndGermanAddressesNobodyFormally()
    {
        Assert.Equal("Reader navigation", BuiltInCatalog.English["reader.nav.name"]);
        Assert.Equal("Thread {0} of {1}", BuiltInCatalog.English["reader.nav.threadPosition"]);
        Assert.Equal("Post {0} of {1}", BuiltInCatalog.English["reader.nav.postPosition"]);
        Assert.Equal("Hide automatically", BuiltInCatalog.English["settings.reader.navigation.auto"]);
        Assert.Equal("Always show", BuiltInCatalog.English["settings.reader.navigation.always"]);
        Assert.Equal("Off (keyboard only)", BuiltInCatalog.English["settings.reader.navigation.off"]);
        Assert.Equal("Show position in reader navigation", BuiltInCatalog.English["settings.reader.navigationPosition"]);
        Assert.Equal("Reader-Navigation", BuiltInCatalog.German["reader.nav.name"]);
        Assert.Equal("Thread {0} von {1}", BuiltInCatalog.German["reader.nav.threadPosition"]);
        Assert.Equal("Post {0} von {1}", BuiltInCatalog.German["reader.nav.postPosition"]);
        Assert.Equal("Automatisch ausblenden", BuiltInCatalog.German["settings.reader.navigation.auto"]);
        Assert.Equal("Immer anzeigen", BuiltInCatalog.German["settings.reader.navigation.always"]);
        Assert.Equal("Aus (nur Tastatur)", BuiltInCatalog.German["settings.reader.navigation.off"]);
        Assert.Equal("Position in der Reader-Navigation anzeigen", BuiltInCatalog.German["settings.reader.navigationPosition"]);
        foreach (string key in BarKeys)
        {
            string german = BuiltInCatalog.German[key];
            Assert.DoesNotContain(" Sie ", " " + german + " ");
            Assert.DoesNotContain(" Ihr", " " + german);
        }
    }
}

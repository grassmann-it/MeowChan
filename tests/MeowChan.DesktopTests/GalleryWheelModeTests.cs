using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.LogicalTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Audit V10: the gallery's mouse wheel setting (Config.GalleryWheelMode, "Zoom" by default or "Browse") and Ctrl inverting it. What is
// tested here: the setting (default, normalising, profile round trip, every copy, the portable export), the decision table of
// GalleryWheel, the viewer's reaction to a real wheel event on its picture surface (the picture only: the filmstrip and the video
// controls are other controls and never reach it), the "⋯" row, and the settings row with its texts in the twelve languages.
[Collection("Gallery viewer construction")]
public sealed class GalleryWheelModeTests : IDisposable
{
    public GalleryWheelModeTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static string Source(string relative, [System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative)));

    // ---- the setting ----

    [Fact]
    public void TheWheelZoomsByDefaultAsItAlwaysDid()
    {
        Assert.Equal("Zoom", new Config().GalleryWheelMode);
        Assert.Equal("Zoom", JsonSerializer.Deserialize("{}", ConfigJsonContext.Default.Config)!.GalleryWheelMode);
        Assert.False(GalleryWheel.IsBrowse(new Config().GalleryWheelMode));
        Assert.Equal("Zoom", SettingsTransfer.CreatePortableSnapshot(new Config()).GalleryWheelMode);
    }

    [Theory]
    [InlineData("Zoom", "Zoom")]
    [InlineData("Browse", "Browse")]
    [InlineData("browse", "Browse")]
    [InlineData(" BROWSE ", "Browse")]
    [InlineData("zoom", "Zoom")]
    [InlineData("", "Zoom")]
    [InlineData("Scroll", "Zoom")]
    [InlineData("1", "Zoom")]
    [InlineData(null, "Zoom")]
    public void AnUnknownValueReadsAsZoom(string? stored, string expected)
    {
        Assert.Equal(expected, new Config { GalleryWheelMode = stored! }.GalleryWheelMode);
        Assert.Equal(expected, Config.NormalizeGalleryWheelMode(stored));
        Config loaded = JsonSerializer.Deserialize("{\"GalleryWheelMode\":" + JsonSerializer.Serialize(stored) + "}", ConfigJsonContext.Default.Config)!;
        Assert.Equal(expected, loaded.GalleryWheelMode);
        Assert.Equal(expected == "Browse", GalleryWheel.IsBrowse(stored));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("[\"Browse\"]")]
    public void ANonTextValueInTheFileReadsAsZoom(string raw)
    {
        Config loaded = JsonSerializer.Deserialize("{\"GalleryWheelMode\":" + raw + ",\"ControlsIdleHideMs\":1500}", ConfigJsonContext.Default.Config)!;
        Assert.Equal("Zoom", loaded.GalleryWheelMode);
        Assert.Equal(1500, loaded.ControlsIdleHideMs); // the rest of the file is read
    }

    [Theory]
    [InlineData("Browse")]
    [InlineData("Zoom")]
    public void TheChoiceRoundTripsThroughTheProfileAndEveryCopy(string mode)
    {
        using TempDirectory directory = new();
        Config original = new() { GalleryWheelMode = mode, DownloadPath = Path.Combine(directory.Path, "media") };
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
            Assert.Equal(mode, candidate.GalleryWheelMode);
            candidate.Validate();
        }
        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.Path, "settings.json")));
        Assert.Equal(mode, saved.RootElement.GetProperty(nameof(Config.GalleryWheelMode)).GetString());
    }

    [Fact]
    public void TheExportCarriesTheChoiceAndAnImportOfTheExportRestoresIt()
    {
        Config source = new() { GalleryWheelMode = "Browse" };
        Assert.Equal("Browse", SettingsTransfer.CreatePortableSnapshot(source).GalleryWheelMode);
        JsonObject exported = SettingsTransfer.Export(source);
        Assert.Equal("Browse", (string?)exported[nameof(Config.GalleryWheelMode)]);
        Config imported = JsonSerializer.Deserialize(exported.ToJsonString(), ConfigJsonContext.Default.Config)!;
        Assert.Equal("Browse", imported.GalleryWheelMode);
        Config target = new();
        target.CopySettingsFieldsFrom(imported);
        Assert.Equal("Browse", target.GalleryWheelMode);
        Assert.Contains("GalleryWheelMode = source.GalleryWheelMode,", Source("Desktop/Settings/SettingsTransfer.cs"));
    }

    // ---- the decision table ----

    [Theory]
    [InlineData("Zoom", KeyModifiers.None, "Zoom")]
    [InlineData("Zoom", KeyModifiers.Control, "Browse")]
    [InlineData("Browse", KeyModifiers.None, "Browse")]
    [InlineData("Browse", KeyModifiers.Control, "Zoom")]
    [InlineData("Zoom", KeyModifiers.Shift, "Zoom")]
    [InlineData("Browse", KeyModifiers.Shift, "Browse")]
    [InlineData("Zoom", KeyModifiers.Control | KeyModifiers.Shift, "Zoom")]   // Ctrl alone inverts, as everywhere else (ItemActivation.IsZoomWheel)
    [InlineData("Browse", KeyModifiers.Control | KeyModifiers.Alt, "Browse")]
    [InlineData("Scroll", KeyModifiers.None, "Zoom")]                          // an unknown value is the default
    [InlineData("Scroll", KeyModifiers.Control, "Browse")]
    [InlineData(null, KeyModifiers.None, "Zoom")]
    public void TheModeSaysWhatTheWheelDoesAndCtrlHoldsTheOtherOne(string? mode, KeyModifiers modifiers, string expected) =>
        Assert.Equal(expected, GalleryWheel.Resolve(mode, modifiers).ToString());

    [Fact]
    public void BrowsingGoesBackOnWheelUpAndOnOnWheelDownLikeTheFilmstrip()
    {
        Assert.Equal(-1, GalleryWheel.BrowseDelta(1));
        Assert.Equal(-1, GalleryWheel.BrowseDelta(5));   // several steps of one event still move by one medium, as a key does
        Assert.Equal(1, GalleryWheel.BrowseDelta(-1));
        Assert.Equal(1, GalleryWheel.BrowseDelta(-3));
        Assert.Equal(0, GalleryWheel.BrowseDelta(0));
        // A touchpad's small deltas add up to whole steps before they move anything.
        WheelSteps steps = new();
        DateTime now = DateTime.UtcNow;
        Assert.Equal(0, GalleryWheel.BrowseDelta(steps.Add(0.3, now)));
        Assert.Equal(0, GalleryWheel.BrowseDelta(steps.Add(0.3, now.AddMilliseconds(10))));
        Assert.Equal(0, GalleryWheel.BrowseDelta(steps.Add(0.3, now.AddMilliseconds(20))));
        Assert.Equal(-1, GalleryWheel.BrowseDelta(steps.Add(0.3, now.AddMilliseconds(30))));
    }

    // ---- the viewer: a wheel turn on the picture surface ----

    private static T Inert<T>() where T : class => DispatchProxy.Create<T, GalleryViewerConstructionTests.InertProxy>();

    private static void WithViewer(Action<GalleryViewer, Grid, List<int>> body) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(Inert<ICursorFactory>());
        GalleryViewer viewer = new();
        viewer.SetPosition("2 / 3", true, true);
        List<int> moves = new();
        viewer.MoveRequested += moves.Add;
        Grid viewport = viewer.GetLogicalDescendants().OfType<Grid>().Single(grid => grid.Name == "GalleryViewport");
        body(viewer, viewport, moves);
    });

    private static PointerWheelEventArgs Wheel(Control target, double deltaY, KeyModifiers modifiers = KeyModifiers.None) =>
        new(target, new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, true), target, new Point(5, 5), 0,
            new PointerPointProperties(), modifiers, new Vector(0, deltaY)) { RoutedEvent = InputElement.PointerWheelChangedEvent };

    [Fact]
    public void InZoomModeThePlainWheelNeverMovesToAnotherMediumAndCtrlWheelDoes() => WithViewer((viewer, viewport, moves) =>
    {
        Assert.Equal("Zoom", viewer.WheelMode);
        PointerWheelEventArgs plain = Wheel(viewport, 1);
        viewport.RaiseEvent(plain);
        Assert.True(plain.Handled);       // the wheel is the picture's, as it always was
        Assert.Empty(moves);
        viewport.RaiseEvent(Wheel(viewport, 1, KeyModifiers.Control));
        viewport.RaiseEvent(Wheel(viewport, -1, KeyModifiers.Control));
        Assert.Equal(new[] { -1, 1 }, moves);
    });

    [Fact]
    public void InBrowseModeThePlainWheelMovesAndCtrlWheelZoomsInstead() => WithViewer((viewer, viewport, moves) =>
    {
        viewer.ConfigureWheelMode("Browse");
        Assert.Equal("Browse", viewer.WheelMode);
        PointerWheelEventArgs up = Wheel(viewport, 1);
        viewport.RaiseEvent(up);
        PointerWheelEventArgs down = Wheel(viewport, -1);
        viewport.RaiseEvent(down);
        Assert.True(up.Handled && down.Handled);
        Assert.Equal(new[] { -1, 1 }, moves);                         // up: the previous medium, down: the next
        moves.Clear();
        PointerWheelEventArgs ctrl = Wheel(viewport, 1, KeyModifiers.Control);
        viewport.RaiseEvent(ctrl);
        Assert.True(ctrl.Handled);                                    // the zoom path (nothing to zoom without a medium)
        Assert.Empty(moves);
    });

    [Fact]
    public void BrowsingStopsAtTheEndsLikeTheArrowKeys() => WithViewer((viewer, viewport, moves) =>
    {
        viewer.ConfigureWheelMode("Browse");
        viewer.SetPosition("1 / 3", false, true);
        viewport.RaiseEvent(Wheel(viewport, 1));    // no previous medium
        Assert.Empty(moves);
        viewport.RaiseEvent(Wheel(viewport, -1));
        Assert.Equal(new[] { 1 }, moves);
        moves.Clear();
        viewer.SetPosition("3 / 3", true, false);
        viewport.RaiseEvent(Wheel(viewport, -1));   // no next medium
        Assert.Empty(moves);
    });

    [Fact]
    public void ASmallTouchpadDeltaAddsUpAndAnUnknownModeFallsBackToZoom() => WithViewer((viewer, viewport, moves) =>
    {
        viewer.ConfigureWheelMode("Browse");
        for (int i = 0; i < 3; i++) viewport.RaiseEvent(Wheel(viewport, -0.3));
        Assert.Empty(moves);
        viewport.RaiseEvent(Wheel(viewport, -0.3));
        Assert.Equal(new[] { 1 }, moves);
        viewer.ConfigureWheelMode("sideways");
        Assert.Equal("Zoom", viewer.WheelMode);
        Assert.False(viewer.WheelBrowseToggle.IsChecked);
        moves.Clear();
        viewport.RaiseEvent(Wheel(viewport, -1));
        Assert.Empty(moves);
    });

    [Fact]
    public void TheFilmstripKeepsItsOwnWheelWhateverTheMode() => WithViewer((viewer, viewport, moves) =>
    {
        // The strip is a control of its own beside the picture surface, never inside it, and the picture's wheel handler is on the
        // surface only: a wheel turn over the strip cannot browse or zoom the medium whichever mode and modifier.
        Assert.DoesNotContain(viewer.Filmstrip.GetLogicalAncestors(), ancestor => ReferenceEquals(ancestor, viewport));
        Assert.DoesNotContain(viewport.GetLogicalDescendants(), child => ReferenceEquals(child, viewer.Filmstrip));
        string viewerCode = Source("Desktop/GalleryViewer.cs");
        Assert.Contains("_viewport.PointerWheelChanged += OnViewWheel;", viewerCode);
        Assert.Contains("_nativeVideoOverlay.PointerWheelChanged += OnViewWheel;", viewerCode);
        Assert.Contains("PointerWheelChanged += OnWheel;", Source("Desktop/Controls/GalleryFilmstrip.cs"));
        Assert.Contains("AdjustVolumeBy(Math.Sign(e.Delta.Y) * VolumeWheelStep);", Source("Desktop/GalleryVideoPlayer.Transport.cs"));
    });

    [Fact]
    public void OverTheVideoControlsTheWheelStaysTheirsInBothModes()
    {
        // The guard that keeps the transport's wheel runs before the mode matters; only the surface itself can browse or zoom.
        string handler = Source("Desktop/GalleryViewer.ViewTransform.cs");
        int start = handler.IndexOf("private void OnViewWheel", StringComparison.Ordinal);
        string body = handler[start..handler.IndexOf("private void OnViewPointerReleased", start, StringComparison.Ordinal)];
        int guard = body.IndexOf("if (!IsOverVideoSurface(e.Source, root)", StringComparison.Ordinal);
        int browse = body.IndexOf("BrowseByWheel(e.Delta.Y)", StringComparison.Ordinal);
        int zoom = body.IndexOf("Zoom(GalleryGeometry.WheelFactor", StringComparison.Ordinal);
        Assert.True(guard > 0 && guard < browse && browse < zoom, body);
    }

    // ---- the "⋯" row ----

    [Fact]
    public void TheMenuRowShowsTheModeAndChangesIt() => WithViewer((viewer, viewport, moves) =>
    {
        ToggleButton row = viewer.WheelBrowseToggle;
        Assert.Contains(row, viewer.OverflowItems);
        Assert.Same(viewer.OverflowMenuContent, row.Parent);
        Assert.False(row.IsChecked);
        Assert.Equal("Mouse wheel browses media", GalleryViewer.LabelOf(row));
        // Beside the other view choices: directly after the filmstrip row.
        Panel menu = Assert.IsAssignableFrom<Panel>(viewer.OverflowMenuContent);
        int index = menu.Children.IndexOf(row);
        Assert.True(index > 0);
        Assert.Contains("Filmstrip", AutomationNameOf(menu.Children[index - 1]) + " " + menu.Children[index - 1].Name);
        List<string> changed = new();
        viewer.WheelModeChanged += changed.Add;
        row.IsChecked = true; row.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("Browse", viewer.WheelMode);
        row.IsChecked = false; row.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("Zoom", viewer.WheelMode);
        Assert.Equal(new[] { "Browse", "Zoom" }, changed);
        // What the shell applies is not reported back as a change.
        viewer.ConfigureWheelMode("Browse");
        Assert.Equal(new[] { "Browse", "Zoom" }, changed);
        Assert.True(row.IsChecked);
    });

    private static string AutomationNameOf(Control control) => Avalonia.Automation.AutomationProperties.GetName(control) ?? string.Empty;

    // ---- the settings row, the shell and the texts ----

    [Fact]
    public void TheSettingsRowIsAUniformChoiceRowOfTheReadingAndMediaGroupWithAliasesAndReset()
    {
        System.Xml.Linq.XDocument markup = System.Xml.Linq.XDocument.Parse(Source("Desktop/Views/Settings/SettingsEditor.axaml"));
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        System.Xml.Linq.XElement group = Assert.Single(markup.Descendants(), e => (string?)e.Attribute(x + "Name") == "SettingsReadingMediaGroup");
        System.Xml.Linq.XElement input = Assert.Single(markup.Descendants(), e => (string?)e.Attribute(x + "Name") == "GalleryWheelModeInput");
        Assert.Equal("ComboBox", input.Name.LocalName);
        Assert.Contains(input.Ancestors(), ancestor => ReferenceEquals(ancestor, group));
        Assert.Equal("{Binding SelectedGalleryWheelMode, Mode=TwoWay}", (string?)input.Attribute("SelectedItem"));
        Assert.Equal("{Binding GalleryWheelModeChoices}", (string?)input.Attribute("ItemsSource"));
        Assert.Contains("Hint", input.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.HelpText").Value);
        System.Xml.Linq.XElement row = input.Ancestors().First(a => a.Name.LocalName == "Grid" && ((string?)a.Attribute("Classes"))?.Contains("settings-row") == true);
        Assert.Equal("*,280", (string?)row.Attribute("ColumnDefinitions"));
        Assert.Equal("settings-divider", (string?)row.Parent!.Attribute("Classes"));
        Assert.Contains("[\"GalleryWheelMode\"] =", Source("Desktop/Views/Settings/SettingsSearchAliases.cs"));
        Assert.Contains("Choice(GalleryWheelModeInput, () => SelectedGalleryWheelMode?.Value,", Source("Desktop/Views/Settings/SettingsEditor.RowMenu.cs"));
        string viewer = Source("Desktop/Views/Settings/SettingsEditor.Viewer.cs");
        Assert.Contains("RefreshGalleryWheelModeChoices(config.GalleryWheelMode);", viewer);                                     // start, discard, reset
        Assert.Contains("candidate.GalleryWheelMode = SelectedGalleryWheelMode?.Value ?? _config.GalleryWheelMode;", viewer);   // save
        Assert.Contains("nameof(GalleryWheelModeLabel), nameof(GalleryWheelModeHint)", viewer);                                  // a language change
        Assert.Contains("nameof(SelectedGalleryWheelMode)", Source("Desktop/Views/Settings/SettingsEditor.axaml.cs"));          // and is no edit
        // The row's default in the reset menu is the default of the setting.
        Assert.Equal("Zoom", new Config().GalleryWheelMode);
    }

    [Fact]
    public void TheShellAppliesTheSettingAndSavesTheMenuChoiceAsAViewPreference()
    {
        Assert.Contains("_gallery?.ConfigureWheelMode(_config.GalleryWheelMode);", Source("Desktop/Shell/MainWindow.GalleryPreferences.cs"));
        Assert.Contains("_gallery.WheelModeChanged += mode => OnGalleryWheelModeChanged(preferenceOwner, mode);", Source("Desktop/Shell/MainWindow.Gallery.cs"));
        string shell = Source("Desktop/Shell/MainWindow.GalleryWheel.cs");
        Assert.Contains("_session.SaveViewPreferencesAsync(config => config.GalleryWheelMode = mode)", shell);
        Assert.Contains("_settingsEditor?.RefreshConfirmedGalleryWheelMode(mode);", shell);
    }

    private static readonly string[] Keys =
    {
        "settings.galleryWheel", "settings.galleryWheel.hint", "settings.galleryWheel.zoom", "settings.galleryWheel.browse",
        "gallery.wheelBrowse", "gallery.wheelBrowse.help"
    };

    [Fact]
    public void EveryTextExistsInEnglishGermanAndTheTenBundledPacks()
    {
        foreach (string key in Keys)
        {
            Assert.True(BuiltInCatalog.English.TryGetValue(key, out string? english) && english.Length > 0, key);
            Assert.True(BuiltInCatalog.German.TryGetValue(key, out string? german) && german.Length > 0 && german != english, key);
        }
        string[] codes = Texts.BundledPackCodes().ToArray();
        Assert.Equal(10, codes.Length);
        foreach (string code in codes)
        {
            using JsonDocument pack = JsonDocument.Parse(Source("Desktop/Localization/Packs/" + code + ".json"));
            JsonElement strings = pack.RootElement.GetProperty("strings");
            foreach (string key in Keys)
            {
                Assert.True(strings.TryGetProperty(key, out JsonElement value), code + " " + key);
                Assert.False(string.IsNullOrWhiteSpace(value.GetString()), code + " " + key);
                Assert.NotEqual(BuiltInCatalog.English[key], value.GetString());
            }
        }
    }

    [Fact]
    public void TheSettingsHintNamesBothModesAndTheMenuRowTheCtrlInversion()
    {
        string hint = BuiltInCatalog.English["settings.galleryWheel.hint"];
        Assert.Contains("Ctrl+wheel", hint);
        Assert.Contains("filmstrip", hint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Ctrl+wheel", BuiltInCatalog.English["gallery.wheelBrowse.help"]);
        Assert.Contains("Strg+Rad", BuiltInCatalog.German["settings.galleryWheel.hint"]);
    }
}

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Tile sizes (S/M/L) of the tagging grid and the archive's media tiles (audit proposals V2 and V3), on the media page's model: the saved setting,
// the shared tile size input, the measures per size and the new defaults of revision 20. Plain objects and a source scan of the wiring; no window.
[Collection("Localization")]
public sealed class TileSizeTests : IDisposable
{
    public TileSizeTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static string Read(string relative, [CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative)));

    // ---- the setting ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheArchiveTileSizeDefaultsToMediumAndReadsDamagedTextAsMedium()
    {
        Config config = new();
        Assert.Equal("Medium", config.ArchiveMediaTileSize);
        config.ArchiveMediaTileSize = "Large";
        Assert.Equal("Large", config.ArchiveMediaTileSize);
        foreach (string damaged in new[] { "", "   ", "huge", "42" }) { config.ArchiveMediaTileSize = damaged; Assert.Equal("Medium", config.ArchiveMediaTileSize); }
        config.ArchiveMediaTileSize = null!;
        Assert.Equal("Medium", config.ArchiveMediaTileSize);
        config.ArchiveMediaTileSize = " small ";
        Assert.Equal("Small", config.ArchiveMediaTileSize);
    }

    [Theory]
    [InlineData("Small", "Small")]
    [InlineData("s", "Small")]
    [InlineData("LARGE", "Large")]
    [InlineData("l", "Large")]
    [InlineData("Medium", "Medium")]
    [InlineData("m", "Medium")]
    [InlineData("", "Medium")]
    [InlineData(null, "Medium")]
    [InlineData("nonsense", "Medium")]
    public void TheSavedSpellingParsesAndUnknownTextMeansMedium(string? text, string expected) =>
        Assert.Equal(expected, MediaTileSizes.Parse(text).ToString());

    [Fact]
    public void TheArchiveTileSizeSurvivesSaveLoadCloneCopyAndTheSettingsTransfer()
    {
        using TempDirectory directory = new();
        Config config = Config.LoadConfig(directory.Path);
        Assert.Equal("Medium", config.ArchiveMediaTileSize);
        config.ArchiveMediaTileSize = nameof(MediaTileSize.Large);
        config.SaveConfig();
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Equal("Large", loaded.ArchiveMediaTileSize);
        Assert.Equal("Large", loaded.Clone().ArchiveMediaTileSize);
        Config target = new();
        target.CopyFrom(loaded);
        Assert.Equal("Large", target.ArchiveMediaTileSize);
        JsonObject export = SettingsTransfer.Export(loaded, "1.1.0-rc.1+build42");
        Assert.Equal("Large", export[nameof(Config.ArchiveMediaTileSize)]!.GetValue<string>());
        Config restored = JsonSerializer.Deserialize(export.ToJsonString(), ConfigJsonContext.Default.Config)!;
        Assert.Equal("Large", restored.ArchiveMediaTileSize);
    }

    [Fact]
    public void ADamagedSavedArchiveTileSizeDoesNotMakeTheFileInvalid()
    {
        using TempDirectory directory = new();
        Config config = Config.LoadConfig(directory.Path);
        config.SaveConfig();
        string path = Directory.GetFiles(directory.Path, "*.json", SearchOption.AllDirectories).First(file => File.ReadAllText(file).Contains("ArchiveMediaTileSize"));
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"ArchiveMediaTileSize\": \"Medium\"", "\"ArchiveMediaTileSize\": \"gigantic\""));
        Assert.Equal("Medium", Config.LoadConfig(directory.Path).ArchiveMediaTileSize);
    }

    [Fact]
    public void TheMediaPageSettingKeepsTheSizeInTheLayoutAndTheTaggingGridSharesIt()
    {
        // One tile size for the media page and the tagging grid: it lives in the media layout and a round trip keeps it.
        MediaPageLayout layout = MediaPageLayout.Standard with { TileSize = MediaTileSize.Large };
        Assert.Equal(MediaTileSize.Large, MediaPageLayout.Parse(layout.Format(), false).TileSize);
        string shell = Read("Desktop/Shell/MainWindow.TaggingPage.cs");
        Assert.Contains("ReadMediaTileSize = () => CurrentMediaLayout().TileSize,", shell);
        Assert.Contains("ChooseMediaTileSize = ChooseSharedTileSize", shell);
        Assert.Contains("MediaPageLayout.Parse(config.MediaLayout, config.MediaBooruMode) with { TileSize = size }", shell);
        Assert.Contains("_mediaPage?.ApplySavedTileSize(size);", shell);
        string grid = Read("Desktop/Views/Tagging/TaggingPage.Grid.cs");
        Assert.Contains("TileSizeInput.Install(_grid, TileSizeCommands.Media,", grid);
        Assert.Contains("MediaTileMetrics.Standard", grid);
    }

    // ---- the shared input ----------------------------------------------------------------------------------------------------

    [Fact]
    public void TheInputStepsWithinTheThreeSizesResetsToMediumAndSavesOncePerChange()
    {
        MediaTileSize size = MediaTileSize.Medium;
        List<MediaTileSize> applied = new();
        TileSizeInput input = TileSizeInput.Install(new Border(), TileSizeCommands.Media, () => size, next => { size = next; applied.Add(next); return true; });
        Assert.True(input.StepBy(1));
        Assert.Equal(MediaTileSize.Large, size);
        Assert.False(input.StepBy(1), "the end stays where it is and saves nothing");
        Assert.True(input.StepBy(-5));
        Assert.Equal(MediaTileSize.Medium, size);
        Assert.True(input.StepBy(-1));
        Assert.False(input.StepBy(-1));
        Assert.Equal(MediaTileSize.Small, size);
        Assert.True(input.Reset());
        Assert.Equal(MediaTileSize.Medium, size);
        Assert.False(input.Reset(), "Medium already: no second save");
        Assert.False(input.StepBy(0));
        Assert.Equal(new[] { MediaTileSize.Large, MediaTileSize.Medium, MediaTileSize.Small, MediaTileSize.Medium }, applied);
    }

    [Fact]
    public void ARefusedChangeLeavesTheSizeAlone()
    {
        MediaTileSize size = MediaTileSize.Medium;
        TileSizeInput input = TileSizeInput.Install(new Border(), TileSizeCommands.Archive, () => size, _ => false);
        Assert.False(input.StepBy(1));
        Assert.Equal(MediaTileSize.Medium, size);
    }

    private static KeyEventArgs Key(Key key, KeyModifiers modifiers) => new() { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers };

    [Theory]
    [InlineData(Avalonia.Input.Key.OemPlus, 1)]
    [InlineData(Avalonia.Input.Key.Add, 1)]
    [InlineData(Avalonia.Input.Key.OemMinus, -1)]
    [InlineData(Avalonia.Input.Key.Subtract, -1)]
    public void CtrlPlusAndMinusStepTheSizeOnEverySurfaceThatInstallsTheInput(Key key, int direction)
    {
        KeyModifiers primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        foreach (TileSizeCommands commands in new[] { TileSizeCommands.Media, TileSizeCommands.Archive })
        {
            MediaTileSize size = MediaTileSize.Medium;
            Border surface = new();
            TileSizeInput.Install(surface, commands, () => size, next => { size = next; return true; });
            KeyEventArgs e = Key(key, primary);
            surface.RaiseEvent(e);
            Assert.True(e.Handled, commands.Larger);
            Assert.Equal(direction > 0 ? MediaTileSize.Large : MediaTileSize.Small, size);
        }
    }

    [Theory]
    [InlineData(Avalonia.Input.Key.D0)]
    [InlineData(Avalonia.Input.Key.NumPad0)]
    public void CtrlZeroSetsMediumAndOtherKeysAreLeftAlone(Key zero)
    {
        KeyModifiers primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        MediaTileSize size = MediaTileSize.Large;
        Border surface = new();
        TileSizeInput.Install(surface, TileSizeCommands.Archive, () => size, next => { size = next; return true; });
        KeyEventArgs reset = Key(zero, primary);
        surface.RaiseEvent(reset);
        Assert.True(reset.Handled);
        Assert.Equal(MediaTileSize.Medium, size);
        // No modifier, or another key with the modifier: the surface keeps its own meaning (typing, the reader's other keys).
        KeyEventArgs plain = Key(Avalonia.Input.Key.OemPlus, KeyModifiers.None);
        surface.RaiseEvent(plain);
        KeyEventArgs other = Key(Avalonia.Input.Key.A, primary);
        surface.RaiseEvent(other);
        Assert.False(plain.Handled);
        Assert.False(other.Handled);
        Assert.Equal(MediaTileSize.Medium, size);
    }

    [Fact]
    public void TheWheelHandlerSitsOnTheTunnelAndSwallowsCtrlWheelOnly()
    {
        string input = Read("Desktop/Controls/TileSizeInput.cs");
        Assert.Contains("surface.AddHandler(InputElement.PointerWheelChangedEvent, input.OnWheel, RoutingStrategies.Tunnel);", input);
        Assert.Contains("surface.AddHandler(InputElement.KeyDownEvent, input.OnKey, RoutingStrategies.Tunnel);", input);
        Assert.Contains("if (e.Handled || !ItemActivation.IsZoomWheel(e.KeyModifiers)) return;", input);
        // The three surfaces: media grid, tagging grid, archive tiles.
        Assert.Contains("TileSizeInput.Install(_grid, TileSizeCommands.Media,", Read("Desktop/Views/Media/MediaPage.Interaction.cs"));
        Assert.Contains("TileSizeInput.Install(_grid, TileSizeCommands.Media,", Read("Desktop/Views/Tagging/TaggingPage.Grid.cs"));
        Assert.Contains("TileSizeInput.Install(ArchiveMediaTiles, TileSizeCommands.Archive,", Read("Desktop/Views/Archive/ArchivePage.TileSize.cs"));
    }

    // ---- the archive tile at every size --------------------------------------------------------------------------------------

    [Fact]
    public void TheMediumArchiveTileIsTheTileAsItAlwaysWas()
    {
        ArchiveTileAppearance medium = ArchiveTileMetrics.For(MediaTileSize.Medium);
        Assert.Equal(MediaTilePanel.CellWidth, medium.CellWidth);
        Assert.Equal(MediaTilePanel.CellHeight, medium.CellHeight);
        Assert.Equal(125, medium.PreviewHeight);
        Assert.Equal(170, medium.PictureWidth);
        Assert.Equal(medium, ArchiveTileMetrics.Standard);
    }

    [Fact]
    public void TheArchiveTileGrowsWithTheSizeAndKeepsItsPartsInsideTheCell()
    {
        ArchiveTileAppearance small = ArchiveTileMetrics.For(MediaTileSize.Small), medium = ArchiveTileMetrics.For(MediaTileSize.Medium), large = ArchiveTileMetrics.For(MediaTileSize.Large);
        Assert.True(small.CellWidth < medium.CellWidth && medium.CellWidth < large.CellWidth);
        Assert.True(small.CellHeight < medium.CellHeight && medium.CellHeight < large.CellHeight);
        Assert.True(small.PreviewHeight < medium.PreviewHeight && medium.PreviewHeight < large.PreviewHeight);
        // The same column width as the media page's tile of the same size (one word, one width).
        Assert.Equal(MediaTileMetrics.For(MediaTileSize.Small, MediaTileCaption.All).CellWidth, small.CellWidth);
        Assert.Equal(MediaTileMetrics.For(MediaTileSize.Large, MediaTileCaption.All).CellWidth, large.CellWidth);
        foreach (ArchiveTileAppearance size in new[] { small, medium, large })
        {
            // Margins (2 x 8 dip) and the picture slot fit the cell; the rows under the slot keep their room at every size.
            Assert.True(size.PictureWidth + 16 <= size.CellWidth);
            Assert.True(size.PreviewHeight + 16 < size.CellHeight);
            Assert.Equal(ArchiveTileMetrics.BelowPreview, size.CellHeight - size.PreviewHeight);
        }
    }

    [Fact]
    public void TheTileSizeSetOnTheListIsInheritedByEveryTileInIt()
    {
        // The tile itself needs the platform (icons), so the inheritance is shown on a stand-in child: the property is inherited down the tree, and the
        // tile reacts to it in OnPropertyChanged (source below), so tiles realized now or later follow the list.
        StackPanel list = new();
        Border child = new();
        list.Children.Add(child);
        Assert.Equal(MediaTileSize.Medium, ArchiveMediaTileView.GetTileSize(child));
        foreach (MediaTileSize size in new[] { MediaTileSize.Large, MediaTileSize.Small, MediaTileSize.Medium })
        {
            ArchiveMediaTileView.SetTileSize(list, size);
            Assert.Equal(size, ArchiveMediaTileView.GetTileSize(child));
        }
        ArchiveMediaTileView.SetTileSize(list, MediaTileSize.Large);
        Border later = new();
        list.Children.Add(later);
        Assert.Equal(MediaTileSize.Large, ArchiveMediaTileView.GetTileSize(later));
        string tile = Read("Desktop/ArchiveMediaTileView.cs");
        Assert.Contains("inherits: true", tile);
        Assert.Contains("if (change.Property == TileSizeProperty) ApplyAppearance(ArchiveTileMetrics.For(change.GetNewValue<MediaTileSize>()));", tile);
        Assert.Contains("ApplyAppearance(ArchiveTileMetrics.Standard);", tile);
    }

    [Fact]
    public void TheTileViewKeepsItsBadgesStarAndSelectionCheckOnTheSlotAtEverySize()
    {
        string tile = Read("Desktop/ArchiveMediaTileView.cs");
        // The badges, the star and the check are arranged on the slot's corners (MarksOnSlot), not on the picture; a size only changes the cell, the slot
        // and the longest side of the picture, so they stay where they were at every size.
        Assert.Contains("preview.MarksOnSlot = true;", tile);
        Assert.Contains("_preview.Height = appearance.PreviewHeight;", tile);
        Assert.Contains("_preview.MaxWidth = appearance.PictureWidth;", tile);
        Assert.Contains("_image.MaxHeight = appearance.PreviewHeight;", tile);
        Assert.Contains("_cell.RowDefinitions = new RowDefinitions(", tile);
        Assert.DoesNotContain("preview.Height = 125", tile);
        string page = Read("Desktop/Views/Archive/ArchivePage.TileSize.cs");
        Assert.Contains("panel.ColumnWidth = appearance.CellWidth;", page);
        Assert.Contains("panel.ItemHeight = appearance.CellHeight;", page);
        Assert.Contains("panel.RestoreAnchor(kept.Index", page);
        // The saved size reaches the page through the shell and a change is saved as a view preference.
        string shell = Read("Desktop/Shell/MainWindow.Navigation.cs");
        Assert.Contains("_archivePage.MediaTileSizePreference = MediaTileSizes.Parse(_config.ArchiveMediaTileSize);", shell);
        Assert.Contains("current.ArchiveMediaTileSize = size.ToString()", shell);
    }

    // ---- the new defaults (revision 20) --------------------------------------------------------------------------------------

    public static IEnumerable<object[]> Platforms() => Enum.GetValues<ShortcutPlatform>().Select(platform => new object[] { platform });

    private static readonly string[] NewIds =
        { "media.tile-size.reset", "archive.media-tile-size.larger", "archive.media-tile-size.smaller", "archive.media-tile-size.reset" };

    [Theory, MemberData(nameof(Platforms))]
    public void TheTileSizeDefaultsAreActiveConflictFreeAndYielding(ShortcutPlatform platform)
    {
        Assert.True(EffectiveShortcutBindings.DefaultsRevision >= 20);
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(null, platform);
        Assert.Empty(bindings.Issues);
        string Keys(string id) => string.Join(" ", bindings.For(id).Select(g => string.Join("+", g.Modifiers.Append(g.Key))));
        Assert.Equal("Primary+OemPlus Primary+Add", Keys("archive.media-tile-size.larger"));
        Assert.Equal("Primary+OemMinus Primary+Subtract", Keys("archive.media-tile-size.smaller"));
        Assert.Equal("Primary+D0 Primary+NumPad0", Keys("archive.media-tile-size.reset"));
        Assert.Equal("Primary+D0 Primary+NumPad0", Keys("media.tile-size.reset"));
        foreach (string id in NewIds)
        {
            Assert.Contains(id, EffectiveShortcutBindings.YieldingDefaultCommandIds);
            Assert.True(EffectiveShortcutBindings.YieldsOn(id, platform), id);
        }
    }

    [Fact]
    public void TheContextsKeepTheArchiveTilesApartFromTheReadersKeysAndShareTheMediaSizeWithTheTaggingGrid()
    {
        Assert.Equal(new[] { "media", "tagging", "booru" }, EffectiveShortcutBindings.ExecutionContexts("media.tile-size.reset"));
        foreach (string id in NewIds.Skip(1)) Assert.Equal(new[] { "archive" }, EffectiveShortcutBindings.ExecutionContexts(id));
        // The reader's own commands (font size and the rest) are another context: Ctrl+Plus there never conflicts with the archive tiles' Ctrl+Plus.
        Assert.False(EffectiveShortcutBindings.ContextsOverlap("archive.media-tile-size.larger", "reader.expand"));
        Assert.False(EffectiveShortcutBindings.ContextsOverlap("archive.media-tile-size.larger", "media.tile-size.larger"));
        Assert.True(EffectiveShortcutBindings.ContextsOverlap("media.tile-size.larger", "tagging.move-up"));
    }

    [Theory, MemberData(nameof(Platforms))]
    public void ASavedBindingOfAnotherCommandOnTheNewKeyWinsAndTheNewDefaultStaysUnbound(ShortcutPlatform platform)
    {
        ShortcutBindingsSection section = new ShortcutBindingsSection()
            .Set("media.select-all", new[] { ShortcutGesture.Parse("Primary+0") })
            .Set("archive.select-page", new[] { ShortcutGesture.Parse("Primary+Plus") });
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(section, platform);
        Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave);
        Assert.Single(bindings.For("media.select-all"));
        Assert.Single(bindings.For("archive.select-page"));
        Assert.Contains(bindings.Issues, issue => issue.CommandId == "media.tile-size.reset" && issue.OtherCommandId == "media.select-all");
        Assert.Contains(bindings.Issues, issue => issue.CommandId == "archive.media-tile-size.larger" && issue.OtherCommandId == "archive.select-page");
    }

    [Fact]
    public void EveryNewCommandHasALabelInEnglishGermanAndAllPacks()
    {
        foreach (string id in NewIds)
        {
            CommandDefinition command = CommandCatalog.Get(id);
            Assert.False(string.IsNullOrWhiteSpace(command.LabelEn));
            Assert.False(string.IsNullOrWhiteSpace(command.LabelDe));
            Assert.Equal(command.LabelEn, CommandCatalog.Label(id));
        }
        foreach (string pack in Directory.GetFiles(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..", "Desktop/Localization/Packs")), "*.json"))
        {
            JsonObject strings = JsonNode.Parse(File.ReadAllText(pack))!["strings"]!.AsObject();
            foreach (string id in NewIds)
                Assert.False(string.IsNullOrWhiteSpace(strings["command." + id]?.GetValue<string>()), Path.GetFileName(pack) + ": " + id);
        }
    }
}

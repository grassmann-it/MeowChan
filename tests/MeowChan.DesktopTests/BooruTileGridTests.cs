using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

/// <summary>
/// E1: the Booru result tiles. The measures, the column count, the arrow routing by rows, the saved view and size, the shortcut contexts of the
/// three tile size commands and a real view in the tile mode. Fixtures only: fake ports, no provider request, no window.
/// </summary>
[Collection("Localization")]
public sealed class BooruTileGridTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    // ---- measures and columns -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 150)]
    [InlineData(1, 190)]
    [InlineData(2, 250)]
    public void TheCellWidthsAreTheMediaPagesAndThePictureIsSquareInsideThePadding(int index, double cell)
    {
        MediaTileSize size = (MediaTileSize)index;
        Assert.Equal(cell, BooruTileMetrics.CellWidth(size));
        Assert.Equal(cell - 18, BooruTileMetrics.PictureSize(size));
        // A decoded preview is never narrower than the largest picture, so no tile shows an enlarged bitmap.
        Assert.True(BooruTileMetrics.PictureSize(MediaTileSize.Large) <= 256);
    }

    [Theory]
    [InlineData(1000, 150, 6)]   // (1000 + 8) / 158 = 6.38
    [InlineData(624, 150, 4)]    // exactly four tiles and three gaps
    [InlineData(623, 150, 3)]
    [InlineData(100, 150, 1)]    // narrower than one cell: still one column
    [InlineData(0, 190, 1)]
    [InlineData(double.PositiveInfinity, 190, 1)]
    [InlineData(double.NaN, 190, 1)]
    [InlineData(1000, 0, 1)]
    public void TheColumnCountIsWhatFitsAndNeverBelowOne(double width, double cell, int expected) =>
        Assert.Equal(expected, BooruTileMetrics.Columns(width, cell));

    [Fact]
    public void TheLineUnderATileIsIdRatingCodeAndScoreAndLeavesOutAMissingScore()
    {
        Assert.Equal("#12345 · Q · 87", BooruTileMetrics.Line("12345", BooruRating.Questionable, 87));
        Assert.Equal("#7 · G · -3", BooruTileMetrics.Line("7", BooruRating.General, -3));
        Assert.Equal("#7 · ?", BooruTileMetrics.Line("7", BooruRating.Unknown, null));
        Assert.Equal(new[] { "?", "G", "S", "Q", "E" }, Enum.GetValues<BooruRating>().Select(BooruTileMetrics.RatingCode));
    }

    [Fact]
    public void ThePanelLaysTilesOutInEqualColumnsAndRowsAsHighAsTheirTallestTile()
    {
        BooruTilePanel panel = new() { ColumnWidth = 150 };
        double[] heights = { 100, 140, 100, 120, 100 };
        foreach (double height in heights) panel.Children.Add(new Border { Height = height });
        panel.Measure(new Size(632, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, 632, panel.DesiredSize.Height));

        Assert.Equal(4, panel.Columns);
        double width = (632 - 3 * 8) / 4.0;
        Assert.Equal(width, panel.Children[0].Bounds.Width, 6);
        Assert.Equal(width + 8, panel.Children[1].Bounds.X - panel.Children[0].Bounds.X, 6);
        Assert.Equal(0, panel.Children[4].Bounds.X);
        // Row 1 is as high as its tallest tile (140), row 2 starts after it and the gap.
        Assert.Equal(140 + 8, panel.Children[4].Bounds.Y);
        Assert.Equal(140 + 8 + 100, panel.DesiredSize.Height);

        panel.Measure(new Size(100, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, 100, panel.DesiredSize.Height));
        Assert.Equal(1, panel.Columns);
        Assert.Equal(heights.Sum() + 4 * 8, panel.DesiredSize.Height);
    }

    // ---- the arrow keys ---------------------------------------------------------------------------------------------------

    private static KeyEventArgs Press(Key key, object source) =>
        new() { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = source };

    [Theory]
    [InlineData(Key.Left, "Previous")]
    [InlineData(Key.Right, "Next")]
    [InlineData(Key.Up, "RowUp")]
    [InlineData(Key.Down, "RowDown")]
    [InlineData(Key.Home, "First")]
    [InlineData(Key.End, "Last")]
    public void InTheTileViewTheArrowsStepByOneTileOrByARow(Key key, string expected)
    {
        Border tile = new();
        Assert.Equal(Enum.Parse<BooruHitKey>(expected), BooruHitKeys.Route(Press(key, tile), tile, tiles: true));
    }

    [Fact]
    public void InTheListLeftAndRightStayUnboundAndUpAndDownStepByOneHit()
    {
        Border card = new();
        Assert.Equal(BooruHitKey.None, BooruHitKeys.Route(Press(Key.Left, card), card));
        Assert.Equal(BooruHitKey.None, BooruHitKeys.Route(Press(Key.Right, card), card));
        Assert.Equal(BooruHitKey.Previous, BooruHitKeys.Route(Press(Key.Up, card), card));
        Assert.Equal(BooruHitKey.Next, BooruHitKeys.Route(Press(Key.Down, card), card));
    }

    [Fact]
    public void ARowStepMovesByTheColumnCountAndStaysInsideTheTiles()
    {
        // 7 tiles in 3 columns: rows 0 1 2 / 3 4 5 / 6.
        Assert.Equal(0, BooruHitKeys.Target(BooruHitKey.RowUp, 0, 7, 3));
        Assert.Equal(2, BooruHitKeys.Target(BooruHitKey.RowUp, 2, 7, 3));
        Assert.Equal(1, BooruHitKeys.Target(BooruHitKey.RowUp, 4, 7, 3));
        Assert.Equal(3, BooruHitKeys.Target(BooruHitKey.RowDown, 0, 7, 3));
        Assert.Equal(5, BooruHitKeys.Target(BooruHitKey.RowDown, 2, 7, 3));
        // Below a row that has no tile in the same column the last tile is next; in the last row nothing moves.
        Assert.Equal(6, BooruHitKeys.Target(BooruHitKey.RowDown, 5, 7, 3));
        Assert.Equal(6, BooruHitKeys.Target(BooruHitKey.RowDown, 6, 7, 3));
        // Left and Right are the one-step keys; the ends stay.
        Assert.Equal(0, BooruHitKeys.Target(BooruHitKey.Previous, 0, 7, 3));
        Assert.Equal(6, BooruHitKeys.Target(BooruHitKey.Next, 6, 7, 3));
        // One column behaves like the list; no tiles give no target.
        Assert.Equal(3, BooruHitKeys.Target(BooruHitKey.RowDown, 2, 7, 1));
        Assert.Equal(-1, BooruHitKeys.Target(BooruHitKey.RowDown, 0, 0, 3));
        Assert.Equal(2, BooruHitKeys.Target(BooruHitKey.Next, 1, 5));
    }

    // ---- saved view and size ------------------------------------------------------------------------------------------------

    [Fact]
    public void TheSavedViewAndSizeReadDamagedTextAsListAndMediumAndRoundTripThroughTheConfig()
    {
        Assert.Equal(BooruResultView.Grid, BooruResultViews.Parse(" grid "));
        foreach (string? damaged in new[] { null, "", "  ", "tiles", "7" }) Assert.Equal(BooruResultView.List, BooruResultViews.Parse(damaged));

        Config config = new();
        Assert.Equal("List", config.BooruPageView);
        Assert.Equal("Medium", config.BooruTileSize);
        config.BooruPageView = "grid";
        config.BooruTileSize = "large";
        Assert.Equal("Grid", config.BooruPageView);
        Assert.Equal("Large", config.BooruTileSize);
        Config copy = config.Clone();
        Assert.Equal("Grid", copy.BooruPageView);
        Assert.Equal("Large", copy.BooruTileSize);
        Config target = new();
        target.CopyFrom(config);
        Assert.Equal("Large", target.BooruTileSize);
        config.BooruPageView = "??";
        config.BooruTileSize = null!;
        Assert.Equal("List", config.BooruPageView);
        Assert.Equal("Medium", config.BooruTileSize);
    }

    // ---- shortcuts ------------------------------------------------------------------------------------------------------------

    public static IEnumerable<object[]> Platforms() => Enum.GetValues<ShortcutPlatform>().Select(platform => new object[] { platform });

    [Theory]
    [MemberData(nameof(Platforms))]
    public void TheMediaTileSizeKeysRunInTheBooruContextOnEveryPlatformWhateverTheLayoutTypes(ShortcutPlatform platform)
    {
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(platform: platform);
        KeyModifiers primary = platform == ShortcutPlatform.MacOS ? KeyModifiers.Meta : KeyModifiers.Control;
        KeyEventArgs Key_(Key key, string? symbol) => new() { Key = key, KeyModifiers = primary, KeySymbol = symbol };
        // The number pad keys work on every layout; the digit row's 0 matches on the key, whatever character the layout types (AZERTY types "à").
        foreach (string context in new[] { "booru", "media", "tagging" })
        {
            Assert.True(bindings.Matches(Key_(Avalonia.Input.Key.Add, "+"), "media.tile-size.larger", context), context);
            Assert.True(bindings.Matches(Key_(Avalonia.Input.Key.Subtract, "-"), "media.tile-size.smaller", context), context);
            foreach (string? symbol in new string?[] { "0", "à", null })
                Assert.True(bindings.Matches(Key_(Avalonia.Input.Key.D0, symbol), "media.tile-size.reset", context), context + " " + symbol);
            Assert.True(bindings.Matches(Key_(Avalonia.Input.Key.NumPad0, "0"), "media.tile-size.reset", context), context);
        }
        foreach (string id in new[] { "media.tile-size.larger", "media.tile-size.smaller", "media.tile-size.reset" })
            Assert.Contains("booru", CommandCatalog.Get(id).ExecutionContexts);
    }

    [Fact]
    public void NoOtherCommandOfTheBooruContextTakesTheTileSizeGestures()
    {
        // Same gesture, another command that also runs in "booru": that would make the keys ambiguous on the page.
        string[] sizeCommands = { "media.tile-size.larger", "media.tile-size.smaller", "media.tile-size.reset" };
        foreach (ShortcutPlatform platform in Enum.GetValues<ShortcutPlatform>())
        {
            EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(platform: platform);
            foreach (string size in sizeCommands)
                foreach (ShortcutGesture gesture in bindings.For(size))
                    foreach (CommandDefinition other in CommandCatalog.All.Where(c => c.IsImplemented && !sizeCommands.Contains(c.Id) && c.ExecutionContexts.Contains("booru")))
                        Assert.DoesNotContain(bindings.For(other.Id), candidate => candidate.Equals(gesture));
        }
    }

    // ---- a real view -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheTileViewShowsTheHitsAsKeyboardTilesWithTheirChecksAndGalleryButtonAndSavesTheChoices() => OnDispatcher(async () =>
    {
        BooruUiHarness harness = new();
        harness.Previews.OnPreview = _ => BooruUiFixtures.Png(1);
        harness.Serve(pages: 1, perPage: 3);
        harness.Queue.States["101"] = BooruLocalState.Loaded;
        using BooruWorkbenchModel model = new(harness.Services);
        using BooruWorkbenchView view = new(model, ownsModel: false);
        view.ShowInMedia = _ => Task.FromResult(true);
        List<BooruResultView> savedViews = new();
        List<MediaTileSize> savedSizes = new();
        view.SaveResultView = savedViews.Add;
        view.SaveTileSize = savedSizes.Add;

        // Restoring does not save; the list is the default until the profile says otherwise.
        Assert.Equal(BooruResultView.List, view.ResultView);
        view.RestoreResultView(BooruResultView.Grid, MediaTileSize.Small);
        Assert.Empty(savedViews);
        Assert.Empty(savedSizes);

        await BooruTestSupport.SearchAsync(model.Search, "cat");
        await BooruTestSupport.WaitAsync(() => view.TilePanel?.Children.Count == 3, "the tiles");
        BooruTilePanel panel = view.TilePanel!;
        Assert.Equal(150, panel.ColumnWidth);
        Assert.Equal(AutomationControlType.List, AutomationProperties.GetControlTypeOverride(panel));
        List<Border> tiles = panel.Children.OfType<Border>().ToList();
        Assert.Equal(new[] { "BooruResult:101", "BooruResult:102", "BooruResult:103" }, tiles.Select(tile => tile.Name));
        Assert.Equal(new[] { 1, 2, 3 }, tiles.Select(AutomationProperties.GetPositionInSet));
        foreach (Border tile in tiles)
        {
            Assert.True(tile.Focusable);
            Assert.True(tile.IsTabStop);
            string id = tile.Name![^3..];
            // The selection check and "Open in gallery" are there once and visible; the hidden card does not repeat them.
            Assert.Single(tile.GetLogicalDescendants().OfType<CheckBox>(), box => box.Name == "BooruSelect:" + id);
            Button gallery = Assert.Single(tile.GetLogicalDescendants().OfType<Button>(), button => button.Name == "BooruView:" + id);
            Assert.True(gallery.IsEffectivelyVisible || !tile.IsEffectivelyVisible);
            Assert.True(tile.GetLogicalDescendants().OfType<CheckBox>().Single(box => box.Name == "BooruSelect:" + id).IsVisible);
            Assert.Contains(tile.GetLogicalDescendants().OfType<TextBlock>(), text => text.Name == "BooruTileLine:" + id && text.Text!.StartsWith("#" + id, StringComparison.Ordinal));
        }

        // The saved hit shows the local mark; the context menu still offers what the card offers.
        Assert.True(Local(tiles[0]).IsVisible);
        Assert.False(Local(tiles[1]).IsVisible);
        ContextMenuBuilder? menu = (ContextMenuBuilder?)typeof(BooruWorkbenchView).GetMethod("ComposeResultMenu", Private)!.Invoke(view, new object[] { tiles[1], "102" });
        string[] entries = Assert.IsType<ContextMenuBuilder>(menu).ToModel().Entries.Select(entry => entry.Id).ToArray();
        Assert.Contains(BooruWorkbenchView.MenuLoad, entries);
        Assert.Contains(BooruWorkbenchView.MenuOpenPost, entries);

        // An arrow key on a tile is handled there (the move itself needs a window).
        KeyEventArgs right = Press(Key.Right, tiles[0]);
        tiles[0].RaiseEvent(right);
        Assert.True(right.Handled);
        KeyEventArgs left = Press(Key.Left, tiles[0].GetLogicalDescendants().OfType<TextBlock>().First());
        tiles[0].GetLogicalDescendants().OfType<TextBlock>().First().RaiseEvent(left);
        Assert.False(left.Handled, "a key of a part inside the tile is not the tile's");

        // The size: one step is shown at once and saved once; the same size again changes nothing.
        Assert.True(view.ChooseTileSize(MediaTileSize.Large));
        Assert.False(view.ChooseTileSize(MediaTileSize.Large));
        Assert.Equal(new[] { MediaTileSize.Large }, savedSizes);
        Assert.Equal(250, view.TilePanel!.ColumnWidth);
        Assert.Equal(3, view.TilePanel.Children.Count);

        // Ctrl+Plus over the tiles steps the size (the media page's command, here in the booru context): Large is the end, Ctrl+0 is Medium.
        KeyModifiers primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        KeyEventArgs reset = new() { RoutedEvent = InputElement.KeyDownEvent, Key = Key.NumPad0, KeyModifiers = primary, Source = view.TilePanel.Children[0] };
        view.TilePanel.Children[0].RaiseEvent(reset);
        Assert.True(reset.Handled);
        Assert.Equal(MediaTileSize.Medium, view.TileSize);
        Assert.Equal(new[] { MediaTileSize.Large, MediaTileSize.Medium }, savedSizes);

        // Back to the list: the cards of the list again, saved once, and the tile panel is gone.
        view.SetResultView(BooruResultView.List, save: true);
        Assert.Equal(new[] { BooruResultView.List }, savedViews);
        Assert.Null(view.TilePanel);
        Assert.Equal(3, Cards(view).Count);
        view.SetResultView(BooruResultView.List, save: true);
        Assert.Single(savedViews);
    });

    [Fact]
    public void ThumbnailsAreNeverEnlargedBeyondTheirOwnPixelsInTheTilesOrTheList()
    {
        // The bitmap decode needs the platform (not available in this fixture), so the one place that builds the picture is checked as source.
        string view = File.ReadAllText(Path.Combine(RepoRoot(), "Desktop/Views/Booru/BooruWorkbenchView.cs"));
        Assert.Contains("Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly", view);
        Assert.Contains("CreatePreview(row, picture)", File.ReadAllText(Path.Combine(RepoRoot(), "Desktop/Views/Booru/BooruWorkbenchView.Grid.cs")));
    }

    private static string RepoRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "MeowChan.csproj"))) directory = Path.GetDirectoryName(directory);
        return directory ?? throw new DirectoryNotFoundException("repository root");
    }

    private static Border Local(Border tile) => tile.GetLogicalDescendants().OfType<Border>().Single(border => border.Name?.StartsWith("BooruResultLocal:", StringComparison.Ordinal) == true);

    private static List<Border> Cards(BooruWorkbenchView view) =>
        ((StackPanel)typeof(BooruWorkbenchView).GetField("_results", Private)!.GetValue(view)!).Children.OfType<Border>().ToList();

    // One actual Avalonia dispatcher owns creation, awaited continuations and assertions (as in BooruHitKeyboardTests).
    private static void OnDispatcher(Func<Task> test)
    {
        FieldInfo primary = typeof(Dispatcher).GetField("s_uiThread", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? previousDispatcher = primary.GetValue(null);
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        string language = Texts.Language;
        Dispatcher owner = Dispatcher.CurrentDispatcher;
        try
        {
            primary.SetValue(null, owner);
            SynchronizationContext.SetSynchronizationContext(new ActionContext(owner));
            Texts.Initialize();
            Texts.SetLanguage("en");
            Task pending = test();
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (!pending.IsCompleted)
            {
                owner.RunJobs();
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Booru tile test dispatcher did not finish.");
                if (!pending.IsCompleted) Thread.Sleep(1);
            }
            pending.GetAwaiter().GetResult();
        }
        finally
        {
            Texts.SetLanguage(language);
            SynchronizationContext.SetSynchronizationContext(previousContext);
            primary.SetValue(null, previousDispatcher);
        }
    }

    private sealed class ActionContext(Dispatcher owner) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => owner.Post(() =>
        {
            SynchronizationContext? previous = Current;
            SetSynchronizationContext(this);
            try { callback(state); }
            finally { SetSynchronizationContext(previous); }
        });
    }
}

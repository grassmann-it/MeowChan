using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Platform;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>
/// 16.11.2026: the reader's Media tab chooses Grid or Table with the Archive results bar's joined pair (segment toggle buttons: icon before text,
/// the one that is on highlighted) instead of one button that names the other view. The saved choice (Config.ArchiveMediaTiles, true = Grid) keeps
/// its meaning. The look is the capture's; here the real toolbar, the pair following the page's one truth, the command and the failure path.
/// </summary>
[Collection("Gallery viewer construction")]
public sealed class ReaderMediaLayoutSwitchTests
{
    [Fact]
    public void TheMediaToolsBarEndsWithTheJoinedGridAndTablePair()
    {
        BooruComponentDispatcher.Run(() => WithPage(page =>
        {
            ActionToolBar bar = page.FindControl<ActionToolBar>("ReaderMediaTools")!;
            Assert.Equal(new[] { "ArchiveRatingFilter", "ArchiveMediaSort", "ArchiveVersionScope", "ArchiveFavoritesFilter",
                "ArchiveMediaColumnsButton", "ArchiveMediaLayoutTiles", "ArchiveMediaLayoutTable" },
                bar.Items.OfType<Control>().Select(item => item.Name));
            ToggleButton grid = page.FindControl<ToggleButton>("ArchiveMediaLayoutTiles")!;
            ToggleButton table = page.FindControl<ToggleButton>("ArchiveMediaLayoutTable")!;
            ToolbarGapPatternTests.AssertJoinedToggleClasses(grid.Classes, "segment-first");
            ToolbarGapPatternTests.AssertJoinedToggleClasses(table.Classes, "segment-last");
        }));
    }

    [Fact]
    public void ThePairShowsTheLoadedViewAndFollowsTheSavedPreference()
    {
        BooruComponentDispatcher.Run(() => WithPage(page =>
        {
            ToggleButton tiles = page.FindControl<ToggleButton>("ArchiveMediaLayoutTiles")!;
            ToggleButton table = page.FindControl<ToggleButton>("ArchiveMediaLayoutTable")!;
            Assert.Equal((true, false), (tiles.IsChecked, table.IsChecked)); // the default is Grid
            Assert.True(page.FindControl<ListBox>("ArchiveMediaTiles")!.IsVisible);

            page.MediaTilesPreference = false; // a saved Table, as the shell loads it
            Assert.Equal((false, true), (tiles.IsChecked, table.IsChecked));

            page.MediaTilesPreference = true;
            Assert.Equal((true, false), (tiles.IsChecked, table.IsChecked));
        }));
    }

    [Fact]
    public void AClickSwitchesTheViewSavesItAndKeepsExactlyOneButtonOn()
    {
        BooruComponentDispatcher.Run(() => WithPage(page =>
        {
            List<bool> saved = [];
            page.SaveMediaTiles = tiles => { saved.Add(tiles); return Task.CompletedTask; };
            ToggleButton tiles = page.FindControl<ToggleButton>("ArchiveMediaLayoutTiles")!;
            ToggleButton table = page.FindControl<ToggleButton>("ArchiveMediaLayoutTable")!;

            CommandBindings.InvokeAction(table); // the same flip + Click a pointer click, Space or Enter makes
            Assert.False(page.MediaShowsTiles);
            Assert.Equal((false, true), (tiles.IsChecked, table.IsChecked));
            Assert.True(page.FindControl<DataGrid>("ArchiveMediaGrid")!.IsVisible);
            Assert.Equal([false], saved);

            CommandBindings.InvokeAction(table); // the one that is on, pressed again: it stays on and nothing is saved
            Assert.Equal((false, true), (tiles.IsChecked, table.IsChecked));
            Assert.Equal([false], saved);

            CommandBindings.InvokeAction(tiles);
            Assert.True(page.MediaShowsTiles);
            Assert.Equal((true, false), (tiles.IsChecked, table.IsChecked));
            Assert.True(page.FindControl<ListBox>("ArchiveMediaTiles")!.IsVisible);
            Assert.Equal([false, true], saved);
        }));
    }

    [Fact]
    public void TheCommandFlipsTheViewAndTheHighlightFollows()
    {
        BooruComponentDispatcher.Run(() => WithPage(page =>
        {
            ToggleButton tiles = page.FindControl<ToggleButton>("ArchiveMediaLayoutTiles")!;
            ToggleButton table = page.FindControl<ToggleButton>("ArchiveMediaLayoutTable")!;
            Task flip = page.SetMediaLayoutAsync(!page.MediaShowsTiles); // what the registered "Choose media view" runs
            Assert.True(flip.IsCompletedSuccessfully);
            Assert.Equal((false, true), (tiles.IsChecked, table.IsChecked));
            Assert.True(page.SetMediaLayoutAsync(!page.MediaShowsTiles).IsCompletedSuccessfully);
            Assert.Equal((true, false), (tiles.IsChecked, table.IsChecked));
        }));
    }

    [Fact]
    public void AFailedSaveBringsTheOldViewAndItsHighlightBack()
    {
        BooruComponentDispatcher.Run(() => WithPage(page =>
        {
            page.SaveMediaTiles = _ => throw new IOException("synthetic save failure");
            ToggleButton tiles = page.FindControl<ToggleButton>("ArchiveMediaLayoutTiles")!;
            ToggleButton table = page.FindControl<ToggleButton>("ArchiveMediaLayoutTable")!;
            CommandBindings.InvokeAction(table);
            Assert.True(page.MediaShowsTiles);
            Assert.Equal((true, false), (tiles.IsChecked, table.IsChecked));
        }));
    }

    [Fact]
    public void TheButtonsSayGridAndTableInTheCurrentLanguage()
    {
        BooruComponentDispatcher.Run(() => WithPage(page =>
        {
            string before = Texts.Language;
            try
            {
                Texts.SetLanguage("de");
                Assert.Equal("Raster", page.FindControl<ToggleButton>("ArchiveMediaLayoutTiles")!.Content);
                Assert.Equal("Tabelle", page.FindControl<ToggleButton>("ArchiveMediaLayoutTable")!.Content);
                Texts.SetLanguage("en");
                Assert.Equal("Grid", page.FindControl<ToggleButton>("ArchiveMediaLayoutTiles")!.Content);
                Assert.Equal("Table", page.FindControl<ToggleButton>("ArchiveMediaLayoutTable")!.Content);
                foreach (string code in Texts.BundledPackCodes())
                {
                    Texts.SetLanguage(code);
                    Assert.Equal(Texts.T("tagging.page.view.grid"), page.FindControl<ToggleButton>("ArchiveMediaLayoutTiles")!.Content);
                    Assert.Equal(Texts.T("gallery.table"), page.FindControl<ToggleButton>("ArchiveMediaLayoutTable")!.Content);
                }
            }
            finally { Texts.SetLanguage(before); }
        }));
    }

    private static void WithPage(Action<MeowChan.ArchivePage> test)
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        TokenApplication.Bind();
        MeowChan.ArchivePage page = new(_ => Task.FromResult<ArchiveReader>(null!), (_, _) => Task.CompletedTask, _ => { });
        try { test(page); }
        finally { page.EndLifetime(); }
    }

}

using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Platform;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

/// <summary>
/// 16.11.2026: the reader's Media tab chooses Tiles or Table with the Archive results bar's joined pair (segment toggle buttons: icon before text,
/// the one that is on highlighted) instead of one button that names the other view. The saved choice (Config.ArchiveMediaTiles, true = Tiles) keeps
/// its meaning. The look is the capture's; here the markup, the pair following the page's one truth, the command and the failure path.
/// </summary>
[Collection("Gallery viewer construction")]
public sealed class ReaderMediaLayoutSwitchTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string Root(string path, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", path));

    [Fact]
    public void TheMediaToolsBarStartsWithTheJoinedTilesAndTablePairLikeTheArchiveResultsBar()
    {
        XElement bar = XDocument.Load(Root("Desktop/Views/Archive/ArchivePage.axaml")).Descendants()
            .Single(element => element.Name.LocalName == "ActionToolBar" && (string?)element.Attribute(X + "Name") == "ReaderMediaTools");
        XElement[] items = bar.Elements().Where(element => !element.Name.LocalName.Contains('.')).ToArray();
        Assert.Equal(["ArchiveMediaLayoutTiles", "ArchiveMediaLayoutTable"], items.Take(2).Select(item => (string)item.Attribute(X + "Name")!));
        Assert.All(items.Take(2), item => Assert.Equal("ToggleButton", item.Name.LocalName));
        Assert.Equal("segment segment-first", (string?)items[0].Attribute("Classes"));
        Assert.Equal("segment segment-last", (string?)items[1].Attribute("Classes"));
        foreach (XElement toggle in items.Take(2))
        {
            Assert.Null(toggle.Attribute("Padding"));
            Assert.Equal("64", (string?)toggle.Attribute("MinWidth"));
            Assert.Equal("{StaticResource Workspace.Size.Control}", (string?)toggle.Attribute("MinHeight")); // the token (32), as on the results bar
            Assert.Equal("OnMediaLayoutClick", (string?)toggle.Attribute("Click"));
        }
        Assert.DoesNotContain("ArchiveMediaMode", File.ReadAllText(Root("Desktop/Views/Archive/ArchivePage.axaml")));
        Assert.DoesNotContain("OnMediaModeClick", File.ReadAllText(Root("Desktop/Views/Archive/ArchivePage.Gallery.cs")));
        // Both views of the pair are work-view elements of the bar (placement: More in Barebones, on the bar otherwise).
        foreach (string id in new[] { "reader.media.layout-tiles", "reader.media.layout-table" })
            Assert.Equal((Presence.Overflow, Presence.Direct, Presence.Direct),
                (WorkViewPolicy.Get(id).Barebones, WorkViewPolicy.Get(id).Simple, WorkViewPolicy.Get(id).Advanced));
        // The command that chose the view before still names both buttons, and keeps one meaning of a switch (registered by the page).
        string adapters = File.ReadAllText(Root("Desktop/Commands/LegacyCommandAdapters.cs"));
        Assert.Contains("[\"ArchiveMediaLayoutTiles\"] = \"archive.media-view\", [\"ArchiveMediaLayoutTable\"] = \"archive.media-view\"", adapters);
        string source = File.ReadAllText(Root("Desktop/Views/Archive/ArchivePage.MediaLayout.cs"));
        Assert.Contains("new(\"archive.media-view\", pane,", source);
        Assert.Contains("SetMediaLayoutAsync(!_mediaTiles)", source);
    }

    [Fact]
    public void ThePairShowsTheLoadedViewAndFollowsTheSavedPreference()
    {
        BooruComponentDispatcher.Run(() => WithPage(page =>
        {
            ToggleButton tiles = page.FindControl<ToggleButton>("ArchiveMediaLayoutTiles")!;
            ToggleButton table = page.FindControl<ToggleButton>("ArchiveMediaLayoutTable")!;
            Assert.Equal((true, false), (tiles.IsChecked, table.IsChecked)); // the default is Tiles
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
    public void TheButtonsSayTilesAndTableInTheCurrentLanguage()
    {
        BooruComponentDispatcher.Run(() => WithPage(page =>
        {
            string before = Texts.Language;
            try
            {
                Texts.SetLanguage("de");
                Assert.Equal("Kacheln", page.FindControl<ToggleButton>("ArchiveMediaLayoutTiles")!.Content);
                Assert.Equal("Tabelle", page.FindControl<ToggleButton>("ArchiveMediaLayoutTable")!.Content);
                Texts.SetLanguage("en");
                Assert.Equal("Tiles", page.FindControl<ToggleButton>("ArchiveMediaLayoutTiles")!.Content);
                Assert.Equal("Table", page.FindControl<ToggleButton>("ArchiveMediaLayoutTable")!.Content);
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
        finally { (page as IDisposable)?.Dispose(); }
    }

}

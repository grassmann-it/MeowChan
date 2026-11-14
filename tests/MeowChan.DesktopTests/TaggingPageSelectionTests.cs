using System.Runtime.CompilerServices;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

// TG1 (second step): the work-view rows of the view switch and the selection bar, the wiring the page must keep (the ids it sets, Esc,
// the saved view) and the badge helper's place. The page itself is a GUI control, so its source is the contract here, as for the other
// page tests; the rules the bar shows (which command applies) are TaggingPageMenuTests'.
[Collection("Localization")]
public sealed class TaggingPageSelectionTests : IDisposable
{
    public TaggingPageSelectionTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static string Read(string relative, [CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative)));

    [Fact]
    public void TheViewSwitchMovesUnderMoreOnlyInBarebonesAndTheSelectionBarKeepsItsCoreEverywhere()
    {
        Assert.Equal(Presence.Overflow, WorkViewPolicy.Resolve("tagging.view-toggle", WorkView.Barebones));
        Assert.Equal(Presence.Direct, WorkViewPolicy.Resolve("tagging.view-toggle", WorkView.Simple));
        Assert.Equal(Presence.Direct, WorkViewPolicy.Resolve("tagging.view-toggle", WorkView.Advanced));
        foreach (WorkView view in Enum.GetValues<WorkView>())
            foreach (string id in new[] { "tagging.selection-bar", "tagging.selection.status", "tagging.selection.tag", "tagging.selection.remove", "tagging.selection.clear" })
                Assert.True(WorkViewPolicy.OnSurface(WorkViewPolicy.Resolve(id, view)), id + " in " + view);
        // Stamping and the state menu thin out like the toolbar: stamp under More in Barebones, state also in Simple.
        Assert.Equal(Presence.Overflow, WorkViewPolicy.Resolve("tagging.selection.stamp", WorkView.Barebones));
        Assert.Equal(Presence.Direct, WorkViewPolicy.Resolve("tagging.selection.stamp", WorkView.Simple));
        Assert.Equal(Presence.Overflow, WorkViewPolicy.Resolve("tagging.selection.state", WorkView.Barebones));
        Assert.Equal(Presence.Overflow, WorkViewPolicy.Resolve("tagging.selection.state", WorkView.Simple));
        Assert.Equal(Presence.Direct, WorkViewPolicy.Resolve("tagging.selection.state", WorkView.Advanced));
    }

    [Fact]
    public void EveryTaggingPolicyRowThatIsAnElementHasItsIdSetOnThePage()
    {
        string workView = Read("Desktop/Views/Tagging/TaggingPage.WorkView.cs");
        foreach (string id in new[] { "tagging.view-toggle", "tagging.selection-bar", "tagging.selection.status", "tagging.selection.tag", "tagging.selection.stamp",
            "tagging.selection.state", "tagging.selection.remove", "tagging.selection.clear" })
            Assert.Contains("\"" + id + "\"", workView);
    }

    [Fact]
    public void BothViewsShareTheSelectionTheMenuTheKeysAndTheDoubleClick()
    {
        string grid = Read("Desktop/Views/Tagging/TaggingPage.Grid.cs");
        string commands = Read("Desktop/Views/Tagging/TaggingPage.Commands.cs");
        // The grid is the media page's panel over multi-select, with its own menu host, the same key handler and the same continue action.
        Assert.Contains("SelectionMode = SelectionMode.Multiple", grid);
        Assert.Contains("new MediaTilePanel", grid);
        Assert.Contains("_gridMenuHost = new ContextMenuHost(_grid, ComposeRowMenu", grid);
        Assert.Contains("_grid.AddHandler(KeyDownEvent, OnListKeyDown", grid);
        Assert.Contains("_grid.DoubleTapped", grid);
        Assert.Contains("_grid.SelectionChanged += OnSelectionChanged;", grid);
        Assert.Contains("_list.SelectionChanged += OnSelectionChanged;", commands);
        // Esc clears the selection first; only the shown list is bound.
        Assert.Contains("e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && SelectedCount > 0", commands);
        Assert.Contains("hidden.ItemsSource = null;", Read("Desktop/Views/Tagging/TaggingPage.cs"));
    }

    [Fact]
    public void TheViewChoiceIsSavedPerProfileAndRestoredWhenThePageIsBuilt()
    {
        string shell = Read("Desktop/Shell/MainWindow.TaggingPage.cs");
        Assert.Contains("config.TaggingPageView = mode.ToString()", shell);
        Assert.Contains("RestoreViewMode(TaggingViewModes.Parse(_session.Config.TaggingPageView))", shell);
        Assert.Contains("SaveViewMode?.Invoke(mode)", Read("Desktop/Views/Tagging/TaggingPage.Grid.cs"));
        // Restoring never saves back.
        Assert.Contains("RestoreViewMode(TaggingViewMode mode) => SetViewMode(mode, save: false)", Read("Desktop/Views/Tagging/TaggingPage.Grid.cs"));
    }

    [Fact]
    public void TheTileBadgesLiveInOneSeparateClassTheTileOnlyPlaces()
    {
        string badges = Read("Desktop/Views/Tagging/TaggingTileBadges.cs");
        string tile = Read("Desktop/Views/Tagging/TaggingQueueTileView.cs");
        Assert.Contains("internal sealed class TaggingTileBadges", badges);
        // MB1b: the one drawn thing is the shared badge view with the tagging grid's options; nothing of the old corner marks is left.
        Assert.Contains("new() { Name = \"TaggingTileBadges\", Options = MediaBadgeOptions.TaggingGrid }", badges);
        foreach (string gone in new[] { "CornerMark", "CornerBadge", "CheckMark", "SkipMark", "NextMark", "VideoMark", "TagMark", "Overlay" })
        {
            Assert.DoesNotContain(gone, badges);
            Assert.DoesNotContain(gone, tile);
        }
        // The tile lays the view over the picture and hands it the row and the shown picture's size; the badges add no focus stop.
        Assert.Contains("_badges.View", tile);
        Assert.Contains("_badges.Show(row, tags, _preview.PictureSize)", tile);
        Assert.Contains("_badges.Place(_preview.PictureSize)", tile);
        Assert.DoesNotContain("Focusable = true", badges);
        Assert.DoesNotContain("TaggingTileBadges", Read("Desktop/Views/Tagging/TaggingPage.cs"));
    }

    [Fact]
    public void TheNewTextsExistInEveryLanguage()
    {
        foreach (string language in new[] { "en", "de" })
        {
            Texts.SetLanguage(language);
            foreach (string key in new[] { "tagging.page.view", "tagging.page.view.list", "tagging.page.view.list.hint", "tagging.page.view.grid",
                "tagging.page.view.grid.hint", "tagging.page.selection.name", "tagging.page.selection.state", "tagging.page.selection.state.hint",
                "tagging.page.selection.remove" })
                Assert.NotEqual(key, Texts.T(key));
        }
    }
}

using System.Text.RegularExpressions;
using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

// Code hygiene package (1.1.0): audit V18 (dialog openers go through OpenActions), the small items of the media split review (K-6) and the pinned Space rule.
public sealed class CodeHygieneTests
{
    [Fact]
    public void NoOpenActionDiscardsItsTask()
    {
        // `_ = Open...Async(` loses a failure (an unobserved task); OpenFrozenSourceAsync catches and shows its own failure.
        var discarded = new Regex(@"_ = [A-Za-z_.]*Open[A-Za-z]*Async\(");
        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(Root(), "Desktop"), "*.cs", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            foreach (Match match in discarded.Matches(text))
                if (!text.Substring(match.Index).StartsWith("_ = OpenFrozenSourceAsync(", StringComparison.Ordinal))
                    offenders.Add(Path.GetFileName(file) + ": " + match.Value);
        }
        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("Desktop/Shell/MainWindow.Profiles.cs", "OpenActions.Start(OpenNewProfileDialogAsync, \"profile.new\")")]
    [InlineData("Desktop/Shell/MainWindow.Profiles.cs", "OpenActions.Start(OpenSwitchProfileDialogAsync, \"profile.switch\")")]
    [InlineData("Desktop/Shell/MainWindow.ReaderGroups.cs", "OpenActions.Start(() => OpenReaderGroupsAsync(), \"readerGroups.open\")")]
    [InlineData("Desktop/Shell/MainWindow.ManualImport.cs", "OpenActions.Start(OpenManualImportDialogCheckedAsync, \"import.manual.open\")")]
    [InlineData("Desktop/Shell/MainWindow.TagQueue.cs", "OpenActions.Start(() => OpenTagQueueGalleryAsync(), \"media.queue.open\")")]
    [InlineData("Desktop/Views/Archive/ArchivePage.SavedSearches.cs", "OpenActions.Start(() => OpenSavedSearchesAsync(saveCurrent), \"archive.savedSearches.menu\")")]
    [InlineData("Desktop/Views/Media/MediaPage.Panel.cs", "OpenActions.Start(OpenPanelMediaAsync, \"media.panel.open\")")]
    public void TheDialogOpenersStartThroughTheSharedSafePath(string file, string expected) => Assert.Contains(expected, Source(file));

    [Fact]
    public async Task AFailingOpenIsReportedOnceAndANeverAwaitedCancellationIsNot()
    {
        var reported = new List<string>();
        await OpenActions.RunAsync(() => throw new InvalidOperationException("boom"), "profile.new", (error, operation) => { reported.Add(operation); return true; });
        await OpenActions.RunAsync(() => Task.FromCanceled(new CancellationToken(true)), "profile.new", (error, operation) => { reported.Add(operation); return true; });
        Assert.Equal(new[] { "profile.new" }, reported);
    }

    // ---- K-6 ----

    [Fact]
    public void SpaceIsThePanelVideosKeyWhileAVideoIsShownAndTheGridsOtherwise()
    {
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(null, ShortcutPlatform.Linux);
        var space = new KeyEventArgs { Key = Key.Space };
        Assert.Equal(new MediaPanelKeyRoute(MediaPanelKeyKind.Video, ReaderVideoAction.PlayPause), MediaPanelKeys.Route(Key.Space, KeyModifiers.None,
            id => bindings.Matches(space, id), panelShown: true, videoOffered: true, nativeInput: false, fromPanel: false));
        Assert.Equal(MediaPanelKeyRoute.None, MediaPanelKeys.Route(Key.Space, KeyModifiers.None, id => bindings.Matches(space, id),
            panelShown: true, videoOffered: false, nativeInput: false, fromPanel: false));
    }

    [Fact]
    public void ThePanelAnnouncesAnotherMediumButNotARefreshOfTheSameOne()
    {
        string panel = Source("Desktop/Views/Media/MediaPage.Panel.cs");
        Assert.Matches(@"if \(!same\)\s*\{[^}]*ScheduleAnnouncement\(Texts\.T\(""media\.panel\.region"",", panel.Replace("\r\n", "\n"));
    }

    [Fact]
    public void AFitLimitDecodesThePanelAgainButAVolumeDoesNot()
    {
        string panel = Source("Desktop/Views/Media/MediaPage.Panel.cs");
        Assert.Contains("bool fitChanged = _panelFit != fit;", panel);
        Assert.Contains("if (fitChanged) RefreshPanel();", panel);
        // The gallery's saved volume reaches the panel once the write is done.
        string preferences = Source("Desktop/Shell/MainWindow.GalleryPreferences.cs");
        Assert.Matches(@"await write;\s*//[^\n]*\n\s*if \(!_isClosing\) ConfigureMediaPanel\(\);", preferences);
        Assert.Equal(new GalleryFitPreferences(), new GalleryFitPreferences());
        Assert.NotEqual(new GalleryFitPreferences(Limit: true), new GalleryFitPreferences());
    }

    [Fact]
    public void ThePanelFindsItsRowAtItsOldPlaceFirst()
    {
        string panel = Source("Desktop/Views/Media/MediaPage.Panel.cs");
        Assert.Contains("RowOfTile(MediaLibraryTileKey.Of(previous.Tile), previous.Index)", panel);
        Assert.Contains("MediaLibraryTileKey.Of(_tiles[hint]) == key) return rows[hint];", panel);
    }

    // ---- K-3 ----

    [Fact]
    public void TheTilePanelSmokeNoLongerSharesTheMediaSidebarsName()
    {
        string scenarios = Source("tests/MeowChan.DesktopSmoke/SmokeScenarios.cs");
        Assert.Contains("Root(\"media-tile-panel\", \"MEOWCHAN_MEDIA_TILE_PANEL_ONLY\")", scenarios);
        Assert.DoesNotContain("Root(\"media-panel\"", scenarios);
        Assert.Contains("[\"media-tile-panel\"] = new(", Source("tests/MeowChan.DesktopSmoke/SmokeScenarios.Runs.cs"));
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../.."));

    private static string Source(string relative) => File.ReadAllText(Path.Combine(Root(), relative));
}

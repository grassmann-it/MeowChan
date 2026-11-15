using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

// GL5c (UX spec S1 "Info = eine Zeile", S4 GL5c): the info line's pure rules, the new GalleryStatusDwellMs setting (default, limits, load repair,
// copy, export) and the plan's two new info rules. The viewer's wiring is pinned in its source (no window here).
public sealed class GalleryInfoLineTests
{
    [Theory]
    [InlineData("Ready", true)] // a confirmation: fades out after the dwell time
    [InlineData("Running", false)] // a state: stays while the slideshow runs
    [InlineData("Loading", false)]
    [InlineData("Paused", false)] // reasons and errors always stay
    [InlineData("SeekPaused", false)]
    [InlineData("Context", false)]
    [InlineData("Help", false)]
    [InlineData("Menu", false)]
    [InlineData("Inactive", false)]
    [InlineData("Ended", false)]
    [InlineData("NoMedia", false)]
    [InlineData("Failed", false)]
    [InlineData("TagHold", false)]
    [InlineData("None", false)]
    public void OnlyTheReadyConfirmationIsTransient(string status, bool transient) =>
        Assert.Equal(transient, GalleryInfoLine.IsTransient(Enum.Parse<SlideshowStatus>(status)));

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)] // the dwell time ran out
    [InlineData(false, false, true)]
    [InlineData(false, true, true)] // a persistent status never expires
    public void ATransientStatusIsGoneOnceItsDwellRanOut(bool transient, bool expired, bool shown) =>
        Assert.Equal(shown, GalleryInfoLine.StatusShown(transient, expired));

    [Theory]
    [InlineData(int.MinValue, 1000)]
    [InlineData(999, 1000)]
    [InlineData(1000, 1000)]
    [InlineData(3000, 3000)]
    [InlineData(10_000, 10_000)]
    [InlineData(10_001, 10_000)]
    [InlineData(int.MaxValue, 10_000)]
    public void TheDwellIsClampedToItsRange(int value, int expected) => Assert.Equal(expected, GalleryInfoLine.ClampDwell(value));

    [Theory]
    [InlineData("1111", "0111")] // the first shown segment has no separator before it, the rest all follow a shown one
    [InlineData("0111", "0011")]
    [InlineData("1010", "0010")]
    [InlineData("0010", "0000")]
    [InlineData("0000", "0000")]
    [InlineData("1001", "0001")]
    public void ASeparatorStandsOnlyBetweenTwoShownSegments(string shown, string separators)
    {
        bool[] result = GalleryInfoLine.Separators(shown.Select(c => c == '1').ToArray());
        Assert.Equal(separators, new string(result.Select(on => on ? '1' : '0').ToArray()));
    }

    [Fact]
    public void ASeparatorStandsBeforeEveryLaterShownSegment()
    {
        Assert.Equal(new[] { false, true, true, true }, GalleryInfoLine.Separators(new[] { true, true, true, true }));
        Assert.Equal(new[] { false, false, true, false }, GalleryInfoLine.Separators(new[] { false, true, true, false }));
    }

    [Fact]
    public void TheStatusDwellIsASettingWithDefaultMinimumAndMaximum()
    {
        Config defaults = new();
        Assert.Equal(3000, defaults.GalleryStatusDwellMs);
        Assert.Equal((3000, 1000, 10_000), (Config.GalleryStatusDwellMsDefault, Config.GalleryStatusDwellMsMinimum, Config.GalleryStatusDwellMsMaximum));
        defaults.Validate();
        foreach (int valid in new[] { 1000, 4500, 10_000 })
        {
            Config candidate = defaults.Clone();
            candidate.GalleryStatusDwellMs = valid;
            candidate.Validate();
            Assert.Equal(valid, candidate.Clone().GalleryStatusDwellMs);
            Config target = new();
            target.CopySettingsFieldsFrom(candidate);
            Assert.Equal(valid, target.GalleryStatusDwellMs);
            Config copy = new();
            copy.CopyFrom(candidate);
            Assert.Equal(valid, copy.GalleryStatusDwellMs);
            // The export carries it, and the portable snapshot too.
            Assert.Equal(valid, SettingsTransfer.CreatePortableSnapshot(candidate).GalleryStatusDwellMs);
            Assert.Equal(valid, SettingsTransfer.Export(candidate, "1.1.0")[nameof(Config.GalleryStatusDwellMs)]!.GetValue<int>());
        }
        foreach (int invalid in new[] { 999, 10_001, int.MinValue, int.MaxValue })
        {
            Config candidate = defaults.Clone();
            candidate.GalleryStatusDwellMs = invalid;
            Assert.Throws<ArgumentOutOfRangeException>(() => candidate.Validate());
            Assert.Throws<ArgumentOutOfRangeException>(() => new Config().CopySettingsFieldsFrom(candidate));
        }
    }

    [Theory]
    [InlineData("4000", 4000)]
    [InlineData("200", 1000)] // clamped up
    [InlineData("99999", 10_000)] // clamped down
    [InlineData("1.5", 3000)]
    [InlineData("\"x\"", 3000)]
    [InlineData("[1,{\"a\":[2]}]", 3000)]
    [InlineData("{}", 3000)]
    public void LoadingRepairsOnlyThePreference(string raw, int expected)
    {
        using TempDirectory directory = new();
        new Config { DownloadPath = Path.Combine(directory.Path, "media"), MaxParallelDownloads = 7 }.SaveConfig(directory.Path);
        string path = Path.Combine(directory.Path, "settings.json");
        JsonObject document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        document[nameof(Config.GalleryStatusDwellMs)] = JsonNode.Parse(raw);
        File.WriteAllText(path, document.ToJsonString());
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        Assert.Equal(expected, loaded.GalleryStatusDwellMs);
        Assert.Equal(7, loaded.MaxParallelDownloads);
        loaded.SaveConfig(directory.Path);
        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(expected, saved.RootElement.GetProperty(nameof(Config.GalleryStatusDwellMs)).GetInt32());
    }

    [Fact]
    public void OldSettingsWithoutTheDwellStayLoadable()
    {
        using TempDirectory directory = new();
        new Config { DownloadPath = Path.Combine(directory.Path, "media") }.SaveConfig(directory.Path);
        string path = Path.Combine(directory.Path, "settings.json");
        JsonObject document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.True(document.Remove(nameof(Config.GalleryStatusDwellMs)));
        File.WriteAllText(path, document.ToJsonString());
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        Assert.Equal(3000, loaded.GalleryStatusDwellMs);
    }

    // The plan's two info rules (S2, QW5 extended): the "where am I" segment goes below NarrowWidth; the info rating yields to a rating button on the bar.
    private static GalleryChromeInput Input(WorkView view, double width, bool panelDecisions = false) =>
        new(GalleryChromeMode.View, view, GalleryLayoutChoice.Default, MediaFocus: false, Overlay: false, ControlsHidden: false, Video: false,
            PanelOpen: false, PanelCoupled: false, width, 900) { PanelDecisions = panelDecisions };

    [Theory]
    [InlineData(1920, true)]
    [InlineData(900, true)]
    [InlineData(899, false)]
    [InlineData(500, false)]
    public void TheScopeSegmentLeavesTheInfoLineBelowTheNarrowWidth(double width, bool scope) =>
        Assert.Equal(scope, GalleryChromePlan.Resolve(Input(WorkView.Advanced, width)).InfoScope);

    [Theory]
    [InlineData(WorkView.Advanced, 1920, false)] // the rating button (☆ 4) shows the value
    [InlineData(WorkView.Advanced, 1050, true)] // the button went under ⋯: the info line says the rating
    [InlineData(WorkView.Simple, 1920, false)]
    [InlineData(WorkView.Simple, 800, true)] // Simple has lost the button by then (S2)
    [InlineData(WorkView.Barebones, 1920, true)] // Barebones has no rating button at all
    public void TheInfoRatingShowsOnlyWithoutARatingButtonOnTheBar(WorkView view, double width, bool rating) =>
        Assert.Equal(rating, GalleryChromePlan.Resolve(Input(view, width)).InfoRating);

    [Fact]
    public void TheInfoRatingStillYieldsToThePanelsDecisionRow() =>
        Assert.False(GalleryChromePlan.Resolve(Input(WorkView.Barebones, 1920, panelDecisions: true)).InfoRating);

    [Fact]
    public void TheViewerWiresTheInfoLineAndTheDwell()
    {
        string line = Source("Desktop/GalleryViewer.InfoLine.cs"), playback = Source("Desktop/GalleryViewer.Playback.cs"), viewer = Source("Desktop/GalleryViewer.cs");
        Assert.Contains("_infoOverlay.Child = BuildInfoContent();", viewer);
        // The line is a grid: segments left, status right; the warning and error lines stay below it.
        Assert.Contains("ColumnDefinitions = new ColumnDefinitions(\"*,Auto\")", line);
        Assert.Contains("content.Children.Add(_infoLine);", line);
        Assert.Contains("content.Children.Add(_favoriteError);", line);
        // The slideshow's status goes through the dwell rule; the timer is the viewer's, set from the setting.
        Assert.Contains("ApplyStatusDwell(_shownSlideshowStatus,", playback);
        Assert.Contains("GalleryInfoLine.IsTransient(status)", line);
        Assert.Contains("_statusDwellTimer.Interval = TimeSpan.FromMilliseconds(_statusDwellMs);", line);
        Assert.Contains("_gallery.StatusDwellMs = _config.GalleryStatusDwellMs;", Source("Desktop/Shell/MainWindow.GalleryPreferences.cs"));
        // Only the info block's own rule sets its visibility, and the segments' texts never go through ApplyChrome's old line.
        Assert.DoesNotContain("_infoOverlay.IsVisible = !_mediaFocus", Source("Desktop/GalleryViewer.Chrome.cs"));
    }

    [Fact]
    public void TheLayoutMenusInfoAndHintsRowsSayTheNewMeaning()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
        string info = Texts.T("gallery.layout.infoHelp"), hints = Texts.T("gallery.layout.hintsHelp");
        Assert.Contains("One line", info);
        Assert.Contains("rating button", info);
        Assert.Contains("status on the right", hints);
        Assert.Contains("fades out", hints);
    }

    private static string Source(string relative, [CallerFilePath] string source = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", relative)));
}

using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

/// <summary>
/// Focus, minimise and tray (Tom, 14.11.): the slideshow used to wait whenever the gallery's window was not the active one, with no way to
/// turn that off. The viewer reached the controller's hold only through its window check; these tests reproduce that and pin the setting.
/// </summary>
public sealed class GallerySlideshowFocusTests
{
    // The reproduction: both places that decided "the slideshow may run" asked the window for its focus unconditionally.
    [Fact]
    public void ReproductionTheViewersSlideshowRuleNoLongerDemandsTheWindowFocusUnconditionally()
    {
        string overlay = ReadSource("Desktop/GalleryViewer.Overlay.cs");
        string canRun = Between(overlay, "private bool CanRunSlideshow", ";");
        Assert.DoesNotContain("IsGalleryWindowActive", canRun);
        string tick = Between(overlay, "UpdateSlideshowInteraction(", ");");
        Assert.DoesNotContain("!active", tick);
        Assert.DoesNotContain("!_interactionAvailable", tick);
    }

    // ---- the rule: the viewer's tick in miniature (observe the window, renew after a focus wait, hold, tick) ----

    private sealed class Rig
    {
        public ManualTime Clock { get; } = new();
        public int Next { get; private set; }
        public GallerySlideshowController Controller { get; }
        public GallerySlideshowWindowGate Gate { get; } = new();
        public bool PauseOnFocusLoss { get; set; }
        public bool Surface { get; set; } = true;
        public bool Focused { get; set; } = true;
        public Rig()
        {
            Controller = new GallerySlideshowController(() => Next++, Clock);
            Controller.SetMode(GalleryPlaybackMode.Slideshow);
        }
        public void Image(long generation) { Controller.BeginMedia(generation, video: false, hasNext: true); Controller.ImageReady(generation); }
        public void Pass(TimeSpan elapsed, long generation = 1)
        {
            // The window's state is taken at the start of a tick, then time passes (a real tick is 250 ms, never a long jump).
            if (Gate.Observe(PauseOnFocusLoss, Surface, Focused) && Controller.IsRunning) Controller.RenewAfterHold(generation);
            Controller.SetAdvanceHeld(generation, !GallerySlideshowWindowGate.Allows(PauseOnFocusLoss, Surface, Focused));
            Clock.Advance(elapsed);
            Controller.Tick();
        }
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(false, true, false, true)]  // focus lost, minimised or in the tray: the same input, the window is not the active one
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, true, false)] // the surface (a disabled main window, the reader) stops it whatever the setting says
    [InlineData(false, false, false, false)]
    [InlineData(true, false, true, false)]
    public void TheWindowRuleIsTheSurfaceAndOnlyWithTheSettingTheFocus(bool pause, bool surface, bool focused, bool allowed)
        => Assert.Equal(allowed, GallerySlideshowWindowGate.Allows(pause, surface, focused));

    [Fact]
    public void WithTheSettingOffTheSlideshowKeepsRunningWhileAnotherWindowHasTheFocus()
    {
        Rig rig = new();
        rig.Image(1);
        rig.Controller.Start();
        rig.Focused = false; // the reproduction: this used to hold the slideshow
        rig.Pass(TimeSpan.FromSeconds(4));
        Assert.Equal(0, rig.Next);
        rig.Pass(TimeSpan.FromSeconds(1));
        Assert.Equal(1, rig.Next);
        Assert.True(rig.Controller.IsRunning);
        Assert.False(rig.Gate.FocusHeld);
    }

    [Fact]
    public void WithTheSettingOffMinimisingAndTheTrayDoNotStopItEither()
    {
        Rig rig = new();
        rig.Image(1);
        rig.Controller.Start();
        rig.Focused = false; // minimised: not active
        rig.Pass(TimeSpan.FromSeconds(5));
        Assert.Equal(1, rig.Next);
        rig.Image(2); // in the tray the window is hidden, not active, and the surface (the gallery in the window) is still usable
        rig.Pass(TimeSpan.FromMinutes(10), 2);
        Assert.Equal(2, rig.Next);
        Assert.True(rig.Controller.IsRunning);
    }

    [Fact]
    public void WithTheSettingOnItWaitsWhileTheFocusIsAwayAndNeverMovesOn()
    {
        Rig rig = new() { PauseOnFocusLoss = true };
        rig.Image(1);
        rig.Controller.Start();
        rig.Pass(TimeSpan.FromSeconds(1));
        rig.Focused = false;
        rig.Pass(TimeSpan.FromMinutes(30));
        Assert.Equal(0, rig.Next);
        Assert.True(rig.Controller.IsRunning); // paused by the hold, not stopped
        Assert.True(rig.Gate.FocusHeld);
    }

    [Fact]
    public void WithTheSettingOnComingBackShowsTheCurrentMediumForAFullPeriod()
    {
        Rig rig = new() { PauseOnFocusLoss = true };
        rig.Image(1);
        rig.Controller.Start();
        rig.Pass(TimeSpan.FromSeconds(4.9)); // 0.1 s left
        rig.Focused = false;
        rig.Pass(TimeSpan.FromMinutes(5));
        rig.Focused = true;
        rig.Pass(TimeSpan.FromSeconds(0.2));
        Assert.Equal(0, rig.Next); // not skipped by the 0.1 s that were left
        rig.Pass(TimeSpan.FromSeconds(4.7));
        Assert.Equal(0, rig.Next);
        rig.Pass(TimeSpan.FromSeconds(0.2));
        Assert.Equal(1, rig.Next);
    }

    [Fact]
    public void WithTheSettingOnAVideoThatEndedWhileAwayWaitsAnImagePeriodAfterTheReturn()
    {
        Rig rig = new() { PauseOnFocusLoss = true };
        rig.Controller.BeginMedia(1, video: true, hasNext: true);
        rig.Controller.VideoReady(1);
        rig.Controller.Start();
        rig.Focused = false;
        rig.Pass(TimeSpan.FromSeconds(3));
        rig.Controller.VideoEnded(1); // the video plays on in the window and ends while the slideshow waits
        rig.Pass(TimeSpan.FromMinutes(1));
        Assert.Equal(0, rig.Next);
        rig.Focused = true;
        rig.Pass(TimeSpan.FromSeconds(1));
        Assert.Equal(0, rig.Next);
        rig.Pass(TimeSpan.FromSeconds(4));
        Assert.Equal(1, rig.Next);
    }

    [Fact]
    public void TurningTheSettingOffWhileWaitingLetsItGoOnWithAFreshPeriod()
    {
        Rig rig = new() { PauseOnFocusLoss = true };
        rig.Image(1);
        rig.Controller.Start();
        rig.Pass(TimeSpan.FromSeconds(4.9));
        rig.Focused = false;
        rig.Pass(TimeSpan.FromMinutes(1));
        Assert.Equal(0, rig.Next);
        rig.PauseOnFocusLoss = false;
        rig.Pass(TimeSpan.FromSeconds(1));
        Assert.Equal(0, rig.Next);
        rig.Pass(TimeSpan.FromSeconds(4));
        Assert.Equal(1, rig.Next);
        Assert.False(rig.Gate.FocusHeld);
    }

    [Fact]
    public void ADisabledSurfaceStopsItEvenWithTheSettingOffAndIsNoFocusWait()
    {
        Rig rig = new();
        rig.Image(1);
        rig.Controller.Start();
        rig.Surface = false;
        rig.Focused = false;
        rig.Pass(TimeSpan.FromMinutes(1));
        Assert.Equal(0, rig.Next);
        Assert.False(rig.Gate.FocusHeld);
        rig.Surface = true;
        rig.Focused = true;
        rig.Pass(TimeSpan.FromSeconds(5));
        Assert.Equal(1, rig.Next);
    }

    [Fact]
    public void TheTagHoldIsUnchangedByTheSettingAndStillWaitsWithItOff()
    {
        Rig rig = new();
        rig.Image(1);
        rig.Controller.Start();
        rig.Pass(TimeSpan.FromSeconds(4.5));
        rig.Controller.SetAdvanceHeld(1, true); // the tag panel is worked in
        rig.Clock.Advance(TimeSpan.FromMinutes(3));
        rig.Controller.Tick();
        Assert.Equal(0, rig.Next);
        rig.Controller.RenewAfterHold(1);
        rig.Pass(TimeSpan.FromSeconds(1)); // the panel hold ends; the window rule allows
        Assert.Equal(0, rig.Next);
        rig.Pass(TimeSpan.FromSeconds(4));
        Assert.Equal(1, rig.Next);
    }

    // ---- the viewer wires the rule (source contracts: the viewer needs a window) ----

    [Fact]
    public void TheViewerUsesTheGateForTheRunRuleTheStatusTheTickAndTheVideoEnd()
    {
        string overlay = ReadSource("Desktop/GalleryViewer.Overlay.cs");
        string playback = ReadSource("Desktop/GalleryViewer.Playback.cs");
        Assert.Contains("GallerySlideshowWindowGate.Allows(_playbackPreferences.PauseOnFocusLoss, SlideshowSurfaceUsable, WindowHasFocus)", overlay);
        Assert.Contains("CanRunSlideshow => !_viewerInteractionStopped && SlideshowWindowAllows", overlay);
        Assert.Contains("ObserveSlideshowWindow();\n        UpdateSlideshowInteraction(!SlideshowWindowAllows", overlay.Replace("\r\n", "\n"));
        Assert.Contains("SlideshowWindowAllows && !_videoSuspended, false, Tagging: IsTagMode", playback);
        Assert.Contains("ObserveSlideshowWindow();\n            _slideshow.SetAdvanceHeld(generation, IsSlideshowAdvanceBlocked)", playback.Replace("\r\n", "\n"));
        // Minimise and the tray leave the window's own chain visible below it; a hidden window must not keep a new image from becoming ready.
        Assert.Contains("!ShownInWindow(_image)", playback);
        Assert.Contains("ShownInWindow(this)", overlay);
    }

    [Fact]
    public void TheMainWindowTellsTheViewerTheSurfaceApartFromTheFocus()
    {
        string source = ReadSource("Desktop/Shell/MainWindow.GalleryPreferences.cs");
        Assert.Contains("SetInteractionAvailable(activeSurface && IsEnabled && !_gallerySuspended, IsEnabled && !_gallerySuspended)", source);
        Assert.Contains("SetInteractionAvailable(false);", ReadSource("Desktop/Shell/MainWindow.Gallery.cs")); // the reader still stops it
    }

    [Fact]
    public void TheSettingIsARowOfTheSlideshowSettingsPanelAndThatPanelIsTheFlyoutsContent()
    {
        string playback = ReadSource("Desktop/GalleryViewer.Playback.cs");
        Assert.Contains("AddOverflowItem(_pauseOnFocusLossToggle, \"gallery.slideshow-pause-on-focus-loss\", check: true, keepOpen: true,", playback);
        Assert.Contains("host: _slideshowHome", Between(playback, "AddOverflowItem(_pauseOnFocusLossToggle", ");"));
        Assert.Contains("PauseOnFocusLoss = !_playbackPreferences.PauseOnFocusLoss", playback);
        Assert.Contains("_slideshowLoan = new PanelLoan(_overflowPanel, _slideshowHome, _slideshowFlyoutHost)", ReadSource("Desktop/GalleryViewer.SlideshowMenu.cs"));
        // The layout menu, the dock, the filmstrip and the tool row are not involved.
        Assert.DoesNotContain("PauseOnFocusLoss", ReadSource("Desktop/GalleryViewer.LayoutMenu.cs"));
        Assert.DoesNotContain("PauseOnFocusLoss", ReadSource("Desktop/GalleryViewer.Filmstrip.cs"));
        Assert.DoesNotContain("PauseOnFocusLoss", ReadSource("Desktop/GalleryViewer.WorkView.cs"));
    }

    [Fact]
    public void VideoPlaybackHasNoDeactivationPauseOfItsOwn()
    {
        // The player only cancels a running seek gesture when its window is deactivated; it never pauses playback.
        string player = ReadSource("Desktop/GalleryVideoPlayer.cs");
        Assert.Contains("private void OnWindowDeactivated(object? sender, EventArgs e) => CancelSeekGesture();", player);
    }

    // ---- the profile choice ----

    [Fact]
    public void TheChoiceIsOffByDefaultAndRoundTripsThroughTheProfileAndThePreferences()
    {
        Assert.False(new Config().GalleryPauseOnFocusLoss);
        Assert.False(new GalleryPlaybackPreferences().PauseOnFocusLoss);
        string dir = Directory.CreateTempSubdirectory("meowchan-focus-pause-").FullName;
        try
        {
            Config config = Config.LoadConfig(dir);
            var edited = GalleryPlaybackPreferences.FromConfig(config) with { PauseOnFocusLoss = true, ImageSeconds = 9 };
            edited.ApplyChangesTo(config, GalleryPlaybackPreferences.FromConfig(config));
            config.SaveConfig(dir);
            Config reloaded = Config.LoadConfig(dir);
            Assert.True(reloaded.GalleryPauseOnFocusLoss);
            Assert.Equal(edited, GalleryPlaybackPreferences.FromConfig(reloaded));
            Assert.True(reloaded.Clone().GalleryPauseOnFocusLoss);
            // The other direction: an unchanged draft of the settings page leaves a newer gallery choice alone.
            Config live = new() { GalleryPauseOnFocusLoss = true };
            // (the page's draft carries the value it was opened with, see CaptureGalleryPlaybackDraft)
            var opened = GalleryPlaybackPreferences.FromConfig(live) with { PauseOnFocusLoss = false };
            (opened with { ImageSeconds = 12 }).ApplyChangesTo(live, opened);
            Assert.True(live.GalleryPauseOnFocusLoss);
            Assert.Equal(12, live.GallerySlideshowSeconds);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("\"banana\"")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("42")]
    public void ADamagedValueFallsBackToOffAndKeepsTheOtherSettings(string damaged)
    {
        string dir = Directory.CreateTempSubdirectory("meowchan-focus-pause-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"),
                "{\"VideoVolume\":29,\"GallerySlideshowSeconds\":7,\"GalleryPauseOnFocusLoss\":" + damaged + ",\"GalleryVideoRepeat\":true}");
            Config config = Config.LoadConfig(dir);
            Assert.Null(config.LoadWarning);
            Assert.False(config.GalleryPauseOnFocusLoss);
            Assert.Equal(29, config.VideoVolume);
            Assert.Equal(7, config.GallerySlideshowSeconds);
            Assert.True(config.GalleryVideoRepeat);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void TheChoiceIsPartOfTheSettingsExportAndComesBackFromIt()
    {
        Config source = new() { GalleryPauseOnFocusLoss = true, GallerySlideshowSeconds = 11 };
        JsonObject export = SettingsTransfer.Export(source, "1.1.0");
        Assert.True(export["GalleryPauseOnFocusLoss"]!.GetValue<bool>());
        Config restored = JsonSerializer.Deserialize(export.ToJsonString(), ConfigJsonContext.Default.Config)!;
        Assert.True(restored.GalleryPauseOnFocusLoss);
        Assert.Equal(11, restored.GallerySlideshowSeconds);
        Assert.False(SettingsTransfer.Export(new Config(), "1.1.0")["GalleryPauseOnFocusLoss"]!.GetValue<bool>());
    }

    [Fact]
    public void TheNameIsTranslatedInEveryLanguagePackAndTheHelpMentionsFocusMinimiseAndTray()
    {
        foreach (string code in new[] { "cs", "es", "fr", "it", "ja", "ko", "pl", "pt-BR", "ru", "zh-Hans" })
        {
            using JsonDocument pack = JsonDocument.Parse(ReadSource("Desktop/Localization/Packs/" + code + ".json"));
            string name = pack.RootElement.GetProperty("strings").GetProperty("viewer.playback.pauseOnFocusLoss").GetString()!;
            string help = pack.RootElement.GetProperty("strings").GetProperty("viewer.playback.pauseOnFocusLossHelp").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(name), code);
            Assert.NotEqual("Pause when MeowChan loses focus", name);
            Assert.Contains("MeowChan", name);
            Assert.True(help.Length > 60, code);
        }
        var entries = MeowChan.Desktop.Localization.GalleryPlaybackCatalog.Entries;
        Assert.Contains(entries, entry => entry.Key == "viewer.playback.pauseOnFocusLoss" && entry.German == "Bei Fokusverlust pausieren");
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan duration) => _timestamp = checked(_timestamp + duration.Ticks);
    }

    internal static string Between(string text, string start, string end)
    {
        int from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, start);
        int to = text.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to > from, end);
        return text.Substring(from, to - from);
    }

    internal static string ReadSource(string path)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, path);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException(path);
    }
}

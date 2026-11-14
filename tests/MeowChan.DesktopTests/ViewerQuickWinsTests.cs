using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>
/// Viewer quick wins (UX viewer review QW-1…QW-10 and the per-video slideshow rule): the slideshow's video rule with a fake
/// clock, the read migration of the old "Repeat" playback mode, the new settings with default/minimum/maximum, the shared idle
/// time and the new gallery shortcuts.
/// </summary>
[Collection("Localization")]
public sealed class ViewerQuickWinsTests : IDisposable
{
    public ViewerQuickWinsTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    // ---- slideshow video rule (fake clock) ----

    [Fact]
    public void PlayOnceAdvancesAtTheFirstNaturalEnd()
    {
        VideoFixture f = new(SlideshowVideoRule.PlayOnce);
        f.Play(TimeSpan.FromSeconds(3));
        f.Controller.VideoEnded(1);
        Assert.Equal(1, f.Next);
        Assert.Equal(0, f.Replays);
    }

    [Fact]
    public void LoopAtLeastPlaysOutThePassThatCrossesTheLimit()
    {
        // A 3 s clip and at least 10 s: passes end at 3, 6 and 9 s (below 10, replay); the fourth pass crosses 10 s and
        // plays to its end at 12 s, then the slideshow advances.
        VideoFixture f = new(SlideshowVideoRule.LoopAtLeastSeconds, atLeastSeconds: 10);
        for (int pass = 1; pass <= 3; pass++)
        {
            f.Play(TimeSpan.FromSeconds(3));
            f.Controller.VideoEnded(1);
            Assert.Equal(pass, f.Replays);
            Assert.Equal(0, f.Next);
        }
        f.Play(TimeSpan.FromSeconds(1));
        Assert.Equal(0, f.Next); // Crossing the limit mid-pass never cuts the pass short.
        f.Play(TimeSpan.FromSeconds(2));
        f.Controller.VideoEnded(1);
        Assert.Equal(1, f.Next);
        Assert.Equal(3, f.Replays);
        Assert.Equal(4, f.Controller.VideoPasses);
    }

    [Fact]
    public void AVideoLongerThanTheMinimumPlaysOnce()
    {
        VideoFixture f = new(SlideshowVideoRule.LoopAtLeastSeconds, atLeastSeconds: 10);
        f.Play(TimeSpan.FromSeconds(15));
        f.Controller.VideoEnded(1);
        Assert.Equal(1, f.Next);
        Assert.Equal(0, f.Replays);
    }

    [Fact]
    public void RepeatTimesPlaysTheConfiguredPassesThenAdvances()
    {
        VideoFixture f = new(SlideshowVideoRule.RepeatTimes, repeats: 3);
        for (int pass = 1; pass <= 2; pass++)
        {
            f.Play(TimeSpan.FromSeconds(1));
            f.Controller.VideoEnded(1);
            Assert.Equal(pass, f.Replays);
        }
        f.Play(TimeSpan.FromSeconds(1));
        f.Controller.VideoEnded(1);
        Assert.Equal(1, f.Next);
        Assert.Equal(2, f.Replays);
    }

    [Fact]
    public void PausedAndHeldTimeDoesNotCountAsPlayedTime()
    {
        VideoFixture f = new(SlideshowVideoRule.LoopAtLeastSeconds, atLeastSeconds: 10);
        f.Play(TimeSpan.FromSeconds(4));
        f.Controller.SetAdvanceHeld(1, true); // e.g. the "⋯" menu is open
        f.Play(TimeSpan.FromMinutes(5));
        f.Controller.SetAdvanceHeld(1, false);
        f.Controller.Pause();
        f.Play(TimeSpan.FromMinutes(5));
        f.Controller.Start();
        f.Play(TimeSpan.FromSeconds(4));
        Assert.Equal(TimeSpan.FromSeconds(8), f.Controller.VideoPlayed);
        f.Controller.VideoEnded(1);
        Assert.Equal(1, f.Replays); // 8 s < 10 s: another pass.
        Assert.Equal(0, f.Next);
    }

    [Fact]
    public void APassThatEndsWhilePausedNeitherReplaysNorAdvances()
    {
        VideoFixture f = new(SlideshowVideoRule.RepeatTimes, repeats: 5);
        f.Play(TimeSpan.FromSeconds(2));
        f.Controller.Pause();
        f.Controller.VideoEnded(1);
        Assert.Equal(0, f.Replays);
        Assert.Equal(0, f.Next);
        f.Controller.Start(); // An observed end is consumed; a replay needs a new selection (existing behaviour).
        f.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, f.Next);
    }

    [Fact]
    public void ManualNavigationWinsAndTheNextVideoStartsItsRuleFromZero()
    {
        VideoFixture f = new(SlideshowVideoRule.RepeatTimes, repeats: 2);
        f.Play(TimeSpan.FromSeconds(2));
        f.Controller.VideoEnded(1);
        Assert.Equal(1, f.Replays);
        // The user moves on: a new selection (generation 2) replaces the counters; a late end of generation 1 is ignored.
        f.Controller.BeginMedia(2, video: true, hasNext: true);
        f.Controller.VideoReady(2);
        f.Controller.VideoEnded(1);
        Assert.Equal(0, f.Controller.VideoPasses);
        f.Play(TimeSpan.FromSeconds(2));
        f.Controller.VideoEnded(2);
        Assert.Equal(2, f.Replays);
        Assert.Equal(0, f.Next);
        f.Play(TimeSpan.FromSeconds(2));
        f.Controller.VideoEnded(2);
        Assert.Equal(1, f.Next);
    }

    [Fact]
    public void WithoutAReplayCallbackEveryRuleDegradesToPlayOnce()
    {
        int next = 0;
        GallerySlideshowController controller = new(() => next++, new ManualTime());
        controller.SetMode(GalleryPlaybackMode.Slideshow);
        controller.SetVideoRule(SlideshowVideoRule.RepeatTimes, TimeSpan.FromSeconds(10), 5);
        controller.BeginMedia(1, video: true, hasNext: true);
        controller.Start();
        controller.VideoEnded(1);
        Assert.Equal(1, next);
    }

    [Fact]
    public void ImagesKeepTheirSecondsWhateverTheVideoRule()
    {
        ManualTime clock = new();
        int next = 0;
        GallerySlideshowController controller = new(() => next++, clock, () => { });
        controller.SetMode(GalleryPlaybackMode.Slideshow);
        controller.SetVideoRule(SlideshowVideoRule.LoopAtLeastSeconds, TimeSpan.FromSeconds(60), 1);
        controller.BeginMedia(1, video: false, hasNext: true);
        controller.ImageReady(1);
        controller.Start();
        clock.Advance(TimeSpan.FromSeconds(5));
        controller.Tick();
        Assert.Equal(1, next);
    }

    [Fact]
    public void InvalidRuleParametersAreRejected()
    {
        GallerySlideshowController controller = new(() => { });
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.SetVideoRule(SlideshowVideoRule.RepeatTimes, TimeSpan.FromSeconds(1), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.SetVideoRule(SlideshowVideoRule.LoopAtLeastSeconds, TimeSpan.Zero, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => controller.SetVideoRule((SlideshowVideoRule)42, TimeSpan.FromSeconds(1), 1));
    }

    // ---- configuration: read migration and new settings ----

    [Fact]
    public void AnOldRepeatModeIsReadAsManualWithRepeatVideoAndWrittenOnlyOnTheNextSave()
    {
        using Profile profile = new();
        string file = Path.Combine(profile.Path, "settings.json");
        File.WriteAllText(file, "{\"GalleryPlaybackMode\":\"Repeat\",\"GallerySlideshowSeconds\":7}");
        Config config = Config.LoadConfig(profile.Path);
        Assert.Equal("Manual", config.GalleryPlaybackMode);
        Assert.True(config.GalleryVideoRepeat);
        Assert.Equal(new GalleryPlaybackPreferences(GalleryPlaybackMode.Manual, 7, VideoRepeat: true), GalleryPlaybackPreferences.FromConfig(config));
        // Loading alone never rewrites the profile.
        Assert.Contains("\"Repeat\"", File.ReadAllText(file));
        config.SaveConfig(profile.Path);
        string saved = File.ReadAllText(file);
        Assert.DoesNotContain("\"Repeat\"", saved);
        Config reloaded = Config.LoadConfig(profile.Path);
        Assert.Equal("Manual", reloaded.GalleryPlaybackMode);
        Assert.True(reloaded.GalleryVideoRepeat);
    }

    [Fact]
    public void ASlideshowProfileKeepsItsModeAndGetsTheVideoDefaults()
    {
        Config config = new() { GalleryPlaybackMode = "Slideshow" };
        GalleryPlaybackPreferences prefs = GalleryPlaybackPreferences.FromConfig(config);
        Assert.Equal(GalleryPlaybackMode.Slideshow, prefs.Mode);
        Assert.False(prefs.VideoRepeat);
        Assert.Equal(SlideshowVideoRule.PlayOnce, prefs.VideoRule);
        Assert.Equal(10, prefs.VideoSeconds);
        Assert.Equal(2, prefs.VideoRepeats);
    }

    [Fact]
    public void TheVideoRuleNumbersHaveDefaultMinimumAndMaximum()
    {
        Assert.Equal((10, 1, 600), (Config.GallerySlideshowVideoSecondsDefault, Config.GallerySlideshowVideoSecondsMinimum, Config.GallerySlideshowVideoSecondsMaximum));
        Assert.Equal((2, 1, 50), (Config.GallerySlideshowVideoRepeatsDefault, Config.GallerySlideshowVideoRepeatsMinimum, Config.GallerySlideshowVideoRepeatsMaximum));
        Config config = new() { GallerySlideshowVideoSeconds = 0, GallerySlideshowVideoRepeats = 1000 };
        Assert.Equal(1, config.GallerySlideshowVideoSeconds);
        Assert.Equal(50, config.GallerySlideshowVideoRepeats);
        config.GallerySlideshowVideoRule = "nonsense";
        Assert.Equal("PlayOnce", config.GallerySlideshowVideoRule);
        config.GallerySlideshowVideoRule = "1";
        Assert.Equal("PlayOnce", config.GallerySlideshowVideoRule);
        config.GallerySlideshowVideoRule = " repeattimes ";
        Assert.Equal("RepeatTimes", config.GallerySlideshowVideoRule);
    }

    [Fact]
    public void TheVideoChoicesRoundTripThroughTheProfile()
    {
        using Profile profile = new();
        Config config = Config.LoadConfig(profile.Path);
        var prefs = new GalleryPlaybackPreferences(GalleryPlaybackMode.Slideshow, 4, VideoRepeat: true,
            VideoRule: SlideshowVideoRule.LoopAtLeastSeconds, VideoSeconds: 42, VideoRepeats: 7);
        prefs.ApplyTo(config);
        config.ControlsIdleHideMs = 4500;
        config.SaveConfig(profile.Path);
        Config loaded = Config.LoadConfig(profile.Path);
        Assert.Equal(prefs, GalleryPlaybackPreferences.FromConfig(loaded));
        Assert.Equal(4500, loaded.ControlsIdleHideMs);
        Assert.Equal(4500, loaded.Clone().ControlsIdleHideMs);
        Config copy = new();
        copy.CopyFrom(loaded);
        Assert.Equal(prefs, GalleryPlaybackPreferences.FromConfig(copy));
        Assert.Equal(4500, copy.ControlsIdleHideMs);
    }

    [Fact]
    public void UnchangedVideoDraftFieldsKeepNewerLiveChoices()
    {
        Config live = new();
        Config draft = live.Clone(); // baseline: defaults
        draft.GallerySlideshowVideoSeconds = 30;
        live.GalleryVideoRepeat = true; // a newer choice from the gallery
        live.CopySettingsFieldsFrom(draft);
        Assert.True(live.GalleryVideoRepeat);
        Assert.Equal(30, live.GallerySlideshowVideoSeconds);
    }

    [Fact]
    public void HideControlsAfterIsALimitWithDefaultMinimumAndMaximum()
    {
        Config defaults = new();
        Assert.Equal(2000, defaults.ControlsIdleHideMs);
        Assert.Equal((500, 30_000), (Config.ControlsIdleHideMsMinimum, Config.ControlsIdleHideMsMaximum));
        defaults.Validate();
        foreach (int valid in new[] { 500, 30_000 })
        {
            Config candidate = defaults.Clone();
            candidate.ControlsIdleHideMs = valid;
            candidate.Validate();
            Config target = new();
            target.CopySettingsFieldsFrom(candidate);
            Assert.Equal(valid, target.ControlsIdleHideMs);
        }
        foreach (int invalid in new[] { 499, 30_001, int.MinValue, int.MaxValue })
        {
            Config candidate = defaults.Clone();
            candidate.ControlsIdleHideMs = invalid;
            Assert.Throws<ArgumentOutOfRangeException>(() => candidate.Validate());
            Assert.Throws<ArgumentOutOfRangeException>(() => new Config().CopySettingsFieldsFrom(candidate));
        }
        // The reader's arrow cross uses the same value and bounds.
        Assert.Equal(Config.ControlsIdleHideMsDefault, ReaderNavigationLayout.IdleHideDelayMsDefault);
        Assert.Equal(Config.ControlsIdleHideMsMinimum, ReaderNavigationLayout.IdleHideDelayMsMinimum);
        Assert.Equal(Config.ControlsIdleHideMsMaximum, ReaderNavigationLayout.IdleHideDelayMsMaximum);
    }

    [Theory]
    [InlineData(1999, 2000, false)]
    [InlineData(2000, 2000, true)]
    [InlineData(600, 500, true)]
    [InlineData(400, 100, false)] // clamped up to the 500 ms minimum
    [InlineData(29_999, 30_000, false)]
    public void GalleryOverlayControlsHideAfterTheConfiguredIdleTime(int idleMs, int settingMs, bool hides)
    {
        var activity = new GalleryOverlayActivity(Overlay: true, AutoHide: true, Available: true, ActiveWindow: true,
            Focused: false, Captured: false, Popup: false, Reading: false);
        Assert.Equal(hides, activity.CanHide(TimeSpan.FromMilliseconds(idleMs), settingMs));
    }

    // ---- shortcuts ----

    [Theory]
    [InlineData("video.seek-back", "Primary+Left")]
    [InlineData("video.seek-forward", "Primary+Right")]
    [InlineData("video.seek-back-far", "Primary+Shift+Left")]
    [InlineData("video.seek-forward-far", "Primary+Shift+Right")]
    [InlineData("video.mute", "Shift+M")]
    [InlineData("video.snapshot", "Shift+S")]
    [InlineData("video.speed-down", "OemOpenBrackets")]
    [InlineData("video.speed-up", "OemCloseBrackets")]
    [InlineData("video.speed-reset", "Back")]
    public void TheNewGalleryCommandsHaveTheirDefaultsAndStayInTheGallery(string id, string shortcut)
    {
        CommandDefinition command = CommandCatalog.Get(id);
        Assert.Equal(shortcut, command.Shortcut);
        Assert.Equal(new[] { "gallery" }, EffectiveShortcutBindings.ExecutionContexts(id));
        foreach (ShortcutPlatform platform in Enum.GetValues<ShortcutPlatform>())
            Assert.NotEmpty(EffectiveShortcutBindings.Build(null, platform).For(id));
    }

    [Fact]
    public void TheFavoriteKeysOfTheShortcutConceptStayAndSpeedHasALayoutSafeSecondGesture()
    {
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(null, ShortcutPlatform.Linux);
        Assert.Contains(bindings.For("favorites.add"), gesture => gesture.Key == "M" && gesture.Modifiers.Count == 0);
        Assert.Contains(bindings.For("gallery.slideshow-toggle"), gesture => gesture.Key == "S" && gesture.Modifiers.Count == 0);
        Assert.Contains(bindings.For("video.speed-down"), gesture => gesture.Key == "OemComma" && gesture.Modifiers.SequenceEqual(new[] { "Shift" }));
        Assert.Contains(bindings.For("video.speed-up"), gesture => gesture.Key == "OemPeriod" && gesture.Modifiers.SequenceEqual(new[] { "Shift" }));
        // Left/Right without a modifier stay previous/next medium.
        Assert.Contains(bindings.For("gallery.previous"), gesture => gesture.Key == "Left" && gesture.Modifiers.Count == 0);
    }

    [Fact]
    public void BracketsAndBackspaceHaveReadableNames()
    {
        Assert.Equal("[", ShortcutGesture.Parse("OemOpenBrackets").Display(ShortcutPlatform.Linux));
        Assert.Equal("]", ShortcutGesture.Parse("OemCloseBrackets").Display(ShortcutPlatform.MacOS));
        Assert.Equal("Backspace", ShortcutGesture.Parse("Back").Display(ShortcutPlatform.Windows));
        Texts.SetLanguage("de");
        Assert.Equal("Rücktaste", ShortcutGesture.Parse("Back").Display(ShortcutPlatform.Windows));
        Assert.Equal("Strg+Umschalt+Left", ShortcutGesture.Parse("Primary+Shift+Left").Display(ShortcutPlatform.Linux));
        Assert.True(EffectiveShortcutBindings.IsSingleCharacterKey(ShortcutGesture.Parse("OemOpenBrackets")));
    }

    [Fact]
    public void SingleKeysOffRemovesTheBareSpeedAndShiftLetterDefaultsButKeepsSeeking()
    {
        EffectiveShortcutBindings off = EffectiveShortcutBindings.Build(new ShortcutBindingsSection().WithSingleKeyShortcuts(false), ShortcutPlatform.Linux);
        Assert.Empty(off.For("video.speed-down"));
        Assert.Empty(off.For("video.mute"));
        Assert.NotEmpty(off.For("video.seek-back"));
        Assert.NotEmpty(off.For("video.speed-reset"));
    }

    [Fact]
    public void ASavedBindingOfAnotherGalleryCommandWinsOverANewSeekDefault()
    {
        // A profile that bound Ctrl+Left to "previous medium" before the seek default existed keeps it.
        ShortcutBindingsSection section = new ShortcutBindingsSection().Set("gallery.previous",
            new[] { ShortcutGesture.Parse("Primary+Left") });
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        Assert.Contains(bindings.For("gallery.previous"), gesture => gesture.Key == "Left" && gesture.Modifiers.SequenceEqual(new[] { "Primary" }));
        Assert.Empty(bindings.For("video.seek-back"));
        Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave);
    }

    // ---- the gallery's way back and saved frames ----

    [Theory]
    [InlineData(0, "Close gallery")]
    [InlineData(1, "To media")]
    [InlineData(2, "Back to thread")]
    [InlineData(3, "Back to Archive")]
    [InlineData(4, "Back to Downloads")]
    public void TheWayBackNamesItsTarget(int value, string english)
    {
        var target = (GalleryReturnTarget)value;
        string key = target switch
        {
            GalleryReturnTarget.Media => "command.gallery.close.media", GalleryReturnTarget.Thread => "command.gallery.close.thread",
            GalleryReturnTarget.Archive => "command.gallery.close.archive", GalleryReturnTarget.Downloads => "command.gallery.close.downloads",
            _ => "command.gallery.close"
        };
        Assert.Equal(english, Texts.T(key));
    }

    [Fact]
    public void ASavedFrameNeverOverwritesAFile()
    {
        using Profile profile = new();
        string first = GalleryFrameFile.Path(profile.Path, "clip:one.webm", 65_432);
        Assert.Equal(Path.Combine(profile.Path, "clip_one-1-05-432.png"), first);
        File.WriteAllText(first, "x");
        string second = GalleryFrameFile.Path(profile.Path, "clip:one.webm", 65_432);
        Assert.Equal(Path.Combine(profile.Path, "clip_one-1-05-432 (2).png"), second);
        Assert.Equal(Path.Combine(profile.Path, "frame-1-00-00-000.png"), GalleryFrameFile.Path(profile.Path, null, 3_600_000));
        string longName = GalleryFrameFile.Path(profile.Path, new string('a', 300) + ".mp4", 0);
        Assert.True(Path.GetFileName(longName).Length <= GalleryFrameFile.MaximumStemLength + 20);
    }

    [Fact]
    public void SpeedLabelsFollowTheInterfaceLanguage()
    {
        Assert.Equal(new[] { 0.5f, 1f, 1.5f, 2f }, GalleryVideoPlayer.Rates);
        Assert.Equal("1.5×", GalleryVideoPlayer.RateLabel(1.5f));
        Texts.SetLanguage("de");
        Assert.Equal("1,5×", GalleryVideoPlayer.RateLabel(1.5f));
        Assert.Equal("2×", GalleryVideoPlayer.RateLabel(2f));
    }

    private sealed class VideoFixture
    {
        public ManualTime Clock { get; } = new();
        public int Next { get; private set; }
        public int Replays { get; private set; }
        public GallerySlideshowController Controller { get; }
        public VideoFixture(SlideshowVideoRule rule, int atLeastSeconds = 10, int repeats = 2)
        {
            Controller = new GallerySlideshowController(() => Next++, Clock, () => Replays++);
            Controller.SetMode(GalleryPlaybackMode.Slideshow);
            Controller.SetVideoRule(rule, TimeSpan.FromSeconds(atLeastSeconds), repeats);
            Controller.BeginMedia(1, video: true, hasNext: true);
            Controller.VideoReady(1);
            Controller.Start();
        }
        public void Play(TimeSpan elapsed) => Clock.Advance(elapsed);
        public void Advance(TimeSpan elapsed) { Clock.Advance(elapsed); Controller.Tick(); }
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => throw new InvalidOperationException("The caller owns Tick; slideshow must not create timers.");
        public void Advance(TimeSpan duration) => _timestamp = checked(_timestamp + duration.Ticks);
    }

    private sealed class Profile : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("meowchan-viewer-qw-").FullName;
        public void Dispose() => Directory.Delete(Path, true);
    }
}

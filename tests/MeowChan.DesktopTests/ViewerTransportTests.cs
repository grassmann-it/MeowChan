using System.Runtime.CompilerServices;
using System.Text.Json;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

/// <summary>
/// Plan V1, the video transport row: the A–B loop state machine with a fake clock, the speed lists (click cycle and the
/// 0.25×–4× choices), audio track selection, the new commands and their defaults, the work-view row of the player menu and the
/// texts in all twelve languages. The row itself needs a platform (icons, LibVLC); its visual proof is the central smoke run.
/// </summary>
[Collection("Localization")]
public sealed class ViewerTransportTests : IDisposable
{
    public ViewerTransportTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    // ---- A–B loop ----

    [Fact]
    public void TheFirstPressSetsATheSecondSetsBAndTheThirdTurnsTheLoopOff()
    {
        VideoAbLoop loop = new();
        Assert.Equal(VideoAbLoopPress.StartSet, loop.Press(4_000, 12_000));
        Assert.Equal(VideoAbLoopState.StartSet, loop.State);
        Assert.False(loop.IsActive);
        Assert.False(loop.ShouldReturn(11_000)); // A alone never loops.
        Assert.Equal(VideoAbLoopPress.Active, loop.Press(9_000, 12_000));
        Assert.Equal((4_000L, 9_000L), (loop.Start, loop.End));
        Assert.True(loop.IsActive);
        Assert.Equal(VideoAbLoopPress.Cleared, loop.Press(6_000, 12_000));
        Assert.Equal(VideoAbLoopState.Off, loop.State);
        Assert.False(loop.ShouldReturn(9_500));
    }

    [Fact]
    public void BPressedBeforeAInTimeIsOrderedAndPositionsStayInsideTheMedium()
    {
        VideoAbLoop loop = new();
        loop.Press(9_000, 12_000);
        loop.Press(4_000, 12_000);
        Assert.Equal((4_000L, 9_000L), (loop.Start, loop.End));
        loop.Clear();
        loop.Press(-50, 12_000);
        loop.Press(99_000, 12_000);
        Assert.Equal((0L, 12_000L), (loop.Start, loop.End));
    }

    [Fact]
    public void ALoopShorterThanTheMinimumIsRefusedAndKeepsA()
    {
        VideoAbLoop loop = new();
        loop.Press(4_000, 12_000);
        Assert.Equal(VideoAbLoopPress.TooShort, loop.Press(4_000 + VideoAbLoop.MinimumSpanMs - 1, 12_000));
        Assert.Equal(VideoAbLoopState.StartSet, loop.State);
        Assert.Equal(4_000L, loop.Start);
        Assert.Equal(VideoAbLoopPress.Active, loop.Press(4_000 + VideoAbLoop.MinimumSpanMs, 12_000));
        Assert.Equal(200L, VideoAbLoop.MinimumSpanMs); // Documented fixed minimum (plan V1), not a setting.
    }

    [Fact]
    public void ReachingBReturnsToAAndTheReturnIsScheduledAheadOfTheNextTimeReport()
    {
        VideoAbLoop loop = new();
        loop.Press(4_000, 12_000);
        loop.Press(9_000, 12_000);
        // A fake clock of time reports, four per second as VLC usually sends them.
        long? scheduledAt = null;
        for (long now = 4_000; now < 9_000 && scheduledAt == null; now += 250)
        {
            Assert.False(loop.ShouldReturn(now));
            if (loop.JumpDelay(now, 1f) is long delay) scheduledAt = now + delay;
        }
        // The return is scheduled exactly at B, not a whole report later.
        Assert.Equal(9_000L, scheduledAt);
        Assert.True(loop.ShouldReturn(9_000));
        Assert.True(loop.ShouldReturn(9_200));
        Assert.Null(loop.JumpDelay(5_000, 1f)); // Far from B: nothing is scheduled.
    }

    [Fact]
    public void TheScheduledReturnFollowsThePlaybackSpeed()
    {
        VideoAbLoop loop = new();
        loop.Press(1_000, 20_000);
        loop.Press(10_000, 20_000);
        Assert.Equal(150L, loop.JumpDelay(9_700, 2f)); // 300 ms of media at 2× are 150 ms of wall clock.
        Assert.Null(loop.JumpDelay(9_700, 0.5f)); // 600 ms at 0.5×: the next report still comes first.
        Assert.Equal(200L, loop.JumpDelay(9_900, 0.5f));
        Assert.Null(loop.JumpDelay(10_000, 1f)); // At or past B the caller returns at once instead.
    }

    // ---- next frame ----

    [Fact]
    public void NextFrameStepsOnlyAShownSeekableMediumAndPausesAPlayingOneFirst()
    {
        Assert.True(VideoFrameStepPolicy.CanStep(canSeek: true, ended: false, VideoPlaybackMode.Paused));
        Assert.True(VideoFrameStepPolicy.CanStep(canSeek: true, ended: false, VideoPlaybackMode.Playing));
        Assert.False(VideoFrameStepPolicy.CanStep(canSeek: true, ended: true, VideoPlaybackMode.Paused));
        Assert.False(VideoFrameStepPolicy.CanStep(canSeek: true, ended: false, VideoPlaybackMode.Stopped));
        Assert.False(VideoFrameStepPolicy.CanStep(canSeek: false, ended: false, VideoPlaybackMode.Paused));
        Assert.True(VideoFrameStepPolicy.PausesFirst(VideoPlaybackMode.Playing));
        Assert.False(VideoFrameStepPolicy.PausesFirst(VideoPlaybackMode.Paused));
    }

    // ---- speed ----

    [Fact]
    public void AClickCyclesTheFourSpeedsAndAChosenSpeedJoinsTheCycleAtTheNextEntry()
    {
        Assert.Equal(new[] { 0.5f, 1f, 1.5f, 2f }, GalleryVideoPlayer.Rates);
        Assert.Equal(1.5f, VideoRates.Next(1f));
        Assert.Equal(0.5f, VideoRates.Next(2f));
        Assert.Equal(1f, VideoRates.Next(0.75f));
        Assert.Equal(0.5f, VideoRates.Next(3f)); // Beyond the cycle wraps to the slowest.
    }

    [Fact]
    public void BracketKeysStepWithoutWrappingAndBackspaceReturnsToNormal()
    {
        Assert.Equal(2f, VideoRates.Step(1.5f, 1));
        Assert.Equal(2f, VideoRates.Step(2f, 1));
        Assert.Equal(0.5f, VideoRates.Step(0.5f, -1));
        Assert.Equal(1f, VideoRates.Step(1.25f, -1));
        Assert.Equal(1.5f, VideoRates.Step(1.25f, 1));
        Assert.Equal(2f, VideoRates.Step(3.5f, -1));
        Assert.Equal(3.5f, VideoRates.Step(3.5f, 1));
        Assert.Equal(0.5f, VideoRates.Step(0.25f, 1));
        Assert.Equal(1f, VideoRates.Step(3.5f, 0));
    }

    [Fact]
    public void TheChoicesRunFromAQuarterToFourInQuarterSteps()
    {
        Assert.Equal(16, VideoRates.Choices.Length);
        Assert.Equal(0.25f, VideoRates.Choices[0]);
        Assert.Equal(4f, VideoRates.Choices[^1]);
        Assert.All(VideoRates.Cycle, rate => Assert.Contains(rate, VideoRates.Choices));
        Assert.Equal(0.25f, VideoRates.Normalize(0.1f));
        Assert.Equal(4f, VideoRates.Normalize(9f));
        Assert.Equal(1.25f, VideoRates.Normalize(1.3f));
        Assert.Equal(1f, VideoRates.Normalize(float.NaN));
    }

    [Fact]
    public void QuarterSpeedLabelsKeepBothDecimalsInEveryLanguage()
    {
        Assert.Equal("0.25×", GalleryVideoPlayer.RateLabel(0.25f));
        Assert.Equal("1.5×", GalleryVideoPlayer.RateLabel(1.5f));
        Assert.Equal("4×", GalleryVideoPlayer.RateLabel(4f));
        Texts.SetLanguage("de");
        Assert.Equal("0,25×", GalleryVideoPlayer.RateLabel(0.25f));
        Assert.Equal("2,75×", GalleryVideoPlayer.RateLabel(2.75f));
    }

    // ---- audio tracks ----

    [Fact]
    public void TheBackendsDisableEntryIsNotATrackAndTheMenuNeedsTwoRealTracks()
    {
        var tracks = VideoAudioTracks.Real(new (int, string?)[] { (-1, "Disable"), (1, " Track 1 - [English] "), (2, null) });
        Assert.Equal(new[] { new VideoAudioTrackChoice(1, "Track 1 - [English]"), new VideoAudioTrackChoice(2, "") }, tracks);
        Assert.Equal(2, VideoAudioTracks.Next(tracks, 1));
        Assert.Equal(1, VideoAudioTracks.Next(tracks, 2));
        Assert.Equal(1, VideoAudioTracks.Next(tracks, -1)); // Nothing selected: the first track.
        Assert.Null(VideoAudioTracks.Next(VideoAudioTracks.Real(new (int, string?)[] { (-1, "Disable"), (3, "Main") }), 3));
    }

    // ---- commands, defaults, work view ----

    [Theory]
    [InlineData("video.ab-loop", "A")]
    [InlineData("video.frame-step", "Shift+OemPeriod")]
    public void TheTransportKeysAreSingleKeysOfTheGallery(string id, string shortcut)
    {
        CommandDefinition command = CommandCatalog.Get(id);
        Assert.Equal(shortcut, command.Shortcut);
        Assert.True(command.IsImplemented);
        Assert.Equal(new[] { "gallery" }, EffectiveShortcutBindings.ExecutionContexts(id));
        Assert.Contains(id, EffectiveShortcutBindings.YieldingDefaultCommandIds);
        foreach (ShortcutPlatform platform in Enum.GetValues<ShortcutPlatform>())
        {
            EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(null, platform);
            Assert.NotEmpty(bindings.For(id));
            Assert.Empty(bindings.Issues);
            Assert.Empty(EffectiveShortcutBindings.Build(new ShortcutBindingsSection().WithSingleKeyShortcuts(false), platform).For(id));
        }
        Assert.True(EffectiveShortcutBindings.DefaultsRevision >= 7);
    }

    [Fact]
    public void NextFrameMovedToShiftPeriodAndShiftCommaStaysFreeBecauseTheBackendCannotStepBack()
    {
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(null, ShortcutPlatform.Linux);
        Assert.Contains(bindings.For("video.frame-step"), gesture => gesture.Key == "OemPeriod" && gesture.Modifiers.SequenceEqual(new[] { "Shift" }));
        // The speed commands have [ ] and X C (revision 14), never an alias: no "<" / ">" alias, Shift+, and Shift+. are not speed keys any more.
        Assert.DoesNotContain(bindings.For("video.speed-up"), gesture => gesture.Key == "OemPeriod");
        Assert.DoesNotContain(bindings.For("video.speed-down"), gesture => gesture.Key == "OemComma");
        Assert.DoesNotContain(CommandCatalog.All, command => bindings.For(command.Id)
            .Any(gesture => gesture.Key == "OemComma" && gesture.Modifiers.SequenceEqual(new[] { "Shift" })));
        Assert.DoesNotContain(CommandCatalog.All, command => command.Id is "video.frame-back" or "video.previous-frame");
    }

    [Theory]
    [InlineData("video.audio-track")]
    [InlineData("video.speed-choose")]
    public void AudioTrackAndSpeedChoicesHaveNoDefaultKeyButCanBeBound(string id)
    {
        CommandDefinition command = CommandCatalog.Get(id);
        Assert.Null(command.Shortcut);
        Assert.True(command.IsImplemented);
        Assert.Equal(new[] { "gallery" }, EffectiveShortcutBindings.ExecutionContexts(id));
    }

    [Fact]
    public void ASavedBindingOfAnotherGalleryCommandOnAWinsOverTheNewLoopDefault()
    {
        ShortcutBindingsSection section = new ShortcutBindingsSection().Set("gallery.fit", new[] { ShortcutGesture.Parse("A") });
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        Assert.Contains(bindings.For("gallery.fit"), gesture => gesture.Key == "A" && gesture.Modifiers.Count == 0);
        Assert.Empty(bindings.For("video.ab-loop"));
        Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave);
    }

    [Fact]
    public void ThePlayerMenuIsPartOfTheTransportAndNeverReduced()
    {
        foreach (WorkView view in WorkViewSetting.All)
            Assert.Equal(Presence.Required, WorkViewPolicy.Resolve("gallery.video.player-menu", view));
        string viewer = ReadSource("Desktop/GalleryViewer.cs");
        Assert.Contains("WorkViewElement.SetId(_video.PlayerMenuControl, \"gallery.video.player-menu\")", viewer, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryTransportKeyIsRoutedByTheViewerThroughTheCommandCatalog()
    {
        string viewer = ReadSource("Desktop/GalleryViewer.cs");
        foreach (string id in new[] { "video.ab-loop", "video.frame-step", "video.speed-choose", "video.audio-track", "video.snapshot",
                     "video.speed-down", "video.speed-up", "video.speed-reset", "video.seek-back", "video.loop" })
            Assert.Contains("\"" + id + "\"", viewer, StringComparison.Ordinal);
        // The transport's full-screen and save-frame buttons go through the viewer's existing paths.
        Assert.Contains("_video.FullscreenRequested += () => FullscreenRequested?.Invoke();", viewer, StringComparison.Ordinal);
        Assert.Contains("_video.SaveFrameRequested += RequestSaveFrame;", viewer, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTransportRowHasNoWordButtonsAndTheVolumeOpensWithoutChangingTheLayout()
    {
        string transport = ReadSource("Desktop/GalleryVideoPlayer.Transport.cs");
        Assert.Equal(400, GalleryVideoPlayer.VolumePopupCloseDelayMs);
        Assert.Equal(5, GalleryVideoPlayer.VolumeWheelStep);
        // The slider lies on a canvas (zero layout size) above the speaker; an icon or symbol label on every control.
        Assert.Contains("_volumeCanvas.Children.Add(_volumePopup)", transport, StringComparison.Ordinal);
        Assert.Contains("TransportIcon(\"video.frame-step\"", transport, StringComparison.Ordinal);
        Assert.Contains("TransportIcon(\"window.fullscreen\"", transport, StringComparison.Ordinal);
        Assert.Contains("WorkspaceIcons.Create(WorkspaceIcons.More)", transport, StringComparison.Ordinal);
    }

    // ---- texts ----

    private static readonly string[] NewKeys =
    {
        "command.video.ab-loop", "command.video.frame-step", "command.video.audio-track", "command.video.speed-choose",
        "viewer.video.speedMore", "viewer.video.speedMenu", "viewer.video.abLoop.off", "viewer.video.abLoop.aSet",
        "viewer.video.abLoop.active", "viewer.video.abLoop.tooShort", "viewer.video.abLoop.nextA", "viewer.video.abLoop.nextB",
        "viewer.video.abLoop.nextOff", "viewer.video.frameStepHelp", "viewer.video.menu", "viewer.video.audioTrack",
        "viewer.video.audioTrack.item", "viewer.video.volumeHelp"
    };

    [Theory]
    [InlineData("es")] [InlineData("fr")] [InlineData("ru")] [InlineData("zh-Hans")] [InlineData("ja")]
    [InlineData("ko")] [InlineData("pt-BR")] [InlineData("cs")] [InlineData("it")] [InlineData("pl")]
    public void EveryPackTranslatesTheTransportTextsWithTheSamePlaceholders(string language)
    {
        Texts.SetLanguage("en");
        Dictionary<string, string> english = NewKeys.ToDictionary(key => key, key => Texts.T(key));
        using JsonDocument pack = JsonDocument.Parse(ReadSource("Desktop/Localization/Packs/" + language + ".json"));
        JsonElement strings = pack.RootElement.GetProperty("strings");
        foreach (string key in NewKeys)
        {
            string translated = strings.GetProperty(key).GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(translated), language + " " + key);
            Assert.NotEqual(english[key], translated);
            Assert.Equal(Placeholders(english[key]), Placeholders(translated));
        }
    }

    [Fact]
    public void GermanAndEnglishHaveTheirOwnTransportTexts()
    {
        Texts.SetLanguage("en");
        Assert.Equal("A–B loop 0:04–0:09", Texts.T("viewer.video.abLoop.active", "0:04", "0:09"));
        Assert.Equal("Next frame", CommandCatalog.Label("video.frame-step"));
        Texts.SetLanguage("de");
        Assert.Equal("A–B-Schleife: A bei 0:04", Texts.T("viewer.video.abLoop.aSet", "0:04"));
        Assert.Equal("Nächstes Einzelbild", CommandCatalog.Label("video.frame-step"));
        Assert.Equal("Tempo wählen…", CommandCatalog.Label("video.speed-choose"));
    }

    [Fact]
    public void TheLoopClockUsesMinutesAndSecondsAndHoursOnlyWhenNeeded()
    {
        Assert.Equal("0:04", GalleryVideoPlayer.FormatVideoTime(4_200));
        Assert.Equal("12:05", GalleryVideoPlayer.FormatVideoTime(725_000));
        Assert.Equal("1:00:00", GalleryVideoPlayer.FormatVideoTime(3_600_000));
        Assert.Equal("0:00", GalleryVideoPlayer.FormatVideoTime(-5));
    }

    private static string[] Placeholders(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text, @"\{\d+\}").Select(match => match.Value).Order(StringComparer.Ordinal).ToArray();

    private static string ReadSource(string relative) => File.ReadAllText(Root(relative));

    private static string Root(string path, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", path));
}

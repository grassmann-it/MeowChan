using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>
/// Video keyboard navigation: "," and "." seek by the step setting, L repeats the video, Shift+. is the next frame, and the
/// filmstrip toggle makes room on Shift+L. Collisions, yielding defaults, the step setting (default/min/max), the pure rules
/// and the texts. The key routing itself needs a window: its proof is the gallery-keyboard / gallery-video smokes.
/// </summary>
[Collection("Localization")]
public sealed class VideoKeyboardTests : IDisposable
{
    public VideoKeyboardTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    public static IEnumerable<object[]> Platforms() => Enum.GetValues<ShortcutPlatform>().Select(platform => new object[] { platform });

    private static IEnumerable<string> Owners(EffectiveShortcutBindings bindings, string key, params string[] modifiers) =>
        CommandCatalog.All.Where(command => bindings.For(command.Id)
            .Any(gesture => gesture.Key == key && gesture.Modifiers.OrderBy(m => m, StringComparer.Ordinal)
                .SequenceEqual(modifiers.OrderBy(m => m, StringComparer.Ordinal)))).Select(command => command.Id);

    [Theory, MemberData(nameof(Platforms))]
    public void EachVideoKeyBelongsToExactlyOneCommandAndTheTableRaisesNoIssue(ShortcutPlatform platform)
    {
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(null, platform);
        Assert.Empty(bindings.Issues);
        Assert.Equal(new[] { "video.step-back" }, Owners(bindings, "OemComma"));
        Assert.Equal(new[] { "video.step-forward" }, Owners(bindings, "OemPeriod"));
        Assert.Equal(new[] { "video.frame-step" }, Owners(bindings, "OemPeriod", "Shift"));
        Assert.Equal(new[] { "video.loop" }, Owners(bindings, "L"));
        Assert.Equal(new[] { "gallery.filmstrip-toggle" }, Owners(bindings, "L", "Shift"));
        // There is no previous-frame command (LibVLC 3 cannot step back), so Shift+, stays unbound.
        Assert.Empty(Owners(bindings, "OemComma", "Shift"));
    }

    [Fact]
    public void TheVideoKeysDoNotTouchTheNavigationRatingTaggingAndModeKeysOfTheGallery()
    {
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(null, ShortcutPlatform.Windows);
        Assert.Contains("gallery.previous", Owners(bindings, "Left"));
        Assert.Contains("gallery.next", Owners(bindings, "Right"));
        Assert.Contains("gallery.mode-toggle", Owners(bindings, "T", "Shift"));
        Assert.DoesNotContain(Owners(bindings, "Left"), id => id.StartsWith("video.", StringComparison.Ordinal));
        Assert.DoesNotContain(Owners(bindings, "Right"), id => id.StartsWith("video.", StringComparison.Ordinal));
        foreach (string digit in new[] { "D1", "D2", "D3", "D4", "D5", "D0" })
            Assert.DoesNotContain(Owners(bindings, digit, "Shift"), id => id.StartsWith("video.", StringComparison.Ordinal));
        // The Ctrl+arrow seeks stay, with their far variants.
        Assert.Contains("video.seek-back", Owners(bindings, "Left", "Primary"));
        Assert.Contains("video.seek-forward", Owners(bindings, "Right", "Primary"));
        Assert.Contains("video.seek-back-far", Owners(bindings, "Left", "Primary", "Shift"));
        Assert.Contains("video.seek-forward-far", Owners(bindings, "Right", "Primary", "Shift"));
        // Speed: [ and ] plus X and C (revision 14), Backspace resets.
        Assert.Equal(new[] { "video.speed-down" }, Owners(bindings, "X"));
        Assert.Equal(new[] { "video.speed-up" }, Owners(bindings, "C"));
        Assert.Equal(new[] { "video.speed-down" }, Owners(bindings, "OemOpenBrackets"));
        Assert.Equal(new[] { "video.speed-up" }, Owners(bindings, "OemCloseBrackets"));
    }

    [Theory]
    [InlineData("video.step-back", "OemComma")]
    [InlineData("video.step-forward", "OemPeriod")]
    [InlineData("video.loop", "L")]
    [InlineData("video.frame-step", "Shift+OemPeriod")]
    [InlineData("gallery.filmstrip-toggle", "Shift+L")]
    public void TheKeysAreGalleryCommandsWithYieldingDefaultsThatTheSingleKeySwitchTurnsOff(string id, string shortcut)
    {
        CommandDefinition command = CommandCatalog.Get(id);
        Assert.Equal(shortcut, command.Shortcut);
        Assert.True(command.IsImplemented);
        // The video keys run in the reader too (on the selected post's video, 15.11.2026) and on the Media page's panel video (revision 18), where they give
        // way to the page's own commands instead of competing; the filmstrip belongs to the gallery alone.
        bool video = id.StartsWith("video.", StringComparison.Ordinal);
        Assert.Equal(video ? new[] { "gallery", "media", "reader" } : new[] { "gallery" }, EffectiveShortcutBindings.ExecutionContexts(id).Order(StringComparer.Ordinal));
        Assert.Equal(video ? new[] { "gallery", "reader" } : new[] { "gallery" }, EffectiveShortcutBindings.ConflictContexts(id).Order(StringComparer.Ordinal));
        Assert.Contains(id, EffectiveShortcutBindings.YieldingDefaultCommandIds);
        Assert.True(EffectiveShortcutBindings.DefaultsRevision >= 12);
        foreach (ShortcutPlatform platform in Enum.GetValues<ShortcutPlatform>())
        {
            Assert.NotEmpty(EffectiveShortcutBindings.Build(null, platform).For(id));
            Assert.Empty(EffectiveShortcutBindings.Build(new ShortcutBindingsSection().WithSingleKeyShortcuts(false), platform).For(id));
        }
    }

    [Theory]
    [InlineData("video.step-back", "OemComma")]
    [InlineData("video.step-forward", "OemPeriod")]
    [InlineData("video.loop", "L")]
    public void ASavedBindingOfAnotherGalleryCommandOnTheNewKeyKeepsWinningWithoutAnyBlockingIssue(string id, string key)
    {
        ShortcutGesture gesture = ShortcutGesture.Parse(key);
        ShortcutBindingsSection section = new ShortcutBindingsSection().Set("gallery.slideshow-toggle", [gesture]);
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Windows);
        Assert.Single(bindings.For("gallery.slideshow-toggle"));
        Assert.Empty(bindings.For(id));
        Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave);
        Assert.Contains(bindings.Issues, issue => issue.CommandId == id && issue.OtherCommandId == "gallery.slideshow-toggle" && !issue.BlocksSave);
    }

    [Fact]
    public void ASavedBindingOfTheVideoCommandsThemselvesIsKept()
    {
        ShortcutBindingsSection section = new ShortcutBindingsSection()
            .Set("video.step-forward", [ShortcutGesture.Parse("Primary+OemPeriod")])
            .Set("video.loop", [ShortcutGesture.Parse("R")]);
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Windows);
        Assert.Equal("R", bindings.For("video.loop").Single().Key);
        Assert.Equal("OemPeriod", bindings.For("video.step-forward").Single().Key);
        Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave);
    }

    // ---- pure rules ----

    [Theory]
    [InlineData(5, 5)]
    [InlineData(1, 1)]
    [InlineData(60, 60)]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(61, 60)]
    [InlineData(int.MaxValue, 60)]
    public void TheStepIsClampedToItsRange(int configured, int expected) => Assert.Equal(expected, VideoKeyboardSteps.ClampSeconds(configured));

    [Fact]
    public void BackIsNegativeForwardIsPositiveAndNeverZero()
    {
        Assert.Equal(TimeSpan.FromSeconds(-5), VideoKeyboardSteps.Offset(forward: false, 5));
        Assert.Equal(TimeSpan.FromSeconds(5), VideoKeyboardSteps.Offset(forward: true, 5));
        Assert.Equal(TimeSpan.FromSeconds(-60), VideoKeyboardSteps.Offset(forward: false, 1000));
        Assert.Equal(TimeSpan.FromSeconds(1), VideoKeyboardSteps.Offset(forward: true, 0));
    }

    [Theory]
    [InlineData(false, true, false, 1)]
    [InlineData(true, false, false, 2)]
    [InlineData(false, false, false, 0)]
    [InlineData(false, false, true, 3)]
    [InlineData(true, true, true, 3)]
    [InlineData(true, true, false, 0)]
    public void TheRepeatKeyReportsWhatItDid(bool before, bool after, bool owned, int expected) =>
        Assert.Equal((VideoLoopKeyResult)expected, VideoKeyboardSteps.LoopResult(before, after, owned));

    // ---- setting ----

    [Fact]
    public void TheStepSettingHasDefaultMinimumAndMaximumAndIsValidatedCopiedAndSaved()
    {
        Config defaults = new();
        Assert.Equal(5, defaults.GalleryVideoSeekStepSeconds);
        Assert.Equal((1, 60), (Config.GalleryVideoSeekStepSecondsMinimum, Config.GalleryVideoSeekStepSecondsMaximum));
        defaults.Validate();
        foreach (int valid in new[] { 1, 5, 60 })
        {
            Config candidate = defaults.Clone();
            candidate.GalleryVideoSeekStepSeconds = valid;
            candidate.Validate();
            Config target = new();
            target.CopySettingsFieldsFrom(candidate);
            Assert.Equal(valid, target.GalleryVideoSeekStepSeconds);
            Config copy = new();
            copy.CopyFrom(candidate);
            Assert.Equal(valid, copy.GalleryVideoSeekStepSeconds);
        }
        foreach (int invalid in new[] { 0, 61, -1, int.MinValue, int.MaxValue })
        {
            Config candidate = defaults.Clone();
            candidate.GalleryVideoSeekStepSeconds = invalid;
            Assert.Throws<ArgumentOutOfRangeException>(() => candidate.Validate());
            Assert.Throws<ArgumentOutOfRangeException>(() => new Config().CopySettingsFieldsFrom(candidate));
        }
        string directory = Directory.CreateTempSubdirectory("meowchan-videokeys-").FullName;
        try
        {
            Config config = Config.LoadConfig(directory);
            config.GalleryVideoSeekStepSeconds = 12;
            config.SaveConfig(directory);
            Config loaded = Config.LoadConfig(directory);
            Assert.Equal(12, loaded.GalleryVideoSeekStepSeconds);
            Assert.Equal(12, loaded.Clone().GalleryVideoSeekStepSeconds);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void AnOutOfRangeSavedStepIsClampedWhenTheProfileLoads()
    {
        string directory = Directory.CreateTempSubdirectory("meowchan-videokeys-").FullName;
        try
        {
            foreach ((int stored, int expected) in new[] { (0, 1), (500, 60), (-9, 1) })
            {
                Config config = Config.LoadConfig(directory);
                config.GalleryVideoSeekStepSeconds = 5;
                config.SaveConfig(directory);
                string path = Directory.EnumerateFiles(directory, "*.json").First(file => File.ReadAllText(file).Contains("GalleryVideoSeekStepSeconds"));
                File.WriteAllText(path, System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(path),
                    "\"GalleryVideoSeekStepSeconds\"\\s*:\\s*-?\\d+", "\"GalleryVideoSeekStepSeconds\": " + stored));
                Assert.Equal(expected, Config.LoadConfig(directory).GalleryVideoSeekStepSeconds);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    // ---- texts ----

    public static IEnumerable<object[]> Languages()
    {
        yield return new object[] { "en" };
        yield return new object[] { "de" };
        foreach (string code in Texts.BundledPackCodes()) yield return new object[] { code };
    }

    [Theory, MemberData(nameof(Languages))]
    public void EveryLanguageHasTheNewTextsWithTheirPlaceholders(string language)
    {
        Texts.SetLanguage(language);
        string keys = Texts.T("viewer.video.seekKeys", "KEYA", "KEYB", 17);
        Assert.Contains("KEYA", keys); Assert.Contains("KEYB", keys); Assert.Contains("17", keys);
        Assert.Contains("KEYA", Texts.T("viewer.video.seekKeyBack", "KEYA", 17)); Assert.Contains("17", Texts.T("viewer.video.seekKeyBack", "KEYA", 17));
        Assert.Contains("KEYA", Texts.T("viewer.video.seekKeyForward", "KEYA", 17)); Assert.Contains("17", Texts.T("viewer.video.seekKeyForward", "KEYA", 17));
        string hint = Texts.T("settings.galleryVideoSeekStepHint", 5, 1, 60);
        Assert.Contains("5", hint); Assert.Contains("60", hint);
        foreach (string key in new[] { "gallery.video.loopOn", "gallery.video.loopOff", "settings.galleryVideoSeekStep", "command.video.step-back", "command.video.step-forward" })
            Assert.NotEqual(key, Texts.T(key));
        Assert.NotEqual(Texts.T("gallery.video.loopOn"), Texts.T("gallery.video.loopOff"));
        // The filmstrip toggle's key sentence takes the key from the binding (revision 13 moved the texts out of the long hint).
        Assert.Contains("KEYA", Texts.T("settings.galleryFilmstripKeyHint", "KEYA"));
        Assert.DoesNotContain("Shift+L", Texts.T("settings.galleryFilmstripHint"));
    }

    [Fact]
    public void TheGermanTextsAreTheOnesTomAskedFor()
    {
        Texts.SetLanguage("de");
        Assert.Equal("Wiederholen an", Texts.T("gallery.video.loopOn"));
        Assert.Equal("Wiederholen aus", Texts.T("gallery.video.loopOff"));
        Assert.Equal("Umschalt+L zeigt oder verbirgt die Reihe.", Texts.T("settings.galleryFilmstripKeyHint", "Umschalt+L"));
    }
}

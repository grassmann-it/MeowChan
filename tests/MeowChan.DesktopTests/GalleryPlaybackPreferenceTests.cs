using System.Text.Json;
using MeowChan.Desktop;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

public sealed class GalleryPlaybackPreferenceTests
{
    [Fact]
    public void ExistingProfilesKeepDockedManualDefaultsAndExistingAudioPolicy()
    {
        using Profile profile = new();
        File.WriteAllText(Path.Combine(profile.Path, "settings.json"), "{\"VideoVolume\":29,\"VideoStartMuted\":true}");
        Config config = Config.LoadConfig(profile.Path);
        Assert.Equal(new GalleryPlaybackPreferences(), GalleryPlaybackPreferences.FromConfig(config));
        Assert.Equal(29, config.VideoVolume);
        Assert.True(config.VideoStartMuted);
    }

    [Fact]
    public void ChoicesRoundTripButNoRunningSlideshowIsPersisted()
    {
        using Profile profile = new();
        Config config = Config.LoadConfig(profile.Path);
        var preferences = new GalleryPlaybackPreferences(GalleryPlaybackMode.Slideshow, 17, true, true, false, true);
        preferences.ApplyTo(config);
        Config copy = new();
        copy.CopyFrom(config.Clone());
        copy.SaveConfig(profile.Path);
        Assert.Equal(preferences.Normalize(), GalleryPlaybackPreferences.FromConfig(Config.LoadConfig(profile.Path)));
        string json = File.ReadAllText(Path.Combine(profile.Path, "settings.json"));
        Assert.DoesNotContain("Running", json);
        Assert.DoesNotContain("SettingsBaseline", json);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.DoesNotContain("Position", string.Join(",", document.RootElement.EnumerateObject()
            .Where(property => property.Name.StartsWith("Gallery")).Select(property => property.Name)));
        Assert.DoesNotContain(typeof(GalleryPlaybackPreferences).GetProperties(), property =>
            property.Name.Contains("Running") || property.Name.Contains("Position"));
    }

    [Theory]
    [InlineData("null", 5)]
    [InlineData("\"invalid\"", 5)]
    [InlineData("{}", 5)]
    [InlineData("0", 1)]
    [InlineData("999", 300)]
    [InlineData("3.5", 5)]
    public void InvalidNewFieldsDoNotDiscardUnrelatedProfileSettings(string duration, int expected)
    {
        using Profile profile = new();
        File.WriteAllText(Path.Combine(profile.Path, "settings.json"),
            "{\"VideoVolume\":29,\"GalleryPlaybackMode\":{},\"GalleryMediaFocus\":\"invalid\","
            + "\"GalleryOverlayControls\":null,\"GalleryAutoHideControls\":[],\"GalleryOverlayPostContext\":42,"
            + "\"GallerySlideshowSeconds\":" + duration + "}");
        Config config = Config.LoadConfig(profile.Path);
        Assert.Null(config.LoadWarning);
        Assert.Equal(29, config.VideoVolume);
        Assert.Equal("Manual", config.GalleryPlaybackMode);
        Assert.Equal(expected, config.GallerySlideshowSeconds);
        Assert.False(config.GalleryMediaFocus);
        Assert.False(config.GalleryOverlayControls);
        Assert.False(config.GalleryAutoHideControls);
        Assert.False(config.GalleryOverlayPostContext);
    }

    [Fact]
    public void UnchangedDraftFieldsPreserveNewerGalleryChoicesAndEditedFieldsWin()
    {
        Config live = new();
        Config draft = live.Clone();
        draft.GallerySlideshowSeconds = 17;
        new GalleryPlaybackPreferences(GalleryPlaybackMode.Repeat, 7, true, true, true, true).ApplyTo(live);
        live.CopySettingsFieldsFrom(draft);
        Assert.Equal(new GalleryPlaybackPreferences(GalleryPlaybackMode.Repeat, 17, true, true, true, true),
            GalleryPlaybackPreferences.FromConfig(live));
    }

    [Fact]
    public void DirectModeChangeDoesNotOverwriteUnrelatedNewerSettings()
    {
        var shown = new GalleryPlaybackPreferences();
        var edited = shown with { Mode = GalleryPlaybackMode.Repeat };
        Config current = new() { GallerySlideshowSeconds = 19, GalleryOverlayControls = true };
        edited.ApplyChangesTo(current, shown);
        Assert.Equal("Repeat", current.GalleryPlaybackMode);
        Assert.Equal(19, current.GallerySlideshowSeconds);
        Assert.True(current.GalleryOverlayControls);
    }

    [Fact]
    public async Task SessionAdmissionRetainsOriginalDraftBaselineAcrossNewerSavedGalleryChoices()
    {
        using Profile profile = new();
        new Config { AutomaticUpdateChecks = false }.SaveConfig(profile.Path);
        await using (var host = new DesktopSessionHost(profile.Path, Array.Empty<IImageBoard>()))
        {
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Config draft = host.Config.Clone();
            draft.GallerySlideshowSeconds = 21;
            var direct = new GalleryPlaybackPreferences(GalleryPlaybackMode.Repeat, 8, true, true, true, false);
            await host.SaveViewPreferencesAsync(direct.ApplyTo).WaitAsync(TimeSpan.FromSeconds(15));
            await host.ApplySettingsAsync(draft).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(direct with { ImageSeconds = 21 }, GalleryPlaybackPreferences.FromConfig(host.Config));
        }
        Assert.Equal(new GalleryPlaybackPreferences(GalleryPlaybackMode.Repeat, 21, true, true, true, false),
            GalleryPlaybackPreferences.FromConfig(Config.LoadConfig(profile.Path)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StaleSessionDraftMergesThePairAndIndependentPreferencesAcrossRestart(bool initiallyEnabled, bool editPair)
    {
        using Profile profile = new();
        Config initial = new() { AutomaticUpdateChecks = false, VideoAutoplay = false, VideoStartMuted = true };
        new GalleryPlaybackPreferences(GalleryPlaybackMode.Slideshow).WithControlsOverlay(initiallyEnabled).ApplyTo(initial);
        initial.SaveConfig(profile.Path);
        var expected = new GalleryPlaybackPreferences(GalleryPlaybackMode.Repeat, 21, true,
            !initiallyEnabled, !initiallyEnabled, true);
        await using (var host = new DesktopSessionHost(profile.Path, Array.Empty<IImageBoard>()))
        {
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Config draft = host.Config.Clone();
            draft.GallerySlideshowSeconds = 21;
            if (editPair)
                GalleryPlaybackPreferences.FromConfig(draft).WithControlsOverlay(!initiallyEnabled).ApplyTo(draft);
            else
            {
                draft.VideoVolume = 29;
                draft.GalleryMediaFocus = true;
                draft.GalleryOverlayPostContext = true;
            }
            // Newer direct choices land after the settings draft has captured its baseline.
            await host.SaveViewPreferencesAsync(config =>
            {
                new GalleryPlaybackPreferences(GalleryPlaybackMode.Repeat, 8, editPair, OverlayPostContext: editPair)
                    .WithControlsOverlay(editPair ? initiallyEnabled : !initiallyEnabled).ApplyTo(config);
                if (editPair) config.VideoVolume = 29;
            }).WaitAsync(TimeSpan.FromSeconds(15));
            await host.ApplySettingsAsync(draft).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(expected, GalleryPlaybackPreferences.FromConfig(host.Config));
            AssertPair(host.Config, !initiallyEnabled);
            Assert.Equal(29, host.Config.VideoVolume);
            Assert.False(host.Config.VideoAutoplay);
            Assert.True(host.Config.VideoStartMuted);
        }
        Config restarted = Config.LoadConfig(profile.Path);
        Assert.Equal(expected, GalleryPlaybackPreferences.FromConfig(restarted));
        AssertPair(restarted, !initiallyEnabled);
        Assert.Equal(29, restarted.VideoVolume);
        Assert.False(restarted.VideoAutoplay);
        Assert.True(restarted.VideoStartMuted);
    }

    [Theory]
    [InlineData(" repeat ", "Repeat")]
    [InlineData("SLIDESHOW", "Slideshow")]
    [InlineData("unexpected", "Manual")]
    public void ModeIsNormalizedWithoutInventingAnActiveSession(string value, string expected)
    {
        var config = new Config { GalleryPlaybackMode = value };
        Assert.Equal(expected, config.GalleryPlaybackMode);
        Assert.Equal(expected, GalleryPlaybackPreferences.FromConfig(config).Mode.ToString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NormalizeAndExplicitActionsHaveDifferentIntent(bool overlay, bool autoHide)
    {
        var legacy = new GalleryPlaybackPreferences(GalleryPlaybackMode.Slideshow, 17, true, overlay, autoHide, true);
        var normalized = legacy.Normalize();
        Assert.Equal(overlay, normalized.OverlayControls);
        Assert.Equal(overlay, normalized.AutoHideControls);
        Assert.Equal(legacy with { AutoHideControls = overlay }, normalized);
        Assert.Equal(normalized, normalized.Normalize());
        foreach (bool enabled in new[] { false, true })
        {
            var edited = legacy.WithControlsOverlay(enabled);
            Assert.Equal(legacy with { OverlayControls = enabled, AutoHideControls = enabled }, edited);
            Assert.Equal(edited, edited.Normalize());
            Config target = new() { VideoVolume = 29, VideoAutoplay = false, VideoStartMuted = true };
            edited.ApplyTo(target);
            Assert.Equal(edited, GalleryPlaybackPreferences.FromConfig(target));
            Assert.Equal(29, target.VideoVolume);
            Assert.False(target.VideoAutoplay);
            Assert.True(target.VideoStartMuted);
        }
    }

    [Theory]
    [InlineData("false", "true", false)]
    [InlineData("true", "false", true)]
    [InlineData("false", "false", false)]
    [InlineData("true", "true", true)]
    [InlineData(null, "true", false)]
    [InlineData("true", null, true)]
    [InlineData(null, null, false)]
    [InlineData("null", "true", false)]
    [InlineData("{}", "true", false)]
    [InlineData("[]", "true", false)]
    [InlineData("\"true\"", "true", false)]
    [InlineData("42", "true", false)]
    [InlineData("true", "null", true)]
    [InlineData("true", "[]", true)]
    [InlineData("true", "{}", true)]
    [InlineData("true", "\"false\"", true)]
    [InlineData("true", "42", true)]
    public void LegacyAndMalformedPairsMigrateAfterAllKeysRegardlessOfOrder(string? overlay, string? autoHide, bool enabled)
    {
        using Profile profile = new();
        var fields = new List<string> { "\"VideoVolume\":29", "\"VideoStartMuted\":true", "\"VideoAutoplay\":false" };
        if (overlay != null) fields.Add("\"GalleryOverlayControls\":" + overlay);
        if (autoHide != null) fields.Add("\"GalleryAutoHideControls\":" + autoHide);
        for (int order = 0; order < 2; order++, fields.Reverse())
        {
            string json = "{" + string.Join(",", fields) + "}";
            // Exercise callback normalization directly, without LoadConfig/Validate masking it.
            AssertPair(JsonSerializer.Deserialize<Config>(json)!, enabled);
            AssertPair(JsonSerializer.Deserialize(json, ConfigJsonContext.Default.Config)!, enabled);
            File.WriteAllText(Path.Combine(profile.Path, "settings.json"), json);
            Config loaded = Config.LoadConfig(profile.Path);
            Assert.Null(loaded.LoadWarning);
            AssertPair(loaded, enabled);
            loaded.SaveConfig(profile.Path);
            Config restarted = Config.LoadConfig(profile.Path);
            AssertPair(restarted, enabled);
            Assert.Equal(29, restarted.VideoVolume);
            Assert.True(restarted.VideoStartMuted);
            Assert.False(restarted.VideoAutoplay);
            using JsonDocument persisted = JsonDocument.Parse(File.ReadAllText(Path.Combine(profile.Path, "settings.json")));
            Assert.Equal(enabled, persisted.RootElement.GetProperty("GalleryOverlayControls").GetBoolean());
            Assert.Equal(enabled, persisted.RootElement.GetProperty("GalleryAutoHideControls").GetBoolean());
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void EverySnapshotCopyApplyAndSaveBoundaryNormalizesLegacyPairs(bool overlay, bool autoHide)
    {
        using Profile profile = new();
        Config Raw() => new() { GalleryOverlayControls = overlay, GalleryAutoHideControls = autoHide };
        var expected = new GalleryPlaybackPreferences().WithControlsOverlay(overlay);
        Config source = Raw();
        Assert.Equal(expected, GalleryPlaybackPreferences.FromConfig(source));
        Config clone = source.Clone();
        AssertPair(clone, overlay);
        Assert.Equal(expected, clone.SettingsBaselineGalleryPlayback);
        Assert.Equal(autoHide, source.GalleryAutoHideControls); // Clone does not mutate its source.
        source.Validate();
        AssertPair(source, overlay);
        Config target = new();
        target.CopyFrom(Raw());
        AssertPair(target, overlay);
        target.CopySettingsFieldsFrom(Raw()); // No baseline means a complete explicit draft.
        AssertPair(target, overlay);
        new GalleryPlaybackPreferences(OverlayControls: overlay, AutoHideControls: autoHide).ApplyTo(target);
        AssertPair(target, overlay);
        Raw().SaveConfig(profile.Path);
        AssertPair(Config.LoadConfig(profile.Path), overlay);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EditedPairMergesAtomicallyWithoutRollingBackIndependentLivePreferences(bool initiallyEnabled)
    {
        Config live = new();
        new GalleryPlaybackPreferences().WithControlsOverlay(initiallyEnabled).ApplyTo(live);
        Config draft = live.Clone();
        GalleryPlaybackPreferences.FromConfig(draft).WithControlsOverlay(!initiallyEnabled).ApplyTo(draft);
        live.VideoVolume = 29;
        live.GalleryMediaFocus = true;
        live.GalleryOverlayPostContext = true;
        live.GallerySlideshowSeconds = 19;
        live.GalleryPlaybackMode = "Repeat";
        live.CopySettingsFieldsFrom(draft);
        AssertPair(live, !initiallyEnabled);
        Assert.Equal(29, live.VideoVolume);
        Assert.Equal(new GalleryPlaybackPreferences(GalleryPlaybackMode.Repeat, 19, true,
            !initiallyEnabled, !initiallyEnabled, true), GalleryPlaybackPreferences.FromConfig(live));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UneditedPairPreservesNewerLivePairWhileIndependentDraftEditsWin(bool initiallyEnabled)
    {
        Config live = new();
        new GalleryPlaybackPreferences().WithControlsOverlay(initiallyEnabled).ApplyTo(live);
        Config draft = live.Clone();
        draft.VideoVolume = 29;
        draft.GalleryMediaFocus = true;
        draft.GalleryOverlayPostContext = true;
        GalleryPlaybackPreferences.FromConfig(live).WithControlsOverlay(!initiallyEnabled).ApplyTo(live);
        live.CopySettingsFieldsFrom(draft);
        AssertPair(live, !initiallyEnabled);
        Assert.Equal(29, live.VideoVolume);
        Assert.True(live.GalleryMediaFocus);
        Assert.True(live.GalleryOverlayPostContext);
    }

    [Fact]
    public void LegacyBaselineNormalizationDoesNotInventAPairEdit()
    {
        var baseline = new GalleryPlaybackPreferences(AutoHideControls: true);
        Config current = new() { GalleryOverlayControls = true, GalleryAutoHideControls = false };
        (baseline.Normalize() with { ImageSeconds = 11 }).ApplyChangesTo(current, baseline);
        AssertPair(current, true);
        Assert.Equal(11, current.GallerySlideshowSeconds);
    }

    private static void AssertPair(Config config, bool enabled)
    {
        Assert.Equal(enabled, config.GalleryOverlayControls);
        Assert.Equal(enabled, config.GalleryAutoHideControls);
    }

    private sealed class Profile : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("meowchan-gallery-prefs-").FullName;
        public void Dispose() => Directory.Delete(Path, true);
    }
}

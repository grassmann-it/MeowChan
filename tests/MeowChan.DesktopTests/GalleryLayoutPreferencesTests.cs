using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

/// <summary>
/// GL4: the saved gallery layout, one choice per mode (View | Tag) and profile. The shape in settings.json, the migration from the old
/// GalleryFilmstrip setting, unknown and damaged members (never discarding the other settings), the independence of the two modes, the
/// reset, the settings page's merge and the export. The viewer's own behaviour (events, the plan) is covered by the plan tests and the smokes.
/// </summary>
public sealed class GalleryLayoutPreferencesTests
{
    private static readonly GalleryLayoutChoice ViewChoice = new(Filmstrip: true, FilmstripDock: GalleryFilmstripDock.Top, Tools: false, Info: true, Hints: false);
    private static readonly GalleryLayoutChoice TagChoice = new(Filmstrip: false, FilmstripDock: GalleryFilmstripDock.Bottom, Tools: null, Info: false, Hints: true);

    private static Config Load(string settingsJson)
    {
        using Profile profile = new();
        File.WriteAllText(Path.Combine(profile.Path, "settings.json"), settingsJson);
        return Config.LoadConfig(profile.Path);
    }

    // ---- round trip ----

    [Fact]
    public void BothModesRoundTripThroughTheProfileFile()
    {
        using Profile profile = new();
        Config config = Config.LoadConfig(profile.Path);
        new GalleryLayoutPreferences(ViewChoice, TagChoice).ApplyTo(config);
        config.SaveConfig(profile.Path);

        Config loaded = Config.LoadConfig(profile.Path);
        Assert.Null(loaded.LoadWarning);
        GalleryLayoutPreferences restored = GalleryLayoutPreferences.FromConfig(loaded);
        Assert.Equal(ViewChoice, restored.For(GalleryChromeMode.View));
        Assert.Equal(TagChoice, restored.For(GalleryChromeMode.Tag));
    }

    [Fact]
    public void TheFileHoldsOnlyTheChosenMembersInTheDocumentedShape()
    {
        using Profile profile = new();
        Config config = Config.LoadConfig(profile.Path);
        new GalleryLayoutPreferences(new GalleryLayoutChoice(Filmstrip: true, FilmstripDock: GalleryFilmstripDock.Top), new GalleryLayoutChoice(Hints: false)).ApplyTo(config);
        config.SaveConfig(profile.Path);

        JsonObject document = JsonNode.Parse(File.ReadAllText(Path.Combine(profile.Path, "settings.json")))!.AsObject();
        JsonObject layout = document["GalleryLayout"]!.AsObject();
        Assert.Equal(new[] { "view", "tag" }, layout.Select(pair => pair.Key));
        Assert.Equal(new[] { "filmstrip", "filmstripDock" }, layout["view"]!.AsObject().Select(pair => pair.Key));
        Assert.Equal("top", layout["view"]!["filmstripDock"]!.GetValue<string>());
        Assert.True(layout["view"]!["filmstrip"]!.GetValue<bool>());
        Assert.Equal(new[] { "hints" }, layout["tag"]!.AsObject().Select(pair => pair.Key));
        Assert.False(layout["tag"]!["hints"]!.GetValue<bool>());
    }

    [Fact]
    public void NothingChosenWritesNoLayoutAndTheFilmstripSettingStaysTrue()
    {
        Config config = new() { GalleryFilmstrip = false };
        GalleryLayoutPreferences.Default.ApplyTo(config);
        Assert.Null(config.GalleryLayout);
        Assert.True(config.GalleryFilmstrip);
        Assert.True(GalleryLayoutPreferences.Default.IsDefault);
        Assert.False(new GalleryLayoutPreferences(ViewChoice, GalleryLayoutChoice.Default).IsDefault);
    }

    // ---- migration of the old GalleryFilmstrip setting ----

    [Fact]
    public void AProfileWithoutALayoutReadsTheOldFilmstripSettingForBothModes()
    {
        GalleryLayoutPreferences on = GalleryLayoutPreferences.FromConfig(Load("{\"GalleryFilmstrip\":true,\"RecheckTimer\":173}"));
        Assert.Equal(GalleryLayoutPreferences.Default, on);

        Config offConfig = Load("{\"GalleryFilmstrip\":false,\"RecheckTimer\":173}");
        GalleryLayoutPreferences off = GalleryLayoutPreferences.FromConfig(offConfig);
        Assert.False(off.View.Filmstrip);
        Assert.False(off.Tag.Filmstrip);
        Assert.Null(off.View.FilmstripDock);
        Assert.Equal(173, offConfig.RecheckTimer);
        // A profile that never knew the setting has it on, so nothing is chosen.
        Assert.Equal(GalleryLayoutPreferences.Default, GalleryLayoutPreferences.FromConfig(Load("{}")));
    }

    [Fact]
    public void AStoredLayoutWithoutAViewFilmstripStillHonoursAnOffSettingForTheViewOnly()
    {
        Config config = Load("{\"GalleryFilmstrip\":false,\"GalleryLayout\":{\"view\":{\"tools\":false},\"tag\":{\"hints\":true}}}");
        GalleryLayoutPreferences preferences = GalleryLayoutPreferences.FromConfig(config);
        Assert.Equal(new GalleryLayoutChoice(Filmstrip: false, Tools: false), preferences.View);
        Assert.Equal(new GalleryLayoutChoice(Hints: true), preferences.Tag);
    }

    [Fact]
    public void AnOffOldSettingBeatsAStoredViewFilmstrip()
    {
        // N5: an older build switched the strip off after this one had saved "on"; the older build only writes the old setting.
        Config config = Load("{\"GalleryFilmstrip\":false,\"GalleryLayout\":{\"view\":{\"filmstrip\":true,\"tools\":false},\"tag\":{\"filmstrip\":true}}}");
        GalleryLayoutPreferences preferences = GalleryLayoutPreferences.FromConfig(config);
        Assert.Equal(new GalleryLayoutChoice(Filmstrip: false, Tools: false), preferences.View);
        Assert.Equal(new GalleryLayoutChoice(Filmstrip: true), preferences.Tag); // the tagging mode's own choice is not the old setting's
    }

    [Fact]
    public void AnOnOldSettingKeepsAStoredViewChoice()
    {
        // The other way round is not mixed-build evidence: a file without the old key reads it as on.
        Config off = Load("{\"GalleryLayout\":{\"view\":{\"filmstrip\":false,\"hints\":true}}}");
        Assert.Equal(new GalleryLayoutChoice(Filmstrip: false, Hints: true), GalleryLayoutPreferences.FromConfig(off).View);
        // A stored "on" stays the person's explicit choice (Barebones has the strip off by default).
        Assert.True(GalleryLayoutPreferences.FromConfig(Load("{\"GalleryFilmstrip\":true,\"GalleryLayout\":{\"view\":{\"filmstrip\":true}}}")).View.Filmstrip);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public void AnOffOldSettingAlwaysAgreesWithTheViewFilmstripAfterReading(bool? stored)
    {
        Config config = new() { GalleryFilmstrip = false, GalleryLayout = new GalleryLayoutPreferences(new GalleryLayoutChoice(Filmstrip: stored, Info: false), TagChoice) };
        GalleryLayoutPreferences preferences = GalleryLayoutPreferences.FromConfig(config);
        Assert.False(preferences.View.Filmstrip);
        Assert.Equal(TagChoice, preferences.Tag);
        Assert.False(preferences.View.Info);
        // Written back, the file agrees as well.
        preferences.ApplyTo(config);
        Assert.False(config.GalleryFilmstrip);
        Assert.Equal(preferences, GalleryLayoutPreferences.FromConfig(config));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(null, true)]
    public void ApplyToWritesTheOldSettingWithTheViewFilmstrip(bool? viewFilmstrip, bool expectedSetting)
    {
        Config config = new() { GalleryFilmstrip = !expectedSetting };
        new GalleryLayoutPreferences(new GalleryLayoutChoice(Filmstrip: viewFilmstrip), TagChoice).ApplyTo(config);
        Assert.Equal(expectedSetting, config.GalleryFilmstrip);
        // The tagging mode's own filmstrip never moves the old setting.
        Config tagOnly = new();
        new GalleryLayoutPreferences(GalleryLayoutChoice.Default, new GalleryLayoutChoice(Filmstrip: false)).ApplyTo(tagOnly);
        Assert.True(tagOnly.GalleryFilmstrip);
    }

    // ---- unknown members ----

    [Fact]
    public void UnknownMembersAreDroppedAndNamedAndTheValidOnesSurvive()
    {
        using JsonDocument document = JsonDocument.Parse(
            "{\"view\":{\"filmstrip\":false,\"sparkle\":1,\"hints\":true},\"tag\":{\"info\":false,\"zzz\":{}},\"third\":{},\"VIEW2\":3}");
        List<string> dropped = new();
        GalleryLayoutPreferences? read = GalleryLayoutPreferences.Read(document.RootElement, dropped);
        Assert.NotNull(read);
        Assert.Equal(new GalleryLayoutChoice(Filmstrip: false, Hints: true), read.View);
        Assert.Equal(new GalleryLayoutChoice(Info: false), read.Tag);
        Assert.Equal(new[] { "view.sparkle", "tag.zzz", "third", "VIEW2" }, dropped);
        // Written back, none of them is kept.
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream)) read.Write(writer);
        string json = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        Assert.DoesNotContain("sparkle", json);
        Assert.DoesNotContain("third", json);
        Assert.DoesNotContain("zzz", json);
    }

    [Fact]
    public void UnknownMembersInTheProfileFileNeverDiscardOtherSettingsAndAreGoneAfterTheNextSave()
    {
        using Profile profile = new();
        File.WriteAllText(Path.Combine(profile.Path, "settings.json"),
            "{\"RecheckTimer\":173,\"VideoVolume\":29,\"GalleryLayout\":{\"view\":{\"filmstrip\":false,\"sparkle\":true},\"extra\":1}}");
        Config config = Config.LoadConfig(profile.Path);
        Assert.Null(config.LoadWarning);
        Assert.Equal((173, 29), (config.RecheckTimer, config.VideoVolume));
        Assert.False(GalleryLayoutPreferences.FromConfig(config).View.Filmstrip);

        config.SaveConfig(profile.Path);
        string saved = File.ReadAllText(Path.Combine(profile.Path, "settings.json"));
        Assert.DoesNotContain("sparkle", saved);
        Assert.DoesNotContain("extra", saved);
    }

    // ---- damaged values ----

    [Theory]
    [InlineData("\"nonsense\"")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("[1,2]")]
    [InlineData("null")]
    public void ADamagedLayoutValueReadsAsNothingChosenAndNeverDiscardsOtherSettings(string value)
    {
        Config config = Load("{\"RecheckTimer\":173,\"VideoVolume\":29,\"GalleryLayout\":" + value + "}");
        Assert.Null(config.LoadWarning);
        Assert.Null(config.GalleryLayout);
        Assert.Equal((173, 29), (config.RecheckTimer, config.VideoVolume));
        Assert.Equal(GalleryLayoutPreferences.Default, GalleryLayoutPreferences.FromConfig(config));
    }

    [Fact]
    public void DamagedMembersAreDroppedOneByOne()
    {
        Config config = Load(
            "{\"RecheckTimer\":173,\"GalleryLayout\":{\"view\":{\"filmstrip\":\"yes\",\"filmstripDock\":\"left\",\"tools\":false,\"info\":0,\"hints\":null},"
            + "\"tag\":[1],\"Unknown\":{}}}");
        Assert.Null(config.LoadWarning);
        Assert.Equal(173, config.RecheckTimer);
        GalleryLayoutPreferences preferences = GalleryLayoutPreferences.FromConfig(config);
        Assert.Equal(new GalleryLayoutChoice(Tools: false), preferences.View);
        Assert.Equal(GalleryLayoutChoice.Default, preferences.Tag);
    }

    [Theory]
    [InlineData("\"TOP\"", "Top")]
    [InlineData("\"Bottom\"", "Bottom")]
    [InlineData("\"middle\"", null)]
    [InlineData("1", null)]
    [InlineData("null", null)]
    public void TheDockIsReadCaseInsensitivelyAndAnythingElseIsDropped(string value, string? expected)
    {
        Config config = Load("{\"GalleryLayout\":{\"tag\":{\"filmstripDock\":" + value + "}}}");
        Assert.Equal(expected, GalleryLayoutPreferences.FromConfig(config).Tag.FilmstripDock?.ToString());
    }

    [Fact]
    public void AnUndefinedDockOfACodeCreatedChoiceIsNormalizedAway()
    {
        GalleryLayoutPreferences preferences = new GalleryLayoutPreferences(new GalleryLayoutChoice(FilmstripDock: (GalleryFilmstripDock)7), TagChoice).Normalize();
        Assert.Null(preferences.View.FilmstripDock);
        Assert.Equal(TagChoice, preferences.Tag);
        Config config = new() { GalleryLayout = new GalleryLayoutPreferences(new GalleryLayoutChoice(FilmstripDock: (GalleryFilmstripDock)7), GalleryLayoutChoice.Default) };
        config.Validate();
        Assert.Null(config.GalleryLayout);
    }

    [Fact]
    public void TheDroppedMembersLogNamesStayBelowTheDocumentedCeiling()
    {
        Assert.Equal(8, GalleryLayoutPreferences.LoggedDroppedMembersCeiling);
        string members = string.Join(",", Enumerable.Range(0, 50).Select(i => $"\"m{i}\":1"));
        Config config = Load("{\"RecheckTimer\":173,\"GalleryLayout\":{" + members + "}}");
        Assert.Equal(173, config.RecheckTimer);
        Assert.Equal(GalleryLayoutPreferences.Default, GalleryLayoutPreferences.FromConfig(config));
    }

    // ---- the two modes are independent ----

    [Fact]
    public void ChangingOneModeLeavesTheOtherUntouched()
    {
        GalleryLayoutPreferences start = new(ViewChoice, TagChoice);
        GalleryLayoutChoice changed = TagChoice with { Hints = false, Info = true };

        GalleryLayoutPreferences tag = start.With(GalleryChromeMode.Tag, changed);
        Assert.Equal(ViewChoice, tag.For(GalleryChromeMode.View));
        Assert.Equal(changed, tag.For(GalleryChromeMode.Tag));

        GalleryLayoutPreferences view = start.With(GalleryChromeMode.View, GalleryLayoutChoice.Default);
        Assert.Equal(GalleryLayoutChoice.Default, view.For(GalleryChromeMode.View));
        Assert.Equal(TagChoice, view.For(GalleryChromeMode.Tag));
        Assert.Equal(start, new GalleryLayoutPreferences(ViewChoice, TagChoice));
        Assert.Throws<ArgumentNullException>(() => start.With(GalleryChromeMode.View, null!));
    }

    [Fact]
    public void ASaveOfOneModeOntoTheLatestFileKeepsAChangeOfTheOtherMode()
    {
        // What MainWindow.SaveGalleryLayoutAsync does with the freshest saved state: only the changed mode is replaced.
        Config saved = new();
        new GalleryLayoutPreferences(ViewChoice, GalleryLayoutChoice.Default).ApplyTo(saved);
        GalleryLayoutChoice tagChange = new(Hints: false);
        GalleryLayoutPreferences.FromConfig(saved).With(GalleryChromeMode.Tag, tagChange).ApplyTo(saved);
        GalleryLayoutChoice viewChange = ViewChoice with { Info = false };
        GalleryLayoutPreferences.FromConfig(saved).With(GalleryChromeMode.View, viewChange).ApplyTo(saved);

        GalleryLayoutPreferences result = GalleryLayoutPreferences.FromConfig(saved);
        Assert.Equal(viewChange, result.View);
        Assert.Equal(tagChange, result.Tag);
    }

    [Fact]
    public void TheWindowWritesTheChangedModesOfOneFlushOntoTheLatestSavedState()
    {
        // MainWindow.ApplyGalleryLayoutChanges: both modes changed within one flush, the saved state got a newer Tag change meanwhile.
        Config saved = new();
        new GalleryLayoutPreferences(ViewChoice, TagChoice).ApplyTo(saved);
        GalleryLayoutChoice view = ViewChoice with { Filmstrip = false };
        MainWindow.ApplyGalleryLayoutChanges(saved, view, null);
        Assert.Equal(new GalleryLayoutPreferences(view, TagChoice), GalleryLayoutPreferences.FromConfig(saved));
        Assert.False(saved.GalleryFilmstrip);
        GalleryLayoutChoice tag = new(Hints: false);
        MainWindow.ApplyGalleryLayoutChanges(saved, ViewChoice, tag);
        Assert.Equal(new GalleryLayoutPreferences(ViewChoice, tag), GalleryLayoutPreferences.FromConfig(saved));
        Assert.True(saved.GalleryFilmstrip);
        GalleryLayoutPreferences before = GalleryLayoutPreferences.FromConfig(saved);
        MainWindow.ApplyGalleryLayoutChanges(saved, null, null);
        Assert.Equal(before, GalleryLayoutPreferences.FromConfig(saved));
    }

    [Fact]
    public void AViewSaveMaterializesTheMigratedFilmstripOffForTheTaggingModeToo()
    {
        // An old profile with the strip off: the first saved change keeps the tagging mode's strip off as well (nothing changes silently).
        Config old = Load("{\"GalleryFilmstrip\":false}");
        GalleryLayoutPreferences.FromConfig(old).With(GalleryChromeMode.View, new GalleryLayoutChoice(Info: false, Filmstrip: false)).ApplyTo(old);
        Assert.False(old.GalleryLayout!.Tag.Filmstrip);
        Assert.False(old.GalleryFilmstrip);
    }

    // ---- reset ----

    [Fact]
    public void ResetGivesTheViewDefaultsInBothModesAndTheOldSettingBackOn()
    {
        Config config = new() { GalleryFilmstrip = false };
        new GalleryLayoutPreferences(ViewChoice with { Filmstrip = false }, TagChoice).ApplyTo(config);
        Assert.False(config.GalleryFilmstrip);

        GalleryLayoutPreferences.FromConfig(config).Reset().ApplyTo(config);
        Assert.Null(config.GalleryLayout);
        Assert.True(config.GalleryFilmstrip);
        Assert.Equal(GalleryLayoutPreferences.Default, GalleryLayoutPreferences.FromConfig(config));
    }

    [Fact]
    public void ResetOfOneModeKeepsTheOther()
    {
        GalleryLayoutPreferences start = new(ViewChoice, TagChoice);
        GalleryLayoutPreferences afterTag = start.With(GalleryChromeMode.Tag, GalleryLayoutChoice.Default);
        Assert.Equal(ViewChoice, afterTag.View);
        Assert.Equal(GalleryLayoutChoice.Default, afterTag.Tag);
        Assert.False(afterTag.IsDefault);
    }

    // ---- the settings page ----

    [Fact]
    public void ASettingsSaveWithoutALayoutChangeLeavesTheLiveLayoutAlone()
    {
        Config live = new();
        new GalleryLayoutPreferences(ViewChoice, TagChoice).ApplyTo(live);
        Config draft = live.Clone();
        draft.ControlsIdleHideMs = 3000;
        // Meanwhile the gallery changed its layout; the draft was taken before.
        GalleryLayoutPreferences newer = new(ViewChoice with { Info = false }, TagChoice);
        newer.ApplyTo(live);

        live.CopySettingsFieldsFrom(draft);
        Assert.Equal(newer, GalleryLayoutPreferences.FromConfig(live));
        Assert.Equal(3000, live.ControlsIdleHideMs);
    }

    [Fact]
    public void AnUntouchedFilmstripBoxOfAnOlderDraftKeepsTheGalleryLayoutOfBothModes()
    {
        // H1: the settings page loaded the box (on), then the gallery switched the strip off with L; saving another field must keep both modes.
        Config live = new();
        GalleryLayoutChoice tag = TagChoice with { Filmstrip = true };
        new GalleryLayoutPreferences(ViewChoice, tag).ApplyTo(live);
        Config draft = live.Clone();
        GalleryLayoutPreferences afterL = GalleryLayoutPreferences.FromConfig(live).With(GalleryChromeMode.View, ViewChoice with { Filmstrip = false });
        afterL.ApplyTo(live);
        Assert.False(live.GalleryFilmstrip);
        draft.Theme = "Dark";

        live.CopySettingsFieldsFrom(draft);
        Assert.Equal(afterL, GalleryLayoutPreferences.FromConfig(live));
        Assert.False(live.GalleryFilmstrip);
        Assert.Equal("Dark", live.Theme);
    }

    [Fact]
    public void TheSettingsPageCandidateComparesTheBoxWithItsBaselineNotWithTheLiveValue()
    {
        // H1 as the settings page builds its candidate: a clone of the live settings (strip already off by L) with the draft's stale box (on).
        Config live = new();
        GalleryLayoutChoice tag = TagChoice with { Filmstrip = true };
        new GalleryLayoutPreferences(ViewChoice with { Filmstrip = false }, tag).ApplyTo(live);
        GalleryLayoutPreferences before = GalleryLayoutPreferences.FromConfig(live);
        Config candidate = live.Clone();
        candidate.GalleryFilmstrip = true;
        candidate.SettingsBaselineGalleryFilmstrip = true;
        candidate.ControlsIdleHideMs = 3000;

        live.CopySettingsFieldsFrom(candidate);
        Assert.Equal(before, GalleryLayoutPreferences.FromConfig(live));
        Assert.False(live.GalleryFilmstrip);
        Assert.Equal(3000, live.ControlsIdleHideMs);

        // The person unticks the box afterwards (against its baseline: changed): the strip is off in both modes, also where it was on.
        Config unticked = live.Clone();
        unticked.GalleryFilmstrip = false;
        unticked.SettingsBaselineGalleryFilmstrip = true;
        live.CopySettingsFieldsFrom(unticked);
        GalleryLayoutPreferences result = GalleryLayoutPreferences.FromConfig(live);
        Assert.False(result.View.Filmstrip);
        Assert.False(result.Tag.Filmstrip);
        Assert.Equal(tag.Info, result.Tag.Info);

        // A ticked box whose baseline was off turns the strip on in both modes, even where the live value is on already.
        Config ticked = live.Clone();
        new GalleryLayoutPreferences(result.View with { Filmstrip = null }, result.Tag).ApplyTo(live); // L in the gallery: on again
        ticked.GalleryFilmstrip = true;
        ticked.SettingsBaselineGalleryFilmstrip = false;
        live.CopySettingsFieldsFrom(ticked);
        result = GalleryLayoutPreferences.FromConfig(live);
        Assert.Null(result.View.Filmstrip);
        Assert.Null(result.Tag.Filmstrip);
        Assert.True(live.GalleryFilmstrip);
    }

    [Fact]
    public void TheFilmstripBaselineIsDraftStateCapturedByCloneAndNeverSaved()
    {
        Config live = new() { GalleryFilmstrip = false };
        Config draft = live.Clone();
        Assert.False(draft.SettingsBaselineGalleryFilmstrip);
        Assert.DoesNotContain(nameof(Config.SettingsBaselineGalleryFilmstrip), JsonSerializer.Serialize(draft, ConfigJsonContext.Default.Config));
        // A draft without a baseline (built by hand) compares with the live value, as before.
        Config live2 = new();
        new GalleryLayoutPreferences(ViewChoice, TagChoice).ApplyTo(live2);
        Config bare = live2.Clone();
        bare.SettingsBaselineGalleryFilmstrip = null;
        bare.GalleryFilmstrip = false;
        live2.CopySettingsFieldsFrom(bare);
        Assert.False(GalleryLayoutPreferences.FromConfig(live2).View.Filmstrip);
        Assert.False(live2.GalleryFilmstrip);
    }

    [Fact]
    public void TheFilmstripBoxOffTurnsTheStripOffInBothModesAndKeepsTheOtherChoices()
    {
        Config live = new();
        new GalleryLayoutPreferences(ViewChoice with { Filmstrip = true }, TagChoice with { Filmstrip = true }).ApplyTo(live);
        Config draft = live.Clone();
        draft.GalleryFilmstrip = false;

        live.CopySettingsFieldsFrom(draft);
        GalleryLayoutPreferences result = GalleryLayoutPreferences.FromConfig(live);
        Assert.False(result.View.Filmstrip);
        Assert.False(result.Tag.Filmstrip);
        Assert.Equal(ViewChoice.Info, result.View.Info);
        Assert.Equal(GalleryFilmstripDock.Top, result.View.FilmstripDock);
        Assert.False(live.GalleryFilmstrip);

        Config back = live.Clone();
        back.GalleryFilmstrip = true;
        live.CopySettingsFieldsFrom(back);
        result = GalleryLayoutPreferences.FromConfig(live);
        Assert.Null(result.View.Filmstrip);
        Assert.Null(result.Tag.Filmstrip);
        Assert.True(live.GalleryFilmstrip);
    }

    [Fact]
    public void TheSettingsResetButtonResetsBothModesWhenTheDraftIsSaved()
    {
        Config live = new();
        new GalleryLayoutPreferences(ViewChoice, TagChoice).ApplyTo(live);
        Config draft = live.Clone();
        Assert.False(draft.GalleryLayoutResetRequested); // the flag is draft state, never copied by Clone
        draft.GalleryLayoutResetRequested = true;
        draft.GalleryFilmstrip = true;

        live.CopySettingsFieldsFrom(draft);
        Assert.Null(live.GalleryLayout);
        Assert.True(live.GalleryFilmstrip);
        Assert.False(live.GalleryLayoutResetRequested);
        Assert.DoesNotContain("GalleryLayoutResetRequested", JsonSerializer.Serialize(draft, ConfigJsonContext.Default.Config));
    }

    [Fact]
    public void TheResetWinsOverAnEditedFilmstripBoxOnlyWhereTheBoxDiffersFromTheReset()
    {
        // Reset, then the box was switched off in the same draft: the strip is off afterwards, everything else is reset.
        Config live = new();
        new GalleryLayoutPreferences(ViewChoice, TagChoice).ApplyTo(live);
        Config draft = live.Clone();
        draft.GalleryLayoutResetRequested = true;
        draft.GalleryFilmstrip = false;

        live.CopySettingsFieldsFrom(draft);
        GalleryLayoutPreferences result = GalleryLayoutPreferences.FromConfig(live);
        Assert.Equal(new GalleryLayoutChoice(Filmstrip: false), result.View);
        Assert.Equal(new GalleryLayoutChoice(Filmstrip: false), result.Tag);
    }

    // ---- copies, export and import ----

    [Fact]
    public void CloneAndCopyFromCarryTheLayout()
    {
        Config config = new();
        new GalleryLayoutPreferences(ViewChoice, TagChoice).ApplyTo(config);
        Assert.Equal(config.GalleryLayout, config.Clone().GalleryLayout);
        Config copy = new();
        copy.CopyFrom(config);
        Assert.Equal(config.GalleryLayout, copy.GalleryLayout);
    }

    [Fact]
    public void TheExportHoldsTheLayoutAndImportingItGivesTheSameLayout()
    {
        Config source = new();
        new GalleryLayoutPreferences(ViewChoice, TagChoice).ApplyTo(source);

        JsonObject export = SettingsTransfer.Export(source, "1.1.0");
        Assert.True(export.ContainsKey("GalleryLayout"));
        Assert.Equal("top", export["GalleryLayout"]!["view"]!["filmstripDock"]!.GetValue<string>());
        Config restored = JsonSerializer.Deserialize(export.ToJsonString(), ConfigJsonContext.Default.Config)!;
        restored.Validate();
        Assert.Equal(source.GalleryLayout, restored.GalleryLayout);
        Assert.Equal(source.GalleryFilmstrip, restored.GalleryFilmstrip);
    }

    [Fact]
    public void ImportingAnExportWithAnUnknownMemberLoadsCleanly()
    {
        Config source = new();
        new GalleryLayoutPreferences(ViewChoice, TagChoice).ApplyTo(source);
        source.RecheckTimer = 173;
        JsonObject export = SettingsTransfer.Export(source, "1.1.0");
        export["GalleryLayout"]!["view"]!["fromTheFuture"] = true;
        export["GalleryLayout"]!["dusk"] = new JsonObject();

        Config restored = JsonSerializer.Deserialize(export.ToJsonString(), ConfigJsonContext.Default.Config)!;
        restored.Validate();
        Assert.Equal(173, restored.RecheckTimer);
        Assert.Equal(source.GalleryLayout, restored.GalleryLayout);
    }

    [Fact]
    public void AnExportOfANeverChosenLayoutHoldsNullAndImportsAsNothingChosen()
    {
        JsonObject export = SettingsTransfer.Export(new Config(), "1.1.0");
        Assert.True(export.ContainsKey("GalleryLayout"));
        Assert.Null(export["GalleryLayout"]);
        Config restored = JsonSerializer.Deserialize(export.ToJsonString(), ConfigJsonContext.Default.Config)!;
        Assert.Null(restored.GalleryLayout);
    }

    // ---- wiring (the viewer and the window need a display; the contract is kept readable here, the behaviour is in the smokes) ----

    [Fact]
    public void TheViewerReportsUserChangesButNotTheLoad()
    {
        string chrome = Source("Desktop/GalleryViewer.Chrome.cs");
        Assert.Contains("internal event Action<GalleryChromeMode, GalleryLayoutChoice>? LayoutChoiceChanged;", chrome);
        Assert.Contains("internal void ChangeLayoutChoice(GalleryChromeMode mode, GalleryLayoutChoice choice)", chrome);
        Assert.Contains("internal GalleryLayoutChoice ResetLayout(GalleryChromeMode? mode = null)", chrome);
        // Only the person's change raises it: SetLayoutChoice (the load) does not.
        string setter = chrome[chrome.IndexOf("internal void SetLayoutChoice", StringComparison.Ordinal)..chrome.IndexOf("internal void ChangeLayoutChoice", StringComparison.Ordinal)];
        Assert.DoesNotContain("LayoutChoiceChanged", setter);
        string toggle = Source("Desktop/GalleryViewer.Filmstrip.cs");
        Assert.Contains("ChangeLayoutChoice(mode, LayoutChoice(mode) with { Filmstrip = show });", toggle);
        Assert.DoesNotContain("FilmstripPreferenceChanged", toggle);
    }

    [Fact]
    public void ALiveViewToggleRefreshesTheSettingsDraftBeforeThePreferenceWriteCompletes()
    {
        string window = Source("Desktop/Shell/MainWindow.GalleryFilmstrip.cs");
        string editor = Source("Desktop/Views/Settings/SettingsEditor.Viewer.cs");

        Assert.Contains("if (mode == GalleryChromeMode.View) _settingsEditor?.RefreshConfirmedGalleryFilmstrip(choice.Filmstrip != false);", window);
        Assert.Contains("internal void RefreshConfirmedGalleryFilmstrip(bool shown)", editor);
        Assert.Contains("GalleryFilmstrip = shown;", editor);
    }

    [Fact]
    public void OpeningSettingsRefreshesTheFilmstripFromTheLiveViewLayout()
    {
        string navigation = Source("Desktop/Shell/MainWindow.Navigation.cs");
        int settings = navigation.IndexOf("if (page == WorkspacePage.Settings)", StringComparison.Ordinal);
        Assert.True(settings >= 0);
        int nextPage = navigation.IndexOf("if (page == WorkspacePage.Rules)", settings, StringComparison.Ordinal);
        Assert.True(nextPage > settings);

        string settingsOpen = navigation[settings..nextPage];
        Assert.Contains("_settingsEditor?.RefreshConfirmedGalleryFilmstrip(GalleryLayoutNow().View.Filmstrip != false);", settingsOpen);
    }

    [Fact]
    public void TheWindowLoadsBothModesAndSavesOnlyTheChangedOne()
    {
        string window = Source("Desktop/Shell/MainWindow.GalleryFilmstrip.cs");
        Assert.Contains("_gallery.SetLayoutChoice(GalleryChromeMode.View, layout.View);", window);
        Assert.Contains("_gallery.SetLayoutChoice(GalleryChromeMode.Tag, layout.Tag);", window);
        Assert.Contains("GalleryLayoutPreferences layout = GalleryLayoutPreferences.FromConfig(current);", window);
        Assert.Contains("if (view != null) layout = layout.With(GalleryChromeMode.View, view);", window);
        Assert.Contains("if (tag != null) layout = layout.With(GalleryChromeMode.Tag, tag);", window);
        // N3: the write goes with the other gallery preferences (debounced, flushed on gallery close, window close and before a settings save).
        Assert.Contains("QueueGalleryPreference(null, null);", window);
        Assert.DoesNotContain("SaveViewPreferencesAsync", window);
        string preferences = Source("Desktop/Shell/MainWindow.GalleryPreferences.cs");
        Assert.Contains("ApplyGalleryLayoutChanges(current, layoutView, layoutTag);", preferences);
        Assert.Contains("_pendingGalleryLayoutView ??= layoutView;", preferences);
        Assert.Contains("_desiredGalleryLayout = null;", preferences);
        // After a settings save the saved layout counts again (a failed earlier write must not mask a reset or the box for good).
        string apply = Source("Desktop/Shell/MainWindow.axaml.cs");
        int at = apply.IndexOf("await _session.ApplySettingsAsync(candidate);", StringComparison.Ordinal);
        Assert.True(at > 0 && apply.IndexOf("ForgetDesiredGalleryLayout();", at, StringComparison.Ordinal) is > 0 and var forget
            && forget < apply.IndexOf("ApplyAppearance();", at, StringComparison.Ordinal));
        Assert.Contains("_gallery.LayoutChoiceChanged +=", Source("Desktop/Shell/MainWindow.Gallery.cs"));
        // The preferences are applied with the other gallery preferences, so a profile change loads them too.
        Assert.Contains("ConfigureGalleryFilmstrip();", Source("Desktop/Shell/MainWindow.GalleryPreferences.cs"));
    }

    private static string Source(string relative) => File.ReadAllText(Root(relative));

    private static string Root(string path, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", path));

    private sealed class Profile : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("meowchan-gallery-layout-").FullName;
        public void Dispose() => Directory.Delete(Path, true);
    }
}

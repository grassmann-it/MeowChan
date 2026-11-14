using System.Globalization;
using Avalonia.Media;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Theming;

namespace MeowChan.DesktopTests;

// TH3 + TH4: the picker arithmetic, the hex field, the draft -> preview -> Apply gate behind the colour dialog, the accent presets and
// the sentences the dialog shows. The dialog and its controls need a platform backend, so they are the theme-colors smoke; everything
// they decide is decided here, in ThemeConfiguratorModel and ThemeColorsText.
[Collection("Localization")]
public sealed class ThemeConfiguratorTests : IDisposable
{
    private static readonly ThemeTokenTheme[] Themes = [ThemeTokenTheme.Light, ThemeTokenTheme.Dark];

    public ThemeConfiguratorTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static Color C(string hex) => ThemeCustomization.ParseHex(hex);
    private static ThemeConfiguratorModel Model(ThemeColorOverrides? saved = null) => new(saved ?? ThemeColorOverrides.Empty, ThemePaletteTests.Base);

    // ---- picker arithmetic -------------------------------------------------------------------------------------------

    [Fact]
    public void AColourSurvivesTheRoundTripThroughHsv()
    {
        // A grid over the whole cube (every 5th value plus the ends) and a seeded sample of 200000 arbitrary colours.
        List<Color> colors = [];
        int[] steps = [.. Enumerable.Range(0, 52).Select(step => Math.Min(255, step * 5)), 254, 253, 1, 2];
        foreach (int r in steps) foreach (int g in steps) foreach (int b in steps) colors.Add(C($"#{r:X2}{g:X2}{b:X2}"));
        Random random = new(14112026);
        for (int index = 0; index < 200_000; index++) colors.Add(C($"#{random.Next(0x1000000):X6}"));
        foreach (Color color in colors)
            Assert.True(ThemeColorMath.FromHsv(ThemeColorMath.ToHsv(color)) == color, ThemeColorMath.Format(color));
    }

    [Fact]
    public void HsvPartsAreBroughtIntoTheirRange()
    {
        Assert.Equal(C("#FF0000"), ThemeColorMath.FromHsv(new PickerHsv(360, 1, 1)));
        Assert.Equal(C("#FF0000"), ThemeColorMath.FromHsv(new PickerHsv(-360, 2, 5)));
        Assert.Equal(C("#000000"), ThemeColorMath.FromHsv(new PickerHsv(120, 1, -1)));
        Assert.Equal(C("#FFFFFF"), ThemeColorMath.FromHsv(new PickerHsv(200, -3, 1)));
        Assert.Equal(new PickerHsv(0, 0, 1), ThemeColorMath.ToHsv(C("#FFFFFF")));
        Assert.Equal(120, ThemeColorMath.ToHsv(C("#00FF00")).Hue, 6);
        PickerHsv clamped = new PickerHsv(725, 1.5, -0.2).Clamped();
        Assert.Equal((5d, 1d, 0d), (clamped.Hue, clamped.Saturation, clamped.Value));
    }

    [Theory]
    [InlineData("#3A7BD5", "#3A7BD5")]
    [InlineData("3a7bd5", "#3A7BD5")]
    [InlineData("  #3a7BD5 ", "#3A7BD5")]
    [InlineData("#000000", "#000000")]
    [InlineData("FFFFFF", "#FFFFFF")]
    public void TheHexFieldTakesSixDigitsWithOrWithoutTheHash(string text, string expected)
    {
        Assert.True(ThemeColorMath.TryParse(text, out Color color));
        Assert.Equal(expected, ThemeColorMath.Format(color));
        Assert.Equal(byte.MaxValue, color.A);
    }

    [Theory]
    [InlineData(null), InlineData(""), InlineData("   "), InlineData("#"), InlineData("#3A7BD"), InlineData("#3A7BD55"), InlineData("#80102030"),
        InlineData("#ABC"), InlineData("#GGGGGG"), InlineData("##3A7BD5"), InlineData("0x3A7BD5"), InlineData("#3A7B D5"), InlineData("rgb(1,2,3)"), InlineData("#３Ａ７ＢＤ５")]
    public void TheHexFieldRefusesEverythingElse(string? text)
    {
        Assert.False(ThemeColorMath.TryParse(text, out Color color));
        Assert.Equal(default, color);
    }

    [Fact]
    public void FormatWritesUpperCaseWithoutAlpha()
    {
        Assert.Equal("#0A0B0C", ThemeColorMath.Format(C("#0a0b0c")));
        Assert.Equal("#102030", ThemeColorMath.Format(C("#80102030")));
    }

    [Theory]
    [InlineData(4.46, 4.4), InlineData(4.5, 4.5), InlineData(4.4999, 4.4), InlineData(4.55, 4.5), InlineData(21, 21), InlineData(3.0, 3.0), InlineData(2.99, 2.9), InlineData(1, 1)]
    public void TheShownRatioIsCutNotRounded(double ratio, double shown) => Assert.Equal(shown, ThemeColorMath.DisplayRatio(ratio));

    // ---- the draft, its preview and the Apply gate -------------------------------------------------------------------

    [Fact]
    public void WithoutAnEditTheDraftIsTheSavedStateAndNothingCanBeApplied()
    {
        ThemeConfiguratorModel model = Model();
        Assert.Same(ThemeColorOverrides.Empty, model.Draft);
        Assert.False(model.IsDirty);
        Assert.False(model.CanApply);
        Assert.Null(model.Block);
        foreach (ThemeTokenTheme theme in Themes)
        {
            Assert.True(model.Preview(theme).Accepted);
            Assert.Equal(8, model.Roles(theme).Count);
            Assert.Equal(ThemeUserColors.All.Select(role => role.Id), model.Roles(theme).Select(view => view.RoleId));
            // The built-in colours pass the gate: no role shows a weak pair, an error or a warning.
            Assert.All(model.Roles(theme), view =>
            {
                Assert.False(view.IsCustom);
                Assert.True(view.PairPasses, $"{theme} {view.RoleId} {view.Ratio:0.00}");
                Assert.Equal((0, 0), (view.Errors, view.Warnings));
                Assert.Equal(model.BuiltIn(theme, view.RoleId), view.Color);
            });
        }
    }

    [Fact]
    public void ARoleFollowsTheDraftInItsVariantOnly()
    {
        ThemeConfiguratorModel model = Model();
        int raised = 0;
        model.Changed += (_, _) => raised++;
        Color green = C("#1D5E3A");
        Assert.True(model.SetRole(ThemeTokenTheme.Light, ThemeUserColors.Accent, green));
        Assert.Equal(1, raised);
        Assert.True(model.IsDirty);
        Assert.True(model.CanApply);
        ThemeRoleView light = model.Role(ThemeTokenTheme.Light, ThemeUserColors.Accent);
        Assert.Equal((green, "#1D5E3A", true), (light.Color, light.Hex, light.IsCustom));
        Assert.Equal(model.BuiltIn(ThemeTokenTheme.Dark, ThemeUserColors.Accent), model.Role(ThemeTokenTheme.Dark, ThemeUserColors.Accent).Color);
        Assert.False(model.Role(ThemeTokenTheme.Dark, ThemeUserColors.Accent).IsCustom);
        // The preview palette is the one the draft gives, derived colours included.
        Assert.Equal(green, model.Preview(ThemeTokenTheme.Light).Palette.Colors["Workspace.Accent"]);
        Assert.NotEqual(ThemePaletteTests.Base(ThemeTokenTheme.Light).Colors["Workspace.AccentHover"], model.Preview(ThemeTokenTheme.Light).Palette.Colors["Workspace.AccentHover"]);
        // The same colour again changes nothing and raises nothing.
        Assert.False(model.SetRole(ThemeTokenTheme.Light, ThemeUserColors.Accent, green));
        Assert.Equal(1, raised);
    }

    [Theory, InlineData("Light"), InlineData("Dark")]
    public void ARoleSetToItsBuiltInColourIsResetNotStored(string themeName)
    {
        ThemeTokenTheme theme = Enum.Parse<ThemeTokenTheme>(themeName);
        ThemeConfiguratorModel model = Model();
        foreach (ThemeUserColor role in ThemeUserColors.All)
        {
            Assert.False(model.SetRole(theme, role.Id, model.BuiltIn(theme, role.Id)), role.Id);
            Assert.True(model.Draft.IsEmpty);
        }
        model.SetRole(theme, ThemeUserColors.Accent, C("#1D5E3A"));
        Assert.True(model.Role(theme, ThemeUserColors.Accent).IsCustom);
        // Back to the built-in colour: the override disappears, the draft is the saved (empty) state again, nothing is dirty.
        Assert.True(model.SetRole(theme, ThemeUserColors.Accent, model.BuiltIn(theme, ThemeUserColors.Accent)));
        Assert.True(model.Draft.IsEmpty);
        Assert.False(model.IsDirty);
        Assert.False(model.CanApply);
        // ... also when it is typed as hex, in either case, with or without the hash.
        model.SetRole(theme, ThemeUserColors.Accent, C("#1D5E3A"));
        string builtIn = ThemeColorMath.Format(model.BuiltIn(theme, ThemeUserColors.Accent)).ToLowerInvariant()[1..];
        Assert.Equal(ThemeHexResult.Changed, model.SetRoleHex(theme, ThemeUserColors.Accent, builtIn));
        Assert.True(model.Draft.IsEmpty);
    }

    [Fact]
    public void ATypedColourIsTakenOrRefusedAndANonColourChangesNothing()
    {
        ThemeConfiguratorModel model = Model();
        Assert.Equal(ThemeHexResult.Changed, model.SetRoleHex(ThemeTokenTheme.Dark, ThemeUserColors.Accent, "#4cc38a"));
        Assert.Equal("#4CC38A", model.Role(ThemeTokenTheme.Dark, ThemeUserColors.Accent).Hex);
        ThemeColorOverrides before = model.Draft;
        Assert.Equal(ThemeHexResult.Unchanged, model.SetRoleHex(ThemeTokenTheme.Dark, ThemeUserColors.Accent, "#4CC38A"));
        foreach (string bad in new[] { "", "#12", "#ZZZZZZ", "blue", "#4CC38A00", "#4CC38" })
            Assert.Equal(ThemeHexResult.Invalid, model.SetRoleHex(ThemeTokenTheme.Dark, ThemeUserColors.Accent, bad));
        Assert.Same(before, model.Draft);
    }

    [Fact]
    public void TextBelowFourPointFiveRefusesApplyAndNamesVariantAndRole()
    {
        ThemeConfiguratorModel model = Model();
        // Text as light as the panel it sits on: unreadable, so the draft cannot be applied.
        model.SetRole(ThemeTokenTheme.Dark, ThemeUserColors.Text, model.Preview(ThemeTokenTheme.Dark).Palette.Colors["Workspace.Panel"]);
        Assert.True(model.IsDirty);
        Assert.False(model.Preview(ThemeTokenTheme.Dark).Accepted);
        Assert.True(model.Preview(ThemeTokenTheme.Light).Accepted);
        ThemeApplyBlock? block = model.Block;
        Assert.NotNull(block);
        Assert.Equal((ThemeTokenTheme.Dark, ThemeUserColors.Text), (block.Theme, block.RoleId));
        Assert.True(block.ErrorCount > 0);
        Assert.False(model.CanApply);
        ThemeRoleView view = model.Role(ThemeTokenTheme.Dark, ThemeUserColors.Text);
        Assert.False(view.PairPasses);
        Assert.True(view.IsTextPair);
        Assert.True(view.Errors > 0);
    }

    // ---- reset, save, commit ------------------------------------------------------------------------------------------

    [Fact]
    public void ResettingARoleThatIsAtItsDefaultChangesNothing()
    {
        ThemeConfiguratorModel model = Model();
        int raised = 0;
        model.Changed += (_, _) => raised++;
        foreach (ThemeTokenTheme theme in Themes)
            foreach (ThemeUserColor role in ThemeUserColors.All)
                Assert.False(model.ResetRole(theme, role.Id), role.Id);
        Assert.False(model.ResetAll());
        Assert.Equal(0, raised);
        Assert.False(model.IsDirty);
    }

    [Fact]
    public void ResettingARoleTouchesThatRoleInThatVariantOnly()
    {
        ThemeConfiguratorModel model = Model();
        model.SetRole(ThemeTokenTheme.Light, ThemeUserColors.Accent, C("#1C5C36"));
        model.SetRole(ThemeTokenTheme.Dark, ThemeUserColors.Accent, C("#6DC793"));
        model.SetRole(ThemeTokenTheme.Light, ThemeUserColors.Favorite, C("#8A5A00"));
        Assert.True(model.ResetRole(ThemeTokenTheme.Light, ThemeUserColors.Accent));
        Assert.False(model.Role(ThemeTokenTheme.Light, ThemeUserColors.Accent).IsCustom);
        Assert.True(model.Role(ThemeTokenTheme.Dark, ThemeUserColors.Accent).IsCustom);
        Assert.True(model.Role(ThemeTokenTheme.Light, ThemeUserColors.Favorite).IsCustom);
        Assert.True(model.ResetAll());
        Assert.True(model.Draft.IsEmpty);
        Assert.False(model.IsDirty);
    }

    [Fact]
    public void ResetAllAfterASavedCustomisationIsADirtyDraftThatCanBeApplied()
    {
        ThemeColorOverrides saved = ThemeColorOverrides.Empty.WithRole(ThemeTokenTheme.Dark, ThemeUserColors.Accent, C("#6DC793"));
        ThemeConfiguratorModel model = Model(saved);
        Assert.False(model.IsDirty);
        Assert.False(model.CanApply);
        Assert.True(model.ResetAll());
        Assert.True(model.IsDirty);
        Assert.True(model.CanApply);
        // Saving makes the draft the baseline again; going back to the saved colours is then the change.
        model.MarkSaved(model.Draft);
        Assert.False(model.IsDirty);
        Assert.False(model.CanApply);
        Assert.True(model.SetRole(ThemeTokenTheme.Dark, ThemeUserColors.Accent, C("#6DC793")));
        Assert.True(model.CanApply);
    }

    [Fact]
    public void AnEditedDraftThatPassesCanBeAppliedAndOneThatFailsInEitherVariantCannot()
    {
        ThemeConfiguratorModel model = Model();
        model.SetRole(ThemeTokenTheme.Light, ThemeUserColors.Accent, C("#1C5C36"));
        Assert.True(model.CanApply);
        // A refused Dark does not hide behind a good Light: the first failing variant is named.
        model.SetRole(ThemeTokenTheme.Dark, ThemeUserColors.Text, model.Preview(ThemeTokenTheme.Dark).Palette.Colors["Workspace.Panel"]);
        Assert.False(model.CanApply);
        Assert.Equal(ThemeTokenTheme.Dark, model.Block!.Theme);
        model.ResetRole(ThemeTokenTheme.Dark, ThemeUserColors.Text);
        Assert.True(model.CanApply);
    }

    [Fact]
    public async Task TheCommitPutsOnlyTheColoursIntoTheProfileThroughTheSettingsPath()
    {
        var committed = new Config { RecheckTimer = 77 };
        ThemeColorOverrides draft = ThemeColorOverrides.Empty.WithRole(ThemeTokenTheme.Light, ThemeUserColors.Accent, C("#1C5C36"));
        Config? applied = null;
        await ThemeColorsCommit.CommitAsync(committed, draft, candidate => { applied = candidate; return Task.CompletedTask; });
        Assert.NotNull(applied);
        Assert.NotSame(committed, applied);
        Assert.Equal(77, applied!.RecheckTimer);
        Assert.Equal(draft, applied.ThemeOverrides);
        Assert.Equal(draft, committed.ThemeOverrides);
    }

    [Fact]
    public async Task AFailingSettingsPathLeavesTheCommittedProfileAlone()
    {
        var committed = new Config();
        ThemeColorOverrides draft = ThemeColorOverrides.Empty.WithRole(ThemeTokenTheme.Light, ThemeUserColors.Accent, C("#1C5C36"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ThemeColorsCommit.CommitAsync(committed, draft, _ => throw new InvalidOperationException("disk full")));
        Assert.True(committed.ThemeOverrides.IsEmpty);
    }

    // ---- the sentences ------------------------------------------------------------------------------------------------

    [Fact]
    public void TheContrastIsWrittenOutAndTheRatioIsCutNotRounded()
    {
        ThemeConfiguratorModel model = Model();
        ThemeRoleView ok = model.Role(ThemeTokenTheme.Light, ThemeUserColors.Text);
        Assert.Matches(@"^\d+\.\d : 1 – good$", ThemeColorsText.Contrast(ok));
        model.SetRole(ThemeTokenTheme.Light, ThemeUserColors.Text, model.Preview(ThemeTokenTheme.Light).Palette.Colors["Workspace.Panel"]);
        ThemeRoleView weak = model.Role(ThemeTokenTheme.Light, ThemeUserColors.Text);
        Assert.EndsWith(" : 1 – too weak for text", ThemeColorsText.Contrast(weak));
        Assert.Matches(@"^1\.\d : 1 – too weak for text$", ThemeColorsText.Contrast(weak));
        Assert.Equal("Measured against: Background", ThemeColorsText.Against(weak));
        Assert.Equal("Default", ThemeColorsText.State(ok));
        Assert.Equal("Changed", ThemeColorsText.State(weak));
    }

    [Fact]
    public void TheGermanSentencesUseTheCommaAndTheBlockNamesVariantAndRole()
    {
        Texts.SetLanguage("de");
        ThemeConfiguratorModel model = Model();
        model.SetRole(ThemeTokenTheme.Dark, ThemeUserColors.Text, model.Preview(ThemeTokenTheme.Dark).Palette.Colors["Workspace.Panel"]);
        ThemeRoleView weak = model.Role(ThemeTokenTheme.Dark, ThemeUserColors.Text);
        Assert.Matches(@"^1,\d : 1 – zu schwach für Text$", ThemeColorsText.Contrast(weak));
        Assert.Equal("Übernehmen geht noch nicht: In „Dunkel“ ist die Farbe „Text“ zu schwach für Text.", ThemeColorsText.BlockText(model.Block));
        Assert.Equal("4,8", ThemeColorsText.Ratio(4.87));
        Assert.Null(ThemeColorsText.BlockText(null));
    }

    [Fact]
    public void AGenericBlockSentenceIsUsedWhenNoRoleOwnsTheFailure()
    {
        Assert.Equal("Can’t apply yet: some text colors in Light are too weak.", ThemeColorsText.BlockText(new ThemeApplyBlock(ThemeTokenTheme.Light, null, 3)));
        Assert.Equal("Can’t apply yet: in Dark, the color “Accent” is too weak for text.",
            ThemeColorsText.BlockText(new ThemeApplyBlock(ThemeTokenTheme.Dark, ThemeUserColors.Accent, 1)));
    }

    // ---- languages ----------------------------------------------------------------------------------------------------

    public static TheoryData<string> AllLanguages()
    {
        TheoryData<string> data = new() { "en", "de" };
        foreach (string code in Texts.BundledPackCodes()) data.Add(code);
        return data;
    }

    [Theory, MemberData(nameof(AllLanguages))]
    public void EveryDialogKeyIsTranslatedWithItsPlaceholders(string code)
    {
        Texts.Initialize();
        Texts.SetLanguage(code);
        foreach (var (key, english, _) in ThemeColorsCatalog.Entries)
        {
            string text = Texts.T(key);
            Assert.NotEqual(key, text);
            Assert.False(string.IsNullOrWhiteSpace(text), $"{code} {key}");
            foreach (string placeholder in new[] { "{0}", "{1}" })
                Assert.Equal(english.Contains(placeholder), text.Contains(placeholder));
        }
        Assert.Equal(57, ThemeColorsCatalog.Entries.Length); // TH5 added "Try in window" and its hint
    }

    [Fact]
    public void TheGermanAndEnglishPackTextsDifferAndEveryRoleAndPresetHasAName()
    {
        foreach (ThemeUserColor role in ThemeUserColors.All)
            Assert.Contains(ThemeColorsCatalog.Entries, entry => entry.Key == "theme.color." + role.Id);
        foreach (ThemeAccentPreset preset in ThemeAccentPresets.All)
            Assert.Contains(ThemeColorsCatalog.Entries, entry => entry.Key == "theme.accent." + preset.Id);
        Assert.All(ThemeColorsCatalog.Entries.Where(entry => entry.Key.StartsWith("theme.colors.", StringComparison.Ordinal) && entry.Key is not ("theme.colors.hex" or "theme.colors.picker.degrees" or "theme.colors.picker.percent" or "theme.colors.preview.chip")),
            entry => Assert.NotEqual(entry.English, entry.German));
    }

    [Fact]
    public void TheGermanIntroKeepsTheSentenceOfTheCustomisationSettings()
    {
        Texts.SetLanguage("de");
        Assert.StartsWith("Eigene Farben gelten getrennt für Hell und Dunkel und wirken sofort, ohne Neustart.", Texts.T("theme.colors.intro"));
    }
}

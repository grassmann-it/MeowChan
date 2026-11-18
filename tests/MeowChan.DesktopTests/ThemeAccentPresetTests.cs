using Avalonia.Media;
using MeowChan.Desktop.Theming;

namespace MeowChan.DesktopTests;

// TH4: the six accent presets pass the contrast gate in Light and in Dark, and applying one sets the accent of both variants.
public sealed class ThemeAccentPresetTests
{
    private static readonly ThemeTokenTheme[] Themes = [ThemeTokenTheme.Light, ThemeTokenTheme.Dark];

    private static ThemeConfiguratorModel Model() => new(ThemeColorOverrides.Empty, ThemePaletteTests.Base);

    public static TheoryData<string, string> PresetsAndThemes()
    {
        TheoryData<string, string> data = new();
        foreach (ThemeAccentPreset preset in ThemeAccentPresets.All)
            foreach (ThemeTokenTheme theme in Themes) data.Add(preset.Id, theme.ToString());
        return data;
    }

    [Fact]
    public void ThereAreSixPresetsWithUniqueIdsInTheDocumentedOrder()
    {
        Assert.Equal(["simba", "mila", "blue", "green", "violet", "pink"], ThemeAccentPresets.All.Select(preset => preset.Id));
        Assert.Equal(6, ThemeAccentPresets.All.Select(preset => preset.Id).Distinct().Count());
        Assert.Same(ThemeAccentPresets.All[2], ThemeAccentPresets.Find("blue"));
        Assert.Null(ThemeAccentPresets.Find("Blue"));
        Assert.Null(ThemeAccentPresets.Find(null));
    }

    [Theory, MemberData(nameof(PresetsAndThemes))]
    public void EveryPresetPassesTheGateInBothVariants(string presetId, string themeName)
    {
        ThemeTokenTheme theme = Enum.Parse<ThemeTokenTheme>(themeName);
        ThemeAccentPreset preset = ThemeAccentPresets.Find(presetId)!;
        ThemeConfiguratorModel model = Model();
        model.ApplyPreset(preset);
        ThemePreview preview = model.Preview(theme);
        string detail = string.Join("; ", preview.Contrast.All.Select(issue =>
            $"{issue.Severity} {issue.Foreground} on {issue.Background} {issue.Ratio:0.00} < {issue.Required}"));
        Assert.True(preview.Accepted, $"{presetId} {theme}: {detail}");
        // The reviewed minima of the built-in accents are tight; a preset may sit under one of them (a warning, allowed) but not drift further.
        Assert.True(preview.Contrast.Warnings.Count <= 1, $"{presetId} {theme}: {detail}");
        Assert.True(model.Role(theme, ThemeUserColors.Accent).PairPasses);
    }

    [Fact]
    public void EveryPresetCanBeAppliedFromTheDialog()
    {
        foreach (ThemeAccentPreset preset in ThemeAccentPresets.All)
        {
            ThemeConfiguratorModel model = Model();
            model.ApplyPreset(preset);
            Assert.Null(model.Block);
            Assert.True(model.CanApply, preset.Id);
        }
    }

    [Fact]
    public void AnAppliedPresetSetsTheAccentOfBothVariantsAndNothingElse()
    {
        foreach (ThemeAccentPreset preset in ThemeAccentPresets.All)
        {
            ThemeConfiguratorModel model = Model();
            Assert.True(model.ApplyPreset(preset), preset.Id);
            foreach (ThemeTokenTheme theme in Themes)
            {
                Color expected = preset.For(theme) ?? model.BuiltInAccent(theme);
                Assert.Equal(expected, model.Role(theme, ThemeUserColors.Accent).Color);
                // Only the accent role is touched.
                Assert.All(model.Roles(theme).Where(view => view.RoleId != ThemeUserColors.Accent), view => Assert.False(view.IsCustom, view.RoleId));
            }
            Assert.Same(preset, model.ActivePreset);
        }
    }

    [Fact]
    public void APresetColourThatIsTheBuiltInAccentIsNotStored()
    {
        ThemeConfiguratorModel model = Model();
        // Light's built-in accent is the Simba orange: the preset's Light entry is "the built-in one" and leaves no override.
        model.ApplyPreset(ThemeAccentPresets.Find("simba")!);
        Assert.Empty(model.Draft.Light);
        Assert.NotEmpty(model.Draft.Dark);
        // Dark's built-in accent is blue: the blue preset leaves Dark alone and sets Light only.
        model = Model();
        model.ApplyPreset(ThemeAccentPresets.Find("blue")!);
        Assert.Empty(model.Draft.Dark);
        Assert.NotEmpty(model.Draft.Light);
        // A preset whose hex equals the built-in colour is reset, not stored (even when written out explicitly).
        ThemeAccentPreset explicitBuiltIn = new("custom", ThemeColorMath.Format(model.BuiltInAccent(ThemeTokenTheme.Light)), ThemeColorMath.Format(model.BuiltInAccent(ThemeTokenTheme.Dark)));
        model = Model();
        model.SetRole(ThemeTokenTheme.Light, ThemeUserColors.Accent, ThemeCustomization.ParseHex("#1D5E3A"));
        model.ApplyPreset(explicitBuiltIn);
        Assert.True(model.Draft.IsEmpty);
    }

    [Fact]
    public void ThePresetsDifferInBothVariantsAndFromEachOther()
    {
        ThemeConfiguratorModel model = Model();
        foreach (ThemeTokenTheme theme in Themes)
        {
            Color[] accents = ThemeAccentPresets.All.Select(preset => preset.For(theme) ?? model.BuiltInAccent(theme)).ToArray();
            Assert.Equal(accents.Length, accents.Distinct().Count());
        }
    }

    [Fact]
    public void ThePresetThatIsOnFollowsTheDraft()
    {
        ThemeConfiguratorModel model = Model();
        Assert.Null(model.ActivePreset);
        model.ApplyPreset(ThemeAccentPresets.Find("violet")!);
        Assert.Equal("violet", model.ActivePreset?.Id);
        // A hand-picked accent in one variant matches no preset.
        model.SetRole(ThemeTokenTheme.Dark, ThemeUserColors.Accent, ThemeCustomization.ParseHex("#123456"));
        Assert.Null(model.ActivePreset);
        model.ApplyPreset(ThemeAccentPresets.Find("pink")!);
        Assert.Equal("pink", model.ActivePreset?.Id);
        model.ResetRole(ThemeTokenTheme.Light, ThemeUserColors.Accent);
        Assert.Null(model.ActivePreset);
        Assert.True(model.ResetAll());
        Assert.Null(model.ActivePreset);
    }
}

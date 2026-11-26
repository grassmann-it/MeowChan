using Avalonia.Media;
using MeowChan.Desktop;
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

    // Tom, 26.11.2026 00:30: "Fable soll den bestehenden Default-Profilen sinnvolle Farben geben, muss nicht Rot und Gold sein, ggf. passt
    // bei anderen Farbprofilen anderes." A preset therefore sets the accent and its own heart and star colour, and nothing else.
    [Fact]
    public void AnAppliedPresetSetsTheAccentAndItsHeartAndStarColourOfBothVariantsAndNothingElse()
    {
        foreach (ThemeAccentPreset preset in ThemeAccentPresets.All)
        {
            ThemeConfiguratorModel model = Model();
            Assert.True(model.ApplyPreset(preset), preset.Id);
            foreach (ThemeTokenTheme theme in Themes)
            {
                Color expected = preset.For(theme) ?? model.BuiltInAccent(theme);
                Assert.Equal(expected, model.Role(theme, ThemeUserColors.Accent).Color);
                foreach (string mark in ThemeAccentPresets.MarkRoles)
                {
                    Color builtIn = ThemePalette.Build(ThemePaletteTests.Base(theme)).Colors[ThemeUserColors.Require(mark).Seed];
                    Assert.Equal(preset.Mark(theme, mark) ?? builtIn, model.Role(theme, mark).Color);
                }
                // Only the accent, the heart and the stars are touched.
                Assert.All(model.Roles(theme).Where(view => view.RoleId != ThemeUserColors.Accent && !ThemeAccentPresets.MarkRoles.Contains(view.RoleId)),
                    view => Assert.False(view.IsCustom, view.RoleId));
            }
            Assert.Same(preset, model.ActivePreset);
        }
    }

    [Theory, MemberData(nameof(PresetsAndThemes))]
    public void HeartAndStarsOfAPresetAreReadableOnEverySurfaceAndStandApartFromAccentErrorAndEachOther(string presetId, string themeName)
    {
        ThemeTokenTheme theme = Enum.Parse<ThemeTokenTheme>(themeName);
        ThemeConfiguratorModel model = Model();
        model.ApplyPreset(ThemeAccentPresets.Find(presetId)!);
        IReadOnlyDictionary<string, Color> colors = model.Preview(theme).Palette.Colors;
        Color heart = colors["Workspace.Favorite"], star = colors["Workspace.Rating"];
        foreach (Color mark in new[] { heart, star })
        {
            foreach (string surface in new[] { "Workspace.Background", "Workspace.Panel", "Workspace.Raised", "Workspace.Card", "Workspace.ChipSurface", "Workspace.AccentSurface" })
                Assert.True(ContrastRules.Contrast(ThemePalette.Hex(mark), ThemePalette.Hex(colors[surface])) >= ContrastGate.GraphicMinimum,
                    $"{presetId} {theme}: {ThemePalette.Hex(mark)} on {surface}");
            if (theme != ThemeTokenTheme.Dark) continue;
            // The Dark tone is also the one on the dark disc of the tile badges and on the viewer's scrim; the worst picture under the disc is white.
            Color disc = VideoPlayMark.DiscColor;
            string overWhite = ContrastRules.CompositeOver($"#{disc.A:X2}{disc.R:X2}{disc.G:X2}{disc.B:X2}", "#FFFFFF");
            Assert.True(ContrastRules.Contrast(ThemePalette.Hex(mark), overWhite) >= ContrastGate.GraphicMinimum, $"{presetId}: {ThemePalette.Hex(mark)} on the badge disc");
        }
        // Told apart at a glance: heart, stars, accent (the selection) and the error colour are four different colours, not shades of one
        // (RGB distance; the dark tones of Light lie closer together than the bright ones of Dark).
        Assert.True(Distance(heart, star) >= 60, $"{presetId} {theme}: heart and stars");
        Assert.True(Distance(heart, colors["Workspace.Accent"]) >= 80, $"{presetId} {theme}: heart and accent");
        Assert.True(Distance(star, colors["Workspace.Accent"]) >= 35, $"{presetId} {theme}: stars and accent");
        Assert.NotEqual(colors["Workspace.Error"], heart);
        Assert.NotEqual(colors["Workspace.Error"], star);
    }

    private static double Distance(Color first, Color second) =>
        Math.Sqrt(Math.Pow(first.R - second.R, 2) + Math.Pow(first.G - second.G, 2) + Math.Pow(first.B - second.B, 2));

    [Fact]
    public void AHeartOrStarColourPickedByHandSurvivesAPresetAndOneFromAPresetFollowsTheNextPreset()
    {
        ThemeConfiguratorModel model = Model();
        Color own = ThemeCustomization.ParseHex("#7A2FB8");
        model.SetRole(ThemeTokenTheme.Light, ThemeUserColors.Favorite, own);
        model.ApplyPreset(ThemeAccentPresets.Find("green")!);
        // The user's own heart stays; the variant and the role without an own colour take the preset's.
        Assert.Equal(own, model.Role(ThemeTokenTheme.Light, ThemeUserColors.Favorite).Color);
        ThemeAccentPreset green = ThemeAccentPresets.Find("green")!, violet = ThemeAccentPresets.Find("violet")!, blue = ThemeAccentPresets.Find("blue")!;
        Assert.Equal(green.Mark(ThemeTokenTheme.Dark, ThemeUserColors.Favorite), model.Role(ThemeTokenTheme.Dark, ThemeUserColors.Favorite).Color);
        Assert.Equal(green.Mark(ThemeTokenTheme.Dark, ThemeUserColors.Rating), model.Role(ThemeTokenTheme.Dark, ThemeUserColors.Rating).Color);
        model.ApplyPreset(violet);
        Assert.Equal(own, model.Role(ThemeTokenTheme.Light, ThemeUserColors.Favorite).Color);
        Assert.Equal(violet.Mark(ThemeTokenTheme.Dark, ThemeUserColors.Favorite), model.Role(ThemeTokenTheme.Dark, ThemeUserColors.Favorite).Color);
        // Violet keeps the built-in gold, and blue the built-in red and gold: the colours of the earlier preset are reset, not kept.
        Assert.False(model.Role(ThemeTokenTheme.Dark, ThemeUserColors.Rating).IsCustom);
        model.ApplyPreset(blue);
        Assert.False(model.Role(ThemeTokenTheme.Dark, ThemeUserColors.Favorite).IsCustom);
        Assert.True(model.Role(ThemeTokenTheme.Light, ThemeUserColors.Favorite).IsCustom);
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

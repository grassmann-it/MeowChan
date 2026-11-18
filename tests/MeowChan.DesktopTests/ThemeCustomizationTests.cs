using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using MeowChan.Desktop.Theming;

namespace MeowChan.DesktopTests;

// TH2: user roles, related-seed derivation, the contrast gate, the preview and the live apply on a plain dictionary.
[Collection("Theme runtime")]
public sealed class ThemeCustomizationTests
{
    private static readonly ThemeTokenTheme[] Themes = [ThemeTokenTheme.Dark, ThemeTokenTheme.Light];

    private static ThemePaletteBase Base(ThemeTokenTheme theme) => ThemePaletteTests.Base(theme);
    private static Color C(string hex) => ThemeCustomization.ParseHex(hex);
    private static Dictionary<string, Color> Seeds(params (string Key, string Hex)[] entries) =>
        entries.ToDictionary(entry => entry.Key, entry => C(entry.Hex), StringComparer.Ordinal);

    [Theory, InlineData("Dark"), InlineData("Light")]
    public void WithoutOverridesThePreviewIsExactlyTheBuiltInPaletteAndPassesTheGate(string themeName)
    {
        ThemeTokenTheme theme = Enum.Parse<ThemeTokenTheme>(themeName);
        ThemePalette builtIn = ThemePalette.Build(Base(theme));
        foreach (IReadOnlyDictionary<string, Color>? overrides in new IReadOnlyDictionary<string, Color>?[]
                     { null, new Dictionary<string, Color>(), ThemeUserColors.All.SelectMany(role => role.Seeds).ToDictionary(seed => seed, seed => Base(theme).Colors[seed]) })
        {
            ThemePreview preview = ThemeCustomization.Preview(Base(theme), overrides);
            Assert.Equal(builtIn.Colors, preview.Palette.Colors);
            Assert.Equal(builtIn.Pinned, preview.Palette.Pinned);
            Assert.Empty(preview.Palette.Overrides);
            Assert.Empty(preview.Derived);
            Assert.Empty(preview.Contrast.Errors);
            Assert.Empty(preview.Contrast.Warnings);
            Assert.True(preview.Accepted);
        }
    }

    [Fact]
    public void TheRolesAreTheCuratedEightOverRealSeedsWithoutSharing()
    {
        Assert.Equal(["accent", "background", "surface", "text", "muted", "border", "favorite", "error"], ThemeUserColors.All.Select(role => role.Id));
        string[] seeds = ThemeUserColors.All.SelectMany(role => role.Seeds).ToArray();
        Assert.Equal(seeds.Length, seeds.Distinct(StringComparer.Ordinal).Count());
        Assert.All(seeds, seed => Assert.Contains(seed, (IReadOnlySet<string>)ThemeTokens.Seeds));
        Assert.Equal(("Workspace.Accent", "Workspace.Background", "Workspace.Panel", "Workspace.Text", "Workspace.Muted", "Workspace.Border",
                "Workspace.Favorite", "Workspace.Error"),
            (Role("accent").Seed, Role("background").Seed, Role("surface").Seed, Role("text").Seed, Role("muted").Seed, Role("border").Seed,
                Role("favorite").Seed, Role("error").Seed));
        Assert.Contains("Workspace.Raised", Role("surface").Seeds);
        Assert.Contains("Workspace.Card", Role("surface").Seeds);
        Assert.Contains("Workspace.ControlBorder", Role("border").Seeds);
        Assert.Equal(["Workspace.AccentHover", "Workspace.OnAccent", "Workspace.AccentHoverText", "Workspace.AccentSurface",
            "Workspace.ChipSelectedSurface", "Workspace.ChipSelectedBorder", "Workspace.Progress"], Role("accent").Related.Select(related => related.Seed));
        foreach (ThemeUserColor role in ThemeUserColors.All)
        {
            Assert.Same(role, ThemeUserColors.OwnerOf(role.Seed));
            Assert.Contains(role.Counterpart, (IReadOnlySet<string>)ThemeTokens.Seeds);
            Assert.Contains(role.Minimum, new[] { ContrastGate.TextMinimum, ContrastGate.GraphicMinimum });
            // An on-colour is measured against seeds already resolved: the primary or a related seed before it.
            for (int index = 0; index < role.Related.Count; index++)
                Assert.All(role.Related[index].Against, key =>
                    Assert.True(key == role.Seed || role.Related.Take(index).Any(earlier => earlier.Seed == key), $"{role.Id}: {key}"));
        }
        Assert.Null(ThemeUserColors.Find("Accent"));
        Assert.Null(ThemeUserColors.OwnerOf("Workspace.Scrim"));
        Assert.Throws<ArgumentException>(() => ThemeUserColors.Require("nope"));
    }

    [Theory, InlineData("Dark"), InlineData("Light")]
    public void AChangedAccentDerivesItsRelatedSeedsDeterministically(string themeName)
    {
        ThemeTokenTheme theme = Enum.Parse<ThemeTokenTheme>(themeName);
        ThemePaletteBase basePalette = Base(theme);
        Color green = C(theme == ThemeTokenTheme.Dark ? "#4CC38A" : "#1D5E3A");
        ThemePreview preview = ThemeCustomization.Preview(basePalette, Seeds(("Workspace.Accent", ThemePalette.Hex(green))));
        IReadOnlyDictionary<string, Color> colors = preview.Palette.Colors;

        Assert.Equal(Role("accent").Related.Select(related => related.Seed).Order(), preview.Derived.Order());
        Assert.Equal(["Workspace.Accent"], preview.Explicit.Keys);
        Assert.Equal(green, colors["Workspace.Accent"]);
        // Equal to the accent in the base: stays equal (chip selection in both, the progress fill in Dark).
        Assert.Equal(green, colors["Workspace.ChipSelectedBorder"]);
        if (theme == ThemeTokenTheme.Dark) Assert.Equal(green, colors["Workspace.Progress"]);
        // The tinted surfaces keep their lightness and take the new hue.
        foreach (string surface in new[] { "Workspace.AccentSurface", "Workspace.ChipSelectedSurface" })
        {
            Assert.Equal(basePalette.Colors[surface].ToHsl().L, colors[surface].ToHsl().L, 2);
            Assert.InRange(Math.Abs(colors[surface].ToHsl().H - green.ToHsl().H), 0, 12);
        }
        Assert.Equal(colors["Workspace.AccentSurface"], colors["Workspace.ChipSelectedSurface"]);
        // The hover tone keeps its step: lighter than the accent, as in the base.
        Assert.Equal(Math.Sign(basePalette.Colors["Workspace.AccentHover"].ToHsl().L - basePalette.Colors["Workspace.Accent"].ToHsl().L),
            Math.Sign(colors["Workspace.AccentHover"].ToHsl().L - green.ToHsl().L));
        // Text on the accent is black or white, the better of the two.
        Assert.Contains(ThemePalette.Hex(colors["Workspace.OnAccent"]), new[] { "#000000", "#FFFFFF" });
        // The Semi mirrors and the variations follow (TH1).
        Assert.Equal(green, colors["ButtonSolidPrimaryBackground"]);
        Assert.Equal(ThemePalette.WithAlpha(green, ThemePalette.PreviewAlpha), colors[DragPreviewKey(theme)]);
        // Deterministic: the same input gives the same palette.
        Assert.Equal(colors, ThemeCustomization.Preview(basePalette, Seeds(("Workspace.Accent", ThemePalette.Hex(green)))).Palette.Colors);
    }

    [Fact]
    public void OnColorPicksBlackOrWhiteByContrast()
    {
        Assert.Equal("#000000", ThemePalette.Hex(ThemeCustomization.OnColor([C("#FFE066")])));
        Assert.Equal("#FFFFFF", ThemePalette.Hex(ThemeCustomization.OnColor([C("#1E3A8A")])));
        // The worse of several backgrounds decides: a mid accent with a light hover tone.
        Color mid = C("#3B82F6"), light = C("#93C5FD");
        Color chosen = ThemeCustomization.OnColor([mid, light]);
        double Worst(Color candidate) => new[] { mid, light }.Min(bg => ContrastRules.Contrast(ThemePalette.Hex(candidate), ThemePalette.Hex(bg)));
        Assert.True(Worst(chosen) >= Worst(ThemePalette.Hex(chosen) == "#000000" ? C("#FFFFFF") : C("#000000")));
        Assert.Equal("#000000", ThemePalette.Hex(ThemeCustomization.Preview(Base(ThemeTokenTheme.Dark),
            Seeds(("Workspace.Accent", "#FFE066"))).Palette.Colors["Workspace.OnAccent"]));
    }

    [Fact]
    public void AnExplicitRelatedSeedIsNeverDerived()
    {
        ThemePreview preview = ThemeCustomization.Preview(Base(ThemeTokenTheme.Dark),
            Seeds(("Workspace.Accent", "#4CC38A"), ("Workspace.AccentHover", "#123456"), ("Workspace.OnAccent", "#FFFFFF")));
        Assert.Equal(C("#123456"), preview.Palette.Colors["Workspace.AccentHover"]);
        Assert.Equal(C("#FFFFFF"), preview.Palette.Colors["Workspace.OnAccent"]);
        Assert.DoesNotContain("Workspace.AccentHover", preview.Derived);
        Assert.DoesNotContain("Workspace.OnAccent", preview.Derived);
        Assert.Contains("Workspace.AccentSurface", preview.Derived);
        Assert.Equal(3, preview.Explicit.Count);
        // Only a related seed (its primary unchanged): nothing is derived.
        ThemePreview only = ThemeCustomization.Preview(Base(ThemeTokenTheme.Dark), Seeds(("Workspace.AccentHover", "#123456")));
        Assert.Empty(only.Derived);
        Assert.Equal(Base(ThemeTokenTheme.Dark).Colors["Workspace.AccentSurface"], only.Palette.Colors["Workspace.AccentSurface"]);
    }

    [Theory, InlineData("Dark", "#232A3A"), InlineData("Light", "#F4F1EA")]
    public void ASurfaceChangeMovesTheSteppedSurfacesByTheirBaseDifference(string themeName, string panel)
    {
        ThemeTokenTheme theme = Enum.Parse<ThemeTokenTheme>(themeName);
        ThemePaletteBase basePalette = Base(theme);
        Color p0 = basePalette.Colors["Workspace.Panel"], p1 = C(panel);
        ThemePreview preview = ThemeCustomization.Preview(basePalette, Seeds(("Workspace.Panel", panel)));
        foreach (string seed in new[] { "Workspace.Raised", "Workspace.Card", "Workspace.Overlay", "Workspace.ChipSurface", "Workspace.Hover" })
        {
            Color r0 = basePalette.Colors[seed], r1 = preview.Palette.Colors[seed];
            Assert.Equal((Clamp(p1.R + r0.R - p0.R), Clamp(p1.G + r0.G - p0.G), Clamp(p1.B + r0.B - p0.B)), (r1.R, r1.G, r1.B));
        }
        Assert.Equal(p1, preview.Palette.Colors["Workspace.Overlay"]); // equal to the panel in the base: stays equal
        Assert.Equal(basePalette.Colors["Workspace.Background"], preview.Palette.Colors["Workspace.Background"]);
        static int Clamp(int value) => Math.Clamp(value, 0, 255);
    }

    [Theory, InlineData("Dark"), InlineData("Light")]
    public void EveryRoleDerivesItsRelatedSeedsAndNothingElse(string themeName)
    {
        ThemeTokenTheme theme = Enum.Parse<ThemeTokenTheme>(themeName);
        ThemePaletteBase basePalette = Base(theme);
        foreach (ThemeUserColor role in ThemeUserColors.All)
        {
            Color changed = ThemePalette.Scale(basePalette.Colors[role.Seed], 0.9);
            var (seeds, derived) = ThemeCustomization.Expand(basePalette, new Dictionary<string, Color> { [role.Seed] = changed });
            Assert.Equal(role.Related.Select(related => related.Seed).Order(), derived.Order());
            Assert.Equal(role.Seeds.Order(), seeds.Keys.Order());
        }
    }

    [Fact]
    public void TextBelowFourPointFiveIsRefusedWithTheFailingPairs()
    {
        ThemePreview preview = ThemeCustomization.Preview(Base(ThemeTokenTheme.Dark), Seeds(("Workspace.Text", "#1B1E22")));
        Assert.False(preview.Accepted);
        ThemeContrastIssue panel = Assert.Single(preview.Contrast.Errors, issue => issue is { Foreground: "Workspace.Text", Background: "Workspace.Panel" });
        Assert.Equal(ThemeContrastSeverity.Error, panel.Severity);
        Assert.Equal(ContrastFailureKind.InsufficientContrast, panel.Kind);
        Assert.Equal(1, panel.Ratio!.Value, 6);
        Assert.Equal(ContrastGate.TextMinimum, panel.Required);
        Assert.All(preview.Contrast.Errors, issue => Assert.True(issue.Ratio < ContrastGate.TextMinimum));
        // A pair on a Semi mirror (here a mirror of a surface) is reported under its own key and resolved to its seed.
        Assert.Contains(preview.Contrast.Errors, issue => !ThemeTokens.Seeds.Contains(issue.Background)
            && ThemeCustomization.SeedsOf(ThemeTokenTheme.Dark, issue.Background).Any());
        Assert.NotEmpty(ThemeCustomization.ForRole(preview.Contrast, ThemeTokenTheme.Dark, ThemeUserColors.Text));
        Assert.NotEmpty(ThemeCustomization.ForRole(preview.Contrast, ThemeTokenTheme.Dark, ThemeUserColors.Surface));
        Assert.Empty(ThemeCustomization.ForRole(preview.Contrast, ThemeTokenTheme.Dark, ThemeUserColors.Favorite));
    }

    [Fact]
    public void AWeakBorderIsRefusedWhenItMakesTheSwitchKnobUnreadable()
    {
        ThemePreview preview = ThemeCustomization.Preview(Base(ThemeTokenTheme.Dark), Seeds(("Workspace.ControlBorder", "#30353C")));
        Assert.False(preview.Accepted);
        Assert.Equal(3, preview.Contrast.Errors.Count);
        Assert.Equal(new[]
        {
            "ToggleSwitchContainerUnCheckedDefaultBackground",
            "ToggleSwitchContainerUnCheckedPointeroverBackground",
            "ToggleSwitchContainerUnCheckedPressedBackground"
        }, preview.Contrast.Errors.Select(issue => issue.Background).ToArray());
        Assert.All(preview.Contrast.Errors, issue => Assert.True(issue is
        {
            Foreground: "ToggleSwitchIndicatorBackground",
            Required: 4.5,
            Kind: ContrastFailureKind.InsufficientContrast
        }));
        ThemeContrastIssue issue = Assert.Single(preview.Contrast.Warnings, issue => issue is { Foreground: "Workspace.ControlBorder", Background: "Workspace.Panel" });
        Assert.Equal(ThemeContrastSeverity.Warning, issue.Severity);
        Assert.Equal(ContrastGate.GraphicMinimum, issue.Required);
        Assert.True(issue.Ratio < ContrastGate.GraphicMinimum);
        Assert.All(preview.Contrast.Warnings, warning => Assert.True(warning.Required < ContrastGate.TextMinimum));
    }

    [Fact]
    public void TextAboveFourPointFiveButBelowAStricterReviewedMinimumWarns()
    {
        // Light pins text on the hover fill at 10.7 : 1; #3A3A3A gives about 7 : 1 there and passes every 4.5 pair.
        ThemePreview preview = ThemeCustomization.Preview(Base(ThemeTokenTheme.Light), Seeds(("Workspace.Text", "#3A3A3A")));
        Assert.True(preview.Accepted, string.Join(", ", preview.Contrast.Errors.Select(issue => issue.Foreground + "/" + issue.Background)));
        ThemeContrastIssue hover = Assert.Single(preview.Contrast.Warnings, issue => issue is { Foreground: "Workspace.Text", Background: "Workspace.Hover", Required: 10.7 });
        Assert.InRange(hover.Ratio!.Value, ContrastGate.TextMinimum, 10.7);
    }

    [Fact]
    public void ANonOpaqueTextColourIsAnErrorNotAPass()
    {
        ThemePreview preview = ThemeCustomization.Preview(Base(ThemeTokenTheme.Dark), Seeds(("Workspace.Text", "#80EEF1F4")));
        Assert.False(preview.Accepted);
        Assert.Contains(preview.Contrast.Errors, issue => issue is { Kind: ContrastFailureKind.InvalidColor, Key: "Workspace.Text", Ratio: null });
    }

    [Fact]
    public void ExpandRejectsNonSeedKeysLikeTheBuilder()
    {
        Assert.Throws<ArgumentException>(() => ThemeCustomization.Expand(Base(ThemeTokenTheme.Dark), Seeds(("ButtonSolidPrimaryBackground", "#123456"))));
        Assert.Throws<ArgumentException>(() => ThemeCustomization.Expand(Base(ThemeTokenTheme.Dark), Seeds(("Workspace.Accent.Nope", "#123456"))));
    }

    [Fact]
    public void StoredOverridesApplyLiveAreGatedAndUnchangedSetsAreSkipped()
    {
        var target = new ResourceDictionary();
        var host = new ThemeRuntimeHost(target, Base(ThemeTokenTheme.Dark), Base(ThemeTokenTheme.Light));
        host.Apply(ThemeTokenTheme.Dark, null);
        host.Apply(ThemeTokenTheme.Light, null);
        ResourceDictionary installed = host.Generated!;

        Assert.False(host.NeedsApply(ThemeColorOverrides.Empty));
        ThemeOverridesApplyResult nothing = host.ApplyOverrides(ThemeColorOverrides.Empty);
        Assert.False(nothing.Changed);
        Assert.Same(installed, host.Generated);

        ThemeColorOverrides green = ThemeColorOverrides.Empty.WithRole(ThemeTokenTheme.Dark, ThemeUserColors.Accent, C("#4CC38A"));
        Assert.True(host.NeedsApply(green));
        ThemeOverridesApplyResult applied = host.ApplyOverrides(green);
        Assert.Equal((ThemeApplyStatus.Applied, ThemeApplyStatus.Unchanged), (applied.Dark.Status, applied.Light.Status));
        Assert.True(applied.Accepted);
        Assert.Single(target.MergedDictionaries);
        Assert.Equal(C("#4CC38A"), Brush(target, "Workspace.Accent", ThemeVariant.Dark));
        Assert.Equal(applied.Dark.Preview!.Palette.Colors["Workspace.AccentHover"], Brush(target, "Workspace.AccentHover", ThemeVariant.Dark));
        Assert.NotEqual(Base(ThemeTokenTheme.Dark).Colors["Workspace.AccentHover"], Brush(target, "Workspace.AccentHover", ThemeVariant.Dark));
        Assert.Equal(Base(ThemeTokenTheme.Light).Colors["Workspace.Accent"], Brush(target, "Workspace.Accent", ThemeVariant.Light));
        ResourceDictionary afterGreen = host.Generated!;
        Assert.False(host.NeedsApply(green));
        Assert.False(host.ApplyOverrides(ThemeColorOverrides.Create(Raw(green.Light), Raw(green.Dark))).Changed);
        Assert.Same(afterGreen, host.Generated);

        // Refused: Dark keeps (falls back to) its built-in palette; the set is remembered so it is not retried on every save.
        ThemeColorOverrides unreadable = green.WithSeed(ThemeTokenTheme.Dark, "Workspace.Text", C("#1B1E22"));
        ThemeOverridesApplyResult refused = host.ApplyOverrides(unreadable);
        Assert.Equal(ThemeApplyStatus.Rejected, refused.Dark.Status);
        Assert.False(refused.Accepted);
        Assert.NotEmpty(refused.Dark.Preview!.Contrast.Errors);
        Assert.Equal(Base(ThemeTokenTheme.Dark).Colors["Workspace.Text"], Brush(target, "Workspace.Text", ThemeVariant.Dark));
        Assert.Equal(Base(ThemeTokenTheme.Dark).Colors["Workspace.Accent"], Brush(target, "Workspace.Accent", ThemeVariant.Dark));
        Assert.False(host.NeedsApply(unreadable));

        // Reset of the variant: built-in again.
        ThemeOverridesApplyResult reset = host.ApplyOverrides(unreadable.ResetTheme(ThemeTokenTheme.Dark));
        Assert.Equal(ThemeApplyStatus.Applied, reset.Dark.Status);
        Assert.Equal(Base(ThemeTokenTheme.Dark).Colors, host.Current(ThemeTokenTheme.Dark)!.Colors);

        // Raw TH1 seeds forget the bookkeeping: the stored set is looked at again.
        host.Apply(ThemeTokenTheme.Light, Seeds(("Workspace.Accent", "#123456")));
        Assert.True(host.NeedsApply(ThemeColorOverrides.Empty));
        Assert.Equal(ThemeApplyStatus.Applied, host.ApplyOverrides(ThemeColorOverrides.Empty).Light.Status);
        Assert.Equal(Base(ThemeTokenTheme.Light).Colors["Workspace.Accent"], Brush(target, "Workspace.Accent", ThemeVariant.Light));
    }

    private static string DragPreviewKey(ThemeTokenTheme theme) =>
        ThemeTokens.ForTheme(theme).Variations.First(pair => pair.Value == ThemeTokenVariation.AccentPreview).Key;

    private static IEnumerable<KeyValuePair<string, string?>> Raw(IReadOnlyDictionary<string, string> map) =>
        map.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value));

    private static ThemeUserColor Role(string id) => ThemeUserColors.Require(id);

    private static Color Brush(IResourceNode resources, string key, ThemeVariant variant) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(resources.TryGetResource(key, variant, out object? value) ? value : null).Color;
}

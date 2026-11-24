using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using MeowChan.Desktop.Theming;

namespace MeowChan.DesktopTests;

// TH1: the runtime palette builder against the XAML dictionaries (parsed, no Avalonia application) and the applier on a plain
// resource dictionary. Brushes are Avalonia objects, so the class runs outside the parallel test threads.
[CollectionDefinition("Theme runtime", DisableParallelization = true)]
public sealed class ThemeRuntimeCollection;

[Collection("Theme runtime")]
public sealed class ThemePaletteTests
{
    private static readonly XNamespace A = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly Color Green = Color.Parse("#2E7D32");

    [Theory, InlineData("Dark"), InlineData("Light")]
    public void WithoutOverridesEveryKeyOfTheXamlDictionaryIsReproducedExactly(string themeName)
    {
        ThemeTokenTheme theme = Enum.Parse<ThemeTokenTheme>(themeName);
        Dictionary<string, XElement> xaml = Xaml(theme);
        ThemePalette palette = ThemePalette.Build(Base(theme));

        Assert.Equal(xaml.Keys.Order(StringComparer.Ordinal), palette.Colors.Keys.Concat(palette.Shadows.Keys).Order(StringComparer.Ordinal));
        foreach (var (key, color) in palette.Colors)
            Assert.True(string.Equals(xaml[key].Value.Trim(), ThemePalette.Hex(color), StringComparison.OrdinalIgnoreCase),
                $"{theme}: {key} is {ThemePalette.Hex(color)}, the XAML says {xaml[key].Value.Trim()}.");
        foreach (var (key, shadows) in palette.Shadows)
            Assert.Equal(BoxShadows.Parse(xaml[key].Value.Trim()), shadows);
        Assert.Empty(palette.Overrides);
    }

    [Theory, InlineData("Dark"), InlineData("Light")]
    public void EveryMirrorIsItsSeedAndEveryVariationHasARule(string themeName)
    {
        ThemeTokenTheme theme = Enum.Parse<ThemeTokenTheme>(themeName);
        ThemeTokenMap map = ThemeTokens.ForTheme(theme);
        ThemePalette palette = ThemePalette.Build(Base(theme));
        foreach (var (mirror, seed) in map.Mirrors) Assert.Equal(palette.Colors[seed], palette.Colors[mirror]);
        Assert.Equal(map.Variations.Values.Distinct().Order(), ThemePalette.Rules(theme).Keys.Order());
        foreach (ThemeVariationRule rule in ThemePalette.Rules(theme).Values)
        {
            Assert.NotEmpty(rule.Sources);
            Assert.All(rule.Sources, source => Assert.Contains(source, (IReadOnlySet<string>)ThemeTokens.Seeds));
            Assert.False(string.IsNullOrWhiteSpace(rule.Description));
        }
    }

    // The rules that do not reproduce today's value: documented here and in ThemePalette, base value kept while the seeds are unchanged.
    [Theory]
    [InlineData("Dark", "#4C535B", "ButtonDefaultDisabledBorderBrush", "ButtonSolidDisabledBorderBrush", "CheckBoxCheckedDisabledBackground",
        "CheckBoxCheckedDisabledBorderBrush", "CheckBoxCardCheckedDisabledBorderBrush", "RadioButtonCheckIconDisabledBackground",
        "RadioButtonCheckIconDisabledBorderBrush", "SliderThumbDisabledBorderBrush", "ToggleSwitchContainerCheckedDisabledBackground")]
    [InlineData("Light", "#5B5346", "ScrollBarThumbPointeroverForeground")]
    public void OnlyTheDocumentedVariationsArePinnedExceptions(string themeName, string ruleValue, params string[] keys)
    {
        ThemeTokenTheme theme = Enum.Parse<ThemeTokenTheme>(themeName);
        ThemePaletteBase basePalette = Base(theme);
        ThemePalette palette = ThemePalette.Build(basePalette);
        Assert.Equal(keys.Order(StringComparer.Ordinal), palette.Pinned.Order(StringComparer.Ordinal));
        ThemeTokenMap map = ThemeTokens.ForTheme(theme);
        foreach (string key in keys)
        {
            ThemeVariationRule rule = ThemePalette.Rules(theme)[map.Variations[key]];
            Assert.Equal(ruleValue, ThemePalette.Hex(rule.Derive(seed => basePalette.Colors[seed])));
            Assert.Equal(basePalette.Colors[key], palette.Colors[key]);
        }
    }

    [Theory, InlineData("Dark"), InlineData("Light")]
    public void OverridingTheAccentChangesItsMirrorsAndVariationsAndNothingElse(string themeName)
    {
        ThemeTokenTheme theme = Enum.Parse<ThemeTokenTheme>(themeName);
        ThemeTokenMap map = ThemeTokens.ForTheme(theme);
        ThemePalette before = ThemePalette.Build(Base(theme));
        ThemePalette after = ThemePalette.Build(Base(theme), new Dictionary<string, Color> { ["Workspace.Accent"] = Green });

        var expected = new HashSet<string>(StringComparer.Ordinal) { "Workspace.Accent" };
        expected.UnionWith(map.Mirrors.Where(pair => pair.Value == "Workspace.Accent").Select(pair => pair.Key));
        expected.UnionWith(map.Variations.Where(pair => ThemePalette.Rules(theme)[pair.Value].Sources.Contains("Workspace.Accent")).Select(pair => pair.Key));
        string[] changed = after.Colors.Keys.Where(key => after.Colors[key] != before.Colors[key]).ToArray();
        Assert.Equal(expected.Order(StringComparer.Ordinal), changed.Order(StringComparer.Ordinal));
        Assert.Contains("ButtonSolidPrimaryBackground", changed);
        Assert.Contains("ProgressBarIndicatorBrush", changed);
        Assert.DoesNotContain("Workspace.Progress", changed); // an independent seed, even where its value equals the accent (Dark)

        Assert.Equal("#662E7D32", ThemePalette.Hex(after.Colors["GridSplitterPreviewBackground"]));
        Assert.Equal("#662E7D32", ThemePalette.Hex(after.Colors["DataGridDropLocationIndicatorBackground"]));
        // Primary buttons press one step (× 0.8) darker in both themes.
        string pressed = theme == ThemeTokenTheme.Dark ? "#2E7D32" : "#256428";
        Assert.Equal("#256428", ThemePalette.Hex(after.Colors["ButtonSolidPrimaryPressedBackground"]));
        Assert.Equal(pressed, ThemePalette.Hex(after.Colors["SliderThumbPressedBorderBrush"]));
        if (theme == ThemeTokenTheme.Light)
        {
            Assert.Equal(pressed, ThemePalette.Hex(after.Colors["CheckBoxPressedBorderBrush"]));
            Assert.Equal(pressed, ThemePalette.Hex(after.Colors["HyperlinkButtonPressedForeground"]));
        }
        else Assert.Equal(before.Colors["CheckBoxPressedBorderBrush"], after.Colors["CheckBoxPressedBorderBrush"]); // follows AccentHover
        Assert.Equal(new[] { "Workspace.Accent" }, after.Overrides.Keys);
    }

    [Fact]
    public void AnOverriddenSourceSeedReleasesAPinnedVariationToItsRule()
    {
        Color outline = Color.Parse("#8090A0");
        ThemePalette dark = ThemePalette.Build(Base(ThemeTokenTheme.Dark), new Dictionary<string, Color> { ["Workspace.ControlBorder"] = outline });
        Assert.Empty(dark.Pinned);
        // (0x80 + 0x1B) / 2 = 77.5 → 78, (0x90 + 0x1E) / 2 = 87, (0xA0 + 0x22) / 2 = 97 (half away from zero).
        Assert.Equal("#4E5761", ThemePalette.Hex(dark.Colors["ButtonDefaultDisabledBorderBrush"]));
        Assert.Equal(outline, dark.Colors["TextBoxDefaultBorderBrush"]);

        ThemePalette light = ThemePalette.Build(Base(ThemeTokenTheme.Light), new Dictionary<string, Color> { ["Workspace.ControlBorder"] = outline });
        Assert.Empty(light.Pinned);
        Assert.Equal("#667380", ThemePalette.Hex(light.Colors["ScrollBarThumbPointeroverForeground"]));
        Assert.Equal(outline, light.Colors["ScrollBarThumbForeground"]);
        // The pinned value never depended on Muted: overriding Muted alone keeps it.
        ThemePalette muted = ThemePalette.Build(Base(ThemeTokenTheme.Light), new Dictionary<string, Color> { ["Workspace.Muted"] = outline });
        Assert.Equal(Base(ThemeTokenTheme.Light).Colors["ScrollBarThumbPointeroverForeground"], muted.Colors["ScrollBarThumbPointeroverForeground"]);
    }

    [Theory, InlineData("Dark"), InlineData("Light")]
    public void AnOverrideEqualToTheBaseIsNoChange(string themeName)
    {
        ThemeTokenTheme theme = Enum.Parse<ThemeTokenTheme>(themeName);
        ThemePaletteBase basePalette = Base(theme);
        var same = ThemeTokens.Seeds.ToDictionary(seed => seed, seed => basePalette.Colors[seed], StringComparer.Ordinal);
        ThemePalette palette = ThemePalette.Build(basePalette, same);
        Assert.Empty(palette.Overrides);
        Assert.Equal(ThemePalette.Build(basePalette).Colors, palette.Colors);
    }

    [Theory]
    [InlineData("ButtonSolidPrimaryBackground")] // mirror
    [InlineData("ButtonSolidPrimaryPressedBackground")] // variation
    [InlineData("Workspace.Shadow.Overlay")] // shadow
    [InlineData("Workspace.NotASeed")]
    [InlineData("workspace.accent")]
    public void OnlyColourSeedsCanBeOverridden(string key) =>
        Assert.Throws<ArgumentException>(() => ThemePalette.Build(Base(ThemeTokenTheme.Dark), new Dictionary<string, Color> { [key] = Green }));

    [Fact]
    public void ABaseWithAMissingOrUnknownKeyIsRejected()
    {
        ThemePaletteBase dark = Base(ThemeTokenTheme.Dark);
        var colors = dark.Colors.ToDictionary(StringComparer.Ordinal);
        colors.Remove("Workspace.Accent");
        Assert.Throws<ArgumentException>(() => new ThemePaletteBase(ThemeTokenTheme.Dark, colors, dark.Shadows));
        // A Light-only override in a Dark base is not part of the Dark inventory.
        colors = dark.Colors.ToDictionary(StringComparer.Ordinal);
        colors["ScrollBarThumbForeground"] = Green;
        Assert.Throws<ArgumentException>(() => new ThemePaletteBase(ThemeTokenTheme.Dark, colors, dark.Shadows));
        var shadows = dark.Shadows.ToDictionary(StringComparer.Ordinal);
        shadows.Remove("FlyoutBorderBoxShadow");
        Assert.Throws<ArgumentException>(() => new ThemePaletteBase(ThemeTokenTheme.Dark, dark.Colors, shadows));
    }

    [Theory, InlineData("Dark"), InlineData("Light")]
    public void TheGeneratedPalettePassesTheProductContrastRulesAndAWeakOverrideFails(string themeName)
    {
        ThemeTokenTheme theme = Enum.Parse<ThemeTokenTheme>(themeName);
        ContrastPalette generated = ThemePalette.Build(Base(theme)).ToContrastPalette();
        Assert.Equal(theme == ThemeTokenTheme.Dark ? ContrastTheme.Dark : ContrastTheme.Light, generated.Theme);
        Assert.Empty(ContrastRules.Check(generated));

        // Text in the panel's own colour cannot pass: the checker sees the generated values, not the XAML.
        ThemePaletteBase basePalette = Base(theme);
        ThemePalette weak = ThemePalette.Build(basePalette, new Dictionary<string, Color> { ["Workspace.Text"] = basePalette.Colors["Workspace.Panel"] });
        IReadOnlyList<ContrastFailure> failures = ContrastRules.Check(weak.ToContrastPalette());
        Assert.Contains(failures, failure => failure.Kind == ContrastFailureKind.InsufficientContrast
            && failure.Rule.Foreground == "Workspace.Text" && failure.Rule.Background == "Workspace.Panel");
        // Text on a mirrored surface (the selected tab) fails as well.
        Assert.Contains(failures, failure => failure.Rule.Foreground == "Workspace.Text" && failure.Rule.Background == "TabItemLineHeaderSelectedBackground");
    }

    [Fact]
    public void HexIsRgbWhenOpaqueAndArgbOtherwise()
    {
        Assert.Equal("#2E7D32", ThemePalette.Hex(Green));
        Assert.Equal("#662E7D32", ThemePalette.Hex(ThemePalette.WithAlpha(Green, ThemePalette.PreviewAlpha)));
        Assert.Equal(102, ThemePalette.PreviewAlpha); // 40 %
        Assert.Equal("#66000000", ThemePalette.Hex(Base(ThemeTokenTheme.Dark).Colors["Workspace.Scrim"]));
    }

    [Fact]
    public void TheApplierWritesBothThemeDictionariesIntoOneReplacedMergedDictionary()
    {
        var target = new ResourceDictionary();
        var host = new ThemeRuntimeHost(target, Base(ThemeTokenTheme.Dark), Base(ThemeTokenTheme.Light));
        host.Apply(ThemeTokenTheme.Dark, null);
        host.Apply(ThemeTokenTheme.Light, null);
        ResourceDictionary first = Assert.IsType<ResourceDictionary>(Assert.Single(target.MergedDictionaries));
        Assert.Same(host.Generated, first);
        Assert.Equal(Base(ThemeTokenTheme.Dark).Colors["Workspace.Accent"], Brush(target, "Workspace.Accent", ThemeVariant.Dark));
        Assert.Equal(Base(ThemeTokenTheme.Light).Colors["Workspace.Accent"], Brush(target, "Workspace.Accent", ThemeVariant.Light));
        // Light-only overrides stay Light-only: Dark keeps Semi's own value for them.
        Assert.True(target.TryGetResource("ScrollBarThumbForeground", ThemeVariant.Light, out _));
        Assert.False(((ResourceDictionary)first.ThemeDictionaries[ThemeVariant.Dark]).ContainsKey("ScrollBarThumbForeground"));
        Assert.True(target.TryGetResource("NotificationCardBoxShadows", ThemeVariant.Dark, out object? shadow) && shadow is BoxShadows);

        ThemePalette applied = host.Apply(ThemeTokenTheme.Dark, new Dictionary<string, Color> { ["Workspace.Accent"] = Green });
        Assert.NotSame(first, Assert.Single(target.MergedDictionaries));
        Assert.Same(applied, host.Current(ThemeTokenTheme.Dark));
        Assert.Equal(Green, Brush(target, "Workspace.Accent", ThemeVariant.Dark));
        Assert.Equal(Green, Brush(target, "ButtonSolidPrimaryBackground", ThemeVariant.Dark));
        Assert.Equal(Base(ThemeTokenTheme.Light).Colors["Workspace.Accent"], Brush(target, "Workspace.Accent", ThemeVariant.Light));
        // A custom variant built on Dark resolves the Dark palette, as ThemeLogo treats it as dark.
        Assert.Equal(Green, Brush(target, "Workspace.Accent", new ThemeVariant("th1-test", ThemeVariant.Dark)));

        host.Apply(ThemeTokenTheme.Dark, new Dictionary<string, Color>());
        Assert.Equal(Base(ThemeTokenTheme.Dark).Colors["Workspace.Accent"], Brush(target, "Workspace.Accent", ThemeVariant.Dark));
    }

    [Fact]
    public void TheBaseIsReadFromLoadedResourcesAndANonSolidResourceIsRejected()
    {
        var styles = new ResourceDictionary();
        foreach (ThemeTokenTheme theme in new[] { ThemeTokenTheme.Dark, ThemeTokenTheme.Light })
        {
            var dictionary = new ResourceDictionary();
            ThemePaletteBase basePalette = Base(theme);
            foreach (var (key, color) in basePalette.Colors) dictionary[key] = new SolidColorBrush(color);
            foreach (var (key, shadows) in basePalette.Shadows) dictionary[key] = shadows;
            styles.ThemeDictionaries[ThemeRuntime.Variant(theme)] = dictionary;
        }
        foreach (ThemeTokenTheme theme in new[] { ThemeTokenTheme.Dark, ThemeTokenTheme.Light })
        {
            ThemePaletteBase read = ThemeRuntime.ReadBase(styles, theme);
            Assert.Equal(Base(theme).Colors, read.Colors);
            Assert.Equal(Base(theme).Shadows, read.Shadows);
        }
        ((ResourceDictionary)styles.ThemeDictionaries[ThemeVariant.Dark])["Workspace.Accent"] = new SolidColorBrush(Green, 0.5);
        Assert.Throws<InvalidOperationException>(() => ThemeRuntime.ReadBase(styles, ThemeTokenTheme.Dark));
    }

    [Fact]
    public void VariantsMapToTheirBuiltInBase()
    {
        Assert.Equal(ThemeTokenTheme.Dark, ThemeRuntime.Base(ThemeVariant.Dark));
        Assert.Equal(ThemeTokenTheme.Light, ThemeRuntime.Base(ThemeVariant.Light));
        Assert.Equal(ThemeTokenTheme.Light, ThemeRuntime.Base(new ThemeVariant("mila-day", ThemeVariant.Light)));
        Assert.Throws<ArgumentException>(() => ThemeRuntime.Base(ThemeVariant.Default));
    }

    [Fact]
    public void StartupInstallsTheRuntimePaletteRightAfterTheXamlIsLoaded()
    {
        string app = File.ReadAllText(Root("App.axaml.cs"));
        int load = app.IndexOf("AvaloniaXamlLoader.Load(this)", StringComparison.Ordinal);
        int install = app.IndexOf("ThemeRuntime.Install(this)", StringComparison.Ordinal);
        Assert.True(load >= 0 && install > load, "ThemeRuntime.Install must run after the XAML (and its theme dictionaries) is loaded.");
        Assert.True(install < app.IndexOf("ThemeLogo.FollowAllWindows()", StringComparison.Ordinal));
    }

    private static Color Brush(IResourceNode resources, string key, ThemeVariant variant) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(resources.TryGetResource(key, variant, out object? value) ? value : null).Color;

    internal static ThemePaletteBase Base(ThemeTokenTheme theme)
    {
        ThemeTokenMap map = ThemeTokens.ForTheme(theme);
        var colors = new Dictionary<string, Color>(StringComparer.Ordinal);
        var shadows = new Dictionary<string, BoxShadows>(StringComparer.Ordinal);
        foreach (var (key, element) in Xaml(theme))
        {
            if (map.Classify(key) == ThemeTokenClassification.Shadow) shadows[key] = BoxShadows.Parse(element.Value.Trim());
            else colors[key] = Color.Parse(element.Value.Trim());
        }
        return new ThemePaletteBase(theme, colors, shadows);
    }

    private static Dictionary<string, XElement> Xaml(ThemeTokenTheme theme)
    {
        XElement dictionary = Assert.Single(XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Descendants(A + "ResourceDictionary"),
            element => (string?)element.Attribute(X + "Key") == theme.ToString());
        return dictionary.Elements().ToDictionary(element => (string)element.Attribute(X + "Key")!, StringComparer.Ordinal);
    }

    internal static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}

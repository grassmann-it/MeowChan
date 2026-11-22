using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MeowChan.Desktop.Theming;

namespace MeowChan.DesktopTests;

// Static XML/data contracts only. No Avalonia app, brush construction, decoder or profile access.
public sealed class ThemeTokenMapTests
{
    private static readonly XNamespace A = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory, InlineData("Dark"), InlineData("Light")]
    public void EveryExplicitResourceHasExactlyOneClassification(string themeName)
    {
        ThemeTokenTheme theme = Enum.Parse<ThemeTokenTheme>(themeName);
        ThemeTokenMap map = ThemeTokens.ForTheme(theme);
        Dictionary<string, XElement> resources = Resources(theme);
        string[] classified = ThemeTokens.Seeds.Concat(map.Mirrors.Keys).Concat(map.Variations.Keys)
            .Append(ThemeTokens.ShadowSeed).Concat(ThemeTokens.ShadowMirrors.Keys).ToArray();

        // Exact inventory in BOTH directions: unknown/new keys, deleted keys, stale mappings,
        // overlapping classes and Light-only keys accidentally applied to Dark all fail.
        Assert.Equal(classified.Length, classified.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(resources.Keys.Order(StringComparer.Ordinal), classified.Order(StringComparer.Ordinal));
        foreach (var (key, resource) in resources)
        {
            ThemeTokenClassification kind = map.Classify(key);
            Assert.NotEqual(ThemeTokenClassification.Unknown, kind);
            Assert.Equal(A + (kind == ThemeTokenClassification.Shadow ? "BoxShadows" : "SolidColorBrush"), resource.Name);
            if (kind != ThemeTokenClassification.Shadow)
                Assert.Matches(@"^#(?:[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$", resource.Value.Trim());
        }
    }

    [Theory, InlineData("Dark"), InlineData("Light")]
    public void EveryMirrorHasItsSemanticSeedColourInItsOwnTheme(string themeName)
    {
        ThemeTokenTheme theme = Enum.Parse<ThemeTokenTheme>(themeName);
        ThemeTokenMap map = ThemeTokens.ForTheme(theme);
        Dictionary<string, XElement> resources = Resources(theme);
        foreach (var (mirror, seed) in map.Mirrors)
        {
            Assert.Contains(seed, (IReadOnlySet<string>)ThemeTokens.Seeds);
            Assert.Equal(ThemeTokenClassification.Mirror, map.Classify(mirror));
            Assert.True(Value(resources, seed) == Value(resources, mirror),
                $"{theme}: {mirror} ({Value(resources, mirror)}) must mirror {seed} ({Value(resources, seed)}).");
        }
    }

    [Theory, InlineData("Dark"), InlineData("Light")]
    public void WorkspaceColourRolesStayIndependentEvenWhenTheirValuesCoincide(string themeName)
    {
        ThemeTokenTheme theme = Enum.Parse<ThemeTokenTheme>(themeName);
        Dictionary<string, XElement> resources = Resources(theme);
        var workspaceColours = resources.Where(pair => pair.Value.Name == A + "SolidColorBrush" &&
            pair.Key.StartsWith("Workspace.", StringComparison.Ordinal)).Select(pair => pair.Key);
        var expectedColours = ThemeTokens.Seeds.Append("Workspace.ToolbarToggleSurface");
        Assert.Equal(expectedColours.Order(StringComparer.Ordinal), workspaceColours.Order(StringComparer.Ordinal));
        Assert.Equal(ThemeTokenClassification.Variation, ThemeTokens.ForTheme(theme).Classify("Workspace.ToolbarToggleSurface"));
        foreach (string seed in ThemeTokens.Seeds)
            Assert.Equal(ThemeTokenClassification.Seed, ThemeTokens.ForTheme(theme).Classify(seed));

        // Equal-colour seeds are not interchangeable semantic identities.
        Assert.Contains("Workspace.Panel", (IReadOnlySet<string>)ThemeTokens.Seeds);
        Assert.Contains("Workspace.Overlay", (IReadOnlySet<string>)ThemeTokens.Seeds);
        Assert.Contains("Workspace.Accent", (IReadOnlySet<string>)ThemeTokens.Seeds);
        Assert.Contains("Workspace.Progress", (IReadOnlySet<string>)ThemeTokens.Seeds);
        Assert.Contains("Workspace.AccentHoverText", (IReadOnlySet<string>)ThemeTokens.Seeds);
        Assert.Contains("Workspace.AccentHover", (IReadOnlySet<string>)ThemeTokens.Seeds);
    }

    [Theory, InlineData("Dark"), InlineData("Light")]
    public void AmbiguousEqualColoursUseTheDocumentedRoleNotAnArbitrarySeed(string themeName)
    {
        ThemeTokenTheme theme = Enum.Parse<ThemeTokenTheme>(themeName);
        var mirrors = ThemeTokens.ForTheme(theme).Mirrors;
        foreach (string key in new[] { "MenuFlyoutBackground", "ComboBoxPopupBackground", "AutoCompleteBoxPopupBackground",
                     "PopConfirmBackground", "NotificationCardBackground", "FlyoutBackground", "ToolTipBackground" })
            Assert.Equal("Workspace.Overlay", mirrors[key]);
        Assert.Equal("Workspace.ChipSurface", mirrors["ClosableTagBackground"]);
        Assert.Equal("Workspace.ChipBorder", mirrors["ClosableTagBorder"]);
        Assert.Equal("Workspace.AccentSurface", mirrors["DataGridRowSelectedBackground"]);
        Assert.Equal("Workspace.ControlBorder", mirrors["TextBoxDefaultBorderBrush"]);
        Assert.Equal("Workspace.OnAccent", mirrors["ButtonSolidForeground"]);
        // These Semi fills are Accent, not the separately styled Workspace progress colour.
        Assert.Equal("Workspace.Accent", mirrors["ProgressBarIndicatorBrush"]);
        Assert.Equal("Workspace.Accent", mirrors["ProgressBarPrimaryForeground"]);
        if (theme == ThemeTokenTheme.Light)
        {
            Assert.Equal("Workspace.AccentHoverText", mirrors["HyperlinkButtonPointeroverForeground"]);
            Assert.Equal("Workspace.ProgressTrack", mirrors["SliderTrackBackground"]);
        }
    }

    [Theory, InlineData("Dark"), InlineData("Light")]
    public void NonMirrorStatesAreAnExactReviewedInventoryWithPinnedValues(string themeName)
    {
        ThemeTokenTheme theme = Enum.Parse<ThemeTokenTheme>(themeName);
        var expected = new Dictionary<string, (ThemeTokenVariation Kind, string Dark, string Light)>(StringComparer.Ordinal);
        Add(ThemeTokenVariation.DisabledOutline, "#4A525C", "#948A7D",
            "ButtonDefaultDisabledBorderBrush", "ButtonSolidDisabledBorderBrush",
            "CheckBoxCheckedDisabledBackground", "CheckBoxCheckedDisabledBorderBrush", "CheckBoxCardCheckedDisabledBorderBrush",
            "RadioButtonCheckIconDisabledBackground", "RadioButtonCheckIconDisabledBorderBrush", "SliderThumbDisabledBorderBrush",
            "ToggleSwitchContainerCheckedDisabledBackground");
        Add(ThemeTokenVariation.PressedAccent, "#6AA7FF", "#662F05",
            "ButtonSolidPrimaryPressedBackground", "ButtonSolidPrimaryPressedBorderBrush",
            "CheckBoxCheckedPressedBackground", "CheckBoxCheckedPressedBorderBrush",
            "RadioButtonCheckIconPressedBackground", "RadioButtonCheckIconPressedBorderBrush", "SliderThumbPressedBorderBrush",
            "ToggleSwitchContainerCheckedPressedBackground");
        Add(ThemeTokenVariation.PressedAccentBorder, "#8ABBFF", "#662F05",
            "CheckBoxPressedBorderBrush", "CheckBoxCardCheckedPressedBorderBrush",
            "RadioButtonUncheckIconPressedBorderBrush", "RadioButtonCardCheckPressedBorderBrush");
        Add(ThemeTokenVariation.PressedAccentText, "#8ABBFF", "#662F05", "BreadcrumbItemPressedForeground");
        Add(ThemeTokenVariation.ToolbarToggleSurface, "#25344A", "#7D7367", "Workspace.ToolbarToggleSurface");
        Add(ThemeTokenVariation.AccentPreview, "#666AA7FF", "#66803B06",
            "GridSplitterPreviewBackground", "DataGridDropLocationIndicatorBackground");
        if (theme == ThemeTokenTheme.Light)
        {
            Add(ThemeTokenVariation.PressedAccentText, "", "#662F05", "HyperlinkButtonPressedForeground");
            Add(ThemeTokenVariation.ScrollBarHover, "", "#575148", "ScrollBarThumbPointeroverForeground");
        }

        ThemeTokenMap map = ThemeTokens.ForTheme(theme);
        Dictionary<string, XElement> resources = Resources(theme);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), map.Variations.Keys.Order(StringComparer.Ordinal));
        foreach (var (key, state) in expected)
        {
            Assert.Equal(ThemeTokenClassification.Variation, map.Classify(key));
            Assert.Equal(state.Kind, map.Variations[key]);
            Assert.Equal(theme == ThemeTokenTheme.Dark ? state.Dark : state.Light, Value(resources, key));
            Assert.False(map.Mirrors.ContainsKey(key), $"{theme}: {key} is not an exact semantic seed alias.");
        }

        void Add(ThemeTokenVariation kind, string dark, string light, params string[] keys)
        {
            foreach (string key in keys) expected.Add(key, (kind, dark, light));
        }
    }

    [Theory, InlineData("Dark"), InlineData("Light")]
    public void DragPreviewsAreTranslucentAccentNotOpaqueMirrors(string themeName)
    {
        ThemeTokenTheme theme = Enum.Parse<ThemeTokenTheme>(themeName);
        Dictionary<string, XElement> resources = Resources(theme);
        foreach (string key in new[] { "GridSplitterPreviewBackground", "DataGridDropLocationIndicatorBackground" })
        {
            Assert.Equal(ThemeTokenVariation.AccentPreview, ThemeTokens.ForTheme(theme).Variations[key]);
            Assert.Equal("#66" + Value(resources, "Workspace.Accent")[1..], Value(resources, key));
        }
    }

    [Fact]
    public void LightOnlyOverridesAreNotInventedForDark()
    {
        string[] lightOnly =
        {
            "ScrollBarThumbForeground", "ScrollBarThumbPointeroverForeground", "TextBoxPlaceholderForeground",
            "ComboBoxSelectorPlaceHolderForeground", "CheckBoxDisabledForeground", "RadioButtonDisabledForeground",
            "ExpanderHeaderDisabledForeground", "TextBoxDisabledForeground", "ExpanderIconForeground",
            "ExpanderContentForeground", "ExpanderHeaderForeground", "ExpanderSeparatorBorderBrush",
            "MenuItemSeparatorBackground", "CommandBarSeparatorBackground", "DateTimePickerSeparatorBackground",
            "SliderTrackBackground", "SliderTickForeground", "HyperlinkButtonForeground",
            "HyperlinkButtonPointeroverForeground", "HyperlinkButtonPressedForeground"
        };
        Assert.Equal(lightOnly.Order(StringComparer.Ordinal),
            Resources(ThemeTokenTheme.Light).Keys.Except(Resources(ThemeTokenTheme.Dark).Keys).Order(StringComparer.Ordinal));
        foreach (string key in lightOnly)
        {
            Assert.Equal(ThemeTokenClassification.Unknown, ThemeTokens.ForTheme(ThemeTokenTheme.Dark).Classify(key));
            Assert.NotEqual(ThemeTokenClassification.Unknown, ThemeTokens.ForTheme(ThemeTokenTheme.Light).Classify(key));
        }
    }

    [Theory, InlineData("Dark"), InlineData("Light")]
    public void ShadowsAreExplicitCompositeResourcesNotColourSeeds(string themeName)
    {
        ThemeTokenTheme theme = Enum.Parse<ThemeTokenTheme>(themeName);
        Dictionary<string, XElement> resources = Resources(theme);
        Assert.Equal("Workspace.Shadow.Overlay", ThemeTokens.ShadowSeed);
        Assert.Equal(new[] { "AutoCompleteBoxPopupBoxShadow", "ComboBoxPopupBoxShadow", "FlyoutBorderBoxShadow",
                "MenuFlyoutBorderBoxShadow", "NotificationCardBoxShadows" },
            ThemeTokens.ShadowMirrors.Keys.Order(StringComparer.Ordinal));
        Assert.False(ThemeTokens.Seeds.Contains(ThemeTokens.ShadowSeed));
        Assert.Equal(theme == ThemeTokenTheme.Dark ? "0 4 16 0 #66000000" : "0 4 16 0 #26000000",
            Value(resources, ThemeTokens.ShadowSeed));
        foreach (var (mirror, seed) in ThemeTokens.ShadowMirrors)
        {
            Assert.Equal(ThemeTokens.ShadowSeed, seed);
            Assert.Equal(Value(resources, seed), Value(resources, mirror));
        }
    }

    [Theory, InlineData("Dark"), InlineData("Light")]
    public void UnknownAndWrongCaseKeysCannotSilentlyBecomeSeedsOrMirrors(string themeName)
    {
        ThemeTokenTheme theme = Enum.Parse<ThemeTokenTheme>(themeName);
        ThemeTokenMap map = ThemeTokens.ForTheme(theme);
        foreach (string key in new[] { "", "Workspace.NotASeed", "ButtonNotAState", "workspace.Accent", "Workspace.accent",
                     "buttonsolidforeground", "Workspace.Shadow.NotAShadow", "MenuFlyoutNotAShadow" })
        {
            Assert.Equal(ThemeTokenClassification.Unknown, map.Classify(key));
            Assert.False(map.Mirrors.ContainsKey(key));
            Assert.False(map.Variations.ContainsKey(key));
        }
    }

    [Fact]
    public void UnknownThemeIsNotSilentlyTreatedAsLight() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ThemeTokens.ForTheme((ThemeTokenTheme)42));

    private static string Value(Dictionary<string, XElement> resources, string key) => resources[key].Value.Trim();

    private static Dictionary<string, XElement> Resources(ThemeTokenTheme theme, [CallerFilePath] string sourcePath = "")
    {
        string path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../../Desktop/WorkspaceStyles.axaml"));
        XElement dictionary = Assert.Single(XDocument.Load(path).Descendants(A + "ResourceDictionary"),
            element => (string?)element.Attribute(X + "Key") == theme.ToString());
        // Do not select only known keys or only hex-valued nodes: every direct entry must be classified.
        return dictionary.Elements().ToDictionary(element => (string)element.Attribute(X + "Key")!, StringComparer.Ordinal);
    }
}

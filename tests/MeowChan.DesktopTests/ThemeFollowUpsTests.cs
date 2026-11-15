using Avalonia.Media;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Theming;

namespace MeowChan.DesktopTests;

// Review wave 6, AP5: the header separator follows a palette apply (N1), the colour dialog opens inside a catch (N8), the raw palette apply is a test
// seam only and overrides are opaque (N9). Source pins stand in for what needs an application; the rest runs on plain values.
[Collection("Theme runtime")]
public sealed class ThemeFollowUpsTests
{
    private static string Read(string relative) => File.ReadAllText(ThemePaletteTests.Root(relative));
    private static Color C(string hex) => ThemeCustomization.ParseHex(hex);

    [Fact]
    public void TheHeaderSeparatorLooksUpItsBrushAgainAfterEveryPaletteApply()
    {
        string bar = Read("Desktop/Controls/HeaderBar.cs");
        Assert.Contains("MeowChan.Desktop.Theming.ThemeRuntime.FollowApplied(this, ResetSeparatorBrush);", bar, StringComparison.Ordinal);
        Assert.Contains("private void OnThemeChanged(object? sender, EventArgs e) => ResetSeparatorBrush();", bar, StringComparison.Ordinal);
        int reset = bar.IndexOf("private void ResetSeparatorBrush()", StringComparison.Ordinal);
        Assert.True(reset > 0);
        string body = bar[reset..bar.IndexOf("public override void Render", StringComparison.Ordinal)];
        Assert.Contains("_separatorBrush = null;", body, StringComparison.Ordinal);
        Assert.Contains("InvalidateVisual();", body, StringComparison.Ordinal);
    }

    [Fact]
    public void OpeningTheColourDialogNeverLetsAnExceptionEscapeTheAsyncVoidHandler()
    {
        string source = Read("Desktop/Views/Settings/SettingsEditor.ThemeColors.cs");
        int handler = source.IndexOf("private async void OnThemeColorsClick", StringComparison.Ordinal);
        int worker = source.IndexOf("private async Task OpenThemeColorsAsync()", StringComparison.Ordinal);
        Assert.True(handler > 0 && worker > handler);
        string body = source[handler..worker];
        Assert.Contains("try { await OpenThemeColorsAsync(); }", body, StringComparison.Ordinal);
        Assert.Contains("catch (Exception error)", body, StringComparison.Ordinal);
        Assert.Contains("Log.Warning(error,", body, StringComparison.Ordinal);
        Assert.Contains("SetErrorMessage(\"settings.themeColors.openFailed\")", body, StringComparison.Ordinal);
        // Building the model and the dialog and showing it all happen inside the guarded part.
        string guarded = source[worker..];
        Assert.Contains("new(_config.ThemeOverrides", guarded, StringComparison.Ordinal);
        Assert.Contains("await dialog.ShowDialog(owner)", guarded, StringComparison.Ordinal);
    }

    [Fact]
    public void TheColourDialogFailureTextExistsInEveryLanguage()
    {
        Texts.Initialize();
        try
        {
            string[] languages = new[] { "en", "de" }.Concat(Texts.BundledPackCodes()).ToArray();
            Assert.Equal(12, languages.Length);
            HashSet<string> seen = [];
            foreach (string code in languages)
            {
                Texts.SetLanguage(code);
                string text = Texts.T("settings.themeColors.openFailed");
                Assert.True(text != "settings.themeColors.openFailed" && text.Length > 0, code);
                seen.Add(text);
            }
            Assert.Equal(12, seen.Count); // a real translation in each pack, not the English text copied
        }
        finally { Texts.Initialize(); Texts.SetLanguage("en"); }
    }

    [Fact]
    public void TheRawPaletteApplyIsATestSeamThatNoProductionCodeCalls()
    {
        string desktop = ThemePaletteTests.Root("Desktop");
        foreach (string file in Directory.EnumerateFiles(desktop, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || file.EndsWith("ThemeRuntime.cs", StringComparison.Ordinal)) continue;
            string text = File.ReadAllText(file);
            Assert.DoesNotContain("ThemeRuntime.ApplyForTests", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ThemeRuntime.Apply(", text, StringComparison.Ordinal);
        }
        string runtime = Read("Desktop/Theming/ThemeRuntime.cs");
        Assert.Contains("internal static ThemePalette? ApplyForTests(", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("internal static ThemePalette? Apply(", runtime, StringComparison.Ordinal);
        Assert.Equal(1, runtime.Split("ApplyForTests(ThemeVariant").Length - 1);
    }

    [Theory, InlineData("#80102030"), InlineData("#00FFFFFF"), InlineData("#FE102030")]
    public void AStoredOrImportedTranslucentColourIsDroppedLikeAMalformedOne(string translucent)
    {
        var dropped = new List<string>();
        ThemeColorOverrides overrides = ThemeColorOverrides.Create(
            [new("Workspace.Border", translucent), new("Workspace.Accent", "#80A0C0"), new("Workspace.Panel", "#FF232A3A")], null, dropped);
        Assert.Equal(new Dictionary<string, string> { ["Workspace.Accent"] = "#80A0C0", ["Workspace.Panel"] = "#232A3A" }, overrides.Light);
        Assert.Equal(["Light: invalid colour for Workspace.Border"], dropped);
        Assert.DoesNotContain(dropped, entry => entry.Contains(translucent, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ASetterRefusesATranslucentColourAndTheOpaqueCheckSitsBesideTheGeneralOne()
    {
        Assert.Throws<ArgumentException>(() => ThemeColorOverrides.Empty.WithSeed(ThemeTokenTheme.Light, "Workspace.Border", C("#80102030")));
        Assert.Throws<ArgumentException>(() => ThemeColorOverrides.Empty.WithRole(ThemeTokenTheme.Dark, ThemeUserColors.Accent, Color.FromArgb(1, 2, 3, 4)));
        Assert.Equal("#102030", ThemeColorOverrides.Empty.WithSeed(ThemeTokenTheme.Light, "Workspace.Border", C("#102030")).Light["Workspace.Border"]);
        Assert.True(ThemeColorOverrides.TryNormalizeOpaqueHex("#ff102030", out string hex) && hex == "#102030");
        Assert.False(ThemeColorOverrides.TryNormalizeOpaqueHex("#80102030", out _));
        Assert.True(ThemeColorOverrides.TryNormalizeHex("#80102030", out _)); // the general parser still reads the shape
    }

    [Theory, InlineData("Dark"), InlineData("Light")]
    public void TheGateRefusesATranslucentOverrideOfAnySeedNotOnlyOfThoseInAContrastPair(string themeName)
    {
        ThemeTokenTheme theme = Enum.Parse<ThemeTokenTheme>(themeName);
        ThemePaletteBase basePalette = ThemePaletteTests.Base(theme);
        foreach (string seed in ThemeTokens.Seeds)
        {
            Color builtIn = basePalette.Colors[seed];
            Color translucent = Color.FromArgb(0x80, 0x12, 0x34, 0x56);
            if (translucent == builtIn) continue;
            ThemePreview preview = ThemeCustomization.Preview(basePalette, new Dictionary<string, Color>(StringComparer.Ordinal) { [seed] = translucent });
            Assert.False(preview.Accepted, seed);
            Assert.Contains(preview.Contrast.Errors, issue => issue.Kind == ContrastFailureKind.InvalidColor && issue.Key == seed);
        }
        // An explicit colour that only repeats the built-in one (even a translucent built-in seed) is no override and passes.
        Assert.True(ThemeCustomization.Preview(basePalette, ThemeTokens.Seeds.ToDictionary(seed => seed, seed => basePalette.Colors[seed], StringComparer.Ordinal)).Accepted);
    }
}

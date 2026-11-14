using System.Runtime.CompilerServices;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Theming;

namespace MeowChan.DesktopTests;

/// <summary>TH1: the header theme button flips what is shown, and its context menu's "Follow system" keeps or restores System.</summary>
[Collection("Localization")]
public sealed class HeaderThemeMenuTests : IDisposable
{
    public HeaderThemeMenuTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    [Theory]
    [InlineData("System", true, "Light")]
    [InlineData("System", false, "Dark")]
    [InlineData("Dark", true, "Light")]
    [InlineData("Light", false, "Dark")]
    [InlineData("nonsense", true, "Light")]
    public void AClickStoresTheOppositeOfWhatIsShown(string preference, bool effectiveDark, string expected) =>
        Assert.Equal(expected, ThemePreference.Toggled(preference, effectiveDark));

    [Theory]
    [InlineData("Light", false, "System")]
    [InlineData("Dark", true, "System")]
    [InlineData("Dark", false, "System")]
    [InlineData("System", true, "Dark")]
    [InlineData("System", false, "Light")]
    [InlineData(" system ", false, "Light")]
    public void FollowSystemRestoresSystemOrKeepsTheShownVariant(string preference, bool effectiveDark, string expected) =>
        Assert.Equal(expected, ThemePreference.FollowSystemToggled(preference, effectiveDark));

    [Theory]
    [InlineData("Light", "Light"), InlineData("SYSTEM", "System"), InlineData("Dark", "Dark"), InlineData(null, "Dark"), InlineData("", "Dark")]
    public void PreferencesReadLikeTheConfig(string? stored, string expected)
    {
        Assert.Equal(expected, ThemePreference.Normalize(stored));
        Assert.Equal(expected, new Config { Theme = stored! }.Theme);
    }

    [Theory]
    [InlineData("System", true, true, "Dark")]
    [InlineData("System", false, true, "Light")]
    [InlineData("Light", false, false, "System")]
    [InlineData("Dark", true, false, "System")]
    public void TheMenuIsOneFollowSystemSwitchCheckedWhileSystemIsStored(string preference, bool effectiveDark, bool isChecked, string chosen)
    {
        string? choice = null;
        ContextMenuModel model = HeaderThemeMenu.Compose(preference, effectiveDark, value => choice = value).ToModel();
        ContextMenuEntry entry = Assert.Single(model.Entries);
        Assert.Equal(ContextMenuEntryKind.Check, entry.Kind);
        Assert.Equal(HeaderThemeMenu.FollowSystemId, entry.Id);
        Assert.Equal("Follow system", entry.Label);
        Assert.Equal(isChecked, entry.Checked);
        entry.Invoke!();
        Assert.Equal(chosen, choice);
    }

    [Fact]
    public void TheMenuSpeaksTheUiLanguage()
    {
        Texts.SetLanguage("de");
        ContextMenuEntry entry = Assert.Single(HeaderThemeMenu.Compose("System", true, _ => { }).ToModel().Entries);
        Assert.Equal("Wie System", entry.Label);
        Assert.Equal("Farbschema", Texts.T("settings.theme"));
    }

    [Fact]
    public void TheWindowWiresTheButtonAndTheMenuThroughThePreferenceRules()
    {
        string window = File.ReadAllText(Root("Desktop/Shell/MainWindow.axaml.cs"));
        Assert.Contains("candidate.Theme = MeowChan.Desktop.Theming.ThemePreference.Toggled(_config.Theme, ActualThemeVariant == ThemeVariant.Dark);", window, StringComparison.Ordinal);
        string menu = File.ReadAllText(Root("Desktop/Shell/MainWindow.HeaderMenu.cs"));
        Assert.Contains("new ContextMenuHost(ThemeToggleButton, ComposeHeaderThemeMenu)", menu, StringComparison.Ordinal);
        Assert.Contains("await SaveAppearanceAsync(candidate);", menu, StringComparison.Ordinal);
    }

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}

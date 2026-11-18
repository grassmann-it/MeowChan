namespace MeowChan.DesktopTests;

public sealed class ThemeColorEditorVisualContractTests
{
    private static string Read(string relative) => File.ReadAllText(ThemePaletteTests.Root(relative));

    [Fact]
    public void RoleSwatchStretchesItsActualColorAcrossTheButtonCard()
    {
        string row = Read("Desktop/Controls/ThemeColorRow.cs");
        Assert.Contains("HorizontalContentAlignment = HorizontalAlignment.Stretch", row);
        Assert.Contains("VerticalContentAlignment = VerticalAlignment.Stretch", row);
        Assert.Contains("_swatchFill.Background = new SolidColorBrush(view.Color);", row);
    }

    [Fact]
    public void SelectedAccentPresetKeepsReadableTextOnItsAccentSurface()
    {
        string dialog = Read("Desktop/Views/Settings/ThemeColorsDialog.cs");
        string styles = Read("Desktop/WorkspaceStyles.axaml");
        Assert.Contains("Classes = { \"accent-preset\" }", dialog);
        Assert.Contains("ToggleButton.accent-preset TextBlock", styles);
        Assert.Contains("ToggleButton.accent-preset:checked", styles);
        Assert.Contains("Workspace.AccentSurface", styles[styles.IndexOf("ToggleButton.accent-preset:checked", StringComparison.Ordinal)..]);
        Assert.Contains("Workspace.Text", styles[styles.IndexOf("ToggleButton.accent-preset:checked", StringComparison.Ordinal)..]);
    }
}

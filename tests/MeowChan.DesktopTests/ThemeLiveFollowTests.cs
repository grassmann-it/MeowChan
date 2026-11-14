namespace MeowChan.DesktopTests;

// TH2: colours that code resolves once follow a live palette apply, and the window applies Config.ThemeOverrides.
public sealed class ThemeLiveFollowTests
{
    [Fact]
    public void ColoursResolvedOnceInCodeFollowAnApply()
    {
        // Source pins (no GUI here): the two gallery overlay tones re-read on every apply, the settings search highlight is a
        // dynamic resource, and the window applies the stored overrides at startup and after every settings apply.
        string filmstrip = File.ReadAllText(ThemePaletteTests.Root("Desktop/GalleryViewer.Filmstrip.cs"));
        string overlay = File.ReadAllText(ThemePaletteTests.Root("Desktop/GalleryViewer.Overlay.cs"));
        string search = File.ReadAllText(ThemePaletteTests.Root("Desktop/Views/Settings/SettingsEditor.Search.cs"));
        string window = File.ReadAllText(ThemePaletteTests.Root("Desktop/Shell/MainWindow.axaml.cs"));
        Assert.Contains("ThemeRuntime.FollowApplied(this, () => { _filmstripStyleKnown = false; UpdateFilmstripPlacement(); });", filmstrip, StringComparison.Ordinal);
        Assert.Contains("ThemeRuntime.FollowApplied(this, ApplyOverlayBackgrounds);", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("TryFindResource(\"Workspace.Accent\"", search, StringComparison.Ordinal);
        Assert.Contains("run[!TextElement.ForegroundProperty] = new DynamicResourceExtension(\"Workspace.Accent\");", search, StringComparison.Ordinal);
        Assert.Equal(2, window.Split("ThemeRuntime.ApplyOverrides(_config.ThemeOverrides)").Length - 1);
        Assert.Contains("ThemeRuntime.NeedsApply(_config.ThemeOverrides)", window, StringComparison.Ordinal);
    }
}

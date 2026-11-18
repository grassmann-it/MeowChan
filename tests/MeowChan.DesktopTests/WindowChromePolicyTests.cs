using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia.Controls;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// Source/pure-policy coverage only. No desktop or compositor evidence.
public sealed class WindowChromePolicyTests
{
    [Theory]
    [InlineData(WindowState.Normal, true, true, WindowState.Maximized)]
    [InlineData(WindowState.Normal, false, true, WindowState.Normal)]
    [InlineData(WindowState.Maximized, false, true, WindowState.Normal)]
    [InlineData(WindowState.Maximized, true, false, WindowState.Maximized)]
    [InlineData(WindowState.FullScreen, true, true, WindowState.FullScreen)]
    [InlineData(WindowState.Minimized, true, true, WindowState.Maximized)]
    public void CaptionRespectsCapabilitiesAndNeverExitsFullscreen(WindowState state, bool canMaximize,
        bool canResize, WindowState expected)
    {
        Assert.Equal(expected, WindowChromePolicy.ToggleMaximize(state, canMaximize, canResize));
        Assert.Equal(expected != state, WindowChromePolicy.CanToggleMaximize(state, canMaximize, canResize));
    }

    [Theory]
    [InlineData(WindowState.Normal, "window.maximize")]
    [InlineData(WindowState.Maximized, "window.restore")]
    public void CaptionUsesExistingLocalizationKeys(WindowState state, string expected) =>
        Assert.Equal(expected, WindowChromePolicy.MaximizeLabel(state));

    // Modals plan MD3: the XAML no longer carries literal sizes or the startup location; the dialog factory's size step
    // (DialogWindow.Configure in the code-behind: size, minimum, centring on the owner, screen fit) does.
    [Theory]
    [InlineData("Desktop/Views/Settings/SettingsWindow.axaml", "DialogSize.XL")]
    [InlineData("Desktop/Views/About/AboutWindow.axaml", "DialogSize.L")]
    public void SecondaryWindowsHaveOwnCaptionWithoutChangingCapabilitiesOrSize(string path, string step)
    {
        XElement window = XDocument.Load(Source(path)).Root!;
        Assert.Equal("BorderOnly", (string?)window.Attribute("WindowDecorations"));
        Assert.Equal("True", (string?)window.Attribute("ExtendClientAreaToDecorationsHint"));
        Assert.Equal("{x:Static desktop:WorkspaceMetrics.TitleBarHeight}", (string?)window.Attribute("ExtendClientAreaTitleBarHeightHint"));
        foreach (string literal in new[] { "WindowStartupLocation", "Width", "Height", "MinWidth", "MinHeight" })
            Assert.Null(window.Attribute(literal));
        Assert.Contains("DialogWindow.Configure(this, " + step + ", modal: ", File.ReadAllText(Source(path + ".cs")));
        Assert.Null(window.Attribute("CanResize"));
        Assert.Null(window.Attribute("CanMaximize"));
        Assert.Null(window.Attribute("CanMinimize"));
        XElement root = window.Elements().Single(e => e.Name.LocalName == "Grid");
        Assert.Equal("Auto,*", (string?)root.Attribute("RowDefinitions"));
        Assert.Equal("{DynamicResource Workspace.Background}", (string?)root.Attribute("Background"));
        Assert.Null(root.Attribute("Margin"));
        Assert.Single(root.Elements().Where(e => e.Name.LocalName == "WindowChromeBar"));
    }

    [Fact]
    public void MainClientRootPaintsWithoutInventingFrameOffsets()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement root = XDocument.Load(Source("Desktop/Shell/MainWindow.axaml")).Descendants()
            .Single(e => (string?)e.Attribute(x + "Name") == "WorkspaceRoot");
        Assert.Equal("{DynamicResource Workspace.Background}", (string?)root.Attribute("Background"));
        Assert.Null(root.Attribute("Margin"));
        Assert.Null(root.Attribute("Width"));
        Assert.Null(root.Attribute("RenderTransform"));
    }

    [Fact]
    public void SettingsKeepsBusyCloseGuardAndEditorLifetime()
    {
        string source = File.ReadAllText(Source("Desktop/Views/Settings/SettingsWindow.axaml.cs"));
        Assert.Contains("if (Editor.IsBusy && e.CloseReason == WindowCloseReason.WindowClosing) e.Cancel = true;", source);
        Assert.Contains("Editor.EndLifetime();", source);
        Assert.Contains("Editor.Saved -= OnFinished;", source);
        Assert.Contains("Editor.Cancelled -= OnFinished;", source);
    }

    private static string Source(string path, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", path));
}

using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace MeowChan.DesktopTests;

// Source/policy seam coverage only. Native client-area pixels, platform hit testing,
// DPI rounding and compositor behavior require a root-owned Windows/Linux/macOS run.
public sealed class FirstRunWindowChromeTests
{
    [Fact]
    public void FirstRunUsesSharedCaptionAndPreservesWindowGeometryAndContentMargin()
    {
        XElement window = XDocument.Load(Source("Desktop/Views/Settings/FirstRunSetupWindow.axaml")).Root!;
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        Assert.Equal("BorderOnly", (string?)window.Attribute("WindowDecorations"));
        Assert.Equal("True", (string?)window.Attribute("ExtendClientAreaToDecorationsHint"));
        Assert.Equal("{x:Static desktop:WorkspaceMetrics.TitleBarHeight}", (string?)window.Attribute("ExtendClientAreaTitleBarHeightHint"));
        // Modals plan MD3: the size comes from the dialog factory's L step, not from XAML literals.
        foreach (string literal in new[] { "WindowStartupLocation", "Width", "Height", "MinWidth", "MinHeight" })
            Assert.Null(window.Attribute(literal));
        Assert.Contains("DialogWindow.Configure(this, DialogSize.L, modal: true);", File.ReadAllText(Source("Desktop/Views/Settings/FirstRunSetupWindow.axaml.cs")));

        XElement root = window.Elements().Single(e => e.Name.LocalName == "Grid");
        Assert.Equal("Auto,*", (string?)root.Attribute("RowDefinitions"));
        Assert.Equal("{DynamicResource Workspace.Background}", (string?)root.Attribute("Background"));
        Assert.Single(root.Elements().Where(e => e.Name.LocalName == "WindowChromeBar"));

        XElement content = root.Elements().Single(e => e.Name.LocalName == "Grid" &&
            (string?)e.Attribute(x + "Name") == null);
        // UX103 P7 dialog frame: header, error strip, scrolling body, full-width footer.
        // The 20 px content inset moved from the frame grid onto header and body.
        Assert.Equal("Auto,Auto,*,Auto", (string?)content.Attribute("RowDefinitions"));
        Assert.Null(content.Attribute("Margin"));
        Assert.Equal("20,16,20,8", (string?)content.Elements().Single(e => (string?)e.Attribute(x + "Name") == "DialogFrameHeader").Attribute("Padding"));
    }

    [Fact]
    public void FirstRunAttachesCaptionWithoutChangingCloseOrCommitGuards()
    {
        string source = File.ReadAllText(Source("Desktop/Views/Settings/FirstRunSetupWindow.axaml.cs"));
        Assert.Contains("AppCaption.Attach(this);", source);
        Assert.Contains("if (_committing && e.CloseReason == WindowCloseReason.WindowClosing) e.Cancel = true;", source);
        Assert.Contains("private void OnLaterClick(object? sender, RoutedEventArgs e) { if (!_committing) Close(); }", source);
        Assert.Contains("if (_closed) return;", source);
        Assert.Contains("_commitTask = _accept(values!);", source);
        Assert.DoesNotContain("+ 1", source);
        Assert.DoesNotContain("+1", source);
    }

    [Fact]
    public void SharedCaptionRemainsTheOnlyCaptionImplementationForThisWindow()
    {
        string markup = File.ReadAllText(Source("Desktop/Views/Settings/FirstRunSetupWindow.axaml"));
        Assert.Contains("<desktop:WindowChromeBar x:Name=\"AppCaption\" />", markup);
        Assert.DoesNotContain("WindowMinimizeButton", markup);
        Assert.DoesNotContain("WindowMaximizeButton", markup);
        Assert.DoesNotContain("WindowCloseButton", markup);
    }

    private static string Source(string path, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", path));
}

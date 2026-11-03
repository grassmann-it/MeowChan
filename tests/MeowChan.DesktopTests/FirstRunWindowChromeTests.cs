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
        Assert.Equal("48", (string?)window.Attribute("ExtendClientAreaTitleBarHeightHint"));
        Assert.Equal("720", (string?)window.Attribute("Width"));
        Assert.Equal("700", (string?)window.Attribute("Height"));
        Assert.Equal("440", (string?)window.Attribute("MinWidth"));
        Assert.Equal("400", (string?)window.Attribute("MinHeight"));

        XElement root = window.Elements().Single(e => e.Name.LocalName == "Grid");
        Assert.Equal("48,*", (string?)root.Attribute("RowDefinitions"));
        Assert.Equal("{DynamicResource Workspace.Background}", (string?)root.Attribute("Background"));
        Assert.Single(root.Elements().Where(e => e.Name.LocalName == "WindowChromeBar"));

        XElement content = root.Elements().Single(e => e.Name.LocalName == "Grid" &&
            (string?)e.Attribute(x + "Name") == null);
        Assert.Equal("Auto,*,Auto", (string?)content.Attribute("RowDefinitions"));
        Assert.Equal("20", (string?)content.Attribute("Margin"));
        Assert.Equal("12", (string?)content.Attribute("RowSpacing"));
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

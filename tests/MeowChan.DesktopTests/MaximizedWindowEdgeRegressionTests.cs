using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// Pure policy and source-contract checks only; no Avalonia platform setup.
// These do not measure a native client rectangle, DPI rounding or compositor pixels.
public sealed class MaximizedWindowEdgeRegressionTests
{
    public static IEnumerable<object[]> CaptionTransitions()
    {
        foreach (WindowState state in new[] { WindowState.Normal, WindowState.Minimized,
                     WindowState.Maximized, WindowState.FullScreen })
        foreach (bool canMaximize in new[] { false, true })
        foreach (bool canResize in new[] { false, true })
        {
            WindowState expected = state switch
            {
                WindowState.FullScreen => WindowState.FullScreen,
                WindowState.Maximized when canResize => WindowState.Normal,
                WindowState.Normal or WindowState.Minimized when canMaximize => WindowState.Maximized,
                _ => state
            };
            yield return new object[] { state, canMaximize, canResize, expected };
        }
    }

    [Theory]
    [MemberData(nameof(CaptionTransitions))]
    public void MaximizingIsAStateRequestNotFullscreenOrAnUnconditionalRestore(WindowState state,
        bool canMaximize, bool canResize, WindowState expected)
    {
        Assert.Equal(expected, WindowChromePolicy.ToggleMaximize(state, canMaximize, canResize));
        Assert.Equal(expected != state, WindowChromePolicy.CanToggleMaximize(state, canMaximize, canResize));
        Assert.Equal(state == WindowState.Maximized ? "window.restore" : "window.maximize",
            WindowChromePolicy.MaximizeLabel(state));
    }

    [Theory]
    [InlineData("Desktop/Shell/MainWindow.axaml")]
    [InlineData("Desktop/Views/Settings/SettingsWindow.axaml")]
    [InlineData("Desktop/Views/About/AboutWindow.axaml")]
    public void ClientRootHasNoLocalInsetsOrFixedExtentThatCouldExposeTheDesktop(string path)
    {
        XElement window = XDocument.Load(Source(path)).Root!;
        // BorderOnly retains the native frame; None is not a seam repair.
        Assert.Equal("BorderOnly", (string?)window.Attribute("WindowDecorations"));
        Assert.Equal("True", (string?)window.Attribute("ExtendClientAreaToDecorationsHint"));
        Assert.Equal("{x:Static desktop:WorkspaceMetrics.TitleBarHeight}", (string?)window.Attribute("ExtendClientAreaTitleBarHeightHint"));
        Assert.Null(window.Attribute("CanResize"));
        Assert.Null(window.Attribute("CanMaximize"));
        Assert.Null(window.Attribute("CanMinimize"));

        XElement root = window.Elements().Single(e => e.Name.LocalName == "Grid");
        Assert.Equal("{DynamicResource Workspace.Background}", (string?)root.Attribute("Background"));
        // These checks deliberately cover vertical geometry too; an edge workaround
        // must not trade a right gap for a displaced or clipped client area.
        foreach (string attribute in new[] { "Margin", "Width", "Height", "MaxWidth", "MaxHeight",
                     "RenderTransform", "LayoutTransform", "Opacity", "HorizontalAlignment", "VerticalAlignment" })
            Assert.Null(root.Attribute(attribute));
    }

    [Theory]
    [InlineData("WindowMinimizeButton", "MinimizeButton")]
    [InlineData("WindowMaximizeButton", "MaximizeButton")]
    [InlineData("WindowCloseButton", "CloseButton")]
    public void MainCaptionKeepsNativeHitTestRolesAndOnlyABottomSeparator(string name, string role)
    {
        XElement window = XDocument.Load(Source("Desktop/Shell/MainWindow.axaml")).Root!;
        XElement frame = Named(window, "WorkspaceTitleBarFrame");
        Assert.Equal("0,0,0,1", (string?)frame.Attribute("BorderThickness"));
        XElement header = Named(frame, "WorkspaceTitleBar");
        Assert.Equal("User", (string?)header.Attribute("WindowDecorationProperties.ElementRole"));
        XElement button = Named(header, name);
        Assert.Equal("Button", button.Name.LocalName);
        Assert.Equal(role, (string?)button.Attribute("WindowDecorationProperties.ElementRole"));
    }

    [Fact]
    public void SecondaryCaptionKeepsNativeRolesAndOnlyABottomSeparator()
    {
        // Geometry.Parse in the real control needs a rendering backend. Inspect
        // its construction contract instead of bootstrapping a fake native window.
        string bar = File.ReadAllText(Source("Desktop/WindowChromeBar.cs"));
        Assert.Contains("Height = WorkspaceMetrics.TitleBarHeight;", bar);
        Assert.Contains("new Border { BorderThickness = new Thickness(0, 0, 0, 1) }", bar);
        Assert.Contains("new Grid { ColumnDefinitions = new ColumnDefinitions(\"*,Auto\"), Background = Brushes.Transparent }", bar);
        Assert.Contains("WindowDecorationProperties.SetElementRole(header, WindowDecorationsElementRole.User);", bar);
        Assert.Contains("WindowDecorationProperties.SetElementRole(button, role);", bar);
        Assert.Contains("CaptionButton(\"WindowMinimizeButton\", \"M 0,5 L 10,5\", WindowDecorationsElementRole.MinimizeButton)", bar);
        Assert.Contains("CaptionButton(\"WindowMaximizeButton\", WindowChromePolicy.MaximizePath, WindowDecorationsElementRole.MaximizeButton)", bar);
        Assert.Contains("CaptionButton(\"WindowCloseButton\", \"M 0,0 L 10,10 M 10,0 L 0,10\", WindowDecorationsElementRole.CloseButton)", bar);
        Assert.Contains("WindowChromePolicy.ToggleMaximize(window.WindowState, window.CanMaximize, window.CanResize)", bar);
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void BothThemeBackgroundsAreOpaqueWithoutUsingFrameOffsets(string theme)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement styles = XDocument.Load(Source("Desktop/WorkspaceStyles.axaml")).Root!;
        XElement dictionary = styles.Descendants().Single(e => e.Name.LocalName == "ResourceDictionary"
            && (string?)e.Attribute(x + "Key") == theme);
        XElement background = dictionary.Elements().Single(e => (string?)e.Attribute(x + "Key") == "Workspace.Background");
        Assert.Equal("SolidColorBrush", background.Name.LocalName);
        Assert.Equal((byte)255, Color.Parse(background.Value.Trim()).A);
        Assert.Null(background.Attribute("Opacity"));
    }

    [Fact]
    public void MainCaptionPreservesTheNormalSnapshotAndWaitsForARealStateTransition()
    {
        // Source guards, not a simulation of X11 ConfigureNotify or Windows messages.
        string chrome = File.ReadAllText(Source("Desktop/Shell/MainWindow.Chrome.cs"));
        Assert.Contains("if (WindowState == WindowState.FullScreen) return;", chrome);
        Assert.Contains("if (!_captionRestorePending || Bounds.Size != _captionRestoreFromSize)", chrome);
        Assert.Contains("_captionRestoreSize = Bounds.Size;", chrome);
        Assert.Contains("_captionRestoreMode = _config.WorkspaceMode;", chrome);
        Assert.Contains("_captionRestorePending = _captionRestoreSize.HasValue;", chrome);
        Assert.Contains("_captionRestoreFromSize = Bounds.Size;", chrome);
        Assert.Contains("WindowState = WindowState.Normal;", chrome);
        Assert.Contains("WindowState = WindowState.Maximized;", chrome);
        Assert.Contains("if (e.Property == WindowStateProperty && !Equals(e.OldValue, e.NewValue)) CompleteCaptionRestore();", chrome);
        Assert.Contains("ContinueCaptionRestore();", chrome);
    }

    private static XElement Named(XElement root, string name)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return root.Descendants().Single(e => (string?)e.Attribute(x + "Name") == name);
    }

    private static string Source(string path, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", path));
}

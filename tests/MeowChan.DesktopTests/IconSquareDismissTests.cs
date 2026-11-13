using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopTests;

// DS-P3/P6: dismiss controls are icon-square buttons with a drawn stroke path. Parsed sources only; no Avalonia application.
public sealed class IconSquareDismissTests
{
    private static readonly XNamespace A = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    // The toast and the shell message dismiss with an icon-square button holding a stroke path, not the glyph character.
    [Fact]
    public void TheShellDismissIsAnIconSquareWithAPathAndKeepsItsAccessibleName()
    {
        XElement dismiss = XDocument.Load(Root("Desktop/Shell/MainWindow.axaml")).Descendants().Single(element => (string?)element.Attribute(X + "Name") == "ShellFeedbackDismissButton");
        Assert.Contains("icon-square", ((string?)dismiss.Attribute("Classes"))!.Split(' '));
        Assert.Equal("{Binding #ShellFeedbackDismissLabel.Text}", (string?)dismiss.Attribute("AutomationProperties.Name"));
        Assert.Equal("{Binding #ShellFeedbackDismissLabel.Text}", (string?)dismiss.Attribute("ToolTip.Tip"));
        Assert.Contains(dismiss.Descendants(), element => element.Name.LocalName == "Path" && (string?)element.Attribute(X + "Name") == "ShellFeedbackDismissIcon");
        Assert.DoesNotContain(dismiss.Descendants(), element => (string?)element.Attribute("Text") == "×");
    }

    // One icon-square definition (P6) and one icon system: the close mark is the drawn WorkspaceIcons.Close on a 16 dip grid.
    [Fact]
    public void TheCloseIconIsADrawn16DipStrokeAndTheSquareHasTheInputHeight()
    {
        Assert.Equal("{StaticResource Workspace.Size.Icon}", Setting("Path.glyph-icon", "Width"));
        Assert.Equal("1.5", Setting("Path.glyph-icon", "StrokeThickness"));
        Assert.Equal("{StaticResource Workspace.Size.Input}", Setting("Button.icon-square", "Width"));
        Assert.Equal("{StaticResource Workspace.Size.Input}", Setting("Button.icon-square", "Height"));
        Assert.Equal("M4,4 L12,12 M12,4 L4,12", WorkspaceIcons.DataOf(WorkspaceIcons.Close));
    }

    private static IEnumerable<XElement> Styles() => XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Root!.Elements(A + "Style");

    private static XElement Style(string selector) =>
        Assert.Single(Styles().Where(style => (string?)style.Attribute("Selector") == selector));

    private static string? Setting(string selector, string property) =>
        (string?)Assert.Single(Style(selector).Elements(A + "Setter").Where(setter => (string?)setter.Attribute("Property") == property)).Attribute("Value");

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}

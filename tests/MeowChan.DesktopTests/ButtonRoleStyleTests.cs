using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MeowChan.DesktopTests;

// Design system P1: one button height, a link without outline, ghost as the only quiet variant, and one class each for the
// hint role and the error text role. Parsed sources only.
public sealed class ButtonRoleStyleTests
{
    private static readonly XNamespace A = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void ButtonsAndInputsShareOneHeight()
    {
        XElement[] sizes = StyleRoot().Descendants(X + "Double").ToArray();
        string Size(string key) => Assert.Single(sizes, element => (string?)element.Attribute(X + "Key") == key).Value.Trim();
        Assert.Equal("32", Size("Workspace.Size.Control"));
        Assert.Equal(Size("Workspace.Size.Control"), Size("Workspace.Size.Input"));
        Assert.Equal("30", Size("Workspace.Size.HeaderIcon"));
        Assert.Equal("{StaticResource Workspace.Size.Control}", Setting("Button", "MinHeight"));
        Assert.Equal("{StaticResource Workspace.Size.HeaderIcon}", Setting("Button.header-icon", "Height"));
    }

    [Fact]
    public void ALinkIsAccentTextWithoutOutlineOrFill()
    {
        Assert.Equal("0", Setting("Button.link", "BorderThickness"));
        Assert.Equal("Transparent", Setting("Button.link", "Background"));
        Assert.Equal("{DynamicResource Workspace.Accent}", Setting("Button.link", "Foreground"));
    }

    [Fact]
    public void NoButtonCombinesSubtleWithGhostOrUsesAnUndefinedClass()
    {
        string[] defined = StyleRoot().Elements(A + "Style").Select(style => (string?)style.Attribute("Selector") ?? "")
            .SelectMany(selector => Regex.Matches(selector, @"Button\.([a-z0-9-]+)").Select(match => match.Groups[1].Value)).ToArray();
        Assert.Contains("ghost", defined);
        Assert.DoesNotContain("accent", defined);
        foreach (string file in Directory.EnumerateFiles(Root("Desktop"), "*.axaml", SearchOption.AllDirectories))
            foreach (XElement button in XDocument.Load(file).Descendants(A + "Button"))
            {
                string[] classes = ((string?)button.Attribute("Classes") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                Assert.False(classes.Contains("subtle") && classes.Contains("ghost"), file + ": " + (string?)button.Attribute(X + "Name"));
                Assert.DoesNotContain("accent", classes);
            }
    }

    [Fact]
    public void ErrorTextHasOneClassAndHintsDoNotMixMutedWithTheHintClass()
    {
        Assert.DoesNotContain(StyleRoot().Elements(A + "Style"), style => ((string?)style.Attribute("Selector") ?? "").Contains("TextBlock.error,"));
        foreach (string file in Directory.EnumerateFiles(Root("Desktop"), "*.axaml", SearchOption.AllDirectories))
            foreach (string classes in XDocument.Load(file).Descendants().Select(element => (string?)element.Attribute("Classes") ?? ""))
            {
                string[] list = classes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                Assert.False(list.Contains("error"), file + ": " + classes);
                Assert.False(list.Contains("muted") && list.Contains("workspace-help"), file + ": " + classes);
            }
        foreach (string file in Directory.EnumerateFiles(Root("Desktop"), "*.cs", SearchOption.AllDirectories))
            Assert.DoesNotMatch("Classes(\\.Add|\\.Set)?\\(?\\s*\"error\"|Classes = \\{[^}]*\"error\"", File.ReadAllText(file));
    }

    private static XElement StyleRoot() => XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Root!;

    private static string? Setting(string selector, string property) =>
        (string?)Assert.Single(Assert.Single(StyleRoot().Elements(A + "Style"), style => (string?)style.Attribute("Selector") == selector).Elements(A + "Setter")
            .Where(setter => (string?)setter.Attribute("Property") == property)).Attribute("Value");

    private static string Root(string path, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", path));
}

using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

/// <summary>Connects the productive footer switch's XAML registration to its WorkView policy row.</summary>
public sealed class BooruWorkViewRegistrationTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Wv = "using:MeowChan.Desktop.WorkViews";

    [Fact]
    public void ProductiveBooruToggleIsRegisteredAsTheRequiredShellFooterElement()
    {
        XDocument shell = XDocument.Load(Root("Desktop/Shell/MainWindow.axaml"));
        XElement toggle = Assert.Single(shell.Descendants(), element =>
            (string?)element.Attribute(X + "Name") == "ShowBooruToolsToggle");

        string id = Assert.IsType<string>(toggle.Attribute(Wv + "WorkViewElement.Id")?.Value);
        WorkViewEntry policy = WorkViewPolicy.Get(id);

        Assert.Equal("shell.footer.booru-toggle", id);
        Assert.Equal(WorkViewPage.Shell, policy.Page);
        Assert.Equal(WorkViewKind.Element, policy.Kind);
        Assert.All(WorkViewSetting.All, view =>
            Assert.Equal(Presence.Required, WorkViewPolicy.Resolve(id, view)));
    }

    private static string Root(string relative, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", relative));
}

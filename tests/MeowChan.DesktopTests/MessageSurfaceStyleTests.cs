using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia.Controls.Notifications;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopTests;

// DS-P3: the one message surface (WorkspaceBanner on Border.workspace-error/-warning/-info/-success). Parsed sources and plain
// controls only; no Avalonia application.
public sealed class MessageSurfaceStyleTests
{
    private static readonly XNamespace A = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("workspace-error", "ErrorSurface", "Error")]
    [InlineData("workspace-warning", "WarningSurface", "Warning")]
    [InlineData("workspace-info", "AccentSurface", "Accent")]
    [InlineData("workspace-success", "SuccessSurface", "Success")]
    public void EverySeverityIsASurfaceTokenOnItsEdgeTokenWithTheMessageInset(string name, string surface, string edge)
    {
        string selector = "Border." + name;
        Assert.Equal("{DynamicResource Workspace." + surface + "}", Setting(selector, "Background"));
        Assert.Equal("{DynamicResource Workspace." + edge + "}", Setting(selector, "BorderBrush"));
        Assert.Equal("{StaticResource Workspace.Inset.Message}", Setting(selector, "Padding"));
        // Edge width and radius come from the shared base rule, so all four follow the tokens together.
        string shared = (string)Assert.Single(Styles().Where(style => ((string?)style.Attribute("Selector"))!.StartsWith("Border.workspace-section,"))).Attribute("Selector")!;
        Assert.Contains(selector + ",", shared + ",");
    }

    [Theory]
    [InlineData(NotificationType.Error, "workspace-error")]
    [InlineData(NotificationType.Warning, "workspace-warning")]
    [InlineData(NotificationType.Information, "workspace-info")]
    [InlineData(NotificationType.Success, "workspace-success")]
    public void TheBannerMapsItsSeverityToExactlyOneSurfaceClass(NotificationType type, string expected)
    {
        WorkspaceBanner banner = new() { Type = type };
        Assert.Equal(new[] { expected }, banner.Classes.Where(name => name.StartsWith("workspace-")).ToArray());
        banner.Type = NotificationType.Error;
        Assert.Equal(new[] { "workspace-error" }, banner.Classes.Where(name => name.StartsWith("workspace-")).ToArray());
    }

    [Fact]
    public void ABannerIsStyledAsABorderSoTheTypeSelectorsApply()
    {
        System.Reflection.PropertyInfo key = typeof(WorkspaceBanner).GetProperty("StyleKeyOverride",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Equal(typeof(Avalonia.Controls.Border), key.GetValue(new WorkspaceBanner()));
    }

    [Fact]
    public void NoUrsaBannerIsLeftInTheDesktopSources()
    {
        foreach (string file in Directory.EnumerateFiles(Root("Desktop"), "*.*", SearchOption.AllDirectories)
                     .Where(path => path.EndsWith(".axaml") || path.EndsWith(".cs")))
        {
            string text = File.ReadAllText(file);
            Assert.DoesNotContain("ursa:Banner", text);
            Assert.DoesNotContain("Ursa.Controls.Banner", text);
        }
    }

    // The accessibility contract of the old banners lives on their text, which every page kept: assertive announcement of an error.
    [Theory]
    [InlineData("Desktop/Views/Plugins/PluginsPage.axaml", "PluginPageError")]
    [InlineData("Desktop/Views/Sources/SourcesPage.axaml", "SourceMediaIntegrityError")]
    [InlineData("Desktop/Views/Sources/SourcesPage.axaml", "SourcesActionError")]
    [InlineData("Desktop/Views/Rules/RulesPage.axaml", "RuleValidationSummary")]
    [InlineData("Desktop/Views/Rules/RulesPage.axaml", "RuleActionError")]
    [InlineData("Desktop/Views/Settings/SettingsEditor.axaml", "SettingsError")]
    [InlineData("Desktop/Views/Archive/ArchivePage.axaml", "ArchiveErrorText")]
    public void ThePageErrorTextsStayAssertiveLiveRegionsInsideABanner(string file, string name)
    {
        XElement text = XDocument.Load(Root(file)).Descendants().Single(element => (string?)element.Attribute(X + "Name") == name);
        Assert.Equal("Assertive", (string?)text.Attribute("AutomationProperties.LiveSetting"));
        Assert.Contains(text.Ancestors(), ancestor => ancestor.Name.LocalName == "WorkspaceBanner");
    }

    [Fact]
    public void TheShellBannerKeepsItsNameAndItsActions()
    {
        XElement banner = XDocument.Load(Root("Desktop/Shell/MainWindow.axaml")).Descendants().Single(element => (string?)element.Attribute(X + "Name") == "ShellActionErrorBanner");
        Assert.Equal("WorkspaceBanner", banner.Name.LocalName);
        Assert.Contains(banner.Descendants(), element => (string?)element.Attribute(X + "Name") == "ShellFeedbackDiagnosticsButton");
        Assert.Contains(banner.Descendants(), element => (string?)element.Attribute(X + "Name") == "ShellFeedbackDismissButton");
    }

    private static IEnumerable<XElement> Styles() => XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Root!.Elements(A + "Style");

    private static XElement Style(string selector) =>
        Assert.Single(Styles().Where(style => (string?)style.Attribute("Selector") == selector));

    private static string? Setting(string selector, string property) =>
        (string?)Assert.Single(Style(selector).Elements(A + "Setter").Where(setter => (string?)setter.Attribute("Property") == property)).Attribute("Value");

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}

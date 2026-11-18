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
    public void TheFooterAndMenuBooruSwitchesAreGoneAndTheirPolicyRowsWithThem()
    {
        // F1 (Tom, 14.11.2026): "Show Booru" left the footer and the ⋯ menu; Settings › Appearance › Footer lists Booru like every page and holds
        // the check box "Show Booru actions" (Config.ShowBooruTools). No XAML element registers a row that does not exist any more.
        XDocument shell = XDocument.Load(Root("Desktop/Shell/MainWindow.axaml"));
        Assert.DoesNotContain(shell.Descendants(), element => (string?)element.Attribute(X + "Name") == "ShowBooruToolsToggle");
        Assert.DoesNotContain(shell.Descendants(), element => (string?)element.Attribute(Wv + "WorkViewElement.Id") is "shell.footer.booru-toggle" or "shell.appmenu.booru-toggle");
        Assert.DoesNotContain(WorkViewPolicy.Entries, entry => entry.Id is "shell.footer.booru-toggle" or "shell.appmenu.booru-toggle");
        Assert.DoesNotContain("BooruToolsMenuItem", File.ReadAllText(Root("Desktop/Shell/MainWindow.WorkViewMenu.cs")), StringComparison.Ordinal);
        // The footer button itself keeps its row: direct from Simple on, in More in Barebones.
        Assert.All(WorkViewSetting.All, view => Assert.Equal(view == WorkView.Barebones ? Presence.Overflow : Presence.Direct,
            WorkViewPolicy.Resolve("shell.footer.booru", view)));
    }

    [Fact]
    public void TheSettingsPageHoldsShowBooruActionsBoundToTheUnchangedProfileSetting()
    {
        string settings = File.ReadAllText(Root("Desktop/Views/Settings/SettingsEditor.axaml"));
        Assert.Contains("x:Name=\"ShowBooruActionsInput\" IsChecked=\"{Binding ShowBooruActions, Mode=TwoWay}\"", settings, StringComparison.Ordinal);
        string footer = File.ReadAllText(Root("Desktop/Views/Settings/SettingsEditor.Footer.cs"));
        Assert.Contains("candidate.ShowBooruTools = ShowBooruActions;", footer, StringComparison.Ordinal);
        Assert.Contains("ShowBooruActions = config.ShowBooruTools;", footer, StringComparison.Ordinal);
        Assert.True(new Config().ShowBooruTools); // the setting and its default are the old ones
    }

    private static string Root(string relative, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", relative));
}

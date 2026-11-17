using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia.Controls;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopTests;

/// <summary>
/// Release review of 17.11.2026, package shell. ST-06: the header's "⋯" menu is a submenu popup of the menu bar, so the context menu rule
/// (a label that does not fit ends in an ellipsis and says its full text as a tooltip) is given to it by <c>MenuLayout.IsSubmenuManaged</c>;
/// the tip logic runs here on plain menu items, the real popup (German labels, both themes) is the smoke context-menu-layout. ST-07: the
/// "View" entry wears the icon of the work view command. LY-03: every page title starts at <see cref="PageHeader.PageTopOffset"/> below
/// the page area; the pages are read from their markup and code here, measured on the real window by the smoke footer-navigation
/// (PageTitleOffsetChecks).
/// </summary>
public sealed class ShellMenuAndTitleOffsetTests
{
    private static readonly XNamespace A = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));

    // The top of a Thickness written as "all", "horizontal,vertical" or "left,top,right,bottom".
    private static double Top(string thickness)
    {
        string[] parts = thickness.Split(',', StringSplitOptions.TrimEntries);
        return double.Parse(parts.Length == 1 ? parts[0] : parts[1], CultureInfo.InvariantCulture);
    }

    // ---- ST-06: labels that do not fit ----

    [Fact]
    public void ATrimmedLabelGetsItsFullTextAsTooltip()
    {
        MenuItem row = new() { Header = "Danbooru-Suche und Suchbeobachtung" };
        MenuLayout.TipSubmenuRow(row, "Danbooru-Suche und Suchbeobachtung");
        Assert.Equal("Danbooru-Suche und Suchbeobachtung", ToolTip.GetTip(row));
    }

    [Fact]
    public void ALabelThatFitsGetsNoTooltip()
    {
        MenuItem row = new() { Header = "Settings" };
        MenuLayout.TipSubmenuRow(row, null);
        Assert.Null(ToolTip.GetTip(row));
    }

    [Fact]
    public void ARowWithATipOfItsOwnKeepsIt()
    {
        // The command rows carry label and shortcut as their tip (CommandBindings.Apply): the layout must not replace it.
        MenuItem row = new() { Header = "Pause / resume downloads and checks" };
        ToolTip.SetTip(row, "Pause downloads and checks\nCtrl+Shift+P");
        MenuLayout.TipSubmenuRow(row, "Pause / resume downloads and checks");
        Assert.Equal("Pause downloads and checks\nCtrl+Shift+P", ToolTip.GetTip(row));
        MenuLayout.TipSubmenuRow(row, null);
        Assert.Equal("Pause downloads and checks\nCtrl+Shift+P", ToolTip.GetTip(row));
    }

    [Fact]
    public void TheTipFollowsTheLabelWhenTheLanguageChangesAndGoesWhenTheLabelFitsAgain()
    {
        MenuItem row = new() { Header = "Import files and folders into the archive…" };
        MenuLayout.TipSubmenuRow(row, "Import files and folders into the archive…");
        MenuLayout.TipSubmenuRow(row, "Dateien und Ordner in das Archiv importieren…");
        Assert.Equal("Dateien und Ordner in das Archiv importieren…", ToolTip.GetTip(row));
        MenuLayout.TipSubmenuRow(row, null);
        Assert.Null(ToolTip.GetTip(row));
        // A tip somebody else sets afterwards is not ours to drop.
        ToolTip.SetTip(row, "Own tip");
        MenuLayout.TipSubmenuRow(row, null);
        Assert.Equal("Own tip", ToolTip.GetTip(row));
    }

    [Fact]
    public void TheSubmenuOptInIsAnAttachedPropertyOfMenuItemsAndTheHeaderMenuTurnsItOn()
    {
        MenuItem owner = new();
        Assert.False(MenuLayout.GetIsSubmenuManaged(owner));
        MenuLayout.SetIsSubmenuManaged(owner, true);
        Assert.True(MenuLayout.GetIsSubmenuManaged(owner));
        Assert.Contains("MenuLayout.SetIsSubmenuManaged(WorkspaceMenuItem, true);", File.ReadAllText(Root("Desktop/Shell/MainWindow.WorkViewMenu.cs")));
    }

    // ---- ST-07 / L-L2: the View entry has the eye icon of the work view command ----

    [Fact]
    public void TheViewEntryOfTheHeaderMenuWearsTheIconOfTheWorkViewCommand()
    {
        string code = File.ReadAllText(Root("Desktop/Shell/MainWindow.WorkViewMenu.cs"));
        Assert.Contains("_workViewGroupMenu.Icon ??= CommandBindings.Icon(\"workspace.view-cycle\");", code);
        // The command's catalog icon is the eye, and the entry inside the group uses the same one.
        Assert.Contains("CommandBindings.Apply(_viewCycleMenu, \"workspace.view-cycle\");", code);
        Assert.Contains("\"workspace.view-cycle\", \"workview.viewCycle\", \"Switch work view\", \"Arbeitsansicht wechseln\", \"eye\"",
            File.ReadAllText(Root("Desktop/Commands/CommandCatalog.cs")));
    }

    // ---- LY-03: one title offset ----

    [Fact]
    public void TheTitleOffsetIsTheTopOfThePageMargin()
    {
        XElement resources = XDocument.Load(Root("Desktop/WorkspaceStyles.axaml")).Root!.Element(A + "Styles.Resources")!;
        string inset = (string)Assert.Single(resources.Descendants(A + "Thickness").Where(e => (string?)e.Attribute(X + "Key") == "Workspace.Inset.Page"));
        Assert.Equal(PageHeader.PageTopOffset, Top(inset));
    }

    [Fact]
    public void ThePagesWithTheirHeadInThePageMarginNeedNothingOfTheirOwnAboveTheTitle()
    {
        // Archive, Plugins/Sources and Rules put their head in a grid with the page margin; Rules used to add 12 on top (title at 77 instead of 64).
        foreach (string file in new[] { "Desktop/Views/Archive/ArchivePage.axaml", "Desktop/Views/Plugins/PluginsPage.axaml", "Desktop/Views/Rules/RulesPage.axaml" })
            Assert.Contains("Margin=\"{StaticResource Workspace.Inset.Page}\"", File.ReadAllText(Root(file)));
        XElement rules = Assert.Single(XDocument.Load(Root("Desktop/Views/Rules/RulesPage.axaml")).Descendants(), e => (string?)e.Attribute(X + "Name") == "RulesHeader");
        Assert.Equal(0d, Top((string)rules.Attribute("Margin")!));
    }

    [Theory]
    [InlineData("Desktop/Views/Status/StatusPage.axaml", "StatusPageHeader")]
    [InlineData("Desktop/Views/Settings/SettingsEditor.axaml", "SettingsHeader")]
    public void ThePagesThatSetTheirOwnHeadMarginStartTheTitleAtTheOneOffset(string file, string name)
    {
        XElement head = Assert.Single(XDocument.Load(Root(file)).Descendants(), e => (string?)e.Attribute(X + "Name") == name);
        Assert.Equal(PageHeader.PageTopOffset, Top((string)head.Attribute("Margin")!));
    }

    [Fact]
    public void TheTaggingPageHeadStartsAtTheOneOffsetFromTheConstant()
    {
        // Built in code; it used to start at 8 (title at 60). Media builds its head the same way (its owner changes it, see the report).
        string code = File.ReadAllText(Root("Desktop/Views/Tagging/TaggingPage.cs"));
        Assert.Contains("Margin = new Thickness(12, PageHeader.PageTopOffset, 12, 8)", code);
    }
}

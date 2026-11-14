using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace MeowChan.DesktopTests;

/// <summary>Source contracts only; native category/locale/focus behavior is covered centrally by settings smokes.</summary>
public sealed class SettingsOverviewIntegrationTests
{
    [Fact]
    public void OverviewHasItsOwnInitiallyHiddenHostAfterBehaviorAndBeforeShortcuts()
    {
        XDocument document = XDocument.Load(Root("Desktop/Views/Settings/SettingsEditor.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement page = Assert.Single(document.Descendants(), e => (string?)e.Attribute(x + "Name") == "SettingsWorkViewsPage");
        Assert.Equal("False", (string?)page.Attribute("IsVisible"));
        Assert.Contains(page.Descendants(), e => (string?)e.Attribute(x + "Name") == "SettingsWorkViewOverviewHost");
        string sections = Read("Sections.cs");
        Assert.Contains("yield return (\"settings.workviews\", SettingsWorkViewsPage);", sections);
        Assert.True(sections.IndexOf("yield return (\"settings.behavior\"", StringComparison.Ordinal)
            < sections.IndexOf("yield return (\"settings.workviews\"", StringComparison.Ordinal));
        Assert.True(sections.IndexOf("yield return (\"settings.workviews\"", StringComparison.Ordinal)
            < sections.IndexOf("yield return (\"settings.shortcuts\"", StringComparison.Ordinal));
        Assert.Contains("if (id == \"settings.workviews\") EnsureWorkViewOverview();", sections);
    }

    [Fact]
    public void OnlyOpeningTheCategoryCreatesTheOverviewAndSearchAlwaysExcludesItsFilters()
    {
        string integration = Read("WorkViewOverview.cs"), editor = Read("axaml.cs"), search = Read("Search.cs");
        Assert.Contains("new WorkViewOverviewControl(CurrentWorkView)", integration);
        Assert.Contains("if (_workViewOverview != null) return _workViewOverview;", integration);
        Assert.Contains("GetWorkViewOverview().EnsureLoaded()", integration);
        Assert.DoesNotContain("EnsureWorkViewOverview();", editor);
        Assert.DoesNotContain("GetWorkViewOverview()", search);
        Assert.Contains("if (id == \"settings.workviews\") continue;", search);
        Assert.Contains("_workViewOverview?.SetActiveView(view);", Read("WorkViews.cs"));
    }

    [Fact]
    public void ExplicitDeepLinkPassesPageAndLiteralTextWhileLocaleAndEndLifetimeAreWired()
    {
        string integration = Read("WorkViewOverview.cs"), editor = Read("axaml.cs");
        Assert.Contains("ShowWorkViewOverview(WorkViewPage? page = null, string? text = null)", integration);
        Assert.Contains("overview.SetPageFilter(page);", integration);
        Assert.Contains("overview.SetTextFilter(text);", integration);
        Assert.Contains("SelectCategory(\"settings.workviews\");", integration);
        Assert.Contains("RefreshWorkViewOverviewTexts();", editor);
        Assert.Contains("if (!_isClosed) _workViewOverview?.LocaleRefresh();", integration);
        Assert.Contains("EndWorkViewOverviewLifetime();", editor);
        Assert.Contains("_workViewOverview?.Dispose();", integration);
        Assert.Contains("SettingsWorkViewOverviewHost.Content = null;", integration);
        Assert.DoesNotContain("Texts.Changed +=", integration);
    }

    private static string Read(string suffix) => File.ReadAllText(Root("Desktop/Views/Settings/SettingsEditor." + suffix));
    private static string Root(string relative, [CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../..", relative));
}

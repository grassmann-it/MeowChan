using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

/// <summary>
/// Plan AP5 WV6: the work-view rows of Rules, Sources and Plugins and the page contracts that keep them honest.
/// The visual proof (what each view shows, ⋯ menus, focus) lives in the work-views smoke of these pages.
/// </summary>
[Collection("Localization")]
public sealed class WorkViewRulesSourcesPluginsTests
{
    private static readonly WorkViewPage[] Pages = [WorkViewPage.Rules, WorkViewPage.Sources, WorkViewPage.Plugins];

    [Fact]
    public void EveryRowOfThePagesIsRegisteredByTheirPageFiles()
    {
        foreach (WorkViewPage page in Pages)
        {
            string files = ReadPageSources(page);
            foreach (WorkViewEntry entry in WorkViewPolicy.Entries.Where(entry => entry.Page == page))
                Assert.True(files.Contains("\"" + entry.Id + "\"", StringComparison.Ordinal)
                    || files.Contains("WorkViewElement.Id=\"" + entry.Id + "\"", StringComparison.Ordinal),
                    $"{entry.Id} is not registered by the {page} page's files.");
        }
    }

    [Fact]
    public void SafetyConsentAndFooterRowsAreRequiredInEveryView()
    {
        string[] required =
        [
            "rules.scope.editing-header", "rules.preview.blocked", "rules.footer.save", "rules.footer.discard",
            "rules.overview", "rules.overview.create", "rules.overview.drafts", "rules.wizard.close", "rules.wizard.back", "rules.wizard.next",
            "rules.scope.new", "rules.preview",
            "sources.detail.media-integrity", "sources.detail.watches", "sources.detail.error",
            "plugins.header.review-repairs", "plugins.header.inventory-problems", "plugins.header.error",
            "plugins.detail.sdk-transition", "plugins.detail.draft-footer"
        ];
        foreach (string id in required)
            foreach (WorkView view in WorkViewSetting.All)
                Assert.Equal(Presence.Required, WorkViewPolicy.Resolve(id, view));
    }

    [Fact]
    public void ReducedBlocksFoldOrMoveUnderMoreInBarebonesAndShowInAdvanced()
    {
        // Barebones: the secondary blocks fold or move; the primary content of each page stays.
        foreach (string id in new[] { "rules.scope.saved", "rules.plugin-filter", "sources.detail.capabilities",
                     "sources.detail.technical", "plugins.directory", "plugins.detail.info" })
            Assert.Equal(Presence.Collapsed, WorkViewPolicy.Resolve(id, WorkView.Barebones));
        foreach (string id in new[] { "rules.templates", "plugins.header.add", "plugins.header.folder", "sources.detail.plugin",
                     "plugins.detail.watches", "sources.detail.rules", "sources.detail.diagnostics", "plugins.detail.developer" })
            Assert.Equal(Presence.Overflow, WorkViewPolicy.Resolve(id, WorkView.Barebones));
        // Nothing is hidden outright (the owner default: hidden tools live under More), and Advanced shows the page as before.
        foreach (WorkViewEntry entry in WorkViewPolicy.Entries.Where(entry => Pages.Contains(entry.Page)))
        {
            foreach (WorkView view in WorkViewSetting.All)
                Assert.NotEqual(Presence.Hidden, entry.For(view));
            Assert.NotEqual(Presence.Overflow, entry.For(WorkView.Advanced));
        }
    }

    [Fact]
    public void AStateThatNeedsAttentionRaisesAReducedBlock()
    {
        foreach (string id in new[] { "rules.preview", "rules.plugin-filter", "rules.templates", "plugins.detail.developer",
                     "plugins.detail.watches", "sources.detail.technical" })
            Assert.True(WorkViewPolicy.OnSurface(WorkViewPolicy.Resolve(id, WorkView.Barebones, nonDefaultState: true)), id);
    }

    [Fact]
    public void FoldedRulesBlocksAreDeclaredAsExpandersAndTheTemplatesCardIsGated()
    {
        string xaml = ReadSource("Desktop/Views/Rules/RulesPage.axaml");
        foreach (string name in new[] { "RuleSavedScopesExpander", "RulePluginExpander", "RuleTemplatesExpander" })
            Assert.Matches(@"<Expander\b[^>]*x:Name=""" + name + @"""", xaml);
        string code = ReadSource("Desktop/Views/Rules/RulesPage.WorkViews.cs");
        // R6: validation or a blocked preview marks the review step (never folded); an active filter opens the filter block.
        Assert.Contains("HasValidationError || HasPreviewBlocked", code);
        Assert.Contains("PluginFilterActive", code);
        Assert.Contains("TemplatesNeedAttention", code);
    }

    [Fact]
    public void PluginApprovalAndActivationControlsStayOutOfTheViewGates()
    {
        string code = ReadSource("Desktop/Views/Plugins/PluginsPage.WorkViews.cs");
        foreach (string name in new[] { "PluginEnable", "PluginCandidate", "PluginSdkTransition", "PluginDraftFooter", "PluginRecover" })
            Assert.DoesNotContain("\"" + name, code);
        // The developer area reappears for any diagnostics, build errors or probe results of the selected plugin.
        Assert.Contains("DeveloperAreaNeedsAttention", code);
        Assert.Contains("entry.Diagnostics.Count > 0", code);
    }

    [Fact]
    public void RevealingDeveloperDetailsAlwaysGoesThroughTheViewGate()
    {
        string actions = ReadSource("Desktop/Views/Plugins/PluginsPage.Actions.cs");
        Assert.Matches(@"case ""plugins\.show-developer-tools"":[^\n]*\n?[^\n]*RevealDeveloperArea", actions);
        string details = ReadSource("Desktop/Views/Plugins/PluginsPage.Details.cs");
        Assert.Contains("RevealDeveloperArea();", details);
    }

    [Fact]
    public void PagesApplyTheViewWhenBuiltAndOnEveryChange()
    {
        string window = ReadSource("Desktop/Shell/MainWindow.WorkViews.cs");
        Assert.Contains("_workViews.Changed +=", window);
        foreach (string page in new[] { "_rulesPage", "_sourcesPage", "_pluginsPage" })
            Assert.Contains(page + "?.ApplyWorkView(view, viewChanged);", window);
        Assert.Contains("_rulesPage.ApplyWorkView(ConfiguredWorkView", ReadSource("Desktop/Shell/MainWindow.Navigation.cs"));
        Assert.Contains("_sourcesPage.ApplyWorkView(ConfiguredWorkView", ReadSource("Desktop/Shell/MainWindow.Navigation.cs"));
        Assert.Contains("_pluginsPage.ApplyWorkView(ConfiguredWorkView", ReadSource("Desktop/Shell/MainWindow.Plugins.cs"));
    }

    [Fact]
    public void MoreButtonsListOnlyWhatTheViewMovesAndAreHiddenWhenEmpty()
    {
        Button add = new() { Content = "Add" }, folder = new() { Content = "Folder" }, stateHidden = new() { Content = "Hidden" };
        WorkViewElement.SetId(add, "plugins.header.add");
        WorkViewElement.SetId(folder, "plugins.header.folder");
        WorkViewElement.SetId(stateHidden, "plugins.detail.watches");
        WorkViewElement.SetStateVisible(add, true);
        WorkViewElement.SetStateVisible(folder, true);
        WorkViewElement.SetStateVisible(stateHidden, false);
        Button[] buttons = [add, folder, stateHidden];
        try
        {
            WorkViewMoreSurface.Entry[] barebones = WorkViewMoreSurface.ForButtons(buttons, WorkView.Barebones).ToArray();
            Assert.Equal(["Add", "Folder"], barebones.Select(entry => entry.Label()));
            Assert.Empty(WorkViewMoreSurface.ForButtons(buttons, WorkView.Advanced));
            Button more = new();
            WorkViewMoreSurface.Configure(more, barebones.Length);
            Assert.True(more.IsVisible);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(more)));
            // K01: the one drawn icon in the icon-only square button, with the localized tooltip.
            Assert.Equal(MeowChan.Desktop.Controls.WorkspaceIcons.More, MeowChan.Desktop.Controls.WorkspaceIcons.KeyOf(more.Content));
            Assert.Contains(MeowChan.Desktop.Controls.WorkspaceIcons.SquareClass, more.Classes);
            Assert.Equal(Texts.T("workview.more"), ToolTip.GetTip(more));
            WorkViewMoreSurface.Configure(more, 0);
            Assert.False(more.IsVisible);
        }
        finally
        {
            foreach (Button button in buttons) WorkViewElement.SetId(button, null);
        }
    }

    [Fact]
    public void EveryMoreButtonIsBuiltBySingleSourceAndTheAppMenuIsNamedAsTheAppMenu()
    {
        // K01: Media, Diagnostics and the three pages without a toolbar all go through WorkViewMoreSurface.Apply (drawn icon,
        // square button, localized tooltip); none builds its own "more" icon or tooltip.
        Assert.Contains("MakeSquare(more, WorkspaceIcons.More", ReadSource("Desktop/WorkViews/WorkViewMoreSurface.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("CommandBindings.Icon(\"plugins.actions\")", ReadSource("Desktop/WorkViews/WorkViewMoreSurface.cs"), StringComparison.Ordinal);
        Assert.Contains("WorkViewMoreSurface.Apply(", ReadSource("Desktop/Views/Media/MediaPage.WorkView.cs"), StringComparison.Ordinal);
        Assert.Contains("WorkViewMoreSurface.Apply(", ReadSource("Desktop/Views/Diagnostics/DiagnosticsPage.WorkView.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("WorkspaceIcons.MakeSquare(", ReadSource("Desktop/Views/Media/MediaPage.WorkView.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("WorkspaceIcons.MakeSquare(", ReadSource("Desktop/Views/Diagnostics/DiagnosticsPage.WorkView.cs"), StringComparison.Ordinal);
        // A24: the application menu's automation name is the app-menu command's label, not "Settings".
        string presentation = ReadSource("Desktop/Shell/MainWindow.Presentation.cs");
        Assert.Contains("SetName(WorkspaceMenuItem, CommandCatalog.Label(\"navigation.app-menu\"))", presentation, StringComparison.Ordinal);
        Assert.DoesNotContain("SetName(WorkspaceMenuItem, Texts.T(\"nav.settings\"))", presentation, StringComparison.Ordinal);
    }

    private static string ReadPageSources(WorkViewPage page)
    {
        string folder = Root("Desktop/Views/" + page);
        return string.Concat(Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".axaml", StringComparison.Ordinal))
            .Select(File.ReadAllText).Concat(Directory.EnumerateFiles(Root("Desktop/Shell"), "MainWindow.WorkViews.cs").Select(File.ReadAllText)));
    }

    private static string ReadSource(string relative) => File.ReadAllText(Root(relative));

    private static string Root(string path, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", path));
}

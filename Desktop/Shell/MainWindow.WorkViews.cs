using MeowChan.Desktop.WorkViews;

namespace MeowChan;

// Work views (plan AP5, WV0): the window's view state, its style classes and the shell's always-present elements.
// What each view shows is WorkViewPolicy; pages read Current/ConfiguredWorkView instead of comparing the setting string.
public partial class MainWindow
{
    private readonly WorkViewState _workViews = new();

    /// <summary>The view this window last applied; raises Changed when ApplyWorkspacePreferences applies another one.</summary>
    internal WorkViewState WorkViews => _workViews;

    /// <summary>The view of the saved setting right now, also while a change is still being applied.</summary>
    private WorkView ConfiguredWorkView => WorkViewSetting.Parse(_config.WorkspaceMode);

    private void InitializeWorkViews()
    {
        // Required in every view (R5/R8); registered so the work-views smoke can check that nothing is orphaned.
        WorkViewElement.SetId(UrlInput, "downloads.intake.url");
        WorkViewElement.SetId(WorkspaceModeComboBox, "shell.footer.view-switch");
        WorkViewElement.SetId(PauseButton, "shell.header.pause");
        WorkViewElement.SetId(WorkspaceMenuItem, "shell.header.app-menu");
        // Header elements with neutral rows: registered so the policy names every one of them. The graph's layout reads the
        // setting and the window width only, never a work view.
        WorkViewElement.SetId(WorkspaceSubtitle, "shell.header.tagline");
        WorkViewElement.SetId(TransferSpeed, "shell.header.graph");
        WorkViewElement.SetId(FullscreenButton, "shell.header.fullscreen");
        WorkViewElement.SetId(ThemeToggleButton, "shell.header.theme");
        InitializeWorkViewMenu();
        // R13 and the reading action follow a view change at once; the first view needs neither.
        _workViews.Changed += (_, view) =>
        {
            ApplyDownloadsSections(view, viewChanged: true);
            RefreshReadingResumePresentation();
        };
        _workViews.Changed += (_, view) => ApplyWorkViewToRulesSourcesPlugins(view, viewChanged: true);
        // Diagnostics and Statistics are built lazily: a built page follows every later change of the view.
        _workViews.Changed += (_, current) => ApplyWorkViewToLazyPages(current);
    }

    // Rules, Sources and Plugins are built after the first frame: a page applies the view when it appears and again on every change (R13).
    private void ApplyWorkViewToRulesSourcesPlugins(WorkView view, bool viewChanged)
    {
        _rulesPage?.ApplyWorkView(view, viewChanged);
        _sourcesPage?.ApplyWorkView(view, viewChanged);
        _pluginsPage?.ApplyWorkView(view, viewChanged);
    }

    // Diagnostics and Statistics (WV7). A page that does not exist yet is skipped: NavigateWorkspace applies the view when it is shown.
    private void ApplyWorkViewToLazyPages(WorkView view)
    {
        _diagnosticsPage?.ApplyWorkView(view);
        _statisticsPage?.ApplyWorkView(view);
    }

    // view-barebones|view-simple|view-advanced are for density only (margins, spacing), never for what is shown;
    // workspace-advanced stays as the historical alias of view-advanced.
    private void ApplyWorkViewClasses(WorkView view)
    {
        Classes.Set("workspace-advanced", view == WorkView.Advanced);
        foreach (WorkView candidate in WorkViewSetting.All)
            Classes.Set(candidate.StyleClass(), candidate == view);
        ApplyHeaderWorkView(view);
        RefreshWorkViewMenu();
    }
}

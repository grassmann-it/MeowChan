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
    }

    // view-barebones|view-simple|view-advanced are for density only (margins, spacing), never for what is shown;
    // workspace-advanced stays as the historical alias of view-advanced.
    private void ApplyWorkViewClasses(WorkView view)
    {
        Classes.Set("workspace-advanced", view == WorkView.Advanced);
        foreach (WorkView candidate in WorkViewSetting.All)
            Classes.Set(candidate.StyleClass(), candidate == view);
    }
}

using System.Threading.Tasks;
using MeowChan.Desktop.WorkViews;

namespace MeowChan;

/// <summary>WV12 Details host: the toast enters the existing Settings route and never owns/discards its draft.</summary>
public partial class MainWindow
{
    private Task OpenWorkViewOverviewAsync(DiffSummary diff)
    {
        // Keep the same readiness/busy protection as an ordinary Settings navigation. In particular, do not
        // reconstruct or cancel SettingsEditor here: its persistent instance owns the user's unsaved draft.
        if (_isClosing || !_restoreCompleted || WorkspaceCommandBusy) return Task.CompletedTask;
        if (!NavigateWorkspace(WorkspacePage.Settings)) return Task.CompletedTask;

        SettingsEditor editor = SettingsEditor;
        editor.ApplyWorkView(_workViews.Current, viewChanged: false);
        editor.ShowWorkViewOverview(diff);
        editor.FocusWorkViewOverview();
        return Task.CompletedTask;
    }
}

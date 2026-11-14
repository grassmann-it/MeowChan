using MeowChan.Desktop;
using MeowChan.Desktop.WorkViews;

namespace MeowChan;

// Work views (WorkViewPolicy.Tagging.cs): the page only applies the table. Progress, start, list and errors are the page in every
// view; the stamp set and the filter stay as tools (a filter other than "All entries" is a state, R6); Barebones drops the key hint;
// the toolbar moves Remove finished / Clear queue under its More as the view says. Row commands stay in the context menu and keys.
public sealed partial class TaggingPage
{
    private WorkView _workView = WorkView.Advanced;

    internal WorkView CurrentWorkView => _workView;

    private void InitializeWorkView()
    {
        WorkViewElement.SetId(_progress, "tagging.progress");
        WorkViewElement.SetId(_start, "tagging.start");
        WorkViewElement.SetId(_lastShown, "tagging.last-shown");
        WorkViewElement.SetId(_list, "tagging.list");
        WorkViewElement.SetId(_error, "tagging.error");
        WorkViewElement.SetId(_filterPanel, "tagging.filter");
        WorkViewElement.SetId(_help, "tagging.help");
        WorkViewElement.SetId(_keysHint, "tagging.keys-hint");
        WorkViewElement.SetId(_add, "tagging.toolbar.add");
        WorkViewElement.SetId(_clearDone, "tagging.toolbar.clear-done");
        WorkViewElement.SetId(_clear, "tagging.toolbar.clear");
    }

    /// <summary>Applies <paramref name="view"/> (when the page is built and whenever the window changes its view).</summary>
    internal void ApplyWorkView(WorkView view)
    {
        if (_queueWork.Ended) return;
        _workView = view;
        RefreshWorkView();
    }

    private void RefreshWorkView()
    {
        if (_queueWork.Ended) return;
        WorkViewPresenter.ApplyToolBar(_actions, _workView, nonDefaultState: item => ReferenceEquals(item, _filterPanel) && Filter != TaggingQueueFilter.All);
        ApplyToolbarIcons();
        ShowStampBlock();
        ApplyDensity();
        _actions.RefreshOverflowLabel();
        WorkViewGate.Apply(_progress, _workView, stateVisible: _queueWork.Queue.Count > 0);
        WorkViewGate.Apply(_lastShown, _workView, stateVisible: TaggingQueueView.LastShownIndex(_queueWork.Queue) != null);
        WorkViewGate.Apply(_error, _workView, stateVisible: !string.IsNullOrEmpty(_errorText.Text));
        WorkViewGate.Apply(_clearConfirm, _workView, stateVisible: _clearConfirm.IsVisible);
        WorkViewGate.Apply(_stampPanel, _workView);
        WorkViewGate.Apply(_keysHint, _workView, stateVisible: _rows.Count > 0);
    }
}

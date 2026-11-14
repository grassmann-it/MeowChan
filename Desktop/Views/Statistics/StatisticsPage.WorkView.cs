using MeowChan.Desktop.WorkViews;

namespace MeowChan;

// Work views (AP5/WV7): the page shows the same rows in every view (IoStatsDisplay); only Reset moves under More.
// Graph internals and the header graph options belong to another package and are not touched here.
public partial class StatisticsPage
{
    /// <summary>Places Reset as the view says. Called when the page is shown and when the window changes its view.</summary>
    internal void ApplyWorkView(WorkView view)
    {
        WorkViewPresenter.ApplyToolBar(StatisticsActions, view);
        StatisticsActions.RefreshOverflowLabel();
    }
}

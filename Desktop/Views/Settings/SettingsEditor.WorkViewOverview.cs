using Avalonia.Threading;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Views.Settings;
using MeowChan.Desktop.WorkViews;

namespace MeowChan;

public partial class SettingsEditor
{
    private WorkViewOverviewControl? _workViewOverview;
    public string WorkViewsTitle => WorkViewOverviewCatalog.T("settings.section.workViews");

    private WorkViewOverviewControl GetWorkViewOverview()
    {
        if (_workViewOverview != null) return _workViewOverview;
        // No DataGrid or policy projection until the category is explicitly opened.
        _workViewOverview = new WorkViewOverviewControl(CurrentWorkView);
        SettingsWorkViewOverviewHost.Content = _workViewOverview;
        return _workViewOverview;
    }

    private void EnsureWorkViewOverview() => GetWorkViewOverview().EnsureLoaded();

    /// <summary>Explicit page/text deep-link seam; ordinary category switches retain the user's filters.</summary>
    internal void ShowWorkViewOverview(WorkViewPage? page = null, string? text = null)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_isClosed || !IsEditable) return;
        WorkViewOverviewControl overview = GetWorkViewOverview();
        overview.SetActiveView(CurrentWorkView);
        overview.SetPageFilter(page);
        overview.SetTextFilter(text);
        SelectCategory("settings.workviews");
    }

    private void RefreshWorkViewOverviewTexts()
    {
        Raise(new[] { nameof(WorkViewsTitle) });
        // Texts subscribers (including registered controls on other pages) finish before the new name snapshot.
        Dispatcher.UIThread.Post(() =>
        {
            if (!_isClosed) _workViewOverview?.LocaleRefresh();
        }, DispatcherPriority.Background);
    }

    private void EndWorkViewOverviewLifetime()
    {
        _workViewOverview?.Dispose();
        _workViewOverview = null;
        SettingsWorkViewOverviewHost.Content = null;
    }
}

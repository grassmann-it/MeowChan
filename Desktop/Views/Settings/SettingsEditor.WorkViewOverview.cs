using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

    /// <summary>Explicit page/text deep-link seam; a normal entry clears any previous Details restriction first.</summary>
    internal void ShowWorkViewOverview(WorkViewPage? page = null, string? text = null)
    {
        ShowWorkViewOverviewCore(null, page, text);
    }

    /// <summary>WV12 Details entry: preserves the editor draft and restricts the existing overview to this diff.</summary>
    internal void ShowWorkViewOverview(DiffSummary diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ShowWorkViewOverviewCore(diff, null, null);
    }

    private void ShowWorkViewOverviewCore(DiffSummary? diff, WorkViewPage? page, string? text)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_isClosed || !IsEditable) return;
        // Navigate first. SelectCategory deliberately resets an old Details/filter visit; the requested
        // destination is applied afterwards so an explicit Details route is not mistaken for a normal entry.
        SelectCategory("settings.workviews");
        WorkViewOverviewControl overview = GetWorkViewOverview();
        overview.EnsureLoaded();
        overview.SetActiveView(CurrentWorkView);
        if (diff != null)
            overview.SetDifferenceFilter(diff);
        else
        {
            overview.SetDifferenceFilter(null);
            overview.SetPageFilter(page);
            overview.SetTextFilter(text);
        }
    }

    /// <summary>Ensures the lazy overview exists and gives Details/normal entry a keyboard target.</summary>
    internal void FocusWorkViewOverview()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_isClosed || !IsEditable) return;
        WorkViewOverviewControl overview = GetWorkViewOverview();
        overview.EnsureLoaded();
        void FocusTarget()
        {
            if (_isClosed || !IsEffectivelyVisible) return;
            Control target = overview.GetVisualDescendants().OfType<Control>().FirstOrDefault(control => control.Name == "WorkViewOverviewTextFilter")
                ?? overview.GetVisualDescendants().OfType<Control>().FirstOrDefault(control => control.Name == "WorkViewOverviewTable")
                ?? overview;
            target.Focus(NavigationMethod.Tab);
        }
        FocusTarget();
        Dispatcher.UIThread.Post(FocusTarget, DispatcherPriority.Loaded);
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

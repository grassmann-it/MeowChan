using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Desktop.WorkViews;

namespace MeowChan;

// Work views (plan AP5, WV6, §1.9): capability cards fold in Barebones; Source rules, Diagnostics and Plugin settings move
// under More as the view asks. The source list, its state, the consent, the observed threads and every error stay in all views.
public partial class SourcesPage
{
    // A page without a main window (fixtures, tests) shows everything, as before work views; the window sets its own view.
    private WorkView _workView = WorkView.Advanced;

    internal WorkView WorkView => _workView;

    internal void ApplyWorkView(WorkView view, bool viewChanged)
    {
        if (_ended) return;
        _workView = view;
        WorkViewPresenter.ApplyExpander(CapabilitiesExpander, view, viewChanged);
        WorkViewPresenter.ApplyExpander(AdapterDetails, view, viewChanged);
        RefreshWorkViewPresentation();
    }

    // Called with every render of the details, so the buttons follow the view whatever the state did to them.
    private void RefreshWorkViewPresentation()
    {
        Button[] moved = MovableActions();
        foreach (Button button in moved) WorkViewGate.Apply(button, _workView);
        WorkViewMoreSurface.Configure(SourcesMoreButton, WorkViewMoreSurface.ForButtons(moved, _workView).Count());
    }

    private Button[] MovableActions() => [SourceRulesButton, SourceDiagnosticsButton, SourcePluginButton];

    private void OnSourcesMoreClick(object? sender, RoutedEventArgs e) =>
        WorkViewMoreSurface.Open(SourcesMoreButton, WorkViewMoreSurface.ForButtons(MovableActions(), _workView));
}

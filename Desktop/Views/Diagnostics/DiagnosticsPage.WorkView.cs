using System;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan;

// Work views (AP5/WV7): what the view changes on this page is the filter group (closed in Barebones) and where "retry filtered"
// and "copy details" sit (under More). Rows, errors and the filters' default values are the same in every view; the policy
// decides the rest, and a button the view moved keeps its command, its disabled state and its shortcut.
public partial class DiagnosticsPage
{
    // Until a window applies a view the page shows everything (Advanced), so a page used on its own is complete.
    private WorkView _workView = WorkView.Advanced;
    private bool _workViewApplied;
    private readonly ContextMenu _moreMenu = new();

    private Expander FiltersSection => this.FindControl<Expander>("DiagnosticFiltersSection")!;
    private Button MoreButton => this.FindControl<Button>("DiagnosticMoreButton")!;
    private Button[] ReducibleActions => new[] { "RetryDiagnosticFilteredButton", "CopyDiagnosticButton" }
        .Select(name => this.FindControl<Button>(name)!).ToArray();

    /// <summary>Applies <paramref name="view"/>: on entering a view the groups take their default, afterwards the user's choice stays.</summary>
    internal void ApplyWorkView(WorkView view)
    {
        bool changed = !_workViewApplied || view != _workView;
        _workView = view;
        _workViewApplied = true;
        ApplyWorkViewPresence(changed);
    }

    // R6: a filter that differs from its default is shown whatever the view says. Value comparison, not a view test.
    private int ActiveDiagnosticFilterCount =>
        (_diagnosticSourceId != null ? 1 : 0)
        + (string.IsNullOrWhiteSpace(this.FindControl<TextBox>("DiagnosticTextFilter")!.Text) ? 0 : 1)
        + (this.FindControl<CheckBox>("DiagnosticProblemsOnly")!.IsChecked == true ? 1 : 0)
        + (this.FindControl<CheckBox>("DiagnosticIncludeWaiting")!.IsChecked == true ? 1 : 0);

    private void ApplyWorkViewPresence(bool viewChanged)
    {
        WorkViewPresenter.ApplyExpander(FiltersSection, _workView, viewChanged, ActiveDiagnosticFilterCount > 0);
        WorkViewPresenter.ApplyExpander(this.FindControl<Expander>("SessionFeedbackSection")!, _workView, viewChanged);
        // Free buttons: the gate hides what the view moved, the More menu lists it.
        foreach (Button button in ReducibleActions) WorkViewGate.Apply(button, _workView);
        RefreshMoreButton();
        RefreshWorkViewTexts();
    }

    private int MoreCount => ReducibleActions.Count(button => WorkViewElement.GetId(button) is { } id
        && WorkViewPolicy.Resolve(id, _workView) == Presence.Overflow);

    // R7: the button announces how many actions it holds; it is there only when the view moved something.
    private void RefreshMoreButton()
    {
        Button more = MoreButton;
        int count = MoreCount;
        more.IsVisible = count > 0;
        string name = Texts.Count("workview.moreCount", count);
        AutomationProperties.SetName(more, name);
        ToolTip.SetTip(more, Texts.T("workview.more"));
    }

    // The filter group's header carries how many filters are set, so a closed group never hides one.
    private void RefreshWorkViewTexts()
    {
        int active = ActiveDiagnosticFilterCount;
        FiltersSection.Header = Texts.T("media.filters") + (active > 0 ? " · " + Texts.T("workview.activeCount", active) : string.Empty);
        RefreshMoreButton();
    }

    // The entries are built when the menu opens, so their enabled state and counts are the buttons' current ones.
    private void OnDiagnosticMoreClick(object? sender, RoutedEventArgs e)
    {
        if (_ended) return;
        string Label(Button button) => AutomationProperties.GetName(button) is { Length: > 0 } name ? name
            : WorkViewElement.GetId(button) is { } id && WorkViewPolicy.Get(id).CommandId is { } command ? CommandCatalog.Label(command) : string.Empty;
        if (WorkViewOverflow.Populate(_moreMenu, ReducibleActions, _workView, Label) > 0) _moreMenu.Open(MoreButton);
    }

    // A filter edit may make the group non-default (open it) or default again (the user's own choice stays).
    private void OnDiagnosticFilterStateChanged()
    {
        if (_ended) return;
        if (_workViewApplied) ApplyWorkViewPresence(viewChanged: false); else RefreshWorkViewTexts();
    }
}

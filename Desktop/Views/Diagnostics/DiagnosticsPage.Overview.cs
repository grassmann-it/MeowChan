using System;
using System.Linq;
using Avalonia.Controls;

namespace MeowChan;

// F14: the table drops the Category column while every row shares one category (it only repeated "Request"), and the detail pane
// shows an overview (rows per category and message) while nothing is selected instead of an empty prompt.
public partial class DiagnosticsPage
{
    private void RefreshDiagnosticOverview(bool hasSelection)
    {
        DataGrid grid = this.FindControl<DataGrid>("DiagnosticsGrid")!;
        grid.Columns[0].IsVisible = _visibleDiagnostics.Select(entry => entry.Category).Distinct(StringComparer.Ordinal).Count() > 1;
        Expander section = this.FindControl<Expander>("DiagnosticOverviewSection")!;
        TextBlock body = this.FindControl<TextBlock>("DiagnosticOverview")!;
        bool show = !hasSelection && _visibleDiagnostics.Count > 0;
        section.IsVisible = body.IsVisible = show;
        if (!show) return;
        body.Text = string.Join(Environment.NewLine, _visibleDiagnostics
            .GroupBy(entry => (entry.Category, entry.Summary))
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key.Category, StringComparer.CurrentCulture)
            .Select(group => group.Key.Category + " · " + group.Key.Summary + " × " + group.Count()));
    }
}

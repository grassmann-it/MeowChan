using Avalonia.Controls;

namespace MeowChan;

// Status page (SM2): a deep link into the diagnostics segment can preset the source filter (Status › Diagnostics for one
// source). The filter is the page's ordinary source choice: the user changes or clears it as usual, and a set filter opens
// the closed filter group (R6) like any other active filter.
public partial class DiagnosticsPage
{
    /// <summary>The source id the source filter is set to; null for all sources.</summary>
    internal string? SourceFilter => _diagnosticSourceId;

    /// <summary>Sets the source filter to <paramref name="sourceId"/> (null: all sources). A source without current entries
    /// is still offered, so the choice stays visible and the empty table says why.</summary>
    internal void FilterSource(string? sourceId)
    {
        if (_ended || _diagnosticSourceId == sourceId) return;
        _diagnosticSourceId = sourceId;
        RefreshDiagnosticSources();
        // The choices may be unchanged (the source already had entries): select it in the box as well, without the change event.
        ComboBox sources = this.FindControl<ComboBox>("DiagnosticSourceFilter")!;
        _refreshingDiagnosticFilters = true;
        try
        {
            foreach (object? item in sources.ItemsSource ?? System.Array.Empty<object>())
                if (item is DiagnosticSourceChoice choice && choice.SourceId == sourceId) { sources.SelectedItem = choice; break; }
        }
        finally { _refreshingDiagnosticFilters = false; }
        ApplyDiagnosticFilters();
        RefreshSelection();
        OnDiagnosticFilterStateChanged();
    }
}

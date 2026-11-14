using Avalonia.Controls;

namespace MeowChan;

// Status page (SM4): the error count of the statistics table leads here with "Only problems" set. A separate file keeps the
// page's other files free of this one setter.
public partial class DiagnosticsPage
{
    /// <summary>Whether the "Only problems" filter is on.</summary>
    internal bool ProblemsOnly => this.FindControl<CheckBox>("DiagnosticProblemsOnly")!.IsChecked == true;

    /// <summary>Sets the "Only problems" filter like the user's click: the table, the selection and the filter count follow.</summary>
    internal void SetProblemsOnly(bool value)
    {
        if (_ended) return;
        this.FindControl<CheckBox>("DiagnosticProblemsOnly")!.IsChecked = value;
    }
}

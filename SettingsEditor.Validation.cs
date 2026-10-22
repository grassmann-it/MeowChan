using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class SettingsEditor
{
    private Control? _firstInvalidInput;
    private readonly Dictionary<string, string> _fieldHelpErrors = new();
    private string FieldHelp(string field, string baseline) => _fieldHelpErrors.TryGetValue(field, out string? error) ? error : baseline;
    public string DownloadPathHelp => FieldHelp(nameof(DownloadPathInput), PathHint);
    public string RecheckTimerHelp => FieldHelp(nameof(RecheckTimerInput), SecondsHelp);
    public string BandwidthInputHelp => FieldHelp(nameof(BandwidthInput), BandwidthHelp);
    public string ParallelDownloadsHelp => FieldHelp(nameof(ParallelDownloadsInput), Texts.T("settings.unitCount"));
    public string ReserveInputHelp => FieldHelp(nameof(ReserveInput), Texts.T("settings.unitGiB") + ". " + ReserveHint);
    public string ProductionWorkersHelp => FieldHelp(nameof(ProductionWorkersInput), Texts.T("settings.unitCount") + ". " + BudgetRestartHint);
    public string WorkerIdleMinutesHelp => FieldHelp(nameof(WorkerIdleMinutesInput), Texts.T("settings.unitMinutes") + ". " + BudgetRestartHint);
    public string SourceHeapMiBHelp => FieldHelp(nameof(SourceHeapMiBInput), Texts.T("settings.unitMiB") + ". " + BudgetRestartHint);
    public string FilterHeapMiBHelp => FieldHelp(nameof(FilterHeapMiBInput), Texts.T("settings.unitMiB") + ". " + BudgetRestartHint);

    private void NotifyFieldHelpChanged()
    {
        foreach (string property in new[] { nameof(DownloadPathHelp), nameof(RecheckTimerHelp), nameof(BandwidthInputHelp),
            nameof(ParallelDownloadsHelp), nameof(ReserveInputHelp), nameof(ProductionWorkersHelp),
            nameof(WorkerIdleMinutesHelp), nameof(SourceHeapMiBHelp), nameof(FilterHeapMiBHelp) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    private void SetFieldErrorHelp(Control input, string message)
    {
        _fieldHelpErrors[input.Name!] = message;
        NotifyFieldHelpChanged();
    }
    private string? _errorDetails;
    public string ErrorDetailsLabel => Texts.T("settings.errorDetails");
    public string? ErrorDetails
    {
        get => _errorDetails;
        private set
        {
            SetField(ref _errorDetails, value, nameof(ErrorDetails));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasErrorDetails)));
        }
    }
    public bool HasErrorDetails => !string.IsNullOrEmpty(ErrorDetails);

    private IEnumerable<(NumericUpDown Input, decimal? Value, string Label, string Unit, decimal Min, decimal Max, bool Integer)> NumericFields()
    {
        yield return (RecheckTimerInput, RecheckTimer, RecheckLabel, SecondsHelp, 10, 4294967, true);
        yield return (BandwidthInput, BandwidthLimiterMB, BandwidthLabel, BandwidthHelp, 0, long.MaxValue / (1024m * 1024m), false);
        yield return (ParallelDownloadsInput, MaxParallelDownloads, ParallelLabel, Texts.T("settings.unitCount"), 1, int.MaxValue, true);
        yield return (ReserveInput, MinimumFreeSpaceGiB, ReserveLabel, Texts.T("settings.unitGiB"), 0, int.MaxValue, true);
        yield return (ProductionWorkersInput, ProductionWorkers, ProductionWorkersLabel, Texts.T("settings.unitCount"), 1, 8, true);
        yield return (WorkerIdleMinutesInput, WorkerIdleMinutes, WorkerIdleMinutesLabel, Texts.T("settings.unitMinutes"), 1, 30, true);
        yield return (SourceHeapMiBInput, SourceHeapMiB, SourceHeapMiBLabel, Texts.T("settings.unitMiB"), 128, 1024, true);
        yield return (FilterHeapMiBInput, FilterHeapMiB, FilterHeapMiBLabel, Texts.T("settings.unitMiB"), 256, 2048, true);
    }

    private void ClearFieldErrors()
    {
        _firstInvalidInput = null;
        ErrorDetails = null;
        SettingsErrorDetails.IsExpanded = false;
        DownloadPathInputError.IsVisible = false;
        DownloadPathInputError.Text = null;
        _fieldHelpErrors.Clear();
        foreach (var field in NumericFields())
        {
            TextBlock error = this.FindControl<TextBlock>(field.Input.Name + "Error")!;
            error.IsVisible = false;
            error.Text = null;

        }
        NotifyFieldHelpChanged();
    }

    private static string FieldLabelWithUnit(string label, string unit)
    {
        string suffix = "(" + unit + ")";
        // Help can spell out a unit and also provide its abbreviation, e.g. MiB/s.
        int abbreviation = unit.LastIndexOf('(');
        if (label.Contains(suffix, StringComparison.OrdinalIgnoreCase) ||
            (abbreviation >= 0 && label.Contains(unit[abbreviation..], StringComparison.OrdinalIgnoreCase)))
            return label;
        return label + " " + suffix;
    }

    // Parse validation runs before the existing shared Config validation/commit path.
    // Never replace malformed text with a previous NumericUpDown.Value.
    private bool ValidateNumericDraft()
    {
        foreach (var field in NumericFields())
        {
            string? message = null;
            bool parsed = decimal.TryParse(field.Input.Text, field.Input.ParsingNumberStyle, field.Input.NumberFormat, out decimal raw);
            bool InvalidNumber(decimal? value) => !value.HasValue || value < field.Min || value > field.Max ||
                (field.Integer && decimal.Truncate(value.Value) != value.Value) || (!field.Integer && value <= 0);
            if (!parsed || DataValidationErrors.GetHasErrors(field.Input))
                message = Texts.T("settings.fieldNumber", FieldLabelWithUnit(field.Label, field.Unit));
            else if (InvalidNumber(raw) || InvalidNumber(field.Value))
                message = field.Integer
                    ? Texts.T("settings.fieldInteger", FieldLabelWithUnit(field.Label, field.Unit), field.Min, field.Max)
                    : Texts.T("settings.bandwidthRange", field.Label, decimal.Floor(field.Max));
            if (message == null) continue;
            TextBlock error = this.FindControl<TextBlock>(field.Input.Name + "Error")!;
            error.Text = message;
            error.IsVisible = true;
            SetFieldErrorHelp(field.Input, message);
            _firstInvalidInput ??= field.Input;
        }
        return FinishFieldValidation();
    }

    private bool FinishFieldValidation()
    {
        if (_firstInvalidInput == null) return true;
        ErrorMessage = Texts.T("settings.notSaved");
        StatusMessage = Texts.T("settings.unsaved");
        Control first = _firstInvalidInput;
        Dispatcher.UIThread.Post(() =>
        {
            if (_isClosed || _firstInvalidInput != first) return;
            // Expanding the existing advanced group keeps its field reachable.
            for (var parent = first.Parent; parent != null; parent = parent.Parent)
                if (parent is Expander expander) expander.IsExpanded = true;
            (first.Parent as Control ?? first).BringIntoView();
            // Focusing NumericUpDown itself redirects to PART_TextBox and causes a
            // root->inner LostFocus commit. Focus the actual editing surface directly.
            if (first is NumericUpDown numeric)
                numeric.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(text => text.Name == "PART_TextBox")?.Focus();
            else first.Focus();
        }, DispatcherPriority.Loaded);
        return false;
    }
}

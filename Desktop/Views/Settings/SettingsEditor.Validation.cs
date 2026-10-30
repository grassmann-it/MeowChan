using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Desktop;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class SettingsEditor
{
    internal DownloadDestinationValidator DestinationValidator { get; set; } = DownloadDestinationValidator.Shared;
    private long _draftRevision;
    private CancellationTokenSource? _destinationWait;
    private DownloadDestinationStatus? _destinationStatus;
    private bool _isCheckingDestination;
    public string CheckDestinationLabel => Texts.T("destination.check");
    public string CancelDestinationCheckLabel => Texts.T("destination.cancel");
    public string DestinationPolicyHint => Texts.T("destination.policy");
    public string DestinationScopeHint => Texts.T("destination.scope");
    public string NormalizedDestination
    {
        get
        {
            DownloadDestinationResult result = DownloadDestinationValidator.Normalize(DownloadPath, _config.DownloadPathBaseDirectory);
            return result.NormalizedPath == null ? Texts.T("destination.noPreview")
                : Texts.T("destination.normalized", result.NormalizedPath);
        }
    }
    public bool IsCheckingDestination => _isCheckingDestination;
    public string DestinationStatusText => Texts.T("destination." + (_destinationStatus?.ToString() ?? "NotChecked"));
    public string DestinationCleanupWarning => DownloadDestinationValidator.Shared.HasCleanupFailure ? Texts.T("destination.cleanupWarning", DownloadDestinationValidator.Shared.LastCleanupFailurePath ?? Texts.T("destination.unknownProbe")) : string.Empty;

    private void OnDestinationCleanupFailed() => Dispatcher.UIThread.Post(() =>
    {
        if (!_isClosed) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DestinationCleanupWarning)));
    });

    private bool ValidateDestinationSyntax(out string? normalizedPath)
    {
        DownloadDestinationResult syntax = DownloadDestinationValidator.Normalize(DownloadPath, _config.DownloadPathBaseDirectory);
        normalizedPath = syntax.NormalizedPath;
        if (normalizedPath != null) return true;
        ShowDestinationError(Texts.T("settings.pathInvalid"));
        return false;
    }

    private void ShowDestinationError(string message)
    {
        DownloadPathInputError.Text = message;
        DownloadPathInputError.IsVisible = true;
        SetFieldErrorHelp(DownloadPathInput, message);
        _firstInvalidInput = DownloadPathInput;
        FinishFieldValidation();
    }

    private bool DestinationNeedsCheck(Config candidate)
    {
        string? previous = DownloadDestinationValidator.Normalize(_config.DownloadPath, _config.DownloadPathBaseDirectory).NormalizedPath;
        return !string.Equals(previous, candidate.DownloadPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || candidate.MinimumFreeSpaceGiB != _config.MinimumFreeSpaceGiB;
    }

    private async Task<bool> CheckDestinationAsync(string path, int reserveGiB)
    {
        long revision = _draftRevision;
        using var wait = new CancellationTokenSource();
        _destinationWait = wait;
        _isCheckingDestination = true;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCheckingDestination)));
        try
        {
            DownloadDestinationResult result = await DestinationValidator.CheckAsync(path,
                _config.DownloadPathBaseDirectory, checked((long)reserveGiB * (1L << 30)), TimeSpan.FromSeconds(5), wait.Token);
            if (_isClosed || !IsAvailable || revision != _draftRevision) return false;
            _destinationStatus = result.Status;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DestinationStatusText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DestinationCleanupWarning)));
            if (!result.CanApply) ShowDestinationError(DestinationStatusText);
            return result.CanApply;
        }
        finally
        {
            _destinationWait = null;
            _isCheckingDestination = false;
            if (!_isClosed) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCheckingDestination)));
        }
    }

    private async void OnCheckDestinationClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (IsBusy || !IsAvailable || _isClosed) return;
        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            if (!ValidateDestinationSyntax(out string? path) || !ValidateNumericDraft()) return;
            await CheckDestinationAsync(path!, GetInteger(MinimumFreeSpaceGiB, ReserveLabel, 0, int.MaxValue));
        }
        finally { IsBusy = false; }
    }

    private void OnCancelDestinationCheckClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _destinationWait?.Cancel();

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
            if (!_isClosed)
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
    private bool ValidateNumericDraft(bool focusError = true)
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
        return FinishFieldValidation(focusError);
    }

    private bool FinishFieldValidation(bool focusError = true)
    {
        if (_firstInvalidInput == null) return true;
        ErrorMessage = Texts.T("settings.notSaved");
        StatusMessage = Texts.T("settings.unsaved");
        if (!focusError) return false;
        Control first = _firstInvalidInput;
        SelectCategoryContaining(first);
        Dispatcher.UIThread.Post(() =>
        {
            if (_isClosed || _firstInvalidInput != first) return;
            // A collapsed group can defer initialization/styling until this expansion.
            // NumericUpDown's Initialized/format handlers force Value->Text, so retain
            // the rejected draft across that first materialization before focusing it.
            NumericUpDown? numeric = first as NumericUpDown;
            string? rawDraft = numeric?.Text;
            for (var parent = first.Parent; parent != null; parent = parent.Parent)
                if (parent is Expander expander) expander.IsExpanded = true;
            TopLevel.GetTopLevel(first)?.UpdateLayout();
            numeric?.ApplyTemplate();
            if (numeric != null && numeric.Text != rawDraft)
                numeric.SetCurrentValue(NumericUpDown.TextProperty, rawDraft);
            (first.Parent as Control ?? first).BringIntoView();
            // Avoid the artificial root->PART_TextBox LostFocus commit.
            if (numeric != null)
                numeric.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(text => text.Name == "PART_TextBox")?.Focus();
            else first.Focus();
        }, DispatcherPriority.Loaded);
        return false;
    }
}

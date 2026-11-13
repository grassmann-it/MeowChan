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
        ShowDestinationError("settings.pathInvalid");
        return false;
    }

    private void ShowDestinationError(string key, params object?[] arguments)
    {
        SetDownloadPathError(key, arguments);
        _firstInvalidInput = DownloadPathInput;
        FinishFieldValidation();
    }

    private void SetDownloadPathError(string key, params object?[] arguments)
        => SetDownloadPathError(new LocalizedMessage(key, arguments));

    private void SetDownloadPathError(LocalizedMessage message)
    {
        string rendered = message.Render();
        DownloadPathInputError.Text = rendered;
        DownloadPathInputError.IsVisible = true;
        SetFieldErrorHelp(DownloadPathInput, message.Render);
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
            if (!result.CanApply) ShowDestinationError("destination." + result.Status);
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
        SetErrorMessage(null);
        SetStatusMessage(null);
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
    private readonly Dictionary<string, Func<string>> _fieldHelpErrors = new();
    private string FieldHelp(string field, string baseline) => _fieldHelpErrors.TryGetValue(field, out Func<string>? error) ? error() : baseline;
    public string DownloadPathHelp => FieldHelp(nameof(DownloadPathInput), PathHint);
    public string RecheckTimerHelp => FieldHelp(nameof(RecheckTimerInput), SecondsHelp);
    public string BandwidthInputHelp => FieldHelp(nameof(BandwidthInput), BandwidthHelp);
    public string ParallelDownloadsHelp => FieldHelp(nameof(ParallelDownloadsInput), Texts.T("settings.unitCount"));
    public string ParallelDownloadsSafetyHelp => FieldHelp(nameof(ParallelDownloadsSafetyInput), ParallelSafetyHint);
    public string ReserveInputHelp => FieldHelp(nameof(ReserveInput), Texts.T("settings.unitGiB") + ". " + ReserveHint);
    public string ProductionWorkersHelp => FieldHelp(nameof(ProductionWorkersInput), Texts.T("settings.unitCount") + ". " + BudgetRestartHint);
    public string WorkerIdleMinutesHelp => FieldHelp(nameof(WorkerIdleMinutesInput), Texts.T("settings.unitMinutes") + ". " + BudgetRestartHint);
    public string SourceHeapMiBHelp => FieldHelp(nameof(SourceHeapMiBInput), Texts.T("settings.unitMiB") + ". " + BudgetRestartHint);
    public string FilterHeapMiBHelp => FieldHelp(nameof(FilterHeapMiBInput), Texts.T("settings.unitMiB") + ". " + BudgetRestartHint);
    public string BooruCatalogMiBHelp => FieldHelp(nameof(BooruCatalogMiBInput), Texts.T("settings.unitMiB") + ". " + BooruCatalogMiBHint);
    public string PrivateTagLimitHelp => FieldHelp(nameof(PrivateTagLimitInput), Texts.T("settings.unitCount") + ". " + PrivateTagLimitHint);
    public string PrivateTagFileMiBHelp => FieldHelp(nameof(PrivateTagFileMiBInput), Texts.T("settings.unitMiB") + ". " + PrivateTagFileMiBHint);
    public string MediaIndexCacheMiBHelp => FieldHelp(nameof(MediaIndexCacheMiBInput), Texts.T("settings.unitMiB") + ". " + MediaIndexCacheMiBHint);
    public string FavoriteEntryLimitHelp => FieldHelp(nameof(FavoriteEntryLimitInput), Texts.T("settings.unitCount") + ". " + FavoriteEntryLimitHint);
    public string FavoriteFileLimitMiBHelp => FieldHelp(nameof(FavoriteFileLimitMiBInput), Texts.T("settings.unitMiB") + ". " + FavoriteFileLimitMiBHint);
    public string PostBookmarkLimitHelp => FieldHelp(nameof(PostBookmarkLimitInput), Texts.T("settings.unitCount") + ". " + PostBookmarkLimitHint);
    public string SavedSearchLimitHelp => FieldHelp(nameof(SavedSearchLimitInput), Texts.T("settings.unitCount") + ". " + SavedSearchLimitHint);
    public string ReaderGroupLimitHelp => FieldHelp(nameof(ReaderGroupLimitInput), Texts.T("settings.unitCount") + ". " + ReaderGroupLimitHint);
    public string ExportConfirmFilesHelp => FieldHelp(nameof(ExportConfirmFilesInput), Texts.T("settings.unitCount") + ". " + ExportConfirmFilesHint);
    public string ExportConfirmGiBHelp => FieldHelp(nameof(ExportConfirmGiBInput), Texts.T("settings.unitGiB") + ". " + ExportConfirmGiBHint);
    public string PosterCacheLimitMiBHelp => FieldHelp(nameof(PosterCacheLimitMiBInput), Texts.T("settings.unitMiB") + ". " + PosterCacheLimitMiBHint);
    public string PosterCacheFileLimitHelp => FieldHelp(nameof(PosterCacheFileLimitInput), Texts.T("settings.unitCount") + ". " + PosterCacheFileLimitHint);
    public string ImportMaxFilesPerBatchHelp => FieldHelp(nameof(ImportMaxFilesPerBatchInput), Texts.T("settings.unitCount") + ". " + ImportMaxFilesPerBatchHint);
    public string ImportMaxBatchGiBHelp => FieldHelp(nameof(ImportMaxBatchGiBInput), Texts.T("settings.unitGiB") + ". " + ImportMaxBatchGiBHint);
    public string ImportMaxFileMiBHelp => FieldHelp(nameof(ImportMaxFileMiBInput), Texts.T("settings.unitMiB") + ". " + ImportMaxFileMiBHint);
    public string ImportMaxTotalFilesHelp => FieldHelp(nameof(ImportMaxTotalFilesInput), Texts.T("settings.unitCount") + ". " + ImportMaxTotalFilesHint);
    public string ImportMaxTagsPerBatchHelp => FieldHelp(nameof(ImportMaxTagsPerBatchInput), Texts.T("settings.unitCount") + ". " + ImportMaxTagsPerBatchHint);
    public string ImportMaxBatchesHelp => FieldHelp(nameof(ImportMaxBatchesInput), Texts.T("settings.unitCount") + ". " + ImportMaxBatchesHint);
    public string MediaIndexMaxEntriesHelp => FieldHelp(nameof(MediaIndexMaxEntriesInput), Texts.T("settings.unitCount") + ". " + MediaIndexMaxEntriesHint);
    public string MediaIndexMaxThreadsHelp => FieldHelp(nameof(MediaIndexMaxThreadsInput), Texts.T("settings.unitCount") + ". " + MediaIndexMaxThreadsHint);
    public string ImportCatalogLimitMiBHelp => FieldHelp(nameof(ImportCatalogLimitMiBInput), Texts.T("settings.unitMiB") + ". " + ImportCatalogLimitMiBHint);

    private void NotifyFieldHelpChanged()
    {
        foreach (string property in new[] { nameof(DownloadPathHelp), nameof(RecheckTimerHelp), nameof(BandwidthInputHelp),
            nameof(ParallelDownloadsHelp), nameof(ParallelDownloadsSafetyHelp), nameof(ReserveInputHelp), nameof(ProductionWorkersHelp),
            nameof(WorkerIdleMinutesHelp), nameof(SourceHeapMiBHelp), nameof(FilterHeapMiBHelp), nameof(BooruCatalogMiBHelp),
            nameof(PrivateTagLimitHelp), nameof(PrivateTagFileMiBHelp), nameof(MediaIndexCacheMiBHelp),
            nameof(FavoriteEntryLimitHelp), nameof(FavoriteFileLimitMiBHelp), nameof(PostBookmarkLimitHelp), nameof(SavedSearchLimitHelp), nameof(ReaderGroupLimitHelp), nameof(ExportConfirmFilesHelp), nameof(ExportConfirmGiBHelp), nameof(PosterCacheLimitMiBHelp), nameof(PosterCacheFileLimitHelp), nameof(ImportMaxFilesPerBatchHelp), nameof(ImportMaxBatchGiBHelp), nameof(ImportMaxFileMiBHelp), nameof(ImportMaxTotalFilesHelp), nameof(ImportCatalogLimitMiBHelp), nameof(ImportMaxTagsPerBatchHelp), nameof(ImportMaxBatchesHelp), nameof(MediaIndexMaxEntriesHelp), nameof(MediaIndexMaxThreadsHelp) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    private void SetFieldErrorHelp(Control input, Func<string> message)
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
        yield return (ParallelDownloadsSafetyInput, ParallelDownloadsSafetyLimit, ParallelSafetyLabel, Texts.T("settings.unitCount"), 1, Downloader.DownloadQueue.MaximumParallelDownloadsSafetyLimit, true);
        yield return (ReserveInput, MinimumFreeSpaceGiB, ReserveLabel, Texts.T("settings.unitGiB"), 0, int.MaxValue, true);
        yield return (ProductionWorkersInput, ProductionWorkers, ProductionWorkersLabel, Texts.T("settings.unitCount"), 1, 8, true);
        yield return (WorkerIdleMinutesInput, WorkerIdleMinutes, WorkerIdleMinutesLabel, Texts.T("settings.unitMinutes"), 1, 30, true);
        yield return (SourceHeapMiBInput, SourceHeapMiB, SourceHeapMiBLabel, Texts.T("settings.unitMiB"), 128, 1024, true);
        yield return (FilterHeapMiBInput, FilterHeapMiB, FilterHeapMiBLabel, Texts.T("settings.unitMiB"), 256, 2048, true);
        yield return (BooruCatalogMiBInput, BooruCatalogMiB, BooruCatalogMiBLabel, Texts.T("settings.unitMiB"),
            Config.BooruCatalogMinimumLimitMiB, Config.BooruCatalogMaximumLimitMiB, true);
        yield return (PrivateTagLimitInput, PrivateTagLimit, PrivateTagLimitLabel, Texts.T("settings.unitCount"),
            Config.PrivateTagMinimumLimit, Config.PrivateTagMaximumLimit, true);
        yield return (PrivateTagFileMiBInput, PrivateTagFileMiB, PrivateTagFileMiBLabel, Texts.T("settings.unitMiB"),
            Config.PrivateTagFileMinimumLimitMiB, Config.PrivateTagFileMaximumLimitMiB, true);
        yield return (MediaIndexCacheMiBInput, MediaIndexCacheMiB, MediaIndexCacheMiBLabel, Texts.T("settings.unitMiB"),
            Config.MediaIndexCacheMinimumLimitMiB, Config.MediaIndexCacheMaximumLimitMiB, true);
        yield return (FavoriteEntryLimitInput, FavoriteEntryLimit, FavoriteEntryLimitLabel, Texts.T("settings.unitCount"),
            Config.FavoriteEntryLimitMinimum, Config.FavoriteEntryLimitMaximum, true);
        yield return (FavoriteFileLimitMiBInput, FavoriteFileLimitMiB, FavoriteFileLimitMiBLabel, Texts.T("settings.unitMiB"),
            Config.FavoriteFileLimitMiBMinimum, Config.FavoriteFileLimitMiBMaximum, true);
        yield return (PostBookmarkLimitInput, PostBookmarkLimit, PostBookmarkLimitLabel, Texts.T("settings.unitCount"),
            Config.PostBookmarkLimitMinimum, Config.PostBookmarkLimitMaximum, true);
        yield return (SavedSearchLimitInput, SavedSearchLimit, SavedSearchLimitLabel, Texts.T("settings.unitCount"),
            Config.SavedSearchLimitMinimum, Config.SavedSearchLimitMaximum, true);
        yield return (ReaderGroupLimitInput, ReaderGroupLimit, ReaderGroupLimitLabel, Texts.T("settings.unitCount"),
            Config.ReaderGroupLimitMinimum, Config.ReaderGroupLimitMaximum, true);
        yield return (ExportConfirmFilesInput, ExportConfirmFiles, ExportConfirmFilesLabel, Texts.T("settings.unitCount"),
            Config.ExportConfirmFilesMinimum, Config.ExportConfirmFilesMaximum, true);
        yield return (ExportConfirmGiBInput, ExportConfirmGiB, ExportConfirmGiBLabel, Texts.T("settings.unitGiB"),
            Config.ExportConfirmGiBMinimum, Config.ExportConfirmGiBMaximum, true);
        yield return (PosterCacheLimitMiBInput, PosterCacheLimitMiB, PosterCacheLimitMiBLabel, Texts.T("settings.unitMiB"),
            Config.PosterCacheLimitMiBMinimum, Config.PosterCacheLimitMiBMaximum, true);
        yield return (PosterCacheFileLimitInput, PosterCacheFileLimit, PosterCacheFileLimitLabel, Texts.T("settings.unitCount"),
            Config.PosterCacheFileLimitMinimum, Config.PosterCacheFileLimitMaximum, true);
        yield return (ImportMaxFilesPerBatchInput, ImportMaxFilesPerBatch, ImportMaxFilesPerBatchLabel, Texts.T("settings.unitCount"),
            Config.ImportMaxFilesPerBatchMinimum, Config.ImportMaxFilesPerBatchMaximum, true);
        yield return (ImportMaxBatchGiBInput, ImportMaxBatchGiB, ImportMaxBatchGiBLabel, Texts.T("settings.unitGiB"),
            Config.ImportMaxBatchGiBMinimum, Config.ImportMaxBatchGiBMaximum, true);
        yield return (ImportMaxFileMiBInput, ImportMaxFileMiB, ImportMaxFileMiBLabel, Texts.T("settings.unitMiB"),
            Config.ImportMaxFileMiBMinimum, Config.ImportMaxFileMiBMaximum, true);
        yield return (ImportMaxTotalFilesInput, ImportMaxTotalFiles, ImportMaxTotalFilesLabel, Texts.T("settings.unitCount"),
            Config.ImportMaxTotalFilesMinimum, Config.ImportMaxTotalFilesMaximum, true);
        yield return (ImportMaxTagsPerBatchInput, ImportMaxTagsPerBatch, ImportMaxTagsPerBatchLabel, Texts.T("settings.unitCount"),
            Config.ImportMaxTagsPerBatchMinimum, Config.ImportMaxTagsPerBatchMaximum, true);
        yield return (ImportMaxBatchesInput, ImportMaxBatches, ImportMaxBatchesLabel, Texts.T("settings.unitCount"),
            Config.ImportMaxBatchesMinimum, Config.ImportMaxBatchesMaximum, true);
        yield return (MediaIndexMaxEntriesInput, MediaIndexMaxEntries, MediaIndexMaxEntriesLabel, Texts.T("settings.unitCount"),
            Config.MediaIndexMaxEntriesMinimum, Config.MediaIndexMaxEntriesMaximum, true);
        yield return (MediaIndexMaxThreadsInput, MediaIndexMaxThreads, MediaIndexMaxThreadsLabel, Texts.T("settings.unitCount"),
            Config.MediaIndexMaxThreadsMinimum, Config.MediaIndexMaxThreadsMaximum, true);
        yield return (ImportCatalogLimitMiBInput, ImportCatalogLimitMiB, ImportCatalogLimitMiBLabel, Texts.T("settings.unitMiB"),
            Config.ImportCatalogLimitMiBMinimum, Config.ImportCatalogLimitMiBMaximum, true);
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
            string? key = null;
            bool parsed = decimal.TryParse(field.Input.Text, field.Input.ParsingNumberStyle, field.Input.NumberFormat, out decimal raw);
            bool InvalidNumber(decimal? value) => !value.HasValue || value < field.Min || value > field.Max ||
                (field.Integer && decimal.Truncate(value.Value) != value.Value) || (!field.Integer && value <= 0);
            if (!parsed || DataValidationErrors.GetHasErrors(field.Input))
                key = "settings.fieldNumber";
            else if (InvalidNumber(raw) || InvalidNumber(field.Value))
                key = field.Integer ? "settings.fieldInteger" : "settings.bandwidthRange";
            if (key == null) continue;
            // Keep the failure kind, but resolve the field label/unit in the current language.
            string messageKey = key;
            NumericUpDown input = field.Input;
            Func<string> message = () => RenderNumericFieldError(input, messageKey);
            TextBlock error = this.FindControl<TextBlock>(field.Input.Name + "Error")!;
            error.Text = message();
            error.IsVisible = true;
            SetFieldErrorHelp(field.Input, message);
            _firstInvalidInput ??= field.Input;
        }
        return FinishFieldValidation(focusError);
    }

    private string RenderNumericFieldError(NumericUpDown input, string key)
    {
        var field = NumericFields().First(field => field.Input == input);
        return key switch
        {
            "settings.fieldInteger" => Texts.T(key, FieldLabelWithUnit(field.Label, field.Unit), field.Min, field.Max),
            "settings.bandwidthRange" => Texts.T(key, field.Label, decimal.Floor(field.Max)),
            _ => Texts.T(key, FieldLabelWithUnit(field.Label, field.Unit))
        };
    }

    private void RefreshFieldErrorMessages()
    {
        // Rendering only: preserve rejected drafts, first-error focus, probe results,
        // technical details and the user's expander state. Do not validate again.
        foreach (var field in _fieldHelpErrors)
        {
            TextBlock error = this.FindControl<TextBlock>(field.Key + "Error")!;
            error.Text = field.Value();
        }
        if (GallerySlideshowSecondsInputError.IsVisible)
            GallerySlideshowSecondsInputError.Text = Texts.T("settings.fieldInteger", GallerySlideshowSecondsLabel, 1, 300);
        NotifyFieldHelpChanged();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GallerySlideshowSecondsHelp)));
    }

    // The visible field error is what explains a failed save. It can sit well below its input (hints,
    // preview and check button in between), so a small window or large text would otherwise keep it off
    // screen while only the input's own row was revealed.
    private TextBlock? VisibleFieldError(Control input)
        => input.Name != null && this.FindControl<TextBlock>(input.Name + "Error") is { IsVisible: true } error ? error : null;

    private Control FieldRevealTarget(Control input) => VisibleFieldError(input) ?? input.Parent as Control ?? input;

    private bool FinishFieldValidation(bool focusError = true)
    {
        if (_firstInvalidInput == null) return true;
        SetErrorMessage("settings.notSaved");
        SetStatusMessage("settings.unsaved");
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
            // Focusing scrolls the input itself into view; the error line must win when both cannot fit.
            VisibleFieldError(first)?.BringIntoView();
        }, DispatcherPriority.Loaded);
        return false;
    }
}

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>Only the explicit accept action may submit a completion. Browse/Check/Later/X never do.</summary>
internal partial class FirstRunSetupWindow : Window
{
    private readonly Config _baseline;
    private readonly Func<FirstRunSetupValues, Task<FirstRunSetupCommitResult>> _accept;
    private readonly DownloadDestinationValidator _validator;
    private CancellationTokenSource? _checkWait;
    private Task<FirstRunSetupCommitResult>? _commitTask;
    private FirstRunSetupRunResult _result = new(FirstRunSetupOutcome.Deferred);
    private bool _initializing = true, _closed, _busy, _committing, _accepting;
    private readonly FirstRunSetupDraftState _draft;
    private long _revision => _draft.Revision;
    private bool _hasAction;
    internal long DraftRevision => _revision;
    private DownloadDestinationStatus? _checkStatus;
    internal bool IsWorking => _busy;
    internal bool IsCommitting => _committing;

    // Designer only: no host, IO or automatic save/probe is constructed here.
    public FirstRunSetupWindow() : this(new Config(), _ => Task.FromResult(new FirstRunSetupCommitResult(false))) { }

    internal FirstRunSetupWindow(Config baseline, Func<FirstRunSetupValues, Task<FirstRunSetupCommitResult>> accept,
        DownloadDestinationValidator? validator = null)
    {
        InitializeComponent();
        _baseline = baseline.Clone();
        _accept = accept;
        _validator = validator ?? DownloadDestinationValidator.Shared;
        SetupFolderInput.Text = baseline.DownloadPath;
        SetupBandwidthInput.Text = (baseline.BandwidthLimiter / (1024m * 1024m)).ToString(CultureInfo.CurrentCulture);
        SetupParallelInput.Text = baseline.MaxParallelDownloads.ToString(CultureInfo.CurrentCulture);
        _draft = new(ReadRawDraft());
        _initializing = false;
        RefreshLabels();
        RefreshPreview();
        Texts.Changed += OnLanguageChanged;
        DownloadDestinationValidator.Shared.CleanupFailed += OnCleanupFailure;
        Opened += (_, _) => { if (!_closed && !_hasAction) SetupFolderInput.Focus(); };
    }

    internal async Task<FirstRunSetupRunResult> ShowAsync(Window owner)
    {
        await ShowDialog(owner);
        // Owner/application shutdown can close the UI during an admitted atomic
        // commit. Reconcile its result; closing is not proof that a write was cancelled.
        if (_commitTask != null)
        {
            try
            {
                FirstRunSetupCommitResult completion = await _commitTask;
                if (completion.Persisted) return new(FirstRunSetupOutcome.Completed, completion.ActivationWarning);
            }
            catch { /* no completion confirmation; don't invent success */ }
        }
        return _result;
    }

    private FirstRunSetupRawDraft ReadRawDraft()
        => new(SetupFolderInput.Text, SetupBandwidthInput.Text, SetupParallelInput.Text);

    // Avalonia TextChanged can be delivered after an immediate routed Accept/Check.
    // Current raw text, not notification count/order, is the draft authority. All
    // three fields are captured together; duplicate deferred events have no effects.
    private FirstRunSetupRawDraft SynchronizeDraft()
    {
        if (_initializing || _closed) return _draft.Current;
        if (_draft.Synchronize(ReadRawDraft()))
        {
            _checkWait?.Cancel();
            _checkStatus = null;
            SetupPathError.IsVisible = SetupLimitsError.IsVisible = SetupError.IsVisible = false;
            RefreshPreview();
            SetupStatus.Text = Texts.T("destination.NotChecked");
        }
        return _draft.Current;
    }

    private void OnDraftChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_initializing && !_closed) SynchronizeDraft();
    }

    private void RefreshPreview()
    {
        DownloadDestinationResult syntax = DownloadDestinationValidator.Normalize(SetupFolderInput.Text, _baseline.DownloadPathBaseDirectory);
        SetupNormalizedPath.Text = syntax.NormalizedPath == null ? Texts.T("destination.noPreview")
            : Texts.T("destination.normalized", syntax.NormalizedPath);
    }

    private void RefreshLabels()
    {
        Title = SetupHeading.Text = Texts.T("setup.title");
        SetupIntroduction.Text = Texts.T("setup.intro");
        SetupFolderLabel.Text = Texts.T("settings.downloadPath");
        SetupBandwidthLabel.Text = Texts.T("setup.bandwidth");
        SetupParallelLabel.Text = Texts.T("setup.parallel");
        SetupBrowseButton.Content = Texts.T("settings.browse");
        SetupCheckButton.Content = Texts.T("destination.check");
        SetupAcceptButton.Content = Texts.T("setup.accept");
        SetupLaterButton.Content = Texts.T(_result.Outcome == FirstRunSetupOutcome.Completed ? "setup.close" : "setup.later");
        SetupCancelCheckButton.Content = Texts.T("destination.cancel");
        SetupReserveHint.Text = Texts.T("setup.reserve", _baseline.MinimumFreeSpaceGiB);
        SetupScopeHint.Text = Texts.T("destination.scope");
        SetupProbePolicy.Text = Texts.T("destination.policy");
        SetupStatus.Text = _result.Outcome == FirstRunSetupOutcome.Completed
            ? Texts.T("setup.savedWithWarning", _result.Message)
            : _committing ? Texts.T("setup.saving")
            : _checkWait != null ? Texts.T("setup.checking")
            : Texts.T("destination." + (_checkStatus?.ToString() ?? "NotChecked"));
        if (SetupLimitsError.IsVisible) SetupLimitsError.Text = Texts.T("setup.positiveLimits");
        if (SetupPathError.IsVisible) SetupPathError.Text = _checkStatus.HasValue
            ? Texts.T("destination." + _checkStatus.Value) : Texts.T("settings.pathInvalid");
        AutomationProperties.SetName(SetupFolderInput, SetupFolderLabel.Text);
        AutomationProperties.SetName(SetupBandwidthInput, SetupBandwidthLabel.Text);
        AutomationProperties.SetName(SetupParallelInput, SetupParallelLabel.Text);
        AutomationProperties.SetHelpText(SetupFolderInput, SetupProbePolicy.Text);
        AutomationProperties.SetHelpText(SetupBandwidthInput, Texts.T("setup.positiveLimits"));
        AutomationProperties.SetHelpText(SetupParallelInput, Texts.T("setup.positiveLimits"));
        RefreshCleanupWarning();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_closed) return;
        RefreshLabels();
        RefreshPreview(); // labels only; preserve every raw draft
    });
    private void OnCleanupFailure() => Dispatcher.UIThread.Post(() => { if (!_closed) RefreshCleanupWarning(); });
    private void RefreshCleanupWarning() => SetupCleanupWarning.Text = DownloadDestinationValidator.Shared.HasCleanupFailure
        ? Texts.T("destination.cleanupWarning", DownloadDestinationValidator.Shared.LastCleanupFailurePath ?? Texts.T("destination.unknownProbe")) : string.Empty;

    private void SetWorking(bool busy, bool checking = false)
    {
        _busy = busy;
        if (_closed) return;
        SetupFields.IsEnabled = SetupAcceptButton.IsEnabled = !busy;
        SetupLaterButton.IsEnabled = !_committing;
        SetupCancelCheckButton.IsVisible = checking;
    }

    private bool TryReadDraft(FirstRunSetupRawDraft raw, out FirstRunSetupValues? values)
    {
        values = null;
        DownloadDestinationResult syntax = DownloadDestinationValidator.Normalize(raw.Folder, _baseline.DownloadPathBaseDirectory);
        if (syntax.NormalizedPath == null)
        {
            SetupPathError.Text = Texts.T("settings.pathInvalid");
            SetupPathError.IsVisible = true;
            Reveal(SetupFolderInput);
            return false;
        }
        if (!FirstRunSetupCoordinator.TryParseLimits(raw.Bandwidth, raw.Parallel,
            CultureInfo.CurrentCulture, out long rate, out int parallel))
        {
            SetupLimitsError.Text = Texts.T("setup.positiveLimits");
            SetupLimitsError.IsVisible = true;
            bool bandwidthValid = FirstRunSetupCoordinator.TryParseLimits(raw.Bandwidth, "1",
                CultureInfo.CurrentCulture, out _, out _);
            Reveal(bandwidthValid ? SetupParallelInput : SetupBandwidthInput);
            return false;
        }
        values = new(syntax.NormalizedPath, rate, parallel);
        return true;
    }

    private void Reveal(Control input)
    {
        long revision = _revision;
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed) return;
            SynchronizeDraft();
            if (revision != _revision) return;
            input.BringIntoView(); input.Focus();
        }, DispatcherPriority.Loaded);
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _closed) return;
        _hasAction = true;
        SynchronizeDraft();
        long revision = _revision;
        SetupError.IsVisible = false;
        SetWorking(true);
        try
        {
            LocalFolderPickResult result = await LocalFolderPicker.PickAsync(StorageProvider, Texts.T("settings.pickerTitle"));
            if (_closed) return;
            SynchronizeDraft();
            if (revision != _revision) return;
            switch (result.Status)
            {
                case LocalFolderPickStatus.Selected:
                    SetupFolderInput.Text = result.Path;
                    SynchronizeDraft();
                    break;
                case LocalFolderPickStatus.Unavailable: ShowError(Texts.T("settings.pickerUnavailable")); break;
                case LocalFolderPickStatus.NonLocal: ShowError(Texts.T("settings.pickerLocalOnly")); break;
                case LocalFolderPickStatus.Failed: ShowError(Texts.T("setup.pickerFailed")); break;
            }
        }
        finally { SetWorking(false); }
    }

    private async Task<bool> CheckAsync(FirstRunSetupValues values, long revision)
    {
        SynchronizeDraft();
        if (_closed || revision != _revision) return false;
        using var wait = new CancellationTokenSource();
        _checkWait = wait;
        SetWorking(true, checking: true);
        SetupStatus.Text = Texts.T("setup.checking");
        try
        {
            DownloadDestinationResult result = await _validator.CheckAsync(values.DownloadPath, _baseline.DownloadPathBaseDirectory,
                checked((long)_baseline.MinimumFreeSpaceGiB * (1L << 30)), TimeSpan.FromSeconds(5), wait.Token);
            if (_closed) return false;
            SynchronizeDraft();
            if (revision != _revision) return false;
            _checkStatus = result.Status;
            SetupStatus.Text = Texts.T("destination." + result.Status);
            if (!result.CanApply)
            {
                SetupPathError.Text = SetupStatus.Text;
                SetupPathError.IsVisible = true;
                Reveal(SetupFolderInput);
            }
            return result.CanApply;
        }
        finally { _checkWait = null; SetWorking(_accepting); }
    }

    private async void OnCheckClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _closed) return;
        _hasAction = true;
        FirstRunSetupRawDraft raw = SynchronizeDraft();
        ClearActionErrors();
        if (!TryReadDraft(raw, out FirstRunSetupValues? values)) return;
        await CheckAsync(values!, _revision);
    }

    private async void OnAcceptClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _accepting || _closed || _result.Outcome == FirstRunSetupOutcome.Completed) return;
        _hasAction = true;
        FirstRunSetupRawDraft raw = SynchronizeDraft();
        ClearActionErrors();
        if (!TryReadDraft(raw, out FirstRunSetupValues? values)) return;
        _accepting = true;
        long revision = _revision;
        try
        {
            if (!await CheckAsync(values!, revision) || _closed) return;
            SynchronizeDraft();
            if (revision != _revision) return;
            _committing = true;
            SetWorking(true);
            SetupStatus.Text = Texts.T("setup.saving");
            _commitTask = _accept(values!);
            FirstRunSetupCommitResult completion = await _commitTask;
            if (!completion.Persisted)
            {
                if (!_closed) ShowError(Texts.T("setup.saveUnconfirmed"));
                return;
            }
            _result = new(FirstRunSetupOutcome.Completed, completion.ActivationWarning);
            _committing = false;
            if (_closed) return;
            if (completion.ActivationWarning == null) Close();
            else
            {
                // The atomic snapshot is already durable. Do NOT offer Accept/retry.
                SetupAcceptButton.IsVisible = false;
                SetupFields.IsVisible = false;
                SetupLaterButton.Content = Texts.T("setup.close");
                SetupStatus.Text = Texts.T("setup.savedWithWarning", completion.ActivationWarning);
            }
        }
        catch (Exception) { if (!_closed) ShowError(Texts.T("setup.saveUnconfirmed")); }
        finally { _accepting = false; _committing = false; SetWorking(false); }
    }

    private void ClearActionErrors() => SetupError.IsVisible = SetupPathError.IsVisible = SetupLimitsError.IsVisible = false;
    private void ShowError(string message) { SetupError.Text = message; SetupError.IsVisible = true; SetupError.BringIntoView(); }
    private void OnCancelCheckClick(object? sender, RoutedEventArgs e) => _checkWait?.Cancel();
    private void OnLaterClick(object? sender, RoutedEventArgs e) { if (!_committing) Close(); }
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_committing && e.CloseReason == WindowCloseReason.WindowClosing) e.Cancel = true;
    }
    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _checkWait?.Cancel(); // ends the caller wait, not physical IO or an admitted commit
        Texts.Changed -= OnLanguageChanged;
        DownloadDestinationValidator.Shared.CleanupFailed -= OnCleanupFailure;
        base.OnClosed(e);
    }
}

/// <summary>Exact raw snapshots; no parsing, normalization, controls or filesystem work.</summary>
internal readonly record struct FirstRunSetupRawDraft(string? Folder, string? Bandwidth, string? Parallel);

internal sealed class FirstRunSetupDraftState(FirstRunSetupRawDraft initial)
{
    internal FirstRunSetupRawDraft Current { get; private set; } = initial;
    internal long Revision { get; private set; }

    internal bool Synchronize(FirstRunSetupRawDraft current)
    {
        if (Current == current) return false;
        Current = current;
        Revision++;
        return true;
    }
}

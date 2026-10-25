using System;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

internal sealed record ExperimentalActivationDecision(bool ContinueAdmission, string? Error);

public partial class ExperimentalActivationDialog : Window
{
    private readonly ExperimentalSourceActivationTarget _original;
    private readonly Func<ExperimentalSourceActivationTarget, Task<ExperimentalSourceActivationResult>> _activate;
    private ExperimentalSourceActivationTarget? _next;
    private ExperimentalSourceActivationResult? _failure;
    private string? _unexpectedError;
    private bool _busy, _closed;
    private long _statusRevealRevision;
    private T C<T>(string name) where T : Control => this.FindControl<T>(name)!;

    internal ExperimentalActivationDialog(ExperimentalSourceActivationTarget target,
        Func<ExperimentalSourceActivationTarget, Task<ExperimentalSourceActivationResult>> activate)
    {
        _original = _next = target; _activate = activate;
        AvaloniaXamlLoader.Load(this);
        C<Button>("ActivationCancel").Click += (_, _) => Dismiss();
        C<Button>("ActivationConfirm").Click += async (_, _) => await ActivateAsync();
        // A queued initial error reveal must not override a newer user scroll,
        // focus action, retry, language change or closed dialog.
        AddHandler(PointerPressedEvent, (_, _) => _statusRevealRevision++, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerWheelChangedEvent, (_, _) => _statusRevealRevision++, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, (_, _) => _statusRevealRevision++, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(GotFocusEvent, (_, _) => _statusRevealRevision++, RoutingStrategies.Bubble, handledEventsToo: true);
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && !IsVisible) _statusRevealRevision++;
        };
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            if (!_busy) Dismiss();
        };
        // A persisted Enable cannot be rolled back by closing the window during Save.
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
        Opened += (_, _) =>
        {
            if (Owner != null)
            {
                MaxWidth = Math.Max(MinWidth, Owner.Bounds.Width);
                MaxHeight = Math.Max(MinHeight, Owner.Bounds.Height);
                Width = Math.Min(Width, MaxWidth); Height = Math.Min(Height, MaxHeight);
            }
            C<Button>("ActivationCancel").Focus();
        };
        Closed += (_, _) => { _closed = true; _statusRevealRevision++; Texts.Changed -= LanguageChanged; };
        Texts.Changed += LanguageChanged;
        Localize();
    }

    private void LanguageChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            if (_closed) return;
            _statusRevealRevision++;
            Localize();
        });

    private void Localize()
    {
        Title = Texts.T("activation.title", _original.SourceName);
        C<TextBlock>("ActivationHeading").Text = Title;
        C<TextBlock>("ActivationExplanation").Text = Texts.T(_original.RequestsMismatchPermission ? "activation.explanation" : "activation.strictExplanation", _original.SourceName);
        C<TextBlock>("ActivationScope").Text = Texts.T(_original.RequestsMismatchPermission ? "activation.scope" : "activation.strictScope", _original.SourceId, _original.PluginOwnerId);
        C<Button>("ActivationCancel").Content = Texts.T(_failure != null || _unexpectedError != null ? "activation.close" : "wizard.cancel");
        string confirm = Texts.T(!_original.RequestsMismatchPermission
            ? _failure == null && _unexpectedError == null ? "activation.strictConfirm" : "activation.strictRetry"
            : _failure == null && _unexpectedError == null ? "activation.confirm"
            : _failure?.ActualState is { IsEnabled: true, IsActive: true } ? "activation.retryGrant" : "activation.retryEnable");
        C<TextBlock>("ActivationConfirmLabel").Text = confirm;
        AutomationProperties.SetName(C<Button>("ActivationConfirm"), confirm);
        C<Button>("ActivationConfirm").IsEnabled = !_busy && _next != null;
        C<Button>("ActivationCancel").IsEnabled = !_busy;
        C<Ursa.Controls.LoadingIcon>("ActivationBusy").IsLoading = _busy;
        C<Ursa.Controls.LoadingIcon>("ActivationBusy").IsVisible = _busy;
        string? status = FailureMessage();
        C<Border>("ActivationStatusPanel").IsVisible = _busy || status != null;
        C<TextBlock>("ActivationStatus").Text = _busy ? Texts.T(_original.RequestsMismatchPermission ? "activation.saving" : "activation.strictSaving") : status;
    }

    internal string? AdmissionError => FailureMessage();

    private string? FailureMessage()
    {
        if (_failure == null && _unexpectedError == null) return null;
        string message = Texts.T(_original.RequestsMismatchPermission ? "activation.failed" : "activation.strictFailed", _original.SourceName, _failure?.Error ?? _unexpectedError ?? "");
        if (_failure?.ActualState is { } state)
            message += Environment.NewLine + Texts.T(_original.RequestsMismatchPermission ? "activation.actualState" : "activation.strictActualState", YesNo(state.IsEnabled), YesNo(state.IsActive),
                YesNo(state.IsGranted), YesNo(state.IsEffective));
        else message += Environment.NewLine + Texts.T("activation.stateUnknown");
        return message + Environment.NewLine + Texts.T(_next != null ? "activation.retryHint" : "activation.recheckHint");
    }

    private static string YesNo(bool value) => Texts.T(value ? "activation.yes" : "activation.no");

    private void Dismiss()
    {
        if (!_busy) Close(new ExperimentalActivationDecision(false, FailureMessage()));
    }

    private async Task ActivateAsync()
    {
        if (_busy || _closed || _next is not { } target) return;
        _statusRevealRevision++;
        _busy = true; Localize();
        bool succeeded = false;
        try
        {
            ExperimentalSourceActivationResult result = await _activate(target);
            succeeded = result.Status == ExperimentalSourceActivationStatus.Succeeded;
            _failure = succeeded ? null : result;
            // Retry only an explicitly supplied continuation of this confirmed operation.
            _next = succeeded ? null : result.RetryTarget;
            _unexpectedError = null;
        }
        catch (Exception error)
        {
            _failure = null; _unexpectedError = error.Message; _next = null;
        }
        finally { _busy = false; }
        if (_closed) return;
        if (succeeded) { Close(new ExperimentalActivationDecision(true, null)); return; }
        Localize();
        C<Button>(_next != null ? "ActivationConfirm" : "ActivationCancel").Focus();
        long revealRevision = _statusRevealRevision;
        ExperimentalSourceActivationResult? failure = _failure;
        string? unexpectedError = _unexpectedError;
        bool IsCurrentReveal() => !_closed && !_busy && IsVisible && revealRevision == _statusRevealRevision
            && ReferenceEquals(_failure, failure) && _unexpectedError == unexpectedError;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!IsCurrentReveal()) return;
            // Localize just made the formerly hidden status panel visible.
            // Its old zero bounds cannot produce a meaningful scroll target.
            UpdateLayout();
            TextBlock status = C<TextBlock>("ActivationStatus");
            if (!IsCurrentReveal() || !status.IsEffectivelyVisible || !status.IsMeasureValid || !status.IsArrangeValid
                || status.Bounds.Width <= 0 || status.Bounds.Height <= 0) return;
            status.BringIntoView();
        }, DispatcherPriority.Loaded);
    }
}

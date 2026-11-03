using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class MainWindow
{
    private readonly ClipboardUrlMonitor _clipboardUrlMonitor = new();
    private DispatcherTimer? _clipboardUrlTimer;

    private bool CanScanClipboard => _hasOpened && _restoreCompleted && !_isClosing && !_importing
        && !WorkspaceCommandBusy && IsEnabled && _session.State == DesktopSessionState.Ready
        && !HasVisibleShellDialog() && !HasBlockingPageDecision();
    private bool CanShowClipboardSuggestion => CanScanClipboard && IsVisible && WindowState != WindowState.Minimized
        && !_largeReaderOpen && _gallery == null && _archivePage?.IsReaderDialogOpen != true;

    private void InitializeClipboardMonitor()
    {
        _clipboardUrlTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _clipboardUrlTimer.Tick += OnClipboardMonitorTick;
        RefreshClipboardMonitor();
    }

    private void RefreshClipboardMonitor()
    {
        bool enabled = _config.MonitorClipboardUrls && !_isClosing;
        _clipboardUrlMonitor.SetEnabled(enabled);
        if (enabled && _hasOpened) _clipboardUrlTimer?.Start();
        else _clipboardUrlTimer?.Stop();
        var panel = this.FindControl<Border>("ClipboardSuggestionPanel");
        if (panel == null) return;
        int count = _clipboardUrlMonitor.Pending.Count;
        panel.IsVisible = enabled && count > 0 && CanShowClipboardSuggestion;
        this.FindControl<TextBlock>("ClipboardSuggestionTitle")!.Text = Texts.T("clipboard.monitor.found", count);
        this.FindControl<Button>("ClipboardSuggestionAccept")!.Content = Texts.T("clipboard.monitor.accept");
        this.FindControl<Button>("ClipboardSuggestionDismiss")!.Content = Texts.T("clipboard.monitor.dismiss");
        this.FindControl<TextBox>("ClipboardSuggestionUrls")!.Text = string.Join(Environment.NewLine, _clipboardUrlMonitor.Pending);
        this.FindControl<Button>("ClipboardSuggestionAccept")!.IsEnabled = panel.IsVisible && !RowCommandBusy;
    }

    private async void OnClipboardMonitorTick(object? sender, EventArgs e) => await PollClipboardMonitorAsync();

    internal async Task PollClipboardMonitorAsync()
    {
        RefreshClipboardMonitor();
        if (!_config.MonitorClipboardUrls || !CanScanClipboard) return;
        var hosts = _session.GetProviderDescriptors().SelectMany(provider => provider.SupportedHosts.Prepend(provider.BaseUri.IdnHost))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        try
        {
            await _clipboardUrlMonitor.PollAsync(() => _config.MonitorClipboardUrls && CanScanClipboard,
                ReadClipboardAsync, hosts, _session.ClassifyAdmissionAsync);
        }
        catch (Exception)
        {
            // Clipboard ownership/permissions can be transient (notably Wayland).
            // Passive detection must not spam notifications, log private contents or bypass admission.
        }
        finally { if (!_isClosing) RefreshClipboardMonitor(); }
    }

    private void StopClipboardMonitor()
    {
        _clipboardUrlTimer?.Stop();
        if (_clipboardUrlTimer != null) _clipboardUrlTimer.Tick -= OnClipboardMonitorTick;
        _clipboardUrlTimer = null;
        _clipboardUrlMonitor.SetEnabled(false); // Invalidates any outstanding native clipboard read.
    }

    private void OnClipboardSuggestionDismiss(object? sender, RoutedEventArgs e)
    {
        _clipboardUrlMonitor.Dismiss();
        RefreshClipboardMonitor();
    }

    private void OnClipboardSuggestionKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || ShortcutService.IsCapturing || !CanShowClipboardSuggestion) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            OnClipboardSuggestionDismiss(sender, new RoutedEventArgs());
        }
        // Only the explicit confirmation button keeps native Enter/Space activation.
        // Enter in the selectable URL preview or an unrelated editor is not consent.
    }

    private async void OnClipboardSuggestionAccept(object? sender, RoutedEventArgs e)
    {
        if (!_config.MonitorClipboardUrls || !CanShowClipboardSuggestion || RowCommandBusy) return;
        // Native Button activation precedes the panel's bubbling KeyDown handler.
        // Capturing a shortcut or a stale/hidden action is not clipboard consent.
        if (ShortcutService.IsCapturing || sender is not Button accept
            || !accept.IsEffectivelyVisible || !accept.IsEffectivelyEnabled) return;
        // Freeze the offered batch, not whatever is now on the clipboard. No read on confirmation.
        var urls = _clipboardUrlMonitor.TakePending();
        if (urls.Count == 0) return;
        _importing = true;
        UpdateInteractionState();
        try { await SubmitInputAsync(string.Join(Environment.NewLine, urls)); }
        finally { _importing = false; UpdateInteractionState(); }
    }
}

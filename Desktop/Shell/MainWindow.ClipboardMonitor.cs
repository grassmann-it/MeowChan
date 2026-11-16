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
    private bool _clipboardMonitorInitialized;

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
        _clipboardUrlMonitor.Configure(_config.ClipboardUrlLimit, _config.ClipboardTextLimitKiB * 1024);
        // Capture the loaded opt-in on the first refresh (which can precede Opened/Ready).
        // Later OFF -> ON saves deliberately process existing text, even before the first poll.
        _clipboardUrlMonitor.SetEnabled(enabled, baselineOnly: !_clipboardMonitorInitialized);
        _clipboardMonitorInitialized = true;
        if (enabled) _clipboardUrlMonitor.SetSupportedHosts(GetClipboardMonitorHosts());
        if (enabled && _hasOpened) _clipboardUrlTimer?.Start();
        else _clipboardUrlTimer?.Stop();
        var panel = this.FindControl<Border>("ClipboardSuggestionPanel");
        if (panel == null) return;
        int count = _clipboardUrlMonitor.Pending.Count;
        // What a read had to leave out is shown too, with the setting that changes it; without URLs only Dismiss remains.
        string? notice = _clipboardUrlMonitor.NoticeText();
        panel.IsVisible = enabled && (count > 0 || notice != null) && CanShowClipboardSuggestion;
        ClipboardSuggestionTitle.Text = count > 0 ? Texts.T("clipboard.monitor.found", count) : notice ?? string.Empty;
        ClipboardSuggestionNotice.Text = count > 0 ? notice : null;
        ClipboardSuggestionNotice.IsVisible = count > 0 && notice != null;
        ClipboardSuggestionAccept.Content = Texts.T("clipboard.monitor.accept");
        ClipboardSuggestionDismiss.Content = Texts.T("clipboard.monitor.dismiss");
        ClipboardSuggestionUrls.Text = string.Join(Environment.NewLine, _clipboardUrlMonitor.Pending);
        ClipboardSuggestionUrls.IsVisible = count > 0;
        ClipboardSuggestionAccept.IsVisible = count > 0;
        ClipboardSuggestionAccept.IsEnabled = panel.IsVisible && !RowCommandBusy; // Hidden without URLs, so it cannot be activated then.
    }

    private string[] GetClipboardMonitorHosts() => _session.GetProviderDescriptors()
        .SelectMany(provider => provider.SupportedHosts.Prepend(provider.BaseUri.IdnHost))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private bool CanScanCurrentClipboard()
    {
        if (!_config.MonitorClipboardUrls || !CanScanClipboard) return false;
        // Observe source changes at async completion too, not just on the next timer tick.
        _clipboardUrlMonitor.SetSupportedHosts(GetClipboardMonitorHosts());
        return true;
    }

    private async void OnClipboardMonitorTick(object? sender, EventArgs e) => await PollClipboardMonitorAsync();

    internal async Task PollClipboardMonitorAsync()
    {
        RefreshClipboardMonitor();
        if (!_config.MonitorClipboardUrls || !CanScanClipboard) return;
        var hosts = GetClipboardMonitorHosts();
        try
        {
            await _clipboardUrlMonitor.PollAsync(CanScanCurrentClipboard,
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
        try { UpdateInteractionState(); await SubmitInputAsync(string.Join(Environment.NewLine, urls)); }
        finally { _importing = false; UpdateInteractionState(); }
    }
}

using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Feedback;
using MeowChan.Desktop.Localization;

namespace MeowChan;

/// <summary>
/// The context menu of the shell banner (right-click, Menu key, Shift+F10): copy the message, dismiss it like its close button, close all
/// toasts. The toasts carry the same menu (<see cref="FeedbackToastContent"/>); closing toasts is presentation only and acknowledges nothing.
/// </summary>
public partial class MainWindow
{
    private ContextMenuHost? _bannerMenu;

    private void InitializeNotificationMenu() =>
        _bannerMenu ??= new ContextMenuHost(ShellActionErrorBanner, ComposeBannerMenu, fallbackFocus: () => ShellFeedbackDismissButton.Focus());

    private ContextMenuBuilder? ComposeBannerMenu()
    {
        SessionFeedback? entry = _shellFeedback;
        if (_isClosing || entry == null || !ShellActionErrorBanner.IsVisible) return null;
        string text = ShellActionError.Text ?? string.Empty;
        return NotificationMenu.Compose(string.Empty, () => _ = CopyNotificationTextAsync(text), () => DismissShellFeedback(entry), CloseAllNotifications);
    }

    /// <summary>Closes every toast; the banner and the diagnostics history keep what they show.</summary>
    internal void CloseAllNotifications() => _notificationManager.CloseAll();

    /// <summary>Copies a notification's text; a failure is reported as an error toast.</summary>
    internal async Task CopyNotificationTextAsync(string text)
    {
        if (await MenuClipboard.CopyAsync(this, text)) return;
        ShowNotification(Texts.T("notification.error"), Texts.T("ctxmenu.copyFailed"), NotificationType.Error, operation: "clipboard.write");
    }
}

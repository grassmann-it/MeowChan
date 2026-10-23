using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using MeowChan.Desktop.Feedback;

namespace MeowChan;

public partial class MainWindow
{
    private readonly SessionFeedbackStore _feedback = new();
    private sealed record PendingFeedback(string Title, string Message, NotificationType Type, FeedbackCategory Category);
    private readonly Queue<PendingFeedback> _startupNotifications = new();

    // CallerMemberName is a safe fallback for future callers: even an unclassified error is retained.
    private void ShowNotification(string title, string message, NotificationType type,
        string? context = null, [CallerMemberName] string operation = "ui")
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ShowNotification(title, message, type, context, operation));
            return;
        }
        if (_allowClose) return;
        FeedbackCategory category = type switch
        {
            NotificationType.Error => FeedbackCategory.Error,
            NotificationType.Warning => FeedbackCategory.Warning,
            NotificationType.Success => FeedbackCategory.Success,
            _ => FeedbackCategory.Information
        };
        // Capture first. Neither popup suppression nor a page rebuild can lose the error.
        SessionFeedback? entry = _feedback.Record(category, operation, context, message);
        if (entry != null)
        {
            TextBlock text = this.FindControl<TextBlock>("ShellActionError")!;
            AutomationProperties.SetLiveSetting(text, SessionFeedbackStore.AllowsPopup(_config, category)
                ? AutomationLiveSetting.Off : AutomationLiveSetting.Assertive);
            text.Text = message;
            this.FindControl<Ursa.Controls.Banner>("ShellActionErrorBanner")!.Type = type;
            this.FindControl<Ursa.Controls.Banner>("ShellActionErrorBanner")!.IsVisible = true;
        }
        PendingFeedback feedback = new(title, message, type, category);
        if (!_hasOpened) _startupNotifications.Enqueue(feedback);
        else RenderNotification(feedback);
    }

    private void RenderNotification(PendingFeedback feedback)
    {
        if (!_allowClose && SessionFeedbackStore.AllowsPopup(_config, feedback.Category))
            _notificationManager.Show(new Notification(feedback.Title, feedback.Message, feedback.Type));
    }
}

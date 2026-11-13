using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Feedback;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class MainWindow
{
    private readonly SessionFeedbackStore _feedback = new();
    private SessionFeedback? _shellFeedback;
    private bool _feedbackDismissWired;
    private ICommand? _feedbackDismissGesture;
    private sealed record PendingFeedback(string Title, string Message, NotificationType Type,
        FeedbackCategory Category, SessionFeedback? Entry, IReadOnlyList<FeedbackToastAction>? Actions = null);
    private readonly Queue<PendingFeedback> _startupNotifications = new();

    // CallerMemberName is a safe fallback for future callers: even an unclassified error is retained.
    private void ShowNotification(string title, string message, NotificationType type,
        string? context = null, [CallerMemberName] string operation = "ui") =>
        ShowNotificationCore(title, message, type, context, operation,
            KnownNotificationFaultKey(type, operation, message));

    // A toast with action buttons (for example "Undo"). Choosing one runs it and closes the toast. The actions are only
    // offered while the toast is shown; the diagnostics history keeps the message, never the actions.
    private void ShowActionNotification(string title, string message, NotificationType type, FeedbackToastAction[] actions,
        string? context = null, [CallerMemberName] string operation = "ui") =>
        ShowNotificationCore(title, message, type, context, operation, actions: actions);

    // Separate helper preserves the five-parameter reflection entry point. Future structured
    // owners may supply a fault key, but disappearance/text changes never imply recovery.
    private void ShowNotificationCore(string title, string message, NotificationType type,
        string? context, string operation, string? faultKey = null, IReadOnlyList<FeedbackToastAction>? actions = null)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ShowNotificationCore(title, message, type, context, operation, faultKey, actions));
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
        SessionFeedback? entry = _feedback.Record(category, operation, context, message, faultKey: faultKey);
        if (entry != null)
        {
            // While media is open ordinary errors are diagnostic history, not another overlay.
            // Do not queue them for a notification flood when returning to the shell.
            if (GallerySuppressesFeedback && operation != "session.shutdown") return;
            // Capture/count repeats even when this occurrence has already been dismissed.
            if (_feedback.IsDismissed(entry)) return;
            _shellFeedback = entry;
            // Freeze the displayed identity in the command, not a mutable control's Tag/current ID.
            Button dismiss = ShellFeedbackDismissButton;
            WireFeedbackDismissGesture(dismiss);
            dismiss.Command = new DelegateCommand(() => DismissShellFeedback(entry));
            TextBlock text = ShellActionError;
            AutomationProperties.SetLiveSetting(text, SessionFeedbackStore.AllowsPopup(_config, category)
                ? AutomationLiveSetting.Off : AutomationLiveSetting.Assertive);
            text.Text = message;
            ShellActionErrorBanner.Type = type;
            ShellActionErrorBanner.IsVisible = true;
        }
        PendingFeedback feedback = new(title, message, type, category, entry, actions);
        if (!_hasOpened) _startupNotifications.Enqueue(feedback);
        else RenderNotification(feedback);
    }

    // Narrow adapter for verified fixed-copy shell callers, avoiding edits to unrelated shell
    // owners. A different message under the same operation MUST retain its exact text identity.
    internal static string? KnownNotificationFaultKey(NotificationType type, string operation, string message)
    {
        string? key = (type, operation) switch
        {
            (NotificationType.Error, "clipboard.read") => "clipboard.readFailed",
            (NotificationType.Error, "clipboard.write") => "clipboard.writeFailed",
            (NotificationType.Error, "target.open") => "location.openFailed",
            (NotificationType.Warning, "directory.open") => "location.missing",
            (NotificationType.Error, "session.shutdown") => "shutdown.failed",
            _ => null
        };
        return key != null && message == Texts.T(key) ? key : null;
    }

    private void WireFeedbackDismissGesture(Button dismiss)
    {
        if (_feedbackDismissWired) return;
        _feedbackDismissWired = true;
        // Remember where the user was working so closing feedback by keyboard can hand focus back.
        RememberFeedbackReturnTarget(FocusManager?.GetFocusedElement());
        AddHandler(GotFocusEvent, (_, e) => RememberFeedbackReturnTarget(e.Source), RoutingStrategies.Bubble, handledEventsToo: true);
        // Button resolves Command on release. Freeze it on press as well, so publishing B
        // between pointer/Space down and up cannot turn the user's close of A into a close of B.
        dismiss.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(dismiss).Properties.IsLeftButtonPressed)
                _feedbackDismissGesture = dismiss.Command;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        dismiss.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space) _feedbackDismissGesture ??= dismiss.Command;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        dismiss.Click += (_, e) =>
        {
            if (_feedbackDismissGesture is not { } captured) return;
            _feedbackDismissGesture = null;
            e.Handled = true; // Prevent Button from also executing the potentially newer Command.
            captured.Execute(null);
        };
        // Bubble runs after Button's release/click processing. Cancelled gestures leave no target.
        dismiss.AddHandler(PointerReleasedEvent, (_, _) => _feedbackDismissGesture = null,
            RoutingStrategies.Bubble, handledEventsToo: true);
        dismiss.AddHandler(KeyUpEvent, (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space) _feedbackDismissGesture = null;
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        dismiss.PointerCaptureLost += (_, _) => _feedbackDismissGesture = null;
        dismiss.LostFocus += (_, _) => _feedbackDismissGesture = null;
    }

    // Stable internal action for the shell close button and smoke coverage. A captured close for A
    // still suppresses A's pending toasts, but must never hide a newer banner B.
    internal void DismissShellFeedback(SessionFeedback entry)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => DismissShellFeedback(entry));
            return;
        }
        if (_allowClose) return;
        _feedback.Dismiss(entry);
        foreach (NotificationCard card in _notificationManager.GetVisualDescendants().OfType<NotificationCard>().ToArray())
            if (card.Content is FeedbackToastContent { Entry: { } shown } toast
                && SessionFeedbackStore.SameOccurrence(shown, entry))
            {
                if (toast.IsKeyboardFocusWithin) RestoreFeedbackDismissFocus();
                card.Close(); // Closing a card never acknowledges another event.
            }
        if (_shellFeedback == null || !SessionFeedbackStore.SameOccurrence(_shellFeedback, entry)) return;
        var banner = ShellActionErrorBanner;
        // Only move focus when removing the focused banner, never on background publication.
        if (banner.IsKeyboardFocusWithin)
            RestoreFeedbackDismissFocus();
        _shellFeedback = null;
        banner.IsVisible = false;
    }

    private void RestoreFeedbackDismissFocus()
    {
        if (_gallery != null && !_gallerySuspended && _gallery.IsEffectivelyVisible)
            _gallery.FocusGallerySurface();
        else if (_feedbackReturnTarget is { } weak && weak.TryGetTarget(out Control? target)
            && target.IsAttachedToVisualTree() && target.IsEffectivelyVisible && target.IsEffectivelyEnabled && target.Focusable
            && ReferenceEquals(TopLevel.GetTopLevel(target), this))
            target.Focus();
        // The earlier control is gone: continue in the page, never at the brand button first.
        else if (FocusManager?.TryMoveFocus(NavigationDirection.Next) != true)
            BrandAboutButton.Focus();
    }

    private WeakReference<Control>? _feedbackReturnTarget;
    private void RememberFeedbackReturnTarget(object? source)
    {
        if (source is not Control control || !control.Focusable) return;
        // Focus inside the banner or a toast is the feedback surface itself, not a place to return to.
        if (control.GetSelfAndVisualAncestors().Any(v => v is Ursa.Controls.Banner or FeedbackToastContent or NotificationCard)) return;
        _feedbackReturnTarget = new WeakReference<Control>(control);
    }

    private bool GallerySuppressesFeedback => _gallery != null && !_gallerySuspended && _gallery.IsEffectivelyVisible;

    private void HideFeedbackForGallery()
    {
        // Presentation only: closing a toast is not acknowledgement or recovery.
        _notificationManager.CloseAll();
        ShellActionErrorBanner.IsVisible = false;
        _shellFeedback = null;
    }

    private void RenderNotification(PendingFeedback feedback)
    {
        if (GallerySuppressesFeedback && (feedback.Category is FeedbackCategory.Warning or FeedbackCategory.Error)
            && feedback.Entry?.Operation != "session.shutdown") return;
        // Pending startup items retain their captured identity even after diagnostics are cleared.
        if (feedback.Entry != null && _feedback.IsDismissed(feedback.Entry)) return;
        if (_allowClose || !SessionFeedbackStore.AllowsPopup(_config, feedback.Category)) return;
        FeedbackToastContent content = new(feedback.Title, feedback.Message, feedback.Entry, toast =>
        {
            if (_allowClose) return;
            if (feedback.Entry != null) DismissShellFeedback(feedback.Entry);
            if (toast.IsKeyboardFocusWithin) RestoreFeedbackDismissFocus();
            _notificationManager.Close(toast);
        }, feedback.Actions);
        // Semi 12.1.0.1 has no template close button. Only the explicit content action acknowledges
        // a fault. Do not use onClose: expiry, max-item eviction and CloseAll also call it.
        _notificationManager.Show(content, feedback.Type,
            ToastDuration.For(feedback.Type, feedback.Title.Length + feedback.Message.Length, feedback.Actions is { Count: > 0 }));
    }
}

/// <summary>The single optional button of a toast. The label is already localized when the toast is shown.</summary>
internal sealed record FeedbackToastAction(string Label, Func<Task> Invoke);

/// <summary>Small theme-independent toast content; expiry has no acknowledgement callback.</summary>
internal sealed class FeedbackToastContent : Grid
{
    internal string Title { get; }
    internal string Message { get; }
    internal SessionFeedback? Entry { get; }
    internal Button DismissButton { get; }
    internal IReadOnlyList<Button> ActionButtons { get; }
    internal Button? ActionButton => ActionButtons.Count > 0 ? ActionButtons[0] : null;

    private bool _attached;
    private Task? _actionRun;
    private readonly IReadOnlyList<FeedbackToastAction> _actions;
    private readonly Action<FeedbackToastContent> _dismiss;

    internal FeedbackToastContent(string title, string message, SessionFeedback? entry, Action<FeedbackToastContent> dismiss,
        IReadOnlyList<FeedbackToastAction>? actions = null)
    {
        _actions = actions ?? Array.Empty<FeedbackToastAction>();
        _dismiss = dismiss;
        Title = title; Message = message;
        Entry = entry;
        Name = "FeedbackToastContent";
        Classes.Add("toast-content"); // D1: arrival animation (WorkspaceStyles), absent under motion-off.
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        MaxWidth = 420;
        StackPanel text = new() { Spacing = 4 };
        text.Children.Add(new TextBlock { Text = title, FontWeight = Avalonia.Media.FontWeight.SemiBold,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        SelectableTextBlock body = new() { Name = "FeedbackToastMessage", Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        // The toast is the only announcement while the popup is allowed (the shell banner is then Off): errors interrupt,
        // everything else (information such as the Danbooru watcher result, success, warnings) waits its turn.
        AutomationProperties.SetLiveSetting(body, LiveSettingFor(entry));
        text.Children.Add(body);
        List<Button> buttons = new();
        if (_actions.Count > 0)
        {
            WrapPanel row = new() { Orientation = Orientation.Horizontal, Margin = new Avalonia.Thickness(0, 4, 0, 0) };
            for (int index = 0; index < _actions.Count; index++)
            {
                int chosen = index;
                Button button = new()
                {
                    Name = index == 0 ? "FeedbackToastActionButton" : "FeedbackToastActionButton" + (index + 1),
                    Content = _actions[index].Label, FontWeight = Avalonia.Media.FontWeight.SemiBold,
                    MinHeight = 32, Padding = new Avalonia.Thickness(12, 2), Margin = new Avalonia.Thickness(0, 0, 6, 0),
                    Command = new DelegateCommand(() => _ = RunActionAsync(chosen))
                };
                button.Classes.Add("subtle");
                AutomationProperties.SetName(button, _actions[index].Label);
                buttons.Add(button);
                row.Children.Add(button);
            }
            text.Children.Add(row);
        }
        ActionButtons = buttons;
        // The card has no peer of its own; the name lets a reader that enters the toast (dismiss button) hear what it says.
        AutomationProperties.SetName(this, AccessibleName(title, message));
        Children.Add(new ScrollViewer { Content = text, MaxHeight = 160,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        DismissButton = new Button
        {
            Name = "FeedbackToastDismissButton", Content = "×", FontSize = 20,
            MinWidth = 32, MinHeight = 32, Padding = new Avalonia.Thickness(8, 2),
            Margin = new Avalonia.Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top,
            Command = new DelegateCommand(() => dismiss(this))
        };
        DismissButton.Classes.Add("subtle");
        LocalizeDismissButton();
        SetColumn(DismissButton, 1);
        Children.Add(DismissButton);
        AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            Texts.Changed += OnLanguageChanged;
            LocalizeDismissButton();
        };
        DetachedFromVisualTree += (_, _) => { _attached = false; Texts.Changed -= OnLanguageChanged; };
    }

    /// <summary>Runs one toast action, once for the whole toast; the toast closes afterwards, whether it worked or not.</summary>
    internal Task RunActionAsync(int index = 0) => _actionRun ??= RunActionCoreAsync(index);

    private async Task RunActionCoreAsync(int index)
    {
        if (index < 0 || index >= _actions.Count) return;
        foreach (Button button in ActionButtons) button.IsEnabled = false;
        try { await _actions[index].Invoke(); }
        catch (Exception exception) { Serilog.Log.Warning(exception, "A toast action failed."); }
        finally { _dismiss(this); }
    }

    internal static AutomationLiveSetting LiveSettingFor(SessionFeedback? entry) =>
        entry?.Category == FeedbackCategory.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite;

    internal static string AccessibleName(string title, string message) =>
        string.IsNullOrWhiteSpace(title) ? message : string.IsNullOrWhiteSpace(message) ? title : title.TrimEnd('.', ' ') + ". " + message;

    private void LocalizeDismissButton()
    {
        AutomationProperties.SetName(DismissButton, Texts.T("feedback.dismiss"));
        ToolTip.SetTip(DismissButton, Texts.T("feedback.dismiss"));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnLanguageChanged(sender, e));
            return;
        }
        if (_attached) LocalizeDismissButton();
    }
}

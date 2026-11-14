using System;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Interactivity;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using Serilog;

namespace MeowChan;

// One-time notice for profiles that existed before the work views reduced every page (plan AP5 question 3).
// Barebones and Simple now keep their other tools under "More"; the notice says so once per profile, offers the switch
// to Advanced and can be dismissed. It never changes the saved view by itself, and a profile that already uses Advanced
// (nothing is reduced there) or is new never sees it.
public partial class MainWindow
{
    private bool _workViewNoticeSettling;

    /// <summary>Whether the notice is on the window right now.</summary>
    internal bool WorkViewNoticeVisible => WorkViewNoticePanel.IsVisible;

    private bool WorkViewNoticePending => _config.WorkViewNoticePending && _config.AllowsOrdinaryWrites;

    internal void RefreshWorkViewNotice()
    {
        if (!_workspaceInitialized) return;
        bool show = WorkViewNoticePending && ConfiguredWorkView != WorkView.Advanced;
        WorkViewNoticePanel.IsVisible = show;
        string text = Texts.T("workview.notice.text");
        WorkViewNoticeText.Text = text;
        AutomationProperties.SetName(WorkViewNoticePanel, text);
        WorkViewNoticeAdvanced.Content = Texts.T("workview.notice.switchAdvanced");
        WorkViewNoticeDismiss.Content = Texts.T("workview.notice.dismiss");
        // Advanced shows everything directly, so there is nothing to tell; settle the flag once the session can write.
        if (WorkViewNoticePending && !show && _restoreCompleted && !_workViewNoticeSettling) _ = SettleWorkViewNoticeAsync(false);
    }

    private async void OnWorkViewNoticeDismiss(object? sender, RoutedEventArgs e) => await SettleWorkViewNoticeAsync(false);

    private async void OnWorkViewNoticeAdvanced(object? sender, RoutedEventArgs e) => await SettleWorkViewNoticeAsync(true);

    private async System.Threading.Tasks.Task SettleWorkViewNoticeAsync(bool switchToAdvanced)
    {
        if (!_restoreCompleted || _workViewNoticeSettling || WorkspaceCommandBusy || !WorkViewNoticePending) return;
        _workViewNoticeSettling = true;
        try
        {
            bool hadFocus = WorkViewNoticePanel.IsKeyboardFocusWithin;
            Config candidate = _config.Clone();
            candidate.WorkViewNoticePending = false;
            if (switchToAdvanced) candidate.WorkspaceMode = WorkView.Advanced.ToSetting();
            await SaveAppearanceAsync(candidate);
            RefreshWorkViewNotice();
            // R11: the focus never gets lost; the view switch is the reference point of the notice.
            if (hadFocus) WorkspaceModeComboBox.Focus();
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Could not save the work-view notice state.");
        }
        finally { _workViewNoticeSettling = false; }
    }
}

// WV12 is separate from the legacy one-time notice above: no flag is read or settled by the switch toast.
public partial class MainWindow
{
    private readonly WorkViewSwitchNoticeState _workViewSwitchNotices = new();
    private FeedbackToastContent? _workViewSwitchToast;

    /// <summary>
    /// WV11 host contract: navigate to settings.workviews, EnsureLoaded, select only rows in diff.Changes
    /// (all pages), and focus the overview. Parent wires this once with the settings-category integration.
    /// A missing host is explicitly reported, never a successful-looking no-op. No config snapshot is passed.
    /// </summary>
    internal event Func<DiffSummary, Task>? WorkViewOverviewRequested;

    private bool WorkViewSwitchNoticeEnabled => _config.WorkViewSwitchNoticeSeconds > 0
        && _config.ShowInformationNotifications && _restoreCompleted && _hasOpened && !_isClosing && !_allowClose;

    private void InitializeWorkViewSwitchNotices()
    {
        // WorkViewState does not publish the initial view. This also covers footer/settings saves, which historically
        // use SaveAppearanceAsync/ApplySettingsAsync directly rather than the app-menu's SetWorkViewAsync.
        _workViews.Changed += OnWorkViewSwitchApplied;
        // The legacy footer saves directly. Revoke the previous lease at intent time, not only when that async save
        // finishes, so pressing an old Undo while a fast footer switch is pending cannot queue the wrong return view.
        WorkspaceModeComboBox.SelectionChanged += (_, _) =>
        {
            if (!_updatingWorkspacePreferences && CanSwitchWorkView
                && WorkspaceModeComboBox.SelectedItem is ComboBoxItem { Tag: string mode }
                && WorkViewSetting.Parse(mode) != ConfiguredWorkView)
                CancelWorkViewSwitchNotice();
        };
        Closing += (_, _) => CancelWorkViewSwitchNotice();
        Closed += (_, _) =>
        {
            _workViews.Changed -= OnWorkViewSwitchApplied;
            _workViewSwitchNotices.Close();
            CloseWorkViewSwitchToast();
            WorkViewOverviewRequested = null;
        };
    }

    private void OnWorkViewSwitchApplied(WorkView from, WorkView to)
    {
        CloseWorkViewSwitchToast();
        var notice = _workViewSwitchNotices.Applied(from, to, _config.WorkViewSwitchNoticeSeconds, WorkViewSwitchNoticeEnabled);
        if (notice == null) return;
        FeedbackToastContent? content = null;
        content = ShowTransientActionNotification(WorkViewSwitchCatalog.T("workview.switch.title"),
            WorkViewSwitchCatalog.Summary(notice.Diff), notice.Duration,
            [
                new FeedbackToastAction(WorkViewSwitchCatalog.T("workview.switch.undo"),
                    () => SetWorkViewAsync(notice.Diff.From, undoNotice: notice)),
                new FeedbackToastAction(WorkViewSwitchCatalog.T("workview.switch.details"),
                    () => RequestWorkViewOverviewAsync(notice))
            ], () =>
            {
                _workViewSwitchNotices.Dismiss(notice);
                if (ReferenceEquals(_workViewSwitchToast, content)) _workViewSwitchToast = null;
            });
        _workViewSwitchToast = content;
        if (content == null) _workViewSwitchNotices.Dismiss(notice);
    }

    private async Task RequestWorkViewOverviewAsync(WorkViewSwitchNoticeState.Notice notice)
    {
        if (!WorkViewSwitchNoticeEnabled || !CanSwitchWorkView) return;
        DiffSummary? diff = _workViewSwitchNotices.TakeDetails(notice, ConfiguredWorkView);
        if (diff == null) return;
        if (WorkViewOverviewRequested is not { } host)
        {
            Log.Warning("WV12 Details requires the WV11 WorkViewOverviewRequested host hook.");
            ShowNotification(WorkViewSwitchCatalog.T("workview.switch.details"),
                WorkViewSwitchCatalog.T("workview.switch.overviewUnavailable"), NotificationType.Warning,
                operation: "workview.switch.details.unavailable");
            return;
        }
        foreach (Func<DiffSummary, Task> handler in host.GetInvocationList()) await handler(diff);
    }

    private void RefreshWorkViewSwitchNoticeAvailability()
    {
        // RefreshWorkViewMenu is already called for every applied settings/appearance refresh, even without a view change.
        if (!WorkViewSwitchNoticeEnabled) CancelWorkViewSwitchNotice();
    }

    private void CancelWorkViewSwitchNotice()
    {
        _workViewSwitchNotices.Cancel();
        CloseWorkViewSwitchToast();
    }

    private void CloseWorkViewSwitchToast()
    {
        FeedbackToastContent? content = _workViewSwitchToast;
        _workViewSwitchToast = null;
        if (content != null) _notificationManager.Close(content);
    }
}

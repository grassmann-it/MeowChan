using System;
using Avalonia.Automation;
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

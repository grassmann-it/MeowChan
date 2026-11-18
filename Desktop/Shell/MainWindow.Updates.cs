using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Desktop;
using Avalonia.Controls.Notifications;
using MeowChan.Desktop.Feedback;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Updates;
using Serilog;

namespace MeowChan;

public partial class MainWindow
{
    private Button? _updateNotice;
    private MenuItem? _checkUpdatesMenu;
    private readonly UpdateNotifier _updateNotifier = new();

    private void InitializeUpdates()
    {
        // A compact, keyboard-accessible status action is present in every workspace mode.
        // The status row (ShrinkRowPanel) shortens its widest item first: the notice stands second, after the state and before the
        // limit warning and the counters. (The former grid gave the notice the warning's column, so the two overlapped.)
        if (this.FindControl<TextBlock>("WorkspaceStatus")?.Parent is Panel status)
        {
            _updateNotice = new Button
            {
                Name = "UpdateNoticeButton", Padding = new Thickness(4, 0), MinHeight = 0,
                IsVisible = false, Classes = { "subtle" }, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            }.CaptionText();
            _updateNotice.Click += OnUpdateNoticeClick;
            status.Children.Insert(Math.Min(1, status.Children.Count), _updateNotice);
        }
        if (this.FindControl<MenuItem>("WorkspaceMenuItem") is { } menu)
        {
            _checkUpdatesMenu = new MenuItem { Name = "CheckUpdatesMenuItem" };
            _checkUpdatesMenu.Click += OnCheckUpdatesMenuClick;
            int index = menu.Items.IndexOf(this.FindControl<MenuItem>("AboutMenuItem"));
            menu.Items.Insert(Math.Max(0, index), _checkUpdatesMenu);
        }
        _session.Updates.Changed += OnUpdatesChanged;
        Texts.Changed += OnUpdateTextsChanged;
        RefreshUpdates();
    }

    private void OnUpdatesChanged() => Dispatcher.UIThread.Post(() => { if (!_isClosing) RefreshUpdates(); });
    private void OnUpdateTextsChanged(object? sender, EventArgs e) => OnUpdatesChanged();

    private void RefreshUpdates()
    {
        UpdateView view = _session.Updates.View;
        RefreshTray();
        // The footer notice and tray entry stay while an update is available; the toast is the one interruption.
        if (_updateNotifier.Evaluate(view, _config.AutomaticUpdateChecks,
                SessionFeedbackStore.AllowsPopup(_config, FeedbackCategory.Information)) is { } announced)
            ShowUpdateAvailableToast(announced);
        if (_updateNotice != null)
        {
            _updateNotice.IsVisible = view.Update != null && !view.EnvironmentDisabled;
            string label = view.Update == null ? string.Empty : Texts.T("updates.available", view.Update.Version);
            _updateNotice.Content = new TextBlock { Text = label, MaxWidth = 220, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
            AutomationProperties.SetName(_updateNotice, label);
            ToolTip.SetTip(_updateNotice, label);
        }
        if (_checkUpdatesMenu != null)
        {
            // The entry carries its command (S3 P2-3): a key bound to "Check for updates" presses it (TryRunUnroutedShellCommand finds menu entries by
            // their command). Label, icon, gesture and tip come from the command; the tip names why checks are off when packaging disabled them.
            MeowChan.Desktop.Commands.CommandBindings.Apply(_checkUpdatesMenu, "updates.check",
                () => _session.Updates.View.EnvironmentDisabled ? Texts.T("updates.disabled") : null, () => Texts.T("updates.check"));
            // The entry stays reachable when packaging disabled checks: About explains the reason.
            _checkUpdatesMenu.IsEnabled = !view.IsChecking;
        }
    }

    // Release notes open in the browser through the same launcher as every other link; nothing is downloaded or installed.
    private void ShowUpdateAvailableToast(UpdateInfo update) => ShowActionNotification(
        Texts.T("updates.toastTitle", update.Version), Texts.T("updates.toastMessage"), NotificationType.Information,
        new[]
        {
            new FeedbackToastAction(Texts.T("updates.toastNotes"), () => { OpenTarget(update.ReleaseUri.AbsoluteUri); return Task.CompletedTask; }),
            new FeedbackToastAction(Texts.T("updates.toastLater"), () => Task.CompletedTask),
            new FeedbackToastAction(Texts.T("updates.skip"), () => SkipAnnouncedUpdateAsync(update))
        }, operation: "update.available");

    private async Task SkipAnnouncedUpdateAsync(UpdateInfo update)
    {
        try { await _session.Updates.SkipAsync(update.Version); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            Log.Warning(error, "Could not skip update.");
            if (!_isClosing) ShowNotification(Texts.T("notification.error"), Texts.T("updates.error.skip"), NotificationType.Warning, operation: "update.skip");
        }
    }

    private async void OnTrayUpdateClick(object? sender, EventArgs e)
    {
        RestoreWindow();
        await ShowUpdatesAsync(false);
    }

    private async void OnUpdateNoticeClick(object? sender, RoutedEventArgs e) => await ShowUpdatesAsync(false);
    private async void OnCheckUpdatesMenuClick(object? sender, RoutedEventArgs e) => await ShowUpdatesAsync(true);

    private async Task ShowUpdatesAsync(bool check)
    {
        if (_isClosing) return;
        AboutWindow about = new(_session.Updates);
        Task dialog = about.ShowDialog(this);
        if (check) await about.CheckUpdatesAsync();
        await dialog;
    }

    private void DisposeUpdates()
    {
        _session.Updates.Changed -= OnUpdatesChanged;
        Texts.Changed -= OnUpdateTextsChanged;
        if (_updateNotice != null) _updateNotice.Click -= OnUpdateNoticeClick;
        if (_checkUpdatesMenu != null) _checkUpdatesMenu.Click -= OnCheckUpdatesMenuClick;
    }
}

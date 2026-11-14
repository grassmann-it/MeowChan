using System;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Localization;

namespace MeowChan;

/// <summary>
/// Main-window host hooks for the lazy Booru workspace page and its presentation-only switch.
/// The page owns the existing workbench model; this file only owns composition identity,
/// navigation visibility and final lifetime.
/// </summary>
public partial class MainWindow
{
    private BooruPage? _booruPage;
    private bool _updatingBooruToolsPresentation;

    internal BooruPage? BooruPageForTests => _booruPage;

    /// <summary>Returns the current page only for the exact service composition that created it.</summary>
    private bool EnsureBooruPage()
    {
        if (!CanOpenBooru || _session.BooruServices is not { } services) return false;
        if (_booruPage is { } current && ReferenceEquals(current.Services, services)) return true;

        // A provider/profile composition is a lifetime boundary, even when it exposes the
        // same provider id. Never let callbacks from the previous page reach the new one.
        DisposeBooruPage();
        BooruPage page = new(services);
        ConfigureBooruMediaLinks(page);
        page.ApplyWorkView(ConfiguredWorkView, viewChanged: true);
        _booruPage = page;
        BooruPageHost.Content = page;
        return true;
    }

    private void DisposeBooruPage()
    {
        BooruPage? page = _booruPage;
        _booruPage = null;
        if (page == null) return;
        if (ReferenceEquals(BooruPageHost.Content, page)) BooruPageHost.Content = null;
        page.Dispose();
    }

    private void OnBooruNavigationClick(object? sender, RoutedEventArgs e)
    {
        ToolsNavigationButton.Flyout?.Hide();
        NavigateWorkspace(WorkspacePage.Booru);
    }

    /// <summary>
    /// Reconciles a late composition/availability callback on the UI thread. Availability can
    /// change independently of the presentation switch; no source, watcher or download state is
    /// changed here.
    /// </summary>
    private void OnBooruChanged()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(OnBooruChanged);
            return;
        }
        if (_isClosing) return;

        BooruWorkbenchServices? services = _session.BooruServices;
        if (_booruPage is { } page && !ReferenceEquals(page.Services, services)) DisposeBooruPage();
        if (_selectedPage == WorkspacePage.Booru && CanOpenBooru)
        {
            if (EnsureBooruPage())
            {
                BooruPageHost.IsVisible = true;
                _ = _booruPage?.ActivateAsync();
            }
        }
        else if (_selectedPage == WorkspacePage.Booru)
        {
            // This is a safe presentation fallback, not a page disposal: a later re-enable
            // must reveal the same draft while the service identity remains valid.
            NavigateWorkspace(WorkspacePage.Downloads);
        }
        UpdateInteractionState();
    }

    /// <summary>Refreshes both switch surfaces without changing any non-presentation preference.</summary>
    private void RefreshBooruToolsPresentation()
    {
        bool available = !_isClosing && _session.BooruServices != null;
        bool visible = available && _config.ShowBooruTools;
        bool enabled = available && _restoreCompleted && !_applyingPreferences && !WorkspaceCommandBusy;
        try
        {
            _updatingBooruToolsPresentation = true;
            ShowBooruToolsToggle.IsVisible = available;
            ShowBooruToolsToggle.IsEnabled = enabled;
            ShowBooruToolsToggle.IsChecked = visible;
            ShowBooruToolsLabel.Text = Texts.T("workview.booru.show");
            string hint = Texts.T("workview.booru.showHint");
            ToolTip.SetTip(ShowBooruToolsToggle, hint);
            AutomationProperties.SetName(ShowBooruToolsToggle, Texts.T("workview.booru.show"));
            AutomationProperties.SetHelpText(ShowBooruToolsToggle, hint);

            if (_booruToolsMenu is { } menu)
            {
                menu.IsVisible = available;
                menu.IsEnabled = enabled;
                menu.IsChecked = visible;
                menu.Header = Texts.T("workview.booru.show");
                AutomationProperties.SetName(menu, Texts.T("workview.booru.show"));
                AutomationProperties.SetHelpText(menu, hint);
            }
            if (_booruMenu != null) _booruMenu.IsVisible = CanOpenBooru;
            if (!CanOpenBooru) BooruMoreMenuItem.IsVisible = false;
            BooruMenuItem.IsVisible = CanOpenBooru;
        }
        finally { _updatingBooruToolsPresentation = false; }
    }

    private void OnBooruToolsToggleChanged(object? sender, RoutedEventArgs e)
    {
        if (_updatingBooruToolsPresentation) return;
        _ = SetBooruToolsVisibilityAsync(ShowBooruToolsToggle.IsChecked == true);
    }

    private async System.Threading.Tasks.Task SetBooruToolsVisibilityAsync(bool visible)
    {
        if (_isClosing || _updatingBooruToolsPresentation || !_restoreCompleted
            || _session.BooruServices == null || WorkspaceCommandBusy)
        {
            RefreshBooruToolsPresentation();
            return;
        }
        try
        {
            await _session.SaveViewPreferencesAsync(current => current.ShowBooruTools = visible);
            if (!visible && _selectedPage == WorkspacePage.Booru)
                NavigateWorkspace(WorkspacePage.Downloads);
            if (!_isClosing) UpdateInteractionState();
        }
        catch (Exception exception)
        {
            Serilog.Log.Error(exception, "Could not save Booru presentation preference.");
            RefreshBooruToolsPresentation();
            ShowNotification(Texts.T("notification.error"),
                Texts.T("preferences.failed", exception.Message),
                Avalonia.Controls.Notifications.NotificationType.Error,
                operation: "preferences.booru-visibility");
        }
    }
}

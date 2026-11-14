using System;
using System.Threading.Tasks;
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
    private readonly BooruUnavailableNavigation _booruUnavailableNavigation = new();
    private bool _booruFallbackRestoreContinuation;
    private bool _booruFallbackRestoreSettled;

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
            _booruUnavailableNavigation.Clear();
            if (EnsureBooruPage())
            {
                BooruPageHost.IsVisible = true;
                _ = _booruPage?.ActivateAsync();
            }
        }
        else if (_selectedPage == WorkspacePage.Booru)
        {
            // Dispose the unavailable composition immediately, but do not bypass the shell's
            // admission/restore gates for the fallback. A blocked route remains pending and is
            // completed from the normal busy/restore transitions below.
            RequestBooruUnavailableFallback();
        }
        SyncBooruNavigation();
        UpdateInteractionState();
        TryCompleteBooruUnavailableFallback();
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
            SyncBooruNavigation();
        }
        finally { _updatingBooruToolsPresentation = false; }
    }

    /// <summary>
    /// The footer's Booru button and the Booru entry of More follow <see cref="CanOpenBooru"/> (Booru composed or revoked, "Show Booru actions", session
    /// state). Runs the footer refresh when that value differs from the one the last refresh used, so the button appears or goes away at once instead of
    /// at the next resize or page change. Cheap when nothing changed; does nothing before the first footer refresh (the constructor builds it).
    /// </summary>
    private void SyncBooruNavigation()
    {
        if (_booruNavigationAvailable is not { } built || built == CanOpenBooru || _isClosing) return;
        UpdatePageNavigation();
    }

    private void RequestBooruUnavailableFallback()
    {
        if (_isClosing || _selectedPage != WorkspacePage.Booru || CanOpenBooru) return;
        // The disposed page must not remain the visible body while the ordinary route is gated.
        BooruPageHost.IsVisible = false;
        _booruUnavailableNavigation.Request();
        TryCompleteBooruUnavailableFallback();
    }

    private void TryCompleteBooruUnavailableFallback()
    {
        if (!_booruUnavailableNavigation.IsPending || _isClosing || _selectedPage != WorkspacePage.Booru
            || CanOpenBooru) return;
        if (_booruUnavailableNavigation.TryComplete(() => NavigateWorkspace(WorkspacePage.Downloads))) return;
        if (!_restoreCompleted && !_booruFallbackRestoreContinuation && !_booruFallbackRestoreSettled)
        {
            _booruFallbackRestoreContinuation = true;
            // The restore task is assigned at the end of the constructor. Post once so an early
            // composition callback cannot observe that field before its assignment.
            Dispatcher.UIThread.Post(() => _ = CompleteBooruUnavailableFallbackAfterRestoreAsync());
        }
    }

    private async Task CompleteBooruUnavailableFallbackAfterRestoreAsync()
    {
        try { await _restoreTask.ConfigureAwait(false); }
        catch { /* The normal navigation gate remains closed after a failed restore. */ }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                _booruFallbackRestoreContinuation = false;
                _booruFallbackRestoreSettled = true;
                if (!_isClosing) TryCompleteBooruUnavailableFallback();
            });
        }
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

internal sealed class BooruUnavailableNavigation
{
    internal bool IsPending { get; private set; }

    internal void Request() => IsPending = true;

    internal void Clear() => IsPending = false;

    internal bool TryComplete(Func<bool> navigate)
    {
        ArgumentNullException.ThrowIfNull(navigate);
        if (!IsPending || !navigate()) return false;
        IsPending = false;
        return true;
    }
}

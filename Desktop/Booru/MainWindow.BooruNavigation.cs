using System;
using System.Collections.Generic;
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
    private BooruSearchTabStore? _booruTabStore;
    private readonly BooruUnavailableNavigation _booruUnavailableNavigation = new();
    private bool _booruFallbackRestoreContinuation;
    private bool _booruFallbackRestoreSettled;

    internal BooruPage? BooruPageForTests => _booruPage;

    /// <summary>Returns the current page only for the exact service composition that created it.</summary>
    private bool EnsureBooruPage()
    {
        if (!CanOpenBooru) return false;
        IReadOnlyList<BooruWorkbenchServices> sources = _session.ActiveBooruServices;
        if (sources.Count == 0) return false;
        BooruWorkbenchServices services = sources[0];
        if (_booruPage is { } current && current.HasSources(sources)) return true;

        // A provider/profile composition is a lifetime boundary, even when it exposes the
        // same provider id. Never let callbacks from the previous page reach the new one.
        DisposeBooruPage();
        BooruPage page = new(services, sources);
        ConfigureBooruMediaLinks(page);
        ConfigureBooruViewPreferences(page);
        // The sdk-mismatch repair route (MainWindow.BooruRepair): review, confirm, restart.
        AttachBooruRepair(page, services);
        page.ApplyWorkView(ConfiguredWorkView, viewChanged: true);
        page.RestoreSearchTabs(_booruTabStore ??= _session.CreateBooruSearchTabStore(), _session.Config);
        _booruPage = page;
        BooruPageHost.Content = page;
        return true;
    }

    private Task FlushBooruSearchTabsAsync()
    {
        _booruPage?.FlushSearchTabs();
        return _booruTabStore?.PendingSave ?? Task.CompletedTask;
    }

    private void DisposeBooruPage()
    {
        BooruPage? page = _booruPage;
        _booruPage = null;
        if (page == null) return;
        if (ReferenceEquals(BooruPageHost.Content, page)) BooruPageHost.Content = null;
        DetachBooruRepair(page);
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

        if (_booruPage is { } page && !page.HasSources(_session.ActiveBooruServices)) DisposeBooruPage();
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

    /// <summary>
    /// Refreshes the Booru entries that depend on <c>Config.ShowBooruTools</c> ("Show Booru actions" in Settings › Appearance › Footer; the check
    /// box that used to stand in the footer and its twin in the ⋯ menu are gone, F1): the Danbooru tools entry of the ⋯ menu and the Booru entry of More.
    /// Nothing else is changed.
    /// </summary>
    private void RefreshBooruToolsPresentation()
    {
        if (_booruMenu != null) _booruMenu.IsVisible = CanOpenBooru;
        if (!CanOpenBooru) BooruMoreMenuItem.IsVisible = false;
        SyncBooruNavigation();
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

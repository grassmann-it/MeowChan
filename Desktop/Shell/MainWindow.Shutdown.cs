using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using Serilog;

namespace MeowChan;

// Bounded shutdown (16.11.2026; Tom: "right now I can't even close the window via X"). A shutdown step that never finished (a drain,
// a native player, a dialog the restore waited for) used to leave the window visible while every handler guarded by _isClosing did
// nothing, and X did nothing either. Now:
//   * up to the veto point (an editor with unsaved changes) nothing changes, so a kept window is fully usable;
//   * from then on the window hides at once and every awaited step shares the shutdown time limit (the setting, default 30 s); a step
//     that runs out of time is logged by name with how long it ran, a failing step is logged and the others still run;
//   * the end follows ShutdownOutcome: everything proven released closes the window; a failure with everything released shows the error
//     once and the next close exits; a step out of time or an owner not proven released ends the process (ShutdownExit), where the
//     process may end itself, and otherwise (smokes) keeps the retained window as before. A watchdog thread ends the process when
//     even that does not happen in time (a UI thread stuck in a native call).
public partial class MainWindow
{
    private ShutdownBudget? _shutdownBudget;
    private ShutdownWatchdog? _shutdownWatchdog;

    /// <summary>The running (or last) shutdown's step budget (test and smoke seam).</summary>
    internal ShutdownBudget? ShutdownBudgetForTests => _shutdownBudget;

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        Log.Debug("Window closing requested: reason {CloseReason}, allowed {AllowClose}, shutdown running {ShutdownRunning}, owned windows {OwnedWindowCount}.",
            e.CloseReason, _allowClose, _shutdownRunning, OwnedWindows.Count);
        base.OnClosing(e);
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (_isClosing)
        {
            return;
        }

        if (!TemporaryFolderSession && _config.CloseToTray && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            MinimizeWindow();
            return;
        }

        try { await CloseApplicationAsync(); }
        catch (Exception error) when (UnhandledErrors.TryRecover(error, "session.shutdown")) { }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        // The window is gone, so the process should follow at once. Whatever still holds it (an exit handler, a foreground thread)
        // is ended after the grace.
        if (!ShutdownExit.ProcessExitAllowed) return;
        _shutdownWatchdog ??= new ShutdownWatchdog();
        _shutdownWatchdog.Arm(ShutdownExit.WatchdogGrace, () => "the window closed, but the process did not end");
        if (TemporaryFolderSession && ProfileStartup.FolderSession is { } folderSession)
            folderSession.WindowShutdownWatchdog = _shutdownWatchdog;
        // S4 F1: the lifetime ends with its last window. A window the hide disowned that refused its own close (a dialog that asks
        // about its draft on a programmatic close) would keep it running until the watchdog: the app is shut down instead, which closes
        // every window still open without asking. Posted, so this window's own close has finished first.
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            Dispatcher.UIThread.Post(() => EndLifetimeAfterWindowClosed(desktop), DispatcherPriority.Background);
    }

    private static void EndLifetimeAfterWindowClosed(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            if (desktop.Windows.Count == 0) return;
            Log.Information("The main window closed while {Count} other window(s) were still open ({Windows}); shutting the application down.",
                desktop.Windows.Count, string.Join(", ", desktop.Windows.Select(DescribeWindow)));
            desktop.Shutdown();
        }
        catch (Exception error) { Log.Warning(error, "The application could not be shut down after the main window closed."); }
    }

    private static string DescribeWindow(Window window) => string.IsNullOrEmpty(window.Name) ? window.GetType().Name : window.Name;

    // ---- S4 F1: owned windows ----

    // Avalonia's Window.Hide hides every owned window and sets its Owner to null, so the window's own final Close (which closes only the
    // windows it still owns) leaves them open and the lifetime (OnLastWindowClose) running. The windows owned when the shutdown starts
    // are remembered here; one that closes itself meanwhile (the cleanup dialog when its operation ended) drops out.
    private readonly List<Window> _shutdownOwnedWindows = new();

    /// <summary>The owned windows remembered at the start of the shutdown and not closed yet (test and smoke seam).</summary>
    internal IReadOnlyList<Window> ShutdownOwnedWindowsForTests => _shutdownOwnedWindows.ToArray();

    private void CaptureOwnedWindowsForShutdown()
    {
        foreach (Window window in OwnedWindows.ToArray())
        {
            if (_shutdownOwnedWindows.Contains(window)) continue;
            _shutdownOwnedWindows.Add(window);
            window.Closed += OnShutdownOwnedWindowClosed;
        }
    }

    private void OnShutdownOwnedWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not Window window) return;
        window.Closed -= OnShutdownOwnedWindowClosed;
        _shutdownOwnedWindows.Remove(window);
    }

    // Everything is released by now, so nothing these windows could still do touches the profile. Each one is closed on its own: one
    // that throws does not keep the others open. The windows that ask about a draft on a plain close have a close for this case (the
    // owner ending: the draft stays where it lives); one that still refuses is named in the log, and OnClosed ends the lifetime over it.
    // Never throws: it also runs on the failure paths.
    private void CloseOwnedWindowsForShutdown()
    {
        foreach (Window window in _shutdownOwnedWindows.ToArray())
        {
            try
            {
                switch (window)
                {
                    case MeowChan.Desktop.Views.Shortcuts.ShortcutsWindow shortcuts: shortcuts.CloseWithoutAsking(); break;
                    case SavedArchiveSearchesDialog searches: searches.CloseForLifetime(); break;
                    default: window.Close(); break;
                }
            }
            catch (Exception error) { Log.Warning(error, "Owned window {Window} could not be closed at the end of the shutdown.", DescribeWindow(window)); }
        }
        try
        {
            if (_shutdownOwnedWindows.Count > 0)
                Log.Information("Owned window(s) {Windows} kept their close back at the end of the shutdown.", string.Join(", ", _shutdownOwnedWindows.Select(DescribeWindow)));
        }
        catch (Exception error) { Log.Warning(error, "The owned windows left open could not be described."); }
    }

    // ---- S4 F2/F4: the steps of the shutdown's start ----

    private void DropPendingSessionRowsForShutdown()
    {
        _pendingSessionProjection = null;
        _sessionRowsApplying = false;
        _sessionRowsApplied?.TrySetResult(); _sessionRowsApplied = null;
        _restoreDownloadSelection?.Dispose(); _restoreDownloadSelection = null;
        _refreshingDownloadSearch = false;
    }

    // S4 F4: the session's stop waits for every accepted command, and the owners that would cancel these end only in ui-teardown, after
    // the stop. Both are cancellable by design: a running check ends at once and a running copy stops after the current file (the files
    // already imported stay); a running archive cleanup stops before its next step (committed steps stay). The non-cancellable
    // permanent deletion never reaches this point: it is asked at the veto point (ConfirmCloseDuringArchiveDeletionAsync).
    private void CancelLongOperationsForShutdown()
    {
        if (_manualImportDialog is { } import)
        {
            if (import.Session.Busy) Log.Information("Shutdown: cancelling the running manual import (stage {Stage}).", import.Session.Stage);
            import.Session.Cancel();
        }
        _archivePage?.CancelLongOperations();
    }

    private async Task CloseApplicationAsync()
    {
        if (_shutdownRunning || _allowClose)
        {
            return;
        }

        // The veto point: until here nothing has changed, so "keep editing", "keep MeowChan open" (or a check that fails) leaves the
        // window fully usable. A running permanent deletion is asked first (it cannot be cancelled half-way, see
        // MainWindow.ArchiveDeletion.cs); the editors after it are asked with what they hold once it has finished.
        bool confirmed = await ShutdownPreparation.ConfirmAsync(new Func<Task<bool>>[]
        {
            ConfirmCloseDuringArchiveDeletionAsync,
            () => _postBookmarkEditor?.PrepareToCloseAsync() ?? Task.FromResult(true),
            () => _postBookmarksDialog?.PrepareToCloseAsync() ?? Task.FromResult(true),
            ConfirmTemporaryFolderCloseAsync,
        });
        if (!confirmed)
        {
            Log.Information("A shutdown confirmation kept the application open.");
            if (IsArchiveDeletionRunning) _archiveDeletionDialog?.Activate();
            else if (_postBookmarkEditor is Window editor) editor.Activate();
            else if (_postBookmarksDialog is Window bookmarks) bookmarks.Activate();
            return;
        }
        // Another close request may have started while the dialogs were asked.
        if (_shutdownRunning || _allowClose || _isClosing)
        {
            return;
        }

        // S4 F2: from here the shutdown runs. The budget and the watchdog come first and everything after them sits inside the try, each
        // preparing call as a step of its own: a call that throws is logged and recorded, the next ones still run, and the window is
        // hidden and ended like any other shutdown. Nothing can leave the visible window with every _isClosing-guarded handler dead.
        ShutdownBudget budget = _shutdownBudget = new ShutdownBudget(TimeSpan.FromSeconds(Math.Clamp(_config.ShutdownTimeLimitSeconds,
            Config.ShutdownTimeLimitSecondsMinimum, Config.ShutdownTimeLimitSecondsMaximum)));
        if (ShutdownExit.ProcessExitAllowed)
        {
            _shutdownWatchdog ??= new ShutdownWatchdog();
            _shutdownWatchdog.Arm(budget.Limit + ShutdownExit.WatchdogGrace, budget.DescribeRunning);
        }
        Log.Information("Application shutdown started; time limit {Limit}.", ShutdownBudget.Seconds(budget.Limit));
        bool nativeOwnershipReleased = false;
        try
        {
            // S4 F3: what the reading position and the quick settings (the reader popover, Ctrl+wheel font steps) still wait to save is
            // submitted now, while nothing is closing yet. The session takes these writes until it seals its final writes, so they join
            // the final preferences below instead of being dropped with "the window is closing".
            Task quickSettings = Task.CompletedTask;
            Task booruSearchTabs = Task.CompletedTask;
            budget.Run("input-flush", () =>
            {
                _archivePage?.FlushReadingPositionInput();
                quickSettings = _archivePage?.FlushQuickSettingsAsync() ?? Task.CompletedTask;
            });
            budget.Run("booru-tabs-flush", () => booruSearchTabs = FlushBooruSearchTabsAsync());
            var booruViewerBounds = _booruPage?.View.OpenViewerBounds;
            _isClosing = true;
            _shutdownRunning = true;
            budget.Run("clipboard-monitor", StopClipboardMonitor);
            budget.Run("session-rows", DropPendingSessionRowsForShutdown);
            budget.Run("bookmark-dialogs", () =>
            {
                _postBookmarksDialog?.CloseForProfileEnd();
                _postBookmarkEditor?.CloseForProfileEnd();
            });
            budget.Run("page-lifetimes", () =>
            {
                _settingsEditor?.EndLifetime();
                _pluginsPage?.EndLifetime();
                _rulesPage?.EndLifetime();
            });
            budget.Run("interaction-state", UpdateInteractionState);
            // S4 F1: hiding hides and disowns every owned window (F1 help, the import dialog, a modal open when tray Quit came), so the
            // final Close would no longer close them and the lifetime would wait for them. They are remembered now and closed before it.
            budget.Run("owned-windows", CaptureOwnedWindowsForShutdown);
            // Nobody is left looking at a window whose every control has stopped answering. Hiding keeps the native video surfaces
            // (they are released below, before anything is detached).
            HideForShutdown();
            // S4 F4: long operations whose owners end only in ui-teardown (after session-stop) are cancelled now, before the session
            // stops admitting and waits for every accepted command; otherwise the time limit would end the process in their middle.
            budget.Run("long-operations", CancelLongOperationsForShutdown);
            // Cancellation/admission must precede a potentially unbounded native gallery cleanup.
            // BeginShutdown retains the profile and permits these last writes until StopAsync seals them.
            Task engineDrain = _session.BeginShutdown();
            // A gallery that failed to close may still own its player: that counts as native ownership not proven released.
            bool galleryClosed = budget.Run("gallery-close", () => CloseGallery(restoreOrigin: false)) == ShutdownStepOutcome.Completed;
            // R10: the reader's inline players, the media sidebar's and the Media panel's are native players too. They are released
            // now, while their drawables are still attached, instead of fire-and-forget when the closed window detaches them.
            Task inlineVideos = ReleaseShellVideosForShutdown();
            Task windowPreferences = SaveWindowPreferencesAsync(booruViewerBounds);
            Task galleryPreferences = FlushGalleryPreferencesAsync();
            Task readingPosition = _readingPositionSave;
            Task finalPreferences = FlushFinalPreferencesAsync(windowPreferences, galleryPreferences, readingPosition, quickSettings);
            Log.Debug("Shutdown: engine cancellation requested; waiting independently for final writes, players and gallery cleanup.");
            await Task.WhenAll(
                budget.RunAsync("engine-drain", engineDrain),
                budget.RunAsync("final-preferences", finalPreferences),
                budget.RunAsync("booru-tabs-save", booruSearchTabs),
                budget.RunAsync("gallery-cleanup", _galleryCleanupTask),
                budget.RunAsync("inline-videos", inlineVideos),
                budget.RunAsync("session-restore", _restoreTask));
            // A faulted drain is NOT proof that its resources were released.
            nativeOwnershipReleased = galleryClosed && _galleryNativeCleanupTask.IsCompletedSuccessfully && inlineVideos.IsCompletedSuccessfully;
            if (!nativeOwnershipReleased) _session.RetainProfileForExternalCleanupFailure();
            if (!budget.TimedOut)
            {
                // The session's own drain and the profile release wait on the engine; after a step ran out of time they would too.
                Log.Debug("Shutdown: draining session; native ownership released {Released}.", nativeOwnershipReleased);
                await budget.RunAsync("session-stop", () => _session.StopAsync());
                // Dispose the profile owner before fallible UI subscription/tray teardown.
                await budget.RunAsync("session-dispose", () => _session.DisposeAsync().AsTask());
            }
            bool ownershipReleased = !budget.TimedOut && nativeOwnershipReleased && _session.IsProfileReleased;
            ShutdownEnd end = ShutdownOutcome.Decide(budget.TimedOut, ownershipReleased, budget.Failure != null, ShutdownExit.ProcessExitAllowed);
            if (end is ShutdownEnd.ExitProcess or ShutdownEnd.RetainWindow)
            {
                EndUnprovenShutdown(budget, end, nativeOwnershipReleased);
                return;
            }

            // Everything that owns the profile or a native surface is released: from here closing is always safe, so a failing or
            // slow teardown step is logged and never keeps the window.
            Log.Debug("Shutdown: session disposed; releasing UI subscriptions.");
            budget.Run("ui-teardown", ReleaseShellForShutdown);
            if (OperatingSystem.IsLinux())
            {
                // Only DBus needs its menu subscriptions detached and queued layout updates drained.
                // Avalonia 11.3.22's macOS exporter rejects Menu=null; other platforms dispose directly.
                Log.Debug("Shutdown: detaching the DBus tray menu; tray present {TrayPresent}.", _trayIcon != null);
                budget.Run("tray-menu-detach", () => { if (_trayIcon != null) _trayIcon.Menu = null; });
                await budget.RunAsync("tray-menu-drain", () => Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background).GetTask());
            }

            Log.Debug("Shutdown: disposing the tray icon; tray present {TrayPresent}.", _trayIcon != null);
            budget.Run("tray-dispose", DisposeTrayIcon);
            if (end == ShutdownEnd.AcknowledgeFailure)
            {
                Log.Error(budget.Failure, "Shutdown cleanup finished, but step {Step} failed (for example the preferences were not confirmed saved).",
                    budget.FailedStep);
                AcknowledgeShutdownFailure();
                return;
            }
            Log.Debug("Shutdown: profile lease released; closing {OwnedWindowCount} remembered owned window(s), then the window.", _shutdownOwnedWindows.Count);
            budget.Run("owned-windows-close", CloseOwnedWindowsForShutdown);
            _allowClose = true;
            Close();
            Log.Information("Application shutdown finished after {Elapsed}; window visible {IsVisible}.", ShutdownBudget.Seconds(budget.Elapsed), IsVisible);
        }
        catch (Exception exception)
        {
            // Not a step failure (those are caught by the budget): something around them. Whatever it was, the window must not stay
            // half-closed: released owners let the next close exit, unproven ones end the process where it may end itself.
            Log.Error(exception, "Failed to stop the application.");
            if (_session.IsProfileReleased && nativeOwnershipReleased) AcknowledgeShutdownFailure();
            else EndUnprovenShutdown(budget, ShutdownExit.ProcessExitAllowed ? ShutdownEnd.ExitProcess : ShutdownEnd.RetainWindow, nativeOwnershipReleased);
        }
        finally
        {
            _shutdownRunning = false;
        }
    }

    private Task ReleaseShellVideosForShutdown()
    {
        try
        {
            return Task.WhenAll(_archivePage?.ReleaseInlineVideos() ?? Task.CompletedTask, _mediaPage?.ReleasePanelVideos() ?? Task.CompletedTask);
        }
        catch (Exception error) { return Task.FromException(error); }
    }

    private void ReleaseShellForShutdown()
    {
        _session.ProjectionPublished -= OnSessionChanged;
        _session.StartupPreviewChanged -= OnStartupPreviewChanged;
        _session.CatalogReadableChanged -= OnCatalogReadableChanged;
        Texts.Changed -= OnLanguageChanged;
        DisposeWorkspace();
        _sessionRefresh.Dispose();
        foreach (ThreadRow row in _threadRows.Values)
        {
            row.Dispose();
        }

        foreach (BoardRow row in _boardRows.Values)
        {
            row.Dispose();
        }

        _clipboardMenuItem = null;
    }

    private void HideForShutdown()
    {
        try { if (IsVisible) Hide(); }
        catch (Exception error) { Log.Warning(error, "The window could not be hidden at the start of the shutdown."); }
    }

    private void ShowAfterShutdown()
    {
        try { if (!IsVisible) Show(); }
        catch (Exception error) { Log.Warning(error, "The window could not be shown again after the shutdown."); }
    }

    // Everything is released, but something failed (the last settings were not confirmed saved, a teardown step threw): the window
    // comes back once with the error, inert, and its next close ends the app at once instead of retrying against a released profile.
    private void AcknowledgeShutdownFailure()
    {
        _shutdownWatchdog?.Disarm(); // the person reads the error; the close that follows arms the exit grace (OnClosed)
        // The windows the hide disowned do not come back with the window: they are closed now, so the next close ends the lifetime.
        CloseOwnedWindowsForShutdown();
        ShowAfterShutdown();
        ShowNotification(Texts.T("notification.error"), Texts.T("shutdown.failed"), NotificationType.Error, operation: "session.shutdown");
        _allowClose = true;
    }

    // A step ran out of time, or a native player or the profile could not be proven released. Detaching the window now could pull a
    // drawable from under a player that still renders into it, so the window is not closed: the process ends instead, which releases
    // every native player and the profile lock at once. Every single write already committed is on disk (see ShutdownExit for what a
    // multi-step operation cut in its middle leaves). Where the process may not end itself (an in-process smoke host), the window and
    // profile stay retained for that host, as before.
    private void EndUnprovenShutdown(ShutdownBudget budget, ShutdownEnd end, bool nativeOwnershipReleased)
    {
        if (!nativeOwnershipReleased) _session.RetainProfileForExternalCleanupFailure();
        string reason = budget.TimedOutStep is { } step ? "step " + step + " did not finish within " + ShutdownBudget.Seconds(budget.Limit)
            : !nativeOwnershipReleased ? "a native player was not proven released" : "the profile was not proven released";
        if (end == ShutdownEnd.ExitProcess)
        {
            Log.Error(budget.Failure, "Shutdown could not finish ({Reason}; after {Elapsed}); ending the process. Committed writes are on disk; the operating system releases the players and the profile.",
                reason, ShutdownBudget.Seconds(budget.Elapsed));
            _shutdownWatchdog?.Disarm();
            ShutdownExit.Terminate(ShutdownExit.IncompleteExitCode);
            return;
        }
        Log.Error(budget.Failure, "Shutdown could not finish ({Reason}); native surface and profile remain retained for the host.", reason);
        ShowAfterShutdown();
        ShowNotification(Texts.T("notification.error"), Texts.T("shutdown.failed"), NotificationType.Error, operation: "session.shutdown");
    }
}

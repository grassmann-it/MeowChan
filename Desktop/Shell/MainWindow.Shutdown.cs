using System;
using System.Threading.Tasks;
using Avalonia.Controls;
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

        if (_config.CloseToTray && e.CloseReason == WindowCloseReason.WindowClosing)
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
    }

    private async Task CloseApplicationAsync()
    {
        if (_shutdownRunning || _allowClose)
        {
            return;
        }

        // The veto point: until here nothing has changed, so "keep editing" (or a check that fails) leaves the window fully usable.
        bool confirmed = await ShutdownPreparation.ConfirmAsync(new Func<Task<bool>>[]
        {
            () => _postBookmarkEditor?.PrepareToCloseAsync() ?? Task.FromResult(true),
            () => _postBookmarksDialog?.PrepareToCloseAsync() ?? Task.FromResult(true),
        });
        if (!confirmed)
        {
            Log.Information("Closing the application was kept back by an open dialog with unsaved changes.");
            if (_postBookmarkEditor is Window editor) editor.Activate();
            else if (_postBookmarksDialog is Window bookmarks) bookmarks.Activate();
            return;
        }
        // Another close request may have started while the dialogs were asked.
        if (_shutdownRunning || _allowClose || _isClosing)
        {
            return;
        }

        _archivePage?.FlushReadingPositionInput();
        _isClosing = true;
        StopClipboardMonitor();
        _pendingSessionProjection = null;
        _sessionRowsApplying = false;
        _sessionRowsApplied?.TrySetResult(); _sessionRowsApplied = null;
        _restoreDownloadSelection?.Dispose(); _restoreDownloadSelection = null;
        _refreshingDownloadSearch = false;
        _postBookmarksDialog?.CloseForProfileEnd();
        _postBookmarkEditor?.CloseForProfileEnd();
        _shutdownRunning = true;
        _settingsEditor?.EndLifetime();
        _pluginsPage?.EndLifetime();
        _rulesPage?.EndLifetime();
        UpdateInteractionState();
        ShutdownBudget budget = _shutdownBudget = new ShutdownBudget(TimeSpan.FromSeconds(Math.Clamp(_config.ShutdownTimeLimitSeconds,
            Config.ShutdownTimeLimitSecondsMinimum, Config.ShutdownTimeLimitSecondsMaximum)));
        if (ShutdownExit.ProcessExitAllowed)
        {
            _shutdownWatchdog ??= new ShutdownWatchdog();
            _shutdownWatchdog.Arm(budget.Limit + ShutdownExit.WatchdogGrace, budget.DescribeRunning);
        }
        Log.Information("Application shutdown started; time limit {Limit}.", ShutdownBudget.Seconds(budget.Limit));
        // Nobody is left looking at a window whose every control has stopped answering. Hiding keeps the native video surfaces
        // (they are released below, before anything is detached).
        HideForShutdown();
        bool nativeOwnershipReleased = false;
        try
        {
            // Cancellation/admission must precede a potentially unbounded native gallery cleanup.
            // BeginShutdown retains the profile and permits these last writes until StopAsync seals them.
            Task engineDrain = _session.BeginShutdown();
            // A gallery that failed to close may still own its player: that counts as native ownership not proven released.
            bool galleryClosed = budget.Run("gallery-close", () => CloseGallery(restoreOrigin: false)) == ShutdownStepOutcome.Completed;
            // R10: the reader's inline players, the media sidebar's and the Media panel's are native players too. They are released
            // now, while their drawables are still attached, instead of fire-and-forget when the closed window detaches them.
            Task inlineVideos = ReleaseShellVideosForShutdown();
            Task windowPreferences = FlushWindowPreferencesAsync();
            Task galleryPreferences = FlushGalleryPreferencesAsync();
            Task readingPosition = _readingPositionSave;
            Task finalPreferences = FlushFinalPreferencesAsync(windowPreferences, galleryPreferences, readingPosition);
            Log.Debug("Shutdown: engine cancellation requested; waiting independently for final writes, players and gallery cleanup.");
            await Task.WhenAll(
                budget.RunAsync("engine-drain", engineDrain),
                budget.RunAsync("final-preferences", finalPreferences),
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
                Log.Error(budget.Failure, "Shutdown cleanup finished, but preferences were not confirmed saved.");
                AcknowledgeShutdownFailure();
                return;
            }
            Log.Debug("Shutdown: profile lease released; requesting final window close.");
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
        ShowAfterShutdown();
        ShowNotification(Texts.T("notification.error"), Texts.T("shutdown.failed"), NotificationType.Error, operation: "session.shutdown");
        _allowClose = true;
    }

    // A step ran out of time, or a native player or the profile could not be proven released. Detaching the window now could pull a
    // drawable from under a player that still renders into it, so the window is not closed: the process ends instead, which releases
    // every native player and the profile lock at once. Committed data is already on disk (every profile write is atomic). Where the
    // process may not end itself (an in-process smoke host), the window and profile stay retained for that host, as before.
    private void EndUnprovenShutdown(ShutdownBudget budget, ShutdownEnd end, bool nativeOwnershipReleased)
    {
        if (!nativeOwnershipReleased) _session.RetainProfileForExternalCleanupFailure();
        string reason = budget.TimedOutStep is { } step ? "step " + step + " did not finish within " + ShutdownBudget.Seconds(budget.Limit)
            : !nativeOwnershipReleased ? "a native player was not proven released" : "the profile was not proven released";
        if (end == ShutdownEnd.ExitProcess)
        {
            Log.Error(budget.Failure, "Shutdown could not finish ({Reason}; after {Elapsed}); ending the process. Saved data is complete; the operating system releases the players and the profile.",
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

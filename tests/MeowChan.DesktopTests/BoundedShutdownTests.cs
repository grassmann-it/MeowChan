using System.Diagnostics;
using System.Runtime.CompilerServices;
using MeowChan.Desktop;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace MeowChan.DesktopTests;

// Bounded shutdown (16.11.2026, Tom: the window stayed visible but dead after X, the process had to be killed).
public sealed class BoundedShutdownTests
{
    [Fact]
    public async Task AHangingStepEndsWithinTheLimitIsLoggedByNameAndTheStepsAfterItAreSkipped()
    {
        (ILogger log, CollectingSink sink) = Logger();
        ShutdownBudget budget = new(TimeSpan.FromMilliseconds(300), log);
        TaskCompletionSource never = new();
        Stopwatch watch = Stopwatch.StartNew();

        ShutdownStepOutcome[] first = await Task.WhenAll(
            budget.RunAsync("engine-drain", never.Task),
            budget.RunAsync("final-preferences", Task.Delay(20)));
        ShutdownStepOutcome after = await budget.RunAsync("session-stop", () => throw new InvalidOperationException("must not start"));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "The shutdown waited past its limit: " + watch.Elapsed);
        Assert.Equal(new[] { ShutdownStepOutcome.TimedOut, ShutdownStepOutcome.Completed }, first);
        Assert.Equal(ShutdownStepOutcome.Skipped, after);
        Assert.True(budget.TimedOut);
        Assert.Equal("engine-drain", budget.TimedOutStep);
        Assert.Null(budget.Failure);
        LogEvent warning = Assert.Single(sink.Events, e => e.Level == LogEventLevel.Warning && e.MessageTemplate.Text.Contains("it ran"));
        Assert.Equal("\"engine-drain\"", warning.Properties["Step"].ToString());
        Assert.True(warning.Properties.ContainsKey("Elapsed"));
        Assert.Contains(sink.Events, e => e.Level == LogEventLevel.Warning && e.MessageTemplate.Text.Contains("skipped")
            && e.Properties["Step"].ToString() == "\"session-stop\"");
        // The hung step ends the process where the app may end itself, and keeps the retained window in an in-process host.
        Assert.Equal(ShutdownEnd.ExitProcess, ShutdownOutcome.Decide(budget.TimedOut, false, budget.Failure != null, processExitAllowed: true));
        Assert.Equal(ShutdownEnd.RetainWindow, ShutdownOutcome.Decide(budget.TimedOut, false, budget.Failure != null, processExitAllowed: false));

        // Its late end still reaches the log.
        never.SetResult();
        await WaitUntil(() => sink.Events.Any(e => e.MessageTemplate.Text.Contains("finished late")));
    }

    [Fact]
    public async Task AThrowingStepIsLoggedAndTheOtherStepsStillRun()
    {
        (ILogger log, CollectingSink sink) = Logger();
        ShutdownBudget budget = new(TimeSpan.FromSeconds(10), log);
        IOException disk = new("disk full");
        bool laterRan = false;

        ShutdownStepOutcome[] outcomes = await Task.WhenAll(
            budget.RunAsync("final-preferences", Task.FromException(disk)),
            budget.RunAsync("gallery-cleanup", Task.CompletedTask),
            budget.RunAsync("inline-videos", () => throw new InvalidOperationException("synchronous start failure")));
        ShutdownStepOutcome later = await budget.RunAsync("session-stop", () => { laterRan = true; return Task.CompletedTask; });
        ShutdownStepOutcome sync = budget.Run("ui-teardown", () => throw new InvalidOperationException("teardown"));

        Assert.Equal(new[] { ShutdownStepOutcome.Failed, ShutdownStepOutcome.Completed, ShutdownStepOutcome.Failed }, outcomes);
        Assert.Equal(ShutdownStepOutcome.Completed, later);
        Assert.Equal(ShutdownStepOutcome.Failed, sync);
        Assert.True(laterRan);
        Assert.False(budget.TimedOut);
        Assert.Same(disk, budget.Failure);
        Assert.Equal(3, sink.Events.Count(e => e.Level == LogEventLevel.Warning && e.MessageTemplate.Text.Contains("failed")));
        // Everything released, one step failed: the window shows the error once and its next close exits.
        Assert.Equal(ShutdownEnd.AcknowledgeFailure, ShutdownOutcome.Decide(false, true, true, processExitAllowed: true));
        Assert.Equal(ShutdownEnd.CloseWindow, ShutdownOutcome.Decide(false, true, false, processExitAllowed: true));
        // An owner not proven released never closes the window in-process.
        Assert.Equal(ShutdownEnd.ExitProcess, ShutdownOutcome.Decide(false, false, false, processExitAllowed: true));
    }

    [Fact]
    public void TheBudgetNamesTheRunningSynchronousStepForTheWatchdog()
    {
        ShutdownBudget budget = new(TimeSpan.FromSeconds(10), Logger().Log);
        string? seen = null;
        budget.Run("tray-dispose", () => seen = budget.DescribeRunning());
        Assert.StartsWith("tray-dispose (", seen);
        Assert.Equal("none", budget.DescribeRunning());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShutdownBudget(TimeSpan.Zero));
    }

    [Fact]
    public async Task TheWatchdogEndsTheProcessWithTheRunningStepsAndADisarmedOneDoesNot()
    {
        (ILogger log, CollectingSink sink) = Logger();
        TaskCompletionSource<int> terminated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ShutdownWatchdog watchdog = new(code => terminated.TrySetResult(code), log);
        watchdog.Arm(TimeSpan.FromMilliseconds(50), () => "tray-dispose (12.0 s)");
        Assert.Equal(ShutdownExit.IncompleteExitCode, await terminated.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(watchdog.Fired);
        LogEvent error = Assert.Single(sink.Events, e => e.Level == LogEventLevel.Error);
        Assert.Contains("tray-dispose", error.RenderMessage());

        int calls = 0;
        using ShutdownWatchdog disarmed = new(_ => Interlocked.Increment(ref calls), log);
        disarmed.Arm(TimeSpan.FromMilliseconds(100), () => "none");
        Assert.True(disarmed.Armed);
        disarmed.Disarm();
        await Task.Delay(400);
        Assert.Equal(0, calls);
        Assert.False(disarmed.Fired);
    }

    [Fact]
    public async Task AVetoOrAFailingCheckKeepsTheWindowAndLaterChecksAreNotAsked()
    {
        int asked = 0;
        Assert.True(await ShutdownPreparation.ConfirmAsync(new Func<Task<bool>>[] { () => Task.FromResult(true), () => Task.FromResult(true) }, Logger().Log));
        Assert.False(await ShutdownPreparation.ConfirmAsync(new Func<Task<bool>>[]
            { () => Task.FromResult(false), () => { asked++; return Task.FromResult(true); } }, Logger().Log));
        Assert.False(await ShutdownPreparation.ConfirmAsync(new Func<Task<bool>>[]
            { () => throw new InvalidOperationException("dialog gone"), () => { asked++; return Task.FromResult(true); } }, Logger().Log));
        Assert.Equal(0, asked);
    }

    [Fact]
    public void TheWindowChangesNothingBeforeTheVetoHidesAtOnceAndNeverStaysHalfClosed()
    {
        string source = Source("Desktop/Shell/MainWindow.Shutdown.cs");
        string close = source[source.IndexOf("private async Task CloseApplicationAsync()", StringComparison.Ordinal)..];
        int veto = close.IndexOf("ShutdownPreparation.ConfirmAsync", StringComparison.Ordinal);
        int closing = close.IndexOf("_isClosing = true;", StringComparison.Ordinal);
        int hide = close.IndexOf("HideForShutdown();", StringComparison.Ordinal);
        int begin = close.IndexOf("_session.BeginShutdown()", StringComparison.Ordinal);
        Assert.True(veto > 0 && veto < closing && closing < hide && hide < begin, "veto, then closing state, then hide, then the session stop");
        // Every awaited step goes through the budget; none is awaited bare.
        foreach (string step in new[] { "engine-drain", "final-preferences", "gallery-cleanup", "inline-videos", "session-restore", "session-stop", "session-dispose", "tray-menu-drain" })
            Assert.Contains("budget.RunAsync(\"" + step + "\"", close);
        Assert.DoesNotContain("await _session.StopAsync()", close);
        Assert.DoesNotContain("await Task.WhenAll(engineDrain", close);
        // The failure ends either exit the process or let the next close exit; the retained window is only for in-process hosts.
        string acknowledge = source[source.IndexOf("private void AcknowledgeShutdownFailure()", StringComparison.Ordinal)..];
        Assert.Contains("_allowClose = true;", acknowledge[..acknowledge.IndexOf('}')]);
        Assert.Contains("ShutdownExit.Terminate(ShutdownExit.IncompleteExitCode)", source);
        Assert.DoesNotContain("ShutdownExit.ProcessExitAllowed", Source("Program.cs"));
        Assert.Contains("ShutdownExit.ProcessExitAllowed = false;", Source("Program.Development.cs"));
        Assert.Contains("Assembly.GetEntryAssembly() == typeof(ShutdownExit).Assembly", Source("Desktop/BoundedShutdown.cs"));
        // The tray cannot bring the hidden window back while the shutdown runs.
        string window = Source("Desktop/Shell/MainWindow.axaml.cs");
        string restore = window[window.IndexOf("private void RestoreWindow()", StringComparison.Ordinal)..];
        Assert.Contains("if (_shutdownRunning) return;", restore[..restore.IndexOf("Show();", StringComparison.Ordinal)]);
    }

    // ---- S4 (16.11.2026): completing the shutdown ----

    // F2: a call that throws while the shutdown starts must not leave the visible, dead window. Each preparing call is a step of its own:
    // a throw is recorded with the step's name, the steps after it still run (the hide among them), and the end is the ordinary one.
    [Fact]
    public void AThrowingPreparingStepIsRecordedByNameAndTheShutdownStillRunsToItsEnd()
    {
        (ILogger log, CollectingSink sink) = Logger();
        ShutdownBudget budget = new(TimeSpan.FromSeconds(10), log);
        List<string> ran = new();

        ShutdownStepOutcome clipboard = budget.Run("clipboard-monitor", () => throw new InvalidOperationException("clipboard gone"));
        ShutdownStepOutcome pages = budget.Run("page-lifetimes", () => ran.Add("page-lifetimes"));
        ShutdownStepOutcome state = budget.Run("interaction-state", () => throw new NullReferenceException("a control was gone"));
        ran.Add("hide"); // HideForShutdown is not a step: it catches its own failure
        ShutdownStepOutcome operations = budget.Run("long-operations", () => ran.Add("long-operations"));

        Assert.Equal(new[] { ShutdownStepOutcome.Failed, ShutdownStepOutcome.Completed, ShutdownStepOutcome.Failed, ShutdownStepOutcome.Completed },
            new[] { clipboard, pages, state, operations });
        Assert.Equal(new[] { "page-lifetimes", "hide", "long-operations" }, ran);
        Assert.Equal("clipboard-monitor", budget.FailedStep); // the first failure is kept, with its step
        Assert.IsType<InvalidOperationException>(budget.Failure);
        Assert.False(budget.TimedOut);
        Assert.Equal(2, sink.Events.Count(e => e.Level == LogEventLevel.Warning && e.MessageTemplate.Text.Contains("failed")));
        // Everything released, a preparing step failed: the window shows the error once and its next close exits (never the dead window).
        Assert.Equal(ShutdownEnd.AcknowledgeFailure, ShutdownOutcome.Decide(budget.TimedOut, true, budget.Failure != null, processExitAllowed: true));
        Assert.Null(new ShutdownBudget(TimeSpan.FromSeconds(1), log).FailedStep);
    }

    [Fact]
    public void BetweenTheVetoAndTheTryOnlyTheBudgetAndTheWatchdogAreSetUpAndEveryPreparingCallIsAStep()
    {
        string close = CloseApplication();
        int recheck = close.IndexOf("if (_shutdownRunning || _allowClose || _isClosing)", StringComparison.Ordinal);
        int tryIndex = close.IndexOf("        try\n        {", recheck, StringComparison.Ordinal);
        Assert.True(recheck > 0 && tryIndex > recheck, "the re-check after the veto, then the try");
        string between = close[recheck..tryIndex];
        between = between[(between.IndexOf("return;", StringComparison.Ordinal) + "return;".Length)..];
        string[] allowed =
        {
            "//", "{", "}", "ShutdownBudget budget = _shutdownBudget = new ShutdownBudget(", "Config.ShutdownTimeLimitSecondsMinimum,",
            "if (ShutdownExit.ProcessExitAllowed)", "_shutdownWatchdog ??= new ShutdownWatchdog();", "_shutdownWatchdog.Arm(budget.Limit",
            "Log.Information(\"Application shutdown started", "bool nativeOwnershipReleased = false;",
        };
        foreach (string line in between.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            Assert.True(allowed.Any(start => trimmed.StartsWith(start, StringComparison.Ordinal)),
                "Only the budget and the watchdog may sit between the veto and the try; a call here could leave the window dead: " + trimmed);
        }
        // The closing state is set inside the try, and the calls that used to run unguarded before it are steps now.
        Assert.True(close.IndexOf("_isClosing = true;", StringComparison.Ordinal) > tryIndex);
        foreach (string step in new[]
        {
            "budget.Run(\"input-flush\"", "budget.Run(\"clipboard-monitor\", StopClipboardMonitor);", "budget.Run(\"session-rows\", DropPendingSessionRowsForShutdown);",
            "budget.Run(\"bookmark-dialogs\"", "budget.Run(\"page-lifetimes\"", "budget.Run(\"interaction-state\", UpdateInteractionState);",
            "budget.Run(\"owned-windows\", CaptureOwnedWindowsForShutdown);", "budget.Run(\"long-operations\", CancelLongOperationsForShutdown);",
        })
            Assert.Contains(step, close);
        string body = close[..close.IndexOf("private Task ReleaseShellVideosForShutdown()", StringComparison.Ordinal)];
        foreach (string call in new[] { "StopClipboardMonitor();", "UpdateInteractionState();", "_restoreDownloadSelection?.Dispose();" })
            Assert.DoesNotContain(call, body);
    }

    // F1: Avalonia's Window.Hide hides and disowns the owned windows, so the final Close no longer closes them and the lifetime would wait
    // for them until the watchdog ends a clean close with exit code 3. (A windowed check with F1 help open belongs to the smoke harness.)
    [Fact]
    public void TheOwnedWindowsAreRememberedBeforeTheHideAndClosedBeforeTheFinalCloseAndAfterAFailure()
    {
        string source = Source("Desktop/Shell/MainWindow.Shutdown.cs");
        string close = CloseApplication();
        int capture = close.IndexOf("CaptureOwnedWindowsForShutdown", StringComparison.Ordinal);
        int hide = close.IndexOf("HideForShutdown();", StringComparison.Ordinal);
        Assert.True(capture > 0 && capture < hide, "the owned windows are remembered before the hide disowns them");
        int closeOwned = close.IndexOf("budget.Run(\"owned-windows-close\", CloseOwnedWindowsForShutdown);", StringComparison.Ordinal);
        int finalClose = close.IndexOf("_allowClose = true;\n            Close();", StringComparison.Ordinal);
        Assert.True(closeOwned > 0 && closeOwned < finalClose, "the remembered windows close before the window's own final close");
        string acknowledge = source[source.IndexOf("private void AcknowledgeShutdownFailure()", StringComparison.Ordinal)..];
        acknowledge = acknowledge[..acknowledge.IndexOf("_allowClose = true;", StringComparison.Ordinal)];
        Assert.Contains("CloseOwnedWindowsForShutdown();", acknowledge);
        string capturing = source[source.IndexOf("private void CaptureOwnedWindowsForShutdown()", StringComparison.Ordinal)..];
        Assert.Contains("OwnedWindows.ToArray()", capturing[..capturing.IndexOf("private void OnShutdownOwnedWindowClosed", StringComparison.Ordinal)]);
        // A window that refused its close cannot hold the lifetime: after the main window closed, the app shuts down over it.
        string closed = source[source.IndexOf("protected override void OnClosed(EventArgs e)", StringComparison.Ordinal)..];
        closed = closed[..closed.IndexOf("private static string DescribeWindow", StringComparison.Ordinal)];
        Assert.True(closed.IndexOf("if (!ShutdownExit.ProcessExitAllowed) return;", StringComparison.Ordinal)
            < closed.IndexOf("EndLifetimeAfterWindowClosed", StringComparison.Ordinal), "only where the process may end itself");
        Assert.Contains("if (desktop.Windows.Count == 0) return;", closed);
        Assert.Contains("desktop.Shutdown();", closed);
    }

    // F3: a quick setting still waiting in its pause when the app quits is saved. The applier flushes at the shutdown start; the session
    // takes the write after BeginShutdown and keeps it with the final preferences; only the seal of session-stop refuses later writes.
    [Fact]
    public async Task AQuickSettingWaitingInItsPauseWhenTheAppQuitsIsSavedWithTheFinalPreferences()
    {
        using TempDirectory directory = new();
        new Config().SaveConfig(directory.Path);
        DesktopSessionHost host = new(directory.Path, Array.Empty<MeowChan.Interfaces.IImageBoard>(), (_, _) => new EmptySessionStore());
        bool disposed = false;
        try
        {
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(ReaderFontSizeSteps.Default, host.Config.ReadingFontSize);
            // The pause is long: only the flush at the shutdown start can save it (what MainWindow.SaveQuickSettingsAsync does while closing).
            QuickSettingsApplier applier = new(change => host.SaveViewPreferencesAsync(change), TimeSpan.FromHours(1));
            List<IReadOnlyList<string>> failed = new();
            applier.Failed += (ids, _) => failed.Add(ids);
            Assert.True(applier.Set(ReaderFontSizeSteps.Field, "Large"));
            Assert.Equal(1, applier.PendingCount);

            Task quickSettings = applier.FlushAsync(); // CloseApplicationAsync: before _isClosing, before BeginShutdown
            Task engineDrain = host.BeginShutdown();
            await quickSettings.WaitAsync(TimeSpan.FromSeconds(10));
            await host.FlushPreferencesAsync().WaitAsync(TimeSpan.FromSeconds(10)); // FlushFinalPreferencesAsync after the shell writes
            await engineDrain.WaitAsync(TimeSpan.FromSeconds(10));
            await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Empty(failed);
            Assert.Equal(0, applier.PendingCount);
            // After the seal the session refuses the write itself, and the applier reports it as any failed save.
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.SaveViewPreferencesAsync(config => config.ReadingFontSize = "Small"));
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            disposed = true;
            Assert.Equal("Large", Config.LoadConfig(directory.Path).ReadingFontSize);
        }
        finally { if (!disposed) await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public void TheQuickSettingsAreFlushedBeforeTheClosingStateAndAWriteWhileClosingIsNotRefusedByTheWindow()
    {
        string close = CloseApplication();
        int flush = close.IndexOf("_archivePage?.FlushQuickSettingsAsync()", StringComparison.Ordinal);
        Assert.True(flush > 0 && flush < close.IndexOf("_isClosing = true;", StringComparison.Ordinal));
        Assert.Contains("FlushFinalPreferencesAsync(windowPreferences, galleryPreferences, readingPosition, quickSettings)", close);
        string quick = Source("Desktop/Shell/MainWindow.QuickSettings.cs");
        string save = quick[quick.IndexOf("private async Task SaveQuickSettingsAsync", StringComparison.Ordinal)..];
        save = save[..save.IndexOf("\n    }", StringComparison.Ordinal)];
        Assert.DoesNotContain("throw", save);
        Assert.Contains("await _session.SaveViewPreferencesAsync(change);", save);
        string page = Source("Desktop/Views/Archive/ArchivePage.QuickSettings.cs");
        string pageFlush = page[page.IndexOf("internal Task FlushQuickSettingsAsync()", StringComparison.Ordinal)..];
        pageFlush = pageFlush[..pageFlush.IndexOf("private void CloseQuickSettings()", StringComparison.Ordinal)];
        Assert.Contains("_readerQuickSettings?.Surface.FlushAsync()", pageFlush);
        Assert.Contains("_readerFontApplier?.FlushAsync()", pageFlush);
    }

    // F4: long operations whose owners end only in ui-teardown (after session-stop) are cancelled before the session stops; the
    // permanent deletion, which cannot stop half-way, is asked at the veto point instead of being cut by the time limit.
    [Fact]
    public void LongOperationsAreCancelledBeforeTheSessionStopsAndARunningDeletionIsAskedAtTheVeto()
    {
        string source = Source("Desktop/Shell/MainWindow.Shutdown.cs");
        string close = CloseApplication();
        int cancel = close.IndexOf("budget.Run(\"long-operations\", CancelLongOperationsForShutdown);", StringComparison.Ordinal);
        Assert.True(cancel > 0 && cancel < close.IndexOf("_session.BeginShutdown()", StringComparison.Ordinal));
        string operations = source[source.IndexOf("private void CancelLongOperationsForShutdown()", StringComparison.Ordinal)..];
        Assert.Contains("import.Session.Cancel();", operations);
        Assert.Contains("_archivePage?.CancelLongOperations();", operations);
        Assert.Contains("_cleanupDialog?.CancelForShutdown()", Source("Desktop/Views/Archive/ArchivePage.Cleanup.cs"));

        // The deletion question is the first check of the veto, so the editors are asked with what they hold after the wait.
        int veto = close.IndexOf("ShutdownPreparation.ConfirmAsync", StringComparison.Ordinal);
        string checks = close[veto..close.IndexOf("});", veto, StringComparison.Ordinal)];
        Assert.True(checks.IndexOf("ConfirmCloseDuringArchiveDeletionAsync,", StringComparison.Ordinal)
            < checks.IndexOf("_postBookmarkEditor?.PrepareToCloseAsync()", StringComparison.Ordinal));
        // Every step the deletion dialog runs without a way to stop half-way marks the run, and every end clears it.
        string deletion = Source("Desktop/Shell/MainWindow.ArchiveDeletion.cs");
        Assert.Equal(3, Occurrences(deletion, "executing = true; BeginArchiveDeletionRun();").Count());
        Assert.Equal(Occurrences(deletion, "executing = true;").Count(), Occurrences(deletion, "executing = true; BeginArchiveDeletionRun();").Count());
        Assert.Equal(Occurrences(deletion, "finally { executing = false;").Count(), Occurrences(deletion, "finally { executing = false; EndArchiveDeletionRun();").Count());
        int marked = deletion.IndexOf("executing = true; BeginArchiveDeletionRun(); delete.IsEnabled", StringComparison.Ordinal);
        Assert.True(marked > 0 && marked < deletion.IndexOf("await _session.DeleteArchivePermanentlyAsync(plan)", StringComparison.Ordinal));
        string ask = deletion[deletion.IndexOf("private async Task<bool> ConfirmCloseDuringArchiveDeletionAsync()", StringComparison.Ordinal)..];
        ask = ask[..ask.IndexOf("\n    }\n", StringComparison.Ordinal)];
        Assert.Contains("if (_archiveDeletionRun == null) return true;", ask);
        Assert.Contains("if (!quitAfter) return false;", ask);
        Assert.Contains("await run.Task;", ask);
        foreach (string key in new[] { "shutdown.deletion.running", "shutdown.deletion.quitAfter", "shutdown.deletion.keepOpen" })
        {
            Assert.Contains("Texts.T(\"" + key + "\")", ask);
            Assert.Contains("(\"" + key + "\", ", Source("Desktop/Localization/BuiltInCatalog.cs"));
        }
        // The data-safety note no longer claims that ending the process loses at most one write.
        Assert.DoesNotContain("loses at most the write still in flight", Source("Desktop/BoundedShutdown.cs"));
    }

    // ---- the setting ----

    [Fact]
    public void TheShutdownTimeLimitHasADefaultBoundsAndSurvivesEveryCopyAndSaveReload()
    {
        Assert.Equal(30, new Config().ShutdownTimeLimitSeconds);
        Assert.Equal((5, 600), (Config.ShutdownTimeLimitSecondsMinimum, Config.ShutdownTimeLimitSecondsMaximum));
        using TempDirectory directory = new();
        Config original = new() { ShutdownTimeLimitSeconds = 600 };
        Config live = new();
        live.CopySettingsFieldsFrom(original.Clone());
        Config copy = new();
        copy.CopyFrom(live);
        Assert.Equal(600, copy.ShutdownTimeLimitSeconds);
        Assert.Equal(600, SettingsTransfer.CreatePortableSnapshot(copy).ShutdownTimeLimitSeconds);
        copy.SaveConfig(directory.Path);
        Assert.Equal(600, Config.LoadConfig(directory.Path).ShutdownTimeLimitSeconds);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(601)]
    [InlineData(0)]
    public void AnOutOfRangeLimitIsRefusedWhenEditedAndClampedWhenLoaded(int seconds)
    {
        Config invalid = new() { ShutdownTimeLimitSeconds = seconds };
        Assert.Throws<ArgumentOutOfRangeException>(() => invalid.Validate());
        using TempDirectory directory = new();
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), "{\"ShutdownTimeLimitSeconds\":" + seconds + "}");
        Assert.Equal(Math.Clamp(seconds, 5, 600), Config.LoadConfig(directory.Path).ShutdownTimeLimitSeconds);
    }

    // ---- guard resets (the other half: an error left a guard set and later clicks did nothing) ----

    [Fact]
    public void AFailingRowFrameClearsTheRowCommandGuard()
    {
        string window = Source("Desktop/Shell/MainWindow.axaml.cs");
        string continueRows = window[window.IndexOf("private void ContinueSessionRows()", StringComparison.Ordinal)..];
        continueRows = continueRows[..continueRows.IndexOf("private void ContinueSessionRowsCore()", StringComparison.Ordinal)];
        Assert.Contains("try { ContinueSessionRowsCore(); }", continueRows);
        Assert.Contains("AbandonSessionRows();", continueRows);
        string refresh = window[window.IndexOf("private void RefreshSession()", StringComparison.Ordinal)..];
        refresh = refresh[..refresh.IndexOf("private void AbandonSessionRows()", StringComparison.Ordinal)];
        Assert.Contains("try { BeginSessionRows(projection); }", refresh);
        string abandon = window[window.IndexOf("private void AbandonSessionRows()", StringComparison.Ordinal)..];
        abandon = abandon[..abandon.IndexOf("if (_isClosing) return;", StringComparison.Ordinal)];
        Assert.Contains("_sessionRowsApplying = false;", abandon);
        Assert.Contains("_sessionRowsApplied?.TrySetResult();", abandon);
        Assert.Contains("_appliedSessionProjection = null;", abandon); // the next projection rebuilds
    }

    [Fact]
    public void TheArchiveReaderAndListSetTheirBusyGuardOnlyRightBeforeTheTryThatClearsIt()
    {
        string page = Source("Desktop/Views/Archive/ArchivePage.axaml.cs");
        int found = 0;
        foreach (int index in Occurrences(page, "        _busy = true;\n"))
        {
            string after = page[(index + "        _busy = true;\n".Length)..];
            int nextTry = after.IndexOf("        try", StringComparison.Ordinal);
            string between = after[..nextTry];
            // Only plain assignments and declarations may sit between the guard and its try.
            foreach (string line in between.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = line.Trim();
                Assert.True(trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.EndsWith("= true;", StringComparison.Ordinal)
                    || trimmed.StartsWith("ResourceId? selected", StringComparison.Ordinal) || trimmed.StartsWith("bool committed", StringComparison.Ordinal),
                    "A call between `_busy = true;` and its try could leave the reader disabled: " + trimmed);
            }
            found++;
        }
        Assert.True(found >= 2);
    }

    // ---- plumbing ----

    private static IEnumerable<int> Occurrences(string text, string value)
    {
        for (int index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + 1, StringComparison.Ordinal))
            yield return index;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "Condition not reached.");
            await Task.Delay(10);
        }
    }

    private static (ILogger Log, CollectingSink Sink) Logger()
    {
        CollectingSink sink = new();
        return (new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger(), sink);
    }

    private sealed class CollectingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = new();
        internal IReadOnlyList<LogEvent> Events { get { lock (_events) return _events.ToArray(); } }
        public void Emit(LogEvent logEvent) { lock (_events) _events.Add(logEvent); }
    }

    private sealed class TempDirectory : IDisposable
    {
        internal string Path { get; } = MeowChan.Testing.TemporaryWorkspacePaths.CreateDirectory("shutdown-limit-");
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch (IOException) { } }
    }

    private static string Root([CallerFilePath] string sourcePath = "") =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(sourcePath)!, "../.."));

    private static string Source(string relative) => File.ReadAllText(System.IO.Path.Combine(Root(), relative)).Replace("\r\n", "\n");

    private static string CloseApplication()
    {
        string source = Source("Desktop/Shell/MainWindow.Shutdown.cs");
        return source[source.IndexOf("private async Task CloseApplicationAsync()", StringComparison.Ordinal)..];
    }

    private sealed class EmptySessionStore : MeowChan.Persistence.ISessionStore
    {
        public Task<MeowChan.Persistence.SessionLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new MeowChan.Persistence.SessionLoadResult(new MeowChan.Persistence.SessionSnapshot(), null, false));
        public Task SaveAsync(MeowChan.Persistence.SessionSnapshot snapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

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
        Assert.Contains("ShutdownExit.ProcessExitAllowed = StartupSmoke == null;", Source("Program.cs"));
        // The tray cannot bring the hidden window back while the shutdown runs.
        string window = Source("Desktop/Shell/MainWindow.axaml.cs");
        string restore = window[window.IndexOf("private void RestoreWindow()", StringComparison.Ordinal)..];
        Assert.Contains("if (_shutdownRunning) return;", restore[..restore.IndexOf("Show();", StringComparison.Ordinal)]);
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

    private static string Source(string relative) => File.ReadAllText(System.IO.Path.Combine(Root(), relative));
}

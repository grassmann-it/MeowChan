using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace MeowChan.Desktop;

internal enum ShutdownStepOutcome { Completed, Failed, TimedOut, Skipped }

/// <summary>
/// The shutdown time limit (the setting "Shutdown time limit", <see cref="Config.ShutdownTimeLimitSecondsDefault"/> s by default): every
/// awaited shutdown step shares one deadline. A step that does not finish by then is logged with its name and how long it ran, and the
/// steps after it are skipped, because they wait on the same drain. A step that fails is logged and the others still run. Nothing here
/// cancels a step: when the limit is spent, the process exit ends what is still running (see <see cref="ShutdownExit"/>).
/// </summary>
internal sealed class ShutdownBudget
{
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly long _started;
    private readonly object _sync = new();
    private readonly Dictionary<string, long> _running = new(StringComparer.Ordinal);
    private string? _timedOut;
    private string? _failedStep;
    private Exception? _failure;

    internal ShutdownBudget(TimeSpan limit, ILogger? log = null, TimeProvider? time = null)
    {
        if (limit <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(limit), "The shutdown time limit must be positive.");
        Limit = limit;
        _log = log ?? Log.Logger;
        _time = time ?? TimeProvider.System;
        _started = _time.GetTimestamp();
    }

    internal TimeSpan Limit { get; }
    internal TimeSpan Elapsed => _time.GetElapsedTime(_started);
    internal TimeSpan Remaining { get { TimeSpan left = Limit - Elapsed; return left > TimeSpan.Zero ? left : TimeSpan.Zero; } }

    /// <summary>The first step that did not finish within the limit; null while every awaited step finished.</summary>
    internal string? TimedOutStep { get { lock (_sync) return _timedOut; } }
    internal bool TimedOut => TimedOutStep != null;

    /// <summary>The first failure of a step (the steps after it still ran).</summary>
    internal Exception? Failure { get { lock (_sync) return _failure; } }

    /// <summary>The name of the step that failed first (see <see cref="Failure"/>); null while no step failed.</summary>
    internal string? FailedStep { get { lock (_sync) return _failedStep; } }

    /// <summary>The steps running now with how long each has run, for the watchdog's message ("none" when nothing runs).</summary>
    internal string DescribeRunning()
    {
        lock (_sync)
        {
            if (_running.Count == 0) return "none";
            return string.Join(", ", _running.Select(step => step.Key + " (" + Seconds(_time.GetElapsedTime(step.Value)) + ")"));
        }
    }

    internal Task<ShutdownStepOutcome> RunAsync(string step, Task task) => RunAsync(step, () => task);

    /// <summary>Awaits one step within what is left of the limit. Never throws; the outcome says what happened and the log says why.</summary>
    internal async Task<ShutdownStepOutcome> RunAsync(string step, Func<Task> start)
    {
        string? earlier = TimedOutStep;
        if (earlier != null)
        {
            _log.Warning("Shutdown step {Step} was skipped: step {TimedOutStep} did not finish within the shutdown time limit of {Limit}.",
                step, earlier, Seconds(Limit));
            return ShutdownStepOutcome.Skipped;
        }
        long begun = Enter(step);
        try
        {
            Task task;
            try { task = start() ?? Task.CompletedTask; }
            catch (Exception error) { RecordFailure(step, error, begun); return ShutdownStepOutcome.Failed; }
            try
            {
                await task.WaitAsync(Remaining, _time);
                _log.Debug("Shutdown step {Step} finished after {Elapsed}.", step, Seconds(_time.GetElapsedTime(begun)));
                return ShutdownStepOutcome.Completed;
            }
            catch (TimeoutException) when (!task.IsCompleted)
            {
                lock (_sync) _timedOut ??= step;
                _log.Warning("Shutdown step {Step} did not finish within the shutdown time limit of {Limit}; it ran {Elapsed}.",
                    step, Seconds(Limit), Seconds(_time.GetElapsedTime(begun)));
                ObserveLate(step, task, begun);
                return ShutdownStepOutcome.TimedOut;
            }
            catch (Exception) when (task.IsCompletedSuccessfully)
            {
                // The step finished in the same instant the limit ran out.
                return ShutdownStepOutcome.Completed;
            }
            catch (Exception error)
            {
                RecordFailure(step, task.Exception?.InnerExceptions.Count == 1 ? task.Exception.InnerException! : error, begun);
                return ShutdownStepOutcome.Failed;
            }
        }
        finally { Leave(step); }
    }

    /// <summary>
    /// A synchronous step (UI teardown, the tray icon): it cannot be cut off on its own thread, but while it runs the watchdog names it.
    /// A failure is recorded like an awaited step's.
    /// </summary>
    internal ShutdownStepOutcome Run(string step, Action action)
    {
        long begun = Enter(step);
        try
        {
            action();
            _log.Debug("Shutdown step {Step} finished after {Elapsed}.", step, Seconds(_time.GetElapsedTime(begun)));
            return ShutdownStepOutcome.Completed;
        }
        catch (Exception error)
        {
            RecordFailure(step, error, begun);
            return ShutdownStepOutcome.Failed;
        }
        finally { Leave(step); }
    }

    private long Enter(string step)
    {
        long begun = _time.GetTimestamp();
        lock (_sync) _running[step] = begun;
        return begun;
    }

    private void Leave(string step) { lock (_sync) _running.Remove(step); }

    private void RecordFailure(string step, Exception error, long begun)
    {
        lock (_sync)
        {
            if (_failure == null) { _failure = error; _failedStep = step; }
        }
        _log.Warning(error, "Shutdown step {Step} failed after {Elapsed}.", step, Seconds(_time.GetElapsedTime(begun)));
    }

    // A step that ran out of time is still observed: its late end (or failure) lands in the log while the process is still there.
    private void ObserveLate(string step, Task task, long begun) => task.ContinueWith(done =>
    {
        if (done.IsFaulted) _log.Warning(done.Exception, "Shutdown step {Step} failed after the time limit, after {Elapsed}.", step, Seconds(_time.GetElapsedTime(begun)));
        else _log.Information("Shutdown step {Step} finished late, after {Elapsed}.", step, Seconds(_time.GetElapsedTime(begun)));
    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    internal static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
}

/// <summary>How a shutdown ends, from what it could prove.</summary>
internal enum ShutdownEnd
{
    /// <summary>Every step finished and every owner is released: the window closes and the process ends normally.</summary>
    CloseWindow,
    /// <summary>Everything is released but a step failed (for example the last settings were not confirmed saved): the window shows
    /// the error once and its next close ends the app at once.</summary>
    AcknowledgeFailure,
    /// <summary>A step ran out of time or an owner could not be proven released: the process ends itself.</summary>
    ExitProcess,
    /// <summary>As <see cref="ExitProcess"/>, in a host that must not end the process (smokes, tests): the window and profile stay retained.</summary>
    RetainWindow
}

internal static class ShutdownOutcome
{
    internal static ShutdownEnd Decide(bool timedOut, bool ownershipReleased, bool failed, bool processExitAllowed)
    {
        if (timedOut || !ownershipReleased) return processExitAllowed ? ShutdownEnd.ExitProcess : ShutdownEnd.RetainWindow;
        return failed ? ShutdownEnd.AcknowledgeFailure : ShutdownEnd.CloseWindow;
    }
}

/// <summary>
/// The last resort of a shutdown that cannot finish in-process. Only the app's own process may end itself (Program.Main allows it for a
/// normal start); an in-process host such as a smoke keeps the window instead. The profile lock and every native player end with the
/// process.
/// <para>
/// What ending the process keeps: every single write that was committed. Profile files are written aside and renamed, media through
/// <c>.part</c> files, and SQLite commits through its rollback journal, so no file is left half written and the write in flight is
/// simply lost. A multi-step operation, however, is not one write: cut between two of its steps it keeps the steps already committed.
/// That is why the shutdown start cancels the long operations that can stop cleanly (a manual import stops after the current file and
/// keeps the files already imported; an archive cleanup stops before its next step), and why a running permanent deletion,
/// which removes the media folders before the catalog rows and cannot stop in between, is asked about at the veto point instead of
/// being cut by the time limit (MainWindow.ArchiveDeletion.cs).
/// </para>
/// </summary>
internal static class ShutdownExit
{
    /// <summary>The exit code of a shutdown that ran out of time or could not prove its owners released.</summary>
    internal const int IncompleteExitCode = 3;

    /// <summary>How long the watchdog waits beyond the shutdown time limit before it ends the process from outside the UI thread
    /// (a UI thread stuck in a native call cannot end it itself). A fixed, documented constant: it only covers the step that is
    /// already over the limit.</summary>
    internal static readonly TimeSpan WatchdogGrace = TimeSpan.FromSeconds(10);

    /// <summary>After <see cref="Environment.Exit"/> was asked, how long until the process is killed outright (an exit handler that
    /// never returns).</summary>
    internal static readonly TimeSpan KillBackstop = TimeSpan.FromSeconds(5);

    // Only a standalone desktop owns its process; an embedding host owns its own lifetime.
    internal static bool ProcessExitAllowed { get; set; } =
        System.Reflection.Assembly.GetEntryAssembly() == typeof(ShutdownExit).Assembly;

    internal static void Terminate(int exitCode)
    {
        Thread backstop = new(() =>
        {
            Thread.Sleep(KillBackstop);
            try { Process.GetCurrentProcess().Kill(); } catch (Exception) { /* nothing is left to try */ }
        }) { IsBackground = true, Name = "MeowChan shutdown backstop" };
        backstop.Start();
        try { Log.CloseAndFlush(); } catch (Exception) { /* the exit matters more than the last log line */ }
        Environment.Exit(exitCode);
    }
}

/// <summary>
/// Ends the process when a shutdown overruns its limit by <see cref="ShutdownExit.WatchdogGrace"/>, from a timer thread, so it also fires
/// while the UI thread is stuck in a synchronous native call (the DBus tray, a native player). Armed only where the process may end itself.
/// </summary>
internal sealed class ShutdownWatchdog : IDisposable
{
    private readonly Action<int> _terminate;
    private readonly ILogger _log;
    private readonly object _sync = new();
    private Timer? _timer;
    private int _fired;

    internal ShutdownWatchdog(Action<int>? terminate = null, ILogger? log = null)
    {
        _terminate = terminate ?? ShutdownExit.Terminate;
        _log = log ?? Log.Logger;
    }

    internal bool Fired => Volatile.Read(ref _fired) != 0;
    internal bool Armed { get { lock (_sync) return _timer != null; } }

    internal void Arm(TimeSpan after, Func<string> running)
    {
        ArgumentNullException.ThrowIfNull(running);
        lock (_sync)
        {
            _timer?.Dispose();
            _timer = new Timer(_ => Fire(after, running), null, after, Timeout.InfiniteTimeSpan);
        }
    }

    internal void Disarm()
    {
        lock (_sync) { _timer?.Dispose(); _timer = null; }
    }

    private void Fire(TimeSpan after, Func<string> running)
    {
        if (Interlocked.Exchange(ref _fired, 1) != 0) return;
        string steps;
        try { steps = running(); } catch (Exception) { steps = "unknown"; }
        _log.Error("Shutdown did not end within {After}; still running: {Steps}. Ending the process; committed data is already saved.",
            ShutdownBudget.Seconds(after), steps);
        _terminate(ShutdownExit.IncompleteExitCode);
    }

    public void Dispose() => Disarm();
}

/// <summary>The veto point of a close: every open editor may keep the window (unsaved changes). A check that fails keeps it too.</summary>
internal static class ShutdownPreparation
{
    internal static async Task<bool> ConfirmAsync(IEnumerable<Func<Task<bool>>> checks, ILogger? log = null)
    {
        foreach (Func<Task<bool>> check in checks)
        {
            try
            {
                if (!await check()) return false;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                (log ?? Log.Logger).Warning(error, "A dialog could not prepare to close; the window stays open.");
                return false;
            }
        }
        return true;
    }
}

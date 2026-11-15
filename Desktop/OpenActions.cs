using System;
using System.Threading.Tasks;

namespace MeowChan.Desktop;

/// <summary>
/// The one safe way for a click, double click, key or menu entry to start an open action (gallery, reader, media) that nobody awaits.
/// A failure is logged and shown in the window's error banner (<see cref="UnhandledErrors.TryRecover"/>, the same path as every other
/// recovered UI failure) and the app keeps running; the task is always observed, so nothing reaches the finalizer as an "Unobserved task
/// exception" or the dispatcher as an "Unhandled exception on the UI thread". A cancellation (the page moved on) is no failure. In
/// smokes and tests <see cref="UnhandledErrors.TryRecover"/> refuses, so the failure still propagates and fails the run.
/// </summary>
internal static class OpenActions
{
    /// <summary>Awaits <paramref name="open"/> and handles its failure as described above; also for callers that await the open.</summary>
    /// <param name="operation">A fixed English name for the log (never a path or a title).</param>
    /// <param name="recover">Test seam; the app uses <see cref="UnhandledErrors.TryRecover"/>.</param>
    internal static async Task RunAsync(Func<Task> open, string operation, Func<Exception, string, bool>? recover = null)
    {
        ArgumentNullException.ThrowIfNull(open);
        try { await (open() ?? Task.CompletedTask); }
        catch (OperationCanceledException) { /* The page, thread or profile moved on: nothing to report. */ }
        catch (Exception error) when ((recover ?? UnhandledErrors.TryRecover)(error, operation)) { /* Logged and shown. */ }
    }

    /// <summary>Starts <paramref name="open"/> without awaiting it (an event handler, a menu entry, a key); see the class.</summary>
    internal static void Start(Func<Task> open, string operation) => Observe(RunAsync(open, operation));

    // A failure that RunAsync did not recover (smokes, tests, out of memory) is rethrown on the UI thread, where the dispatcher's own
    // handler fails the smoke; it never stays in an unobserved task.
    private static async void Observe(Task running) => await running;
}

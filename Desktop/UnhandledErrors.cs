using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using Serilog;

namespace MeowChan.Desktop;

/// <summary>
/// Last net for exceptions no handler caught, e.g. from an <c>async void</c> event handler. Always logs the full
/// exception. In normal use a UI-thread failure is shown and the app keeps running, because a crash loses more
/// than a possibly half-finished action. Apphost smokes fail hard instead, so no swallowed error can turn green.
/// </summary>
internal sealed class UnhandledErrors
{
    /// <summary>EX_SOFTWARE: the smoke process ends with this code after an unhandled exception.</summary>
    internal const int SmokeExitCode = 70;
    private static UnhandledErrors? _installed;
    private readonly ILogger _log;
    private readonly bool _failHard;
    private readonly Action<int> _exit;

    internal UnhandledErrors(ILogger log, bool failHard, Action<int> exit)
    {
        _log = log; _failHard = failHard; _exit = exit;
    }

    /// <summary>Shows a recovered UI-thread failure; owned by the main window, null before it exists.</summary>
    internal static Action<Exception>? Presenter { get; set; }

    /// <summary>Process-wide handlers. The dispatcher handler follows in <see cref="AttachDispatcher"/>.</summary>
    internal static void Install(bool failHard)
    {
        if (_installed != null) return;
        UnhandledErrors errors = _installed = new UnhandledErrors(Log.Logger, failHard, Environment.Exit);
        AppDomain.CurrentDomain.UnhandledException += errors.OnDomainException;
        TaskScheduler.UnobservedTaskException += errors.OnUnobservedTask;
    }

    /// <summary>Called after the platform set up the UI dispatcher; touching it earlier would create a wrong one.</summary>
    internal static void AttachDispatcher()
    {
        if (_installed != null) Dispatcher.UIThread.UnhandledException += _installed.OnDispatcherException;
    }

    /// <summary>
    /// For a catch filter in an <c>async void</c> handler: logs and shows an unexpected failure and returns true,
    /// so the caller ends normally. False in smokes, in tests and for out-of-memory: the exception keeps propagating.
    /// </summary>
    internal static bool TryRecover(Exception error, string operation) => _installed?.Recover(error, operation) == true;

    internal bool Recover(Exception error, string operation)
    {
        if (_failHard || error is OutOfMemoryException) return false;
        _log.Error(error, "Unexpected failure in {Operation}; the app keeps running.", operation);
        Present(error);
        return true;
    }

    internal void OnDispatcherException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log.Error(e.Exception, "Unhandled exception on the UI thread.");
        if (_failHard) { FailHard("UI thread", e.Exception); return; }
        e.Handled = true;
        Present(e.Exception);
    }

    // Posted: neither a catch filter nor the dispatcher's exception path is a place to change controls.
    private void Present(Exception error) => Dispatcher.UIThread.Post(() =>
    {
        try { Presenter?.Invoke(error); }
        catch (Exception failure) { _log.Error(failure, "Could not show an unexpected failure."); }
    });

    internal void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // Raised by the finalizer long after the owner is gone, so there is no action to report to the user.
        // The runtime's default (ignore) stays unchanged in every mode; only the record is new.
        _log.Error(e.Exception, "Unobserved task exception.");
    }

    private void OnDomainException(object? sender, UnhandledExceptionEventArgs e)
    {
        // The runtime terminates the process after this event; only the record can be kept.
        _log.Fatal(e.ExceptionObject as Exception, "Unhandled exception; the process terminates. {Detail}",
            e.ExceptionObject is Exception ? string.Empty : e.ExceptionObject?.ToString());
        Log.CloseAndFlush();
    }

    private void FailHard(string source, Exception exception)
    {
        Console.Error.WriteLine($"APPHOST UNHANDLED EXCEPTION ({source}): {exception}");
        _exit(SmokeExitCode);
    }
}

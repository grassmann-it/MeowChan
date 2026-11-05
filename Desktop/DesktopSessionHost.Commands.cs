using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Diagnostics;
using MeowChan.Persistence;

namespace MeowChan.Desktop;

/// <summary>An explicit refusal: the caller must retain its input/draft and may retry.</summary>
internal sealed class DesktopSessionBusyException : InvalidOperationException
{
    internal DesktopSessionBusyException(string action)
        : base("Busy: the session is already processing this action or has eight pending actions. Please retry. (" + action + ")") { }
}

internal sealed partial class DesktopSessionHost
{
    internal const int InteractiveCommandLimit = 8;
    // This lock protects admission bookkeeping and short, synchronous kernel acceptance only.
    // Never acquire a product lock, dispose a token, or run user work inside it.
    private readonly object _admission = new();
    private readonly HashSet<(string Action, string Target)> _acceptedCommands = new();
    private readonly Dictionary<string, (string Target, Task Task)> _acceptedControls = new();
    private TaskCompletionSource? _commandsDrained;
    private bool _admissionClosed;
    private readonly FinalWriteFlushRegistration?[] _acceptedFinalWriteFlushes = new FinalWriteFlushRegistration?[2];
    private bool _finalWriteFlushCommandsClosed;

    private void ReleaseFinalWriteFlushSlotIfEnded(FinalWriteFlushRegistration registration)
    {
        if (registration.CommandEnded && registration.SegmentsEnded
            && ReferenceEquals(_acceptedFinalWriteFlushes[registration.Slot], registration))
            _acceptedFinalWriteFlushes[registration.Slot] = null;
    }
    private void CompleteFinalWriteFlushCommand(FinalWriteFlushRegistration registration)
    {
        // A synchronous onAccepted/dispatch failure may precede any binding. Do not
        // release a bound segment's slot merely because its command token ended.
        lock (_finalWrites) registration.Watermark ??= new(Array.Empty<(long, Task)>());
        ObserveCommandFinalWriteFence(registration);
        lock (_admission) { registration.CommandEnded = true; ReleaseFinalWriteFlushSlotIfEnded(registration); }
    }
    private void CloseTransitionCommandIngress()
    {
        lock (_admission)
        {
            if (_admissionClosed) throw new InvalidOperationException("The desktop session is stopping.");
            _finalWriteFlushCommandsClosed = true;
            var markers = new List<FinalWriteFlushRegistration>(2);
            foreach (var marker in _acceptedFinalWriteFlushes) if (marker != null) markers.Add(marker);
            _cutFlushRegistrations = markers.ToArray();
        }
    }
    private void ReopenTransitionCommandIngress()
    { lock (_admission) { if (!_admissionClosed) _finalWriteFlushCommandsClosed = false; } }

    internal int AcceptedCommandCount { get { lock (_admission) return _acceptedCommands.Count; } }

    private Task RunInteractiveAsync(string action, string target, Func<Task> operation, Action? onAccepted = null, bool captureAfter = true)
        => RunInteractiveAsync<object?>(action, target, async () => { await operation().ConfigureAwait(false); return null; }, onAccepted, captureAfter);

    private Task<T> RunInteractiveAsync<T>(string action, string target, Func<Task<T>> operation, Action? onAccepted = null, bool captureAfter = true)
        => RunInteractiveCoreAsync(action, target, _ => operation(), onAccepted, captureAfter);

    private Task RunInteractiveWithAdmissionAsync(string action, string target,
        Func<ProfileAdmissionToken, Task> operation, Action? onAccepted = null, bool captureAfter = true,
        FinalWriteFlushRegistration? finalWriteFlush = null)
        => RunInteractiveCoreAsync<object?>(action, target, async admission =>
        { await operation(admission).ConfigureAwait(false); return null; }, onAccepted, captureAfter, finalWriteFlush);

    private Task<T> RunInteractiveWithAdmissionAsync<T>(string action, string target,
        Func<ProfileAdmissionToken, Task<T>> operation, Action? onAccepted = null, bool captureAfter = true,
        FinalWriteFlushRegistration? finalWriteFlush = null)
        => RunInteractiveCoreAsync(action, target, operation, onAccepted, captureAfter, finalWriteFlush);

    private Task<T> RunInteractiveCoreAsync<T>(string action, string target,
        Func<ProfileAdmissionToken, Task<T>> operation, Action? onAccepted, bool captureAfter,
        FinalWriteFlushRegistration? finalWriteFlush = null)
    {
        long request = PerformanceDiagnostics.NextId();
        var key = (action, target);
        ProfileAdmissionToken? admission = null;
        try
        {
            lock (_admission)
            {
                if (_admissionClosed || _state is DesktopSessionState.Stopping or DesktopSessionState.Stopped) return Task.FromException<T>(new InvalidOperationException("The desktop session is stopping."));
                // A failed startup is not a retryable transition. Its closed flush gate
                // must not mask RecoveryFailed as Busy; Ready rollback keeps that gate.
                if (_state == DesktopSessionState.RecoveryFailed)
                    return Task.FromException<T>(new InvalidOperationException("The desktop session is not ready to change watches or settings."));
                if (_acceptedCommands.Count >= InteractiveCommandLimit || _acceptedCommands.Contains(key)
                    || finalWriteFlush != null && (_finalWriteFlushCommandsClosed || _acceptedFinalWriteFlushes[finalWriteFlush.Slot] != null))
                {
                    PerformanceDiagnostics.Record("host.command.admission", "busy", request, target, epoch: _epoch);
                    return Task.FromException<T>(new DesktopSessionBusyException(action));
                }
                // Acquire before publishing bookkeeping. A kernel refusal leaves no
                // host command/slot/drain behind, and returns the existing error type.
                try { admission = _profileLease.Admission.EnterOrdinaryWriter(); }
                catch (InvalidOperationException error) { return Task.FromException<T>(error); }
                _acceptedCommands.Add(key);
                if (finalWriteFlush != null) _acceptedFinalWriteFlushes[finalWriteFlush.Slot] = finalWriteFlush;
                _commandsDrained ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            ProfileAdmissionToken acceptedAdmission = admission!;
            onAccepted?.Invoke();
            if (finalWriteFlush != null)
            {
                BeforeCommandFinalWriteBindForTests?.Invoke();
                BindCommandFinalWriteFlush(finalWriteFlush);
                AfterCommandFinalWriteBindForTests?.Invoke();
            }
            PerformanceDiagnostics.Record("host.command.admission", "accepted", request, target, epoch: _epoch);
            // The first product instruction, including a synchronously satisfied semaphore and argument validation,
            // executes on a worker. _commands in the existing implementations remains the sole mutation serializer.
            return Task.Run(async () =>
            {
                try
                {
                    using var measurement = PerformanceDiagnostics.Measure("host.command", request, target, epoch: _epoch);
                    T result = await operation(acceptedAdmission).ConfigureAwait(false);
                    // All command completions observe the same bounded publication path, including forced reads.
                    if (captureAfter) await CaptureProjectionAsync().ConfigureAwait(false);
                    return result;
                }
                finally { ReleaseInteractiveCommand(key, acceptedAdmission, finalWriteFlush); }
            });
        }
        catch
        {
            // onAccepted and dispatch can fail synchronously, before a worker owns cleanup.
            if (admission != null) ReleaseInteractiveCommand(key, admission, finalWriteFlush);
            throw;
        }
    }

    private void ReleaseInteractiveCommand((string Action, string Target) key, ProfileAdmissionToken admission,
        FinalWriteFlushRegistration? finalWriteFlush = null)
    {
        try
        {
            lock (_admission)
            {
                _acceptedCommands.Remove(key);
                CompleteCommandDrainIfEmpty();
            }
        }
        finally
        {
            try { if (finalWriteFlush != null) CompleteFinalWriteFlushCommand(finalWriteFlush); }
            finally { admission.Dispose(); }
        }
    }

    // Separate finite lanes: duplicate controls share their actual completion, and a conflicting desired state
    // is refused visibly. Controls never consume or wait for an interactive admission slot.
    private Task RunControlAsync(string action, string target, Func<Task> operation)
        => RunControlWithAdmissionAsync(action, target, _ => operation());

    private Task RunControlWithAdmissionAsync(string action, string target, Func<ProfileAdmissionToken, Task> operation)
    {
        ProfileAdmissionToken? admission = null;
        try
        {
            lock (_admission)
            {
                if (_admissionClosed || _state is DesktopSessionState.Stopping or DesktopSessionState.Stopped) return Task.FromException(new InvalidOperationException("The desktop session is stopping."));
                if (_acceptedControls.TryGetValue(action, out var accepted))
                    return accepted.Target == target ? accepted.Task : Task.FromException(new DesktopSessionBusyException(action));
                try { admission = _profileLease.Admission.EnterOrdinaryWriter(); }
                catch (InvalidOperationException error) { return Task.FromException(error); }
                _commandsDrained ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                ProfileAdmissionToken acceptedAdmission = admission!;
                Task task = Task.Run(async () =>
                {
                    try
                    {
                        using var measurement = PerformanceDiagnostics.Measure("host.control." + action, epoch: _epoch);
                        await operation(acceptedAdmission).ConfigureAwait(false);
                    }
                    finally { ReleaseControlCommand(action, acceptedAdmission); }
                });
                // The worker's cleanup also takes _admission, so registration always
                // precedes removal, even if the control completes synchronously.
                _acceptedControls.Add(action, (target, task));
                return task;
            }
        }
        catch
        {
            if (admission != null) ReleaseControlCommand(action, admission);
            throw;
        }
    }

    private void ReleaseControlCommand(string action, ProfileAdmissionToken admission)
    {
        try
        {
            lock (_admission)
            {
                _acceptedControls.Remove(action);
                CompleteCommandDrainIfEmpty();
            }
        }
        finally { admission.Dispose(); }
    }

    // Caller holds _admission. Token release follows outside the lock, so shutdown
    // also waits for ProfileAdmission.DrainAsync rather than trusting this alone.
    private void CompleteCommandDrainIfEmpty()
    {
        if (_acceptedCommands.Count == 0 && _acceptedControls.Count == 0)
        {
            _commandsDrained?.TrySetResult();
            _commandsDrained = null;
        }
    }

    private Task CloseInteractiveAdmission()
    {
        lock (_admission)
        {
            _admissionClosed = true;
            return _commandsDrained?.Task ?? Task.CompletedTask;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Diagnostics;

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
    // This lock protects admission bookkeeping only. Never acquire a product lock or run user work inside it.
    private readonly object _admission = new();
    private readonly HashSet<(string Action, string Target)> _acceptedCommands = new();
    private readonly Dictionary<string, (string Target, Task Task)> _acceptedControls = new();
    private TaskCompletionSource? _commandsDrained;
    private bool _admissionClosed;
    internal int AcceptedCommandCount { get { lock (_admission) return _acceptedCommands.Count; } }

    private Task RunInteractiveAsync(string action, string target, Func<Task> operation, Action? onAccepted = null, bool captureAfter = true)
        => RunInteractiveAsync<object?>(action, target, async () => { await operation().ConfigureAwait(false); return null; }, onAccepted, captureAfter);

    private Task<T> RunInteractiveAsync<T>(string action, string target, Func<Task<T>> operation, Action? onAccepted = null, bool captureAfter = true)
    {
        long request = PerformanceDiagnostics.NextId();
        var key = (action, target);
        lock (_admission)
        {
            if (_admissionClosed || _state is DesktopSessionState.Stopping or DesktopSessionState.Stopped) return Task.FromException<T>(new InvalidOperationException("The desktop session is stopping."));
            if (_acceptedCommands.Count >= InteractiveCommandLimit || !_acceptedCommands.Add(key))
            {
                PerformanceDiagnostics.Record("host.command.admission", "busy", request, target, epoch: _epoch);
                return Task.FromException<T>(new DesktopSessionBusyException(action));
            }
            _commandsDrained ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        onAccepted?.Invoke();
        PerformanceDiagnostics.Record("host.command.admission", "accepted", request, target, epoch: _epoch);
        // The first product instruction, including a synchronously satisfied semaphore and argument validation,
        // executes on a worker. _commands in the existing implementations remains the sole mutation serializer.
        return Task.Run(async () =>
        {
            using var measurement = PerformanceDiagnostics.Measure("host.command", request, target, epoch: _epoch);
            try
            {
                T result = await operation().ConfigureAwait(false);
                // All command completions observe the same bounded publication path, including forced reads.
                if (captureAfter) await CaptureProjectionAsync().ConfigureAwait(false);
                return result;
            }
            finally
            {
                lock (_admission)
                {
                    _acceptedCommands.Remove(key);
                    if (_acceptedCommands.Count == 0 && _acceptedControls.Count == 0)
                    {
                        _commandsDrained?.TrySetResult();
                        _commandsDrained = null;
                    }
                }
            }
        });
    }

    // Separate finite lanes: duplicate controls share their actual completion, and a conflicting desired state
    // is refused visibly. Controls never consume or wait for an interactive admission slot.
    private Task RunControlAsync(string action, string target, Func<Task> operation)
    {
        lock (_admission)
        {
            if (_admissionClosed || _state is DesktopSessionState.Stopping or DesktopSessionState.Stopped) return Task.FromException(new InvalidOperationException("The desktop session is stopping."));
            if (_acceptedControls.TryGetValue(action, out var accepted))
                return accepted.Target == target ? accepted.Task : Task.FromException(new DesktopSessionBusyException(action));
            _commandsDrained ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task task = Task.Run(async () =>
            {
                using var measurement = PerformanceDiagnostics.Measure("host.control." + action, epoch: _epoch);
                try { await operation().ConfigureAwait(false); }
                finally
                {
                    lock (_admission)
                    {
                        _acceptedControls.Remove(action);
                        if (_acceptedControls.Count == 0 && _acceptedCommands.Count == 0)
                        {
                            _commandsDrained?.TrySetResult();
                            _commandsDrained = null;
                        }
                    }
                }
            });
            _acceptedControls.Add(action, (target, task));
            return task;
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

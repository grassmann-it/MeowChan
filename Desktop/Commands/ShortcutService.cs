using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using Avalonia.Threading;
using Serilog;

namespace MeowChan.Desktop.Commands;

/// <summary>One committed profile snapshot for routing and every presentation.</summary>
public static class ShortcutService
{
    private static EffectiveShortcutBindings _current = EffectiveShortcutBindings.Build();
    private static int _captureDepth;
    private static readonly List<WeakReference<Action>> Refreshers = new();
    public static EffectiveShortcutBindings Current => _current;
    public static long Revision { get; private set; }
    internal static int ObserverCount => Refreshers.Count;
    public static bool IsCapturing => _captureDepth > 0;
    public static event Action? Changed;
    public static IDisposable BeginCapture() { ++_captureDepth; return new CaptureLease(); }
    private sealed class CaptureLease : IDisposable
    { private bool _disposed; public void Dispose() { if (_disposed) return; _disposed = true; --_captureDepth; } }
    public static void ValidateForCommit(ShortcutBindingsSection section)
    {
        var failures = EffectiveShortcutBindings.Build(section).Issues.Where(i => i.BlocksSave).ToArray();
        if (failures.Length != 0) throw new ArgumentException(string.Join("; ", failures.Select(i => i.CommandId + ": " + i.Message)), nameof(Config.ShortcutBindings));
    }
    public static void Publish(ShortcutBindingsSection section)
    {
        var snapshot = EffectiveShortcutBindings.Build(section);
        void Apply()
        {
            _current = snapshot; ++Revision;
            IReadOnlyList<Exception> failures = RefreshAll(Refreshers);
            // Always: a failed presentation must not keep the others (and every Changed listener) on the old snapshot.
            Changed?.Invoke();
            Report(failures);
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply(); else Dispatcher.UIThread.Post(Apply);
    }

    /// <summary>
    /// Runs every live callback once. The list is emptied first because each presentation registers again from its own
    /// refresh. A callback that throws does not stop the others and stays registered for the next snapshot (added again
    /// unless it already registered itself before it failed). Returns the failures, in order.
    /// </summary>
    internal static IReadOnlyList<Exception> RefreshAll(List<WeakReference<Action>> refreshers)
    {
        var callbacks = refreshers.Select(w => w.TryGetTarget(out var callback) ? callback : null).Where(x => x != null).ToArray();
        refreshers.Clear();
        List<Exception>? failures = null;
        foreach (var refresh in callbacks)
        {
            try { refresh!(); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                (failures ??= new()).Add(error);
                if (!refreshers.Any(w => w.TryGetTarget(out var known) && ReferenceEquals(known, refresh))) refreshers.Add(new(refresh!));
            }
        }
        return failures ?? (IReadOnlyList<Exception>)Array.Empty<Exception>();
    }

    // Logged and shown like any recovered UI failure; in smokes and tests (no recovery installed) the first one propagates.
    private static void Report(IReadOnlyList<Exception> failures)
    {
        if (failures.Count == 0) return;
        for (int i = 1; i < failures.Count; i++) Log.Error(failures[i], "A shortcut presentation could not refresh.");
        if (!UnhandledErrors.TryRecover(failures[0], "shortcuts.refresh")) ExceptionDispatchInfo.Throw(failures[0]);
    }

    /// <summary>Floor of the registrations between two scans for dead observers (housekeeping, not a limit).</summary>
    internal const int MinimumObserversBetweenPrunes = 1024;
    private static int _observersUntilPrune = MinimumObserversBetweenPrunes;

    // Owner retains the callback; this service must not retain dead windows/controls.
    internal static void Observe(Action refresh) => Observe(Refreshers, refresh, ref _observersUntilPrune);

    /// <summary>
    /// Adds an observer; every so often drops dead ones first. A scan is paid for by as many registrations as the population
    /// it left alive (at least <see cref="MinimumObserversBetweenPrunes"/>), so many live observers never make each
    /// registration scan the whole list. True when this call scanned.
    /// </summary>
    internal static bool Observe(List<WeakReference<Action>> refreshers, Action refresh, ref int untilPrune)
    {
        bool pruned = false;
        if (--untilPrune <= 0)
        {
            refreshers.RemoveAll(w => !w.TryGetTarget(out _));
            untilPrune = Math.Max(MinimumObserversBetweenPrunes, refreshers.Count);
            pruned = true;
        }
        refreshers.Add(new(refresh));
        return pruned;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;

namespace MeowChan.Desktop.Commands;

/// <summary>One committed profile snapshot for routing and every presentation.</summary>
public static class ShortcutService
{
    private static EffectiveShortcutBindings _current = EffectiveShortcutBindings.Build();
    private static int _captureDepth;
    private static readonly List<WeakReference<Action>> Refreshers = new();
    public static EffectiveShortcutBindings Current => _current;
    public static long Revision { get; private set; }
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
            var callbacks = Refreshers.Select(w => w.TryGetTarget(out var callback) ? callback : null).Where(x => x != null).ToArray();
            Refreshers.Clear();
            foreach (var refresh in callbacks) refresh!();
            Changed?.Invoke();
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply(); else Dispatcher.UIThread.Post(Apply);
    }
    // Owner retains the callback; this service must not retain dead windows/controls.
    internal static void Observe(Action refresh)
    {
        if (Refreshers.Count > 1024) Refreshers.RemoveAll(w => !w.TryGetTarget(out _));
        Refreshers.Add(new(refresh));
    }
}

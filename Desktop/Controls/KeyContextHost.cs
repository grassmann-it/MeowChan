using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia;
using Avalonia.Threading;
using MeowChan.Desktop.Commands;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// Runs every KeyDown handler of a page in the page's execution context (<see cref="EffectiveShortcutBindings.EnterKeyContext"/>), as the gallery's
/// OnKeyDown does for itself. A default that a saved binding took away only in another context (a saved archive command on F against favorites.toggle)
/// then stays active on this page instead of going dead wherever no context is named. The scope opens in a Tunnel handler on the page root, which is
/// registered first, and closes in a Bubble handler on the root, which is registered once the page is attached, so after the page's own root handlers.
/// Both see handled events too. A nested key event raised from inside a handler gets its own scope (the closing handler ends the scope of its own event
/// together with any scope above it that never got its close). The large reader's pane lives outside the page, so it installs the same host on itself
/// while it is there and removes it again (the returned <see cref="IDisposable"/>); a key that already runs in the same context does not enter it twice.
/// A handler that throws aborts the route before the closing handler: a posted safety net then ends the scope, so the context never outlives the event.
/// </summary>
internal static class KeyContextHost
{
    /// <param name="closeNow">Registers the closing handler at once instead of on attach (a test's detached tree).</param>
    /// <returns>Removes the handlers and ends every scope still open.</returns>
    internal static IDisposable Install(Control root, Func<KeyEventArgs, string> context, bool closeNow = false)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(context);
        Installation installation = new(root, context);
        installation.Start(closeNow);
        return installation;
    }

    /// <summary>The context the current key is handled in, or null outside any scope (tests).</summary>
    internal static string? Current => EffectiveShortcutBindings.CurrentKeyContext;

    // The context the innermost host resolved for a key event (ContextOf). Weak: the entry goes with the event.
    private static readonly ConditionalWeakTable<KeyEventArgs, string> Resolved = new();

    /// <summary>
    /// The context the innermost host on the key's route resolved for <paramref name="e"/>, or null when no host saw it (a key from the header, the
    /// footer or the window itself). The window's own routes run in its Bubble handler, after every page scope has closed; they enter this context
    /// again (MainWindow.ShellKeyContext), so a default that gives way only in another context stays active for them too.
    /// </summary>
    internal static string? ContextOf(KeyEventArgs e) => Resolved.TryGetValue(e, out string? context) ? context : null;

    private sealed class Entry
    {
        internal readonly KeyEventArgs Event;
        internal EffectiveShortcutBindings.KeyContextScope? Scope;
        internal bool Closed;
        internal Entry(KeyEventArgs @event) => Event = @event;
    }

    private sealed class Installation : IDisposable
    {
        private readonly Control _root;
        private readonly Func<KeyEventArgs, string> _context;
        private readonly Stack<Entry> _open = new();
        private readonly EventHandler<KeyEventArgs> _enter, _leave;
        private EventHandler<VisualTreeAttachmentEventArgs>? _attach;
        private bool _closing, _disposed;

        internal Installation(Control root, Func<KeyEventArgs, string> context)
        {
            _root = root;
            _context = context;
            _enter = Enter;
            _leave = Leave;
        }

        internal void Start(bool closeNow)
        {
            _root.AddHandler(InputElement.KeyDownEvent, _enter, RoutingStrategies.Tunnel, handledEventsToo: true);
            if (closeNow || TopLevel.GetTopLevel(_root) != null) AddClose();
            else
            {
                _attach = (_, _) => AddClose();
                _root.AttachedToVisualTree += _attach;
            }
        }

        private void AddClose()
        {
            if (_closing || _disposed) return;
            _closing = true;
            _root.AddHandler(InputElement.KeyDownEvent, _leave, RoutingStrategies.Bubble, handledEventsToo: true);
        }

        private void Enter(object? sender, KeyEventArgs e)
        {
            if (_disposed) return;
            string name = _context(e);
            // Tunnel runs from the root inward, so a host nearer the source (the pane inside the page) records last.
            Resolved.AddOrUpdate(e, name);
            Entry entry = new(e);
            // Another host (the page above a pane that is back inside it) already runs this key in the same context: nothing to enter, nothing to end.
            if (!string.Equals(EffectiveShortcutBindings.CurrentKeyContext, name, StringComparison.Ordinal))
                entry.Scope = EffectiveShortcutBindings.EnterKeyContext(name);
            _open.Push(entry);
            Dispatcher.UIThread.Post(() => Settle(entry), DispatcherPriority.Background);
        }

        private void Leave(object? sender, KeyEventArgs e)
        {
            Entry? own = null;
            foreach (Entry entry in _open) if (ReferenceEquals(entry.Event, e)) { own = entry; break; }
            if (own != null) Settle(own);
        }

        // Ends the scope of <paramref name="entry"/> and of every scope opened after it that is still open (innermost first, as the context is restored).
        private void Settle(Entry entry)
        {
            if (entry.Closed) return;
            while (_open.Count > 0)
            {
                Entry top = _open.Pop();
                top.Closed = true;
                top.Scope?.Dispose();
                if (ReferenceEquals(top, entry)) return;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _root.RemoveHandler(InputElement.KeyDownEvent, _enter);
            if (_closing) _root.RemoveHandler(InputElement.KeyDownEvent, _leave);
            if (_attach != null) _root.AttachedToVisualTree -= _attach;
            _attach = null;
            while (_open.Count > 0)
            {
                Entry top = _open.Pop();
                top.Closed = true;
                top.Scope?.Dispose();
            }
        }
    }
}

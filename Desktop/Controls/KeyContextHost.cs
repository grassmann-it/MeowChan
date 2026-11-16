using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Desktop.Commands;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// Runs every KeyDown handler of a page in the page's execution context (<see cref="EffectiveShortcutBindings.EnterKeyContext"/>), as the gallery's
/// OnKeyDown does for itself. A default that a saved binding took away only in another context (a saved archive command on F against favorites.toggle)
/// then stays active on this page instead of going dead wherever no context is named. The scope opens in a Tunnel handler on the page root, which is
/// registered first, and closes in a Bubble handler on the root, which is registered once the page is attached, so after the page's own root handlers.
/// Both see handled events too. A nested key event raised from inside a handler gets its own scope.
/// </summary>
internal static class KeyContextHost
{
    /// <param name="closeNow">Registers the closing handler at once instead of on attach (a test's detached tree).</param>
    internal static void Install(Control root, Func<KeyEventArgs, string> context, bool closeNow = false)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(context);
        Stack<EffectiveShortcutBindings.KeyContextScope> scopes = new();
        root.AddHandler(InputElement.KeyDownEvent, (_, e) => scopes.Push(EffectiveShortcutBindings.EnterKeyContext(context(e))),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        bool closing = false;
        void AddClose()
        {
            if (closing) return;
            closing = true;
            root.AddHandler(InputElement.KeyDownEvent, (_, _) => { if (scopes.Count > 0) scopes.Pop().Dispose(); }, RoutingStrategies.Bubble, handledEventsToo: true);
        }
        if (closeNow || TopLevel.GetTopLevel(root) != null) AddClose();
        else root.AttachedToVisualTree += (_, _) => AddClose();
    }

    /// <summary>The context the current key is handled in, or null outside any scope (tests).</summary>
    internal static string? Current => EffectiveShortcutBindings.CurrentKeyContext;
}

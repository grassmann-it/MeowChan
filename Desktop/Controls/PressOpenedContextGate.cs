using System;
using Avalonia.Input;
using Avalonia.Threading;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// The pairing of a right-click press that opens a context menu with the <c>ContextRequested</c> that Avalonia 12 raises while it processes the
/// RELEASE of that click. The press opens the menu itself (nothing the pointer does afterwards can swallow it) and arms this gate; the request that
/// follows with the release is then consumed once instead of opening a second menu over the first. The gate therefore lives from the press until the
/// release is through: a click lasts far longer than a Background tick, so a flag dropped by a post made in the press handler (the first version of
/// the media grid) ended before the release and let the request through. A key or the next press ends a gesture whose release never came (a menu
/// popup took it); a synthetic keyboard request after that is routed normally.
/// </summary>
internal sealed class PressOpenedContextGate
{
    private readonly Action<Action> _afterRelease;
    private int _gesture;

    /// <param name="afterRelease">How the end of the gate is deferred past the release's own <c>ContextRequested</c>; the default posts it on a
    /// Background tick of the UI thread, a test passes a capture.</param>
    internal PressOpenedContextGate(Action<Action>? afterRelease = null) =>
        _afterRelease = afterRelease ?? (end => Dispatcher.UIThread.Post(end, DispatcherPriority.Background));

    /// <summary>Whether a menu opened by a press waits for the request that comes with its release.</summary>
    internal bool IsPending { get; private set; }

    /// <summary>A press starts: whatever gesture came before is over.</summary>
    internal void BeginPress() { IsPending = false; ++_gesture; }

    /// <summary>The press opened the menu: the request of its release is consumed once.</summary>
    internal void MenuOpenedByPress() { IsPending = true; ++_gesture; }

    /// <summary>The release of a pointer button went through the press path. Only the right button ends the gate, and only after the request that
    /// Avalonia sends with it (it comes through the handlers after this one), so the end is deferred.</summary>
    internal void Released(MouseButton button)
    {
        if (!IsPending || button != MouseButton.Right) return;
        int gesture = _gesture; // a press that came before the deferred end belongs to the next gesture
        _afterRelease(() => { if (gesture == _gesture) IsPending = false; });
    }

    /// <summary>A <c>ContextRequested</c> arrived: true when it is the one the press already answered (consumed, the gate ends).</summary>
    internal bool TryConsume()
    {
        if (!IsPending) return false;
        IsPending = false; ++_gesture;
        return true;
    }

    /// <summary>A key, a hidden or replaced surface: a gesture whose release never came is over.</summary>
    internal void Reset() { IsPending = false; ++_gesture; }
}

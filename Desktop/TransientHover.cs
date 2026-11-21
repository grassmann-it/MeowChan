using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MeowChan.Desktop;

/// <summary>A pointer lease, not an enter-event latch. Invalidation needs a fresh move/entry,
/// never a click or window activation. Keyboard focus remains owned by the caller.</summary>
internal sealed class TransientHoverState
{
    internal bool Hovered { get; private set; }
    private bool _fresh;
    internal bool Observe(bool pointerOver, bool usable, bool moved = false)
    {
        if (!pointerOver || !usable) _fresh = false;
        else if (moved) _fresh = true;
        return Hovered = pointerOver && usable && _fresh;
    }
    internal void Release() { _fresh = false; Hovered = false; }
}

/// <summary>Reconciles a custom hover with Avalonia hit testing and the owning window. Only an
/// active hover polls (to catch covered/repositioned/virtualized anchors without a leave event).
/// All handlers are detached with the anchor; no process-wide input hook or per-tile idle timer.</summary>
internal sealed class TransientHover : IDisposable
{
    private readonly Control _anchor;
    private readonly Action<bool> _changed;
    private readonly Func<bool>? _extraPointer;
    private readonly TransientHoverState _state = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private Window? _window;
    private bool _disposed;

    internal TransientHover(Control anchor, Action<bool> changed, Func<bool>? extraPointer = null)
    {
        _anchor = anchor; _changed = changed; _extraPointer = extraPointer;
        anchor.AttachedToVisualTree += Attached;
        anchor.DetachedFromVisualTree += Detached;
        anchor.PointerEntered += Moved;
        anchor.PointerMoved += Moved;
        anchor.PointerExited += Exited;
        anchor.DataContextChanged += Replaced;
        anchor.PropertyChanged += AnchorChanged;
        _timer.Tick += Tick;
        AttachWindow();
    }

    // Lifetime owners without their own Dispose: the lease lives exactly as long as its owner (the
    // anchor for Attach), so nothing is leaked and no owner has to become IDisposable.
    private static readonly ConditionalWeakTable<object, List<TransientHover>> Attached_ = new();
    private static readonly ConditionalWeakTable<object, List<TransientHover>> Slots = new();

    /// <summary>Creates a lease that lives as long as <paramref name="anchor"/> itself.</summary>
    internal static void Attach(Control anchor, Action<bool> changed, Func<bool>? extraPointer = null)
    {
        TransientHover lease = new(anchor, changed, extraPointer);
        Attached_.GetOrCreateValue(anchor).Add(lease);
    }

    /// <summary>Starts the single replaceable lease of <paramref name="owner"/>; the previous one is disposed.</summary>
    internal static void Replace(object owner, Control anchor, Action<bool> changed, Func<bool>? extraPointer = null)
    {
        Release(owner);
        TransientHover lease = new(anchor, changed, extraPointer);
        Slots.GetOrCreateValue(owner).Add(lease);
        lease.Reconcile(moved: true);
    }

    internal static void Release(object owner)
    {
        if (!Slots.TryGetValue(owner, out List<TransientHover>? held)) return;
        TransientHover[] previous = held.ToArray();
        held.Clear();
        foreach (TransientHover lease in previous) lease.Dispose();
    }

    internal static TransientHover? Current(object owner) => Slots.TryGetValue(owner, out List<TransientHover>? held) && held.Count > 0 ? held[0] : null;

    internal static bool WindowUsable(Control anchor) => anchor.IsAttachedToVisualTree()
        && anchor.IsEffectivelyVisible && anchor.IsEffectivelyEnabled
        && TopLevel.GetTopLevel(anchor) is Window { IsActive: true, IsVisible: true, WindowState: not WindowState.Minimized } window
        && !Commands.CommandRouter.HasOpenPopup(window);

    internal bool IsCurrent => _state.Hovered && PointerOver && WindowUsable(_anchor);
    private bool PointerOver => _anchor.IsPointerOver || _extraPointer?.Invoke() == true;
    internal void Reconcile(bool moved = false)
    {
        Reconcile(PointerOver, WindowUsable(_anchor), moved);
    }
    // Deterministic state-machine seam: production always samples IsPointerOver above.
    internal void Reconcile(bool pointerOver, bool usable, bool moved)
    {
        bool old = _state.Hovered;
        bool current = _state.Observe(pointerOver, usable, moved);
        if (current) _timer.Start(); else _timer.Stop();
        if (old != current) _changed(current);
    }
    internal void Release()
    {
        _state.Release(); _timer.Stop();
        _changed(false);
    }
    private void Moved(object? s, PointerEventArgs e) => Reconcile(moved: true);
    private void Exited(object? s, PointerEventArgs e) => Reconcile();
    private void Tick(object? s, EventArgs e) => Reconcile();
    private void Replaced(object? s, EventArgs e) => Release();
    private void AnchorChanged(object? s, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == InputElement.IsPointerOverProperty || e.Property == Visual.IsVisibleProperty || e.Property == InputElement.IsEnabledProperty)
            Reconcile();
    }
    private void WindowChanged(object? s, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty || e.Property == Visual.IsVisibleProperty || e.Property == InputElement.IsEnabledProperty) Release();
    }
    private void Interrupt(object? s, RoutedEventArgs e)
    {
        // A click on this surface is not a different interaction. Never withdraw a
        // chevron/star under its own press before the button can receive its release.
        if (e is PointerPressedEventArgs && e.Source is Visual source
            && (ReferenceEquals(source, _anchor) || _anchor.IsVisualAncestorOf(source))) return;
        Release();
    }
    private void WindowLeft(object? s, PointerEventArgs e) { if (_window?.IsPointerOver != true && _extraPointer?.Invoke() != true) Release(); }
    private void Deactivated(object? s, EventArgs e) => Release();
    private void Attached(object? s, VisualTreeAttachmentEventArgs e) => AttachWindow();
    private void Detached(object? s, VisualTreeAttachmentEventArgs e) { Release(); DetachWindow(); }
    private void AttachWindow()
    {
        if (_disposed || ReferenceEquals(_window, TopLevel.GetTopLevel(_anchor))) return;
        DetachWindow();
        _window = TopLevel.GetTopLevel(_anchor) as Window;
        if (_window == null) return;
        _window.Deactivated += Deactivated;
        _window.PropertyChanged += WindowChanged;
        _window.PointerExited += WindowLeft;
        _window.AddHandler(InputElement.KeyDownEvent, Interrupt, RoutingStrategies.Tunnel, true);
        _window.AddHandler(InputElement.PointerPressedEvent, Interrupt, RoutingStrategies.Tunnel, true);
        _window.AddHandler(InputElement.PointerWheelChangedEvent, Interrupt, RoutingStrategies.Tunnel, true);
        _window.AddHandler(ScrollViewer.ScrollChangedEvent, Interrupt, RoutingStrategies.Bubble, true);
    }
    private void DetachWindow()
    {
        if (_window == null) return;
        _window.Deactivated -= Deactivated;
        _window.PropertyChanged -= WindowChanged;
        _window.PointerExited -= WindowLeft;
        _window.RemoveHandler(InputElement.KeyDownEvent, Interrupt);
        _window.RemoveHandler(InputElement.PointerPressedEvent, Interrupt);
        _window.RemoveHandler(InputElement.PointerWheelChangedEvent, Interrupt);
        _window.RemoveHandler(ScrollViewer.ScrollChangedEvent, Interrupt);
        _window = null;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _state.Release(); _timer.Stop(); DetachWindow();
        _anchor.AttachedToVisualTree -= Attached;
        _anchor.DetachedFromVisualTree -= Detached;
        _anchor.PointerEntered -= Moved;
        _anchor.PointerMoved -= Moved;
        _anchor.PointerExited -= Exited;
        _anchor.DataContextChanged -= Replaced;
        _anchor.PropertyChanged -= AnchorChanged;
    }
}

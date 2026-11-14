using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.MediaActions;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// Gives one control a context menu from a <see cref="ContextMenuBuilder"/>: a right-click opens it at the pointer, the Menu key,
/// Shift+F10 and the configurable context-open command open it under the control, the menu is composed fresh at every opening (so
/// its checked entries and texts are current), it is the control's menu only while it is open (so Escape closes it with the other
/// shell popups) and the keyboard focus returns to where it was when it closes.
/// </summary>
internal sealed class ContextMenuHost : IDisposable
{
    // Avalonia may follow a handled key or press with a positionless ContextRequested of its own; it is consumed, not opened again.
    private static readonly TimeSpan PairedRequestWindow = TimeSpan.FromMilliseconds(400);

    private readonly Control _origin;
    private readonly Func<ContextMenuBuilder?> _compose;
    private readonly Action? _fallbackFocus;
    private ContextMenu? _menu;
    private DateTime _lastKeyOpen = DateTime.MinValue;
    private bool _disposed;

    /// <param name="origin">The control the menu belongs to; focus returns to it.</param>
    /// <param name="compose">Composes the menu for this opening; null (or an empty composition) opens nothing.</param>
    /// <param name="fallbackFocus">Where focus goes when neither the previous focus nor the origin can take it any more.</param>
    internal ContextMenuHost(Control origin, Func<ContextMenuBuilder?> compose, Action? fallbackFocus = null)
    {
        _origin = origin ?? throw new ArgumentNullException(nameof(origin));
        _compose = compose ?? throw new ArgumentNullException(nameof(compose));
        _fallbackFocus = fallbackFocus;
        origin.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
        origin.AddHandler(Control.ContextRequestedEvent, OnContextRequested, RoutingStrategies.Bubble);
    }

    /// <summary>The menu that is open now, if any.</summary>
    internal ContextMenu? OpenMenu => _menu is { IsOpen: true } ? _menu : null;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_disposed || !MediaMenuPresenter.IsContextKey(e)) return;
        _lastKeyOpen = DateTime.UtcNow;
        e.Handled = true;
        Show(atPointer: false);
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_disposed || e.Handled) return;
        bool positioned = e.TryGetPosition(_origin, out _);
        e.Handled = true;
        if (!positioned && DateTime.UtcNow - _lastKeyOpen < PairedRequestWindow) return;
        Show(atPointer: positioned);
    }

    /// <summary>Opens the menu on the origin: at the pointer, or under the control for the keyboard.</summary>
    internal void Show(bool atPointer = false)
    {
        if (_disposed || !_origin.IsAttachedToVisualTree() || !_origin.IsEffectivelyVisible || !_origin.IsEffectivelyEnabled) return;
        Close();
        ContextMenu? menu = _compose()?.Build();
        if (menu == null) return;
        object? previousFocus = TopLevel.GetTopLevel(_origin)?.FocusManager?.GetFocusedElement();
        if (!atPointer) menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
        _menu = menu;
        _origin.ContextMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_menu, menu))
            {
                if (ReferenceEquals(_origin.ContextMenu, menu)) _origin.ContextMenu = null;
                _menu = null;
            }
            RestoreFocus(menu, previousFocus as Control);
        };
        menu.Open(_origin);
    }

    /// <summary>Closes the open menu, if any.</summary>
    internal void Close()
    {
        ContextMenu? previous = _menu;
        if (previous == null) return;
        _menu = null;
        previous.Close();
        if (ReferenceEquals(_origin.ContextMenu, previous)) _origin.ContextMenu = null;
    }

    private void RestoreFocus(ContextMenu menu, Control? previous)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // A newer menu, or a window or page that the chosen entry opened, owns the focus now.
            if (_disposed || _menu != null || menu.IsOpen) return;
            TopLevel? top = TopLevel.GetTopLevel(_origin);
            if (top is Window owner && owner.OwnedWindows.Any(window => window.IsVisible)) return;
            static bool Usable(Control? control) => control != null && control.IsAttachedToVisualTree() && control.IsEffectivelyVisible && control.IsEffectivelyEnabled;
            // Focus held by something else that can take it (the page an entry opened) stays; focus left in the closed menu returns.
            object? focused = top?.FocusManager?.GetFocusedElement();
            if (focused is Control current && Usable(current) && !current.GetSelfAndVisualAncestors().OfType<ContextMenu>().Any()) return;
            if (Usable(previous)) previous!.Focus();
            else if (Usable(_origin)) _origin.Focus();
            else _fallbackFocus?.Invoke();
        }, DispatcherPriority.Background);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Close();
        _disposed = true;
        _origin.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        _origin.RemoveHandler(Control.ContextRequestedEvent, OnContextRequested);
    }
}

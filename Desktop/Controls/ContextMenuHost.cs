using System;
using Avalonia;
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
    private readonly Func<Control>? _resolveOrigin;
    private Control? _openOn;
    private readonly Action? _fallbackFocus;
    private ContextMenu? _menu;
    private DateTime _lastKeyOpen = DateTime.MinValue;
    private bool _disposed;

    /// <param name="origin">The control the menu belongs to; focus returns to it.</param>
    /// <param name="compose">Composes the menu for this opening; null (or an empty composition) opens nothing.</param>
    /// <param name="fallbackFocus">Where focus goes when neither the previous focus nor the origin can take it any more.</param>
    /// <param name="resolveOrigin">Optional: the control the menu opens on for this opening (a sibling when <paramref name="origin"/> is not on the screen,
    /// for example a button that moved under "More"); the handlers stay on <paramref name="origin"/>.</param>
    internal ContextMenuHost(Control origin, Func<ContextMenuBuilder?> compose, Action? fallbackFocus = null, Func<Control>? resolveOrigin = null)
    {
        _resolveOrigin = resolveOrigin;
        _origin = origin ?? throw new ArgumentNullException(nameof(origin));
        _compose = compose ?? throw new ArgumentNullException(nameof(compose));
        _fallbackFocus = fallbackFocus;
        // A list or table takes the key in the tunnel phase: its focused item or row consumes Enter, Space, Home, End and the arrows before a bubble
        // handler of the list sees them, so a rebinding of "Open context menu" to one of them would never arrive. Other origins keep the bubble phase,
        // where a child that handles the key itself (a nested chip with a menu of its own) still wins.
        origin.AddHandler(InputElement.KeyDownEvent, OnKeyDown, (origin is ListBox or DataGrid) ? RoutingStrategies.Tunnel : RoutingStrategies.Bubble);
        origin.AddHandler(Control.ContextRequestedEvent, OnContextRequested, RoutingStrategies.Bubble);
        // A card/row can be rebuilt while its popup is open (Booru paging, archive refresh, virtualized lists).
        // Close before the detached control can leave a menu whose buttons still point at the old row.
        origin.DetachedFromVisualTree += OnOriginDetached;
    }

    /// <summary>The menu that is open now, if any.</summary>
    internal ContextMenu? OpenMenu => _menu is { IsOpen: true } ? _menu : null;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_disposed || !MediaMenuPresenter.IsContextKey(e)) return;
        // In the tunnel phase an interactive part of a row (a nested button or text field) keeps the key.
        if (_origin is ListBox or DataGrid && ItemActivation.IsOwnedByInteractivePart(e.Source, _origin)) return;
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
        Control origin = _resolveOrigin?.Invoke() ?? _origin;
        if (_disposed || !origin.IsAttachedToVisualTree() || !origin.IsEffectivelyVisible || !origin.IsEffectivelyEnabled) return;
        Close();
        ContextMenu? menu = _compose()?.Build();
        if (menu == null) return;
        object? previousFocus = TopLevel.GetTopLevel(origin)?.FocusManager?.GetFocusedElement();
        if (!atPointer) menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
        _menu = menu;
        _openOn = origin;
        origin.ContextMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_menu, menu))
            {
                if (ReferenceEquals(origin.ContextMenu, menu)) origin.ContextMenu = null;
                _menu = null;
            }
            RestoreFocus(menu, previousFocus as Control, origin);
        };
        menu.Open(origin);
    }

    private void OnOriginDetached(object? sender, VisualTreeAttachmentEventArgs e) => Close();

    /// <summary>Closes the open menu, if any.</summary>
    internal void Close()
    {
        ContextMenu? previous = _menu;
        if (previous == null) return;
        _menu = null;
        previous.Close();
        Control openOn = _openOn ?? _origin;
        if (ReferenceEquals(openOn.ContextMenu, previous)) openOn.ContextMenu = null;
    }

    private void RestoreFocus(ContextMenu menu, Control? previous, Control origin)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // A newer menu, or a window or page that the chosen entry opened, owns the focus now.
            if (_disposed || _menu != null || menu.IsOpen) return;
            TopLevel? top = TopLevel.GetTopLevel(origin);
            if (top is Window owner && owner.OwnedWindows.Any(window => window.IsVisible)) return;
            static bool Usable(Control? control) => control != null && control.IsAttachedToVisualTree() && control.IsEffectivelyVisible && control.IsEffectivelyEnabled;
            // Focus held by something else that can take it (the page an entry opened) stays; focus left in the closed menu returns.
            object? focused = top?.FocusManager?.GetFocusedElement();
            if (focused is Control current && Usable(current) && !current.GetSelfAndVisualAncestors().OfType<ContextMenu>().Any()) return;
            if (Usable(previous)) previous!.Focus();
            else if (Usable(origin)) origin.Focus();
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
        _origin.DetachedFromVisualTree -= OnOriginDetached;
    }
}

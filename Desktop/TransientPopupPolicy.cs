using System;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace MeowChan.Desktop;

/// <summary>Shared release paths for framework tooltips and explicit flyouts/popovers.
/// A click/keyboard flyout is not a hover: moving from its anchor into its content must
/// not dismiss an editor. It does retire with its window, anchor, or scroll position.</summary>
internal static class TransientPopupPolicy
{
    private static bool _installed;
    private static readonly ConditionalWeakTable<Control, TransientHover> Tips = new();
    private static readonly ConditionalWeakTable<ListBoxItem, TransientHover> Tiles = new();
    private static readonly ConditionalWeakTable<Popup, PopupLease> Popups = new();

    internal static void Install()
    {
        if (_installed) return;
        _installed = true;
        ToolTip.IsOpenProperty.Changed.AddClassHandler<Control>((anchor, change) =>
        {
            if (Tips.TryGetValue(anchor, out var previous)) { Tips.Remove(anchor); previous.Dispose(); }
            if (!change.GetNewValue<bool>()) return;
            var lease = new TransientHover(anchor, over => { if (!over) ToolTip.SetIsOpen(anchor, false); });
            Tips.Add(anchor, lease);
            lease.Reconcile(moved: true);
            if (!lease.IsCurrent) ToolTip.SetIsOpen(anchor, false);
        });
        Control.LoadedEvent.AddClassHandler<ListBoxItem>((tile, _) =>
        {
            if (Tiles.TryGetValue(tile, out var existing)) return;
            Tiles.Add(tile, new TransientHover(tile, over => tile.Classes.Set("transient-pointer-over", over)));
        });
        Popup.IsOpenProperty.Changed.AddClassHandler<Popup>((popup, change) =>
        {
            if (Popups.TryGetValue(popup, out var previous)) { Popups.Remove(popup); previous.Dispose(); }
            if (!change.GetNewValue<bool>()) return;
            if (popup.ShouldUseOverlayLayer && popup.Child is ToolTip) popup.IsHitTestVisible = false;
            if (popup.PlacementTarget is Control anchor) Popups.Add(popup, new PopupLease(popup, anchor));
        });
    }

    private sealed class PopupLease : IDisposable
    {
        private readonly Popup _popup;
        private readonly Control _anchor;
        private readonly Window? _window;
        internal PopupLease(Popup popup, Control anchor)
        {
            _popup = popup; _anchor = anchor;
            _window = TopLevel.GetTopLevel(anchor) as Window;
            anchor.DetachedFromVisualTree += Detached;
            anchor.DataContextChanged += Retired;
            anchor.PropertyChanged += AnchorChanged;
            if (_window == null) return;
            _window.Deactivated += Retired;
            _window.PropertyChanged += WindowChanged;
            _window.AddHandler(ScrollViewer.ScrollChangedEvent, Scrolled, RoutingStrategies.Bubble, true);
        }
        private void Retired(object? s, EventArgs e) => _popup.Close();
        private void Detached(object? s, VisualTreeAttachmentEventArgs e) => _popup.Close();
        private void Scrolled(object? s, ScrollChangedEventArgs e)
        {
            // Scrolling INSIDE a popover is intentional; scrolling its anchor away is not.
            if (e.Source is Visual source && (ReferenceEquals(source, _anchor) || source.IsVisualAncestorOf(_anchor))) _popup.Close();
        }
        private void AnchorChanged(object? s, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Visual.IsVisibleProperty && !_anchor.IsEffectivelyVisible) _popup.Close();
        }
        private void WindowChanged(object? s, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Window.WindowStateProperty || e.Property == Visual.IsVisibleProperty || e.Property == InputElement.IsEnabledProperty) _popup.Close();
        }
        public void Dispose()
        {
            _anchor.DetachedFromVisualTree -= Detached;
            _anchor.DataContextChanged -= Retired;
            _anchor.PropertyChanged -= AnchorChanged;
            if (_window == null) return;
            _window.Deactivated -= Retired;
            _window.PropertyChanged -= WindowChanged;
            _window.RemoveHandler(ScrollViewer.ScrollChangedEvent, Scrolled);
        }
    }
}

using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
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
            if (!change.GetNewValue<bool>() || !ToolTip.GetIsOpen(anchor)) return;
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
            if ((popup.ShouldUseOverlayLayer || popup.IsUsingOverlayLayer) && popup.Child is ToolTip) popup.IsHitTestVisible = false;
            if (popup.PlacementTarget is Control anchor) Popups.Add(popup, new PopupLease(popup, anchor));
        });
    }

    internal static void GuardFooterToolTips(Control footer) => _ = new FooterTip(footer);

    private sealed class FooterTip
    {
        private readonly Control _footer;
        private Control? _anchor;
        private DispatcherTimer? _timer;

        internal FooterTip(Control footer)
        {
            _footer = footer;
            // The framework also starts tooltips after scene invalidation. In the footer only
            // routed mouse movement starts the show delay, even if a relayout moves a button.
            ToolTip.SetServiceEnabled(footer, false);
            footer.AddHandler(InputElement.PointerMovedEvent, Moved, RoutingStrategies.Tunnel, handledEventsToo: true);
            footer.AddHandler(InputElement.PointerPressedEvent, (_, _) => Clear(), RoutingStrategies.Tunnel, handledEventsToo: true);
            footer.DetachedFromVisualTree += (_, _) => Clear();
        }

        private void Moved(object? sender, PointerEventArgs args)
        {
            if (args.Pointer.Type == PointerType.Touch || args.Pointer.Captured != null) return;
            Control? anchor = (args.Source as Visual)?.GetSelfAndVisualAncestors().TakeWhile(visual => visual != _footer)
                .OfType<Control>().FirstOrDefault(control => ToolTip.GetTip(control) != null);
            if (anchor == _anchor) return;
            Clear();
            if (anchor == null) return;
            _anchor = anchor;
            anchor.PointerExited += Exited;
            anchor.DetachedFromVisualTree += Detached;
            int delay = ToolTip.GetShowDelay(anchor);
            if (delay <= 0) Open();
            else
            {
                if (_timer == null)
                {
                    _timer = new DispatcherTimer();
                    _timer.Tick += (_, _) => Open();
                }
                _timer.Interval = TimeSpan.FromMilliseconds(delay);
                _timer.Start();
            }
        }

        private void Open()
        {
            _timer?.Stop();
            if (_anchor is { IsPointerOver: true } anchor && TransientHover.WindowUsable(anchor))
                ToolTip.SetIsOpen(anchor, true);
            else Clear();
        }

        private void Exited(object? sender, PointerEventArgs args) => Clear();
        private void Detached(object? sender, VisualTreeAttachmentEventArgs args) => Clear();

        private void Clear()
        {
            _timer?.Stop();
            if (_anchor is not { } anchor) return;
            _anchor = null;
            anchor.PointerExited -= Exited;
            anchor.DetachedFromVisualTree -= Detached;
            ToolTip.SetIsOpen(anchor, false);
        }
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

using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia;
using Avalonia.VisualTree;

namespace MeowChan.Desktop;

/// <summary>
/// The held-key latch of the favourite keys outside the gallery (the gallery's own is GalleryViewer.ConsumeFavoriteRepeat): a held key repeats KeyDown
/// without a KeyUp, so only the first press acts. The latch is released by the key's KeyUp on the window and cleared when the window loses the activation
/// (it never sees the KeyUp then). <see cref="Observe"/> wires the window once, lazily, and lets go of it when the page leaves the visual tree.
/// </summary>
internal sealed class HeldKeyLatch
{
    private readonly HashSet<Key> _held = new();
    private Window? _owner;
    private Control? _host;

    /// <summary>True when the key is held already (a repeat: swallow it); otherwise it is latched and the press is a new one.</summary>
    internal bool IsRepeat(Key key) => !_held.Add(key);

    /// <summary>Whether any key is latched (tests).</summary>
    internal bool IsLatched => _held.Count > 0;

    internal void Release(Key key) => _held.Remove(key);

    internal void Clear() => _held.Clear();

    /// <summary>Starts watching the window of <paramref name="host"/> for key releases and lost activation (idempotent).</summary>
    internal void Observe(Control host)
    {
        if (_host != null || TopLevel.GetTopLevel(host) is not Window owner) return;
        _host = host;
        _owner = owner;
        owner.AddHandler(InputElement.KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        owner.Deactivated += OnDeactivated;
        host.DetachedFromVisualTree += OnDetached;
    }

    private void OnKeyUp(object? sender, KeyEventArgs e) => _held.Remove(e.Key);

    private void OnDeactivated(object? sender, EventArgs e) => _held.Clear();

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _held.Clear();
        if (_owner is { } owner)
        {
            owner.RemoveHandler(InputElement.KeyUpEvent, OnKeyUp);
            owner.Deactivated -= OnDeactivated;
        }
        if (_host is { } host) host.DetachedFromVisualTree -= OnDetached;
        _owner = null;
        _host = null;
    }
}

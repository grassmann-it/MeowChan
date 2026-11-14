using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Motion;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// The behaviour that the archive and the media filter surfaces share (UX103 P2/P8, S1/S2): a <see cref="ToggleButton"/> (the
/// <see cref="FilterToggleButton"/>; checked = open) and a border with the filter fields. The archive surface is still a
/// <c>workspace-overlay</c> that opens over the page and fades in through <see cref="OverlayReveal"/> (<c>overlay</c>); the media
/// surface is an inline section that pushes the tiles down and has no reveal (S1).
/// Escape closes the surface and returns focus to the toggle; hiding it never strands focus on an invisible field; only a
/// deliberate open from the toggle moves focus into the first field. What differs per page stays with the page: who drives the
/// surface's visibility (<c>drivesVisibility</c>: the media page sets it here, the archive page binds it in XAML) and what
/// ends the page's lifetime (<c>isEnded</c>).
/// </summary>
internal sealed class FilterSurfaceController
{
    private readonly Visual _host;
    private readonly ToggleButton _toggle;
    private readonly Border _surface;
    private readonly Func<bool> _isEnded;
    private readonly bool _drivesVisibility;

    internal FilterSurfaceController(Visual host, ToggleButton toggle, Border surface, Func<bool> isEnded, bool drivesVisibility, bool overlay = true)
    {
        _host = host;
        _toggle = toggle;
        _surface = surface;
        _isEnded = isEnded;
        _drivesVisibility = drivesVisibility;
        if (overlay) OverlayReveal.Follow(surface);
        surface.KeyDown += OnKeyDown;
        toggle.PropertyChanged += (_, e) =>
        {
            if (e.Property == ToggleButton.IsCheckedProperty) OnToggled(e.GetNewValue<bool?>() == true);
        };
    }

    internal bool IsOpen => _toggle.IsChecked == true;

    internal ToggleButton Toggle => _toggle;

    /// <summary>Whether the keyboard focus sits on <paramref name="owner"/> or inside it.</summary>
    internal static bool FocusWithin(Visual host, Control owner) => TopLevel.GetTopLevel(host)?.FocusManager?.GetFocusedElement() is Visual focused
        && focused.GetSelfAndVisualAncestors().Contains(owner);

    internal bool FocusWithin(Control owner) => FocusWithin(_host, owner);

    private void OnToggled(bool open)
    {
        if (_isEnded()) return;
        if (!open)
        {
            // Hiding the surface must not strand keyboard focus on an invisible field.
            bool focused = FocusWithin(_surface);
            if (_drivesVisibility) _surface.IsVisible = false;
            if (focused) Toggle.Focus(NavigationMethod.Tab);
            return;
        }
        if (_drivesVisibility) _surface.IsVisible = true;
        // Only a deliberate open from the toggle moves focus in; programmatic opens
        // (invalid draft reveal, commands) keep their own focus target.
        if (!FocusWithin(_toggle)) return;
        FocusFirstField();
    }

    internal void FocusFirstField()
    {
        Border surface = _surface;
        Dispatcher.UIThread.Post(() =>
        {
            if (_isEnded() || !IsOpen || !surface.IsEffectivelyVisible) return;
            surface.GetVisualDescendants().OfType<InputElement>()
                .FirstOrDefault(element => element.Focusable && element.IsEffectivelyEnabled && element.IsEffectivelyVisible)
                ?.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    internal void Close(bool restoreFocus)
    {
        bool focused = FocusWithin(_surface);
        _toggle.IsChecked = false;
        if (restoreFocus || focused) Toggle.Focus(NavigationMethod.Tab);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Open dropdowns, suggestions and date editors handle their own Escape first.
        if (e.Handled || e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None) return;
        e.Handled = true;
        Close(restoreFocus: true);
    }
}

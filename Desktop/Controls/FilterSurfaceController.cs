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
/// The behaviour that the archive and the media filter surfaces share (UX103 P2/P8): an <see cref="Expander"/> that only keeps
/// the toggle, and a <c>workspace-overlay</c> border that opens over the page and fades in through <see cref="OverlayReveal"/>.
/// Escape closes the surface and returns focus to the toggle; hiding it never strands focus on an invisible field; only a
/// deliberate open from the toggle moves focus into the first field. What differs per page stays with the page: who drives the
/// surface's visibility (<c>drivesVisibility</c>: the media page sets it here, the archive page binds it in XAML) and what
/// ends the page's lifetime (<c>isEnded</c>).
/// </summary>
internal sealed class FilterSurfaceController
{
    private readonly Visual _host;
    private readonly Expander _expander;
    private readonly Border _surface;
    private readonly Func<bool> _isEnded;
    private readonly bool _drivesVisibility;

    internal FilterSurfaceController(Visual host, Expander expander, Border surface, Func<bool> isEnded, bool drivesVisibility)
    {
        _host = host;
        _expander = expander;
        _surface = surface;
        _isEnded = isEnded;
        _drivesVisibility = drivesVisibility;
        OverlayReveal.Follow(surface);
        surface.KeyDown += OnKeyDown;
        expander.PropertyChanged += (_, e) =>
        {
            if (e.Property == Expander.IsExpandedProperty) OnToggled(e.GetNewValue<bool>());
        };
    }

    internal bool IsOpen => _expander.IsExpanded;

    internal ToggleButton? Toggle => _expander.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault();

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
            if (focused) Toggle?.Focus(NavigationMethod.Tab);
            return;
        }
        if (_drivesVisibility) _surface.IsVisible = true;
        // Only a deliberate open from the toggle moves focus in; programmatic opens
        // (invalid draft reveal, commands) keep their own focus target.
        if (!FocusWithin(_expander)) return;
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
        _expander.IsExpanded = false;
        if (restoreFocus || focused) Toggle?.Focus(NavigationMethod.Tab);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Open dropdowns, suggestions and date editors handle their own Escape first.
        if (e.Handled || e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None) return;
        e.Handled = true;
        Close(restoreFocus: true);
    }
}

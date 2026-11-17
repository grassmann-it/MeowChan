using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// The one rule for chrome that sits on top of a surface with keys of its own (the gallery, a video in the reader): a click with the mouse, a pen or a
/// finger is an action, not a request to move the keyboard into the control that was hit. A Button takes the focus when it is pressed, so without this the
/// next Space or Enter would press the same button again (the favourite badge un-favourites, Next moves on, mute toggles) instead of reaching the
/// surface's playback and shortcut handling. After the pointer is released and the control has run its own click, the focus goes back to the owner
/// (<c>reclaim</c>). Keyboard activation (Tab to the control, Enter or Space) raises no pointer event, so it keeps the native behaviour: the focus stays on
/// the control.
/// <list type="bullet">
/// <item>Only Buttons (and, by <see cref="ReclaimsProperty"/>, a control the owner names such as the volume slider) are given back. A slider that
/// keeps the focus on purpose (the timeline) and every text input (<see cref="TextBox"/>, <see cref="NumericUpDown"/>, <see cref="ComboBox"/>, the suggestion
/// box) are never a target.</item>
/// <item>A subtree marked with <see cref="ExemptProperty"/> (edge handles, the tag panel's regions, a button that opens another surface) is left alone.</item>
/// <item>The focus only goes back when it still rests on the clicked control or nowhere: a click that moved the focus on purpose (into a field, a
/// menu, another surface) keeps that.</item>
/// <item>A click that opened a flyout or a context menu keeps the focus with the menu (the reclaim must not take it from the menu that has just
/// opened); the focus goes back when the menu closes, never to the invoking button.</item>
/// </list>
/// </summary>
internal sealed class PointerFocusReclaim : IDisposable
{
    /// <summary>
    /// Marks a control and everything below it as no business of the reclaim. Not an inherited property: <see cref="Resolve"/> walks from the hit up to the
    /// scope and stops at the first exempt control on the way, which gives the subtree meaning without leaking into a scope that sits below a marked control.
    /// </summary>
    internal static readonly AttachedProperty<bool> ExemptProperty =
        AvaloniaProperty.RegisterAttached<PointerFocusReclaim, Control, bool>("Exempt", false);

    /// <summary>Marks a control that is not a Button but gives the focus back after a pointer release as a Button does (the volume slider).</summary>
    internal static readonly AttachedProperty<bool> ReclaimsProperty =
        AvaloniaProperty.RegisterAttached<PointerFocusReclaim, Control, bool>("Reclaims", false);

    internal static bool GetExempt(Control control) => control.GetValue(ExemptProperty);
    internal static void SetExempt(Control control, bool value) => control.SetValue(ExemptProperty, value);
    internal static bool GetReclaims(Control control) => control.GetValue(ReclaimsProperty);
    internal static void SetReclaims(Control control, bool value) => control.SetValue(ReclaimsProperty, value);

    private readonly Control _scope;
    private readonly Action _reclaim;
    private readonly Action<Action> _defer;
    private readonly Func<IInputElement?> _focused;
    private readonly EventHandler<PointerReleasedEventArgs> _released;

    private PointerFocusReclaim(Control scope, Action reclaim, Action<Action>? defer, Func<IInputElement?>? focused)
    {
        _scope = scope;
        _reclaim = reclaim;
        // After the pointer's own handling (the button's click, a flyout opening): a tick of the UI thread behind the input that is being routed.
        _defer = defer ?? (step => Dispatcher.UIThread.Post(step, DispatcherPriority.Input));
        _focused = focused ?? (() => TopLevel.GetTopLevel(scope)?.FocusManager?.GetFocusedElement());
        _released = OnReleased;
    }

    /// <summary>
    /// Watches pointer releases below <paramref name="scope"/> (also the handled ones: a Button handles its own release) and calls
    /// <paramref name="reclaim"/> where the rule above says the focus goes back to the owner. <paramref name="defer"/> and <paramref name="focused"/>
    /// are the seams of the tests (the default posts to the UI thread and reads the focus manager of the scope's top level).
    /// </summary>
    internal static PointerFocusReclaim Attach(Control scope, Action reclaim, Action<Action>? defer = null, Func<IInputElement?>? focused = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(reclaim);
        PointerFocusReclaim item = new(scope, reclaim, defer, focused);
        scope.AddHandler(InputElement.PointerReleasedEvent, item._released, RoutingStrategies.Bubble, handledEventsToo: true);
        return item;
    }

    public void Dispose() => _scope.RemoveHandler(InputElement.PointerReleasedEvent, _released);

    /// <summary>
    /// The control a pointer release at <paramref name="source"/> activated, as far as this rule is concerned: the outermost Button (or control that
    /// reclaims) between the source and <paramref name="scope"/>; null for a text input, a slider that keeps its focus, an exempt subtree or a hit
    /// that is no control at all (the picture, a label).
    /// </summary>
    internal static Control? Resolve(Control scope, Visual source)
    {
        Control? candidate = null;
        bool inside = false;
        foreach (Visual node in source.GetSelfAndVisualAncestors())
        {
            if (ReferenceEquals(node, scope)) { inside = true; break; }
            if (node is not Control control) continue;
            if (GetExempt(control) || IsTextInput(control) || control is ScrollBar) return null;
            // A slider owns its pointer and its keys; only one the owner names gives the focus back (the timeline keeps it: arrows seek there).
            // Its own parts (the track's repeat buttons) are Buttons too, so a slider decides before them.
            if (control is Slider) { if (!GetReclaims(control)) return null; candidate = control; continue; }
            if (control is Button || GetReclaims(control)) candidate = control;
        }
        return inside ? candidate : null; // a release that reached the scope through a popup's own tree is not this surface's
    }

    internal static bool IsTextInput(Visual visual) =>
        visual is TextBox or NumericUpDown or ComboBox or Ursa.Controls.AutoCompleteBox;

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        // The left button is the activation (a finger and a pen report it too); the right one only matters when it opens a context menu.
        MouseButton button = e.InitialPressMouseButton;
        if (button is not (MouseButton.Left or MouseButton.Right) || e.Source is not Visual source) return;
        if (Resolve(_scope, source) is not { } control) return;
        bool activation = button == MouseButton.Left;
        _defer(() => Complete(control, activation));
    }

    private void Complete(Control control, bool activation)
    {
        if (WaitForPopup(control)) return;
        if (activation && FocusRestsOn(control)) _reclaim();
    }

    // The click opened a menu of this control (the flyout of a button, the context menu of a right click): the menu owns the focus until it closes.
    private bool WaitForPopup(Control control)
    {
        if (control is Button { Flyout: { IsOpen: true } flyout })
        {
            EventHandler? closed = null;
            closed = (_, _) => { flyout.Closed -= closed; _defer(AfterPopup); };
            flyout.Closed += closed;
            return true;
        }
        if (control.ContextMenu is { IsOpen: true } menu)
        {
            EventHandler<RoutedEventArgs>? closed = null;
            closed = (_, _) => { menu.Closed -= closed; _defer(AfterPopup); };
            menu.Closed += closed;
            return true;
        }
        return false;
    }

    // The menu is gone: back to the owner unless something took the focus on purpose meanwhile (a field the menu entry opened, another surface).
    private void AfterPopup()
    {
        if (_focused() is not Visual focused || FocusIsLost(focused)) { _reclaim(); return; }
        if (IsTextInput(focused) || focused.GetVisualAncestors().Any(IsTextInput)) return;
        if (focused.GetSelfAndVisualAncestors().Any(node => ReferenceEquals(node, _scope))) _reclaim();
    }

    // A focus that is not in any tree any more (the closed menu's entry) is no focus.
    private static bool FocusIsLost(Visual focused) => focused.GetVisualRoot() == null;

    private bool FocusRestsOn(Control control) =>
        _focused() is not Visual focused || FocusIsLost(focused) || focused.GetSelfAndVisualAncestors().Any(node => ReferenceEquals(node, control));
}

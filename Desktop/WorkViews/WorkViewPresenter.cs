using System;
using System.Linq;
using Avalonia.Controls;
using Ursa.Controls;

namespace MeowChan.Desktop.WorkViews;

/// <summary>
/// Applies a resolved <see cref="Presence"/> per element kind (plan AP5 §3.1 item 4). The page's own logic keeps owning
/// <c>IsVisible</c> (state); the presenter only writes what belongs to the view, so the two never fight over a property.
/// </summary>
public static class WorkViewPresenter
{
    /// <summary>Toolbar placement: Required → Never, Direct/Collapsed → AsNeeded, Overflow/Hidden → Always (Hidden is also omitted).</summary>
    public static OverflowMode ToolBarMode(Presence presence) => presence switch
    {
        Presence.Required => OverflowMode.Never,
        Presence.Overflow or Presence.Hidden => OverflowMode.Always,
        _ => OverflowMode.AsNeeded
    };

    /// <summary>
    /// One <see cref="ActionToolBar"/> item: its id (or <paramref name="fallbackId"/> for items without one) decides the
    /// overflow mode and, on a prioritized toolbar, which items move into "More" first (OverflowOrder), and "K" (<see cref="Presence.Hidden"/>) leaves it out of the bar and the popup. Items with neither
    /// keep their XAML overflow mode. Returns whether the item took part.
    /// </summary>
    public static bool ApplyToolBarItem(Control item, WorkView view, string? fallbackId = null, bool nonDefaultState = false) =>
        ApplyToolBarItem(item, view, fallbackId, nonDefaultState, out _);

    /// <summary>As <see cref="ApplyToolBarItem(Control, WorkView, string?, bool)"/>; <paramref name="changed"/> tells whether any of the three values differed.</summary>
    public static bool ApplyToolBarItem(Control item, WorkView view, string? fallbackId, bool nonDefaultState, out bool changed)
    {
        changed = false;
        string? id = WorkViewElement.GetId(item) ?? fallbackId;
        if (id == null) return false;
        Presence presence = WorkViewPolicy.Resolve(id, view, nonDefaultState);
        OverflowMode mode = ToolBarMode(presence);
        bool omitted = presence == Presence.Hidden;
        // Read only by toolbars with PrioritizedOverflow: the policy's OverflowOrder is then the one keep-visible order.
        int priority = WorkViewPolicy.Get(id).OverflowOrder;
        if (ToolBar.GetOverflowMode(item) != mode) { ToolBar.SetOverflowMode(item, mode); changed = true; }
        if (ActionToolBar.GetOmitted(item) != omitted) { ActionToolBar.SetOmitted(item, omitted); changed = true; }
        if (ActionToolBar.GetOverflowPriority(item) != priority) { ActionToolBar.SetOverflowPriority(item, priority); changed = true; }
        return true;
    }

    /// <summary>
    /// Every item of <paramref name="toolbar"/>; <paramref name="nonDefaultState"/> receives the item's resolved id (R6).
    /// The toolbar is invalidated only when an item's placement really changed; returns whether it did.
    /// </summary>
    public static bool ApplyToolBar(ActionToolBar toolbar, WorkView view, string? fallbackId = null,
        Func<Control, bool>? nonDefaultState = null)
    {
        bool changed = false;
        foreach (object? entry in toolbar.Items)
        {
            if (entry is not Control item) continue;
            ApplyToolBarItem(item, view, fallbackId, nonDefaultState?.Invoke(item) == true, out bool itemChanged);
            changed |= itemChanged;
        }
        if (changed) toolbar.InvalidateMeasure();
        return changed;
    }

    /// <summary>
    /// A collapsible section (R13): on entering a view a Collapsed section closes and a Direct/Required one opens; between
    /// view changes the user's choice stays for the session (nothing is saved). A non-default state (R6) opens it.
    /// </summary>
    public static void ApplyExpander(Expander expander, WorkView view, bool viewChanged, bool nonDefaultState = false)
    {
        string id = WorkViewElement.GetId(expander)
            ?? throw new InvalidOperationException("A work-view expander needs a WorkViewElement.Id.");
        Presence presence = WorkViewPolicy.Resolve(id, view, nonDefaultState);
        if (viewChanged || nonDefaultState) expander.IsExpanded = presence != Presence.Collapsed;
    }

    /// <summary>The same rule (R13) for a toggle that opens a surface, such as the filter toggle of Archive and Media.</summary>
    public static void ApplyToggle(Avalonia.Controls.Primitives.ToggleButton toggle, WorkView view, bool viewChanged, bool nonDefaultState = false)
    {
        string id = WorkViewElement.GetId(toggle)
            ?? throw new InvalidOperationException("A work-view toggle needs a WorkViewElement.Id.");
        Presence presence = WorkViewPolicy.Resolve(id, view, nonDefaultState);
        if (viewChanged || nonDefaultState) toggle.IsChecked = presence != Presence.Collapsed;
    }
}

/// <summary>
/// The one call a page makes for a free element outside a toolbar: <c>WorkViewGate.Apply(control, view, stateVisible)</c>
/// instead of scattered view checks. Avalonia styles cannot hide a locally shown control, so the page writes the result.
/// An element the view hides while its state wants it shown keeps its shortcut (CommandBindings.RouteVisibleActions).
/// </summary>
public static class WorkViewGate
{
    /// <summary>Whether the view shows the element on its surface (Direct, Required, or Collapsed with its header).</summary>
    public static bool Visible(Control control, WorkView view, bool nonDefaultState = false) =>
        WorkViewPolicy.OnSurface(WorkViewPolicy.Resolve(Id(control), view, nonDefaultState));

    /// <summary>Sets <c>IsVisible</c> = state and view; remembers the state so reduced elements stay reachable by shortcut.</summary>
    public static void Apply(Control control, WorkView view, bool stateVisible = true, bool nonDefaultState = false)
    {
        WorkViewElement.SetStateVisible(control, stateVisible);
        WorkViewElement.SetGated(control, true);
        control.IsVisible = stateVisible && Visible(control, view, nonDefaultState);
    }

    /// <summary>True when a gate hides the element only because of the view (its state would show it).</summary>
    internal static bool HiddenByView(Control control) =>
        !control.IsVisible && WorkViewElement.GetGated(control) && WorkViewElement.GetStateVisible(control)
        && WorkViewElement.GetId(control) != null;

    private static string Id(Control control) => WorkViewElement.GetId(control)
        ?? throw new InvalidOperationException((control.Name ?? control.GetType().Name) + " has no WorkViewElement.Id.");
}

/// <summary>The view of one main window (plan §3.1 item 5). Pages and dialogs built later read <see cref="Current"/> when opening.</summary>
public sealed class WorkViewState
{
    private WorkView? _current;

    public WorkView Current => _current ?? WorkView.Barebones;

    /// <summary>Raised after the window applied a different view: (previous, current). Not raised for the first view.</summary>
    public event Action<WorkView, WorkView>? Changed;

    internal void Set(WorkView view)
    {
        if (_current is not { } previous) { _current = view; return; }
        if (view == previous) return;
        _current = view;
        Changed?.Invoke(previous, view);
    }
}

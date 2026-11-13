using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;

namespace MeowChan.Desktop.WorkViews;

/// <summary>
/// R2: a shortcut keeps working when the view (or a narrow window) moved its button off the bar. The generic
/// <see cref="CommandBindings.RouteVisibleActions"/> finds only effectively visible visual descendants; a button inside the
/// closed "More" popup of an <see cref="ActionToolBar"/>, an omitted ("K") item and a free element hidden by
/// <see cref="WorkViewGate"/> are none of these. When no visible action matched, that route asks this one, which applies
/// the same rules to exactly those reduced controls of the same context: their surface must be on screen, an explicit
/// <see cref="CommandRouter"/> registration still wins, several matches stay ambiguous, a disabled match consumes the key.
/// No per-button registration is needed, so moving a button never needs shortcut code.
/// </summary>
public static class WorkViewShortcuts
{
    /// <summary>The reduced controls of <paramref name="context"/> whose own surface is effectively visible.</summary>
    public static IReadOnlyList<Control> ReducedControls(Control context)
    {
        List<Control> reduced = new();
        foreach (ActionToolBar toolbar in context.GetVisualDescendants().Prepend(context).OfType<ActionToolBar>()
            .Where(toolbar => toolbar.IsEffectivelyVisible))
            reduced.AddRange(toolbar.ReducedItems.Where(item => item.IsVisible));
        TopLevel? top = TopLevel.GetTopLevel(context);
        foreach (Control element in WorkViewElement.All().Where(WorkViewGate.HiddenByView))
            if (element.GetVisualParent() is Visual { IsEffectivelyVisible: true } parent
                && (ReferenceEquals(parent, context) || context.IsVisualAncestorOf(parent))
                && ReferenceEquals(TopLevel.GetTopLevel(element), top)
                && !reduced.Contains(element))
                reduced.Add(element);
        return reduced;
    }

    /// <summary>Continuation of <see cref="CommandBindings.RouteVisibleActions"/> when it found no visible match.</summary>
    internal static bool RouteReduced(Control context, KeyEventArgs e)
    {
        if (e.Handled) return false;
        // Most unmatched keys belong to no command at all: skip the tree walk for them.
        if (!CommandCatalog.All.Any(command => ShortcutService.Current.Matches(e, command.Id))) return false;
        Control[] matches = ReducedControls(context)
            .Where(control => control is Button or MenuItem or ComboBox or TabItem or Expander)
            .Where(control => CommandBindings.ActionId(control) is string id
                && !CommandRouter.HasExplicitRegistration(id, context) && CommandBindings.Matches(e, id))
            .ToArray();
        if (matches.Length == 0) return false;
        e.Handled = true;
        // As for visible actions: never invent a target among several materialized ones.
        if (matches.Length > 1) return true;
        Control target = matches[0];
        if (!Enabled(target)) return true;
        CommandBindings.InvokeAction(target);
        return true;
    }

    // A detached popup child has no enabled ancestors to inherit from; its own state and its surface's decide.
    private static bool Enabled(Control target)
    {
        if (!target.IsEnabled) return false;
        Control? surface = target.FindLogicalAncestorOfType<ActionToolBar>() ?? target.GetVisualParent() as Control;
        return surface?.IsEffectivelyEnabled ?? true;
    }
}

/// <summary>
/// "More" for surfaces without an <see cref="ActionToolBar"/> (gallery overflow, plugin "More actions", rules ⋯, media header):
/// menu entries that forward to the moved button itself, mirroring its label, shortcut, enabled state and disabled reason.
/// There is no second feature logic; the button stays the single executor.
/// </summary>
public static class WorkViewOverflow
{
    /// <summary>A menu entry for <paramref name="target"/> under its catalog command <paramref name="commandId"/>.</summary>
    public static MenuItem CreateForwardingItem(Button target, string commandId, Func<string?>? unavailableReason = null) =>
        CommandBindings.CreateMenuItem(commandId, () => CommandBindings.InvokeAction(target),
            () => target.IsEnabled && (target.Command?.CanExecute(target.CommandParameter) ?? true), unavailableReason);

    /// <summary>
    /// Fills <paramref name="menu"/> with one forwarding entry per button of <paramref name="buttons"/> that the policy puts
    /// into the overflow (<see cref="Presence.Overflow"/>) in <paramref name="view"/> and whose state shows it, in policy order.
    /// Returns the number of entries (for "More, n actions", R7).
    /// </summary>
    public static int Populate(ItemsControl menu, IEnumerable<Button> buttons, WorkView view)
    {
        menu.Items.Clear();
        var entries = buttons
            .Select(button => (Button: button, Id: WorkViewElement.GetId(button)))
            .Where(item => item.Id != null && WorkViewElement.GetStateVisible(item.Button))
            .Select(item => (item.Button, Entry: WorkViewPolicy.Get(item.Id!)))
            .Where(item => item.Entry.For(view) == Presence.Overflow && item.Entry.CommandId != null)
            .OrderBy(item => item.Entry.OverflowOrder).ToArray();
        foreach (var (button, entry) in entries)
            menu.Items.Add(CreateForwardingItem(button, entry.CommandId!));
        return entries.Length;
    }
}

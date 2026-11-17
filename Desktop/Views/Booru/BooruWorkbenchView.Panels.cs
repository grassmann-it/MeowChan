using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MeowChan.Desktop.Booru;

/// <summary>Replaces the children of a card list while keeping keyboard focus where the user had it.</summary>
internal sealed partial class BooruWorkbenchView
{
    /// <summary>
    /// Replaces the children while keeping keyboard focus inside the panel when it was there: on <paramref name="moveTo"/>
    /// when given, else on the same named control (or its pause/resume counterpart) when it survives enabled. A surviving but
    /// disabled one is waited for when <paramref name="waitWhileDisabled"/> (a running command), otherwise, and when the
    /// control is gone, focus goes to the first enabled button of the same card or its nearest neighbour, then to
    /// <paramref name="fallback"/>. Without a fallback a vanished control leaves focus where the platform puts it.
    /// With <paramref name="incremental"/> the list returned by <paramref name="build"/> may contain controls that are already children
    /// (the unchanged tiles of the result list): those stay in place, only the others are removed, inserted or moved, and focus
    /// that sits in a surviving control is not touched at all.
    /// </summary>
    private void RebuildPanel(Panel panel, Func<List<Control>> build, string? moveTo = null, Func<Control?>? fallback = null,
        bool waitWhileDisabled = false, bool incremental = false)
    {
        Visual? focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
        int card = -1;
        if (focused is not null)
            for (int index = 0; index < panel.Children.Count; index++)
                if (ReferenceEquals(panel.Children[index], focused) || panel.Children[index].IsVisualAncestorOf(focused)) { card = index; break; }
        string? focusedName = card >= 0 && focused is Control { Name: { } name } ? name : null;
        if (_pendingFocus is { } pending && ReferenceEquals(pending.Panel, panel))
        {
            _pendingFocus = null;
            // Focus that fell out of the window when its button was disabled comes back; focus the user moved elsewhere stays.
            if (card < 0 && (focused is null || ReferenceEquals(focused, TopLevel.GetTopLevel(this)))) { card = pending.Card; focusedName = pending.Name; }
        }
        if (incremental) ReconcileChildren(panel, build());
        else
        {
            panel.Children.Clear();
            foreach (Control child in build()) panel.Children.Add(child);
        }
        if (card < 0) return;
        // The focused control is part of a tile that stayed: nothing was detached, so there is nothing to bring back.
        if (incremental && moveTo is null && focused is not null && !ReferenceEquals(focused, TopLevel.GetTopLevel(this))
            && ReferenceEquals(TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement(), focused) && panel.IsVisualAncestorOf(focused))
            return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed) return;
            Control? target = Enabled(panel, moveTo) ?? Enabled(panel, focusedName) ?? Enabled(panel, Counterpart(focusedName));
            if (target is null && waitWhileDisabled && focusedName is not null
                && panel.GetVisualDescendants().OfType<Control>().Any(control => control.Name == focusedName))
            {
                _pendingFocus = (panel, focusedName, card);
                return;
            }
            if (target is null && fallback is not null)
            {
                if (panel.Children.Count > 0)
                    target = panel.Children[Math.Min(card, panel.Children.Count - 1)].GetVisualDescendants().OfType<Button>()
                        .FirstOrDefault(button => button.IsEffectivelyEnabled && button.IsEffectivelyVisible && button.Focusable);
                target ??= fallback();
            }
            target?.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Makes <paramref name="panel"/> hold exactly <paramref name="desired"/> in that order, detaching only controls that are no longer wanted or had to move.</summary>
    internal static void ReconcileChildren(Panel panel, IReadOnlyList<Control> desired)
    {
        HashSet<Control> wanted = new(desired, ReferenceEqualityComparer.Instance);
        for (int index = panel.Children.Count - 1; index >= 0; index--)
            if (!wanted.Contains(panel.Children[index])) panel.Children.RemoveAt(index);
        for (int index = 0; index < desired.Count; index++)
        {
            Control want = desired[index];
            if (index < panel.Children.Count && ReferenceEquals(panel.Children[index], want)) continue;
            int at = panel.Children.IndexOf(want);
            if (at >= 0) panel.Children.RemoveAt(at);
            panel.Children.Insert(index, want);
        }
    }

    private static Control? Enabled(Panel panel, string? name) => name is null ? null
        : panel.GetVisualDescendants().OfType<Control>().FirstOrDefault(control => control.Name == name && control.IsEffectivelyEnabled);

    private static string? Counterpart(string? name) =>
        name is null ? null
        : name.StartsWith(PausePrefix, StringComparison.Ordinal) ? ResumePrefix + name[PausePrefix.Length..]
        : name.StartsWith(ResumePrefix, StringComparison.Ordinal) ? PausePrefix + name[ResumePrefix.Length..]
        : null;
}

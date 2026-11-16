using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>What a favourite key asks for (<see cref="FavoriteKeys.Classify"/>).</summary>
internal enum FavoriteKeyAction { None, Add, Remove, Toggle }

/// <summary>
/// The favourite keys outside the gallery (pure; Tom's decision of 15.11.2026, defaults revision 20): M adds, U removes and F toggles, uniformly on every
/// surface that shows or selects a medium (the reader's post with the medium of <see cref="ReaderMediaActivation.Choose"/>, its media sidebar, the archive's
/// media tiles and list, the Media page's grid and panel). The keys are always read from the bindings. The gallery keeps its own routing (F there asks
/// before it removes). A selection of several media is written as one decision: add touches only the ones that are no favourites, remove only the ones that
/// are, and the toggle removes all when every one is a favourite and otherwise adds the missing ones. A medium whose state is unknown is never written.
/// </summary>
internal static class FavoriteKeys
{
    internal const string AddId = "favorites.add", RemoveId = "favorites.remove", ToggleId = "favorites.toggle";

    /// <summary>The favourite command a key matches (add before remove before toggle); None for any other key.</summary>
    internal static FavoriteKeyAction Classify(Func<string, bool> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        return matches(AddId) ? FavoriteKeyAction.Add : matches(RemoveId) ? FavoriteKeyAction.Remove
            : matches(ToggleId) ? FavoriteKeyAction.Toggle : FavoriteKeyAction.None;
    }

    /// <summary>
    /// The decision of an action over <paramref name="marked"/> (true: a favourite, false: none, null: unknown): Add or Remove, or None when nothing is known or
    /// the action is none. The toggle removes all when every known one is a favourite and otherwise adds the rest.
    /// </summary>
    private static FavoriteKeyAction Decide(FavoriteKeyAction action, IReadOnlyList<bool?> marked)
    {
        if (action != FavoriteKeyAction.Toggle) return action;
        bool any = false, all = true;
        foreach (bool? state in marked)
        {
            if (state == null) continue;
            any = true;
            all &= state == true;
        }
        return !any ? FavoriteKeyAction.None : all ? FavoriteKeyAction.Remove : FavoriteKeyAction.Add;
    }

    /// <summary>
    /// The positions of <paramref name="marked"/> (true: a favourite, false: none, null: unknown) the action writes, in order. Empty when nothing changes: add on
    /// favourites only, remove on none, or nothing known. Callers write each one as an explicit target (<see cref="Writes"/>), never as a flip.
    /// </summary>
    internal static IReadOnlyList<int> Targets(FavoriteKeyAction action, IReadOnlyList<bool?> marked)
    {
        ArgumentNullException.ThrowIfNull(marked);
        action = Decide(action, marked);
        if (action is not (FavoriteKeyAction.Add or FavoriteKeyAction.Remove)) return Array.Empty<int>();
        bool wanted = action == FavoriteKeyAction.Remove; // the state that is changed
        List<int> targets = new();
        for (int i = 0; i < marked.Count; i++)
            if (marked[i] == wanted) targets.Add(i);
        return targets;
    }

    /// <summary>
    /// The explicit writes of an action: the positions of <see cref="Targets"/> with the value each one is SET to (add: true, remove: false). A held or
    /// repeated key therefore never undoes itself: M twice stays a favourite, U twice stays none. <paramref name="effective"/> is the value a medium has
    /// once its pending write lands (the pending target when a write is pending, else the saved state); the toggle is relative to it.
    /// </summary>
    internal static IReadOnlyList<(int Index, bool Selected)> Writes(FavoriteKeyAction action, IReadOnlyList<bool?> effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        IReadOnlyList<int> targets = Targets(action, effective);
        if (targets.Count == 0) return Array.Empty<(int, bool)>();
        bool selected = Decide(action, effective) == FavoriteKeyAction.Add;
        return targets.Select(index => (index, selected)).ToArray();
    }

    /// <summary>
    /// The tooltip of a favourite star (every star of the app that toggles): its name with the toggle's key, then the keys that add and remove
    /// ("Add to favorites · F" / "Add favorite · M / B · Remove favorite · U"). All keys come from the bindings, so a rebinding never makes it lie.
    /// </summary>
    internal static string Tooltip(bool marked)
    {
        string label = Texts.T(marked ? "viewer.favorite.remove" : "viewer.favorite.add");
        string headline = CommandBindings.TooltipHeadline(ToggleId, label);
        List<string> keys = new();
        foreach (string id in new[] { AddId, RemoveId })
            if (CommandCatalog.ShortcutDisplay(id) != null) keys.Add(CommandBindings.TooltipHeadline(id));
        return keys.Count == 0 ? headline : headline + "\n" + string.Join(" · ", keys);
    }
}

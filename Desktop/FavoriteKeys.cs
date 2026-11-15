using System;
using System.Collections.Generic;
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
    /// The positions of <paramref name="marked"/> (true: a favourite, false: none, null: unknown) the action writes, in order. Each one is flipped (the shared
    /// favourite call is a toggle). Empty when nothing changes: add on favourites only, remove on none, or nothing known.
    /// </summary>
    internal static IReadOnlyList<int> Targets(FavoriteKeyAction action, IReadOnlyList<bool?> marked)
    {
        ArgumentNullException.ThrowIfNull(marked);
        if (action == FavoriteKeyAction.Toggle)
        {
            bool any = false, all = true;
            foreach (bool? state in marked)
            {
                if (state == null) continue;
                any = true;
                all &= state == true;
            }
            if (!any) return Array.Empty<int>();
            action = all ? FavoriteKeyAction.Remove : FavoriteKeyAction.Add;
        }
        if (action == FavoriteKeyAction.None) return Array.Empty<int>();
        bool wanted = action == FavoriteKeyAction.Remove; // the state that is flipped
        List<int> targets = new();
        for (int i = 0; i < marked.Count; i++)
            if (marked[i] == wanted) targets.Add(i);
        return targets;
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

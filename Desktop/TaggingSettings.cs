using System;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Desktop;

// PT7 (PLAN-POWER-TAGGEN-2026-11-14): the tagging settings as the code that uses them needs them, without any UI. The stamp size, the walk
// window, the undo depth and the card choices are Config fields with default, minimum and maximum (Config.TagStampSize,
// Config.TagQueueWalkWindow, Config.TagQueueUndoDepth, Config.TaggingCardCount / TaggingCards / TaggingCardsDefault); this is the one place that turns them into option objects and card lists,
// so the window, the settings page and the tests read the same values. A value outside its range is clamped here once more, so a stale or
// hand-built Config cannot produce one.

/// <summary>The tagging settings of a profile as option objects and card lists.</summary>
internal static class TaggingSettings
{
    /// <summary>The quick tag options with the configured stamp size (the other limits keep their defaults until the host binds them).</summary>
    internal static QuickTagOptions StampOptions(Config config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return QuickTagSettings.SurfaceOptions(config); // one place turns the settings into options (stamp size included)
    }

    /// <summary>How many open queue entries one walk opens at once.</summary>
    internal static int WalkWindow(Config config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Math.Clamp(config.TagQueueWalkWindow, Config.TagQueueWalkWindowMinimum, Config.TagQueueWalkWindowMaximum);
    }

    /// <summary>How many queue decisions of a walk can be taken back (the walk's undo stack and the host's decision history).</summary>
    internal static int UndoDepth(Config config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Math.Clamp(config.TagQueueUndoDepth, Config.TagQueueUndoDepthMinimum, Config.TagQueueUndoDepthMaximum);
    }

    /// <summary>
    /// The ids of all card slots (key 1 first) as the settings page shows them: the saved choice, repeated and unknown names dropped,
    /// short lists filled up from the default set. Always <see cref="TaggingCardCommands.CardKeys"/> ids, all different.
    /// </summary>
    internal static IReadOnlyList<string> CardSlotIds(Config config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Slots(config.TaggingCards);
    }

    /// <summary>
    /// What to save for the slots the person chose: all different ids in slot order, or an empty list when they are the default set
    /// (so a profile that never changed the cards keeps following the default).
    /// </summary>
    internal static string[] CardsToSave(IEnumerable<string?> slotIds)
    {
        string[] ids = Slots(slotIds).ToArray();
        return ids.SequenceEqual(TaggingCardActions.DefaultIds) ? [] : ids;
    }

    /// <summary>
    /// Gives slot <paramref name="slot"/> the action <paramref name="id"/>. When another slot already has it, that slot takes the action the
    /// chosen slot had (the two swap), so the five slots always stay different. An unknown id or slot changes nothing.
    /// </summary>
    internal static string[] WithCard(IReadOnlyList<string> ids, int slot, string id)
    {
        string[] result = Slots(ids).ToArray();
        if (slot < 0 || slot >= result.Length || TaggingCardActions.Find(id) is not { } action) return result;
        int other = Array.FindIndex(result, item => item == action.Id);
        if (other >= 0 && other != slot) result[other] = result[slot];
        result[slot] = action.Id;
        return result;
    }

    private static IReadOnlyList<string> Slots(IEnumerable<string?>? ids) =>
        TaggingCardSettings.From(false, ids, TaggingCardCommands.CardKeys).Cards.Select(action => action.Id).ToArray();
}

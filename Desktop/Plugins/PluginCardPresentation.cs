using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Plugins;

/// <summary>What the order of the Plugins list needs to know about one card; a plain value so the order is testable without a window.</summary>
internal sealed record PluginCardRank(string Key, string Name, PluginAvailability Availability, bool Running);

/// <summary>
/// The order of the plugin cards: plugins with activity right now first, then by status (active, disabled, unavailable, not loaded),
/// then by name. Total and deterministic: the name compares ignoring case and culture, then exactly, then the key decides, so the
/// order never depends on the order the catalog happens to list the entries in. The same input gives the same order on every refresh.
/// </summary>
internal static class PluginCardOrder
{
    internal static int StatusRank(PluginAvailability availability) => availability switch
    {
        PluginAvailability.Active => 0, PluginAvailability.Disabled => 1, PluginAvailability.Unavailable => 2, _ => 3
    };

    internal static IReadOnlyList<PluginCardRank> Order(IEnumerable<PluginCardRank> cards) => cards
        .OrderBy(card => card.Running ? 0 : 1)
        .ThenBy(card => StatusRank(card.Availability))
        .ThenBy(card => card.Name, StringComparer.InvariantCultureIgnoreCase)
        .ThenBy(card => card.Name, StringComparer.Ordinal)
        .ThenBy(card => card.Key, StringComparer.Ordinal)
        .ToArray();
}

/// <summary>
/// Which plugins count as "running" for the order. A worker that is processing (or about to process) a request is busy only for a
/// moment, so a plugin stays in the running group for <see cref="Hold"/> after it was last seen busy: the list does not jump up
/// and down while a plugin alternates between requests and idling.
/// </summary>
internal sealed class PluginActivityTracker
{
    /// <summary>How long a plugin that was busy stays in the running group.</summary>
    internal static readonly TimeSpan Hold = TimeSpan.FromSeconds(10);

    private readonly Dictionary<string, DateTimeOffset> _lastBusy = new(StringComparer.Ordinal);

    /// <summary>Worker states that mean a request is being processed or is about to be.</summary>
    internal static bool IsBusyState(string? state) => state is "running" or "waiting-for-call";

    internal static bool IsBusy(PluginEntryPresentation presentation) => IsBusyState(presentation.Worker?.State);

    /// <summary>Records the observation of one plugin and says whether it belongs to the running group now.</summary>
    internal bool Observe(string key, bool busyNow, DateTimeOffset now)
    {
        if (busyNow) _lastBusy[key] = now;
        return _lastBusy.TryGetValue(key, out DateTimeOffset last) && now - last < Hold && now >= last;
    }

    /// <summary>Forgets plugins that no longer have a card.</summary>
    internal void Retain(IEnumerable<string> keys)
    {
        HashSet<string> keep = new(keys, StringComparer.Ordinal);
        foreach (string gone in _lastBusy.Keys.Where(key => !keep.Contains(key)).ToArray()) _lastBusy.Remove(gone);
    }
}

/// <summary>
/// The stats at the foot of every plugin card, the same four on every card: a disabled, unused or not yet loaded plugin shows
/// zeros, never a blank or a missing row. Priority 0 stays longest in a narrow card.
/// </summary>
internal static class PluginCardStats
{
    internal static IReadOnlyList<StatFooterItem> Items(PluginEntryPresentation? presentation, int files, bool busyNow) =>
    [
        new("sources", "globe", Texts.T("plugins.stat.sources"), presentation?.Sources.Count ?? 0, 0),
        new("filters", "filter", Texts.T("plugins.stat.filters"), presentation?.Filters.Count ?? 0, 1),
        new("running", "activity", Texts.T("plugins.stat.running"), busyNow ? 1 : 0, 2),
        new("files", "list", Texts.T("plugins.stat.files"), Math.Max(0, files), 3)
    ];
}

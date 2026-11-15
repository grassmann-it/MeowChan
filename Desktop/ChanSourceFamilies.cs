using System;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Desktop;

/// <summary>
/// Host-owned table of the bundled engine-family chan plugins (one plugin, several sites). Every site is its own
/// source with a stable source ID and its own on/off switch (<see cref="ChanSiteSwitches"/>); the family plugin's
/// enabled state stays the master switch. Never inferred from a plugin's self-declared identity.
/// </summary>
internal static class ChanSourceFamilies
{
    internal const string Vichan = "meowchan.source.vichan";
    internal const string Jschan = "meowchan.source.jschan";

    /// <summary>Known sites per family. A site that a (local, edited) family build adds beyond this list starts off.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> Sites = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [Vichan] = ["lainchan", "leftypol", "sushichan", "uboachan", "wizchan"],
        [Jschan] = ["zzzchan"]
    };

    /// <summary>The single-site bundles of 1.0.x that a family replaced: old plugin ID → (family, unchanged source ID).</summary>
    internal static readonly IReadOnlyDictionary<string, (string Family, string SourceId)> Replaced =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["meowchan.source.lainchan"] = (Vichan, "lainchan"),
            ["meowchan.source.leftypol"] = (Vichan, "leftypol"),
            ["meowchan.source.sushichan"] = (Vichan, "sushichan"),
            ["meowchan.source.uboachan"] = (Vichan, "uboachan"),
            ["meowchan.source.wizchan"] = (Vichan, "wizchan"),
            ["meowchan.source.zzzchan"] = (Jschan, "zzzchan")
        };

    /// <summary>Old plugin ID → successor plugin ID, for the profile store's replaced-identity rule.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Successors =
        Replaced.ToDictionary(pair => pair.Key, pair => pair.Value.Family, StringComparer.Ordinal);

    internal static bool IsFamily(string pluginId) => Sites.ContainsKey(pluginId);

    /// <summary>A site's state before the user chooses: experimental sites start off, the others on, unknown sites off.</summary>
    internal static bool DefaultEnabled(string pluginId, string sourceId) =>
        Sites.TryGetValue(pluginId, out string[]? sites) && sites.Contains(sourceId, StringComparer.Ordinal)
        && !ExperimentalSourceActivationPolicy.IsEligible(pluginId, sourceId);
}

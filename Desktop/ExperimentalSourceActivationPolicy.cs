using System;
using System.Collections.Generic;

namespace MeowChan.Desktop;

/// <summary>Host-owned identities for the explicit experimental-source activation flow.</summary>
internal static class ExperimentalSourceActivationPolicy
{
    private static readonly IReadOnlyDictionary<string, string> Sources = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["meowchan.source.kohlchan"] = "kohlchan",
        ["meowchan.source.wizchan"] = "wizchan",
        ["meowchan.source.endchan"] = "endchan",
        ["meowchan.source.leftypol"] = "leftypol",
        ["meowchan.source.2ch"] = "2ch",
        ["meowchan.source.uboachan"] = "uboachan",
        ["meowchan.source.zzzchan"] = "zzzchan",
        ["meowchan.source.sushichan"] = "sushichan",
        // Bundled 1.0.3 imageboard adapters are MeowChan-maintained experimental
        // sources too. Keep them opt-in even though they live under Examples in
        // the repository; that path is a build layout, not a product tier.
        ["meowchan.source.sevenchan"] = "sevenchan",
        ["meowchan.source.bbwchan"] = "bbwchan",
        ["meowchan.source.anonimageboards"] = "anonimageboards",
        ["meowchan.source.eightchanmoe"] = "eightchanmoe",
        ["meowchan.source.gurocx"] = "gurocx",
        ["meowchan.source.fchan"] = "fchan",
        ["meowchan.source.capybarachan"] = "capybarachan"
    };

    // Engine-family plugins (one plugin, several sites): the experimental sites of each family.
    // A family itself is enabled by default; each of these sites starts off behind its own switch
    // (ChanSiteSwitches), so a family never turns on an experimental site by itself.
    private static readonly IReadOnlyDictionary<string, string[]> FamilySources = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [ChanSourceFamilies.Vichan] = ["leftypol", "sushichan", "uboachan", "wizchan"],
        [ChanSourceFamilies.Jschan] = ["zzzchan"]
    };

    // Experimental describes delivery maturity, not operator approval or a
    // third-party/product-origin tier. The seven bundled 1.0.3 adapters are
    // MeowChan-maintained and opt-in, but they do not inherit this separate
    // strict mismatch policy merely from that label. These four existing
    // sources retain strict comparisons.
    internal static bool IsStrictExperimentalSource(string pluginOwnerId, string sourceId) =>
        IsEligible(pluginOwnerId, sourceId) && sourceId is "2ch" or "uboachan" or "zzzchan" or "sushichan";

    internal static bool RequestsMismatchPermission(string pluginOwnerId, string sourceId) =>
        IsEligible(pluginOwnerId, sourceId) && !IsStrictExperimentalSource(pluginOwnerId, sourceId);

    internal static bool IsDisabledByDefault(string pluginId) => Sources.ContainsKey(pluginId);

    internal static bool InitiallyEnabled(string pluginId) => !IsDisabledByDefault(pluginId);

    internal static bool IsEligible(string pluginOwnerId, string sourceId) =>
        Sources.TryGetValue(pluginOwnerId, out string? expectedSource) && expectedSource == sourceId
        || FamilySources.TryGetValue(pluginOwnerId, out string[]? family) && Array.IndexOf(family, sourceId) >= 0;
}

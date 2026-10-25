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
        ["meowchan.source.sushichan"] = "sushichan"
    };

    // Experimental describes delivery maturity; it never implies permission to bypass
    // exact provider metadata. These four sources retain strict comparisons.
    internal static bool IsStrictExperimentalSource(string pluginOwnerId, string sourceId) =>
        IsEligible(pluginOwnerId, sourceId) && sourceId is "2ch" or "uboachan" or "zzzchan" or "sushichan";

    internal static bool RequestsMismatchPermission(string pluginOwnerId, string sourceId) =>
        IsEligible(pluginOwnerId, sourceId) && !IsStrictExperimentalSource(pluginOwnerId, sourceId);

    internal static bool IsDisabledByDefault(string pluginId) => Sources.ContainsKey(pluginId);

    internal static bool InitiallyEnabled(string pluginId) => !IsDisabledByDefault(pluginId);

    internal static bool IsEligible(string pluginOwnerId, string sourceId) =>
        Sources.TryGetValue(pluginOwnerId, out string? expectedSource) && expectedSource == sourceId;
}

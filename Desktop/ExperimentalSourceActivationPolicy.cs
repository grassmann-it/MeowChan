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
        ["meowchan.source.endchan"] = "endchan"
    };

    internal static bool IsDisabledByDefault(string pluginId) => Sources.ContainsKey(pluginId);

    internal static bool InitiallyEnabled(string pluginId) => !IsDisabledByDefault(pluginId);

    internal static bool IsEligible(string pluginOwnerId, string sourceId) =>
        Sources.TryGetValue(pluginOwnerId, out string? expectedSource) && expectedSource == sourceId;
}

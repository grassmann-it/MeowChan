using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.PluginHost.Sources;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop;

internal sealed record PluginSourcePolicyAuthority(string PluginOwnerId, string SourceId, SourceMaturity Maturity, bool IsActive);

internal sealed partial class PluginSession
{
    private readonly Func<string, string, SourceMediaIntegrityPolicyService>? _sourceMediaIntegrityPolicyFactory;

    internal IReadOnlyList<PluginSourcePolicyAuthority> GetSourceMediaIntegrityAuthorities()
    {
        lock (_state)
            return Array.AsReadOnly(Sources.Select(source => new PluginSourcePolicyAuthority(
                _sourceOwners[source], source.Name, source.Description.Maturity,
                IsSourceMediaIntegrityActive(source))).ToArray());
    }

    // Caller holds _state. Candidate/Pending/LastGood descriptions never confer authority.
    private bool IsSourceMediaIntegrityActive(PluginSourceImageBoard source) => !_disposed
        && _sourceOwners.TryGetValue(source, out string? owner)
        && _active.TryGetValue(owner, out var activation) && activation.Enabled && activation.Active != null;

    private static void SetSourceMediaIntegrityActive(PluginSourceImageBoard source, bool active)
    {
        if (source.MediaIntegrityPolicy is SourceMediaIntegrityPolicyService policy) policy.SetActive(active);
    }
}

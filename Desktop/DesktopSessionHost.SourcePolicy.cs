using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MeowChan.Desktop;

internal sealed partial class DesktopSessionHost
{
    private readonly object _sourcePolicySync = new();
    private bool _sourceMediaPoliciesRetired;
    private readonly Dictionary<string, SourceMediaIntegrityPolicyService> _sourceMediaPolicies = new(StringComparer.Ordinal);

    internal SourceMediaIntegrityPolicyService GetOrCreateSourceMediaIntegrityPolicy(string pluginOwnerId, string sourceId)
    {
        string key = SourceMediaIntegrityPermission.CreateKey(pluginOwnerId, sourceId);
        lock (_sourcePolicySync)
        {
            if (!_sourceMediaPolicies.TryGetValue(key, out SourceMediaIntegrityPolicyService? policy))
            {
                Config.SourceMediaIntegrityPermissions.TryGetValue(key, out SourceMediaIntegrityPermission? saved);
                // A persisted unknown/hand-edited grant cannot relax a strict new source.
                if (ExperimentalSourceActivationPolicy.IsStrictExperimentalSource(pluginOwnerId, sourceId)) saved = null;
                policy = new SourceMediaIntegrityPolicyService(_profileLease.ProfileDirectory, pluginOwnerId, sourceId, saved);
                if (_sourceMediaPoliciesRetired) policy.Retire();
                _sourceMediaPolicies.Add(key, policy);
            }
            return policy;
        }
    }

    internal IReadOnlyList<SourceMediaIntegrityPolicyView> GetSourceMediaIntegrityPolicies() => Projection.SourcePolicies;

    private IReadOnlyList<SourceMediaIntegrityPolicyView> CaptureSourceMediaIntegrityPolicies()
    {
        PluginSourcePolicyAuthority[] authorities = Plugins?.GetSourceMediaIntegrityAuthorities().ToArray() ?? [];
        return Array.AsReadOnly(authorities.Select(authority =>
            GetOrCreateSourceMediaIntegrityPolicy(authority.PluginOwnerId, authority.SourceId).GetView(authority.Maturity)).ToArray());
    }

    internal Task SetSourceMediaIntegrityPermissionAsync(string pluginOwnerId, string sourceId, bool allow, long expectedRevision)
        => RunInteractiveAsync(nameof(SetSourceMediaIntegrityPermissionAsync), pluginOwnerId + "/" + sourceId, () => SetSourceMediaIntegrityPermissionAdmittedCoreAsync(pluginOwnerId, sourceId, allow, expectedRevision));

    private async Task SetSourceMediaIntegrityPermissionAdmittedCoreAsync(string pluginOwnerId, string sourceId, bool allow, long expectedRevision)
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureReady();
            await SetSourceMediaIntegrityPermissionCoreAsync(pluginOwnerId, sourceId, allow, expectedRevision).ConfigureAwait(false);
        }
        finally { _commands.Release(); }
        SignalChanged();
    }

    // Caller already holds _commands. Keep the existing authority and persistence path.
    private Task SetSourceMediaIntegrityPermissionCoreAsync(string pluginOwnerId, string sourceId, bool allow, long expectedRevision)
    {
        if (allow && ExperimentalSourceActivationPolicy.IsStrictExperimentalSource(pluginOwnerId, sourceId))
            throw new InvalidOperationException("This experimental source retains strict provider-integrity checks.");
        SourceMediaIntegrityPolicyService policy = GetOrCreateSourceMediaIntegrityPolicy(pluginOwnerId, sourceId);
        return policy.SetPermissionAsync(allow, expectedRevision, permission =>
        {
            Config candidate = Config.Clone();
            candidate.SourceMediaIntegrityPermissions[SourceMediaIntegrityPermission.CreateKey(pluginOwnerId, sourceId)] = permission;
            candidate.Validate();
            candidate.SaveConfig(_profileLease.ProfileDirectory);
            PublishConfig(candidate);
        });
    }

    internal void RetireSourceMediaIntegrityPolicies()
    {
        SourceMediaIntegrityPolicyService[] policies;
        lock (_sourcePolicySync)
        {
            _sourceMediaPoliciesRetired = true;
            policies = _sourceMediaPolicies.Values.ToArray();
        }
        foreach (SourceMediaIntegrityPolicyService policy in policies)
            policy.Retire();
    }
}

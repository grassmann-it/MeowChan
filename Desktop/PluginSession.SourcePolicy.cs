using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.PluginHost.Sources;
using MeowChan.PluginHost.Configuration;
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

    // Uses the adapters' declared router only. Neither this read nor projection starts a worker.
    internal ExperimentalSourceActivationObservation ObserveExperimentalSourceActivation(Guid sessionId, string profileId,
        string url, PluginProfileSnapshot persisted, bool ready)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return new(null, null, ready);
        lock (_state)
        {
            PluginSourceImageBoard[] matches = Sources.Where(source => source.Router.TryResolve(uri, out _, out _, out _, out _)).ToArray();
            if (matches.Length == 0) return new(null, null, ready);
            if (matches.Length != 1) return new(null, null, ready, "The URL has conflicting source owners.");
            PluginSourceImageBoard source = matches[0];
            string owner = _sourceOwners[source];
            if (!ExperimentalSourceActivationPolicy.IsEligible(owner, source.Name)) return new(null, null, ready);
            var saved = persisted.Plugins.SingleOrDefault(plugin => plugin.PluginId == owner);
            SourceMediaIntegrityPolicyView? policy = (source.MediaIntegrityPolicy as SourceMediaIntegrityPolicyService)?.GetView(source.Description.Maturity);
            if (saved == null || policy == null) return new(null, null, ready, "The source owner or host policy is unavailable.");
            var actual = new ExperimentalSourceActivationActualState(saved.Enabled,
                IsSourceMediaIntegrityActive(source) && source.Enabled && policy.IsActive,
                policy.IsGranted, policy.Snapshot.AllowProviderSizeMd5Mismatch,
                persisted.Revision, saved.LifecycleRevision, policy.Snapshot.Revision);
            if (_disposed || !ready) return new(null, actual, false, "The desktop session is stopping.");
            if (persisted.SelectedSourceOwners.TryGetValue(source.Name, out string? selectedOwner) && selectedOwner != owner)
                return new(null, actual, ready, "The selected source owner changed. Restart or submit the URL again.");
            if (!_active.TryGetValue(owner, out var runtime) || runtime.Active == null || saved.Active == null
                || runtime.Active.CandidateId != saved.Active.CandidateId
                || runtime.Active.ExecutionRevision != saved.Active.ExecutionRevision
                || runtime.Active.CompilationFingerprint != saved.Active.CompilationFingerprint
                || runtime.Active.Artifact.AssemblySha256 != saved.Active.Artifact.AssemblySha256
                // A confirmed repair preserves its selected anchor but executes the validated target.
                // Compare to the startup-established runtime anchor, never trust saved.Active alone.
                || runtime.SelectedCandidateId != saved.SelectedCandidateId
                || runtime.Enabled != saved.Enabled || runtime.LifecycleRevision != saved.LifecycleRevision
                || actual.IsActive != saved.Enabled)
                return new(null, actual, ready, "The source has no matching selected active build or its lifecycle changed.");
            source.Router.TryResolve(uri, out _, out _, out _, out Uri? canonical);
            var target = new ExperimentalSourceActivationTarget(sessionId, profileId, owner, source.Name,
                source.NiceName, canonical!.AbsoluteUri, saved.Active.CandidateId, saved.Active.ExecutionRevision,
                saved.Active.CompilationFingerprint, saved.Active.Artifact.AssemblySha256, persisted.Revision,
                saved.LifecycleRevision, policy.Snapshot.Revision, saved.Enabled, policy.IsGranted,
                ExperimentalSourceActivationPolicy.RequestsMismatchPermission(owner, source.Name));
            return new(target, actual, ready);
        }
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

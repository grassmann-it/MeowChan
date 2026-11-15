using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Sources;
using MeowChan.PluginHost.Configuration;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop;

internal sealed record PluginSourcePolicyAuthority(string PluginOwnerId, string SourceId, SourceMaturity Maturity,
    bool IsActive, SourceWordingClassification? Classification = null);

internal sealed partial class PluginSession
{
    private readonly Func<string, string, SourceMediaIntegrityPolicyService>? _sourceMediaIntegrityPolicyFactory;

    internal IReadOnlyList<PluginSourcePolicyAuthority> GetSourceMediaIntegrityAuthorities()
    {
        lock (_state)
            return Array.AsReadOnly(Sources.Select(source =>
            {
                string owner = _sourceOwners[source];
                PluginReleaseSnapshot? active = _active.TryGetValue(owner, out PluginActivationSnapshot? activation)
                    ? activation.Active : null;
                SourceWordingClassification? classification = active is { } release &&
                    release.Description.Sources.Contains(source.Description)
                        ? SourceWordingEvidence.Classify(release.Description, source.Description, release.ExecutionRevision)
                        : null;
                return new PluginSourcePolicyAuthority(owner, source.Name, source.Description.Maturity,
                    IsSourceMediaIntegrityActive(source), classification);
            }).ToArray());
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
            // A family site needs its plugin and its own switch; any other source has only its plugin switch.
            bool? siteEnabled = ChanSiteEnabled(owner, source.Name);
            bool enabled = saved.Enabled && siteEnabled != false;
            var actual = new ExperimentalSourceActivationActualState(enabled,
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
                || actual.IsActive != enabled)
                return new(null, actual, ready, "The source has no matching selected active build or its lifecycle changed.");
            source.Router.TryResolve(uri, out _, out _, out _, out Uri? canonical);
            var target = new ExperimentalSourceActivationTarget(sessionId, profileId, owner, source.Name,
                source.NiceName, canonical!.AbsoluteUri, saved.Active.CandidateId, saved.Active.ExecutionRevision,
                saved.Active.CompilationFingerprint, saved.Active.Artifact.AssemblySha256, persisted.Revision,
                saved.LifecycleRevision, policy.Snapshot.Revision, enabled, policy.IsGranted,
                ExperimentalSourceActivationPolicy.RequestsMismatchPermission(owner, source.Name),
                siteEnabled is null ? null : saved.Enabled);
            return new(target, actual, ready);
        }
    }

    // Caller holds _state. Candidate/Pending/LastGood descriptions never confer authority.
    private bool IsSourceMediaIntegrityActive(PluginSourceImageBoard source) => !_disposed
        && _sourceOwners.TryGetValue(source, out string? owner)
        && _active.TryGetValue(owner, out var activation) && activation.Enabled && activation.Active != null
        && SiteSwitches.IsEnabled(owner, source.Name);

    /// <summary>
    /// Turns one site of an engine-family plugin on or off. The plugin's own enabled state stays the master switch:
    /// a site is used only while both are on. False for a source without a site switch or when it cannot be saved.
    /// </summary>
    internal bool SetChanSiteEnabled(string sourceId, bool enabled)
    {
        lock (_state)
        {
            if (_disposed) return false;
            PluginSourceImageBoard? source = Sources.FirstOrDefault(item => item.Name == sourceId);
            if (source == null || !_sourceOwners.TryGetValue(source, out string? owner) || !ChanSourceFamilies.IsFamily(owner)
                || !SiteSwitches.SetEnabled(owner, sourceId, enabled)) return false;
            bool active = IsSourceMediaIntegrityActive(source);
            // Invalidate before returning, as the plugin lifecycle callback does.
            SetSourceMediaIntegrityActive(source, active);
            source.SetEnabled(active);
            return true;
        }
    }

    /// <summary>The site's own switch, or null for a source that has none (a single-site or non-family plugin).</summary>
    internal bool? ChanSiteEnabled(string pluginOwnerId, string sourceId) =>
        ChanSourceFamilies.IsFamily(pluginOwnerId) ? SiteSwitches.IsEnabled(pluginOwnerId, sourceId) : null;

    private static void SetSourceMediaIntegrityActive(PluginSourceImageBoard source, bool active)
    {
        if (source.MediaIntegrityPolicy is SourceMediaIntegrityPolicyService policy) policy.SetActive(active);
    }
}

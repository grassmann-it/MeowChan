using System.Security.Cryptography;
using System.Text;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Compilation;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Configuration;

/// <summary>Host-observed delivered provenance, never inferred from a plugin's self-declared ID.
/// Slot identity is independent of install root and build; candidate/execution identities are not.</summary>
public sealed record DeliveredCandidateProvenance(int Version, string SlotId, string CandidateId,
    string PluginId, string ExecutionRevision, string CandidatePath, string ManifestSha256,
    string DescriptionSha256, string AssemblySha256, string SdkFingerprint, string CompilerFingerprint);

public sealed record BundledCandidateRepairPreview(long ProfileRevision, IReadOnlyList<BundledCandidateRepair> Rows);

/// <summary>Frozen preview. Confirmation rederives this row under the profile writer lease.</summary>
public sealed record BundledCandidateRepair(string PluginId, string OldCandidateId, string? NewCandidateId,
    string? SlotId, string? OldPath, string? NewPath, string? OldExecutionRevision,
    string? NewExecutionRevision, bool BuildChanged, string? BlockedReason)
{
    public bool CanConfirm => BlockedReason is null && NewCandidateId is not null;
    // Exact, deterministic review payload. Strings preserve frozen-row value equality
    // across rederivation, unlike mutable/list reference equality.
    public string? PermissionReviewJson { get; init; }
    public string? PermissionFingerprint { get; init; }
    // A separate declaration/settings decision. Ordinary repair remains fail-closed.
    public string? ChangeReviewJson { get; init; }
    public string? ChangeFingerprint { get; init; }
    public bool CanReviewChanges => BlockedReason is "description-changed" or "configuration-incompatible" &&
        NewCandidateId is not null && ChangeReviewJson is not null && ChangeFingerprint is not null;
    public bool CanReauthorize => BlockedReason == "inherited-grants" && NewCandidateId is not null &&
        PermissionReviewJson is not null && PermissionFingerprint is not null;
}

/// <summary>Existing special permissions, not requests for additional permissions.</summary>
public sealed record BundledRepairPermissions(IReadOnlyList<PluginGrantSnapshot> FilterGrants,
    IReadOnlyList<string> SourceIds);

internal static class BundledRepairPermissionReview
{
    internal static string Json(BundledProfileDocument document, PluginActivationSnapshot plugin,
        PluginCandidateSnapshot target) => Encoding.UTF8.GetString(SdkJson.Serialize(new BundledRepairPermissions(
            document.Profile.Grants.Where(g => g.PluginId == plugin.PluginId && g.Access != FilterDataAccess.None)
                .OrderBy(g => g.FilterKey, StringComparer.Ordinal).ThenBy(g => g.Scope.SourceId, StringComparer.Ordinal)
                .ThenBy(g => g.Scope.BoardId, StringComparer.Ordinal).ThenBy(g => g.Scope.RuleId, StringComparer.Ordinal).ToArray(),
            (target.Description?.Sources.Select(s => s.SourceId) ?? [])
                .Where(id => document.RestrictedSourceIds.Contains(id, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())));

    internal static string Fingerprint(string json) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
}

// Kept inside the versioned store payload, not injected into SDK/provider metadata.
internal sealed record BundledRepairBinding(string PluginId, string SelectedCandidateId,
    string OldExecutionRevision, string TargetCandidateId, string TargetExecutionRevision, string SlotId)
{
    // Confirmation can replace the only Pending snapshot before any successful activation.
    // Retain the exact proven OLD release, not merely NEW Pending as the comparison basis.
    public PluginReleaseSnapshot? OriginalRelease { get; init; }
    // Explicit UI consent is scoped to TargetCandidateId + TargetExecutionRevision
    // and the exact effective permissions (including filter-grant revisions).
    public string? ReauthorizedPermissionFingerprint { get; init; }
    // Optional on legacy bindings. Material declaration consent is exact, even if a provider
    // reports different metadata without changing its assembly/execution fingerprint.
    public string? ReviewedDescriptionFingerprint { get; init; }
}
internal sealed record PreReplacementBasis(PluginReleaseSnapshot? Release, string? CandidateId,
    string? ExecutionRevision, DeliveredCandidateProvenance? Provenance, bool HasHistory)
{
    internal bool Missing => HasHistory && ExecutionRevision is null;
}
// First SDK 1.1.0 executable ever activated for a delivered plugin. Active/LastGood lose it
// after an activation failure, so this is the only proof the one-time SDK bridge was used.
internal sealed record AcceptedSdkUpgrade(string PluginId, string ExecutionRevision);
internal sealed record BundledProfileDocument(PluginProfileSnapshot Profile,
    IReadOnlyList<DeliveredCandidateProvenance> Provenance, IReadOnlyList<BundledRepairBinding> Bindings,
    IReadOnlyList<string> RestrictedSourceIds)
{
    // Bind the actual first pre-selection envelope, without pretending it was v1.
    public string? ReselectionBackupSha256 { get; init; }
    public IReadOnlyList<AcceptedSdkUpgrade> AcceptedSdkUpgrades { get; init; } = [];
}

internal static class BundledCandidateMigration
{
    internal const int MaximumEvidence = 4096;
    internal static string DescriptionHash(PluginDescription description) =>
        Convert.ToHexStringLower(SHA256.HashData(SdkJson.Serialize(description)));

    internal static string Slot(string manifestName)
    {
        if (string.IsNullOrWhiteSpace(manifestName) || manifestName.Length > 240 ||
            manifestName.Contains('/') || manifestName.Contains('\\') || manifestName.Contains(':') ||
            !manifestName.EndsWith(".plugin.json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid delivered manifest slot.");
        // Case-equivalent filenames share a slot on every platform; duplicates are rejected.
        return "meowchan-delivered-v1:" + Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(manifestName.ToLowerInvariant())));
    }

    internal static bool Matches(DeliveredCandidateProvenance proof, PluginCandidateSnapshot candidate) =>
        candidate.Artifact is { } artifact && candidate.Description is { } description &&
        proof.CandidateId == candidate.CandidateId && proof.PluginId == candidate.PluginId &&
        proof.CandidatePath == candidate.CandidatePath && Matches(proof, artifact, description);

    internal static bool Matches(DeliveredCandidateProvenance proof, PluginArtifact artifact, PluginDescription description) =>
        proof.Version == 1 && proof.PluginId == description.PluginId && proof.ExecutionRevision == artifact.ExecutionRevision &&
        proof.AssemblySha256 == artifact.AssemblySha256 && proof.SdkFingerprint == artifact.SdkFingerprint &&
        proof.CompilerFingerprint == artifact.CompilerFingerprint && proof.DescriptionSha256 == DescriptionHash(description);

    internal static bool SameExecutableAndDescription(PluginReleaseSnapshot retained, PluginCandidateSnapshot target)
    {
        if (target.Artifact is not { } right || target.Description is not { } description ||
            target.PluginId != retained.Description.PluginId || description.PluginId != retained.Description.PluginId) return false;
        PluginArtifact left = retained.Artifact;
        // Physical assembly/cache roots may differ; executable identity and logical resource identities may not.
        return left.ExecutionRevision == right.ExecutionRevision && left.CompilationFingerprint == right.CompilationFingerprint &&
            left.AssemblySha256 == right.AssemblySha256 && left.SdkFingerprint == right.SdkFingerprint &&
            left.CompilerFingerprint == right.CompilerFingerprint && left.Resources is not null && right.Resources is not null &&
            left.Resources.OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Rid, StringComparer.Ordinal)
                .ThenBy(r => r.RelativePath, StringComparer.Ordinal).ThenBy(r => r.Sha256, StringComparer.Ordinal)
                .SequenceEqual(right.Resources.OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Rid, StringComparer.Ordinal)
                    .ThenBy(r => r.RelativePath, StringComparer.Ordinal).ThenBy(r => r.Sha256, StringComparer.Ordinal)) &&
            SdkJson.Serialize(retained.Description).AsSpan().SequenceEqual(SdkJson.Serialize(description));
    }

    internal static PluginReleaseSnapshot? IdenticalEstablishedLocalAlias(BundledProfileDocument document,
        PluginActivationSnapshot plugin, PluginCandidateSnapshot candidate, bool profileLocal)
    {
        if (!profileLocal || plugin.Pinned is not null || !candidate.IsPresent || candidate.Problem is not null ||
            document.Provenance.Any(p => p.CandidateId == candidate.CandidateId)) return null;
        PreReplacementBasis basis = TrustedPreReplacementBasis(document, plugin);
        PluginReleaseSnapshot? established = plugin.Active ?? plugin.LastGood;
        return !basis.Missing && basis.Release is not null && established is not null &&
            SameExecutableAndDescription(established, candidate) && SameExecutableAndDescription(basis.Release, candidate)
            ? basis.Release : null;
    }

    /// <summary>One pre-replacement identity decision shared by startup, ordinary writes and repair.
    /// Pending-only history is authority too; a confirmed NEW Pending is never its own OLD basis.</summary>
    internal static PreReplacementBasis TrustedPreReplacementBasis(BundledProfileDocument document, PluginActivationSnapshot plugin)
    {
        PreReplacementBasis Missing() => new(null, null, null, null, true);
        PreReplacementBasis FromRelease(PluginReleaseSnapshot release)
        {
            DeliveredCandidateProvenance[] proofs = document.Provenance.Where(p => p.CandidateId == release.CandidateId &&
                Matches(p, release.Artifact, release.Description)).ToArray();
            return proofs.Length > 1 ? Missing() : new(release, release.CandidateId, release.ExecutionRevision,
                proofs.SingleOrDefault(), true);
        }
        PluginReleaseSnapshot LatestSettings(PluginReleaseSnapshot prior) => plugin.Pending is { } pending &&
            pending.CandidateId == prior.CandidateId && pending.ExecutionRevision == prior.ExecutionRevision ? pending : prior;

        BundledRepairBinding? binding = document.Bindings.SingleOrDefault(b => b.PluginId == plugin.PluginId &&
            b.SelectedCandidateId == plugin.SelectedCandidateId);
        if (binding is not null)
        {
            // Earlier bindings did not preserve OLD when confirmation replaced sole Pending.
            // Do not infer that history from NEW Pending or normalize an ambiguous retained state.
            PluginReleaseSnapshot? original = binding.OriginalRelease;
            if (original is null) return Missing();
            PreReplacementBasis old = FromRelease(original);
            if (old.Missing || old.Provenance is null || old.Provenance.SlotId != binding.SlotId ||
                old.ExecutionRevision != binding.OldExecutionRevision || original.Description.PluginId != plugin.PluginId) return Missing();
            // Active/LastGood can attest that this exact target genuinely activated before a
            // later move/failure. Pending target/configuration alone cannot attest that transition.
            PluginReleaseSnapshot? established = plugin.Active ?? plugin.LastGood;
            if (established is not null && established.CandidateId == binding.TargetCandidateId &&
                established.ExecutionRevision == binding.TargetExecutionRevision)
            {
                PreReplacementBasis current = FromRelease(LatestSettings(established));
                return current.Provenance?.SlotId == binding.SlotId ? current : Missing();
            }
            return old;
        }

        PluginReleaseSnapshot? prior = plugin.Active ?? plugin.LastGood ?? plugin.Pending;
        if (prior is not null) return FromRelease(LatestSettings(prior));
        if (plugin.SelectedCandidateId is { } selected)
        {
            // Without a retained release, a refreshed same-path inventory cannot tell us which
            // of several proven executions owned the original selection/configuration authority.
            if (document.Provenance.Where(p => p.CandidateId == selected)
                .Select(p => p.ExecutionRevision).Distinct(StringComparer.Ordinal).Take(2).Count() > 1) return Missing();
            PluginCandidateSnapshot[] candidates = plugin.Candidates.Where(c => c.CandidateId == selected &&
                c.Artifact is not null && c.Description is not null).ToArray();
            if (candidates.Length != 1) return Missing();
            PluginCandidateSnapshot candidate = candidates[0];
            DeliveredCandidateProvenance[] proofs = document.Provenance.Where(p => Matches(p, candidate)).ToArray();
            // Known delivered identity cannot borrow authority from an unproven inventory refresh.
            // Local-only candidates have no delivered history and retain their ordinary settings path.
            if (document.Provenance.Any(p => p.CandidateId == selected) && proofs.Length != 1) return Missing();
            return proofs.Length > 1 ? Missing() : new(null, candidate.CandidateId, candidate.Artifact!.ExecutionRevision,
                proofs.SingleOrDefault(), true);
        }
        bool rightsWithoutBasis = document.Profile.Grants.Any(g => g.PluginId == plugin.PluginId && g.Access != FilterDataAccess.None) ||
            plugin.Candidates.Any(c => c.Description?.Sources.Any(source => document.RestrictedSourceIds.Contains(source.SourceId, StringComparer.Ordinal)) == true);
        return rightsWithoutBasis ? Missing() : new(null, null, null, null, false);
    }

    internal static BundledCandidateRepairPreview Preview(BundledProfileDocument document)
    {
        List<BundledCandidateRepair> rows = new();
        foreach (PluginActivationSnapshot plugin in document.Profile.Plugins)
        {
            if (plugin.SelectedCandidateId is not { } selected) continue;
            PreReplacementBasis previous = TrustedPreReplacementBasis(document, plugin);
            PluginReleaseSnapshot? basis = previous.Release;
            PluginCandidateSnapshot? selectedCandidate = plugin.Candidates.FirstOrDefault(c => c.CandidateId == selected);
            // Normal available candidates need no repair. A pending confirmed binding is already durable.
            if (!previous.Missing && plugin.Active is not null && selectedCandidate is { IsPresent: true, Problem: null, Artifact: not null } &&
                plugin.Active.ExecutionRevision == selectedCandidate.Artifact.ExecutionRevision) continue;
            if (!previous.Missing && document.Bindings.Any(b => b.PluginId == plugin.PluginId && b.SelectedCandidateId == selected &&
                plugin.Pending?.CandidateId == b.TargetCandidateId && plugin.Pending.ExecutionRevision == b.TargetExecutionRevision &&
                plugin.Candidates.Any(c => c.CandidateId == b.TargetCandidateId && c.IsPresent && c.Problem is null &&
                    c.Artifact?.ExecutionRevision == b.TargetExecutionRevision &&
                    (b.ReviewedDescriptionFingerprint is null || c.Description is not null &&
                        DescriptionHash(c.Description) == b.ReviewedDescriptionFingerprint) &&
                    !HasUnreviewedInheritedRights(document, plugin, c, previous.ExecutionRevision != c.Artifact.ExecutionRevision)))) continue;
            if (!previous.Missing && plugin.Active is not null && document.Bindings.Any(b => b.PluginId == plugin.PluginId &&
                b.SelectedCandidateId == selected && b.TargetCandidateId == plugin.Active.CandidateId &&
                b.TargetExecutionRevision == plugin.Active.ExecutionRevision && plugin.Candidates.Any(c =>
                    c.CandidateId == b.TargetCandidateId && c.IsPresent && c.Problem is null && c.Artifact?.ExecutionRevision == b.TargetExecutionRevision))) continue;

            DeliveredCandidateProvenance? old = previous.Provenance;
            // An explicit local selection does not acquire its former release's delivered role.
            if (!document.Provenance.Any(p => p.CandidateId == selected) &&
                !document.Bindings.Any(b => b.PluginId == plugin.PluginId && b.SelectedCandidateId == selected)) old = null;
            PluginCandidateSnapshot[] present = plugin.Candidates.Where(c => c.IsPresent).ToArray();
            PluginCandidateSnapshot[] targets = old is null ? [] : present.Where(c => c.Problem is null &&
                document.Provenance.Any(p => p.SlotId == old.SlotId && Matches(p, c))).ToArray();
            PluginCandidateSnapshot? target = targets.Length == 1 ? targets[0] : null;
            bool changed = target?.Artifact is { } artifact && (previous.Missing || previous.ExecutionRevision != artifact.ExecutionRevision);
            string? reason = plugin.Pinned is not null ? "pinned" :
                previous.Missing || basis is null || old is null ? "unproven-history" :
                plugin.Pending is { } pendingChoice && (pendingChoice.CandidateId != basis.CandidateId || pendingChoice.ExecutionRevision != basis.ExecutionRevision) &&
                    !document.Bindings.Any(b => b.PluginId == plugin.PluginId && b.TargetCandidateId == pendingChoice.CandidateId &&
                        b.TargetExecutionRevision == pendingChoice.ExecutionRevision) ? "pending-choice" :
                present.Length > 1 || targets.Length > 1 || document.Profile.Plugins.SelectMany(p => p.Candidates)
                    .Count(c => c.IsPresent && document.Provenance.Any(p => p.SlotId == old.SlotId && Matches(p, c))) > 1 ? "ambiguous" :
                target is null ? "no-proven-target" :
                !SameReviewedDescription(basis.Description, target.Description!) ? "description-changed" :
                HasOwnerConflict(document.Profile, plugin.PluginId, target.Description!) ? "source-owner-conflict" :
                SdkValidation.ValidateConfiguration(target.Description!, basis.Configuration).Count != 0 ? "configuration-incompatible" :
                HasInheritedRights(document, plugin, target, changed) ? "inherited-grants" : null;
            string? permissions = reason == "inherited-grants"
                ? BundledRepairPermissionReview.Json(document, plugin, target!) : null;
            string? changeReview = null;
            if (reason is "description-changed" or "configuration-incompatible" &&
                basis is not null && target?.Description is { } targetDescription &&
                !HasOwnerConflict(document.Profile, plugin.PluginId, targetDescription) &&
                basis.Description.Sources.Select(s => s.SourceId).Order(StringComparer.Ordinal).SequenceEqual(
                    targetDescription.Sources.Select(s => s.SourceId).Order(StringComparer.Ordinal)))
            {
                // The exact retained settings and all existing rights are frozen with the declarations.
                // A changed source identity is not a settings repair and cannot acquire an owner here.
                var rights = SdkJson.Deserialize<BundledRepairPermissions>(Encoding.UTF8.GetBytes(
                    BundledRepairPermissionReview.Json(document, plugin, target)));
                changeReview = Encoding.UTF8.GetString(SdkJson.Serialize(new BundledPluginChangeReview(
                    basis.Description, targetDescription, basis.Configuration, rights)));
            }
            rows.Add(new(plugin.PluginId, selected, target?.CandidateId, old?.SlotId,
                old?.CandidatePath ?? selectedCandidate?.CandidatePath, target?.CandidatePath,
                previous.ExecutionRevision, target?.Artifact?.ExecutionRevision, changed, reason)
            {
                PermissionReviewJson = permissions,
                PermissionFingerprint = permissions is null ? null : BundledRepairPermissionReview.Fingerprint(permissions),
                ChangeReviewJson = changeReview,
                ChangeFingerprint = changeReview is null ? null : BundledRepairPermissionReview.Fingerprint(changeReview)
            });
        }
        return new(document.Profile.Revision, rows);
    }

    // A normal in-place app/development rebuild is not a change of plugin owner.
    // Only current host-observed delivery evidence may authorize this exception;
    // persisted inventory alone, local plugins and unproven pending history cannot.
    internal static bool CanAutomaticallyUpdateKnownBundle(BundledProfileDocument document,
        PluginActivationSnapshot plugin, PluginCandidateSnapshot? candidate, PreReplacementBasis previous,
        IReadOnlyList<DeliveredCandidateProvenance>? currentDelivery, int presentCount)
    {
        if (currentDelivery is null || presentCount != 1 || plugin.Pinned is not null
            || candidate is not { IsPresent: true, Problem: null, Artifact: not null, Description: not null }
            || previous.Missing || previous.Release is null || previous.Provenance is null
            || previous.CandidateId != candidate.CandidateId) return false;
        PluginReleaseSnapshot? established = plugin.Active ?? plugin.LastGood;
        if (established is not null)
        {
            if (established.CandidateId != previous.CandidateId
                || established.ExecutionRevision != previous.ExecutionRevision) return false;
        }
        else
        {
            // Explicit new selection/configuration can be complete and trusted
            // before its first activation. Its retained OLD Pending proof—not
            // today's inventory or a global backup marker—must supply the basis.
            bool provenPendingBeforeFirstActivation = plugin.Pending is { } pendingBeforeFirst
                && plugin.SelectedCandidateId == pendingBeforeFirst.CandidateId
                && pendingBeforeFirst.CandidateId == previous.CandidateId
                && pendingBeforeFirst.ExecutionRevision == previous.ExecutionRevision
                && !document.Bindings.Any(binding => binding.PluginId == plugin.PluginId);
            if (!provenPendingBeforeFirstActivation) return false;
        }
        // Retain independent pending choices. A prior explicit SAME-IDENTITY repair
        // must not trap an established in-place source in a rebuild/confirm loop.
        if (plugin.Pending is { } pending && (pending.CandidateId != previous.CandidateId
            || pending.ExecutionRevision != previous.ExecutionRevision))
        {
            bool approvedInPlacePending = pending.CandidateId == previous.CandidateId
                && document.Bindings.Any(binding => binding.PluginId == plugin.PluginId
                    && binding.SelectedCandidateId == plugin.SelectedCandidateId
                    && binding.TargetCandidateId == previous.CandidateId
                    && binding.TargetExecutionRevision == pending.ExecutionRevision
                    && binding.SlotId == previous.Provenance.SlotId)
                && SdkJson.Serialize(pending.Description).AsSpan().SequenceEqual(SdkJson.Serialize(candidate.Description));
            if (!approvedInPlacePending) return false;
        }
        bool selectedIdentity = plugin.SelectedCandidateId == candidate.CandidateId
            || document.Bindings.Any(binding => binding.PluginId == plugin.PluginId
                && binding.SelectedCandidateId == plugin.SelectedCandidateId
                && binding.TargetCandidateId == candidate.CandidateId && binding.SlotId == previous.Provenance.SlotId);
        if (!selectedIdentity || HasInheritedRights(document, plugin, candidate, changed: true)
            || HasOwnerConflict(document.Profile, plugin.PluginId, candidate.Description)
            || !SdkJson.Serialize(previous.Release.Description).AsSpan().SequenceEqual(SdkJson.Serialize(candidate.Description))
            || SdkValidation.ValidateConfiguration(candidate.Description, plugin.Pending?.Configuration ?? previous.Release.Configuration).Count != 0) return false;
        DeliveredCandidateProvenance[] currentSlot = currentDelivery
            .Where(proof => proof.SlotId == previous.Provenance.SlotId).ToArray();
        return currentSlot.Length == 1 && Matches(currentSlot[0], candidate);
    }

    // This is not a general executable equivalence or permission reauthorization. The
    // only supported declaration transition is the shipped SDK 1.0 -> 1.1 change.
    internal static bool CanAutomaticallyUpgradeSdk(BundledProfileDocument document,
        PluginActivationSnapshot plugin, PluginCandidateSnapshot? candidate, PreReplacementBasis previous,
        IReadOnlyList<DeliveredCandidateProvenance>? currentDelivery, int presentCount)
    {
        // One bridge per plugin: once any 1.1.0 build was activated, a failed one leaves LastGood
        // 1.0.0 behind, which must not authorize a further rebuild to inherit rights.
        if (currentDelivery is null || presentCount != 1 || plugin.Pinned is not null ||
            document.AcceptedSdkUpgrades.Any(u => u.PluginId == plugin.PluginId) ||
            candidate is not { IsPresent: true, Problem: null, Artifact: not null, Description: not null } ||
            previous.Missing || previous.Release is null || previous.Provenance is null ||
            previous.CandidateId != candidate.CandidateId ||
            previous.Provenance.CandidatePath != candidate.CandidatePath) return false;

        PluginReleaseSnapshot? established = plugin.Active ?? plugin.LastGood;
        if (established is null || established.CandidateId != previous.CandidateId ||
            established.ExecutionRevision != previous.ExecutionRevision ||
            established.Description.SdkVersion != "1.0.0" ||
            previous.Release.Description.SdkVersion != "1.0.0" || candidate.Description.SdkVersion != "1.1.0" ||
            !SdkJson.Serialize(established.Description).AsSpan().SequenceEqual(SdkJson.Serialize(previous.Release.Description)) ||
            !IsSdkOnlyUpgrade(previous.Release.Description, candidate.Description)) return false;

        // Unlike the ordinary no-rights rebuild path, an unactivated confirmed target
        // or an independent pending choice cannot establish compatibility authority.
        if (plugin.Pending is { } pending && (pending.CandidateId != previous.CandidateId ||
            pending.ExecutionRevision != previous.ExecutionRevision)) return false;
        BundledRepairBinding? binding = document.Bindings.SingleOrDefault(b => b.PluginId == plugin.PluginId);
        if (binding is null)
        {
            if (plugin.SelectedCandidateId != candidate.CandidateId) return false;
        }
        else if (binding.SelectedCandidateId != plugin.SelectedCandidateId || binding.SlotId != previous.Provenance.SlotId ||
            binding.TargetCandidateId != established.CandidateId || binding.TargetExecutionRevision != established.ExecutionRevision ||
            binding.ReauthorizedPermissionFingerprint is { } fingerprint && fingerprint !=
                BundledRepairPermissionReview.Fingerprint(BundledRepairPermissionReview.Json(document, plugin, candidate))) return false;

        // Validate exactly the configuration startup activates.
        if (HasOwnerConflict(document.Profile, plugin.PluginId, candidate.Description) ||
            SdkValidation.ValidateConfiguration(candidate.Description, plugin.Pending?.Configuration ?? previous.Release.Configuration).Count != 0) return false;
        DeliveredCandidateProvenance[] currentSlot = currentDelivery.Where(p => p.SlotId == previous.Provenance.SlotId).ToArray();
        return currentSlot.Length == 1 && Matches(currentSlot[0], candidate);
    }

    // The only exempt declaration change: the shipped SDK 1.0.0 -> 1.1.0 field, every other
    // byte equal. Reverse, unknown or combined changes remain a description change.
    internal static bool IsSdkOnlyUpgrade(PluginDescription previous, PluginDescription candidate) =>
        previous.SdkVersion == "1.0.0" && candidate.SdkVersion == "1.1.0" &&
        SdkJson.Serialize(previous with { SdkVersion = "1.1.0" }).AsSpan().SequenceEqual(SdkJson.Serialize(candidate));

    // Shared by the repair preview and every confirmation that rederives it, so an SDK-only
    // build reaches the ordinary owner/configuration/grant review instead of a dead end.
    internal static bool SameReviewedDescription(PluginDescription previous, PluginDescription candidate) =>
        SdkJson.Serialize(previous).AsSpan().SequenceEqual(SdkJson.Serialize(candidate)) || IsSdkOnlyUpgrade(previous, candidate);

    internal static bool HasInheritedRights(BundledProfileDocument document, PluginActivationSnapshot plugin,
        PluginCandidateSnapshot target, bool changed) => changed &&
        (document.Profile.Grants.Any(g => g.PluginId == plugin.PluginId && g.Access != FilterDataAccess.None) ||
         (target.Description?.Sources.Any(s => document.RestrictedSourceIds.Contains(s.SourceId, StringComparer.Ordinal)) ?? false));

    internal static bool HasUnreviewedInheritedRights(BundledProfileDocument document, PluginActivationSnapshot plugin,
        PluginCandidateSnapshot target, bool changed)
    {
        if (!HasInheritedRights(document, plugin, target, changed)) return false;
        BundledRepairBinding? binding = document.Bindings.SingleOrDefault(b => b.PluginId == plugin.PluginId &&
            b.SelectedCandidateId == plugin.SelectedCandidateId && b.TargetCandidateId == target.CandidateId &&
            b.TargetExecutionRevision == target.Artifact?.ExecutionRevision);
        return binding?.ReauthorizedPermissionFingerprint is not { } fingerprint ||
            fingerprint != BundledRepairPermissionReview.Fingerprint(BundledRepairPermissionReview.Json(document, plugin, target));
    }

    internal static bool HasOwnerConflict(PluginProfileSnapshot state, string pluginId, PluginDescription description) =>
        description.Sources.Any(s => state.SelectedSourceOwners.TryGetValue(s.SourceId, out string? owner) && owner != pluginId ||
            state.Plugins.Any(p => p.PluginId != pluginId &&
                (p.Active?.Description.Sources.Any(other => other.SourceId == s.SourceId) == true ||
                 p.Candidates.Any(c => c.IsPresent && c.Description?.Sources.Any(other => other.SourceId == s.SourceId) == true))));

    internal static void Validate(BundledProfileDocument document)
    {
        if (document.ReselectionBackupSha256 is { } backupHash && !CompilerWire.IsHash(backupHash))
            throw new ArgumentException("Invalid new-selection backup binding.");
        if (document.Provenance is null || document.Bindings is null || document.RestrictedSourceIds is null ||
            document.AcceptedSdkUpgrades is null || document.AcceptedSdkUpgrades.Count > MaximumEvidence ||
            document.Provenance.Count > MaximumEvidence || document.Bindings.Count > MaximumEvidence || document.RestrictedSourceIds.Count > 4096)
            throw new ArgumentException("Invalid bounded delivered provenance collections.");
        if (document.AcceptedSdkUpgrades.GroupBy(u => u?.PluginId).Any(g => g.Count() != 1) ||
            document.AcceptedSdkUpgrades.Any(u => u is null || string.IsNullOrWhiteSpace(u.PluginId) || u.PluginId.Length > 512 ||
                string.IsNullOrWhiteSpace(u.ExecutionRevision) || u.ExecutionRevision.Length > 512))
            throw new ArgumentException("Invalid accepted SDK upgrade history.");
        if (document.Provenance.GroupBy(p => (p.CandidateId, p.ExecutionRevision)).Any(g => g.Count() != 1) ||
            document.Bindings.GroupBy(b => b.PluginId).Any(g => g.Count() != 1))
            throw new ArgumentException("Duplicate delivered provenance or repair binding.");
        foreach (DeliveredCandidateProvenance p in document.Provenance)
        {
            if (p.Version != 1 || string.IsNullOrWhiteSpace(p.CandidateId) || p.CandidateId.Length > 512 ||
                string.IsNullOrWhiteSpace(p.PluginId) || p.PluginId.Length > 512 || string.IsNullOrWhiteSpace(p.ExecutionRevision) ||
                p.ExecutionRevision.Length > 512 || string.IsNullOrWhiteSpace(p.CandidatePath) || !Path.IsPathFullyQualified(p.CandidatePath) ||
                p.SlotId != Slot(Path.GetFileName(p.CandidatePath)) || !CompilerWire.IsHash(p.ManifestSha256) ||
                !CompilerWire.IsHash(p.DescriptionSha256) || !CompilerWire.IsHash(p.AssemblySha256) ||
                !CompilerWire.IsHash(p.SdkFingerprint) || !CompilerWire.IsHash(p.CompilerFingerprint))
                throw new ArgumentException("Invalid delivered provenance.");
        }
        foreach (BundledRepairBinding b in document.Bindings)
            if (!document.Profile.Plugins.Any(p => p.PluginId == b.PluginId) ||
                !document.Provenance.Any(p => p.PluginId == b.PluginId && p.SlotId == b.SlotId &&
                    p.CandidateId == b.TargetCandidateId && p.ExecutionRevision == b.TargetExecutionRevision) ||
                !document.Provenance.Any(p => p.PluginId == b.PluginId && p.SlotId == b.SlotId && p.ExecutionRevision == b.OldExecutionRevision) ||
                string.IsNullOrWhiteSpace(b.SelectedCandidateId) ||
                b.ReauthorizedPermissionFingerprint is { } fingerprint && !CompilerWire.IsHash(fingerprint) ||
                b.ReviewedDescriptionFingerprint is { } descriptionFingerprint && !CompilerWire.IsHash(descriptionFingerprint))
                throw new ArgumentException("Unproven delivered repair binding.");
        foreach (BundledRepairBinding binding in document.Bindings.Where(b => b.OriginalRelease is not null))
        {
            PluginReleaseSnapshot original = binding.OriginalRelease!;
            if (original.ExecutionRevision != binding.OldExecutionRevision || original.Description.PluginId != binding.PluginId ||
                document.Provenance.Count(p => p.CandidateId == original.CandidateId && p.SlotId == binding.SlotId &&
                    Matches(p, original.Artifact, original.Description)) != 1 ||
                SdkValidation.ValidateConfiguration(original.Description, original.Configuration).Count != 0)
                throw new ArgumentException("Invalid immutable pre-replacement release.");
        }
        if (document.RestrictedSourceIds.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 512))
            throw new ArgumentException("Invalid source permission identity.");
    }
}

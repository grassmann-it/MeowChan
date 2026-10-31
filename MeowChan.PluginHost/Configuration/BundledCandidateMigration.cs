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
}

// Kept inside the versioned store payload, not injected into SDK/provider metadata.
internal sealed record BundledRepairBinding(string PluginId, string SelectedCandidateId,
    string OldExecutionRevision, string TargetCandidateId, string TargetExecutionRevision, string SlotId)
{
    // Confirmation can replace the only Pending snapshot before any successful activation.
    // Retain the exact proven OLD release, not merely NEW Pending as the comparison basis.
    public PluginReleaseSnapshot? OriginalRelease { get; init; }
}
internal sealed record PreReplacementBasis(PluginReleaseSnapshot? Release, string? CandidateId,
    string? ExecutionRevision, DeliveredCandidateProvenance? Provenance, bool HasHistory)
{
    internal bool Missing => HasHistory && ExecutionRevision is null;
}
internal sealed record BundledProfileDocument(PluginProfileSnapshot Profile,
    IReadOnlyList<DeliveredCandidateProvenance> Provenance, IReadOnlyList<BundledRepairBinding> Bindings,
    IReadOnlyList<string> RestrictedSourceIds)
{
    // Bind the actual first pre-selection envelope, without pretending it was v1.
    public string? ReselectionBackupSha256 { get; init; }
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
                    !HasInheritedRights(document, plugin, c, previous.ExecutionRevision != c.Artifact.ExecutionRevision)))) continue;
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
                !SdkJson.Serialize(basis.Description).AsSpan().SequenceEqual(SdkJson.Serialize(target.Description!)) ? "description-changed" :
                HasInheritedRights(document, plugin, target, changed) ? "inherited-grants" :
                HasOwnerConflict(document.Profile, plugin.PluginId, target.Description!) ? "source-owner-conflict" :
                SdkValidation.ValidateConfiguration(target.Description!, basis.Configuration).Count != 0 ? "configuration-incompatible" : null;
            rows.Add(new(plugin.PluginId, selected, target?.CandidateId, old?.SlotId,
                old?.CandidatePath ?? selectedCandidate?.CandidatePath, target?.CandidatePath,
                previous.ExecutionRevision, target?.Artifact?.ExecutionRevision, changed, reason));
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

    internal static bool HasInheritedRights(BundledProfileDocument document, PluginActivationSnapshot plugin,
        PluginCandidateSnapshot target, bool changed) => changed &&
        (document.Profile.Grants.Any(g => g.PluginId == plugin.PluginId && g.Access != FilterDataAccess.None) ||
         (target.Description?.Sources.Any(s => document.RestrictedSourceIds.Contains(s.SourceId, StringComparer.Ordinal)) ?? false));

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
            document.Provenance.Count > MaximumEvidence || document.Bindings.Count > MaximumEvidence || document.RestrictedSourceIds.Count > 4096)
            throw new ArgumentException("Invalid bounded delivered provenance collections.");
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
                string.IsNullOrWhiteSpace(b.SelectedCandidateId))
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

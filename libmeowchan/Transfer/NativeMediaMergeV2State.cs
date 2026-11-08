using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using MeowChan.Storage;

namespace MeowChan.Transfer;

// Frozen semantic evidence of the existing owner-issued merge plan. It is not an
// execution capability: only the owner's CWT plus live Exclusive admits mutations.
internal sealed class NativeMediaMergeV2State
{
    internal NativeMediaMergeV2State(NativeMediaMaterializationPlan materialization,
        NativeMediaCandidateSelection safeSelection, NativeMediaPersistedIdentity identity,
        ImmutableArray<NativeMediaMergeRoot> roots, NativeMediaMergeCounts counts)
    {
        if (identity.PlanVersion != 2 || materialization.Resource != identity.Resource
            || materialization.Package.Envelope.Fingerprint != identity.PackageFingerprint
            || materialization.FullSelection.IsEmpty || roots.IsDefault)
            throw new InvalidDataException("Invalid versioned merge identity.");
        materialization.ValidateSelection(safeSelection);
        Materialization = materialization; SafeSelection = safeSelection; Identity = identity;
        Roots = roots; RootsJson = JsonSerializer.Serialize(roots); Counts = counts;
        FullInventory = FullSelection.CreateInventory(identity.OperationId, identity.PackageFingerprint, identity.ImportedAtUtc);
        SafeInventory = safeSelection.IsEmpty ? null
            : safeSelection.CreateInventory(identity.OperationId, identity.PackageFingerprint, identity.ImportedAtUtc);
        CatalogBytes = materialization.Package.Envelope.CatalogBytes;
        if (counts.NewPosts < 0 || counts.UpdatedPosts < 0 || counts.NewAttachments < 0
            || counts.NewBindings < 0 || counts.Conflicts < 0 || counts.EvidenceAdditions < 0
            || counts.DerivedDescriptorAdditions < 0 || counts.ContainedDerivedAdditions < 0
            || counts.ContainedDerivedAdditions > counts.DerivedDescriptorAdditions
            || counts.NewBindings > (SafeInventory?.Bindings.Length ?? 0)
            || counts.EvidenceAdditions > (SafeInventory?.OriginalEvidence.Length ?? 0)
            || counts.DerivedDescriptorAdditions > (SafeInventory?.DerivedRepresentations.Length ?? 0)
            || counts.ContainedDerivedAdditions > (SafeInventory?.DerivedRepresentations.Count(
                d => d.IncomingStatus == MediaDerivedStatus.Contained) ?? 0))
            throw new InvalidDataException("Invalid versioned merge counters.");
    }

    internal NativeMediaMaterializationPlan Materialization { get; }
    internal NativeMediaPersistedIdentity Identity { get; }
    internal ImmutableArray<NativeMediaMergeRoot> Roots { get; }
    internal string RootsJson { get; }
    internal NativeMediaMergeCounts Counts { get; }
    internal ImmutableArray<byte> CatalogBytes { get; }
    internal NativeMediaCandidateSelection FullSelection => Materialization.FullSelection;
    internal NativeMediaCandidateSelection SafeSelection { get; }
    internal NativeMediaCandidateInventory FullInventory { get; }
    internal NativeMediaCandidateInventory? SafeInventory { get; }
    internal bool ReuseFullAsSafe => Counts.Conflicts == 0 && FullSelection.CanonicalJson == SafeSelection.CanonicalJson;

    internal bool MatchesOperation(NativeMediaPersistedMergeOperation operation, NativeMediaImportOperationState phase)
    {
        if (operation.Identity != Identity || operation.State != phase || operation.Counts != Counts
            || operation.RootsJson != RootsJson || !operation.Roots.SequenceEqual(Roots)
            || !operation.CatalogBytes.AsSpan().SequenceEqual(CatalogBytes.AsSpan())) return false;
        return MatchesRoot(operation.Full, FullSelection, FullInventory, phase)
            && MatchesRoot(operation.Safe, SafeSelection, SafeInventory, phase);
    }

    private static bool MatchesRoot(NativeMediaPersistedRoot root, NativeMediaCandidateSelection selection,
        NativeMediaCandidateInventory? inventory, NativeMediaImportOperationState phase)
    {
        if (root.LegacyKeysJson != "[]" || !root.LegacyKeys.IsEmpty || root.LegacyBindingsSha256 != null)
            return false;
        if (selection.IsEmpty)
            return root.Selection == null && root.SelectionSha256 == null && root.MemberCount == 0
                && root.InventorySha256 == null && root.Directory == null && root.ManifestGeneration == null;
        if (root.Selection?.CanonicalJson != selection.CanonicalJson || root.SelectionSha256 != selection.Sha256
            || root.MemberCount != selection.Members.Length) return false;
        return phase == NativeMediaImportOperationState.Preparing
            ? root.Directory == null && root.ManifestGeneration == null && root.InventorySha256 == null
            : root.Directory != null && root.ManifestGeneration is { Length: 64 } && root.InventorySha256 == inventory!.Sha256;
    }

    internal bool MatchesCandidate(PassiveMediaImportCandidate candidate, bool safe)
    {
        NativeMediaCandidateSelection selection = safe ? SafeSelection : FullSelection;
        NativeMediaCandidateInventory? inventory = safe ? SafeInventory : FullInventory;
        return inventory != null && !selection.IsEmpty && candidate.PlanVersion == 2 && !candidate.IsLegacyCandidate
            && candidate.OperationId == Identity.OperationId && candidate.Resource == Identity.Resource
            && candidate.PackageFingerprint == Identity.PackageFingerprint && candidate.ImportedAtUtc == Identity.ImportedAtUtc
            && candidate.Selection?.CanonicalJson == selection.CanonicalJson && candidate.Selection.Sha256 == selection.Sha256
            && candidate.InventorySha256 == inventory.Sha256
            && candidate.OriginalEvidence.SequenceEqual(inventory.OriginalEvidence)
            && candidate.DerivedRepresentations.SequenceEqual(inventory.DerivedRepresentations)
            && candidate.Bindings.SequenceEqual(inventory.Bindings);
    }

    internal NativeMediaMergeResult CommittedResult(bool replay = false)
        => new(NativeMediaImportCommitStatus.Committed, Counts.NewPosts, Counts.UpdatedPosts, Counts.NewAttachments,
            Counts.NewBindings, Counts.Conflicts, IsReplay: replay, EvidenceAdditions: Counts.EvidenceAdditions,
            DerivedDescriptorAdditions: Counts.DerivedDescriptorAdditions, ContainedDerivedAdditions: Counts.ContainedDerivedAdditions);
}

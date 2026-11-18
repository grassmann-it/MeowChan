using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Installation;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Configuration;

public sealed partial class PluginProfileStore
{
    /// <summary>Explicit declaration/settings consent; never live activation or an implicit rights transfer.</summary>
    public async Task<PluginProfileSnapshot> ConfirmBundledChangeAsync(BundledCandidateRepair repair,
        PluginConfiguration configuration, bool acknowledgeChanges, bool authorizePermissions,
        long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repair);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!acknowledgeChanges) throw new InvalidOperationException("Review and acknowledge the changed plugin before saving.");
        // Own the caller's mutable collections before the first asynchronous boundary.
        configuration = Clone(configuration);
        if (repairRestrictedSources is not null && repairPermissionGate is null)
            throw new InvalidOperationException("The source-permission authority is not serialized for declaration review.");
        if (repairPermissionGate is not null) await repairPermissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await MutateDocumentAsync(expectedRevision, async document =>
            {
                document = WithCurrentRepairRestrictions(document);
                BundledCandidateRepair? Current() => BundledCandidateMigration.Preview(document).Rows
                    .SingleOrDefault(row => row.PluginId == repair.PluginId && row.OldCandidateId == repair.OldCandidateId);
                BundledCandidateRepair? current = Current();
                if (current is null || current != repair || !current.CanReviewChanges)
                    throw new InvalidOperationException("The bundled declaration review changed or is blocked. Review it again.");
                PluginActivationSnapshot plugin = Find(document.Profile, current.PluginId);
                PluginCandidateSnapshot candidate = AvailableCandidate(plugin, current.NewCandidateId!);
                DeliveredCandidateProvenance proof = document.Provenance.Single(p => p.SlotId == current.SlotId &&
                    BundledCandidateMigration.Matches(p, candidate));
                bool rights = BundledCandidateMigration.HasInheritedRights(document, plugin, candidate, changed: true);
                if (rights && !authorizePermissions)
                    throw new InvalidOperationException("Explicit approval of the displayed existing permissions is required.");
                ThrowValidation(SdkValidation.ValidateConfiguration(candidate.Description!, configuration));
                await PluginInstallationInventory.VerifyRepairTargetAsync(proof, candidate.Artifact!, candidate.Description!, cancellationToken)
                    .ConfigureAwait(false);
                document = WithCurrentRepairRestrictions(document);
                if (Current() != current)
                    throw new InvalidOperationException("The declarations or permissions changed during verification. Review them again.");
                PluginReleaseSnapshot basis = BundledCandidateMigration.TrustedPreReplacementBasis(document, plugin).Release
                    ?? throw new InvalidOperationException("No trusted pre-replacement configuration is retained.");
                string? permissionFingerprint = rights ? BundledRepairPermissionReview.Fingerprint(
                    BundledRepairPermissionReview.Json(document, plugin, candidate)) : null;
                BundledRepairBinding binding = new(plugin.PluginId, plugin.SelectedCandidateId!, basis.ExecutionRevision,
                    candidate.CandidateId, candidate.Artifact!.ExecutionRevision, proof.SlotId)
                {
                    OriginalRelease = Clone(basis),
                    ReauthorizedPermissionFingerprint = permissionFingerprint,
                    ReviewedDescriptionFingerprint = BundledCandidateMigration.DescriptionHash(candidate.Description!)
                };
                // Only this exact, validated configuration is pending. The old release, enabled state,
                // ownership, pins and grants remain unchanged; startup rechecks identity and rights.
                return document with
                {
                    Profile = Replace(document.Profile, plugin with
                    {
                        Pending = new(candidate.CandidateId, candidate.Artifact!, candidate.Description!, configuration),
                        AutomaticSdkTransition = null,
                        Problem = "Changed plugin and settings explicitly reviewed for next start; original state retained."
                    }),
                    Bindings = document.Bindings.Where(b => b.PluginId != plugin.PluginId).Append(binding).ToArray()
                };
            }, cancellationToken, preserveReselectionBackup: true).ConfigureAwait(false);
        }
        finally { repairPermissionGate?.Release(); }
    }
}

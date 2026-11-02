using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Installation;

namespace MeowChan.PluginHost.Configuration;

public sealed partial class PluginProfileStore
{
    /// <summary>
    /// Separate consent operation: ordinary repair and automatic startup must never
    /// implicitly authorize an executable to use its predecessor's special rights.
    /// </summary>
    public async Task<PluginProfileSnapshot> ReauthorizeBundledRepairAsync(BundledCandidateRepair repair,
        long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repair);
        // Same lock order as startup and source-permission writers. The lease spans
        // byte verification, backup IO and atomic publication of the reviewed choice.
        if (repairRestrictedSources is not null && repairPermissionGate is null)
            throw new InvalidOperationException("The source-permission authority is not serialized for reauthorization.");
        if (repairPermissionGate is not null) await repairPermissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await MutateDocumentAsync(expectedRevision, async document =>
            {
                document = WithCurrentRepairRestrictions(document);
                BundledCandidateRepair? current = BundledCandidateMigration.Preview(document).Rows
                    .SingleOrDefault(row => row.PluginId == repair.PluginId && row.OldCandidateId == repair.OldCandidateId);
                if (current is null || current != repair || !current.CanReauthorize)
                    throw new InvalidOperationException("Bundled permission review changed or is blocked. Review it again.");
                PluginActivationSnapshot plugin = Find(document.Profile, current.PluginId);
                PluginCandidateSnapshot candidate = AvailableCandidate(plugin, current.NewCandidateId!);
                DeliveredCandidateProvenance proof = document.Provenance.Single(p => p.SlotId == current.SlotId &&
                    BundledCandidateMigration.Matches(p, candidate));
                await PluginInstallationInventory.VerifyRepairTargetAsync(proof, candidate.Artifact!, candidate.Description!, cancellationToken)
                    .ConfigureAwait(false);
                document = WithCurrentRepairRestrictions(document);
                if (BundledCandidateMigration.Preview(document).Rows.SingleOrDefault(row => row.PluginId == current.PluginId) != current)
                    throw new InvalidOperationException("Bundled permissions changed during verification. Review them again.");
                PluginReleaseSnapshot basis = BundledCandidateMigration.TrustedPreReplacementBasis(document, plugin).Release
                    ?? throw new InvalidOperationException("No trusted pre-replacement configuration is retained.");
                BundledRepairBinding binding = new(plugin.PluginId, plugin.SelectedCandidateId!, basis.ExecutionRevision,
                    candidate.CandidateId, candidate.Artifact!.ExecutionRevision, proof.SlotId)
                {
                    OriginalRelease = Clone(basis),
                    ReauthorizedPermissionFingerprint = current.PermissionFingerprint
                };
                // No grant/configuration/owner/enable mutation and no live activation.
                // Recheck this exact authority at startup; changed rights or code fail closed.
                return document with
                {
                    Profile = Replace(document.Profile, plugin with
                    {
                        Pending = new(candidate.CandidateId, candidate.Artifact!, candidate.Description!, basis.Configuration),
                        Problem = "Bundled permissions explicitly reconfirmed for this exact build at next start; original state retained."
                    }),
                    Bindings = document.Bindings.Where(b => b.PluginId != plugin.PluginId).Append(binding).ToArray()
                };
            }, cancellationToken, preserveReselectionBackup: true).ConfigureAwait(false);
        }
        finally { repairPermissionGate?.Release(); }
    }
}

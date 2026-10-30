using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Installation;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Configuration;

public sealed partial class PluginProfileStore
{
    /// <summary>Actual state before the first explicitly confirmed new selection, not historical v1 evidence.</summary>
    public string PreReselectionBackupPath => Path.Combine(directory, "state.pre-reselection.json");

    public async Task<BundledCandidateReselectionPreview> PreviewBundledReselectionsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(directory)) return new(0, []);
        await using FileStream lease = await AcquireLockAsync(cancellationToken, FileAccess.Read).ConfigureAwait(false);
        return PreviewReselections(WithCurrentRepairRestrictions(await ReadDocumentCoreAsync(cancellationToken).ConfigureAwait(false)));
    }

    private static BundledCandidateReselectionPreview PreviewReselections(BundledProfileDocument document)
    {
        List<BundledCandidateReselection> rows = new();
        foreach (PluginActivationSnapshot plugin in document.Profile.Plugins)
        {
            if (plugin.SelectedCandidateId is not { } selected || plugin.Active is not null && plugin.Pinned is null) continue;
            PluginCandidateSnapshot? missing = plugin.Candidates.SingleOrDefault(c => c.CandidateId == selected);
            // This route is only a fresh choice after a missing legacy selection.
            // Existing local selections and proven delivered repair history keep their own paths.
            if (missing is { IsPresent: true } || document.Provenance.Any(p => p.CandidateId == selected)
                || document.Bindings.Any(b => b.PluginId == plugin.PluginId)) continue;
            PreReplacementBasis prior = BundledCandidateMigration.TrustedPreReplacementBasis(document, plugin);
            PluginReleaseSnapshot? basis = prior.Release;
            PluginCandidateSnapshot[] present = plugin.Candidates.Where(c => c.IsPresent).ToArray();
            PluginCandidateSnapshot[] targets = present.Where(c => c.Problem is null && c.Artifact is not null && c.Description is not null
                && document.Provenance.Count(p => BundledCandidateMigration.Matches(p, c)) == 1).ToArray();
            PluginCandidateSnapshot? target = targets.Length == 1 ? targets[0] : null;
            DeliveredCandidateProvenance? proof = target is null ? null : document.Provenance.Single(p => BundledCandidateMigration.Matches(p, target));
            bool rights = document.Profile.Grants.Any(g => g.PluginId == plugin.PluginId && g.Access != FilterDataAccess.None)
                || (basis?.Description.Sources.Concat(target?.Description?.Sources ?? []).Any(s =>
                    document.RestrictedSourceIds.Contains(s.SourceId, StringComparer.Ordinal)) ?? false);
            string? reason = plugin.Pinned is not null ? "pinned" :
                prior.Missing || basis is null || basis.CandidateId != selected ? "unproven-configuration" :
                plugin.Pending is { } pending && (pending.CandidateId != basis.CandidateId || pending.ExecutionRevision != basis.ExecutionRevision)
                    ? "pending-choice" :
                present.Length > 1 || targets.Length > 1 ? "ambiguous" :
                target is null ? "no-proven-target" :
                rights ? "inherited-grants" :
                !basis.Description.Sources.Select(s => s.SourceId).Order(StringComparer.Ordinal).SequenceEqual(
                    target.Description!.Sources.Select(s => s.SourceId).Order(StringComparer.Ordinal)) ? "source-identity-changed" :
                BundledCandidateMigration.HasOwnerConflict(document.Profile, plugin.PluginId, target.Description!) ? "source-owner-conflict" :
                SdkValidation.ValidateConfiguration(target.Description!, basis.Configuration).Count != 0 ? "configuration-incompatible" : null;
            rows.Add(new(plugin.PluginId, selected, target?.CandidateId, proof?.SlotId,
                missing?.CandidatePath, target?.CandidatePath, basis?.ExecutionRevision, target?.Artifact?.ExecutionRevision,
                basis is null ? null : Encoding.UTF8.GetString(SdkJson.Serialize(basis.Configuration)), reason));
        }
        return new(document.Profile.Revision, rows);
    }

    public async Task<PluginProfileSnapshot> ConfirmBundledReselectionAsync(BundledCandidateReselection selection, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        // The host supplies the SAME gate used by all source-permission writers.
        // It spans final validation, awaited backups, and atomic publication.
        if (repairRestrictedSources is not null && repairPermissionGate is null)
            throw new InvalidOperationException("The source-permission authority is not serialized for new selection.");
        if (repairPermissionGate is not null) await repairPermissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await MutateDocumentAsync(expectedRevision, async document =>
            {
                document = WithCurrentRepairRestrictions(document);
                BundledCandidateReselection? current = PreviewReselections(document).Rows.SingleOrDefault(row => row.PluginId == selection.PluginId);
                if (current is null || current != selection || !current.CanConfirm)
                    throw new InvalidOperationException("Bundled new selection is blocked or its preview changed. Review it again.");
                PluginActivationSnapshot plugin = Find(document.Profile, current.PluginId);
                PluginCandidateSnapshot candidate = AvailableCandidate(plugin, current.NewCandidateId!);
                DeliveredCandidateProvenance proof = document.Provenance.Single(p => BundledCandidateMigration.Matches(p, candidate));
                await PluginInstallationInventory.VerifyRepairTargetAsync(proof, candidate.Artifact!, candidate.Description!, cancellationToken).ConfigureAwait(false);
                // Live permission state may have changed while disk verification ran.
                document = WithCurrentRepairRestrictions(document);
                if (PreviewReselections(document).Rows.SingleOrDefault(row => row.PluginId == current.PluginId) != current)
                    throw new InvalidOperationException("Bundled new-selection restrictions changed. Review it again.");
                PluginConfiguration configuration = SdkJson.Deserialize<PluginConfiguration>(Encoding.UTF8.GetBytes(current.ConfigurationJson!));
                // This is a NEW selection, not an old-to-new provenance binding. Do not
                // retain an old executable as authority for this new choice. Its exact
                // original state remains in the immutable backups and old inventory.
                PluginActivationSnapshot next = plugin with
                {
                    SelectedCandidateId = candidate.CandidateId,
                    Active = null, LastGood = null,
                    Pending = new(candidate.CandidateId, candidate.Artifact!, candidate.Description!, configuration),
                    Problem = "New bundled selection confirmed for next start; original state retained in backup."
                };
                return document with { Profile = Replace(document.Profile, next) };
            }, cancellationToken, preserveReselectionBackup: true).ConfigureAwait(false);
        }
        finally { repairPermissionGate?.Release(); }
    }

    private async Task PreserveReselectionBackupAsync(byte[] original, string expectedHash, CancellationToken token)
    {
        string file = PreReselectionBackupPath, digestFile = file + ".sha256";
        if (File.Exists(file) && File.Exists(digestFile))
        {
            RejectLinkedBackup(file); RejectLinkedBackup(digestFile);
            byte[] saved = await ReadBoundedStateBytesAsync(file, token).ConfigureAwait(false);
            ValidateReselectionBackup(saved);
            if (!StringComparer.Ordinal.Equals(Convert.ToHexStringLower(SHA256.HashData(saved)), expectedHash))
                throw new IOException("The original new-selection backup does not match this profile's retained binding.");
            byte[] digest = await ReadBoundedStateBytesAsync(digestFile, token, 65).ConfigureAwait(false);
            if (!digest.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(saved)) + "\n")))
                throw new IOException("Original new-selection backup digest differs; state was not published.");
            return;
        }
        if (!StringComparer.Ordinal.Equals(Convert.ToHexStringLower(SHA256.HashData(original)), expectedHash))
            throw new IOException("The bound original new-selection backup is missing or incomplete; retain current state.");
        await PreserveExactFileAsync(file, original, MaximumStoreBytes, token).ConfigureAwait(false);
        await PreserveExactFileAsync(digestFile, Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(original)) + "\n"), 65, token).ConfigureAwait(false);
    }

    private static void ValidateReselectionBackup(byte[] bytes)
    {
        try
        {
            Envelope envelope = JsonSerializer.Deserialize<Envelope>(bytes, SdkJson.CreateOptions()) ?? throw new JsonException("Null envelope.");
            if (envelope.Payload is null || envelope.Sha256 != Convert.ToHexString(SHA256.HashData(envelope.Payload)))
                throw new JsonException("Backup integrity differs.");
            if (envelope.Version == FormatVersion) ValidateState(SdkJson.Deserialize<PluginProfileSnapshot>(envelope.Payload));
            else if (envelope.Version == DeliveredFormatVersion)
            {
                BundledProfileDocument document = SdkJson.Deserialize<BundledProfileDocument>(envelope.Payload);
                ValidateState(document.Profile); BundledCandidateMigration.Validate(document);
            }
            else throw new JsonException("Unsupported backup envelope.");
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or OverflowException or NullReferenceException)
        { throw new IOException("Original new-selection backup is invalid; state was not published.", exception); }
    }
}

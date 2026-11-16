using System.Security.Cryptography;
using MeowChan.PluginHost.Activation;

namespace MeowChan.PluginHost.Configuration;

/// <summary>What one retirement of replaced bundled identities changed.</summary>
/// <param name="Profile">The profile after the retirement (unchanged when nothing was retired).</param>
/// <param name="RetiredPluginIds">The removed identities, ordinal order; empty when nothing changed.</param>
/// <param name="ReleasedExecutionRevisions">Cached builds that only the removed records referenced.</param>
/// <param name="ReleasedCompilationFingerprints">Cached compilations that only the removed records referenced.</param>
/// <param name="BackupPath">The immutable copy of the profile state from before the retirement, or null when nothing changed.</param>
public sealed record SupersededRetirement(PluginProfileSnapshot Profile, IReadOnlyList<string> RetiredPluginIds,
    IReadOnlySet<string> ReleasedExecutionRevisions, IReadOnlySet<string> ReleasedCompilationFingerprints, string? BackupPath);

public sealed partial class PluginProfileStore
{
    private const string RetirementBackupPrefix = "state.pre-retirement-";

    /// <summary>The immutable pre-retirement copy of a state envelope with this SHA-256 (one per distinct original).</summary>
    public string PreRetirementBackupPath(string envelopeSha256) =>
        Path.Combine(directory, RetirementBackupPrefix + envelopeSha256[..16].ToLowerInvariant() + ".json");

    /// <summary>
    /// Removes replaced bundled identities from the active profile once their successor serves every source they
    /// declared (<see cref="SupersededBundledPlugins.InProfile"/>) and the caller has taken over what it needs from
    /// them (<paramref name="eligible"/>, for example the per-site switches). A replaced identity is only history:
    /// it never activates or owns a source again, so keeping it only showed a stale enabled state and kept its cached
    /// builds alive. The exact state envelope from before the removal is kept as an immutable backup next to the
    /// profile; the removal and the backup are one atomic, idempotent step (nothing to retire writes nothing).
    /// Its grants, explicit source-owner choices, repair bindings and SDK bridge records go with it; delivered
    /// provenance evidence stays. A record with an active or pinned build, or any present candidate, is never retired.
    /// </summary>
    public async Task<SupersededRetirement> RetireSupersededAsync(IReadOnlySet<string> eligible, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eligible);
        PluginProfileSnapshot current = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (current.Revision != expectedRevision) throw new PluginProfileConflictException(expectedRevision, current.Revision);
        if (Retireable(current, eligible).Count == 0) return Unchanged(current);
        string[] retired = [];
        HashSet<string> releasedRevisions = new(StringComparer.Ordinal), releasedFingerprints = new(StringComparer.Ordinal);
        string? backup = null;
        PluginProfileSnapshot next = await MutateDocumentAsync(expectedRevision, document =>
        {
            HashSet<string> retire = Retireable(document.Profile, eligible);
            retired = retire.Order(StringComparer.Ordinal).ToArray();
            PluginProfileSnapshot state = document.Profile;
            PluginActivationSnapshot[] removed = state.Plugins.Where(plugin => retire.Contains(plugin.PluginId)).ToArray();
            PluginProfileSnapshot kept = state with
            {
                Plugins = state.Plugins.Where(plugin => !retire.Contains(plugin.PluginId)).ToArray(),
                Grants = state.Grants.Where(grant => !retire.Contains(grant.PluginId)).ToArray(),
                SelectedSourceOwners = state.SelectedSourceOwners.Where(pair => !retire.Contains(pair.Value))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            };
            BundledProfileDocument updated = document with
            {
                Profile = kept,
                Bindings = document.Bindings.Where(binding => !retire.Contains(binding.PluginId)).ToArray(),
                AcceptedSdkUpgrades = document.AcceptedSdkUpgrades.Where(upgrade => !retire.Contains(upgrade.PluginId)).ToArray()
            };
            // A cached build stays while anything that remains could still start or restore it.
            HashSet<string> keepRevisions = new(kept.RetainedExecutionRevisions, StringComparer.Ordinal);
            HashSet<string> keepFingerprints = new(kept.RetainedCompilationFingerprints, StringComparer.Ordinal);
            foreach (PluginReleaseSnapshot original in updated.Bindings.Select(binding => binding.OriginalRelease).OfType<PluginReleaseSnapshot>())
            {
                keepRevisions.Add(original.ExecutionRevision);
                keepFingerprints.Add(original.CompilationFingerprint);
            }
            PluginProfileSnapshot gone = new(0, removed, []);
            releasedRevisions = gone.RetainedExecutionRevisions.Where(revision => !keepRevisions.Contains(revision)).ToHashSet(StringComparer.Ordinal);
            releasedFingerprints = gone.RetainedCompilationFingerprints.Where(fingerprint => !keepFingerprints.Contains(fingerprint)).ToHashSet(StringComparer.Ordinal);
            return Task.FromResult(updated);
        }, cancellationToken, skipWriteWhenUnchanged: true, preserveOriginal: async (original, token) =>
        {
            if (retired.Length == 0) return;
            string file = PreRetirementBackupPath(Convert.ToHexStringLower(SHA256.HashData(original)));
            await PreserveExactFileAsync(file, original, MaximumStoreBytes, token, "Pre-retirement backup").ConfigureAwait(false);
            backup = file;
        }).ConfigureAwait(false);
        return retired.Length == 0 || backup is null ? Unchanged(next)
            : new(next, retired, releasedRevisions, releasedFingerprints, backup);
    }

    private static SupersededRetirement Unchanged(PluginProfileSnapshot profile) =>
        new(profile, [], new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), null);

    private HashSet<string> Retireable(PluginProfileSnapshot profile, IReadOnlySet<string> eligible) =>
        Configuration.SupersededBundledPlugins.InProfile(profile, SupersededBundledPlugins)
            .Where(eligible.Contains)
            .Where(id => profile.Plugins.Single(plugin => plugin.PluginId == id) is { Active: null, Pinned: null } old
                && old.Candidates.All(candidate => !candidate.IsPresent))
            .ToHashSet(StringComparer.Ordinal);
}

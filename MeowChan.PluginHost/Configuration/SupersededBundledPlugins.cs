using MeowChan.PluginHost.Activation;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Configuration;

/// <summary>
/// Bundled plugin identities that a later delivery replaced by another bundled plugin (for example
/// several single-site sources merged into one engine-family plugin with the same source IDs).
/// The table is host-owned; it is never inferred from a plugin's self-declared identity.
/// </summary>
/// <remarks>
/// A replaced identity is never rewritten or deleted: its saved record (enabled state, history, explicit
/// choices) stays as it was, so an older release still finds it. It only stops claiming its source IDs,
/// and only while all of these hold: it has no present candidate and no pinned build, and the successor
/// covers every source the replaced identity ever declared — at startup through a delivered, problem-free
/// candidate, afterwards through the successor's own activated release. A local copy of the old plugin, a
/// pinned build or a missing successor keeps the ordinary explicit source-conflict rules.
/// Once the host has taken over what it needs from such a record, <see cref="PluginProfileStore.RetireSupersededAsync"/>
/// removes it from the active profile; the exact state before that stays in an immutable pre-retirement backup.
/// </remarks>
public static class SupersededBundledPlugins
{
    /// <summary>Replaced identities in a saved profile (the successor activated covering their sources).</summary>
    public static IReadOnlySet<string> InProfile(PluginProfileSnapshot profile, IReadOnlyDictionary<string, string> successors)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(successors);
        HashSet<string> result = new(StringComparer.Ordinal);
        foreach (PluginActivationSnapshot old in profile.Plugins)
        {
            if (!Candidate(old, successors, out string successorId) || old.Candidates.Any(candidate => candidate.IsPresent)) continue;
            PluginDescription? successor = profile.Plugins.SingleOrDefault(plugin => plugin.PluginId == successorId) is { } owner
                ? (owner.Active ?? owner.LastGood)?.Description : null;
            if (successor is not null && Covers(successor, old)) result.Add(old.PluginId);
        }
        return result;
    }

    internal static HashSet<string> AtStartup(PluginProfileSnapshot state, IReadOnlyList<PluginCandidateObservation> startup,
        IReadOnlyList<DeliveredCandidateProvenance>? delivered, IReadOnlyDictionary<string, string> successors)
    {
        HashSet<string> result = new(StringComparer.Ordinal);
        if (successors.Count == 0 || delivered is null) return result;
        foreach (PluginActivationSnapshot old in state.Plugins)
        {
            if (!Candidate(old, successors, out string successorId)
                || startup.Any(candidate => candidate.PluginId == old.PluginId)) continue;
            bool covered = startup.Any(candidate => candidate.PluginId == successorId && candidate.Problem is null
                && candidate.Artifact is not null && candidate.Description is { } description
                && delivered.Any(proof => proof.CandidateId == candidate.CandidateId && proof.PluginId == candidate.PluginId
                    && proof.CandidatePath == candidate.CandidatePath && BundledCandidateMigration.Matches(proof, candidate.Artifact, description))
                && Covers(description, old));
            if (covered) result.Add(old.PluginId);
        }
        return result;
    }

    private static bool Candidate(PluginActivationSnapshot old, IReadOnlyDictionary<string, string> successors, out string successorId)
    {
        successorId = string.Empty;
        if (!successors.TryGetValue(old.PluginId, out string? successor) || successor == old.PluginId) return false;
        successorId = successor;
        return old.Pinned is null;
    }

    private static bool Covers(PluginDescription successor, PluginActivationSnapshot old)
    {
        string[] declared = old.Candidates.Select(candidate => candidate.Description)
            .Concat(new[] { old.Active?.Description, old.Pending?.Description, old.LastGood?.Description, old.Pinned?.Description })
            .OfType<PluginDescription>().SelectMany(description => description.Sources).Select(source => source.SourceId)
            .Distinct(StringComparer.Ordinal).ToArray();
        return declared.All(id => successor.Sources.Any(source => source.SourceId == id));
    }
}

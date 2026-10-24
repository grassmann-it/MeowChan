using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop.Plugins;

internal enum PluginDescriptionOrigin { Candidate, ActiveFallback, LastGoodFallback, Unavailable }
internal enum PluginAvailability { NotLoaded, Disabled, Active, Unavailable }
internal enum PluginPendingKind { None, Build, Configuration }

/// <summary>A production client observed through its owning desktop session, never a test-worker guess.</summary>
internal sealed record PluginProductionWorkerSnapshot(string PluginId, PluginWorkerSnapshot Worker);

/// <summary>
/// Read-only meanings shared by list, details and actions. Availability describes the local
/// selected release, not a running process, a successful request or website health.
/// </summary>
internal sealed class PluginEntryPresentation
{
    internal string Key { get; private init; } = string.Empty;
    internal string? PluginId { get; private init; }
    internal string? CandidateId { get; private init; }
    internal string Name { get; private init; } = string.Empty;
    internal string? Description => DescriptionMetadata?.Description;
    internal PluginDescription? DescriptionMetadata { get; private init; }
    internal PluginDescriptionOrigin DescriptionOrigin { get; private init; }
    internal bool HasRetainedDescription => DescriptionOrigin is PluginDescriptionOrigin.ActiveFallback or PluginDescriptionOrigin.LastGoodFallback;
    internal string? CandidateVersion { get; private init; }
    internal IReadOnlyList<SourceDescription> Sources => DescriptionMetadata?.Sources ?? Array.Empty<SourceDescription>();
    internal IReadOnlyList<FilterDescription> Filters => DescriptionMetadata?.Filters ?? Array.Empty<FilterDescription>();
    internal bool IsSource => Sources.Count > 0;
    internal bool IsFilter => Filters.Count > 0;
    internal PluginAvailability Availability { get; private init; }
    internal PluginReleaseSnapshot? Active { get; private init; }
    internal PluginReleaseSnapshot? Pending { get; private init; }
    internal PluginReleaseSnapshot? Pinned { get; private init; }
    internal PluginReleaseSnapshot? LastGood { get; private init; }
    internal PluginPendingKind PendingKind { get; private init; }
    internal bool IsRollbackPinned => Pinned != null;
    internal bool IsSelectedCandidate { get; private init; }
    internal bool IsActiveCandidate { get; private init; }
    internal bool IsPendingCandidateBuild { get; private init; }
    internal bool HasChangedCandidateBuild { get; private init; }
    internal bool CandidateMissing { get; private init; }
    internal bool HasCandidateConflict { get; private init; }
    internal string? CandidateProblem { get; private init; }
    internal string? ActivationProblem { get; private init; }
    internal bool CanSelectCandidate { get; private init; }
    internal bool CandidateChoiceRequired { get; private init; }
    internal PluginWorkerSnapshot? Worker { get; private init; }
    internal bool HasWorkerProblem => Worker?.State is "unavailable" or "intervention-required";
    internal bool WorkerNeedsIntervention => Worker?.State == "intervention-required";
    internal bool HasDraft { get; private init; }
    internal bool HasUnsavedChanges { get; private init; }
    internal bool HasDiscardableChanges { get; private init; }
    internal bool IsDraftStale { get; private init; }
    internal bool NeedsMigrationAcknowledgement { get; private init; }

    internal static PluginEntryPresentation Create(PluginCatalogEntry entry,
        IReadOnlyList<PluginProductionWorkerSnapshot>? productionWorkers = null,
        PluginConfigurationDraft? draft = null, long? profileRevision = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        PluginActivationSnapshot? state = entry.Activation;
        PluginCandidateSnapshot? candidate = entry.Candidate;
        (PluginDescription? description, PluginDescriptionOrigin origin) = candidate?.Description is { } current
            ? (current, PluginDescriptionOrigin.Candidate)
            : state?.Active?.Description is { } activeDescription
                ? (activeDescription, PluginDescriptionOrigin.ActiveFallback)
                : state?.LastGood?.Description is { } retainedDescription
                    ? (retainedDescription, PluginDescriptionOrigin.LastGoodFallback)
                    : (null, PluginDescriptionOrigin.Unavailable);
        string? pluginId = entry.PluginId ?? state?.PluginId ?? candidate?.PluginId;
        bool sameActiveCandidate = entry.CandidateId != null && state?.Active?.CandidateId == entry.CandidateId;
        bool activeBuild = sameActiveCandidate && candidate?.Artifact != null &&
            candidate.Artifact.ExecutionRevision == state!.Active!.ExecutionRevision;
        bool canSelect = pluginId != null && entry.CandidateId != null && candidate is
            { IsPresent: true, Problem: null, Artifact: not null, Description: not null } &&
            candidate.CandidateId == entry.CandidateId && candidate.PluginId == pluginId;
        bool selected = entry.CandidateId != null && state?.SelectedCandidateId == entry.CandidateId;
        // Re-selecting the same path is meaningful after rebuilding it, and always releases a pin.
        bool alreadyScheduled = selected && state?.Pinned == null &&
            (state?.Pending is { } pending
                ? MatchesCandidateBuild(pending, entry)
                : state?.Active is { } active && MatchesCandidateBuild(active, entry));
        bool matchingDraft = draft != null && entry.CandidateId != null && draft.CandidateId == entry.CandidateId;
        PluginWorkerSnapshot[] workers = state?.Active is { } release && pluginId != null && state.Enabled
            ? (productionWorkers ?? Array.Empty<PluginProductionWorkerSnapshot>())
                .Where(item => item.PluginId == pluginId &&
                    item.Worker.Identity.ExecutionRevision == release.ExecutionRevision &&
                    item.Worker.Identity.ConfigurationRevision == release.Configuration.Revision)
                .Select(item => item.Worker).Take(2).ToArray()
            : Array.Empty<PluginWorkerSnapshot>();

        return new PluginEntryPresentation
        {
            Key = entry.Key,
            PluginId = pluginId,
            CandidateId = entry.CandidateId,
            Name = entry.Name,
            DescriptionMetadata = description,
            DescriptionOrigin = origin,
            CandidateVersion = candidate?.Description?.PluginVersion,
            Availability = state == null ? PluginAvailability.NotLoaded :
                !state.Enabled ? PluginAvailability.Disabled :
                state.Active != null ? PluginAvailability.Active : PluginAvailability.Unavailable,
            Active = state?.Active,
            Pending = state?.Pending,
            Pinned = state?.Pinned,
            LastGood = state?.LastGood,
            PendingKind = state?.Pending == null ? PluginPendingKind.None :
                state.Active != null && SameBuild(state.Active, state.Pending)
                    ? PluginPendingKind.Configuration : PluginPendingKind.Build,
            IsSelectedCandidate = selected,
            IsActiveCandidate = activeBuild,
            IsPendingCandidateBuild = state?.Pending is { } pendingRelease && MatchesCandidateBuild(pendingRelease, entry),
            HasChangedCandidateBuild = sameActiveCandidate && candidate?.Artifact != null && !activeBuild,
            CandidateMissing = candidate?.IsPresent == false,
            HasCandidateConflict = state?.Candidates.Count(item => item.IsPresent) > 1,
            CandidateProblem = candidate?.Problem,
            ActivationProblem = state?.Problem,
            CanSelectCandidate = canSelect,
            CandidateChoiceRequired = canSelect && !alreadyScheduled,
            // Multiple matching observations are ambiguous; do not choose by enumeration order.
            Worker = workers.Length == 1 ? workers[0] : null,
            HasDraft = matchingDraft,
            HasUnsavedChanges = matchingDraft && draft!.Dirty,
            HasDiscardableChanges = matchingDraft && draft!.HasDiscardableChanges,
            IsDraftStale = matchingDraft && profileRevision is long revision && draft!.ExpectedRevision != revision,
            NeedsMigrationAcknowledgement = matchingDraft && draft!.NeedsMigrationAcknowledgement
        };
    }

    private static bool SameBuild(PluginReleaseSnapshot left, PluginReleaseSnapshot right) =>
        left.CandidateId == right.CandidateId && left.ExecutionRevision == right.ExecutionRevision;

    private static bool MatchesCandidateBuild(PluginReleaseSnapshot release, PluginCatalogEntry entry) =>
        release.CandidateId == entry.CandidateId && entry.Candidate?.Artifact is { } artifact &&
        release.ExecutionRevision == artifact.ExecutionRevision;
}

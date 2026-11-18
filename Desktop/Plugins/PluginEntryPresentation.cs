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
    internal string? Description => DescriptionMetadata is null ? null : SourceWording.DisplayDescription(DescriptionMetadata);
    internal PluginDescription? DescriptionMetadata { get; private init; }
    internal PluginDescriptionOrigin DescriptionOrigin { get; private init; }
    internal bool HasRetainedDescription => DescriptionOrigin is PluginDescriptionOrigin.ActiveFallback or PluginDescriptionOrigin.LastGoodFallback;
    internal string? CandidateVersion { get; private init; }
    internal IReadOnlyList<SourceDescription> Sources => DescriptionMetadata?.Sources ?? Array.Empty<SourceDescription>();
    internal IReadOnlyList<SourceWordingClassification> SourceClassifications { get; private init; } = Array.Empty<SourceWordingClassification>();
    internal IReadOnlyList<FilterDescription> Filters => DescriptionMetadata?.Filters ?? Array.Empty<FilterDescription>();
    internal bool IsSource => Sources.Count > 0;
    internal bool HasSourceClassifications => SourceClassifications.Count > 0;
    internal bool HasExperimentalSources => SourceClassifications.Any(source => source.Maturity == SourceWordingMaturity.Experimental);
    internal bool IsFilter => Filters.Count > 0;
    internal PluginAvailability Availability { get; private init; }
    internal PluginReleaseSnapshot? Active { get; private init; }
    internal PluginReleaseSnapshot? Pending { get; private init; }
    internal PluginReleaseSnapshot? Pinned { get; private init; }
    internal PluginReleaseSnapshot? LastGood { get; private init; }
    internal PluginSdkTransitionSnapshot? AutomaticSdkTransition { get; private init; }
    internal bool HasAutomaticSdkTransition => AutomaticSdkTransition is { ExplicitlyApproved: false } transition &&
        Active is { } active && active.ExecutionRevision == transition.ExecutionRevision &&
        active.Description.SdkVersion == transition.ToSdkVersion;
    internal bool HasExplicitlyReviewedSdkTransition => AutomaticSdkTransition is { ExplicitlyApproved: true } transition &&
        Active is { } active && active.ExecutionRevision == transition.ExecutionRevision &&
        active.Description.SdkVersion == transition.ToSdkVersion;
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
    /// <summary>
    /// The plugin is not running because its bundled build waits for an explicit repair review, or its running build was
    /// observed failing with <c>sdk-mismatch</c> in this session (it runs but cannot work until another build is confirmed).
    /// </summary>
    internal bool NeedsRepairReview => HasSdkMismatch
        || !IsActiveCandidate && MeowChan.PluginHost.Configuration.PluginActivationProblems.RequiresRepairReview(ActivationProblem);
    /// <summary>The exact active build was observed failing with <c>sdk-mismatch</c> in this session (presentation evidence only).</summary>
    internal bool HasSdkMismatch { get; private init; }
    /// <summary>No build is active in the profile and a build waits for the next start: only a restart remains.</summary>
    internal bool IsConfirmedForNextStart => Availability == PluginAvailability.Unavailable && Active == null && PendingKind == PluginPendingKind.Build;
    /// <summary>
    /// The activation problem worth showing as a problem. The store's own note after a confirmed new bundled selection is the
    /// confirmation receipt, not a problem: the "confirmed for next start" status line says it in the UI language.
    /// </summary>
    internal string? VisibleActivationProblem => IsConfirmedForNextStart && ActivationProblem == ReselectionConfirmedNote ||
        IsPinnedReleaseUpdatedInPlace ? null : ActivationProblem;
    /// <summary>
    /// This start moved the pinned release to its rebuilt bundled build of the same version (#130): a calm one-start notice,
    /// not a problem. The store's note is cleared by the next start, so the card says it once.
    /// </summary>
    internal bool IsPinnedReleaseUpdatedInPlace =>
        ActivationProblem == MeowChan.PluginHost.Configuration.PluginActivationProblems.PinnedReleaseUpdatedInPlace &&
        Pinned is { } pin && Active is { } active && SameBuild(pin, active);
    /// <summary>The exact note <c>PluginProfileStore.ConfirmBundledReselectionAsync</c> leaves (pinned by a source test).</summary>
    internal const string ReselectionConfirmedNote = "New bundled selection confirmed for next start; original state retained in backup.";
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
        PluginConfigurationDraft? draft = null, long? profileRevision = null,
        IReadOnlyList<SourceWordingMaintenanceEvidence>? maintenanceEvidence = null,
        IReadOnlyList<SourceWordingMaturityEvidence>? maturityEvidence = null,
        Func<string, string, bool>? sdkMismatch = null)
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
        PluginArtifact? displayedArtifact = origin switch
        {
            PluginDescriptionOrigin.Candidate => candidate?.Artifact,
            PluginDescriptionOrigin.ActiveFallback => state?.Active?.Artifact,
            PluginDescriptionOrigin.LastGoodFallback => state?.LastGood?.Artifact,
            _ => null
        };
        // The compiled hash allowlist is trusted presentation evidence; changed local candidates fail closed.
        IReadOnlyList<SourceWordingClassification> classifications =
            maintenanceEvidence == null && maturityEvidence == null
                ? SourceWordingEvidence.DisplayClassifications(description, displayedArtifact?.ExecutionRevision)
                : null!;
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
            SourceClassifications = classifications ?? description?.Sources.Select(source => SourceWording.Classify(
                description, source, displayedArtifact?.ExecutionRevision, maintenanceEvidence, maturityEvidence)).ToArray()
                ?? Array.Empty<SourceWordingClassification>(),
            DescriptionOrigin = origin,
            CandidateVersion = candidate?.Description?.PluginVersion,
            Availability = state == null ? PluginAvailability.NotLoaded :
                !state.Enabled ? PluginAvailability.Disabled :
                state.Active != null ? PluginAvailability.Active : PluginAvailability.Unavailable,
            Active = state?.Active,
            Pending = state?.Pending,
            Pinned = state?.Pinned,
            LastGood = state?.LastGood,
            AutomaticSdkTransition = state?.AutomaticSdkTransition,
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
            HasSdkMismatch = sdkMismatch != null && pluginId != null && state is { Enabled: true, Active: { } running }
                && sdkMismatch(pluginId, running.ExecutionRevision),
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

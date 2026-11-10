using System;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Desktop;

internal sealed record NativeTransferPreparedImport(NativeTransferImportScope Scope,
    NativeTransferImportPlanSummary Summary, NativeMediaImportPlan? Import, NativeMediaMergePlan? Merge);

internal sealed partial class DesktopSessionHost
{
    // Observers only; neither callback supplies a plan, token or success disposition.
    internal Func<Task>? BeforeNativeImportCommitForTests { get; set; }
    internal Action<NativeMediaImportPhase>? NativeImportCheckpointForTests { get; set; }

    internal NativeTransferImportSession CreateNativeTransferImportSession(string workParentDirectory)
    {
        EnsureReady();
        return new(this, workParentDirectory);
    }

    // Explicit scope is the production contract: the selected operation is the only
    // planner consulted. A missing target never falls through from Merge to New, and
    // an existing target never falls through from New to Merge.
    internal Task<(NativeTransferPreparedImport? Plan, NativeTransferImportError Error)>
        PrepareNativeImportAsync(NativeTransferImportStage stage, NativeTransferImportScope scope,
            CancellationToken token)
        => scope switch
        {
            NativeTransferImportScope.New => PrepareNativeImportNewAsync(stage, token),
            NativeTransferImportScope.Merge => PrepareNativeImportMergeAsync(stage, token),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown native transfer import scope.")
        };

    // Compatibility path for pre-scope tests and internal callers. New UI code must use
    // the explicit overload above; keeping this isolated makes the migration auditable
    // without changing the core plan/receipt authorities.
    internal async Task<(NativeTransferPreparedImport? Plan, NativeTransferImportError Error)>
        PrepareNativeImportAsync(NativeTransferImportStage stage, CancellationToken token)
    {
        EnsureReady();
        NativeMediaMergePreparation merge = await _content.Catalog.PrepareNativeMediaMergeAsync(stage, token).ConfigureAwait(false);
        if (merge.Plan is { } existing) return BuildMerge(existing);
        if (merge.Error != NativeMediaMergeError.MissingTarget)
            return (null, MapMergeError(merge.Error));
        NativeMediaImportPreparation added = await _content.Catalog.PrepareNativeMediaImportAsync(stage, token).ConfigureAwait(false);
        return added.Plan is { } plan ? BuildNew(plan) : (null, MapImportError(added.Error));
    }

    private async Task<(NativeTransferPreparedImport? Plan, NativeTransferImportError Error)>
        PrepareNativeImportNewAsync(NativeTransferImportStage stage, CancellationToken token)
    {
        EnsureReady();
        NativeMediaImportPreparation added = await _content.Catalog.PrepareNativeMediaImportAsync(stage, token).ConfigureAwait(false);
        if (added.Plan is { } plan) return BuildNew(plan);
        return added.Error == NativeMediaImportError.ResourceExists
            ? (null, NativeTransferImportError.ScopeTargetMismatch)
            : (null, MapImportError(added.Error));
    }

    private async Task<(NativeTransferPreparedImport? Plan, NativeTransferImportError Error)>
        PrepareNativeImportMergeAsync(NativeTransferImportStage stage, CancellationToken token)
    {
        EnsureReady();
        NativeMediaMergePreparation merge = await _content.Catalog.PrepareNativeMediaMergeAsync(stage, token).ConfigureAwait(false);
        if (merge.Plan is { } existing) return BuildMerge(existing);
        return merge.Error == NativeMediaMergeError.MissingTarget
            ? (null, NativeTransferImportError.ScopeTargetMismatch)
            : (null, MapMergeError(merge.Error));
    }

    private static (NativeTransferPreparedImport? Plan, NativeTransferImportError Error)
        BuildNew(NativeMediaImportPlan added)
    {
        var counts = added.Representation?.Counts;
        // Attachments is a legacy-only getter and throws on representation plans.
        return (new(NativeTransferImportScope.New,
            new(added.Resource, false, counts?.PostCount ?? added.Thread.Posts.Length, 0,
                counts?.AttachmentCount ?? added.Attachments.Length, 0,
                EvidenceAdditions: counts?.EvidenceAdditions ?? 0,
                DerivedDescriptorAdditions: counts?.DerivedDescriptorAdditions ?? 0,
                ContainedDerivedAdditions: counts?.ContainedDerivedAdditions ?? 0), added, null),
            NativeTransferImportError.None);
    }

    private static (NativeTransferPreparedImport? Plan, NativeTransferImportError Error)
        BuildMerge(NativeMediaMergePlan existing)
    {
        var counts = existing.Representation?.Counts;
        // Versioned counts describe actual additions, not the Full inventory totals
        // (which can include retained context and inactive conflict alternatives).
        return (new(NativeTransferImportScope.Merge,
            new(existing.Resource, true, counts?.NewPosts ?? existing.AddedPosts.Length,
                counts?.UpdatedPosts ?? existing.UpdatedPosts.Length,
                counts?.NewAttachments ?? existing.NewAttachmentCount, counts?.Conflicts ?? existing.Conflicts,
                EvidenceAdditions: counts?.EvidenceAdditions ?? 0,
                DerivedDescriptorAdditions: counts?.DerivedDescriptorAdditions ?? 0,
                ContainedDerivedAdditions: counts?.ContainedDerivedAdditions ?? 0), null, existing),
            NativeTransferImportError.None);
    }

    private static NativeTransferImportError MapImportError(NativeMediaImportError error) => error switch
    {
        NativeMediaImportError.ResourceExists => NativeTransferImportError.ScopeTargetMismatch,
        NativeMediaImportError.UnsupportedPackage => NativeTransferImportError.UnsupportedPackage,
        NativeMediaImportError.PrivateConsentNotIssued => NativeTransferImportError.PrivateMetadataUnsupported,
        _ => NativeTransferImportError.TargetUnavailable
    };

    private static NativeTransferImportError MapMergeError(NativeMediaMergeError error) => error switch
    {
        NativeMediaMergeError.UnsupportedPackage => NativeTransferImportError.UnsupportedPackage,
        NativeMediaMergeError.TargetBudgetExceeded => NativeTransferImportError.LimitExceeded,
        NativeMediaMergeError.MissingTarget => NativeTransferImportError.ScopeTargetMismatch,
        _ => NativeTransferImportError.TargetUnavailable
    };

    internal async Task<ProfileExclusiveOutcome<NativeTransferImportResult>> ApplyNativeImportAsync(
        NativeTransferPreparedImport prepared, CancellationToken token)
    {
        // Keep the actual reconciled receipt even if producer reactivation subsequently fails.
        // This is historical result data, never permission to resume a failed profile.
        NativeTransferImportResult? observed = null;
        bool entered = false;
        try
        {
            return await RunWithProfileProducerExclusiveAsync(async admission =>
            {
                entered = true;
                try
                {
                    if (BeforeNativeImportCommitForTests is { } before) await before().ConfigureAwait(false);
                    ProfileExclusiveDisposition disposition;
                    MediaStore store = new();
                    if (prepared.Scope == NativeTransferImportScope.New && prepared.Import is { } added)
                    {
                        var outcome = await _content.Catalog.CommitNativeMediaImportAdmittedAsync(
                            admission, added, store, token, NativeImportCheckpointForTests).ConfigureAwait(false);
                        var result = outcome.Result;
                        observed = new(result.Status, result.NewThreads, result.NewPosts, 0,
                            result.NewAttachments, 0, result.IsReplay,
                            EvidenceAdditions: result.EvidenceAdditions,
                            DerivedDescriptorAdditions: result.DerivedDescriptorAdditions,
                            ContainedDerivedAdditions: result.ContainedDerivedAdditions);
                        disposition = outcome.Disposition;
                    }
                    else if (prepared.Scope == NativeTransferImportScope.Merge && prepared.Merge is { } existing)
                    {
                        var outcome = await _content.Catalog.CommitNativeMediaMergeAdmittedAsync(
                            admission, existing, store, token, checkpoint: NativeImportCheckpointForTests).ConfigureAwait(false);
                        var result = outcome.Result;
                        observed = new(result.Status, 0, result.NewPosts, result.UpdatedPosts,
                            result.NewAttachments, result.Conflicts, result.IsReplay,
                            EvidenceAdditions: result.EvidenceAdditions,
                            DerivedDescriptorAdditions: result.DerivedDescriptorAdditions,
                            ContainedDerivedAdditions: result.ContainedDerivedAdditions);
                        disposition = outcome.Disposition;
                    }
                    else throw new InvalidOperationException("The native scope and plan kind do not match.");
                    // Preserve the core consumer's disposition, independently of its status.
                    return disposition == ProfileExclusiveDisposition.SafeToResume
                        ? ProfileExclusiveOutcome<NativeTransferImportResult>.SafeToResume(observed)
                        : ProfileExclusiveOutcome<NativeTransferImportResult>.RecoveryRequired(observed);
                }
                catch (Exception)
                {
                    // Classify before the Registry sees an exception: its recovery
                    // participant publishes error.Message. Raw I/O paths must never
                    // reach that public projection, nor imply a safe resumption.
                    return ProfileExclusiveOutcome<NativeTransferImportResult>.RecoveryRequired(
                        observed ?? new(NativeMediaImportCommitStatus.Unknown, 0, 0, 0, 0, 0, false));
                }
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!entered)
        {
            // No consumer ran. The Registry still owns transition cleanup/resume;
            // propagate cancellation, not a fabricated SafeToResume attestation.
            throw;
        }
        catch (Exception) when (entered)
        {
            RequireProfileRecovery(new InvalidOperationException("Native transfer requires recovery; profile writers remain suspended."));
            return ProfileExclusiveOutcome<NativeTransferImportResult>.RecoveryRequired(
                observed ?? new(NativeMediaImportCommitStatus.Unknown, 0, 0, 0, 0, 0, false));
        }
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Desktop;

internal sealed record NativeTransferPreparedImport(NativeTransferImportPlanSummary Summary,
    NativeMediaImportPlan? Import, NativeMediaMergePlan? Merge);

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

    internal async Task<(NativeTransferPreparedImport? Plan, NativeTransferImportError Error)>
        PrepareNativeImportAsync(NativeTransferImportStage stage, CancellationToken token)
    {
        EnsureReady();
        // Merge has the broader retained-binding grammar. Do not let the narrower
        // new-thread validator reject a package before checking its existing target.
        NativeMediaMergePreparation merge = await _content.Catalog.PrepareNativeMediaMergeAsync(stage, token).ConfigureAwait(false);
        if (merge.Plan is { } existing)
            return (new(new(existing.Resource, true, existing.AddedPosts.Length, existing.UpdatedPosts.Length,
                existing.NewAttachmentCount, existing.Conflicts), null, existing), NativeTransferImportError.None);
        if (merge.Error != NativeMediaMergeError.MissingTarget)
            return (null, merge.Error == NativeMediaMergeError.UnsupportedPackage
                ? NativeTransferImportError.UnsupportedPackage : NativeTransferImportError.TargetUnavailable);
        NativeMediaImportPreparation prepared = await _content.Catalog.PrepareNativeMediaImportAsync(stage, token).ConfigureAwait(false);
        if (prepared.Plan is { } added)
            return (new(new(added.Resource, false, added.Thread.Posts.Length, 0, added.Attachments.Length, 0), added, null), NativeTransferImportError.None);
        return (null, prepared.Error == NativeMediaImportError.ResourceExists
            ? NativeTransferImportError.TargetUnavailable : NativeTransferImportError.UnsupportedPackage);
    }

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
                    if (prepared.Import is { } added)
                    {
                        var outcome = await _content.Catalog.CommitNativeMediaImportAdmittedAsync(
                            admission, added, store, token, NativeImportCheckpointForTests).ConfigureAwait(false);
                        var result = outcome.Result;
                        observed = new(result.Status, result.NewThreads, result.NewPosts, 0,
                            result.NewAttachments, 0, result.IsReplay);
                        disposition = outcome.Disposition;
                    }
                    else if (prepared.Merge is { } existing)
                    {
                        var outcome = await _content.Catalog.CommitNativeMediaMergeAdmittedAsync(
                            admission, existing, store, token, checkpoint: NativeImportCheckpointForTests).ConfigureAwait(false);
                        var result = outcome.Result;
                        observed = new(result.Status, 0, result.NewPosts, result.UpdatedPosts,
                            result.NewAttachments, result.Conflicts, result.IsReplay);
                        disposition = outcome.Disposition;
                    }
                    else throw new InvalidOperationException("No owned native import plan.");
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

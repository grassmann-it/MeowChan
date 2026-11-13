using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginSdk;

namespace MeowChan;

internal enum PluginApprovalKind { Repair, Reauthorization, Reselection, ChangeReview }
internal enum PluginApprovalState { Saved, NotExecuted, NeedsReview, Failed, CheckStore }
internal sealed record PluginApprovalChoice(PluginApprovalKind Kind, long Revision,
    BundledCandidateRepair? Repair = null, BundledCandidateReselection? Selection = null,
    PluginConfiguration? ChangeConfiguration = null, bool AcknowledgeChanges = false, bool AuthorizePermissions = false)
{
    internal string PluginId => Repair?.PluginId ?? Selection!.PluginId;
}
internal sealed record PluginApprovalReceipt(PluginApprovalChoice Choice, PluginApprovalState State, long? Revision = null);

/// <summary>One dialog, not a transaction or a general consent rebase. No activation APIs.</summary>
internal sealed class PluginMultiApproval(IPluginProfileStore store)
{
    private readonly List<PluginApprovalReceipt> _receipts = new();
    private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);
    private volatile bool _stop;
    internal IReadOnlyList<PluginApprovalReceipt> Receipts => _receipts;
    internal Task? InFlight { get; private set; }
    internal bool Saving => InFlight is { IsCompleted: false };
    internal string? IssueKey { get; private set; }
    internal bool MayOffer(string pluginId) => !_consumed.Contains(pluginId);
    internal void Stop() => _stop = true;

    /// <summary>
    /// Keys that report a problem with the frozen selection or the store. Everything else this dialog
    /// shows (progress, refreshed review, stopping) is neutral status.
    /// </summary>
    internal static bool IsProblem(string key) => key is "plugins.approval.failed" or "plugins.approval.checkStore" or
        "plugins.approval.needsReview" or "plugins.approval.refreshFailed" or "plugins.approval.stopped";

    internal Task SubmitAsync(IReadOnlyList<PluginApprovalChoice> selected, Func<bool> admit,
        CancellationToken token = default)
    {
        if (Saving) return InFlight!;
        // Take an owned, immutable-by-caller list before any await. No newly checked rows join it.
        // Own the nested settings values before any asynchronous boundary. The dialog can keep
        // editing its draft while the writer rechecks the exact frozen row.
        PluginApprovalChoice[] frozen = selected.Select(CloneChoice).ToArray();
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        InFlight = completion.Task; // Publish the lock even when a store completes synchronously.
        _ = CompleteAsync();
        return completion.Task;

        async Task CompleteAsync()
        {
            try { await SaveAsync(frozen, admit, token); }
            catch (Exception) { IssueKey = "plugins.approval.failed"; } // Observe even unexpected adapter failures.
            finally { completion.SetResult(); }
        }
    }

    private async Task SaveAsync(PluginApprovalChoice[] frozen, Func<bool> admit, CancellationToken token)
    {
        IssueKey = null;
        int start = _receipts.Count;
        _receipts.AddRange(frozen.Select(choice => new PluginApprovalReceipt(choice, PluginApprovalState.NotExecuted)));
        if (frozen.Length == 0) return;
        long expected = frozen[0].Revision;
        if (frozen.Any(choice => choice.Revision != expected || !MayOffer(choice.PluginId) || !Eligible(choice)) ||
            frozen.Select(choice => choice.PluginId).Distinct(StringComparer.Ordinal).Count() != frozen.Length ||
            (frozen.Any(choice => choice.Kind == PluginApprovalKind.ChangeReview) &&
                (frozen.Length != 1 || frozen[0].Kind != PluginApprovalKind.ChangeReview)) ||
            (frozen.Length > 1 && frozen.Any(choice => choice.Kind != PluginApprovalKind.Reauthorization)))
        { IssueKey = "plugins.approval.needsReview"; return; }

        for (int i = 0; i < frozen.Length; i++)
        {
            PluginApprovalChoice choice = frozen[i];
            bool dispatched = false;
            try
            {
                if (_stop || token.IsCancellationRequested || !admit())
                { IssueKey = "plugins.approval.stopped"; return; }
                bool same;
                if (choice.Kind == PluginApprovalKind.Reselection)
                {
                    var preview = await store.PreviewBundledReselectionsAsync(token);
                    var matches = preview.Rows.Where(row => row.PluginId == choice.PluginId &&
                        row.OldCandidateId == choice.Selection!.OldCandidateId).ToArray();
                    same = preview.ProfileRevision == expected && matches.Length == 1 &&
                        matches[0] == choice.Selection && matches[0].CanConfirm;
                }
                else
                {
                    var preview = await store.PreviewBundledRepairsAsync(token);
                    var matches = preview.Rows.Where(row => row.PluginId == choice.PluginId &&
                        row.OldCandidateId == choice.Repair!.OldCandidateId).ToArray();
                    // Record value equality includes every identity, path, execution, blocker and rights string.
                    same = preview.ProfileRevision == expected && matches.Length == 1 && matches[0] == choice.Repair &&
                        (choice.Kind == PluginApprovalKind.Reauthorization ? matches[0].CanReauthorize : matches[0].CanConfirm);
                }
                // A stop or cancellation during the preview read wins over its result: nothing was dispatched.
                if (_stop || token.IsCancellationRequested || !admit())
                { IssueKey = "plugins.approval.stopped"; return; }
                if (!same)
                {
                    _receipts[start + i] = new(choice, PluginApprovalState.NeedsReview);
                    IssueKey = "plugins.approval.needsReview";
                    return; // In particular: never adopt a foreign preview revision.
                }
                dispatched = true;
                var result = await (choice.Kind switch
                {
                    PluginApprovalKind.Reauthorization => store.ReauthorizeBundledRepairAsync(choice.Repair!, expected, token),
                    PluginApprovalKind.Repair => store.ConfirmBundledRepairAsync(choice.Repair!, expected, token),
                    PluginApprovalKind.ChangeReview => store.ConfirmBundledChangeAsync(choice.Repair!, choice.ChangeConfiguration!,
                        choice.AcknowledgeChanges, choice.AuthorizePermissions, expected, token),
                    _ => store.ConfirmBundledReselectionAsync(choice.Selection!, expected, token)
                });
                _consumed.Add(choice.PluginId);
                _receipts[start + i] = new(choice, PluginApprovalState.Saved, result.Revision);
                if (result.Revision <= expected) { IssueKey = "plugins.approval.needsReview"; return; }
                expected = result.Revision; // ONLY the return revision of our immediately preceding successful write.
            }
            catch (PluginProfileConflictException)
            {
                _receipts[start + i] = new(choice, PluginApprovalState.NeedsReview);
                IssueKey = "plugins.approval.needsReview";
                return;
            }
            catch (Exception exception) when (!dispatched &&
                (exception is OperationCanceledException || _stop || token.IsCancellationRequested))
            {
                // Stopped before the write was dispatched: this row was not executed, so its receipt stays NotExecuted.
                IssueKey = "plugins.approval.stopped";
                return;
            }
            catch (Exception exception)
            {
                // IO/cancellation/unknown exceptions after dispatch do not prove that publication did not happen.
                bool uncertain = dispatched && (exception is IOException or OperationCanceledException ||
                    exception is not (InvalidOperationException or ArgumentException));
                if (uncertain) _consumed.Add(choice.PluginId); // No retry of a possibly successful write in this dialog.
                var state = uncertain ? PluginApprovalState.CheckStore : PluginApprovalState.Failed;
                _receipts[start + i] = new(choice, state);
                IssueKey = uncertain ? "plugins.approval.checkStore" : "plugins.approval.failed";
                return; // Honest partial results; no automatic retry, rollback or remaining writes.
            }
        }
    }

    private static bool Eligible(PluginApprovalChoice choice) => choice.Kind switch
    {
        PluginApprovalKind.Reauthorization => choice.Selection is null && choice.ChangeConfiguration is null && choice.Repair?.CanReauthorize == true,
        PluginApprovalKind.Repair => choice.Selection is null && choice.ChangeConfiguration is null && choice.Repair?.CanConfirm == true,
        PluginApprovalKind.Reselection => choice.Repair is null && choice.ChangeConfiguration is null && choice.Selection?.CanConfirm == true,
        PluginApprovalKind.ChangeReview => choice.Selection is null && choice.Repair?.CanReviewChanges == true &&
            choice.ChangeConfiguration is not null && choice.AcknowledgeChanges,
        _ => false
    };

    private static PluginApprovalChoice CloneChoice(PluginApprovalChoice choice)
    {
        PluginConfiguration? configuration = choice.ChangeConfiguration is { } value
            ? SdkJson.Deserialize<PluginConfiguration>(SdkJson.Serialize(value))
            : null;
        return choice with { ChangeConfiguration = configuration };
    }
}

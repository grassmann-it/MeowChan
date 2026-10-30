using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MeowChan.Interfaces;

namespace MeowChan.Desktop;

internal sealed record DiagnosticRetryTarget(DiagnosticTarget Target, bool Check, bool Media)
{
    // Display labels, URLs and current selection are never action identities.
    public ResourceId? ResourceId { get; init; }
}

internal sealed record DiagnosticRetryResult(int Targets, int Checks, int AlreadyScheduled, int Files,
    int Skipped, int Failures, IReadOnlyList<string> Errors);

internal static class DiagnosticRetryScope
{
    internal static bool Matches(DiagnosticEntry entry, string? source, string? query, bool problemsOnly)
    {
        if (source != null && entry.ResourceId?.Source != source || problemsOnly && !entry.IsProblem) return false;
        string text = query?.Trim() ?? string.Empty;
        return text.Length == 0 || new[] { entry.Resource, entry.Summary, entry.ResourceId?.Source,
            entry.ResourceId?.Board, entry.ResourceId?.Thread,
            entry.ResourceId == null ? null : entry.ResourceId.Source + " /" + entry.ResourceId.Board + "/ #" + entry.ResourceId.Thread }.Any(value => value?.Contains(text, StringComparison.OrdinalIgnoreCase) == true);
    }

    internal static bool IsCheckProblem(DiagnosticEntry entry) => entry.Kind is DiagnosticKind.Check
        or DiagnosticKind.Capture or DiagnosticKind.Selection || entry.Kind == DiagnosticKind.Media && entry.CheckAlso;

    // Capture once before awaiting/menu invocation. Several failure rows refer to
    // one watch, not individually selected files. Storage checks are a separate action.
    internal static DiagnosticRetryTarget[] Capture(IEnumerable<DiagnosticEntry> entries) => entries
        .Where(entry => entry.IsProblem && entry.Target != null && entry.Kind != DiagnosticKind.Storage)
        .GroupBy(entry => entry.Target!)
        // Inconsistent identity projections fail closed, rather than choosing the first source.
        .Where(group => group.First().ResourceId != null
            && group.Select(entry => entry.ResourceId).Distinct().Count() == 1)
        .Select(group => new DiagnosticRetryTarget(group.Key,
            group.Any(IsCheckProblem),
            group.Key.Kind == DiagnosticTargetKind.Thread && group.Any(entry => entry.Kind == DiagnosticKind.Media))
            { ResourceId = group.First().ResourceId })
        .Where(item => item.Check || item.Media)
        .ToArray();

    // Sequential, finite executor shared by UI and deterministic async cases. It
    // owns a defensive copy before any await, and asks admission afresh for EACH
    // operation. The host remains the final authority after its own queue/await.
    internal static async Task<DiagnosticRetryResult> ExecuteAsync(IEnumerable<DiagnosticRetryTarget> targets,
        Func<DiagnosticRetryTarget, bool, bool> canSubmit,
        Func<DiagnosticTarget, Task<CheckRequestStatus>>? check,
        Func<DiagnosticTarget, Task<int>>? media)
    {
        DiagnosticRetryTarget[] scope = targets.ToArray();
        int checks = 0, already = 0, files = 0, skipped = 0, failures = 0;
        List<string> errors = new();
        bool canceled = false;
        void Failed(DiagnosticTarget target, Exception error)
        {
            failures++;
            if (errors.Count < 5) errors.Add(target.Kind + " #" + target.WatchId + ": "
                + error.Message[..Math.Min(300, error.Message.Length)]);
            // Cancellation terminates this batch's intent, not just one operation.
            if (error is OperationCanceledException) canceled = true;
        }
        foreach (DiagnosticRetryTarget item in scope)
        {
            if (item.Media)
            {
                try
                {
                    if (canceled || media == null || !canSubmit(item, true)) skipped++;
                    else
                    {
                        int accepted = await media(item.Target);
                        files += Math.Max(0, accepted);
                        if (accepted <= 0) skipped++;
                    }
                }
                catch (Exception error) { Failed(item.Target, error); }
            }
            if (item.Check)
            {
                try
                {
                    if (canceled || check == null || !canSubmit(item, false)) skipped++;
                    else
                    {
                        CheckRequestStatus result = await check(item.Target);
                        if (result == CheckRequestStatus.Scheduled) checks++;
                        else if (result == CheckRequestStatus.AlreadyScheduled) already++;
                        else skipped++;
                    }
                }
                catch (Exception error) { Failed(item.Target, error); }
            }
        }
        return new(scope.Length, checks, already, files, skipped, failures, errors.AsReadOnly());
    }
}

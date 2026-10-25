using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;

namespace MeowChan.Desktop;

public sealed record WorkStatusPresentation(string Status, string Progress, string Details, double Completion, bool CanRetry)
{
    public string ExceptionHint { get; init; } = string.Empty;
    public string IssueDetails { get; init; } = string.Empty;
    public string ContextDetails { get; init; } = string.Empty;
}

/// <summary>Passive, shared projection of authoritative work; displaying it never schedules work.</summary>
public static class WorkStatusFormatter
{
    public static WorkStatusPresentation Format(ThreadWorkSnapshot work, bool isPaused = false)
    {
        ArgumentNullException.ThrowIfNull(work);
        MediaWork[] missing = work.Media.Where(item => item.Status != MediaWorkStatus.Succeeded).ToArray();
        bool intervention = work.CapturePending || missing.Any(item => item.Status == MediaWorkStatus.NeedsIntervention);
        string key = work.Intent switch
        {
            WorkIntent.Suppressed => "work.stopped",
            WorkIntent.Completed => "work.completed",
            WorkIntent.Repairing => "work.repairing",
            _ when intervention => "work.intervention",
            _ when work.Source != ThreadSourceStateValue.Active => "work.runout",
            _ => "work.watching"
        };
        string status = Texts.T(key);
        if (isPaused && work.Intent is WorkIntent.Watching or WorkIntent.Repairing)
            status += " · " + Texts.T("work.paused");
        string progress = Texts.T("work.saved", work.SavedMedia, work.Media.Count);
        List<string> details = new();
        List<string> exceptions = new();
        List<string> issues = new();
        List<string> context = new();
        if (work.Source != ThreadSourceStateValue.Active)
        {
            string source = Texts.T(work.Source == ThreadSourceStateValue.Archived ? "work.sourceArchived" : "work.sourceEnded");
            details.Add(source); context.Add(source);
        }
        foreach (IGrouping<(MediaWorkStatus Status, int? HttpStatus), MediaWork> group in missing
                     .GroupBy(item => (item.Status, item.HttpStatus)))
        {
            string cause = group.Key.HttpStatus is int code ? code.ToString(CultureInfo.InvariantCulture)
                : Texts.T(group.Key.Status == MediaWorkStatus.Ready ? "work.transferPending" : "work.transferFailure");
            string state = group.Key.Status switch
            {
                MediaWorkStatus.Waiting => "work.waitingCount",
                MediaWorkStatus.NeedsIntervention => "work.interventionCount",
                MediaWorkStatus.Expired => "work.missingCount",
                _ => "work.pendingCount"
            };
            string summary = Texts.T(state, group.Count(), cause);
            progress += " · " + summary;
            if (group.Key.Status is MediaWorkStatus.Waiting or MediaWorkStatus.NeedsIntervention or MediaWorkStatus.Expired)
            {
                exceptions.Add(summary); issues.Add(summary);
            }
        }
        if (work.NextDueAt is DateTimeOffset due)
        {
            string retry = Texts.T("work.nextDue", due.ToLocalTime().ToString("g", Texts.Culture));
            details.Add(retry); context.Add(retry);
        }
        if (work.Deadline is DateTimeOffset deadline)
        {
            string limit = Texts.T("work.deadline", deadline.ToLocalTime().ToString("g", Texts.Culture));
            details.Add(limit); context.Add(limit);
        }
        if (work.CapturePending)
        {
            string capture = Texts.T("work.capturePending");
            details.Add(capture); issues.Add(capture);
        }
        foreach (MediaWork item in missing.Take(5))
        {
            string name = string.IsNullOrWhiteSpace(item.OriginalFileName) ? Texts.T("work.unnamedMedia") : item.OriginalFileName;
            string cause = item.HttpStatus is int code ? code.ToString(CultureInfo.InvariantCulture)
                : Texts.T(item.Status == MediaWorkStatus.Ready && item.Failure == null ? "work.transferPending" : "work.transferFailure");
            string detail = Texts.T("work.mediaDetail", name, cause, item.Attempts);
            details.Add(detail);
            if (item.Status is MediaWorkStatus.Waiting or MediaWorkStatus.NeedsIntervention or MediaWorkStatus.Expired
                || !string.IsNullOrWhiteSpace(item.Failure)) issues.Add(detail);
            if (!string.IsNullOrWhiteSpace(item.Failure))
            {
                details.Add(item.Failure); issues.Add(item.Failure);
            }
            if (item.DueAt is DateTimeOffset mediaDue)
            {
                string retry = Texts.T("work.nextDue", mediaDue.ToLocalTime().ToString("g", Texts.Culture));
                details.Add(retry); context.Add(retry);
            }
        }
        double completion = work.Media.Count == 0 ? 0 : 100d * work.SavedMedia / work.Media.Count;
        bool canRetry = work.Intent != WorkIntent.Suppressed && missing.Any(item => !item.ManualAttempt);
        return new WorkStatusPresentation(status, progress, string.Join(Environment.NewLine, details), completion, canRetry)
        {
            ExceptionHint = string.Join(" · ", exceptions),
            IssueDetails = string.Join(Environment.NewLine, issues.Distinct()),
            ContextDetails = string.Join(Environment.NewLine, context.Distinct())
        };
    }
}

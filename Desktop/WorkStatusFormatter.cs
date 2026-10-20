using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;

namespace MeowChan.Desktop;

public sealed record WorkStatusPresentation(string Status, string Progress, string Details, double Completion, bool CanRetry);

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
            progress += " · " + Texts.T(state, group.Count(), cause);
        }
        if (work.NextDueAt is DateTimeOffset due)
            details.Add(Texts.T("work.nextDue", due.ToLocalTime().ToString("g", Texts.Culture)));
        if (work.Deadline is DateTimeOffset deadline)
            details.Add(Texts.T("work.deadline", deadline.ToLocalTime().ToString("g", Texts.Culture)));
        if (work.CapturePending) details.Add(Texts.T("work.capturePending"));
        foreach (MediaWork item in missing.Take(5))
        {
            string name = string.IsNullOrWhiteSpace(item.OriginalFileName) ? Texts.T("work.unnamedMedia") : item.OriginalFileName;
            string cause = item.HttpStatus is int code ? code.ToString(CultureInfo.InvariantCulture)
                : Texts.T(item.Status == MediaWorkStatus.Ready && item.Failure == null ? "work.transferPending" : "work.transferFailure");
            details.Add(Texts.T("work.mediaDetail", name, cause, item.Attempts));
            if (!string.IsNullOrWhiteSpace(item.Failure)) details.Add(item.Failure);
            if (item.DueAt is DateTimeOffset mediaDue)
                details.Add(Texts.T("work.nextDue", mediaDue.ToLocalTime().ToString("g", Texts.Culture)));
        }
        double completion = work.Media.Count == 0 ? 0 : 100d * work.SavedMedia / work.Media.Count;
        bool canRetry = work.Intent != WorkIntent.Suppressed && missing.Any(item => !item.ManualAttempt);
        return new WorkStatusPresentation(status, progress, string.Join(Environment.NewLine, details), completion, canRetry);
    }
}

using System.Collections.Generic;
using System.Linq;
using MeowChan.Persistence;

namespace MeowChan.Desktop;

/// <summary>Why "retry missing media" scheduled nothing, judged from the local work snapshot of the watch.</summary>
internal enum RetryNoneReason { Removed, Stopped, NothingMissing, AllQueued, Unavailable }

internal static class RetryNoneClassifier
{
    internal static RetryNoneReason Classify(ThreadWorkSnapshot? work, bool watchExists)
    {
        if (!watchExists) return RetryNoneReason.Removed;
        if (work == null) return RetryNoneReason.NothingMissing;
        if (work.Intent == WorkIntent.Suppressed) return RetryNoneReason.Stopped;
        MediaWork[] missing = work.Media.Where(item => item.Status is not (MediaWorkStatus.Succeeded or MediaWorkStatus.Excluded)).ToArray();
        if (missing.Length == 0) return RetryNoneReason.NothingMissing;
        // Missing media exist but the host declined: either every one already has its manual attempt,
        // or the watch is not active in this session.
        return missing.All(item => item.ManualAttempt) ? RetryNoneReason.AllQueued : RetryNoneReason.Unavailable;
    }

    /// <summary>The most actionable reason across several targets (a blocked one outranks "nothing to do").</summary>
    internal static RetryNoneReason Combine(IEnumerable<RetryNoneReason> reasons) =>
        reasons.DefaultIfEmpty(RetryNoneReason.Removed).Max();

    internal static string KeyFor(RetryNoneReason reason) => reason switch
    {
        RetryNoneReason.Removed => "work.retryNoneRemoved",
        RetryNoneReason.Stopped => "work.retryNoneStopped",
        RetryNoneReason.NothingMissing => "work.retryNoneComplete",
        RetryNoneReason.AllQueued => "work.retryNoneQueued",
        _ => "work.retryNoneUnavailable"
    };
}

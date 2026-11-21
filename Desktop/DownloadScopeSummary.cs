using System.Collections.Generic;
using System.Linq;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;

namespace MeowChan.Desktop;

/// <summary>
/// Honest numbers for what a media retry over a set of threads will and will not touch (P6). It reads the local work snapshots only,
/// uses the same predicate as the host (<c>RetryMissingMediaAdmittedCoreAsync</c>) and never counts finished files as work.
/// </summary>
internal readonly record struct DownloadScopeSummary(int Threads, int Media, int Saved, int Retriable, int AlreadyQueued, int Excluded)
{
    internal static DownloadScopeSummary Compute(IEnumerable<ThreadWorkSnapshot?> works)
    {
        int threads = 0, media = 0, saved = 0, retriable = 0, queued = 0, excluded = 0;
        foreach (ThreadWorkSnapshot? work in works)
        {
            if (work == null) continue;
            threads++;
            foreach (MediaWork item in work.Media)
            {
                media++;
                if (item.Status == MediaWorkStatus.Succeeded) saved++;
                else if (item.Status == MediaWorkStatus.Excluded) excluded++;
                else if (work.Intent == WorkIntent.Suppressed) continue;
                else if (item.ManualAttempt) queued++;
                else retriable++;
            }
        }
        return new(threads, media, saved, retriable, queued, excluded);
    }

    /// <summary>One line for a notification: the retry scope, then what stays untouched.</summary>
    internal string Describe() => Texts.Count("work.scopeSummary", Retriable, Retriable, Threads, Saved, Media, AlreadyQueued);
}

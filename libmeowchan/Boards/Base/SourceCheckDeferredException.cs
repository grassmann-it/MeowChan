using System;

namespace MeowChan.Boards.Base;

/// <summary>Normal source admission wait, not a failed observation or a successful check.
/// The provider remains responsible for enforcing its request limits on resumption.</summary>
internal sealed class SourceCheckDeferredException(DateTimeOffset retryAt, string reasonCode, bool initialNoSend, Exception? failure = null)
    : Exception($"Source check deferred until {retryAt:O}: {reasonCode}.")
{
    internal bool InitialNoSend { get; } = initialNoSend;
    internal Exception? Failure { get; } = failure;
    public DateTimeOffset RetryAt { get; } = retryAt;
    public string ReasonCode { get; } = reasonCode;
    // Malformed/past deadlines must not create a synchronous retry spin.
    internal TimeSpan Remaining(DateTimeOffset now)
    {
        TimeSpan remaining = RetryAt - now;
        if (remaining <= TimeSpan.Zero) return TimeSpan.FromSeconds(1);
        return remaining < TimeSpan.FromMilliseconds(10) ? TimeSpan.FromMilliseconds(10) : remaining;
    }
}

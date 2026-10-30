namespace MeowChan.Downloader;

// Separate from DownloadPriority (Manual/BoardDiscovery acquisition origin).
internal enum UserDownloadPriority { Normal, High }

/// <summary>Pure whole-file dispatch policy; caller supplies only runnable candidates.</summary>
internal static class UserPriorityPolicy
{
    // Technical V1 weight, not a throughput/byte-share promise. Across any four
    // contended reservations Normal gets at least one; idle capacity is never held.
    internal const int HighBurst = 3;
    internal static UserDownloadPriority? Preferred(bool highEligible, bool normalEligible, int highStreak)
        => highEligible ? normalEligible && highStreak >= HighBurst
            ? UserDownloadPriority.Normal : UserDownloadPriority.High
            : normalEligible ? UserDownloadPriority.Normal : null;

    internal static int Advance(int highStreak, UserDownloadPriority selected)
        => selected == UserDownloadPriority.Normal ? 0 : System.Math.Min(HighBurst, highStreak + 1);

    // LastDispatch precedes origin: even continuously arriving Manual work must
    // not starve a Discovery thread in the same user group. Origin breaks ties.
    internal static bool Better(long dispatch, bool preferredOrigin, long chosenDispatch, bool chosenPreferredOrigin)
        => dispatch < chosenDispatch || dispatch == chosenDispatch && preferredOrigin && !chosenPreferredOrigin;
}

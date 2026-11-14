using MeowChan.Desktop.Stats;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// A reset observed after the UI's asynchronous acknowledgement. Window sampling remains active: new idle samples are
/// valid, but no old counts, rates or nonzero history may survive. Shared with the deterministic clock/contract tests.
/// </summary>
internal static class StatisticsResetContract
{
    internal static bool IsCleared(IoStatsSnapshot snapshot) => snapshot.Sources.Count == 0
        && snapshot.Total.Session.IsZero && snapshot.Total.Today.IsZero && snapshot.Total.Retained.IsZero
        && snapshot.Rate == IoRates.Zero && snapshot.Total.Rate == IoRates.Zero
        && new[] { snapshot.Series.NetDown, snapshot.Series.NetUp, snapshot.Series.DiskWrite,
            snapshot.Series.DiskRead, snapshot.Series.Requests }.All(series => series.All(value => value == 0));
}

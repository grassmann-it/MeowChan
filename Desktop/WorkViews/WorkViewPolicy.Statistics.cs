using System.Collections.Generic;

namespace MeowChan.Desktop.WorkViews;

// Plan AP5 §1.12: statistics page. Owned by WV7; no rows yet (the page ignores the work view today).
public static partial class WorkViewPolicy
{
    private static IEnumerable<WorkViewEntry> StatisticsEntries() => [];
}

using System.Collections.Generic;
using static MeowChan.Desktop.WorkViews.Presence;

namespace MeowChan.Desktop.WorkViews;

// Plan AP5 §1.12: statistics page. Owned by WV7. "The statistics page always shows all rows" (IoStatsDisplay): now, history and
// the sources table are the same in every view; only the destructive, rarely used Reset moves under More.
// The history graphs themselves (header graph options) are another package's; this page only registers its sections.
public static partial class WorkViewPolicy
{
    private static IEnumerable<WorkViewEntry> StatisticsEntries() =>
    [
        Same("statistics.now", WorkViewPage.Statistics, WorkViewKind.Element, Required),
        Same("statistics.history", WorkViewPage.Statistics, WorkViewKind.Element, Required),
        Same("statistics.sources", WorkViewPage.Statistics, WorkViewKind.Element, Required),
        // Destructive and rare: under More in Barebones and Simple, direct in Advanced. The confirmation question and the
        // load/save warnings are state and stay visible whenever they apply (R5).
        new("statistics.reset", WorkViewPage.Statistics, WorkViewKind.Action, Overflow, Overflow, Direct),
        Same("statistics.reset-confirm", WorkViewPage.Statistics, WorkViewKind.Element, Required, safety: true),
        Same("statistics.load-notice", WorkViewPage.Statistics, WorkViewKind.Element, Required, safety: true),
        Same("statistics.persist-error", WorkViewPage.Statistics, WorkViewKind.Element, Required, safety: true),
    ];
}

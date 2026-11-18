using System.Collections.Generic;
using static MeowChan.Desktop.WorkViews.Presence;

namespace MeowChan.Desktop.WorkViews;

// Status page (Diagnostics and Statistics merged, SM2): a thin container. Its own rows are the shared status line and the
// segment bar; everything inside a segment stays a row of WorkViewPolicy.Diagnostics / WorkViewPolicy.Statistics.
public static partial class WorkViewPolicy
{
    private static IEnumerable<WorkViewEntry> StatusEntries() =>
    [
        // Active, waiting, problems and the current rates: data, the same in every view (R4).
        Same("status.strip", WorkViewPage.Status, WorkViewKind.Element, Required),
        // The only way between the two segments: never reduced (R5).
        Same("status.segments", WorkViewPage.Status, WorkViewKind.Element, Required, safety: true),
    ];
}

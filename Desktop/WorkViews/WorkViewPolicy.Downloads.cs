using System.Collections.Generic;
using static MeowChan.Desktop.WorkViews.Presence;

namespace MeowChan.Desktop.WorkViews;

// Plan AP5 §1.3: the Downloads page. Owned by WV1 after the foundation. Values are today's behaviour (WV0 ports, no change).
public static partial class WorkViewPolicy
{
    private const string Threads = "ThreadsDataGrid";

    private static IEnumerable<WorkViewEntry> DownloadsEntries() =>
    [
        Same("downloads.intake.url", WorkViewPage.Downloads, WorkViewKind.Element, Required),
        // The page heading: the selected footer destination names the page where the heading is off.
        new("downloads.header.heading", WorkViewPage.Downloads, WorkViewKind.Element, Hidden, Direct, Direct,
            Alternative: "shell.footer.downloads"),
        // The URL field's own tooltip carries the hint where the line is off.
        new("downloads.intake.url-hint", WorkViewPage.Downloads, WorkViewKind.Element, Hidden, Hidden, Direct,
            Alternative: "downloads.intake.url"),
        new("downloads.toolbar.columns", WorkViewPage.Downloads, WorkViewKind.Action, Overflow, Direct, Direct,
            "table.choose-columns", OverflowOrder: 10),
        new("downloads.toolbar.copy-urls", WorkViewPage.Downloads, WorkViewKind.Action, Overflow, Direct, Direct,
            "watch.copy-all-links", OverflowOrder: 20),
        // Thread table: title and progress carry identity and state in every view (R4, RequiredDownloadIdentityToRestore).
        Same(DownloadColumnId(Threads, "Title"), WorkViewPage.Downloads, WorkViewKind.Column, Required),
        Same(DownloadColumnId(Threads, "Completion"), WorkViewPage.Downloads, WorkViewKind.Column, Required),
        new(DownloadColumnId(Threads, "SourceBoard"), WorkViewPage.Downloads, WorkViewKind.Column, Overflow, Direct, Direct),
        new(DownloadColumnId(Threads, "ThreadNumber"), WorkViewPage.Downloads, WorkViewKind.Column, Overflow, Overflow, Direct),
        new(DownloadColumnId(Threads, "Status"), WorkViewPage.Downloads, WorkViewKind.Column, Overflow, Overflow, Direct),
        new(DownloadColumnId(Threads, "Queued"), WorkViewPage.Downloads, WorkViewKind.Column, Overflow, Overflow, Direct),
        new(DownloadColumnId(Threads, "Active"), WorkViewPage.Downloads, WorkViewKind.Column, Overflow, Overflow, Direct),
        // Technical work columns: offered only in Advanced, off by default there.
        new(DownloadColumnId(Threads, "MissingMedia"), WorkViewPage.Downloads, WorkViewKind.Column, Hidden, Hidden, Overflow),
        new(DownloadColumnId(Threads, "EarliestRetry"), WorkViewPage.Downloads, WorkViewKind.Column, Hidden, Hidden, Overflow),
        Same(DownloadColumnId(Threads, "*"), WorkViewPage.Downloads, WorkViewKind.Column, Direct),
        // Board table: every column offered and on by default in every view.
        Same(DownloadColumnId("BoardsDataGrid", "*"), WorkViewPage.Downloads, WorkViewKind.Column, Direct),
    ];
}

using System.Collections.Generic;
using static MeowChan.Desktop.WorkViews.Presence;

namespace MeowChan.Desktop.WorkViews;

// Plan AP5 §1.3: the Downloads page (WV1).
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
        // List toolbar (plan §1.3). Hidden tools sit under "More" of the same bar; their shortcuts keep working (R2).
        // The bar itself appears only once the list has rows or a search runs (state, not view).
        new("downloads.toolbar.search", WorkViewPage.Downloads, WorkViewKind.Action, Overflow, Direct, Direct,
            "input.focus-download-search", OverflowOrder: 5),
        new("downloads.toolbar.columns", WorkViewPage.Downloads, WorkViewKind.Action, Overflow, Overflow, Direct,
            "table.choose-columns", OverflowOrder: 10),
        new("downloads.toolbar.copy-urls", WorkViewPage.Downloads, WorkViewKind.Action, Overflow, Overflow, Direct,
            "watch.copy-all-links", OverflowOrder: 20),
        new("downloads.toolbar.details", WorkViewPage.Downloads, WorkViewKind.Action, Overflow, Direct, Direct,
            "thread.details", OverflowOrder: 30),
        // Capture result (R4/R5): the counter line and the repair banner with the first problem are never reduced; the full
        // report (successes included) is the collapsed section, as it always was.
        Same("downloads.results.panel", WorkViewPage.Downloads, WorkViewKind.Section, Collapsed),
        Same("downloads.results.repair", WorkViewPage.Downloads, WorkViewKind.Element, Required, safety: true),
        // Inspector: it is opened on purpose, so nothing is reduced inside it except the source details (collapsed, as before).
        Same("downloads.inspector.error", WorkViewPage.Downloads, WorkViewKind.Element, Required, safety: true),
        Same("downloads.inspector.next-step", WorkViewPage.Downloads, WorkViewKind.Element, Required),
        Same("downloads.inspector.check", WorkViewPage.Downloads, WorkViewKind.Action, Required, "watch.check", safety: true),
        Same("downloads.inspector.retry-media", WorkViewPage.Downloads, WorkViewKind.Action, Required, "watch.retry-media", safety: true),
        Same("downloads.inspector.local-actions", WorkViewPage.Downloads, WorkViewKind.Element, Direct),
        Same("downloads.inspector.folder", WorkViewPage.Downloads, WorkViewKind.Action, Direct, "location.open-folder"),
        Same("downloads.inspector.browser", WorkViewPage.Downloads, WorkViewKind.Action, Direct, "location.open-source"),
        Same("downloads.inspector.remove", WorkViewPage.Downloads, WorkViewKind.Action, Direct, "watch.remove"),
        Same("downloads.inspector.source-details", WorkViewPage.Downloads, WorkViewKind.Section, Collapsed),
        // Thread table: title and progress carry identity and state in every view (R4, RequiredDownloadIdentityToRestore).
        Same(DownloadColumnId(Threads, "Title"), WorkViewPage.Downloads, WorkViewKind.Column, Required),
        Same(DownloadColumnId(Threads, "Completion"), WorkViewPage.Downloads, WorkViewKind.Column, Required),
        new(DownloadColumnId(Threads, "SourceBoard"), WorkViewPage.Downloads, WorkViewKind.Column, Overflow, Direct, Direct),
        new(DownloadColumnId(Threads, "ThreadNumber"), WorkViewPage.Downloads, WorkViewKind.Column, Overflow, Overflow, Direct),
        new(DownloadColumnId(Threads, "Status"), WorkViewPage.Downloads, WorkViewKind.Column, Overflow, Overflow, Direct),
        new(DownloadColumnId(Threads, "Queued"), WorkViewPage.Downloads, WorkViewKind.Column, Overflow, Overflow, Direct),
        new(DownloadColumnId(Threads, "Active"), WorkViewPage.Downloads, WorkViewKind.Column, Overflow, Overflow, Direct),
        // Technical work columns: offered in "Columns…" from Simple on (new), off by default in every view.
        new(DownloadColumnId(Threads, "MissingMedia"), WorkViewPage.Downloads, WorkViewKind.Column, Hidden, Overflow, Overflow),
        new(DownloadColumnId(Threads, "EarliestRetry"), WorkViewPage.Downloads, WorkViewKind.Column, Hidden, Overflow, Overflow),
        Same(DownloadColumnId(Threads, "*"), WorkViewPage.Downloads, WorkViewKind.Column, Direct),
        // Board table: every column offered and on by default in every view.
        Same(DownloadColumnId("BoardsDataGrid", "*"), WorkViewPage.Downloads, WorkViewKind.Column, Direct),
    ];
}

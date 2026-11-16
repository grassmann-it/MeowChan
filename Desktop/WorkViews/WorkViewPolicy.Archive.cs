using System.Collections.Generic;
using System.Linq;
using static MeowChan.Desktop.WorkViews.Presence;

namespace MeowChan.Desktop.WorkViews;

// Plan AP5 §1.4: archive list, filters and results bar. Owned by WV2. Columns keep today's per-view defaults (ArchivePage.ThreadColumnKeys).
public static partial class WorkViewPolicy
{
    private const string ArchiveThreads = "ArchiveThreadTable", ArchiveMedia = "ArchiveMediaGrid";

    private static IEnumerable<WorkViewEntry> ArchiveEntries() =>
    [
        // Page header: Refresh (F5) is a Simple-and-up tool; cleanup and the transfer import/export are maintenance. Under More in the same bar.
        new("archive.header.transfer-preview", WorkViewPage.Archive, WorkViewKind.Action, Overflow, Overflow, Direct, "archive.transfer-preview", OverflowOrder: 10),
        new("archive.header.transfer-export", WorkViewPage.Archive, WorkViewKind.Action, Overflow, Overflow, Direct, "archive.transfer-export", OverflowOrder: 15),
        new("archive.header.cleanup", WorkViewPage.Archive, WorkViewKind.Action, Overflow, Overflow, Direct, "archive.cleanup", OverflowOrder: 20),
        new("archive.header.refresh", WorkViewPage.Archive, WorkViewKind.Action, Overflow, Direct, Direct, "archive.refresh", OverflowOrder: 30),
        // The search field is the core task; the scope chip is only needed once the scope is not Text (R6 shows it then).
        // Without the chip the field still opens the scope menu with Alt+Down / F4.
        Same("archive.search.field", WorkViewPage.Archive, WorkViewKind.Element, Required),
        new("archive.search.scope", WorkViewPage.Archive, WorkViewKind.Element, Hidden, Direct, Direct, Alternative: "archive.search.field"),
        // Filters: the expander stays the toggle (collapsed; its shortcut works in every view); applied chips are never reduced (R4).
        Same("archive.filters.expander", WorkViewPage.Archive, WorkViewKind.Section, Collapsed),
        Same("archive.filters.applied", WorkViewPage.Archive, WorkViewKind.Element, Required),
        // Fields inside the surface. Overflow = under "More filters" in the same surface; a field with a value or an error is raised (R6).
        Same("archive.filters.source", WorkViewPage.Archive, WorkViewKind.Element, Direct),
        Same("archive.filters.board", WorkViewPage.Archive, WorkViewKind.Element, Direct),
        new("archive.filters.state", WorkViewPage.Archive, WorkViewKind.Element, Overflow, Direct, Direct),
        Same("archive.filters.tags", WorkViewPage.Archive, WorkViewKind.Element, Direct),
        new("archive.filters.tag-mode", WorkViewPage.Archive, WorkViewKind.Element, Overflow, Direct, Direct),
        new("archive.filters.capture", WorkViewPage.Archive, WorkViewKind.Element, Overflow, Overflow, Direct),
        new("archive.filters.posts", WorkViewPage.Archive, WorkViewKind.Element, Overflow, Overflow, Direct),
        new("archive.filters.work", WorkViewPage.Archive, WorkViewKind.Element, Overflow, Overflow, Direct),
        new("archive.filters.text-scope", WorkViewPage.Archive, WorkViewKind.Element, Overflow, Direct, Direct),
        Same("archive.filters.sort", WorkViewPage.Archive, WorkViewKind.Element, Direct),
        new("archive.filters.date", WorkViewPage.Archive, WorkViewKind.Element, Overflow, Direct, Direct),
        new("archive.filters.zone", WorkViewPage.Archive, WorkViewKind.Element, Overflow, Overflow, Direct),
        // The "More filters" section itself: closed whenever a view is entered; shown only while a field sits in it.
        Same("archive.filters.more", WorkViewPage.Archive, WorkViewKind.Section, Collapsed),
        // Results bar: "media of the matching threads" stays direct in every view; the pager and range line are data (P).
        Same("archive.results.bar", WorkViewPage.Archive, WorkViewKind.Element, Required),
        Same("archive.results.gallery", WorkViewPage.Archive, WorkViewKind.Action, Direct, "gallery.open-results"),
        new("archive.results.layout-list", WorkViewPage.Archive, WorkViewKind.Action, Overflow, Direct, Direct, OverflowOrder: 10),
        new("archive.results.layout-table", WorkViewPage.Archive, WorkViewKind.Action, Overflow, Direct, Direct, OverflowOrder: 20),
        new("archive.results.open-in-media", WorkViewPage.Archive, WorkViewKind.Action, Overflow, Direct, Direct, "archive.open-results-in-media", OverflowOrder: 25),
        new("archive.results.favorites", WorkViewPage.Archive, WorkViewKind.Action, Overflow, Direct, Direct, "favorites.open", OverflowOrder: 30),
        new("archive.results.columns", WorkViewPage.Archive, WorkViewKind.Action, Overflow, Overflow, Direct, "table.choose-columns", OverflowOrder: 40),
        new("archive.results.export", WorkViewPage.Archive, WorkViewKind.Action, Overflow, Overflow, Direct, "export.open-all-results-html-dialog", OverflowOrder: 50),
        new("archive.results.reader-groups", WorkViewPage.Archive, WorkViewKind.Action, Overflow, Overflow, Direct, "reader-groups.open-manager", OverflowOrder: 60),
        new("archive.results.saved-searches", WorkViewPage.Archive, WorkViewKind.Action, Overflow, Overflow, Direct, "archive.saved-searches.open-manager", OverflowOrder: 70),
        new("archive.results.bookmarks", WorkViewPage.Archive, WorkViewKind.Action, Overflow, Overflow, Direct, "post-bookmarks.open-manager", OverflowOrder: 80),
        // Every other results action, including ones added later, until it gets a row of its own.
        new("archive.results.other", WorkViewPage.Archive, WorkViewKind.Action, Overflow, Direct, Direct, OverflowOrder: 100),
        // Thread table columns (ArchivePage.ThreadColumnKeys).
        Same(ArchiveColumnId(ArchiveThreads, "identity"), WorkViewPage.Archive, WorkViewKind.Column, Direct),
        Same(ArchiveColumnId(ArchiveThreads, "title"), WorkViewPage.Archive, WorkViewKind.Column, Direct),
        Same(ArchiveColumnId(ArchiveThreads, "posts"), WorkViewPage.Archive, WorkViewKind.Column, Direct),
        Same(ArchiveColumnId(ArchiveThreads, "media"), WorkViewPage.Archive, WorkViewKind.Column, Direct),
        // A2: the favourite star has its own pinned first column, shown in every view (it used to sit in the title cell, which always showed).
        Same(ArchiveColumnId(ArchiveThreads, "favorite"), WorkViewPage.Archive, WorkViewKind.Column, Direct),
        // Relevance: shown only while the result is ranked by where the text was found (ArchivePage.Relevance).
        Same(ArchiveColumnId(ArchiveThreads, "foundIn"), WorkViewPage.Archive, WorkViewKind.Column, Direct),
        new(ArchiveColumnId(ArchiveThreads, "source"), WorkViewPage.Archive, WorkViewKind.Column, Overflow, Direct, Direct),
        new(ArchiveColumnId(ArchiveThreads, "work"), WorkViewPage.Archive, WorkViewKind.Column, Overflow, Overflow, Direct),
        new(ArchiveColumnId(ArchiveThreads, "lastCapture"), WorkViewPage.Archive, WorkViewKind.Column, Overflow, Overflow, Direct),
        new(ArchiveColumnId(ArchiveThreads, "tags"), WorkViewPage.Archive, WorkViewKind.Column, Overflow, Overflow, Direct),
        new(ArchiveColumnId(ArchiveThreads, "*"), WorkViewPage.Archive, WorkViewKind.Column, Overflow, Direct, Direct),
        // Saved-media table columns (ArchivePage.MediaColumnKeys); the name always shows.
        Same(ArchiveColumnId(ArchiveMedia, "name"), WorkViewPage.Archive, WorkViewKind.Column, Required),
        Same(ArchiveColumnId(ArchiveMedia, "size"), WorkViewPage.Archive, WorkViewKind.Column, Direct),
        new(ArchiveColumnId(ArchiveMedia, "version"), WorkViewPage.Archive, WorkViewKind.Column, Overflow, Direct, Direct),
        new(ArchiveColumnId(ArchiveMedia, "rating"), WorkViewPage.Archive, WorkViewKind.Column, Overflow, Direct, Direct),
        new(ArchiveColumnId(ArchiveMedia, "*"), WorkViewPage.Archive, WorkViewKind.Column, Overflow, Direct, Direct),
    ];

    /// <summary>
    /// The archive filter field groups in the order the surface lists them (ArchivePage.FilterGroups follows it; a lint test compares the
    /// order of the ids in ArchivePage.axaml). A method, not a field, because partial files have no defined initialization order.
    /// </summary>
    public static IReadOnlyList<string> ArchiveFilterGroupIds() =>
    [
        "archive.filters.source", "archive.filters.board", "archive.filters.state", "archive.filters.tags", "archive.filters.tag-mode",
        "archive.filters.capture", "archive.filters.posts", "archive.filters.work", "archive.filters.text-scope", "archive.filters.sort", "archive.filters.date",
        "archive.filters.zone"
    ];

    /// <summary>
    /// The groups that sit under "More filters" in <paramref name="view"/>: the ones the policy reduces, minus those whose field holds
    /// a value, an applied filter or an error (<paramref name="holdsState"/>, rule R6).
    /// </summary>
    public static IReadOnlyList<string> ArchiveFiltersUnderMore(WorkView view, System.Func<string, bool>? holdsState = null) =>
        ArchiveFilterGroupIds().Where(id => !OnSurface(Resolve(id, view, holdsState?.Invoke(id) == true))).ToArray();
}

using System.Collections.Generic;
using static MeowChan.Desktop.WorkViews.Presence;

namespace MeowChan.Desktop.WorkViews;

// Plan AP5 §1.4: archive list, filters and results bar. Owned by WV2 after the foundation. Values are today's behaviour.
public static partial class WorkViewPolicy
{
    private const string ArchiveThreads = "ArchiveThreadTable", ArchiveMedia = "ArchiveMediaGrid";

    private static IEnumerable<WorkViewEntry> ArchiveEntries() =>
    [
        // Results bar: the List/Table choice never moves; "media of the matching threads" stays direct in every view.
        Same("archive.results.layout-list", WorkViewPage.Archive, WorkViewKind.Action, Required),
        Same("archive.results.layout-table", WorkViewPage.Archive, WorkViewKind.Action, Required),
        Same("archive.results.gallery", WorkViewPage.Archive, WorkViewKind.Action, Direct, "gallery.open-results"),
        // Every other results action, including ones added later, until WV2 gives them rows of their own.
        new("archive.results.other", WorkViewPage.Archive, WorkViewKind.Action, Overflow, Direct, Direct, OverflowOrder: 100),
        // Thread table columns (ArchivePage.ThreadColumnKeys).
        Same(ArchiveColumnId(ArchiveThreads, "identity"), WorkViewPage.Archive, WorkViewKind.Column, Direct),
        Same(ArchiveColumnId(ArchiveThreads, "title"), WorkViewPage.Archive, WorkViewKind.Column, Direct),
        Same(ArchiveColumnId(ArchiveThreads, "posts"), WorkViewPage.Archive, WorkViewKind.Column, Direct),
        Same(ArchiveColumnId(ArchiveThreads, "media"), WorkViewPage.Archive, WorkViewKind.Column, Direct),
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
}

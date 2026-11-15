using System.Collections.Generic;
using static MeowChan.Desktop.WorkViews.Presence;

namespace MeowChan.Desktop.WorkViews;

// Plan AP5 §1.7: media page header, filter fields and selection bar. Owned by WV5.
// Ids are generic on purpose (a control keeps its id when the filter surface is reworked): media.search, media.sort,
// media.filter.toggle, media.toolbar.refresh, … Direct/Required rows never move; Overflow rows sit under the header's or the
// selection bar's "More" (⋯), a filter field under "More filters". A filter field whose value is not the default (an
// active filter) is shown in every view (R6), so no view hides why the grid shows what it shows (R4).
public static partial class WorkViewPolicy
{
    private static IEnumerable<WorkViewEntry> MediaEntries() =>
    [
        // Header: finding, ordering and the filter toggle (with its badge) are the page's job in every view.
        Same("media.search", WorkViewPage.Media, WorkViewKind.Element, Required, "input.focus-media-search"),
        Same("media.filter.toggle", WorkViewPage.Media, WorkViewKind.Element, Required),
        Same("media.sort", WorkViewPage.Media, WorkViewKind.Element, Required),
        // The "More" (⋯) button itself: shown whenever the view keeps something under it.
        Same("media.toolbar.more", WorkViewPage.Media, WorkViewKind.Element, Required),
        // M4: the focus mode (⤢) hides the header rows above the grid; Barebones keeps it under More, from Simple it is a button of the
        // header. The shortcut (Shift+F) is the catalog's in every view, so the entry under More forwards to the same command.
        new("media.toolbar.focus", WorkViewPage.Media, WorkViewKind.Action, Overflow, Direct, Direct, "media.focus-mode", OverflowOrder: 5),
        new("media.toolbar.refresh", WorkViewPage.Media, WorkViewKind.Action, Overflow, Direct, Direct, OverflowOrder: 10),
        new("media.toolbar.shuffle", WorkViewPage.Media, WorkViewKind.Action, Overflow, Direct, Direct, OverflowOrder: 20),
        // MP4: the tag list (the "Booru mode", beside the grid) is a switch of the header from Simple on; Barebones keeps it under More as a checked entry.
        // MP5: the switch is called "Tag list"; the "Layout ▾" menu (the same block and the other blocks, presets) follows it, under More in Barebones.
        new("media.toolbar.tag-list", WorkViewPage.Media, WorkViewKind.Action, Overflow, Direct, Direct, OverflowOrder: 3),
        new("media.toolbar.layout", WorkViewPage.Media, WorkViewKind.Action, Overflow, Direct, Direct, OverflowOrder: 4),
        // MP3: the two ways from the media page to tagging, side by side under More in every view.
        new("media.toolbar.queue-walk", WorkViewPage.Media, WorkViewKind.Action, Overflow, Overflow, Overflow, OverflowOrder: 32),
        new("media.toolbar.tagging-page", WorkViewPage.Media, WorkViewKind.Action, Overflow, Overflow, Overflow, OverflowOrder: 34),
        // QW-A #10: the mass action of the tagging way was only in the chip menu and the context menus; it is an entry under More as well.
        // It confirms above the queue limit of Settings (Storage and limits), like the chip menu entry.
        new("media.toolbar.queue-all", WorkViewPage.Media, WorkViewKind.Action, Overflow, Overflow, Overflow, "media.queue.all", OverflowOrder: 36),
        // QW-A #1: the way to the folder sources (Settings, Storage and limits, Media folders) from the page they feed.
        new("media.toolbar.folders", WorkViewPage.Media, WorkViewKind.Action, Overflow, Overflow, Overflow, "media.folders", OverflowOrder: 60),
        // The existing Booru workbench is optional; when available its launcher is appended to this same More surface.
        new("media.toolbar.booru", WorkViewPage.Media, WorkViewKind.Action, Overflow, Overflow, Overflow, OverflowOrder: 30),
        // Since S3 the header holds search, filter toggle, order and refresh; exporting every match is under More in every view
        // (showing missing files moved to the filter fields below: it is a filter, and its chip shows while it is on).
        new("media.toolbar.export-all", WorkViewPage.Media, WorkViewKind.Action, Overflow, Overflow, Overflow, OverflowOrder: 40),
        // After AP6 the saved-ratings manager lives under the media page's More in every view; its shortcut is routed by the shell.
        new("media.toolbar.manage-ratings", WorkViewPage.Media, WorkViewKind.Action, Overflow, Overflow, Overflow,
            "media.rating.manage", OverflowOrder: 50),
        // Filter fields: Barebones keeps name (above), type and favourites; Simple adds source, board, rating and tags;
        // Advanced adds sound, "without media tags" and the tag mode. The rest is under "More filters" (never lost).
        Same("media.filter.kind", WorkViewPage.Media, WorkViewKind.Element, Direct),
        Same("media.filter.favorites", WorkViewPage.Media, WorkViewKind.Element, Direct),
        new("media.filter.source", WorkViewPage.Media, WorkViewKind.Element, Overflow, Direct, Direct),
        new("media.filter.board", WorkViewPage.Media, WorkViewKind.Element, Overflow, Direct, Direct),
        new("media.filter.rating", WorkViewPage.Media, WorkViewKind.Element, Overflow, Direct, Direct),
        new("media.filter.tags", WorkViewPage.Media, WorkViewKind.Element, Overflow, Direct, Direct),
        new("media.filter.sound", WorkViewPage.Media, WorkViewKind.Element, Overflow, Overflow, Direct),
        new("media.filter.without-media-tags", WorkViewPage.Media, WorkViewKind.Element, Overflow, Overflow, Direct),
        new("media.filter.tag-mode", WorkViewPage.Media, WorkViewKind.Element, Overflow, Overflow, Direct),
        // Showing captured but missing files is a maintenance view: under "More filters" until Advanced; switched on it is a state
        // (an active filter with its chip) and stays shown in every view (R6).
        new("media.filter.missing", WorkViewPage.Media, WorkViewKind.Element, Overflow, Overflow, Direct),
        // M2: "+ Filter" at the end of the chip row opens the picker over every field. It shows with the row (while a filter is
        // active) in every view: it sits beside the chips, which no view reduces (R4/R5).
        Same("media.filter.add", WorkViewPage.Media, WorkViewKind.Element, Direct),
        // The toggle that opens the fields a view keeps back; shown while any is held back or while it is open.
        Same("media.filter.more", WorkViewPage.Media, WorkViewKind.Element, Required),
        // The "N without media tags" count is derived information; Barebones leaves it out (its filter is under More filters).
        new("media.status.untagged", WorkViewPage.Media, WorkViewKind.Element, Hidden, Direct, Direct,
            Alternative: "media.filter.without-media-tags"),
        // Selection bar: the count, the gallery and "Clear selection" are the selection itself (state, never reduced).
        Same("media.selection.status", WorkViewPage.Media, WorkViewKind.Element, Required),
        Same("media.selection.gallery", WorkViewPage.Media, WorkViewKind.Action, Required),
        Same("media.selection.clear", WorkViewPage.Media, WorkViewKind.Action, Required),
        Same("media.selection.more", WorkViewPage.Media, WorkViewKind.Element, Required),
        new("media.selection.tag", WorkViewPage.Media, WorkViewKind.Action, Overflow, Direct, Direct, OverflowOrder: 10),
        // MP2: queueing comes before tagging together, in the bar (direct from Simple on, like tagging) and under its More.
        new("media.selection.queue", WorkViewPage.Media, WorkViewKind.Action, Overflow, Direct, Direct, OverflowOrder: 5),
        new("media.selection.export", WorkViewPage.Media, WorkViewKind.Action, Overflow, Overflow, Direct, OverflowOrder: 20),
    ];
}

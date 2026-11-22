using System.Collections.Generic;
using static MeowChan.Desktop.WorkViews.Presence;

namespace MeowChan.Desktop.WorkViews;

// Plan AP5 §1.5: reader toolbar, status line, tabs and the saved-media tab. Owned by WV3.
// Owner defaults (Tom, 13./14.11.): what a view does not show sits under "More" of the same bar (never gone); posts, errors,
// active filters, the reading mark and the capture/live state are never reduced; shortcuts keep working (WV0 fallback route).
public static partial class WorkViewPolicy
{
    /// <summary>
    /// The reader toolbar's one keep-visible order (OverflowOrder, lower stays on the bar longer). The toolbar has
    /// PrioritizedOverflow, so this list, not the visual order of the bar, decides what a narrow window moves into "More"
    /// first: the tail of this list. RL1 (15.11.): gallery, read, media sidebar, thread list, find, tags, export, settings (the gear that replaced "View"),
    /// thread; the rarely used history return goes before full screen, which ends the list: it is the first to move under More
    /// when the bar runs out of room (review 15.11.: an action only overflows when it does not fit; a forced "More" with free room
    /// beside it hid actions). The post-capture switch is no bar item any more: it is an entry of the Thread menu (R2).
    /// Required items (way back, favourite star, state returns) never move.
    /// </summary>
    public static IReadOnlyList<string> ReaderToolbarKeepVisibleOrder => ReaderKeepVisible();

    // A method, not a static field: partial files have no defined field initialization order.
    private static string[] ReaderKeepVisible() =>
    [
        "reader.toolbar.gallery", "reader.toolbar.read", "reader.toolbar.media-sidebar", "reader.toolbar.thread-list", "reader.toolbar.find", "reader.toolbar.tags", "reader.toolbar.export",
        "reader.toolbar.thread", "reader.toolbar.settings", "reader.toolbar.history-back", "reader.toolbar.fullscreen"
    ];

    private static int ReaderOrder(string id) => 10 * (System.Array.IndexOf(ReaderKeepVisible(), id) + 1);

    private static WorkViewEntry ReaderTool(string id, Presence barebones, Presence simple, Presence advanced, string? commandId) =>
        new(id, WorkViewPage.Reader, WorkViewKind.Action, barebones, simple, advanced, commandId, OverflowOrder: ReaderOrder(id));

    private static IEnumerable<WorkViewEntry> ReaderEntries() =>
    [
        // ---- Way back and state navigation: Required (never under More). ----
        Same("reader.header.back", WorkViewPage.Reader, WorkViewKind.Element, Required, "archive.back-to-list"),
        Same("reader.toolbar.close", WorkViewPage.Reader, WorkViewKind.Action, Required, "reader.close"),
        // J5: the thread star is a mark, not a tool; it stays where it always was.
        Same("reader.toolbar.thread-favorite", WorkViewPage.Reader, WorkViewKind.Action, Required, "thread.favorite-toggle"),
        // The reading mark and its return appear only when there is one (state); the view never hides them.
        Same("reader.toolbar.unread-return", WorkViewPage.Reader, WorkViewKind.Action, Required),
        Same("reader.toolbar.gallery-return", WorkViewPage.Reader, WorkViewKind.Action, Required, "gallery.return"),
        Same("reader.toolbar.media-return", WorkViewPage.Reader, WorkViewKind.Action, Required, "media.return"),
        Same("reader.toolbar.skip-unavailable", WorkViewPage.Reader, WorkViewKind.Action, Required, "reader.skip-unavailable-group-member"),

        // ---- Tools, in ReaderToolbarKeepVisibleOrder. ----
        ReaderTool("reader.toolbar.gallery", Direct, Direct, Direct, "gallery.open"),
        ReaderTool("reader.toolbar.read", Direct, Direct, Direct, "reader.expand"),
        // The media sidebar toggle is a direct reader affordance in every work view; it must
        // participate in the same overflow/work-view contract as the neighboring tools.
        ReaderTool("reader.toolbar.media-sidebar", Direct, Direct, Direct, null),
        // The thread list switch (21.11.): shown only beside a wide enough list (embedded reader), so it never competes for room in a narrow window;
        // next to the media sidebar switch it is a direct reader affordance in every view and may move under More when the bar runs out.
        ReaderTool("reader.toolbar.thread-list", Direct, Direct, Direct, "archive.thread-list.toggle"),
        ReaderTool("reader.toolbar.find", Overflow, Direct, Direct, "reader.find"),
        ReaderTool("reader.toolbar.tags", Overflow, Direct, Direct, "thread.tags"),
        ReaderTool("reader.toolbar.export", Overflow, Direct, Direct, "export.open-html-dialog"),
        // R2: the thread's downloader-side actions (capture, check now, retry media, media rules, details, folder, source, link, watch);
        // a menu button, so Barebones moves it under More, where it opens the same menu. F16 (21.11.): Thread stands before the gear.
        ReaderTool("reader.toolbar.thread", Overflow, Direct, Direct, null),
        // RL1: the gear "Settings" is the former View menu and the quick settings popover in one (a popover, not a menu); under More
        // it opens the same popover, anchored at More.
        ReaderTool("reader.toolbar.settings", Overflow, Direct, Direct, null),
        // Shown only with a history (state); as a navigation it stays on the bar in every view while it fits.
        ReaderTool("reader.toolbar.history-back", Direct, Direct, Direct, "reader.history-back"),
        // Review 15.11. (Tom): with room to spare, nothing hides under More. Full screen stands at the end of the bar while it fits and
        // is the first to move under More (the end of the keep-visible order); Barebones keeps it under More. The header button and F11
        // carry it as well. RL1 had it under More in every view, which showed a "More" holding only this one action beside free room.
        ReaderTool("reader.toolbar.fullscreen", Overflow, Direct, Direct, "window.fullscreen"),

        // ---- Status line and messages: state and safety, Required in every view (R5). ----
        Same("reader.status.capture", WorkViewPage.Reader, WorkViewKind.Element, Required, safety: true),
        // R3: the text that says new posts are shown automatically; the switch is "Show new posts automatically" in the settings popover (section Thread).
        Same("reader.status.follow-live", WorkViewPage.Reader, WorkViewKind.Element, Required, "watch.follow-live", safety: true),
        Same("reader.status.new-posts", WorkViewPage.Reader, WorkViewKind.Element, Required, safety: true),
        Same("reader.status.show-new-posts", WorkViewPage.Reader, WorkViewKind.Element, Required, "reader.show-new-posts", safety: true),
        Same("reader.status.group-availability", WorkViewPage.Reader, WorkViewKind.Element, Required, safety: true),
        Same("reader.media.rating-error", WorkViewPage.Reader, WorkViewKind.Element, Required, safety: true),

        // ---- Tabs: data access (R4); three tabs cost no room. References still appears only with replies (state). ----
        Same("reader.tabs.posts", WorkViewPage.Reader, WorkViewKind.Element, Direct),
        Same("reader.tabs.media", WorkViewPage.Reader, WorkViewKind.Element, Direct),
        Same("reader.tabs.references", WorkViewPage.Reader, WorkViewKind.Element, Direct),

        // ---- Saved-media tab: tools bar (ReaderMediaTools) and actions bar (ReaderMediaActions). ----
        new("reader.media.sort", WorkViewPage.Reader, WorkViewKind.Action, Direct, Direct, Direct, OverflowOrder: 10),
        // An active favourites or rating filter, or all versions, is a non-default state and stays on the bar (R6).
        new("reader.media.favorites-filter", WorkViewPage.Reader, WorkViewKind.Action, Overflow, Direct, Direct, OverflowOrder: 20),
        new("reader.media.rating-filter", WorkViewPage.Reader, WorkViewKind.Action, Overflow, Direct, Direct, OverflowOrder: 30),
        new("reader.media.layout-tiles", WorkViewPage.Reader, WorkViewKind.Action, Overflow, Direct, Direct, OverflowOrder: 40),
        new("reader.media.layout-table", WorkViewPage.Reader, WorkViewKind.Action, Overflow, Direct, Direct, OverflowOrder: 45),
        new("reader.media.version-scope", WorkViewPage.Reader, WorkViewKind.Action, Overflow, Overflow, Direct, OverflowOrder: 50),
        new("reader.media.columns", WorkViewPage.Reader, WorkViewKind.Action, Overflow, Overflow, Direct, "table.choose-columns", OverflowOrder: 60),
        new("reader.media.edit-rating", WorkViewPage.Reader, WorkViewKind.Action, Overflow, Direct, Direct, "media.rating.edit", OverflowOrder: 10),
        new("reader.media.preview", WorkViewPage.Reader, WorkViewKind.Action, Overflow, Direct, Direct, OverflowOrder: 20),
        new("reader.media.manage-ratings", WorkViewPage.Reader, WorkViewKind.Action, Overflow, Overflow, Direct, "media.rating.manage", OverflowOrder: 30),
    ];
}

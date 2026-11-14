using System.Collections.Generic;
using System.Linq;
using static MeowChan.Desktop.WorkViews.Presence;

namespace MeowChan.Desktop.WorkViews;

// Plan AP5 §1.6: the gallery viewer (GalleryViewer.WorkView.cs). Owned by WV4.
// The gallery has no ActionToolBar: its toolbar is a one-row strip of tool groups and "More" (⋯) is its own flyout of rows.
// A *movable tool* is therefore one control that the view puts either on the bar (Direct) or into the flyout (Overflow); the
// viewer moves the very same instance (commands, enabled state, tooltip and shortcut exist once). Barebones is the reduced
// layout the gallery always had; Simple and Advanced bring the curating tools onto the bar.
//
// Owner design rule (Tom, 14.11.): the way back stands alone at the top right edge, the favourite star sits directly left of
// it, every other action is its own group away from both. Never reduced: the way back, the favourite mark, the position,
// the video transport, errors, integrity warnings, the consent to remove a favourite and the way to the post panel.
//
// Deliberately NOT moved (documented deviations from the plan's matrix, see the WV4 report): the post panel toggle stays on
// the bar in every view (46 smoke call sites and the later tag panel slot rely on it), and the overlay switches, "Export
// original…", "Save frame…" and zoom steps stay rows of the flyout in every view (they are settings-like or already have a
// context-menu twin); the open-external and folder actions belong to the file-only plate and the media context menu.
public static partial class WorkViewPolicy
{
    /// <summary>
    /// The movable tools in keep-visible order (OverflowOrder): also the order they appear on the bar and in the flyout.
    /// A method, not a static field: partial files have no defined field initialization order.
    /// </summary>
    public static IReadOnlyList<string> GalleryMovableToolIds => GalleryMovable();

    private static string[] GalleryMovable() =>
    [
        "gallery.toolbar.rating", "gallery.toolbar.tags", "gallery.toolbar.slideshow", "gallery.toolbar.slideshow-seconds",
        "gallery.toolbar.playback-mode", "gallery.toolbar.help"
    ];

    /// <summary>
    /// The movable tools a <paramref name="view"/> keeps on the gallery bar, in policy order. A chosen or running slideshow is a
    /// non-default state (R6) that keeps its toggle on the bar in every view.
    /// </summary>
    public static IReadOnlyList<string> GalleryBarToolIds(WorkView view, bool slideshowActive = false) => GalleryMovable()
        .Where(id => OnSurface(Resolve(id, view, nonDefaultState: slideshowActive && id == "gallery.toolbar.slideshow"))).ToArray();

    /// <summary>
    /// Whether the view keeps the button of <paramref name="commandId"/> under the gallery's "More" (or leaves it out), so a dialog
    /// the command opened returns focus to "More" (A19). Commands without a gallery button are never reduced.
    /// </summary>
    public static bool GalleryCommandReduced(string commandId, WorkView view) => Entries.FirstOrDefault(entry =>
        entry.Page == WorkViewPage.Gallery && entry.Kind == WorkViewKind.Action && entry.CommandId == commandId) is { } row
        && Rank(row.For(view)) < Rank(Collapsed);

    private static int GalleryOrder(string id) => 10 * (System.Array.IndexOf(GalleryMovable(), id) + 1);

    private static WorkViewEntry GalleryTool(string id, Presence barebones, Presence simple, Presence advanced, string? commandId) =>
        new(id, WorkViewPage.Gallery, WorkViewKind.Action, barebones, simple, advanced, commandId, OverflowOrder: GalleryOrder(id));

    // A flyout row no view moves (Overflow everywhere). Listed so the policy names every control of the surface.
    private static WorkViewEntry GalleryMenuRow(string id, string? commandId, int order) =>
        new(id, WorkViewPage.Gallery, WorkViewKind.Action, Overflow, Overflow, Overflow, commandId, OverflowOrder: order);

    private static IEnumerable<WorkViewEntry> GalleryEntries() =>
    [
        // ---- Way back, favourite, position, transport: Required (the core of the viewer, never reduced). ----
        Same("gallery.back", WorkViewPage.Gallery, WorkViewKind.Action, Required, "gallery.close"),
        Same("gallery.nav.previous", WorkViewPage.Gallery, WorkViewKind.Action, Required, "gallery.previous"),
        Same("gallery.nav.next", WorkViewPage.Gallery, WorkViewKind.Action, Required, "gallery.next"),
        Same("gallery.position", WorkViewPage.Gallery, WorkViewKind.Element, Required),
        Same("gallery.favorite", WorkViewPage.Gallery, WorkViewKind.Action, Required, "favorites.toggle"),
        // The passive saved/not-saved mark beside the way back; a state, so never reduced.
        Same("gallery.favorite.indicator", WorkViewPage.Gallery, WorkViewKind.Element, Required, safety: true),
        // Play/pause, seek, volume and mute, repeat and speed of a shown video or GIF (GalleryVideoPlayer's one transport).
        Same("gallery.video.transport", WorkViewPage.Gallery, WorkViewKind.Element, Required),
        // The transport's own "⋯" (speed choices, audio track, save frame, repeat): part of the transport, never reduced.
        Same("gallery.video.player-menu", WorkViewPage.Gallery, WorkViewKind.Action, Required),
        // The tools toggle brings the hidden tools back; "More" is the way to everything a view moved; "Read posts" appears
        // only for an empty gallery (state) and is then the way on.
        Same("gallery.toolbar.tools-toggle", WorkViewPage.Gallery, WorkViewKind.Action, Required, "gallery.tools-toggle"),
        Same("gallery.toolbar.more", WorkViewPage.Gallery, WorkViewKind.Element, Required),
        Same("gallery.toolbar.read-posts", WorkViewPage.Gallery, WorkViewKind.Action, Required, "thread.read"),

        // ---- Post panel: the way to the post, its association and (later) the tag panel slot. Direct in every view. ----
        Same("gallery.toolbar.post-panel", WorkViewPage.Gallery, WorkViewKind.Action, Direct, "gallery.context-toggle"),
        Same("gallery.post-panel", WorkViewPage.Gallery, WorkViewKind.Element, Direct),
        // V2: the segment "Post | Tags" in the head of the side panel (the host stays "gallery.post-panel"). It is the panel's own
        // navigation, so no view reduces it. Elements, not actions: the commands (P, Ctrl+Shift+T) keep their rows on the bar
        // ("gallery.toolbar.post-panel", "gallery.toolbar.tags"), which decide where a dialog returns the focus to.
        Same("gallery.panel.post", WorkViewPage.Gallery, WorkViewKind.Element, Direct),
        Same("gallery.panel.tags", WorkViewPage.Gallery, WorkViewKind.Element, Direct),

        // ---- Picture tools next to the post panel: fit and 1:1 are frequent and cost one icon each. ----
        Same("gallery.toolbar.fit", WorkViewPage.Gallery, WorkViewKind.Action, Direct, "gallery.fit"),
        Same("gallery.toolbar.actual", WorkViewPage.Gallery, WorkViewKind.Action, Direct, "gallery.actual-size"),

        // ---- Movable tools (plan §1.6), in GalleryMovableToolIds order. A shortcut keeps working in every view: the
        // viewer routes them by their command, not by where the button sits. ----
        // Curating: rating and tags are Simple's reason to open the gallery. Barebones keeps them under More.
        GalleryTool("gallery.toolbar.rating", Overflow, Direct, Direct, "media.rating.edit"),
        GalleryTool("gallery.toolbar.tags", Overflow, Direct, Direct, "media.tags.edit"),
        // Slideshow: a chosen or running slideshow keeps its toggle on the bar in every view (R6, the viewer passes the state).
        GalleryTool("gallery.toolbar.slideshow", Overflow, Direct, Direct, "gallery.slideshow-toggle"),
        GalleryTool("gallery.toolbar.slideshow-seconds", Overflow, Direct, Direct, null),
        GalleryTool("gallery.toolbar.playback-mode", Overflow, Overflow, Direct, null),
        GalleryTool("gallery.toolbar.help", Overflow, Overflow, Direct, "viewer.shortcut-help"),

        // ---- Rows of the flyout that no view moves (Overflow everywhere; their shortcuts and the media context menu twin stay). ----
        GalleryMenuRow("gallery.menu.zoom-in", "gallery.zoom-in", 110),
        GalleryMenuRow("gallery.menu.zoom-out", "gallery.zoom-out", 120),
        GalleryMenuRow("gallery.menu.manage-ratings", "media.rating.manage", 130),
        GalleryMenuRow("gallery.menu.export-original", "media.export-original", 140),
        GalleryMenuRow("gallery.menu.save-frame", "video.snapshot", 150),
        GalleryMenuRow("gallery.menu.video-repeat", "video.loop", 160),
        // The per-video slideshow rule (what a running slideshow does with a video) and its number: a setting of the flyout.
        GalleryMenuRow("gallery.menu.video-rule", null, 170),
        GalleryMenuRow("gallery.menu.overlay-toggle", "gallery.overlay-toggle", 180),
        GalleryMenuRow("gallery.menu.post-overlay-toggle", "gallery.post-overlay-toggle", 190),
        GalleryMenuRow("gallery.menu.fullscreen", "window.fullscreen", 200),

        // ---- Information lines under the picture. ----
        // "Where am I" (thread or result list with its source): Barebones leaves it to the details (I, the media context menu
        // and the post panel); the position between the arrows stays. The quality line is technical but one short line.
        new("gallery.info.scope", WorkViewPage.Gallery, WorkViewKind.Element, Hidden, Direct, Direct, Alternative: "media.details"),
        Same("gallery.info.quality", WorkViewPage.Gallery, WorkViewKind.Element, Direct),
        // The provenance line only ever says something for a source mismatch or a passive import (an integrity warning and a
        // consent state): a safety line, never reduced.
        Same("gallery.info.provenance", WorkViewPage.Gallery, WorkViewKind.Element, Required, safety: true),

        // ---- Errors, warnings and consent: Required in every view (R5). ----
        Same("gallery.status.media-unavailable", WorkViewPage.Gallery, WorkViewKind.Element, Required, safety: true),
        Same("gallery.status.file-only", WorkViewPage.Gallery, WorkViewKind.Element, Required, safety: true),
        Same("gallery.status.favorite-error", WorkViewPage.Gallery, WorkViewKind.Element, Required, safety: true),
        Same("gallery.status.rating", WorkViewPage.Gallery, WorkViewKind.Element, Required, safety: true),
        Same("gallery.status.slideshow", WorkViewPage.Gallery, WorkViewKind.Element, Required, safety: true),
        Same("gallery.status.overlay-fallback", WorkViewPage.Gallery, WorkViewKind.Element, Required, safety: true),
        // "Remove from favourites?" (keep is the default): the consent before a favourite is dropped.
        Same("gallery.favorite.confirm", WorkViewPage.Gallery, WorkViewKind.Element, Required, safety: true),
    ];
}

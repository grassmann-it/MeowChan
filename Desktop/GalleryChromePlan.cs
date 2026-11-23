using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.Desktop;

// G1 (PLAN-TAGGING-GALERIE-UX-1.1 §3, §6) with the GL0 contract (PLAN-GALERIE-LAYOUT-BLOECKE §4): the one pure answer to "which part of
// the gallery chrome is where". Three layers decide, in this order:
//   1. The work view (WorkViewPolicy.Gallery): the default of every block (ViewDefault) and what sits directly on the bar or under "More".
//   2. The person's layout choice for the mode (GalleryLayoutChoice; null = the view's default). The choice beats the view (R6).
//   3. The state, never saved as a layout: hidden tools (MediaFocus), auto-hidden controls, a low viewer, the tagging mode and its panel.
// Consuming (a running slideshow, full screen, auto-hide) is a state, not a mode: the layout choice applies unchanged. Errors, warnings,
// the consent to remove a favourite, the way back and the passive favourite star are not in the plan: the viewer always shows them.
// The viewer only applies the answer (GalleryViewer.ApplyChrome); this file never touches a control, so the matrix is tested without a window.

/// <summary>The two modes of the gallery (the switch "View | Tag"). Tag = the walk through the tagging queue.</summary>
internal enum GalleryChromeMode { View, Tag }

/// <summary>The blocks a person switches on or off (GL3's layout menu). The video bar, the arrows with the position, "More", the way back and the tagging decision strip are not among them.</summary>
internal enum GalleryBlock { Filmstrip, Tools, Info, Hints }

/// <summary>Where the filmstrip sits in the gallery (Tom, GL E1: at the bottom of the dock by default; the top stays selectable).</summary>
internal enum GalleryFilmstripDock { Top, Bottom }

/// <summary>Where the favourite of the shown medium can be changed: the bar's button, the tag panel's decision row (E1), or only by key (F, M, U) while the passive star shows the state.</summary>
internal enum GalleryFavoriteSurface { Bar, Panel, KeysOnly }

/// <summary>
/// The person's layout choice for one mode; null = the work view's default. One set per mode, saved per profile (GL4). Only the blocks
/// of <see cref="GalleryBlock"/> and the filmstrip's dock can be chosen.
/// </summary>
internal sealed record GalleryLayoutChoice(bool? Filmstrip = null, GalleryFilmstripDock? FilmstripDock = null,
    bool? Tools = null, bool? Info = null, bool? Hints = null)
{
    internal static GalleryLayoutChoice Default => new();

    /// <summary>The choice of one block (null: the view decides).</summary>
    internal bool? Of(GalleryBlock block) => block switch
    {
        GalleryBlock.Filmstrip => Filmstrip,
        GalleryBlock.Tools => Tools,
        GalleryBlock.Info => Info,
        _ => Hints
    };

    /// <summary>The same choice with one block set (null: back to the view's default); the layout menu's rows use it.</summary>
    internal GalleryLayoutChoice With(GalleryBlock block, bool? shown) => block switch
    {
        GalleryBlock.Filmstrip => this with { Filmstrip = shown },
        GalleryBlock.Tools => this with { Tools = shown },
        GalleryBlock.Info => this with { Info = shown },
        _ => this with { Hints = shown }
    };
}

/// <summary>
/// Everything the plan reads (GL0). <paramref name="Overlay"/>: the controls float over the picture; <paramref name="ControlsHidden"/>: the
/// overlay hid them (auto-hide); <paramref name="PanelCoupled"/>: the tag panel is coupled to the tagging mode (it says every sentence).
/// The init members are G1's additions for the bar's single tools.
/// </summary>
internal sealed record GalleryChromeInput(GalleryChromeMode Mode, WorkView View, GalleryLayoutChoice Choice,
    bool MediaFocus, bool Overlay, bool ControlsHidden, bool Video, bool PanelOpen, bool PanelCoupled, double Width, double Height)
{
    /// <summary>A slideshow is chosen or running: its toggle stays on the bar in every view, width and with hidden tools (R6).</summary>
    public bool SlideshowActive { get; init; }
    /// <summary>A slideshow runs now (the tagging mode shows its toggle on the bar only then).</summary>
    public bool SlideshowRunning { get; init; }
    /// <summary>The tag panel's favourite and rating row (PT6) is on the screen.</summary>
    public bool PanelDecisions { get; init; }
    /// <summary>The shown list can be walked as the tagging queue (Barebones shows the switch then; elsewhere it is always there).</summary>
    public bool TagAvailable { get; init; }
    /// <summary>The Tag mode walks the tagging queue: only then is there a decision strip (Back, Skip, Done &amp; next, Stamp &amp; next) and a progress.</summary>
    public bool Walk { get; init; }
}

/// <summary>The answer (GL0 members first, G1's single tools as init members). A record: an equal plan needs no new layout pass.</summary>
internal sealed record GalleryChromePlan(bool Filmstrip, GalleryFilmstripDock FilmstripDock, bool Transport, bool Tools, bool Info,
    bool InfoScope, bool InfoRating, bool Hints, bool DecisionRow, bool ToolsToggle, bool FilmstripPeek, IReadOnlyList<string> BarPriority)
{
    /// <summary>From this viewer width on, every tool the view offers fits on the bar.</summary>
    internal const double WideWidth = 1280;
    /// <summary>Below this width the labelled controls show only their icons (GalleryViewer.CompactToolbarWidth) and two tools leave the bar.</summary>
    internal const double CompactWidth = 1100;
    /// <summary>Below this width every tool with a menu row leaves the bar, apart from an active slideshow's toggle.</summary>
    internal const double NarrowWidth = 900;
    /// <summary>Below this width the 1:1 button leaves the bar (its key stays). The way back has no stage of its own: it shows only its arrow where its measured label no longer fits (<see cref="GalleryBackLabel"/>).</summary>
    internal const double TinyWidth = 640;
    /// <summary>Below this viewer height the filmstrip stays away (GalleryViewer.FilmstripMinimumHeight).</summary>
    internal const double FilmstripMinimumHeight = 420;

    /// <summary>
    /// The keep-visible order of the bar (plan §3.2), as policy ids: the first is the last to go. The arrows with the position, "More"
    /// and the tools toggle never go; the favourite is Required and never goes by width either. Only the movable tools
    /// (<see cref="WorkViewPolicy.GalleryMovableToolIds"/>) move under "More" when the width is short, last first.
    /// </summary>
    internal static IReadOnlyList<string> Priority { get; } = Array.AsReadOnly(new[]
    {
        "gallery.position", "gallery.toolbar.more", "gallery.toolbar.tools-toggle", "gallery.toolbar.layout", "gallery.mode", "gallery.toolbar.fit",
        "gallery.toolbar.post-panel", "gallery.toolbar.tags", "gallery.favorite", "gallery.toolbar.slideshow",
        "gallery.toolbar.help"
    });

    /// <summary>
    /// GL5d (UX spec S2): the overflow stage of a bar width: 0 from <see cref="WideWidth"/> (everything the view offers is on the bar), 1 below it,
    /// 2 below <see cref="CompactWidth"/> (texts go, the layout button becomes a section of "More"), 3 below <see cref="NarrowWidth"/>, 4 below
    /// <see cref="TinyWidth"/>. An unknown width (not laid out yet, 0 or less) is stage 0.
    /// </summary>
    internal static int OverflowStage(double width) =>
        width <= 0 || width >= WideWidth ? 0 : width >= CompactWidth ? 1 : width >= NarrowWidth ? 2 : width >= TinyWidth ? 3 : 4;

    /// <summary>
    /// The overflow table of S2 for the movable tools: the first stage at which the tool is under "More" in <paramref name="view"/> (the tool
    /// is on the bar below this stage). Advanced: help (?) at 1, tags and the slideshow at 3. Simple: tags at 2, the slideshow at 3. Barebones offers none of them on the bar; every other id leaves at 3.
    /// An active slideshow is not decided here: its toggle never leaves (R6).
    /// </summary>
    internal static int LeaveStage(WorkView view, string id) => (view, id) switch
    {
        (WorkView.Advanced, "gallery.toolbar.help") => 1,
        (WorkView.Simple, "gallery.toolbar.tags") => 2,
        _ => 3
    };

    /// <summary>The movable tools that leave the bar for "More" when the width is short, first to go first.</summary>
    internal static IReadOnlyList<string> OverflowOrder { get; } =
        Array.AsReadOnly(Priority.Reverse().Where(id => WorkViewPolicy.GalleryMovableToolIds.Contains(id)).ToArray());

    // ---- G1: the single tools of the bar ------------------------------------------------------------------------------------

    public GalleryChromeMode Mode { get; init; }
    /// <summary>The arrows with the position between them.</summary>
    public bool Nav { get; init; }
    /// <summary>Fit and 1:1.</summary>
    public bool Zoom { get; init; }
    /// <summary>The 1:1 button on the bar (S2 stage 4 puts it under "More"); needs <see cref="Zoom"/>.</summary>
    public bool ActualSize { get; init; }
    /// <summary>The mode switch shows icons instead of words (S2 stage 2: the bar is narrower than <see cref="CompactWidth"/>).</summary>
    public bool ModeIcons { get; init; }
    public bool PanelToggle { get; init; }
    public bool Favorite { get; init; }
    public bool Tags { get; init; }
    public bool Slideshow { get; init; }
    public bool Help { get; init; }
    public bool More { get; init; }
    /// <summary>
    /// The "Layout" button on the bar (UX spec S4 GL3+a): the work view puts it there (Advanced) and the bar is wide enough
    /// (<see cref="CompactWidth"/>); otherwise the same rows are a section of "More".
    /// </summary>
    public bool LayoutOnBar { get; init; }
    public bool ModeSwitch { get; init; }
    public bool InfoQuality { get; init; }
    /// <summary>The tagging mode's strip breaks into two rows below <see cref="WideWidth"/>.</summary>
    public bool StripTwoRows { get; init; }
    /// <summary>E1: where the favourite can be changed; the star beside the way back shows the state in every case.</summary>
    public GalleryFavoriteSurface FavoriteSurface { get; init; }

    /// <summary>The movable tools on the bar, in policy order (a computed member: not part of the record's equality).</summary>
    internal IReadOnlyList<string> BarToolIds => WorkViewPolicy.GalleryMovableToolIds.Where(OnBar).ToArray();

    /// <summary>Whether the movable tool <paramref name="id"/> sits on the bar (seconds and mode: never, E2).</summary>
    internal bool OnBar(string id) => id switch
    {
        "gallery.toolbar.tags" => Tags,
        "gallery.toolbar.slideshow" => Slideshow,
        "gallery.toolbar.help" => Help,
        _ => false
    };

    /// <summary>The same tools on the bar and in the menu (only visibility may differ).</summary>
    internal bool SamePlacement(GalleryChromePlan other) =>
        Tags == other.Tags && Slideshow == other.Slideshow && Help == other.Help;

    /// <summary>The mode of the viewer: Tag while it walks the tagging queue, else View.</summary>
    internal static GalleryChromeMode ResolveMode(bool tagging) => tagging ? GalleryChromeMode.Tag : GalleryChromeMode.View;

    /// <summary>
    /// The work view's default layout for a mode: the filmstrip as the policy row "gallery.filmstrip" says (Barebones: away), at the bottom
    /// (Tom, GL E1); tools and info on; hints on in View, off in Tag (the tag panel says every sentence there).
    /// </summary>
    internal static GalleryLayoutChoice ViewDefault(GalleryChromeMode mode, WorkView view) => new(
        Filmstrip: WorkViewPolicy.OnSurface(WorkViewPolicy.Resolve("gallery.filmstrip", view)),
        FilmstripDock: GalleryFilmstripDock.Bottom,
        Tools: true,
        Info: true,
        Hints: mode == GalleryChromeMode.View);

    /// <summary>Resolves the plan: (1) the view's default, (2) the choice where it is not null, (3) the state.</summary>
    internal static GalleryChromePlan Resolve(GalleryChromeInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        GalleryLayoutChoice fallback = ViewDefault(input.Mode, input.View), choice = input.Choice ?? GalleryLayoutChoice.Default;
        bool filmstrip = choice.Filmstrip ?? fallback.Filmstrip!.Value;
        GalleryFilmstripDock dock = choice.FilmstripDock ?? fallback.FilmstripDock!.Value;
        bool tools = choice.Tools ?? fallback.Tools!.Value;
        bool info = choice.Info ?? fallback.Info!.Value;
        bool hints = choice.Hints ?? fallback.Hints!.Value;
        bool tag = input.Mode == GalleryChromeMode.Tag;
        bool bar = !input.ControlsHidden;

        // (3) The state. Hidden tools take every layout block away (the choice stays; "show" restores exactly it); auto-hidden controls
        // take everything but the video bar; a low viewer has no room for the filmstrip; the tagging mode's strip replaces the tools.
        if (input.MediaFocus || input.ControlsHidden) filmstrip = tools = info = hints = false;
        if (input.Height > 0 && input.Height < FilmstripMinimumHeight) filmstrip = false;
        if (tag) tools = false;
        hints &= !input.PanelCoupled;

        int stage = OverflowStage(input.Width);
        bool Fits(string id) => stage < LeaveStage(input.View, id);
        bool ViewOffers(string id, bool raised = false) => WorkViewPolicy.OnSurface(WorkViewPolicy.Resolve(id, input.View, raised));
        bool Movable(string id) => bar && tools && ViewOffers(id) && Fits(id);

        GalleryFavoriteSurface favoriteSurface = tag && input.PanelOpen && input.PanelDecisions ? GalleryFavoriteSurface.Panel
            : input.MediaFocus || !bar ? GalleryFavoriteSurface.KeysOnly : GalleryFavoriteSurface.Bar;
        return new GalleryChromePlan(
            Filmstrip: filmstrip,
            FilmstripDock: dock,
            Transport: input.Video,
            Tools: tools,
            Info: info,
            // S2: the "where am I" segment goes first when the bar gets narrow (below NarrowWidth).
            InfoScope: info && !tag && ViewOffers("gallery.info.scope") && (input.Width <= 0 || input.Width >= NarrowWidth),
            // QW5: the info line's rating yields to the panel's decision row (the Post and the Tags segment both carry one), which shows the same
            // value and is the place to change it. There is no rating button on the bar any more (review 15.11.).
            InfoRating: info && InfoRatingVisible(mediaFocus: false, input.PanelDecisions),
            Hints: hints,
            // Only a walk of the tagging queue has decisions; Tag in any other gallery is the tag panel alone.
            DecisionRow: tag && input.Walk && bar,
            // The way back to hidden tools; in the tagging mode there are no optional tools to hide (unless they are hidden already).
            ToolsToggle: bar && (!tag || input.MediaFocus),
            FilmstripPeek: filmstrip && dock == GalleryFilmstripDock.Top && input.Overlay,
            BarPriority: Priority)
        {
            Mode = input.Mode,
            Nav = bar,
            More = bar,
            LayoutOnBar = bar && ViewOffers("gallery.toolbar.layout") && (input.Width <= 0 || input.Width >= CompactWidth),
            Zoom = bar && tools,
            // S2 stage 4: below TinyWidth the 1:1 button is a row of "More" (the key 1 stays); Fit stays on the bar.
            ActualSize = bar && tools && stage < 4,
            // S2 stage 2: below CompactWidth the mode switch shows its icons (eye | tag) instead of its words.
            ModeIcons = input.Width > 0 && input.Width < CompactWidth,
            PanelToggle = bar && tools,
            // E1: with the panel's decision row on the screen, the panel is the one favourite surface; without it (panel closed) the
            // button comes back in the tagging mode too, so changing the favourite is never only a key away while the tools show.
            Favorite = bar && (tools || tag && !input.MediaFocus && favoriteSurface == GalleryFavoriteSurface.Bar),
            FavoriteSurface = favoriteSurface,
            Tags = Movable("gallery.toolbar.tags"),
            Help = Movable("gallery.toolbar.help"),
            // R6: a chosen or running slideshow keeps its pause one click away (with hidden tools too); in the tagging mode only while it runs.
            Slideshow = bar && (input.SlideshowActive && (!tag || input.SlideshowRunning)
                || tools && ViewOffers("gallery.toolbar.slideshow") && Fits("gallery.toolbar.slideshow")),
            // Barebones (the reduced bar) leaves the switch to its key; the mode and a walkable list raise it (R6).
            // In the tagging mode it is the way out and stays even with hidden tools.
            ModeSwitch = bar && ViewOffers("gallery.mode", tag || input.TagAvailable) && (tag || !input.MediaFocus),
            InfoQuality = info,
            // The queue decisions/status keep their own row so the gallery toolbar retains the same full-width geometry in both modes.
            StripTwoRows = tag && input.Walk
        };
    }

    // ---- the "say it once" rules of the tagging mode (QW1 to QW5, folded in from GalleryTaggingChrome) ---------------------------

    /// <summary>
    /// QW1: the position ("Queue 3 of 27") stands once, between the arrows. The tag panel's head shows no "Medium 3 of 27" in the
    /// tagging mode; outside the mode it shows it as before.
    /// </summary>
    internal static GalleryTagPanelPosition? PanelCounter(GalleryTagPanelPosition? counter, bool taggingMode) => taggingMode ? null : counter;

    /// <summary>
    /// QW1: the filmstrip repeats the position only while the arrows are away (the controls are hidden and the strip peeks in alone
    /// at the top edge); with the arrows on the screen it shows no text.
    /// </summary>
    internal static string? FilmstripPosition(string? position, bool controlsHidden) => controlsHidden ? position : null;

    /// <summary>
    /// QW2: the strip's status line speaks only without the tag panel; with the panel open its own status line says every sentence
    /// (decisions, undo, errors), so it is never read or shown twice. It is a status line (it carries refusals), not a hint.
    /// </summary>
    internal static bool StripStatusVisible(bool panelCoupled) => !panelCoupled;

    /// <summary>
    /// QW5: the info line's "Rating: …" yields to the panel's favorite and rating row, which shows the same value for the same
    /// medium (or for the marked ones) and is the one place to change it.
    /// </summary>
    internal static bool InfoRatingVisible(bool mediaFocus, bool decisionRowVisible) => !mediaFocus && !decisionRowVisible;

    /// <summary>
    /// The slideshow lines that are hints (what it does), as opposed to reasons (why it waits) and errors, which always show: the person's
    /// "Hints" choice and the tagging mode silence only these.
    /// </summary>
    internal static bool IsHint(SlideshowStatus status) => status is SlideshowStatus.Ready or SlideshowStatus.Running or SlideshowStatus.Loading;
}

/// <summary>
/// Tom's E3 addition: in the tagging mode the slideshow may run, but it waits while the tag panel is being worked in (the focus is in
/// the panel, text stands in the tag field, or its suggestion list is open). When that ends the shown medium gets a fresh period, so
/// the medium the person just tagged is never skipped by a clock that ran out while they typed.
/// </summary>
internal static class TaggingSlideshowHold
{
    /// <summary>Whether the tag panel holds a running slideshow now. Outside the tagging mode the open panel pauses it as before.</summary>
    internal static bool Holds(bool taggingMode, bool tagPanelOpen, bool panelFocused, bool fieldHasText, bool listOpen) =>
        taggingMode && tagPanelOpen && (panelFocused || fieldHasText || listOpen);

    /// <summary>Whether the side panel stops the slideshow altogether: the post (reading) always does; the tag panel only outside the tagging mode.</summary>
    internal static bool PanelBlocks(bool panelOpen, bool tagPanelOpen, bool taggingMode) => panelOpen && !(taggingMode && tagPanelOpen);
}

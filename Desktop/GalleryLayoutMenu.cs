using System;
using System.Collections.Generic;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

// GL3 (PLAN-GALERIE-LAYOUT-BLOECKE §3, Tom's E2: a "Layout" button beside "More" and the same rows on a right click of the free dock area).
// The gallery's half of the layout menu, pure and without a control: Rows() describes the menu for a plan, the person's choice of the shown
// mode and the work view's default; Apply() says what a row does to the choice. The flyout (LayoutMenuPanel) and the context menu
// (LayoutMenuBuilder.ToContextMenu) are both made from the rows, so they can never disagree. Nothing here is saved: the viewer applies the
// returned choice with ChangeLayoutChoice, which raises LayoutChoiceChanged for the window (GL4).
//
// Layers (plan §3.2): the work view and the state decide what is available at all, the person's choice decides visibility within that.
// A row that the state makes unavailable stays in the menu, shows its effective state and says why it is disabled:
//   * hidden tools (MediaFocus): all four blocks are away until the tools toggle brings them back;
//   * a low viewer: no room for the filmstrip;
//   * the Tag mode: its decision row replaces the tools; the coupled tag panel says the hints itself.
// The filmstrip's position rows wait for a shown filmstrip.

/// <summary>The rows of the gallery's layout menu and what they do to a <see cref="GalleryLayoutChoice"/>.</summary>
internal static class GalleryLayoutMenu
{
    internal const string FilmstripId = "gallery.layout.filmstrip";
    internal const string FilmstripTopId = "gallery.layout.filmstrip-top";
    internal const string FilmstripBottomId = "gallery.layout.filmstrip-bottom";
    internal const string ToolsId = "gallery.layout.tools";
    internal const string InfoId = "gallery.layout.info";
    internal const string HintsId = "gallery.layout.hints";
    internal const string ResetId = "gallery.layout.reset";
    /// <summary>The exclusive group of the two position rows.</summary>
    internal const string DockGroup = "gallery.layout.filmstrip-dock";
    /// <summary>The command that opens the menu from the keyboard (unbound by default, rebindable).</summary>
    internal const string OpenCommandId = "gallery.layout-menu";

    /// <summary>The row ids in menu order (the interactive ones).</summary>
    internal static IReadOnlyList<string> RowIds { get; } = Array.AsReadOnly(
        new[] { FilmstripId, FilmstripTopId, FilmstripBottomId, ToolsId, InfoId, HintsId, ResetId });

    /// <summary>The command whose shortcut a row shows: the filmstrip's toggle for the filmstrip, the hide-tools switch (Shift+H, gallery key
    /// scheme of revision 13) for the tools row, the row's own layout command otherwise (info Shift+I and hints Shift+N by the scheme).</summary>
    internal static string CommandOf(string rowId) => rowId switch
    {
        FilmstripId => "gallery.filmstrip-toggle",
        ToolsId => "gallery.tools-toggle",
        _ => rowId
    };

    /// <summary>The block a switch row stands for (null for the other rows).</summary>
    internal static GalleryBlock? BlockOf(string rowId) => rowId switch
    {
        FilmstripId => GalleryBlock.Filmstrip,
        ToolsId => GalleryBlock.Tools,
        InfoId => GalleryBlock.Info,
        HintsId => GalleryBlock.Hints,
        _ => null
    };

    /// <summary>
    /// The menu for the shown mode. <paramref name="plan"/> is what is on the screen (its values are the rows' effective state),
    /// <paramref name="choice"/> the person's saved choice of the mode, <paramref name="viewDefault"/> the work view's default for it.
    /// <paramref name="state"/> (the plan's input) lets a disabled row name its reason; without it only the mode explains a row.
    /// </summary>
    internal static LayoutMenuModel Rows(GalleryChromePlan plan, GalleryLayoutChoice choice, GalleryLayoutChoice viewDefault, GalleryChromeInput? state = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        choice ??= GalleryLayoutChoice.Default;
        viewDefault ??= GalleryLayoutChoice.Default;
        bool tag = plan.Mode == GalleryChromeMode.Tag;
        string filmstripReason = BlockedReason(GalleryBlock.Filmstrip, plan, state) ?? Texts.T("gallery.layout.whyFilmstripOff");
        bool filmstripOn = plan.Filmstrip;

        LayoutMenuRow Toggle(string id, GalleryBlock block, bool shown, string labelKey, string helpKey) => new(LayoutMenuRowKind.Toggle, id, Texts.T(labelKey))
        {
            Checked = shown,
            CommandId = CommandOf(id),
            DisabledReason = BlockedReason(block, plan, state),
            Hint = Texts.T(Customized(block, choice, viewDefault) ? "gallery.layout.hintOwn" : "gallery.layout.hintView"),
            Description = Texts.T(helpKey)
        };
        LayoutMenuRow Dock(string id, GalleryFilmstripDock dock, string labelKey) => new(LayoutMenuRowKind.Choice, id, Texts.T(labelKey))
        {
            Checked = filmstripOn && plan.FilmstripDock == dock,
            Group = DockGroup,
            CommandId = CommandOf(id),
            Indent = 1,
            DisabledReason = filmstripOn ? null : filmstripReason,
            Hint = Texts.T(choice.FilmstripDock != null && choice.FilmstripDock != viewDefault.FilmstripDock ? "gallery.layout.hintOwn" : "gallery.layout.hintView"),
            Description = Texts.T("gallery.layout.filmstripHelp")
        };

        List<LayoutMenuRow> rows =
        [
            Toggle(FilmstripId, GalleryBlock.Filmstrip, plan.Filmstrip, "gallery.layout.filmstrip", "gallery.layout.filmstripHelp"),
            Dock(FilmstripTopId, GalleryFilmstripDock.Top, "gallery.layout.filmstripTop"),
            Dock(FilmstripBottomId, GalleryFilmstripDock.Bottom, "gallery.layout.filmstripBottom"),
            Toggle(ToolsId, GalleryBlock.Tools, plan.Tools, "gallery.layout.tools", "gallery.layout.toolsHelp"),
            Toggle(InfoId, GalleryBlock.Info, plan.Info, "gallery.layout.info", "gallery.layout.infoHelp"),
            Toggle(HintsId, GalleryBlock.Hints, plan.Hints, "gallery.layout.hints", "gallery.layout.hintsHelp"),
            new(LayoutMenuRowKind.Separator, "gallery.layout.separator", string.Empty),
            new(LayoutMenuRowKind.Action, ResetId, Texts.T("gallery.layout.reset"))
            {
                CommandId = CommandOf(ResetId),
                DisabledReason = choice == GalleryLayoutChoice.Default ? Texts.T("gallery.layout.whyDefault") : null,
                Description = Texts.T("gallery.layout.resetHelp")
            }
        ];
        return new LayoutMenuModel(Texts.T("gallery.layout.mode", Texts.T(tag ? "gallery.mode.tag" : "gallery.mode.view")), rows);
    }

    /// <summary>Whether the person's choice differs from the work view's default for the block (the row's "your choice" hint).</summary>
    internal static bool Customized(GalleryBlock block, GalleryLayoutChoice choice, GalleryLayoutChoice viewDefault) =>
        choice.Of(block) is { } own && own != viewDefault.Of(block);

    /// <summary>Why the state makes a block unavailable now, or null when the person can choose it.</summary>
    internal static string? BlockedReason(GalleryBlock block, GalleryChromePlan plan, GalleryChromeInput? state)
    {
        if (state is { MediaFocus: true }) return Texts.T("gallery.layout.whyFocus");
        switch (block)
        {
            case GalleryBlock.Filmstrip when state is { Height: > 0 } low && low.Height < GalleryChromePlan.FilmstripMinimumHeight:
                return Texts.T("gallery.layout.whyLow");
            case GalleryBlock.Tools when plan.Mode == GalleryChromeMode.Tag:
                return Texts.T("gallery.layout.whyTag");
            case GalleryBlock.Hints when plan.Mode == GalleryChromeMode.Tag && state is { PanelCoupled: true }:
                return Texts.T("gallery.layout.whyPanel");
            default:
                return null;
        }
    }

    /// <summary>
    /// What choosing a row does: the new choice of the mode (the same instance when nothing changes; null for an unknown row or a row the
    /// state makes unavailable). A value equal to the work view's default is stored as "the view decides" (null), so the choice follows the
    /// view again when the person has not really chosen. The reset row gives <see cref="GalleryLayoutChoice.Default"/>; the viewer runs it
    /// through <c>ResetLayout</c> to offer the undo.
    /// </summary>
    internal static GalleryLayoutChoice? Apply(string rowId, GalleryChromePlan plan, GalleryLayoutChoice choice, GalleryLayoutChoice viewDefault,
        GalleryChromeInput? state = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        choice ??= GalleryLayoutChoice.Default;
        viewDefault ??= GalleryLayoutChoice.Default;
        if (rowId == ResetId) return GalleryLayoutChoice.Default;
        if (BlockOf(rowId) is { } block)
        {
            if (BlockedReason(block, plan, state) != null) return null;
            bool shown = block switch
            {
                GalleryBlock.Filmstrip => plan.Filmstrip,
                GalleryBlock.Tools => plan.Tools,
                GalleryBlock.Info => plan.Info,
                _ => plan.Hints
            };
            bool wanted = !shown;
            return choice.With(block, wanted == viewDefault.Of(block) ? null : wanted);
        }
        if (rowId is FilmstripTopId or FilmstripBottomId)
        {
            if (!plan.Filmstrip) return null;
            GalleryFilmstripDock dock = rowId == FilmstripTopId ? GalleryFilmstripDock.Top : GalleryFilmstripDock.Bottom;
            return choice with { FilmstripDock = dock == viewDefault.FilmstripDock ? null : dock };
        }
        return null;
    }
}

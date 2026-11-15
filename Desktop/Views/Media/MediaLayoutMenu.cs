using System;
using System.Collections.Generic;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

/// <summary>
/// MP5 on GL3's shared layout menu (C15): the media page's half of the "Layout" menu, pure and without a control, like <c>GalleryLayoutMenu</c>.
/// <see cref="Rows"/> describes the menu for a layout as a <see cref="LayoutMenuModel"/>; <see cref="Apply"/> says which layout a row asks for.
/// The "Layout" button's flyout (<see cref="LayoutMenuPanel"/>), its submenu under More (Barebones, narrow pages) and the right-click on empty
/// grid space (<see cref="LayoutMenuBuilder.ToContextMenu"/>) are all made from the rows, so they cannot disagree, and a test can read what the
/// menu offers and what each row would change without a window. Every row computes the whole next layout from the current one; the page applies
/// it (visibility and sizes only) and saves it.
/// Sections: the tag list (switch and side; the width is the drag handle on its edge), the media panel (switch and side, likewise), the rows above the grid, the parts of a tile's caption,
/// the tile size, the ready-made layouts (the one that matches is checked) and "Reset layout".
/// </summary>
internal static class MediaLayoutMenu
{
    internal const string IdPrefix = "media.layout.";
    internal const string TagListId = IdPrefix + "tagList", SideLeftId = IdPrefix + "side.left", SideRightId = IdPrefix + "side.right",
        PanelId = IdPrefix + "panel", PanelSideLeftId = IdPrefix + "panelSide.left", PanelSideRightId = IdPrefix + "panelSide.right",
        ChipsId = IdPrefix + "chips", StatusId = IdPrefix + "status", SubtitleId = IdPrefix + "subtitle",
        CaptionNameId = IdPrefix + "caption.name", CaptionTagsId = IdPrefix + "caption.tags", CaptionQueueId = IdPrefix + "caption.queue",
        CaptionSourceId = IdPrefix + "caption.source", SizeSmallId = IdPrefix + "size.small", SizeMediumId = IdPrefix + "size.medium",
        SizeLargeId = IdPrefix + "size.large", PresetStandardId = IdPrefix + "preset.standard", PresetBooruId = IdPrefix + "preset.booru",
        PresetGridId = IdPrefix + "preset.grid", ResetId = IdPrefix + "reset";

    /// <summary>The exclusive groups of the tag list's side, the media panel's side, the size and the presets.</summary>
    internal const string SideGroup = "side", PanelSideGroup = "panelSide", SizeGroup = "size", PresetGroup = "preset";

    private static readonly (string Id, string Key, MediaTileCaption Part)[] Captions =
    [
        (CaptionNameId, "media.layout.caption.name", MediaTileCaption.Name),
        (CaptionTagsId, "media.layout.caption.tags", MediaTileCaption.TagCount),
        (CaptionQueueId, "media.layout.caption.queue", MediaTileCaption.QueueMark),
        (CaptionSourceId, "media.layout.caption.source", MediaTileCaption.Source)
    ];

    private static readonly (string Id, string Key, MediaTileSize Size)[] Sizes =
    [
        (SizeSmallId, "media.layout.size.small", MediaTileSize.Small),
        (SizeMediumId, "media.layout.size.medium", MediaTileSize.Medium),
        (SizeLargeId, "media.layout.size.large", MediaTileSize.Large)
    ];

    private static readonly (string Id, string Key, MediaLayoutPreset Preset)[] Presets =
    [
        (PresetStandardId, "media.layout.preset.standard", MediaLayoutPreset.Standard),
        (PresetBooruId, "media.layout.preset.booru", MediaLayoutPreset.BooruView),
        (PresetGridId, "media.layout.preset.grid", MediaLayoutPreset.GridOnly)
    ];

    /// <summary>The accessible name of the menu in its context-menu form.</summary>
    internal static string MenuName => Texts.T("media.layout.menuName");

    /// <summary>
    /// The menu for the layout as it is now. It has no title line (the button and the menu's name say what it edits). <paramref name="panelHint"/> is what
    /// the media panel's row says while the panel is chosen but hidden by the page width (the tooltip of the row).
    /// </summary>
    internal static LayoutMenuModel Rows(MediaPageLayout current, string? panelHint = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        MediaPageLayout now = current.Normalized();
        List<LayoutMenuRow> rows = [];
        void Toggle(string id, string label, bool on) => rows.Add(new(LayoutMenuRowKind.Toggle, id, label) { Checked = on });
        void Choice(string group, string id, string label, bool on) => rows.Add(new(LayoutMenuRowKind.Choice, id, label) { Checked = on, Group = group });
        void Separator() => rows.Add(new(LayoutMenuRowKind.Separator, IdPrefix + "separator." + rows.Count, string.Empty));

        Toggle(TagListId, Texts.T("media.tagList"), now.TagList);
        Choice(SideGroup, SideLeftId, Texts.T("media.layout.side.left"), now.TagListSide == MediaTagListSide.Left);
        Choice(SideGroup, SideRightId, Texts.T("media.layout.side.right"), now.TagListSide == MediaTagListSide.Right);
        Separator();
        rows.Add(new(LayoutMenuRowKind.Toggle, PanelId, Texts.T("media.layout.panel")) { Checked = now.Panel, Hint = now.Panel ? panelHint : null, CommandId = MeowChan.Desktop.MediaPanelKeys.ToggleCommandId });
        Choice(PanelSideGroup, PanelSideLeftId, Texts.T("media.layout.panelSide.left"), now.PanelSide == MediaPanelSide.Left);
        Choice(PanelSideGroup, PanelSideRightId, Texts.T("media.layout.panelSide.right"), now.PanelSide == MediaPanelSide.Right);
        Separator();
        Toggle(ChipsId, Texts.T("media.layout.chips"), now.FilterChips);
        Toggle(StatusId, Texts.T("media.layout.status"), now.StatusLine);
        Toggle(SubtitleId, Texts.T("media.layout.subtitle"), now.Subtitle);
        Separator();
        foreach ((string id, string key, MediaTileCaption part) in Captions) Toggle(id, Texts.T(key), now.Caption.HasFlag(part));
        Separator();
        foreach ((string id, string key, MediaTileSize size) in Sizes) Choice(SizeGroup, id, Texts.T(key), now.TileSize == size);
        Separator();
        MediaLayoutPreset? matching = now.MatchingPreset;
        foreach ((string id, string key, MediaLayoutPreset preset) in Presets) Choice(PresetGroup, id, Texts.T(key), matching == preset);
        Separator();
        rows.Add(new(LayoutMenuRowKind.Action, ResetId, Texts.T("media.layout.reset")) { Hint = Texts.T("media.layout.resetHelp") });
        return new LayoutMenuModel(string.Empty, rows);
    }

    /// <summary>The layout the row <paramref name="rowId"/> asks for, computed from <paramref name="current"/>; null for an unknown row.</summary>
    internal static MediaPageLayout? Apply(string rowId, MediaPageLayout current)
    {
        ArgumentNullException.ThrowIfNull(current);
        MediaPageLayout now = current.Normalized();
        switch (rowId)
        {
            case TagListId: return now with { TagList = !now.TagList };
            case SideLeftId: return now with { TagListSide = MediaTagListSide.Left };
            case SideRightId: return now with { TagListSide = MediaTagListSide.Right };
            case PanelId: return now with { Panel = !now.Panel };
            case PanelSideLeftId: return now with { PanelSide = MediaPanelSide.Left };
            case PanelSideRightId: return now with { PanelSide = MediaPanelSide.Right };
            case ChipsId: return now with { FilterChips = !now.FilterChips };
            case StatusId: return now with { StatusLine = !now.StatusLine };
            case SubtitleId: return now with { Subtitle = !now.Subtitle };
            case ResetId: return MediaPageLayout.Reset;
        }
        foreach ((string id, _, MediaTileCaption part) in Captions)
            if (id == rowId) return now with { Caption = now.Caption ^ part };
        foreach ((string id, _, MediaTileSize size) in Sizes)
            if (id == rowId) return now with { TileSize = size };
        foreach ((string id, _, MediaLayoutPreset preset) in Presets)
            if (id == rowId) return MediaPageLayout.For(preset, now);
        return null;
    }
}

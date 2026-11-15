using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

/// <summary>
/// MP5: the entries of the media page's "Layout" menu, composed as plain data (a <see cref="ContextMenuBuilder"/>), so the same entries serve the
/// "Layout" button's flyout, its submenu under More (Barebones, narrow pages) and the right-click on empty grid space, and a test can read what
/// the menu offers and what each entry would change without a window. Every entry computes the whole next layout from the current one and hands
/// it to <c>choose</c>; the page applies it (visibility and sizes only) and saves it. The gallery's own Layout menu is a separate factory.
/// Sections: the tag list (switch and side; the width is the drag handle on its edge), the rows above the grid, the parts of a tile's caption,
/// the tile size, the ready-made layouts (the one that matches is checked) and "Reset layout".
/// </summary>
internal static class MediaLayoutMenu
{
    internal const string IdPrefix = "media.layout.";
    internal const string TagListId = IdPrefix + "tagList", SideLeftId = IdPrefix + "side.left", SideRightId = IdPrefix + "side.right",
        ChipsId = IdPrefix + "chips", StatusId = IdPrefix + "status", SubtitleId = IdPrefix + "subtitle",
        CaptionNameId = IdPrefix + "caption.name", CaptionTagsId = IdPrefix + "caption.tags", CaptionQueueId = IdPrefix + "caption.queue",
        CaptionSourceId = IdPrefix + "caption.source", SizeSmallId = IdPrefix + "size.small", SizeMediumId = IdPrefix + "size.medium",
        SizeLargeId = IdPrefix + "size.large", PresetStandardId = IdPrefix + "preset.standard", PresetBooruId = IdPrefix + "preset.booru",
        PresetGridId = IdPrefix + "preset.grid", ResetId = IdPrefix + "reset";

    /// <summary>The menu for the layout as it is now; <paramref name="choose"/> receives the layout an entry asks for.</summary>
    internal static ContextMenuBuilder Compose(MediaPageLayout current, Action<MediaPageLayout> choose)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(choose);
        MediaPageLayout now = current.Normalized();
        ContextMenuBuilder menu = new ContextMenuBuilder().Named(Texts.T("media.layout.menuName"));
        menu.Check(TagListId, Texts.T("media.tagList"), now.TagList, () => choose(now with { TagList = !now.TagList }))
            .Radio("side", SideLeftId, Texts.T("media.layout.side.left"), now.TagListSide == MediaTagListSide.Left, () => choose(now with { TagListSide = MediaTagListSide.Left }))
            .Radio("side", SideRightId, Texts.T("media.layout.side.right"), now.TagListSide == MediaTagListSide.Right, () => choose(now with { TagListSide = MediaTagListSide.Right }));
        menu.Section()
            .Check(ChipsId, Texts.T("media.layout.chips"), now.FilterChips, () => choose(now with { FilterChips = !now.FilterChips }))
            .Check(StatusId, Texts.T("media.layout.status"), now.StatusLine, () => choose(now with { StatusLine = !now.StatusLine }))
            .Check(SubtitleId, Texts.T("media.layout.subtitle"), now.Subtitle, () => choose(now with { Subtitle = !now.Subtitle }));
        menu.Section();
        foreach ((string id, string key, MediaTileCaption part) in Captions)
            menu.Check(id, Texts.T(key), now.Caption.HasFlag(part), () => choose(now with { Caption = now.Caption ^ part }));
        menu.Section();
        foreach ((string id, string key, MediaTileSize size) in Sizes)
            menu.Radio("size", id, Texts.T(key), now.TileSize == size, () => choose(now with { TileSize = size }));
        menu.Section();
        MediaLayoutPreset? matching = now.MatchingPreset;
        foreach ((string id, string key, MediaLayoutPreset preset) in Presets)
            menu.Radio("preset", id, Texts.T(key), matching == preset, () => choose(MediaPageLayout.For(preset, now)));
        menu.Section().Item(ResetId, Texts.T("media.layout.reset"), () => choose(MediaPageLayout.Reset));
        return menu;
    }

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

    /// <summary>
    /// The entries as controls, new at every call (a control has one parent: the flyout, the submenu under More and the context menu each take
    /// their own copy). Composed fresh at every opening so the checked entries are current.
    /// </summary>
    internal static IReadOnlyList<Control> BuildItems(MediaPageLayout current, Action<MediaPageLayout> choose) =>
        ContextMenuBuilder.Build(Compose(current, choose).ToModel())?.ItemsSource is IEnumerable<Control> items ? items.ToArray() : [];
}

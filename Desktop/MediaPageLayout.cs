using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MeowChan.Desktop;

/// <summary>Which side of the grid the tag list stands on.</summary>
internal enum MediaTagListSide { Left, Right }

/// <summary>Which side of the grid the media panel (the large medium beside the grid) stands on.</summary>
internal enum MediaPanelSide { Left, Right }

/// <summary>The size of a tile: preview, cell width and cell height follow it (<see cref="MediaTileMetrics"/>).</summary>
internal enum MediaTileSize { Small, Medium, Large }

/// <summary>The parts of a tile's caption that can be shown or left out (the picture, its type badge, the selection check and the favourite star always stay).</summary>
[Flags]
internal enum MediaTileCaption
{
    None = 0,
    /// <summary>The file name under the picture.</summary>
    Name = 1,
    /// <summary>The number of own tags (the tag mark in the lower left corner of the picture).</summary>
    TagCount = 2,
    /// <summary>The queue mark ("in the tagging queue") in the lower left corner of the picture.</summary>
    QueueMark = 4,
    /// <summary>Where the medium comes from: the thread or post line under the name and the provider badge on the picture.</summary>
    Source = 8,
    All = Name | TagCount | QueueMark | Source
}

/// <summary>The ready-made layouts of the media page's "Layout" menu.</summary>
internal enum MediaLayoutPreset { Standard, BooruView, GridWithSidebar }

/// <summary>
/// MP5: how the user composed the media page, as plain data (no control): the tag list (on or off, side, width), the rows above the grid
/// (filter chips, status line, page subtitle), the parts of a tile's caption and the tile size. The toolbar is not a block: it is the way to
/// change the layout and never goes away. The layout is kept per profile: the tag list switch in its old key
/// (<see cref="MeowChan.Config.MediaBooruMode"/>, so earlier profiles keep their choice), everything else as one short text
/// (<see cref="Format"/> / <see cref="Parse"/>, <see cref="MeowChan.Config.MediaLayout"/>) that reads whatever it can from a damaged value
/// and falls back to the standard for the rest, so a bad entry never costs the profile its other settings.
/// </summary>
internal sealed record MediaPageLayout
{
    /// <summary>The standard layout: today's media page.</summary>
    internal static MediaPageLayout Standard { get; } = new();

    /// <summary>The tag list stands beside the grid.</summary>
    internal bool TagList { get; init; }
    internal MediaTagListSide TagListSide { get; init; } = MediaTagListSide.Left;
    /// <summary>The width of the tag list in dip (<see cref="MeowChan.Config.MediaTagListWidthMinimum"/> to <see cref="MeowChan.Config.MediaTagListWidthMaximum"/>).</summary>
    internal int TagListWidth { get; init; } = MeowChan.Config.MediaTagListWidthDefault;
    /// <summary>The row of the active filters (chips, "+ Filter", "Reset all").</summary>
    internal bool FilterChips { get; init; } = true;
    /// <summary>The counts line under the filters.</summary>
    internal bool StatusLine { get; init; } = true;
    /// <summary>The sentence under the page title.</summary>
    internal bool Subtitle { get; init; } = true;
    internal MediaTileCaption Caption { get; init; } = MediaTileCaption.All;
    internal MediaTileSize TileSize { get; init; } = MediaTileSize.Medium;

    /// <summary>
    /// The media panel: the selected medium large beside the grid. On by default; it still hides by itself while the page is narrower than
    /// <see cref="MeowChan.Config.MediaPanelMinimumPageWidth"/> (the choice stays, see <see cref="MediaPanelColumns"/>).
    /// </summary>
    internal bool Panel { get; init; } = true;
    internal MediaPanelSide PanelSide { get; init; } = MediaPanelSide.Right;
    /// <summary>The width of the media panel in dip (<see cref="MeowChan.Config.MediaPanelWidthMinimum"/> to <see cref="MeowChan.Config.MediaPanelWidthMaximum"/>).</summary>
    internal int PanelWidth { get; init; } = MeowChan.Config.MediaPanelWidthDefault;

    /// <summary>The same layout with the widths brought into their ranges.</summary>
    internal MediaPageLayout Normalized() => this with
    {
        TagListWidth = Math.Clamp(TagListWidth, MeowChan.Config.MediaTagListWidthMinimum, MeowChan.Config.MediaTagListWidthMaximum),
        Caption = Caption & MediaTileCaption.All,
        TagListSide = Enum.IsDefined(TagListSide) ? TagListSide : MediaTagListSide.Left,
        TileSize = Enum.IsDefined(TileSize) ? TileSize : MediaTileSize.Medium,
        PanelWidth = Math.Clamp(PanelWidth, MeowChan.Config.MediaPanelWidthMinimum, MeowChan.Config.MediaPanelWidthMaximum),
        PanelSide = Enum.IsDefined(PanelSide) ? PanelSide : MediaPanelSide.Right
    };

    /// <summary>
    /// The layout a preset makes. The width of the tag list and the media panel (shown or not, its side and width) are the user's measures and stay (only "Reset" takes the defaults back).
    /// Standard is today's page; the Booru view puts the tag list on the left with compact tiles that show the tag count (and the queue
    /// mark), the chips and the status line on and the subtitle off; the grid-with-sidebar view keeps the toolbar and tiles while showing
    /// the selected medium in the existing media sidebar.
    /// </summary>
    internal static MediaPageLayout For(MediaLayoutPreset preset, MediaPageLayout current)
    {
        MediaPageLayout own = current.Normalized();
        return preset switch
        {
            MediaLayoutPreset.BooruView => new MediaPageLayout
            {
                TagList = true, TagListSide = MediaTagListSide.Left, TagListWidth = own.TagListWidth, FilterChips = true, StatusLine = true,
                Subtitle = false, Caption = MediaTileCaption.TagCount | MediaTileCaption.QueueMark, TileSize = MediaTileSize.Small,
                Panel = own.Panel, PanelSide = own.PanelSide, PanelWidth = own.PanelWidth
            },
            MediaLayoutPreset.GridWithSidebar => new MediaPageLayout
            {
                TagList = false, TagListSide = own.TagListSide, TagListWidth = own.TagListWidth, FilterChips = false,
                StatusLine = false, Subtitle = false, Caption = MediaTileCaption.None, TileSize = MediaTileSize.Medium,
                Panel = true, PanelSide = own.PanelSide, PanelWidth = own.PanelWidth
            },
            _ => Standard with
            {
                TagListSide = own.TagListSide, TagListWidth = own.TagListWidth, Panel = own.Panel, PanelSide = own.PanelSide, PanelWidth = own.PanelWidth
            }
        };
    }

    /// <summary>"Reset": every block and measure at its default, the width of the tag list included.</summary>
    internal static MediaPageLayout Reset => Standard;

    /// <summary>The preset this layout equals (the tag list's width and side do not count), or null for a layout of the user's own.</summary>
    internal MediaLayoutPreset? MatchingPreset
    {
        get
        {
            MediaPageLayout self = Normalized();
            foreach (MediaLayoutPreset preset in Enum.GetValues<MediaLayoutPreset>())
                if (SameBlocks(self, For(preset, self))) return preset;
            return null;
        }
    }

    private static bool SameBlocks(MediaPageLayout a, MediaPageLayout b) =>
        a.TagList == b.TagList && a.FilterChips == b.FilterChips && a.StatusLine == b.StatusLine && a.Subtitle == b.Subtitle
        && a.Panel == b.Panel
        && a.Caption == b.Caption && a.TileSize == b.TileSize && (!a.TagList || a.TagListSide == b.TagListSide);

    // ---- The saved text ----

    private const string SideKey = "side", WidthKey = "width", ChipsKey = "chips", StatusKey = "status", SubtitleKey = "subtitle", CaptionKey = "caption", SizeKey = "size",
        PanelKey = "panel", PanelSideKey = "pside", PanelWidthKey = "pwidth";
    private static readonly (MediaTileCaption Part, string Name)[] CaptionNames =
    [
        (MediaTileCaption.Name, "name"), (MediaTileCaption.TagCount, "tags"), (MediaTileCaption.QueueMark, "queue"), (MediaTileCaption.Source, "source")
    ];

    /// <summary>
    /// The saved text of everything but the tag list switch (that one lives in <see cref="MeowChan.Config.MediaBooruMode"/>):
    /// <c>side=left;width=256;chips=1;status=1;subtitle=1;caption=name,tags,queue,source;size=m</c>. A caption without parts is <c>caption=none</c>.
    /// The media panel's three parts follow only where they differ from their defaults (<c>panel=0;pside=left;pwidth=420</c>), so a profile that never
    /// touched the panel keeps exactly the text it had.
    /// </summary>
    internal string Format()
    {
        MediaPageLayout self = Normalized();
        string caption = self.Caption == MediaTileCaption.None ? "none" : string.Join(',', CaptionNames.Where(part => self.Caption.HasFlag(part.Part)).Select(part => part.Name));
        List<string> parts = new()
        {
            SideKey + "=" + (self.TagListSide == MediaTagListSide.Right ? "right" : "left"),
            WidthKey + "=" + self.TagListWidth.ToString(CultureInfo.InvariantCulture),
            ChipsKey + "=" + Bit(self.FilterChips),
            StatusKey + "=" + Bit(self.StatusLine),
            SubtitleKey + "=" + Bit(self.Subtitle),
            CaptionKey + "=" + caption,
            SizeKey + "=" + self.TileSize switch { MediaTileSize.Small => "s", MediaTileSize.Large => "l", _ => "m" }
        };
        if (!self.Panel) parts.Add(PanelKey + "=0");
        if (self.PanelSide != MediaPanelSide.Right) parts.Add(PanelSideKey + "=left");
        if (self.PanelWidth != MeowChan.Config.MediaPanelWidthDefault) parts.Add(PanelWidthKey + "=" + self.PanelWidth.ToString(CultureInfo.InvariantCulture));
        return string.Join(';', parts);
    }

    private static string Bit(bool value) => value ? "1" : "0";

    /// <summary>
    /// Reads a saved text. Never throws: a missing, empty or damaged text, an unknown key or a value out of range gives the standard for that
    /// part (a width outside its range is brought into it), every other part is read as it stands. <paramref name="tagList"/> is the saved
    /// tag list switch (its own key).
    /// </summary>
    internal static MediaPageLayout Parse(string? text, bool tagList)
    {
        MediaPageLayout layout = Standard with { TagList = tagList };
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumTextLength) return layout;
        foreach (string pair in text.Split(';'))
        {
            int equals = pair.IndexOf('=');
            if (equals <= 0) continue;
            string key = pair[..equals].Trim().ToLowerInvariant(), value = pair[(equals + 1)..].Trim().ToLowerInvariant();
            switch (key)
            {
                case SideKey:
                    layout = layout with { TagListSide = value == "right" ? MediaTagListSide.Right : MediaTagListSide.Left };
                    break;
                case WidthKey when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int width):
                    layout = layout with { TagListWidth = Math.Clamp(width, MeowChan.Config.MediaTagListWidthMinimum, MeowChan.Config.MediaTagListWidthMaximum) };
                    break;
                case ChipsKey when TryBit(value, out bool chips): layout = layout with { FilterChips = chips }; break;
                case StatusKey when TryBit(value, out bool status): layout = layout with { StatusLine = status }; break;
                case SubtitleKey when TryBit(value, out bool subtitle): layout = layout with { Subtitle = subtitle }; break;
                case CaptionKey when TryCaption(value, out MediaTileCaption caption): layout = layout with { Caption = caption }; break;
                case SizeKey when value is "s" or "m" or "l":
                    layout = layout with { TileSize = value == "s" ? MediaTileSize.Small : value == "l" ? MediaTileSize.Large : MediaTileSize.Medium };
                    break;
                case PanelKey when TryBit(value, out bool panel): layout = layout with { Panel = panel }; break;
                case PanelSideKey: layout = layout with { PanelSide = value == "left" ? MediaPanelSide.Left : MediaPanelSide.Right }; break;
                case PanelWidthKey when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int panelWidth):
                    layout = layout with { PanelWidth = Math.Clamp(panelWidth, MeowChan.Config.MediaPanelWidthMinimum, MeowChan.Config.MediaPanelWidthMaximum) };
                    break;
            }
        }
        return layout;
    }

    /// <summary>A saved text longer than this is not read (a layout text is under 100 characters): a documented ceiling of the format, not a setting.</summary>
    internal const int MaximumTextLength = 512;

    /// <summary>The canonical saved text for any text: parsed and written again.</summary>
    internal static string Canonical(string? text) => Parse(text, tagList: false).Format();

    private static bool TryBit(string value, out bool bit)
    {
        bit = value is "1" or "true" or "on";
        return bit || value is "0" or "false" or "off";
    }

    private static bool TryCaption(string value, out MediaTileCaption caption)
    {
        caption = MediaTileCaption.None;
        if (value == "none") return true;
        foreach (string name in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            (MediaTileCaption part, string known) = CaptionNames.FirstOrDefault(entry => entry.Name == name);
            if (known == null) { caption = MediaTileCaption.None; return false; } // one unknown part: the whole value is damaged
            caption |= part;
        }
        return value.Length > 0;
    }
}

/// <summary>
/// What a tile looks like at a size and with a caption: the measures in dip of its cell and its preview and which caption parts it shows.
/// The measures are layout measures of the tile (medium is today's tile), not limits of the user's data. The cell is only as high as its
/// rows: a tile without a name or source line is shorter by that line.
/// </summary>
internal readonly record struct MediaTileAppearance(double CellWidth, double CellHeight, double PreviewHeight, MediaTileCaption Caption)
{
    /// <summary>The longest side of the picture: the cell minus the tile's own 8 dip margins.</summary>
    internal double PictureWidth => CellWidth - 16;
    internal bool Shows(MediaTileCaption part) => (Caption & part) == part;
}

/// <summary>The measures of the tile sizes (pure).</summary>
internal static class MediaTileMetrics
{
    internal const double SmallCellWidth = 150, MediumCellWidth = 190, LargeCellWidth = 250;
    internal const double SmallPreviewHeight = 90, MediumPreviewHeight = 120, LargePreviewHeight = 170;
    /// <summary>What the rows under the picture take besides the name and source lines: margins, the size line, the tag row and the row gaps.</summary>
    internal const double BaseBelowPreview = 106;
    /// <summary>The height of the name line and of the source line with the 2 dip row gap in front of it (what a tile without them gives back).</summary>
    internal const double NameLine = 20, SourceLine = 18;

    /// <summary>The medium tile with every part: today's tile, 190 by 226 dip.</summary>
    internal static MediaTileAppearance Standard { get; } = For(MediaTileSize.Medium, MediaTileCaption.All);

    internal static MediaTileAppearance For(MediaTileSize size, MediaTileCaption caption)
    {
        (double width, double preview) = size switch
        {
            MediaTileSize.Small => (SmallCellWidth, SmallPreviewHeight),
            MediaTileSize.Large => (LargeCellWidth, LargePreviewHeight),
            _ => (MediumCellWidth, MediumPreviewHeight)
        };
        double height = preview + BaseBelowPreview
            - (caption.HasFlag(MediaTileCaption.Name) ? 0 : NameLine) - (caption.HasFlag(MediaTileCaption.Source) ? 0 : SourceLine);
        return new MediaTileAppearance(width, height, preview, caption & MediaTileCaption.All);
    }
}

/// <summary>What the page knows besides the layout that decides which blocks show: the focus mode and whether the status line has something to say that must not be lost.</summary>
internal readonly record struct MediaPageLayoutState(bool FocusMode, bool StatusNeedsAttention);

/// <summary>
/// MP5: the layout turned into what the page shows now (pure, no control): state in, visible blocks out. The focus mode keeps working as
/// before (it takes the title, the subtitle, the toolbar, the chips and the status line away and brings them back as chosen); the tag list
/// and the tiles stay as chosen in it. A status line the user left out still shows while it carries a problem (the archive could not be
/// read, threads are missing, the saved index is over its limit) or while there is nothing to count yet, because those sentences are not
/// information the user can do without. Applying a plan only changes visibility, side, width and the tile appearance; it never presents the grid again.
/// </summary>
internal sealed record MediaPageLayoutPlan(bool Title, bool Subtitle, bool Toolbar, bool FilterChips, bool StatusLine, bool TagList,
    MediaTagListSide TagListSide, double TagListWidth, bool Panel, MediaPanelSide PanelSide, double PanelWidth, MediaTileAppearance Tiles)
{
    internal static MediaPageLayoutPlan Of(MediaPageLayout layout, MediaPageLayoutState state)
    {
        ArgumentNullException.ThrowIfNull(layout);
        MediaPageLayout self = layout.Normalized();
        bool header = !state.FocusMode;
        return new MediaPageLayoutPlan(
            Title: header,
            Subtitle: header && self.Subtitle,
            Toolbar: header,
            FilterChips: header && self.FilterChips,
            StatusLine: header && (self.StatusLine || state.StatusNeedsAttention),
            TagList: self.TagList,
            TagListSide: self.TagListSide,
            TagListWidth: self.TagListWidth,
            Panel: self.Panel,
            PanelSide: self.PanelSide,
            PanelWidth: self.PanelWidth,
            Tiles: MediaTileMetrics.For(self.TileSize, self.Caption));
    }

    /// <summary>The grid keeps at least this much room beside the tag list (about one tile), however wide the list is saved: a layout measure.</summary>
    internal const double MinimumGridWidth = 200;
    /// <summary>The page margins, the handle and the list's own margin that sit beside the grid in the body row (a layout measure).</summary>
    internal const double BodyChrome = 12 + 6 + 12;

    /// <summary>
    /// The widest the tag list may be in a body row of this width: its range's maximum, less where the grid would fall under
    /// <see cref="MinimumGridWidth"/>; never below the range's minimum. An unknown width (not measured yet) leaves the range as it is.
    /// </summary>
    internal static double EffectiveTagListMaximum(double bodyWidth)
    {
        double maximum = MeowChan.Config.MediaTagListWidthMaximum;
        if (double.IsFinite(bodyWidth) && bodyWidth > 0) maximum = Math.Min(maximum, bodyWidth - MinimumGridWidth - BodyChrome);
        return Math.Max(MeowChan.Config.MediaTagListWidthMinimum, maximum);
    }

    /// <summary>The width the tag list gets in a body row of this width: the saved width within what <see cref="EffectiveTagListMaximum"/> allows.</summary>
    internal static double EffectiveTagListWidth(double savedWidth, double bodyWidth) =>
        Math.Clamp(savedWidth, MeowChan.Config.MediaTagListWidthMinimum, EffectiveTagListMaximum(bodyWidth));
}

/// <summary>
/// The widths of the body row with the media panel (pure, no control): what the tag list and the panel get beside the grid in a body row of
/// this width, and whether the panel shows at all. The panel is chosen in the layout (default on) and hides by itself while the body row is
/// narrower than the minimum page width setting, or when even its smallest width would leave the grid under <see cref="MediaPageLayoutPlan.MinimumGridWidth"/>;
/// the choice stays, the panel returns when the page is wide again. The tag list and the panel can both be shown: the panel keeps its smallest width
/// first, then the tag list takes what it needs, and the grid keeps the rest (never less than its minimum). An unknown width (not laid out yet)
/// counts as wide enough, so opening the page does not lay it out twice.
/// </summary>
internal readonly record struct MediaPanelColumns(bool PanelShown, bool CollapsedByWidth, double TagListWidth, double PanelWidth)
{
    /// <summary>What the panel's column adds beside the grid besides its width: its edge margin and the drag handle (a layout measure).</summary>
    internal const double PanelChrome = 12 + 6 + 4;
    internal const double MinimumCombinedContentWidth = 616;

    internal static MediaPanelColumns Of(MediaPageLayout layout, double bodyWidth, int minimumPageWidth)
    {
        ArgumentNullException.ThrowIfNull(layout);
        MediaPageLayout self = layout.Normalized();
        bool known = double.IsFinite(bodyWidth) && bodyWidth > 0;
        int minimum = Math.Clamp(minimumPageWidth, MeowChan.Config.MediaPanelMinimumPageWidthMinimum, MeowChan.Config.MediaPanelMinimumPageWidthMaximum);
        bool collapsed = self.Panel && known && bodyWidth < minimum;
        bool wanted = self.Panel && !collapsed;
        double contentMinimum = self.TagList && wanted ? MinimumCombinedContentWidth : MediaPageLayoutPlan.MinimumGridWidth;
        double TagListFor(bool reservePanel) => !self.TagList ? 0 : !known ? self.TagListWidth : Math.Clamp(self.TagListWidth, MeowChan.Config.MediaTagListWidthMinimum,
            Math.Max(MeowChan.Config.MediaTagListWidthMinimum, Math.Min(MeowChan.Config.MediaTagListWidthMaximum,
                bodyWidth - contentMinimum - MediaPageLayoutPlan.BodyChrome
                - (reservePanel ? MeowChan.Config.MediaPanelWidthMinimum + PanelChrome : 0))));
        double tagList = TagListFor(wanted);
        double panel = self.PanelWidth;
        if (known && wanted)
        {
            double room = bodyWidth - contentMinimum - MediaPageLayoutPlan.BodyChrome - PanelChrome - tagList;
            if (room < MeowChan.Config.MediaPanelWidthMinimum) { wanted = false; collapsed = true; tagList = TagListFor(false); }
            else panel = Math.Clamp(self.PanelWidth, MeowChan.Config.MediaPanelWidthMinimum, Math.Min(MeowChan.Config.MediaPanelWidthMaximum, room));
        }
        return new MediaPanelColumns(wanted, collapsed, tagList, wanted ? panel : 0);
    }

    /// <summary>
    /// The body width from which the panel fits at all: the page-width setting, or more where the smallest panel, the smallest tag list (when shown) and the
    /// grid's minimum need more. The Layout menu names it when the choice stays hidden, so the sentence is true both for a narrow page and for no room beside the tag list.
    /// </summary>
    internal static double MinimumBodyWidth(MediaPageLayout layout, int minimumPageWidth)
    {
        MediaPageLayout self = layout.Normalized();
        int setting = Math.Clamp(minimumPageWidth, MeowChan.Config.MediaPanelMinimumPageWidthMinimum, MeowChan.Config.MediaPanelMinimumPageWidthMaximum);
        double contentMinimum = self.TagList ? MinimumCombinedContentWidth : MediaPageLayoutPlan.MinimumGridWidth;
        double needed = contentMinimum + MediaPageLayoutPlan.BodyChrome + PanelChrome + MeowChan.Config.MediaPanelWidthMinimum
            + (self.TagList ? MeowChan.Config.MediaTagListWidthMinimum : 0);
        return Math.Ceiling(Math.Max(setting, needed));
    }

    /// <summary>
    /// The widest the tag list may be while the panel is shown at <paramref name="panelWidth"/> in a body row of this width (the list's drag handle's limit): the list's
    /// own limit less what the panel takes; never below the list's minimum. Without a shown panel it is <see cref="MediaPageLayoutPlan.EffectiveTagListMaximum"/>.
    /// </summary>
    internal static double MaximumTagListWidth(double bodyWidth, bool panelShown, double panelWidth)
    {
        double maximum = MediaPageLayoutPlan.EffectiveTagListMaximum(bodyWidth);
        if (!panelShown || !double.IsFinite(bodyWidth) || bodyWidth <= 0) return maximum;
        return Math.Max(MeowChan.Config.MediaTagListWidthMinimum, Math.Min(maximum, bodyWidth - MinimumCombinedContentWidth - MediaPageLayoutPlan.BodyChrome - PanelChrome - panelWidth));
    }

    /// <summary>The widest the panel may be in a body row of this width with this layout (the drag handle's limit); its range's maximum when the width is unknown.</summary>
    internal static double MaximumPanelWidth(MediaPageLayout layout, double bodyWidth)
    {
        double maximum = MeowChan.Config.MediaPanelWidthMaximum;
        if (!double.IsFinite(bodyWidth) || bodyWidth <= 0) return maximum;
        MediaPageLayout self = layout.Normalized();
        double tagList = self.TagList ? Math.Min(self.TagListWidth, MediaPageLayoutPlan.EffectiveTagListMaximum(bodyWidth)) : 0;
        double contentMinimum = self.TagList ? MinimumCombinedContentWidth : MediaPageLayoutPlan.MinimumGridWidth;
        double room = bodyWidth - contentMinimum - MediaPageLayoutPlan.BodyChrome - PanelChrome - tagList;
        return Math.Max(MeowChan.Config.MediaPanelWidthMinimum, Math.Min(maximum, room));
    }
}

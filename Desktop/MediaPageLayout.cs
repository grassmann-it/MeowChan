using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MeowChan.Desktop;

/// <summary>Which side of the grid the tag list stands on.</summary>
internal enum MediaTagListSide { Left, Right }

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
internal enum MediaLayoutPreset { Standard, BooruView, GridOnly }

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

    /// <summary>The same layout with the width brought into its range.</summary>
    internal MediaPageLayout Normalized() => this with
    {
        TagListWidth = Math.Clamp(TagListWidth, MeowChan.Config.MediaTagListWidthMinimum, MeowChan.Config.MediaTagListWidthMaximum),
        Caption = Caption & MediaTileCaption.All,
        TagListSide = Enum.IsDefined(TagListSide) ? TagListSide : MediaTagListSide.Left,
        TileSize = Enum.IsDefined(TileSize) ? TileSize : MediaTileSize.Medium
    };

    /// <summary>
    /// The layout a preset makes. The width of the tag list is the user's measure and stays (only "Reset" takes the default width back).
    /// Standard is today's page; the Booru view puts the tag list on the left with compact tiles that show the tag count (and the queue
    /// mark), the chips and the status line on and the subtitle off; the grid only keeps nothing but the toolbar and the tiles.
    /// </summary>
    internal static MediaPageLayout For(MediaLayoutPreset preset, MediaPageLayout current) => preset switch
    {
        MediaLayoutPreset.BooruView => new MediaPageLayout
        {
            TagList = true, TagListSide = MediaTagListSide.Left, TagListWidth = current.Normalized().TagListWidth, FilterChips = true, StatusLine = true,
            Subtitle = false, Caption = MediaTileCaption.TagCount | MediaTileCaption.QueueMark, TileSize = MediaTileSize.Small
        },
        MediaLayoutPreset.GridOnly => new MediaPageLayout
        {
            TagList = false, TagListSide = current.Normalized().TagListSide, TagListWidth = current.Normalized().TagListWidth, FilterChips = false,
            StatusLine = false, Subtitle = false, Caption = MediaTileCaption.None, TileSize = MediaTileSize.Medium
        },
        _ => Standard with { TagListSide = current.Normalized().TagListSide, TagListWidth = current.Normalized().TagListWidth }
    };

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
        && a.Caption == b.Caption && a.TileSize == b.TileSize && (!a.TagList || a.TagListSide == b.TagListSide);

    // ---- The saved text ----

    private const string SideKey = "side", WidthKey = "width", ChipsKey = "chips", StatusKey = "status", SubtitleKey = "subtitle", CaptionKey = "caption", SizeKey = "size";
    private static readonly (MediaTileCaption Part, string Name)[] CaptionNames =
    [
        (MediaTileCaption.Name, "name"), (MediaTileCaption.TagCount, "tags"), (MediaTileCaption.QueueMark, "queue"), (MediaTileCaption.Source, "source")
    ];

    /// <summary>
    /// The saved text of everything but the tag list switch (that one lives in <see cref="MeowChan.Config.MediaBooruMode"/>):
    /// <c>side=left;width=256;chips=1;status=1;subtitle=1;caption=name,tags,queue,source;size=m</c>. A caption without parts is <c>caption=none</c>.
    /// </summary>
    internal string Format()
    {
        MediaPageLayout self = Normalized();
        string caption = self.Caption == MediaTileCaption.None ? "none" : string.Join(',', CaptionNames.Where(part => self.Caption.HasFlag(part.Part)).Select(part => part.Name));
        return string.Join(';',
            SideKey + "=" + (self.TagListSide == MediaTagListSide.Right ? "right" : "left"),
            WidthKey + "=" + self.TagListWidth.ToString(CultureInfo.InvariantCulture),
            ChipsKey + "=" + Bit(self.FilterChips),
            StatusKey + "=" + Bit(self.StatusLine),
            SubtitleKey + "=" + Bit(self.Subtitle),
            CaptionKey + "=" + caption,
            SizeKey + "=" + self.TileSize switch { MediaTileSize.Small => "s", MediaTileSize.Large => "l", _ => "m" });
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
    MediaTagListSide TagListSide, double TagListWidth, MediaTileAppearance Tiles)
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

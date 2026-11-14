using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using SkiaSharp;

namespace MeowChan.Desktop.Controls;

// Unified media badges (MB1a): the small flags every thumbnail carries in the same place and look on every surface. Three layers, so the
// rules are tested without a display:
//   MediaBadgeModel   state of a medium            -> the badges that apply (corner, priority, icon, label)
//   MediaBadgeLayout  badges + size of the picture -> where each one sits (unit, collapse to dots, per-corner limit, overlap guard)
//   MediaBadgeView    one render-only control per thumbnail that draws such a plan (no child controls, nothing allocated per frame)
// Corners carry a meaning: top left the verdict (favourite, rating), top right the state (file missing, queue / tagging state, booru source),
// bottom left the kind (video duration, GIF), bottom right the tags. The centre belongs to the play mark alone.

/// <summary>What kind of file a medium is, as far as the badges care.</summary>
internal enum MediaBadgeKind { Image, Video, Gif }

/// <summary>The tagging page's own state of a medium (TG1): not part of a surface's badges unless that surface is the tagging page.</summary>
internal enum MediaTaggingState { None, Done, Skipped }

/// <summary>The badges, in the order that breaks a tie of priority.</summary>
internal enum MediaBadgeId { Missing, Favorite, PlayMark, Video, Gif, Queued, Tagging, TagCount, Untagged, Rating, Booru }

internal enum MediaBadgeCorner { TopLeft, TopRight, BottomLeft, BottomRight, Center }

/// <summary>Normal: the play mark is 45 percent of the shorter side; dense (a filmstrip tile): 32 percent.</summary>
internal enum MediaBadgeDensity { Normal, Dense }

/// <summary>
/// Who draws the play mark of a video. <b>Drawn</b>: the badge view (and the duration chip then has no play glyph). <b>External</b>: the surface
/// keeps its own mark (a play button that is also a control); the chip is the same as with a drawn one. <b>None</b>: no mark at all, so the
/// bottom left chip carries the play glyph.
/// </summary>
internal enum MediaPlayMarkMode { Drawn, External, None }

/// <summary>
/// What the corner badges are measured and placed against. <b>Picture</b>: the shown picture (the badges hug a letterboxed picture, so a portrait
/// and a landscape tile carry them at different heights). <b>Slot</b>: the tile's own slot, independent of the letterbox: every tile of a row
/// carries the same badge at the same place, and a badge may lie outside the picture over the dark slot. The centred play mark stays on the picture.
/// </summary>
internal enum MediaBadgeAnchor { Picture, Slot }

/// <summary>How much a picture of this size can carry: nothing, one dot per corner, or the full badges.</summary>
internal enum MediaBadgeMode { Hidden, Dots, Full }

/// <summary>Everything a thumbnail's badges depend on. The default state has no badge at all.</summary>
/// <param name="Favorite">The medium is a favourite.</param>
/// <param name="Rating">The own rating (1 to 5); null or below 1: not rated.</param>
/// <param name="Kind">Image, video or animated image (GIF).</param>
/// <param name="Duration">The length of a video when it is known.</param>
/// <param name="TagCount">The number of own tags; null: not known, 0: untagged.</param>
/// <param name="Queued">The tagging queue holds the medium (not set on the tagging page itself, where that is implicit).</param>
/// <param name="Tagging">Done or skipped on the tagging page.</param>
/// <param name="Missing">The file is not (or no longer) there.</param>
/// <param name="Booru">The medium comes from a booru.</param>
internal readonly record struct MediaBadgeState(
    bool Favorite = false, int? Rating = null, MediaBadgeKind Kind = MediaBadgeKind.Image, TimeSpan? Duration = null,
    int? TagCount = null, bool Queued = false, MediaTaggingState Tagging = MediaTaggingState.None, bool Missing = false, bool Booru = false);

/// <summary>What a surface shows of the badge set: two badges are opt-in per surface, the play mark and the density depend on the surface's picture.</summary>
/// <param name="ShowUntagged">The "no tags yet" mark (media page and tagging grid only, F7).</param>
/// <param name="ShowBooru">The booru source mark (media page and tagging grid only, F6).</param>
/// <param name="PlayMark">Who draws the play mark.</param>
/// <param name="Density">The size of the play mark.</param>
/// <param name="Anchor">What the corner badges, the unit and the limits follow: the shown picture (default) or the tile's slot.</param>
internal readonly record struct MediaBadgeOptions(
    bool ShowUntagged = false, bool ShowBooru = false, MediaPlayMarkMode PlayMark = MediaPlayMarkMode.Drawn, MediaBadgeDensity Density = MediaBadgeDensity.Normal,
    MediaBadgeAnchor Anchor = MediaBadgeAnchor.Picture)
{
    /// <summary>No opt-in badge, a drawn mark: the base of every surface.</summary>
    internal static MediaBadgeOptions Default => new();
    /// <summary>The media page's tiles: with the untagged and booru marks.</summary>
    internal static MediaBadgeOptions MediaPage => new(ShowUntagged: true, ShowBooru: true);
    /// <summary>The tagging page's grid and cards: with the untagged and booru marks.</summary>
    internal static MediaBadgeOptions TaggingGrid => new(ShowUntagged: true, ShowBooru: true);
    /// <summary>The archive's attachment tiles and the thread list's thumbnails.</summary>
    internal static MediaBadgeOptions Archive => new();
    /// <summary>The gallery filmstrip: small tiles, the dense play mark.</summary>
    internal static MediaBadgeOptions Filmstrip => new(Density: MediaBadgeDensity.Dense);
    /// <summary>The reader's media sidebar: the sidebar keeps its own play button.</summary>
    internal static MediaBadgeOptions Sidebar => new(PlayMark: MediaPlayMarkMode.External);
    /// <summary>Every fact, for a description that does not depend on a surface.</summary>
    internal static MediaBadgeOptions Everything => new(ShowUntagged: true, ShowBooru: true);
}

/// <summary>One badge that applies: where it goes, how important it is (1 stays longest), which icon and which short text.</summary>
/// <param name="Id">Which badge.</param>
/// <param name="Corner">The corner it belongs to.</param>
/// <param name="Priority">1 is kept the longest; a corner that is full drops the highest numbers first.</param>
/// <param name="Icon">A mask of Assets/icons/feather/masks (file name without extension), <see cref="MediaBadgeIcons.Untagged"/>, or null (play mark, text chip).</param>
/// <param name="Label">The text of the chip (duration, "GIF", a count, a rating), or null.</param>
internal readonly record struct MediaBadge(MediaBadgeId Id, MediaBadgeCorner Corner, int Priority, string? Icon, string? Label);

/// <summary>The icons the badges use: existing masks of the feather catalog, plus the one drawn mark of the "no tags" badge.</summary>
internal static class MediaBadgeIcons
{
    internal const string Missing = "x-circle", Favorite = "star-filled", Play = "play", Queued = "clipboard", Done = "check",
        Skipped = "skip-forward", Tag = "tag", Rating = "star", Booru = "globe";
    /// <summary>Not a mask: the existing drawn mark of the untagged state (<see cref="WorkspaceChips.MarkUntagged"/>).</summary>
    internal const string Untagged = "untagged";
    /// <summary>The masks the badges can use (the lint of the assets in the tests).</summary>
    internal static readonly string[] Masks = [Missing, Favorite, Play, Queued, Done, Skipped, Tag, Rating, Booru];
}

/// <summary>
/// The three user limits of the badges (Settings, Advanced; <see cref="Config.MediaBadgeCollapseBelow"/> and the two after it). Immutable; the
/// current values are handed over once with <see cref="Configure(Config)"/> when a profile's limits are applied, and every badge view reads them
/// again on its next <see cref="MediaBadgeView.Apply(in MediaBadgeState, Size)"/>.
/// </summary>
internal sealed record MediaBadgeLimits(int CollapseBelow, int HideBelow, int MaxPerCorner)
{
    private static volatile MediaBadgeLimits _current = new MediaBadgeLimits(
        Config.MediaBadgeCollapseBelowDefault, Config.MediaBadgeHideBelowDefault, Config.MediaBadgeMaxPerCornerDefault).Normalize();

    /// <summary>The limits in force.</summary>
    internal static MediaBadgeLimits Current => _current;

    /// <summary>The limits of a default profile.</summary>
    internal static MediaBadgeLimits Default { get; } = new MediaBadgeLimits(
        Config.MediaBadgeCollapseBelowDefault, Config.MediaBadgeHideBelowDefault, Config.MediaBadgeMaxPerCornerDefault).Normalize();

    internal static void Configure(Config config) => Configure(config.MediaBadgeCollapseBelow, config.MediaBadgeHideBelow, config.MediaBadgeMaxPerCorner);

    internal static void Configure(int collapseBelow, int hideBelow, int maxPerCorner) =>
        _current = new MediaBadgeLimits(collapseBelow, hideBelow, maxPerCorner).Normalize();

    /// <summary>Every value inside its range, and the hide limit never above the collapse limit (a picture is hidden only below where it would be dots).</summary>
    internal MediaBadgeLimits Normalize()
    {
        int collapse = Math.Clamp(CollapseBelow, Config.MediaBadgeCollapseBelowMinimum, Config.MediaBadgeCollapseBelowMaximum);
        int hide = Math.Min(Math.Clamp(HideBelow, Config.MediaBadgeHideBelowMinimum, Config.MediaBadgeHideBelowMaximum), collapse);
        int perCorner = Math.Clamp(MaxPerCorner, Config.MediaBadgeMaxPerCornerMinimum, Config.MediaBadgeMaxPerCornerMaximum);
        return collapse == CollapseBelow && hide == HideBelow && perCorner == MaxPerCorner ? this : new MediaBadgeLimits(collapse, hide, perCorner);
    }
}

/// <summary>The rules that turn the state of a medium into the badges that apply to it (pure).</summary>
internal static class MediaBadgeModel
{
    /// <summary>A rating above this is shown as this.</summary>
    internal const int MaximumRating = 5;
    /// <summary>A tag count above this is shown as "99+" (the chip stays small).</summary>
    internal const int MaximumShownTags = 99;

    private static readonly MediaBadge[] None = [];

    /// <summary>
    /// The badges that apply to <paramref name="state"/> on a surface with <paramref name="options"/>, most important first (priority, then the
    /// badge's own order). Not yet limited by a corner's capacity or by the picture's size: that is <see cref="MediaBadgeLayout.Plan"/>.
    /// </summary>
    internal static MediaBadge[] Resolve(in MediaBadgeState state, in MediaBadgeOptions options)
    {
        List<MediaBadge>? list = null;
        void Add(MediaBadge badge) => (list ??= new List<MediaBadge>(6)).Add(badge);

        if (state.Missing) Add(new(MediaBadgeId.Missing, MediaBadgeCorner.TopRight, 1, MediaBadgeIcons.Missing, null));
        if (state.Favorite) Add(new(MediaBadgeId.Favorite, MediaBadgeCorner.TopLeft, 2, MediaBadgeIcons.Favorite, null));
        if (state.Kind == MediaBadgeKind.Video)
        {
            if (options.PlayMark == MediaPlayMarkMode.Drawn) Add(new(MediaBadgeId.PlayMark, MediaBadgeCorner.Center, 3, null, null));
            string? length = state.Duration is { } duration && duration >= TimeSpan.Zero ? MediaBadgeLayout.FormatDuration(duration) : null;
            // A mark in the middle says "video"; the chip then only adds the length. Without a mark the chip has to say it itself.
            if (length != null || options.PlayMark == MediaPlayMarkMode.None)
                Add(new(MediaBadgeId.Video, MediaBadgeCorner.BottomLeft, 3, options.PlayMark == MediaPlayMarkMode.None ? MediaBadgeIcons.Play : null, length));
        }
        else if (state.Kind == MediaBadgeKind.Gif) Add(new(MediaBadgeId.Gif, MediaBadgeCorner.BottomLeft, 3, null, "GIF"));
        if (state.Queued) Add(new(MediaBadgeId.Queued, MediaBadgeCorner.TopRight, 4, MediaBadgeIcons.Queued, null));
        if (state.Tagging == MediaTaggingState.Done) Add(new(MediaBadgeId.Tagging, MediaBadgeCorner.TopRight, 4, MediaBadgeIcons.Done, null));
        else if (state.Tagging == MediaTaggingState.Skipped) Add(new(MediaBadgeId.Tagging, MediaBadgeCorner.TopRight, 4, MediaBadgeIcons.Skipped, null));
        if (state.TagCount is > 0 and var tags)
            Add(new(MediaBadgeId.TagCount, MediaBadgeCorner.BottomRight, 5, MediaBadgeIcons.Tag, TagLabel(tags)));
        else if (state.TagCount == 0 && options.ShowUntagged)
            Add(new(MediaBadgeId.Untagged, MediaBadgeCorner.BottomRight, 6, MediaBadgeIcons.Untagged, null));
        if (state.Rating is >= 1 and var rating)
            Add(new(MediaBadgeId.Rating, MediaBadgeCorner.TopLeft, 7, MediaBadgeIcons.Rating, Math.Min(rating, MaximumRating).ToString(CultureInfo.InvariantCulture)));
        if (state.Booru && options.ShowBooru) Add(new(MediaBadgeId.Booru, MediaBadgeCorner.TopRight, 8, MediaBadgeIcons.Booru, null));

        if (list == null) return None;
        MediaBadge[] result = list.ToArray();
        Array.Sort(result, static (a, b) => a.Priority != b.Priority ? a.Priority.CompareTo(b.Priority) : a.Id.CompareTo(b.Id));
        return result;
    }

    internal static string TagLabel(int tags) =>
        tags > MaximumShownTags ? MaximumShownTags.ToString(CultureInfo.InvariantCulture) + "+" : tags.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A badge with its place on its area: the picture, or the slot with <see cref="MediaBadgeAnchor.Slot"/> (area coordinates, dip). <paramref name="Dot"/>: only the dot of a collapsed corner.</summary>
internal readonly record struct PlacedBadge(MediaBadge Badge, Rect Bounds, bool Dot);

/// <summary>The plan of one picture's badges: the mode its size allows, the unit and the badges with their places.</summary>
internal sealed record MediaBadgePlan(MediaBadgeMode Mode, double Unit, double FontSize, PlacedBadge[] Badges)
{
    /// <summary>No badge.</summary>
    internal static MediaBadgePlan Empty { get; } = new(MediaBadgeMode.Hidden, 0, 0, []);
}

/// <summary>
/// The geometry of the badges (pure, no control). The unit <c>u</c> follows the picture that is really shown: <c>u = clamp(even(0.2 x shorter
/// side), 12, 20)</c>, so a 64 dip tile gets 12, a 76 dip row 16 and a 160 dip tile 20. A chip is <c>u</c> high with round ends. Below the
/// collapse limit every corner shows only its most important badge as a dot; below the hide limit nothing is shown.
/// </summary>
internal static class MediaBadgeLayout
{
    internal const double UnitRatio = 0.2, UnitMinimum = 12, UnitMaximum = 20;
    /// <summary>The diameter of the dot of a collapsed corner, and the space between two chips of a corner stack (dip).</summary>
    internal const double DotSize = 8, Gap = 2;
    /// <summary>The share of the unit the rating's star has, the room beside an icon that has a text, and the font size's share of the unit.</summary>
    internal const double RatingIconShare = 0.6, IconTextGap = 2, FontShare = 0.75;
    internal const double FontMinimum = 9, FontMaximum = 12;

    /// <summary>The shorter side of the picture; 0 when there is no usable picture.</summary>
    internal static double ShortSide(Size picture)
    {
        if (!double.IsFinite(picture.Width) || !double.IsFinite(picture.Height)) return 0;
        double shorter = Math.Min(picture.Width, picture.Height);
        return shorter > 0 ? shorter : 0;
    }

    /// <summary>The unit for a picture whose shorter side is <paramref name="shortSide"/>: the nearest even number to 20 percent of it, from 12 to 20.</summary>
    internal static double Unit(double shortSide)
    {
        if (!double.IsFinite(shortSide) || shortSide <= 0) return UnitMinimum;
        return Math.Clamp(Math.Round(UnitRatio * shortSide / 2, MidpointRounding.AwayFromZero) * 2, UnitMinimum, UnitMaximum);
    }

    /// <summary>The font size of a chip's text: 75 percent of the unit, from 9 to 12.</summary>
    internal static double FontSize(double unit) => Math.Clamp(FontShare * unit, FontMinimum, FontMaximum);

    /// <summary>The distance from the picture's edge: a quarter of the unit, at least 3.</summary>
    internal static double Margin(double unit) => Math.Max(3, unit / 4);

    /// <summary>The side of an icon inside a chip: the unit less 4 (the rating's star is smaller).</summary>
    internal static double IconSize(MediaBadgeId id, double unit) => id == MediaBadgeId.Rating ? RatingIconShare * unit : unit - 4;

    /// <summary>What a picture of this shorter side can carry under these limits.</summary>
    internal static MediaBadgeMode ModeFor(double shortSide, MediaBadgeLimits limits)
    {
        if (!double.IsFinite(shortSide) || shortSide <= 0) return MediaBadgeMode.Hidden;
        limits = limits.Normalize();
        return shortSide < limits.HideBelow ? MediaBadgeMode.Hidden : shortSide < limits.CollapseBelow ? MediaBadgeMode.Dots : MediaBadgeMode.Full;
    }

    /// <summary>The length of a video as "m:ss", from an hour on "h:mm:ss" (whole seconds, rounded down like a player).</summary>
    internal static string FormatDuration(TimeSpan duration)
    {
        long seconds = (long)Math.Floor(Math.Max(0, duration.TotalSeconds));
        long hours = seconds / 3600, minutes = seconds / 60 % 60, rest = seconds % 60;
        return hours > 0
            ? hours.ToString(CultureInfo.InvariantCulture) + ":" + minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + rest.ToString("00", CultureInfo.InvariantCulture)
            : (seconds / 60).ToString(CultureInfo.InvariantCulture) + ":" + rest.ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The width of a chip's text without a display: digits are tabular, the colon and the plus are narrow, capitals wide. Close enough that the
    /// text, which the view centres on its chip, never touches the ends.
    /// </summary>
    internal static double EstimateTextWidth(string text, double fontSize)
    {
        double em = 0;
        foreach (char c in text) em += char.IsDigit(c) ? 0.62 : c is ':' or '.' ? 0.3 : c == '+' ? 0.6 : c == 'I' ? 0.32 : 0.7;
        return em * fontSize;
    }

    /// <summary>The width of a chip: the unit for an icon alone, otherwise padding, icon, space and text, never narrower than the unit.</summary>
    internal static double ChipWidth(in MediaBadge badge, double unit, double fontSize)
    {
        bool icon = badge.Icon != null, text = badge.Label != null;
        if (icon && !text) return unit;
        double padding = unit / 4;
        double width = 2 * padding + (text ? EstimateTextWidth(badge.Label!, fontSize) : 0)
            + (icon ? IconSize(badge.Id, unit) + (text ? IconTextGap : 0) : 0);
        return Math.Max(unit, width);
    }

    /// <summary>The plan with the limits in force.</summary>
    internal static MediaBadgePlan Plan(in MediaBadgeState state, Size picture, in MediaBadgeOptions options) =>
        Plan(state, picture, options, MediaBadgeLimits.Current);

    /// <summary>
    /// The area the badges are measured and placed on: the picture, or with <see cref="MediaBadgeAnchor.Slot"/> the slot when it is usable (a slot
    /// that is not falls back to the picture).
    /// </summary>
    internal static Size Area(Size picture, Size slot, MediaBadgeAnchor anchor) =>
        anchor == MediaBadgeAnchor.Slot && ShortSide(slot) > 0 ? slot : picture;

    /// <summary>
    /// The picture's rectangle in the coordinates of the area: the whole area when the anchor is the picture or the picture is not usable,
    /// otherwise the picture centred in the slot (the letterbox centres it) and never larger than the slot.
    /// </summary>
    internal static Rect PictureIn(Size area, Size picture)
    {
        if (ShortSide(picture) <= 0) return new Rect(default, area);
        double width = Math.Min(picture.Width, area.Width), height = Math.Min(picture.Height, area.Height);
        return new Rect((area.Width - width) / 2, (area.Height - height) / 2, width, height);
    }

    /// <summary>The plan of a picture in a slot (the slot matters only with <see cref="MediaBadgeAnchor.Slot"/>) with the limits in force.</summary>
    internal static MediaBadgePlan Plan(in MediaBadgeState state, Size picture, Size slot, in MediaBadgeOptions options) =>
        Plan(state, picture, slot, options, MediaBadgeLimits.Current);

    internal static MediaBadgePlan Plan(in MediaBadgeState state, Size picture, in MediaBadgeOptions options, MediaBadgeLimits limits) =>
        Plan(state, picture, picture, options, limits);

    /// <summary>
    /// The badges of a medium on a picture of this size (or in this slot, <see cref="MediaBadgeAnchor.Slot"/>): the badges that apply, the corners' capacity (<see cref="MediaBadgeLimits.MaxPerCorner"/>,
    /// the most important first and next to the corner), the collapse to dots and the hiding of everything below the limits. When two stacks of
    /// one edge would meet, the least important badge of either drops out (an edge keeps at least one badge).
    /// </summary>
    internal static MediaBadgePlan Plan(in MediaBadgeState state, Size picture, Size slotSize, in MediaBadgeOptions options, MediaBadgeLimits limits)
    {
        limits = limits.Normalize();
        // With the slot as anchor the unit, the limits and the corners follow the slot (the same for every picture of a tile row); the centred play mark follows the picture.
        Size area = Area(picture, slotSize, options.Anchor);
        double shorter = ShortSide(area);
        MediaBadgeMode mode = ModeFor(shorter, limits);
        if (mode == MediaBadgeMode.Hidden) return MediaBadgePlan.Empty;
        MediaBadge[] badges = MediaBadgeModel.Resolve(state, options);
        if (badges.Length == 0) return MediaBadgePlan.Empty;

        double unit = Unit(shorter), font = FontSize(unit), margin = Margin(unit);
        double width = area.Width, height = area.Height;
        int capacity = mode == MediaBadgeMode.Dots ? 1 : limits.MaxPerCorner;
        List<PlacedBadge> placed = new(badges.Length);

        foreach (MediaBadge badge in badges)
            if (badge.Corner == MediaBadgeCorner.Center)
            {
                double ratio = options.Density == MediaBadgeDensity.Dense ? VideoPlayMark.DenseRatio : VideoPlayMark.Ratio;
                placed.Add(new(badge, VideoPlayMark.Place(PictureIn(area, picture), ratio), false));
            }

        // The members of each corner, most important first, up to its capacity.
        Span<int> members = stackalloc int[4 * 3];
        Span<int> counts = stackalloc int[4];
        for (int index = 0; index < badges.Length; index++)
        {
            int corner = (int)badges[index].Corner;
            if (corner > (int)MediaBadgeCorner.BottomRight || counts[corner] >= capacity) continue;
            members[corner * 3 + counts[corner]++] = index;
        }

        if (mode == MediaBadgeMode.Dots)
        {
            for (int corner = 0; corner < 4; corner++)
            {
                if (counts[corner] == 0) continue;
                double x = corner is (int)MediaBadgeCorner.TopLeft or (int)MediaBadgeCorner.BottomLeft ? margin : width - margin - DotSize;
                double y = corner is (int)MediaBadgeCorner.TopLeft or (int)MediaBadgeCorner.TopRight ? margin : height - margin - DotSize;
                placed.Add(new(badges[members[corner * 3]], new Rect(x, y, DotSize, DotSize), true));
            }
            return new(mode, unit, font, placed.ToArray());
        }

        Span<double> widths = stackalloc double[4 * 3];
        for (int corner = 0; corner < 4; corner++)
            for (int slot = 0; slot < counts[corner]; slot++)
                widths[corner * 3 + slot] = ChipWidth(badges[members[corner * 3 + slot]], unit, font);

        // Two stacks of one edge (left and right) must not meet.
        double available = width - 2 * margin - Gap;
        ResolveEdgeOverlap(badges, members, counts, widths, (int)MediaBadgeCorner.TopLeft, (int)MediaBadgeCorner.TopRight, available);
        ResolveEdgeOverlap(badges, members, counts, widths, (int)MediaBadgeCorner.BottomLeft, (int)MediaBadgeCorner.BottomRight, available);

        for (int corner = 0; corner < 4; corner++)
        {
            bool left = corner is (int)MediaBadgeCorner.TopLeft or (int)MediaBadgeCorner.BottomLeft;
            double y = corner is (int)MediaBadgeCorner.TopLeft or (int)MediaBadgeCorner.TopRight ? margin : height - margin - unit;
            double edge = left ? margin : width - margin;
            for (int slot = 0; slot < counts[corner]; slot++)
            {
                double w = widths[corner * 3 + slot];
                double x = left ? edge : edge - w;
                placed.Add(new(badges[members[corner * 3 + slot]], new Rect(x, y, w, unit), false));
                edge = left ? x + w + Gap : x - Gap;
            }
        }
        return new(mode, unit, font, placed.ToArray());
    }

    private static void ResolveEdgeOverlap(MediaBadge[] badges, Span<int> members, Span<int> counts, Span<double> widths, int left, int right, double available)
    {
        // While the two stacks of the edge would meet, the least important badge of either gives way (a tie goes to the longer stack, then to
        // the right one). The edge keeps at least one badge: a single chip is never wider than a picture of 48 dip or more.
        while (Extent(widths, counts, left) + Extent(widths, counts, right) > available && counts[left] + counts[right] > 1)
        {
            int lastLeft = counts[left] > 0 ? badges[members[left * 3 + counts[left] - 1]].Priority : -1;
            int lastRight = counts[right] > 0 ? badges[members[right * 3 + counts[right] - 1]].Priority : -1;
            int drop = lastLeft > lastRight || lastLeft == lastRight && counts[left] >= counts[right] ? left : right;
            counts[drop]--;
        }
    }

    private static double Extent(Span<double> widths, Span<int> counts, int corner)
    {
        double sum = 0;
        for (int slot = 0; slot < counts[corner]; slot++) sum += widths[corner * 3 + slot] + (slot > 0 ? Gap : 0);
        return sum;
    }
}

/// <summary>The spoken form of a medium's badges: one sentence for the AutomationName or HelpText of a tile, row or filmstrip item.</summary>
internal static class MediaBadgeText
{
    private const string Separator = " · ";

    /// <summary>Every fact of the state, most important first, for example "File missing · Favorite · Video 0:42 · 3 tags · Rating 4 of 5". Empty when there is none.</summary>
    internal static string Describe(in MediaBadgeState state) => Describe(state, MediaBadgeOptions.Everything);

    /// <summary>
    /// The facts a surface shows: the same sentence, without the opt-in ones (no tags, booru source) that surface leaves out. Collapsed dots
    /// never change it: the sentence is the whole state, not what fits on the picture.
    /// </summary>
    internal static string Describe(in MediaBadgeState state, in MediaBadgeOptions options)
    {
        List<string>? parts = null;
        void Add(string text) => (parts ??= new List<string>(6)).Add(text);

        if (state.Missing) Add(Texts.T("media.badge.missing"));
        if (state.Favorite) Add(Texts.T("media.badge.favorite"));
        if (state.Kind == MediaBadgeKind.Video)
            Add(state.Duration is { } duration && duration >= TimeSpan.Zero
                ? Texts.T("media.badge.videoDuration", MediaBadgeLayout.FormatDuration(duration)) : Texts.T("media.badge.video"));
        else if (state.Kind == MediaBadgeKind.Gif) Add(Texts.T("media.badge.gif"));
        if (state.Queued) Add(Texts.T("media.badge.queued"));
        if (state.Tagging == MediaTaggingState.Done) Add(Texts.T("media.badge.tagging.done"));
        else if (state.Tagging == MediaTaggingState.Skipped) Add(Texts.T("media.badge.tagging.skipped"));
        if (state.TagCount is > 0 and var tags) Add(Texts.Count("media.badge.tags", tags));
        else if (state.TagCount == 0 && options.ShowUntagged) Add(Texts.T("media.badge.untagged"));
        if (state.Rating is >= 1 and var rating) Add(Texts.T("media.badge.rating", Math.Min(rating, MediaBadgeModel.MaximumRating)));
        if (state.Booru && options.ShowBooru) Add(Texts.T("media.badge.booru"));
        return parts == null ? string.Empty : string.Join(Separator, parts);
    }
}

/// <summary>
/// The white copies of the badge masks the view draws with, made once per icon from the catalog's masks (black with an alpha channel) and kept for
/// the life of the process: a drawn bitmap is much cheaper than a mask layer on every tile. Needs the platform; the view calls it while it draws.
/// </summary>
internal static class MediaBadgeIconBitmaps
{
    private static readonly Dictionary<string, Bitmap> Cache = new(StringComparer.Ordinal);

    internal static Bitmap Get(string icon)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(icon, out Bitmap? bitmap)) return bitmap;
            using Stream source = AssetLoader.Open(new Uri(CommandBindings.IconUri(icon)));
            using SKBitmap mask = SKBitmap.Decode(source);
            for (int y = 0; y < mask.Height; y++)
                for (int x = 0; x < mask.Width; x++)
                    mask.SetPixel(x, y, new SKColor(255, 255, 255, mask.GetPixel(x, y).Alpha));
            using SKImage image = SKImage.FromBitmap(mask);
            using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
            using Stream output = png.AsStream();
            bitmap = new Bitmap(output);
            Cache.Add(icon, bitmap);
            return bitmap;
        }
    }
}

/// <summary>
/// The badges of one thumbnail: a single control that draws its <see cref="MediaBadgePlan"/> (a dark disc or pill with a white ring, a white icon
/// and text), so a grid of tens of thousands of virtualized tiles holds no child control per badge. It lies over the shown picture: the owner
/// gives it the picture's size with <see cref="Apply(in MediaBadgeState, Size)"/> and its place with alignment and margin (centred on the slot by
/// default). It is not a hit target and not part of the accessibility tree; the tile carries the words (<see cref="MediaBadgeText.Describe(in MediaBadgeState)"/>).
/// Same look in both themes as the play mark (<see cref="VideoPlayMark"/>): nothing here is a theme token, because the badges lie on arbitrary pictures.
/// </summary>
internal sealed class MediaBadgeView : Control
{
    private static readonly Pen Ring = new(VideoPlayMark.RingBrush, 1);
    private static Geometry? s_triangle, s_untagged;

    private MediaBadgeState _state;
    private Size _picture, _slot;
    private MediaBadgeOptions _options;
    private MediaBadgeLimits? _limits;
    private bool _applied;
    private MediaBadgePlan _plan = MediaBadgePlan.Empty;
    private FormattedText?[]? _texts;

    internal MediaBadgeView()
    {
        IsHitTestVisible = false;
        Focusable = false;
        IsVisible = false;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
    }

    /// <summary>What this surface shows of the badges; used by the next <see cref="Apply(in MediaBadgeState, Size)"/>.</summary>
    internal MediaBadgeOptions Options { get => _options; set => _options = value; }

    /// <summary>The plan that is drawn (tests and smokes).</summary>
    internal MediaBadgePlan Plan => _plan;

    /// <summary>The state that is drawn.</summary>
    internal MediaBadgeState State => _state;

    internal void Apply(in MediaBadgeState state, Size picture) => Apply(state, picture, picture, _options);

    internal void Apply(in MediaBadgeState state, Size picture, in MediaBadgeOptions options) => Apply(state, picture, picture, options);

    /// <summary>Shows the badges on a picture in a slot with the options of the view (the slot matters only with <see cref="MediaBadgeAnchor.Slot"/>).</summary>
    internal void Apply(in MediaBadgeState state, Size picture, Size slot) => Apply(state, picture, slot, _options);

    /// <summary>
    /// Shows the badges of <paramref name="state"/> on a picture of this size. With <see cref="MediaBadgeAnchor.Slot"/> the corners follow
    /// <paramref name="slot"/> instead, and the view's own size is the slot's: the owner then arranges it on the whole slot, not on the picture.
    /// Nothing happens, and nothing is allocated, when state, sizes, options and the limits in force are the ones that are already drawn. A
    /// picture that carries no badge hides the view.
    /// </summary>
    internal void Apply(in MediaBadgeState state, Size picture, Size slot, in MediaBadgeOptions options)
    {
        MediaBadgeLimits limits = MediaBadgeLimits.Current;
        Size effectiveSlot = options.Anchor == MediaBadgeAnchor.Slot ? slot : picture; // the slot is part of the state only when it is the anchor
        if (_applied && state.Equals(_state) && picture == _picture && effectiveSlot == _slot && options == _options && ReferenceEquals(limits, _limits)) return;
        _applied = true;
        _state = state;
        _picture = picture;
        _slot = effectiveSlot;
        _options = options;
        _limits = limits;
        _plan = MediaBadgeLayout.Plan(state, picture, slot, options, limits);
        _texts = null;
        Size area = MediaBadgeLayout.Area(picture, slot, options.Anchor);
        bool usable = double.IsFinite(area.Width) && double.IsFinite(area.Height) && area.Width > 0 && area.Height > 0;
        Width = usable ? area.Width : 0;
        Height = usable ? area.Height : 0;
        IsVisible = _plan.Badges.Length > 0;
        InvalidateVisual();
    }

    /// <summary>Takes the badges off (a recycled tile with no medium).</summary>
    internal void Clear() => Apply(default, default, default, _options);

    /// <summary>The slot the plan was made for (the picture's size unless the anchor is the slot).</summary>
    internal Size Slot => _slot;

    public override void Render(DrawingContext context)
    {
        PlacedBadge[] badges = _plan.Badges;
        if (badges.Length == 0) return;
        Typeface typeface = new(TextElement.GetFontFamily(this), FontStyle.Normal, FontWeight.SemiBold);
        for (int index = 0; index < badges.Length; index++)
        {
            PlacedBadge placed = badges[index];
            Rect bounds = placed.Bounds;
            if (placed.Badge.Id == MediaBadgeId.PlayMark) { DrawPlayMark(context, bounds); continue; }
            context.DrawRectangle(VideoPlayMark.DiscBrush, Ring, bounds, bounds.Height / 2, bounds.Height / 2);
            if (placed.Dot) continue;
            DrawContent(context, placed.Badge, bounds, index, typeface);
        }
    }

    private void DrawContent(DrawingContext context, in MediaBadge badge, Rect bounds, int index, Typeface typeface)
    {
        double unit = _plan.Unit;
        double icon = badge.Icon != null ? MediaBadgeLayout.IconSize(badge.Id, unit) : 0;
        FormattedText? text = null;
        if (badge.Label != null)
        {
            _texts ??= new FormattedText?[_plan.Badges.Length];
            text = _texts[index] ??= new FormattedText(badge.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, _plan.FontSize, VideoPlayMark.GlyphBrush);
        }
        // Icon and text are centred as one group on the chip, so a text a little wider than the estimate still has equal room on both sides.
        double group = icon + (icon > 0 && text != null ? MediaBadgeLayout.IconTextGap : 0) + (text?.Width ?? 0);
        double x = bounds.X + (bounds.Width - group) / 2;
        if (badge.Icon != null)
        {
            Rect target = new(x, bounds.Y + (bounds.Height - icon) / 2, icon, icon);
            if (badge.Icon == MediaBadgeIcons.Untagged) DrawUntagged(context, target);
            else
            {
                Bitmap mask = MediaBadgeIconBitmaps.Get(badge.Icon);
                using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality }))
                    context.DrawImage(mask, new Rect(mask.Size), target);
            }
            x += icon + MediaBadgeLayout.IconTextGap;
        }
        if (text != null) context.DrawText(text, new Point(x, bounds.Y + (bounds.Height - text.Height) / 2));
    }

    private static void DrawPlayMark(DrawingContext context, Rect circle)
    {
        context.DrawEllipse(VideoPlayMark.DiscBrush, Ring, circle.Center, circle.Width / 2, circle.Height / 2);
        Size glyph = VideoPlayMark.GlyphSize(circle.Width);
        // The triangle is made once, in the unit of the 44 dip mark (14 x 18), and scaled and nudged like VideoPlayMarkView does.
        s_triangle ??= Geometry.Parse("M 0,0 L 0,18 L 14,9 Z");
        double scale = glyph.Width / 14;
        Matrix place = Matrix.CreateScale(scale, scale)
            * Matrix.CreateTranslation(circle.Center.X - glyph.Width / 2 + VideoPlayMark.GlyphOffset(circle.Width), circle.Center.Y - glyph.Height / 2);
        using (context.PushTransform(place)) context.DrawGeometry(VideoPlayMark.GlyphBrush, null, s_triangle);
    }

    private static void DrawUntagged(DrawingContext context, Rect target)
    {
        // The existing drawn mark of an untagged medium (a tag outline with a slash, 12 x 12), stroked in white.
        s_untagged ??= Geometry.Parse(WorkspaceChips.MarkUntagged);
        double scale = target.Width / 12;
        Pen pen = new(VideoPlayMark.GlyphBrush, Math.Max(1, 1.5 * scale), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        Matrix place = Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(target.X, target.Y);
        using (context.PushTransform(place)) context.DrawGeometry(null, pen, s_untagged);
    }
}

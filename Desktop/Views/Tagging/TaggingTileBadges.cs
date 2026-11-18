using System;
using Avalonia;
using Avalonia.Controls;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>
/// The badges of one grid tile of the tagging page (TG1), kept apart from the tile on purpose: the tile only lays <see cref="View"/> over its
/// picture and hands it the row. Since MB1b the drawing is the shared <see cref="MediaBadgeView"/> with <see cref="MediaBadgeOptions.TaggingGrid"/>
/// (the tagging state finished or skipped, the video mark and length, the number of own tags or the no-tags mark, the booru globe); this class
/// only turns a queue entry into the shared <see cref="MediaBadgeState"/> and asks the shared cache for the spoken sentence of that state. The entry's number and
/// the walk marker are not badges: they are the caption under the tile.
/// Recycle-safe: one view per tile, nothing is rebuilt; an unchanged state, picture and language allocate nothing, and the container's name and the tile's own name share one sentence.
/// </summary>
internal sealed class TaggingTileBadges
{
    private readonly MediaBadgeView _view = new() { Name = "TaggingTileBadges", Options = MediaBadgeOptions.TaggingGrid };
    private MediaBadgeState _state;
    private bool _hasRow;

    /// <summary>The shared badge view the tile lays over its slot (not hit-testable, not part of the accessibility tree); its corners are the slot's.</summary>
    internal MediaBadgeView View => _view;

    /// <summary>What the badges show now (tests and smokes).</summary>
    internal MediaBadgeState State => _state;

    /// <summary>
    /// The badge sentence of the shown state ("Video · Tagging done · 3 tags"), from the shared sentence cache (<see cref="MediaBadgeSentences"/>); empty
    /// when there is no row or no badge. It is part of the tile's accessible name and tooltip.
    /// </summary>
    internal string Description => _hasRow ? MediaBadgeSentences.Get(_state, _view.Options) : string.Empty;

    /// <summary>
    /// The badges of a queue entry: the tagging state, a video (its length is not recorded in the queue), the number of own tags (null: not read
    /// yet, so neither a count nor the no-tags mark) and a booru source. The queue is the tagging page itself, so "in the queue" is never set.
    /// </summary>
    internal static MediaBadgeState StateOf(TaggingQueueRow row, int? tagCount)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new MediaBadgeState(
            Kind: row.IsVideo ? MediaBadgeKind.Video : MediaBadgeKind.Image,
            TagCount: tagCount,
            Tagging: row.Item.State switch
            {
                TagQueueState.Done => MediaTaggingState.Done,
                TagQueueState.Skipped => MediaTaggingState.Skipped,
                _ => MediaTaggingState.None
            },
            Booru: row.Item.BooruPost != null || BooruMediaIdentity.IsBooruThread(row.Item.Thread));
    }

    /// <summary>The sentence a tile's name carries for this entry: the state's badges on the tagging grid (empty when there is none).</summary>
    internal static string DescribeOf(TaggingQueueRow row, int? tagCount) =>
        MediaBadgeSentences.Get(StateOf(row, tagCount), MediaBadgeOptions.TaggingGrid);

    /// <summary>
    /// Shows the badges of <paramref name="row"/> in a slot, over a picture of this shown size (the picture only places the play mark: the corners
    /// follow the slot, which a slot that is not known yet, the default, leaves to the picture); null (a recycled tile with no entry) takes them off.
    /// </summary>
    internal void Show(TaggingQueueRow? row, int? tagCount, Size picture, Size slot = default)
    {
        if (row == null) { _hasRow = false; _state = default; _view.Clear(); return; }
        MediaBadgeState state = StateOf(row, tagCount);
        _hasRow = true;
        _state = state;
        _view.Apply(state, picture, slot);
    }

    /// <summary>The shown picture or the slot changed its size (loaded, replaced, tile resized): the badges follow.</summary>
    internal void Place(Size picture, Size slot = default)
    {
        if (_hasRow) _view.Apply(_state, picture, slot);
    }

    /// <summary>
    /// How far the badges of the top-right corner reach into the area they are placed on (the tile's slot) from its right edge (0 when the corner
    /// holds none): the selection check is laid beside them, never on top.
    /// </summary>
    internal static double TopRightReach(MediaBadgePlan plan, double areaWidth)
    {
        double reach = 0;
        foreach (PlacedBadge placed in plan.Badges)
            if (placed.Badge.Corner == MediaBadgeCorner.TopRight) reach = Math.Max(reach, areaWidth - placed.Bounds.Left);
        return reach;
    }
}

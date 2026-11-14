using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>
/// The badges of one grid tile of the tagging page (TG1), kept apart from the tile on purpose: the tile only places this overlay on its
/// picture and hands it the row. The planned shared thumbnail badge component (MB1: favourite, video, no tags, in queue, rating) replaces
/// this class alone, so nothing of the badge drawing lives in <see cref="TaggingQueueTileView"/> or the page.
/// Corners: top left the number (bold with a next mark where the walk continues), top right the state (finished, skipped; nothing for
/// open), bottom left a video mark, bottom right the number of own tags. All are drawn marks with their meaning as tooltip and are part
/// of the tile's accessible name, never colour alone.
/// </summary>
internal sealed class TaggingTileBadges
{
    /// <summary>Marks drawn on the 12 dip mark grid (the same grid as the media tile marks).</summary>
    internal const string CheckMark = "M1,6.4 L4.6,10 L11,2.4";
    internal const string SkipMark = "M1,1 L7,6 L1,11 Z M9.5,1 L9.5,11";
    internal const string NextMark = "M4,1 L9,6 L4,11";
    internal const string VideoMark = "M2,1 L10,6 L2,11 Z";
    internal const string TagMark = "M1,1 L6,1 L11,6 L6,11 L1,6 Z M3.6,3.6 L3.7,3.7";

    private readonly Border _number = WorkspaceChips.CreateCornerBadge("TaggingTileNumber", HorizontalAlignment.Left, VerticalAlignment.Center);
    private readonly Border _next = WorkspaceChips.CreateCornerMark("TaggingTileNext", HorizontalAlignment.Left, VerticalAlignment.Center);
    private readonly Border _state = WorkspaceChips.CreateCornerMark("TaggingTileState", HorizontalAlignment.Right, VerticalAlignment.Top);
    private readonly Border _video = WorkspaceChips.CreateCornerMark("TaggingTileVideo", HorizontalAlignment.Left, VerticalAlignment.Bottom);
    private readonly TextBlock _tagCountText = new() { Name = "TaggingTileTagCountText", VerticalAlignment = VerticalAlignment.Center, Classes = { WorkspaceChips.Label, "tnum" } };
    private readonly Border _tags;

    internal TaggingTileBadges()
    {
        Avalonia.Controls.Shapes.Path glyph = new() { Classes = { "tile-mark-glyph" }, Stretch = Stretch.Uniform, Width = 10, Height = 10, IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center };
        glyph.AttachedToVisualTree += (_, _) => glyph.Data ??= Geometry.Parse(TagMark);
        _tags = new Border
        {
            Name = "TaggingTileTagCount", Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { glyph, _tagCountText } },
            Classes = { WorkspaceChips.Chip, WorkspaceChips.Badge, WorkspaceChips.Compact, WorkspaceChips.Corner }, IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom
        };
        AutomationProperties.SetAccessibilityView(_tags, AccessibilityView.Raw);
        StackPanel topLeft = new() { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4, 4, 0, 0), Children = { _number, _next } };
        _number.HorizontalAlignment = HorizontalAlignment.Left; _number.VerticalAlignment = VerticalAlignment.Center;
        _next.HorizontalAlignment = HorizontalAlignment.Left; _next.VerticalAlignment = VerticalAlignment.Center;
        _state.Margin = new Thickness(0, 4, 4, 0);
        _video.Margin = new Thickness(4, 0, 0, 4);
        _tags.Margin = new Thickness(0, 0, 4, 4);
        // The overlay only draws: every click goes through to the tile (selection, double click, menu).
        Overlay = new Grid { IsHitTestVisible = false, Children = { topLeft, _state, _video, _tags } };
    }

    /// <summary>The overlay the tile lays over its picture.</summary>
    internal Control Overlay { get; }
    internal Border Number => _number;
    internal Border Next => _next;
    internal Border State => _state;
    internal Border Video => _video;
    internal Border Tags => _tags;

    /// <summary>Shows the marks of <paramref name="row"/>; <paramref name="tagCount"/> null (not read) or 0 shows no tag badge.</summary>
    internal void Show(TaggingQueueRow? row, int? tagCount)
    {
        _number.IsVisible = row != null;
        if (_number.Child is TextBlock number)
        {
            number.Text = row == null ? null : "#" + row.NumberText;
            number.FontWeight = row?.Next == true ? FontWeight.Bold : FontWeight.Normal;
        }
        WorkspaceChips.SetCornerMark(_next, row?.Next == true ? NextMark : null, Texts.T("tagging.page.next"));
        WorkspaceChips.SetCornerMark(_state, row?.Item.State switch
        {
            TagQueueState.Done => CheckMark,
            TagQueueState.Skipped => SkipMark,
            _ => null
        }, row == null ? null : row.StateText);
        WorkspaceChips.SetCornerMark(_video, row?.IsVideo == true ? VideoMark : null, TaggingQueueView.KindText(SavedMediaKind.Video));
        int count = tagCount ?? 0;
        _tags.IsVisible = count > 0;
        _tagCountText.Text = count > 0 ? count.ToString("N0", Texts.Culture) : null;
        ToolTip.SetTip(_tags, count > 0 ? Texts.T("tagging.page.tile.tags", count.ToString("N0", Texts.Culture)) : null);
    }
}

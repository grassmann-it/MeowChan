using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop.Controls;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>
/// One tile of the tagging page's grid (TG1): the preview (the media page's thumbnail path), the badges overlay
/// (<see cref="TaggingTileBadges"/>), a selection check and the file name under the picture. Built once per container and filled from the
/// <see cref="TaggingQueueRow"/> it is given. The tile's name for assistive technology and its tooltip are
/// <see cref="TaggingQueueRow.TileName"/>; the selection is the list item's (the check only repeats it, never colour alone).
/// </summary>
internal sealed class TaggingQueueTileView : Border
{
    /// <summary>The cell height the grid's panel uses (the width is <see cref="MediaTilePanel.CellWidth"/>, shared with the media page).</summary>
    internal const double TileHeight = 176;
    internal const double PreviewHeight = 124;

    private readonly Func<TagQueueItem, int?>? _tagCount;
    private readonly TaggingThumbnail _preview;
    private readonly TaggingTileBadges _badges = new();
    private readonly Border _selected = WorkspaceChips.CreateSelectionCheck("TaggingTileSelected");
    private readonly TextBlock _name = new() { MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
    private IDisposable? _selection;

    internal TaggingQueueTileView(TaggingThumbnails? thumbnails = null, Func<TagQueueItem, int?>? tagCount = null)
    {
        _tagCount = tagCount;
        MaxWidth = MediaTilePanel.CellWidth;
        Height = TileHeight;
        Classes.Add("tagging-tile");
        _preview = new TaggingThumbnail(showText: true) { Source = thumbnails, Height = PreviewHeight };
        AutomationProperties.SetAccessibilityView(_preview, AccessibilityView.Raw);
        _selected.HorizontalAlignment = HorizontalAlignment.Right;
        _selected.VerticalAlignment = VerticalAlignment.Top;
        _selected.Margin = new Thickness(0, 4, 32, 0);
        Grid picture = new() { Children = { _preview, _badges.Overlay, _selected } };
        Grid cell = new() { Margin = new Thickness(8, 8, 8, 4), RowDefinitions = new RowDefinitions(PreviewHeight + ",Auto"), RowSpacing = 4 };
        cell.Children.Add(picture);
        Grid.SetRow(_name, 1);
        cell.Children.Add(_name);
        Child = cell;
        AttachedToVisualTree += (_, _) => { _selection?.Dispose(); _selection = WorkspaceChips.ObserveSelection(this, _selected); };
        DetachedFromVisualTree += (_, _) => { _selection?.Dispose(); _selection = null; };
    }

    internal TaggingQueueRow? Row { get; private set; }
    internal TaggingThumbnail Preview => _preview;
    internal TaggingTileBadges Badges => _badges;
    /// <summary>The name line under the picture (smokes).</summary>
    internal TextBlock NameText => _name;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Show(DataContext as TaggingQueueRow);
    }

    internal void Show(TaggingQueueRow? row)
    {
        Row = row;
        int? tags = row == null ? null : _tagCount?.Invoke(row.Item);
        _preview.Item = row?.Item;
        _badges.Show(row, tags);
        _name.Text = row?.Name;
        string? name = row?.TileName(tags);
        ToolTip.SetTip(this, name);
        AutomationProperties.SetName(this, name);
    }
}

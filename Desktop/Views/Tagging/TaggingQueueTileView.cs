using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>
/// One tile of the tagging page's grid (TG1): the preview (the media page's thumbnail path), the shared media badges over exactly the shown
/// picture (<see cref="TaggingTileBadges"/>, MB1b), a selection check and, under the picture, the entry's number (bold with a mark where the
/// walk continues) and its file name. Built once per container and filled from the <see cref="TaggingQueueRow"/> it is given. The tile's
/// name for assistive technology and its tooltip are <see cref="TaggingQueueRow.TileName"/> (the badges' sentence is part of it; the badge
/// view itself is not in the accessibility tree and takes no focus); the selection is the list item's (the check only repeats it, never colour alone).
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
    // The number is a caption, not a badge (MB1b): "#3", bold with a following mark where "continue" starts.
    private readonly TextBlock _number = new() { MaxLines = 1, Margin = new Thickness(0, 0, 4, 0), Classes = { "tnum" } };
    /// <summary>The space the selection check keeps from the picture's right edge when no badge sits in that corner (dip).</summary>
    internal const double CheckInset = 4, CheckGap = 4;
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
        _selected.Margin = new Thickness(0, 4, CheckInset, 0);
        AutomationProperties.SetAccessibilityView(_number, AccessibilityView.Raw);
        // The badge view lies over the shown picture (it is as large as the picture and centred on the slot, like the picture itself).
        Grid picture = new() { Children = { _preview, _badges.View, _selected } };
        _preview.PictureChanged += () => { _badges.Place(_preview.PictureSize); PlaceCheck(); };
        Grid cell = new() { Margin = new Thickness(8, 8, 8, 4), RowDefinitions = new RowDefinitions(PreviewHeight + ",Auto"), RowSpacing = 4 };
        cell.Children.Add(picture);
        Grid caption = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(_name, 1);
        caption.Children.Add(_number);
        caption.Children.Add(_name);
        Grid.SetRow(caption, 1);
        cell.Children.Add(caption);
        Child = cell;
        AttachedToVisualTree += (_, _) => { _selection?.Dispose(); _selection = WorkspaceChips.ObserveSelection(this, _selected); };
        DetachedFromVisualTree += (_, _) => { _selection?.Dispose(); _selection = null; };
    }

    internal TaggingQueueRow? Row { get; private set; }
    internal TaggingThumbnail Preview => _preview;
    internal TaggingTileBadges Badges => _badges;
    /// <summary>The number caption under the picture (smokes).</summary>
    internal TextBlock NumberText => _number;
    /// <summary>The selection check (tests and smokes).</summary>
    internal Border SelectedMark => _selected;
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
        _badges.Show(row, tags, _preview.PictureSize);
        PlaceCheck();
        _name.Text = row?.Name;
        _number.Text = row == null ? null : "#" + row.NumberText + (row.Next ? " \u203A" : null);
        _number.FontWeight = row?.Next == true ? FontWeight.Bold : FontWeight.Normal;
        ToolTip.SetTip(_number, row?.Next == true ? Texts.T("tagging.page.next") : null);
        // The row's sentence plus the badges' sentence (kept by the badges until the state or the language changes).
        string? name = row == null ? null : _badges.Description.Length == 0 ? row.AccessibleName : row.AccessibleName + ", " + _badges.Description;
        ToolTip.SetTip(this, name);
        AutomationProperties.SetName(this, name);
    }

    /// <summary>
    /// Lays the selection check in the top-right corner of the picture, beside the badges that sit there (the tagging state, the booru globe),
    /// never on top of them. The picture is centred in the slot, so the gap between its edge and the slot's is part of the inset.
    /// </summary>
    private void PlaceCheck()
    {
        Size picture = _preview.PictureSize;
        double edge = Math.Max(0, (_preview.Bounds.Width - picture.Width) / 2);
        double reach = TaggingTileBadges.TopRightReach(_badges.View.Plan, picture.Width);
        double inset = edge + (reach > 0 ? reach + CheckGap : CheckInset);
        if (_selected.Margin.Right != inset) _selected.Margin = new Thickness(0, 4, inset, 0);
    }
}

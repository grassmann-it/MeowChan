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
/// One tile of the tagging page's grid (TG1): the preview (the media page's thumbnail path), the shared media badges on the corners of the
/// tile's slot (<see cref="TaggingTileBadges"/>, MB1b; the same place on every tile of a row, the play mark on the picture), a selection check and, under the picture, the entry's number (bold with a mark where the
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
    private readonly MediaTileAppearance _appearance;
    private readonly TaggingThumbnail _preview;
    private readonly TaggingTileBadges _badges = new();
    private readonly Border _selected = WorkspaceChips.CreateSelectionCheck("TaggingTileSelected");
    private readonly TextBlock _name = new() { MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
    // The number is a caption, not a badge (MB1b): "#3", bold with a following mark where "continue" starts.
    private readonly TextBlock _number = new() { MaxLines = 1, Margin = new Thickness(0, 0, 4, 0), Classes = { "tnum" } };
    private readonly TextBlock _group = new() { MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "muted" } };
    /// <summary>The space the selection check keeps from the slot's right edge when no badge sits in that corner (dip).</summary>
    internal const double CheckInset = 4, CheckGap = 4;
    private IDisposable? _selection;

    internal TaggingQueueTileView(TaggingThumbnails? thumbnails = null, Func<TagQueueItem, int?>? tagCount = null, MediaTileAppearance appearance = default)
    {
        _tagCount = tagCount;
        _appearance = appearance.CellWidth > 0 ? appearance : MediaTileMetrics.Standard;
        MaxWidth = _appearance.CellWidth;
        Height = _appearance.CellHeight;
        Classes.Add("tagging-tile");
        _preview = new TaggingThumbnail(showText: true) { Source = thumbnails, Height = _appearance.PreviewHeight };
        AutomationProperties.SetAccessibilityView(_preview, AccessibilityView.Raw);
        _selected.HorizontalAlignment = HorizontalAlignment.Right;
        _selected.VerticalAlignment = VerticalAlignment.Top;
        _selected.Margin = new Thickness(0, 4, CheckInset, 0);
        AutomationProperties.SetAccessibilityView(_number, AccessibilityView.Raw);
        // The badge view lies over the whole slot (the picture is letterboxed inside it): its corners are the slot's, so every tile carries them at one height.
        Grid picture = new() { Children = { _preview, _badges.View, _selected } };
        _badges.View.PlanRefreshed += PlaceCheck; // other badge limits can move the badges, and the check keeps clear of them
        _preview.PictureChanged += PlaceBadges;
        // The slot's size is known only after the first layout and changes with the column width: the badges follow it (they hang on the slot, not on the picture).
        _preview.SizeChanged += (_, _) => PlaceBadges();
        Grid cell = new() { Margin = new Thickness(8, 8, 8, 4), RowDefinitions = new RowDefinitions(_appearance.PreviewHeight + ",Auto,Auto"), RowSpacing = 4 };
        cell.Children.Add(picture);
        Grid caption = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(_name, 1);
        caption.Children.Add(_number);
        caption.Children.Add(_name);
        Grid.SetRow(caption, 1);
        cell.Children.Add(caption);
        Grid.SetRow(_group, 2);
        cell.Children.Add(_group);
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
        _badges.Show(row, tags, _preview.PictureSize, _preview.Bounds.Size);
        PlaceCheck();
        _name.Text = row?.Name;
        _number.Text = row == null ? null : "#" + row.NumberText + (row.Next ? " \u203A" : null);
        _number.FontWeight = row?.Next == true ? FontWeight.Bold : FontWeight.Normal;
        ToolTip.SetTip(_number, row?.Next == true ? Texts.T("tagging.page.next") : null);
        _group.Text = row?.GroupText;
        // The row's sentence plus the badges' sentence (kept by the badges until the state or the language changes).
        string? name = row == null ? null : _badges.Description.Length == 0 ? row.AccessibleName : row.AccessibleName + ", " + _badges.Description;
        ToolTip.SetTip(this, name);
        AutomationProperties.SetName(this, name);
    }

    private void PlaceBadges()
    {
        _badges.Place(_preview.PictureSize, _preview.Bounds.Size);
        PlaceCheck();
    }

    /// <summary>
    /// Lays the selection check in the top-right corner of the slot, beside the badges that sit there (the tagging state, the booru globe),
    /// never on top of them. The badges and the check share the slot's corner, whatever the picture's shape.
    /// </summary>
    private void PlaceCheck()
    {
        double reach = TaggingTileBadges.TopRightReach(_badges.View.Plan, _preview.Bounds.Width);
        double inset = reach > 0 ? reach + CheckGap : CheckInset;
        if (_selected.Margin.Right != inset) _selected.Margin = new Thickness(0, 4, inset, 0);
    }
}

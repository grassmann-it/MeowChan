using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop.Commands;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>
/// One row of the tagging page's queue list (44 dip, 32 in the compact density): #, preview, type, file, thread or source, own tags,
/// state. Built once per container and filled from the <see cref="TaggingQueueRow"/> it is given, so a recycled container never shows
/// the previous entry. The entry where the walk continues is marked by a bold number and a "next" glyph, and the state is an icon plus
/// its word, never colour alone.
/// </summary>
internal sealed class TaggingQueueRowView : Grid
{
    /// <summary>The shared column widths of the header and the rows: # · preview · type · file · thread or source · group · tags · state.</summary>
    internal const string Columns = "56,56,72,*,220,120,56,112";
    internal static string ColumnsFor(bool showPreview) => showPreview ? Columns : "56,0,72,*,220,120,56,112";
    internal const double RowHeight = WorkspaceMetrics.NormalRowHeight;
    internal const double CompactHeight = WorkspaceMetrics.CompactRowHeight;
    /// <summary>The preview's edge in the normal and in the compact density (inside the row's own padding).</summary>
    internal const double PreviewSize = 40, CompactPreviewSize = 24;

    private readonly Func<TagQueueItem, int?>? _tagCount;
    private readonly TextBlock _number = Cell(TextAlignment.Right);
    // The next entry of the walk: a catalog chevron (no glyph), created when the row is shown (the icon needs the asset loader).
    private readonly ContentControl _next = new() { Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Focusable = false };
    private readonly TaggingThumbnail _preview;
    private readonly TextBlock _kind = Cell(TextAlignment.Left, muted: true);
    private readonly TextBlock _name = Cell(TextAlignment.Left);
    private readonly TextBlock _source = Cell(TextAlignment.Left, muted: true);
    private readonly TextBlock _group = Cell(TextAlignment.Left, muted: true);
    private readonly TextBlock _tags = Cell(TextAlignment.Right, muted: true);
    private readonly ContentControl _stateIcon = new() { VerticalAlignment = VerticalAlignment.Center, Focusable = false };
    private readonly TextBlock _state = Cell(TextAlignment.Left);
    private string? _shownIcon;

    internal TaggingQueueRowView(TaggingThumbnails? thumbnails = null, Func<TagQueueItem, int?>? tagCount = null, bool compact = false, bool showPreview = true)
    {
        _tagCount = tagCount;
        ColumnDefinitions = new ColumnDefinitions(ColumnsFor(showPreview));
        ColumnSpacing = showPreview ? 8 : 0;
        MinHeight = compact ? CompactHeight : RowHeight;
        double size = compact ? CompactPreviewSize : PreviewSize;
        _preview = new TaggingThumbnail(showText: false) { Source = thumbnails, Width = size, Height = size, IsVisible = showPreview, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAccessibilityView(_preview, AccessibilityView.Raw);
        StackPanel number = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { _next, _number } };
        AutomationProperties.SetAccessibilityView(_next, AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(_stateIcon, AccessibilityView.Raw);
        Children.Add(number);
        Add(_preview, 1); Add(_kind, 2); Add(_name, 3); Add(_source, 4); Add(_group, 5); Add(_tags, 6);
        Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { _stateIcon, _state } }, 7);
        _number.Classes.Add("tnum");
        _tags.Classes.Add("tnum");
    }

    internal TaggingQueueRow? Row { get; private set; }
    /// <summary>The preview of this row (test seam).</summary>
    internal TaggingThumbnail Preview => _preview;

    private void Add(Control control, int column)
    {
        SetColumn(control, column);
        Children.Add(control);
    }

    private static TextBlock Cell(TextAlignment alignment, bool muted = false)
    {
        TextBlock text = new() { TextAlignment = alignment, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        if (muted) text.Classes.Add("muted");
        return text;
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        Show(DataContext as TaggingQueueRow);
    }

    internal void Show(TaggingQueueRow? row)
    {
        Row = row;
        _number.Text = row?.NumberText;
        _number.FontWeight = row?.Next == true ? FontWeight.Bold : FontWeight.Normal;
        _next.IsVisible = row?.Next == true;
        if (_next.IsVisible && _next.Content == null) _next.Content = CommandBindings.IconFromKey("chevron-right", 12);
        _preview.Item = row?.Item;
        _kind.Text = row?.KindText;
        _name.Text = row?.Name;
        _source.Text = row?.SourceText;
        _group.Text = row?.GroupText;
        _tags.Text = row == null ? null : _tagCount?.Invoke(row.Item) is { } count ? count.ToString("N0", Localization.Texts.Culture) : "–";
        string? icon = row?.StateIcon;
        if (icon != _shownIcon) { _shownIcon = icon; _stateIcon.Content = icon == null ? null : CommandBindings.IconFromKey(icon, 14); }
        _state.Text = row?.StateText;
        ToolTip.SetTip(_name, row?.Name);
        ToolTip.SetTip(_next, row?.Next == true ? Localization.Texts.T("tagging.page.next") : null);
    }
}

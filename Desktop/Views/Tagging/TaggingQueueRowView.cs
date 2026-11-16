using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
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
    /// <summary>The gap between two columns, in the header and in every row, with or without the preview column.</summary>
    internal const double ColumnGap = 12;
    /// <summary>Layout limits of the table's columns (the file and source widths stand in a 3 : 2 ratio between them).</summary>
    internal const double FileMinWidth = 160, SourceMinWidth = 120, SourceMaxWidth = 420;
    /// <summary>The compact table keeps every column but gives the text columns less room before they start trimming.</summary>
    internal const double CompactFileMinWidth = 96, CompactSourceMinWidth = 72, CompactSourceMaxWidth = 240;
    /// <summary>The page's list surface and its header each have 12 dip margins on both sides.</summary>
    internal const double TableViewportInset = 48;

    /// <summary>
    /// The one definition of the columns of the header and of the rows: # · preview (0 wide when hidden) · type · file (3*) · thread or
    /// source (2*, at most <see cref="SourceMaxWidth"/>) · group · tags · state. A fresh set per call (a definition belongs to one grid).
    /// Both grids use these same widths and the same <see cref="ColumnGap"/>, so a scroll bar that narrows the rows moves nothing apart.
    /// </summary>
    internal static ColumnDefinitions BuildColumns(bool showPreview, bool compact = false) => new()
    {
        // A grid puts its gap on both sides of a 0 wide column: without the preview the number column is one gap shorter and its cell
        // reaches into that gap (NumberMargin), so the type column still stands one ColumnGap after the number.
        new ColumnDefinition(showPreview ? NumberWidth : NumberWidth - ColumnGap, GridUnitType.Pixel),
        new ColumnDefinition(showPreview ? 56 : 0, GridUnitType.Pixel),
        new ColumnDefinition(64, GridUnitType.Pixel),
        new ColumnDefinition(3, GridUnitType.Star) { MinWidth = compact ? CompactFileMinWidth : FileMinWidth },
        new ColumnDefinition(2, GridUnitType.Star)
        {
            MinWidth = compact ? CompactSourceMinWidth : SourceMinWidth,
            MaxWidth = compact ? CompactSourceMaxWidth : SourceMaxWidth
        },
        new ColumnDefinition(compact ? 96 : 132, GridUnitType.Pixel),
        new ColumnDefinition(compact ? 56 : 72, GridUnitType.Pixel),
        new ColumnDefinition(compact ? 104 : 128, GridUnitType.Pixel)
    };

    /// <summary>Returns the minimum width needed to keep all eight columns in the table, including their seven gaps.</summary>
    internal static double MinimumTableWidth(bool showPreview, bool compact)
    {
        double file = compact ? CompactFileMinWidth : FileMinWidth;
        double source = compact ? CompactSourceMinWidth : SourceMinWidth;
        double fixedColumns = (showPreview ? NumberWidth : NumberWidth - ColumnGap) + (showPreview ? 56 : 0) + 64 + file + source
            + (compact ? 96 : 132) + (compact ? 56 : 72) + (compact ? 104 : 128);
        return fixedColumns + ColumnGap * 7;
    }

    /// <summary>
    /// The page's outer margins leave 48 dip less than its width for the header. Compact columns are selected only while the normal
    /// preview table cannot fit there; this keeps the normal geometry at 920, 1100 and 1440 dip and fixes the 760 dip case.
    /// </summary>
    internal static bool UseCompactColumns(double pageWidth) => double.IsFinite(pageWidth) && pageWidth > 0
        && MinimumTableWidth(showPreview: true, compact: false) > Math.Max(0, pageWidth - TableViewportInset);

    private const double NumberWidth = 48;

    /// <summary>The margin of the number cell (header and rows): it reaches into the gap that the hidden preview column would double.</summary>
    internal static Thickness NumberMargin(bool showPreview) => new(0, 0, showPreview ? 0 : -ColumnGap, 0);

    /// <summary>The state cell keeps this much air to its left, so the right-aligned tag count and the state never touch.</summary>
    internal static Thickness StateMargin => new(ColumnGap, 0, 0, 0);
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

    internal TaggingQueueRowView(TaggingThumbnails? thumbnails = null, Func<TagQueueItem, int?>? tagCount = null, bool compact = false,
        bool showPreview = true, bool compactTable = false)
    {
        _tagCount = tagCount;
        ColumnDefinitions = BuildColumns(showPreview, compactTable);
        ColumnSpacing = ColumnGap;
        MinHeight = compact ? CompactHeight : RowHeight;
        double size = compact ? CompactPreviewSize : PreviewSize;
        _preview = new TaggingThumbnail(showText: false) { Source = thumbnails, Width = size, Height = size, IsVisible = showPreview, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAccessibilityView(_preview, AccessibilityView.Raw);
        StackPanel number = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { _next, _number } };
        AutomationProperties.SetAccessibilityView(_next, AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(_stateIcon, AccessibilityView.Raw);
        number.Margin = NumberMargin(showPreview);
        Children.Add(number);
        Add(_preview, 1); Add(_kind, 2); Add(_name, 3); Add(_source, 4); Add(_group, 5); Add(_tags, 6);
        Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = StateMargin, VerticalAlignment = VerticalAlignment.Center, Children = { _stateIcon, _state } }, 7);
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
        TrimTipTextBlock text = new() { TextAlignment = alignment, VerticalAlignment = VerticalAlignment.Center };
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

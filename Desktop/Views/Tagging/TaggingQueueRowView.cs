using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MeowChan.Desktop;

/// <summary>
/// One row of the tagging page's queue list (32 dip): #, type, file, thread or source, state. Built once per container and filled
/// from the <see cref="TaggingQueueRow"/> it is given, so a recycled container never shows the previous entry. The entry where the
/// walk continues is marked by a bold number and a "next" glyph, never by colour alone.
/// </summary>
internal sealed class TaggingQueueRowView : Grid
{
    /// <summary>The shared column widths of the header and the rows.</summary>
    internal const string Columns = "56,72,*,220,112";
    internal const double RowHeight = WorkspaceMetrics.CompactRowHeight;

    private readonly TextBlock _number = Cell(TextAlignment.Right);
    // The next entry of the walk: a catalog chevron (no glyph), created when the row is shown (the icon needs the asset loader).
    private readonly ContentControl _next = new() { Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Focusable = false };
    private readonly TextBlock _kind = Cell(TextAlignment.Left, muted: true);
    private readonly TextBlock _name = Cell(TextAlignment.Left);
    private readonly TextBlock _source = Cell(TextAlignment.Left, muted: true);
    private readonly TextBlock _state = Cell(TextAlignment.Left);

    internal TaggingQueueRowView()
    {
        ColumnDefinitions = new ColumnDefinitions(Columns);
        ColumnSpacing = 8;
        MinHeight = RowHeight;
        StackPanel number = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { _next, _number } };
        AutomationProperties.SetAccessibilityView(_next, AccessibilityView.Raw);
        Children.Add(number);
        Add(_kind, 1); Add(_name, 2); Add(_source, 3); Add(_state, 4);
        _number.Classes.Add("tnum");
    }

    internal TaggingQueueRow? Row { get; private set; }

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
        if (_next.IsVisible && _next.Content == null) _next.Content = MeowChan.Desktop.Commands.CommandBindings.IconFromKey("chevron-right", 12);
        _kind.Text = row?.KindText;
        _name.Text = row?.Name;
        _source.Text = row?.SourceText;
        _state.Text = row?.StateText;
        ToolTip.SetTip(_name, row?.Name);
        ToolTip.SetTip(_next, row?.Next == true ? Localization.Texts.T("tagging.page.next") : null);
    }
}

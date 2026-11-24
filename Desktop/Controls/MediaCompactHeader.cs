using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Controls;

/// <summary>Reader and Media header. Only the filename gives up space in the metadata line.</summary>
internal sealed class MediaCompactHeader : Grid
{
    private readonly TextBlock _title;
    private readonly MediaPositionPill _pill;
    private readonly double _actionsWidth;
    private string _file = "", _detail = "";
    private readonly Button _fileButton = new()
    {
        Name = "ReaderMediaSidebarFactSummary", Classes = { "ghost" }, Focusable = false, IsTabStop = false,
        Padding = new Thickness(0), Height = 16, MinHeight = 0, MinWidth = 0, BorderThickness = new Thickness(0),
        HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center
    };
    private readonly Flyout _facts = new() { Placement = PlacementMode.BottomEdgeAlignedLeft };
    private readonly ScrollViewer _factScroll = new() { Padding = new Thickness(12), MaxHeight = 318,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    internal void SetFactsContent(Control content)
    {
        if (_fileButton.Flyout != null)
        {
            _factScroll.Content = content;
            return;
        }
        Children.Remove(Meta);
        SetRow(Meta, 0);
        SetColumnSpan(Meta, 1);
        var chevron = WorkspaceIcons.Create(WorkspaceIcons.ChevronDown);
        chevron.Width = chevron.Height = 12;
        SetColumn(chevron, 1);
        _fileButton.Content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 4, Children = { Meta, chevron } };
        SetRow(_fileButton, 1);
        SetColumnSpan(_fileButton, 3);
        Children.Add(_fileButton);
        _factScroll.Content = content;
        _facts.Content = _factScroll;
        _facts.FlyoutPresenterClasses.Add("quick-tag-popover");
        _fileButton.Flyout = _facts;
        _facts.Opening += (_, _) => SizeFacts();
        SizeChanged += (_, _) => { if (_facts.IsOpen) SizeFacts(); };
    }

    internal void CloseFacts()
    {
        if (_facts.IsOpen) _facts.Hide();
    }

    private void SizeFacts() => _factScroll.Width = Math.Max(0, Math.Min(Bounds.Width, 480) - 2);

    internal TextBlock Meta { get; } = new()
    {
        Name = "ReaderMediaSidebarMeta", Classes = { "workspace-meta" }, MaxLines = 1, Height = 16
    };

    internal MediaCompactHeader(TextBlock title, MediaPositionPill pill, params Button[] buttons)
    {
        _title = title;
        _pill = pill;
        RowDefinitions = new RowDefinitions("32,16");
        const int actionSpacing = 4;
        _actionsWidth = buttons.Length * WorkspaceMetrics.InputHeight + Math.Max(0, buttons.Length - 1) * actionSpacing;
        ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto");
        Children.Add(title);
        pill.HorizontalAlignment = HorizontalAlignment.Center;
        SetColumn(pill, 1);
        Children.Add(pill);
        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = actionSpacing };
        foreach (Button button in buttons)
        {
            button.Width = button.Height = WorkspaceMetrics.InputHeight;
            button.Classes.Add(WorkspaceIcons.SquareClass);
            actions.Children.Add(button);
        }
        SetColumn(actions, 2);
        Children.Add(actions);
        SetRow(Meta, 1);
        SetColumnSpan(Meta, 3);
        Children.Add(Meta);
    }

    internal void Describe(string file, string detail)
    {
        _file = file;
        _detail = detail;
        _fileButton.IsVisible = file.Length > 0 || detail.Length > 0;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double room = Math.Max(0, availableSize.Width - _actionsWidth);
        double title = Math.Max(64, MeasureText(_title, _title.Text));
        double bare = MeasureText(_pill.Label, _pill.Bare) + 18;
        _pill.IsVisible = _pill.HasPosition && title + 16 + bare <= room;
        _title.MaxWidth = Math.Max(64, _pill.IsVisible ? title : room);
        string prefix = _pill.HasPosition && !_pill.IsVisible ? _pill.Bare! : "";
        string Line(string file) => ReaderMediaSidebarRules.HeaderText(prefix, file, _detail);
        double fileRoom = availableSize.Width - (_fileButton.Flyout == null ? 0 : 16);
        string full = Line(_file);
        string file = MediaFactGrid.FitFileName(_file, candidate => MeasureText(Meta, Line(candidate)) <= fileRoom);
        Meta.Text = Line(file);
        ToolTip.SetTip(_fileButton, full);
        AutomationProperties.SetName(_fileButton, full);
        ToolTip.SetTip(Meta, full);
        AutomationProperties.SetName(Meta, full);
        return base.MeasureOverride(availableSize);
    }

    private static double MeasureText(TextBlock block, string? text) => new FormattedText(text ?? "", Texts.Culture,
        block.FlowDirection, new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch),
        block.FontSize, block.Foreground).Width;
}

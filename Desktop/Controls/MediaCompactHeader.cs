using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Controls;

/// <summary>Reader and Media header. Only the filename gives up space in the metadata line.</summary>
internal sealed class MediaCompactHeader : Grid
{
    private readonly TextBlock _title;
    private readonly MediaPositionPill _pill;
    private string _file = "", _detail = "";
    internal TextBlock Meta { get; } = new()
    {
        Name = "ReaderMediaSidebarMeta", Classes = { "workspace-meta" }, MaxLines = 1, Height = 16
    };

    internal MediaCompactHeader(TextBlock title, MediaPositionPill pill, params Button[] buttons)
    {
        _title = title;
        _pill = pill;
        RowDefinitions = new RowDefinitions("32,16");
        ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto");
        Children.Add(title);
        pill.HorizontalAlignment = HorizontalAlignment.Left;
        pill.Margin = new Thickness(8, 0, 0, 0);
        SetColumn(pill, 1);
        Children.Add(pill);
        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (Button button in buttons)
        {
            button.Width = button.Height = WorkspaceMetrics.InputHeight;
            button.Classes.Add(WorkspaceIcons.SquareClass);
            actions.Children.Add(button);
        }
        SetColumn(actions, 3);
        Children.Add(actions);
        SetRow(Meta, 1);
        SetColumnSpan(Meta, 4);
        Children.Add(Meta);
    }

    internal void Describe(string file, string detail)
    {
        _file = file;
        _detail = detail;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double room = Math.Max(0, availableSize.Width - 140);
        double title = Math.Max(64, MeasureText(_title, _title.Text));
        double bare = MeasureText(_pill.Label, _pill.Bare) + 18;
        _pill.IsVisible = _pill.HasPosition && title + 16 + bare <= room;
        _title.MaxWidth = Math.Max(64, _pill.IsVisible ? title : room);
        ColumnDefinitions[1].MaxWidth = _pill.IsVisible ? Math.Max(0, room - title - 8) : 0;
        _pill.MaxWidth = Math.Max(0, ColumnDefinitions[1].MaxWidth - 8);
        string prefix = _pill.HasPosition && !_pill.IsVisible ? _pill.Bare! : "";
        string Line(string file) => ReaderMediaSidebarRules.HeaderText(prefix, file, _detail);
        string full = Line(_file);
        string file = MediaFactGrid.FitFileName(_file, candidate => MeasureText(Meta, Line(candidate)) <= availableSize.Width);
        Meta.Text = Line(file);
        ToolTip.SetTip(Meta, full);
        AutomationProperties.SetName(Meta, full);
        return base.MeasureOverride(availableSize);
    }

    private static double MeasureText(TextBlock block, string? text) => new FormattedText(text ?? "", Texts.Culture,
        block.FlowDirection, new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch),
        block.FontSize, block.Foreground).Width;
}

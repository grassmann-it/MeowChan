using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace MeowChan.Desktop.Controls;

/// <summary>A control-height row for the tabs, the centred position pill and the close button.</summary>
internal class MediaSidebarHeader : Grid
{
    internal const double TopInset = WorkspaceMetrics.SectionInset;
    internal const double HeaderGap = 8;

    private readonly MediaPositionPill _position;
    private readonly Button _close;
    private readonly Control _leading;

    internal MediaSidebarHeader(Control leading, MediaPositionPill position, Button close, params Control[] body)
    {
        _leading = leading;
        _position = position;
        _close = close ?? throw new ArgumentNullException(nameof(close));
        RowDefinitions = new RowDefinitions { new(new GridLength(WorkspaceMetrics.InputHeight)), new(GridLength.Auto) };
        ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto");
        position.Margin = new Thickness(HeaderGap, 0, HeaderGap, 0);
        close.Width = close.Height = WorkspaceMetrics.InputHeight;
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(leading);
        Children.Add(position);
        SetColumn(position, 1);
        foreach (Control child in body)
        {
            Children.Add(child);
            SetRow(child, 1);
            SetColumnSpan(child, 3);
        }
        Children.Add(close);
        SetColumn(close, 2);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Size rowSize = new(double.PositiveInfinity, WorkspaceMetrics.InputHeight);
        _close.Measure(rowSize);
        double free = Math.Max(0, availableSize.Width - _close.DesiredSize.Width);
        double reserve = WorkspaceMetrics.MediaPanelPositionMinWidth + 2 * HeaderGap;
        _position.IsVisible = _position.HasPosition && free >= reserve + Math.Max(64, _leading.MinWidth);
        ColumnDefinitions[0].MaxWidth = _position.IsVisible ? free - reserve : free;
        _leading.MaxWidth = ColumnDefinitions[0].MaxWidth;
        return base.MeasureOverride(availableSize);
    }
}

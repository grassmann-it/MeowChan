using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace MeowChan.Desktop.Controls;

/// <summary>Keeps the post, media position and actions on a control-height row with room below for long file details.</summary>
internal sealed class MediaSidebarHeader : Grid
{
    internal const double TopInset = 8;

    private readonly Control _heading;
    private readonly TextBlock _metadata;
    private readonly Control _position;
    private readonly Control _actions;

    internal MediaSidebarHeader(Control heading, TextBlock metadata, Control position, Control actions)
    {
        _heading = heading;
        _metadata = metadata;
        _position = position;
        _actions = actions;
        RowDefinitions = new RowDefinitions { new(new GridLength(WorkspaceMetrics.InputHeight)), new(GridLength.Auto) };
        ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto");
        heading.Margin = new Thickness(0, 0, 8, 0);
        heading.ClipToBounds = true;
        position.Margin = new Thickness(8, 0, 0, 0);
        actions.Margin = new Thickness(8, 0, 0, 0);
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        SetColumn(position, 2);
        SetColumn(actions, 3);
        Children.Add(heading);
        Children.Add(metadata);
        Children.Add(position);
        Children.Add(actions);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _heading.Measure(Size.Infinity);
        _metadata.Measure(Size.Infinity);
        _position.Measure(Size.Infinity);
        _actions.Measure(Size.Infinity);
        double textWidth = Math.Max(0, availableSize.Width - _actions.DesiredSize.Width);
        double positionWidth = _position.DesiredSize.Width;
        double counterReserve = Math.Min(positionWidth, ShrinkRowPanel.MinimumShrunkWidth + _position.Margin.Left);
        double headingWidth = Math.Min(_heading.DesiredSize.Width, Math.Max(0, textWidth - counterReserve));
        ColumnDefinitions[0].MaxWidth = headingWidth;
        ColumnDefinitions[2].MaxWidth = Math.Max(0, textWidth - headingWidth);
        double remaining = textWidth - headingWidth - positionWidth;
        // Use the full width for the file when it would need more than two lines beside the controls.
        bool stackMetadata = _metadata.IsVisible && remaining < _metadata.DesiredSize.Width / _metadata.MaxLines;
        SetRow(_metadata, stackMetadata ? 1 : 0);
        SetColumn(_metadata, stackMetadata ? 0 : 1);
        SetColumnSpan(_metadata, stackMetadata ? 4 : 1);
        return base.MeasureOverride(availableSize);
    }
}

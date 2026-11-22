using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace MeowChan.Desktop.Controls;

/// <summary>Places the post and the actions on a control-height row and gives long file details room below it.</summary>
internal sealed class MediaSidebarHeader : Grid
{
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
        position.Margin = new Thickness(8, 0, 0, 0);
        actions.Margin = new Thickness(8, 0, 0, 0);
        actions.HorizontalAlignment = HorizontalAlignment.Right;
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
        double remaining = availableSize.Width - _heading.DesiredSize.Width - _actions.DesiredSize.Width - _position.DesiredSize.Width;
        // Use the full width for the file when it would need more than two lines beside the controls.
        bool stackMetadata = _metadata.IsVisible && remaining < _metadata.DesiredSize.Width / _metadata.MaxLines;
        bool stackPosition = _position.IsVisible && remaining < 0;
        SetRow(_metadata, stackMetadata ? 1 : 0);
        SetColumn(_metadata, stackMetadata ? 0 : 1);
        SetColumnSpan(_metadata, stackMetadata ? (stackPosition ? 3 : 4) : 1);
        SetRow(_position, stackPosition ? 1 : 0);
        SetColumn(_position, stackPosition ? 3 : 2);
        return base.MeasureOverride(availableSize);
    }
}

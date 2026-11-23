using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace MeowChan.Desktop.Controls;

internal sealed class MediaPositionPill : Border
{
    internal TextBlock Label { get; } = new()
    {
        Classes = { "tnum" }, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1
    };
    private readonly Path _check = WorkspaceIcons.Create(WorkspaceIcons.Check, inheritForeground: false);
    private string? _short, _bare;
    internal bool HasPosition => _short != null;

    protected override System.Type StyleKeyOverride => typeof(Border);

    internal MediaPositionPill(string name)
    {
        Name = name;
        Classes.Add("chip-position");
        _check.Width = _check.Height = 12;
        _check.Stretch = Stretch.Uniform;
        _check.Margin = new Thickness(0, 0, 4, 0);
        _check[!Path.StrokeProperty] = new DynamicResourceExtension("Workspace.Accent");
        _check.IsVisible = false;
        Grid.SetColumn(Label, 1);
        Child = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { _check, Label } };
        ClipToBounds = true;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        IsVisible = false;
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(Label, AutomationLiveSetting.Polite);
    }

    internal void Show(GalleryTagPanelPosition? position, bool marked = false)
    {
        string? text = MediaPositionText.Long(position, marked);
        _short = MediaPositionText.Short(position);
        _bare = position is { } p ? $"{p.Number} / {p.Count}{(p.More ? "+" : "")}" : null;
        Label.Text = _short;
        Classes.Set("marked", marked);
        _check.IsVisible = marked;
        ToolTip.SetTip(this, text);
        AutomationProperties.SetName(this, text);
        IsVisible = HasPosition;
        InvalidateMeasure();
        (Parent as Control)?.InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Label.Text = _short;
        Label.Measure(Size.Infinity);
        double natural = Label.DesiredSize.Width + Padding.Left + Padding.Right + BorderThickness.Left + BorderThickness.Right
            + (_check.IsVisible ? 16 : 0);
        if (natural > availableSize.Width) Label.Text = _bare;
        return base.MeasureOverride(availableSize);
    }
}

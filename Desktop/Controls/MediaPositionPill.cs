using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MeowChan.Desktop.Controls;

internal sealed class MediaPositionPill : Border
{
    private readonly TextBlock _label = new()
    {
        Classes = { WorkspaceChips.Label, "tnum" },
        TextAlignment = TextAlignment.Left,
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxLines = 1
    };

    protected override System.Type StyleKeyOverride => typeof(Border);

    internal MediaPositionPill(string name)
    {
        Name = name;
        Classes.AddRange([WorkspaceChips.Chip, WorkspaceChips.Badge, WorkspaceChips.Compact]);
        Child = _label;
        ClipToBounds = true;
        VerticalAlignment = VerticalAlignment.Center;
        IsVisible = false;
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_label, AutomationLiveSetting.Polite);
    }

    internal void Show(GalleryTagPanelPosition? position, bool marked = false)
    {
        string? text = MediaPositionText.Long(position, marked);
        _label.Text = MediaPositionText.Short(position);
        ToolTip.SetTip(this, text);
        AutomationProperties.SetName(this, text);
        IsVisible = _label.Text != null;
    }
}

using System;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Controls;

internal sealed class PanelCloseButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    internal PanelCloseButton(string name)
    {
        Name = name;
        Width = Height = WorkspaceMetrics.InputHeight;
        WorkspaceIcons.MakeSquare(this, WorkspaceIcons.Close, "", subtle: false);
    }

    internal void Localize(string label, string? tip = null)
    {
        tip ??= Texts.T("viewer.closeLayer", label);
        AutomationProperties.SetName(this, label);
        AutomationProperties.SetHelpText(this, tip);
        ToolTip.SetTip(this, tip);
    }
}

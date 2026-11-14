using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// An icon-only button of the toolbar family: a square of the input height (<c>icon-only</c> in WorkspaceStyles.axaml) with a
/// catalog icon (<see cref="CommandBindings.NamedIcon"/>, e.g. <c>refresh-cw</c>), the name as tooltip and accessible name. The icon is
/// never the only name. The icon is created when the button is shown, because it needs the platform's asset loader.
/// </summary>
internal static class IconButtons
{
    /// <summary>Turns <paramref name="button"/> into an icon-only button; calling it again only renames it (language change).</summary>
    internal static void Apply(Button button, string icon, string name)
    {
        ToolTip.SetTip(button, name);
        AutomationProperties.SetName(button, name);
        if (button.Classes.Contains("icon-only")) return;
        button.Classes.Add("icon-only");
        button.Classes.Add("subtle");
        void Show() { if (button.Content is not Control) button.Content = CommandBindings.NamedIcon(icon); }
        if (button.IsAttachedToVisualTree()) Show();
        else button.AttachedToVisualTree += (_, _) => Show();
    }
}

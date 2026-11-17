using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>
/// DL-07: the open Downloads search bar had two crosses side by side (the field's clear button and the bar's close). The field keeps the
/// standard clear cross; closing the bar is a labelled "Close" with the collapse chevron, never a second ×. Esc does both in order: it
/// clears the text first and closes the bar from an empty field; the tooltip says so.
/// </summary>
internal static class DownloadSearchCloseButton
{
    /// <summary>The catalog icon (Assets/icons/feather/masks) shown before the label.</summary>
    internal const string Icon = "chevron-up";

    /// <summary>Gives the button its label, icon, accessible name and tooltip; calling it again only renames it (language change).</summary>
    internal static void Apply(Button button)
    {
        string label = Texts.T("downloads.searchClose");
        button.Content = label;
        CommandBindings.EnsureIconBeforeText(button, Icon);
        AutomationProperties.SetName(button, label);
        ToolTip.SetTip(button, Texts.T("downloads.searchCloseTip"));
    }
}

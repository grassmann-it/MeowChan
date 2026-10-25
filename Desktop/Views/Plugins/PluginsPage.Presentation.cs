using System;
using System.Linq;
using System.Collections.Generic;
using Avalonia.Automation;
using Avalonia.VisualTree;
using Avalonia.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginSdk;
using Ursa.Controls;

namespace MeowChan;

public partial class PluginsPage
{
    private void RenderIdentity(PluginCatalogEntry entry) => RenderTechnicalIdentity(entry);

    // The reason and enabled state share one projection. The associated selectable
    // text remains keyboard reachable even though disabled buttons cannot take focus.
    private void SetAction(string name, string unavailableKey, bool available)
    {
        Button button = C<Button>(name);
        string? reason = _busy ? Texts.T("plugins.reason.busy") : !available ? Texts.T(unavailableKey) : null;
        button.IsEnabled = reason == null;
        AutomationProperties.SetHelpText(button, reason ?? ToolTip.GetTip(button)?.ToString() ?? string.Empty);
        _actionReasons[name] = reason;
    }
    private void ShowReasons(string target, params string[] actions)
    {
        string text = string.Join("\n", actions.Where(name => _actionReasons.GetValueOrDefault(name) != null)
            .Select(name => AutomationProperties.GetName(C<Button>(name)) + ": " + _actionReasons[name]));
        SelectableTextBlock control = C<SelectableTextBlock>(target);
        if (control.Text != text) control.Text = text;
        control.IsVisible = text.Length > 0;
        AutomationProperties.SetName(control, text);
    }
    private void UpdateGrantActions()
    {
        foreach (Button revoke in C<StackPanel>("PluginGrants").Children.OfType<Button>())
        {
            if (revoke.Tag is not PluginGrantSnapshot grant) continue;
            string? reason = _busy ? Texts.T("plugins.reason.busy") : grant.Access == FilterDataAccess.None ? Texts.T("plugins.reason.noGrant") : null;
            revoke.IsEnabled = reason == null;
            AutomationProperties.SetHelpText(revoke, reason ?? string.Empty);
            if (revoke.Parent is StackPanel panel && panel.Children.IndexOf(revoke) + 1 is int index && index < panel.Children.Count && panel.Children[index] is SelectableTextBlock help)
            { help.Text = reason; help.IsVisible = reason != null; AutomationProperties.SetName(help, reason ?? string.Empty); }
        }
    }
}

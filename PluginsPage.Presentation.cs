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
    private void RebuildLocalizedSettings(PluginCatalogEntry entry)
    {
        Control? focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        string? scope = focused == null ? null : _editors.FirstOrDefault(pair =>
            focused.GetVisualAncestors().Contains(pair.Value)).Key;
        string? name = focused?.Name;
        var expansion = C<StackPanel>("PluginSettings").Children.OfType<Expander>().Select(item => item.IsExpanded).ToArray();
        var offset = C<ScrollViewer>("PluginDetailsPane").Offset;
        BuildSettings(entry);
        int index = 0;
        foreach (Expander expander in C<StackPanel>("PluginSettings").Children.OfType<Expander>())
            if (index < expansion.Length) expander.IsExpanded = expansion[index++];
        if (scope != null && name != null && _editors.TryGetValue(scope, out var editor))
        {
            // Restore the same scoped input after layout; invalid raw text stays in the draft.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_ended || Selected?.Key != entry.Key || !_editors.TryGetValue(scope, out var current) || !ReferenceEquals(editor, current) || !editor.IsEffectivelyVisible) return;
                editor.GetVisualDescendants().OfType<Control>().FirstOrDefault(control => control.Name == name)?.Focus();
                C<ScrollViewer>("PluginDetailsPane").Offset = offset;
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    private void RenderIdentity(PluginCatalogEntry entry)
    {
        Descriptions descriptions = C<Descriptions>("PluginIdentity");
        void Row(string key, string? value)
        {
            if (!_identityRows.TryGetValue(key, out var row))
            {
                SelectableTextBlock text = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
                DescriptionsItem item = new() { Content = text };
                row = (item, text); _identityRows.Add(key, row); descriptions.Items.Add(item);
            }
            string label = Texts.T("plugins.detail." + key);
            row.Item.Label = label;
            string display = string.IsNullOrEmpty(value) ? Texts.T("plugins.detail.unavailable") : value;
            if (row.Value.Text != display) row.Value.Text = display;
            AutomationProperties.SetName(row.Value, label + ": " + display);
        }
        static string? Release(PluginReleaseSnapshot? release) => release == null ? null :
            release.Description.PluginVersion + " · SDK " + release.Description.SdkVersion + "\n" + release.ExecutionRevision;
        Row("id", entry.PluginId ?? entry.Description?.PluginId);
        // A retained description from another release is never the candidate's version.
        Row("candidate", entry.CandidateId);
        Row("version", entry.Candidate?.Description?.PluginVersion);
        Row("sdk", entry.Candidate?.Description?.SdkVersion);
        Row("path", entry.LoaderPath);
        Row("active", Release(entry.Activation?.Active));
        Row("pending", Release(entry.Activation?.Pending));
        Row("pinned", Release(entry.Activation?.Pinned));
        Row("lastGood", Release(entry.Activation?.LastGood));
    }

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

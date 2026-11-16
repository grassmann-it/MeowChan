using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using Ursa.Controls;

namespace MeowChan;

public partial class SourcesPage
{
    private IReadOnlyList<SourceRow> _registeredRows = Array.Empty<SourceRow>();
    private bool _projectingSources;
    internal PluginsPage PluginManagement => SourcePluginManagement;

    private void InitializeMergedSources()
    {
        SourcePluginManagement.CatalogChanged += (_, _) => RefreshSourceRows();
        SourcePluginManagement.PresentationChanged += (_, _) => RenderSourcePluginBlock(select: false);
        SourcePluginManagement.BusyChanged += (_, _) => { RenderDetails(); BusyChanged?.Invoke(this, EventArgs.Empty); };
        SourcePluginManagement.SourceOverviewRequested += (_, _) =>
        {
            if (_ended) return;
            RenderDetails();
            FocusSelectedSource();
        };
        SourcePluginManagement.FocusSourceCard = FocusSelectedSource;
        SourcePluginManagement.OpenSourceWatches = SelectSource;
        AutomationProperties.SetName(this.FindControl<SelectionList>("SourceCards")!, Texts.T("plugins.sources"));
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Handled || e.Key != Key.Escape || IsBusy || SourcePluginManagement.ShowingSourcePluginDetails) return;
            if (_narrow && _showDetails) { OnBackClick(this, new RoutedEventArgs()); e.Handled = true; }
        }, RoutingStrategies.Bubble);
    }

    /// <summary>Focus lands on the detail heading when only the detail shows (narrow), else on the selected source's card; false when there is none.</summary>
    private bool FocusSelectedSource()
    {
        if (_ended) return false;
        if (_narrow && _showDetails) { FocusSourceControlAfterLayout(this.FindControl<TextBlock>("SourceHeading")!); return true; }
        if (_selectedId is { } id && _sourceCards.TryGetValue(id, out var card)) { FocusSourceControlAfterLayout(card.Control); return true; }
        return false;
    }

    internal bool TryDismissMergedLayer()
    {
        if (SourcePluginManagement.TryDismissMergedLayer()) return true;
        if (!_busy && _narrow && _showDetails) { OnBackClick(this, new RoutedEventArgs()); return true; }
        return false;
    }

    internal Task ActivateAsync(SourcesPluginsFilter filter = SourcesPluginsFilter.Sources) => SourcePluginManagement.ActivateMergedAsync(filter);
    internal async Task ShowPlugin(string sourceId)
    {
        await SourcePluginManagement.ActivateMergedAsync();
        if (_ended) return;
        SelectSource(sourceId);
        OnPluginClick(this, new RoutedEventArgs());
    }

    private void RenderSourcePluginBlock(bool select = true)
    {
        if (_ended) return;
        var row = Selected;
        var plugin = row?.Plugin;
        if (SourcePluginManagement.ShowingSourcePluginDetails) return;
        this.FindControl<Border>("SourcePluginBlock")!.IsVisible = plugin != null;
        this.FindControl<Border>("SourcePluginBlock")!.IsEnabled = !IsBusy;
        if (plugin == null) return;
        if (select && SourcePluginManagement.Filter == SourcesPluginsFilter.Sources && SourcePluginManagement.Selected?.PluginId != plugin.PluginId)
            SourcePluginManagement.SelectSourcePlugin(plugin.Key);
        if (SourcePluginManagement.Selected?.PluginId != plugin.PluginId) return;
        SourcePluginManagement.CopySourcePluginPresentation(
            this.FindControl<TextBlock>("SourcePluginSummary")!, this.FindControl<WrapPanel>("SourcePluginSteps")!,
            this.FindControl<TextBlock>("SourcePluginNext")!, this.FindControl<TextBlock>("SourcePluginShared")!,
            this.FindControl<Button>("SourcePluginEnabled")!, this.FindControl<Button>("SourcePluginCandidate")!, this.FindControl<Button>("SourcePluginSdk")!, row!.Id);
        ApplyChanSiteSwitch(plugin, row);
        Button details = this.FindControl<Button>("SourcePluginButton")!;
        CommandBindings.ApplyLabeled(details, "plugins.details", Texts.T("sources.pluginDetails"));
        details.IsEnabled = !IsBusy;
        CommandBindings.Apply(this.FindControl<Button>("SourcePluginMore")!, "plugins.actions", iconOnly: true);
        this.FindControl<Button>("SourcePluginMore")!.IsEnabled = !IsBusy;
    }

    private sealed record ChanSiteToggle(string SourceId, bool Enabled);

    // A site of an engine-family plugin has its own switch while its plugin is on; with the plugin off, the copied
    // plugin button (which turns every site of the family on together) stays, so the master switch is reachable here.
    private void ApplyChanSiteSwitch(Desktop.Plugins.PluginCatalogEntry plugin, SourceListRow row)
    {
        if (plugin.PluginId is not { } owner || plugin.Activation?.Enabled != true
            || GetChanSiteEnabled?.Invoke(owner, row.Id) is not bool enabled) return;
        Button button = this.FindControl<Button>("SourcePluginEnabled")!;
        CommandBindings.ApplyLabeled(button, enabled ? "plugins.disable" : "plugins.enable",
            Texts.T(enabled ? "sources.site.disable" : "sources.site.enable"));
        button.Tag = new ChanSiteToggle(row.Id, !enabled);
        button.IsVisible = true;
        button.IsEnabled = !IsBusy && SetChanSiteEnabled != null;
        TextBlock shared = this.FindControl<TextBlock>("SourcePluginShared")!;
        shared.Text = Texts.T("sources.familySite", row.Name, plugin.Name);
        shared.IsVisible = true;
    }

    private async void OnSourcePluginEnabledClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ChanSiteToggle toggle } && !IsBusy && SetChanSiteEnabled is { } set)
        {
            bool saved = false;
            _busy = true; BusyChanged?.Invoke(this, EventArgs.Empty); RenderDetails();
            try { saved = await set(toggle.SourceId, toggle.Enabled); }
            catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException) { }
            finally
            {
                _busy = false;
                if (!_ended)
                {
                    SetActionError(saved ? null : Texts.T("sources.site.saveFailed"));
                    if (GetSourceMediaIntegrityPolicies?.Invoke() is { } current)
                        _rows = _rows.Select(item => item with { MediaIntegrityPolicy = SourcesSnapshot.FindPolicy(item.Id, current) }).ToArray();
                    BusyChanged?.Invoke(this, EventArgs.Empty); RenderDetails();
                }
            }
            return;
        }
        await ExecuteSourcePluginButton(sender);
    }
    private async void OnSourcePluginCandidateClick(object? sender, RoutedEventArgs e) => await ExecuteSourcePluginButton(sender);
    private Task ExecuteSourcePluginButton(object? sender) => sender is Button action && !IsBusy
        ? SourcePluginManagement.ExecuteSourcePluginActionAsync(action) : Task.CompletedTask;
    private void OnSourcePluginSdkClick(object? sender, RoutedEventArgs e) { if (!IsBusy) SourcePluginManagement.ApproveSourceSdk(); }
    private void OnSourcePluginMoreClick(object? sender, RoutedEventArgs e)
    {
        if (!IsBusy && Selected?.Plugin is { } plugin && sender is Button origin)
            SourcePluginManagement.OpenPluginActionMenu(plugin.Key, origin, details: true);
    }

    private void OnSourceSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_projectingSources || IsBusy) return;
        if (this.FindControl<SelectionList>("SourceCards")!.SelectedItem is SelectionListItem selected)
        {
            string? id = _sourceCards.FirstOrDefault(pair => ReferenceEquals(pair.Value.Control, selected)).Key;
            if (id != null) { SetSelectedSource(id); RenderDetails(); }
        }
    }
    private void OnSourceListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || IsBusy) return;
        SelectionList list = this.FindControl<SelectionList>("SourceCards")!;
        if (CommandBindings.Matches(e, "menu.context-open"))
        {
            if (_selectedId is { } id && _sourceCards.TryGetValue(id, out var card)) OpenSourceContextMenu(card.Control, id);
            e.Handled = true; return;
        }
        if (e.Key == Key.Enter && Selected != null)
        { _showDetails = true; RenderDetails(); FocusSourceControlAfterLayout(this.FindControl<TextBlock>("SourceHeading")!); e.Handled = true; return; }
        if (e.Key is not (Key.Up or Key.Down or Key.Home or Key.End) || list.Items.Count == 0) return;
        list.SelectedIndex = e.Key switch { Key.Home => 0, Key.End => list.Items.Count - 1,
            Key.Up => Math.Max(0, list.SelectedIndex - 1), _ => Math.Min(list.Items.Count - 1, list.SelectedIndex + 1) };
        if (list.SelectedItem is Control item) { item.Focus(); item.BringIntoView(); }
        e.Handled = true;
    }
}

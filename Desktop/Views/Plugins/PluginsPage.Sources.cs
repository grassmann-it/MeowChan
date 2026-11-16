using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;

namespace MeowChan;

internal enum SourcesPluginsFilter { Sources, ImageFilters, AllPlugins }

internal static class SourcesPluginsFilterPolicy
{
    internal static bool Includes(PluginCatalogEntry entry, SourcesPluginsFilter filter) => filter switch
    {
        SourcesPluginsFilter.Sources => entry.IsSource,
        SourcesPluginsFilter.ImageFilters => entry.IsFilter,
        _ => true
    };
}

// This management control now lives inside SourcesPage, not in a separate workspace destination. Keeping the existing
// action/draft/repair owner intact also keeps captured revisions, generations and all confirmation dialogs intact.
public partial class PluginsPage
{
    internal event EventHandler? CatalogChanged;
    internal event EventHandler? PresentationChanged;
    internal event EventHandler? SourceOverviewRequested;
    /// <summary>Set by the Sources page: focuses its selected source card (or the detail heading, narrow). False when it has none.</summary>
    internal Func<bool>? FocusSourceCard { get; set; }
    internal IReadOnlyList<PluginCatalogEntry> CatalogEntries => _catalog?.Entries ?? Array.Empty<PluginCatalogEntry>();
    internal SourcesPluginsFilter Filter => (SourcesPluginsFilter)Math.Max(0, C<ComboBox>("PluginCategory").SelectedIndex);
    internal bool ShowingSourcePluginDetails => SourceContent != null && Filter == SourcesPluginsFilter.Sources && _showDetails;
    private string _sourceCountText = string.Empty;

    /// <summary>The source overview supplied by SourcesPage. It shares this control's header, operations and inventory banners.</summary>
    public Control? SourceContent
    {
        get => C<ContentControl>("SourceContentHost").Content as Control;
        set { C<ContentControl>("SourceContentHost").Content = value; SetFilter(SourcesPluginsFilter.Sources); }
    }

    private bool _sourceOperationBusy;
    internal void SetSourceOperationBusy(bool busy)
    {
        if (_sourceOperationBusy == busy) return;
        _sourceOperationBusy = busy;
        C<WrapPanel>("PluginsHeaderActions").IsEnabled = !busy;
        UpdateTestButtons();
        if (!busy && !_busy && _testStatusKey == null) C<TextBlock>("PluginTestStatus").Text = string.Empty;
        FitStatusRow();
    }

    internal bool TryDismissMergedLayer()
    {
        if (PluginActionMenu?.IsOpen == true) { ClosePluginActionMenu(restoreFocus: true); return true; }
        if (_busy) { OnCancelClick(this, new Avalonia.Interactivity.RoutedEventArgs()); return true; }
        if (ShowingSourcePluginDetails) { ReturnToSourceOverview(); return true; }
        return false;
    }

    internal void SetSourceCount(int total, int active)
    {
        _sourceCountText = Texts.T("sources.countActive", total, active);
        if (SourceContent != null && Filter == SourcesPluginsFilter.Sources)
        {
            C<TextBlock>("PluginsCount").Text = _sourceCountText;
            C<TextBlock>("PluginsCount").IsVisible = true;
        }
    }

    internal void SetFilter(SourcesPluginsFilter filter)
    {
        if (_ended || _busy || IsDiscardConfirmationOpen) return;
        _showDetails = false;
        C<ComboBox>("PluginCategory").SelectedIndex = (int)filter;
        RenderList(); RenderDetails();
    }

    internal async Task ActivateMergedAsync(SourcesPluginsFilter filter = SourcesPluginsFilter.Sources)
    {
        SetFilter(filter);
        await ActivateAsync();
    }

    internal void SelectSourcePlugin(string key, bool details = false)
    {
        if (_busy || IsDiscardConfirmationOpen) return;
        SelectCore(key, openDetails: details);
        if (details) FocusPluginHeading();
    }

    private void FocusPluginHeading() => Dispatcher.UIThread.Post(() =>
    {
        TextBlock heading = C<TextBlock>("PluginHeading");
        if (_ended || !heading.IsEffectivelyVisible) return;
        heading.Focusable = true; heading.BringIntoView(); heading.Focus();
    }, DispatcherPriority.Loaded);

    internal void ReturnToSourceOverview()
    {
        if (_busy || IsDiscardConfirmationOpen) return;
        _showDetails = false;
        ApplyPaneVisibility();
        SourceOverviewRequested?.Invoke(this, EventArgs.Empty);
    }

    internal bool SourcePluginNeedsAttention(PluginCatalogEntry entry) => GroupNeedsAttention(entry);

    internal void CopySourcePluginPresentation(TextBlock summary, WrapPanel steps, TextBlock next, TextBlock shared,
        Button enabled, Button candidate, Button sdk, string sourceId, Button? review = null)
    {
        if (Selected is not { } entry) return;
        PluginEntryPresentation presentation = PluginEntryPresentation.Create(entry, _services?.GetProductionWorkerSnapshots?.Invoke(), CurrentDraft, _catalog?.Profile.Revision);
        summary.Text = Texts.T("sources.pluginSummary", entry.Name, PluginContentPresentation.DescribeStatus(presentation));
        RenderLifecycleSteps(steps, entry, presentation);
        next.Text = C<TextBlock>("PluginLifecycleNext").Text;
        next.IsVisible = !string.IsNullOrEmpty(next.Text);
        string[] siblings = presentation.Sources.Where(source => source.SourceId != sourceId).Select(source => source.Name).ToArray();
        shared.Text = siblings.Length > 0 ? Texts.T("sources.appliesTogether", string.Join(", ", siblings)) : string.Empty;
        shared.IsVisible = siblings.Length > 0;
        CopyAction("PluginEnabled", enabled);
        CopyAction("PluginSelectCandidate", candidate);
        CopyAction("PluginApproveSdkTransition", sdk);
        if (review is not null) CopyAction("PluginReviewRepair", review);
        void CopyAction(string name, Button target)
        {
            Button original = C<Button>(name);
            string id = name == "PluginEnabled" ? entry.Activation?.Enabled == true ? "plugins.disable" : "plugins.enable"
                : "plugins.select-candidate";
            if (name is "PluginApproveSdkTransition" or "PluginReviewRepair")
            {
                string label = Texts.T(name == "PluginReviewRepair" ? "plugins.repair.reviewNow" : "plugins.sdkTransition.action");
                target.Content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
                    Children = { CommandBindings.NamedIcon("check"), new TextBlock { Text = label } } };
                AutomationProperties.SetName(target, label);
            }
            else CommandBindings.ApplyLabeled(target, id, CommandCatalog.Label(id));
            target.IsVisible = original.IsVisible;
            target.IsEnabled = original.IsEnabled;
            // Reuse the exact action invocation, not a second activation implementation.
            target.Tag = original.Tag;
            AutomationProperties.SetHelpText(target, AutomationProperties.GetHelpText(original));
        }
    }

    internal Task ExecuteSourcePluginActionAsync(Button action) => action.Tag is PluginActionInvocation invocation
        ? ExecutePluginActionAsync(invocation, action) : Task.CompletedTask;

    internal void AppendSourcePluginActions(ContextMenuBuilder menu, string key, Control origin)
    {
        if (_catalog?.Entries.FirstOrDefault(entry => entry.Key == key) is not { } entry) return;
        var target = PluginActionTarget.Capture(entry, _catalog.Profile.Revision);
        menu.Section();
        foreach (string id in new[] { "plugins.details", entry.Activation?.Enabled == true ? "plugins.disable" : "plugins.enable",
            "plugins.show-settings", "plugins.select-candidate", "plugins.rollback", "plugins.show-grants", "plugins.show-developer-tools" })
        {
            var invocation = CapturePluginActionInvocation(id, target);
            bool CanExecute() => PluginActionUnavailableReason(invocation) == null;
            menu.Command(id, () => _ = ExecutePluginActionAsync(invocation, origin),
                CanExecute(), PluginActionUnavailableReason(invocation), availableWhen: CanExecute,
                reasonWhen: () => PluginActionUnavailableReason(invocation));
        }
    }

    /// <summary>Opens the plugin's details and there the bundled repair review, as the plugin page's own button does.</summary>
    internal void ReviewSourceRepair()
    {
        if (Selected is not { } entry || _busy || IsDiscardConfirmationOpen) return;
        SelectSourcePlugin(entry.Key, details: true);
        Dispatcher.UIThread.Post(() =>
        {
            if (!_ended && Selected?.Key == entry.Key && ShowingSourcePluginDetails)
                OnReviewBundledRepairsClick(C<Button>("PluginReviewRepair"), new Avalonia.Interactivity.RoutedEventArgs());
        }, DispatcherPriority.Loaded);
    }

    internal void ApproveSourceSdk()
    {
        if (Selected is not { } entry || _busy || IsDiscardConfirmationOpen) return;
        SelectSourcePlugin(entry.Key, details: true);
        Dispatcher.UIThread.Post(() =>
        {
            if (!_ended && Selected?.Key == entry.Key && ShowingSourcePluginDetails)
                OnApproveSdkTransitionClick(C<Button>("PluginApproveSdkTransition"), new Avalonia.Interactivity.RoutedEventArgs());
        }, DispatcherPriority.Loaded);
    }
}

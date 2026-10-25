using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Compilation;
using Ursa.Controls;

namespace MeowChan;

public partial class PluginsPage
{
    private sealed class PluginToolViewState
    {
        internal string Url = string.Empty;
        internal string Result = string.Empty;
        internal IReadOnlyList<PluginBuildDiagnostic> Diagnostics = Array.Empty<PluginBuildDiagnostic>();
    }

    private readonly Dictionary<string, Dictionary<string, bool>> _pluginExpansionStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginToolViewState> _pluginToolStates = new(StringComparer.Ordinal);
    private string? _pluginToolKey;
    private bool _restoringPluginTools;
    private bool _editorOptionsLoaded;

    private void InitializePluginDetails()
    {
        TrackExpansion(C<Expander>("PluginDeveloperExpander"), "developer-details");
        TrackExpansion(C<Expander>("PluginInfoExpander"), "information");
        TrackExpansion(C<Expander>("PluginGrantExpander"), "grants");
        TrackExpansion(C<Expander>("PluginEditorExpander"), "editor-options");
        C<TextBox>("PluginProbeUrl").TextChanging += (_, _) => CapturePluginProbeState();
        C<TextBox>("PluginProbeResult").TextChanging += (_, _) => CapturePluginProbeState();

        void TrackExpansion(Expander expander, string id)
        {
            expander.PropertyChanged += (_, args) =>
            {
                if (args.Property != Expander.IsExpandedProperty || _restoringPluginTools || _pluginToolKey is not { } key) return;
                ExpansionState(key)[id] = expander.IsExpanded;
                QueuePluginNavigationVisibility();
            };
        }
    }

    private Dictionary<string, bool> ExpansionState(string key)
    {
        if (!_pluginExpansionStates.TryGetValue(key, out var state))
            _pluginExpansionStates.Add(key, state = new(StringComparer.Ordinal));
        return state;
    }

    private PluginToolViewState ToolState(string key)
    {
        if (!_pluginToolStates.TryGetValue(key, out var state))
            _pluginToolStates.Add(key, state = new());
        return state;
    }

    private void CapturePluginProbeState()
    {
        if (_restoringPluginTools || _pluginToolKey is not { } key) return;
        PluginToolViewState state = ToolState(key);
        state.Url = C<TextBox>("PluginProbeUrl").Text ?? string.Empty;
        state.Result = C<TextBox>("PluginProbeResult").Text ?? string.Empty;
    }

    // Selection and rebuild restore UI state, never an old test authorization or a configuration draft.
    private void RestorePluginToolState(PluginCatalogEntry entry)
    {
        CapturePluginProbeState();
        _pluginToolKey = entry.Key;
        _restoringPluginTools = true;
        try
        {
            if (!_editorOptionsLoaded)
            {
                PluginEditorOptions options = GetEditorOptions?.Invoke() ?? new(null, new[] { "{file}" });
                C<TextBox>("PluginEditorExecutable").Text = options.Executable;
                C<TextBox>("PluginEditorArguments").Text = string.Join(Environment.NewLine, options.Arguments);
                _editorOptionsLoaded = true;
            }
            var expansion = ExpansionState(entry.Key);
            foreach (var item in new[] { ("PluginDeveloperExpander", "developer-details"), ("PluginInfoExpander", "information"),
                ("PluginGrantExpander", "grants"), ("PluginEditorExpander", "editor-options") })
                C<Expander>(item.Item1).IsExpanded = expansion.GetValueOrDefault(item.Item2);
            PluginToolViewState state = ToolState(entry.Key);
            C<TextBox>("PluginProbeUrl").Text = state.Url;
            C<TextBox>("PluginProbeResult").Text = state.Result;
            RenderDiagnostics(state.Diagnostics, entry);
            C<TextBlock>("PluginTechnicalCopyStatus").Text = string.Empty;
        }
        finally { _restoringPluginTools = false; }
    }

    private void RenderTechnicalIdentity(PluginCatalogEntry entry)
    {
        Descriptions descriptions = C<Descriptions>("PluginIdentity");
        foreach (PluginTechnicalDetail detail in TechnicalDetails(entry))
        {
            if (!_identityRows.TryGetValue(detail.Key, out var row))
            {
                SelectableTextBlock value = new() { TextWrapping = TextWrapping.Wrap, Focusable = true };
                DescriptionsItem item = new() { Content = value };
                row = (item, value); _identityRows.Add(detail.Key, row); descriptions.Items.Add(item);
            }
            row.Item.Label = detail.Label;
            if (row.Value.Text != detail.Value) row.Value.Text = detail.Value;
            AutomationProperties.SetName(row.Value, detail.Label + ": " + detail.Value);
        }
    }

    private IReadOnlyList<PluginTechnicalDetail> TechnicalDetails(PluginCatalogEntry entry) => PluginTechnicalDetails.Create(entry,
        PluginEntryPresentation.Create(entry, _services?.GetProductionWorkerSnapshots?.Invoke(), CurrentDraft, _catalog?.Profile.Revision).Worker);

    private async void OnCopyTechnicalDetailsClick(object? sender, RoutedEventArgs args)
    {
        if (_ended || Selected is not { } entry) return;
        string key = entry.Key;
        string details = PluginTechnicalDetails.Format(TechnicalDetails(entry));
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard
                ?? throw new InvalidOperationException(Texts.T("plugins.clipboardUnavailable"));
            await clipboard.SetTextAsync(details);
            if (!_ended && Selected?.Key == key)
                C<TextBlock>("PluginTechnicalCopyStatus").Text = Texts.T("plugins.technicalDetailsCopied");
        }
        catch (Exception exception)
        {
            if (!_ended && Selected?.Key == key)
                C<TextBlock>("PluginTechnicalCopyStatus").Text = Texts.T("plugins.technicalDetailsCopyFailed", exception.Message);
        }
    }

    internal void OpenPluginDeveloperDetails()
    {
        if (!_ended && Selected is { } entry && Included(entry))
            C<Expander>("PluginDeveloperExpander").IsExpanded = true;
    }

    private void FocusPluginBuildDiagnostics()
    {
        string? key = _selectedKey;
        long generation = _operationGeneration;
        if (_ended || _busy || key == null) return;
        OpenPluginDeveloperDetails();
        Dispatcher.UIThread.Post(() =>
        {
            if (_ended || _busy || _selectedKey != key || generation != _operationGeneration || TopLevel.GetTopLevel(this) == null) return;
            StackPanel diagnostics = C<StackPanel>("PluginBuildDiagnostics");
            if (diagnostics.Children.Count == 0) return;
            TopLevel.GetTopLevel(this)!.UpdateLayout();
            diagnostics.Focusable = true;
            AutomationProperties.SetName(diagnostics, Texts.T("plugins.buildDiagnostics"));
            diagnostics.BringIntoView(); diagnostics.Focus();
        }, DispatcherPriority.Loaded);
    }
}

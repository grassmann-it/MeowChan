using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;

namespace MeowChan;

public partial class PluginsPage
{
    private readonly Dictionary<string, Expander> _settingsGroups = new(StringComparer.Ordinal);
    private Button? _pluginSchemaAcknowledgement;
    private string? _settingsEntryKey;
    private long _settingsRenderGeneration;

    private void BuildSettings(PluginCatalogEntry entry)
    {
        _settingsRenderGeneration++;
        _settingsEntryKey = entry.Key;
        _pluginSchemaAcknowledgement = null;
        StackPanel settings = C<StackPanel>("PluginSettings");
        settings.Children.Clear(); _editors.Clear(); _settingsGroups.Clear();
        if (entry.Description == null || entry.CandidateId == null) return;
        if (!_drafts.TryGetValue(entry.Key, out PluginConfigurationDraft? draft))
        {
            draft = new(entry, _catalog!.Profile.Revision); _drafts[entry.Key] = draft;
            draft.Plugin.Changed += UpdateDraftState;
            draft.Plugin.Changed += CancelDiscardConfirmation;
            foreach (PluginSettingsDraft source in draft.Sources.Values)
            { source.Changed += UpdateDraftState; source.Changed += CancelDiscardConfirmation; }
        }
        if (draft.NeedsMigrationAcknowledgement)
        {
            settings.Children.Add(Label(Texts.T("plugins.removedSettings") + "\n" + string.Join("\n", draft.RemovedSettings.Select(issue => issue.Path))));
            Button acknowledge = new() { Content = Texts.T("plugins.acceptSchema") };
            AutomationProperties.SetAutomationId(acknowledge, "PluginSchemaAcknowledgement:" + Uri.EscapeDataString(entry.Key));
            _pluginSchemaAcknowledgement = acknowledge;
            acknowledge.Click += (_, _) =>
            {
                if (_ended || _busy || Selected?.Key != entry.Key || !ReferenceEquals(CurrentDraft, draft)) return;
                draft.AcknowledgeMigration(); BuildSettings(entry); UpdateDraftState(); RefreshPluginSections();
                Control target = _settingsGroups.Values.FirstOrDefault() as Control ?? C<StackPanel>("PluginSettings");
                target.Focusable = true; target.BringIntoView(); target.Focus();
            };
            settings.Children.Add(acknowledge);
        }
        AddSettings(Texts.T("plugins.pluginSettings"), string.Empty, draft.Plugin);
        foreach ((string sourceId, PluginSettingsDraft source) in draft.Sources)
            AddSettings(Texts.T("plugins.sourceSettings", draft.Description.Sources.First(value => value.SourceId == sourceId).Name), sourceId, source);
        if (_settingsGroups.Count == 0 && !draft.NeedsMigrationAcknowledgement)
            settings.Children.Add(Label(Texts.T("plugins.noSettings")));
        QueuePluginNavigationVisibility();
    }

    private void AddSettings(string label, string scope, PluginSettingsDraft draft)
    {
        if (draft.Schema.Fields.Count == 0 && draft.UnmappedValues.Count == 0) return;
        PluginSettingsEditor editor = new(draft, scope); _editors.Add(scope, editor);
        string entryKey = _settingsEntryKey!;
        string sectionId = SettingsSectionId(scope);
        Dictionary<string, bool> expansion = ExpansionState(entryKey);
        bool first = _settingsGroups.Count == 0;
        Expander group = new()
        {
            Header = label + (draft.UnmappedValues.Count > 0 ? " · " + Texts.T("plugins.settingsReviewRequired") : string.Empty),
            Content = editor,
            IsExpanded = expansion.TryGetValue(sectionId, out bool expanded) ? expanded : first,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };
        expansion.TryAdd(sectionId, group.IsExpanded);
        group.PropertyChanged += (_, args) =>
        {
            if (args.Property != Expander.IsExpandedProperty) return;
            ExpansionState(entryKey)[sectionId] = group.IsExpanded;
            QueuePluginNavigationVisibility();
        };
        _settingsGroups.Add(scope, group);
        C<StackPanel>("PluginSettings").Children.Add(group);
    }

    private static string SettingsSectionId(string scope) => "settings/" +
        (scope.Length == 0 ? "plugin" : "source/" + Uri.EscapeDataString(scope));

    private Control? ResolveSettingsTarget(string scope) => _settingsGroups.GetValueOrDefault(scope);

    private void RebuildLocalizedSettings(PluginCatalogEntry entry)
    {
        Control? focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        string? scope = _editors.FirstOrDefault(pair => ReferenceEquals(focused, pair.Value)
            || focused?.GetVisualAncestors().Contains(pair.Value) == true).Key;
        string? fieldIdentity = focused == null ? null : AutomationProperties.GetAutomationId(focused);
        bool focusedSchema = ReferenceEquals(focused, _pluginSchemaAcknowledgement);
        var offset = C<ScrollViewer>("PluginDetailsPane").Offset;
        long operation = _operationGeneration;
        BuildSettings(entry);
        long render = _settingsRenderGeneration;
        Dispatcher.UIThread.Post(() =>
        {
            if (_ended || _busy || operation != _operationGeneration || render != _settingsRenderGeneration || Selected?.Key != entry.Key) return;
            if (scope != null && !string.IsNullOrEmpty(fieldIdentity) && _editors.TryGetValue(scope, out var editor) && editor.IsEffectivelyVisible)
                editor.GetVisualDescendants().OfType<Control>().FirstOrDefault(control =>
                    AutomationProperties.GetAutomationId(control) == fieldIdentity)?.Focus();
            else if (focusedSchema) _pluginSchemaAcknowledgement?.Focus();
            C<ScrollViewer>("PluginDetailsPane").Offset = offset;
        }, DispatcherPriority.Loaded);
    }

    private void FocusFirstSettingError()
    {
        PluginConfigurationDraft? draft = CurrentDraft;
        string? key = _selectedKey;
        long generation = _operationGeneration;
        // Worker validation runs with the settings disabled. Wait for FinishOperation to enable them.
        Dispatcher.UIThread.Post(() =>
        {
            if (_ended || _busy || generation != _operationGeneration || !ReferenceEquals(CurrentDraft, draft)
                || TopLevel.GetTopLevel(this) == null)
            {
                Serilog.Log.Debug("Plugin save error focus skipped: ended {Ended}, busy {Busy}, generationMatches {GenerationMatches}, draftMatches {DraftMatches}, attached {Attached}",
                    _ended, _busy, generation == _operationGeneration, ReferenceEquals(CurrentDraft, draft), TopLevel.GetTopLevel(this) != null);
                return;
            }
            bool Current() => !_ended && !_busy && generation == _operationGeneration
                && _selectedKey == key && ReferenceEquals(CurrentDraft, draft);
            if (draft?.NeedsMigrationAcknowledgement == true && _pluginSchemaAcknowledgement is { } globalAcknowledgement)
            { globalAcknowledgement.BringIntoView(); globalAcknowledgement.Focus(); return; }
            foreach ((string scope, PluginSettingsEditor editor) in _editors)
                if (editor.TryFocusFirstError(() => Current() && _editors.GetValueOrDefault(scope) == editor)) return;
            foreach ((string scope, PluginSettingsEditor editor) in _editors)
            {
                if (editor.Draft.UnmappedValues.Count == 0 || editor.SchemaAcknowledgement is not { } fieldAcknowledgement) continue;
                if (_settingsGroups.TryGetValue(scope, out var group)) group.IsExpanded = true;
                Dispatcher.UIThread.Post(() =>
                {
                    if (!Current() || _editors.GetValueOrDefault(scope) != editor || !fieldAcknowledgement.IsEffectivelyVisible) return;
                    fieldAcknowledgement.BringIntoView(); fieldAcknowledgement.Focus();
                }, DispatcherPriority.Loaded);
                return;
            }
            TextBlock summary = C<TextBlock>("PluginPageError");
            summary.Focusable = true;
            AutomationProperties.SetName(summary, summary.Text);
            summary.BringIntoView();
            summary.Focus();
        }, DispatcherPriority.Background);
    }
}

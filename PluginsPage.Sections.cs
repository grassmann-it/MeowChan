using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class PluginsPage
{
    private SectionNavigationController? _pluginSectionNavigation;
    private (string Id, string Title, Control? Target)[] _pluginSectionSnapshot = Array.Empty<(string, string, Control?)>();
    public bool CanOpenSectionNavigation => !_ended && !_busy && !IsDiscardConfirmationOpen && Selected is { } entry && Included(entry)
        && _pluginSectionNavigation?.IsAvailable == true;
    public string? SelectedSectionId => C<SectionNavigationControl>("PluginSections").SelectedSectionId;
    public void OpenSectionNavigation() { if (CanOpenSectionNavigation) C<SectionNavigationControl>("PluginSections").OpenNavigation(); }
    public bool CanGoToSection(string id) => CanOpenSectionNavigation && C<SectionNavigationControl>("PluginSections").IsExpanded
        && _pluginSectionNavigation?.CanNavigate(id) == true;
    public void GoToSection(string id) { if (CanGoToSection(id)) C<SectionNavigationControl>("PluginSections").Navigate(id); }

    private void InitializePluginSections()
    {
        _pluginSectionNavigation = new SectionNavigationController(C<ScrollViewer>("PluginDetailsPane"),
            () => !_ended && !_busy && Selected is { } entry && Included(entry));
        C<SectionNavigationControl>("PluginSections").Connect(_pluginSectionNavigation);
    }
    private void RefreshPluginSections()
    {
        if (_pluginSectionNavigation == null) return;
        var sections = new List<SectionDescriptor>();
        if (Selected is { } entry)
        {
            string key = Uri.EscapeDataString(entry.Key) + "/";
            sections.Add(new(key + "overview", Texts.T("plugins.information") + " · " + entry.Name, () => C<TextBlock>("PluginHeading")));
            AddSetting(string.Empty, Texts.T("plugins.pluginSettings"));
            if (CurrentDraft is { } draft)
                foreach (string sourceId in draft.Sources.Keys)
                {
                    var currentSource = entry.Description?.Sources.FirstOrDefault(source => source.SourceId == sourceId);
                    if (currentSource == null) continue; // Retained dirty drafts do not make removed catalog scopes navigable.
                    string label = currentSource.Name;
                    // IDs remain distinct even when several Source scopes have the same display name.
                    AddSetting(sourceId, Texts.T("plugins.sourceSettings", label) + " · " + sourceId);
                }
            foreach (var section in new[] { ("information", "PluginInfoExpander"), ("grants", "PluginGrantExpander"),
                ("source-tools", "PluginSourceExpander"), ("editor", "PluginEditorExpander") })
            {
                string name = section.Item2;
                Expander expander = C<Expander>(name);
                if (!expander.IsVisible || section.Item1 == "editor" && !C<Expander>("PluginSourceExpander").IsVisible) continue;
                sections.Add(new(key + section.Item1, expander.Header?.ToString() ?? section.Item1,
                    () => Selected?.Key == entry.Key ? C<Expander>(name) : null, () => expander.IsExpanded = true));
            }
            void AddSetting(string scope, string title)
            {
                Control? Resolve()
                {
                    if (Selected?.Key != entry.Key || !_editors.TryGetValue(scope, out var editor)) return null;
                    // Recheck live catalog membership even for an already captured descriptor.
                    if (scope.Length > 0 && Selected?.Description?.Sources.Any(source => source.SourceId == scope) != true) return null;
                    return C<StackPanel>("PluginSettings").Children.OfType<Expander>().SingleOrDefault(group => ReferenceEquals(group.Content, editor));
                }
                if (!_editors.TryGetValue(scope, out var form) || form.Draft.Schema.Fields.Count == 0) return;
                sections.Add(new(key + "settings/" + (scope.Length == 0 ? "plugin" : "source/" + Uri.EscapeDataString(scope)), title,
                    Resolve, () => { if (Resolve() is Expander expander) expander.IsExpanded = true; }));
            }
        }
        var snapshot = sections.Select(section => (section.Id, section.Title, section.ResolveTarget())).ToArray();
        if (_pluginSectionSnapshot.SequenceEqual(snapshot)) return; // status refresh never rebuilds active navigation
        _pluginSectionSnapshot = snapshot;
        _pluginSectionNavigation.SetSections(sections);
        C<SectionNavigationControl>("PluginSections").Refresh();
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class PluginsPage
{
    private SectionNavigationController? _pluginSectionNavigation;
    private (string Id, string Title, Control? Target)[] _pluginSectionSnapshot = Array.Empty<(string, string, Control?)>();
    private bool _pluginNavigationVisibilityQueued;
    private bool _pluginNavigationUseful;
    public bool CanOpenSectionNavigation => !_ended && !_busy && !IsDiscardConfirmationOpen && Selected is { } entry && Included(entry)
        && _pluginNavigationUseful && _pluginSectionNavigation?.IsAvailable == true;
    public string? SelectedSectionId => C<SectionNavigationControl>("PluginSections").SelectedSectionId;
    public void OpenSectionNavigation() { if (CanOpenSectionNavigation) C<SectionNavigationControl>("PluginSections").OpenNavigation(); }
    public bool CanGoToSection(string id) => CanOpenSectionNavigation && C<SectionNavigationControl>("PluginSections").IsExpanded
        && _pluginSectionNavigation?.CanNavigate(id) == true;
    public void GoToSection(string id) { if (CanGoToSection(id)) C<SectionNavigationControl>("PluginSections").Navigate(id); }

    private void InitializePluginSections()
    {
        ScrollViewer scroll = C<ScrollViewer>("PluginDetailsPane");
        _pluginSectionNavigation = new SectionNavigationController(scroll,
            () => !_ended && !_busy && Selected is { } entry && Included(entry));
        C<SectionNavigationControl>("PluginSections").Connect(_pluginSectionNavigation);
        scroll.SizeChanged += (_, _) => QueuePluginNavigationVisibility();
        C<StackPanel>("PluginDetailContent").LayoutUpdated += (_, _) => QueuePluginNavigationVisibility();
    }

    private void RefreshPluginSections()
    {
        if (_pluginSectionNavigation == null) return;
        var sections = new List<SectionDescriptor>();
        if (Selected is { } entry)
        {
            string key = Uri.EscapeDataString(entry.Key) + "/";
            sections.Add(new(key + "overview", entry.Name, () => Selected?.Key == entry.Key ? C<TextBlock>("PluginHeading") : null));
            if (CurrentDraft?.NeedsMigrationAcknowledgement == true && _pluginSchemaAcknowledgement != null)
                sections.Add(new(key + "settings/schema", Texts.T("plugins.reviewSchema"),
                    () => Selected?.Key == entry.Key ? _pluginSchemaAcknowledgement : null));
            AddSetting(string.Empty, Texts.T("plugins.pluginSettings"));
            if (CurrentDraft is { } draft)
                foreach (string sourceId in draft.Sources.Keys)
                {
                    var currentSource = entry.Description?.Sources.FirstOrDefault(source => source.SourceId == sourceId);
                    if (currentSource == null) continue;
                    AddSetting(sourceId, Texts.T("plugins.sourceSettings", currentSource.Name) + " · " + sourceId);
                }
            foreach (var section in new[] { ("information", "PluginInfoExpander"), ("grants", "PluginGrantExpander"),
                ("developer-details", "PluginDeveloperExpander") })
            {
                string name = section.Item2;
                Expander expander = C<Expander>(name);
                if (!expander.IsVisible || section.Item1 == "information" && C<StackPanel>("PluginInformation").Children.Count == 0) continue;
                sections.Add(new(key + section.Item1, expander.Header?.ToString() ?? section.Item1,
                    () => Selected?.Key == entry.Key ? C<Expander>(name) : null, () => expander.IsExpanded = true));
            }
            void AddSetting(string scope, string title)
            {
                Control? Resolve()
                {
                    if (Selected?.Key != entry.Key || !_editors.ContainsKey(scope)) return null;
                    if (scope.Length > 0 && Selected?.Description?.Sources.Any(source => source.SourceId == scope) != true) return null;
                    return ResolveSettingsTarget(scope);
                }
                if (Resolve() == null) return;
                sections.Add(new(key + SettingsSectionId(scope), title, Resolve,
                    () => { if (Resolve() is Expander expander) expander.IsExpanded = true; }));
            }
        }
        var snapshot = sections.Select(section => (section.Id, section.Title, section.ResolveTarget())).ToArray();
        if (!_pluginSectionSnapshot.SequenceEqual(snapshot))
        {
            _pluginSectionSnapshot = snapshot;
            _pluginSectionNavigation.SetSections(sections);
            C<SectionNavigationControl>("PluginSections").Refresh();
        }
        QueuePluginNavigationVisibility();
    }

    private void QueuePluginNavigationVisibility()
    {
        if (_ended || _pluginNavigationVisibilityQueued) return;
        _pluginNavigationVisibilityQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _pluginNavigationVisibilityQueued = false;
            if (_ended) return;
            SectionNavigationControl navigation = C<SectionNavigationControl>("PluginSections");
            ScrollViewer scroll = C<ScrollViewer>("PluginDetailsPane");
            bool hasTargets = (_pluginSectionNavigation?.Sections.Count ?? 0) > 1;
            bool hasUserContent = _settingsGroups.Count > 0 || _pluginSchemaAcknowledgement != null
                || C<Expander>("PluginGrantExpander").IsVisible
                || C<StackPanel>("PluginInformation").Children.Count > 0
                || C<Expander>("PluginDeveloperExpander").IsExpanded;
            // Exclude the navigator's own height, so showing it cannot make itself necessary.
            double naturalHeight = scroll.Extent.Height - (navigation.IsVisible ? navigation.Bounds.Height + C<StackPanel>("PluginDetailContent").Spacing : 0);
            bool longContent = scroll.Viewport.Height > 0 && naturalHeight > scroll.Viewport.Height + 1;
            _pluginNavigationUseful = hasTargets && hasUserContent && (longContent || navigation.IsExpanded || navigation.IsKeyboardFocusWithin);
            if (navigation.IsVisible != _pluginNavigationUseful) navigation.IsVisible = _pluginNavigationUseful;
        }, DispatcherPriority.Loaded);
    }
}

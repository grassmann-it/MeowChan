using System.Collections.Generic;
using System.Linq;
using Avalonia.VisualTree;
using Avalonia.Controls;
using MeowChan.Desktop;

namespace MeowChan;

public partial class SettingsEditor
{
    private SectionNavigationController? _sectionNavigation;
    public bool CanOpenSectionNavigation => IsEditable && _sectionNavigation?.IsAvailable == true;
    public string? SelectedSectionId => SettingsSections.SelectedSectionId;
    public void OpenSectionNavigation() { if (CanOpenSectionNavigation) SettingsSections.OpenNavigation(); }
    public bool CanGoToSection(string id) => IsEditable && SettingsSections.IsExpanded && _sectionNavigation?.CanNavigate(id) == true;
    public void GoToSection(string id) { if (CanGoToSection(id)) SettingsSections.Navigate(id); }

    private void InitializeSectionNavigation()
    {
        _sectionNavigation = new SectionNavigationController(SettingsScrollViewer, () => !_isClosed && IsEditable);
        _sectionNavigation.Navigating += () => { _suppressPassiveErrorReveal = true; _errorRevealRevision++; };
        SettingsSections.Connect(_sectionNavigation);
        RefreshSectionNavigation();
        AttachedToVisualTree += (_, _) => RefreshSectionNavigation();
    }

    private void RefreshSectionNavigation()
    {
        if (_sectionNavigation == null || _isClosed) return;
        var sections = new List<SectionDescriptor>
        {
            new("settings.interface", InterfaceTitle, () => SettingsInterfaceHeading),
            new("settings.reading-media", ReadingMediaTitle, () => SettingsReadingMediaHeading),
            new("settings.downloads", DownloadsTitle, () => SettingsDownloadsHeading),
            new("settings.worker-budgets", AdvancedBudgetsLabel, () => SettingsWorkerBudgetsHeading,
                () => SettingsWorkerBudgetsHeading.IsExpanded = true),
            new("settings.updates", UpdatesTitle, () => SettingsUpdatesHeading),
            new("settings.behavior", BehaviorTitle, () => SettingsBehaviorHeading)
        };
        AddOptional("settings.notifications", "SettingsNotificationsHeading");
        AddOptional("settings.shortcuts", "ShortcutsEditor");
        _sectionNavigation.SetSections(sections);
        SettingsSections.Refresh();

        void AddOptional(string id, string name)
        {
            Control? target = this.FindControl<Control>(name);
            if (target == null) return;
            string title = target is TextBlock text ? text.Text ?? id
                : MeowChan.Desktop.Localization.Texts.T("shortcuts.title");
            sections.Add(new SectionDescriptor(id, title, () =>
            {
                Control? current = this.FindControl<Control>(name);
                return name == "ShortcutsEditor"
                    ? (current as MeowChan.Desktop.Commands.ShortcutEditor)?.SectionHeading ?? current
                    : current;
            }));
        }
    }
}

using System;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Desktop.Commands;

namespace MeowChan;

public partial class MainWindow
{
    private void OnBrandAboutClick(object? sender, RoutedEventArgs e) => OnAboutMenuItemClick(sender, e);

    private void RefreshBrandNavigation()
    {
        RefreshReadingResumeLabels();
        RefreshPostBookmarkLabels();
        foreach (var (name, action) in new[]
        {
            ("DownloadsNavigationButton", "navigation.downloads"),
            ("ArchiveNavigationButton", "navigation.archive"),
            ("RulesNavigationButton", "navigation.rules"),
            ("SourcesNavigationButton", "navigation.sources"),
            ("DiagnosticsNavigationButton", "navigation.diagnostics"),
            ("SettingsNavigationButton", "navigation.settings"),
            ("PluginsNavigationButton", "navigation.plugins"),
            ("ToolsNavigationButton", "navigation.tools-menu")
        })
            CommandBindings.Apply(WorkspaceControl<Button>(name), action, iconWithLabel: true);

        foreach (var (name, action) in new[]
        {
            ("ArchiveMenuItem", "navigation.archive"),
            ("RulesMenuItem", "navigation.rules"),
            ("SourcesMenuItem", "navigation.sources"),
            ("PluginsMenuItem", "navigation.plugins"),
            ("DiagnosticsMenuItem", "navigation.diagnostics"),
            ("SettingsMenuItem", "navigation.settings"),
            ("AboutMenuItem", "navigation.about")
        })
            CommandBindings.Apply(WorkspaceControl<MenuItem>(name), action);

        if (WorkspaceControl<Button>("ToolsNavigationButton").Flyout is MenuFlyout tools)
            foreach (MenuItem item in tools.Items.OfType<MenuItem>())
                if (item.Tag is string key && key.StartsWith("nav.", StringComparison.Ordinal))
                    CommandBindings.Apply(item, "navigation." + key[4..]);

        Button brand = WorkspaceControl<Button>("BrandAboutButton");
        string about = CommandCatalog.Label("navigation.about");
        ToolTip.SetTip(brand, about);
        AutomationProperties.SetName(brand, about);
        AutomationProperties.SetHelpText(brand, about);
        UpdatePageNavigation();
    }
}

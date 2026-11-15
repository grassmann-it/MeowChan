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
        RefreshTagQueueLabels();
        foreach (string id in MainPageOrder.CommandIds)
            CommandBindings.Apply(WorkspaceControl<Button>(MainPageOrder.ControlStem(id) + "NavigationButton"), id, iconWithLabel: true);
        CommandBindings.Apply(WorkspaceControl<Button>("SettingsNavigationButton"), "navigation.settings", iconWithLabel: true);
        CommandBindings.Apply(WorkspaceControl<Button>("ToolsNavigationButton"), "navigation.tools-menu", iconWithLabel: true);

        CommandBindings.Apply(WorkspaceControl<MenuItem>("SettingsMenuItem"), "navigation.settings");
        CommandBindings.Apply(WorkspaceControl<MenuItem>("AboutMenuItem"), "navigation.about");

        if (ToolsNavigationButton.Flyout is MenuFlyout tools)
            foreach (MenuItem item in tools.Items.OfType<MenuItem>())
                if (item.Tag is string key && key.StartsWith("nav.", StringComparison.Ordinal))
                    CommandBindings.Apply(item, "navigation." + key[4..]);

        Button brand = BrandAboutButton;
        string about = CommandCatalog.Label("navigation.about");
        ToolTip.SetTip(brand, about);
        AutomationProperties.SetName(brand, about);
        AutomationProperties.SetHelpText(brand, about);
        UpdatePageNavigation();
    }
}

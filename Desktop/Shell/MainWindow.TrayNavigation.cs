using Avalonia.Controls;

namespace MeowChan;

/// <summary>
/// The tray menu's two page entries, below pause and the update reminder and above Exit: the statistics ("Status" once that page
/// is merged; today the statistics page) and the settings. Like the other tray entries they restore the window first, also from the
/// tray or a minimized state.
/// </summary>
public partial class MainWindow
{
    private NativeMenuItem? _trayStatusItem, _traySettingsItem;

    private void AddTrayNavigationItems(NativeMenu menu)
    {
        _trayStatusItem = CreateTrayCommandItem("navigation.statistics");
        _trayStatusItem.Click += (_, _) => ShowStatisticsFromTray();
        menu.Items.Add(_trayStatusItem);
        _traySettingsItem = CreateTrayCommandItem("navigation.settings");
        _traySettingsItem.Click += (_, _) =>
        {
            if (_isClosing) return;
            RestoreWindow();
            NavigateWorkspace(WorkspacePage.Settings);
        };
        menu.Items.Add(_traySettingsItem);
        menu.Items.Add(new NativeMenuItemSeparator());
    }

    private void RefreshTrayNavigationIcons()
    {
        if (_trayStatusItem != null) ApplyTrayCommandItem(_trayStatusItem, "navigation.statistics");
        if (_traySettingsItem != null) ApplyTrayCommandItem(_traySettingsItem, "navigation.settings");
    }

    private void DisposeTrayNavigation()
    {
        _trayStatusItem = null;
        _traySettingsItem = null;
    }
}

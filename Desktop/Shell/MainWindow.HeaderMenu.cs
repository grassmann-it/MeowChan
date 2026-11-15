using System;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Stats;
using Serilog;

namespace MeowChan;

/// <summary>
/// The context menu of the header's network and disk display (right-click, Menu key, Shift+F10): the arrangement as exclusive
/// choices (the last one, "Off", hides the display; focus then moves to the pause button), open the statistics, open the settings at the statistics block. A choice writes the same setting as the settings page
/// (<c>Config.StatsHeaderGraph</c>) and is saved at once, so the header, the menu and the settings never disagree. The entries are
/// composed in <see cref="HeaderIoMenu"/>; the opening and the focus return are <see cref="ContextMenuHost"/>.
/// </summary>
public partial class MainWindow
{
    private ContextMenuHost? _headerIoMenu;

    /// <summary>The menu host of the header display; read by the smokes.</summary>
    internal ContextMenuHost? HeaderIoMenuHost => _headerIoMenu;

    private ContextMenuHost? _headerThemeMenu;

    /// <summary>The menu host of the header theme button ("Follow system"); read by the smokes.</summary>
    internal ContextMenuHost? HeaderThemeMenuHost => _headerThemeMenu;

    private void InitializeHeaderIoMenu()
    {
        _headerIoMenu = new ContextMenuHost(TransferSpeed, ComposeHeaderIoMenu, fallbackFocus: () => (TransferSpeed.IsVisible ? TransferSpeed : PauseButton).Focus());
        _headerThemeMenu = new ContextMenuHost(ThemeToggleButton, ComposeHeaderThemeMenu);
    }

    private ContextMenuBuilder? ComposeHeaderThemeMenu() =>
        _isClosing || !_restoreCompleted || WorkspaceCommandBusy ? null
            : HeaderThemeMenu.Compose(_config.Theme, ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark, ChooseThemePreference);

    /// <summary>Stores and applies a theme preference from the header menu (same save path as the theme button).</summary>
    private async void ChooseThemePreference(string preference)
    {
        if (_isClosing || !_restoreCompleted || WorkspaceCommandBusy || preference == _config.Theme) return;
        Config candidate = _config.Clone();
        candidate.Theme = preference;
        await SaveAppearanceAsync(candidate);
    }

    private ContextMenuBuilder? ComposeHeaderIoMenu() =>
        _isClosing || !_restoreCompleted ? null : HeaderIoMenu.Compose(_config.StatsHeaderGraph, ChooseHeaderGraph, () => ShowStatistics(), ShowStatisticsSettings);

    /// <summary>Applies the arrangement at once and saves it; a save that fails restores the previous value and says so.</summary>
    private async void ChooseHeaderGraph(string value)
    {
        string normalized = IoStatsDisplay.NormalizeHeaderGraph(value);
        string previous = _config.StatsHeaderGraph;
        if (_isClosing || normalized == previous) return;
        _config.StatsHeaderGraph = normalized;
        ApplyHeaderGraphMode();
        try
        {
            await _session.SaveViewPreferencesAsync(config => config.StatsHeaderGraph = normalized);
            _settingsEditor?.RefreshConfirmedStatsHeaderGraph();
        }
        catch (Exception error)
        {
            Log.Error(error, "Could not save the header graph arrangement.");
            _config.StatsHeaderGraph = previous;
            ApplyHeaderGraphMode();
            ShowNotification(Texts.T("notification.error"), Texts.T("preferences.failed", error.Message), NotificationType.Error, operation: "preferences.appearance");
        }
    }

    /// <summary>The settings page, scrolled to the "Header graph" row of the statistics block.</summary>
    private void ShowStatisticsSettings()
    {
        if (NavigateWorkspace(WorkspacePage.Settings)) SettingsEditor.RevealStatsHeaderGraph();
    }
}

using System;
using Avalonia.Controls;

namespace oChan;

public partial class MainWindow
{
    private SettingsEditor? _settingsEditor;
    private bool _settingsPageSelected;
    internal SettingsEditor SettingsEditor => _settingsEditor
        ?? throw new InvalidOperationException("The settings page has not been initialized.");
    internal bool SettingsPageVisible => _settingsPageSelected;

    private void InitializeWorkspacePages()
    {
        _settingsEditor = new SettingsEditor(_config, ApplySettingsAsync);
        _settingsEditor.BusyChanged += OnSettingsBusyChanged;
        this.FindControl<ContentControl>("SettingsPageHost")!.Content = _settingsEditor;
    }

    private void OnSettingsBusyChanged(object? sender, EventArgs e)
    {
        if (!_isClosing) UpdateInteractionState();
    }

    private bool NavigateWorkspace(bool settings)
    {
        if (!_restoreCompleted || _isClosing || _applyingPreferences || (_settingsEditor?.IsBusy ?? false)) return false;
        _settingsPageSelected = settings;
        this.FindControl<Grid>("WorkspaceBody")!.IsVisible = !settings;
        this.FindControl<ContentControl>("SettingsPageHost")!.IsVisible = settings;
        this.FindControl<Button>("DownloadsNavigationButton")!.Classes.Set("selected", !settings);
        this.FindControl<Button>("SettingsNavigationButton")!.Classes.Set("selected", settings);
        if (settings)
        {
            _settingsEditor?.RevealCurrentError();
            _settingsEditor?.FindControl<ComboBox>("WorkspaceModeInput")?.Focus();
        }
        return true;
    }

    private void DisposeWorkspacePages()
    {
        if (_settingsEditor == null) return;
        _settingsEditor.BusyChanged -= OnSettingsBusyChanged;
        _settingsEditor.EndLifetime();
    }
}

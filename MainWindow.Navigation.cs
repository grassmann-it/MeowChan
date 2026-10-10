using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using oChan.Desktop;

namespace oChan;

public partial class MainWindow
{
    internal enum WorkspacePage { Downloads, Diagnostics, Settings }
    private SettingsEditor? _settingsEditor;
    private DiagnosticsPage? _diagnosticsPage;
    private WorkspacePage _selectedPage;
    internal SettingsEditor SettingsEditor => _settingsEditor
        ?? throw new InvalidOperationException("The settings page has not been initialized.");
    internal bool SettingsPageVisible => _selectedPage == WorkspacePage.Settings;
    internal WorkspacePage SelectedPage => _selectedPage;

    private void InitializeWorkspacePages()
    {
        _settingsEditor = new SettingsEditor(_config, ApplySettingsAsync);
        _settingsEditor.BusyChanged += OnSettingsBusyChanged;
        this.FindControl<ContentControl>("SettingsPageHost")!.Content = _settingsEditor;
        _diagnosticsPage = new DiagnosticsPage(OpenDiagnosticWatch, async text =>
        {
            if (_isClosing) return;
            if (Clipboard == null) throw new InvalidOperationException("Clipboard is unavailable.");
            await Clipboard.SetTextAsync(text);
        });
        this.FindControl<ContentControl>("DiagnosticsPageHost")!.Content = _diagnosticsPage;
    }

    private void OnSettingsBusyChanged(object? sender, EventArgs e)
    {
        if (!_isClosing) UpdateInteractionState();
    }

    private bool NavigateWorkspace(WorkspacePage page)
    {
        if ((!_restoreCompleted && !(page == WorkspacePage.Diagnostics && _session.State == DesktopSessionState.RecoveryFailed))
            || _isClosing || _applyingPreferences || (_settingsEditor?.IsBusy ?? false)) return false;
        _selectedPage = page;
        this.FindControl<Grid>("WorkspaceBody")!.IsVisible = page == WorkspacePage.Downloads;
        this.FindControl<ContentControl>("SettingsPageHost")!.IsVisible = page == WorkspacePage.Settings;
        this.FindControl<ContentControl>("DiagnosticsPageHost")!.IsVisible = page == WorkspacePage.Diagnostics;
        this.FindControl<Button>("DownloadsNavigationButton")!.Classes.Set("selected", page == WorkspacePage.Downloads);
        this.FindControl<Button>("SettingsNavigationButton")!.Classes.Set("selected", page == WorkspacePage.Settings);
        this.FindControl<Button>("DiagnosticsNavigationButton")!.Classes.Set("selected", page == WorkspacePage.Diagnostics);
        this.FindControl<Button>("ToolsNavigationButton")!.Classes.Set("selected", page == WorkspacePage.Diagnostics);
        if (page == WorkspacePage.Settings)
        {
            _settingsEditor?.RevealCurrentError();
            _settingsEditor?.FindControl<ComboBox>("WorkspaceModeInput")?.Focus();
        }
        if (page == WorkspacePage.Diagnostics)
        {
            RefreshDiagnosticPage();
            _diagnosticsPage?.FindControl<DataGrid>("DiagnosticsGrid")?.Focus();
        }
        return true;
    }

    private void RefreshDiagnosticPage(DesktopSessionView? view = null) => _diagnosticsPage?.Refresh(view ?? _session.GetView(),
        !_isClosing && !_applyingPreferences && !(_settingsEditor?.IsBusy ?? false));

    private bool OpenDiagnosticWatch(DiagnosticTarget target)
    {
        if (!DiagnosticsSnapshot.ContainsTarget(_session.GetView(), target)) return false;
        if (target.Kind == DiagnosticTargetKind.Thread)
        {
            if (!_threadRows.TryGetValue(target.WatchId, out ThreadRow? row)) return false;
            if (!NavigateWorkspace(WorkspacePage.Downloads)) return false;
            this.FindControl<TabControl>("DownloadsTabs")!.SelectedIndex = 0;
            this.FindControl<DataGrid>("ThreadsDataGrid")!.SelectedItem = row;
        }
        else
        {
            if (!_boardRows.TryGetValue(target.WatchId, out BoardRow? row)) return false;
            if (!NavigateWorkspace(WorkspacePage.Downloads)) return false;
            this.FindControl<TabControl>("DownloadsTabs")!.SelectedIndex = 1;
            this.FindControl<DataGrid>("BoardsDataGrid")!.SelectedItem = row;
        }
        OpenWorkspaceDetails();
        return true;
    }

    private void OnDiagnosticsNavigationClick(object? sender, RoutedEventArgs e)
    {
        this.FindControl<Button>("ToolsNavigationButton")!.Flyout?.Hide();
        NavigateWorkspace(WorkspacePage.Diagnostics);
    }

    private void DisposeWorkspacePages()
    {
        _diagnosticsPage?.EndLifetime();
        if (_settingsEditor == null) return;
        _settingsEditor.BusyChanged -= OnSettingsBusyChanged;
        _settingsEditor.EndLifetime();
    }
}

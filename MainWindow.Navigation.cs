using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using oChan.Desktop;

namespace oChan;

public partial class MainWindow
{
    internal enum WorkspacePage { Downloads, Archive, Rules, Diagnostics, Settings }
    private SettingsEditor? _settingsEditor;
    private DiagnosticsPage? _diagnosticsPage;
    private RulesPage? _rulesPage;
    private ArchivePage? _archivePage;
    private WorkspacePage _selectedPage;
    internal SettingsEditor SettingsEditor => _settingsEditor
        ?? throw new InvalidOperationException("The settings page has not been initialized.");
    internal bool SettingsPageVisible => _selectedPage == WorkspacePage.Settings;
    internal WorkspacePage SelectedPage => _selectedPage;
    internal DesktopSessionHost SessionHost => _session;
    internal RulesPage RulesPage => _rulesPage ?? throw new InvalidOperationException("The rules page is not initialized.");

    internal ArchivePage ArchivePage => _archivePage ?? throw new InvalidOperationException("The archive page is not initialized.");
    private bool WorkspaceCommandBusy => _isClosing || _applyingPreferences || (_settingsEditor?.IsBusy ?? false) || (_rulesPage?.IsBusy ?? false);

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
        _rulesPage = new RulesPage(() => _session.MediaRules, _session.SaveRulesAsync);
        _rulesPage.BusyChanged += OnSettingsBusyChanged;
        this.FindControl<ContentControl>("RulesPageHost")!.Content = _rulesPage;
        _archivePage = new ArchivePage(_session.OpenArchiveReaderAsync, _session.SetCaptureEnabledAsync, OpenDirectory);
        this.FindControl<ContentControl>("ArchivePageHost")!.Content = _archivePage;
    }

    private void OnSettingsBusyChanged(object? sender, EventArgs e)
    {
        if (!_isClosing) UpdateInteractionState();
    }

    private bool NavigateWorkspace(WorkspacePage page)
    {
        if ((!_restoreCompleted && !(page == WorkspacePage.Diagnostics && _session.State == DesktopSessionState.RecoveryFailed))
            || WorkspaceCommandBusy) return false;
        _selectedPage = page;
        this.FindControl<Grid>("WorkspaceBody")!.IsVisible = page == WorkspacePage.Downloads;
        this.FindControl<ContentControl>("SettingsPageHost")!.IsVisible = page == WorkspacePage.Settings;
        this.FindControl<ContentControl>("DiagnosticsPageHost")!.IsVisible = page == WorkspacePage.Diagnostics;
        this.FindControl<ContentControl>("RulesPageHost")!.IsVisible = page == WorkspacePage.Rules;
        this.FindControl<ContentControl>("ArchivePageHost")!.IsVisible = page == WorkspacePage.Archive;
        this.FindControl<Button>("DownloadsNavigationButton")!.Classes.Set("selected", page == WorkspacePage.Downloads);
        this.FindControl<Button>("SettingsNavigationButton")!.Classes.Set("selected", page == WorkspacePage.Settings);
        this.FindControl<Button>("DiagnosticsNavigationButton")!.Classes.Set("selected", page == WorkspacePage.Diagnostics);
        this.FindControl<Button>("RulesNavigationButton")!.Classes.Set("selected", page == WorkspacePage.Rules);
        this.FindControl<Button>("ArchiveNavigationButton")!.Classes.Set("selected", page == WorkspacePage.Archive);
        UpdatePageNavigation();
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
        if (page == WorkspacePage.Rules) _rulesPage?.RefreshConfirmedRules();
        if (page == WorkspacePage.Archive) _ = _archivePage?.ActivateAsync();
        return true;
    }

    private void UpdatePageNavigation()
    {
        bool full = _config.WorkspaceMode == "Advanced" && (Bounds.Width <= 0 || Bounds.Width >= 1120);
        bool archive = _config.WorkspaceMode != "Barebones";
        this.FindControl<Button>("DiagnosticsNavigationButton")!.IsVisible = full;
        this.FindControl<Button>("RulesNavigationButton")!.IsVisible = full;
        this.FindControl<Button>("ArchiveNavigationButton")!.IsVisible = archive;
        Button tools = this.FindControl<Button>("ToolsNavigationButton")!;
        tools.IsVisible = !full;
        tools.Classes.Set("selected", !full && (_selectedPage is WorkspacePage.Diagnostics or WorkspacePage.Rules
            || (!archive && _selectedPage == WorkspacePage.Archive)));
    }

    private void RefreshDiagnosticPage(DesktopSessionView? view = null) => _diagnosticsPage?.Refresh(view ?? _session.GetView(),
        !WorkspaceCommandBusy);

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

    private void OnArchiveNavigationClick(object? sender, RoutedEventArgs e)
    {
        this.FindControl<Button>("ToolsNavigationButton")!.Flyout?.Hide();
        NavigateWorkspace(WorkspacePage.Archive);
    }

    private void OnRulesNavigationClick(object? sender, RoutedEventArgs e)
    {
        this.FindControl<Button>("ToolsNavigationButton")!.Flyout?.Hide();
        NavigateWorkspace(WorkspacePage.Rules);
    }

    private void OnEditSelectionRules(object? sender, RoutedEventArgs e)
    {
        ResourceId? id = GetWorkspaceSelection() switch { ThreadRow thread => thread.Snapshot.Id, BoardRow board => board.Snapshot.Id, _ => null };
        if (id == null || !NavigateWorkspace(WorkspacePage.Rules)) return;
        _rulesPage?.EditScope(id.Thread == null ? Rules.MediaRuleScope.ForBoard(id.Source, id.Board)
            : Rules.MediaRuleScope.ForThread(id.Source, id.Board, id.Thread));
    }

    private void DisposeWorkspacePages()
    {
        _diagnosticsPage?.EndLifetime();
        _archivePage?.EndLifetime();
        if (_rulesPage != null) { _rulesPage.BusyChanged -= OnSettingsBusyChanged; _rulesPage.EndLifetime(); }
        if (_settingsEditor == null) return;
        _settingsEditor.BusyChanged -= OnSettingsBusyChanged;
        _settingsEditor.EndLifetime();
    }
}

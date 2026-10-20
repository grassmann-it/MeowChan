using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class MainWindow
{
    internal enum WorkspacePage { Downloads, Archive, Rules, Sources, Diagnostics, Settings }
    private SettingsEditor? _settingsEditor;
    private DiagnosticsPage? _diagnosticsPage;
    private RulesPage? _rulesPage;
    private ArchivePage? _archivePage;
    private SourcesPage? _sourcesPage;
    private WorkspacePage _selectedPage;
    internal SettingsEditor SettingsEditor => _settingsEditor
        ?? throw new InvalidOperationException("The settings page has not been initialized.");
    internal bool SettingsPageVisible => _selectedPage == WorkspacePage.Settings;
    internal WorkspacePage SelectedPage => _selectedPage;
    internal DesktopSessionHost SessionHost => _session;
    internal RulesPage RulesPage => _rulesPage ?? throw new InvalidOperationException("The rules page is not initialized.");

    internal ArchivePage ArchivePage => _archivePage ?? throw new InvalidOperationException("The archive page is not initialized.");
    private bool WorkspaceCommandBusy => _isClosing || _applyingPreferences || (_settingsEditor?.IsBusy ?? false) || (_rulesPage?.IsBusy ?? false) || (_sourcesPage?.IsBusy ?? false);

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
        }, _session.RequestCheckAsync, _session.CanRequestCheck);
        this.FindControl<ContentControl>("DiagnosticsPageHost")!.Content = _diagnosticsPage;
        _rulesPage = new RulesPage(() => _session.MediaRules, _session.SaveRulesAsync);
        _rulesPage.BusyChanged += OnSettingsBusyChanged;
        this.FindControl<ContentControl>("RulesPageHost")!.Content = _rulesPage;
        _archivePage = new ArchivePage(_session.OpenArchiveReaderAsync, _session.SetCaptureEnabledAsync, OpenDirectory);
        _archivePage.ExportHistory = _session.ExportHistory;
        _archivePage.OpenExportFolder = ExternalLauncher.OpenAsync;
        _archivePage.GetWorkSnapshots = _session.GetThreadWorkSnapshots;
        _archivePage.MediaTilesPreference = _config.ArchiveMediaTiles;
        _archivePage.SaveMediaTiles = async tiles =>
        {
            Config candidate = _config.Clone();
            candidate.ArchiveMediaTiles = tiles;
            await _session.ApplySettingsAsync(candidate);
        };
        _archivePage.OpenGallery = (id, key, origin) => OpenGalleryAsync(id, key, origin);
        _archivePage.OpenMediaGallery = (entries, start, origin) => OpenMediaGalleryAsync(entries, start, origin);
        _archivePage.OpenResultsGallery = (query, workFilter, origin) => OpenSearchGalleryAsync(query, workFilter, origin);
        this.FindControl<ContentControl>("ArchivePageHost")!.Content = _archivePage;
        _sourcesPage = new SourcesPage(OpenDiagnosticWatch, source =>
        {
            if (NavigateWorkspace(WorkspacePage.Rules)) _rulesPage.EditScope(Rules.MediaRuleScope.ForSource(source));
        }, () => NavigateWorkspace(WorkspacePage.Diagnostics), () => NavigateWorkspace(WorkspacePage.Downloads), SubmitInputAsync);
        _sourcesPage.BusyChanged += OnSettingsBusyChanged;
        this.FindControl<ContentControl>("SourcesPageHost")!.Content = _sourcesPage;
    }

    private void OnSettingsBusyChanged(object? sender, EventArgs e)
    {
        if (!_isClosing) UpdateInteractionState();
    }

    private bool NavigateWorkspace(WorkspacePage page, bool activateArchive = true)
    {
        if ((!_restoreCompleted && !(page == WorkspacePage.Diagnostics && _session.State == DesktopSessionState.RecoveryFailed))
            || WorkspaceCommandBusy) return false;
        if (page != WorkspacePage.Archive) _archivePage?.CancelPendingGalleryOpen();
        _selectedPage = page;
        this.FindControl<Grid>("WorkspaceBody")!.IsVisible = page == WorkspacePage.Downloads;
        this.FindControl<ContentControl>("SettingsPageHost")!.IsVisible = page == WorkspacePage.Settings;
        this.FindControl<ContentControl>("DiagnosticsPageHost")!.IsVisible = page == WorkspacePage.Diagnostics;
        this.FindControl<ContentControl>("RulesPageHost")!.IsVisible = page == WorkspacePage.Rules;
        this.FindControl<ContentControl>("ArchivePageHost")!.IsVisible = page == WorkspacePage.Archive;
        this.FindControl<ContentControl>("SourcesPageHost")!.IsVisible = page == WorkspacePage.Sources;
        this.FindControl<Button>("DownloadsNavigationButton")!.Classes.Set("selected", page == WorkspacePage.Downloads);
        this.FindControl<Button>("SettingsNavigationButton")!.Classes.Set("selected", page == WorkspacePage.Settings);
        this.FindControl<Button>("DiagnosticsNavigationButton")!.Classes.Set("selected", page == WorkspacePage.Diagnostics);
        this.FindControl<Button>("RulesNavigationButton")!.Classes.Set("selected", page == WorkspacePage.Rules);
        this.FindControl<Button>("ArchiveNavigationButton")!.Classes.Set("selected", page == WorkspacePage.Archive);
        this.FindControl<Button>("SourcesNavigationButton")!.Classes.Set("selected", page == WorkspacePage.Sources);
        UpdatePageNavigation();
        if (page == WorkspacePage.Sources) RefreshSourcesPage();
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
        if (page == WorkspacePage.Archive && activateArchive) _ = _archivePage?.ActivateAsync();
        return true;
    }

    private void UpdatePageNavigation()
    {
        bool full = _config.WorkspaceMode == "Advanced" && (Bounds.Width <= 0 || Bounds.Width >= 1120);
        bool archive = _config.WorkspaceMode != "Barebones";
        this.FindControl<Button>("DiagnosticsNavigationButton")!.IsVisible = full;
        this.FindControl<Button>("RulesNavigationButton")!.IsVisible = full;
        this.FindControl<Button>("SourcesNavigationButton")!.IsVisible = full;
        this.FindControl<Button>("ArchiveNavigationButton")!.IsVisible = archive;
        Button tools = this.FindControl<Button>("ToolsNavigationButton")!;
        tools.IsVisible = !full;
        tools.Classes.Set("selected", !full && (_selectedPage is WorkspacePage.Diagnostics or WorkspacePage.Rules or WorkspacePage.Sources
            || (!archive && _selectedPage == WorkspacePage.Archive)));
    }

    private void RefreshDiagnosticPage(DesktopSessionView? view = null) => _diagnosticsPage?.Refresh(view ?? _session.GetView(),
        !WorkspaceCommandBusy);

    private void RefreshSourcesPage(DesktopSessionView? view = null) => _sourcesPage?.Refresh(_session.GetProviderDescriptors(),
        view ?? _session.GetView(), _restoreCompleted && !WorkspaceCommandBusy && !_importing);

    private void OnSourcesNavigationClick(object? sender, RoutedEventArgs e)
    {
        this.FindControl<Button>("ToolsNavigationButton")!.Flyout?.Hide();
        NavigateWorkspace(WorkspacePage.Sources);
    }

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

    private async void OnCheckSelectionClick(object? sender, RoutedEventArgs e)
    {
        if (!_restoreCompleted || WorkspaceCommandBusy) return;
        DiagnosticTarget? target = GetWorkspaceSelection() switch
        {
            ThreadRow thread => new DiagnosticTarget(DiagnosticTargetKind.Thread, thread.WatchId),
            BoardRow board => new DiagnosticTarget(DiagnosticTargetKind.Board, board.WatchId),
            _ => null
        };
        if (target == null) return;
        try
        {
            Interfaces.CheckRequestStatus result = await _session.RequestCheckAsync(target);
            ShowNotification(Texts.T("notification.session"), CheckActionText.ForResult(result, target.Kind),
                result is Interfaces.CheckRequestStatus.Scheduled or Interfaces.CheckRequestStatus.AlreadyScheduled
                    ? Avalonia.Controls.Notifications.NotificationType.Information : Avalonia.Controls.Notifications.NotificationType.Warning);
        }
        catch (Exception exception)
        {
            ShowNotification(Texts.T("notification.error"), Texts.T("check.failed", exception.Message),
                Avalonia.Controls.Notifications.NotificationType.Error);
        }
    }

    private void DisposeWorkspacePages()
    {
        _diagnosticsPage?.EndLifetime();
        if (_sourcesPage != null) { _sourcesPage.BusyChanged -= OnSettingsBusyChanged; _sourcesPage.EndLifetime(); }
        _archivePage?.EndLifetime();
        if (_rulesPage != null) { _rulesPage.BusyChanged -= OnSettingsBusyChanged; _rulesPage.EndLifetime(); }
        if (_settingsEditor == null) return;
        _settingsEditor.BusyChanged -= OnSettingsBusyChanged;
        _settingsEditor.EndLifetime();
    }
}

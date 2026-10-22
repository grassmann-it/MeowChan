using System;
using System.Linq;
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
        _settingsEditor = new SettingsEditor(_config, ApplySettingsAsync, ForgetReadingPositionAsync);
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
        _archivePage.CollectionExportHistory = _session.CollectionExportHistory;
        _archivePage.OpenExportFolder = ExternalLauncher.OpenAsync;
        _archivePage.GetWorkSnapshots = _session.GetThreadWorkSnapshots;
        _archivePage.CanFollowLive = id => _threadRows.Values.Any(row => row.Snapshot.Id == id);
        _archivePage.ReaderRefreshAllowed = () => !_isClosing && _gallery == null && !_archiveDeletionOpen;
        _archivePage.LastExportDirectoryPreference = _config.LastExportDirectory;
        _archivePage.SaveExportDirectory = path => _session.SaveViewPreferencesAsync(current => current.LastExportDirectory = path);
        _archivePage.ArchiveColumnModePreference = _config.WorkspaceMode;
        _archivePage.ArchiveColumnWidthsPreference = _config.ColumnWidths;
        _archivePage.ArchiveColumnVisibilityPreference = _config.ColumnVisibility;
        _archivePage.ArchiveColumnOrderPreference = _config.ColumnOrder;
        _archivePage.SaveArchiveColumnWidthsAsync = widths =>
        {
            string mode = _config.WorkspaceMode;
            var frozen = new System.Collections.Generic.Dictionary<string, double>(widths);
            return _session.SaveViewPreferencesAsync(current =>
            {
                var saved = current.GetLayoutProfile(mode).ColumnWidths;
                foreach (string key in saved.Keys.Where(IsArchiveColumnKey).ToArray()) saved.Remove(key);
                foreach (var pair in frozen.Where(pair => IsArchiveColumnKey(pair.Key))) saved[pair.Key] = pair.Value;
            });
        };
        _archivePage.SaveArchiveColumnVisibilityAsync = visibility =>
        {
            string mode = _config.WorkspaceMode;
            var frozen = new System.Collections.Generic.Dictionary<string, bool>(visibility);
            return _session.SaveViewPreferencesAsync(current =>
            {
                var saved = current.GetLayoutProfile(mode).ColumnVisibility;
                foreach (string key in saved.Keys.Where(IsArchiveColumnKey).ToArray()) saved.Remove(key);
                foreach (var pair in frozen.Where(pair => IsArchiveColumnKey(pair.Key))) saved[pair.Key] = pair.Value;
            });
        };
        _archivePage.SaveArchiveColumnOrderAsync = order =>
        {
            string mode = _config.WorkspaceMode;
            var frozen = new System.Collections.Generic.Dictionary<string, int>(order);
            return _session.SaveViewPreferencesAsync(current =>
            {
                var saved = current.GetLayoutProfile(mode).ColumnOrder;
                foreach (string key in saved.Keys.Where(IsArchiveColumnKey).ToArray()) saved.Remove(key);
                foreach (var pair in frozen.Where(pair => IsArchiveColumnKey(pair.Key))) saved[pair.Key] = pair.Value;
            });
        };
        _archivePage.MediaTilesPreference = _config.ArchiveMediaTiles;
        _archivePage.SaveMediaTiles = tiles =>
        {
            string mode = _config.WorkspaceMode;
            return _session.SaveViewPreferencesAsync(current => current.GetLayoutProfile(mode).ArchiveMediaTiles = tiles);
        };
        _archivePage.FullscreenRequested = ToggleFullscreen;
        _archivePage.ReadingFontSizePreference = ReadingFontSize();
        _archivePage.GetFavorites = _session.GetFavorites;
        _archivePage.IsArchiveFavorite = _session.IsFavorite;
        _archivePage.ToggleArchiveFavoriteAsync = async entry =>
        {
            _galleryFavoriteErrors.Remove(Favorites.FavoriteAssetKey.From(entry.Key.Thread, entry.Asset));
            await _session.SetFavoriteAsync(entry, !_session.IsFavorite(entry));
            RefreshGalleryFavorite();
        };
        _archivePage.ResolveFavoriteAssets = _session.ResolveFavoriteAssetsAsync;
        _archivePage.RemoveFavoriteAsync = async entry =>
        {
            _galleryFavoriteErrors.Remove(entry.Key);
            _archivePage.ClearFavoriteError(entry.Key);
            await _session.SetFavoriteAsync(entry, false);
            RefreshGalleryFavorite();
        };
        _archivePage.OpenFavoritesGallery = OpenFavoritesGalleryAsync;
        _archivePage.ArchiveLayoutPreference = _config.ArchiveThreadLayout;
        _archivePage.ArchiveSplitPreference = _config.ArchiveSplitWidth;
        _archivePage.SaveViewPreferencesAsync = (layout, split) =>
        {
            string mode = _config.WorkspaceMode;
            return _session.SaveViewPreferencesAsync(current =>
            {
                PresentationProfile profile = current.GetLayoutProfile(mode);
                profile.ArchiveThreadLayout = layout;
                profile.ArchiveSplitWidth = split;
            });
        };
        _archivePage.OpenReaderRequested = OpenLargeReader;
        _archivePage.CloseReaderRequested = () => CloseLargeReader();
        _archivePage.OpenGallery = (id, key, origin) => OpenGalleryAsync(id, key, origin);
        _archivePage.OpenMediaGallery = (entries, start, origin) => OpenMediaGalleryAsync(entries, start, origin);
        _archivePage.OpenResultsGallery = (query, workFilter, origin) => OpenSearchGalleryAsync(query, workFilter, origin);
        this.FindControl<ContentControl>("ArchivePageHost")!.Content = _archivePage;
        InitializeReadingResume();
        _sourcesPage = new SourcesPage(OpenDiagnosticWatch, source =>
        {
            if (NavigateWorkspace(WorkspacePage.Rules)) _rulesPage.EditScope(Rules.MediaRuleScope.ForSource(source));
        }, () => NavigateWorkspace(WorkspacePage.Diagnostics), () => NavigateWorkspace(WorkspacePage.Downloads), SubmitInputAsync);
        _sourcesPage.BusyChanged += OnSettingsBusyChanged;
        this.FindControl<ContentControl>("SourcesPageHost")!.Content = _sourcesPage;
    }

    private void ApplyArchiveModePreferences()
    {
        if (_archivePage == null) return;
        _archivePage.ArchiveColumnModePreference = _config.WorkspaceMode;
        _archivePage.ArchiveColumnWidthsPreference = _config.ColumnWidths;
        _archivePage.ArchiveColumnVisibilityPreference = _config.ColumnVisibility;
        _archivePage.ArchiveColumnOrderPreference = _config.ColumnOrder;
        _archivePage.MediaTilesPreference = _config.ArchiveMediaTiles;
        _archivePage.ArchiveLayoutPreference = _config.ArchiveThreadLayout;
        _archivePage.ArchiveSplitPreference = _config.ArchiveSplitWidth;
        _archivePage.ReadingFontSizePreference = ReadingFontSize();
    }

    private static bool IsArchiveColumnKey(string key) =>
        key.StartsWith("ArchiveThreadTable.", StringComparison.Ordinal) ||
        key.StartsWith("ArchiveMediaGrid.", StringComparison.Ordinal);

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

    private async void OnCheckSelectionClick(object? sender, RoutedEventArgs e) =>
        await CheckDownloadSelectionAsync(this.FindControl<TabControl>("DownloadsTabs")!.SelectedIndex == 1, false);

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

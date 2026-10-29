using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Input;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class MainWindow
{
    internal enum WorkspacePage { Downloads, Archive, Rules, Sources, Diagnostics, Settings, Plugins }
    private SettingsEditor? _settingsEditor;
    private DiagnosticsPage? _diagnosticsPage;
    private RulesPage? _rulesPage;
    private ArchivePage? _archivePage;
    private SourcesPage? _sourcesPage;
    private PluginsPage? _pluginsPage;
    private WorkspacePage _selectedPage;
    internal SettingsEditor SettingsEditor => _settingsEditor
        ?? throw new InvalidOperationException("The settings page has not been initialized.");
    internal bool SettingsPageVisible => _selectedPage == WorkspacePage.Settings;
    internal WorkspacePage SelectedPage => _selectedPage;
    internal DesktopSessionHost SessionHost => _session;
    internal RulesPage RulesPage => _rulesPage ?? throw new InvalidOperationException("The rules page is not initialized.");

    internal ArchivePage ArchivePage => _archivePage ?? throw new InvalidOperationException("The archive page is not initialized.");
    private bool WorkspaceEditorBusy => _isClosing || _applyingPreferences || (_settingsEditor?.IsBusy ?? false) || (_rulesPage?.IsBusy ?? false) || (_sourcesPage?.IsBusy ?? false);

    private bool WorkspaceCommandBusy => WorkspaceEditorBusy;
    private bool RowCommandBusy => WorkspaceEditorBusy || _sessionRowsApplying;

    private void InitializeWorkspacePages()
    {
        InitializeUpdates();
        _settingsEditor = new SettingsEditor(_config, ApplySettingsAsync, ForgetReadingPositionAsync);
        _settingsEditor.Updates = _session.Updates;
        _settingsEditor.BusyChanged += OnSettingsBusyChanged;
        this.FindControl<ContentControl>("SettingsPageHost")!.Content = _settingsEditor;
        _diagnosticsPage = new DiagnosticsPage(OpenDiagnosticWatch, async text =>
        {
            if (_isClosing) return;
            if (Clipboard == null) throw new InvalidOperationException("Clipboard is unavailable.");
            await Clipboard.SetTextAsync(text);
        }, _session.RequestCheckAsync, _session.CanRequestCheck);
        _diagnosticsPage.RetryMediaAsync = (target) => _session.RetryMissingMediaAsync(target.WatchId);
        _diagnosticsPage.CanRetryMedia = target => target.Kind == DiagnosticTargetKind.Thread
            && _session.CanRetryMissingMedia(target.WatchId);
        _diagnosticsPage.AttachFeedback(_feedback);
        _diagnosticsPage.ManageMediaRatingsAsync = ManageMediaRatingsAsync;
        this.FindControl<ContentControl>("DiagnosticsPageHost")!.Content = _diagnosticsPage;
        _rulesPage = new RulesPage(() => _session.MediaRules, _session.SaveRulesAsync);
        _rulesPage.GetSuggestionContexts = GetRuleSuggestionContexts;
        _rulesPage.AttachTemplateServices(_session.LoadRuleTemplatesAsync, _session.SaveRuleTemplatesAsync);
        _rulesPage.BusyChanged += OnSettingsBusyChanged;
        this.FindControl<ContentControl>("RulesPageHost")!.Content = _rulesPage;
        _archivePage = new ArchivePage(_session.OpenArchiveReaderAsync, _session.SetCaptureEnabledAsync, directory => OpenDirectory(directory, _archivePage?.CaptureActionErrorReporter()));
        _archivePage.AttachSavedSearches(_session);
        _archivePage.AttachThreadFavorites(_session);
        _archivePage.AttachArchiveCleanup(_session, () => _gallery == null && _galleryCleanupTask.IsCompletedSuccessfully
            ? Array.Empty<ResourceId>() : _galleryThreads.Concat(_galleryEntries.Select(entry => entry.Key.Thread)).Distinct().ToArray(),
            () => _gallery == null && _galleryCleanupTask.IsCompletedSuccessfully && !_largeReaderOpen && !_archiveDeletionOpen);
        _archivePage.ExportHistory = _session.ExportHistory;
        _archivePage.CollectionExportHistory = _session.CollectionExportHistory;
        _archivePage.OpenExportFolder = ExternalLauncher.OpenAsync;
        _archivePage.GetWorkSnapshots = _session.GetThreadWorkSnapshots;
        _archivePage.CanFollowLive = id => !_sessionRowsApplying && _threadRows.Values.Any(row => row.Snapshot.Id == id);
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
        _archivePage.GetMediaRating = _session.GetMediaRating;
        _archivePage.SetMediaRatingAsync = SetMediaRatingAsync;
        _archivePage.CanChangeMediaRating = key => !_isClosing && !_ratingChanges.Contains(key);
        _archivePage.EditMediaRatingAsync = EditMediaRatingAsync;
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
        InitializePostBookmarks();
        _sourcesPage = new SourcesPage(OpenDiagnosticWatch, source =>
        {
            if (NavigateWorkspace(WorkspacePage.Rules)) _rulesPage.EditScope(Rules.MediaRuleScope.ForSource(source));
        }, () => NavigateWorkspace(WorkspacePage.Diagnostics), () => NavigateWorkspace(WorkspacePage.Downloads),
            (input, observeModal) => SubmitInputAsync(input, observeModal));
        _sourcesPage.BusyChanged += OnSettingsBusyChanged;
        this.FindControl<ContentControl>("SourcesPageHost")!.Content = _sourcesPage;
        InitializePluginPage();
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
        if ((!_restoreCompleted && !(page == WorkspacePage.Diagnostics && _session.Projection.View.State == DesktopSessionState.RecoveryFailed))
            || WorkspaceCommandBusy) return false;
        if (page != WorkspacePage.Archive) _archivePage?.CancelPendingGalleryOpen();
        ++_readingResumeNavigationRevision;
        if (page != WorkspacePage.Plugins) _pluginsPage?.Deactivate();
        if (_selectedPage == WorkspacePage.Rules && page != WorkspacePage.Rules) _rulesPage?.DeactivateRuleDecisions();
        _selectedPage = page;
        this.FindControl<Grid>("WorkspaceBody")!.IsVisible = page == WorkspacePage.Downloads;
        this.FindControl<ContentControl>("SettingsPageHost")!.IsVisible = page == WorkspacePage.Settings;
        this.FindControl<ContentControl>("DiagnosticsPageHost")!.IsVisible = page == WorkspacePage.Diagnostics;
        this.FindControl<ContentControl>("RulesPageHost")!.IsVisible = page == WorkspacePage.Rules;
        this.FindControl<ContentControl>("ArchivePageHost")!.IsVisible = page == WorkspacePage.Archive;
        this.FindControl<ContentControl>("SourcesPageHost")!.IsVisible = page == WorkspacePage.Sources;
        this.FindControl<ContentControl>("PluginsPageHost")!.IsVisible = page == WorkspacePage.Plugins;
        this.FindControl<Button>("DownloadsNavigationButton")!.Classes.Set("selected", page == WorkspacePage.Downloads);
        this.FindControl<Button>("SettingsNavigationButton")!.Classes.Set("selected", page == WorkspacePage.Settings);
        this.FindControl<Button>("DiagnosticsNavigationButton")!.Classes.Set("selected", page == WorkspacePage.Diagnostics);
        this.FindControl<Button>("RulesNavigationButton")!.Classes.Set("selected", page == WorkspacePage.Rules);
        this.FindControl<Button>("ArchiveNavigationButton")!.Classes.Set("selected", page == WorkspacePage.Archive);
        this.FindControl<Button>("SourcesNavigationButton")!.Classes.Set("selected", page == WorkspacePage.Sources);
        this.FindControl<Button>("PluginsNavigationButton")!.Classes.Set("selected", page == WorkspacePage.Plugins);
        UpdatePageNavigation();
        RefreshReadingResumePresentation();
        if (page == WorkspacePage.Sources) RefreshSourcesPage();
        if (page == WorkspacePage.Plugins) _ = _pluginsPage?.ActivateAsync();
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
        if (page == WorkspacePage.Rules) { _rulesPage?.RefreshConfirmedRules(); _ = _rulesPage?.RefreshPluginFiltersAsync(); }
        if (page == WorkspacePage.Archive && activateArchive) _ = _archivePage?.ActivateAsync();
        return true;
    }

    private bool _measuringPageNavigation;

    private void UpdatePageNavigation()
    {
        if (_measuringPageNavigation) return;
        _measuringPageNavigation = true;
        try
        {
            bool advanced = _config.WorkspaceMode == "Advanced";
            Button downloads = this.FindControl<Button>("DownloadsNavigationButton")!;
            Button settings = this.FindControl<Button>("SettingsNavigationButton")!;
            Button tools = this.FindControl<Button>("ToolsNavigationButton")!;
            StackPanel panel = this.FindControl<StackPanel>("WorkspaceNavigationButtons")!;
            Border footer = this.FindControl<Border>("WorkspaceNavigation")!;
            ComboBox modes = this.FindControl<ComboBox>("WorkspaceModeComboBox")!;
            var destinations = new[]
            {
                (Page: WorkspacePage.Archive, Button: this.FindControl<Button>("ArchiveNavigationButton")!, Tag: "nav.archive"),
                (Page: WorkspacePage.Rules, Button: this.FindControl<Button>("RulesNavigationButton")!, Tag: "nav.rules"),
                (Page: WorkspacePage.Plugins, Button: this.FindControl<Button>("PluginsNavigationButton")!, Tag: "nav.plugins"),
                (Page: WorkspacePage.Sources, Button: this.FindControl<Button>("SourcesNavigationButton")!, Tag: "nav.sources"),
                (Page: WorkspacePage.Diagnostics, Button: this.FindControl<Button>("DiagnosticsNavigationButton")!, Tag: "nav.diagnostics")
            };
            // Capture the actual focused menu destination before changing visibility:
            // hiding its presenter can clear FocusManager synchronously.
            Control? focusedBefore = FocusManager?.GetFocusedElement() as Control;
            Button? returnDestination = tools.Flyout is MenuFlyout openMenu && openMenu.IsOpen
                ? destinations.Where(destination => openMenu.Items.OfType<MenuItem>().Any(item =>
                    item.Tag as string == destination.Tag && (ReferenceEquals(item, focusedBefore)
                        || item.IsKeyboardFocusWithin))).Select(destination => destination.Button).FirstOrDefault()
                : null;
            bool[] direct = destinations.Select(destination => destination.Page == WorkspacePage.Archive
                ? _config.WorkspaceMode != "Barebones" : advanced).ToArray();

            // Measure the localized labels instead of guessing a breakpoint. Hidden
            // destinations move into Tools exactly once, including in compact Advanced.
            CommandBindings.Apply(downloads, "navigation.downloads", iconWithLabel: true);
            CommandBindings.Apply(settings, "navigation.settings", iconWithLabel: true);
            CommandBindings.Apply(tools, "navigation.tools-menu", iconWithLabel: true);
            Size unconstrained = new(double.PositiveInfinity, double.PositiveInfinity);
            double Measure(Button button)
            {
                // ContentPresenter may still hold the previous command presentation
                // until layout runs. Measure the new content itself so compacting one
                // label immediately releases space for the remaining labeled actions.
                Control content = button.Content as Control ?? new TextBlock { Text = button.Content?.ToString() };
                TextElement.SetFontFamily(content, button.FontFamily);
                TextElement.SetFontSize(content, button.FontSize);
                TextElement.SetFontStyle(content, button.FontStyle);
                TextElement.SetFontWeight(content, button.FontWeight);
                TextElement.SetFontStretch(content, button.FontStretch);
                // A reused root can retain its cached size while a localized child
                // is already invalid. Visit the current children before deciding fit.
                content.InvalidateMeasure();
                content.Measure(unconstrained);
                return Math.Max(button.MinWidth, content.DesiredSize.Width
                    + button.Padding.Left + button.Padding.Right
                    + button.BorderThickness.Left + button.BorderThickness.Right);
            }
            double downloadsWidth = Measure(downloads);
            double settingsWidth = Measure(settings);
            double toolsWidth = Measure(tools);
            double[] destinationWidths = destinations.Select(destination => Measure(destination.Button)).ToArray();
            double width = ClientSize.Width > 0 ? ClientSize.Width : Width;
            Grid layout = (Grid)panel.Parent!;
            double available = width - footer.Padding.Left - footer.Padding.Right - modes.Width - layout.ColumnSpacing;
            double RequiredWidth()
            {
                bool overflow = direct.Any(visible => !visible);
                int count = 2 + direct.Count(visible => visible) + (overflow ? 1 : 0);
                return downloadsWidth + settingsWidth
                    + destinationWidths.Where((_, index) => direct[index]).Sum()
                    + (overflow ? toolsWidth : 0) + Math.Max(0, count - 1) * panel.Spacing;
            }
            // Preserve Archive longest; remove the rarer destinations first.
            foreach (int index in new[] { 4, 3, 2, 1, 0 })
            {
                if (RequiredWidth() <= available) break;
                direct[index] = false;
            }
            // A small screen can shrink even below the usual workspace minimum.
            // Compact familiar icons only when the actual localized destinations do not fit.
            if (RequiredWidth() > available)
            {
                CommandBindings.Apply(settings, "navigation.settings", iconOnly: true);
                settingsWidth = Measure(settings);
            }
            if (RequiredWidth() > available)
            {
                CommandBindings.Apply(downloads, "navigation.downloads", iconOnly: true);
                downloadsWidth = Measure(downloads);
            }
            if (RequiredWidth() > available)
                CommandBindings.Apply(tools, "navigation.tools-menu", iconOnly: true);

            bool hasOverflow = direct.Any(visible => !visible);
            tools.IsVisible = hasOverflow;
            tools.Classes.Set("selected", destinations.Where((_, index) => !direct[index])
                .Any(destination => destination.Page == _selectedPage));
            for (int index = 0; index < destinations.Length; index++)
            {
                var destination = destinations[index];
                destination.Button.IsVisible = direct[index];
                destination.Button.Classes.Set("selected", destination.Page == _selectedPage);
                if (tools.Flyout is MenuFlyout menu)
                    foreach (MenuItem item in menu.Items.OfType<MenuItem>())
                        if (item.Tag as string == destination.Tag) item.IsVisible = !direct[index];
            }
            if (!hasOverflow) tools.Flyout?.Hide();
            if (returnDestination is { IsVisible: true } target)
                Dispatcher.UIThread.Post(() =>
                {
                    object? current = FocusManager?.GetFocusedElement();
                    if (current != null && !ReferenceEquals(current, focusedBefore) && !ReferenceEquals(current, tools)) return;
                    if (target.IsEffectivelyVisible && target.IsEffectivelyEnabled
                        && target.Bounds.Width > 0 && target.Bounds.Height > 0
                        && ReferenceEquals(TopLevel.GetTopLevel(target), this))
                        target.Focus(NavigationMethod.Tab);
                }, DispatcherPriority.Loaded);
        }
        finally { _measuringPageNavigation = false; }
    }

    private void RefreshDiagnosticPage(DesktopSessionView? view = null)
    {
        // Navigation refreshes the page after selecting it, so hidden pages need
        // neither a snapshot nor a rebuilt presentation for background progress.
        if (_selectedPage != WorkspacePage.Diagnostics) return;
        if (_galleryCoversWorkspace || _sessionRowsApplying) { _sessionDirtyWhileCovered = true; return; }
        _diagnosticsPage?.Refresh(view ?? _session.Projection.View, !WorkspaceCommandBusy);
    }

    private System.Collections.Generic.IEnumerable<MeowChan.Archive.ArchiveBoardIdentity> GetRuleSuggestionContexts()
    {
        // Enumerate passive session snapshots without keeping another watch-object cache.
        var view = _session.Projection.View;
        foreach (var item in view.Threads) yield return new(item.Snapshot.Id.Source, item.Snapshot.Id.Board);
        foreach (var item in view.Boards) yield return new(item.Snapshot.Id.Source, item.Snapshot.Id.Board);
        foreach (var item in _session.GetProviderDescriptors()) yield return new(item.SourceId, "");
    }

    private void RefreshSourcesPage(DesktopSessionView? view = null)
    {
        if (_selectedPage != WorkspacePage.Sources) return;
        if (_galleryCoversWorkspace || _sessionRowsApplying) { _sessionDirtyWhileCovered = true; return; }
        _sourcesPage?.Refresh(_session.GetProviderDescriptors(), view ?? _session.Projection.View,
            _restoreCompleted && !WorkspaceCommandBusy && !_importing);
    }

    private void OnSourcesNavigationClick(object? sender, RoutedEventArgs e)
    {
        this.FindControl<Button>("ToolsNavigationButton")!.Flyout?.Hide();
        NavigateWorkspace(WorkspacePage.Sources);
    }

    private bool OpenDiagnosticWatch(DiagnosticTarget target)
    {
        if (_sessionRowsApplying || !DiagnosticsSnapshot.ContainsTarget(_session.Projection.View, target)) return false;
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
        DisposeUpdates();
        _pluginsPage?.EndLifetime();
        _diagnosticsPage?.EndLifetime();
        if (_sourcesPage != null) { _sourcesPage.BusyChanged -= OnSettingsBusyChanged; _sourcesPage.EndLifetime(); }
        _archivePage?.EndLifetime();
        if (_rulesPage != null) { _rulesPage.BusyChanged -= OnSettingsBusyChanged; _rulesPage.EndLifetime(); }
        if (_settingsEditor == null) return;
        _settingsEditor.BusyChanged -= OnSettingsBusyChanged;
        _settingsEditor.EndLifetime();
    }
}

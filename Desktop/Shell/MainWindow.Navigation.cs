using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Notifications;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Input;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Stats;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.WorkViews;
using Serilog;

namespace MeowChan;

public partial class MainWindow
{
    /// <summary>The workspace pages. Status holds the Statistics and Diagnostics segments (<see cref="StatusSegment"/>).</summary>
    internal enum WorkspacePage { Downloads, Archive, Media, Tagging, Booru, Rules, Sources, Status, Settings }
    /// <summary>The workspace page behind a main-page command of <see cref="MainPageOrder"/>.</summary>
    internal static WorkspacePage PageFor(string commandId) => commandId switch
    {
        "navigation.downloads" => WorkspacePage.Downloads, "navigation.archive" => WorkspacePage.Archive,
        "navigation.media" => WorkspacePage.Media, "navigation.tagging" => WorkspacePage.Tagging,
        "navigation.booru" => WorkspacePage.Booru,
        "navigation.rules" => WorkspacePage.Rules,
        "navigation.sources" => WorkspacePage.Sources, "navigation.status" => WorkspacePage.Status,
        "navigation.plugins" => WorkspacePage.Sources,
        _ => throw new ArgumentOutOfRangeException(nameof(commandId), commandId, "Not a main page.")
    };
    private SettingsEditor? _settingsEditor;
    private DiagnosticsPage? _diagnosticsPage;
    private RulesPage? _rulesPage;
    private ArchivePage? _archivePage;
    private SourcesPage? _sourcesPage;
    private PluginsPage? _pluginsPage;
    private StatisticsPage? _statisticsPage;
    private WorkspacePage _selectedPage;
    private int _mediaPageNavigationRevision;
    // Built after the first frame; asking for the page is reason enough to build it now.
    internal SettingsEditor SettingsEditor
    {
        get
        {
            EnsureDeferredWorkspacePages();
            return _settingsEditor ?? throw new InvalidOperationException("The settings page has not been initialized.");
        }
    }
    internal bool SettingsPageVisible => _selectedPage == WorkspacePage.Settings;
    internal WorkspacePage SelectedPage => _selectedPage;
    internal DesktopSessionHost SessionHost => _session;
    // Built after the first frame; asking for the page is reason enough to build it now.
    internal RulesPage RulesPage
    {
        get
        {
            EnsureDeferredWorkspacePages();
            return _rulesPage ?? throw new InvalidOperationException("The rules page is not initialized.");
        }
    }

    internal ArchivePage ArchivePage => _archivePage ?? throw new InvalidOperationException("The archive page is not initialized.");
    private bool WorkspaceEditorBusy => _isClosing || _applyingPreferences || (_settingsEditor?.IsBusy ?? false) || (_rulesPage?.IsBusy ?? false) || (_sourcesPage?.IsBusy ?? false);

    private bool WorkspaceCommandBusy => WorkspaceEditorBusy;
    private bool RowCommandBusy => WorkspaceEditorBusy || _sessionRowsApplying;

    private void InitializeWorkspacePages()
    {
        MeowChan.Diagnostics.PerformanceDiagnostics.Record("startup.pages", "begin");
        InitializeUpdates();
        InitializeMediaFocusChrome();
        _archivePage = new ArchivePage(_session.OpenArchiveReaderAsync, _session.SetCaptureEnabledAsync, directory => OpenDirectory(directory, _archivePage?.CaptureActionErrorReporter()));
        _archivePage.PlaybackGate = _playbackGate;
        _archivePage.AttachTransferImport(_session);
        _archivePage.AttachTransferExport(_session);
        _archivePage.AttachSavedSearches(_session);
        _archivePage.AttachThreadFavorites(_session);
        _archivePage.AttachPrivateTags(_session);
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
        _archivePage.ExportFilenameTemplate = () => _config.ExportFilenameTemplate;
        _archivePage.ReaderNavigationIdleDelayMs = _config.ControlsIdleHideMs;
        _archivePage.ReaderNavigationMode = _config.ReaderNavigationMode;
        _archivePage.ReaderNavigationShowPosition = _config.ReaderNavigationShowPosition;
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
        // The tile size of the saved-media tiles is a view preference of the profile (not of the work view): one size for every mode.
        _archivePage.MediaTileSizePreference = MediaTileSizes.Parse(_config.ArchiveMediaTileSize);
        _archivePage.SaveMediaTileSize = size => _session.SaveViewPreferencesAsync(current => current.ArchiveMediaTileSize = size.ToString());
        // The reader's quick media switches (View menu) write only the switch that was flipped; Settings shows the confirmed value again.
        _archivePage.SaveReaderMediaSwitches = async change =>
        {
            await _session.SaveViewPreferencesAsync(change);
            _settingsEditor?.RefreshConfirmedReaderMedia();
        };
        _archivePage.FullscreenRequested = ToggleFullscreen;
        _archivePage.ReadingFontSizePreference = ReadingFontSize();
        _archivePage.ReaderPostWidthPreference = _config.ReaderPostWidth;
        ReaderSettingsPreferences.Apply(_archivePage, _config);
        _archivePage.QuickSettingsHost = CreateQuickSettingsHost();
        _archivePage.GetFavorites = _session.GetFavorites;
        _archivePage.GetMediaRating = _session.GetMediaRating;
        _archivePage.SetMediaRatingAsync = SetMediaRatingAsync;
        _archivePage.CanChangeMediaRating = key => !_isClosing && !_ratingChanges.Contains(key);
        _archivePage.EditArchiveMediaTagsAsync = entry => EditGalleryMediaTagsAsync(entry);
        _archivePage.TagPopoverAsync = OpenArchiveMediaTagPopoverAsync;
        _archivePage.TagPopoverOptions = TagPopoverDefaults;
        _archivePage.DecisionFor = DecisionFor;
        _archivePage.CopyFilePathAsync = CopyFilePathAsync;
        _archivePage.ManageMediaRatingsAsync = ManageMediaRatingsAsync;
        _archivePage.IsArchiveFavorite = _session.IsFavorite;
        _archivePage.ArchiveFavoritesUnreadable = () => _session.FavoritesUnreadable;
        _archivePage.EffectiveArchiveFavorite = EffectiveArchiveFavorite;
        // The toggle is relative to the effective value (a pending write's target), the favourite keys write an explicit target.
        _archivePage.ToggleArchiveFavoriteAsync = entry => SetArchiveFavoriteAsync(entry, !EffectiveArchiveFavorite(entry));
        _archivePage.SetArchiveFavoriteAsync = SetArchiveFavoriteAsync;
        _archivePage.ResolveFavoriteAssets = _session.ResolveFavoriteAssetsAsync;
        _archivePage.RemoveFavoriteAsync = async entry =>
        {
            _archivePage.ClearFavoriteError(entry.Key);
            await _session.SetFavoriteAsync(entry, false);
            RefreshGalleryFavorite();
        };
        _archivePage.OpenFavoritesGallery = OpenFavoritesGalleryAsync;
        _archivePage.ArchiveLayoutPreference = _config.ArchiveThreadLayout;
        _archivePage.ArchiveSplitPreference = _config.ArchiveSplitWidth;
        _archivePage.ArchiveThreadListHiddenPreference = _config.ArchiveThreadListHidden;
        _archivePage.SaveThreadListHiddenAsync = hidden => _session.SaveViewPreferencesAsync(current => current.ArchiveThreadListHidden = hidden);
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
        _archivePage.GoToDownloadsRequested = GoToDownloads;
        // AP2 search carry-over: Archive (or the reader) -> Media file name search.
        _archivePage.SearchInMediaRequested = query =>
        {
            if (NavigateWorkspace(WorkspacePage.Media)) _mediaPage?.ReceiveCarriedSearch(query, Texts.T("nav.archive"));
        };
        _archivePage.OpenGallery = (id, key, origin) => OpenGalleryAsync(id, key, origin);
        _archivePage.OpenMediaGallery = (entries, start, origin) => OpenMediaGalleryAsync(entries, start, origin,
            threadMedia: _archivePage.ThreadMediaForGallery(entries));
        _archivePage.OpenResultsGallery = (query, workFilter, origin) => OpenSearchGalleryAsync(query, workFilter, origin);
        // The shell hides the host, not the page, when another page is shown (as for the media page host): inline and sidebar reader videos pause then.
        ArchivePageHost.PropertyChanged += (_, e) => { if (e.Property == Avalonia.Visual.IsVisibleProperty && e.NewValue is false) _ = _archivePage?.PauseInlineVideos(); };
        ArchivePageHost.Content = _archivePage;
        MeowChan.Diagnostics.PerformanceDiagnostics.Record("startup.pages", "archive-done");
        InitializeMediaPage();
        MeowChan.Diagnostics.PerformanceDiagnostics.Record("startup.pages", "media-done");
        InitializeReadingResume();
        InitializePostBookmarks();
        InitializeTagQueue();
        InitializeTaggingPage();
        InitializePostFilters();
        InitializeGalleryFilmstripFlags();
        InitializeFolderSources();
        InitializeBooru();
        MeowChan.Diagnostics.PerformanceDiagnostics.Record("startup.pages", "resume-bookmarks-booru-done");
    }

    private int _deferredPageStep;
    private bool _deferredWorkspacePages;

    /// <summary>
    /// Pages whose control trees are built after the shell's first frame. No gate offers any of them
    /// before the shell is interactive: Status, Rules, Plugins stay behind
    /// <c>navigationReady</c>/<c>diagnosticsReady</c>, and the early Settings choice reaches its page only
    /// through <see cref="NavigateWorkspace"/>, which builds it first. Keeping them out of the constructor
    /// takes their construction off the path to the first frame.
    /// </summary>
    private bool BuildNextDeferredWorkspacePage()
    {
        switch (_deferredPageStep++)
        {
            case 0:
                _settingsEditor = new SettingsEditor(_config, ApplySettingsAsync, ForgetReadingPositionAsync);
                _settingsEditor.HeaderMonitorToggled += show =>
                    ChooseHeaderGraph(show ? IoStatsDisplay.HeaderGraphShownAgain(_lastShownHeaderGraph) : IoStatsDisplay.HeaderGraphOff);
                _settingsEditor.Updates = _session.Updates;
                _settingsEditor.FolderSources = CreateFolderSourceServices();
                _settingsEditor.BusyChanged += OnSettingsBusyChanged;
                // The page is built after the first frame: it starts in the window's view and follows every later change (WV8).
                _settingsEditor.ApplyWorkView(_workViews.Current, viewChanged: true);
                _workViews.Changed += (_, current) => _settingsEditor?.ApplyWorkView(current, viewChanged: true);
                SettingsPageHost.Content = _settingsEditor;
                return true;
            case 1:
                _diagnosticsPage = new DiagnosticsPage(OpenDiagnosticWatch, async text =>
                {
                    if (_isClosing) return;
                    if (Clipboard == null) throw new InvalidOperationException("Clipboard is unavailable.");
                    await Clipboard.SetTextAsync(text);
                }, _session.RequestCheckAsync, _session.CanRequestCheck);
                _diagnosticsPage.RetryMediaAsync = target => !_isClosing && target.Kind == DiagnosticTargetKind.Thread
                    ? _session.RetryMissingMediaAsync(target.WatchId) : System.Threading.Tasks.Task.FromResult(0);
                _diagnosticsPage.CanRetryMedia = target => !_isClosing && target.Kind == DiagnosticTargetKind.Thread
                    && _session.CanRetryMissingMedia(target.WatchId);
                _diagnosticsPage.AttachFeedback(_feedback);
                InitializeStatusPage();
                _statusPage!.AttachDiagnostics(_diagnosticsPage);
                return true;
            case 2:
                _rulesPage = new RulesPage(() => _session.MediaRules, _session.SaveRulesAsync);
                _rulesPage.GetSuggestionContexts = GetRuleSuggestionContexts;
                _rulesPage.AttachTemplateServices(_session.LoadRuleTemplatesAsync, _session.SaveRuleTemplatesAsync);
                _rulesPage.BusyChanged += OnSettingsBusyChanged;
                _rulesPage.ApplyWorkView(ConfiguredWorkView, viewChanged: true);
                RulesPageHost.Content = _rulesPage;
                return true;
            case 3:
                _sourcesPage = new SourcesPage(OpenDiagnosticWatch, source =>
                {
                    if (NavigateWorkspace(WorkspacePage.Rules)) _rulesPage!.EditScope(Rules.MediaRuleScope.ForSource(source));
                }, sourceId => ShowStatus(StatusSegment.Diagnostics, sourceId), GoToDownloads, () => NavigateWorkspace(WorkspacePage.Downloads))
                {
                    GetWatchPreviewRows = () => _session.Config.SourcesWatchPreviewRows,
                    // The media folders live in Settings › Storage and limits since 14.11.2026; a hint under the source list leads there.
                    OpenMediaFolders = () => ShowSettingsAt(SettingsFieldId.MediaFolders)
                };
                _sourcesPage.BusyChanged += OnSettingsBusyChanged;
                _sourcesPage.ApplyWorkView(ConfiguredWorkView, viewChanged: true);
                SourcesPageHost.Content = _sourcesPage;
                return true;
            case 4:
                InitializePluginPage();
                InitializeStatisticsPage();
                // The registrations the constructor used to install, now that their owners exist.
                RegisterSectionCommands();
                RegisterRuleTemplateCommands();
                _deferredWorkspacePages = true;
                return false;
            default:
                _deferredWorkspacePages = true;
                return false;
        }
    }

    /// <summary>Builds every page that is still missing, right now. Idempotent.</summary>
    private void EnsureDeferredWorkspacePages()
    {
        if (_deferredWorkspacePages || _isClosing) return;
        using (MeowChan.Diagnostics.PerformanceDiagnostics.Measure("startup.pages.deferred"))
            while (BuildNextDeferredWorkspacePage()) { }
        if (!_isClosing) UpdateInteractionState();
    }

    /// <summary>
    /// Builds one page per background dispatcher pass, so the shell that has just shown its first frame
    /// stays responsive instead of blocking in one long construction. Any earlier demand (navigation, the
    /// page accessors, the end of restoration) completes the rest synchronously through
    /// <see cref="EnsureDeferredWorkspacePages"/>.
    /// </summary>
    private void PumpDeferredWorkspacePages()
    {
        if (_deferredWorkspacePages || _isClosing) return;
        bool more;
        using (MeowChan.Diagnostics.PerformanceDiagnostics.Measure("startup.pages.deferred.slice"))
            more = BuildNextDeferredWorkspacePage();
        if (more) Dispatcher.UIThread.Post(PumpDeferredWorkspacePages, DispatcherPriority.Background);
        else if (!_isClosing) UpdateInteractionState();
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
        if (!_isClosing)
        {
            UpdateInteractionState();
            TryCompleteBooruUnavailableFallback();
        }
    }

    private bool NavigateWorkspace(WorkspacePage page, bool activateArchive = true)
    {
        if ((!_restoreCompleted && !(page == WorkspacePage.Status && _session.Projection.View.State == DesktopSessionState.RecoveryFailed)
                && !(EarlyPageChoice && IsEarlyPage(page)))
            || WorkspaceCommandBusy) return false;
        if (page == WorkspacePage.Booru && !CanOpenBooru) return false;
        // Settings is an early page and the recovery-failure route reaches Status (its Diagnostics segment) without a
        // completed restore: both can arrive before the background pass has built their page. Booru is deliberately
        // independent of the deferred legacy page batch and is created only by its own lazy host hook.
        if (page != WorkspacePage.Downloads && page != WorkspacePage.Archive && page != WorkspacePage.Media
            && page != WorkspacePage.Booru)
            EnsureDeferredWorkspacePages();
        if (page == WorkspacePage.Booru && !EnsureBooruPage()) return false;
        if (page != WorkspacePage.Archive) _archivePage?.CancelPendingGalleryOpen();
        if (page != WorkspacePage.Archive) ForgetMediaReturn(); // the way back belongs to the archive the Medien page opened
        if (page != _selectedPage)
        {
            ++_mediaPageNavigationRevision;
            _mediaPage?.InvalidateSharedMediaMenu();
            if (_selectedPage == WorkspacePage.Tagging) ++_taggingPageVisit;
        }
        ++_readingResumeNavigationRevision;
        if (page != WorkspacePage.Sources) _pluginsPage?.Deactivate();
        // Without a restored session only the diagnostics segment exists (the recovery-failure route).
        if (page == WorkspacePage.Status) _statusSegment = EffectiveStatusSegment(CurrentStatusSegment);
        if (page != WorkspacePage.Status || _statusSegment != StatusSegment.Statistics) _statisticsPage?.Deactivate();
        if (page != WorkspacePage.Settings) _settingsEditor?.EndSearchVisit();
        if (_selectedPage == WorkspacePage.Rules && page != WorkspacePage.Rules) _rulesPage?.DeactivateRuleDecisions();
        _selectedPage = page;
        UpdateMediaFocusChrome(); // the media focus mode hides the shell's chrome on the Media page only
        WorkspaceBody.IsVisible = page == WorkspacePage.Downloads;
        SettingsPageHost.IsVisible = page == WorkspacePage.Settings;
        StatusPageHost.IsVisible = page == WorkspacePage.Status;
        RulesPageHost.IsVisible = page == WorkspacePage.Rules;
        ArchivePageHost.IsVisible = page == WorkspacePage.Archive;
        MediaPageHost.IsVisible = page == WorkspacePage.Media;
        BooruPageHost.IsVisible = page == WorkspacePage.Booru;
        SourcesPageHost.IsVisible = page == WorkspacePage.Sources;
        TaggingPageHost.IsVisible = page == WorkspacePage.Tagging;
        DownloadsNavigationButton.Classes.Set("selected", page == WorkspacePage.Downloads);
        SettingsNavigationButton.Classes.Set("selected", page == WorkspacePage.Settings);
        StatusNavigationButton.Classes.Set("selected", page == WorkspacePage.Status);
        RulesNavigationButton.Classes.Set("selected", page == WorkspacePage.Rules);
        ArchiveNavigationButton.Classes.Set("selected", page == WorkspacePage.Archive);
        MediaNavigationButton.Classes.Set("selected", page == WorkspacePage.Media);
        BooruNavigationButton.Classes.Set("selected", page == WorkspacePage.Booru);
        TaggingNavigationButton.Classes.Set("selected", page == WorkspacePage.Tagging);
        SourcesNavigationButton.Classes.Set("selected", page == WorkspacePage.Sources);
        RefreshStartupPreview(); // The historical preview belongs to the Downloads page only.
        if (page == WorkspacePage.Archive) MarkArchiveBridgeHintSeen(); // reading has been found by another route
        UpdatePageNavigation();
        RefreshReadingResumePresentation();
        if (page == WorkspacePage.Sources) RefreshSourcesPage();
        if (page == WorkspacePage.Sources) _ = _sourcesPage?.ActivateAsync();
        if (page != WorkspacePage.Status) { UpdateSpeedSamplingState(); RefreshStatusRates(); } // a page gives up its claim when left
        // The segment shown, its claim (statistics only), view, data and focus.
        if (page == WorkspacePage.Status) ActivateStatusSegment();
        if (page == WorkspacePage.Settings)
        {
            // The gallery may have changed its live View layout before the deferred Settings page was built.
            // Read the live layout wish here instead of the config snapshot used to construct the editor.
            _settingsEditor?.RefreshConfirmedGalleryFilmstrip(GalleryLayoutNow().View.Filmstrip != false);
            _settingsEditor?.RevealCurrentError();
            _settingsEditor?.FindControl<ComboBox>("WorkspaceModeInput")?.Focus();
        }
        if (page == WorkspacePage.Rules) { _rulesPage?.RefreshConfirmedRules(); _ = _rulesPage?.RefreshPluginFiltersAsync(); }
        if (page == WorkspacePage.Archive && activateArchive) _ = _archivePage?.ActivateAsync();
        if (page == WorkspacePage.Media) _ = _mediaPage?.ActivateAsync();
        if (page == WorkspacePage.Booru) { _ = _booruPage?.ActivateAsync(); RequestBooruRepairRefresh(); }
        if (page == WorkspacePage.Tagging) ActivateTaggingPage();
        return true;
    }

    private bool _measuringPageNavigation;
    private bool? _booruNavigationAvailable;

    // The footer's separators: one trailing slot behind every page button (so the order of the children follows the footer layout only), shown
    // where the next shown button belongs to another group and behind the last one. The three XAML separators are the slots behind Booru,
    // Plugins and Status, the places they have in the default layout.
    private Dictionary<string, Border>? _footerSeparatorAfter;
    private string? _footerArrangement;

    /// <summary>The footer layout of this profile (Settings › Appearance › Footer).</summary>
    internal FooterLayout FooterLayout => _config.FooterLayout;

    /// <summary>
    /// Shows or hides one page's footer button and saves the choice (the small API for the hint "You use Booru - show it in the footer?").
    /// Booru additionally needs <c>Config.ShowBooruTools</c> ("Show Booru actions" in Settings, the master of the Booru page and the Danbooru
    /// tools): showing Booru here switches that on too, so the button the hint offers really appears. Downloads cannot be hidden.
    /// </summary>
    internal async Task SetFooterPageVisibleAsync(string pageId, bool visible)
    {
        if (_isClosing) return;
        string[] stored = FooterLayout.SetPageVisible(_config.FooterPages, pageId, visible);
        bool enableBooruActions = visible && FooterLayout.CommandId(pageId.Trim()) == "navigation.booru" && !_config.ShowBooruTools;
        if (stored.SequenceEqual(_config.FooterPages, StringComparer.Ordinal) && !enableBooruActions) return;
        try
        {
            await _session.SaveViewPreferencesAsync(current =>
            {
                current.FooterPages = stored;
                if (enableBooruActions) current.ShowBooruTools = true;
            });
            _config.FooterPages = stored;
            if (enableBooruActions) _config.ShowBooruTools = true;
            _settingsEditor?.RefreshConfirmedFooter();
            ApplyFooterLayout();
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Could not save the footer layout.");
            ShowNotification(Texts.T("notification.error"), Texts.T("preferences.failed", exception.Message), NotificationType.Error, operation: "preferences.footer");
        }
    }

    /// <summary>Follows the profile's footer layout: the digits (Ctrl/Cmd+1, 2, 3 ... follow the buttons shown in the work view from left to right), the button order and visibility.</summary>
    private void ApplyFooterLayout()
    {
        ApplyFooterNumbering(_config.FooterLayout, ConfiguredWorkView, CanOpenBooru);
        UpdatePageNavigation();
    }

    // The digits follow the layout. Publishing the unchanged bindings again rebuilds their snapshot with the new numbering, so tooltips, menus,
    // the shortcuts window and the router all see it at once.
    // The digits follow the layout AND the work view (the buttons effectively shown, FooterLayout.NumberingFor), never the window width.
    private static void ApplyFooterNumbering(FooterLayout layout, WorkView view, bool booruAvailable)
    {
        if (MainPageOrder.SetNumbering(layout.NumberingFor(view, id => id != "navigation.booru" || booruAvailable))) ShortcutService.Publish(ShortcutService.CurrentSection);
    }

    private Dictionary<string, Border> FooterSeparators()
    {
        if (_footerSeparatorAfter != null) return _footerSeparatorAfter;
        var map = new Dictionary<string, Border>(StringComparer.Ordinal)
        {
            ["navigation.booru"] = FooterSeparatorWork, ["navigation.sources"] = FooterSeparatorSetUp, ["navigation.status"] = FooterSeparatorObserve
        };
        foreach (string id in MainPageOrder.CommandIds)
        {
            if (map.ContainsKey(id)) continue;
            map[id] = new Border
            {
                Name = "FooterSeparatorAfter" + MainPageOrder.ControlStem(id), Width = 1, Height = 18, Margin = new Thickness(4, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, IsHitTestVisible = false, Focusable = false, IsVisible = false
            };
            map[id].Classes.Add("nav-separator");
            AutomationProperties.SetAccessibilityView(map[id], AccessibilityView.Raw);
        }
        return _footerSeparatorAfter = map;
    }

    // The order of the children is the footer order; it changes only when the layout does (never on a resize or a page change), so the
    // keyboard focus stays where it is. Every page button is a child, a hidden one is simply not visible.
    private void ArrangeFooterButtons(FooterLayout layout)
    {
        string key = string.Join('|', layout.Order);
        if (key == _footerArrangement) return;
        StackPanel panel = WorkspaceNavigationButtons;
        Dictionary<string, Border> separators = FooterSeparators();
        List<Control> desired = new();
        foreach (string id in layout.Order)
        {
            desired.Add(WorkspaceControl<Button>(MainPageOrder.ControlStem(id) + "NavigationButton"));
            desired.Add(separators[id]);
        }
        desired.Add(ToolsNavigationButton);
        desired.Add(SettingsNavigationButton);
        Control? focused = FocusManager?.GetFocusedElement() as Control;
        panel.Children.Clear();
        panel.Children.AddRange(desired);
        if (focused != null && desired.Contains(focused)) Dispatcher.UIThread.Post(() => { if (focused.IsEffectivelyVisible) focused.Focus(NavigationMethod.Tab); }, DispatcherPriority.Loaded);
        _footerArrangement = key;
    }

    /// <summary>
    /// The place the tool entries of the ⋯ menu (Continue reading, Post bookmarks, Tagging queue) insert themselves behind: the slot in front of
    /// Import files. The menu holds no page entries since F1 (the pages are reached through the footer and its More menu), so this replaces
    /// the former separator behind the page group.
    /// </summary>
    private void SetMoreMenuItemEnabled(string tag, bool enabled)
    {
        if (ToolsNavigationButton.Flyout is MenuFlyout menu)
            foreach (MenuItem item in menu.Items.OfType<MenuItem>().Where(item => item.Tag as string == tag)) item.IsEnabled = enabled;
    }

    private int MenuToolsAnchor => WorkspaceMenuItem.Items.IndexOf(ManualImportMenuItem) - 1;

    private void UpdatePageNavigation()
    {
        if (_measuringPageNavigation) return;
        FooterLayout layout = _config.FooterLayout;
        ApplyFooterNumbering(layout, ConfiguredWorkView, CanOpenBooru);
        _booruNavigationAvailable = CanOpenBooru; // what this refresh builds the Booru button and More entry from (SyncBooruNavigation compares)
        _measuringPageNavigation = true;
        try
        {
            // Availability and the presentation switch remove the destination completely. If a
            // transition races the refresh while the page is active, leave for Downloads without
            // disposing the still-composed page (the toggle must preserve its draft).
            if (_selectedPage != WorkspacePage.Booru) _booruUnavailableNavigation.Clear();
            if (!_isClosing && _selectedPage == WorkspacePage.Booru && !CanOpenBooru)
            {
                _booruUnavailableNavigation.Request();
                TryCompleteBooruUnavailableFallback();
            }
            WorkView view = ConfiguredWorkView;
            Button downloads = DownloadsNavigationButton;
            Button settings = SettingsNavigationButton;
            Button tools = ToolsNavigationButton;
            StackPanel panel = WorkspaceNavigationButtons;
            Border footer = WorkspaceNavigation;
            ComboBox modes = WorkspaceModeComboBox;
            ArrangeFooterButtons(layout);
            Dictionary<string, Border> separatorAfter = FooterSeparators();
            // Footer order, the profile's layout (Downloads is always shown). A page the layout hides, a work view reduces or an unavailable
            // Booru has no button of its own; the first two are in More, the last is nowhere.
            var destinations = layout.Order
                .Select(id => (Id: id, Page: PageFor(id), Button: WorkspaceControl<Button>(MainPageOrder.ControlStem(id) + "NavigationButton"),
                    Tag: MainPageOrder.Tag(id), Available: id != "navigation.booru" || CanOpenBooru,
                    IsDownloads: id == "navigation.downloads")).ToArray();
            // Capture the actual focused menu destination before changing visibility:
            // hiding its presenter can clear FocusManager synchronously.
            Control? focusedBefore = FocusManager?.GetFocusedElement() as Control;
            Button? returnDestination = tools.Flyout is MenuFlyout openMenu && openMenu.IsOpen
                ? destinations.Where(destination => openMenu.Items.OfType<MenuItem>().Any(item =>
                    item.Tag as string == destination.Tag && (ReferenceEquals(item, focusedBefore)
                        || item.IsKeyboardFocusWithin))).Select(destination => destination.Button).FirstOrDefault()
                : null;
            // The setting can hide a button but never shows one a work view reduces (the policy is the upper bound). Media sits with Archive:
            // both are ordinary reading pages outside Barebones (WorkViewPolicy.Shell).
            bool[] direct = destinations.Select(destination => destination.Available && layout.ShowsDirectly(destination.Id, view)).ToArray();

            // Measure the localized labels instead of guessing a breakpoint. Hidden
            // destinations move into Tools exactly once, including in compact Advanced.
            CommandBindings.Apply(downloads, "navigation.downloads", iconWithLabel: true);
            CommandBindings.Apply(settings, "navigation.settings", iconWithLabel: true);
            CommandBindings.Apply(tools, "navigation.tools-menu", iconWithLabel: true);
            // Every page button starts each pass with icon and text (an earlier narrow pass may have left icons only).
            foreach (var destination in destinations.Where(destination => !destination.IsDownloads))
                CommandBindings.Apply(destination.Button, destination.Id, iconWithLabel: true);
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
            double[] destinationWidths = destinations.Select(destination => destination.IsDownloads ? 0 : Measure(destination.Button)).ToArray();
            // Footer groups (Downloads, Archive, Media, Tagging, Booru | Rules, Plugins | Status): a separator stands behind a shown button whose
            // next shown button belongs to another group, and behind the last shown one (before More and Settings). With the default layout
            // this is today's footer; with a layout of the person's own it follows the order they chose, so no two separators meet.
            int[] groups = destinations.Select(destination => MainPageOrder.Group(destination.Id)).ToArray();
            bool[] SeparatorFlags(bool[] visible)
            {
                bool[] flags = new bool[visible.Length];
                int previous = -1;
                for (int index = 0; index < visible.Length; index++)
                {
                    if (!visible[index]) continue;
                    if (previous >= 0) flags[previous] = groups[previous] != groups[index];
                    previous = index;
                }
                if (previous >= 0) flags[previous] = true;
                return flags;
            }
            double separatorWidth = separatorAfter["navigation.status"].Width + separatorAfter["navigation.status"].Margin.Left + separatorAfter["navigation.status"].Margin.Right;
            double width = ClientSize.Width > 0 ? ClientSize.Width : Width;
            Grid layoutGrid = (Grid)panel.Parent!;
            // The work view selector is the only fixed item next to the buttons (the former "Show Booru" check box moved to Settings, F1).
            double fixedFooterWidth = modes.Width + 2 * layoutGrid.ColumnSpacing;
            double available = width - footer.Padding.Left - footer.Padding.Right - fixedFooterWidth;
            // A page the layout or work view moved to More needs the More button; one that is unavailable does not.
            bool NeedsMore() => destinations.Where((destination, index) => destination.Available && !direct[index]).Any();
            // QW-A (review 15.11.2026, item 6): every page button is icon and text; when that does not fit the page buttons become icons (tooltip
            // and accessible name stay the command's) before a page moves to More, then Settings, Downloads and More lose their text, in this
            // order (FooterFitPlanner). The rightmost page leaves first, so the buttons the person put at the front stay longest (with the
            // default layout this is the order Status, Plugins, Rules, Booru, Tagging, Media, Archive: MainPageOrder.OverflowOrder).
            static double IconOnlyWidth(Button button) => Math.Max(button.MinWidth, 16 + button.Padding.Left + button.Padding.Right
                + button.BorderThickness.Left + button.BorderThickness.Right);
            FooterFitButton[] fitPages = destinations.Select((destination, index) => new FooterFitButton(destination.Id,
                destination.IsDownloads ? downloadsWidth : destinationWidths[index], IconOnlyWidth(destination.Button), direct[index], destination.Available)).ToArray();
            FooterFitResult fit = FooterFitPlanner.Plan(available, panel.Spacing, separatorWidth, fitPages, "navigation.downloads",
                new FooterFitButton("navigation.settings", settingsWidth, IconOnlyWidth(settings)),
                new FooterFitButton("navigation.tools-menu", toolsWidth, IconOnlyWidth(tools)),
                flags => SeparatorFlags(flags).Count(flag => flag));
            for (int index = 0; index < destinations.Length; index++)
                if (fit.MovedToMore.Contains(destinations[index].Id)) direct[index] = false;
            foreach (var destination in destinations.Where(destination => !destination.IsDownloads))
                CommandBindings.Apply(destination.Button, destination.Id, iconOnly: fit.PagesIconOnly, iconWithLabel: !fit.PagesIconOnly);
            if (fit.SettingsIconOnly) CommandBindings.Apply(settings, "navigation.settings", iconOnly: true);
            if (fit.DownloadsIconOnly) CommandBindings.Apply(downloads, "navigation.downloads", iconOnly: true);
            if (fit.ToolsIconOnly) CommandBindings.Apply(tools, "navigation.tools-menu", iconOnly: true);

            bool hasOverflow = NeedsMore();
            tools.IsVisible = hasOverflow;
            // The static More entry has no destination record when Booru is unavailable; clear it before
            // the normal destination loop so stale availability cannot leave a clickable target behind.
            BooruMoreMenuItem.IsVisible = false;
            tools.Classes.Set("selected", destinations.Where((destination, index) => destination.Available && !direct[index])
                .Any(destination => destination.Page == _selectedPage));
            for (int index = 0; index < destinations.Length; index++)
            {
                var destination = destinations[index];
                destination.Button.IsVisible = direct[index];
                destination.Button.Classes.Set("selected", destination.Page == _selectedPage);
                if (tools.Flyout is MenuFlyout menu)
                    foreach (MenuItem item in menu.Items.OfType<MenuItem>())
                        if (item.Tag as string == destination.Tag) item.IsVisible = destination.Available && !direct[index];
            }
            bool[] separatorFlags = SeparatorFlags(direct);
            for (int index = 0; index < destinations.Length; index++) separatorAfter[destinations[index].Id].IsVisible = separatorFlags[index];
            if (tools.Flyout is MenuFlyout flyout)
            {
                // The flyout lists only the pages that left the footer, in the groups of the base order; its separators follow the same rule.
                bool GroupInFlyout(int group) => destinations.Where((destination, index) => groups[index] == group && destination.Available && !direct[index]).Any();
                int groupCount = MainPageOrder.Groups.Count;
                foreach (Separator separator in flyout.Items.OfType<Separator>())
                    if (separator.Tag is string tag && tag.StartsWith("nav.group-separator.", StringComparison.Ordinal)
                        && int.TryParse(tag["nav.group-separator.".Length..], out int boundary))
                        separator.IsVisible = GroupInFlyout(boundary) && Enumerable.Range(boundary + 1, groupCount - boundary - 1).Any(GroupInFlyout);
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
        // The Status page's line counts in both segments; the diagnostics table only while its segment is shown.
        if (_selectedPage != WorkspacePage.Status) return;
        if (_galleryCoversWorkspace || _sessionRowsApplying) { _sessionDirtyWhileCovered = true; return; }
        DesktopSessionView current = view ?? _session.Projection.View;
        DiagnosticsSnapshot? snapshot = RefreshStatusCounts(current);
        if (DiagnosticsSegmentShown) _diagnosticsPage?.Refresh(current, !WorkspaceCommandBusy, snapshot);
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
        ToolsNavigationButton.Flyout?.Hide();
        NavigateWorkspace(WorkspacePage.Sources);
    }

    private bool OpenDiagnosticWatch(DiagnosticTarget target)
    {
        if (_sessionRowsApplying || !DiagnosticsSnapshot.ContainsTarget(_session.Projection.View, target)) return false;
        if (target.Kind == DiagnosticTargetKind.Thread)
        {
            if (!_threadRows.TryGetValue(target.WatchId, out ThreadRow? row)) return false;
            if (!NavigateWorkspace(WorkspacePage.Downloads)) return false;
            DownloadsTabs.SelectedIndex = 0;
            ThreadsDataGrid.SelectedItem = row;
        }
        else
        {
            if (!_boardRows.TryGetValue(target.WatchId, out BoardRow? row)) return false;
            if (!NavigateWorkspace(WorkspacePage.Downloads)) return false;
            DownloadsTabs.SelectedIndex = 1;
            BoardsDataGrid.SelectedItem = row;
        }
        OpenWorkspaceDetails();
        return true;
    }

    private void OnMediaNavigationClick(object? sender, RoutedEventArgs e)
    {
        ToolsNavigationButton.Flyout?.Hide();
        NavigateWorkspace(WorkspacePage.Media);
    }

    // The page is built on first use (EnsureTaggingPage), so the footer, More and ⋯ entries go through ShowTaggingPage.
    private void OnTaggingNavigationClick(object? sender, RoutedEventArgs e)
    {
        ToolsNavigationButton.Flyout?.Hide();
        ShowTaggingPage();
    }

    private void OnArchiveNavigationClick(object? sender, RoutedEventArgs e)
    {
        ToolsNavigationButton.Flyout?.Hide();
        NavigateWorkspace(WorkspacePage.Archive);
    }

    private void OnRulesNavigationClick(object? sender, RoutedEventArgs e)
    {
        ToolsNavigationButton.Flyout?.Hide();
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
        await CheckDownloadSelectionAsync(DownloadsTabs.SelectedIndex == 1, false);

    private void DisposeWorkspacePages()
    {
        DisposeUpdates();
        DisposeMacMenu();
        _pluginsPage?.EndLifetime();
        _statisticsPage?.EndLifetime();
        _diagnosticsPage?.EndLifetime();
        DisposeStatusPage();
        if (_sourcesPage != null) { _sourcesPage.BusyChanged -= OnSettingsBusyChanged; _sourcesPage.EndLifetime(); }
        _archivePage?.EndLifetime();
        _mediaPage?.EndLifetime();
        _taggingPage?.Dispose();
        if (_rulesPage != null) { _rulesPage.BusyChanged -= OnSettingsBusyChanged; _rulesPage.EndLifetime(); }
        DisposeBooruPage();
        if (_settingsEditor == null) return;
        _settingsEditor.BusyChanged -= OnSettingsBusyChanged;
        _settingsEditor.EndLifetime();
    }
}

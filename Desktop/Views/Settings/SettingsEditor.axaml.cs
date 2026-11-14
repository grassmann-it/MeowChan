using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Updates;

namespace MeowChan
{
    public partial class SettingsEditor : UserControl, INotifyPropertyChanged
    {
        public static IMultiValueConverter NumericActionNameConverter { get; } =
            new FuncMultiValueConverter<object?, string>((IReadOnlyList<object?> values) =>
                values.Count == 2 && values[0] is string format && values[1] is string field
                    ? string.Format(CultureInfo.CurrentCulture, format, field) : string.Empty);

        private readonly Config _config;
        private readonly Func<Config, Task>? _applySettings;
        private readonly Func<Task>? _forgetReadingPosition;
        private readonly bool _closeOnDiscard;
        private bool _rememberReadingPosition;
        private bool _monitorClipboardUrls;
        private string _downloadPath = string.Empty;
        private decimal? _recheckTimer;
        private decimal? _maxParallelDownloads;
        private decimal? _parallelDownloadsSafetyLimit;
        private decimal? _bandwidthLimiterMB;
        private bool _saveUrlsOnExit;
        private bool _showBoardAdmissionWizard;
        private bool _minimizeToTray;
        private bool _closeToTray;
        private bool _isBusy;
        private bool _isClosed;
        private bool _errorRevealPending;
        private bool _suppressPassiveErrorReveal;
        private long _errorRevealRevision;
        private bool _refreshingLanguageLabels;
        private string? _errorMessage;
        private LocalizedMessage? _errorMessageState;
        private LocalizedMessage? _statusMessageState;

        // Only app-owned wrappers are localized; provider/exception arguments stay verbatim.
        private sealed record LocalizedMessage(string Key, object?[] Arguments)
        {
            public string Render() => Texts.T(Key, Arguments);
        }

        private void SetErrorMessage(string? key, params object?[] arguments)
        {
            _errorMessageState = key == null ? null : new LocalizedMessage(key, arguments);
            ErrorMessage = _errorMessageState?.Render();
        }

        // A success message describes the saved draft only; a later real edit must not keep promising it.
        private void RetireSavedStatus()
        {
            if (_statusMessageState?.Key == "settings.saved") SetStatusMessage("settings.unsaved");
        }

        private void SetStatusMessage(string? key, params object?[] arguments)
        {
            _statusMessageState = key == null ? null : new LocalizedMessage(key, arguments);
            StatusMessage = _statusMessageState?.Render();
        }

        private void RefreshLocalizedMessages()
        {
            if (_errorMessageState != null) ErrorMessage = _errorMessageState.Render();
            if (_statusMessageState != null) StatusMessage = _statusMessageState.Render();
        }
        private SettingsChoice? _selectedTheme;
        private SettingsChoice? _selectedAnimations;
        private SettingsChoice? _selectedWorkspaceMode;
        private LanguageInfo? _selectedLanguage;
        private bool _compactDensity;
        private bool _initialCompactDensity;
        private string _initialReadingFontSize = "Normal";
        private string _initialWorkspaceMode = "Barebones";
        private int _initialVideoVolume;
        private GalleryPlaybackPreferences _initialGalleryPlayback = new();
        private SettingsChoice? _selectedGalleryPlaybackMode;
        private decimal? _gallerySlideshowSeconds = 5;
        private bool _galleryMediaFocus;
        private bool _galleryOverlayControls;
        private bool _galleryOverlayPostContext;
        private SettingsChoice? _selectedReadingFontSize;
        private SettingsChoice? _selectedReaderPostWidth;
        private bool _videoAutoplay;
        private bool _videoStartMuted;
        private int _videoVolume;
        private bool _showShortcutHelp;
        private bool _showInformationNotifications = true;
        private bool _showWarningNotifications = true;
        private bool _showErrorNotifications = true;
        private bool _automaticUpdateChecks;
        private bool _includePrereleaseUpdates;
        private decimal? _minimumFreeSpaceGiB;
        private UpdateSession? _updates;
        internal UpdateSession? Updates
        {
            get => _updates;
            set
            {
                if (_updates != null) _updates.Changed -= OnUpdateStateChanged;
                _updates = value;
                if (_updates != null) _updates.Changed += OnUpdateStateChanged;
                OnUpdateStateChanged();
            }
        }

        private void OnUpdateStateChanged() => Dispatcher.UIThread.Post(() =>
        {
            if (_isClosed) return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UpdatesDisabled)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UpdatesEnabled)));
        });
        private decimal? _productionWorkers;
        private decimal? _workerIdleMinutes;
        private decimal? _sourceHeapMiB;
        private decimal? _filterHeapMiB;
        private decimal? _booruCatalogMiB;
        private decimal? _privateTagLimit;
        private decimal? _privateTagFileMiB;
        private decimal? _mediaIndexCacheMiB;
        private decimal? _favoriteEntryLimit;
        private decimal? _favoriteFileLimitMiB;
        private decimal? _postBookmarkLimit;
        private decimal? _savedSearchLimit;
        private decimal? _readerGroupLimit;
        private decimal? _sourcesWatchPreviewRows;
        private decimal? _exportConfirmFiles;
        private decimal? _exportConfirmGiB;
        private decimal? _posterCacheLimitMiB;
        private decimal? _posterCacheFileLimit;
        private decimal? _audioProbeCacheLimitMiB;
        private decimal? _audioProbeCacheEntries;
        private decimal? _thumbnailCacheTiles;
        private decimal? _clipboardUrlLimit;
        private decimal? _clipboardTextLimitKiB;
        private decimal? _galleryImageCacheMiB;
        private decimal? _galleryPreloadAhead;
        private decimal? _galleryPreloadBehind;
        private decimal? _importMaxFilesPerBatch;
        private decimal? _importMaxBatchGiB;
        private decimal? _importMaxFileMiB;
        private decimal? _importMaxTotalFiles;
        private decimal? _importMaxTagsPerBatch;
        private decimal? _importMaxBatches;
        private decimal? _mediaIndexMaxEntries;
        private decimal? _mediaIndexMaxThreads;
        private decimal? _importCatalogLimitMiB;


        public IReadOnlyList<SettingsChoice> ThemeChoices { get; private set; } = Array.Empty<SettingsChoice>();
        public IReadOnlyList<SettingsChoice> AnimationsChoices { get; private set; } = Array.Empty<SettingsChoice>();
        public IReadOnlyList<SettingsChoice> ReadingFontSizeChoices { get; private set; } = Array.Empty<SettingsChoice>();
        public IReadOnlyList<SettingsChoice> ReaderPostWidthChoices { get; private set; } = Array.Empty<SettingsChoice>();
        public IReadOnlyList<SettingsChoice> GalleryPlaybackModeChoices { get; private set; } = Array.Empty<SettingsChoice>();
        public IReadOnlyList<SettingsChoice> WorkspaceChoices { get; private set; } = Array.Empty<SettingsChoice>();
        public IReadOnlyList<LanguageInfo> LanguageChoices { get; private set; } = Array.Empty<LanguageInfo>();

        public new event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler? BusyChanged;
        public event EventHandler? Saved;
        public event EventHandler? Cancelled;
        private bool _isAvailable = true;
        private string? _statusMessage;

        public bool IsAvailable
        {
            get => _isAvailable;
            set
            {
                SetField(ref _isAvailable, value, nameof(IsAvailable));
                if (!_isClosed)
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEditable)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanForgetReadingPosition)));
                }
            }
        }
        public bool IsEditable => IsAvailable && !IsBusy;
        public string? BusyStatusMessage => IsBusy ? Texts.T("feedback.working") : StatusMessage;
        public string? StatusMessage
        {
            get => _statusMessage;
            private set
            {
                SetField(ref _statusMessage, value, nameof(StatusMessage));
                if (!_isClosed)
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BusyStatusMessage)));
            }
        }

        public SettingsEditor() : this(new Config())
        {
        }

        /// <param name="applySettings">Commits the validated candidate and applies it to the running session. The compatibility host can omit this to save an isolated profile directly.</param>
        public SettingsEditor(Config config, Func<Config, Task>? applySettings = null, Func<Task>? forgetReadingPosition = null, bool closeOnDiscard = false)
        {
            ArgumentNullException.ThrowIfNull(config);
            InitializeComponent();
            _config = config;
            ShortcutsEditor.Load(config.ShortcutBindings);
            ShortcutsEditor.DraftEdited += (_, _) => RetireSavedStatus();
            _initialCompactDensity = config.CompactDensity;
            _initialReadingFontSize = config.ReadingFontSize;
            _initialWorkspaceMode = config.WorkspaceMode;
            _initialVideoVolume = config.VideoVolume;
            _applySettings = applySettings;
            _forgetReadingPosition = forgetReadingPosition ?? (applySettings == null
                ? () => Task.Run(config.ForgetReadingPositionFile) : null);
            _closeOnDiscard = closeOnDiscard;
            DownloadPath = config.DownloadPath;
            RecheckTimer = config.RecheckTimer;
            MaxParallelDownloads = config.MaxParallelDownloads;
            ParallelDownloadsSafetyLimit = config.ParallelDownloadsSafetyLimit;
            SaveUrlsOnExit = config.SaveUrlsOnExit;
            ShowBoardAdmissionWizard = config.ShowBoardAdmissionWizard;
            MinimizeToTray = config.MinimizeToTray;
            CloseToTray = config.CloseToTray;
            BandwidthLimiterMB = config.BandwidthLimiter / (1024m * 1024m);
            CompactDensity = config.CompactDensity;
            VideoAutoplay = config.VideoAutoplay;
            VideoStartMuted = config.VideoStartMuted;
            VideoVolume = config.VideoVolume;
            ResetGalleryPlaybackDraft();
            ShowShortcutHelp = config.ShowShortcutHelp;
            RememberReadingPosition = config.RememberReadingPosition;
            MonitorClipboardUrls = config.MonitorClipboardUrls;
            AutomaticUpdateChecks = config.AutomaticUpdateChecks;
            ShowInformationNotifications = config.ShowInformationNotifications;
            ShowWarningNotifications = config.ShowWarningNotifications;
            ShowErrorNotifications = config.ShowErrorNotifications;
            IncludePrereleaseUpdates = config.IncludePrereleaseUpdates;
            MinimumFreeSpaceGiB = config.MinimumFreeSpaceGiB;
            ProductionWorkers = config.PluginMaximumProductionWorkers;
            WorkerIdleMinutes = config.PluginWorkerIdleMinutes;
            SourceHeapMiB = config.PluginSourceHeapMiB;
            FilterHeapMiB = config.PluginFilterHeapMiB;
            BooruCatalogMiB = config.BooruCatalogLimitMiB;
            PrivateTagLimit = config.PrivateTagLimit;
            PrivateTagFileMiB = config.PrivateTagFileLimitMiB;
            MediaIndexCacheMiB = config.MediaIndexCacheLimitMiB;
            FavoriteEntryLimit = config.FavoriteEntryLimit;
            FavoriteFileLimitMiB = config.FavoriteFileLimitMiB;
            PostBookmarkLimit = config.PostBookmarkLimit;
            SavedSearchLimit = config.SavedSearchLimit;
            ReaderGroupLimit = config.ReaderGroupLimit;
            SourcesWatchPreviewRows = config.SourcesWatchPreviewRows;
            ExportConfirmFiles = config.ExportConfirmFiles;
            ExportConfirmGiB = config.ExportConfirmGiB;
            PosterCacheLimitMiB = config.PosterCacheLimitMiB;
            PosterCacheFileLimit = config.PosterCacheFileLimit;
            AudioProbeCacheLimitMiB = config.AudioProbeCacheLimitMiB;
            AudioProbeCacheEntries = config.AudioProbeCacheEntries;
            ThumbnailCacheTiles = config.ThumbnailCacheTiles;
            ClipboardUrlLimit = config.ClipboardUrlLimit;
            ClipboardTextLimitKiB = config.ClipboardTextLimitKiB;
            GalleryImageCacheMiB = config.GalleryImageCacheMiB;
            GalleryPreloadAhead = config.GalleryPreloadAhead;
            GalleryPreloadBehind = config.GalleryPreloadBehind;
            ImportMaxFilesPerBatch = config.ImportMaxFilesPerBatch;
            ImportMaxBatchGiB = config.ImportMaxBatchGiB;
            ImportMaxFileMiB = config.ImportMaxFileMiB;
            ImportMaxTotalFiles = config.ImportMaxTotalFiles;
            ImportMaxTagsPerBatch = config.ImportMaxTagsPerBatch;
            ImportMaxBatches = config.ImportMaxBatches;
            MediaIndexMaxEntries = config.MediaIndexMaxEntries;
            MediaIndexMaxThreads = config.MediaIndexMaxThreads;
            ImportCatalogLimitMiB = config.ImportCatalogLimitMiB;

            LoadStatisticsDraft(config);
            LoadReaderDraft(config);
            RefreshChoices(config.Theme, config.WorkspaceMode, config.Language);
            DataContext = this;
            ApplyCommandIcons();
            InitializeSectionNavigation();
            InitializeSearch();
            ResetNumber(RecheckTimerInput, RecheckTimer!.Value);
            ResetNumber(ParallelDownloadsInput, MaxParallelDownloads!.Value);
            ResetNumber(ParallelDownloadsSafetyInput, ParallelDownloadsSafetyLimit!.Value);
            ResetNumber(BandwidthInput, BandwidthLimiterMB!.Value);
            ResetNumber(ReserveInput, MinimumFreeSpaceGiB!.Value);
            ResetNumber(ProductionWorkersInput, ProductionWorkers!.Value);
            ResetNumber(WorkerIdleMinutesInput, WorkerIdleMinutes!.Value);
            ResetNumber(SourceHeapMiBInput, SourceHeapMiB!.Value);
            ResetNumber(FilterHeapMiBInput, FilterHeapMiB!.Value);
            ResetNumber(BooruCatalogMiBInput, BooruCatalogMiB!.Value);
            ResetNumber(PrivateTagLimitInput, PrivateTagLimit!.Value);
            ResetNumber(PrivateTagFileMiBInput, PrivateTagFileMiB!.Value);
            ResetNumber(MediaIndexCacheMiBInput, MediaIndexCacheMiB!.Value);
            ResetNumber(FavoriteEntryLimitInput, FavoriteEntryLimit!.Value);
            ResetNumber(FavoriteFileLimitMiBInput, FavoriteFileLimitMiB!.Value);
            ResetNumber(PostBookmarkLimitInput, PostBookmarkLimit!.Value);
            ResetNumber(SavedSearchLimitInput, SavedSearchLimit!.Value);
            ResetNumber(ReaderGroupLimitInput, ReaderGroupLimit!.Value);
            ResetNumber(SourcesWatchPreviewRowsInput, SourcesWatchPreviewRows!.Value);
            ResetNumber(ExportConfirmFilesInput, ExportConfirmFiles!.Value);
            ResetNumber(ExportConfirmGiBInput, ExportConfirmGiB!.Value);
            ResetNumber(PosterCacheLimitMiBInput, PosterCacheLimitMiB!.Value);
            ResetNumber(PosterCacheFileLimitInput, PosterCacheFileLimit!.Value);
            ResetNumber(AudioProbeCacheLimitMiBInput, AudioProbeCacheLimitMiB!.Value);
            ResetNumber(AudioProbeCacheEntriesInput, AudioProbeCacheEntries!.Value);
            ResetNumber(ThumbnailCacheTilesInput, ThumbnailCacheTiles!.Value);
            ResetNumber(ClipboardUrlLimitInput, ClipboardUrlLimit!.Value);
            ResetNumber(ClipboardTextLimitKiBInput, ClipboardTextLimitKiB!.Value);
            ResetNumber(GalleryImageCacheMiBInput, GalleryImageCacheMiB!.Value);
            ResetNumber(GalleryPreloadAheadInput, GalleryPreloadAhead!.Value);
            ResetNumber(GalleryPreloadBehindInput, GalleryPreloadBehind!.Value);
            ResetNumber(ImportMaxFilesPerBatchInput, ImportMaxFilesPerBatch!.Value);
            ResetNumber(ImportMaxBatchGiBInput, ImportMaxBatchGiB!.Value);
            ResetNumber(ImportMaxFileMiBInput, ImportMaxFileMiB!.Value);
            ResetNumber(ImportMaxTotalFilesInput, ImportMaxTotalFiles!.Value);
            ResetNumber(ImportMaxTagsPerBatchInput, ImportMaxTagsPerBatch!.Value);
            ResetNumber(ImportMaxBatchesInput, ImportMaxBatches!.Value);
            ResetNumber(MediaIndexMaxEntriesInput, MediaIndexMaxEntries!.Value);
            ResetNumber(MediaIndexMaxThreadsInput, MediaIndexMaxThreads!.Value);
            ResetNumber(ImportCatalogLimitMiBInput, ImportCatalogLimitMiB!.Value);
            ResetNumber(GallerySlideshowSecondsInput, GallerySlideshowSeconds!.Value);
            InitializeWorkViews();

            Texts.Changed += OnLanguageChanged;
            DownloadDestinationValidator.Shared.CleanupFailed += OnDestinationCleanupFailed;
        }

        public string NotificationsTitle => Texts.T("feedback.settings");
        public string InformationNotificationsLabel => Texts.T("feedback.information");
        public string WarningNotificationsLabel => Texts.T("feedback.warnings");
        public string ErrorNotificationsLabel => Texts.T("feedback.errors");
        public string NotificationsHint => Texts.T("feedback.hint");
        public bool ShowInformationNotifications { get => _showInformationNotifications; set => SetField(ref _showInformationNotifications, value, nameof(ShowInformationNotifications)); }
        public bool ShowWarningNotifications { get => _showWarningNotifications; set => SetField(ref _showWarningNotifications, value, nameof(ShowWarningNotifications)); }
        public bool ShowErrorNotifications { get => _showErrorNotifications; set => SetField(ref _showErrorNotifications, value, nameof(ShowErrorNotifications)); }
        public string UpdatesTitle => Texts.T("updates.title");
        public string AutomaticUpdateChecksLabel => Texts.T("updates.automatic");
        public string IncludePrereleaseUpdatesLabel => Texts.T("updates.prereleases");
        public string UpdatePrivacyHint => Texts.T("updates.privacy");
        public string UpdateDisabledReason => Texts.T("updates.disabled");
        public bool UpdatesDisabled => Updates?.View.EnvironmentDisabled ?? Environment.GetEnvironmentVariable("MEOWCHAN_DISABLE_UPDATE_CHECK") == "1";
        public bool UpdatesEnabled => !UpdatesDisabled;
        public string ReserveLabel => Texts.T("budgets.reserve");
        public string ReserveHint => Texts.T("budgets.reserveHint");
        public string AdvancedBudgetsLabel => Texts.T("budgets.advanced");
        public string StorageLimitsLabel => Texts.T("settings.storageLimits");
        public string StorageLimitsIntro => Texts.T("budgets.intro");
        public string BudgetRestartHint => Texts.T("budgets.nextSession");
        public bool AutomaticUpdateChecks { get => _automaticUpdateChecks; set => SetField(ref _automaticUpdateChecks, value, nameof(AutomaticUpdateChecks)); }
        public bool IncludePrereleaseUpdates { get => _includePrereleaseUpdates; set => SetField(ref _includePrereleaseUpdates, value, nameof(IncludePrereleaseUpdates)); }
        public decimal? MinimumFreeSpaceGiB { get => _minimumFreeSpaceGiB; set => SetField(ref _minimumFreeSpaceGiB, value, nameof(MinimumFreeSpaceGiB)); }
        public string ProductionWorkersLabel => Texts.T("budgets.workers");
        public decimal? ProductionWorkers { get => _productionWorkers; set => SetField(ref _productionWorkers, value, nameof(ProductionWorkers)); }
        public string WorkerIdleMinutesLabel => Texts.T("budgets.idle");
        public decimal? WorkerIdleMinutes { get => _workerIdleMinutes; set => SetField(ref _workerIdleMinutes, value, nameof(WorkerIdleMinutes)); }
        public string SourceHeapMiBLabel => Texts.T("budgets.sourceHeap");
        public decimal? SourceHeapMiB { get => _sourceHeapMiB; set => SetField(ref _sourceHeapMiB, value, nameof(SourceHeapMiB)); }
        public string FilterHeapMiBLabel => Texts.T("budgets.filterHeap");
        public decimal? FilterHeapMiB { get => _filterHeapMiB; set => SetField(ref _filterHeapMiB, value, nameof(FilterHeapMiB)); }
        public string BooruCatalogMiBLabel => Texts.T("budgets.booruCatalog");
        public string BooruCatalogMiBHint => Texts.T("budgets.booruCatalogHint");
        public decimal? BooruCatalogMiB { get => _booruCatalogMiB; set => SetField(ref _booruCatalogMiB, value, nameof(BooruCatalogMiB)); }
        public string PrivateTagLimitLabel => Texts.T("budgets.privateTags");
        public string PrivateTagLimitHint => Texts.T("budgets.privateTagsHint");
        public decimal? PrivateTagLimit { get => _privateTagLimit; set => SetField(ref _privateTagLimit, value, nameof(PrivateTagLimit)); }
        public string PrivateTagFileMiBLabel => Texts.T("budgets.privateTagFile");
        public string PrivateTagFileMiBHint => Texts.T("budgets.privateTagFileHint");
        public decimal? PrivateTagFileMiB { get => _privateTagFileMiB; set => SetField(ref _privateTagFileMiB, value, nameof(PrivateTagFileMiB)); }
        public string MediaIndexCacheMiBLabel => Texts.T("budgets.mediaIndexCache");
        public string MediaIndexCacheMiBHint => Texts.T("budgets.mediaIndexCacheHint");
        public decimal? MediaIndexCacheMiB { get => _mediaIndexCacheMiB; set => SetField(ref _mediaIndexCacheMiB, value, nameof(MediaIndexCacheMiB)); }
        public string FavoriteEntryLimitLabel => Texts.T("budgets.favoriteEntries");
        public string FavoriteEntryLimitHint => Texts.T("budgets.favoriteEntriesHint");
        public decimal? FavoriteEntryLimit { get => _favoriteEntryLimit; set => SetField(ref _favoriteEntryLimit, value, nameof(FavoriteEntryLimit)); }
        public string FavoriteFileLimitMiBLabel => Texts.T("budgets.favoriteFile");
        public string FavoriteFileLimitMiBHint => Texts.T("budgets.favoriteFileHint");
        public decimal? FavoriteFileLimitMiB { get => _favoriteFileLimitMiB; set => SetField(ref _favoriteFileLimitMiB, value, nameof(FavoriteFileLimitMiB)); }
        public string PostBookmarkLimitLabel => Texts.T("budgets.postBookmarks");
        public string PostBookmarkLimitHint => Texts.T("budgets.postBookmarksHint");
        public decimal? PostBookmarkLimit { get => _postBookmarkLimit; set => SetField(ref _postBookmarkLimit, value, nameof(PostBookmarkLimit)); }
        public string SavedSearchLimitLabel => Texts.T("budgets.savedSearches");
        public string SavedSearchLimitHint => Texts.T("budgets.savedSearchesHint");
        public decimal? SavedSearchLimit { get => _savedSearchLimit; set => SetField(ref _savedSearchLimit, value, nameof(SavedSearchLimit)); }
        public string ReaderGroupLimitLabel => Texts.T("budgets.readerGroups");
        public string ReaderGroupLimitHint => Texts.T("budgets.readerGroupsHint");
        public decimal? ReaderGroupLimit { get => _readerGroupLimit; set => SetField(ref _readerGroupLimit, value, nameof(ReaderGroupLimit)); }
        public string SourcesWatchPreviewRowsLabel => Texts.T("budgets.sourcesWatchPreviewRows");
        public string SourcesWatchPreviewRowsHint => Texts.T("budgets.sourcesWatchPreviewRowsHint");
        public decimal? SourcesWatchPreviewRows { get => _sourcesWatchPreviewRows; set => SetField(ref _sourcesWatchPreviewRows, value, nameof(SourcesWatchPreviewRows)); }
        public string ExportConfirmFilesLabel => Texts.T("budgets.exportConfirmFiles");
        public string ExportConfirmFilesHint => Texts.T("budgets.exportConfirmFilesHint");
        public decimal? ExportConfirmFiles { get => _exportConfirmFiles; set => SetField(ref _exportConfirmFiles, value, nameof(ExportConfirmFiles)); }
        public string ExportConfirmGiBLabel => Texts.T("budgets.exportConfirmGiB");
        public string ExportConfirmGiBHint => Texts.T("budgets.exportConfirmGiBHint");
        public decimal? ExportConfirmGiB { get => _exportConfirmGiB; set => SetField(ref _exportConfirmGiB, value, nameof(ExportConfirmGiB)); }
        public string PosterCacheLimitMiBLabel => Texts.T("budgets.posterCacheMiB");
        public string PosterCacheLimitMiBHint => Texts.T("budgets.posterCacheMiBHint");
        public decimal? PosterCacheLimitMiB { get => _posterCacheLimitMiB; set => SetField(ref _posterCacheLimitMiB, value, nameof(PosterCacheLimitMiB)); }
        public string PosterCacheFileLimitLabel => Texts.T("budgets.posterCacheFiles");
        public string ThumbnailCacheTilesLabel => Texts.T("budgets.thumbnailCacheTiles");
        public string GalleryImageCacheMiBLabel => Texts.T("budgets.galleryImageCacheMiB");
        public string GalleryPreloadAheadLabel => Texts.T("budgets.galleryPreloadAhead");
        public string GalleryPreloadBehindLabel => Texts.T("budgets.galleryPreloadBehind");
        public string PosterCacheFileLimitHint => Texts.T("budgets.posterCacheFilesHint");
        public string ThumbnailCacheTilesHint => Texts.T("budgets.thumbnailCacheTilesHint");
        public string GalleryImageCacheMiBHint => Texts.T("budgets.galleryImageCacheMiBHint");
        public string GalleryPreloadAheadHint => Texts.T("budgets.galleryPreloadAheadHint");
        public string GalleryPreloadBehindHint => Texts.T("budgets.galleryPreloadBehindHint");
        public decimal? PosterCacheFileLimit { get => _posterCacheFileLimit; set => SetField(ref _posterCacheFileLimit, value, nameof(PosterCacheFileLimit)); }
        public string AudioProbeCacheLimitMiBLabel => Texts.T("budgets.audioCacheMiB");
        public string AudioProbeCacheLimitMiBHint => Texts.T("budgets.audioCacheMiBHint");
        public decimal? AudioProbeCacheLimitMiB { get => _audioProbeCacheLimitMiB; set => SetField(ref _audioProbeCacheLimitMiB, value, nameof(AudioProbeCacheLimitMiB)); }
        public string AudioProbeCacheEntriesLabel => Texts.T("budgets.audioCacheEntries");
        public string AudioProbeCacheEntriesHint => Texts.T("budgets.audioCacheEntriesHint");
        public decimal? AudioProbeCacheEntries { get => _audioProbeCacheEntries; set => SetField(ref _audioProbeCacheEntries, value, nameof(AudioProbeCacheEntries)); }
        public decimal? ThumbnailCacheTiles { get => _thumbnailCacheTiles; set => SetField(ref _thumbnailCacheTiles, value, nameof(ThumbnailCacheTiles)); }
        public string ClipboardUrlLimitLabel => Texts.T("budgets.clipboardUrls");
        public string ClipboardUrlLimitHint => Texts.T("budgets.clipboardUrlsHint");
        public decimal? ClipboardUrlLimit { get => _clipboardUrlLimit; set => SetField(ref _clipboardUrlLimit, value, nameof(ClipboardUrlLimit)); }
        public string ClipboardTextLimitKiBLabel => Texts.T("budgets.clipboardText");
        public string ClipboardTextLimitKiBHint => Texts.T("budgets.clipboardTextHint");
        public decimal? ClipboardTextLimitKiB { get => _clipboardTextLimitKiB; set => SetField(ref _clipboardTextLimitKiB, value, nameof(ClipboardTextLimitKiB)); }
        public decimal? GalleryImageCacheMiB { get => _galleryImageCacheMiB; set => SetField(ref _galleryImageCacheMiB, value, nameof(GalleryImageCacheMiB)); }
        public decimal? GalleryPreloadAhead { get => _galleryPreloadAhead; set => SetField(ref _galleryPreloadAhead, value, nameof(GalleryPreloadAhead)); }
        public decimal? GalleryPreloadBehind { get => _galleryPreloadBehind; set => SetField(ref _galleryPreloadBehind, value, nameof(GalleryPreloadBehind)); }
        public string ImportMaxFilesPerBatchLabel => Texts.T("budgets.importFiles");
        public string ImportMaxFilesPerBatchHint => Texts.T("budgets.importFilesHint");
        public decimal? ImportMaxFilesPerBatch { get => _importMaxFilesPerBatch; set => SetField(ref _importMaxFilesPerBatch, value, nameof(ImportMaxFilesPerBatch)); }
        public string ImportMaxBatchGiBLabel => Texts.T("budgets.importBatchGiB");
        public string ImportMaxBatchGiBHint => Texts.T("budgets.importBatchGiBHint");
        public decimal? ImportMaxBatchGiB { get => _importMaxBatchGiB; set => SetField(ref _importMaxBatchGiB, value, nameof(ImportMaxBatchGiB)); }
        public string ImportMaxFileMiBLabel => Texts.T("budgets.importFileMiB");
        public string ImportMaxFileMiBHint => Texts.T("budgets.importFileMiBHint");
        public decimal? ImportMaxFileMiB { get => _importMaxFileMiB; set => SetField(ref _importMaxFileMiB, value, nameof(ImportMaxFileMiB)); }
        public string ImportMaxTotalFilesLabel => Texts.T("budgets.importTotal");
        public string ImportMaxTotalFilesHint => Texts.T("budgets.importTotalHint");
        public decimal? ImportMaxTotalFiles { get => _importMaxTotalFiles; set => SetField(ref _importMaxTotalFiles, value, nameof(ImportMaxTotalFiles)); }
        public string ImportMaxTagsPerBatchLabel => Texts.T("budgets.importTags");
        public string ImportMaxTagsPerBatchHint => Texts.T("budgets.importTagsHint");
        public decimal? ImportMaxTagsPerBatch { get => _importMaxTagsPerBatch; set => SetField(ref _importMaxTagsPerBatch, value, nameof(ImportMaxTagsPerBatch)); }
        public string ImportMaxBatchesLabel => Texts.T("budgets.importBatches");
        public string ImportMaxBatchesHint => Texts.T("budgets.importBatchesHint");
        public decimal? ImportMaxBatches { get => _importMaxBatches; set => SetField(ref _importMaxBatches, value, nameof(ImportMaxBatches)); }
        public string MediaIndexMaxEntriesLabel => Texts.T("budgets.mediaIndexEntries");
        public string MediaIndexMaxEntriesHint => Texts.T("budgets.mediaIndexEntriesHint");
        public decimal? MediaIndexMaxEntries { get => _mediaIndexMaxEntries; set => SetField(ref _mediaIndexMaxEntries, value, nameof(MediaIndexMaxEntries)); }
        public string MediaIndexMaxThreadsLabel => Texts.T("budgets.mediaIndexThreads");
        public string MediaIndexMaxThreadsHint => Texts.T("budgets.mediaIndexThreadsHint");
        public decimal? MediaIndexMaxThreads { get => _mediaIndexMaxThreads; set => SetField(ref _mediaIndexMaxThreads, value, nameof(MediaIndexMaxThreads)); }
        public string ImportCatalogLimitMiBLabel => Texts.T("budgets.importCatalogMiB");
        public string ImportCatalogLimitMiBHint => Texts.T("budgets.importCatalogMiBHint");
        public decimal? ImportCatalogLimitMiB { get => _importCatalogLimitMiB; set => SetField(ref _importCatalogLimitMiB, value, nameof(ImportCatalogLimitMiB)); }
        public string ListsGroupLabel => Texts.T("budgets.group.lists");
        public string PreviewsGroupLabel => Texts.T("budgets.group.previews");
        public string ImportGroupLabel => Texts.T("budgets.group.import");

        public string SettingsTitle => Texts.T("settings.title");
        public string SettingsSubtitle => Texts.T("settings.subtitle");
        public string DownloadsTitle => Texts.T("settings.downloads");
        public string InterfaceTitle => Texts.T("settings.interface");
        public string ReadingMediaTitle => Texts.T("r08d.settings.readingMedia");
        public string ReadingFontSizeLabel => Texts.T("r08d.settings.readingFontSize");
        public string ReaderPostWidthLabel => Texts.T("reader.postWidth");
        public string ReaderPostWidthHint => Texts.T("reader.postWidth.hint");
        public string VideoAutoplayLabel => Texts.T("r08d.settings.videoAutoplay");
        public string VideoStartMutedLabel => Texts.T("r08d.settings.videoStartMuted");
        public string VideoVolumeLabel => Texts.T("r08d.settings.videoVolume");
        public string GalleryPlaybackTitle => GalleryPlaybackCatalog.Title;
        public string GalleryPlaybackModeLabel => GalleryPlaybackCatalog.Mode;
        public string GallerySlideshowSecondsLabel => GalleryPlaybackCatalog.ImageSeconds;
        public string GallerySlideshowSecondsHelp => GallerySlideshowSecondsInputError.IsVisible
            ? GallerySlideshowSecondsInputError.Text ?? GalleryPlaybackCatalog.ImageSecondsHelp : GalleryPlaybackCatalog.ImageSecondsHelp;
        public string GalleryPlaybackModeHelp => GalleryPlaybackCatalog.ModeHelp;
        public string GalleryMediaFocusLabel => GalleryPlaybackCatalog.MediaFocus;
        public string GalleryOverlayControlsLabel => GalleryPlaybackCatalog.OverlayControls;
        public string GalleryAutoHideControlsLabel => GalleryPlaybackCatalog.AutoHideControls;
        public string GalleryOverlayPostContextLabel => GalleryPlaybackCatalog.OverlayPostContext;
        public string GalleryOverlayHelp => GalleryPlaybackCatalog.OverlayHelp;
        public string GalleryPlaybackProfileHelp => GalleryPlaybackCatalog.ProfileHelp;
        public string ShowShortcutHelpLabel => Texts.T("r08d.settings.shortcutHelp");
        public string AboutLabel => Texts.T("r08d.settings.about");
        public string BehaviorTitle => Texts.T("settings.behavior");
        public string DownloadPathLabel => Texts.T("settings.downloadPath");
        public string BrowseLabel => Texts.T("settings.browse");
        public string PathHint => Texts.T("settings.pathHint");
        public string RecheckLabel => Texts.T("settings.recheckSeconds");
        public string BandwidthLabel => Texts.T("settings.bandwidth");
        public string ParallelLabel => Texts.T("settings.parallelDownloads");
        public string ParallelSafetyLabel => Texts.T("settings.parallelSafetyLimit");
        public string ParallelSafetyHint => Texts.T("settings.parallelSafetyHint");
        public string ParallelHint => Texts.T("settings.parallelHint");
        public string BandwidthHint => Texts.T("settings.bandwidthHint");
        // Live from the two drafts: the smaller value is what the queue applies. Empty while
        // either draft is not a valid whole number (the field errors already say why).
        public string ParallelEffectiveText =>
            MaxParallelDownloads is { } requested && ParallelDownloadsSafetyLimit is { } cap
            && requested >= Config.MaxParallelDownloadsMinimum && requested == decimal.Truncate(requested) && requested <= Config.MaxParallelDownloadsMaximum
            && cap >= 1 && cap == decimal.Truncate(cap) && cap <= Downloader.DownloadQueue.MaximumParallelDownloadsSafetyLimit
                ? Texts.T("settings.parallelEffective", (int)Math.Min(requested, cap)) : string.Empty;
        private void RaiseParallelEffectiveChanged()
        {
            if (!_isClosed) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ParallelEffectiveText)));
        }
        public string RestoreSessionLabel => Texts.T("settings.restoreSession");
        public string BoardAdmissionWizardLabel => Texts.T("settings.boardAdmissionWizard");
        public string MonitorClipboardLabel => Texts.T("settings.monitorClipboard");
        public string MonitorClipboardHint => Texts.T("settings.monitorClipboardHint");
        public string CloseToTrayLabel => Texts.T("settings.closeToTray");
        public string MinimizeToTrayLabel => Texts.T("settings.minimizeToTray");
        public string ThemeLabel => Texts.T("settings.theme");
        public string AnimationsLabel => Texts.T("settings.animations");
        public string AnimationsHint => Texts.T("settings.animations.hint");
        public string LanguageLabel => Texts.T("settings.language");
        public string LanguagePacksLabel => Texts.T("settings.languagePacksTitle");
        public string LanguagePacksHint => Texts.T("settings.languagePacks");
        public string WorkspaceModeLabel => Texts.T("settings.viewMode");
        public string DensityLabel => Texts.T("settings.density");
        public string CompactLabel => Texts.T("density.compact");
        public string ModeHint => Texts.T("settings.modeHint");
        public string SaveLabel => Texts.T("settings.save");
        public string CancelLabel => Texts.T(_closeOnDiscard ? "settings.cancel" : "rules.discard");
        public string RememberReadingPositionLabel => Texts.T("resume.remember");
        public string ReadingPositionHint => Texts.T("resume.hint");
        public string ForgetReadingPositionLabel => Texts.T("resume.forget");
        public string SecondsHelp => Texts.T("settings.unitSeconds");
        public string BandwidthHelp => Texts.T("settings.unitBandwidth");
        public string VolumeHelp => Texts.T("settings.unitPercent");
        public string IncreaseNumericValueFormat => Texts.T("settings.increaseValue");
        public string DecreaseNumericValueFormat => Texts.T("settings.decreaseValue");
        public bool CanForgetReadingPosition => _forgetReadingPosition != null && IsEditable;
        public bool RememberReadingPosition { get => _rememberReadingPosition; set => SetField(ref _rememberReadingPosition, value, nameof(RememberReadingPosition)); }
        public string SupportedSources => Texts.T("settings.supportedSources");

        public SettingsChoice? SelectedAnimations
        {
            get => _selectedAnimations;
            set => SetField(ref _selectedAnimations, value, nameof(SelectedAnimations));
        }

        public SettingsChoice? SelectedTheme
        {
            get => _selectedTheme;
            set => SetField(ref _selectedTheme, value, nameof(SelectedTheme));
        }

        public SettingsChoice? SelectedReadingFontSize
        {
            get => _selectedReadingFontSize;
            set => SetField(ref _selectedReadingFontSize, value, nameof(SelectedReadingFontSize));
        }
        public SettingsChoice? SelectedReaderPostWidth
        {
            get => _selectedReaderPostWidth;
            set => SetField(ref _selectedReaderPostWidth, value, nameof(SelectedReaderPostWidth));
        }
        public bool VideoAutoplay { get => _videoAutoplay; set => SetField(ref _videoAutoplay, value, nameof(VideoAutoplay)); }
        public bool VideoStartMuted { get => _videoStartMuted; set => SetField(ref _videoStartMuted, value, nameof(VideoStartMuted)); }
        public int VideoVolume { get => _videoVolume; set => SetField(ref _videoVolume, value, nameof(VideoVolume)); }
        public SettingsChoice? SelectedGalleryPlaybackMode
        {
            get => _selectedGalleryPlaybackMode;
            set
            {
                SetField(ref _selectedGalleryPlaybackMode, value, nameof(SelectedGalleryPlaybackMode));
                // Replacing localized ItemsSource values can transiently clear the
                // TwoWay ComboBox selection. Do not let that transient null hide
                // the focused slideshow editor; publish the final semantic state
                // once RefreshChoices has restored the selection.
                if (!_refreshingLanguageLabels)
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsGallerySlideshow)));
            }
        }
        public bool IsGallerySlideshow => SelectedGalleryPlaybackMode?.Value == "Slideshow";
        public decimal? GallerySlideshowSeconds { get => _gallerySlideshowSeconds; set => SetField(ref _gallerySlideshowSeconds, value, nameof(GallerySlideshowSeconds)); }
        public bool GalleryMediaFocus
        {
            get => _galleryMediaFocus;
            set => SetField(ref _galleryMediaFocus, value, nameof(GalleryMediaFocus));
        }
        public bool GalleryOverlayControls
        {
            get => _galleryOverlayControls;
            set
            {
                if (_galleryOverlayControls == value) return;
                // One backing value keeps both aliases coherent even during notifications.
                SetField(ref _galleryOverlayControls, value, nameof(GalleryOverlayControls));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GalleryAutoHideControls)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowGalleryOverlayOptions)));
            }
        }
        public bool ShowGalleryOverlayOptions => GalleryOverlayControls;
        public bool GalleryAutoHideControls { get => GalleryOverlayControls; set => GalleryOverlayControls = value; }
        public bool GalleryOverlayPostContext { get => _galleryOverlayPostContext; set => SetField(ref _galleryOverlayPostContext, value, nameof(GalleryOverlayPostContext)); }

        private void ResetGalleryPlaybackDraft()
        {
            // Capture at editor creation, explicit discard and successful save only.
            // A later runtime gallery change must not become this draft's baseline.
            _initialGalleryPlayback = GalleryPlaybackPreferences.FromConfig(_config);
            string mode = _initialGalleryPlayback.Mode.ToString();
            SelectedGalleryPlaybackMode = GalleryPlaybackModeChoices.FirstOrDefault(item => item.Value == mode) ?? new(mode, mode);
            GallerySlideshowSeconds = _initialGalleryPlayback.ImageSeconds;
            GalleryMediaFocus = _initialGalleryPlayback.MediaFocus;
            GalleryOverlayControls = _initialGalleryPlayback.OverlayControls;
            GalleryOverlayPostContext = _initialGalleryPlayback.OverlayPostContext;
        }

        public bool ShowShortcutHelp { get => _showShortcutHelp; set => SetField(ref _showShortcutHelp, value, nameof(ShowShortcutHelp)); }

        public SettingsChoice? SelectedWorkspaceMode
        {
            get => _selectedWorkspaceMode;
            set => SetField(ref _selectedWorkspaceMode, value, nameof(SelectedWorkspaceMode));
        }

        public LanguageInfo? SelectedLanguage
        {
            get => _selectedLanguage;
            set => SetField(ref _selectedLanguage, value, nameof(SelectedLanguage));
        }

        public bool CompactDensity
        {
            get => _compactDensity;
            set => SetField(ref _compactDensity, value, nameof(CompactDensity));
        }

        public string DownloadPath
        {
            get => _downloadPath;
            set => SetField(ref _downloadPath, value, nameof(DownloadPath));
        }

        public decimal? RecheckTimer
        {
            get => _recheckTimer;
            set => SetField(ref _recheckTimer, value, nameof(RecheckTimer));
        }

        public decimal? MaxParallelDownloads
        {
            get => _maxParallelDownloads;
            set { SetField(ref _maxParallelDownloads, value, nameof(MaxParallelDownloads)); RaiseParallelEffectiveChanged(); }
        }

        public decimal? ParallelDownloadsSafetyLimit
        {
            get => _parallelDownloadsSafetyLimit;
            set { SetField(ref _parallelDownloadsSafetyLimit, value, nameof(ParallelDownloadsSafetyLimit)); RaiseParallelEffectiveChanged(); }
        }

        public decimal? BandwidthLimiterMB
        {
            get => _bandwidthLimiterMB;
            set => SetField(ref _bandwidthLimiterMB, value, nameof(BandwidthLimiterMB));
        }

        public bool SaveUrlsOnExit
        {
            get => _saveUrlsOnExit;
            set => SetField(ref _saveUrlsOnExit, value, nameof(SaveUrlsOnExit));
        }

        // Opt-in stays in this draft until the explicit Save commits its candidate.
        public bool MonitorClipboardUrls
        {
            get => _monitorClipboardUrls;
            set => SetField(ref _monitorClipboardUrls, value, nameof(MonitorClipboardUrls));
        }

        public bool ShowBoardAdmissionWizard
        {
            get => _showBoardAdmissionWizard;
            set => SetField(ref _showBoardAdmissionWizard, value, nameof(ShowBoardAdmissionWizard));
        }

        public bool CloseToTray
        {
            get => _closeToTray;
            set => SetField(ref _closeToTray, value, nameof(CloseToTray));
        }

        public bool MinimizeToTray
        {
            get => _minimizeToTray;
            set => SetField(ref _minimizeToTray, value, nameof(MinimizeToTray));
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (_isBusy == value) return;
                SetField(ref _isBusy, value, nameof(IsBusy));
                if (!_isClosed)
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BusyStatusMessage)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEditable)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanForgetReadingPosition)));
                    BusyChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public string? ErrorMessage
        {
            get => _errorMessage;
            private set
            {
                if (_isClosed)
                    return;
                if (value == null)
                {
                    ClearFieldErrors();
                    ClearGalleryPlaybackError();
                }
                else if (!_refreshingLanguageLabels) { _suppressPassiveErrorReveal = false; _sectionNavigation?.CancelPendingNavigation(); }
                SetField(ref _errorMessage, value, nameof(ErrorMessage));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasError)));
                if (!_refreshingLanguageLabels) RevealErrorAfterLayout();
            }
        }

        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

        private void OnSettingsScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            // Native window limits can change the viewport after the initial layout.
            // Ignore offset-only changes so users can still scroll away from an error.
            if (ReferenceEquals(e.Source, SettingsScrollViewer) &&
                (e.ViewportDelta.X != 0 || e.ViewportDelta.Y != 0 || e.ExtentDelta.X != 0 || e.ExtentDelta.Y != 0))
            {
                RevealErrorAfterLayout();
            }
        }

        private void RevealErrorAfterLayout()
        {
            if (_isClosed || !IsVisible || !HasError || _errorRevealPending || _suppressPassiveErrorReveal)
                return;

            _errorRevealPending = true;
            long revealRevision = _errorRevealRevision;
            Dispatcher.UIThread.Post(() =>
            {
                _errorRevealPending = false;
                if (!_suppressPassiveErrorReveal && revealRevision == _errorRevealRevision && !_isClosed && IsVisible && HasError && SettingsError.IsAttachedToVisualTree() && SettingsError.Bounds.Height > 0)
                {
                    if (_firstInvalidInput != null)
                        FieldRevealTarget(_firstInvalidInput).BringIntoView();
                    else
                        SettingsError.BringIntoView();
                }
            }, DispatcherPriority.Loaded);
        }

        public void EndLifetime()
        {
            if (_isClosed) return;
            _isClosed = true;
            _destinationWait?.Cancel();
            DownloadDestinationValidator.Shared.CleanupFailed -= OnDestinationCleanupFailed;
            _sectionNavigation?.Dispose();
            Texts.Changed -= OnLanguageChanged;
            if (_updates != null) _updates.Changed -= OnUpdateStateChanged;
            SettingsScrollViewer.ScrollChanged -= OnSettingsScrollChanged;
        }

        internal void RefreshConfirmedAppearance(bool theme, bool workspaceMode)
        {
            if (_isClosed) return;
            if (theme) SelectedTheme = ThemeChoices.First(item => item.Value == _config.Theme);
            if (workspaceMode) SelectedWorkspaceMode = WorkspaceChoices.First(item => item.Value == _config.WorkspaceMode);
        }

        // The shell creates this editor before the first-run modal is offered.
        // Refresh only the fields that setup durably committed; do not discard an
        // unrelated settings draft or re-read values merely on page navigation.
        internal void RefreshConfirmedFirstRunSetup()
        {
            if (_isClosed) return;
            DownloadPath = _config.DownloadPath;
            MaxParallelDownloads = _config.MaxParallelDownloads;
            BandwidthLimiterMB = _config.BandwidthLimiter / (1024m * 1024m);
            // Settings validates raw numeric text, not just the bound Value.
            ResetNumber(ParallelDownloadsInput, MaxParallelDownloads.Value);
            ResetNumber(BandwidthInput, BandwidthLimiterMB.Value);
        }

        internal void RevealCurrentError() => RevealErrorAfterLayout();

        internal void CancelChanges()
        {
            if (IsBusy || !IsAvailable || _isClosed) return;
            ShortcutsEditor.Load(_config.ShortcutBindings);
            DownloadPath = _config.DownloadPath;
            RecheckTimer = _config.RecheckTimer;
            MaxParallelDownloads = _config.MaxParallelDownloads;
            ParallelDownloadsSafetyLimit = _config.ParallelDownloadsSafetyLimit;
            SaveUrlsOnExit = _config.SaveUrlsOnExit;
            ShowBoardAdmissionWizard = _config.ShowBoardAdmissionWizard;
            MinimizeToTray = _config.MinimizeToTray;
            CloseToTray = _config.CloseToTray;
            BandwidthLimiterMB = _config.BandwidthLimiter / (1024m * 1024m);
            CompactDensity = _config.CompactDensity;
            VideoAutoplay = _config.VideoAutoplay;
            VideoStartMuted = _config.VideoStartMuted;
            _initialVideoVolume = _config.VideoVolume;
            VideoVolume = _config.VideoVolume;
            ResetGalleryPlaybackDraft();
            ShowShortcutHelp = _config.ShowShortcutHelp;
            RememberReadingPosition = _config.RememberReadingPosition;
            MonitorClipboardUrls = _config.MonitorClipboardUrls;
            AutomaticUpdateChecks = _config.AutomaticUpdateChecks;
            ShowInformationNotifications = _config.ShowInformationNotifications;
            ShowWarningNotifications = _config.ShowWarningNotifications;
            ShowErrorNotifications = _config.ShowErrorNotifications;
            IncludePrereleaseUpdates = _config.IncludePrereleaseUpdates;
            MinimumFreeSpaceGiB = _config.MinimumFreeSpaceGiB;
            ProductionWorkers = _config.PluginMaximumProductionWorkers;
            WorkerIdleMinutes = _config.PluginWorkerIdleMinutes;
            SourceHeapMiB = _config.PluginSourceHeapMiB;
            FilterHeapMiB = _config.PluginFilterHeapMiB;
            BooruCatalogMiB = _config.BooruCatalogLimitMiB;
            PrivateTagLimit = _config.PrivateTagLimit;
            PrivateTagFileMiB = _config.PrivateTagFileLimitMiB;
            MediaIndexCacheMiB = _config.MediaIndexCacheLimitMiB;
            FavoriteEntryLimit = _config.FavoriteEntryLimit;
            FavoriteFileLimitMiB = _config.FavoriteFileLimitMiB;
            PostBookmarkLimit = _config.PostBookmarkLimit;
            SavedSearchLimit = _config.SavedSearchLimit;
            ReaderGroupLimit = _config.ReaderGroupLimit;
            SourcesWatchPreviewRows = _config.SourcesWatchPreviewRows;
            ExportConfirmFiles = _config.ExportConfirmFiles;
            ExportConfirmGiB = _config.ExportConfirmGiB;
            PosterCacheLimitMiB = _config.PosterCacheLimitMiB;
            PosterCacheFileLimit = _config.PosterCacheFileLimit;
            AudioProbeCacheLimitMiB = _config.AudioProbeCacheLimitMiB;
            AudioProbeCacheEntries = _config.AudioProbeCacheEntries;
            ThumbnailCacheTiles = _config.ThumbnailCacheTiles;
            ClipboardUrlLimit = _config.ClipboardUrlLimit;
            ClipboardTextLimitKiB = _config.ClipboardTextLimitKiB;
            GalleryImageCacheMiB = _config.GalleryImageCacheMiB;
            GalleryPreloadAhead = _config.GalleryPreloadAhead;
            GalleryPreloadBehind = _config.GalleryPreloadBehind;
            ImportMaxFilesPerBatch = _config.ImportMaxFilesPerBatch;
            ImportMaxBatchGiB = _config.ImportMaxBatchGiB;
            ImportMaxFileMiB = _config.ImportMaxFileMiB;
            ImportMaxTotalFiles = _config.ImportMaxTotalFiles;
            ImportMaxTagsPerBatch = _config.ImportMaxTagsPerBatch;
            ImportMaxBatches = _config.ImportMaxBatches;
            MediaIndexMaxEntries = _config.MediaIndexMaxEntries;
            MediaIndexMaxThreads = _config.MediaIndexMaxThreads;
            ImportCatalogLimitMiB = _config.ImportCatalogLimitMiB;

            LoadStatisticsDraft(_config);
            LoadReaderDraft(_config);
            RefreshChoices(_config.Theme, _config.WorkspaceMode, _config.Language);
            ResetNumber(RecheckTimerInput, RecheckTimer!.Value);
            ResetNumber(ParallelDownloadsInput, MaxParallelDownloads!.Value);
            ResetNumber(ParallelDownloadsSafetyInput, ParallelDownloadsSafetyLimit!.Value);
            ResetNumber(BandwidthInput, BandwidthLimiterMB!.Value);
            ResetNumber(ReserveInput, MinimumFreeSpaceGiB!.Value);
            ResetNumber(ProductionWorkersInput, ProductionWorkers!.Value);
            ResetNumber(WorkerIdleMinutesInput, WorkerIdleMinutes!.Value);
            ResetNumber(SourceHeapMiBInput, SourceHeapMiB!.Value);
            ResetNumber(FilterHeapMiBInput, FilterHeapMiB!.Value);
            ResetNumber(BooruCatalogMiBInput, BooruCatalogMiB!.Value);
            ResetNumber(PrivateTagLimitInput, PrivateTagLimit!.Value);
            ResetNumber(PrivateTagFileMiBInput, PrivateTagFileMiB!.Value);
            ResetNumber(MediaIndexCacheMiBInput, MediaIndexCacheMiB!.Value);
            ResetNumber(FavoriteEntryLimitInput, FavoriteEntryLimit!.Value);
            ResetNumber(FavoriteFileLimitMiBInput, FavoriteFileLimitMiB!.Value);
            ResetNumber(PostBookmarkLimitInput, PostBookmarkLimit!.Value);
            ResetNumber(SavedSearchLimitInput, SavedSearchLimit!.Value);
            ResetNumber(ReaderGroupLimitInput, ReaderGroupLimit!.Value);
            ResetNumber(SourcesWatchPreviewRowsInput, SourcesWatchPreviewRows!.Value);
            ResetNumber(ExportConfirmFilesInput, ExportConfirmFiles!.Value);
            ResetNumber(ExportConfirmGiBInput, ExportConfirmGiB!.Value);
            ResetNumber(PosterCacheLimitMiBInput, PosterCacheLimitMiB!.Value);
            ResetNumber(PosterCacheFileLimitInput, PosterCacheFileLimit!.Value);
            ResetNumber(AudioProbeCacheLimitMiBInput, AudioProbeCacheLimitMiB!.Value);
            ResetNumber(AudioProbeCacheEntriesInput, AudioProbeCacheEntries!.Value);
            ResetNumber(ThumbnailCacheTilesInput, ThumbnailCacheTiles!.Value);
            ResetNumber(ClipboardUrlLimitInput, ClipboardUrlLimit!.Value);
            ResetNumber(ClipboardTextLimitKiBInput, ClipboardTextLimitKiB!.Value);
            ResetNumber(GalleryImageCacheMiBInput, GalleryImageCacheMiB!.Value);
            ResetNumber(GalleryPreloadAheadInput, GalleryPreloadAhead!.Value);
            ResetNumber(GalleryPreloadBehindInput, GalleryPreloadBehind!.Value);
            ResetNumber(ImportMaxFilesPerBatchInput, ImportMaxFilesPerBatch!.Value);
            ResetNumber(ImportMaxBatchGiBInput, ImportMaxBatchGiB!.Value);
            ResetNumber(ImportMaxFileMiBInput, ImportMaxFileMiB!.Value);
            ResetNumber(ImportMaxTotalFilesInput, ImportMaxTotalFiles!.Value);
            ResetNumber(ImportMaxTagsPerBatchInput, ImportMaxTagsPerBatch!.Value);
            ResetNumber(ImportMaxBatchesInput, ImportMaxBatches!.Value);
            ResetNumber(MediaIndexMaxEntriesInput, MediaIndexMaxEntries!.Value);
            ResetNumber(MediaIndexMaxThreadsInput, MediaIndexMaxThreads!.Value);
            ResetNumber(ImportCatalogLimitMiBInput, ImportCatalogLimitMiB!.Value);
            ResetNumber(GallerySlideshowSecondsInput, GallerySlideshowSeconds!.Value);
            ResetGroupBaselines();

            SetErrorMessage(null);
            SetStatusMessage(null);
        }

        private static void ResetNumber(NumericUpDown input, decimal value)
        {
            input.Value = value;
            input.Text = value.ToString(CultureInfo.CurrentCulture);
        }

        private void OnEditorSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            bool categories = e.NewSize.Width >= 820;
            bool presentationChanged = categories != SettingsCategories.IsVisible;
            bool navigationFocus = SettingsCategories.IsKeyboardFocusWithin || SettingsSections.IsKeyboardFocusWithin;
            string? selectedSection = SelectedSectionId;
            TopLevel? focusOwner = TopLevel.GetTopLevel(this);
            Control? focusOrigin = focusOwner?.FocusManager?.GetFocusedElement() as Control;
            if (presentationChanged && !navigationFocus && _pendingSectionFocusTransfer is { } pending
                && ReferenceEquals(pending.Owner, focusOwner)
                && ReferenceEquals(pending.Focus, focusOrigin))
            {
                navigationFocus = true;
                selectedSection = pending.SectionId;
            }
            if (presentationChanged)
            {
                _sectionFocusTransferRevision++;
                _pendingSectionFocusTransfer = null;
                if (navigationFocus) _sectionNavigation?.CancelPendingNavigation();
            }
            SettingsCategories.IsVisible = categories;
            SettingsSections.IsVisible = !categories;
            SettingsFormLayout.ColumnDefinitions[0].Width = new GridLength(categories ? 176 : 0);
            SettingsFormLayout.ColumnSpacing = categories ? 16 : 0;
            Grid.SetColumn(SettingsScrollViewer, categories ? 1 : 0);
            Grid.SetColumnSpan(SettingsScrollViewer, categories ? 1 : 2);
            double sharedWidth = Math.Min(1088, Math.Max(0, e.NewSize.Width - 40));
            SettingsFormLayout.Width = sharedWidth;
            SettingsHeader.Width = sharedWidth;
            SettingsFooter.Width = sharedWidth;
            double width = Math.Max(0, sharedWidth - 20 - (categories ? 192 : 0));
            SettingsContent.Width = width;
            if (presentationChanged && navigationFocus && focusOwner != null)
                QueueSectionNavigationFocus(focusOwner, focusOrigin, categories, selectedSection);
            bool narrow = width < 700;
            foreach (Grid row in SettingsContent.GetLogicalDescendants().OfType<Grid>().Where(row => row.Classes.Contains("settings-row")))
            {
                row.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 280);
                row.RowSpacing = narrow ? 6 : 0;
                row.ColumnSpacing = narrow ? 0 : 24;
                Control editor = row.Children[1];
                Grid.SetColumn(editor, narrow ? 0 : 1);
                Grid.SetRow(editor, narrow ? 1 : 0);
                row.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            }
            Grid.SetColumn(SettingsFooterActions, narrow ? 0 : 1);
            Grid.SetRow(SettingsFooterActions, narrow ? 1 : 0);
            Grid.SetColumnSpan(SettingsFooterActions, narrow ? 2 : 1);
            Grid.SetColumnSpan(SettingsFooter.Children[0], narrow ? 2 : 1);
            RevealErrorAfterLayout();
        }

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            void Refresh()
            {
                if (_isClosed)
                    return;
                RefreshChoiceLabels();
                // Refresh only labels/help; a language change is not a numeric Value change.
                foreach (string property in new[]
                {
                    nameof(BusyStatusMessage), nameof(NotificationsTitle), nameof(InformationNotificationsLabel), nameof(WarningNotificationsLabel), nameof(ErrorNotificationsLabel), nameof(NotificationsHint), nameof(UpdatesTitle), nameof(AutomaticUpdateChecksLabel), nameof(IncludePrereleaseUpdatesLabel), nameof(UpdatePrivacyHint),
                    nameof(UpdateDisabledReason), nameof(ReserveLabel), nameof(ReserveHint), nameof(AdvancedBudgetsLabel), nameof(StorageLimitsLabel), nameof(StorageLimitsIntro), nameof(ParallelEffectiveText),
                    nameof(BudgetRestartHint), nameof(ProductionWorkersLabel), nameof(WorkerIdleMinutesLabel), nameof(SourceHeapMiBLabel),
                    nameof(FilterHeapMiBLabel), nameof(BooruCatalogMiBLabel), nameof(BooruCatalogMiBHint), nameof(BooruCatalogMiBHelp),
                    nameof(PrivateTagLimitLabel), nameof(PrivateTagLimitHint), nameof(PrivateTagLimitHelp),
                    nameof(PrivateTagFileMiBLabel), nameof(PrivateTagFileMiBHint), nameof(PrivateTagFileMiBHelp), nameof(MediaIndexCacheMiBLabel), nameof(MediaIndexCacheMiBHint), nameof(MediaIndexCacheMiBHelp), nameof(FavoriteEntryLimitLabel), nameof(FavoriteEntryLimitHint), nameof(FavoriteEntryLimitHelp), nameof(FavoriteFileLimitMiBLabel), nameof(FavoriteFileLimitMiBHint), nameof(FavoriteFileLimitMiBHelp), nameof(PostBookmarkLimitLabel), nameof(PostBookmarkLimitHint), nameof(PostBookmarkLimitHelp), nameof(SavedSearchLimitLabel), nameof(SavedSearchLimitHint), nameof(SavedSearchLimitHelp), nameof(ReaderGroupLimitLabel), nameof(ReaderGroupLimitHint), nameof(ReaderGroupLimitHelp), nameof(SourcesWatchPreviewRowsLabel), nameof(SourcesWatchPreviewRowsHint), nameof(SourcesWatchPreviewRowsHelp), nameof(ExportConfirmFilesLabel), nameof(ExportConfirmFilesHint), nameof(ExportConfirmFilesHelp), nameof(ExportConfirmGiBLabel), nameof(ExportConfirmGiBHint), nameof(ExportConfirmGiBHelp), nameof(PosterCacheLimitMiBLabel), nameof(PosterCacheLimitMiBHint), nameof(PosterCacheLimitMiBHelp), nameof(PosterCacheFileLimitLabel), nameof(PosterCacheFileLimitHint), nameof(PosterCacheFileLimitHelp), nameof(AudioProbeCacheLimitMiBLabel), nameof(AudioProbeCacheLimitMiBHint), nameof(AudioProbeCacheLimitMiBHelp), nameof(AudioProbeCacheEntriesLabel), nameof(AudioProbeCacheEntriesHint), nameof(AudioProbeCacheEntriesHelp), nameof(ThumbnailCacheTilesLabel), nameof(ThumbnailCacheTilesHint), nameof(ThumbnailCacheTilesHelp), nameof(GalleryImageCacheMiBLabel), nameof(GalleryImageCacheMiBHint), nameof(GalleryImageCacheMiBHelp), nameof(GalleryPreloadAheadLabel), nameof(GalleryPreloadAheadHint), nameof(GalleryPreloadAheadHelp), nameof(GalleryPreloadBehindLabel), nameof(GalleryPreloadBehindHint), nameof(GalleryPreloadBehindHelp), nameof(ClipboardUrlLimitLabel), nameof(ClipboardUrlLimitHint), nameof(ClipboardUrlLimitHelp), nameof(ClipboardTextLimitKiBLabel), nameof(ClipboardTextLimitKiBHint), nameof(ClipboardTextLimitKiBHelp), nameof(ImportMaxFilesPerBatchLabel), nameof(ImportMaxFilesPerBatchHint), nameof(ImportMaxFilesPerBatchHelp), nameof(ImportMaxBatchGiBLabel), nameof(ImportMaxBatchGiBHint), nameof(ImportMaxBatchGiBHelp), nameof(ImportMaxFileMiBLabel), nameof(ImportMaxFileMiBHint), nameof(ImportMaxFileMiBHelp), nameof(ImportMaxTotalFilesLabel), nameof(ImportMaxTotalFilesHint), nameof(ImportMaxTotalFilesHelp), nameof(ImportCatalogLimitMiBLabel), nameof(ImportCatalogLimitMiBHint), nameof(ImportCatalogLimitMiBHelp), nameof(ImportMaxTagsPerBatchLabel), nameof(ImportMaxTagsPerBatchHint), nameof(ImportMaxTagsPerBatchHelp), nameof(ImportMaxBatchesLabel), nameof(ImportMaxBatchesHint), nameof(ImportMaxBatchesHelp), nameof(MediaIndexMaxEntriesLabel), nameof(MediaIndexMaxEntriesHint), nameof(MediaIndexMaxEntriesHelp), nameof(MediaIndexMaxThreadsLabel), nameof(MediaIndexMaxThreadsHint), nameof(MediaIndexMaxThreadsHelp), nameof(ListsGroupLabel), nameof(PreviewsGroupLabel), nameof(ImportGroupLabel), nameof(SettingsTitle), nameof(SettingsSubtitle), nameof(DownloadsTitle),
                    nameof(InterfaceTitle), nameof(ReadingMediaTitle), nameof(ReadingFontSizeLabel), nameof(ReaderPostWidthLabel), nameof(ReaderPostWidthHint), nameof(VideoAutoplayLabel),
                    nameof(VideoStartMutedLabel), nameof(VideoVolumeLabel), nameof(ShowShortcutHelpLabel), nameof(AboutLabel),
                    nameof(GalleryPlaybackTitle), nameof(GalleryPlaybackModeLabel), nameof(GallerySlideshowSecondsLabel), nameof(GallerySlideshowSecondsHelp),
                    nameof(GalleryPlaybackModeHelp), nameof(GalleryMediaFocusLabel), nameof(GalleryOverlayControlsLabel),
                    nameof(GalleryAutoHideControlsLabel), nameof(GalleryOverlayPostContextLabel), nameof(GalleryOverlayHelp), nameof(GalleryPlaybackProfileHelp),
                    nameof(BehaviorTitle), nameof(DownloadPathLabel), nameof(BrowseLabel), nameof(PathHint),
                    nameof(RecheckLabel), nameof(BandwidthLabel), nameof(BandwidthHint), nameof(ParallelLabel), nameof(ParallelHint), nameof(ParallelSafetyLabel), nameof(ParallelSafetyHint), nameof(RestoreSessionLabel),
                    nameof(MonitorClipboardLabel), nameof(MonitorClipboardHint),
                    nameof(BoardAdmissionWizardLabel), nameof(CloseToTrayLabel), nameof(MinimizeToTrayLabel), nameof(ThemeLabel), nameof(AnimationsLabel), nameof(AnimationsHint),
                    nameof(LanguageLabel), nameof(LanguagePacksLabel), nameof(LanguagePacksHint), nameof(WorkspaceModeLabel),
                    nameof(DensityLabel), nameof(CompactLabel), nameof(ModeHint), nameof(SaveLabel),
                    nameof(CancelLabel), nameof(RememberReadingPositionLabel), nameof(ReadingPositionHint), nameof(ForgetReadingPositionLabel),
                    nameof(SecondsHelp), nameof(BandwidthHelp), nameof(VolumeHelp), nameof(IncreaseNumericValueFormat),
                    nameof(DecreaseNumericValueFormat), nameof(SupportedSources), nameof(DownloadPathHelp), nameof(CheckDestinationLabel), nameof(CancelDestinationCheckLabel),
                    nameof(DestinationPolicyHint), nameof(DestinationScopeHint), nameof(NormalizedDestination),
                    nameof(DestinationStatusText), nameof(DestinationCleanupWarning), nameof(RecheckTimerHelp),
                    nameof(BandwidthInputHelp), nameof(ParallelDownloadsHelp), nameof(ParallelDownloadsSafetyHelp), nameof(ReserveInputHelp), nameof(ProductionWorkersHelp),
                    nameof(WorkerIdleMinutesHelp), nameof(SourceHeapMiBHelp), nameof(FilterHeapMiBHelp), nameof(ErrorDetailsLabel),
                })
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
                RaiseStatisticsTextsChanged();
                RaiseReaderTextsChanged();
                _refreshingLanguageLabels = true;
                try
                {
                    RefreshLocalizedMessages();
                    RefreshFieldErrorMessages();
                }
                finally { _refreshingLanguageLabels = false; }
                ApplyCommandIcons();
                RefreshSectionNavigation();
            }

            if (Dispatcher.UIThread.CheckAccess())
                Refresh();
            else
                Dispatcher.UIThread.Post(Refresh);
        }

        private void ApplyCommandIcons()
        {
            CommandBindings.ApplyLabeled(BrowseButton, "settings.browse-folder", BrowseLabel);
            CommandBindings.ApplyLabeled(AboutSettingsButton, "navigation.about", AboutLabel);
            CommandBindings.ApplyLabeled(CancelSettingsButton, "settings.discard", CancelLabel);
            CommandBindings.ApplyLabeled(SaveSettingsButton, "settings.save", SaveLabel);
        }

        private void RefreshChoiceLabels()
        {
            // Capture semantic values before ItemsSource notifications can transiently
            // clear bound selections. Label rebinding must not invalidate pending work.
            string theme = SelectedTheme?.Value ?? _config.Theme;
            string mode = SelectedWorkspaceMode?.Value ?? _config.WorkspaceMode;
            string language = SelectedLanguage?.Code ?? _config.Language;
            string readingFontSize = SelectedReadingFontSize?.Value ?? _config.ReadingFontSize;
            string animations = SelectedAnimations?.Value ?? _config.Animations;
            string statsHeaderGraph = SelectedStatsHeaderGraph?.Value ?? _config.StatsHeaderGraph;
            string readerPostWidth = SelectedReaderPostWidth?.Value ?? _config.ReaderPostWidth;
            bool wasRefreshing = _refreshingLanguageLabels;
            _refreshingLanguageLabels = true;
            try { RefreshChoices(theme, mode, language, readingFontSize, animations, statsHeaderGraph, readerPostWidth); }
            finally { _refreshingLanguageLabels = wasRefreshing; }
        }

        private void RefreshChoices(string theme, string mode, string language, string? readingFontSize = null, string? animations = null, string? statsHeaderGraph = null, string? readerPostWidth = null)
        {
            ThemeChoices = new SettingsChoice[]
            {
                new SettingsChoice("Dark", Texts.T("theme.dark")),
                new SettingsChoice("Light", Texts.T("theme.light")),
                new SettingsChoice("System", Texts.T("r08d.settings.systemTheme"))
            };
            AnimationsChoices = new SettingsChoice[]
            {
                new SettingsChoice(MeowChan.Desktop.Motion.MotionPolicy.System, Texts.T("settings.animations.system")),
                new SettingsChoice(MeowChan.Desktop.Motion.MotionPolicy.On, Texts.T("settings.animations.on")),
                new SettingsChoice(MeowChan.Desktop.Motion.MotionPolicy.Off, Texts.T("settings.animations.off"))
            };
            ReadingFontSizeChoices = new SettingsChoice[]
            {
                new SettingsChoice("Small", Texts.T("r08d.settings.fontSmall")),
                new SettingsChoice("Normal", Texts.T("r08d.settings.fontNormal")),
                new SettingsChoice("Large", Texts.T("r08d.settings.fontLarge"))
            };
            ReaderPostWidthChoices = new SettingsChoice[]
            {
                new SettingsChoice(Config.ReaderPostWidthFit, Texts.T("reader.postWidth.fit")),
                new SettingsChoice(Config.ReaderPostWidthFull, Texts.T("reader.postWidth.full")),
                new SettingsChoice(Config.ReaderPostWidthColumn, Texts.T("reader.postWidth.column"))
            };
            string galleryMode = SelectedGalleryPlaybackMode?.Value ?? _initialGalleryPlayback.Mode.ToString();
            GalleryPlaybackModeChoices = new SettingsChoice[]
            {
                new("Manual", GalleryPlaybackCatalog.Manual),
                new("Repeat", GalleryPlaybackCatalog.Repeat),
                new("Slideshow", GalleryPlaybackCatalog.Slideshow)
            };
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GalleryPlaybackModeChoices)));
            SelectedGalleryPlaybackMode = GalleryPlaybackModeChoices.FirstOrDefault(item => item.Value == galleryMode) ?? GalleryPlaybackModeChoices[0];
            // Emit one stable visibility update after the semantic selection is back.
            // During the replacement above the setter intentionally suppresses the
            // transient null notification to preserve focus/raw input state.
            if (_refreshingLanguageLabels)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsGallerySlideshow)));
            WorkspaceChoices = new SettingsChoice[]
            {
                new SettingsChoice("Barebones", Texts.T("mode.barebones")),
                new SettingsChoice("Simple", Texts.T("mode.simple")),
                new SettingsChoice("Advanced", Texts.T("mode.advanced"))
            };
            List<LanguageInfo> languages = new List<LanguageInfo>(Texts.Languages);
            if (!languages.Any(item => string.Equals(item.Code, language, StringComparison.OrdinalIgnoreCase)))
                languages.Add(new LanguageInfo(language, Texts.T("settings.unavailableLanguage", language)));
            LanguageChoices = languages;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThemeChoices)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AnimationsChoices)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReadingFontSizeChoices)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReaderPostWidthChoices)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WorkspaceChoices)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LanguageChoices)));
            SelectedTheme = ThemeChoices.First(item => item.Value == theme);
            SelectedAnimations = AnimationsChoices.First(item => item.Value == (animations ?? _config.Animations));
            SelectedReadingFontSize = ReadingFontSizeChoices.First(item => item.Value == (readingFontSize ?? _config.ReadingFontSize));
            SelectedReaderPostWidth = ReaderPostWidthChoices.First(item => item.Value == (readerPostWidth ?? _config.ReaderPostWidth));
            SelectedWorkspaceMode = WorkspaceChoices.First(item => item.Value == mode);
            SelectedLanguage = LanguageChoices.First(item => string.Equals(item.Code, language, StringComparison.OrdinalIgnoreCase));
            RefreshStatsHeaderGraphChoices(statsHeaderGraph ?? _config.StatsHeaderGraph);
            RefreshReaderChoiceLabels();
        }

        private void OnCancelButtonClick(object? sender, RoutedEventArgs e)
        {
            if (IsBusy || !IsAvailable || _isClosed) return;
            CancelChanges();
            Cancelled?.Invoke(this, EventArgs.Empty);
        }

        private async void OnForgetReadingPositionClick(object? sender, RoutedEventArgs e)
        {
            if (!CanForgetReadingPosition || _isClosed) return;
            IsBusy = true;
            SetErrorMessage(null);
            SetStatusMessage(null);
            try
            {
                await _forgetReadingPosition!();
                SetStatusMessage("resume.forgotten");
            }
            catch (Exception exception) { SetErrorMessage("resume.forgetFailed", exception.Message); }
            finally { IsBusy = false; }
        }

        private async void OnAboutButtonClick(object? sender, RoutedEventArgs e)
        {
            if (TopLevel.GetTopLevel(this) is Window owner)
                await new AboutWindow(Updates).ShowDialog(owner);
        }

        private async void OnBrowseButtonClick(object? sender, RoutedEventArgs e)
        {
            if (IsBusy || !IsAvailable || _isClosed)
            {
                return;
            }

            long pickerRevision = _draftRevision;
            bool restorePickerFocus = false;
            IsBusy = true;
            SetErrorMessage(null);
            SetStatusMessage(null);
            try
            {
                LocalFolderPickResult result = await LocalFolderPicker.PickAsync(
                    TopLevel.GetTopLevel(this)?.StorageProvider, Texts.T("settings.pickerTitle"));
                if (_isClosed || !IsAvailable || pickerRevision != _draftRevision) return;
                restorePickerFocus = true;
                switch (result.Status)
                {
                    case LocalFolderPickStatus.Selected: DownloadPath = result.Path!; break;
                    case LocalFolderPickStatus.Unavailable: SetErrorMessage("settings.pickerUnavailable"); break;
                    case LocalFolderPickStatus.NonLocal: SetErrorMessage("settings.pickerLocalOnly"); break;
                    case LocalFolderPickStatus.Failed: SetErrorMessage("settings.pickerFailed", result.Error); break;
                }
            }
            finally
            {
                IsBusy = false;
                if (restorePickerFocus && !_isClosed && IsEffectivelyVisible && IsAvailable)
                    BrowseButton.Focus();
            }
        }

        private async void OnSaveButtonClick(object? sender, RoutedEventArgs e)
        {
            if (IsBusy || !IsAvailable || _isClosed)
            {
                return;
            }

            IsBusy = true;
            SetErrorMessage(null);
            SetStatusMessage(null);
            try
            {
                if (!ValidateDestinationSyntax(out string? normalizedPath) || !ValidateNumericDraft() || !ValidateGalleryPlaybackDraft())
                    return;

                // Reveal before shortcut validation captures focus for its guarded
                // deferred error reveal. A valid draft keeps the current page.
                if (EffectiveShortcutBindings.Build(ShortcutsEditor.Draft).Issues.Any(issue => issue.BlocksSave))
                    SelectCategory("settings.shortcuts");
                if (!ShortcutsEditor.ValidateDraft()) return;
                Config candidate = _config.Clone();
                candidate.ShortcutBindings = ShortcutsEditor.Draft.Clone();
                candidate.DownloadPath = normalizedPath!;
                candidate.RecheckTimer = GetPositiveInteger(RecheckTimer, Texts.T("settings.recheckLabel"));
                candidate.MaxParallelDownloads = GetInteger(MaxParallelDownloads, Texts.T("settings.parallelLabel"), Config.MaxParallelDownloadsMinimum, Config.MaxParallelDownloadsMaximum);
                candidate.ParallelDownloadsSafetyLimit = GetInteger(ParallelDownloadsSafetyLimit, ParallelSafetyLabel, 1, Downloader.DownloadQueue.MaximumParallelDownloadsSafetyLimit);
                candidate.SaveUrlsOnExit = SaveUrlsOnExit;
                candidate.ShowBoardAdmissionWizard = ShowBoardAdmissionWizard;
                candidate.MinimizeToTray = MinimizeToTray;
                candidate.CloseToTray = CloseToTray;
                candidate.Theme = SelectedTheme?.Value ?? _config.Theme;
                candidate.Animations = SelectedAnimations?.Value ?? _config.Animations;
                candidate.StatsHeaderGraph = SelectedStatsHeaderGraph?.Value ?? _config.StatsHeaderGraph;
                candidate.Language = SelectedLanguage?.Code ?? _config.Language;
                candidate.WorkspaceMode = SelectedWorkspaceMode?.Value ?? _config.WorkspaceMode;
                if (CompactDensity != _initialCompactDensity)
                    candidate.CompactDensity = CompactDensity;
                candidate.SettingsBaselineCompactDensity = _initialCompactDensity;
                string selectedReadingFontSize = SelectedReadingFontSize?.Value ?? _config.ReadingFontSize;
                if (selectedReadingFontSize != _initialReadingFontSize)
                    candidate.ReadingFontSize = selectedReadingFontSize;
                candidate.SettingsBaselineReadingFontSize = _initialReadingFontSize;
                candidate.SettingsBaselineWorkspaceMode = _initialWorkspaceMode;
                candidate.ReaderPostWidth = SelectedReaderPostWidth?.Value ?? _config.ReaderPostWidth;
                candidate.VideoAutoplay = VideoAutoplay;
                candidate.VideoStartMuted = VideoStartMuted;
                candidate.VideoVolume = VideoVolume;
                candidate.SettingsBaselineVideoVolume = _initialVideoVolume;
                CaptureGalleryPlaybackDraft().ApplyTo(candidate);
                candidate.SettingsBaselineGalleryPlayback = _initialGalleryPlayback;
                candidate.ShowShortcutHelp = ShowShortcutHelp;
                candidate.RememberReadingPosition = RememberReadingPosition;
                candidate.MonitorClipboardUrls = MonitorClipboardUrls;
                candidate.AutomaticUpdateChecks = AutomaticUpdateChecks;
                candidate.ShowInformationNotifications = ShowInformationNotifications;
                candidate.ShowWarningNotifications = ShowWarningNotifications;
                candidate.ShowErrorNotifications = ShowErrorNotifications;
                candidate.IncludePrereleaseUpdates = IncludePrereleaseUpdates;
                candidate.MinimumFreeSpaceGiB = GetInteger(MinimumFreeSpaceGiB, ReserveLabel, Config.MinimumFreeSpaceGiBMinimum, Config.MinimumFreeSpaceGiBMaximum);
                candidate.PluginMaximumProductionWorkers = GetInteger(ProductionWorkers, ProductionWorkersLabel, 1, 8);
                candidate.PluginWorkerIdleMinutes = GetInteger(WorkerIdleMinutes, WorkerIdleMinutesLabel, 1, 30);
                candidate.PluginSourceHeapMiB = GetInteger(SourceHeapMiB, SourceHeapMiBLabel, 128, 1024);
                candidate.PluginFilterHeapMiB = GetInteger(FilterHeapMiB, FilterHeapMiBLabel, 256, 2048);
                candidate.BooruCatalogLimitMiB = GetInteger(BooruCatalogMiB, BooruCatalogMiBLabel, Config.BooruCatalogMinimumLimitMiB, Config.BooruCatalogMaximumLimitMiB);
                candidate.PrivateTagLimit = GetInteger(PrivateTagLimit, PrivateTagLimitLabel, Config.PrivateTagMinimumLimit, Config.PrivateTagMaximumLimit);
                candidate.PrivateTagFileLimitMiB = GetInteger(PrivateTagFileMiB, PrivateTagFileMiBLabel, Config.PrivateTagFileMinimumLimitMiB, Config.PrivateTagFileMaximumLimitMiB);
                candidate.MediaIndexCacheLimitMiB = GetInteger(MediaIndexCacheMiB, MediaIndexCacheMiBLabel, Config.MediaIndexCacheMinimumLimitMiB, Config.MediaIndexCacheMaximumLimitMiB);
                candidate.FavoriteEntryLimit = GetInteger(FavoriteEntryLimit, FavoriteEntryLimitLabel, Config.FavoriteEntryLimitMinimum, Config.FavoriteEntryLimitMaximum);
                candidate.FavoriteFileLimitMiB = GetInteger(FavoriteFileLimitMiB, FavoriteFileLimitMiBLabel, Config.FavoriteFileLimitMiBMinimum, Config.FavoriteFileLimitMiBMaximum);
                candidate.PostBookmarkLimit = GetInteger(PostBookmarkLimit, PostBookmarkLimitLabel, Config.PostBookmarkLimitMinimum, Config.PostBookmarkLimitMaximum);
                candidate.SavedSearchLimit = GetInteger(SavedSearchLimit, SavedSearchLimitLabel, Config.SavedSearchLimitMinimum, Config.SavedSearchLimitMaximum);
                candidate.ReaderGroupLimit = GetInteger(ReaderGroupLimit, ReaderGroupLimitLabel, Config.ReaderGroupLimitMinimum, Config.ReaderGroupLimitMaximum);
                candidate.SourcesWatchPreviewRows = GetInteger(SourcesWatchPreviewRows, SourcesWatchPreviewRowsLabel, Config.SourcesWatchPreviewRowsMinimum, Config.SourcesWatchPreviewRowsMaximum);
                candidate.ExportConfirmFiles = GetInteger(ExportConfirmFiles, ExportConfirmFilesLabel, Config.ExportConfirmFilesMinimum, Config.ExportConfirmFilesMaximum);
                candidate.ExportConfirmGiB = GetInteger(ExportConfirmGiB, ExportConfirmGiBLabel, Config.ExportConfirmGiBMinimum, Config.ExportConfirmGiBMaximum);
                candidate.PosterCacheLimitMiB = GetInteger(PosterCacheLimitMiB, PosterCacheLimitMiBLabel, Config.PosterCacheLimitMiBMinimum, Config.PosterCacheLimitMiBMaximum);
                candidate.PosterCacheFileLimit = GetInteger(PosterCacheFileLimit, PosterCacheFileLimitLabel, Config.PosterCacheFileLimitMinimum, Config.PosterCacheFileLimitMaximum);
                candidate.AudioProbeCacheLimitMiB = GetInteger(AudioProbeCacheLimitMiB, AudioProbeCacheLimitMiBLabel, Config.AudioProbeCacheLimitMiBMinimum, Config.AudioProbeCacheLimitMiBMaximum);
                candidate.AudioProbeCacheEntries = GetInteger(AudioProbeCacheEntries, AudioProbeCacheEntriesLabel, Config.AudioProbeCacheEntriesMinimum, Config.AudioProbeCacheEntriesMaximum);
                candidate.ThumbnailCacheTiles = GetInteger(ThumbnailCacheTiles, ThumbnailCacheTilesLabel, Config.ThumbnailCacheTilesMinimum, Config.ThumbnailCacheTilesMaximum);
                candidate.ClipboardUrlLimit = GetInteger(ClipboardUrlLimit, ClipboardUrlLimitLabel, Config.ClipboardUrlLimitMinimum, Config.ClipboardUrlLimitMaximum);
                candidate.ClipboardTextLimitKiB = GetInteger(ClipboardTextLimitKiB, ClipboardTextLimitKiBLabel, Config.ClipboardTextLimitKiBMinimum, Config.ClipboardTextLimitKiBMaximum);
                candidate.GalleryImageCacheMiB = GetInteger(GalleryImageCacheMiB, GalleryImageCacheMiBLabel, Config.GalleryImageCacheMiBMinimum, Config.GalleryImageCacheMiBMaximum);
                candidate.GalleryPreloadAhead = GetInteger(GalleryPreloadAhead, GalleryPreloadAheadLabel, Config.GalleryPreloadAheadMinimum, Config.GalleryPreloadAheadMaximum);
                candidate.GalleryPreloadBehind = GetInteger(GalleryPreloadBehind, GalleryPreloadBehindLabel, Config.GalleryPreloadBehindMinimum, Config.GalleryPreloadBehindMaximum);
                candidate.ImportMaxFilesPerBatch = GetInteger(ImportMaxFilesPerBatch, ImportMaxFilesPerBatchLabel, Config.ImportMaxFilesPerBatchMinimum, Config.ImportMaxFilesPerBatchMaximum);
                candidate.ImportMaxBatchGiB = GetInteger(ImportMaxBatchGiB, ImportMaxBatchGiBLabel, Config.ImportMaxBatchGiBMinimum, Config.ImportMaxBatchGiBMaximum);
                candidate.ImportMaxFileMiB = GetInteger(ImportMaxFileMiB, ImportMaxFileMiBLabel, Config.ImportMaxFileMiBMinimum, Config.ImportMaxFileMiBMaximum);
                candidate.ImportMaxTotalFiles = GetInteger(ImportMaxTotalFiles, ImportMaxTotalFilesLabel, Config.ImportMaxTotalFilesMinimum, Config.ImportMaxTotalFilesMaximum);
                candidate.ImportMaxTagsPerBatch = GetInteger(ImportMaxTagsPerBatch, ImportMaxTagsPerBatchLabel, Config.ImportMaxTagsPerBatchMinimum, Config.ImportMaxTagsPerBatchMaximum);
                candidate.ImportMaxBatches = GetInteger(ImportMaxBatches, ImportMaxBatchesLabel, Config.ImportMaxBatchesMinimum, Config.ImportMaxBatchesMaximum);
                candidate.MediaIndexMaxEntries = GetInteger(MediaIndexMaxEntries, MediaIndexMaxEntriesLabel, Config.MediaIndexMaxEntriesMinimum, Config.MediaIndexMaxEntriesMaximum);
                candidate.MediaIndexMaxThreads = GetInteger(MediaIndexMaxThreads, MediaIndexMaxThreadsLabel, Config.MediaIndexMaxThreadsMinimum, Config.MediaIndexMaxThreadsMaximum);
                candidate.ImportCatalogLimitMiB = GetInteger(ImportCatalogLimitMiB, ImportCatalogLimitMiBLabel, Config.ImportCatalogLimitMiBMinimum, Config.ImportCatalogLimitMiBMaximum);
                ApplyStatisticsDraft(candidate, _config);
                ApplyReaderDraft(candidate);

                if (!BandwidthLimiterMB.HasValue || BandwidthLimiterMB.Value <= 0)
                {
                    throw new ArgumentException(Texts.T("settings.bandwidthPositive"));
                }

                candidate.BandwidthLimiter = checked((long)(BandwidthLimiterMB.Value * 1024m * 1024m));
                try { candidate.Validate(); }
                catch (ArgumentException exception) when (exception.ParamName == nameof(Config.DownloadPath))
                {
                    SetDownloadPathError("settings.pathInvalid");
                    _firstInvalidInput = DownloadPathInput;
                    FinishFieldValidation();
                    return;
                }
                // Only changed target/reserve needs a new physical check. An unrelated
                // appearance save must not rewrite or "repair" existing job destinations.
                if (DestinationNeedsCheck(candidate) && !await CheckDestinationAsync(candidate.DownloadPath, candidate.MinimumFreeSpaceGiB))
                    return;
                if (_isClosed || !IsAvailable) return;
                if (_applySettings != null)
                {
                    await _applySettings(candidate);
                }
                else
                {
                    if (!candidate.RememberReadingPosition) await Task.Run(candidate.ForgetReadingPositionFile);
                    candidate.SaveConfig();
                    ShortcutService.Publish(candidate.ShortcutBindings);
                }

                if (_applySettings == null)
                    _config.CopyFrom(candidate);
                else
                    _config.CopySettingsFieldsFrom(candidate);
                _initialCompactDensity = _config.CompactDensity;
                _initialReadingFontSize = _config.ReadingFontSize;
                _initialWorkspaceMode = _config.WorkspaceMode;
                if (!_isClosed)
                {
                    CancelChangesAfterSave();
                    SetStatusMessage("settings.saved");
                    IsBusy = false;
                    Saved?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception exception)
            {
                ErrorDetails = exception.ToString();
                SetErrorMessage("settings.saveProblem");
                SetStatusMessage("settings.unsaved");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void CancelChangesAfterSave()
        {
            ShortcutsEditor.Load(_config.ShortcutBindings);
            DownloadPath = _config.DownloadPath;
            RecheckTimer = _config.RecheckTimer;
            MaxParallelDownloads = _config.MaxParallelDownloads;
            ParallelDownloadsSafetyLimit = _config.ParallelDownloadsSafetyLimit;
            BandwidthLimiterMB = _config.BandwidthLimiter / (1024m * 1024m);
            VideoAutoplay = _config.VideoAutoplay;
            VideoStartMuted = _config.VideoStartMuted;
            _initialVideoVolume = _config.VideoVolume;
            VideoVolume = _config.VideoVolume;
            ResetGalleryPlaybackDraft();
            ShowShortcutHelp = _config.ShowShortcutHelp;
            RememberReadingPosition = _config.RememberReadingPosition;
            MonitorClipboardUrls = _config.MonitorClipboardUrls;
            AutomaticUpdateChecks = _config.AutomaticUpdateChecks;
            ShowInformationNotifications = _config.ShowInformationNotifications;
            ShowWarningNotifications = _config.ShowWarningNotifications;
            ShowErrorNotifications = _config.ShowErrorNotifications;
            IncludePrereleaseUpdates = _config.IncludePrereleaseUpdates;
            MinimumFreeSpaceGiB = _config.MinimumFreeSpaceGiB;
            ProductionWorkers = _config.PluginMaximumProductionWorkers;
            WorkerIdleMinutes = _config.PluginWorkerIdleMinutes;
            SourceHeapMiB = _config.PluginSourceHeapMiB;
            FilterHeapMiB = _config.PluginFilterHeapMiB;
            BooruCatalogMiB = _config.BooruCatalogLimitMiB;
            PrivateTagLimit = _config.PrivateTagLimit;
            PrivateTagFileMiB = _config.PrivateTagFileLimitMiB;
            MediaIndexCacheMiB = _config.MediaIndexCacheLimitMiB;
            FavoriteEntryLimit = _config.FavoriteEntryLimit;
            FavoriteFileLimitMiB = _config.FavoriteFileLimitMiB;
            PostBookmarkLimit = _config.PostBookmarkLimit;
            SavedSearchLimit = _config.SavedSearchLimit;
            ReaderGroupLimit = _config.ReaderGroupLimit;
            SourcesWatchPreviewRows = _config.SourcesWatchPreviewRows;
            ExportConfirmFiles = _config.ExportConfirmFiles;
            ExportConfirmGiB = _config.ExportConfirmGiB;
            PosterCacheLimitMiB = _config.PosterCacheLimitMiB;
            PosterCacheFileLimit = _config.PosterCacheFileLimit;
            AudioProbeCacheLimitMiB = _config.AudioProbeCacheLimitMiB;
            AudioProbeCacheEntries = _config.AudioProbeCacheEntries;
            ThumbnailCacheTiles = _config.ThumbnailCacheTiles;
            ClipboardUrlLimit = _config.ClipboardUrlLimit;
            ClipboardTextLimitKiB = _config.ClipboardTextLimitKiB;
            GalleryImageCacheMiB = _config.GalleryImageCacheMiB;
            GalleryPreloadAhead = _config.GalleryPreloadAhead;
            GalleryPreloadBehind = _config.GalleryPreloadBehind;
            ImportMaxFilesPerBatch = _config.ImportMaxFilesPerBatch;
            ImportMaxBatchGiB = _config.ImportMaxBatchGiB;
            ImportMaxFileMiB = _config.ImportMaxFileMiB;
            ImportMaxTotalFiles = _config.ImportMaxTotalFiles;
            ImportMaxTagsPerBatch = _config.ImportMaxTagsPerBatch;
            ImportMaxBatches = _config.ImportMaxBatches;
            MediaIndexMaxEntries = _config.MediaIndexMaxEntries;
            MediaIndexMaxThreads = _config.MediaIndexMaxThreads;
            ImportCatalogLimitMiB = _config.ImportCatalogLimitMiB;

            LoadStatisticsDraft(_config);
            LoadReaderDraft(_config);
            RefreshChoices(_config.Theme, _config.WorkspaceMode, _config.Language);
            ResetNumber(RecheckTimerInput, RecheckTimer!.Value);
            ResetNumber(ParallelDownloadsInput, MaxParallelDownloads!.Value);
            ResetNumber(ParallelDownloadsSafetyInput, ParallelDownloadsSafetyLimit!.Value);
            ResetNumber(BandwidthInput, BandwidthLimiterMB!.Value);
            ResetNumber(ReserveInput, MinimumFreeSpaceGiB!.Value);
            ResetNumber(ProductionWorkersInput, ProductionWorkers!.Value);
            ResetNumber(WorkerIdleMinutesInput, WorkerIdleMinutes!.Value);
            ResetNumber(SourceHeapMiBInput, SourceHeapMiB!.Value);
            ResetNumber(FilterHeapMiBInput, FilterHeapMiB!.Value);
            ResetNumber(BooruCatalogMiBInput, BooruCatalogMiB!.Value);
            ResetNumber(PrivateTagLimitInput, PrivateTagLimit!.Value);
            ResetNumber(PrivateTagFileMiBInput, PrivateTagFileMiB!.Value);
            ResetNumber(MediaIndexCacheMiBInput, MediaIndexCacheMiB!.Value);
            ResetNumber(FavoriteEntryLimitInput, FavoriteEntryLimit!.Value);
            ResetNumber(FavoriteFileLimitMiBInput, FavoriteFileLimitMiB!.Value);
            ResetNumber(PostBookmarkLimitInput, PostBookmarkLimit!.Value);
            ResetNumber(SavedSearchLimitInput, SavedSearchLimit!.Value);
            ResetNumber(ReaderGroupLimitInput, ReaderGroupLimit!.Value);
            ResetNumber(SourcesWatchPreviewRowsInput, SourcesWatchPreviewRows!.Value);
            ResetNumber(ExportConfirmFilesInput, ExportConfirmFiles!.Value);
            ResetNumber(ExportConfirmGiBInput, ExportConfirmGiB!.Value);
            ResetNumber(PosterCacheLimitMiBInput, PosterCacheLimitMiB!.Value);
            ResetNumber(PosterCacheFileLimitInput, PosterCacheFileLimit!.Value);
            ResetNumber(AudioProbeCacheLimitMiBInput, AudioProbeCacheLimitMiB!.Value);
            ResetNumber(AudioProbeCacheEntriesInput, AudioProbeCacheEntries!.Value);
            ResetNumber(ThumbnailCacheTilesInput, ThumbnailCacheTiles!.Value);
            ResetNumber(ClipboardUrlLimitInput, ClipboardUrlLimit!.Value);
            ResetNumber(ClipboardTextLimitKiBInput, ClipboardTextLimitKiB!.Value);
            ResetNumber(GalleryImageCacheMiBInput, GalleryImageCacheMiB!.Value);
            ResetNumber(GalleryPreloadAheadInput, GalleryPreloadAhead!.Value);
            ResetNumber(GalleryPreloadBehindInput, GalleryPreloadBehind!.Value);
            ResetNumber(ImportMaxFilesPerBatchInput, ImportMaxFilesPerBatch!.Value);
            ResetNumber(ImportMaxBatchGiBInput, ImportMaxBatchGiB!.Value);
            ResetNumber(ImportMaxFileMiBInput, ImportMaxFileMiB!.Value);
            ResetNumber(ImportMaxTotalFilesInput, ImportMaxTotalFiles!.Value);
            ResetNumber(ImportMaxTagsPerBatchInput, ImportMaxTagsPerBatch!.Value);
            ResetNumber(ImportMaxBatchesInput, ImportMaxBatches!.Value);
            ResetNumber(MediaIndexMaxEntriesInput, MediaIndexMaxEntries!.Value);
            ResetNumber(MediaIndexMaxThreadsInput, MediaIndexMaxThreads!.Value);
            ResetNumber(ImportCatalogLimitMiBInput, ImportCatalogLimitMiB!.Value);
            ResetNumber(GallerySlideshowSecondsInput, GallerySlideshowSeconds!.Value);
            ResetGroupBaselines();

        }

        private void ClearGalleryPlaybackError()
        {
            GallerySlideshowSecondsInputError.IsVisible = false;
            GallerySlideshowSecondsInputError.Text = null;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GallerySlideshowSecondsHelp)));
        }

        internal static bool TryParseGalleryImageSeconds(string? text, NumberStyles style, IFormatProvider? format, out int seconds)
        {
            seconds = 0;
            if (!decimal.TryParse(text, style, format, out decimal number) || number < 1 || number > 300 || decimal.Truncate(number) != number)
                return false;
            seconds = decimal.ToInt32(number);
            return true;
        }

        private bool ValidateGalleryPlaybackDraft(bool focusError = true)
        {
            if (!IsGallerySlideshow) return true;
            if (TryParseGalleryImageSeconds(GallerySlideshowSecondsInput.Text, GallerySlideshowSecondsInput.ParsingNumberStyle,
                GallerySlideshowSecondsInput.NumberFormat, out _) && !DataValidationErrors.GetHasErrors(GallerySlideshowSecondsInput)) return true;
            GallerySlideshowSecondsInputError.Text = Texts.T("settings.fieldInteger", GallerySlideshowSecondsLabel, 1, 300);
            GallerySlideshowSecondsInputError.IsVisible = true;
            SweepGroups();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GallerySlideshowSecondsHelp)));
            _firstInvalidInput ??= GallerySlideshowSecondsInput;
            return FinishFieldValidation(focusError);
        }

        private GalleryPlaybackPreferences CaptureGalleryPlaybackDraft()
        {
            // Inactive slideshow controls cannot block an unrelated save with rejected hidden text.
            int seconds = TryParseGalleryImageSeconds(GallerySlideshowSecondsInput.Text, GallerySlideshowSecondsInput.ParsingNumberStyle,
                GallerySlideshowSecondsInput.NumberFormat, out int parsed) ? parsed : _initialGalleryPlayback.ImageSeconds;
            GalleryPlaybackMode mode = SelectedGalleryPlaybackMode?.Value switch
            {
                "Repeat" => GalleryPlaybackMode.Repeat,
                "Slideshow" => GalleryPlaybackMode.Slideshow,
                _ => GalleryPlaybackMode.Manual
            };
            return new(mode, seconds, GalleryMediaFocus, GalleryOverlayControls, GalleryAutoHideControls, GalleryOverlayPostContext);
        }

        private static int GetInteger(decimal? value, string label, int minimum, int maximum)
        {
            if (!value.HasValue || value < minimum || value > maximum || decimal.Truncate(value.Value) != value.Value)
                throw new ArgumentException(Texts.T("budgets.range", label, minimum, maximum));
            return decimal.ToInt32(value.Value);
        }

        private static int GetPositiveInteger(decimal? value, string label)
        {
            if (!value.HasValue || value.Value <= 0 || value.Value > int.MaxValue || decimal.Truncate(value.Value) != value.Value)
            {
                throw new ArgumentException(Texts.T("settings.positiveInteger", label));
            }

            return decimal.ToInt32(value.Value);
        }

        private void SetField<T>(ref T field, T value, string propertyName)
        {
            if (!EqualityComparer<T>.Default.Equals(field, value))
            {
                field = value;
                bool rebindingChoice = _refreshingLanguageLabels && propertyName is
                    nameof(SelectedTheme) or nameof(SelectedAnimations) or nameof(SelectedWorkspaceMode) or nameof(SelectedLanguage) or
                    nameof(SelectedReadingFontSize) or nameof(SelectedReaderPostWidth) or nameof(SelectedGalleryPlaybackMode) or nameof(SelectedStatsHeaderGraph) or nameof(SelectedReaderDateStyle) or nameof(SelectedReaderSpoilers) or nameof(SelectedGalleryFitUpscale);
                if (!rebindingChoice && propertyName is not (nameof(IsBusy) or nameof(ErrorMessage) or nameof(ErrorDetails) or nameof(StatusMessage)))
                {
                    _draftRevision++;
                    RetireSavedStatus();
                    _destinationWait?.Cancel();
                    _destinationStatus = null;
                    if (!_isClosed && _config != null)
                    {
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NormalizedDestination)));
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DestinationStatusText)));
                    }
                }
                if (!_isClosed)
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }

    public sealed record SettingsChoice(string Value, string Label);
}

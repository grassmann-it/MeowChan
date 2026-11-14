using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MeowChan.Models;
using MeowChan.Desktop;
using MeowChan.Persistence;
using Serilog;

namespace MeowChan
{
    public sealed class PresentationProfile
    {
        public bool CompactDensity { get; set; }
        public bool ArchiveMediaTiles { get; set; } = true;
        public string ReadingFontSize { get; set; } = "Normal";
        public string ArchiveThreadLayout { get; set; } = "List";
        public double ArchiveSplitWidth { get; set; } = 270;
        [JsonConverter(typeof(DesktopPreferenceColumnWidthsConverter))]
        public Dictionary<string, double> ColumnWidths { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, bool> ColumnVisibility { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> ColumnOrder { get; set; } = new(StringComparer.Ordinal);

        public PresentationProfile Clone() => new()
        {
            CompactDensity = CompactDensity, ArchiveMediaTiles = ArchiveMediaTiles,
            ReadingFontSize = ReadingFontSize, ArchiveThreadLayout = ArchiveThreadLayout,
            ArchiveSplitWidth = ArchiveSplitWidth,
            ColumnWidths = ColumnWidths == null ? new(StringComparer.Ordinal) : new(ColumnWidths, StringComparer.Ordinal),
            ColumnVisibility = ColumnVisibility == null ? new(StringComparer.Ordinal) : new(ColumnVisibility, StringComparer.Ordinal),
            ColumnOrder = ColumnOrder == null ? new(StringComparer.Ordinal) : new(ColumnOrder, StringComparer.Ordinal)
        };
    }

    /// <summary>Host-owned consent. Plugin settings and metadata cannot grant it.</summary>
    public sealed record SourceMediaIntegrityPermission(string PluginOwnerId, string SourceId,
        bool AllowProviderSizeMd5Mismatch, long Revision)
    {
        public static string CreateKey(string pluginOwnerId, string sourceId)
        {
            ValidateIdentity(pluginOwnerId, nameof(pluginOwnerId));
            ValidateIdentity(sourceId, nameof(sourceId));
            return Uri.EscapeDataString(pluginOwnerId) + "/" + Uri.EscapeDataString(sourceId);
        }

        internal static void ValidateIdentity(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
                throw new ArgumentException("A bounded source/owner identity is required.", name);
        }
    }

    /// <summary>What a settings load that failed fell back to.</summary>
    internal enum SettingsLoadProblem { None, RecoveredFromBackup, DefaultsInUse }
    internal enum ProfileOrigin { Unbound, ConfirmedNew, ExistingValid, BackupRecovery, ExistingMissingSettings, Damaged, Unsupported }

    public class Config : IJsonOnDeserialized
    {
        private string? _profileDirectory;
        private ProfileLease? _writeLease;
        [JsonIgnore] internal ProfileOrigin Origin { get; private set; }
        [JsonIgnore] internal bool AllowsOrdinaryWrites => Origin is ProfileOrigin.Unbound or ProfileOrigin.ConfirmedNew or ProfileOrigin.ExistingValid;
        internal void EnsureOrdinaryWritesAllowed()
        {
            if (!AllowsOrdinaryWrites) throw new InvalidOperationException($"Settings recovery requires an explicit recovery decision ({Origin}); existing files were preserved.");
        }
        private string _relativePathBaseDirectory = Path.GetFullPath(Environment.CurrentDirectory);
        [JsonIgnore] internal string DownloadPathBaseDirectory => _relativePathBaseDirectory;
        private string _theme = "Dark";
        private string _animations = "System";
        private string _language = "en";
        private string _workspaceMode = "Barebones";
        private int _videoVolume = 70;
        private string _galleryPlaybackMode = "Manual";
        private int _gallerySlideshowSeconds = 5;
        private string _gallerySlideshowVideoRule = nameof(Desktop.SlideshowVideoRule.PlayOnce);
        private int _gallerySlideshowVideoSeconds = GallerySlideshowVideoSecondsDefault;
        private int _gallerySlideshowVideoRepeats = GallerySlideshowVideoRepeatsDefault;
        private double _windowWidth = 1380;
        private double _windowHeight = 850;
        private double? _windowPositionX;
        private double? _windowPositionY;
        private Dictionary<string, PresentationProfile> _layoutProfiles = new(StringComparer.Ordinal);
        private Dictionary<string, PresentationProfile>? _deserializedLayoutProfiles;
        private string _lastExportDirectory = string.Empty;
        private string _galleryFrameDirectory = string.Empty;

        public Desktop.Commands.ShortcutBindingsSection ShortcutBindings { get; set; } = new();
        public int SchemaVersion { get; set; } = 1;
        public bool FirstRunSetupPending { get; set; }
        public int FirstRunSetupVersion { get; set; }
        public Dictionary<string, SourceMediaIntegrityPermission> SourceMediaIntegrityPermissions { get; set; } = new(StringComparer.Ordinal);
        public string DownloadPath { get; set; } = GetDefaultDownloadPath();
        public int RecheckTimer { get; set; } = 60;
        public bool SaveUrlsOnExit { get; set; } = true;
        public bool ShowBoardAdmissionWizard { get; set; } = true;
        /// <summary>
        /// One-time notice (plan AP5 question 3): a profile that existed before the work views reduced every page is told once that
        /// Barebones and Simple now show fewer tools. Loading a stored profile whose file lacks the field turns it on; a new
        /// profile and every in-memory configuration start false, and dismissing or acting on the notice saves false.
        /// </summary>
        public bool WorkViewNoticePending { get; set; }
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool RememberReadingPosition { get; set; }
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool MonitorClipboardUrls { get; set; }
        public bool DownloadsPaused { get; set; }
        public bool MinimizeToTray { get; set; }
        public bool CloseToTray { get; set; }
        /// <summary>Top speed of all downloads together in bytes per second. Edited in MiB/s; the maximum is a sanity bound far above any link.</summary>
        public long BandwidthLimiter { get; set; } = 1024 * 1024;
        internal const int BandwidthLimiterMaximumMiB = 100_000;
        internal const long BandwidthLimiterMaximumBytes = BandwidthLimiterMaximumMiB * 1024L * 1024L;
        /// <summary>Requested simultaneous media transfers. The effective value is the smaller of this and <see cref="ParallelDownloadsSafetyLimit"/>, so the maximum is that limit's ceiling.</summary>
        public int MaxParallelDownloads { get; set; } = 5;
        internal const int MaxParallelDownloadsMinimum = 1;
        internal const int MaxParallelDownloadsMaximum = Downloader.DownloadQueue.MaximumParallelDownloadsSafetyLimit;
        public int ParallelDownloadsSafetyLimit { get; set; } = Downloader.DownloadQueue.ProtectiveParallelDownloadsCeiling;
        [JsonConverter(typeof(DesktopPreferenceTrueBooleanConverter))]
        public bool AutomaticUpdateChecks { get; set; } = true;
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool IncludePrereleaseUpdates { get; set; }
        [JsonConverter(typeof(DesktopPreferenceTrueBooleanConverter))]
        public bool ShowInformationNotifications { get; set; } = true;
        [JsonConverter(typeof(DesktopPreferenceTrueBooleanConverter))]
        public bool ShowWarningNotifications { get; set; } = true;
        [JsonConverter(typeof(DesktopPreferenceTrueBooleanConverter))]
        public bool ShowErrorNotifications { get; set; } = true;
        /// <summary>Downloads wait while a target has less free space than this. The maximum (64 TiB) keeps a typo from silencing every download.</summary>
        public int MinimumFreeSpaceGiB { get; set; } = 1;
        internal const int MinimumFreeSpaceGiBMinimum = 0;
        internal const int MinimumFreeSpaceGiBMaximum = 65_536;
        public int PluginMaximumProductionWorkers { get; set; } = 4;
        public int PluginWorkerIdleMinutes { get; set; } = 5;
        public int PluginSourceHeapMiB { get; set; } = 256;
        public int PluginFilterHeapMiB { get; set; } = 512;
        /// <summary>
        /// Write limit of the local Danbooru catalog (provider claims, findings, watchers) in MiB. Lowering it below the
        /// current size keeps the catalog readable and only stops it from growing.
        /// </summary>
        public int BooruCatalogLimitMiB { get; set; } = BooruCatalogDefaultLimitMiB;
        internal const int BooruCatalogDefaultLimitMiB = Archive.BooruCatalogStore.DefaultDocumentLimitBytes / (1024 * 1024);
        internal const int BooruCatalogMinimumLimitMiB = Archive.BooruCatalogStore.MinimumDocumentLimitBytes / (1024 * 1024);
        internal const int BooruCatalogMaximumLimitMiB = Archive.BooruCatalogStore.MaximumDocumentBytes / (1024 * 1024);
        /// <summary>
        /// Most own tags the profile may define. Lowering it below the current number keeps every tag readable and
        /// usable; only new tags are refused until the count is below the limit again.
        /// </summary>
        public int PrivateTagLimit { get; set; } = Tags.PrivateTagLimits.DefaultTagLimit;
        internal const int PrivateTagMinimumLimit = Tags.PrivateTagLimits.MinimumTagLimit;
        internal const int PrivateTagMaximumLimit = Tags.PrivateTagLimits.MaximumTagLimit;
        /// <summary>
        /// Write limit of private-tags.json (own tags and their assignments) in MiB. Lowering it below the current size
        /// keeps the file readable; only changes that would make it larger are refused.
        /// </summary>
        public int PrivateTagFileLimitMiB { get; set; } = PrivateTagFileDefaultLimitMiB;
        internal const int PrivateTagFileDefaultLimitMiB = Tags.PrivateTagLimits.DefaultDocumentLimitBytes / (1024 * 1024);
        internal const int PrivateTagFileMinimumLimitMiB = Tags.PrivateTagLimits.MinimumDocumentLimitBytes / (1024 * 1024);
        internal const int PrivateTagFileMaximumLimitMiB = Tags.PrivateTagLimits.MaximumDocumentLimitBytes / (1024 * 1024);
        /// <summary>
        /// Write limit of the saved media page index (cache/media-index-v1.json) in MiB. A larger index is not saved and the
        /// page says so; lowering the limit deletes nothing and a larger saved index stays readable up to the fixed ceiling.
        /// </summary>
        public int MediaIndexCacheLimitMiB { get; set; } = MediaIndexCacheDefaultLimitMiB;
        internal const int MediaIndexCacheDefaultLimitMiB = (int)(Gallery.MediaLibraryIndexCache.DefaultLimitBytes / (1024 * 1024));
        internal const int MediaIndexCacheMinimumLimitMiB = (int)(Gallery.MediaLibraryIndexCache.MinimumLimitBytes / (1024 * 1024));
        internal const int MediaIndexCacheMaximumLimitMiB = (int)(Gallery.MediaLibraryIndexCache.MaximumBytes / (1024 * 1024));
        /// <summary>Most entries each favorite and rating list (favorite media, favorite threads, ratings) may hold. Lowering it below the current number keeps every entry readable; only new entries are refused until the count is below the limit again.</summary>
        public int FavoriteEntryLimit { get; set; } = FavoriteEntryLimitDefault;
        internal const int FavoriteEntryLimitDefault = Persistence.ProfileStoreLimits.DefaultFavoriteEntries;
        internal const int FavoriteEntryLimitMinimum = Persistence.ProfileStoreLimits.MinimumFavoriteEntries;
        internal const int FavoriteEntryLimitMaximum = Persistence.ProfileStoreLimits.FavoriteEntryCeiling;
        /// <summary>Write limit of each favorite and rating file in MiB. Lowering it below the current size keeps the file readable; only writes that would make it larger are refused.</summary>
        public int FavoriteFileLimitMiB { get; set; } = FavoriteFileLimitMiBDefault;
        internal const int FavoriteFileLimitMiBDefault = Persistence.ProfileStoreLimits.DefaultFavoriteFileBytes / (1024 * 1024);
        internal const int FavoriteFileLimitMiBMinimum = Persistence.ProfileStoreLimits.MinimumFavoriteFileBytes / (1024 * 1024);
        internal const int FavoriteFileLimitMiBMaximum = Persistence.ProfileStoreLimits.FavoriteFileCeilingBytes / (1024 * 1024);
        /// <summary>Most post bookmarks. Lowering it below the current number keeps every bookmark; only new bookmarks are refused. Note length and file size stay fixed technical limits.</summary>
        public int PostBookmarkLimit { get; set; } = PostBookmarkLimitDefault;
        internal const int PostBookmarkLimitDefault = Persistence.ProfileStoreLimits.DefaultBookmarks;
        internal const int PostBookmarkLimitMinimum = Persistence.ProfileStoreLimits.MinimumBookmarks;
        internal const int PostBookmarkLimitMaximum = Persistence.ProfileStoreLimits.BookmarkCeiling;
        /// <summary>Most saved archive searches. Lowering it below the current number keeps every search; only new searches are refused.</summary>
        public int SavedSearchLimit { get; set; } = SavedSearchLimitDefault;
        internal const int SavedSearchLimitDefault = Persistence.ProfileStoreLimits.DefaultSavedSearches;
        internal const int SavedSearchLimitMinimum = Persistence.ProfileStoreLimits.MinimumSavedSearches;
        internal const int SavedSearchLimitMaximum = Persistence.ProfileStoreLimits.SavedSearchCeiling;
        /// <summary>Most reader groups. Lowering it below the current number keeps every group; only new groups are refused. Members per group, memberships and file size stay fixed technical limits.</summary>
        public int ReaderGroupLimit { get; set; } = ReaderGroupLimitDefault;
        internal const int ReaderGroupLimitDefault = Persistence.ProfileStoreLimits.DefaultReaderGroups;
        internal const int ReaderGroupLimitMinimum = Persistence.ProfileStoreLimits.MinimumReaderGroups;
        internal const int ReaderGroupLimitMaximum = Persistence.ProfileStoreLimits.ReaderGroupCeiling;
        /// <summary>Rows in a source's watch list on the Sources page before "Show all in Downloads" takes over. Every watch stays reachable under Downloads.</summary>
        public int SourcesWatchPreviewRows { get; set; } = SourcesWatchPreviewRowsDefault;
        internal const int SourcesWatchPreviewRowsDefault = 8;
        internal const int SourcesWatchPreviewRowsMinimum = 3;
        internal const int SourcesWatchPreviewRowsMaximum = 200;
        /// <summary>File count above which "Export all matches" on the Media page asks before it starts. A confirmed export is never cut short.</summary>
        public int ExportConfirmFiles { get; set; } = ExportConfirmFilesDefault;
        internal const int ExportConfirmFilesDefault = Gallery.MediaLibraryExportSize.ConfirmAboveFiles;
        internal const int ExportConfirmFilesMinimum = 10;
        internal const int ExportConfirmFilesMaximum = 100_000;
        /// <summary>Total size in GiB above which "Export all matches" on the Media page asks before it starts.</summary>
        public int ExportConfirmGiB { get; set; } = ExportConfirmGiBDefault;
        internal const int ExportConfirmGiBDefault = (int)(Gallery.MediaLibraryExportSize.ConfirmAboveBytes / (1024L * 1024 * 1024));
        internal const int ExportConfirmGiBMinimum = 1;
        internal const int ExportConfirmGiBMaximum = 1024;
        /// <summary>Size limit of the video preview cache in MiB. Previews are derived files: when the cache is full the oldest are removed and recreated when needed. A single preview (4 MiB) and the video size that gets one (512 MiB) are fixed technical limits.</summary>
        public int PosterCacheLimitMiB { get; set; } = PosterCacheLimitMiBDefault;
        internal const int PosterCacheLimitMiBDefault = (int)(Desktop.VideoPosterCache.DefaultCacheBytes / (1024 * 1024));
        internal const int PosterCacheLimitMiBMinimum = 16;
        internal const int PosterCacheLimitMiBMaximum = 8192;
        /// <summary>Most preview files the video preview cache keeps.</summary>
        public int PosterCacheFileLimit { get; set; } = PosterCacheFileLimitDefault;
        internal const int PosterCacheFileLimitDefault = Desktop.VideoPosterCache.DefaultCacheFiles;
        internal const int PosterCacheFileLimitMinimum = 32;
        internal const int PosterCacheFileLimitMaximum = 50_000;
        /// <summary>Size limit of the saved sound check of videos (cache/media-audio-v1.json) in MiB. It is derived data: above the limit only the entries that fit are saved and the rest is checked again when needed. A larger saved file stays readable up to the fixed ceiling.</summary>
        public int AudioProbeCacheLimitMiB { get; set; } = AudioProbeCacheLimitMiBDefault;
        internal const int AudioProbeCacheLimitMiBDefault = (int)(Gallery.MediaAudioCache.DefaultLimitBytes / (1024 * 1024));
        internal const int AudioProbeCacheLimitMiBMinimum = (int)(Gallery.MediaAudioCache.MinimumLimitBytes / (1024 * 1024));
        internal const int AudioProbeCacheLimitMiBMaximum = (int)(Gallery.MediaAudioCache.MaximumBytes / (1024 * 1024));
        /// <summary>Most videos (distinct files) the saved sound check keeps.</summary>
        public int AudioProbeCacheEntries { get; set; } = AudioProbeCacheEntriesDefault;
        internal const int AudioProbeCacheEntriesDefault = Gallery.MediaAudioCache.DefaultEntryLimit;
        internal const int AudioProbeCacheEntriesMinimum = Gallery.MediaAudioCache.MinimumEntryLimit;
        internal const int AudioProbeCacheEntriesMaximum = Gallery.MediaAudioCache.EntryCeiling;
        /// <summary>How many decoded tile previews of the Media page stay in memory (up to about 0.17 MiB each, a video poster 0.33 MiB), so scrolling back shows them at once.</summary>
        public int ThumbnailCacheTiles { get; set; } = ThumbnailCacheTilesDefault;
        internal const int ThumbnailCacheTilesDefault = Desktop.MediaThumbnailCache.DefaultCapacity;
        internal const int ThumbnailCacheTilesMinimum = 32;
        internal const int ThumbnailCacheTilesMaximum = 8192;
        /// <summary>Most URLs of one clipboard text that clipboard monitoring checks and offers; further URLs are skipped and the suggestion says so.</summary>
        public int ClipboardUrlLimit { get; set; } = ClipboardUrlLimitDefault;
        internal const int ClipboardUrlLimitDefault = Desktop.ClipboardUrlMonitor.MaximumUrls;
        internal const int ClipboardUrlLimitMinimum = Desktop.ClipboardUrlMonitor.MinimumUrlLimit;
        internal const int ClipboardUrlLimitMaximum = Desktop.ClipboardUrlMonitor.MaximumUrlLimit;
        /// <summary>Largest clipboard text, in KiB, that clipboard monitoring searches for URLs; a larger text is not searched and the suggestion says so.</summary>
        public int ClipboardTextLimitKiB { get; set; } = ClipboardTextLimitKiBDefault;
        internal const int ClipboardTextLimitKiBDefault = Desktop.ClipboardUrlMonitor.MaximumTextLength / 1024;
        internal const int ClipboardTextLimitKiBMinimum = Desktop.ClipboardUrlMonitor.MinimumTextLimitKiB;
        internal const int ClipboardTextLimitKiBMaximum = Desktop.ClipboardUrlMonitor.MaximumTextLimitKiB;

        // Network and disk statistics (Desktop/Stats). Every number is a setting with a default, a minimum and a maximum;
        // the fixed technical ceilings (tray tooltip text 120 characters, io-stats.json 16 MiB when read) are documented only.
        /// <summary>Sampling interval of the network and disk counters in milliseconds while the window is visible.</summary>
        public int StatsSampleIntervalMs { get; set; } = StatsSampleIntervalMsDefault;
        internal const int StatsSampleIntervalMsDefault = 1000, StatsSampleIntervalMsMinimum = 500, StatsSampleIntervalMsMaximum = 10_000;
        /// <summary>How many samples the statistics graph keeps (its width in samples); the time span is this times the interval.</summary>
        public int StatsHistorySamples { get; set; } = StatsHistorySamplesDefault;
        internal const int StatsHistorySamplesDefault = 120, StatsHistorySamplesMinimum = 30, StatsHistorySamplesMaximum = 3600;
        /// <summary>Sampling interval in milliseconds while the window is hidden, downloads are active and the tray tooltip shows statistics. Nothing is sampled while hidden and idle.</summary>
        public int StatsBackgroundIntervalMs { get; set; } = StatsBackgroundIntervalMsDefault;
        internal const int StatsBackgroundIntervalMsDefault = 5000, StatsBackgroundIntervalMsMinimum = 2000, StatsBackgroundIntervalMsMaximum = 60_000;
        /// <summary>Whether the tray tooltip shows the current rates.</summary>
        [JsonConverter(typeof(DesktopPreferenceTrueBooleanConverter))]
        public bool StatsTrayTooltip { get; set; } = true;
        /// <summary>Whether the tray menu starts with two statistics lines.</summary>
        [JsonConverter(typeof(DesktopPreferenceTrueBooleanConverter))]
        public bool StatsTrayMenu { get; set; } = true;
        /// <summary>Whether the per-day totals are kept in the profile (io-stats.json) so "today" and the retention total survive a restart.</summary>
        [JsonConverter(typeof(DesktopPreferenceTrueBooleanConverter))]
        public bool StatsPersist { get; set; } = true;
        /// <summary>How many days of totals are kept (today included); older days are dropped when the statistics load and at midnight.</summary>
        public int StatsRetentionDays { get; set; } = StatsRetentionDaysDefault;
        internal const int StatsRetentionDaysDefault = 30, StatsRetentionDaysMinimum = 1, StatsRetentionDaysMaximum = 365;
        /// <summary>How often changed totals are written to io-stats.json, in minutes (they are also written at exit).</summary>
        public int StatsPersistIntervalMinutes { get; set; } = StatsPersistIntervalMinutesDefault;
        internal const int StatsPersistIntervalMinutesDefault = 5, StatsPersistIntervalMinutesMinimum = 1, StatsPersistIntervalMinutesMaximum = 60;
        /// <summary>Most sources (rows and keys per day); further sources are summed into one "other" row.</summary>
        public int StatsMaxSources { get; set; } = StatsMaxSourcesDefault;
        internal const int StatsMaxSourcesDefault = 64, StatsMaxSourcesMinimum = 8, StatsMaxSourcesMaximum = 256;
        /// <summary>What the graph in the header shows: "Auto", "Network", "Disk", "NetworkAndDisk" (both stacked), "SideBySide" or "Text" (see IoStatsDisplay.ResolveHeaderLayout). The statistics page always shows both.</summary>
        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string StatsHeaderGraph
        {
            get => _statsHeaderGraph;
            set => _statsHeaderGraph = Desktop.Stats.IoStatsDisplay.NormalizeHeaderGraph(value);
        }
        private string _statsHeaderGraph = Desktop.Stats.IoStatsDisplay.HeaderGraphAuto;
        /// <summary>Memory in MiB for the decoded images of the gallery (decoded at display size), so stepping back and forth shows them at once; 0 keeps nothing.</summary>
        public int GalleryImageCacheMiB { get; set; } = GalleryImageCacheMiBDefault;
        internal const int GalleryImageCacheMiBDefault = (int)(Desktop.GalleryImageCache.DefaultBudgetBytes / (1024 * 1024));
        internal const int GalleryImageCacheMiBMinimum = 0;
        internal const int GalleryImageCacheMiBMaximum = 4096;
        /// <summary>How many images after the shown one the gallery decodes in the background; 0 turns it off.</summary>
        public int GalleryPreloadAhead { get; set; } = GalleryPreloadAheadDefault;
        internal const int GalleryPreloadAheadDefault = Desktop.GalleryImageCache.DefaultPreloadAhead;
        internal const int GalleryPreloadAheadMinimum = 0;
        internal const int GalleryPreloadAheadMaximum = 20;
        /// <summary>How many images before the shown one the gallery decodes in the background; 0 turns it off.</summary>
        public int GalleryPreloadBehind { get; set; } = GalleryPreloadBehindDefault;
        internal const int GalleryPreloadBehindDefault = Desktop.GalleryImageCache.DefaultPreloadBehind;
        internal const int GalleryPreloadBehindMinimum = 0;
        internal const int GalleryPreloadBehindMaximum = 20;

        // Reader P1: persisted values share the component limits, keep old defaults and are applied only after save.
        // Reading clamps these preference values; a newly edited candidate must pass Validate without coercion.
        public int ReaderThumbnailWidth { get; set; } = ReaderThumbnailWidthDefault;
        internal const int ReaderThumbnailWidthDefault = ReaderDisplayPreferences.ReaderThumbnailWidthDefault;
        internal const int ReaderThumbnailWidthMinimum = ReaderDisplayPreferences.ReaderThumbnailWidthMinimum;
        internal const int ReaderThumbnailWidthMaximum = ReaderDisplayPreferences.ReaderThumbnailWidthMaximum;
        public int ReaderFoldLines { get; set; } = ReaderFoldLinesDefault;
        internal const int ReaderFoldLinesDefault = ReaderDisplayLimits.FoldLinesDefault;
        internal const int ReaderFoldLinesMinimum = ReaderDisplayLimits.FoldLinesMinimum;
        internal const int ReaderFoldLinesMaximum = ReaderDisplayLimits.FoldLinesMaximum;
        public int ReaderPreviewLines { get; set; } = ReaderPreviewLinesDefault;
        internal const int ReaderPreviewLinesDefault = ReaderDisplayLimits.PreviewLinesDefault;
        internal const int ReaderPreviewLinesMinimum = ReaderDisplayLimits.PreviewLinesMinimum;
        internal const int ReaderPreviewLinesMaximum = ReaderDisplayLimits.PreviewLinesMaximum;
        public int ReaderPreviewDelayMs { get; set; } = ReaderPreviewDelayMsDefault;
        internal const int ReaderPreviewDelayMsDefault = ReaderDisplayLimits.PreviewDelayDefault;
        internal const int ReaderPreviewDelayMsMinimum = ReaderDisplayLimits.PreviewDelayMinimum;
        internal const int ReaderPreviewDelayMsMaximum = ReaderDisplayLimits.PreviewDelayMaximum;
        public int ReaderReplyStep { get; set; } = ReaderReplyStepDefault;
        internal const int ReaderReplyStepDefault = ReaderDisplayLimits.ReplyStepDefault;
        internal const int ReaderReplyStepMinimum = ReaderDisplayLimits.ReplyStepMinimum;
        internal const int ReaderReplyStepMaximum = ReaderDisplayLimits.ReplyStepMaximum;
        public int ReaderReplyLines { get; set; } = ReaderReplyLinesDefault;
        internal const int ReaderReplyLinesDefault = ReaderDisplayLimits.ReplyLinesDefault;
        internal const int ReaderReplyLinesMinimum = ReaderDisplayLimits.ReplyLinesMinimum;
        internal const int ReaderReplyLinesMaximum = ReaderDisplayLimits.ReplyLinesMaximum;
        public int ReaderPageSize { get; set; } = ReaderPageSizeDefault;
        internal const int ReaderPageSizeDefault = ArchivePage.ReaderPageSizeDefault;
        internal const int ReaderPageSizeMinimum = ArchivePage.ReaderPageSizeMinimum;
        internal const int ReaderPageSizeMaximum = ArchivePage.ReaderPageSizeMaximum;
        public int ReaderThreadingDepth { get; set; } = ReaderThreadingDepthDefault;
        internal const int ReaderThreadingDepthDefault = ReaderConversationTree.DepthDefault;
        internal const int ReaderThreadingDepthMinimum = ReaderConversationTree.DepthMinimum;
        internal const int ReaderThreadingDepthMaximum = ReaderConversationTree.DepthMaximum;
        public int GalleryFitMaxUpscalePercent { get; set; } = GalleryFitMaxUpscalePercentDefault;
        internal const int GalleryFitMaxUpscalePercentDefault = GalleryFitPreferences.MaxUpscalePercentDefault;
        internal const int GalleryFitMaxUpscalePercentMinimum = GalleryFitPreferences.MaxUpscalePercentMinimum;
        internal const int GalleryFitMaxUpscalePercentMaximum = GalleryFitPreferences.MaxUpscalePercentMaximum;
        public int PostFilterRuleLimit { get; set; } = PostFilterRuleLimitDefault;
        internal const int PostFilterRuleLimitDefault = ProfileStoreLimits.DefaultPostFilterRules;
        internal const int PostFilterRuleLimitMinimum = ProfileStoreLimits.MinimumPostFilterRules;
        internal const int PostFilterRuleLimitMaximum = ProfileStoreLimits.PostFilterRuleCeiling;
        public int HiddenPostLimit { get; set; } = HiddenPostLimitDefault;
        internal const int HiddenPostLimitDefault = ProfileStoreLimits.DefaultHiddenPosts;
        internal const int HiddenPostLimitMinimum = ProfileStoreLimits.MinimumHiddenPosts;
        internal const int HiddenPostLimitMaximum = ProfileStoreLimits.HiddenPostCeiling;
        private string _readerDateStyle = "System", _readerSpoilers = "Hide";
        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string ReaderDateStyle
        {
            get => _readerDateStyle;
            set => _readerDateStyle = Enum.TryParse(value?.Trim(), true, out Desktop.ReaderDateStyle style) && Enum.IsDefined(style)
                ? style.ToString() : "System";
        }
        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string ReaderSpoilers
        {
            get => _readerSpoilers;
            set => _readerSpoilers = Enum.TryParse(value?.Trim(), true, out ReaderSpoilerMode mode) && Enum.IsDefined(mode)
                ? mode.ToString() : "Hide";
        }
        // The order of the media page (MediaSortPreference): a criterion name and "Default"/"Reversed". The random order is not kept.
        private string _mediaSort = MediaSortPreference.Format(MediaSortPreference.DefaultSort), _mediaSortDirection = "Default";
        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string MediaSort
        {
            get => _mediaSort;
            set => _mediaSort = MediaSortPreference.Format(MediaSortPreference.ParseSort(value));
        }
        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string MediaSortDirection
        {
            get => _mediaSortDirection;
            set => _mediaSortDirection = MediaSortPreference.ParseDirection(value).ToString();
        }
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool GalleryFitUpscaleLimited { get; set; }

        // Manual import limits (contract manual-import-v1 §4). Every limit is a setting; the stores enforce technical ceilings.
        /// <summary>Most files (new and already present) one import batch may hold.</summary>
        public int ImportMaxFilesPerBatch { get; set; } = Import.ManualImportLimits.DefaultMaximumFiles;
        /// <summary>Most bytes one import batch may hold, in GiB.</summary>
        public int ImportMaxBatchGiB { get; set; } = (int)(Import.ManualImportLimits.DefaultMaximumBatchBytes / (1024L * 1024 * 1024));
        /// <summary>Largest single file an import accepts, in MiB.</summary>
        public int ImportMaxFileMiB { get; set; } = (int)(Import.ManualImportLimits.DefaultMaximumFileBytes / (1024L * 1024));
        /// <summary>Most imported files over all batches.</summary>
        public int ImportMaxTotalFiles { get; set; } = Import.ManualImportCatalogStore.DefaultMaximumTotalItems;
        /// <summary>Most own tags one import batch may carry.</summary>
        public int ImportMaxTagsPerBatch { get; set; } = Import.ManualImportCatalogStore.DefaultMaximumTags;
        /// <summary>Most import batches over time; lowering it only stops new batches.</summary>
        public int ImportMaxBatches { get; set; } = Import.ManualImportCatalogStore.DefaultMaximumBatches;
        /// <summary>Most entries the saved media index may hold; above it the index is rebuilt at every start instead of saved.</summary>
        public int MediaIndexMaxEntries { get; set; } = Gallery.MediaLibraryIndexCache.DefaultEntryLimit;
        /// <summary>Most threads the saved media index may hold.</summary>
        public int MediaIndexMaxThreads { get; set; } = Gallery.MediaLibraryIndexCache.DefaultThreadLimit;
        /// <summary>Write limit of the import catalog file in MiB; lowering it only stops growth.</summary>
        public int ImportCatalogLimitMiB { get; set; } = Import.ManualImportCatalogStore.DefaultDocumentLimitBytes / (1024 * 1024);
        internal const int ImportMaxFilesPerBatchMinimum = 1, ImportMaxFilesPerBatchMaximum = Import.ManualImportCatalogStore.MaximumItemsPerBatch;
        internal const int ImportMaxBatchGiBMinimum = 1, ImportMaxBatchGiBMaximum = 16 * 1024;
        internal const int ImportMaxFileMiBMinimum = 1, ImportMaxFileMiBMaximum = 1024 * 1024;
        internal const int ImportMaxTotalFilesMinimum = 1, ImportMaxTotalFilesMaximum = Import.ManualImportCatalogStore.MaximumTotalItemsCeiling;
        internal const int ImportMaxTagsPerBatchMinimum = 1, ImportMaxTagsPerBatchMaximum = Import.ManualImportCatalogStore.MaximumTagsCeiling;
        internal const int ImportMaxBatchesMinimum = Import.ManualImportCatalogStore.MinimumMaximumBatches, ImportMaxBatchesMaximum = Import.ManualImportCatalogStore.MaximumBatchesCeiling;
        internal const int MediaIndexMaxEntriesMinimum = Gallery.MediaLibraryIndexCache.MinimumEntryLimit, MediaIndexMaxEntriesMaximum = Gallery.MediaLibraryIndexCache.EntryCeiling;
        internal const int MediaIndexMaxThreadsMinimum = Gallery.MediaLibraryIndexCache.MinimumThreadLimit, MediaIndexMaxThreadsMaximum = Gallery.MediaLibraryIndexCache.ThreadCeiling;
        internal const int ImportCatalogLimitMiBMinimum = 1, ImportCatalogLimitMiBMaximum = Import.ManualImportCatalogStore.MaximumDocumentBytes / (1024 * 1024);

        internal Import.ManualImportLimits ImportLimits => new(ImportMaxFilesPerBatch, ImportMaxBatchGiB * 1024L * 1024 * 1024,
            ImportMaxFileMiB * 1024L * 1024);

        public string? PluginEditorExecutable { get; set; }
        public string[] PluginEditorArguments { get; set; } = ["{file}"];


        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string Theme
        {
            get => _theme;
            set => _theme = string.Equals(value?.Trim(), "Light", StringComparison.OrdinalIgnoreCase) ? "Light"
                : string.Equals(value?.Trim(), "System", StringComparison.OrdinalIgnoreCase) ? "System" : "Dark";
        }

        /// <summary>
        /// D1: "System" follows the operating system's reduced-motion setting, "On" always animates, "Off" never does.
        /// The environment (MEOWCHAN_MOTION, MEOWCHAN_REDUCED_MOTION) still wins over this setting; see <see cref="Desktop.Motion.MotionPolicy"/>.
        /// </summary>
        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string Animations
        {
            get => _animations;
            set => _animations = Desktop.Motion.MotionPolicy.NormalizeSetting(value);
        }

        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string Language
        {
            get => _language;
            set => _language = NormalizeLanguage(value);
        }

        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string WorkspaceMode
        {
            get => _workspaceMode;
            set
            {
                string? mode = value?.Trim();
                _workspaceMode = string.Equals(mode, "Advanced", StringComparison.OrdinalIgnoreCase) ? "Advanced"
                    : string.Equals(mode, "Simple", StringComparison.OrdinalIgnoreCase) ? "Simple" : "Barebones";
            }
        }

        [JsonPropertyOrder(100)]
        public Dictionary<string, PresentationProfile> LayoutProfiles
        {
            get => _layoutProfiles;
            set
            {
                if (value == null) return;
                _deserializedLayoutProfiles = value
                    .Where(pair => pair.Value != null)
                    .ToDictionary(pair => pair.Key, pair => NormalizeProfile(pair.Value), StringComparer.Ordinal);
                foreach (string mode in new[] { "Barebones", "Simple", "Advanced" })
                    if (value.TryGetValue(mode, out PresentationProfile? profile) && profile != null)
                        _layoutProfiles[mode] = NormalizeProfile(profile);
            }
        }

        void IJsonOnDeserialized.OnDeserialized()
        {
            // Couple only after all legacy keys have been read, independent of JSON order.
            NormalizeGalleryControls();
            ReaderThumbnailWidth = Math.Clamp(ReaderThumbnailWidth, ReaderThumbnailWidthMinimum, ReaderThumbnailWidthMaximum);
            ReaderFoldLines = Math.Clamp(ReaderFoldLines, ReaderFoldLinesMinimum, ReaderFoldLinesMaximum);
            ReaderPreviewLines = Math.Clamp(ReaderPreviewLines, ReaderPreviewLinesMinimum, ReaderPreviewLinesMaximum);
            ReaderPreviewDelayMs = Math.Clamp(ReaderPreviewDelayMs, ReaderPreviewDelayMsMinimum, ReaderPreviewDelayMsMaximum);
            ReaderReplyStep = Math.Clamp(ReaderReplyStep, ReaderReplyStepMinimum, ReaderReplyStepMaximum);
            ReaderReplyLines = Math.Clamp(ReaderReplyLines, ReaderReplyLinesMinimum, ReaderReplyLinesMaximum);
            ReaderPageSize = Math.Clamp(ReaderPageSize, ReaderPageSizeMinimum, ReaderPageSizeMaximum);
            ReaderThreadingDepth = Math.Clamp(ReaderThreadingDepth, ReaderThreadingDepthMinimum, ReaderThreadingDepthMaximum);
            GalleryFitMaxUpscalePercent = Math.Clamp(GalleryFitMaxUpscalePercent, GalleryFitMaxUpscalePercentMinimum, GalleryFitMaxUpscalePercentMaximum);
            PostFilterRuleLimit = Math.Clamp(PostFilterRuleLimit, PostFilterRuleLimitMinimum, PostFilterRuleLimitMaximum);
            HiddenPostLimit = Math.Clamp(HiddenPostLimit, HiddenPostLimitMinimum, HiddenPostLimitMaximum);

            // The new profiles win even if a hand-edited file places legacy active
            // preference fields after LayoutProfiles.
            if (_deserializedLayoutProfiles == null) return;
            foreach (string mode in new[] { "Barebones", "Simple", "Advanced" })
                if (_deserializedLayoutProfiles.TryGetValue(mode, out PresentationProfile? profile))
                    _layoutProfiles[mode] = profile;
            _deserializedLayoutProfiles = null;
        }

        public PresentationProfile GetLayoutProfile(string mode)
        {
            mode = NormalizeWorkspaceMode(mode);
            if (!_layoutProfiles.TryGetValue(mode, out PresentationProfile? profile))
                _layoutProfiles[mode] = profile = new PresentationProfile();
            return profile;
        }

        private PresentationProfile ActiveProfile => GetLayoutProfile(WorkspaceMode);

        private static string NormalizeWorkspaceMode(string? mode) =>
            string.Equals(mode?.Trim(), "Advanced", StringComparison.OrdinalIgnoreCase) ? "Advanced" :
            string.Equals(mode?.Trim(), "Simple", StringComparison.OrdinalIgnoreCase) ? "Simple" : "Barebones";

        private static PresentationProfile NormalizeProfile(PresentationProfile profile)
        {
            PresentationProfile result = profile.Clone();
            result.ReadingFontSize = string.Equals(profile.ReadingFontSize?.Trim(), "Small", StringComparison.OrdinalIgnoreCase) ? "Small"
                : string.Equals(profile.ReadingFontSize?.Trim(), "Large", StringComparison.OrdinalIgnoreCase) ? "Large" : "Normal";
            result.ArchiveThreadLayout = string.Equals(profile.ArchiveThreadLayout?.Trim(), "Table", StringComparison.OrdinalIgnoreCase) ? "Table" : "List";
            result.ArchiveSplitWidth = double.IsFinite(profile.ArchiveSplitWidth) ? Math.Clamp(profile.ArchiveSplitWidth, 180, 1200) : 270;
            return result;
        }

        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool CompactDensity { get => ActiveProfile.CompactDensity; set => ActiveProfile.CompactDensity = value; }

        [JsonIgnore]
        internal bool? SettingsBaselineCompactDensity { get; set; }
        [JsonIgnore]
        internal string? SettingsBaselineReadingFontSize { get; set; }
        [JsonIgnore]
        internal string? SettingsBaselineWorkspaceMode { get; set; }
        [JsonIgnore]
        internal Dictionary<string, PresentationProfile>? SettingsBaselineProfiles { get; set; }
        [JsonIgnore]
        internal int? SettingsBaselineVideoVolume { get; set; }
        [JsonIgnore]
        internal GalleryPlaybackPreferences? SettingsBaselineGalleryPlayback { get; set; }

        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool ArchiveMediaTiles { get => ActiveProfile.ArchiveMediaTiles; set => ActiveProfile.ArchiveMediaTiles = value; }

        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string ReadingFontSize
        {
            get => ActiveProfile.ReadingFontSize;
            set => ActiveProfile.ReadingFontSize = string.Equals(value?.Trim(), "Small", StringComparison.OrdinalIgnoreCase) ? "Small"
                : string.Equals(value?.Trim(), "Large", StringComparison.OrdinalIgnoreCase) ? "Large" : "Normal";
        }

        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string ArchiveThreadLayout
        {
            get => ActiveProfile.ArchiveThreadLayout;
            set => ActiveProfile.ArchiveThreadLayout = string.Equals(value?.Trim(), "Table", StringComparison.OrdinalIgnoreCase) ? "Table" : "List";
        }

        // Reader post cards: "Fit" (default) sizes a card to its content up to the reading width, "Full" uses the whole list,
        // "Column" makes every card as wide as the reading width allows and centres that column.
        private string _readerPostWidth = ReaderPostWidthFit;
        public const string ReaderPostWidthFit = "Fit";
        public const string ReaderPostWidthFull = "Full";
        public const string ReaderPostWidthColumn = "Column";
        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string ReaderPostWidth
        {
            get => _readerPostWidth;
            set => _readerPostWidth = string.Equals(value?.Trim(), ReaderPostWidthFull, StringComparison.OrdinalIgnoreCase) ? ReaderPostWidthFull
                : string.Equals(value?.Trim(), ReaderPostWidthColumn, StringComparison.OrdinalIgnoreCase) ? ReaderPostWidthColumn : ReaderPostWidthFit;
        }

        [JsonConverter(typeof(DesktopPreferenceTrueBooleanConverter))]
        public bool VideoAutoplay { get; set; } = true;
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool VideoStartMuted { get; set; }
        [JsonConverter(typeof(DesktopPreferenceVolumeConverter))]
        public int VideoVolume { get => _videoVolume; set => _videoVolume = Math.Clamp(value, 0, 100); }
        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string GalleryPlaybackMode
        {
            get => _galleryPlaybackMode;
            set
            {
                // Read migration: "Repeat video" was the third playback mode until it became its own choice. An old
                // profile's "Repeat" turns it on and leaves the mode Manual; the file changes only on its next save.
                if (string.Equals(value?.Trim(), "Repeat", StringComparison.OrdinalIgnoreCase)) GalleryVideoRepeat = true;
                _galleryPlaybackMode = string.Equals(value?.Trim(), "Slideshow", StringComparison.OrdinalIgnoreCase) ? "Slideshow" : "Manual";
            }
        }
        [JsonConverter(typeof(DesktopPreferenceSlideshowSecondsConverter))]
        public int GallerySlideshowSeconds { get => _gallerySlideshowSeconds; set => _gallerySlideshowSeconds = Math.Clamp(value, 1, 300); }
        /// <summary>Repeat the shown video outside a slideshow (the transport's loop button).</summary>
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool GalleryVideoRepeat { get; set; }
        /// <summary>What a running slideshow does with a video: play it once, loop it for at least
        /// <see cref="GallerySlideshowVideoSeconds"/> or repeat it <see cref="GallerySlideshowVideoRepeats"/> times.</summary>
        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string GallerySlideshowVideoRule
        {
            get => _gallerySlideshowVideoRule;
            set => _gallerySlideshowVideoRule = Desktop.GalleryPlaybackPreferences.ParseVideoRule(value).ToString();
        }
        [JsonConverter(typeof(DesktopPreferenceSlideshowVideoSecondsConverter))]
        public int GallerySlideshowVideoSeconds
        {
            get => _gallerySlideshowVideoSeconds;
            set => _gallerySlideshowVideoSeconds = Math.Clamp(value, GallerySlideshowVideoSecondsMinimum, GallerySlideshowVideoSecondsMaximum);
        }
        internal const int GallerySlideshowVideoSecondsDefault = 10, GallerySlideshowVideoSecondsMinimum = 1, GallerySlideshowVideoSecondsMaximum = 600;
        [JsonConverter(typeof(DesktopPreferenceSlideshowVideoRepeatsConverter))]
        public int GallerySlideshowVideoRepeats
        {
            get => _gallerySlideshowVideoRepeats;
            set => _gallerySlideshowVideoRepeats = Math.Clamp(value, GallerySlideshowVideoRepeatsMinimum, GallerySlideshowVideoRepeatsMaximum);
        }
        internal const int GallerySlideshowVideoRepeatsDefault = 2, GallerySlideshowVideoRepeatsMinimum = 1, GallerySlideshowVideoRepeatsMaximum = 50;
        /// <summary>Idle time without pointer movement before the gallery's overlay controls and the reader's arrow cross hide.</summary>
        public int ControlsIdleHideMs { get; set; } = ControlsIdleHideMsDefault;
        internal const int ControlsIdleHideMsDefault = 2000, ControlsIdleHideMsMinimum = 500, ControlsIdleHideMsMaximum = 30_000;
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool GalleryMediaFocus { get; set; }
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool GalleryOverlayControls { get; set; }
        // Keep both serialized keys for older profiles; explicit actions use
        // GalleryPlaybackPreferences.WithControlsOverlay rather than coupled setters.
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool GalleryAutoHideControls { get; set; }
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool GalleryOverlayPostContext { get; set; }

        [JsonConverter(typeof(DesktopPreferenceTrueBooleanConverter))]
        public bool ShowShortcutHelp { get; set; } = true;
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool GalleryContextOpen { get; set; }
        [JsonConverter(typeof(DesktopPreferenceSplitWidthConverter))]
        public double ArchiveSplitWidth
        {
            get => ActiveProfile.ArchiveSplitWidth;
            set => ActiveProfile.ArchiveSplitWidth = double.IsFinite(value) ? Math.Clamp(value, 180, 1200) : 270;
        }

        [JsonConverter(typeof(DesktopPreferenceWindowWidthConverter))]
        public double WindowWidth { get => _windowWidth; set => _windowWidth = double.IsFinite(value) ? Math.Clamp(value, 320, 10000) : 1380; }
        [JsonConverter(typeof(DesktopPreferenceWindowHeightConverter))]
        public double WindowHeight { get => _windowHeight; set => _windowHeight = double.IsFinite(value) ? Math.Clamp(value, 240, 10000) : 850; }
        [JsonConverter(typeof(DesktopPreferenceWindowPositionConverter))]
        public double? WindowPositionX { get => _windowPositionX; set => _windowPositionX = value.HasValue && double.IsFinite(value.Value) && Math.Abs(value.Value) <= 100000 ? value : null; }
        [JsonConverter(typeof(DesktopPreferenceWindowPositionConverter))]
        public double? WindowPositionY { get => _windowPositionY; set => _windowPositionY = value.HasValue && double.IsFinite(value.Value) && Math.Abs(value.Value) <= 100000 ? value : null; }
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool WindowMaximized { get; set; }
        [JsonConverter(typeof(DesktopPreferenceColumnWidthsConverter))]
        public Dictionary<string, double> ColumnWidths
        {
            get => ActiveProfile.ColumnWidths;
            set => ActiveProfile.ColumnWidths = value == null ? new(StringComparer.Ordinal) : new(value, StringComparer.Ordinal);
        }
        public Dictionary<string, bool> ColumnVisibility
        {
            get => ActiveProfile.ColumnVisibility;
            set => ActiveProfile.ColumnVisibility = value == null ? new(StringComparer.Ordinal) : new(value, StringComparer.Ordinal);
        }
        public Dictionary<string, int> ColumnOrder
        {
            get => ActiveProfile.ColumnOrder;
            set => ActiveProfile.ColumnOrder = value == null ? new(StringComparer.Ordinal) : new(value, StringComparer.Ordinal);
        }
        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string LastExportDirectory { get => _lastExportDirectory; set => _lastExportDirectory = value ?? string.Empty; }
        /// <summary>The folder saved video frames go to: chosen by the person on the first "Save frame as image", never an export folder. Empty until then.</summary>
        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string GalleryFrameDirectory { get => _galleryFrameDirectory; set => _galleryFrameDirectory = value ?? string.Empty; }
        /// <summary>The one-time Downloads hint "read it in the Archive" was answered (dismissed, or its action used). Not a setting: no editor shows it.</summary>
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool ArchiveBridgeHintSeen { get; set; }

        /// <summary>English, path-free description of a failed load for the log; the window shows <see cref="LocalizedLoadWarning"/>.</summary>
        [JsonIgnore]
        public string? LoadWarning { get; private set; }
        /// <summary>What a failed load fell back to; drives the localized warning.</summary>
        [JsonIgnore] internal SettingsLoadProblem LoadProblem { get; private set; }
        /// <summary>The recovered backup carried source media exceptions, which were switched off.</summary>
        [JsonIgnore] internal bool LoadDisabledSourceGrants { get; private set; }

        /// <summary>The load warning in the current language, naming only file names, or null after a clean load.</summary>
        internal string? LocalizedLoadWarning()
        {
            string? text = LoadProblem switch
            {
                SettingsLoadProblem.RecoveredFromBackup => Desktop.Localization.Texts.T("settings.load.recovered", SettingsFileName, SettingsFileName + ".bak"),
                SettingsLoadProblem.DefaultsInUse => Desktop.Localization.Texts.T("settings.load.defaults", SettingsFileName),
                _ => null
            };
            return text != null && LoadDisabledSourceGrants ? text + " " + Desktop.Localization.Texts.T("settings.load.grantsDisabled") : text;
        }

        private const string SettingsFileName = "settings.json";

        /// <summary>Resolves the profile without creating files or directories.</summary>
        public static string GetConfigDirectory()
        {
            string? overrideDirectory = ResolveProfileOverrides(
                Environment.GetEnvironmentVariable("MEOWCHAN_PROFILE"),
                Environment.GetEnvironmentVariable("OCHAN_PROFILE"));
            if (overrideDirectory != null)
                return overrideDirectory;

            string homePath = OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (string.IsNullOrWhiteSpace(homePath))
                throw new InvalidOperationException("The user configuration directory could not be resolved.");

            return ResolveDefaultProfileDirectory(homePath);
        }

        internal static string? ResolveProfileOverrides(string? meowChanProfile, string? oChanProfile)
        {
            string? current = string.IsNullOrWhiteSpace(meowChanProfile) ? null :
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(meowChanProfile));
            string? legacy = string.IsNullOrWhiteSpace(oChanProfile) ? null :
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(oChanProfile));
            if (current != null && legacy != null && !string.Equals(current, legacy,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "MEOWCHAN_PROFILE and OCHAN_PROFILE select different profiles. Set only one, or set both to the same directory.");

            return current ?? legacy;
        }

        internal static string ResolveDefaultProfileDirectory(string homePath)
        {
            string current = Path.GetFullPath(Path.Combine(homePath, ".MeowChan"));
            string legacy = Path.GetFullPath(Path.Combine(homePath, ".oChan"));
            bool hasCurrent = ProfileDirectoryExists(current);
            bool hasLegacy = ProfileDirectoryExists(legacy);
            if (hasCurrent && hasLegacy)
                throw new InvalidOperationException(
                    $"Both MeowChan profiles exist ('{current}' and '{legacy}'). Select one explicitly with MEOWCHAN_PROFILE.");

            return hasLegacy ? legacy : current;
        }

        private static bool ProfileDirectoryExists(string path)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) == 0)
                    throw new IOException($"The profile path '{path}' is a file, not a directory.");
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }

        private static string GetDefaultDownloadPath() => GetDefaultDownloadPath("MeowChan");

        private static string GetDefaultDownloadPath(string productDirectory)
        {
            string homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(homePath))
                throw new InvalidOperationException("The user home directory could not be resolved.");

            return Path.GetFullPath(Path.Combine(homePath, "Downloads", productDirectory));
        }

        public static Config LoadConfig() => LoadConfig(GetConfigDirectory());

        /// <summary>Loads an isolated profile. Loading never changes existing files.</summary>
        public static Config LoadConfig(string profileDirectory) => LoadConfigCore(profileDirectory, null);

        internal static Config LoadConfig(string profileDirectory, ProfileLease lease)
        {
            lease.EnsureMatches(profileDirectory);
            Config config = LoadConfigCore(profileDirectory, lease.WasEmptyAtAcquisition);
            config._writeLease = lease;
            return config;
        }

        private static Config LoadConfigCore(string profileDirectory, bool? confirmedNew)
        {
            string directory = ResolveProfileDirectory(profileDirectory);
            string filePath = Path.Combine(directory, "settings.json");
            string relativePathBaseDirectory = Path.GetFullPath(Environment.CurrentDirectory);
            LoadStatus status = TryRead(filePath, relativePathBaseDirectory, out Config? config, out string? error);
            if (status == LoadStatus.Valid)
                return BindProfile(config!, directory, relativePathBaseDirectory, ProfileOrigin.ExistingValid);

            string backupPath = filePath + ".bak";
            LoadStatus backupStatus = TryRead(backupPath, relativePathBaseDirectory, out Config? backup, out string? backupError);
            if (backupStatus == LoadStatus.Valid)
            {
                Config recovered = BindProfile(backup!, directory, relativePathBaseDirectory,
                    status == LoadStatus.Unsupported ? ProfileOrigin.Unsupported : ProfileOrigin.BackupRecovery);
                // A backup can predate an explicit revoke. Recovery restores settings, but
                // source exceptions require fresh consent rather than reviving an old grant.
                bool disabledSourceGrants = recovered.SourceMediaIntegrityPermissions.Values
                    .Any(permission => permission.AllowProviderSizeMd5Mismatch);
                if (disabledSourceGrants)
                    recovered.SourceMediaIntegrityPermissions = recovered.SourceMediaIntegrityPermissions.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value.AllowProviderSizeMd5Mismatch
                            ? pair.Value with { AllowProviderSizeMd5Mismatch = false } : pair.Value,
                        StringComparer.Ordinal);
                recovered.LoadProblem = SettingsLoadProblem.RecoveredFromBackup;
                recovered.LoadDisabledSourceGrants = disabledSourceGrants;
                recovered.LoadWarning = $"Settings could not be loaded from '{Path.GetFileName(filePath)}' ({error ?? "file missing"}). " +
                    $"Recovered settings from backup '{Path.GetFileName(backupPath)}'. The original files were preserved." +
                    (disabledSourceGrants ? " Source media exception permissions from the backup were disabled and require explicit confirmation in Sources." : string.Empty);
                Log.Warning("{LoadWarning}", recovered.LoadWarning);
                return recovered;
            }

            ProfileOrigin origin = status == LoadStatus.Unsupported || backupStatus == LoadStatus.Unsupported ? ProfileOrigin.Unsupported
                : status != LoadStatus.Missing || backupStatus != LoadStatus.Missing ? ProfileOrigin.Damaged
                : (confirmedNew ?? IsEmptyProfile(directory)) ? ProfileOrigin.ConfirmedNew : ProfileOrigin.ExistingMissingSettings;
            Config defaults = BindProfile(new Config(), directory, relativePathBaseDirectory, origin);
            // Only a positively new profile starts in Simple, where Archive and Media are footer buttons. The field
            // default stays Barebones: a stored file without a mode, damaged or unsupported settings and a profile
            // that has other files but no settings.json keep the view they have always had.
            if (origin == ProfileOrigin.ConfirmedNew) defaults.WorkspaceMode = "Simple";
            if (IsLegacyProfileWithoutSettings(directory))
                defaults.DownloadPath = GetDefaultDownloadPath("oChan");
            if (status != LoadStatus.Missing || backupStatus != LoadStatus.Missing)
            {
                defaults.LoadProblem = SettingsLoadProblem.DefaultsInUse;
                defaults.LoadWarning = $"Settings could not be loaded from '{Path.GetFileName(filePath)}' ({error ?? "file missing"}); " +
                    $"no valid backup was available ({backupError ?? "file missing"}). " +
                    "Default settings are in use. The original files were preserved.";
                Log.Warning("{LoadWarning}", defaults.LoadWarning);
            }

            return defaults;
        }

        private void NormalizeGalleryControls()
        {
            GalleryAutoHideControls = GalleryOverlayControls;
            SettingsBaselineGalleryPlayback = SettingsBaselineGalleryPlayback?.Normalize();
        }

        public void Validate()
        {
            NormalizeGalleryControls();
            if (SourceMediaIntegrityPermissions == null || SourceMediaIntegrityPermissions.Count > 1024)
                throw new InvalidDataException("Saved source media permissions are invalid.");
            foreach ((string key, SourceMediaIntegrityPermission permission) in SourceMediaIntegrityPermissions)
            {
                if (permission == null || permission.Revision < 0)
                    throw new InvalidDataException("Saved source media permission identity is invalid.");
                string expectedKey;
                try { expectedKey = SourceMediaIntegrityPermission.CreateKey(permission.PluginOwnerId, permission.SourceId); }
                catch (ArgumentException error) { throw new InvalidDataException("Saved source media permission identity is invalid.", error); }
                if (!string.Equals(key, expectedKey, StringComparison.Ordinal))
                    throw new InvalidDataException("Saved source media permission identity is invalid.");
            }
            if (FirstRunSetupVersion is < 0 or > 1 || FirstRunSetupPending && FirstRunSetupVersion != 0)
                throw new InvalidDataException("Unsupported or inconsistent first-run setup state.");
            if (SchemaVersion != 1)
                throw new InvalidDataException($"Unsupported settings schema version: {SchemaVersion}.");
            if (RecheckTimer < 10 || TimeSpan.FromSeconds(RecheckTimer).TotalMilliseconds > uint.MaxValue - 1)
                throw new ArgumentOutOfRangeException(nameof(RecheckTimer), "The recheck interval must be between 10 seconds and the supported timer maximum.");
            if (BandwidthLimiter <= 0 || BandwidthLimiter > BandwidthLimiterMaximumBytes)
                throw new ArgumentOutOfRangeException(nameof(BandwidthLimiter), "The bandwidth limit must be greater than zero and at most " + BandwidthLimiterMaximumMiB + " MiB/s.");
            if (MaxParallelDownloads is < MaxParallelDownloadsMinimum or > MaxParallelDownloadsMaximum)
                throw new ArgumentOutOfRangeException(nameof(MaxParallelDownloads), "Parallel downloads must be between " + MaxParallelDownloadsMinimum + " and " + MaxParallelDownloadsMaximum + ".");
            if (ParallelDownloadsSafetyLimit is < 1 or > Downloader.DownloadQueue.MaximumParallelDownloadsSafetyLimit)
                throw new ArgumentOutOfRangeException(nameof(ParallelDownloadsSafetyLimit), "The parallel download safety limit must be between 1 and 256.");
            if (PluginMaximumProductionWorkers is < 1 or > 8)
                throw new ArgumentOutOfRangeException(nameof(PluginMaximumProductionWorkers), "The plugin limit must be between 1 and 8.");
            if (PluginWorkerIdleMinutes is < 1 or > 30)
                throw new ArgumentOutOfRangeException(nameof(PluginWorkerIdleMinutes), "The plugin limit must be between 1 and 30.");
            if (PluginSourceHeapMiB is < 128 or > 1024)
                throw new ArgumentOutOfRangeException(nameof(PluginSourceHeapMiB), "The plugin limit must be between 128 and 1024.");
            if (PluginFilterHeapMiB is < 256 or > 2048)
                throw new ArgumentOutOfRangeException(nameof(PluginFilterHeapMiB), "The plugin limit must be between 256 and 2048.");
            if (MinimumFreeSpaceGiB is < MinimumFreeSpaceGiBMinimum or > MinimumFreeSpaceGiBMaximum)
                throw new ArgumentOutOfRangeException(nameof(MinimumFreeSpaceGiB), "Free-space reserve must be between " + MinimumFreeSpaceGiBMinimum + " and " + MinimumFreeSpaceGiBMaximum + " GiB.");
            if (BooruCatalogLimitMiB is < BooruCatalogMinimumLimitMiB or > BooruCatalogMaximumLimitMiB)
                throw new ArgumentOutOfRangeException(nameof(BooruCatalogLimitMiB), "The Danbooru catalog limit must be between 8 and 128 MiB.");
            if (PrivateTagLimit is < PrivateTagMinimumLimit or > PrivateTagMaximumLimit)
                throw new ArgumentOutOfRangeException(nameof(PrivateTagLimit), "The own tag limit must be between " + PrivateTagMinimumLimit + " and " + PrivateTagMaximumLimit + ".");
            if (PrivateTagFileLimitMiB is < PrivateTagFileMinimumLimitMiB or > PrivateTagFileMaximumLimitMiB)
                throw new ArgumentOutOfRangeException(nameof(PrivateTagFileLimitMiB), "The own tag file limit must be between 8 and 128 MiB.");
            if (MediaIndexCacheLimitMiB is < MediaIndexCacheMinimumLimitMiB or > MediaIndexCacheMaximumLimitMiB)
                throw new ArgumentOutOfRangeException(nameof(MediaIndexCacheLimitMiB), "The media index cache limit must be between 16 and 1024 MiB.");
            if (FavoriteEntryLimit is < FavoriteEntryLimitMinimum or > FavoriteEntryLimitMaximum)
                throw new ArgumentOutOfRangeException(nameof(FavoriteEntryLimit), "FavoriteEntryLimit is outside its supported range (" + FavoriteEntryLimitMinimum + " to " + FavoriteEntryLimitMaximum + ").");
            if (FavoriteFileLimitMiB is < FavoriteFileLimitMiBMinimum or > FavoriteFileLimitMiBMaximum)
                throw new ArgumentOutOfRangeException(nameof(FavoriteFileLimitMiB), "FavoriteFileLimitMiB is outside its supported range (" + FavoriteFileLimitMiBMinimum + " to " + FavoriteFileLimitMiBMaximum + ").");
            if (PostBookmarkLimit is < PostBookmarkLimitMinimum or > PostBookmarkLimitMaximum)
                throw new ArgumentOutOfRangeException(nameof(PostBookmarkLimit), "PostBookmarkLimit is outside its supported range (" + PostBookmarkLimitMinimum + " to " + PostBookmarkLimitMaximum + ").");
            if (SavedSearchLimit is < SavedSearchLimitMinimum or > SavedSearchLimitMaximum)
                throw new ArgumentOutOfRangeException(nameof(SavedSearchLimit), "SavedSearchLimit is outside its supported range (" + SavedSearchLimitMinimum + " to " + SavedSearchLimitMaximum + ").");
            if (ReaderGroupLimit is < ReaderGroupLimitMinimum or > ReaderGroupLimitMaximum)
                throw new ArgumentOutOfRangeException(nameof(ReaderGroupLimit), "ReaderGroupLimit is outside its supported range (" + ReaderGroupLimitMinimum + " to " + ReaderGroupLimitMaximum + ").");
            if (SourcesWatchPreviewRows is < SourcesWatchPreviewRowsMinimum or > SourcesWatchPreviewRowsMaximum)
                throw new ArgumentOutOfRangeException(nameof(SourcesWatchPreviewRows), "SourcesWatchPreviewRows is outside its supported range (" + SourcesWatchPreviewRowsMinimum + " to " + SourcesWatchPreviewRowsMaximum + ").");
            if (ExportConfirmFiles is < ExportConfirmFilesMinimum or > ExportConfirmFilesMaximum)
                throw new ArgumentOutOfRangeException(nameof(ExportConfirmFiles), "ExportConfirmFiles is outside its supported range (" + ExportConfirmFilesMinimum + " to " + ExportConfirmFilesMaximum + ").");
            if (ExportConfirmGiB is < ExportConfirmGiBMinimum or > ExportConfirmGiBMaximum)
                throw new ArgumentOutOfRangeException(nameof(ExportConfirmGiB), "ExportConfirmGiB is outside its supported range (" + ExportConfirmGiBMinimum + " to " + ExportConfirmGiBMaximum + ").");
            if (PosterCacheLimitMiB is < PosterCacheLimitMiBMinimum or > PosterCacheLimitMiBMaximum)
                throw new ArgumentOutOfRangeException(nameof(PosterCacheLimitMiB), "PosterCacheLimitMiB is outside its supported range (" + PosterCacheLimitMiBMinimum + " to " + PosterCacheLimitMiBMaximum + ").");
            if (PosterCacheFileLimit is < PosterCacheFileLimitMinimum or > PosterCacheFileLimitMaximum)
                throw new ArgumentOutOfRangeException(nameof(PosterCacheFileLimit), "PosterCacheFileLimit is outside its supported range (" + PosterCacheFileLimitMinimum + " to " + PosterCacheFileLimitMaximum + ").");
            if (AudioProbeCacheLimitMiB is < AudioProbeCacheLimitMiBMinimum or > AudioProbeCacheLimitMiBMaximum)
                throw new ArgumentOutOfRangeException(nameof(AudioProbeCacheLimitMiB), "AudioProbeCacheLimitMiB is outside its supported range (" + AudioProbeCacheLimitMiBMinimum + " to " + AudioProbeCacheLimitMiBMaximum + ").");
            if (AudioProbeCacheEntries is < AudioProbeCacheEntriesMinimum or > AudioProbeCacheEntriesMaximum)
                throw new ArgumentOutOfRangeException(nameof(AudioProbeCacheEntries), "AudioProbeCacheEntries is outside its supported range (" + AudioProbeCacheEntriesMinimum + " to " + AudioProbeCacheEntriesMaximum + ").");
            if (ThumbnailCacheTiles is < ThumbnailCacheTilesMinimum or > ThumbnailCacheTilesMaximum)
                throw new ArgumentOutOfRangeException(nameof(ThumbnailCacheTiles), "ThumbnailCacheTiles is outside its supported range (" + ThumbnailCacheTilesMinimum + " to " + ThumbnailCacheTilesMaximum + ").");
            if (ClipboardUrlLimit is < ClipboardUrlLimitMinimum or > ClipboardUrlLimitMaximum)
                throw new ArgumentOutOfRangeException(nameof(ClipboardUrlLimit), "ClipboardUrlLimit is outside its supported range (" + ClipboardUrlLimitMinimum + " to " + ClipboardUrlLimitMaximum + ").");
            if (ClipboardTextLimitKiB is < ClipboardTextLimitKiBMinimum or > ClipboardTextLimitKiBMaximum)
                throw new ArgumentOutOfRangeException(nameof(ClipboardTextLimitKiB), "ClipboardTextLimitKiB is outside its supported range (" + ClipboardTextLimitKiBMinimum + " to " + ClipboardTextLimitKiBMaximum + ").");
            if (ControlsIdleHideMs is < ControlsIdleHideMsMinimum or > ControlsIdleHideMsMaximum)
                throw new ArgumentOutOfRangeException(nameof(ControlsIdleHideMs), "ControlsIdleHideMs is outside its supported range (" + ControlsIdleHideMsMinimum + " to " + ControlsIdleHideMsMaximum + ").");
            if (StatsSampleIntervalMs is < StatsSampleIntervalMsMinimum or > StatsSampleIntervalMsMaximum)
                throw new ArgumentOutOfRangeException(nameof(StatsSampleIntervalMs), "StatsSampleIntervalMs is outside its supported range (" + StatsSampleIntervalMsMinimum + " to " + StatsSampleIntervalMsMaximum + ").");
            if (StatsHistorySamples is < StatsHistorySamplesMinimum or > StatsHistorySamplesMaximum)
                throw new ArgumentOutOfRangeException(nameof(StatsHistorySamples), "StatsHistorySamples is outside its supported range (" + StatsHistorySamplesMinimum + " to " + StatsHistorySamplesMaximum + ").");
            if (StatsBackgroundIntervalMs is < StatsBackgroundIntervalMsMinimum or > StatsBackgroundIntervalMsMaximum)
                throw new ArgumentOutOfRangeException(nameof(StatsBackgroundIntervalMs), "StatsBackgroundIntervalMs is outside its supported range (" + StatsBackgroundIntervalMsMinimum + " to " + StatsBackgroundIntervalMsMaximum + ").");
            if (StatsRetentionDays is < StatsRetentionDaysMinimum or > StatsRetentionDaysMaximum)
                throw new ArgumentOutOfRangeException(nameof(StatsRetentionDays), "StatsRetentionDays is outside its supported range (" + StatsRetentionDaysMinimum + " to " + StatsRetentionDaysMaximum + ").");
            if (StatsPersistIntervalMinutes is < StatsPersistIntervalMinutesMinimum or > StatsPersistIntervalMinutesMaximum)
                throw new ArgumentOutOfRangeException(nameof(StatsPersistIntervalMinutes), "StatsPersistIntervalMinutes is outside its supported range (" + StatsPersistIntervalMinutesMinimum + " to " + StatsPersistIntervalMinutesMaximum + ").");
            if (StatsMaxSources is < StatsMaxSourcesMinimum or > StatsMaxSourcesMaximum)
                throw new ArgumentOutOfRangeException(nameof(StatsMaxSources), "StatsMaxSources is outside its supported range (" + StatsMaxSourcesMinimum + " to " + StatsMaxSourcesMaximum + ").");
            if (GalleryImageCacheMiB is < GalleryImageCacheMiBMinimum or > GalleryImageCacheMiBMaximum)
                throw new ArgumentOutOfRangeException(nameof(GalleryImageCacheMiB), "GalleryImageCacheMiB is outside its supported range (" + GalleryImageCacheMiBMinimum + " to " + GalleryImageCacheMiBMaximum + ").");
            if (GalleryPreloadAhead is < GalleryPreloadAheadMinimum or > GalleryPreloadAheadMaximum)
                throw new ArgumentOutOfRangeException(nameof(GalleryPreloadAhead), "GalleryPreloadAhead is outside its supported range (" + GalleryPreloadAheadMinimum + " to " + GalleryPreloadAheadMaximum + ").");
            if (GalleryPreloadBehind is < GalleryPreloadBehindMinimum or > GalleryPreloadBehindMaximum)
                throw new ArgumentOutOfRangeException(nameof(GalleryPreloadBehind), "GalleryPreloadBehind is outside its supported range (" + GalleryPreloadBehindMinimum + " to " + GalleryPreloadBehindMaximum + ").");
            if (ReaderThumbnailWidth is < ReaderThumbnailWidthMinimum or > ReaderThumbnailWidthMaximum)
                throw new ArgumentOutOfRangeException(nameof(ReaderThumbnailWidth), "ReaderThumbnailWidth is outside its supported range.");
            if (ReaderFoldLines is < ReaderFoldLinesMinimum or > ReaderFoldLinesMaximum)
                throw new ArgumentOutOfRangeException(nameof(ReaderFoldLines), "ReaderFoldLines is outside its supported range.");
            if (ReaderPreviewLines is < ReaderPreviewLinesMinimum or > ReaderPreviewLinesMaximum)
                throw new ArgumentOutOfRangeException(nameof(ReaderPreviewLines), "ReaderPreviewLines is outside its supported range.");
            if (ReaderPreviewDelayMs is < ReaderPreviewDelayMsMinimum or > ReaderPreviewDelayMsMaximum)
                throw new ArgumentOutOfRangeException(nameof(ReaderPreviewDelayMs), "ReaderPreviewDelayMs is outside its supported range.");
            if (ReaderReplyStep is < ReaderReplyStepMinimum or > ReaderReplyStepMaximum)
                throw new ArgumentOutOfRangeException(nameof(ReaderReplyStep), "ReaderReplyStep is outside its supported range.");
            if (ReaderReplyLines is < ReaderReplyLinesMinimum or > ReaderReplyLinesMaximum)
                throw new ArgumentOutOfRangeException(nameof(ReaderReplyLines), "ReaderReplyLines is outside its supported range.");
            if (ReaderPageSize is < ReaderPageSizeMinimum or > ReaderPageSizeMaximum)
                throw new ArgumentOutOfRangeException(nameof(ReaderPageSize), "ReaderPageSize is outside its supported range.");
            if (ReaderThreadingDepth is < ReaderThreadingDepthMinimum or > ReaderThreadingDepthMaximum)
                throw new ArgumentOutOfRangeException(nameof(ReaderThreadingDepth), "ReaderThreadingDepth is outside its supported range.");
            if (GalleryFitMaxUpscalePercent is < GalleryFitMaxUpscalePercentMinimum or > GalleryFitMaxUpscalePercentMaximum)
                throw new ArgumentOutOfRangeException(nameof(GalleryFitMaxUpscalePercent), "GalleryFitMaxUpscalePercent is outside its supported range.");
            if (PostFilterRuleLimit is < PostFilterRuleLimitMinimum or > PostFilterRuleLimitMaximum)
                throw new ArgumentOutOfRangeException(nameof(PostFilterRuleLimit), "PostFilterRuleLimit is outside its supported range.");
            if (HiddenPostLimit is < HiddenPostLimitMinimum or > HiddenPostLimitMaximum)
                throw new ArgumentOutOfRangeException(nameof(HiddenPostLimit), "HiddenPostLimit is outside its supported range.");
            if (ImportMaxFilesPerBatch is < ImportMaxFilesPerBatchMinimum or > ImportMaxFilesPerBatchMaximum)
                throw new ArgumentOutOfRangeException(nameof(ImportMaxFilesPerBatch), "ImportMaxFilesPerBatch is outside its supported range (" + ImportMaxFilesPerBatchMinimum + " to " + ImportMaxFilesPerBatchMaximum + ").");
            if (ImportMaxBatchGiB is < ImportMaxBatchGiBMinimum or > ImportMaxBatchGiBMaximum)
                throw new ArgumentOutOfRangeException(nameof(ImportMaxBatchGiB), "ImportMaxBatchGiB is outside its supported range (" + ImportMaxBatchGiBMinimum + " to " + ImportMaxBatchGiBMaximum + ").");
            if (ImportMaxFileMiB is < ImportMaxFileMiBMinimum or > ImportMaxFileMiBMaximum)
                throw new ArgumentOutOfRangeException(nameof(ImportMaxFileMiB), "ImportMaxFileMiB is outside its supported range (" + ImportMaxFileMiBMinimum + " to " + ImportMaxFileMiBMaximum + ").");
            if (ImportMaxTotalFiles is < ImportMaxTotalFilesMinimum or > ImportMaxTotalFilesMaximum)
                throw new ArgumentOutOfRangeException(nameof(ImportMaxTotalFiles), "ImportMaxTotalFiles is outside its supported range (" + ImportMaxTotalFilesMinimum + " to " + ImportMaxTotalFilesMaximum + ").");
            if (ImportMaxTagsPerBatch is < ImportMaxTagsPerBatchMinimum or > ImportMaxTagsPerBatchMaximum)
                throw new ArgumentOutOfRangeException(nameof(ImportMaxTagsPerBatch), "ImportMaxTagsPerBatch is outside its supported range (" + ImportMaxTagsPerBatchMinimum + " to " + ImportMaxTagsPerBatchMaximum + ").");
            if (ImportMaxBatches is < ImportMaxBatchesMinimum or > ImportMaxBatchesMaximum)
                throw new ArgumentOutOfRangeException(nameof(ImportMaxBatches), "ImportMaxBatches is outside its supported range (" + ImportMaxBatchesMinimum + " to " + ImportMaxBatchesMaximum + ").");
            if (MediaIndexMaxEntries is < MediaIndexMaxEntriesMinimum or > MediaIndexMaxEntriesMaximum)
                throw new ArgumentOutOfRangeException(nameof(MediaIndexMaxEntries), "MediaIndexMaxEntries is outside its supported range (" + MediaIndexMaxEntriesMinimum + " to " + MediaIndexMaxEntriesMaximum + ").");
            if (MediaIndexMaxThreads is < MediaIndexMaxThreadsMinimum or > MediaIndexMaxThreadsMaximum)
                throw new ArgumentOutOfRangeException(nameof(MediaIndexMaxThreads), "MediaIndexMaxThreads is outside its supported range (" + MediaIndexMaxThreadsMinimum + " to " + MediaIndexMaxThreadsMaximum + ").");
            if (ImportCatalogLimitMiB is < ImportCatalogLimitMiBMinimum or > ImportCatalogLimitMiBMaximum)
                throw new ArgumentOutOfRangeException(nameof(ImportCatalogLimitMiB), "ImportCatalogLimitMiB is outside its supported range (" + ImportCatalogLimitMiBMinimum + " to " + ImportCatalogLimitMiBMaximum + ").");
            if (PluginEditorExecutable is { Length: > 4096 } || PluginEditorExecutable?.Any(char.IsControl) == true
                || PluginEditorArguments == null || PluginEditorArguments.Length > 32
                || PluginEditorArguments.Any(value => value == null || value.Length > 4096 || value.Any(char.IsControl)))
                throw new ArgumentException("The plugin editor command is invalid.", nameof(PluginEditorExecutable));
            if (string.IsNullOrWhiteSpace(DownloadPath))
                throw new ArgumentException("A download directory is required.", nameof(DownloadPath));

            try { _ = Path.GetFullPath(DownloadPath, _relativePathBaseDirectory); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            { throw new ArgumentException("The download directory path is invalid.", nameof(DownloadPath), error); }
            if (LastExportDirectory.Length > 4096 || LastExportDirectory.Any(char.IsControl))
                throw new ArgumentException("The last export directory is invalid.", nameof(LastExportDirectory));
            if (GalleryFrameDirectory.Length > 4096 || GalleryFrameDirectory.Any(char.IsControl))
                throw new ArgumentException("The video frame directory is invalid.", nameof(GalleryFrameDirectory));
            foreach (string mode in new[] { "Barebones", "Simple", "Advanced" })
            {
                PresentationProfile profile = GetLayoutProfile(mode);
                if (profile.ColumnWidths.Count > 64 || profile.ColumnWidths.Any(entry => string.IsNullOrWhiteSpace(entry.Key) || entry.Key.Length > 100 ||
                    entry.Key.Any(char.IsControl) || !double.IsFinite(entry.Value) || entry.Value < 24 || entry.Value > 2000))
                    throw new ArgumentException("Saved column widths are invalid.", nameof(ColumnWidths));
                if (profile.ColumnVisibility.Count > 64 || profile.ColumnVisibility.Keys.Any(key =>
                    string.IsNullOrWhiteSpace(key) || key.Length > 100 || key.Any(char.IsControl)))
                    throw new ArgumentException("Saved column visibility is invalid.", nameof(ColumnVisibility));
                if (profile.ColumnOrder.Count > 64 || profile.ColumnOrder.Any(entry =>
                    string.IsNullOrWhiteSpace(entry.Key) || entry.Key.Length > 100 || entry.Key.Any(char.IsControl)
                    || entry.Value < 0 || entry.Value > 63))
                    throw new ArgumentException("Saved column order is invalid.", nameof(ColumnOrder));
            }
        }

        public Config Clone()
        {
            return new Config
            {
                SchemaVersion = SchemaVersion,
                FirstRunSetupPending = FirstRunSetupPending, FirstRunSetupVersion = FirstRunSetupVersion,
                SourceMediaIntegrityPermissions = SourceMediaIntegrityPermissions.ToDictionary(pair => pair.Key, pair => pair.Value with { }, StringComparer.Ordinal),
                DownloadPath = DownloadPath,
                RecheckTimer = RecheckTimer,
                SaveUrlsOnExit = SaveUrlsOnExit,
                ShowBoardAdmissionWizard = ShowBoardAdmissionWizard,
                WorkViewNoticePending = WorkViewNoticePending,
                RememberReadingPosition = RememberReadingPosition,
                MonitorClipboardUrls = MonitorClipboardUrls,
                DownloadsPaused = DownloadsPaused,
                MinimizeToTray = MinimizeToTray,
                CloseToTray = CloseToTray,
                BandwidthLimiter = BandwidthLimiter,
                MaxParallelDownloads = MaxParallelDownloads,
                ParallelDownloadsSafetyLimit = ParallelDownloadsSafetyLimit,
                AutomaticUpdateChecks = AutomaticUpdateChecks,
                ShowInformationNotifications = ShowInformationNotifications,
                ShowWarningNotifications = ShowWarningNotifications,
                ShowErrorNotifications = ShowErrorNotifications,
                IncludePrereleaseUpdates = IncludePrereleaseUpdates,
                MinimumFreeSpaceGiB = MinimumFreeSpaceGiB,
                PluginMaximumProductionWorkers = PluginMaximumProductionWorkers,
                PluginWorkerIdleMinutes = PluginWorkerIdleMinutes,
                PluginSourceHeapMiB = PluginSourceHeapMiB,
                PluginFilterHeapMiB = PluginFilterHeapMiB,
                BooruCatalogLimitMiB = BooruCatalogLimitMiB,
                PrivateTagLimit = PrivateTagLimit,
                PrivateTagFileLimitMiB = PrivateTagFileLimitMiB,
                MediaIndexCacheLimitMiB = MediaIndexCacheLimitMiB,
                FavoriteEntryLimit = FavoriteEntryLimit,
                FavoriteFileLimitMiB = FavoriteFileLimitMiB,
                PostBookmarkLimit = PostBookmarkLimit,
                SavedSearchLimit = SavedSearchLimit,
                ReaderGroupLimit = ReaderGroupLimit,
                SourcesWatchPreviewRows = SourcesWatchPreviewRows,
                ExportConfirmFiles = ExportConfirmFiles,
                ExportConfirmGiB = ExportConfirmGiB,
                PosterCacheLimitMiB = PosterCacheLimitMiB,
                PosterCacheFileLimit = PosterCacheFileLimit,
                AudioProbeCacheLimitMiB = AudioProbeCacheLimitMiB,
                AudioProbeCacheEntries = AudioProbeCacheEntries,
                ThumbnailCacheTiles = ThumbnailCacheTiles,
                ClipboardUrlLimit = ClipboardUrlLimit,
                ClipboardTextLimitKiB = ClipboardTextLimitKiB,
                StatsSampleIntervalMs = StatsSampleIntervalMs,
                ControlsIdleHideMs = ControlsIdleHideMs,
                StatsHistorySamples = StatsHistorySamples,
                StatsBackgroundIntervalMs = StatsBackgroundIntervalMs,
                StatsTrayTooltip = StatsTrayTooltip,
                StatsTrayMenu = StatsTrayMenu,
                StatsPersist = StatsPersist,
                StatsRetentionDays = StatsRetentionDays,
                StatsPersistIntervalMinutes = StatsPersistIntervalMinutes,
                StatsMaxSources = StatsMaxSources,
                StatsHeaderGraph = StatsHeaderGraph,
                GalleryImageCacheMiB = GalleryImageCacheMiB,
                GalleryPreloadAhead = GalleryPreloadAhead,
                GalleryPreloadBehind = GalleryPreloadBehind,
                ImportMaxFilesPerBatch = ImportMaxFilesPerBatch,
                ImportMaxBatchGiB = ImportMaxBatchGiB,
                ImportMaxFileMiB = ImportMaxFileMiB,
                ImportMaxTotalFiles = ImportMaxTotalFiles,
                ImportMaxTagsPerBatch = ImportMaxTagsPerBatch,
                ImportMaxBatches = ImportMaxBatches,
                MediaIndexMaxEntries = MediaIndexMaxEntries,
                MediaIndexMaxThreads = MediaIndexMaxThreads,
                ImportCatalogLimitMiB = ImportCatalogLimitMiB,
                ReaderThumbnailWidth = ReaderThumbnailWidth,
                ReaderFoldLines = ReaderFoldLines,
                ReaderPreviewLines = ReaderPreviewLines,
                ReaderPreviewDelayMs = ReaderPreviewDelayMs,
                ReaderReplyStep = ReaderReplyStep,
                ReaderReplyLines = ReaderReplyLines,
                ReaderPageSize = ReaderPageSize,
                ReaderThreadingDepth = ReaderThreadingDepth,
                GalleryFitMaxUpscalePercent = GalleryFitMaxUpscalePercent,
                PostFilterRuleLimit = PostFilterRuleLimit,
                HiddenPostLimit = HiddenPostLimit,
                ReaderDateStyle = ReaderDateStyle,
                ReaderSpoilers = ReaderSpoilers,
                MediaSort = MediaSort,
                MediaSortDirection = MediaSortDirection,
                GalleryFitUpscaleLimited = GalleryFitUpscaleLimited,

                ShortcutBindings = ShortcutBindings.Clone(),
                PluginEditorExecutable = PluginEditorExecutable,
                PluginEditorArguments = (string[])PluginEditorArguments.Clone(),
                Theme = Theme,
                Animations = Animations,
                Language = Language,
                WorkspaceMode = WorkspaceMode,
                CompactDensity = CompactDensity,
                SettingsBaselineCompactDensity = CompactDensity,
                SettingsBaselineReadingFontSize = ReadingFontSize,
                SettingsBaselineWorkspaceMode = WorkspaceMode,
                SettingsBaselineVideoVolume = VideoVolume,
                // A fresh draft captures the current normalized state. Session admission
                // restores the editor's original baseline after freezing via Clone().
                SettingsBaselineGalleryPlayback = GalleryPlaybackPreferences.FromConfig(this),
                SettingsBaselineProfiles = new[] { "Barebones", "Simple", "Advanced" }
                    .ToDictionary(mode => mode, mode => GetLayoutProfile(mode).Clone(), StringComparer.Ordinal),
                ArchiveMediaTiles = ArchiveMediaTiles,
                ArchiveThreadLayout = ArchiveThreadLayout,
                ReadingFontSize = ReadingFontSize,
                ReaderPostWidth = ReaderPostWidth,
                VideoAutoplay = VideoAutoplay,
                VideoStartMuted = VideoStartMuted,
                VideoVolume = VideoVolume,
                GalleryPlaybackMode = GalleryPlaybackMode,
                GallerySlideshowSeconds = GallerySlideshowSeconds,
                GalleryVideoRepeat = GalleryVideoRepeat,
                GallerySlideshowVideoRule = GallerySlideshowVideoRule,
                GallerySlideshowVideoSeconds = GallerySlideshowVideoSeconds,
                GallerySlideshowVideoRepeats = GallerySlideshowVideoRepeats,
                GalleryMediaFocus = GalleryMediaFocus,
                GalleryOverlayControls = GalleryOverlayControls,
                GalleryAutoHideControls = GalleryOverlayControls,
                GalleryOverlayPostContext = GalleryOverlayPostContext,
                ShowShortcutHelp = ShowShortcutHelp,
                GalleryContextOpen = GalleryContextOpen,
                ArchiveSplitWidth = ArchiveSplitWidth,
                WindowWidth = WindowWidth,
                WindowHeight = WindowHeight,
                WindowPositionX = WindowPositionX,
                WindowPositionY = WindowPositionY,
                WindowMaximized = WindowMaximized,
                ColumnWidths = ColumnWidths,
                LayoutProfiles = _layoutProfiles.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal),
                LastExportDirectory = LastExportDirectory,
                GalleryFrameDirectory = GalleryFrameDirectory,
                ArchiveBridgeHintSeen = ArchiveBridgeHintSeen,
                LoadWarning = LoadWarning,
                LoadProblem = LoadProblem,
                LoadDisabledSourceGrants = LoadDisabledSourceGrants,
                _profileDirectory = _profileDirectory,
                _writeLease = _writeLease,
                _relativePathBaseDirectory = _relativePathBaseDirectory,
                Origin = Origin,
                // Null-default instance observers only; never copy a token or grant authority.
                BeforeAdmittedSaveMutationForTests = BeforeAdmittedSaveMutationForTests,
                AfterAdmittedSaveMutationForTests = AfterAdmittedSaveMutationForTests
            };
        }

        /// <summary>Applies validated values without changing this instance's profile.</summary>
        public void CopyFrom(Config source)
        {
            ArgumentNullException.ThrowIfNull(source);
            source.Validate();
            // A value copy cannot turn a recovered/indeterminate profile into an ordinary writer.
            if (!source.AllowsOrdinaryWrites) Origin = source.Origin;
            else if (AllowsOrdinaryWrites && source.Origin == ProfileOrigin.ExistingValid && _profileDirectory != null
                && string.Equals(_profileDirectory, source._profileDirectory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                Origin = ProfileOrigin.ExistingValid;
            string absoluteDownloadPath = Path.GetFullPath(source.DownloadPath, source._relativePathBaseDirectory);

            SchemaVersion = source.SchemaVersion;
            FirstRunSetupPending = source.FirstRunSetupPending; FirstRunSetupVersion = source.FirstRunSetupVersion;
            // Consent is portable within this profile's clones, never across profile-bound instances.
            if (string.Equals(_profileDirectory, source._profileDirectory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                SourceMediaIntegrityPermissions = source.SourceMediaIntegrityPermissions.ToDictionary(pair => pair.Key, pair => pair.Value with { }, StringComparer.Ordinal);
            DownloadPath = absoluteDownloadPath;
            RecheckTimer = source.RecheckTimer;
            SaveUrlsOnExit = source.SaveUrlsOnExit;
            ShowBoardAdmissionWizard = source.ShowBoardAdmissionWizard;
            WorkViewNoticePending = source.WorkViewNoticePending;
            RememberReadingPosition = source.RememberReadingPosition;
            MonitorClipboardUrls = source.MonitorClipboardUrls;
            DownloadsPaused = source.DownloadsPaused;
            MinimizeToTray = source.MinimizeToTray;
            CloseToTray = source.CloseToTray;
            BandwidthLimiter = source.BandwidthLimiter;
            MaxParallelDownloads = source.MaxParallelDownloads;
            ParallelDownloadsSafetyLimit = source.ParallelDownloadsSafetyLimit;
            AutomaticUpdateChecks = source.AutomaticUpdateChecks;
            ShowInformationNotifications = source.ShowInformationNotifications;
            ShowWarningNotifications = source.ShowWarningNotifications;
            ShowErrorNotifications = source.ShowErrorNotifications;
            IncludePrereleaseUpdates = source.IncludePrereleaseUpdates;
            MinimumFreeSpaceGiB = source.MinimumFreeSpaceGiB;
            PluginMaximumProductionWorkers = source.PluginMaximumProductionWorkers;
            PluginWorkerIdleMinutes = source.PluginWorkerIdleMinutes;
            PluginSourceHeapMiB = source.PluginSourceHeapMiB;
            PluginFilterHeapMiB = source.PluginFilterHeapMiB;
            BooruCatalogLimitMiB = source.BooruCatalogLimitMiB;
            PrivateTagLimit = source.PrivateTagLimit;
            PrivateTagFileLimitMiB = source.PrivateTagFileLimitMiB;
            MediaIndexCacheLimitMiB = source.MediaIndexCacheLimitMiB;
            FavoriteEntryLimit = source.FavoriteEntryLimit;
            FavoriteFileLimitMiB = source.FavoriteFileLimitMiB;
            PostBookmarkLimit = source.PostBookmarkLimit;
            SavedSearchLimit = source.SavedSearchLimit;
            ReaderGroupLimit = source.ReaderGroupLimit;
            SourcesWatchPreviewRows = source.SourcesWatchPreviewRows;
            ExportConfirmFiles = source.ExportConfirmFiles;
            ExportConfirmGiB = source.ExportConfirmGiB;
            PosterCacheLimitMiB = source.PosterCacheLimitMiB;
            PosterCacheFileLimit = source.PosterCacheFileLimit;
            AudioProbeCacheLimitMiB = source.AudioProbeCacheLimitMiB;
            AudioProbeCacheEntries = source.AudioProbeCacheEntries;
            ThumbnailCacheTiles = source.ThumbnailCacheTiles;
            ClipboardUrlLimit = source.ClipboardUrlLimit;
            ClipboardTextLimitKiB = source.ClipboardTextLimitKiB;
            StatsSampleIntervalMs = source.StatsSampleIntervalMs;
            ControlsIdleHideMs = source.ControlsIdleHideMs;
            StatsHistorySamples = source.StatsHistorySamples;
            StatsBackgroundIntervalMs = source.StatsBackgroundIntervalMs;
            StatsTrayTooltip = source.StatsTrayTooltip;
            StatsTrayMenu = source.StatsTrayMenu;
            StatsPersist = source.StatsPersist;
            StatsRetentionDays = source.StatsRetentionDays;
            StatsPersistIntervalMinutes = source.StatsPersistIntervalMinutes;
            StatsMaxSources = source.StatsMaxSources;
            StatsHeaderGraph = source.StatsHeaderGraph;
            GalleryImageCacheMiB = source.GalleryImageCacheMiB;
            GalleryPreloadAhead = source.GalleryPreloadAhead;
            GalleryPreloadBehind = source.GalleryPreloadBehind;
            ImportMaxFilesPerBatch = source.ImportMaxFilesPerBatch;
            ImportMaxBatchGiB = source.ImportMaxBatchGiB;
            ImportMaxFileMiB = source.ImportMaxFileMiB;
            ImportMaxTotalFiles = source.ImportMaxTotalFiles;
            ImportMaxTagsPerBatch = source.ImportMaxTagsPerBatch;
            ImportMaxBatches = source.ImportMaxBatches;
            MediaIndexMaxEntries = source.MediaIndexMaxEntries;
            MediaIndexMaxThreads = source.MediaIndexMaxThreads;
            ImportCatalogLimitMiB = source.ImportCatalogLimitMiB;
            ReaderThumbnailWidth = source.ReaderThumbnailWidth;
            ReaderFoldLines = source.ReaderFoldLines;
            ReaderPreviewLines = source.ReaderPreviewLines;
            ReaderPreviewDelayMs = source.ReaderPreviewDelayMs;
            ReaderReplyStep = source.ReaderReplyStep;
            ReaderReplyLines = source.ReaderReplyLines;
            ReaderPageSize = source.ReaderPageSize;
            ReaderThreadingDepth = source.ReaderThreadingDepth;
            GalleryFitMaxUpscalePercent = source.GalleryFitMaxUpscalePercent;
            PostFilterRuleLimit = source.PostFilterRuleLimit;
            HiddenPostLimit = source.HiddenPostLimit;
            ReaderDateStyle = source.ReaderDateStyle;
            ReaderSpoilers = source.ReaderSpoilers;
            MediaSort = source.MediaSort;
            MediaSortDirection = source.MediaSortDirection;
            GalleryFitUpscaleLimited = source.GalleryFitUpscaleLimited;

            ShortcutBindings = source.ShortcutBindings.Clone();
            Theme = source.Theme;
            Animations = source.Animations;
            Language = source.Language;
            WorkspaceMode = source.WorkspaceMode;
            CompactDensity = source.CompactDensity;
            ArchiveMediaTiles = source.ArchiveMediaTiles;
            ArchiveThreadLayout = source.ArchiveThreadLayout;
            ReadingFontSize = source.ReadingFontSize;
            ReaderPostWidth = source.ReaderPostWidth;
            VideoAutoplay = source.VideoAutoplay;
            VideoStartMuted = source.VideoStartMuted;
            VideoVolume = source.VideoVolume;
            GalleryPlaybackPreferences.FromConfig(source).ApplyTo(this);
            ShowShortcutHelp = source.ShowShortcutHelp;
            GalleryContextOpen = source.GalleryContextOpen;
            ArchiveSplitWidth = source.ArchiveSplitWidth;
            WindowWidth = source.WindowWidth;
            WindowHeight = source.WindowHeight;
            WindowPositionX = source.WindowPositionX;
            WindowPositionY = source.WindowPositionY;
            WindowMaximized = source.WindowMaximized;
            ColumnWidths = source.ColumnWidths;
            LayoutProfiles = source._layoutProfiles.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
            LastExportDirectory = source.LastExportDirectory;
            GalleryFrameDirectory = source.GalleryFrameDirectory;
            ArchiveBridgeHintSeen = source.ArchiveBridgeHintSeen;
            PluginEditorExecutable = source.PluginEditorExecutable;
            PluginEditorArguments = (string[])source.PluginEditorArguments.Clone();
            LoadWarning = source.LoadWarning;
            LoadProblem = source.LoadProblem;
            LoadDisabledSourceGrants = source.LoadDisabledSourceGrants;
        }

        /// <summary>Copies only fields edited by Settings, preserving direct view preferences.</summary>
        internal void CopySettingsFieldsFrom(Config source)
        {
            ArgumentNullException.ThrowIfNull(source);
            source.Validate();
            DownloadPath = Path.GetFullPath(source.DownloadPath, source._relativePathBaseDirectory);
            RecheckTimer = source.RecheckTimer;
            SaveUrlsOnExit = source.SaveUrlsOnExit;
            ShowBoardAdmissionWizard = source.ShowBoardAdmissionWizard;
            RememberReadingPosition = source.RememberReadingPosition;
            MonitorClipboardUrls = source.MonitorClipboardUrls;
            MinimizeToTray = source.MinimizeToTray;
            CloseToTray = source.CloseToTray;
            BandwidthLimiter = source.BandwidthLimiter;
            MaxParallelDownloads = source.MaxParallelDownloads;
            ParallelDownloadsSafetyLimit = source.ParallelDownloadsSafetyLimit;
            AutomaticUpdateChecks = source.AutomaticUpdateChecks;
            ShowInformationNotifications = source.ShowInformationNotifications;
            ShowWarningNotifications = source.ShowWarningNotifications;
            ShowErrorNotifications = source.ShowErrorNotifications;
            IncludePrereleaseUpdates = source.IncludePrereleaseUpdates;
            MinimumFreeSpaceGiB = source.MinimumFreeSpaceGiB;
            PluginMaximumProductionWorkers = source.PluginMaximumProductionWorkers;
            PluginWorkerIdleMinutes = source.PluginWorkerIdleMinutes;
            PluginSourceHeapMiB = source.PluginSourceHeapMiB;
            PluginFilterHeapMiB = source.PluginFilterHeapMiB;
            BooruCatalogLimitMiB = source.BooruCatalogLimitMiB;
            PrivateTagLimit = source.PrivateTagLimit;
            PrivateTagFileLimitMiB = source.PrivateTagFileLimitMiB;
            MediaIndexCacheLimitMiB = source.MediaIndexCacheLimitMiB;
            FavoriteEntryLimit = source.FavoriteEntryLimit;
            FavoriteFileLimitMiB = source.FavoriteFileLimitMiB;
            PostBookmarkLimit = source.PostBookmarkLimit;
            SavedSearchLimit = source.SavedSearchLimit;
            ReaderGroupLimit = source.ReaderGroupLimit;
            SourcesWatchPreviewRows = source.SourcesWatchPreviewRows;
            ExportConfirmFiles = source.ExportConfirmFiles;
            ExportConfirmGiB = source.ExportConfirmGiB;
            PosterCacheLimitMiB = source.PosterCacheLimitMiB;
            PosterCacheFileLimit = source.PosterCacheFileLimit;
            AudioProbeCacheLimitMiB = source.AudioProbeCacheLimitMiB;
            AudioProbeCacheEntries = source.AudioProbeCacheEntries;
            ThumbnailCacheTiles = source.ThumbnailCacheTiles;
            ClipboardUrlLimit = source.ClipboardUrlLimit;
            ClipboardTextLimitKiB = source.ClipboardTextLimitKiB;
            StatsSampleIntervalMs = source.StatsSampleIntervalMs;
            ControlsIdleHideMs = source.ControlsIdleHideMs;
            StatsHistorySamples = source.StatsHistorySamples;
            StatsBackgroundIntervalMs = source.StatsBackgroundIntervalMs;
            StatsTrayTooltip = source.StatsTrayTooltip;
            StatsTrayMenu = source.StatsTrayMenu;
            StatsPersist = source.StatsPersist;
            StatsRetentionDays = source.StatsRetentionDays;
            StatsPersistIntervalMinutes = source.StatsPersistIntervalMinutes;
            StatsMaxSources = source.StatsMaxSources;
            StatsHeaderGraph = source.StatsHeaderGraph;
            GalleryImageCacheMiB = source.GalleryImageCacheMiB;
            GalleryPreloadAhead = source.GalleryPreloadAhead;
            GalleryPreloadBehind = source.GalleryPreloadBehind;
            ImportMaxFilesPerBatch = source.ImportMaxFilesPerBatch;
            ImportMaxBatchGiB = source.ImportMaxBatchGiB;
            ImportMaxFileMiB = source.ImportMaxFileMiB;
            ImportMaxTotalFiles = source.ImportMaxTotalFiles;
            ImportMaxTagsPerBatch = source.ImportMaxTagsPerBatch;
            ImportMaxBatches = source.ImportMaxBatches;
            MediaIndexMaxEntries = source.MediaIndexMaxEntries;
            MediaIndexMaxThreads = source.MediaIndexMaxThreads;
            ImportCatalogLimitMiB = source.ImportCatalogLimitMiB;
            ReaderThumbnailWidth = source.ReaderThumbnailWidth;
            ReaderFoldLines = source.ReaderFoldLines;
            ReaderPreviewLines = source.ReaderPreviewLines;
            ReaderPreviewDelayMs = source.ReaderPreviewDelayMs;
            ReaderReplyStep = source.ReaderReplyStep;
            ReaderReplyLines = source.ReaderReplyLines;
            ReaderPageSize = source.ReaderPageSize;
            ReaderThreadingDepth = source.ReaderThreadingDepth;
            GalleryFitMaxUpscalePercent = source.GalleryFitMaxUpscalePercent;
            PostFilterRuleLimit = source.PostFilterRuleLimit;
            HiddenPostLimit = source.HiddenPostLimit;
            ReaderDateStyle = source.ReaderDateStyle;
            ReaderSpoilers = source.ReaderSpoilers;
            GalleryFitUpscaleLimited = source.GalleryFitUpscaleLimited;

            ShortcutBindings = source.ShortcutBindings.Clone();
            Theme = source.Theme;
            Animations = source.Animations;
            Language = source.Language;
            foreach (string mode in new[] { "Barebones", "Simple", "Advanced" })
            {
                PresentationProfile edited = source.GetLayoutProfile(mode);
                PresentationProfile baseline = source.SettingsBaselineProfiles != null &&
                    source.SettingsBaselineProfiles.TryGetValue(mode, out PresentationProfile? captured)
                    ? captured : new PresentationProfile();
                PresentationProfile liveEdited = GetLayoutProfile(mode);
                if (baseline.ReadingFontSize != edited.ReadingFontSize)
                    liveEdited.ReadingFontSize = edited.ReadingFontSize;
                if (baseline.CompactDensity != edited.CompactDensity)
                    liveEdited.CompactDensity = edited.CompactDensity;
            }
            WorkspaceMode = source.WorkspaceMode;
            ReaderPostWidth = source.ReaderPostWidth;
            VideoAutoplay = source.VideoAutoplay;
            VideoStartMuted = source.VideoStartMuted;
            // Gallery volume is also a direct preference. An unchanged settings
            // draft must not roll back a newer gallery adjustment.
            if (!source.SettingsBaselineVideoVolume.HasValue || source.VideoVolume != source.SettingsBaselineVideoVolume.Value)
                VideoVolume = source.VideoVolume;
            GalleryPlaybackPreferences galleryDraft = GalleryPlaybackPreferences.FromConfig(source);
            if (source.SettingsBaselineGalleryPlayback is { } galleryBaseline)
                galleryDraft.ApplyChangesTo(this, galleryBaseline);
            else
                galleryDraft.ApplyTo(this);
            ShowShortcutHelp = source.ShowShortcutHelp;
        }

        // Compatibility settings host uses its existing isolated config owner.
        internal void ForgetReadingPositionFile() => File.Delete(Path.Combine(_profileDirectory ?? GetConfigDirectory(), "reading-position.json"));

        public void SaveConfig() => SaveConfig(_profileDirectory ?? GetConfigDirectory());

        /// <summary>Atomically saves settings; validation and write errors reach the caller.</summary>
        public void SaveConfig(string profileDirectory)
        {
            if (_writeLease is { } lease)
            {
                using ProfileAdmissionToken admission = lease.Admission.EnterStoreWriter();
                SaveConfigAdmitted(profileDirectory, admission);
            }
            else SaveConfigCore(profileDirectory); // Offline compatibility, outside the bound domain.
        }

        [JsonIgnore] internal Action<ProfileAdmissionToken>? BeforeAdmittedSaveMutationForTests { get; set; }
        [JsonIgnore] internal Action<ProfileAdmissionToken>? AfterAdmittedSaveMutationForTests { get; set; }

        // Borrow the caller's exact Startup/Ordinary token; never enter or release its owner.
        internal void SaveConfigAdmitted(string profileDirectory, ProfileAdmissionToken admission)
        {
            ProfileLease lease = _writeLease ?? throw new InvalidOperationException("Admitted settings writes require a bound profile lease.");
            lease.RunStoreWrite(admission, () =>
            {
                SaveConfigCore(profileDirectory, () => BeforeAdmittedSaveMutationForTests?.Invoke(admission));
                AfterAdmittedSaveMutationForTests?.Invoke(admission);
                return true;
            });
        }

        private void SaveConfigCore(string profileDirectory, Action? beforeMutation = null)
        {
            EnsureOrdinaryWritesAllowed();
            Validate();
            string directory = ResolveProfileDirectory(profileDirectory);
            _writeLease?.EnsureMatches(directory);
            bool sameProfile = string.Equals(directory, _profileDirectory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            string filePath = Path.Combine(directory, "settings.json");
            DownloadPath = Path.GetFullPath(DownloadPath, _relativePathBaseDirectory);
            if (Origin == ProfileOrigin.ConfirmedNew && FirstRunSetupVersion == 0) FirstRunSetupPending = true;
            string json = JsonSerializer.Serialize(this, ConfigJsonContext.Default.Config);
            // The read side judges a larger file damaged without reading it; never write one that the next start would refuse.
            if (System.Text.Encoding.UTF8.GetByteCount(json) > SettingsFileCeilingBytes)
                throw new InvalidDataException($"The settings are larger than their fixed {SettingsFileCeilingBytes / (1024 * 1024)} MiB file ceiling; nothing was written.");

            LoadStatus currentStatus = TryRead(filePath, _relativePathBaseDirectory, out _, out _);
            string backupPath = filePath + ".bak";
            LoadStatus backupStatus = TryRead(backupPath, _relativePathBaseDirectory, out _, out _);
            // Recheck destination too: a freshly constructed Config is not a recovery bypass.
            if (currentStatus is LoadStatus.Invalid or LoadStatus.Unsupported || backupStatus == LoadStatus.Unsupported
                || currentStatus == LoadStatus.Missing && backupStatus != LoadStatus.Missing
                    && !(sameProfile && Origin == ProfileOrigin.ExistingValid && backupStatus == LoadStatus.Valid)
                || currentStatus == LoadStatus.Missing && !IsEmptyProfile(directory)
                    && !(sameProfile && (Origin == ProfileOrigin.ExistingValid || Origin == ProfileOrigin.ConfirmedNew && _writeLease != null)))
                throw new InvalidOperationException("Settings destination requires an explicit recovery decision; existing files were preserved.");
            beforeMutation?.Invoke(); // Inside the real store pin, before Preserve/atomic I/O.
            if (currentStatus == LoadStatus.Valid && backupStatus == LoadStatus.Invalid)
                AtomicJsonFile.PreserveFile(backupPath);

            AtomicJsonFile.WriteAllText(filePath, json, backupCurrentFile: currentStatus == LoadStatus.Valid);
            _profileDirectory = directory;
            Origin = ProfileOrigin.ExistingValid;
            LoadWarning = null;
            LoadProblem = SettingsLoadProblem.None;
            LoadDisabledSourceGrants = false;
            Log.Information("Configuration saved to {ConfigFilePath}", filePath);
        }

        private static Config BindProfile(Config config, string directory, string relativePathBaseDirectory, ProfileOrigin origin)
        {
            config.Origin = origin;
            config._profileDirectory = directory;
            config._relativePathBaseDirectory = relativePathBaseDirectory;
            return config;
        }

        private static string ResolveProfileDirectory(string profileDirectory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
            return Path.GetFullPath(profileDirectory);
        }

        private static bool IsEmptyProfile(string directory)
        {
            try { return !Directory.EnumerateFileSystemEntries(directory).Any(); }
            catch (DirectoryNotFoundException) { return true; }
        }

        private static bool IsLegacyProfileWithoutSettings(string directory)
        {
            if (string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)), ".oChan",
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return true;

            // The owner can have published the new protocol marker and lock before
            // settings are loaded. These original data files still identify a legacy
            // profile whose implicit download directory must remain unchanged.
            return ProfileFileExists(Path.Combine(directory, "saved_urls.json")) ||
                ProfileFileExists(Path.Combine(directory, ".ochan-profile.lock"));
        }

        private static bool ProfileFileExists(string path)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) != 0)
                    throw new IOException($"The profile file path '{path}' is a directory, not a file.");
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }

        private static string NormalizeLanguage(string? value)
        {
            string code = value?.Trim() ?? string.Empty;
            if (code.Length > 35 || !Regex.IsMatch(code, "^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$", RegexOptions.CultureInvariant))
                return "en";

            try
            {
                return CultureInfo.GetCultureInfo(code).Name;
            }
            catch (CultureNotFoundException)
            {
                return "en";
            }
        }

        /// <summary>
        /// Fixed read ceiling of settings.json and its .bak, not a setting. A real settings file holds a few KiB (even
        /// with every source permission, layout and shortcut filled in it stays far below 1 MiB); a larger file is judged
        /// damaged from its length alone and is never read into memory.
        /// </summary>
        internal const int SettingsFileCeilingBytes = 4 * 1024 * 1024;

        /// <summary>Reads at most <see cref="SettingsFileCeilingBytes"/>, decoded as <see cref="File.ReadAllText(string)"/> decodes.</summary>
        private static string ReadSettingsText(string filePath)
        {
            using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            long length = stream.Length;
            if (length > SettingsFileCeilingBytes)
                throw new InvalidDataException($"The settings file is larger than its fixed {SettingsFileCeilingBytes / (1024 * 1024)} MiB ceiling.");
            byte[] bytes = new byte[length];
            try { stream.ReadExactly(bytes); }
            catch (EndOfStreamException) { throw new InvalidDataException("The settings file changed while it was read."); }
            // Never judge a prefix: a file that grew meanwhile is read again on the next start.
            if (stream.ReadByte() != -1)
                throw new InvalidDataException("The settings file changed while it was read.");
            using StreamReader reader = new(new MemoryStream(bytes, writable: false), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        private static LoadStatus TryRead(string filePath, string relativePathBaseDirectory, out Config? config, out string? error)
        {
            config = null;
            error = null;
            string json;
            try
            {
                json = ReadSettingsText(filePath);
            }
            catch (FileNotFoundException)
            {
                return LoadStatus.Missing;
            }
            catch (DirectoryNotFoundException)
            {
                return LoadStatus.Missing;
            }
            catch (InvalidDataException ex)
            {
                // Too large or changing under the read: damaged like a document that does not parse.
                error = ex.Message;
                return LoadStatus.Invalid;
            }

            // An inaccessible document is not a damaged document. Propagate read
            // failures so temporary locks or permissions cannot activate defaults
            // or allow SaveConfig to replace a file it could not inspect.
            try
            {
                config = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.Config);
                if (config == null)
                    throw new InvalidDataException("The settings document contains null.");

                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    // The old MinimizeToTray preference controlled close only.
                    // An explicit CloseToTray field marks the independent preferences.
                    if (!document.RootElement.TryGetProperty(nameof(CloseToTray), out _))
                    {
                        config.CloseToTray = config.MinimizeToTray;
                        config.MinimizeToTray = false;
                    }
                    if (!document.RootElement.TryGetProperty(nameof(DownloadPath), out _))
                        config.DownloadPath = GetDefaultDownloadPath("oChan");
                    // A profile saved before the field existed predates the reduced work views: tell it once.
                    if (!document.RootElement.TryGetProperty(nameof(WorkViewNoticePending), out _))
                        config.WorkViewNoticePending = true;
                }
                // These three limits had no maximum before 1.1.0. A larger value an older version saved keeps the
                // file loadable and is reduced to today's maximum, which is also what was effective for parallel downloads.
                if (config.MaxParallelDownloads > MaxParallelDownloadsMaximum) config.MaxParallelDownloads = MaxParallelDownloadsMaximum;
                if (config.BandwidthLimiter > BandwidthLimiterMaximumBytes) config.BandwidthLimiter = BandwidthLimiterMaximumBytes;
                if (config.MinimumFreeSpaceGiB > MinimumFreeSpaceGiBMaximum) config.MinimumFreeSpaceGiB = MinimumFreeSpaceGiBMaximum;

                config._relativePathBaseDirectory = relativePathBaseDirectory;
                if (config.SchemaVersion != 1)
                { error = $"Unsupported settings schema version: {config.SchemaVersion}."; config = null; return LoadStatus.Unsupported; }
                config.Validate();
                // Existing relative settings keep the location used by the old application.
                // Resolve once on load so later working-directory changes cannot move jobs.
                config.DownloadPath = Path.GetFullPath(config.DownloadPath, relativePathBaseDirectory);
                return LoadStatus.Valid;
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException or NotSupportedException or PathTooLongException)
            {
                config = null;
                error = ex.Message;
                return LoadStatus.Invalid;
            }
        }

        private enum LoadStatus { Missing, Valid, Invalid, Unsupported }
    }

    // Invalid presentation preferences must not discard valid download/session settings.
    internal sealed class DesktopPreferenceStringConverter : JsonConverter<string>
    {
        public DesktopPreferenceStringConverter() { }
        public override bool HandleNull => true;

        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
                return reader.GetString() ?? string.Empty;
            reader.Skip();
            return string.Empty;
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }

    internal sealed class DesktopPreferenceBooleanConverter : JsonConverter<bool>
    {
        public DesktopPreferenceBooleanConverter() { }
        public override bool HandleNull => true;

        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                return reader.GetBoolean();
            reader.Skip();
            return false;
        }

        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
    }

    internal sealed class DesktopPreferenceTrueBooleanConverter : JsonConverter<bool>
    {
        public DesktopPreferenceTrueBooleanConverter() { }
        public override bool HandleNull => true;
        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                return reader.GetBoolean();
            reader.Skip();
            return true;
        }
        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
    }

    internal sealed class DesktopPreferenceVolumeConverter : JsonConverter<int>
    {
        public DesktopPreferenceVolumeConverter() { }
        public override bool HandleNull => true;
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int value))
                return Math.Clamp(value, 0, 100);
            reader.Skip();
            return 70;
        }
        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }

    internal sealed class DesktopPreferenceSlideshowSecondsConverter : JsonConverter<int>
    {
        public override bool HandleNull => true;
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int value))
                return Math.Clamp(value, 1, 300);
            reader.Skip();
            return 5;
        }
        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }

    internal sealed class DesktopPreferenceSlideshowVideoSecondsConverter : JsonConverter<int>
    {
        public override bool HandleNull => true;
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int value))
                return Math.Clamp(value, Config.GallerySlideshowVideoSecondsMinimum, Config.GallerySlideshowVideoSecondsMaximum);
            reader.Skip();
            return Config.GallerySlideshowVideoSecondsDefault;
        }
        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }

    internal sealed class DesktopPreferenceSlideshowVideoRepeatsConverter : JsonConverter<int>
    {
        public override bool HandleNull => true;
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int value))
                return Math.Clamp(value, Config.GallerySlideshowVideoRepeatsMinimum, Config.GallerySlideshowVideoRepeatsMaximum);
            reader.Skip();
            return Config.GallerySlideshowVideoRepeatsDefault;
        }
        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }

    internal sealed class DesktopPreferenceSplitWidthConverter : JsonConverter<double>
    {
        public DesktopPreferenceSplitWidthConverter() { }
        public override bool HandleNull => true;
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out double value) && double.IsFinite(value))
                return Math.Clamp(value, 180, 1200);
            reader.Skip();
            return 270;
        }
        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }

    internal sealed class DesktopPreferenceWindowWidthConverter : JsonConverter<double>
    {
        public DesktopPreferenceWindowWidthConverter() { }
        public override bool HandleNull => true;
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out double value) && double.IsFinite(value))
                return Math.Clamp(value, 320, 10000);
            reader.Skip();
            return 1380;
        }
        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }

    internal sealed class DesktopPreferenceWindowHeightConverter : JsonConverter<double>
    {
        public DesktopPreferenceWindowHeightConverter() { }
        public override bool HandleNull => true;
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out double value) && double.IsFinite(value))
                return Math.Clamp(value, 240, 10000);
            reader.Skip();
            return 850;
        }
        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }

    internal sealed class DesktopPreferenceWindowPositionConverter : JsonConverter<double?>
    {
        public DesktopPreferenceWindowPositionConverter() { }
        public override bool HandleNull => true;
        public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out double value) &&
                double.IsFinite(value) && Math.Abs(value) <= 100000)
                return value;
            reader.Skip();
            return null;
        }
        public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
        {
            if (value.HasValue) writer.WriteNumberValue(value.Value);
            else writer.WriteNullValue();
        }
    }

    internal sealed class DesktopPreferenceColumnWidthsConverter : JsonConverter<Dictionary<string, double>>
    {
        public DesktopPreferenceColumnWidthsConverter() { }
        public override bool HandleNull => true;
        public override Dictionary<string, double> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            Dictionary<string, double> result = new(StringComparer.Ordinal);
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                return result;
            }
            using JsonDocument document = JsonDocument.ParseValue(ref reader);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (result.Count >= 64) break;
                if (string.IsNullOrWhiteSpace(property.Name) || property.Name.Length > 100 || property.Name.Any(char.IsControl) ||
                    property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetDouble(out double width) ||
                    !double.IsFinite(width)) continue;
                result[property.Name] = Math.Clamp(width, 24, 2000);
            }
            return result;
        }
        public override void Write(Utf8JsonWriter writer, Dictionary<string, double> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (KeyValuePair<string, double> entry in value.OrderBy(item => item.Key, StringComparer.Ordinal))
                writer.WriteNumber(entry.Key, entry.Value);
            writer.WriteEndObject();
        }
    }

    [JsonSerializable(typeof(Desktop.Commands.ShortcutBindingsSection))]
    [JsonSerializable(typeof(Config))]
    [JsonSerializable(typeof(SavedUrls))]
    public partial class ConfigJsonContext : JsonSerializerContext
    {
    }
}

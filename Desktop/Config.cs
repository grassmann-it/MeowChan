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
using MeowChan.PluginHost.Taggers;
using Serilog;

namespace MeowChan
{
    public sealed class PresentationProfile
    {
        public bool CompactDensity { get; set; }
        public bool ArchiveMediaTiles { get; set; } = true;
        public string ReadingFontSize { get; set; } = "Normal";
        public string ArchiveThreadLayout { get; set; } = "List";
        public double ArchiveSplitWidth { get; set; } = Config.ArchiveSplitWidthDefault;
        public bool DiagnosticsProblemsCollapsed { get; set; }
        public bool DiagnosticsActivityCollapsed { get; set; }
        [JsonConverter(typeof(DesktopPreferenceColumnWidthsConverter))]
        public Dictionary<string, double> ColumnWidths { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, bool> ColumnVisibility { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> ColumnOrder { get; set; } = new(StringComparer.Ordinal);

        public PresentationProfile Clone() => new()
        {
            CompactDensity = CompactDensity, ArchiveMediaTiles = ArchiveMediaTiles,
            ReadingFontSize = ReadingFontSize, ArchiveThreadLayout = ArchiveThreadLayout,
            ArchiveSplitWidth = ArchiveSplitWidth,
            DiagnosticsProblemsCollapsed = DiagnosticsProblemsCollapsed,
            DiagnosticsActivityCollapsed = DiagnosticsActivityCollapsed,
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

    public partial class Config : IJsonOnDeserializing, IJsonOnDeserialized
    {
        private string? _profileDirectory;
        private ProfileLease? _writeLease;
        /// <summary>The profile folder these settings belong to; null for settings not bound to a profile.</summary>
        [JsonIgnore] internal string? ProfileDirectory => _profileDirectory;
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
        private string _statusPageSegment = StatusPageSegmentStatistics;
        private string _galleryFrameDirectory = string.Empty;

        public Desktop.Commands.ShortcutBindingsSection ShortcutBindings { get; set; } = new();
        public int SchemaVersion { get; set; } = 1;
        /// <summary>
        /// Revision of the one-shot migrations of view-preference defaults (1: the archive list width default moved from 270 to 330).
        /// A new profile starts at the current value; a file without the field is read as 0 (see OnDeserializing), so an old value is
        /// migrated exactly once and a width the person chose later, 270 included, is kept.
        /// </summary>
        public int PreferenceDefaultsRevision { get; set; } = CurrentPreferenceDefaultsRevision;
        internal const int CurrentPreferenceDefaultsRevision = 1;
        public bool FirstRunSetupPending { get; set; }
        public int FirstRunSetupVersion { get; set; }
        public Dictionary<string, SourceMediaIntegrityPermission> SourceMediaIntegrityPermissions { get; set; } = new(StringComparer.Ordinal);
        public string DownloadPath { get; set; } = GetDefaultDownloadPath();
        /// <summary>
        /// The filename template of exported originals (tokens {source} {post} {role} {hash} {ext}, ending with ".{ext}"); empty (the default) keeps the
        /// current naming. A damaged or unusable value in the file reads as empty. The length is a fixed ceiling, see <see cref="MeowChan.Interop.ExportFilenameTemplate.MaxTemplateLength"/>.
        /// </summary>
        [JsonConverter(typeof(DesktopPreferenceExportFilenameTemplateConverter))]
        public string ExportFilenameTemplate { get; set; } = string.Empty;
        public int RecheckTimer { get; set; } = 60;
        public bool SaveUrlsOnExit { get; set; } = true;
        public bool ShowBoardAdmissionWizard { get; set; } = true;
        /// <summary>
        /// One-time notice (plan AP5 question 3): a profile that existed before the work views reduced every page is told once that
        /// Barebones and Simple now show fewer tools. Loading a stored profile whose file lacks the field turns it on; a new
        /// profile and every in-memory configuration start false, and dismissing or acting on the notice saves false.
        /// </summary>
        public bool WorkViewNoticePending { get; set; }
        /// <summary>Lifetime of the work-view switch toast in seconds; zero disables it, including Undo and Details.</summary>
        public int WorkViewSwitchNoticeSeconds { get; set; } = 8;
        /// <summary>
        /// Whether Booru pages and tools are offered in this profile. This is a presentation preference only:
        /// hiding the tools never changes plugin enablement, integrity permissions, saved watchers or downloads.
        /// Existing profiles keep the currently visible tools unless the user explicitly hides them.
        /// </summary>
        [JsonConverter(typeof(DesktopPreferenceTrueBooleanConverter))]
        public bool ShowBooruTools { get; set; } = true;
        /// <summary>
        /// The footer of this profile (Settings › Appearance › Footer): the main pages in footer order, a leading "-" marking a page whose button is
        /// not shown directly (it stays in More, the ⋯ menu and under its shortcut). Empty is the default footer.
        /// Read and written through <see cref="Desktop.Commands.FooterLayout"/>: unknown ids and repeats are dropped, a page the list does not know
        /// takes its default place, Downloads is always shown. The empty 1.1.0 layout follows the new default; a stored "-booru" stays hidden.
        /// <see cref="ShowBooruTools"/> stays the master for Booru: while it is off the Booru
        /// page is unavailable whatever this list says.
        /// </summary>
        [JsonConverter(typeof(DesktopPreferenceNameListConverter))]
        public string[] FooterPages
        {
            get => _footerPages;
            set => _footerPages = Desktop.Commands.FooterLayout.FromStored(value).ToStored();
        }
        private string[] _footerPages = [];
        /// <summary>The footer layout of <see cref="FooterPages"/>.</summary>
        [JsonIgnore] internal Desktop.Commands.FooterLayout FooterLayout => Desktop.Commands.FooterLayout.FromStored(_footerPages);
        internal const int WorkViewSwitchNoticeSecondsMinimum = 0;
        internal const int WorkViewSwitchNoticeSecondsMaximum = 60;
        /// <summary>Whether entering Barebones may shrink the window; layout data itself is retained.</summary>
        [JsonConverter(typeof(DesktopPreferenceTrueBooleanConverter))]
        public bool ShrinkWindowForBarebones { get; set; } = true;
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool RememberReadingPosition { get; set; }
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool MonitorClipboardUrls { get; set; }
        public bool DownloadsPaused { get; set; }
        public bool MinimizeToTray { get; set; }
        public bool CloseToTray { get; set; }
        /// <summary>
        /// Shutdown time limit in seconds: the longest the app waits for its shutdown steps (downloads and watches stopping, the last
        /// settings, video players) after the window closed. A step still running then is logged by name and the process ends; every
        /// saved file is already complete. Read when the shutdown starts, so a change applies to the next close.
        /// </summary>
        public int ShutdownTimeLimitSeconds { get; set; } = ShutdownTimeLimitSecondsDefault;
        internal const int ShutdownTimeLimitSecondsDefault = 30;
        internal const int ShutdownTimeLimitSecondsMinimum = 5;
        internal const int ShutdownTimeLimitSecondsMaximum = 600;
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
        [JsonInclude]
        internal int LocalTaggerManifestKiB { get; set; } = LocalTaggerModelSettings.ManifestKiBRange.Default;
        [JsonInclude]
        internal int LocalTaggerWeightsMiB { get; set; } = LocalTaggerModelSettings.WeightsMiBRange.Default;
        [JsonInclude]
        internal int LocalTaggerInputTensorMiB { get; set; } = LocalTaggerModelSettings.InputTensorMiBRange.Default;

        internal LocalTaggerModelSettings LocalTaggerSettings() => new()
        {
            MaximumManifestKiB = LocalTaggerManifestKiB,
            MaximumWeightsMiB = LocalTaggerWeightsMiB,
            MaximumInputTensorMiB = LocalTaggerInputTensorMiB,
        };
        /// <summary>
        /// Write limit of the local Danbooru catalog (provider claims, findings, watchers) in MiB. Lowering it below the
        /// current size keeps the catalog readable and only stops it from growing.
        /// </summary>
        public int BooruCatalogLimitMiB { get; set; } = BooruCatalogDefaultLimitMiB;
        internal const int BooruCatalogDefaultLimitMiB = Archive.BooruCatalogStore.DefaultDocumentLimitBytes / (1024 * 1024);
        internal const int BooruCatalogMinimumLimitMiB = Archive.BooruCatalogStore.MinimumDocumentLimitBytes / (1024 * 1024);
        internal const int BooruCatalogMaximumLimitMiB = Archive.BooruCatalogStore.MaximumDocumentBytes / (1024 * 1024);
        /// <summary>Maximum provider-requested Retry-After cooldown for Danbooru, in hours.</summary>
        public int BooruRetryAfterCooldownHours { get; set; } = BooruRetryAfterCooldownDefaultHours;
        internal const int BooruRetryAfterCooldownDefaultHours = 24;
        internal const int BooruRetryAfterCooldownMinimumHours = 1;
        internal const int BooruRetryAfterCooldownMaximumHours = 168;
        /// <summary>Admission limit per include/exclude list. Never apply this preference when reading saved filters.</summary>
        public int ArchiveTagFilterTagsPerList { get; set; } = ArchiveTagFilterTagsPerListDefault;
        internal const int ArchiveTagFilterTagsPerListDefault = Archive.ArchiveTagFilter.DefaultTagsPerList;
        internal const int ArchiveTagFilterTagsPerListMinimum = Archive.ArchiveTagFilter.MinimumTagsPerList;
        internal const int ArchiveTagFilterTagsPerListMaximum = Archive.ArchiveTagFilter.MaximumConfigurableTagsPerList;
        /// <summary>Writer output budget in MiB; URL syntax/identity and path safety ceilings are independent.</summary>
        public int UrlTextExportLimitMiB { get; set; } = UrlTextExportLimitMiBDefault;
        internal const int UrlTextExportLimitMiBDefault = Desktop.UrlTextExportFileWriter.DefaultLimitMiB;
        internal const int UrlTextExportLimitMiBMinimum = Desktop.UrlTextExportFileWriter.MinimumLimitMiB;
        internal const int UrlTextExportLimitMiBMaximum = Desktop.UrlTextExportFileWriter.MaximumLimitMiB;
        public int ExportHistorySizeMiB { get; set; } = ExportHistorySizeMiBDefault;
        internal const int ExportHistorySizeMiBDefault = Persistence.ExportHistoryLimits.DefaultSizeMiB;
        internal const int ExportHistorySizeMiBMinimum = Persistence.ExportHistoryLimits.MinimumSizeMiB;
        internal const int ExportHistorySizeMiBMaximum = Persistence.ExportHistoryLimits.MaximumSizeMiB;
        public int ExportHistoryEntryLimit { get; set; } = ExportHistoryEntryLimitDefault;
        internal const int ExportHistoryEntryLimitDefault = Persistence.ExportHistoryLimits.DefaultEntries;
        internal const int ExportHistoryEntryLimitMinimum = Persistence.ExportHistoryLimits.MinimumEntries;
        internal const int ExportHistoryEntryLimitMaximum = Persistence.ExportHistoryLimits.MaximumEntries;
        public int BoardAdmissionSizeMiB { get; set; } = BoardAdmissionSizeMiBDefault;
        internal const int BoardAdmissionSizeMiBDefault = Admission.BoardAdmissionStore.DefaultSizeMiB;
        internal const int BoardAdmissionSizeMiBMinimum = Admission.BoardAdmissionStore.MinimumSizeMiB;
        internal const int BoardAdmissionSizeMiBMaximum = Admission.BoardAdmissionStore.MaximumSizeMiB;
        /// <summary>Most slash-marker suggestions offered, without changing the text/token scan ceilings.</summary>
        public int SlashTagSuggestionCount { get; set; } = SlashTagSuggestionCountDefault;
        internal const int SlashTagSuggestionCountDefault = Tags.SlashTagSuggestions.DefaultSuggestionCount;
        internal const int SlashTagSuggestionCountMinimum = Tags.SlashTagSuggestions.MinimumSuggestionCount;
        internal const int SlashTagSuggestionCountMaximum = Tags.SlashTagSuggestions.MaximumSuggestionCount;
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
        /// <summary>Most entries of the tagging queue. Lowering it below the current number keeps every entry; only adding is refused until the count is below the limit again.</summary>
        public int TagQueueLimit { get; set; } = TagQueueLimitDefault;
        internal const int TagQueueLimitDefault = Persistence.ProfileStoreLimits.DefaultTagQueueEntries;
        internal const int TagQueueLimitMinimum = Persistence.ProfileStoreLimits.MinimumTagQueueEntries;
        internal const int TagQueueLimitMaximum = Persistence.ProfileStoreLimits.TagQueueEntryCeiling;
        /// <summary>Write limit of tag-queue.json in MiB. Lowering it below the current size keeps the file readable; only writes that would make it larger are refused.</summary>
        public int TagQueueFileLimitMiB { get; set; } = TagQueueFileLimitMiBDefault;
        internal const int TagQueueFileLimitMiBDefault = Persistence.ProfileStoreLimits.DefaultTagQueueFileBytes / (1024 * 1024);
        internal const int TagQueueFileLimitMiBMinimum = Persistence.ProfileStoreLimits.MinimumTagQueueFileBytes / (1024 * 1024);
        internal const int TagQueueFileLimitMiBMaximum = Persistence.ProfileStoreLimits.TagQueueFileCeilingBytes / (1024 * 1024);
        /// <summary>How many finished entries the tagging queue keeps; older finished entries drop out. 0 keeps none. Open and skipped entries are never dropped.</summary>
        public int TagQueueHistory { get; set; } = TagQueueHistoryDefault;
        internal const int TagQueueHistoryDefault = Persistence.ProfileStoreLimits.DefaultTagQueueHistory;
        internal const int TagQueueHistoryMinimum = Persistence.ProfileStoreLimits.MinimumTagQueueHistory;
        internal const int TagQueueHistoryMaximum = Persistence.ProfileStoreLimits.TagQueueHistoryCeiling;
        /// <summary>Maximum named tagging groups. Lowering it keeps existing groups and only blocks new groups.</summary>
        public int TagQueueGroupLimit { get; set; } = TagQueueGroupLimitDefault;
        internal const int TagQueueGroupLimitDefault = Persistence.ProfileStoreLimits.DefaultTagQueueGroups;
        internal const int TagQueueGroupLimitMinimum = Persistence.ProfileStoreLimits.MinimumTagQueueGroups;
        internal const int TagQueueGroupLimitMaximum = Persistence.ProfileStoreLimits.TagQueueGroupCeiling;
        /// <summary>Maximum length of a newly created or renamed tagging group. Existing longer names remain readable.</summary>
        public int TagQueueGroupNameLimit { get; set; } = TagQueueGroupNameLimitDefault;
        internal const int TagQueueGroupNameLimitDefault = Persistence.ProfileStoreLimits.DefaultTagQueueGroupNameCharacters;
        internal const int TagQueueGroupNameLimitMinimum = Persistence.ProfileStoreLimits.MinimumTagQueueGroupNameCharacters;
        internal const int TagQueueGroupNameLimitMaximum = Persistence.ProfileStoreLimits.TagQueueGroupNameCeiling;
        /// <summary>Adding more media than this to the tagging queue at once asks for confirmation first. Nothing is cut short: after the confirmation every medium is added.</summary>
        public int TagQueueConfirmAbove { get; set; } = TagQueueConfirmAboveDefault;
        internal const int TagQueueConfirmAboveDefault = 200;
        internal const int TagQueueConfirmAboveMinimum = 10;
        internal const int TagQueueConfirmAboveMaximum = 10_000;
        /// <summary>MP4: how many tags each group of the media page's Booru-mode tag list shows before "Show all" (the rest stays one click away).</summary>
        public int MediaTagListTop { get; set; } = MediaTagListTopDefault;
        internal const int MediaTagListTopDefault = 30;
        internal const int MediaTagListTopMinimum = 5;
        internal const int MediaTagListTopMaximum = 1_000;
        /// <summary>E2: how many source tag chips a Danbooru result in the list shows before "+N more" (0 shows none until "+N more" is used).</summary>
        public int BooruResultTagsShown { get; set; } = BooruResultTagsShownDefault;
        internal const int BooruResultTagsShownDefault = 12;
        internal const int BooruResultTagsShownMinimum = 0;
        internal const int BooruResultTagsShownMaximum = 200;
        /// <summary>Destination used when submitting from the Booru search field.</summary>
        public string BooruNewSearchTarget { get; set; } = BooruSearchTargetNewTab;
        internal const string BooruSearchTargetNewTab = "NewTab";
        internal const string BooruSearchTargetCurrentTab = "CurrentTab";

        /// <summary>How many extra search tabs the Booru page may hold during the session.</summary>
        public int BooruSearchTabs { get; set; } = BooruSearchTabsDefault;
        internal const int BooruSearchTabsDefault = 8;
        internal const int BooruSearchTabsMinimum = 1;
        internal const int BooruSearchTabsMaximum = 32;
        public bool RestoreBooruSearchTabs { get; set; } = true;
        public int BooruSearchPages { get; set; } = BooruSearchPagesDefault;
        internal const int BooruSearchPagesDefault = BooruSearchPagesMaximum;
        internal const int BooruSearchPagesMinimum = 1;
        internal const int BooruSearchPagesMaximum = Desktop.Booru.BooruSearchModel.MaximumPage;
        public int BooruSearchPostsPerPage { get; set; } = BooruSearchPostsPerPageDefault;
        internal const int BooruSearchPostsPerPageDefault = BooruSearchPostsPerPageMaximum;
        internal const int BooruSearchPostsPerPageMinimum = 1;
        internal const int BooruSearchPostsPerPageMaximum = Desktop.Booru.BooruSearchModel.PageSize;
        public int BooruSavedTabs { get; set; } = BooruSavedTabsDefault;
        internal const int BooruSavedTabsDefault = BooruSearchTabsDefault + 1;
        internal const int BooruSavedTabsMinimum = 2;
        internal const int BooruSavedTabsMaximum = BooruSearchTabsMaximum + 1;
        public int BooruSavedResultsPerTab { get; set; } = BooruSavedResultsPerTabDefault;
        internal const int BooruSavedResultsPerTabDefault = BooruSavedResultsPerTabMaximum;
        internal const int BooruSavedResultsPerTabMinimum = 0;
        internal const int BooruSavedResultsPerTabMaximum = Desktop.Booru.BooruSearchModel.PageSize;

        /// <summary>The largest reduced-size image (sample) the inline Danbooru gallery loads for display, in MiB; a larger one is refused and the preview stays.</summary>
        public int BooruGallerySampleMiB { get; set; } = BooruGallerySampleMiBDefault;
        internal const int BooruGallerySampleMiBDefault = 8;
        internal const int BooruGallerySampleMiBMinimum = 1;
        internal const int BooruGallerySampleMiBMaximum = 32;
        /// <summary>How many MiB of loaded samples the inline Danbooru gallery keeps in memory so paging back does not fetch them again (never stored on disk).</summary>
        public int BooruGalleryCacheMiB { get; set; } = BooruGalleryCacheMiBDefault;
        internal const int BooruGalleryCacheMiBDefault = 32;
        internal const int BooruGalleryCacheMiBMinimum = 8;
        internal const int BooruGalleryCacheMiBMaximum = 256;
        /// <summary>MP5: the width of the media page's tag list in dip, set by dragging its edge (kept in <see cref="MediaLayout"/>, brought into this range when read).</summary>
        internal const int MediaTagListWidthDefault = 256;
        internal const int MediaTagListWidthMinimum = 180;
        internal const int MediaTagListWidthMaximum = 480;
        /// <summary>
        /// The media panel of the Media page (large medium beside the grid): its width in dip, set by dragging its edge or in Settings
        /// (kept in <see cref="MediaLayout"/> next to the other layout parts, brought into this range when read).
        /// </summary>
        internal const int MediaPanelWidthDefault = 360;
        internal const int MediaPanelWidthMinimum = 240;
        internal const int MediaPanelWidthMaximum = 900;
        /// <summary>The panel hides by itself while the Media page body is narrower than this many dip; the choice to show it stays.</summary>
        public int MediaPanelMinimumPageWidth { get; set; } = MediaPanelMinimumPageWidthDefault;
        internal const int MediaPanelMinimumPageWidthDefault = 1000;
        internal const int MediaPanelMinimumPageWidthMinimum = 600;
        internal const int MediaPanelMinimumPageWidthMaximum = 3000;
        /// <summary>Most entries in the list of known profiles (profiles.json, shared by all profiles). Lowering it keeps every entry; only new profiles are not listed.</summary>
        public int ProfileRegistryLimit { get; set; } = ProfileRegistryLimitDefault;
        internal const int ProfileRegistryLimitDefault = Persistence.ProfileRegistry.DefaultLimit;
        internal const int ProfileRegistryLimitMinimum = Persistence.ProfileRegistry.MinimumLimit;
        internal const int ProfileRegistryLimitMaximum = Persistence.ProfileRegistry.LimitCeiling;
        /// <summary>
        /// How many open entries of the tagging queue one walk opens at once (the gallery is built over them); the next walk continues behind them.
        /// Nothing is dropped from the queue: a smaller window only walks the queue in more rounds.
        /// </summary>
        public int TagQueueWalkWindow { get; set; } = TagQueueWalkWindowDefault;
        internal const int TagQueueWalkWindowDefault = 200;
        internal const int TagQueueWalkWindowMinimum = 10;
        internal const int TagQueueWalkWindowMaximum = 5_000;
        /// <summary>
        /// How many queue decisions (finish, skip) of a walk through the tagging queue can be taken back, newest first. Lowering it drops the
        /// oldest undo steps with the next decision; nothing in the queue changes. The undo history lives in memory only.
        /// </summary>
        public int TagQueueUndoDepth { get; set; } = TagQueueUndoDepthDefault;
        internal const int TagQueueUndoDepthDefault = Tags.TagQueueDecisionLog.DefaultDepth;
        internal const int TagQueueUndoDepthMinimum = Tags.TagQueueDecisionLog.MinimumDepth;
        internal const int TagQueueUndoDepthMaximum = Tags.TagQueueDecisionLog.MaximumDepth;
        /// <summary>
        /// Tags the stamp set of quick tagging holds (PT5). Lowering it cuts the stamp from its end; the cut names are gone, nothing else changes.
        /// </summary>
        public int TagStampSize { get; set; } = TagStampSizeDefault;
        internal const int TagStampSizeDefault = Desktop.QuickTagOptions.DefaultStampSize;
        internal const int TagStampSizeMinimum = Desktop.QuickTagOptions.MinimumStampSize;
        internal const int TagStampSizeMaximum = Desktop.QuickTagOptions.MaximumStampSize;
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
        /// <summary>Size limit of the seek-hover video scrub cache in MiB. It is separate from poster files and is recreated when entries are evicted.</summary>
        public int VideoScrubCacheLimitMiB { get; set; } = VideoScrubCacheLimitMiBDefault;
        internal const int VideoScrubCacheLimitMiBDefault = VideoScrubPreviewOptions.CacheLimitMiBDefault;
        internal const int VideoScrubCacheLimitMiBMinimum = VideoScrubPreviewOptions.CacheLimitMiBMinimum;
        internal const int VideoScrubCacheLimitMiBMaximum = VideoScrubPreviewOptions.CacheLimitMiBMaximum;
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
        /// <summary>What the graph in the header shows: "Auto", "Network", "Disk", "NetworkAndDisk" (both stacked), "SideBySide", "Text" or "Off" (hides the display and its measuring; see IoStatsDisplay.ResolveHeaderLayout). The statistics page always shows both.</summary>
        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string StatsHeaderGraph
        {
            get => _statsHeaderGraph;
            set => _statsHeaderGraph = Desktop.Stats.IoStatsDisplay.NormalizeHeaderGraph(value);
        }
        private string _statsHeaderGraph = Desktop.Stats.IoStatsDisplay.HeaderGraphAuto;
        /// <summary>Use coloured network/disk direction icons instead of words in the header. Off by default; the accessible description is unchanged.</summary>
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool StatsHeaderIcons { get; set; }
        /// <summary>What the header tagline does: "Off", "Fixed" (the quip of <see cref="HeaderTaglineFixedId"/>), "Daily" (one per day) or "PerStart" (a new one at every start, the default), "Interval" (a new one every <see cref="HeaderTaglineIntervalMinutes"/> minutes while the app runs); see TaglinePicker.</summary>
        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string HeaderTagline
        {
            get => _headerTagline;
            set => _headerTagline = Desktop.TaglinePicker.NormalizeMode(value);
        }
        private string _headerTagline = Desktop.TaglinePicker.ModePerStart;
        /// <summary>The quip the "Fixed" mode shows (a TaglineCatalog id; an id that no longer exists falls back to the classic one).</summary>
        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string HeaderTaglineFixedId
        {
            get => _headerTaglineFixedId;
            set => _headerTaglineFixedId = Desktop.TaglineCatalog.NormalizeId(value);
        }
        private string _headerTaglineFixedId = Desktop.TaglineCatalog.DefaultId;
        /// <summary>Minutes between two quips in the "Interval" mode of the header tagline (default 5, from 1 to 1440, one day).</summary>
        [JsonConverter(typeof(DesktopPreferenceHeaderTaglineIntervalMinutesConverter))]
        public int HeaderTaglineIntervalMinutes { get; set; } = HeaderTaglineIntervalMinutesDefault;
        internal const int HeaderTaglineIntervalMinutesDefault = 5, HeaderTaglineIntervalMinutesMinimum = 1, HeaderTaglineIntervalMinutesMaximum = 1440;
        /// <summary>The version (major.minor.build) whose release quip this profile has had its chance to see; empty until the first start with a tagline.</summary>
        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string LastTaglineVersion
        {
            get => _lastTaglineVersion;
            set => _lastTaglineVersion = Desktop.TaglineCatalog.NormalizeVersion(value);
        }
        private string _lastTaglineVersion = string.Empty;
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
        /// <summary>Open the nested reply cards of every post as soon as its card is shown (default off); a post the reader collapsed by hand stays collapsed for the open thread.</summary>
        public bool ReaderExpandReplies { get; set; } = false;
        public int ReaderPageSize { get; set; } = ReaderPageSizeDefault;
        internal const int ReaderPageSizeDefault = ArchivePage.ReaderPageSizeDefault;
        internal const int ReaderPageSizeMinimum = ArchivePage.ReaderPageSizeMinimum;
        internal const int ReaderPageSizeMaximum = ArchivePage.ReaderPageSizeMaximum;
        public int ReaderThreadingDepth { get; set; } = ReaderThreadingDepthDefault;
        internal const int ReaderThreadingDepthDefault = ReaderConversationTree.DepthDefault;
        internal const int ReaderThreadingDepthMinimum = ReaderConversationTree.DepthMinimum;
        internal const int ReaderThreadingDepthMaximum = ReaderConversationTree.DepthMaximum;
        // Reading history and reader media size (R5/R6): every number has default/minimum/maximum; the switches are quick view toggles too.
        public int ReadingHistoryLimit { get; set; } = ReadingHistoryLimitDefault;
        internal const int ReadingHistoryLimitDefault = ReaderDisplayPreferences.ReadingHistoryLimitDefault;
        internal const int ReadingHistoryLimitMinimum = ReaderDisplayPreferences.ReadingHistoryLimitMinimum;
        internal const int ReadingHistoryLimitMaximum = ReaderDisplayPreferences.ReadingHistoryLimitMaximum;
        // RH1: threads listed by the right-click menu of "Continue reading" (never more than the history keeps).
        public int ReadingHistoryMenuEntries { get; set; } = ReadingHistoryMenuEntriesDefault;
        internal const int ReadingHistoryMenuEntriesDefault = ReaderDisplayPreferences.ReadingHistoryMenuEntriesDefault;
        internal const int ReadingHistoryMenuEntriesMinimum = ReaderDisplayPreferences.ReadingHistoryMenuEntriesMinimum;
        internal const int ReadingHistoryMenuEntriesMaximum = ReaderDisplayPreferences.ReadingHistoryMenuEntriesMaximum;
        public int ReaderTextlessMediaWidth { get; set; } = ReaderTextlessMediaWidthDefault;
        internal const int ReaderTextlessMediaWidthDefault = ReaderDisplayPreferences.TextlessMediaWidthDefault;
        internal const int ReaderTextlessMediaWidthMinimum = ReaderDisplayPreferences.TextlessMediaWidthMinimum;
        internal const int ReaderTextlessMediaWidthMaximum = ReaderDisplayPreferences.TextlessMediaWidthMaximum;
        public int ReaderHoverEnlargeSize { get; set; } = ReaderHoverEnlargeSizeDefault;
        internal const int ReaderHoverEnlargeSizeDefault = ReaderDisplayPreferences.HoverEnlargeSizeDefault;
        internal const int ReaderHoverEnlargeSizeMinimum = ReaderDisplayPreferences.HoverEnlargeSizeMinimum;
        internal const int ReaderHoverEnlargeSizeMaximum = ReaderDisplayPreferences.HoverEnlargeSizeMaximum;
        public int ReaderHoverEnlargeDelayMs { get; set; } = ReaderHoverEnlargeDelayMsDefault;
        internal const int ReaderHoverEnlargeDelayMsDefault = ReaderDisplayPreferences.HoverEnlargeDelayDefault;
        internal const int ReaderHoverEnlargeDelayMsMinimum = ReaderDisplayPreferences.HoverEnlargeDelayMinimum;
        internal const int ReaderHoverEnlargeDelayMsMaximum = ReaderDisplayPreferences.HoverEnlargeDelayMaximum;
        public int ReaderFocusedMediaScalePercent { get; set; } = ReaderFocusedMediaScalePercentDefault;
        internal const int ReaderFocusedMediaScalePercentDefault = ReaderDisplayPreferences.FocusedMediaScaleDefault;
        internal const int ReaderFocusedMediaScalePercentMinimum = ReaderDisplayPreferences.FocusedMediaScaleMinimum;
        internal const int ReaderFocusedMediaScalePercentMaximum = ReaderDisplayPreferences.FocusedMediaScaleMaximum;
        public bool ReaderTextlessLargeImages { get; set; } = true;
        public bool ReaderHoverEnlarge { get; set; } = false;
        public bool ReaderFocusedMediaEnlarge { get; set; } = false;
        // The media sidebar of the large reader (R8): on by default (Tom, 15.11.2026; a saved choice stays), switched in the View menu or Settings, on the left unless set to the right.
        public bool ReaderMediaSidebar { get; set; } = true;
        private string _readerMediaSidebarSide = ReaderMediaSidebarSideLeft;
        internal const string ReaderMediaSidebarSideLeft = "Left", ReaderMediaSidebarSideRight = "Right";
        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string ReaderMediaSidebarSide
        {
            get => _readerMediaSidebarSide;
            set => _readerMediaSidebarSide = string.Equals(value?.Trim(), ReaderMediaSidebarSideRight, StringComparison.OrdinalIgnoreCase)
                ? ReaderMediaSidebarSideRight : ReaderMediaSidebarSideLeft;
        }
        // Where the switched-on sidebar shows (15.11.2026): only in the large reader (default, the behaviour before this choice existed) or also
        // beside the posts of the archive view. With ReaderMediaSidebar it is the one choice Off / Large reader only / Always.
        private string _readerMediaSidebarScope = ReaderMediaSidebarScopeLargeReader;
        internal const string ReaderMediaSidebarScopeLargeReader = "LargeReader", ReaderMediaSidebarScopeAlways = "Always";
        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string ReaderMediaSidebarScope
        {
            get => _readerMediaSidebarScope;
            set => _readerMediaSidebarScope = string.Equals(value?.Trim(), ReaderMediaSidebarScopeAlways, StringComparison.OrdinalIgnoreCase)
                ? ReaderMediaSidebarScopeAlways : ReaderMediaSidebarScopeLargeReader;
        }
        public int ReaderMediaSidebarWidth { get; set; } = ReaderMediaSidebarWidthDefault;
        internal const int ReaderMediaSidebarWidthDefault = ReaderDisplayPreferences.MediaSidebarWidthDefault;
        internal const int ReaderMediaSidebarWidthMinimum = ReaderDisplayPreferences.MediaSidebarWidthMinimum;
        internal const int ReaderMediaSidebarWidthMaximum = ReaderDisplayPreferences.MediaSidebarWidthMaximum;
        public int ReaderMediaSidebarMinimumWidth { get; set; } = ReaderMediaSidebarMinimumWidthDefault;
        internal const int ReaderMediaSidebarMinimumWidthDefault = ReaderDisplayPreferences.MediaSidebarReaderWidthDefault;
        internal const int ReaderMediaSidebarMinimumWidthMinimum = ReaderDisplayPreferences.MediaSidebarReaderWidthMinimum;
        internal const int ReaderMediaSidebarMinimumWidthMaximum = ReaderDisplayPreferences.MediaSidebarReaderWidthMaximum;
        // Inline video in the reader (R10): off unless chosen; then a click or Enter plays a video in its post or in the media sidebar.
        // Autoplay (muted) is a second opt-in. The number of live players and the largest file played inline are limits.
        // Since 15.11.2026 this is the merged setting "Open media in the post" (videos play, images expand there): the choice "Post" of
        // ReaderMediaClick. It keeps its stored name so that a saved "on" carries over; ReaderMediaClick wins when it was chosen.
        public bool ReaderInlineVideo { get; set; } = false;
        // "Clicking a medium" in the reader: Sidebar (default; the gallery while the sidebar is closed), Post (play or expand in the
        // post) or Gallery. Empty = not chosen yet: the old ReaderInlineVideo decides (true: Post, else Sidebar).
        private string _readerMediaClick = string.Empty;
        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string ReaderMediaClick
        {
            get => _readerMediaClick;
            set => _readerMediaClick = ReaderMediaClickRules.Normalize(value);
        }
        internal ReaderMediaClickMode EffectiveReaderMediaClick => ReaderMediaClickRules.Effective(_readerMediaClick, ReaderInlineVideo);
        /// <summary>Chooses how a click on a medium works and keeps the old inline video flag in step with it (older builds read that one).</summary>
        internal void ChooseReaderMediaClick(ReaderMediaClickMode mode)
        {
            _readerMediaClick = ReaderMediaClickRules.ToKey(mode);
            ReaderInlineVideo = mode == ReaderMediaClickMode.Post;
        }
        public bool ReaderInlineVideoAutoplayMuted { get; set; } = false;
        /// <summary>"Open in the post": an image expanded in its post stays expanded when it scrolls out of the view and back, and when the posts are rebuilt (open thread only). Off: it goes back to its thumbnail.</summary>
        public bool ReaderKeepImagesExpanded { get; set; } = false;
        // "Videos in the media sidebar" (the one viewer of the reader, the large reader, the archive view and the Media page's panel): its own three
        // switches, apart from the videos in posts. Autoplay and loop are on by default (a file without these keys, an older profile, gets them too);
        // start muted is off, so the sound follows the remembered volume. The volume itself is the gallery's (VideoVolume).
        [JsonConverter(typeof(DesktopPreferenceTrueBooleanConverter))]
        public bool ReaderSidebarVideoAutoplay { get; set; } = true;
        [JsonConverter(typeof(DesktopPreferenceTrueBooleanConverter))]
        public bool ReaderSidebarVideoLoop { get; set; } = true;
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool ReaderSidebarVideoStartMuted { get; set; }
        // The quote preview's medium (Tom, 16.11.2026): a video or GIF in it starts by itself and repeats, a video muted. Both on by default (a file
        // without these keys, an older profile, gets them too); off shows the still picture, or plays with the remembered volume (VideoVolume).
        [JsonConverter(typeof(DesktopPreferenceTrueBooleanConverter))]
        public bool ReaderQuotePreviewAutoplay { get; set; } = true;
        [JsonConverter(typeof(DesktopPreferenceTrueBooleanConverter))]
        public bool ReaderQuotePreviewMuted { get; set; } = true;
        public int ReaderInlineVideoMaxPlayers { get; set; } = ReaderInlineVideoMaxPlayersDefault;
        internal const int ReaderInlineVideoMaxPlayersDefault = ReaderInlineVideoOptions.MaxPlayersDefault;
        internal const int ReaderInlineVideoMaxPlayersMinimum = ReaderInlineVideoOptions.MaxPlayersMinimum;
        internal const int ReaderInlineVideoMaxPlayersMaximum = ReaderInlineVideoOptions.MaxPlayersMaximum;
        public int ReaderInlineVideoMaxFileMiB { get; set; } = ReaderInlineVideoMaxFileMiBDefault;
        internal const int ReaderInlineVideoMaxFileMiBDefault = ReaderInlineVideoOptions.MaxFileMiBDefault;
        internal const int ReaderInlineVideoMaxFileMiBMinimum = ReaderInlineVideoOptions.MaxFileMiBMinimum;
        internal const int ReaderInlineVideoMaxFileMiBMaximum = ReaderInlineVideoOptions.MaxFileMiBMaximum;
        // Several media in one post (R11): the sidebar's thumbnail strip shows at most this many files at once (a larger post slides a
        // window along); posts with a little text show their images as a two-column grid instead of one long column.
        public int ReaderMediaStripLimit { get; set; } = ReaderMediaStripLimitDefault;
        internal const int ReaderMediaStripLimitDefault = ReaderDisplayPreferences.MediaStripLimitDefault;
        internal const int ReaderMediaStripLimitMinimum = ReaderDisplayPreferences.MediaStripLimitMinimum;
        internal const int ReaderMediaStripLimitMaximum = ReaderDisplayPreferences.MediaStripLimitMaximum;
        public bool ReaderMediaGrid { get; set; } = true;
        public int ReaderMediaGridTextLimit { get; set; } = ReaderMediaGridTextLimitDefault;
        internal const int ReaderMediaGridTextLimitDefault = ReaderDisplayPreferences.MediaGridTextLimitDefault;
        internal const int ReaderMediaGridTextLimitMinimum = ReaderDisplayPreferences.MediaGridTextLimitMinimum;
        internal const int ReaderMediaGridTextLimitMaximum = ReaderDisplayPreferences.MediaGridTextLimitMaximum;
        /// <summary>Posts before and after the visible ones whose previews the reader decodes ahead; 0 turns preloading off.</summary>
        public int ReaderPreviewPrefetchPosts { get; set; } = ReaderPreviewPrefetchPostsDefault;
        internal const int ReaderPreviewPrefetchPostsDefault = ReaderPreviewReservation.PrefetchPostsDefault;
        internal const int ReaderPreviewPrefetchPostsMinimum = ReaderPreviewReservation.PrefetchPostsMinimum;
        internal const int ReaderPreviewPrefetchPostsMaximum = ReaderPreviewReservation.PrefetchPostsMaximum;
        /// <summary>Preview sizes the reader remembers for this session, so posts are reserved at their final size at once.</summary>
        public int ReaderPreviewSizeMemory { get; set; } = ReaderPreviewSizeMemoryDefault;
        internal const int ReaderPreviewSizeMemoryDefault = ReaderPreviewReservation.SizeMemoryDefault;
        internal const int ReaderPreviewSizeMemoryMinimum = ReaderPreviewReservation.SizeMemoryMinimum;
        internal const int ReaderPreviewSizeMemoryMaximum = ReaderPreviewReservation.SizeMemoryMaximum;
        public int GalleryFitMaxUpscalePercent { get; set; } = GalleryFitMaxUpscalePercentDefault;
        internal const int GalleryFitMaxUpscalePercentDefault = GalleryFitPreferences.MaxUpscalePercentDefault;
        internal const int GalleryFitMaxUpscalePercentMinimum = GalleryFitPreferences.MaxUpscalePercentMinimum;
        internal const int GalleryFitMaxUpscalePercentMaximum = GalleryFitPreferences.MaxUpscalePercentMaximum;
        public int GalleryMaxZoomPercent { get; set; } = GalleryMaxZoomPercentDefault;
        internal const int GalleryMaxZoomPercentDefault = GalleryZoomLimits.MaxPercentDefault;
        internal const int GalleryMaxZoomPercentMinimum = GalleryZoomLimits.MaxPercentMinimum;
        internal const int GalleryMaxZoomPercentMaximum = GalleryZoomLimits.MaxPercentMaximum;
        public int PostFilterRuleLimit { get; set; } = PostFilterRuleLimitDefault;
        internal const int PostFilterRuleLimitDefault = ProfileStoreLimits.DefaultPostFilterRules;
        internal const int PostFilterRuleLimitMinimum = ProfileStoreLimits.MinimumPostFilterRules;
        internal const int PostFilterRuleLimitMaximum = ProfileStoreLimits.PostFilterRuleCeiling;
        public int HiddenPostLimit { get; set; } = HiddenPostLimitDefault;
        internal const int HiddenPostLimitDefault = ProfileStoreLimits.DefaultHiddenPosts;
        internal const int HiddenPostLimitMinimum = ProfileStoreLimits.MinimumHiddenPosts;
        internal const int HiddenPostLimitMaximum = ProfileStoreLimits.HiddenPostCeiling;
        // Quick tagging (gallery tag panel and tag popover). The limits come from the quick tag types; a value outside the range is
        // reduced on load and refused by Validate, like the reader display limits.
        /// <summary>Suggestion chips the quick tag surface shows while the field is empty (more than nine have no number key).</summary>
        public int QuickTagSuggestionCount { get; set; } = QuickTagSuggestionCountDefault;
        internal const int QuickTagSuggestionCountDefault = Desktop.QuickTagOptions.DefaultSuggestionCount;
        internal const int QuickTagSuggestionCountMinimum = Desktop.QuickTagOptions.MinimumSuggestionCount;
        internal const int QuickTagSuggestionCountMaximum = Desktop.QuickTagOptions.MaximumSuggestionCount;
        /// <summary>The most suggestion chips the gallery tag panel asks for when its height lets it show more than <see cref="QuickTagSuggestionCount"/>.</summary>
        public int QuickTagPanelSuggestionLimit { get; set; } = QuickTagPanelSuggestionLimitDefault;
        internal const int QuickTagPanelSuggestionLimitDefault = Desktop.QuickTagOptions.DefaultPanelSuggestionLimit;
        internal const int QuickTagPanelSuggestionLimitMinimum = Desktop.QuickTagOptions.MinimumPanelSuggestionLimit;
        internal const int QuickTagPanelSuggestionLimitMaximum = Desktop.QuickTagOptions.MaximumPanelSuggestionLimit;
        /// <summary>Own tags the quick tag surface remembers as recently used in this session; 0 switches the recent-tags hint off.</summary>
        public int QuickTagRecentCount { get; set; } = QuickTagRecentCountDefault;
        internal const int QuickTagRecentCountDefault = Desktop.TagContextOptions.DefaultRecentCount;
        internal const int QuickTagRecentCountMinimum = Desktop.TagContextOptions.MinimumRecentCount;
        internal const int QuickTagRecentCountMaximum = Desktop.TagContextOptions.MaximumRecentCount;
        /// <summary>Undo steps each quick tag surface keeps.</summary>
        public int QuickTagUndoDepth { get; set; } = QuickTagUndoDepthDefault;
        internal const int QuickTagUndoDepthDefault = Desktop.QuickTagOptions.DefaultUndoDepth;
        internal const int QuickTagUndoDepthMinimum = Desktop.QuickTagOptions.MinimumUndoDepth;
        internal const int QuickTagUndoDepthMaximum = Desktop.QuickTagOptions.MaximumUndoDepth;
        /// <summary>Neighbouring media entries the quick tag surface looks at for suggestions around the shown medium.</summary>
        public int QuickTagNeighbourScan { get; set; } = QuickTagNeighbourScanDefault;
        internal const int QuickTagNeighbourScanDefault = Desktop.TagContextOptions.DefaultNeighbourScan;
        internal const int QuickTagNeighbourScanMinimum = Desktop.TagContextOptions.MinimumNeighbourScan;
        internal const int QuickTagNeighbourScanMaximum = Desktop.TagContextOptions.MaximumNeighbourScan;
        /// <summary>Words of the file name the quick tag surface matches against own tags; 0 switches the file name hint off.</summary>
        public int QuickTagFilenameTokens { get; set; } = QuickTagFilenameTokensDefault;
        internal const int QuickTagFilenameTokensDefault = Desktop.TagContextOptions.DefaultFilenameTokens;
        internal const int QuickTagFilenameTokensMinimum = Desktop.TagContextOptions.MinimumFilenameTokens;
        internal const int QuickTagFilenameTokensMaximum = Desktop.TagContextOptions.MaximumFilenameTokens;
        /// <summary>Source tags per medium the quick tag surface offers as new own tags; 0 switches them off.</summary>
        public int QuickTagSourceTagsPerMedium { get; set; } = QuickTagSourceTagsPerMediumDefault;
        internal const int QuickTagSourceTagsPerMediumDefault = Desktop.TagContextOptions.DefaultSourceTagsPerMedium;
        internal const int QuickTagSourceTagsPerMediumMinimum = Desktop.TagContextOptions.MinimumSourceTagsPerMedium;
        internal const int QuickTagSourceTagsPerMediumMaximum = Desktop.TagContextOptions.MaximumSourceTagsPerMedium;
        /// <summary>Media files the gallery can mark for one tag change; the write itself stays under the fixed assignment limit of the tag store.</summary>
        public int QuickTagMarkedMediaLimit { get; set; } = QuickTagMarkedMediaLimitDefault;
        internal const int QuickTagMarkedMediaLimitDefault = 500, QuickTagMarkedMediaLimitMinimum = 10, QuickTagMarkedMediaLimitMaximum = 5000;
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
        // The order of the media page (MediaSortPreference): a criterion name and "Default"/"Reversed". The random order is never written; the last other choice stays.
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
        // MP4: the Booru mode of the media page (the tag list beside the grid) and the order of that list ("Count" or "Name").
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool MediaBooruMode { get; set; }
        private string _mediaTagListSort = nameof(Desktop.MediaTagListSort.Count);
        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string MediaTagListSort
        {
            get => _mediaTagListSort;
            set => _mediaTagListSort = Enum.TryParse(value?.Trim(), true, out Desktop.MediaTagListSort sort) && Enum.IsDefined(sort) ? sort.ToString() : nameof(Desktop.MediaTagListSort.Count);
        }
        // MP5: the rest of the media page's layout (tag list side and width, filter chips, status line, subtitle, tile caption, tile size) as one short
        // text (Desktop/MediaPageLayout.cs); the tag list switch is MediaBooruMode above (the key predates the name "Tag list"). A damaged text
        // reads as the standard layout for the damaged parts, so it never makes the settings file invalid.
        private string _mediaLayout = Desktop.MediaPageLayout.Standard.Format();
        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string MediaLayout
        {
            get => _mediaLayout;
            set => _mediaLayout = Desktop.MediaPageLayout.Canonical(value);
        }
        // The tile size (Small, Medium, Large) of the archive's saved-media tiles (the reader's Media tab, tile view); a view preference of the profile.
        // The media page and the tagging grid share the size inside MediaLayout; the archive tiles are another surface with their own measures. An
        // unknown or empty text reads as Medium, so a damaged entry never makes the settings file invalid.
        private string _archiveMediaTileSize = nameof(Desktop.MediaTileSize.Medium);
        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string ArchiveMediaTileSize
        {
            get => _archiveMediaTileSize;
            set => _archiveMediaTileSize = Desktop.Controls.MediaTileSizes.Parse(value).ToString();
        }
        // E1: the Booru page shows its results as a list or as tiles ("List" or "Grid") and the tiles in a size (Small, Medium, Large); view
        // preferences of the profile. Unknown or empty text reads as the list and Medium, so a damaged entry never makes the settings file invalid.
        private string _booruPageView = nameof(Desktop.Booru.BooruResultView.List);
        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string BooruPageView
        {
            get => _booruPageView;
            set => _booruPageView = Desktop.Booru.BooruResultViews.Parse(value).ToString();
        }
        private string _booruTileSize = nameof(Desktop.MediaTileSize.Medium);
        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string BooruTileSize
        {
            get => _booruTileSize;
            set => _booruTileSize = Desktop.Controls.MediaTileSizes.Parse(value).ToString();
        }
        // TG1: the tagging page shows its queue as a list or as a grid of thumbnails ("List" or "Grid"); a view preference of the profile.
        private string _taggingPageView = nameof(TaggingViewMode.List);
        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string TaggingPageView
        {
            get => _taggingPageView;
            set => _taggingPageView = TaggingViewModes.Parse(value).ToString();
        }
        // C16: the list view's preview column can be hidden without changing the queue data.
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool TaggingPreviewColumn { get; set; } = true;
        // TG1: the stamp block of the tagging page: "Auto" (follows the work view: closed in Barebones), "Open" or "Closed" once chosen.
        private string _taggingStampBlock = nameof(TaggingStampBlockState.Auto);
        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string TaggingStampBlock
        {
            get => _taggingStampBlock;
            set => _taggingStampBlock = TaggingStampBlocks.Parse(value).ToString();
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

        // Read-only folder sources (PLAN-POWER-TAGGEN PR2/PR3, E3). Every limit is a setting; the catalog enforces fixed ceilings
        // (2,000,000 items per profile, 1,024 characters per path).
        /// <summary>Most files over all folder sources (missing ones do not count); lowering it keeps every file, a scan only adds no more.</summary>
        public int FolderSourceMaxFiles { get; set; } = FolderSources.FolderSourceCatalog.DefaultMaxFiles;
        /// <summary>Most folder sources; lowering it keeps every source, only new ones are refused.</summary>
        public int FolderSourceMaxRoots { get; set; } = FolderSources.FolderSourceCatalog.DefaultMaxRoots;
        /// <summary>Files hashed at the same time.</summary>
        public int FolderSourceHashParallelism { get; set; } = FolderSources.FolderSourceHasher.DefaultParallelism;
        /// <summary>Largest file hashed without a user action (viewing, background), in GiB; a larger one is hashed when you favorite, rate or tag it.</summary>
        public int FolderSourceHashMaxFileGiB { get; set; } = FolderSources.FolderSourceHasher.DefaultMaxFileGiB;
        /// <summary>Minutes between automatic rescans of every folder source; 0 = only by hand.</summary>
        public int FolderSourceRescanMinutes { get; set; }
        /// <summary>Size limit of the folder source catalog (folder-sources.db) in MiB; lowering it only stops growth.</summary>
        public int FolderSourceCatalogLimitMiB { get; set; } = FolderSources.FolderSourceCatalog.DefaultCatalogLimitMiB;
        /// <summary>Unhashed folder files on each side of the opened one that the gallery hashes when it opens; farther ones are left out until hashed.</summary>
        public int FolderSourceGalleryHashWindow { get; set; } = FolderSourceGalleryHashWindowDefault;
        /// <summary>Whether unhashed folder files are hashed in the background while you are idle.</summary>
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool FolderSourceBackgroundHashing { get; set; } = true;
        internal const int FolderSourceMaxFilesMinimum = FolderSources.FolderSourceCatalog.MinimumMaxFiles, FolderSourceMaxFilesMaximum = FolderSources.FolderSourceCatalog.MaxFilesCeiling;
        internal const int FolderSourceMaxRootsMinimum = FolderSources.FolderSourceCatalog.MinimumMaxRoots, FolderSourceMaxRootsMaximum = FolderSources.FolderSourceCatalog.MaxRootsCeiling;
        internal const int FolderSourceHashParallelismMinimum = FolderSources.FolderSourceHasher.MinimumParallelism, FolderSourceHashParallelismMaximum = FolderSources.FolderSourceHasher.ParallelismCeiling;
        internal const int FolderSourceHashMaxFileGiBMinimum = FolderSources.FolderSourceHasher.MinimumMaxFileGiB, FolderSourceHashMaxFileGiBMaximum = FolderSources.FolderSourceHasher.MaxFileGiBCeiling;
        internal const int FolderSourceRescanMinutesMinimum = 0, FolderSourceRescanMinutesMaximum = 1_440;
        internal const int FolderSourceCatalogLimitMiBMinimum = FolderSources.FolderSourceCatalog.MinimumCatalogLimitMiB, FolderSourceCatalogLimitMiBMaximum = FolderSources.FolderSourceCatalog.CatalogLimitMiBCeiling;
        internal const int FolderSourceGalleryHashWindowDefault = 16, FolderSourceGalleryHashWindowMinimum = 0, FolderSourceGalleryHashWindowMaximum = 200;

        public string? PluginEditorExecutable { get; set; }
        public string[] PluginEditorArguments { get; set; } = ["{file}"];

        /// <summary>
        /// The stamp set of quick tagging (PT5): own tag names in stamp order that every tag surface offers first and gives to the
        /// shown medium with one key. A direct view preference (saved when the stamp changes), not a limit; its length is bounded by
        /// the stamp size, and invalid or repeated names are dropped when it is read (<c>QuickTagStamp</c>). Replaced by the stamp of
        /// <c>tag-queue.json</c> once the tagging queue ships.
        /// </summary>
        [JsonConverter(typeof(DesktopPreferenceNameListConverter))]
        public string[] TagStamp { get => _tagStamp; set => _tagStamp = value ?? []; }
        private string[] _tagStamp = [];

        /// <summary>
        /// Card mode of the tagging mode (PT4): a walk starts in the card mode (the choice the person made with K last). A direct view
        /// preference, saved when the mode is toggled.
        /// </summary>
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool TaggingCardsDefault { get; set; }

        /// <summary>
        /// The cards of the card mode, in order (key 1 first): any of back, skip, done, favorite, stamp, rate-1 to rate-5. Empty is the default
        /// set; unknown and repeated names are dropped and a short list is filled up (<c>TaggingCardSettings</c>).
        /// </summary>
        [JsonConverter(typeof(DesktopPreferenceNameListConverter))]
        public string[] TaggingCards { get => _taggingCards; set => _taggingCards = value ?? []; }
        private string[] _taggingCards = [];

        /// <summary>How many of the cards are shown (default 5, minimum 2, maximum 5: one key each); an out-of-range value is clamped.</summary>
        [JsonConverter(typeof(DesktopPreferenceTaggingCardCountConverter))]
        public int TaggingCardCount
        {
            get => _taggingCardCount;
            set => _taggingCardCount = Math.Clamp(value, TaggingCardCountMinimum, TaggingCardCountMaximum);
        }
        private int _taggingCardCount = TaggingCardCountDefault;
        internal const int TaggingCardCountDefault = 5, TaggingCardCountMinimum = 2, TaggingCardCountMaximum = TaggingCardCommands.CardKeys;


        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string Theme
        {
            get => _theme;
            set => _theme = string.Equals(value?.Trim(), "Light", StringComparison.OrdinalIgnoreCase) ? "Light"
                : string.Equals(value?.Trim(), "System", StringComparison.OrdinalIgnoreCase) ? "System" : "Dark";
        }

        /// <summary>
        /// TH2: the user's colour overrides per base theme, <c>{ "Light": { seed: "#RRGGBB" | "#AARRGGBB" }, "Dark": { … } }</c>.
        /// Each variant object has the shape of a future theme.json "seeds" object (keep them identical). Unknown or non-seed keys
        /// and malformed colours are dropped on load with a log warning; see <see cref="Desktop.Theming.ThemeColorOverrides"/>.
        /// Applied live by the window through ThemeRuntime.ApplyOverrides, which refuses a set below 4.5 : 1 text contrast.
        /// </summary>
        [JsonConverter(typeof(Desktop.Theming.ThemeColorOverridesConverter))]
        public Desktop.Theming.ThemeColorOverrides ThemeOverrides
        {
            get => _themeOverrides;
            set => _themeOverrides = value ?? Desktop.Theming.ThemeColorOverrides.Empty;
        }
        private Desktop.Theming.ThemeColorOverrides _themeOverrides = Desktop.Theming.ThemeColorOverrides.Empty;

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

        void IJsonOnDeserializing.OnDeserializing() => PreferenceDefaultsRevision = 0;

        void IJsonOnDeserialized.OnDeserialized()
        {
            MigratePreferenceDefaults();
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
            ReadingHistoryLimit = Math.Clamp(ReadingHistoryLimit, ReadingHistoryLimitMinimum, ReadingHistoryLimitMaximum);
            ReadingHistoryMenuEntries = Math.Clamp(ReadingHistoryMenuEntries, ReadingHistoryMenuEntriesMinimum, ReadingHistoryMenuEntriesMaximum);
            ReaderTextlessMediaWidth = Math.Clamp(ReaderTextlessMediaWidth, ReaderTextlessMediaWidthMinimum, ReaderTextlessMediaWidthMaximum);
            ReaderHoverEnlargeSize = Math.Clamp(ReaderHoverEnlargeSize, ReaderHoverEnlargeSizeMinimum, ReaderHoverEnlargeSizeMaximum);
            ReaderHoverEnlargeDelayMs = Math.Clamp(ReaderHoverEnlargeDelayMs, ReaderHoverEnlargeDelayMsMinimum, ReaderHoverEnlargeDelayMsMaximum);
            ReaderFocusedMediaScalePercent = Math.Clamp(ReaderFocusedMediaScalePercent, ReaderFocusedMediaScalePercentMinimum, ReaderFocusedMediaScalePercentMaximum);
            ReaderMediaSidebarWidth = Math.Clamp(ReaderMediaSidebarWidth, ReaderMediaSidebarWidthMinimum, ReaderMediaSidebarWidthMaximum);
            ReaderMediaSidebarMinimumWidth = Math.Clamp(ReaderMediaSidebarMinimumWidth, ReaderMediaSidebarMinimumWidthMinimum, ReaderMediaSidebarMinimumWidthMaximum);
            ReaderInlineVideoMaxPlayers = Math.Clamp(ReaderInlineVideoMaxPlayers, ReaderInlineVideoMaxPlayersMinimum, ReaderInlineVideoMaxPlayersMaximum);
            ReaderInlineVideoMaxFileMiB = Math.Clamp(ReaderInlineVideoMaxFileMiB, ReaderInlineVideoMaxFileMiBMinimum, ReaderInlineVideoMaxFileMiBMaximum);
            ReaderMediaStripLimit = Math.Clamp(ReaderMediaStripLimit, ReaderMediaStripLimitMinimum, ReaderMediaStripLimitMaximum);
            ReaderMediaGridTextLimit = Math.Clamp(ReaderMediaGridTextLimit, ReaderMediaGridTextLimitMinimum, ReaderMediaGridTextLimitMaximum);
            ReaderPreviewPrefetchPosts = Math.Clamp(ReaderPreviewPrefetchPosts, ReaderPreviewPrefetchPostsMinimum, ReaderPreviewPrefetchPostsMaximum);
            ReaderPreviewSizeMemory = Math.Clamp(ReaderPreviewSizeMemory, ReaderPreviewSizeMemoryMinimum, ReaderPreviewSizeMemoryMaximum);
            GalleryFitMaxUpscalePercent = Math.Clamp(GalleryFitMaxUpscalePercent, GalleryFitMaxUpscalePercentMinimum, GalleryFitMaxUpscalePercentMaximum);
            GalleryMaxZoomPercent = Math.Clamp(GalleryMaxZoomPercent, GalleryMaxZoomPercentMinimum, GalleryMaxZoomPercentMaximum);
            PostFilterRuleLimit = Math.Clamp(PostFilterRuleLimit, PostFilterRuleLimitMinimum, PostFilterRuleLimitMaximum);
            HiddenPostLimit = Math.Clamp(HiddenPostLimit, HiddenPostLimitMinimum, HiddenPostLimitMaximum);
            QuickTagSuggestionCount = Math.Clamp(QuickTagSuggestionCount, QuickTagSuggestionCountMinimum, QuickTagSuggestionCountMaximum);
            QuickTagPanelSuggestionLimit = Math.Clamp(QuickTagPanelSuggestionLimit, QuickTagPanelSuggestionLimitMinimum, QuickTagPanelSuggestionLimitMaximum);
            QuickTagRecentCount = Math.Clamp(QuickTagRecentCount, QuickTagRecentCountMinimum, QuickTagRecentCountMaximum);
            QuickTagUndoDepth = Math.Clamp(QuickTagUndoDepth, QuickTagUndoDepthMinimum, QuickTagUndoDepthMaximum);
            QuickTagNeighbourScan = Math.Clamp(QuickTagNeighbourScan, QuickTagNeighbourScanMinimum, QuickTagNeighbourScanMaximum);
            QuickTagFilenameTokens = Math.Clamp(QuickTagFilenameTokens, QuickTagFilenameTokensMinimum, QuickTagFilenameTokensMaximum);
            QuickTagSourceTagsPerMedium = Math.Clamp(QuickTagSourceTagsPerMedium, QuickTagSourceTagsPerMediumMinimum, QuickTagSourceTagsPerMediumMaximum);
            QuickTagMarkedMediaLimit = Math.Clamp(QuickTagMarkedMediaLimit, QuickTagMarkedMediaLimitMinimum, QuickTagMarkedMediaLimitMaximum);
            VideoScrubCacheLimitMiB = Math.Clamp(VideoScrubCacheLimitMiB, VideoScrubCacheLimitMiBMinimum, VideoScrubCacheLimitMiBMaximum);
            ArchiveTagFilterTagsPerList = Math.Clamp(ArchiveTagFilterTagsPerList, ArchiveTagFilterTagsPerListMinimum, ArchiveTagFilterTagsPerListMaximum);
            UrlTextExportLimitMiB = Math.Clamp(UrlTextExportLimitMiB, UrlTextExportLimitMiBMinimum, UrlTextExportLimitMiBMaximum);
            ExportHistorySizeMiB = Math.Clamp(ExportHistorySizeMiB, ExportHistorySizeMiBMinimum, ExportHistorySizeMiBMaximum);
            ExportHistoryEntryLimit = Math.Clamp(ExportHistoryEntryLimit, ExportHistoryEntryLimitMinimum, ExportHistoryEntryLimitMaximum);
            BoardAdmissionSizeMiB = Math.Clamp(BoardAdmissionSizeMiB, BoardAdmissionSizeMiBMinimum, BoardAdmissionSizeMiBMaximum);
            SlashTagSuggestionCount = Math.Clamp(SlashTagSuggestionCount, SlashTagSuggestionCountMinimum, SlashTagSuggestionCountMaximum);
            GalleryVideoSeekStepSeconds = Math.Clamp(GalleryVideoSeekStepSeconds, GalleryVideoSeekStepSecondsMinimum, GalleryVideoSeekStepSecondsMaximum);
            // A hand-edited file or a newer version with another range must not make the whole profile invalid (Validate throws): clamp like the seek step.
            MediaBadgeCollapseBelow = Math.Clamp(MediaBadgeCollapseBelow, MediaBadgeCollapseBelowMinimum, MediaBadgeCollapseBelowMaximum);
            MediaBadgeHideBelow = Math.Clamp(MediaBadgeHideBelow, MediaBadgeHideBelowMinimum, MediaBadgeHideBelowMaximum);
            MediaBadgeMaxPerCorner = Math.Clamp(MediaBadgeMaxPerCorner, MediaBadgeMaxPerCornerMinimum, MediaBadgeMaxPerCornerMaximum);

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

        /// <summary>Applies the preference-default migrations a file written before <see cref="CurrentPreferenceDefaultsRevision"/> still needs, once.</summary>
        private void MigratePreferenceDefaults()
        {
            if (PreferenceDefaultsRevision >= CurrentPreferenceDefaultsRevision) return;
            if (PreferenceDefaultsRevision < 1)
            {
                // Every profile saved before the revision holds the old default, dragged or never touched.
                foreach (PresentationProfile profile in _layoutProfiles.Values)
                    profile.ArchiveSplitWidth = MigrateArchiveSplitWidthDefault(profile.ArchiveSplitWidth);
                if (_deserializedLayoutProfiles != null)
                    foreach (PresentationProfile profile in _deserializedLayoutProfiles.Values)
                        profile.ArchiveSplitWidth = MigrateArchiveSplitWidthDefault(profile.ArchiveSplitWidth);
            }
            PreferenceDefaultsRevision = CurrentPreferenceDefaultsRevision;
        }

        private static string NormalizeWorkspaceMode(string? mode) =>
            string.Equals(mode?.Trim(), "Advanced", StringComparison.OrdinalIgnoreCase) ? "Advanced" :
            string.Equals(mode?.Trim(), "Simple", StringComparison.OrdinalIgnoreCase) ? "Simple" : "Barebones";

        private static PresentationProfile NormalizeProfile(PresentationProfile profile)
        {
            PresentationProfile result = profile.Clone();
            result.ReadingFontSize = string.Equals(profile.ReadingFontSize?.Trim(), "Small", StringComparison.OrdinalIgnoreCase) ? "Small"
                : string.Equals(profile.ReadingFontSize?.Trim(), "Large", StringComparison.OrdinalIgnoreCase) ? "Large" : "Normal";
            result.ArchiveThreadLayout = string.Equals(profile.ArchiveThreadLayout?.Trim(), "Table", StringComparison.OrdinalIgnoreCase) ? "Table" : "List";
            result.ArchiveSplitWidth = NormalizeArchiveSplitWidth(profile.ArchiveSplitWidth);
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
        /// <summary>
        /// Draft only: the settings page's filmstrip box as it was loaded (start, discard, after a save). The gallery changes the layout
        /// directly (L, the ⋯ row), so an unchanged box of an older draft must not roll the layout of either mode back (H1).
        /// </summary>
        [JsonIgnore]
        internal bool? SettingsBaselineGalleryFilmstrip { get; set; }
        /// <summary>
        /// Draft only: the settings page's tag list width as it was loaded (start, discard, after a save). The width is part of the media layout, which the
        /// media page changes directly (the drag handle), so a draft takes over only a width the person changed in the box, never the rest of its layout (M1).
        /// </summary>
        [JsonIgnore]
        internal int? SettingsBaselineMediaTagListWidth { get; set; }
        /// <summary>Draft only: the settings page's media panel width as it was loaded, for the same reason as <see cref="SettingsBaselineMediaTagListWidth"/> (the panel's handle changes the layout directly).</summary>
        [JsonIgnore]
        internal int? SettingsBaselineMediaPanelWidth { get; set; }

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
        /// <summary>Off (default): a running slideshow goes on while another window has the focus, while MeowChan is minimised or in the tray.
        /// On: it waits until MeowChan is back and then shows the current medium for a full period.</summary>
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool GalleryPauseOnFocusLoss { get; set; }
        /// <summary>Idle time without pointer movement before the gallery's overlay controls and the reader's navigation bar hide.</summary>
        [JsonConverter(typeof(DesktopPreferenceControlsIdleHideMsConverter))]
        public int ControlsIdleHideMs { get; set; } = ControlsIdleHideMsDefault;
        internal const int ControlsIdleHideMsDefault = 2000, ControlsIdleHideMsMinimum = 500, ControlsIdleHideMsMaximum = 30_000;
        // Reader navigation bar (thread and post arrows with counters, floating at the lower right of the large reader): "Auto" (default)
        // hides it while the pointer is idle (after ControlsIdleHideMs), "Always" keeps it shown, "Off" removes it (keys only). Any other
        // stored value reads as "Auto".
        private string _readerNavigationMode = ReaderNavigationModeAuto;
        public const string ReaderNavigationModeAuto = "Auto", ReaderNavigationModeAlways = "Always", ReaderNavigationModeOff = "Off";
        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string ReaderNavigationMode
        {
            get => _readerNavigationMode;
            set => _readerNavigationMode = NormalizeReaderNavigationMode(value);
        }
        internal static string NormalizeReaderNavigationMode(string? value) =>
            string.Equals(value?.Trim(), ReaderNavigationModeAlways, StringComparison.OrdinalIgnoreCase) ? ReaderNavigationModeAlways
            : string.Equals(value?.Trim(), ReaderNavigationModeOff, StringComparison.OrdinalIgnoreCase) ? ReaderNavigationModeOff : ReaderNavigationModeAuto;
        /// <summary>Whether the reader navigation bar shows the "3 / 27" and "12 / 405" counters (default on); off forces its compact form.</summary>
        [JsonConverter(typeof(DesktopPreferenceTrueBooleanConverter))]
        public bool ReaderNavigationShowPosition { get; set; } = true;
        // Mouse wheel over the gallery's picture or video: "Zoom" (default, the wheel zooms at the pointer) or "Browse" (the wheel moves to the
        // next or previous medium). Ctrl+wheel does the other one. Any other stored value reads as "Zoom". The filmstrip, the video controls
        // and the panels keep their own wheel (see GalleryWheel).
        private string _galleryWheelMode = GalleryWheelModeZoom;
        public const string GalleryWheelModeZoom = "Zoom", GalleryWheelModeBrowse = "Browse";
        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string GalleryWheelMode
        {
            get => _galleryWheelMode;
            set => _galleryWheelMode = NormalizeGalleryWheelMode(value);
        }
        internal static string NormalizeGalleryWheelMode(string? value) =>
            string.Equals(value?.Trim(), GalleryWheelModeBrowse, StringComparison.OrdinalIgnoreCase) ? GalleryWheelModeBrowse : GalleryWheelModeZoom;
        /// <summary>GL5c: how long a transient status of the gallery's info line (the slideshow's "ready", a confirmation) stays before it fades out; a running slideshow and every error stay.</summary>
        [JsonConverter(typeof(DesktopPreferenceGalleryStatusDwellMsConverter))]
        public int GalleryStatusDwellMs { get; set; } = GalleryStatusDwellMsDefault;
        internal const int GalleryStatusDwellMsDefault = 3000, GalleryStatusDwellMsMinimum = 1000, GalleryStatusDwellMsMaximum = 10_000;
        /// <summary>How many tags the gallery's Post segment lists as chips; the rest is one "+N more" chip (the whole list is one click away in the Tags segment).</summary>
        [JsonConverter(typeof(DesktopPreferenceGalleryPostTagChipLimitConverter))]
        public int GalleryPostTagChipLimit { get; set; } = GalleryPostTagChipLimitDefault;
        internal const int GalleryPostTagChipLimitDefault = 30, GalleryPostTagChipLimitMinimum = 5, GalleryPostTagChipLimitMaximum = 200;
        /// <summary>The filmstrip of the gallery (thumbnails of the media before and after the shown one at the top): shown or not.</summary>
        public bool GalleryFilmstrip { get; set; } = true;
        /// <summary>Height of the filmstrip thumbnails in dip (the tiles are square).</summary>
        public int GalleryFilmstripHeight { get; set; } = GalleryFilmstripHeightDefault;
        internal const int GalleryFilmstripHeightDefault = 64, GalleryFilmstripHeightMinimum = 48, GalleryFilmstripHeightMaximum = 160;
        /// <summary>GL5e: the gap between two filmstrip tiles in dip.</summary>
        [JsonConverter(typeof(DesktopPreferenceGalleryFilmstripTileGapConverter))]
        public int GalleryFilmstripTileGap { get; set; } = GalleryFilmstripTileGapDefault;
        internal const int GalleryFilmstripTileGapDefault = 8, GalleryFilmstripTileGapMinimum = 4, GalleryFilmstripTileGapMaximum = 16;
        /// <summary>Filmstrip flags (decision 123): whether a filmstrip tile carries the favourite star (default on).</summary>
        [JsonConverter(typeof(DesktopPreferenceTrueBooleanConverter))]
        public bool GalleryFilmstripFlagFavorite { get; set; } = true;
        /// <summary>Filmstrip flags: whether a filmstrip tile carries the own rating, a small number with the half-star icon (default on).</summary>
        [JsonConverter(typeof(DesktopPreferenceTrueBooleanConverter))]
        public bool GalleryFilmstripFlagRating { get; set; } = true;
        // Filmstrip flags: when a tile carries the "no tags yet" mark. "Tagging" (default) shows it only while the gallery's tagging view (the
        // right tag panel) is open, "Always" shows it whenever the medium has no own tag, "Off" never. Any other stored value reads as "Tagging".
        private string _galleryFilmstripFlagUntagged = GalleryFilmstripUntaggedWithTagging;
        public const string GalleryFilmstripUntaggedWithTagging = "Tagging", GalleryFilmstripUntaggedAlways = "Always", GalleryFilmstripUntaggedOff = "Off";
        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string GalleryFilmstripFlagUntagged
        {
            get => _galleryFilmstripFlagUntagged;
            set => _galleryFilmstripFlagUntagged = NormalizeGalleryFilmstripUntagged(value);
        }
        internal static string NormalizeGalleryFilmstripUntagged(string? value) =>
            string.Equals(value?.Trim(), GalleryFilmstripUntaggedAlways, StringComparison.OrdinalIgnoreCase) ? GalleryFilmstripUntaggedAlways
            : string.Equals(value?.Trim(), GalleryFilmstripUntaggedOff, StringComparison.OrdinalIgnoreCase) ? GalleryFilmstripUntaggedOff : GalleryFilmstripUntaggedWithTagging;
        /// <summary>How many thumbnails on each side of the visible ones the filmstrip decodes in advance.</summary>
        public int GalleryFilmstripPreload { get; set; } = GalleryFilmstripPreloadDefault;
        internal const int GalleryFilmstripPreloadDefault = 12, GalleryFilmstripPreloadMinimum = 4, GalleryFilmstripPreloadMaximum = 60;
        /// <summary>How many seconds the "," and "." keys of a gallery video jump back and forward (the video seek step).</summary>
        public int GalleryVideoSeekStepSeconds { get; set; } = GalleryVideoSeekStepSecondsDefault;
        internal const int GalleryVideoSeekStepSecondsDefault = 5, GalleryVideoSeekStepSecondsMinimum = 1, GalleryVideoSeekStepSecondsMaximum = 60;
        /// <summary>Media badges (MB1): below this shorter picture side (dip) every corner of a thumbnail shows one dot instead of its badges.</summary>
        public int MediaBadgeCollapseBelow { get; set; } = MediaBadgeCollapseBelowDefault;
        internal const int MediaBadgeCollapseBelowDefault = 48, MediaBadgeCollapseBelowMinimum = 24, MediaBadgeCollapseBelowMaximum = 96;
        /// <summary>Media badges (MB1): below this shorter picture side (dip) a thumbnail shows no badges at all (never above the collapse limit).</summary>
        public int MediaBadgeHideBelow { get; set; } = MediaBadgeHideBelowDefault;
        internal const int MediaBadgeHideBelowDefault = 32, MediaBadgeHideBelowMinimum = 16, MediaBadgeHideBelowMaximum = 64;
        /// <summary>Media badges (MB1): how many badges one corner of a thumbnail holds; the ones of lower priority drop out.</summary>
        public int MediaBadgeMaxPerCorner { get; set; } = MediaBadgeMaxPerCornerDefault;
        internal const int MediaBadgeMaxPerCornerDefault = 2, MediaBadgeMaxPerCornerMinimum = 1, MediaBadgeMaxPerCornerMaximum = 3;
        /// <summary>
        /// GL4: the person's layout choice of the gallery, one set per mode (the typed value; null = nothing chosen). A profile without it reads
        /// <see cref="GalleryFilmstrip"/> instead (GalleryLayoutPreferences.FromConfig). Saved as "GalleryLayout" (<see cref="GalleryLayoutJson"/>).
        /// </summary>
        [JsonIgnore] internal GalleryLayoutPreferences? GalleryLayout { get; set; }
        /// <summary>
        /// The saved form of <see cref="GalleryLayout"/>, key "GalleryLayout": { "view": {...}, "tag": {...} } with the optional members filmstrip,
        /// filmstripDock ("top"|"bottom"), tools, info and hints (absent = the work view's default). Any JSON is accepted on load: a value that
        /// is no object reads as nothing chosen, and unknown or damaged members are dropped and logged, so the other settings are never lost.
        /// </summary>
        [JsonPropertyName("GalleryLayout")]
        public System.Text.Json.Nodes.JsonNode? GalleryLayoutJson
        {
            get => GalleryLayout?.ToJsonNode();
            set => GalleryLayout = GalleryLayoutPreferences.FromJsonNode(value);
        }
        /// <summary>Draft only (never saved): the settings page asked to reset the gallery layout of both modes; applied by <see cref="CopySettingsFieldsFrom"/>.</summary>
        [JsonIgnore] internal bool GalleryLayoutResetRequested { get; set; }
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool GalleryMediaFocus { get; set; }
        /// <summary>
        /// The gallery's controls overlay (auto-hides, revealed at the bottom edge). Default on; a profile that has the key keeps its value,
        /// on or off, and a missing or damaged value reads as the default (on).
        /// </summary>
        [JsonConverter(typeof(DesktopPreferenceTrueBooleanConverter))]
        public bool GalleryOverlayControls { get; set; } = true;
        // Keep both serialized keys for older profiles; explicit actions use
        // GalleryPlaybackPreferences.WithControlsOverlay rather than coupled setters.
        [JsonConverter(typeof(DesktopPreferenceTrueBooleanConverter))]
        public bool GalleryAutoHideControls { get; set; } = true;
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool GalleryOverlayPostContext { get; set; }

        [JsonConverter(typeof(DesktopPreferenceTrueBooleanConverter))]
        public bool ShowShortcutHelp { get; set; } = true;
        /// <summary>
        /// The focus mode of the media page (Shift+F) also hides the application header (title bar with the I/O monitor and the header
        /// buttons). A thin strip at the top edge shows it again while the pointer rests there. Default on; off keeps the header and
        /// hides only the page's own rows and the footer.
        /// </summary>
        [JsonConverter(typeof(DesktopPreferenceTrueBooleanConverter))]
        public bool MediaFocusHidesHeader { get; set; } = true;
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool GalleryContextOpen { get; set; }
        /// <summary>
        /// The gallery side panel's width in dip (V2), dragged at its left edge. A view preference of the layout profile, kept as
        /// ColumnWidths["gallery.panelWidth"]; outside the limits it is clamped.
        /// </summary>
        internal const int GalleryPanelWidthDefault = 320, GalleryPanelWidthMinimum = 280, GalleryPanelWidthMaximum = 480;
        /// <summary>
        /// Width of the archive list pane (left of the detail pane) in dip, dragged at the splitter; a view preference of the layout
        /// profile. The default holds the complete English search placeholder next to the scope chip and the search button
        /// (measured by ArchiveListWidthChecks); outside the limits a saved value is clamped.
        /// </summary>
        internal const double ArchiveSplitWidthDefault = 330, ArchiveSplitWidthMinimum = 180, ArchiveSplitWidthMaximum = 1200;
        /// <summary>The default before the list pane was widened. Every profile saved before PreferenceDefaultsRevision 1 holds it, whether dragged or never touched, so there a loaded 270 reads as "never customised".</summary>
        internal const double ArchiveSplitWidthPreviousDefault = 270;

        internal static double NormalizeArchiveSplitWidth(double width) =>
            double.IsFinite(width) ? Math.Clamp(width, ArchiveSplitWidthMinimum, ArchiveSplitWidthMaximum) : ArchiveSplitWidthDefault;

        /// <summary>The one-shot migration for a file older than PreferenceDefaultsRevision 1: the previous default moves to the new one, any other width stays.</summary>
        internal static double MigrateArchiveSplitWidthDefault(double width) =>
            width == ArchiveSplitWidthPreviousDefault ? ArchiveSplitWidthDefault : NormalizeArchiveSplitWidth(width);

        [JsonConverter(typeof(DesktopPreferenceSplitWidthConverter))]
        public double ArchiveSplitWidth
        {
            get => ActiveProfile.ArchiveSplitWidth;
            set => ActiveProfile.ArchiveSplitWidth = NormalizeArchiveSplitWidth(value);
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
        /// <summary>The Archive's thread list is hidden by its toolbar switch (the reader takes the width). Not a setting: no editor shows it, the
        /// reader toolbar's "Thread list" switch writes it. Only a window wide enough for list and reader side by side honours it; a narrow window
        /// keeps its own list-or-reader choice.</summary>
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool ArchiveThreadListHidden { get; set; }
        internal const string StatusPageSegmentStatistics = "statistics";
        internal const string StatusPageSegmentDiagnostics = "diagnostics";
        /// <summary>The segment of the Status page that its footer button and menu entry open: the one used last. Not a
        /// setting: no editor shows it. Anything unknown reads as the statistics segment.</summary>
        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string StatusPageSegment
        {
            get => _statusPageSegment;
            set => _statusPageSegment = value == StatusPageSegmentDiagnostics ? StatusPageSegmentDiagnostics : StatusPageSegmentStatistics;
        }

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
            if (Desktop.ProfileStartup.FolderSession?.SelectedProfile is { } folderProfile) return folderProfile;
            string? overrideDirectory = ResolveProfileOverrides(CommandLineProfile,
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

        private static string? s_commandLineProfile;

        /// <summary>The profile folder given with <c>--profile</c> (absolute), set once by the app's entry point before any
        /// profile is resolved; null when the start had no such argument. It wins over the default profile.</summary>
        internal static string? CommandLineProfile
        {
            get => System.Threading.Volatile.Read(ref s_commandLineProfile);
            set => System.Threading.Volatile.Write(ref s_commandLineProfile, value == null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)));
        }

        /// <summary>
        /// The profile chosen by <c>--profile</c> (<paramref name="argument"/>) or by the environment variables, or null for the
        /// default profile. The argument and an environment variable may name the same folder; different folders are refused
        /// like two different environment variables, so a start never silently opens another profile than one it was given.
        /// </summary>
        internal static string? ResolveProfileOverrides(string? argument, string? meowChanProfile, string? oChanProfile)
        {
            string? environment = ResolveProfileOverrides(meowChanProfile, oChanProfile);
            string? selected = string.IsNullOrWhiteSpace(argument) ? null :
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(argument));
            if (selected != null && environment != null && !string.Equals(selected, environment,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "--profile and MEOWCHAN_PROFILE (or OCHAN_PROFILE) select different profiles. Use only one, or name the same directory.");
            return selected ?? environment;
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
            GalleryLayout = GalleryLayout is { } layout && layout.Normalize() is { IsDefault: false } normalized ? normalized : null;
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
            if (WorkViewSwitchNoticeSeconds is < WorkViewSwitchNoticeSecondsMinimum or > WorkViewSwitchNoticeSecondsMaximum)
                throw new ArgumentOutOfRangeException(nameof(WorkViewSwitchNoticeSeconds), "The work-view switch notice duration must be between 0 and 60 seconds.");
            if (ShutdownTimeLimitSeconds is < ShutdownTimeLimitSecondsMinimum or > ShutdownTimeLimitSecondsMaximum)
                throw new ArgumentOutOfRangeException(nameof(ShutdownTimeLimitSeconds), "The shutdown time limit must be between 5 and 600 seconds.");
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
            if (!LocalTaggerModelSettings.ManifestKiBRange.Contains(LocalTaggerManifestKiB))
                throw new ArgumentOutOfRangeException(nameof(LocalTaggerManifestKiB));
            if (!LocalTaggerModelSettings.WeightsMiBRange.Contains(LocalTaggerWeightsMiB))
                throw new ArgumentOutOfRangeException(nameof(LocalTaggerWeightsMiB));
            if (!LocalTaggerModelSettings.InputTensorMiBRange.Contains(LocalTaggerInputTensorMiB))
                throw new ArgumentOutOfRangeException(nameof(LocalTaggerInputTensorMiB));
            if (MinimumFreeSpaceGiB is < MinimumFreeSpaceGiBMinimum or > MinimumFreeSpaceGiBMaximum)
                throw new ArgumentOutOfRangeException(nameof(MinimumFreeSpaceGiB), "Free-space reserve must be between " + MinimumFreeSpaceGiBMinimum + " and " + MinimumFreeSpaceGiBMaximum + " GiB.");
            if (BooruCatalogLimitMiB is < BooruCatalogMinimumLimitMiB or > BooruCatalogMaximumLimitMiB)
                throw new ArgumentOutOfRangeException(nameof(BooruCatalogLimitMiB), "The Danbooru catalog limit must be between 8 and 128 MiB.");
            if (BooruRetryAfterCooldownHours is < BooruRetryAfterCooldownMinimumHours or > BooruRetryAfterCooldownMaximumHours)
                throw new ArgumentOutOfRangeException(nameof(BooruRetryAfterCooldownHours), "The Danbooru Retry-After cooldown must be between 1 and 168 hours.");
            if (ArchiveTagFilterTagsPerList is < ArchiveTagFilterTagsPerListMinimum or > ArchiveTagFilterTagsPerListMaximum)
                throw new ArgumentOutOfRangeException(nameof(ArchiveTagFilterTagsPerList), "ArchiveTagFilterTagsPerList is outside its supported range (" + ArchiveTagFilterTagsPerListMinimum + " to " + ArchiveTagFilterTagsPerListMaximum + ").");
            if (UrlTextExportLimitMiB is < UrlTextExportLimitMiBMinimum or > UrlTextExportLimitMiBMaximum)
                throw new ArgumentOutOfRangeException(nameof(UrlTextExportLimitMiB), "UrlTextExportLimitMiB is outside its supported range (" + UrlTextExportLimitMiBMinimum + " to " + UrlTextExportLimitMiBMaximum + ").");
            if (ExportHistorySizeMiB is < ExportHistorySizeMiBMinimum or > ExportHistorySizeMiBMaximum)
                throw new ArgumentOutOfRangeException(nameof(ExportHistorySizeMiB), "ExportHistorySizeMiB is outside its supported range (" + ExportHistorySizeMiBMinimum + " to " + ExportHistorySizeMiBMaximum + ").");
            if (ExportHistoryEntryLimit is < ExportHistoryEntryLimitMinimum or > ExportHistoryEntryLimitMaximum)
                throw new ArgumentOutOfRangeException(nameof(ExportHistoryEntryLimit), "ExportHistoryEntryLimit is outside its supported range (" + ExportHistoryEntryLimitMinimum + " to " + ExportHistoryEntryLimitMaximum + ").");
            if (BoardAdmissionSizeMiB is < BoardAdmissionSizeMiBMinimum or > BoardAdmissionSizeMiBMaximum)
                throw new ArgumentOutOfRangeException(nameof(BoardAdmissionSizeMiB), "BoardAdmissionSizeMiB is outside its supported range (" + BoardAdmissionSizeMiBMinimum + " to " + BoardAdmissionSizeMiBMaximum + ").");
            if (SlashTagSuggestionCount is < SlashTagSuggestionCountMinimum or > SlashTagSuggestionCountMaximum)
                throw new ArgumentOutOfRangeException(nameof(SlashTagSuggestionCount), "SlashTagSuggestionCount is outside its supported range (" + SlashTagSuggestionCountMinimum + " to " + SlashTagSuggestionCountMaximum + ").");
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
            if (TagQueueLimit is < TagQueueLimitMinimum or > TagQueueLimitMaximum)
                throw new ArgumentOutOfRangeException(nameof(TagQueueLimit), "TagQueueLimit is outside its supported range (" + TagQueueLimitMinimum + " to " + TagQueueLimitMaximum + ").");
            if (TagQueueFileLimitMiB is < TagQueueFileLimitMiBMinimum or > TagQueueFileLimitMiBMaximum)
                throw new ArgumentOutOfRangeException(nameof(TagQueueFileLimitMiB), "TagQueueFileLimitMiB is outside its supported range (" + TagQueueFileLimitMiBMinimum + " to " + TagQueueFileLimitMiBMaximum + ").");
            if (TagQueueHistory is < TagQueueHistoryMinimum or > TagQueueHistoryMaximum)
                throw new ArgumentOutOfRangeException(nameof(TagQueueHistory), "TagQueueHistory is outside its supported range (" + TagQueueHistoryMinimum + " to " + TagQueueHistoryMaximum + ").");
            if (TagQueueGroupLimit is < TagQueueGroupLimitMinimum or > TagQueueGroupLimitMaximum)
                throw new ArgumentOutOfRangeException(nameof(TagQueueGroupLimit), "TagQueueGroupLimit is outside its supported range (" + TagQueueGroupLimitMinimum + " to " + TagQueueGroupLimitMaximum + ").");
            if (TagQueueGroupNameLimit is < TagQueueGroupNameLimitMinimum or > TagQueueGroupNameLimitMaximum)
                throw new ArgumentOutOfRangeException(nameof(TagQueueGroupNameLimit), "TagQueueGroupNameLimit is outside its supported range (" + TagQueueGroupNameLimitMinimum + " to " + TagQueueGroupNameLimitMaximum + ").");
            if (TagQueueConfirmAbove is < TagQueueConfirmAboveMinimum or > TagQueueConfirmAboveMaximum)
                throw new ArgumentOutOfRangeException(nameof(TagQueueConfirmAbove), "TagQueueConfirmAbove is outside its supported range (" + TagQueueConfirmAboveMinimum + " to " + TagQueueConfirmAboveMaximum + ").");
            if (MediaTagListTop is < MediaTagListTopMinimum or > MediaTagListTopMaximum)
                throw new ArgumentOutOfRangeException(nameof(MediaTagListTop), "MediaTagListTop is outside its supported range (" + MediaTagListTopMinimum + " to " + MediaTagListTopMaximum + ").");
            if (BooruResultTagsShown is < BooruResultTagsShownMinimum or > BooruResultTagsShownMaximum)
                throw new ArgumentOutOfRangeException(nameof(BooruResultTagsShown), "BooruResultTagsShown is outside its supported range (" + BooruResultTagsShownMinimum + " to " + BooruResultTagsShownMaximum + ").");
            if (BooruNewSearchTarget is not (BooruSearchTargetNewTab or BooruSearchTargetCurrentTab))
                throw new ArgumentOutOfRangeException(nameof(BooruNewSearchTarget));
            if (BooruSearchTabs is < BooruSearchTabsMinimum or > BooruSearchTabsMaximum)
                throw new ArgumentOutOfRangeException(nameof(BooruSearchTabs), "BooruSearchTabs is outside its supported range (" + BooruSearchTabsMinimum + " to " + BooruSearchTabsMaximum + ").");
            if (BooruSearchPages is < BooruSearchPagesMinimum or > BooruSearchPagesMaximum)
                throw new ArgumentOutOfRangeException(nameof(BooruSearchPages));
            if (BooruSearchPostsPerPage is < BooruSearchPostsPerPageMinimum or > BooruSearchPostsPerPageMaximum)
                throw new ArgumentOutOfRangeException(nameof(BooruSearchPostsPerPage));
            if (BooruSavedTabs is < BooruSavedTabsMinimum or > BooruSavedTabsMaximum)
                throw new ArgumentOutOfRangeException(nameof(BooruSavedTabs));
            if (BooruSavedResultsPerTab is < BooruSavedResultsPerTabMinimum or > BooruSavedResultsPerTabMaximum)
                throw new ArgumentOutOfRangeException(nameof(BooruSavedResultsPerTab));
            if (BooruGallerySampleMiB is < BooruGallerySampleMiBMinimum or > BooruGallerySampleMiBMaximum)
                throw new ArgumentOutOfRangeException(nameof(BooruGallerySampleMiB), "BooruGallerySampleMiB is outside its supported range (" + BooruGallerySampleMiBMinimum + " to " + BooruGallerySampleMiBMaximum + ").");
            if (BooruGalleryCacheMiB is < BooruGalleryCacheMiBMinimum or > BooruGalleryCacheMiBMaximum)
                throw new ArgumentOutOfRangeException(nameof(BooruGalleryCacheMiB), "BooruGalleryCacheMiB is outside its supported range (" + BooruGalleryCacheMiBMinimum + " to " + BooruGalleryCacheMiBMaximum + ").");
            ValidateBooruGalleryLimits();
            if (MediaPanelMinimumPageWidth is < MediaPanelMinimumPageWidthMinimum or > MediaPanelMinimumPageWidthMaximum)
                throw new ArgumentOutOfRangeException(nameof(MediaPanelMinimumPageWidth), "MediaPanelMinimumPageWidth is outside its supported range (" + MediaPanelMinimumPageWidthMinimum + " to " + MediaPanelMinimumPageWidthMaximum + ").");
            if (ProfileRegistryLimit is < ProfileRegistryLimitMinimum or > ProfileRegistryLimitMaximum)
                throw new ArgumentOutOfRangeException(nameof(ProfileRegistryLimit), "ProfileRegistryLimit is outside its supported range (" + ProfileRegistryLimitMinimum + " to " + ProfileRegistryLimitMaximum + ").");
            if (TagQueueWalkWindow is < TagQueueWalkWindowMinimum or > TagQueueWalkWindowMaximum)
                throw new ArgumentOutOfRangeException(nameof(TagQueueWalkWindow), "TagQueueWalkWindow is outside its supported range (" + TagQueueWalkWindowMinimum + " to " + TagQueueWalkWindowMaximum + ").");
            if (TagQueueUndoDepth is < TagQueueUndoDepthMinimum or > TagQueueUndoDepthMaximum)
                throw new ArgumentOutOfRangeException(nameof(TagQueueUndoDepth), "TagQueueUndoDepth is outside its supported range (" + TagQueueUndoDepthMinimum + " to " + TagQueueUndoDepthMaximum + ").");
            if (TagStampSize is < TagStampSizeMinimum or > TagStampSizeMaximum)
                throw new ArgumentOutOfRangeException(nameof(TagStampSize), "TagStampSize is outside its supported range (" + TagStampSizeMinimum + " to " + TagStampSizeMaximum + ").");
            if (SourcesWatchPreviewRows is < SourcesWatchPreviewRowsMinimum or > SourcesWatchPreviewRowsMaximum)
                throw new ArgumentOutOfRangeException(nameof(SourcesWatchPreviewRows), "SourcesWatchPreviewRows is outside its supported range (" + SourcesWatchPreviewRowsMinimum + " to " + SourcesWatchPreviewRowsMaximum + ").");
            if (ExportConfirmFiles is < ExportConfirmFilesMinimum or > ExportConfirmFilesMaximum)
                throw new ArgumentOutOfRangeException(nameof(ExportConfirmFiles), "ExportConfirmFiles is outside its supported range (" + ExportConfirmFilesMinimum + " to " + ExportConfirmFilesMaximum + ").");
            if (ExportConfirmGiB is < ExportConfirmGiBMinimum or > ExportConfirmGiBMaximum)
                throw new ArgumentOutOfRangeException(nameof(ExportConfirmGiB), "ExportConfirmGiB is outside its supported range (" + ExportConfirmGiBMinimum + " to " + ExportConfirmGiBMaximum + ").");
            if (PosterCacheLimitMiB is < PosterCacheLimitMiBMinimum or > PosterCacheLimitMiBMaximum)
                throw new ArgumentOutOfRangeException(nameof(PosterCacheLimitMiB), "PosterCacheLimitMiB is outside its supported range (" + PosterCacheLimitMiBMinimum + " to " + PosterCacheLimitMiBMaximum + ").");
            if (VideoScrubCacheLimitMiB is < VideoScrubCacheLimitMiBMinimum or > VideoScrubCacheLimitMiBMaximum)
                throw new ArgumentOutOfRangeException(nameof(VideoScrubCacheLimitMiB), "VideoScrubCacheLimitMiB is outside its supported range (" + VideoScrubCacheLimitMiBMinimum + " to " + VideoScrubCacheLimitMiBMaximum + ").");
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
            if (GalleryStatusDwellMs is < GalleryStatusDwellMsMinimum or > GalleryStatusDwellMsMaximum)
                throw new ArgumentOutOfRangeException(nameof(GalleryStatusDwellMs), "GalleryStatusDwellMs is outside its supported range (" + GalleryStatusDwellMsMinimum + " to " + GalleryStatusDwellMsMaximum + ").");
            if (HeaderTaglineIntervalMinutes is < HeaderTaglineIntervalMinutesMinimum or > HeaderTaglineIntervalMinutesMaximum)
                throw new ArgumentOutOfRangeException(nameof(HeaderTaglineIntervalMinutes), "HeaderTaglineIntervalMinutes is outside its supported range (" + HeaderTaglineIntervalMinutesMinimum + " to " + HeaderTaglineIntervalMinutesMaximum + ").");
            if (GalleryPostTagChipLimit is < GalleryPostTagChipLimitMinimum or > GalleryPostTagChipLimitMaximum)
                throw new ArgumentOutOfRangeException(nameof(GalleryPostTagChipLimit), "GalleryPostTagChipLimit is outside its supported range (" + GalleryPostTagChipLimitMinimum + " to " + GalleryPostTagChipLimitMaximum + ").");
            if (GalleryFilmstripHeight is < GalleryFilmstripHeightMinimum or > GalleryFilmstripHeightMaximum)
                throw new ArgumentOutOfRangeException(nameof(GalleryFilmstripHeight), "GalleryFilmstripHeight is outside its supported range (" + GalleryFilmstripHeightMinimum + " to " + GalleryFilmstripHeightMaximum + ").");
            if (GalleryFilmstripTileGap is < GalleryFilmstripTileGapMinimum or > GalleryFilmstripTileGapMaximum)
                throw new ArgumentOutOfRangeException(nameof(GalleryFilmstripTileGap), "GalleryFilmstripTileGap is outside its supported range (" + GalleryFilmstripTileGapMinimum + " to " + GalleryFilmstripTileGapMaximum + ").");
            if (GalleryFilmstripPreload is < GalleryFilmstripPreloadMinimum or > GalleryFilmstripPreloadMaximum)
                throw new ArgumentOutOfRangeException(nameof(GalleryFilmstripPreload), "GalleryFilmstripPreload is outside its supported range (" + GalleryFilmstripPreloadMinimum + " to " + GalleryFilmstripPreloadMaximum + ").");
            if (GalleryVideoSeekStepSeconds is < GalleryVideoSeekStepSecondsMinimum or > GalleryVideoSeekStepSecondsMaximum)
                throw new ArgumentOutOfRangeException(nameof(GalleryVideoSeekStepSeconds), "GalleryVideoSeekStepSeconds is outside its supported range (" + GalleryVideoSeekStepSecondsMinimum + " to " + GalleryVideoSeekStepSecondsMaximum + ").");
            if (MediaBadgeCollapseBelow is < MediaBadgeCollapseBelowMinimum or > MediaBadgeCollapseBelowMaximum)
                throw new ArgumentOutOfRangeException(nameof(MediaBadgeCollapseBelow), "MediaBadgeCollapseBelow is outside its supported range (" + MediaBadgeCollapseBelowMinimum + " to " + MediaBadgeCollapseBelowMaximum + ").");
            if (MediaBadgeHideBelow is < MediaBadgeHideBelowMinimum or > MediaBadgeHideBelowMaximum)
                throw new ArgumentOutOfRangeException(nameof(MediaBadgeHideBelow), "MediaBadgeHideBelow is outside its supported range (" + MediaBadgeHideBelowMinimum + " to " + MediaBadgeHideBelowMaximum + ").");
            if (MediaBadgeMaxPerCorner is < MediaBadgeMaxPerCornerMinimum or > MediaBadgeMaxPerCornerMaximum)
                throw new ArgumentOutOfRangeException(nameof(MediaBadgeMaxPerCorner), "MediaBadgeMaxPerCorner is outside its supported range (" + MediaBadgeMaxPerCornerMinimum + " to " + MediaBadgeMaxPerCornerMaximum + ").");
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
            if (ReadingHistoryLimit is < ReadingHistoryLimitMinimum or > ReadingHistoryLimitMaximum)
                throw new ArgumentOutOfRangeException(nameof(ReadingHistoryLimit), "ReadingHistoryLimit is outside its supported range.");
            if (ReadingHistoryMenuEntries is < ReadingHistoryMenuEntriesMinimum or > ReadingHistoryMenuEntriesMaximum)
                throw new ArgumentOutOfRangeException(nameof(ReadingHistoryMenuEntries), "ReadingHistoryMenuEntries is outside its supported range.");
            if (ReaderTextlessMediaWidth is < ReaderTextlessMediaWidthMinimum or > ReaderTextlessMediaWidthMaximum)
                throw new ArgumentOutOfRangeException(nameof(ReaderTextlessMediaWidth), "ReaderTextlessMediaWidth is outside its supported range.");
            if (ReaderHoverEnlargeSize is < ReaderHoverEnlargeSizeMinimum or > ReaderHoverEnlargeSizeMaximum)
                throw new ArgumentOutOfRangeException(nameof(ReaderHoverEnlargeSize), "ReaderHoverEnlargeSize is outside its supported range.");
            if (ReaderHoverEnlargeDelayMs is < ReaderHoverEnlargeDelayMsMinimum or > ReaderHoverEnlargeDelayMsMaximum)
                throw new ArgumentOutOfRangeException(nameof(ReaderHoverEnlargeDelayMs), "ReaderHoverEnlargeDelayMs is outside its supported range.");
            if (ReaderFocusedMediaScalePercent is < ReaderFocusedMediaScalePercentMinimum or > ReaderFocusedMediaScalePercentMaximum)
                throw new ArgumentOutOfRangeException(nameof(ReaderFocusedMediaScalePercent), "ReaderFocusedMediaScalePercent is outside its supported range.");
            if (ReaderMediaSidebarWidth is < ReaderMediaSidebarWidthMinimum or > ReaderMediaSidebarWidthMaximum)
                throw new ArgumentOutOfRangeException(nameof(ReaderMediaSidebarWidth), "ReaderMediaSidebarWidth is outside its supported range.");
            if (ReaderMediaSidebarMinimumWidth is < ReaderMediaSidebarMinimumWidthMinimum or > ReaderMediaSidebarMinimumWidthMaximum)
                throw new ArgumentOutOfRangeException(nameof(ReaderMediaSidebarMinimumWidth), "ReaderMediaSidebarMinimumWidth is outside its supported range.");
            if (ReaderInlineVideoMaxPlayers is < ReaderInlineVideoMaxPlayersMinimum or > ReaderInlineVideoMaxPlayersMaximum)
                throw new ArgumentOutOfRangeException(nameof(ReaderInlineVideoMaxPlayers), "ReaderInlineVideoMaxPlayers is outside its supported range.");
            if (ReaderInlineVideoMaxFileMiB is < ReaderInlineVideoMaxFileMiBMinimum or > ReaderInlineVideoMaxFileMiBMaximum)
                throw new ArgumentOutOfRangeException(nameof(ReaderInlineVideoMaxFileMiB), "ReaderInlineVideoMaxFileMiB is outside its supported range.");
            if (ReaderMediaStripLimit is < ReaderMediaStripLimitMinimum or > ReaderMediaStripLimitMaximum)
                throw new ArgumentOutOfRangeException(nameof(ReaderMediaStripLimit), "ReaderMediaStripLimit is outside its supported range.");
            if (ReaderMediaGridTextLimit is < ReaderMediaGridTextLimitMinimum or > ReaderMediaGridTextLimitMaximum)
                throw new ArgumentOutOfRangeException(nameof(ReaderMediaGridTextLimit), "ReaderMediaGridTextLimit is outside its supported range.");
            if (ReaderPreviewPrefetchPosts is < ReaderPreviewPrefetchPostsMinimum or > ReaderPreviewPrefetchPostsMaximum)
                throw new ArgumentOutOfRangeException(nameof(ReaderPreviewPrefetchPosts), "ReaderPreviewPrefetchPosts is outside its supported range.");
            if (ReaderPreviewSizeMemory is < ReaderPreviewSizeMemoryMinimum or > ReaderPreviewSizeMemoryMaximum)
                throw new ArgumentOutOfRangeException(nameof(ReaderPreviewSizeMemory), "ReaderPreviewSizeMemory is outside its supported range.");
            if (GalleryFitMaxUpscalePercent is < GalleryFitMaxUpscalePercentMinimum or > GalleryFitMaxUpscalePercentMaximum)
                throw new ArgumentOutOfRangeException(nameof(GalleryFitMaxUpscalePercent), "GalleryFitMaxUpscalePercent is outside its supported range.");
            if (GalleryMaxZoomPercent is < GalleryMaxZoomPercentMinimum or > GalleryMaxZoomPercentMaximum)
                throw new ArgumentOutOfRangeException(nameof(GalleryMaxZoomPercent), "GalleryMaxZoomPercent is outside its supported range.");
            if (PostFilterRuleLimit is < PostFilterRuleLimitMinimum or > PostFilterRuleLimitMaximum)
                throw new ArgumentOutOfRangeException(nameof(PostFilterRuleLimit), "PostFilterRuleLimit is outside its supported range.");
            if (HiddenPostLimit is < HiddenPostLimitMinimum or > HiddenPostLimitMaximum)
                throw new ArgumentOutOfRangeException(nameof(HiddenPostLimit), "HiddenPostLimit is outside its supported range.");
            if (QuickTagSuggestionCount is < QuickTagSuggestionCountMinimum or > QuickTagSuggestionCountMaximum)
                throw new ArgumentOutOfRangeException(nameof(QuickTagSuggestionCount), "QuickTagSuggestionCount is outside its supported range (" + QuickTagSuggestionCountMinimum + " to " + QuickTagSuggestionCountMaximum + ").");
            if (QuickTagPanelSuggestionLimit is < QuickTagPanelSuggestionLimitMinimum or > QuickTagPanelSuggestionLimitMaximum)
                throw new ArgumentOutOfRangeException(nameof(QuickTagPanelSuggestionLimit), "QuickTagPanelSuggestionLimit is outside its supported range (" + QuickTagPanelSuggestionLimitMinimum + " to " + QuickTagPanelSuggestionLimitMaximum + ").");
            if (QuickTagRecentCount is < QuickTagRecentCountMinimum or > QuickTagRecentCountMaximum)
                throw new ArgumentOutOfRangeException(nameof(QuickTagRecentCount), "QuickTagRecentCount is outside its supported range (" + QuickTagRecentCountMinimum + " to " + QuickTagRecentCountMaximum + ").");
            if (QuickTagUndoDepth is < QuickTagUndoDepthMinimum or > QuickTagUndoDepthMaximum)
                throw new ArgumentOutOfRangeException(nameof(QuickTagUndoDepth), "QuickTagUndoDepth is outside its supported range (" + QuickTagUndoDepthMinimum + " to " + QuickTagUndoDepthMaximum + ").");
            if (QuickTagNeighbourScan is < QuickTagNeighbourScanMinimum or > QuickTagNeighbourScanMaximum)
                throw new ArgumentOutOfRangeException(nameof(QuickTagNeighbourScan), "QuickTagNeighbourScan is outside its supported range (" + QuickTagNeighbourScanMinimum + " to " + QuickTagNeighbourScanMaximum + ").");
            if (QuickTagFilenameTokens is < QuickTagFilenameTokensMinimum or > QuickTagFilenameTokensMaximum)
                throw new ArgumentOutOfRangeException(nameof(QuickTagFilenameTokens), "QuickTagFilenameTokens is outside its supported range (" + QuickTagFilenameTokensMinimum + " to " + QuickTagFilenameTokensMaximum + ").");
            if (QuickTagSourceTagsPerMedium is < QuickTagSourceTagsPerMediumMinimum or > QuickTagSourceTagsPerMediumMaximum)
                throw new ArgumentOutOfRangeException(nameof(QuickTagSourceTagsPerMedium), "QuickTagSourceTagsPerMedium is outside its supported range (" + QuickTagSourceTagsPerMediumMinimum + " to " + QuickTagSourceTagsPerMediumMaximum + ").");
            if (QuickTagMarkedMediaLimit is < QuickTagMarkedMediaLimitMinimum or > QuickTagMarkedMediaLimitMaximum)
                throw new ArgumentOutOfRangeException(nameof(QuickTagMarkedMediaLimit), "QuickTagMarkedMediaLimit is outside its supported range (" + QuickTagMarkedMediaLimitMinimum + " to " + QuickTagMarkedMediaLimitMaximum + ").");
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
            if (FolderSourceMaxFiles is < FolderSourceMaxFilesMinimum or > FolderSourceMaxFilesMaximum)
                throw new ArgumentOutOfRangeException(nameof(FolderSourceMaxFiles), "FolderSourceMaxFiles is outside its supported range (" + FolderSourceMaxFilesMinimum + " to " + FolderSourceMaxFilesMaximum + ").");
            if (FolderSourceMaxRoots is < FolderSourceMaxRootsMinimum or > FolderSourceMaxRootsMaximum)
                throw new ArgumentOutOfRangeException(nameof(FolderSourceMaxRoots), "FolderSourceMaxRoots is outside its supported range (" + FolderSourceMaxRootsMinimum + " to " + FolderSourceMaxRootsMaximum + ").");
            if (FolderSourceHashParallelism is < FolderSourceHashParallelismMinimum or > FolderSourceHashParallelismMaximum)
                throw new ArgumentOutOfRangeException(nameof(FolderSourceHashParallelism), "FolderSourceHashParallelism is outside its supported range (" + FolderSourceHashParallelismMinimum + " to " + FolderSourceHashParallelismMaximum + ").");
            if (FolderSourceHashMaxFileGiB is < FolderSourceHashMaxFileGiBMinimum or > FolderSourceHashMaxFileGiBMaximum)
                throw new ArgumentOutOfRangeException(nameof(FolderSourceHashMaxFileGiB), "FolderSourceHashMaxFileGiB is outside its supported range (" + FolderSourceHashMaxFileGiBMinimum + " to " + FolderSourceHashMaxFileGiBMaximum + ").");
            if (FolderSourceRescanMinutes is < FolderSourceRescanMinutesMinimum or > FolderSourceRescanMinutesMaximum)
                throw new ArgumentOutOfRangeException(nameof(FolderSourceRescanMinutes), "FolderSourceRescanMinutes is outside its supported range (" + FolderSourceRescanMinutesMinimum + " to " + FolderSourceRescanMinutesMaximum + ").");
            if (FolderSourceCatalogLimitMiB is < FolderSourceCatalogLimitMiBMinimum or > FolderSourceCatalogLimitMiBMaximum)
                throw new ArgumentOutOfRangeException(nameof(FolderSourceCatalogLimitMiB), "FolderSourceCatalogLimitMiB is outside its supported range (" + FolderSourceCatalogLimitMiBMinimum + " to " + FolderSourceCatalogLimitMiBMaximum + ").");
            if (FolderSourceGalleryHashWindow is < FolderSourceGalleryHashWindowMinimum or > FolderSourceGalleryHashWindowMaximum)
                throw new ArgumentOutOfRangeException(nameof(FolderSourceGalleryHashWindow), "FolderSourceGalleryHashWindow is outside its supported range (" + FolderSourceGalleryHashWindowMinimum + " to " + FolderSourceGalleryHashWindowMaximum + ").");
            if (PluginEditorExecutable is { Length: > 4096 } || PluginEditorExecutable?.Any(char.IsControl) == true
                || PluginEditorArguments == null || PluginEditorArguments.Length > 32
                || PluginEditorArguments.Any(value => value == null || value.Length > 4096 || value.Any(char.IsControl)))
                throw new ArgumentException("The plugin editor command is invalid.", nameof(PluginEditorExecutable));
            if (string.IsNullOrWhiteSpace(DownloadPath))
                throw new ArgumentException("A download directory is required.", nameof(DownloadPath));

            try { _ = Path.GetFullPath(DownloadPath, _relativePathBaseDirectory); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            { throw new ArgumentException("The download directory path is invalid.", nameof(DownloadPath), error); }
            if (!MeowChan.Interop.ExportFilenameTemplate.Check(ExportFilenameTemplate).Ok)
                throw new ArgumentException("The export filename template is invalid.", nameof(ExportFilenameTemplate));
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
                PreferenceDefaultsRevision = PreferenceDefaultsRevision,
                FirstRunSetupPending = FirstRunSetupPending, FirstRunSetupVersion = FirstRunSetupVersion,
                SourceMediaIntegrityPermissions = SourceMediaIntegrityPermissions.ToDictionary(pair => pair.Key, pair => pair.Value with { }, StringComparer.Ordinal),
                DownloadPath = DownloadPath,
                ExportFilenameTemplate = ExportFilenameTemplate,
                RecheckTimer = RecheckTimer,
                SaveUrlsOnExit = SaveUrlsOnExit,
                ShowBoardAdmissionWizard = ShowBoardAdmissionWizard,
                WorkViewNoticePending = WorkViewNoticePending,
                WorkViewSwitchNoticeSeconds = WorkViewSwitchNoticeSeconds,
                ShowBooruTools = ShowBooruTools,
                FooterPages = (string[])FooterPages.Clone(),
                RememberReadingPosition = RememberReadingPosition,
                MonitorClipboardUrls = MonitorClipboardUrls,
                DownloadsPaused = DownloadsPaused,
                MinimizeToTray = MinimizeToTray,
                CloseToTray = CloseToTray,
                ShutdownTimeLimitSeconds = ShutdownTimeLimitSeconds,
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
                LocalTaggerManifestKiB = LocalTaggerManifestKiB,
                LocalTaggerWeightsMiB = LocalTaggerWeightsMiB,
                LocalTaggerInputTensorMiB = LocalTaggerInputTensorMiB,
                BooruCatalogLimitMiB = BooruCatalogLimitMiB,
                BooruRetryAfterCooldownHours = BooruRetryAfterCooldownHours,
                ArchiveTagFilterTagsPerList = ArchiveTagFilterTagsPerList,
                UrlTextExportLimitMiB = UrlTextExportLimitMiB,
                ExportHistorySizeMiB = ExportHistorySizeMiB,
                ExportHistoryEntryLimit = ExportHistoryEntryLimit,
                BoardAdmissionSizeMiB = BoardAdmissionSizeMiB,
                SlashTagSuggestionCount = SlashTagSuggestionCount,
                ShrinkWindowForBarebones = ShrinkWindowForBarebones,
                PrivateTagLimit = PrivateTagLimit,
                PrivateTagFileLimitMiB = PrivateTagFileLimitMiB,
                MediaIndexCacheLimitMiB = MediaIndexCacheLimitMiB,
                FavoriteEntryLimit = FavoriteEntryLimit,
                FavoriteFileLimitMiB = FavoriteFileLimitMiB,
                PostBookmarkLimit = PostBookmarkLimit,
                SavedSearchLimit = SavedSearchLimit,
                ReaderGroupLimit = ReaderGroupLimit,
                TagQueueLimit = TagQueueLimit,
                TagQueueFileLimitMiB = TagQueueFileLimitMiB,
                TagQueueHistory = TagQueueHistory,
                TagQueueGroupLimit = TagQueueGroupLimit,
                TagQueueGroupNameLimit = TagQueueGroupNameLimit,
                TagQueueConfirmAbove = TagQueueConfirmAbove,
                MediaTagListTop = MediaTagListTop,
                BooruResultTagsShown = BooruResultTagsShown,
                BooruNewSearchTarget = BooruNewSearchTarget,
                BooruSearchTabs = BooruSearchTabs,
                RestoreBooruSearchTabs = RestoreBooruSearchTabs,
                BooruSearchPages = BooruSearchPages,
                BooruSearchPostsPerPage = BooruSearchPostsPerPage,
                BooruSavedTabs = BooruSavedTabs,
                BooruSavedResultsPerTab = BooruSavedResultsPerTab,
                BooruGallerySampleMiB = BooruGallerySampleMiB,
                BooruGalleryCacheMiB = BooruGalleryCacheMiB,
                BooruGalleryMaximumPixels = BooruGalleryMaximumPixels,
                BooruGalleryMaximumWidth = BooruGalleryMaximumWidth,
                BooruGalleryTimeoutSeconds = BooruGalleryTimeoutSeconds,
                MediaPanelMinimumPageWidth = MediaPanelMinimumPageWidth,
                ProfileRegistryLimit = ProfileRegistryLimit,
                TagQueueWalkWindow = TagQueueWalkWindow,
                TagQueueUndoDepth = TagQueueUndoDepth,
                TagStampSize = TagStampSize,
                SourcesWatchPreviewRows = SourcesWatchPreviewRows,
                ExportConfirmFiles = ExportConfirmFiles,
                ExportConfirmGiB = ExportConfirmGiB,
                PosterCacheLimitMiB = PosterCacheLimitMiB,
                VideoScrubCacheLimitMiB = VideoScrubCacheLimitMiB,
                PosterCacheFileLimit = PosterCacheFileLimit,
                AudioProbeCacheLimitMiB = AudioProbeCacheLimitMiB,
                AudioProbeCacheEntries = AudioProbeCacheEntries,
                ThumbnailCacheTiles = ThumbnailCacheTiles,
                ClipboardUrlLimit = ClipboardUrlLimit,
                ClipboardTextLimitKiB = ClipboardTextLimitKiB,
                StatsSampleIntervalMs = StatsSampleIntervalMs,
                ControlsIdleHideMs = ControlsIdleHideMs,
                ReaderNavigationMode = ReaderNavigationMode,
                ReaderNavigationShowPosition = ReaderNavigationShowPosition,
                GalleryWheelMode = GalleryWheelMode,
                GalleryStatusDwellMs = GalleryStatusDwellMs,
                GalleryPostTagChipLimit = GalleryPostTagChipLimit,
                GalleryFilmstrip = GalleryFilmstrip,
                GalleryLayout = GalleryLayout,
                GalleryFilmstripHeight = GalleryFilmstripHeight,
                GalleryFilmstripTileGap = GalleryFilmstripTileGap,
                GalleryFilmstripFlagFavorite = GalleryFilmstripFlagFavorite,
                GalleryFilmstripFlagRating = GalleryFilmstripFlagRating,
                GalleryFilmstripFlagUntagged = GalleryFilmstripFlagUntagged,
                GalleryFilmstripPreload = GalleryFilmstripPreload,
                GalleryVideoSeekStepSeconds = GalleryVideoSeekStepSeconds,
                MediaBadgeCollapseBelow = MediaBadgeCollapseBelow,
                MediaBadgeHideBelow = MediaBadgeHideBelow,
                MediaBadgeMaxPerCorner = MediaBadgeMaxPerCorner,
                StatsHistorySamples = StatsHistorySamples,
                StatsBackgroundIntervalMs = StatsBackgroundIntervalMs,
                StatsTrayTooltip = StatsTrayTooltip,
                StatsTrayMenu = StatsTrayMenu,
                StatsPersist = StatsPersist,
                StatsRetentionDays = StatsRetentionDays,
                StatsPersistIntervalMinutes = StatsPersistIntervalMinutes,
                StatsMaxSources = StatsMaxSources,
                StatsHeaderGraph = StatsHeaderGraph,
                StatsHeaderIcons = StatsHeaderIcons,
                HeaderTagline = HeaderTagline,
                HeaderTaglineFixedId = HeaderTaglineFixedId,
                HeaderTaglineIntervalMinutes = HeaderTaglineIntervalMinutes,
                LastTaglineVersion = LastTaglineVersion,
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
                FolderSourceMaxFiles = FolderSourceMaxFiles,
                FolderSourceMaxRoots = FolderSourceMaxRoots,
                FolderSourceHashParallelism = FolderSourceHashParallelism,
                FolderSourceHashMaxFileGiB = FolderSourceHashMaxFileGiB,
                FolderSourceRescanMinutes = FolderSourceRescanMinutes,
                FolderSourceCatalogLimitMiB = FolderSourceCatalogLimitMiB,
                FolderSourceGalleryHashWindow = FolderSourceGalleryHashWindow,
                FolderSourceBackgroundHashing = FolderSourceBackgroundHashing,
                ReaderThumbnailWidth = ReaderThumbnailWidth,
                ReaderFoldLines = ReaderFoldLines,
                ReaderPreviewLines = ReaderPreviewLines,
                ReaderPreviewDelayMs = ReaderPreviewDelayMs,
                ReaderReplyStep = ReaderReplyStep,
                ReaderReplyLines = ReaderReplyLines,
                ReaderExpandReplies = ReaderExpandReplies,
                ReaderPageSize = ReaderPageSize,
                ReaderThreadingDepth = ReaderThreadingDepth,
                ReadingHistoryLimit = ReadingHistoryLimit,
                ReadingHistoryMenuEntries = ReadingHistoryMenuEntries,
                ReaderTextlessMediaWidth = ReaderTextlessMediaWidth,
                ReaderHoverEnlargeSize = ReaderHoverEnlargeSize,
                ReaderHoverEnlargeDelayMs = ReaderHoverEnlargeDelayMs,
                ReaderFocusedMediaScalePercent = ReaderFocusedMediaScalePercent,
                ReaderTextlessLargeImages = ReaderTextlessLargeImages,
                ReaderHoverEnlarge = ReaderHoverEnlarge,
                ReaderFocusedMediaEnlarge = ReaderFocusedMediaEnlarge,
                ReaderMediaSidebar = ReaderMediaSidebar,
                ReaderMediaSidebarSide = ReaderMediaSidebarSide,
                ReaderMediaSidebarScope = ReaderMediaSidebarScope,
                ReaderMediaSidebarWidth = ReaderMediaSidebarWidth,
                ReaderMediaSidebarMinimumWidth = ReaderMediaSidebarMinimumWidth,
                ReaderInlineVideo = ReaderInlineVideo,
                ReaderMediaClick = ReaderMediaClick,
                ReaderInlineVideoAutoplayMuted = ReaderInlineVideoAutoplayMuted,
                ReaderKeepImagesExpanded = ReaderKeepImagesExpanded,
                ReaderSidebarVideoAutoplay = ReaderSidebarVideoAutoplay,
                ReaderSidebarVideoLoop = ReaderSidebarVideoLoop,
                ReaderSidebarVideoStartMuted = ReaderSidebarVideoStartMuted,
                ReaderQuotePreviewAutoplay = ReaderQuotePreviewAutoplay,
                ReaderQuotePreviewMuted = ReaderQuotePreviewMuted,
                ReaderInlineVideoMaxPlayers = ReaderInlineVideoMaxPlayers,
                ReaderInlineVideoMaxFileMiB = ReaderInlineVideoMaxFileMiB,
                ReaderMediaStripLimit = ReaderMediaStripLimit,
                ReaderMediaGrid = ReaderMediaGrid,
                ReaderMediaGridTextLimit = ReaderMediaGridTextLimit,
                ReaderPreviewPrefetchPosts = ReaderPreviewPrefetchPosts,
                ReaderPreviewSizeMemory = ReaderPreviewSizeMemory,
                GalleryFitMaxUpscalePercent = GalleryFitMaxUpscalePercent,
                GalleryMaxZoomPercent = GalleryMaxZoomPercent,
                PostFilterRuleLimit = PostFilterRuleLimit,
                HiddenPostLimit = HiddenPostLimit,
                QuickTagSuggestionCount = QuickTagSuggestionCount,
                QuickTagPanelSuggestionLimit = QuickTagPanelSuggestionLimit,
                QuickTagRecentCount = QuickTagRecentCount,
                QuickTagUndoDepth = QuickTagUndoDepth,
                QuickTagNeighbourScan = QuickTagNeighbourScan,
                QuickTagFilenameTokens = QuickTagFilenameTokens,
                QuickTagSourceTagsPerMedium = QuickTagSourceTagsPerMedium,
                QuickTagMarkedMediaLimit = QuickTagMarkedMediaLimit,
                ReaderDateStyle = ReaderDateStyle,
                ReaderSpoilers = ReaderSpoilers,
                MediaSort = MediaSort,
                MediaSortDirection = MediaSortDirection,
                MediaBooruMode = MediaBooruMode,
                MediaTagListSort = MediaTagListSort,
                MediaLayout = MediaLayout,
                ArchiveMediaTileSize = ArchiveMediaTileSize,
                TaggingPageView = TaggingPageView,
                BooruPageView = BooruPageView,
                BooruTileSize = BooruTileSize,
                TaggingPreviewColumn = TaggingPreviewColumn,
                TaggingStampBlock = TaggingStampBlock,
                GalleryFitUpscaleLimited = GalleryFitUpscaleLimited,

                ShortcutBindings = ShortcutBindings.Clone(),
                PluginEditorExecutable = PluginEditorExecutable,
                PluginEditorArguments = (string[])PluginEditorArguments.Clone(),
                TagStamp = (string[])TagStamp.Clone(),
                TaggingCardsDefault = TaggingCardsDefault,
                TaggingCards = (string[])TaggingCards.Clone(),
                TaggingCardCount = TaggingCardCount,
                Theme = Theme,
                ThemeOverrides = ThemeOverrides,
                Animations = Animations,
                Language = Language,
                WorkspaceMode = WorkspaceMode,
                CompactDensity = CompactDensity,
                SettingsBaselineCompactDensity = CompactDensity,
                SettingsBaselineReadingFontSize = ReadingFontSize,
                SettingsBaselineWorkspaceMode = WorkspaceMode,
                SettingsBaselineVideoVolume = VideoVolume,
                SettingsBaselineGalleryFilmstrip = GalleryFilmstrip,
                SettingsBaselineMediaTagListWidth = Desktop.MediaPageLayout.Parse(MediaLayout, MediaBooruMode).TagListWidth,
                SettingsBaselineMediaPanelWidth = Desktop.MediaPageLayout.Parse(MediaLayout, MediaBooruMode).PanelWidth,
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
                GalleryPauseOnFocusLoss = GalleryPauseOnFocusLoss,
                GalleryMediaFocus = GalleryMediaFocus,
                GalleryOverlayControls = GalleryOverlayControls,
                GalleryAutoHideControls = GalleryOverlayControls,
                GalleryOverlayPostContext = GalleryOverlayPostContext,
                ShowShortcutHelp = ShowShortcutHelp,
                MediaFocusHidesHeader = MediaFocusHidesHeader,
                GalleryContextOpen = GalleryContextOpen,
                ArchiveSplitWidth = ArchiveSplitWidth,
                WindowWidth = WindowWidth,
                WindowHeight = WindowHeight,
                WindowPositionX = WindowPositionX,
                WindowPositionY = WindowPositionY,
                WindowMaximized = WindowMaximized,
                BooruViewerPlacement = BooruViewerPlacement,
                ColumnWidths = ColumnWidths,
                LayoutProfiles = _layoutProfiles.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal),
                LastExportDirectory = LastExportDirectory,
                GalleryFrameDirectory = GalleryFrameDirectory,
                ArchiveBridgeHintSeen = ArchiveBridgeHintSeen,
                ArchiveThreadListHidden = ArchiveThreadListHidden,
                StatusPageSegment = StatusPageSegment,
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
            PreferenceDefaultsRevision = source.PreferenceDefaultsRevision;
            FirstRunSetupPending = source.FirstRunSetupPending; FirstRunSetupVersion = source.FirstRunSetupVersion;
            // Consent is portable within this profile's clones, never across profile-bound instances.
            if (string.Equals(_profileDirectory, source._profileDirectory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                SourceMediaIntegrityPermissions = source.SourceMediaIntegrityPermissions.ToDictionary(pair => pair.Key, pair => pair.Value with { }, StringComparer.Ordinal);
            DownloadPath = absoluteDownloadPath;
            ExportFilenameTemplate = source.ExportFilenameTemplate;
            RecheckTimer = source.RecheckTimer;
            SaveUrlsOnExit = source.SaveUrlsOnExit;
            ShowBoardAdmissionWizard = source.ShowBoardAdmissionWizard;
            WorkViewNoticePending = source.WorkViewNoticePending;
            WorkViewSwitchNoticeSeconds = source.WorkViewSwitchNoticeSeconds;
            ShowBooruTools = source.ShowBooruTools;
            FooterPages = (string[])source.FooterPages.Clone();
            RememberReadingPosition = source.RememberReadingPosition;
            MonitorClipboardUrls = source.MonitorClipboardUrls;
            DownloadsPaused = source.DownloadsPaused;
            MinimizeToTray = source.MinimizeToTray;
            CloseToTray = source.CloseToTray;
            ShutdownTimeLimitSeconds = source.ShutdownTimeLimitSeconds;
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
            LocalTaggerManifestKiB = source.LocalTaggerManifestKiB;
            LocalTaggerWeightsMiB = source.LocalTaggerWeightsMiB;
            LocalTaggerInputTensorMiB = source.LocalTaggerInputTensorMiB;
            BooruCatalogLimitMiB = source.BooruCatalogLimitMiB;
            BooruRetryAfterCooldownHours = source.BooruRetryAfterCooldownHours;
            ArchiveTagFilterTagsPerList = source.ArchiveTagFilterTagsPerList;
            UrlTextExportLimitMiB = source.UrlTextExportLimitMiB;
            ExportHistorySizeMiB = source.ExportHistorySizeMiB;
            ExportHistoryEntryLimit = source.ExportHistoryEntryLimit;
            BoardAdmissionSizeMiB = source.BoardAdmissionSizeMiB;
            SlashTagSuggestionCount = source.SlashTagSuggestionCount;
            ShrinkWindowForBarebones = source.ShrinkWindowForBarebones;
            PrivateTagLimit = source.PrivateTagLimit;
            PrivateTagFileLimitMiB = source.PrivateTagFileLimitMiB;
            MediaIndexCacheLimitMiB = source.MediaIndexCacheLimitMiB;
            FavoriteEntryLimit = source.FavoriteEntryLimit;
            FavoriteFileLimitMiB = source.FavoriteFileLimitMiB;
            PostBookmarkLimit = source.PostBookmarkLimit;
            SavedSearchLimit = source.SavedSearchLimit;
            ReaderGroupLimit = source.ReaderGroupLimit;
            TagQueueLimit = source.TagQueueLimit;
            TagQueueFileLimitMiB = source.TagQueueFileLimitMiB;
            TagQueueHistory = source.TagQueueHistory;
            TagQueueGroupLimit = source.TagQueueGroupLimit;
            TagQueueGroupNameLimit = source.TagQueueGroupNameLimit;
            TagQueueConfirmAbove = source.TagQueueConfirmAbove;
            MediaTagListTop = source.MediaTagListTop;
            BooruResultTagsShown = source.BooruResultTagsShown;
            BooruNewSearchTarget = source.BooruNewSearchTarget;
            BooruSearchTabs = source.BooruSearchTabs;
            RestoreBooruSearchTabs = source.RestoreBooruSearchTabs;
            BooruSearchPages = source.BooruSearchPages;
            BooruSearchPostsPerPage = source.BooruSearchPostsPerPage;
            BooruSavedTabs = source.BooruSavedTabs;
            BooruSavedResultsPerTab = source.BooruSavedResultsPerTab;
            BooruGallerySampleMiB = source.BooruGallerySampleMiB;
            BooruGalleryCacheMiB = source.BooruGalleryCacheMiB;
            CopyBooruGalleryLimitsFrom(source);
            MediaPanelMinimumPageWidth = source.MediaPanelMinimumPageWidth;
            ProfileRegistryLimit = source.ProfileRegistryLimit;
            TagQueueWalkWindow = source.TagQueueWalkWindow;
            TagQueueUndoDepth = source.TagQueueUndoDepth;
            TagStampSize = source.TagStampSize;
            SourcesWatchPreviewRows = source.SourcesWatchPreviewRows;
            ExportConfirmFiles = source.ExportConfirmFiles;
            ExportConfirmGiB = source.ExportConfirmGiB;
            PosterCacheLimitMiB = source.PosterCacheLimitMiB;
            VideoScrubCacheLimitMiB = source.VideoScrubCacheLimitMiB;
            PosterCacheFileLimit = source.PosterCacheFileLimit;
            AudioProbeCacheLimitMiB = source.AudioProbeCacheLimitMiB;
            AudioProbeCacheEntries = source.AudioProbeCacheEntries;
            ThumbnailCacheTiles = source.ThumbnailCacheTiles;
            ClipboardUrlLimit = source.ClipboardUrlLimit;
            ClipboardTextLimitKiB = source.ClipboardTextLimitKiB;
            StatsSampleIntervalMs = source.StatsSampleIntervalMs;
            ControlsIdleHideMs = source.ControlsIdleHideMs;
            ReaderNavigationMode = source.ReaderNavigationMode;
            ReaderNavigationShowPosition = source.ReaderNavigationShowPosition;
            GalleryWheelMode = source.GalleryWheelMode;
            GalleryStatusDwellMs = source.GalleryStatusDwellMs;
            GalleryPostTagChipLimit = source.GalleryPostTagChipLimit;
            GalleryFilmstrip = source.GalleryFilmstrip;
            GalleryLayout = source.GalleryLayoutResetRequested ? null : source.GalleryLayout; // (a draft that asked for the reset: nothing chosen)
            GalleryFilmstripHeight = source.GalleryFilmstripHeight;
            GalleryFilmstripTileGap = source.GalleryFilmstripTileGap;
            GalleryFilmstripFlagFavorite = source.GalleryFilmstripFlagFavorite;
            GalleryFilmstripFlagRating = source.GalleryFilmstripFlagRating;
            GalleryFilmstripFlagUntagged = source.GalleryFilmstripFlagUntagged;
            GalleryFilmstripPreload = source.GalleryFilmstripPreload;
            GalleryVideoSeekStepSeconds = source.GalleryVideoSeekStepSeconds;
            MediaBadgeCollapseBelow = source.MediaBadgeCollapseBelow;
            MediaBadgeHideBelow = source.MediaBadgeHideBelow;
            MediaBadgeMaxPerCorner = source.MediaBadgeMaxPerCorner;
            StatsHistorySamples = source.StatsHistorySamples;
            StatsBackgroundIntervalMs = source.StatsBackgroundIntervalMs;
            StatsTrayTooltip = source.StatsTrayTooltip;
            StatsTrayMenu = source.StatsTrayMenu;
            StatsPersist = source.StatsPersist;
            StatsRetentionDays = source.StatsRetentionDays;
            StatsPersistIntervalMinutes = source.StatsPersistIntervalMinutes;
            StatsMaxSources = source.StatsMaxSources;
            StatsHeaderGraph = source.StatsHeaderGraph;
            StatsHeaderIcons = source.StatsHeaderIcons;
            HeaderTagline = source.HeaderTagline;
            HeaderTaglineFixedId = source.HeaderTaglineFixedId;
            HeaderTaglineIntervalMinutes = source.HeaderTaglineIntervalMinutes;
            LastTaglineVersion = source.LastTaglineVersion;
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
            FolderSourceMaxFiles = source.FolderSourceMaxFiles;
            FolderSourceMaxRoots = source.FolderSourceMaxRoots;
            FolderSourceHashParallelism = source.FolderSourceHashParallelism;
            FolderSourceHashMaxFileGiB = source.FolderSourceHashMaxFileGiB;
            FolderSourceRescanMinutes = source.FolderSourceRescanMinutes;
            FolderSourceCatalogLimitMiB = source.FolderSourceCatalogLimitMiB;
            FolderSourceGalleryHashWindow = source.FolderSourceGalleryHashWindow;
            FolderSourceBackgroundHashing = source.FolderSourceBackgroundHashing;
            ReaderThumbnailWidth = source.ReaderThumbnailWidth;
            ReaderFoldLines = source.ReaderFoldLines;
            ReaderPreviewLines = source.ReaderPreviewLines;
            ReaderPreviewDelayMs = source.ReaderPreviewDelayMs;
            ReaderReplyStep = source.ReaderReplyStep;
            ReaderReplyLines = source.ReaderReplyLines;
            ReaderExpandReplies = source.ReaderExpandReplies;
            ReaderPageSize = source.ReaderPageSize;
            ReaderThreadingDepth = source.ReaderThreadingDepth;
            ReadingHistoryLimit = source.ReadingHistoryLimit;
            ReadingHistoryMenuEntries = source.ReadingHistoryMenuEntries;
            ReaderTextlessMediaWidth = source.ReaderTextlessMediaWidth;
            ReaderHoverEnlargeSize = source.ReaderHoverEnlargeSize;
            ReaderHoverEnlargeDelayMs = source.ReaderHoverEnlargeDelayMs;
            ReaderFocusedMediaScalePercent = source.ReaderFocusedMediaScalePercent;
            ReaderTextlessLargeImages = source.ReaderTextlessLargeImages;
            ReaderHoverEnlarge = source.ReaderHoverEnlarge;
            ReaderFocusedMediaEnlarge = source.ReaderFocusedMediaEnlarge;
            ReaderMediaSidebar = source.ReaderMediaSidebar;
            ReaderMediaSidebarSide = source.ReaderMediaSidebarSide;
            ReaderMediaSidebarScope = source.ReaderMediaSidebarScope;
            ReaderMediaSidebarWidth = source.ReaderMediaSidebarWidth;
            ReaderMediaSidebarMinimumWidth = source.ReaderMediaSidebarMinimumWidth;
            ReaderInlineVideo = source.ReaderInlineVideo;
            ReaderMediaClick = source.ReaderMediaClick;
            ReaderInlineVideoAutoplayMuted = source.ReaderInlineVideoAutoplayMuted;
            ReaderKeepImagesExpanded = source.ReaderKeepImagesExpanded;
            ReaderSidebarVideoAutoplay = source.ReaderSidebarVideoAutoplay;
            ReaderSidebarVideoLoop = source.ReaderSidebarVideoLoop;
            ReaderSidebarVideoStartMuted = source.ReaderSidebarVideoStartMuted;
            ReaderQuotePreviewAutoplay = source.ReaderQuotePreviewAutoplay;
            ReaderQuotePreviewMuted = source.ReaderQuotePreviewMuted;
            ReaderInlineVideoMaxPlayers = source.ReaderInlineVideoMaxPlayers;
            ReaderInlineVideoMaxFileMiB = source.ReaderInlineVideoMaxFileMiB;
            ReaderMediaStripLimit = source.ReaderMediaStripLimit;
            ReaderMediaGrid = source.ReaderMediaGrid;
            ReaderMediaGridTextLimit = source.ReaderMediaGridTextLimit;
            ReaderPreviewPrefetchPosts = source.ReaderPreviewPrefetchPosts;
            ReaderPreviewSizeMemory = source.ReaderPreviewSizeMemory;
            GalleryFitMaxUpscalePercent = source.GalleryFitMaxUpscalePercent;
            GalleryMaxZoomPercent = source.GalleryMaxZoomPercent;
            PostFilterRuleLimit = source.PostFilterRuleLimit;
            HiddenPostLimit = source.HiddenPostLimit;
            QuickTagSuggestionCount = source.QuickTagSuggestionCount;
            QuickTagPanelSuggestionLimit = source.QuickTagPanelSuggestionLimit;
            QuickTagRecentCount = source.QuickTagRecentCount;
            QuickTagUndoDepth = source.QuickTagUndoDepth;
            QuickTagNeighbourScan = source.QuickTagNeighbourScan;
            QuickTagFilenameTokens = source.QuickTagFilenameTokens;
            QuickTagSourceTagsPerMedium = source.QuickTagSourceTagsPerMedium;
            QuickTagMarkedMediaLimit = source.QuickTagMarkedMediaLimit;
            ReaderDateStyle = source.ReaderDateStyle;
            ReaderSpoilers = source.ReaderSpoilers;
            MediaSort = source.MediaSort;
            MediaSortDirection = source.MediaSortDirection;
            MediaBooruMode = source.MediaBooruMode;
            MediaTagListSort = source.MediaTagListSort;
            MediaLayout = source.MediaLayout;
            ArchiveMediaTileSize = source.ArchiveMediaTileSize;
            TaggingPageView = source.TaggingPageView;
            BooruPageView = source.BooruPageView;
            BooruTileSize = source.BooruTileSize;
            TaggingPreviewColumn = source.TaggingPreviewColumn;
            TaggingStampBlock = source.TaggingStampBlock;
            GalleryFitUpscaleLimited = source.GalleryFitUpscaleLimited;

            ShortcutBindings = source.ShortcutBindings.Clone();
            Theme = source.Theme;
            ThemeOverrides = source.ThemeOverrides;
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
            MediaFocusHidesHeader = source.MediaFocusHidesHeader;
            GalleryContextOpen = source.GalleryContextOpen;
            ArchiveSplitWidth = source.ArchiveSplitWidth;
            WindowWidth = source.WindowWidth;
            WindowHeight = source.WindowHeight;
            WindowPositionX = source.WindowPositionX;
            WindowPositionY = source.WindowPositionY;
            WindowMaximized = source.WindowMaximized;
            BooruViewerPlacement = source.BooruViewerPlacement;
            ColumnWidths = source.ColumnWidths;
            LayoutProfiles = source._layoutProfiles.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
            LastExportDirectory = source.LastExportDirectory;
            GalleryFrameDirectory = source.GalleryFrameDirectory;
            ArchiveBridgeHintSeen = source.ArchiveBridgeHintSeen;
            ArchiveThreadListHidden = source.ArchiveThreadListHidden;
            StatusPageSegment = source.StatusPageSegment;
            PluginEditorExecutable = source.PluginEditorExecutable;
            PluginEditorArguments = (string[])source.PluginEditorArguments.Clone();
            TagStamp = (string[])source.TagStamp.Clone();
            TaggingCardsDefault = source.TaggingCardsDefault;
            TaggingCards = (string[])source.TaggingCards.Clone();
            TaggingCardCount = source.TaggingCardCount;
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
            ExportFilenameTemplate = source.ExportFilenameTemplate;
            RecheckTimer = source.RecheckTimer;
            SaveUrlsOnExit = source.SaveUrlsOnExit;
            ShowBoardAdmissionWizard = source.ShowBoardAdmissionWizard;
            WorkViewSwitchNoticeSeconds = source.WorkViewSwitchNoticeSeconds;
            ShowBooruTools = source.ShowBooruTools;
            FooterPages = (string[])source.FooterPages.Clone();
            RememberReadingPosition = source.RememberReadingPosition;
            MonitorClipboardUrls = source.MonitorClipboardUrls;
            MinimizeToTray = source.MinimizeToTray;
            CloseToTray = source.CloseToTray;
            ShutdownTimeLimitSeconds = source.ShutdownTimeLimitSeconds;
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
            LocalTaggerManifestKiB = source.LocalTaggerManifestKiB;
            LocalTaggerWeightsMiB = source.LocalTaggerWeightsMiB;
            LocalTaggerInputTensorMiB = source.LocalTaggerInputTensorMiB;
            BooruCatalogLimitMiB = source.BooruCatalogLimitMiB;
            BooruRetryAfterCooldownHours = source.BooruRetryAfterCooldownHours;
            ArchiveTagFilterTagsPerList = source.ArchiveTagFilterTagsPerList;
            UrlTextExportLimitMiB = source.UrlTextExportLimitMiB;
            ExportHistorySizeMiB = source.ExportHistorySizeMiB;
            ExportHistoryEntryLimit = source.ExportHistoryEntryLimit;
            BoardAdmissionSizeMiB = source.BoardAdmissionSizeMiB;
            SlashTagSuggestionCount = source.SlashTagSuggestionCount;
            ShrinkWindowForBarebones = source.ShrinkWindowForBarebones;
            PrivateTagLimit = source.PrivateTagLimit;
            PrivateTagFileLimitMiB = source.PrivateTagFileLimitMiB;
            MediaIndexCacheLimitMiB = source.MediaIndexCacheLimitMiB;
            FavoriteEntryLimit = source.FavoriteEntryLimit;
            FavoriteFileLimitMiB = source.FavoriteFileLimitMiB;
            PostBookmarkLimit = source.PostBookmarkLimit;
            SavedSearchLimit = source.SavedSearchLimit;
            ReaderGroupLimit = source.ReaderGroupLimit;
            TagQueueLimit = source.TagQueueLimit;
            TagQueueFileLimitMiB = source.TagQueueFileLimitMiB;
            TagQueueHistory = source.TagQueueHistory;
            TagQueueGroupLimit = source.TagQueueGroupLimit;
            TagQueueGroupNameLimit = source.TagQueueGroupNameLimit;
            TagQueueConfirmAbove = source.TagQueueConfirmAbove;
            MediaTagListTop = source.MediaTagListTop;
            BooruResultTagsShown = source.BooruResultTagsShown;
            BooruNewSearchTarget = source.BooruNewSearchTarget;
            BooruSearchTabs = source.BooruSearchTabs;
            RestoreBooruSearchTabs = source.RestoreBooruSearchTabs;
            BooruSearchPages = source.BooruSearchPages;
            BooruSearchPostsPerPage = source.BooruSearchPostsPerPage;
            BooruSavedTabs = source.BooruSavedTabs;
            BooruSavedResultsPerTab = source.BooruSavedResultsPerTab;
            BooruGallerySampleMiB = source.BooruGallerySampleMiB;
            BooruGalleryCacheMiB = source.BooruGalleryCacheMiB;
            CopyBooruGalleryLimitsFrom(source);
            MediaPanelMinimumPageWidth = source.MediaPanelMinimumPageWidth;
            ProfileRegistryLimit = source.ProfileRegistryLimit;
            TagQueueWalkWindow = source.TagQueueWalkWindow;
            TagQueueUndoDepth = source.TagQueueUndoDepth;
            TagStampSize = source.TagStampSize;
            // The card mode choices are edited on the settings page (Tagging); K in the gallery still saves the start choice as a view preference.
            TaggingCardsDefault = source.TaggingCardsDefault;
            TaggingCards = (string[])source.TaggingCards.Clone();
            TaggingCardCount = source.TaggingCardCount;
            SourcesWatchPreviewRows = source.SourcesWatchPreviewRows;
            ExportConfirmFiles = source.ExportConfirmFiles;
            ExportConfirmGiB = source.ExportConfirmGiB;
            PosterCacheLimitMiB = source.PosterCacheLimitMiB;
            VideoScrubCacheLimitMiB = source.VideoScrubCacheLimitMiB;
            PosterCacheFileLimit = source.PosterCacheFileLimit;
            AudioProbeCacheLimitMiB = source.AudioProbeCacheLimitMiB;
            AudioProbeCacheEntries = source.AudioProbeCacheEntries;
            ThumbnailCacheTiles = source.ThumbnailCacheTiles;
            ClipboardUrlLimit = source.ClipboardUrlLimit;
            ClipboardTextLimitKiB = source.ClipboardTextLimitKiB;
            StatsSampleIntervalMs = source.StatsSampleIntervalMs;
            ControlsIdleHideMs = source.ControlsIdleHideMs;
            ReaderNavigationMode = source.ReaderNavigationMode;
            ReaderNavigationShowPosition = source.ReaderNavigationShowPosition;
            GalleryWheelMode = source.GalleryWheelMode;
            GalleryStatusDwellMs = source.GalleryStatusDwellMs;
            GalleryPostTagChipLimit = source.GalleryPostTagChipLimit;
            // GL4: the layout is a direct preference of the gallery and is not taken from a draft, except its reset. The filmstrip box of the
            // settings page is the old setting: only a box the person changed sets the filmstrip choice of both modes. Changed means against
            // the layout after the reset when the draft asked for one, otherwise against the box as the draft loaded it (its baseline); the
            // live value is no measure, the gallery may have changed it since (H1). A draft without a baseline compares with the live value.
            bool filmstripBaseline = source.SettingsBaselineGalleryFilmstrip ?? GalleryFilmstrip;
            if (source.GalleryLayoutResetRequested) { GalleryLayoutPreferences.Default.ApplyTo(this); filmstripBaseline = GalleryFilmstrip; }
            if (filmstripBaseline != source.GalleryFilmstrip)
                GalleryLayoutPreferences.FromConfig(this).WithFilmstripSetting(source.GalleryFilmstrip).ApplyTo(this);
            // (GalleryFilmstrip itself follows the View set's filmstrip through ApplyTo; an unchanged box leaves both as they are.)
            GalleryFilmstripHeight = source.GalleryFilmstripHeight;
            GalleryFilmstripTileGap = source.GalleryFilmstripTileGap;
            GalleryFilmstripFlagFavorite = source.GalleryFilmstripFlagFavorite;
            GalleryFilmstripFlagRating = source.GalleryFilmstripFlagRating;
            GalleryFilmstripFlagUntagged = source.GalleryFilmstripFlagUntagged;
            GalleryFilmstripPreload = source.GalleryFilmstripPreload;
            GalleryVideoSeekStepSeconds = source.GalleryVideoSeekStepSeconds;
            // M1: only the tag list width of the draft's media layout, and only when the box was changed against its baseline. The media page changes the
            // rest of the layout (and the width, by its handle) directly; copying the layout as a whole would roll a dragged width back to a stale draft (H1).
            int draftTagListWidth = Desktop.MediaPageLayout.Parse(source.MediaLayout, source.MediaBooruMode).TagListWidth;
            Desktop.MediaPageLayout liveLayout = Desktop.MediaPageLayout.Parse(MediaLayout, MediaBooruMode);
            if (draftTagListWidth != (source.SettingsBaselineMediaTagListWidth ?? liveLayout.TagListWidth) && draftTagListWidth != liveLayout.TagListWidth)
                MediaLayout = (liveLayout with { TagListWidth = draftTagListWidth }).Format();
            // The media panel's width follows the same rule (only a box the person changed against its baseline takes over).
            int draftPanelWidth = Desktop.MediaPageLayout.Parse(source.MediaLayout, source.MediaBooruMode).PanelWidth;
            Desktop.MediaPageLayout panelLayout = Desktop.MediaPageLayout.Parse(MediaLayout, MediaBooruMode);
            if (draftPanelWidth != (source.SettingsBaselineMediaPanelWidth ?? panelLayout.PanelWidth) && draftPanelWidth != panelLayout.PanelWidth)
                MediaLayout = (panelLayout with { PanelWidth = draftPanelWidth }).Format();
            MediaBadgeCollapseBelow = source.MediaBadgeCollapseBelow;
            MediaBadgeHideBelow = source.MediaBadgeHideBelow;
            MediaBadgeMaxPerCorner = source.MediaBadgeMaxPerCorner;
            StatsHistorySamples = source.StatsHistorySamples;
            StatsBackgroundIntervalMs = source.StatsBackgroundIntervalMs;
            StatsTrayTooltip = source.StatsTrayTooltip;
            StatsTrayMenu = source.StatsTrayMenu;
            StatsPersist = source.StatsPersist;
            StatsRetentionDays = source.StatsRetentionDays;
            StatsPersistIntervalMinutes = source.StatsPersistIntervalMinutes;
            StatsMaxSources = source.StatsMaxSources;
            StatsHeaderGraph = source.StatsHeaderGraph;
            StatsHeaderIcons = source.StatsHeaderIcons;
            HeaderTagline = source.HeaderTagline;
            HeaderTaglineFixedId = source.HeaderTaglineFixedId;
            HeaderTaglineIntervalMinutes = source.HeaderTaglineIntervalMinutes;
            // LastTaglineVersion is a direct view preference (the window writes it), not edited in Settings.
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
            FolderSourceMaxFiles = source.FolderSourceMaxFiles;
            FolderSourceMaxRoots = source.FolderSourceMaxRoots;
            FolderSourceHashParallelism = source.FolderSourceHashParallelism;
            FolderSourceHashMaxFileGiB = source.FolderSourceHashMaxFileGiB;
            FolderSourceRescanMinutes = source.FolderSourceRescanMinutes;
            FolderSourceCatalogLimitMiB = source.FolderSourceCatalogLimitMiB;
            FolderSourceGalleryHashWindow = source.FolderSourceGalleryHashWindow;
            FolderSourceBackgroundHashing = source.FolderSourceBackgroundHashing;
            ReaderThumbnailWidth = source.ReaderThumbnailWidth;
            ReaderFoldLines = source.ReaderFoldLines;
            ReaderPreviewLines = source.ReaderPreviewLines;
            ReaderPreviewDelayMs = source.ReaderPreviewDelayMs;
            ReaderReplyStep = source.ReaderReplyStep;
            ReaderReplyLines = source.ReaderReplyLines;
            ReaderExpandReplies = source.ReaderExpandReplies;
            ReaderPageSize = source.ReaderPageSize;
            ReaderThreadingDepth = source.ReaderThreadingDepth;
            ReadingHistoryLimit = source.ReadingHistoryLimit;
            ReadingHistoryMenuEntries = source.ReadingHistoryMenuEntries;
            ReaderTextlessMediaWidth = source.ReaderTextlessMediaWidth;
            ReaderHoverEnlargeSize = source.ReaderHoverEnlargeSize;
            ReaderHoverEnlargeDelayMs = source.ReaderHoverEnlargeDelayMs;
            ReaderFocusedMediaScalePercent = source.ReaderFocusedMediaScalePercent;
            ReaderTextlessLargeImages = source.ReaderTextlessLargeImages;
            ReaderHoverEnlarge = source.ReaderHoverEnlarge;
            ReaderFocusedMediaEnlarge = source.ReaderFocusedMediaEnlarge;
            ReaderMediaSidebar = source.ReaderMediaSidebar;
            ReaderMediaSidebarSide = source.ReaderMediaSidebarSide;
            ReaderMediaSidebarScope = source.ReaderMediaSidebarScope;
            ReaderMediaSidebarWidth = source.ReaderMediaSidebarWidth;
            ReaderMediaSidebarMinimumWidth = source.ReaderMediaSidebarMinimumWidth;
            ReaderInlineVideo = source.ReaderInlineVideo;
            ReaderMediaClick = source.ReaderMediaClick;
            ReaderInlineVideoAutoplayMuted = source.ReaderInlineVideoAutoplayMuted;
            ReaderKeepImagesExpanded = source.ReaderKeepImagesExpanded;
            ReaderSidebarVideoAutoplay = source.ReaderSidebarVideoAutoplay;
            ReaderSidebarVideoLoop = source.ReaderSidebarVideoLoop;
            ReaderSidebarVideoStartMuted = source.ReaderSidebarVideoStartMuted;
            ReaderQuotePreviewAutoplay = source.ReaderQuotePreviewAutoplay;
            ReaderQuotePreviewMuted = source.ReaderQuotePreviewMuted;
            ReaderInlineVideoMaxPlayers = source.ReaderInlineVideoMaxPlayers;
            ReaderInlineVideoMaxFileMiB = source.ReaderInlineVideoMaxFileMiB;
            ReaderMediaStripLimit = source.ReaderMediaStripLimit;
            ReaderMediaGrid = source.ReaderMediaGrid;
            ReaderMediaGridTextLimit = source.ReaderMediaGridTextLimit;
            ReaderPreviewPrefetchPosts = source.ReaderPreviewPrefetchPosts;
            ReaderPreviewSizeMemory = source.ReaderPreviewSizeMemory;
            GalleryFitMaxUpscalePercent = source.GalleryFitMaxUpscalePercent;
            GalleryMaxZoomPercent = source.GalleryMaxZoomPercent;
            PostFilterRuleLimit = source.PostFilterRuleLimit;
            HiddenPostLimit = source.HiddenPostLimit;
            QuickTagSuggestionCount = source.QuickTagSuggestionCount;
            QuickTagPanelSuggestionLimit = source.QuickTagPanelSuggestionLimit;
            QuickTagRecentCount = source.QuickTagRecentCount;
            QuickTagUndoDepth = source.QuickTagUndoDepth;
            QuickTagNeighbourScan = source.QuickTagNeighbourScan;
            QuickTagFilenameTokens = source.QuickTagFilenameTokens;
            QuickTagSourceTagsPerMedium = source.QuickTagSourceTagsPerMedium;
            QuickTagMarkedMediaLimit = source.QuickTagMarkedMediaLimit;
            ReaderDateStyle = source.ReaderDateStyle;
            ReaderSpoilers = source.ReaderSpoilers;
            GalleryFitUpscaleLimited = source.GalleryFitUpscaleLimited;

            ShortcutBindings = source.ShortcutBindings.Clone();
            Theme = source.Theme;
            ThemeOverrides = source.ThemeOverrides;
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
            MediaFocusHidesHeader = source.MediaFocusHidesHeader;
        }

        internal void ForgetReadingPositionFile()
        {
            string directory = _profileDirectory ?? GetConfigDirectory();
            string history = Path.Combine(directory, LocalReadingHistory.FileName);
            // Try every copy even when one of the deletes fails.
            try { File.Delete(history + LocalReadingHistory.CorruptSuffix); }
            finally
            {
                try { File.Delete(history); }
                finally { File.Delete(Path.Combine(directory, LocalReadingPosition.FileName)); }
            }
        }

        internal bool TryCleanUpReadingPositionAfterOptOut()
        {
            if (RememberReadingPosition) return true;
            try { ForgetReadingPositionFile(); return true; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Log.Warning(error, "Could not remove all saved reading files after opting out.");
                return false;
            }
        }

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
            if (ShortcutBindings.HasRetiredOverrides) ShortcutBindings = ShortcutBindings.WithoutRetired(); // dead bindings of removed commands leave with the next save
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
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // A dangling symbolic link reads like a missing file, but the entry is there: damaged, not missing (as SessionStore does).
                // The next save keeps the link as a sidecar and replaces the entry (AtomicJsonFile).
                if (new FileInfo(filePath).LinkTarget != null)
                {
                    error = "symbolic link target is missing";
                    return LoadStatus.Invalid;
                }
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

                // Coerce stored preferences only; edited candidates must pass Validate without coercion.
                config.WorkViewSwitchNoticeSeconds = Math.Clamp(config.WorkViewSwitchNoticeSeconds,
                    WorkViewSwitchNoticeSecondsMinimum, WorkViewSwitchNoticeSecondsMaximum);
                config.ShutdownTimeLimitSeconds = Math.Clamp(config.ShutdownTimeLimitSeconds,
                    ShutdownTimeLimitSecondsMinimum, ShutdownTimeLimitSecondsMaximum);

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

    /// <summary>
    /// A list of names kept as a view preference (the tag stamp): a damaged value (not an array, other element types) never fails the
    /// whole settings file; it reads as the strings that are there, at most <see cref="MaximumItems"/> of them.
    /// </summary>
    internal sealed class DesktopPreferenceNameListConverter : JsonConverter<string[]>
    {
        internal const int MaximumItems = 64;
        public DesktopPreferenceNameListConverter() { }
        public override bool HandleNull => true;

        public override string[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                reader.Skip();
                return [];
            }
            List<string> items = new();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.String && items.Count < MaximumItems && reader.GetString() is { } text) items.Add(text);
                else reader.Skip();
            }
            return items.ToArray();
        }

        public override void Write(Utf8JsonWriter writer, string[] value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (string item in value ?? []) writer.WriteStringValue(item);
            writer.WriteEndArray();
        }
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

    internal sealed class DesktopPreferenceTaggingCardCountConverter : JsonConverter<int>
    {
        public override bool HandleNull => true;
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int value))
                return Math.Clamp(value, Config.TaggingCardCountMinimum, Config.TaggingCardCountMaximum);
            reader.Skip();
            return Config.TaggingCardCountDefault;
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
                return Config.NormalizeArchiveSplitWidth(value);
            reader.Skip();
            return Config.ArchiveSplitWidthDefault;
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

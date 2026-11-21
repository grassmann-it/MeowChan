using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeowChan.Desktop;

/// <summary>Builds the portable, non-destructive settings export. File I/O belongs to the UI caller.</summary>
public static class SettingsTransfer
{
    private const string ExportedByPrefix = "MeowChan ";

    /// <summary>
    /// Returns the current settings in the same JSON shape as <see cref="Config"/> serialization, with
    /// profile-local, runtime, consent and window-state fields removed. The input is not changed.
    /// </summary>
    public static JsonObject Export(Config source) => Export(source, ProductVersion());

    /// <summary>
    /// Returns an export using an explicit product version. Build metadata after '+' is not part of the displayed version.
    /// </summary>
    public static JsonObject Export(Config source, string productVersion)
        => Export(source, productVersion, SerializeSnapshot);

    // Per-call seam for observing the serialization boundary (including failures), without global test state.
    internal static JsonObject Export(Config source, string productVersion, Func<Config, JsonObject> serializeSnapshot)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(serializeSnapshot);
        string version = NormalizeProductVersion(productVersion);

        JsonObject document = serializeSnapshot(CreatePortableSnapshot(source));
        foreach (string key in ExcludedKeys)
            document.Remove(key);

        // SchemaVersion remains the Config schema marker so the document can be read by the existing settings serializer.
        document["ExportedBy"] = ExportedByPrefix + version;
        return document;
    }

    private static readonly string[] ExcludedKeys =
    [
        nameof(Config.WindowWidth),
        nameof(Config.WindowHeight),
        nameof(Config.WindowPositionX),
        nameof(Config.WindowPositionY),
        nameof(Config.WindowMaximized),
        nameof(Config.FirstRunSetupPending),
        nameof(Config.FirstRunSetupVersion),
        nameof(Config.WorkViewNoticePending),
        nameof(Config.SourceMediaIntegrityPermissions),
        nameof(Config.DownloadsPaused),
        nameof(Config.LastExportDirectory),
        nameof(Config.GalleryFrameDirectory),
        // These are persisted view/notice state rather than user settings.
        nameof(Config.GalleryContextOpen),
        nameof(Config.ArchiveBridgeHintSeen),
        nameof(Config.StatusPageSegment),
        nameof(Config.LastTaglineVersion), // the release quip already shown: notice state of this profile
    ];

    private static JsonObject SerializeSnapshot(Config snapshot)
    {
        JsonNode? node = JsonSerializer.SerializeToNode(snapshot, ConfigJsonContext.Default.Config);
        return node?.AsObject() ?? throw new InvalidOperationException("Config serialization returned no object.");
    }

    // Capture on the owning thread before awaiting a picker. The caller owns this detached value copy;
    // serialization (including compatibility getters) and background I/O must only see the copy.
    internal static Config CreatePortableSnapshot(Config source)
    {
        ArgumentNullException.ThrowIfNull(source);
        // Do not use Config.Clone(): it reads ActiveProfile and GetLayoutProfile for editor baselines,
        // materializing missing entries on source. An explicit portable value copy also keeps consent,
        // runtime state, geometry and last-used directories out of the serialization input altogether.
        // Keep this list in sync with Config; SettingsTransferTests compares every portable JSON key/value.
        Config snapshot = new()
        {
            SchemaVersion = source.SchemaVersion,
            // An export carries its own defaults revision, so an import migrates the file by what it was written with.
            PreferenceDefaultsRevision = source.PreferenceDefaultsRevision,
            DownloadPath = source.DownloadPath,
            RecheckTimer = source.RecheckTimer,
            SaveUrlsOnExit = source.SaveUrlsOnExit,
            ShowBoardAdmissionWizard = source.ShowBoardAdmissionWizard,
            RememberReadingPosition = source.RememberReadingPosition,
            MonitorClipboardUrls = source.MonitorClipboardUrls,
            MinimizeToTray = source.MinimizeToTray,
            CloseToTray = source.CloseToTray,
            ShutdownTimeLimitSeconds = source.ShutdownTimeLimitSeconds,
            BandwidthLimiter = source.BandwidthLimiter,
            MaxParallelDownloads = source.MaxParallelDownloads,
            ParallelDownloadsSafetyLimit = source.ParallelDownloadsSafetyLimit,
            AutomaticUpdateChecks = source.AutomaticUpdateChecks,
            ShowInformationNotifications = source.ShowInformationNotifications,
            ShowWarningNotifications = source.ShowWarningNotifications,
            ShowErrorNotifications = source.ShowErrorNotifications,
            IncludePrereleaseUpdates = source.IncludePrereleaseUpdates,
            MinimumFreeSpaceGiB = source.MinimumFreeSpaceGiB,
            PluginMaximumProductionWorkers = source.PluginMaximumProductionWorkers,
            PluginWorkerIdleMinutes = source.PluginWorkerIdleMinutes,
            PluginSourceHeapMiB = source.PluginSourceHeapMiB,
            PluginFilterHeapMiB = source.PluginFilterHeapMiB,
            BooruCatalogLimitMiB = source.BooruCatalogLimitMiB,
            BooruRetryAfterCooldownHours = source.BooruRetryAfterCooldownHours,
            ArchiveTagFilterTagsPerList = source.ArchiveTagFilterTagsPerList,
            UrlTextExportLimitMiB = source.UrlTextExportLimitMiB,
            SlashTagSuggestionCount = source.SlashTagSuggestionCount,
            ShrinkWindowForBarebones = source.ShrinkWindowForBarebones,
            PrivateTagLimit = source.PrivateTagLimit,
            PrivateTagFileLimitMiB = source.PrivateTagFileLimitMiB,
            MediaIndexCacheLimitMiB = source.MediaIndexCacheLimitMiB,
            FavoriteEntryLimit = source.FavoriteEntryLimit,
            FavoriteFileLimitMiB = source.FavoriteFileLimitMiB,
            PostBookmarkLimit = source.PostBookmarkLimit,
            SavedSearchLimit = source.SavedSearchLimit,
            ReaderGroupLimit = source.ReaderGroupLimit,
            SourcesWatchPreviewRows = source.SourcesWatchPreviewRows,
            ExportConfirmFiles = source.ExportConfirmFiles,
            ExportConfirmGiB = source.ExportConfirmGiB,
            PosterCacheLimitMiB = source.PosterCacheLimitMiB,
            VideoScrubCacheLimitMiB = source.VideoScrubCacheLimitMiB,
            PosterCacheFileLimit = source.PosterCacheFileLimit,
            AudioProbeCacheLimitMiB = source.AudioProbeCacheLimitMiB,
            AudioProbeCacheEntries = source.AudioProbeCacheEntries,
            ThumbnailCacheTiles = source.ThumbnailCacheTiles,
            ClipboardUrlLimit = source.ClipboardUrlLimit,
            ClipboardTextLimitKiB = source.ClipboardTextLimitKiB,
            StatsSampleIntervalMs = source.StatsSampleIntervalMs,
            ControlsIdleHideMs = source.ControlsIdleHideMs,
            ReaderNavigationMode = source.ReaderNavigationMode,
            ReaderNavigationShowPosition = source.ReaderNavigationShowPosition,
            GalleryWheelMode = source.GalleryWheelMode,
            GalleryStatusDwellMs = source.GalleryStatusDwellMs,
            GalleryPostTagChipLimit = source.GalleryPostTagChipLimit,
            StatsHistorySamples = source.StatsHistorySamples,
            StatsBackgroundIntervalMs = source.StatsBackgroundIntervalMs,
            StatsTrayTooltip = source.StatsTrayTooltip,
            StatsTrayMenu = source.StatsTrayMenu,
            StatsPersist = source.StatsPersist,
            StatsRetentionDays = source.StatsRetentionDays,
            StatsPersistIntervalMinutes = source.StatsPersistIntervalMinutes,
            StatsMaxSources = source.StatsMaxSources,
            StatsHeaderGraph = source.StatsHeaderGraph,
            StatsHeaderIcons = source.StatsHeaderIcons,
            GalleryImageCacheMiB = source.GalleryImageCacheMiB,
            GalleryPreloadAhead = source.GalleryPreloadAhead,
            GalleryPreloadBehind = source.GalleryPreloadBehind,
            ImportMaxFilesPerBatch = source.ImportMaxFilesPerBatch,
            ImportMaxBatchGiB = source.ImportMaxBatchGiB,
            ImportMaxFileMiB = source.ImportMaxFileMiB,
            ImportMaxTotalFiles = source.ImportMaxTotalFiles,
            ImportMaxTagsPerBatch = source.ImportMaxTagsPerBatch,
            ImportMaxBatches = source.ImportMaxBatches,
            MediaIndexMaxEntries = source.MediaIndexMaxEntries,
            MediaIndexMaxThreads = source.MediaIndexMaxThreads,
            ImportCatalogLimitMiB = source.ImportCatalogLimitMiB,
            ReaderThumbnailWidth = source.ReaderThumbnailWidth,
            ReaderFoldLines = source.ReaderFoldLines,
            ReaderPreviewLines = source.ReaderPreviewLines,
            ReaderPreviewDelayMs = source.ReaderPreviewDelayMs,
            ReaderReplyStep = source.ReaderReplyStep,
            ReaderReplyLines = source.ReaderReplyLines,
            ReaderExpandReplies = source.ReaderExpandReplies,
            ReaderPageSize = source.ReaderPageSize,
            ReaderThreadingDepth = source.ReaderThreadingDepth,
            GalleryFitMaxUpscalePercent = source.GalleryFitMaxUpscalePercent,
            PostFilterRuleLimit = source.PostFilterRuleLimit,
            HiddenPostLimit = source.HiddenPostLimit,
            QuickTagSuggestionCount = source.QuickTagSuggestionCount,
            QuickTagPanelSuggestionLimit = source.QuickTagPanelSuggestionLimit,
            QuickTagRecentCount = source.QuickTagRecentCount,
            QuickTagUndoDepth = source.QuickTagUndoDepth,
            QuickTagNeighbourScan = source.QuickTagNeighbourScan,
            QuickTagFilenameTokens = source.QuickTagFilenameTokens,
            QuickTagSourceTagsPerMedium = source.QuickTagSourceTagsPerMedium,
            QuickTagMarkedMediaLimit = source.QuickTagMarkedMediaLimit,
            // wave3: the settings of R5/R6/R8, V3, V4, the taglines, PT1, PT4, PR1 and PR2/PR3, and the PT5 stamp.
            FolderSourceBackgroundHashing = source.FolderSourceBackgroundHashing,
            FolderSourceCatalogLimitMiB = source.FolderSourceCatalogLimitMiB,
            FolderSourceGalleryHashWindow = source.FolderSourceGalleryHashWindow,
            FolderSourceHashMaxFileGiB = source.FolderSourceHashMaxFileGiB,
            FolderSourceHashParallelism = source.FolderSourceHashParallelism,
            FolderSourceMaxFiles = source.FolderSourceMaxFiles,
            FolderSourceMaxRoots = source.FolderSourceMaxRoots,
            FolderSourceRescanMinutes = source.FolderSourceRescanMinutes,
            GalleryFilmstrip = source.GalleryFilmstrip,
            GalleryLayout = source.GalleryLayout, // GL4: the layout choice of both modes travels with the profile settings
            GalleryFilmstripHeight = source.GalleryFilmstripHeight,
            GalleryFilmstripTileGap = source.GalleryFilmstripTileGap,
            GalleryFilmstripFlagFavorite = source.GalleryFilmstripFlagFavorite,
            GalleryFilmstripFlagRating = source.GalleryFilmstripFlagRating,
            GalleryFilmstripFlagUntagged = source.GalleryFilmstripFlagUntagged,
            GalleryFilmstripPreload = source.GalleryFilmstripPreload,
            GalleryVideoSeekStepSeconds = source.GalleryVideoSeekStepSeconds,
            MediaBadgeCollapseBelow = source.MediaBadgeCollapseBelow,
            MediaBadgeHideBelow = source.MediaBadgeHideBelow,
            MediaBadgeMaxPerCorner = source.MediaBadgeMaxPerCorner,
            HeaderTagline = source.HeaderTagline,
            HeaderTaglineFixedId = source.HeaderTaglineFixedId,
            ProfileRegistryLimit = source.ProfileRegistryLimit,
            ReaderFocusedMediaEnlarge = source.ReaderFocusedMediaEnlarge,
            ReaderFocusedMediaScalePercent = source.ReaderFocusedMediaScalePercent,
            ReaderHoverEnlarge = source.ReaderHoverEnlarge,
            ReaderHoverEnlargeDelayMs = source.ReaderHoverEnlargeDelayMs,
            ReaderHoverEnlargeSize = source.ReaderHoverEnlargeSize,
            ReaderMediaSidebar = source.ReaderMediaSidebar,
            ReaderMediaSidebarMinimumWidth = source.ReaderMediaSidebarMinimumWidth,
            ReaderMediaSidebarScope = source.ReaderMediaSidebarScope,
            ReaderMediaSidebarSide = source.ReaderMediaSidebarSide,
            ReaderMediaSidebarWidth = source.ReaderMediaSidebarWidth,
            ReaderTextlessLargeImages = source.ReaderTextlessLargeImages,
            ReaderTextlessMediaWidth = source.ReaderTextlessMediaWidth,
            ReadingHistoryLimit = source.ReadingHistoryLimit,
            ReadingHistoryMenuEntries = source.ReadingHistoryMenuEntries,
            TagQueueConfirmAbove = source.TagQueueConfirmAbove,
            MediaTagListTop = source.MediaTagListTop,
            BooruResultTagsShown = source.BooruResultTagsShown,
            MediaPanelMinimumPageWidth = source.MediaPanelMinimumPageWidth,
            TagQueueFileLimitMiB = source.TagQueueFileLimitMiB,
            TagQueueHistory = source.TagQueueHistory,
            TagQueueGroupLimit = source.TagQueueGroupLimit,
            TagQueueGroupNameLimit = source.TagQueueGroupNameLimit,
            TagQueueLimit = source.TagQueueLimit,
            TagStamp = (string[])source.TagStamp.Clone(),
            GalleryMaxZoomPercent = source.GalleryMaxZoomPercent,
            TaggingCardCount = source.TaggingCardCount,
            TaggingCardsDefault = source.TaggingCardsDefault,
            TagQueueWalkWindow = source.TagQueueWalkWindow,
            TagQueueUndoDepth = source.TagQueueUndoDepth,
            ReaderInlineVideo = source.ReaderInlineVideo,
            ReaderMediaClick = source.ReaderMediaClick,
            ReaderInlineVideoAutoplayMuted = source.ReaderInlineVideoAutoplayMuted,
            ReaderKeepImagesExpanded = source.ReaderKeepImagesExpanded,
            ReaderSidebarVideoAutoplay = source.ReaderSidebarVideoAutoplay,
            ReaderSidebarVideoLoop = source.ReaderSidebarVideoLoop,
            ReaderSidebarVideoStartMuted = source.ReaderSidebarVideoStartMuted,
            ReaderQuotePreviewAutoplay = source.ReaderQuotePreviewAutoplay,
            ReaderQuotePreviewMuted = source.ReaderQuotePreviewMuted,
            ReaderInlineVideoMaxPlayers = source.ReaderInlineVideoMaxPlayers,
            ReaderInlineVideoMaxFileMiB = source.ReaderInlineVideoMaxFileMiB,
            ReaderMediaStripLimit = source.ReaderMediaStripLimit,
            ReaderPreviewPrefetchPosts = source.ReaderPreviewPrefetchPosts,
            ReaderPreviewSizeMemory = source.ReaderPreviewSizeMemory,
            ReaderMediaGrid = source.ReaderMediaGrid,
            ReaderMediaGridTextLimit = source.ReaderMediaGridTextLimit,
            TagStampSize = source.TagStampSize,
            TaggingCards = (string[])source.TaggingCards.Clone(),
            ReaderDateStyle = source.ReaderDateStyle,
            ReaderSpoilers = source.ReaderSpoilers,
            MediaSort = source.MediaSort,
            MediaSortDirection = source.MediaSortDirection,
            MediaBooruMode = source.MediaBooruMode,
            MediaTagListSort = source.MediaTagListSort,
            MediaLayout = source.MediaLayout,
            ArchiveMediaTileSize = source.ArchiveMediaTileSize,
            TaggingPageView = source.TaggingPageView,
            TaggingPreviewColumn = source.TaggingPreviewColumn,
            TaggingStampBlock = source.TaggingStampBlock,
            GalleryFitUpscaleLimited = source.GalleryFitUpscaleLimited,
            PluginEditorExecutable = source.PluginEditorExecutable,
            Theme = source.Theme,
            ThemeOverrides = source.ThemeOverrides, // immutable: sharing the instance is a detached value copy
            Animations = source.Animations,
            Language = source.Language,
            WorkspaceMode = source.WorkspaceMode,
            WorkViewSwitchNoticeSeconds = source.WorkViewSwitchNoticeSeconds,
            ShowBooruTools = source.ShowBooruTools,
            FooterPages = (string[])source.FooterPages.Clone(), // F1: the footer layout of the profile
            ReaderPostWidth = source.ReaderPostWidth,
            VideoAutoplay = source.VideoAutoplay,
            VideoStartMuted = source.VideoStartMuted,
            VideoVolume = source.VideoVolume,
            GalleryPlaybackMode = source.GalleryPlaybackMode,
            GallerySlideshowSeconds = source.GallerySlideshowSeconds,
            GalleryVideoRepeat = source.GalleryVideoRepeat,
            GallerySlideshowVideoRule = source.GallerySlideshowVideoRule,
            GallerySlideshowVideoSeconds = source.GallerySlideshowVideoSeconds,
            GallerySlideshowVideoRepeats = source.GallerySlideshowVideoRepeats,
            GalleryPauseOnFocusLoss = source.GalleryPauseOnFocusLoss,
            GalleryMediaFocus = source.GalleryMediaFocus,
            GalleryOverlayControls = source.GalleryOverlayControls,
            GalleryAutoHideControls = source.GalleryAutoHideControls,
            GalleryOverlayPostContext = source.GalleryOverlayPostContext,
            ShowShortcutHelp = source.ShowShortcutHelp,
            MediaFocusHidesHeader = source.MediaFocusHidesHeader,
            ShortcutBindings = source.ShortcutBindings.Clone(),
            PluginEditorArguments = (string[])source.PluginEditorArguments.Clone(),
        };

        // Use the dictionary getter, not GetLayoutProfile/legacy aliases or the normalizing setter.
        // Clone every profile and its column dictionaries without validating, dropping unknown entries,
        // or coupling legacy values. Only the detached snapshot may materialize its active profile when
        // the existing serializer reads compatibility getters (and it may fail without touching source).
        foreach ((string name, PresentationProfile profile) in source.LayoutProfiles)
            snapshot.LayoutProfiles.Add(name, profile.Clone());
        return snapshot;
    }

    private static string ProductVersion()
    {
        Assembly assembly = typeof(SettingsTransfer).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
            return NormalizeProductVersion(informational);

        Version? assemblyVersion = assembly.GetName().Version;
        return assemblyVersion == null
            ? "0.0.0"
            : string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{Math.Max(assemblyVersion.Build, 0)}");
    }

    private static string NormalizeProductVersion(string productVersion)
    {
        ArgumentNullException.ThrowIfNull(productVersion);
        string version = productVersion.Split('+', 2)[0].Trim();
        if (version.Length is 0 or > 64 || version.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')))
            throw new ArgumentException("A non-empty product version without control characters is required.", nameof(productVersion));
        return version;
    }
}

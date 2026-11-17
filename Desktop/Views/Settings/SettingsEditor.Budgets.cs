namespace MeowChan;

// The "Storage and limits" numbers (budgets, caches, imports, media index, folder sources) are loaded from one place for the four moments that
// show a Config in the draft: the start of the page, a discard, a save, and the section/all reset (which passes the defaults). Before this the
// four lists were copies of each other, and the folder-source limits were missing from the reset.
public partial class SettingsEditor
{
    /// <summary>
    /// Writes the budget, limit and folder-source values of <paramref name="config"/> into the draft and into the raw text of their inputs.
    /// Only values: it never touches a baseline (what counts as unchanged), so the reset can use it for the defaults.
    /// </summary>
    private void LoadBudgetsDraft(Config config)
    {
        MinimumFreeSpaceGiB = config.MinimumFreeSpaceGiB;
        ProductionWorkers = config.PluginMaximumProductionWorkers;
        WorkerIdleMinutes = config.PluginWorkerIdleMinutes;
        SourceHeapMiB = config.PluginSourceHeapMiB;
        FilterHeapMiB = config.PluginFilterHeapMiB;
        BooruCatalogMiB = config.BooruCatalogLimitMiB;
        BooruRetryAfterCooldownHours = config.BooruRetryAfterCooldownHours;
        PrivateTagLimit = config.PrivateTagLimit;
        PrivateTagFileMiB = config.PrivateTagFileLimitMiB;
        MediaIndexCacheMiB = config.MediaIndexCacheLimitMiB;
        FavoriteEntryLimit = config.FavoriteEntryLimit;
        FavoriteFileLimitMiB = config.FavoriteFileLimitMiB;
        PostBookmarkLimit = config.PostBookmarkLimit;
        SavedSearchLimit = config.SavedSearchLimit;
        ReaderGroupLimit = config.ReaderGroupLimit;
        ProfileRegistryLimit = config.ProfileRegistryLimit;
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
        FolderSourceMaxFiles = config.FolderSourceMaxFiles;
        FolderSourceMaxRoots = config.FolderSourceMaxRoots;
        FolderSourceHashParallelism = config.FolderSourceHashParallelism;
        FolderSourceHashMaxFileGiB = config.FolderSourceHashMaxFileGiB;
        FolderSourceRescanMinutes = config.FolderSourceRescanMinutes;
        FolderSourceCatalogLimitMiB = config.FolderSourceCatalogLimitMiB;
        FolderSourceGalleryHashWindow = config.FolderSourceGalleryHashWindow;
        FolderSourceBackgroundHashing = config.FolderSourceBackgroundHashing;
        MediaIndexMaxEntries = config.MediaIndexMaxEntries;
        MediaIndexMaxThreads = config.MediaIndexMaxThreads;
        ImportCatalogLimitMiB = config.ImportCatalogLimitMiB;

        ResetNumber(ReserveInput, config.MinimumFreeSpaceGiB);
        ResetNumber(ProductionWorkersInput, config.PluginMaximumProductionWorkers);
        ResetNumber(WorkerIdleMinutesInput, config.PluginWorkerIdleMinutes);
        ResetNumber(SourceHeapMiBInput, config.PluginSourceHeapMiB);
        ResetNumber(FilterHeapMiBInput, config.PluginFilterHeapMiB);
        ResetNumber(BooruCatalogMiBInput, config.BooruCatalogLimitMiB);
        ResetNumber(BooruRetryAfterCooldownHoursInput, config.BooruRetryAfterCooldownHours);
        ResetNumber(PrivateTagLimitInput, config.PrivateTagLimit);
        ResetNumber(PrivateTagFileMiBInput, config.PrivateTagFileLimitMiB);
        ResetNumber(MediaIndexCacheMiBInput, config.MediaIndexCacheLimitMiB);
        ResetNumber(FavoriteEntryLimitInput, config.FavoriteEntryLimit);
        ResetNumber(FavoriteFileLimitMiBInput, config.FavoriteFileLimitMiB);
        ResetNumber(PostBookmarkLimitInput, config.PostBookmarkLimit);
        ResetNumber(SavedSearchLimitInput, config.SavedSearchLimit);
        ResetNumber(ReaderGroupLimitInput, config.ReaderGroupLimit);
        ResetNumber(ProfileRegistryLimitInput, config.ProfileRegistryLimit);
        ResetNumber(SourcesWatchPreviewRowsInput, config.SourcesWatchPreviewRows);
        ResetNumber(ExportConfirmFilesInput, config.ExportConfirmFiles);
        ResetNumber(ExportConfirmGiBInput, config.ExportConfirmGiB);
        ResetNumber(PosterCacheLimitMiBInput, config.PosterCacheLimitMiB);
        ResetNumber(PosterCacheFileLimitInput, config.PosterCacheFileLimit);
        ResetNumber(AudioProbeCacheLimitMiBInput, config.AudioProbeCacheLimitMiB);
        ResetNumber(AudioProbeCacheEntriesInput, config.AudioProbeCacheEntries);
        ResetNumber(ThumbnailCacheTilesInput, config.ThumbnailCacheTiles);
        ResetNumber(ClipboardUrlLimitInput, config.ClipboardUrlLimit);
        ResetNumber(ClipboardTextLimitKiBInput, config.ClipboardTextLimitKiB);
        ResetNumber(GalleryImageCacheMiBInput, config.GalleryImageCacheMiB);
        ResetNumber(GalleryPreloadAheadInput, config.GalleryPreloadAhead);
        ResetNumber(GalleryPreloadBehindInput, config.GalleryPreloadBehind);
        ResetNumber(ImportMaxFilesPerBatchInput, config.ImportMaxFilesPerBatch);
        ResetNumber(ImportMaxBatchGiBInput, config.ImportMaxBatchGiB);
        ResetNumber(ImportMaxFileMiBInput, config.ImportMaxFileMiB);
        ResetNumber(ImportMaxTotalFilesInput, config.ImportMaxTotalFiles);
        ResetNumber(ImportMaxTagsPerBatchInput, config.ImportMaxTagsPerBatch);
        ResetNumber(ImportMaxBatchesInput, config.ImportMaxBatches);
        ResetNumber(FolderSourceMaxFilesInput, config.FolderSourceMaxFiles);
        ResetNumber(FolderSourceMaxRootsInput, config.FolderSourceMaxRoots);
        ResetNumber(FolderSourceHashParallelismInput, config.FolderSourceHashParallelism);
        ResetNumber(FolderSourceHashMaxFileGiBInput, config.FolderSourceHashMaxFileGiB);
        ResetNumber(FolderSourceRescanMinutesInput, config.FolderSourceRescanMinutes);
        ResetNumber(FolderSourceCatalogLimitMiBInput, config.FolderSourceCatalogLimitMiB);
        ResetNumber(FolderSourceGalleryHashWindowInput, config.FolderSourceGalleryHashWindow);
        ResetNumber(MediaIndexMaxEntriesInput, config.MediaIndexMaxEntries);
        ResetNumber(MediaIndexMaxThreadsInput, config.MediaIndexMaxThreads);
        ResetNumber(ImportCatalogLimitMiBInput, config.ImportCatalogLimitMiB);
    }
}

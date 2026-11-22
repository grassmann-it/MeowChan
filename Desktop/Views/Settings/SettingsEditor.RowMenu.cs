using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

/// <summary>
/// The context menu of a row of the settings page (right-click on the row, Menu key or Shift+F10 inside it): reset the row's setting to its
/// default and copy the setting's id. The row is found the way the settings search finds rows, so both agree on what a setting is. A reset writes
/// the default into the draft like typing it would; nothing is saved until Save.
/// </summary>
public partial class SettingsEditor
{
    private ContextMenuHost? _rowMenu;
    private ContextMenuTarget? _rowTarget;
    private Dictionary<string, DefaultSetting>? _defaultSettings;

    /// <summary>One setting that can be reset: its input's name, whether the draft already holds the default, and how to write it.</summary>
    private sealed record DefaultSetting(string Input, Func<Config, bool> IsAtDefault, Action<Config> Apply);

    private void InitializeRowMenu()
    {
        _rowTarget = new ContextMenuTarget(SettingsScrollViewer);
        ContextMenuHost host = _rowMenu = new ContextMenuHost(SettingsScrollViewer, ComposeRowMenu, fallbackFocus: () => FocusSettingsContent());
        // A number field and a combo box keep the key for their own menu elsewhere; on this page the row menu is the more useful one, so it
        // opens from them too. A free-text field (the download folder) keeps its text menu.
        SettingsScrollViewer.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Handled || _isClosed || !IsContextGesture(e) || e.Source is not Visual source) return;
            if (source.GetSelfAndVisualAncestors().Any(node => node is NumericUpDown or ComboBox) && !source.GetSelfAndVisualAncestors().Any(node => node is ComboBox { IsDropDownOpen: true }))
            { e.Handled = true; host.Show(atPointer: false); }
        }, RoutingStrategies.Tunnel);
    }

    private static bool IsContextGesture(KeyEventArgs e) =>
        e.Key == Key.Apps && e.KeyModifiers == KeyModifiers.None || e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift
        || CommandBindings.Matches(e, "menu.context-open");

    /// <summary>The row menu host; read by tests and smokes.</summary>
    internal ContextMenuHost? RowMenuHost => _rowMenu;

    private ContextMenuBuilder? ComposeRowMenu() => _isClosed || _rowTarget?.Source is not Visual target ? null : ComposeRowMenu(target);

    /// <summary>The menu of the row that holds <paramref name="target"/> (null outside a row); also the test seam.</summary>
    internal ContextMenuBuilder? ComposeRowMenu(Visual target)
    {
        if (_isClosed || FindSettingRow(target) is not { } row) return null;
        Config defaults = new();
        DefaultSetting? setting = row.Element.GetSelfAndLogicalDescendants().OfType<Control>()
            .Where(IsSettingInput).Select(input => input.Name).Where(name => name != null)
            .Select(name => DefaultSettings().GetValueOrDefault(name!)).FirstOrDefault(found => found != null);
        SettingsRowMenu.ResetState state = setting == null ? SettingsRowMenu.ResetState.NoDefault
            : setting.IsAtDefault(defaults) ? SettingsRowMenu.ResetState.AtDefault : SettingsRowMenu.ResetState.Resettable;
        return SettingsRowMenu.Compose(row.Label, row.Key, state,
            reset: () => { if (setting != null && !_isClosed && !IsBusy) { setting.Apply(new Config()); RetireSavedStatus(); } },
            copyId: () => _ = CopySettingIdAsync(row.Key ?? string.Empty));
    }

    private async System.Threading.Tasks.Task CopySettingIdAsync(string id)
    {
        if (await MenuClipboard.CopyAsync(this, id)) return;
        if (!_isClosed) SetErrorMessage("ctxmenu.copyFailed");
    }

    /// <summary>The innermost row of the settings search that holds the control: its element, label and id (the name of its input).</summary>
    internal (Control Element, string Label, string? Key)? FindSettingRow(Visual target)
    {
        Dictionary<Control, bool> memo = new();
        _inputMemo = memo;
        List<SearchEntryBuilder> entries;
        try { entries = ScanSearchEntries(); }
        finally { _inputMemo = null; }
        SearchEntryBuilder? best = null;
        int bestDepth = -1;
        foreach (SearchEntryBuilder entry in entries)
        {
            // A heading or a group title is not a setting; a row has an input and so a key (or at least an input to reset).
            if (entry.Element is TextBlock || entry.Element is Expander) continue;
            if (!target.GetSelfAndVisualAncestors().Contains(entry.Element)) continue;
            int depth = entry.Element.GetVisualAncestors().Count();
            if (depth > bestDepth) { best = entry; bestDepth = depth; }
        }
        return best == null ? null : (best.Element, best.Label, best.Key);
    }

    private Dictionary<string, DefaultSetting> DefaultSettings() => _defaultSettings ??= new DefaultSetting[]
    {
            Number(RecheckTimerInput, () => RecheckTimer, value => RecheckTimer = value, config => config.RecheckTimer),
            Number(ParallelDownloadsInput, () => MaxParallelDownloads, value => MaxParallelDownloads = value, config => config.MaxParallelDownloads),
            Number(ParallelDownloadsSafetyInput, () => ParallelDownloadsSafetyLimit, value => ParallelDownloadsSafetyLimit = value, config => config.ParallelDownloadsSafetyLimit),
            Number(BandwidthInput, () => BandwidthLimiterMB, value => BandwidthLimiterMB = value, config => config.BandwidthLimiter / (1024m * 1024m)),
            Number(ReserveInput, () => MinimumFreeSpaceGiB, value => MinimumFreeSpaceGiB = value, config => config.MinimumFreeSpaceGiB),
            Number(ProductionWorkersInput, () => ProductionWorkers, value => ProductionWorkers = value, config => config.PluginMaximumProductionWorkers),
            Number(WorkerIdleMinutesInput, () => WorkerIdleMinutes, value => WorkerIdleMinutes = value, config => config.PluginWorkerIdleMinutes),
            Number(SourceHeapMiBInput, () => SourceHeapMiB, value => SourceHeapMiB = value, config => config.PluginSourceHeapMiB),
            Number(FilterHeapMiBInput, () => FilterHeapMiB, value => FilterHeapMiB = value, config => config.PluginFilterHeapMiB),
            Number(BooruCatalogMiBInput, () => BooruCatalogMiB, value => BooruCatalogMiB = value, config => config.BooruCatalogLimitMiB),
            Number(BooruSearchTabsInput, () => BooruSearchTabs, value => BooruSearchTabs = value, config => config.BooruSearchTabs),
            Number(BooruGallerySampleMiBInput, () => BooruGallerySampleMiB, value => BooruGallerySampleMiB = value, config => config.BooruGallerySampleMiB),
            Number(BooruGalleryCacheMiBInput, () => BooruGalleryCacheMiB, value => BooruGalleryCacheMiB = value, config => config.BooruGalleryCacheMiB),
            Number(BooruGalleryMaximumPixelsInput, () => BooruGalleryMaximumPixels, value => BooruGalleryMaximumPixels = value, config => config.BooruGalleryMaximumPixels),
            Number(BooruGalleryMaximumWidthInput, () => BooruGalleryMaximumWidth, value => BooruGalleryMaximumWidth = value, config => config.BooruGalleryMaximumWidth),
            Number(BooruGalleryTimeoutSecondsInput, () => BooruGalleryTimeoutSeconds, value => BooruGalleryTimeoutSeconds = value, config => config.BooruGalleryTimeoutSeconds),
            Number(PrivateTagLimitInput, () => PrivateTagLimit, value => PrivateTagLimit = value, config => config.PrivateTagLimit),
            Number(PrivateTagFileMiBInput, () => PrivateTagFileMiB, value => PrivateTagFileMiB = value, config => config.PrivateTagFileLimitMiB),
            Number(MediaIndexCacheMiBInput, () => MediaIndexCacheMiB, value => MediaIndexCacheMiB = value, config => config.MediaIndexCacheLimitMiB),
            Number(FavoriteEntryLimitInput, () => FavoriteEntryLimit, value => FavoriteEntryLimit = value, config => config.FavoriteEntryLimit),
            Number(FavoriteFileLimitMiBInput, () => FavoriteFileLimitMiB, value => FavoriteFileLimitMiB = value, config => config.FavoriteFileLimitMiB),
            Number(PostBookmarkLimitInput, () => PostBookmarkLimit, value => PostBookmarkLimit = value, config => config.PostBookmarkLimit),
            Number(SavedSearchLimitInput, () => SavedSearchLimit, value => SavedSearchLimit = value, config => config.SavedSearchLimit),
            Number(ReaderGroupLimitInput, () => ReaderGroupLimit, value => ReaderGroupLimit = value, config => config.ReaderGroupLimit),
            Number(SourcesWatchPreviewRowsInput, () => SourcesWatchPreviewRows, value => SourcesWatchPreviewRows = value, config => config.SourcesWatchPreviewRows),
            Number(ExportConfirmFilesInput, () => ExportConfirmFiles, value => ExportConfirmFiles = value, config => config.ExportConfirmFiles),
            Number(ExportConfirmGiBInput, () => ExportConfirmGiB, value => ExportConfirmGiB = value, config => config.ExportConfirmGiB),
            Number(PosterCacheLimitMiBInput, () => PosterCacheLimitMiB, value => PosterCacheLimitMiB = value, config => config.PosterCacheLimitMiB),
            Number(PosterCacheFileLimitInput, () => PosterCacheFileLimit, value => PosterCacheFileLimit = value, config => config.PosterCacheFileLimit),
            Number(AudioProbeCacheLimitMiBInput, () => AudioProbeCacheLimitMiB, value => AudioProbeCacheLimitMiB = value, config => config.AudioProbeCacheLimitMiB),
            Number(AudioProbeCacheEntriesInput, () => AudioProbeCacheEntries, value => AudioProbeCacheEntries = value, config => config.AudioProbeCacheEntries),
            Number(ThumbnailCacheTilesInput, () => ThumbnailCacheTiles, value => ThumbnailCacheTiles = value, config => config.ThumbnailCacheTiles),
            Number(ClipboardUrlLimitInput, () => ClipboardUrlLimit, value => ClipboardUrlLimit = value, config => config.ClipboardUrlLimit),
            Number(ClipboardTextLimitKiBInput, () => ClipboardTextLimitKiB, value => ClipboardTextLimitKiB = value, config => config.ClipboardTextLimitKiB),
            Number(GalleryImageCacheMiBInput, () => GalleryImageCacheMiB, value => GalleryImageCacheMiB = value, config => config.GalleryImageCacheMiB),
            Number(HeaderTaglineIntervalMinutesInput, () => HeaderTaglineIntervalMinutes, value => HeaderTaglineIntervalMinutes = value, config => config.HeaderTaglineIntervalMinutes),
            Number(GalleryPreloadAheadInput, () => GalleryPreloadAhead, value => GalleryPreloadAhead = value, config => config.GalleryPreloadAhead),
            Number(GalleryPreloadBehindInput, () => GalleryPreloadBehind, value => GalleryPreloadBehind = value, config => config.GalleryPreloadBehind),
            Number(ImportMaxFilesPerBatchInput, () => ImportMaxFilesPerBatch, value => ImportMaxFilesPerBatch = value, config => config.ImportMaxFilesPerBatch),
            Number(ImportMaxBatchGiBInput, () => ImportMaxBatchGiB, value => ImportMaxBatchGiB = value, config => config.ImportMaxBatchGiB),
            Number(ImportMaxFileMiBInput, () => ImportMaxFileMiB, value => ImportMaxFileMiB = value, config => config.ImportMaxFileMiB),
            Number(ImportMaxTotalFilesInput, () => ImportMaxTotalFiles, value => ImportMaxTotalFiles = value, config => config.ImportMaxTotalFiles),
            Number(ImportMaxTagsPerBatchInput, () => ImportMaxTagsPerBatch, value => ImportMaxTagsPerBatch = value, config => config.ImportMaxTagsPerBatch),
            Number(ImportMaxBatchesInput, () => ImportMaxBatches, value => ImportMaxBatches = value, config => config.ImportMaxBatches),
            Number(MediaIndexMaxEntriesInput, () => MediaIndexMaxEntries, value => MediaIndexMaxEntries = value, config => config.MediaIndexMaxEntries),
            Number(MediaIndexMaxThreadsInput, () => MediaIndexMaxThreads, value => MediaIndexMaxThreads = value, config => config.MediaIndexMaxThreads),
            Number(ImportCatalogLimitMiBInput, () => ImportCatalogLimitMiB, value => ImportCatalogLimitMiB = value, config => config.ImportCatalogLimitMiB),
            Number(ShutdownTimeLimitSecondsInput, () => ShutdownTimeLimitSeconds, value => ShutdownTimeLimitSeconds = value, config => config.ShutdownTimeLimitSeconds),
            Number(GallerySlideshowSecondsInput, () => GallerySlideshowSeconds, value => GallerySlideshowSeconds = value, config => config.GallerySlideshowSeconds),
            Flag(RestoreSessionInput, () => SaveUrlsOnExit, value => SaveUrlsOnExit = value, config => config.SaveUrlsOnExit),
            Flag(BoardAdmissionWizardInput, () => ShowBoardAdmissionWizard, value => ShowBoardAdmissionWizard = value, config => config.ShowBoardAdmissionWizard),
            Flag(MinimizeToTrayInput, () => MinimizeToTray, value => MinimizeToTray = value, config => config.MinimizeToTray),
            Flag(CloseToTrayInput, () => CloseToTray, value => CloseToTray = value, config => config.CloseToTray),
            Flag(CompactDensityInput, () => CompactDensity, value => CompactDensity = value, config => config.CompactDensity),
            Flag(RememberReadingPositionInput, () => RememberReadingPosition, value => RememberReadingPosition = value, config => config.RememberReadingPosition),
            Flag(MonitorClipboardUrlsInput, () => MonitorClipboardUrls, value => MonitorClipboardUrls = value, config => config.MonitorClipboardUrls),
            Flag(AutomaticUpdatesInput, () => AutomaticUpdateChecks, value => AutomaticUpdateChecks = value, config => config.AutomaticUpdateChecks),
            Flag(PrereleaseUpdatesInput, () => IncludePrereleaseUpdates, value => IncludePrereleaseUpdates = value, config => config.IncludePrereleaseUpdates),
            Flag(InformationNotificationsInput, () => ShowInformationNotifications, value => ShowInformationNotifications = value, config => config.ShowInformationNotifications),
            Flag(WarningNotificationsInput, () => ShowWarningNotifications, value => ShowWarningNotifications = value, config => config.ShowWarningNotifications),
            Flag(ErrorNotificationsInput, () => ShowErrorNotifications, value => ShowErrorNotifications = value, config => config.ShowErrorNotifications),
            Flag(ShowShortcutHelpInput, () => ShowShortcutHelp, value => ShowShortcutHelp = value, config => config.ShowShortcutHelp),
            Flag(MediaFocusHidesHeaderInput, () => MediaFocusHidesHeader, value => MediaFocusHidesHeader = value, config => config.MediaFocusHidesHeader),
            Flag(ReaderExpandRepliesInput, () => ReaderExpandReplies, value => ReaderExpandReplies = value, config => config.ReaderExpandReplies),
            Flag(ReaderNavigationShowPositionInput, () => ReaderNavigationShowPosition, value => ReaderNavigationShowPosition = value, config => config.ReaderNavigationShowPosition),
            Flag(GalleryFilmstripFlagFavoriteInput, () => GalleryFilmstripFlagFavorite, value => GalleryFilmstripFlagFavorite = value, config => config.GalleryFilmstripFlagFavorite),
            Flag(GalleryFilmstripFlagRatingInput, () => GalleryFilmstripFlagRating, value => GalleryFilmstripFlagRating = value, config => config.GalleryFilmstripFlagRating),
            Choice(GalleryFilmstripFlagUntaggedInput, () => SelectedGalleryFilmstripFlagUntagged?.Value, value => SelectedGalleryFilmstripFlagUntagged = GalleryFilmstripFlagUntaggedChoices.FirstOrDefault(item => item.Value == value) ?? SelectedGalleryFilmstripFlagUntagged, config => config.GalleryFilmstripFlagUntagged),
            Choice(GalleryWheelModeInput, () => SelectedGalleryWheelMode?.Value, value => SelectedGalleryWheelMode = GalleryWheelModeChoices.FirstOrDefault(item => item.Value == value) ?? SelectedGalleryWheelMode, config => config.GalleryWheelMode),
            Choice(ReaderNavigationModeInput, () => SelectedReaderNavigationMode?.Value, value => SelectedReaderNavigationMode = ReaderNavigationModeChoices.FirstOrDefault(item => item.Value == value) ?? SelectedReaderNavigationMode, config => config.ReaderNavigationMode),
            Choice(ThemeInput, () => SelectedTheme?.Value, value => SelectedTheme = ThemeChoices.FirstOrDefault(item => item.Value == value) ?? SelectedTheme, config => config.Theme),
            Choice(AnimationsInput, () => SelectedAnimations?.Value, value => SelectedAnimations = AnimationsChoices.FirstOrDefault(item => item.Value == value) ?? SelectedAnimations, config => config.Animations),
            Choice(WorkspaceModeInput, () => SelectedWorkspaceMode?.Value, value => SelectedWorkspaceMode = WorkspaceChoices.FirstOrDefault(item => item.Value == value) ?? SelectedWorkspaceMode, config => config.WorkspaceMode),
            Choice(ReadingFontSizeInput, () => SelectedReadingFontSize?.Value, value => SelectedReadingFontSize = ReadingFontSizeChoices.FirstOrDefault(item => item.Value == value) ?? SelectedReadingFontSize, config => config.ReadingFontSize),
            Choice(ReaderPostWidthInput, () => SelectedReaderPostWidth?.Value, value => SelectedReaderPostWidth = ReaderPostWidthChoices.FirstOrDefault(item => item.Value == value) ?? SelectedReaderPostWidth, config => config.ReaderPostWidth),
            Choice(StatsHeaderGraphInput, () => SelectedStatsHeaderGraph?.Value, value => SelectedStatsHeaderGraph = StatsHeaderGraphChoices.FirstOrDefault(item => item.Value == value) ?? SelectedStatsHeaderGraph, config => config.StatsHeaderGraph),
            Text(DownloadPathInput, () => DownloadPath, value => DownloadPath = value, config => config.DownloadPath),
            Text(ExportFilenameTemplateInput, () => ExportFilenameTemplate, value => ExportFilenameTemplate = value, config => config.ExportFilenameTemplate),
    }.ToDictionary(setting => setting.Input, StringComparer.Ordinal);

    /// <summary>The names of the inputs that can be reset (tests check that each one is an input of this page).</summary>
    internal IReadOnlyCollection<string> ResettableInputNames => DefaultSettings().Keys;

    private static DefaultSetting Number(Control input, Func<decimal?> current, Action<decimal?> set, Func<Config, decimal> defaultOf) =>
        new(input.Name!, config => current() == defaultOf(config), config =>
        {
            decimal value = defaultOf(config);
            set(value);
            if (input is NumericUpDown number) ResetNumber(number, value);
        });

    private static DefaultSetting Flag(Control input, Func<bool> current, Action<bool> set, Func<Config, bool> defaultOf) =>
        new(input.Name!, config => current() == defaultOf(config), config => set(defaultOf(config)));

    private static DefaultSetting Choice(Control input, Func<string?> current, Action<string> set, Func<Config, string> defaultOf) =>
        new(input.Name!, config => string.Equals(current(), defaultOf(config), StringComparison.Ordinal), config => set(defaultOf(config)));

    private static DefaultSetting Text(Control input, Func<string?> current, Action<string> set, Func<Config, string> defaultOf) =>
        new(input.Name!, config => string.Equals(current(), defaultOf(config), StringComparison.Ordinal), config => set(defaultOf(config)));
}

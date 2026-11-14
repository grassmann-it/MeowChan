using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

/// <summary>
/// Draft-only reset actions for the settings page. Every assignment is explicit:
/// the reset surface never copies hidden runtime state, consent, packs or shortcut
/// bindings, and it never writes or applies a Config.
/// </summary>
public partial class SettingsEditor
{
    internal enum SettingsResetScope
    {
        Interface,
        ReadingMedia,
        Downloads,
        WorkerBudgets,
        Updates,
        Notifications,
        Behavior,
        All,
    }

    private SettingsDefaults? _resetDefaults;
    private Flyout? _resetConfirmation;
    private Button? _resetConfirmationOrigin;
    private bool _resetStatusVisible;

    private SettingsDefaults ResetDefaults => _resetDefaults ??= new SettingsDefaults();

    public string ResetLabel => Texts.T("settings.reset");
    public string ResetAllLabel => Texts.T("settings.reset.all");
    public string ResetInterfaceLabel => Texts.T("settings.reset.section", InterfaceTitle);
    public string ResetReadingMediaLabel => Texts.T("settings.reset.section", ReadingMediaTitle);
    public string ResetDownloadsLabel => Texts.T("settings.reset.section", DownloadsTitle);
    public string ResetWorkerBudgetsLabel => Texts.T("settings.reset.section", StorageLimitsLabel);
    public string ResetUpdatesLabel => Texts.T("settings.reset.section", UpdatesTitle);
    public string ResetNotificationsLabel => Texts.T("settings.reset.section", NotificationsTitle);
    public string ResetBehaviorLabel => Texts.T("settings.reset.section", BehaviorTitle);

    private string ResetQuestion(SettingsResetScope scope) => scope == SettingsResetScope.All
        ? Texts.T("settings.reset.allQuestion")
        : Texts.T("settings.reset.question", ResetScopeTitle(scope));

    private string ResetScopeTitle(SettingsResetScope scope) => scope switch
    {
        SettingsResetScope.Interface => InterfaceTitle,
        SettingsResetScope.ReadingMedia => ReadingMediaTitle,
        SettingsResetScope.Downloads => DownloadsTitle,
        SettingsResetScope.WorkerBudgets => StorageLimitsLabel,
        SettingsResetScope.Updates => UpdatesTitle,
        SettingsResetScope.Notifications => NotificationsTitle,
        SettingsResetScope.Behavior => BehaviorTitle,
        _ => SettingsTitle,
    };

    private void RaiseResetTextsChanged()
    {
        if (_isClosed) return;
        foreach (string property in new[]
        {
            nameof(ResetLabel), nameof(ResetAllLabel), nameof(ResetInterfaceLabel), nameof(ResetReadingMediaLabel),
            nameof(ResetDownloadsLabel), nameof(ResetWorkerBudgetsLabel), nameof(ResetUpdatesLabel),
            nameof(ResetNotificationsLabel), nameof(ResetBehaviorLabel)
        })
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(property));
        if (_resetStatusVisible) SettingsResetStatus.Text = Texts.T("settings.reset.done");
        if (_resetConfirmation is { IsOpen: true }) OpenResetConfirmation(_resetConfirmationOrigin!, GetResetScope(_resetConfirmationOrigin!));
    }

    private void OnResetCategoryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id || !TryGetResetScope(id, out SettingsResetScope scope)) return;
        OpenResetConfirmation(button, scope);
    }

    private static bool TryGetResetScope(string id, out SettingsResetScope scope)
    {
        scope = id switch
        {
            "settings.interface" => SettingsResetScope.Interface,
            "settings.reading-media" => SettingsResetScope.ReadingMedia,
            "settings.downloads" => SettingsResetScope.Downloads,
            "settings.worker-budgets" => SettingsResetScope.WorkerBudgets,
            "settings.updates" => SettingsResetScope.Updates,
            "settings.notifications" => SettingsResetScope.Notifications,
            "settings.behavior" => SettingsResetScope.Behavior,
            _ => SettingsResetScope.All,
        };
        return id is "settings.interface" or "settings.reading-media" or "settings.downloads"
            or "settings.worker-budgets" or "settings.updates" or "settings.notifications" or "settings.behavior";
    }

    private static SettingsResetScope GetResetScope(Button button) => button.Tag is string id && TryGetResetScope(id, out SettingsResetScope scope)
        ? scope : SettingsResetScope.All;

    private void OpenResetConfirmation(Button origin, SettingsResetScope scope)
    {
        if (_isClosed || !IsEditable) return;
        _resetConfirmation?.Hide();
        _resetConfirmationOrigin = origin;

        var question = new TextBlock { Text = ResetQuestion(scope), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(question, AutomationLiveSetting.Polite);
        var confirm = new Button { Content = Texts.T("settings.reset.confirm"), HorizontalAlignment = HorizontalAlignment.Stretch };
        confirm.Classes.Add("danger");
        AutomationProperties.SetName(confirm, Texts.T("settings.reset.confirm"));
        var cancel = new Button { Content = Texts.T("settings.reset.cancel"), HorizontalAlignment = HorizontalAlignment.Stretch };
        cancel.Classes.Add("ghost");
        AutomationProperties.SetName(cancel, Texts.T("settings.reset.cancel"));
        var actions = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(confirm);
        actions.Children.Add(cancel);
        var panel = new Border
        {
            Padding = new Thickness(12),
            MaxWidth = 360,
            Child = new StackPanel { Spacing = 12, Children = { question, actions } }
        };
        AutomationProperties.SetName(panel, ResetQuestion(scope));
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight, Content = panel };
        _resetConfirmation = flyout;
        confirm.Click += (_, _) =>
        {
            ResetDraft(scope);
            flyout.Hide();
            origin.Focus();
        };
        cancel.Click += (_, _) =>
        {
            flyout.Hide();
            origin.Focus();
        };
        flyout.Opened += (_, _) => cancel.Focus();
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_resetConfirmation, flyout)) _resetConfirmation = null;
        };
        flyout.ShowAt(origin);
    }

    private void ResetDraft(SettingsResetScope scope)
    {
        if (_isClosed || IsBusy || !IsAvailable) return;
        switch (scope)
        {
            case SettingsResetScope.Interface: ResetInterfaceDraft(); break;
            case SettingsResetScope.ReadingMedia: ResetReadingMediaDraft(); break;
            case SettingsResetScope.Downloads: ResetDownloadsDraft(); break;
            case SettingsResetScope.WorkerBudgets: ResetWorkerBudgetsDraft(); break;
            case SettingsResetScope.Updates: ResetUpdatesDraft(); break;
            case SettingsResetScope.Notifications: ResetNotificationsDraft(); break;
            case SettingsResetScope.Behavior: ResetBehaviorDraft(); break;
            case SettingsResetScope.All:
                ResetInterfaceDraft();
                ResetReadingMediaDraft();
                ResetDownloadsDraft();
                ResetWorkerBudgetsDraft();
                ResetUpdatesDraft();
                ResetNotificationsDraft();
                ResetBehaviorDraft();
                break;
        }

        // A reset returns the affected numeric text to a valid draft. Reuse the
        // editor's existing validation cleanup and let R6 perform one coalesced
        // dirty/error sweep after the burst of property changes.
        ClearFieldErrors();
        SetStatusMessage("settings.reset.done");
        _resetStatusVisible = true;
        SettingsResetStatus.Text = Texts.T("settings.reset.done");
        SettingsResetStatus.IsVisible = true;
        SweepGroups();
    }

    private void ResetInterfaceDraft()
    {
        Config defaults = ResetDefaults.Values;
        SelectedTheme = Choice(ThemeChoices, defaults.Theme);
        SelectedAnimations = Choice(AnimationsChoices, defaults.Animations);
        SelectedLanguage = LanguageChoices.FirstOrDefault(item => string.Equals(item.Code, defaults.Language, StringComparison.OrdinalIgnoreCase));
        SelectedWorkspaceMode = Choice(WorkspaceChoices, defaults.WorkspaceMode);
        CompactDensity = defaults.CompactDensity;
        ShrinkWindowForBarebones = defaults.ShrinkWindowForBarebones;
    }

    private void ResetReadingMediaDraft()
    {
        Config defaults = ResetDefaults.Values;
        SelectedReadingFontSize = Choice(ReadingFontSizeChoices, defaults.ReadingFontSize);
        SelectedReaderPostWidth = Choice(ReaderPostWidthChoices, defaults.ReaderPostWidth);
        VideoAutoplay = defaults.VideoAutoplay;
        VideoStartMuted = defaults.VideoStartMuted;
        VideoVolume = defaults.VideoVolume;
        ResetNumber(VideoVolumeInput, defaults.VideoVolume);
        RememberReadingPosition = defaults.RememberReadingPosition;
        ShowShortcutHelp = defaults.ShowShortcutHelp;

        LoadReaderDraft(defaults);
        GalleryPlaybackPreferences playback = GalleryPlaybackPreferences.FromConfig(defaults);
        SelectedGalleryPlaybackMode = Choice(GalleryPlaybackModeChoices, playback.Mode.ToString());
        GallerySlideshowSeconds = playback.ImageSeconds;
        ResetNumber(GallerySlideshowSecondsInput, playback.ImageSeconds);
        GalleryMediaFocus = playback.MediaFocus;
        GalleryOverlayControls = playback.OverlayControls;
        GalleryOverlayPostContext = playback.OverlayPostContext;
        LoadGalleryVideoDraft(playback);
        LoadViewerDraft(defaults);
    }

    private void ResetDownloadsDraft()
    {
        Config defaults = ResetDefaults.Values;
        DownloadPath = defaults.DownloadPath;
        RecheckTimer = defaults.RecheckTimer;
        MaxParallelDownloads = defaults.MaxParallelDownloads;
        ParallelDownloadsSafetyLimit = defaults.ParallelDownloadsSafetyLimit;
        BandwidthLimiterMB = ResetDefaults.BandwidthLimiterMiB;
        ResetNumber(RecheckTimerInput, defaults.RecheckTimer);
        ResetNumber(ParallelDownloadsInput, defaults.MaxParallelDownloads);
        ResetNumber(ParallelDownloadsSafetyInput, defaults.ParallelDownloadsSafetyLimit);
        ResetNumber(BandwidthInput, BandwidthLimiterMB.Value);
        _destinationStatus = null;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DestinationStatusText)));
    }

    private void ResetWorkerBudgetsDraft()
    {
        Config defaults = ResetDefaults.Values;
        MinimumFreeSpaceGiB = defaults.MinimumFreeSpaceGiB;
        ProductionWorkers = defaults.PluginMaximumProductionWorkers;
        WorkerIdleMinutes = defaults.PluginWorkerIdleMinutes;
        SourceHeapMiB = defaults.PluginSourceHeapMiB;
        FilterHeapMiB = defaults.PluginFilterHeapMiB;
        BooruCatalogMiB = defaults.BooruCatalogLimitMiB;
        BooruRetryAfterCooldownHours = defaults.BooruRetryAfterCooldownHours;
        PrivateTagLimit = defaults.PrivateTagLimit;
        PrivateTagFileMiB = defaults.PrivateTagFileLimitMiB;
        MediaIndexCacheMiB = defaults.MediaIndexCacheLimitMiB;
        FavoriteEntryLimit = defaults.FavoriteEntryLimit;
        FavoriteFileLimitMiB = defaults.FavoriteFileLimitMiB;
        PostBookmarkLimit = defaults.PostBookmarkLimit;
        SavedSearchLimit = defaults.SavedSearchLimit;
        ReaderGroupLimit = defaults.ReaderGroupLimit;
        SourcesWatchPreviewRows = defaults.SourcesWatchPreviewRows;
        ExportConfirmFiles = defaults.ExportConfirmFiles;
        ExportConfirmGiB = defaults.ExportConfirmGiB;
        PosterCacheLimitMiB = defaults.PosterCacheLimitMiB;
        PosterCacheFileLimit = defaults.PosterCacheFileLimit;
        AudioProbeCacheLimitMiB = defaults.AudioProbeCacheLimitMiB;
        AudioProbeCacheEntries = defaults.AudioProbeCacheEntries;
        ThumbnailCacheTiles = defaults.ThumbnailCacheTiles;
        ClipboardUrlLimit = defaults.ClipboardUrlLimit;
        ClipboardTextLimitKiB = defaults.ClipboardTextLimitKiB;
        GalleryImageCacheMiB = defaults.GalleryImageCacheMiB;
        GalleryPreloadAhead = defaults.GalleryPreloadAhead;
        GalleryPreloadBehind = defaults.GalleryPreloadBehind;
        ImportMaxFilesPerBatch = defaults.ImportMaxFilesPerBatch;
        ImportMaxBatchGiB = defaults.ImportMaxBatchGiB;
        ImportMaxFileMiB = defaults.ImportMaxFileMiB;
        ImportMaxTotalFiles = defaults.ImportMaxTotalFiles;
        ImportMaxTagsPerBatch = defaults.ImportMaxTagsPerBatch;
        ImportMaxBatches = defaults.ImportMaxBatches;
        MediaIndexMaxEntries = defaults.MediaIndexMaxEntries;
        MediaIndexMaxThreads = defaults.MediaIndexMaxThreads;
        ImportCatalogLimitMiB = defaults.ImportCatalogLimitMiB;
        PostFilterRuleLimit = defaults.PostFilterRuleLimit;
        HiddenPostLimit = defaults.HiddenPostLimit;
        LoadQuickTagDraft(defaults);
        LoadLimitNumbersDraft(defaults);

        ResetNumber(ReserveInput, defaults.MinimumFreeSpaceGiB);
        ResetNumber(ProductionWorkersInput, defaults.PluginMaximumProductionWorkers);
        ResetNumber(WorkerIdleMinutesInput, defaults.PluginWorkerIdleMinutes);
        ResetNumber(SourceHeapMiBInput, defaults.PluginSourceHeapMiB);
        ResetNumber(FilterHeapMiBInput, defaults.PluginFilterHeapMiB);
        ResetNumber(BooruCatalogMiBInput, defaults.BooruCatalogLimitMiB);
        ResetNumber(BooruRetryAfterCooldownHoursInput, defaults.BooruRetryAfterCooldownHours);
        ResetNumber(PrivateTagLimitInput, defaults.PrivateTagLimit);
        ResetNumber(PrivateTagFileMiBInput, defaults.PrivateTagFileLimitMiB);
        ResetNumber(MediaIndexCacheMiBInput, defaults.MediaIndexCacheLimitMiB);
        ResetNumber(FavoriteEntryLimitInput, defaults.FavoriteEntryLimit);
        ResetNumber(FavoriteFileLimitMiBInput, defaults.FavoriteFileLimitMiB);
        ResetNumber(PostBookmarkLimitInput, defaults.PostBookmarkLimit);
        ResetNumber(SavedSearchLimitInput, defaults.SavedSearchLimit);
        ResetNumber(ReaderGroupLimitInput, defaults.ReaderGroupLimit);
        ResetNumber(SourcesWatchPreviewRowsInput, defaults.SourcesWatchPreviewRows);
        ResetNumber(ExportConfirmFilesInput, defaults.ExportConfirmFiles);
        ResetNumber(ExportConfirmGiBInput, defaults.ExportConfirmGiB);
        ResetNumber(PosterCacheLimitMiBInput, defaults.PosterCacheLimitMiB);
        ResetNumber(PosterCacheFileLimitInput, defaults.PosterCacheFileLimit);
        ResetNumber(AudioProbeCacheLimitMiBInput, defaults.AudioProbeCacheLimitMiB);
        ResetNumber(AudioProbeCacheEntriesInput, defaults.AudioProbeCacheEntries);
        ResetNumber(ThumbnailCacheTilesInput, defaults.ThumbnailCacheTiles);
        ResetNumber(ClipboardUrlLimitInput, defaults.ClipboardUrlLimit);
        ResetNumber(ClipboardTextLimitKiBInput, defaults.ClipboardTextLimitKiB);
        ResetNumber(GalleryImageCacheMiBInput, defaults.GalleryImageCacheMiB);
        ResetNumber(GalleryPreloadAheadInput, defaults.GalleryPreloadAhead);
        ResetNumber(GalleryPreloadBehindInput, defaults.GalleryPreloadBehind);
        ResetNumber(ImportMaxFilesPerBatchInput, defaults.ImportMaxFilesPerBatch);
        ResetNumber(ImportMaxBatchGiBInput, defaults.ImportMaxBatchGiB);
        ResetNumber(ImportMaxFileMiBInput, defaults.ImportMaxFileMiB);
        ResetNumber(ImportMaxTotalFilesInput, defaults.ImportMaxTotalFiles);
        ResetNumber(ImportMaxTagsPerBatchInput, defaults.ImportMaxTagsPerBatch);
        ResetNumber(ImportMaxBatchesInput, defaults.ImportMaxBatches);
        ResetNumber(MediaIndexMaxEntriesInput, defaults.MediaIndexMaxEntries);
        ResetNumber(MediaIndexMaxThreadsInput, defaults.MediaIndexMaxThreads);
        ResetNumber(ImportCatalogLimitMiBInput, defaults.ImportCatalogLimitMiB);
        ResetNumber(PostFilterRuleLimitInput, defaults.PostFilterRuleLimit);
        ResetNumber(HiddenPostLimitInput, defaults.HiddenPostLimit);
        ResetNumber(QuickTagSuggestionCountInput, defaults.QuickTagSuggestionCount);
        ResetNumber(QuickTagRecentCountInput, defaults.QuickTagRecentCount);
        ResetNumber(QuickTagUndoDepthInput, defaults.QuickTagUndoDepth);
        ResetNumber(QuickTagNeighbourScanInput, defaults.QuickTagNeighbourScan);
        ResetNumber(QuickTagFilenameTokensInput, defaults.QuickTagFilenameTokens);
        ResetNumber(QuickTagSourceTagsPerMediumInput, defaults.QuickTagSourceTagsPerMedium);
        ResetNumber(QuickTagMarkedMediaLimitInput, defaults.QuickTagMarkedMediaLimit);
        ResetNumber(QuickTagStampSizeInput, defaults.QuickTagStampSize);
    }

    private void ResetUpdatesDraft()
    {
        Config defaults = ResetDefaults.Values;
        AutomaticUpdateChecks = defaults.AutomaticUpdateChecks;
        IncludePrereleaseUpdates = defaults.IncludePrereleaseUpdates;
    }

    private void ResetNotificationsDraft()
    {
        Config defaults = ResetDefaults.Values;
        ShowInformationNotifications = defaults.ShowInformationNotifications;
        ShowWarningNotifications = defaults.ShowWarningNotifications;
        ShowErrorNotifications = defaults.ShowErrorNotifications;
        WorkViewSwitchNoticeSeconds = defaults.WorkViewSwitchNoticeSeconds;
        ResetNumber(WorkViewSwitchNoticeSecondsInput, defaults.WorkViewSwitchNoticeSeconds);
    }

    private void ResetBehaviorDraft()
    {
        Config defaults = ResetDefaults.Values;
        SaveUrlsOnExit = defaults.SaveUrlsOnExit;
        ShowBoardAdmissionWizard = defaults.ShowBoardAdmissionWizard;
        MonitorClipboardUrls = defaults.MonitorClipboardUrls;
        MinimizeToTray = defaults.MinimizeToTray;
        CloseToTray = defaults.CloseToTray;
        LoadStatisticsDraft(defaults);
        StatsHeaderIcons = defaults.StatsHeaderIcons;
        RefreshStatsHeaderGraphChoices(defaults.StatsHeaderGraph);
    }

    private static SettingsChoice? Choice(System.Collections.Generic.IReadOnlyList<SettingsChoice> choices, string value) =>
        choices.FirstOrDefault(item => string.Equals(item.Value, value, StringComparison.Ordinal));

    private void EndResetLifetime()
    {
        _resetConfirmation?.Hide();
        _resetConfirmation = null;
        _resetConfirmationOrigin = null;
    }
}

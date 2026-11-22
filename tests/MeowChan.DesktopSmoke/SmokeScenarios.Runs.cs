using Avalonia;
using MeowChan;
using MeowChan.Desktop;
using MeowChan.Persistence;

namespace MeowChan.DesktopSmoke;

// When a Root/Branch run starts, relative to the fixed startup sequence in Program.RunAsync.
internal enum SmokeStage
{
    // The main window exists and nothing has been awaited, not even its first visibility.
    BeforeVisible,
    // The main window is visible; the desktop session has not been started.
    BeforeSession,
    // The session reached Ready with an empty isolated profile.
    Ready,
    // After CommandRefreshChecks.Run(), which every later run and the modes share.
    AfterCommandRefresh,
    // The ordinary normal head has completed and disposed its synthetic rows.
    NormalHead
}

// A Root/Branch scenario's run. Program sets _checksPassed and closes the window after Run returns.
internal sealed record SmokeRun(SmokeStage Stage, Func<MainWindow, Task> Run)
{
    // Edits the initial settings before they are saved.
    internal Action<Config>? Configure { get; init; }
    // Prepares the isolated profile after the initial settings are saved, before the app starts.
    internal Func<string, Task>? Seed { get; init; }
    // Replaces the production App for this run.
    internal Func<AppBuilder>? App { get; init; }
    // Runs after the normal close with the reacquired profile lease.
    internal Action<string, ProfileLease>? AfterClose { get; init; }
    // A configuration error (exit 2) when this run cannot start here; checked before the profile exists.
    internal Func<string?>? Precondition { get; init; }
    // Created from Main's entry timestamps before the profile exists and kept until Main returns;
    // Program reports the run's outcome to it before the final result line.
    internal Func<long, DateTimeOffset, StartupFirstFrameMeasurement>? Measure { get; init; }
    // Keeps Avalonia's native tray on private Xvfb instead of the no-tray platform.
    internal bool NativeTray { get; init; }
}

internal static partial class SmokeScenarios
{
    internal static Task RunReaderAlternativeAsync(string id, MainWindow window) => id switch
    {
        "video-lifecycle" => VideoLifecycleChecks.RunAsync(),
        "window-minimum" => WindowMinimumChecks.RunAsync(window),
        "reader-owned-navigation" or "reader-gallery-roundtrip" or "reader-list-gallery-return"
            => ReaderUxChecks.RunJourneyAsync(window, id),
        _ => ReaderUxChecks.RunAsync(window)
    };

    // Exactly the Root and Branch ids of All; Program refuses to start otherwise. Command-line
    // modes keep their bespoke profile, app and lifetime setup in Program; nested selectors are read
    // by their parent's checks.
    internal static readonly IReadOnlyDictionary<string, SmokeRun> Runs = new Dictionary<string, SmokeRun>(StringComparer.Ordinal)
    {
        ["startup-first-frame"] = new(SmokeStage.BeforeVisible, mainWindow => StartupFirstFrameChecks.RunAsync(mainWindow))
        {
            App = AppBuilder.Configure<StartupFirstFrameSmokeApp>,
            Seed = profile => { StartupFirstFrameChecks.Prepare(profile); return Task.CompletedTask; },
            Precondition = () => OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") == "1"
                ? null : "startup measurement requires the private Linux Xvfb runner.",
            Measure = (entryTimestamp, entryUtc) => new StartupFirstFrameMeasurement(entryTimestamp, entryUtc),
            NativeTray = true
        },
        ["startup-preview"] = new(SmokeStage.BeforeSession, mainWindow => StartupPreviewChecks.RunAsync(mainWindow))
        {
            App = AppBuilder.Configure<StartupPreviewSmokeApp>,
            Seed = profile => { StartupPreviewChecks.Prepare(profile); return Task.CompletedTask; }
        },
        ["interrupted-startup"] = new(SmokeStage.BeforeSession,
            mainWindow => InterruptedStartupChecks.RunAsync(mainWindow, condition => Program.WaitUntilAsync(condition)))
        {
            Seed = profile => { InterruptedStartupChecks.Seed(profile); return Task.CompletedTask; }
        },
        ["diagnostic-evidence"] = new(SmokeStage.Ready, mainWindow => DiagnosticEvidenceChecks.RunAsync(mainWindow)),
        ["reader-post-star"] = new(SmokeStage.Ready, mainWindow => ReaderPostStarChecks.RunAsync(mainWindow))
        {
            Seed = ReaderPostStarChecks.SeedAsync,
            Configure = config => { config.DownloadsPaused = true; config.SaveUrlsOnExit = false; }
        },
        ["reader-preview-return"] = new(SmokeStage.Ready, mainWindow => ReaderPreviewChecks.RunAsync(mainWindow, returnOnly: true)),
        ["tag-autocomplete-commit"] = new(SmokeStage.Ready, mainWindow => TagAutocompleteCommitChecks.RunAsync(mainWindow)),
        ["quick-tag-native-typing"] = new(SmokeStage.Ready, mainWindow => QuickTagNativeTypingChecks.RunAsync(mainWindow))
        {
            Seed = QuickTagNativeTypingChecks.SeedAsync,
            Configure = config => { config.DownloadsPaused = true; config.SaveUrlsOnExit = false; }
        },
        ["filename-search-large-reader"] = new(SmokeStage.Ready, mainWindow => FilenameSearchLargeReaderChecks.RunAsync(mainWindow)),
        ["local-search-language"] = new(SmokeStage.Ready, mainWindow => LocalSearchLanguageRefreshChecks.RunAsync(mainWindow)),
        ["media-page"] = new(SmokeStage.Ready, async mainWindow =>
        {
            await MediaMenuCompositionChecks.RunAsync(mainWindow);
            await MediaPageChecks.RunAsync(mainWindow);
        }),
        ["media-panel-selection"] = new(SmokeStage.Ready, mainWindow => MediaPageChecks.RunPanelSelectionAsync(mainWindow)),
        ["gallery-exif-orientation"] = new(SmokeStage.Ready, mainWindow => GalleryExifOrientationChecks.RunAsync(mainWindow))
        {
            Seed = ExifOrientationFixture.SeedAsync,
            Configure = config => { config.WorkspaceMode = "Advanced"; config.DownloadsPaused = true; config.SaveUrlsOnExit = false; }
        },
        ["media-page-booru-captures"] = new(SmokeStage.Ready, mainWindow => MediaPageChecks.RunBooruCapturesAsync(mainWindow))
        {
            Seed = ExifOrientationFixture.SeedAsync,
            Configure = config => { config.DownloadsPaused = true; config.SaveUrlsOnExit = false; }
        },
        ["media-slot"] = new(SmokeStage.Ready, mainWindow => MediaSlotChecks.RunAsync(mainWindow)),
        ["media-menu-surfaces"] = new(SmokeStage.Ready, mainWindow => MediaMenuSurfacesChecks.RunAsync(mainWindow)),
        ["focus-click-contracts"] = new(SmokeStage.Ready, mainWindow => MediaPageChecks.RunClickContractsAsync(mainWindow)),
        ["motion"] = new(SmokeStage.Ready, mainWindow => MotionChecks.RunAsync(mainWindow)),
        ["gallery-toolbar"] = new(SmokeStage.Ready, mainWindow => GalleryToolbarLayoutChecks.RunAsync(mainWindow)),
        ["gallery-narrow"] = new(SmokeStage.Ready, mainWindow => GalleryNarrowChromeChecks.RunAsync(mainWindow))
        {
            // The busiest bar by default (Advanced, window minimum 760); MEOWCHAN_GALLERY_NARROW_VIEW=Barebones reaches the 640 dip window of the compact density.
            Configure = config => { config.WorkspaceMode = Environment.GetEnvironmentVariable("MEOWCHAN_GALLERY_NARROW_VIEW") ?? "Advanced"; config.DownloadsPaused = true; config.SaveUrlsOnExit = false; }
        },
        ["theme-colors"] = new(SmokeStage.Ready, mainWindow => ThemeColorsChecks.RunAsync(mainWindow)),
        ["tagging-page"] = new(SmokeStage.Ready, mainWindow => TaggingPageChecks.RunAsync(mainWindow))
        {
            Seed = MediaPageChecks.SeedAsync,
            Configure = config => { config.Theme = "Light"; config.WorkViewSwitchNoticeSeconds = 0; }
        },
        ["gallery-media-tags"] = new(SmokeStage.Ready, mainWindow => GalleryMediaTagChecks.RunAsync(mainWindow)),
        ["tagging-mode"] = new(SmokeStage.Ready, mainWindow => TaggingModeChecks.RunAsync(mainWindow)),
        ["tagging-cards"] = new(SmokeStage.Ready, mainWindow => TaggingCardsChecks.RunAsync(mainWindow)),
        ["local-media-tags"] = new(SmokeStage.Ready, mainWindow => LocalMediaTagChecks.RunAsync(mainWindow)),
        ["manual-import"] = new(SmokeStage.Ready, mainWindow => ManualImportChecks.RunAsync(mainWindow)),
        ["profile-switch"] = new(SmokeStage.Ready, mainWindow => ProfileSwitchChecks.RunAsync(mainWindow)),
        ["folder-source"] = new(SmokeStage.Ready, mainWindow => FolderSourceChecks.RunAsync(mainWindow)),
        ["booru-media"] = new(SmokeStage.Ready, mainWindow => BooruMediaChecks.RunAsync(mainWindow)),
        ["private-tags"] = new(SmokeStage.Ready, mainWindow => PrivateTagBarChecks.RunAsync(mainWindow)),
        ["reader-find"] = new(SmokeStage.Ready, mainWindow => ReaderThreadFindChecks.RunAsync(mainWindow)),
        ["filename-search"] = new(SmokeStage.Ready, mainWindow => FilenameSearchChecks.RunAsync(mainWindow)),
        ["media-hash-search"] = new(SmokeStage.Ready, mainWindow => MediaHashSearchChecks.RunAsync(mainWindow)),
        ["unified-search"] = new(SmokeStage.Ready, mainWindow => UnifiedSearchChecks.RunAsync(mainWindow)),
        ["filename-search-host"] = new(SmokeStage.Ready, async mainWindow =>
        {
            await FilenameSearchHostNavigationChecks.RunAsync(mainWindow);
            await FilenameSearchHostLifetimeChecks.RunAsync(mainWindow);
        }),
        ["urltext-export"] = new(SmokeStage.Ready, mainWindow => UrlTextExportWorkflowChecks.RunAsync(mainWindow)),
        ["urltext-page-boundary"] = new(SmokeStage.Ready, mainWindow => UrlTextExportPageBoundaryChecks.RunAsync(mainWindow)),
        ["gallery-volume-routed"] = new(SmokeStage.Ready, mainWindow => GalleryVolumeNativeEditingChecks.RunAsync(mainWindow)),
        ["first-run-reopen"] = new(SmokeStage.Ready, mainWindow => FirstRunSettingsReopenChecks.RunAsync(mainWindow)),
        ["dialog-frame"] = new(SmokeStage.Ready, mainWindow => DialogFrameChecks.RunAsync(mainWindow)),
        ["dialog-gallery"] = new(SmokeStage.Ready, mainWindow => DialogGalleryChecks.RunAsync(mainWindow)),
        ["html-export-action-layout"] = new(SmokeStage.Ready, mainWindow => HtmlExportActionLayoutChecks.RunAsync(mainWindow)),
        ["shortcut-issue-language"] = new(SmokeStage.Ready, mainWindow => ShortcutIssueLanguageRefreshChecks.RunAsync(mainWindow)),
        ["export-status-language"] = new(SmokeStage.Ready, mainWindow => ArchiveExportStatusLanguageRefreshChecks.RunAsync(mainWindow)),
        ["reader-groups-binding"] = new(SmokeStage.Ready, mainWindow => ReaderGroupsBindingLanguageChecks.RunAsync(mainWindow)),
        ["native-transfer-preview"] = new(SmokeStage.Ready, async mainWindow =>
        {
            await NativeTransferPreviewLifecycleChecks.RunAsync(mainWindow);
            await NativeTransferV4PreviewChecks.RunAsync(mainWindow);
        }),
        ["html-export-labels"] = new(SmokeStage.Ready, mainWindow => HtmlExportButtonLabelChecks.RunAsync(mainWindow)),
        ["gif-playback-core"] = new(SmokeStage.Ready, mainWindow => GifPlaybackChecks.RunPlaybackAsync(mainWindow)),
        ["gif-successors"] = new(SmokeStage.Ready, mainWindow => GifPlaybackChecks.RunSuccessorsAsync(mainWindow)),
        ["gif-verified-failures"] = new(SmokeStage.Ready, mainWindow => GifPlaybackChecks.RunVerifiedFailuresAsync(mainWindow)),
        ["gif-reader-association"] = new(SmokeStage.Ready, mainWindow => GifPlaybackChecks.RunReaderAssociationAsync(mainWindow)),
        ["gif-ownership"] = new(SmokeStage.Ready, mainWindow => GifOwnershipChecks.RunAsync(mainWindow))
        {
            // The unsplit GIF root previously supplied this guard before ownership ran.
            Precondition = () => (OperatingSystem.IsLinux()
                ? Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") == "1"
                : Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_NATIVE_ALLOWED") == "1")
                ? null : "GIF ownership requires an authorized disposable native desktop; no personal display fallback."
        },
        ["gallery-errors-retirement-clip"] = new(SmokeStage.Ready, mainWindow => GalleryErrorInteractionChecks.RunRetirementClipAsync(mainWindow)),
        ["gallery-errors-layout-roots"] = new(SmokeStage.Ready, mainWindow => GalleryErrorInteractionChecks.RunLayoutRootsAsync(mainWindow)),
        ["gallery-errors-notifications"] = new(SmokeStage.Ready, mainWindow => GalleryErrorInteractionChecks.RunNotificationsAsync(mainWindow)),
        ["gallery-errors-failure-reader-return"] = new(SmokeStage.Ready, mainWindow => GalleryErrorInteractionChecks.RunFailureReaderReturnAsync(mainWindow)),
        ["gif-playback"] = new(SmokeStage.Ready, async mainWindow =>
        {
            await GifPlaybackChecks.RunAsync(mainWindow);
            await GifOwnershipChecks.RunAsync(mainWindow);
        }),
        ["clipboard-monitor"] = new(SmokeStage.Ready, mainWindow => ClipboardMonitorChecks.RunAsync(mainWindow)),
        ["download-safety"] = new(SmokeStage.Ready, mainWindow => DownloadSafetyLimitChecks.RunAsync(mainWindow)),
        ["booru-workbench"] = new(SmokeStage.Ready, mainWindow => BooruWorkbenchChecks.RunAsync(mainWindow)),
        ["booru-composition"] = new(SmokeStage.Ready, mainWindow => BooruCompositionChecks.RunAsync(mainWindow)),
        ["booru-busy-fallback"] = new(SmokeStage.Ready, mainWindow => BooruBusyFallbackChecks.RunAsync(mainWindow)),
        ["board-language-refresh"] = new(SmokeStage.Ready, mainWindow => BoardAdmissionLanguageRefreshChecks.RunAsync(mainWindow)),
        ["rating-language-refresh"] = new(SmokeStage.Ready, mainWindow => RatingLanguageRefreshChecks.RunAsync(mainWindow)),
        ["html-export-language"] = new(SmokeStage.Ready, mainWindow => HtmlExportLanguageChecks.RunAsync(mainWindow)),
        ["secondary-settings-language"] = new(SmokeStage.Ready, async mainWindow =>
        {
            string cell = Environment.GetEnvironmentVariable("MEOWCHAN_SECONDARY_SETTINGS_CELL") ?? "all";
            switch (cell)
            {
                case "gallery": await SecondarySettingsLanguageRefreshChecks.RunGalleryPlaybackAsync(mainWindow); break;
                case "frame": await SecondarySettingsLanguageRefreshChecks.RunShortcutFrameAsync(mainWindow); break;
                case "capture": await SecondarySettingsLanguageRefreshChecks.RunShortcutCaptureAsync(mainWindow); break;
                case "all": await SecondarySettingsLanguageRefreshChecks.RunAsync(mainWindow); break;
                default: throw new InvalidOperationException("Unknown secondary Settings language cell: " + cell);
            }
        }),
        ["gallery-hidden-favorite"] = new(SmokeStage.Ready, mainWindow => GalleryHiddenFavoriteChecks.RunAsync(mainWindow)),
        ["settings-language-refresh"] = new(SmokeStage.Ready, mainWindow => SettingsLanguageRefreshChecks.RunAsync(mainWindow)),
        ["settings-reader-display"] = new(SmokeStage.Ready, mainWindow => SettingsReaderDisplayChecks.RunAsync(mainWindow))
        { Seed = SettingsReaderDisplayChecks.SeedReaderAsync },
        ["first-run-settings"] = new(SmokeStage.Ready, mainWindow => FirstRunSettingsChecks.RunAsync(mainWindow)),
        ["first-run-setup"] = new(SmokeStage.Ready, mainWindow => FirstRunSetupChecks.RunAsync(mainWindow)),
        ["preference-startup"] = new(SmokeStage.Ready, mainWindow => PreferenceStartupChecks.RunAsync(mainWindow)),
        ["native-drain-boundary"] = new(SmokeStage.Ready, mainWindow => NativeDrainBoundaryChecks.RunAsync(mainWindow)),
        ["compact-diagnostics"] = new(SmokeStage.Ready, async mainWindow =>
        {
            await DownloadCompactStatusChecks.RunAsync(mainWindow);
            await DiagnosticsScopedRetryChecks.RunAsync(mainWindow);
        }),
        ["gallery-error"] = new(SmokeStage.Ready, mainWindow => GalleryErrorInteractionChecks.RunAsync(mainWindow)),
        ["feedback"] = new(SmokeStage.Ready, mainWindow => FeedbackChecks.CheckDismissalAsync(mainWindow)),
        ["gallery-playback-features"] = new(SmokeStage.Ready, mainWindow => GalleryPlaybackFeatureChecks.RunAsync(mainWindow)),
        ["gallery-mute"] = new(SmokeStage.Ready, mainWindow => GalleryMutePersistenceChecks.RunAsync(mainWindow)) { Seed = SeedTwoVideosAsync },
        ["media-interaction"] = new(SmokeStage.Ready, mainWindow => MediaInteractionChecks.RunAsync(mainWindow)) { Seed = SeedTwoVideosAsync },
        ["native-ime"] = new(SmokeStage.Ready, mainWindow => NativeImeChecks.RunAsync(mainWindow)),
        ["gallery-integrity-gui"] = new(SmokeStage.Ready, mainWindow => GalleryIntegrityChecks.RunAsync(mainWindow)),
        ["smoke-alpha2-copy"] = new(SmokeStage.Ready, mainWindow => Alpha2CopyChecks.RunAsync(mainWindow)),
        ["source-filter-wave"] = new(SmokeStage.Ready, mainWindow => SourceFilterWaveChecks.RunAsync(mainWindow)),
        ["filter-pointer-selection"] = new(SmokeStage.Ready, mainWindow => FilterPointerSelectionChecks.RunAsync(mainWindow)),
        ["archive-media-counts"] = new(SmokeStage.Ready, async mainWindow =>
        {
            await ArchiveMediaCountsChecks.RunAsync(mainWindow);
            await Program.RunOptionalCheckAsync("ArchiveUpdateCoalescingChecks", mainWindow);
        }),
        ["source-integrity-gui"] = new(SmokeStage.Ready, mainWindow => SourceIntegrityChecks.RunAsync(mainWindow)),
        ["suggestions"] = new(SmokeStage.Ready, mainWindow => SuggestionsPresentationChecks.RunAsync(mainWindow)),
        ["ursa-native-suite-groups"] = new(SmokeStage.Ready, mainWindow => ReaderGroupChecks.RunAsync(mainWindow)),
        ["media-fit-benchmark"] = new(SmokeStage.Ready, async mainWindow =>
        {
            GalleryImageDecoder.DecodeStarted = request => Console.WriteLine("MEDIA_DECODE " + request.GetType().Name);
            try { await GalleryFitBenchmark.RunAsync(mainWindow); }
            finally { GalleryImageDecoder.DecodeStarted = null; }
        }),
        // Measures the session start itself, so it runs before the harness awaits it.
        ["media-scale-benchmark"] = new(SmokeStage.BeforeSession, mainWindow => MediaScaleBenchmark.RunAsync(mainWindow))
        {
            Configure = MediaScaleBenchmark.Configure,
            Seed = MediaScaleBenchmark.SeedAsync
        },
        ["media-ratings"] = new(SmokeStage.Ready, mainWindow => MediaRatingChecks.RunAsync(mainWindow))
        {
            Configure = config => config.WorkspaceMode = Environment.GetEnvironmentVariable("MEOWCHAN_RATINGS_MODE") ?? "Advanced",
            Seed = MediaRatingFixture.SeedAsync,
            AfterClose = MediaRatingChecks.VerifyAfterClose
        },
        ["section-comparison"] = new(SmokeStage.Ready, mainWindow => SectionComparisonChecks.RunAsync(mainWindow)),
        ["template-comparison"] = new(SmokeStage.AfterCommandRefresh, mainWindow => TemplateComparisonChecks.RunAsync(mainWindow)),
        ["archive-comparison"] = new(SmokeStage.AfterCommandRefresh, mainWindow => ArchiveComparisonChecks.RunAsync(mainWindow)),
        // Its disposable profile, settings and fixture stay in Program.Main (MEOWCHAN_SAVED_SEARCH_TEST_PROFILE).
        ["saved-searches"] = new(SmokeStage.AfterCommandRefresh,
            mainWindow => SavedArchiveSearchChecks.RunAsync(mainWindow, Config.GetConfigDirectory())),
        ["footer-navigation"] = new(SmokeStage.AfterCommandRefresh, async mainWindow =>
        {
            await FooterNavigationChecks.RunAsync(mainWindow);
            await BrandNavigationChecks.RunAsync(mainWindow);
            await PageTitleOffsetChecks.RunAsync(mainWindow);
        }),
        ["statistics"] = new(SmokeStage.AfterCommandRefresh, mainWindow => StatisticsChecks.RunAsync(mainWindow))
        {
            Configure = config => { config.WorkspaceMode = "Advanced"; config.StatsSampleIntervalMs = StatisticsChecks.SampleIntervalMs; }
        },
        ["status-deep-links"] = new(SmokeStage.AfterCommandRefresh, mainWindow => StatusDeepLinkChecks.RunAsync(mainWindow)),
        ["context-menus-header"] = new(SmokeStage.AfterCommandRefresh, mainWindow => ContextMenusHeaderChecks.RunAsync(mainWindow))
        {
            Configure = config => { config.WorkspaceMode = "Advanced"; config.StatsHeaderGraph = "Auto"; }
        },
        ["context-menu-layout"] = new(SmokeStage.AfterCommandRefresh, mainWindow => ContextMenuLayoutChecks.RunAsync(mainWindow))
        {
            Configure = config => config.WorkspaceMode = "Advanced"
        },
        ["picker-lifetime"] = new(SmokeStage.AfterCommandRefresh, mainWindow => SettingsPickerLifetimeChecks.RunAsync(mainWindow)),
        ["settings-category"] = new(SmokeStage.AfterCommandRefresh, mainWindow => SettingsCategoryChecks.RunAsync(mainWindow)),
        ["settings-search"] = new(SmokeStage.AfterCommandRefresh, mainWindow => SettingsSearchChecks.RunAsync(mainWindow)),
        ["settings-statistics"] = new(SmokeStage.AfterCommandRefresh, mainWindow => StatisticsSettingsChecks.RunAsync(mainWindow)),
        ["rules-settings-captures"] = new(SmokeStage.AfterCommandRefresh, mainWindow => RulesSettingsCaptureChecks.RunAsync(mainWindow)),
        ["rules-walkthrough-captures"] = new(SmokeStage.AfterCommandRefresh, mainWindow => RulesWalkthroughCaptures.RunAsync(mainWindow)),
        ["settings-lifetime"] = new(SmokeStage.AfterCommandRefresh, mainWindow => SettingsPageChecks.RunLateSaveFailureAsync(mainWindow)),
        ["path-input"] = new(SmokeStage.AfterCommandRefresh, mainWindow => SettingsPathInputChecks.RunAsync(mainWindow)),
        ["numeric-focus"] = new(SmokeStage.AfterCommandRefresh, async mainWindow =>
        {
            await SettingsAccessibilityChecks.RunAsync(mainWindow);
            await SettingsChecks.RunAsync(mainWindow);
        }),
        ["media-performance"] = new(SmokeStage.AfterCommandRefresh, async mainWindow =>
        {
            string language = Environment.GetEnvironmentVariable("MEOWCHAN_MEDIA_TEST_LANGUAGE") ?? "en";
            string theme = Environment.GetEnvironmentVariable("MEOWCHAN_MEDIA_TEST_THEME") ?? "Light";
            MeowChan.Desktop.Localization.Texts.SetLanguage(language);
            Application.Current!.RequestedThemeVariant = theme.Equals("dark", StringComparison.OrdinalIgnoreCase)
                ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
            await MediaTilePanelChecks.RunAsync(mainWindow);
            await MediaTileIntegrationChecks.RunAsync(mainWindow);
            await GalleryFitChecks.RunAsync(mainWindow);
            await GalleryViewerChecks.RunAsync(mainWindow);
        }),
        ["media-integration"] = new(SmokeStage.AfterCommandRefresh, mainWindow => MediaTileIntegrationChecks.RunAsync(mainWindow)),
        ["text-selection"] = new(SmokeStage.AfterCommandRefresh, mainWindow => TextSelectionChecks.RunAsync(mainWindow)),
        ["gallery-favorites"] = new(SmokeStage.AfterCommandRefresh, mainWindow => GalleryViewerChecks.RunAsync(mainWindow)),
        ["gallery-theme"] = new(SmokeStage.AfterCommandRefresh, mainWindow => GalleryViewerChecks.RunAsync(mainWindow)),
        ["gallery-fit"] = new(SmokeStage.AfterCommandRefresh, async mainWindow =>
        {
            await GalleryFitChecks.RunAsync(mainWindow);
            await GalleryViewerChecks.RunAsync(mainWindow);
        }),
        ["media-tile-panel"] = new(SmokeStage.AfterCommandRefresh, async mainWindow =>
        {
            await MediaTilePanelChecks.RunAsync(mainWindow);
            await MediaTileRefillChecks.RunAsync(mainWindow);
        }),
        ["diagnostic-feedback"] = new(SmokeStage.AfterCommandRefresh, mainWindow => DiagnosticsFeedbackChecks.RunAsync(mainWindow)),
        ["normal-core"] = new(SmokeStage.NormalHead, _ => Task.CompletedTask),
        ["normal-board-admission"] = new(SmokeStage.AfterCommandRefresh, mainWindow => BoardAdmissionChecks.RunAsync(mainWindow)),
        ["normal-grid"] = new(SmokeStage.AfterCommandRefresh, mainWindow => GridChecks.RunAsync(mainWindow)),
        ["normal-settings-page"] = new(SmokeStage.AfterCommandRefresh, mainWindow => SettingsPageChecks.RunAsync(mainWindow)),
        ["normal-diagnostics"] = new(SmokeStage.AfterCommandRefresh, mainWindow => DiagnosticsChecks.RunAsync(mainWindow)),
        ["sources-page"] = new(SmokeStage.AfterCommandRefresh, mainWindow => SourcesChecks.RunAsync(mainWindow)),
        ["pause-controls"] = new(SmokeStage.AfterCommandRefresh, mainWindow => PauseChecks.RunAsync(mainWindow)),
        ["rules-page"] = new(SmokeStage.AfterCommandRefresh, mainWindow => RulesPageChecks.RunAsync(mainWindow)),
        ["settings-core"] = new(SmokeStage.AfterCommandRefresh, mainWindow => SettingsChecks.RunAsync(mainWindow)),
        ["page-refresh"] = new(SmokeStage.AfterCommandRefresh, async mainWindow =>
        {
            await DiagnosticsChecks.RunAsync(mainWindow);
            await SourcesChecks.RunAsync(mainWindow);
        }),
        ["stat-footer"] = new(SmokeStage.AfterCommandRefresh, mainWindow => StatFooterChecks.RunAsync(mainWindow)),
        ["post-bookmark-transitions"] = new(SmokeStage.AfterCommandRefresh, mainWindow => PostBookmarkTransitionChecks.RunAsync(mainWindow)),
        ["post-bookmark-locale"] = new(SmokeStage.AfterCommandRefresh, mainWindow => PostBookmarkLocaleChecks.RunAsync(mainWindow)),
        ["post-bookmark-comparison"] = new(SmokeStage.AfterCommandRefresh, mainWindow => PostBookmarkComparisonChecks.RunAsync(mainWindow)),
        ["post-bookmarks"] = new(SmokeStage.AfterCommandRefresh, mainWindow => PostBookmarkChecks.RunAsync(mainWindow)),
        ["command-contrast"] = new(SmokeStage.AfterCommandRefresh, mainWindow => CommandButtonContrastChecks.RunAsync(mainWindow)),
        ["hover-contrast"] = new(SmokeStage.AfterCommandRefresh, mainWindow => HoverContrastChecks.RunAsync(mainWindow)),
        ["action-dynamic"] = new(SmokeStage.AfterCommandRefresh, mainWindow => ActionToolBarDynamicChecks.RunAsync(mainWindow)),
        ["action-overflow"] = new(SmokeStage.AfterCommandRefresh, mainWindow => ActionToolBarPresentationChecks.RunAsync(mainWindow)),
        ["download-search"] = new(SmokeStage.AfterCommandRefresh, mainWindow => DownloadSearchChecks.RunAsync(mainWindow)),
        ["settings-footer"] = new(SmokeStage.AfterCommandRefresh, mainWindow => SettingsPageChecks.RunFooterOverlapDiagnosticAsync(mainWindow)),
        ["ui-refinement"] = new(SmokeStage.AfterCommandRefresh, async mainWindow =>
        {
            await ReaderLiveChecks.RunAsync(mainWindow);
            await ReaderRepliesChecks.RunAsync(mainWindow);
            await SettingsPageChecks.RunAsync(mainWindow);
            await DiagnosticsChecks.RunAsync(mainWindow);
            await SourcesChecks.RunAsync(mainWindow);
            await RulesPageChecks.RunAsync(mainWindow);
            await SettingsChecks.RunAsync(mainWindow);
        }),
        ["plugin-menu-language"] = new(SmokeStage.AfterCommandRefresh, mainWindow => PluginMenuLanguageSwitchChecks.RunAsync(mainWindow)),
        ["plugin-settings-language"] = new(SmokeStage.AfterCommandRefresh, mainWindow =>
        {
            PluginSettingsLanguageSwitchChecks.Run();
            return Task.CompletedTask;
        }),
        ["language-switch-footer"] = new(SmokeStage.AfterCommandRefresh, mainWindow => LanguageSwitchFooterChecks.RunAsync(mainWindow))
        {
            Configure = config => config.WorkspaceMode = "Advanced"
        },
        ["preview-languages"] = new(SmokeStage.AfterCommandRefresh, mainWindow => PreviewLanguagePackChecks.RunAsync(mainWindow))
        {
            Seed = profile => PreviewLanguagePackChecks.SeedAsync(profile) // The CJK captures read real archived threads.
        },
        ["window-chrome"] = new(SmokeStage.AfterCommandRefresh, async mainWindow =>
        {
            if (WindowChrome.Mode == WindowChromeMode.System)
            {
                await SecondaryWindowChromeChecks.RunSystemFallbackAsync(mainWindow);
                return;
            }
            await WindowChromeChecks.RunAsync(mainWindow);
            await SecondaryWindowChromeChecks.RunAsync(mainWindow);
        }),
        ["logo-theme"] = new(SmokeStage.Ready, mainWindow => LogoThemeChecks.RunAsync(mainWindow)),
        ["shortcut-presentation"] = new(SmokeStage.AfterCommandRefresh, mainWindow => ShortcutPresentationChecks.RunAsync(mainWindow)),
        ["poweruser"] = new(SmokeStage.AfterCommandRefresh, async mainWindow =>
        {
            await PowerUserCommandChecks.RunAsync(mainWindow);
            await ShortcutRebindingChecks.RunAsync(mainWindow);
            await RulesPageChecks.RunAsync(mainWindow);
        }),
        // Evidence run over the offline showcase archive (same fixture as work-views).
        ["ui-catalog"] = new(SmokeStage.AfterCommandRefresh, mainWindow => UiCatalogChecks.RunAsync(mainWindow))
        {
            App = AppBuilder.Configure<ShowcaseSmokeApp>,
            Seed = ShowcaseData.SeedAsync
        },
        // Offline showcase archive (no providers); downloads rows are passive synthetic snapshots.
        ["work-views"] = new(SmokeStage.AfterCommandRefresh, mainWindow => WorkViewCaptures.RunAsync(mainWindow))
        {
            App = AppBuilder.Configure<ShowcaseSmokeApp>,
            Seed = ShowcaseData.SeedAsync
        }
    };

    // Null when every Root/Branch entry has exactly one run and nothing else has one.
    internal static string? CheckRuns()
    {
        string[] expected = All.Where(scenario => scenario.Kind is SmokeScenarioKind.Root or SmokeScenarioKind.Branch)
            .Select(scenario => scenario.Id).ToArray();
        string[] missing = expected.Except(Runs.Keys, StringComparer.Ordinal).ToArray();
        string[] extra = Runs.Keys.Except(expected, StringComparer.Ordinal).ToArray();
        return missing.Length == 0 && extra.Length == 0 ? null
            : "scenario registry and runs disagree; without a run: " + string.Join(", ", missing)
                + "; run without a Root/Branch entry: " + string.Join(", ", extra);
    }

    private static Task SeedTwoVideosAsync(string profile) => VideoGalleryChecks.SeedAsync(profile,
        Environment.GetEnvironmentVariable("MEOWCHAN_VIDEO_FIXTURE")
            ?? throw new InvalidOperationException("Gallery audio/video fixture is required."), includeSecondVideo: true);
}

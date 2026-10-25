using MeowChan.Testing;
using System.Collections.Concurrent;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Logging;
using Avalonia.Media.Imaging;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan;
using MeowChan.Desktop;
using MeowChan.Downloader;
using MeowChan.Http;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using Serilog;

namespace MeowChan.DesktopSmoke;

internal static class Program
{
    private static bool _savedSearchesOnly;
    private static bool _checksPassed;
    private static bool _closed;
    private static Exception? _failure;
    private static bool _lockedProfileMode;
    private static bool _noTrayMode;
    private static bool _trayFailureMode;
    private static bool _downloadSessionMode;
    private static bool _trayLifecycleMode;
    private static bool _trayHostMode;
    private static bool _visualMode;
    private static bool _readerUxMode;
    private static bool _galleryCloseOnly;
    private static bool _selectionMode;
    private static bool _aboutLinksMode;
    private static bool _boardAdmissionMode;
    private static bool _readingPositionMode;
    private static bool _accessibilityMode;
    private static bool _windowGeometryMode;
    private static bool _videoContrastMode;
    private static bool _pluginsMode;
    private static bool _archiveCleanupMode;

    [STAThread]
    private static int Main(string[] args)
    {
        Environment.SetEnvironmentVariable("MEOWCHAN_DISABLE_UPDATE_CHECK", "1");
        _savedSearchesOnly = Environment.GetEnvironmentVariable("MEOWCHAN_SAVED_SEARCHES_ONLY") == "1";
        if (args.Length > 1 || (args.Length == 1 && args[0] is not ("--locked-profile" or "--no-tray" or "--tray-failure" or "--download-session" or "--tray-lifecycle" or "--visual" or "--reader-ux" or "--selection" or "--tray-host" or "--about-links" or "--board-admission" or "--reading-position" or "--accessibility-probe" or "--window-geometry" or "--video-contrast" or "--plugins" or "--archive-cleanup")))
        {
            Console.Error.WriteLine("Usage: MeowChan.DesktopSmoke [--locked-profile|--no-tray|--tray-failure|--download-session|--tray-lifecycle|--visual|--reader-ux|--selection|--tray-host|--about-links|--board-admission|--reading-position|--accessibility-probe|--window-geometry|--video-contrast|--plugins|--archive-cleanup]");
            return 2;
        }

        _readingPositionMode = args.Contains("--reading-position", StringComparer.Ordinal);
        _selectionMode = args.Contains("--selection", StringComparer.Ordinal);
        _aboutLinksMode = args.Contains("--about-links", StringComparer.Ordinal);
        _boardAdmissionMode = args.Contains("--board-admission", StringComparer.Ordinal);
        _accessibilityMode = args.Contains("--accessibility-probe", StringComparer.Ordinal);
        _windowGeometryMode = args.Contains("--window-geometry", StringComparer.Ordinal);
        if (_windowGeometryMode) Environment.SetEnvironmentVariable("MEOWCHAN_NATIVE_GEOMETRY_TRACE", "1");
        _archiveCleanupMode = args.Contains("--archive-cleanup", StringComparer.Ordinal);
        _pluginsMode = args.Contains("--plugins", StringComparer.Ordinal);
        _videoContrastMode = args.Contains("--video-contrast", StringComparer.Ordinal);
        if (_selectionMode) Environment.SetEnvironmentVariable("MEOWCHAN_FULLSCREEN_TRACE", "1");
        _readerUxMode = args.Contains("--reader-ux", StringComparer.Ordinal);
        _galleryCloseOnly = _readerUxMode && Environment.GetEnvironmentVariable("MEOWCHAN_GALLERY_CLOSE_ONLY") == "1";
        _visualMode = args.Contains("--visual", StringComparer.Ordinal);
        string? showcaseRoot = _visualMode ? Path.Combine(TemporaryWorkspacePaths.TempRoot, "MeowChan-showcase") : null;
        if (showcaseRoot != null && Directory.Exists(showcaseRoot))
            throw new IOException("Showcase directory already exists; choose a clean isolated run: " + showcaseRoot);
        string profile = showcaseRoot != null ? Path.Combine(showcaseRoot, "profile")
            : Path.Combine(TemporaryWorkspacePaths.TempRoot, "meowchan-desktop-smoke-" + Guid.NewGuid().ToString("N"));
        if (_readingPositionMode && Environment.GetEnvironmentVariable("MEOWCHAN_READING_TEST_PROFILE") is { Length: > 0 } readingProfile)
        {
            profile = Path.GetFullPath(readingProfile);
            if (Directory.Exists(profile) && !File.Exists(Path.Combine(profile, ".reading-smoke-profile")))
                throw new IOException("Reading smoke refuses an unmarked existing profile.");
            Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile, ".reading-smoke-profile"), "Disposable reading-position test profile");
        }
        if (_savedSearchesOnly)
        {
            string savedProfile = Environment.GetEnvironmentVariable("MEOWCHAN_SAVED_SEARCH_TEST_PROFILE")
                ?? throw new InvalidOperationException("Saved-search restart smoke requires an explicit disposable test profile.");
            profile = Path.GetFullPath(savedProfile);
            if (Directory.Exists(profile) && !File.Exists(Path.Combine(profile, ".saved-search-smoke-profile")))
                throw new IOException("Saved-search smoke refuses an unmarked existing profile.");
            Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile, ".saved-search-smoke-profile"), "Disposable saved-search acceptance profile");
        }
        Directory.CreateDirectory(profile);
        Environment.SetEnvironmentVariable("OCHAN_PROFILE", null);
        Environment.SetEnvironmentVariable("MEOWCHAN_PROFILE", null);
        Environment.SetEnvironmentVariable("MEOWCHAN_PROFILE", profile);
        _lockedProfileMode = args.Contains("--locked-profile", StringComparer.Ordinal);
        _noTrayMode = args.Contains("--no-tray", StringComparer.Ordinal);
        _trayFailureMode = args.Contains("--tray-failure", StringComparer.Ordinal);
        _downloadSessionMode = args.Contains("--download-session", StringComparer.Ordinal);
        _trayHostMode = args.Contains("--tray-host", StringComparer.Ordinal);
        _trayLifecycleMode = args.Contains("--tray-lifecycle", StringComparer.Ordinal);
        bool xvfbEnvironment = OperatingSystem.IsLinux()
            && Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") == "1";
        SmokeLogSink sink = new SmokeLogSink();
        Log.Logger = new LoggerConfiguration().MinimumLevel.Debug()
            .WriteTo.Console(outputTemplate: "SMOKE APP [{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        try
        {
            Config config = new Config
            {
                DownloadPath = Path.Combine(profile, "Downloads"),
                MinimizeToTray = _galleryCloseOnly || _noTrayMode || _trayFailureMode || _trayHostMode,
                CloseToTray = _noTrayMode || _trayFailureMode || _trayHostMode,
                DownloadsPaused = _selectionMode,
                SaveUrlsOnExit = true
            };
            if (_savedSearchesOnly)
            {
                config.WorkspaceMode = Environment.GetEnvironmentVariable("MEOWCHAN_SAVED_SEARCH_MODE") ?? "Advanced";
                config.Language = Environment.GetEnvironmentVariable("MEOWCHAN_SAVED_SEARCH_LANGUAGE") ?? "en";
                config.Theme = Environment.GetEnvironmentVariable("MEOWCHAN_SAVED_SEARCH_THEME") ?? "Dark";
                config.DownloadsPaused = true; config.SaveUrlsOnExit = false;
            }
            if (_galleryCloseOnly) config.WorkspaceMode = "Advanced";
            if (Environment.GetEnvironmentVariable("MEOWCHAN_MEDIA_RATINGS_ONLY") == "1")
                config.WorkspaceMode = Environment.GetEnvironmentVariable("MEOWCHAN_RATINGS_MODE") ?? "Advanced";
            if ((!_readingPositionMode && !_savedSearchesOnly) || !File.Exists(Path.Combine(profile, "settings.json"))) config.SaveConfig();
            if (_savedSearchesOnly) SavedArchiveSearchChecks.PrepareAsync(profile).GetAwaiter().GetResult();
            if (_readingPositionMode && !File.Exists(Path.Combine(profile, "reading-fixture-ready")))
            {
                ReaderUxChecks.SeedAsync(profile).GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(profile, "reading-fixture-ready"), "ready");
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_MEDIA_RATINGS_ONLY") == "1")
                MediaRatingFixture.SeedAsync(profile).GetAwaiter().GetResult();
            if (_visualMode) ShowcaseData.SeedAsync(profile).GetAwaiter().GetResult();
            if (_videoContrastMode) VideoContrastChecks.SeedAsync(profile).GetAwaiter().GetResult();
            if (_archiveCleanupMode) ArchiveCleanupChecks.SeedAsync(profile).GetAwaiter().GetResult();
            if (_selectionMode) ReaderUxChecks.SeedAsync(profile, deletionFixture: true).GetAwaiter().GetResult();
            if (_readerUxMode)
            {
                ReaderUxChecks.SeedAsync(profile).GetAwaiter().GetResult();
                if (Environment.GetEnvironmentVariable("MEOWCHAN_VIDEO_FIXTURE") is { Length: > 0 } fixture)
                    VideoGalleryChecks.SeedAsync(profile, fixture).GetAwaiter().GetResult();
            }
            using ProfileLease? conflictingLease = _lockedProfileMode ? ProfileLease.Acquire(profile) : null;
            Avalonia.Logging.Logger.Sink = sink;
            Console.WriteLine("SMOKE isolated profile: " + profile);
            AppBuilder appBuilder = (_downloadSessionMode ? AppBuilder.Configure<DownloadSessionSmokeApp>()
                : _visualMode ? AppBuilder.Configure<ShowcaseSmokeApp>() : AppBuilder.Configure<App>())
                .UsePlatformDetect()
                .WithInterFont();
            if (xvfbEnvironment)
            {
                Console.WriteLine("SMOKE isolated environment: " + (Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_KWIN") == "1" ? "headless KWin/XWayland" : "Xvfb/Openbox")
                    + "; X11 software rendering; tray fixture=" + (_trayHostMode ? "private StatusNotifier watcher/host" : "no native tray"));
                appBuilder.With(new X11PlatformOptions
                {
                    RenderingMode = new X11RenderingMode[] { X11RenderingMode.Software }
                });
            }
            int exitCode = appBuilder
                .AfterPlatformServicesSetup(builder =>
                {
                    if (!_lockedProfileMode)
                        ClipboardChecks.Install();
                    if ((xvfbEnvironment && !_trayHostMode) || _noTrayMode || _trayFailureMode)
                    {
                        IWindowingPlatform platform = AvaloniaLocator.Current.GetRequiredService<IWindowingPlatform>();
                        AvaloniaLocator.CurrentMutable.Bind<IWindowingPlatform>()
                            .ToConstant(new NoTrayPlatform(platform, _trayFailureMode));
                    }
                })
                .AfterSetup(builder => Dispatcher.UIThread.Post(async () => await RunAsync(), DispatcherPriority.Background))
                .StartWithClassicDesktopLifetime(Array.Empty<string>(),
                    _downloadSessionMode ? ShutdownMode.OnExplicitShutdown : ShutdownMode.OnMainWindowClose);

            // StartupErrorWindow acknowledges an initialization failure by shutting
            // down with 2. Only this scenario deliberately creates that failure;
            // its readable conflict dialog, Exit action and close are checked below.
            int expectedExitCode = _lockedProfileMode ? 2 : 0;
            if (exitCode != expectedExitCode || !_checksPassed || !_closed || _failure != null || !sink.Errors.IsEmpty)
            {
                Console.Error.WriteLine($"SMOKE FAILED: exit={exitCode}, checks={_checksPassed}, closed={_closed}, errors={sink.Errors.Count}; {_failure}");
                return 1;
            }

            if (!_lockedProfileMode)
            {
                using ProfileLease reacquired = ProfileLease.Acquire(profile);
                Console.WriteLine("SMOKE profile lease released after normal close.");
                if (Environment.GetEnvironmentVariable("MEOWCHAN_MEDIA_RATINGS_ONLY") == "1")
                    MediaRatingChecks.VerifyAfterClose(profile, reacquired);
            }

            string scenario = Environment.GetEnvironmentVariable("MEOWCHAN_NATIVE_IME_ONLY") == "1"
                ? "IME product checkpoints completed; real engine and UI acceptance require the independent native driver"
                : Environment.GetEnvironmentVariable("MEOWCHAN_SMOKE_ALPHA2_COPY_ONLY") == "1"
                ? "alpha2 copy: Sources granted/strict/null/inactive and Rules help at actual scale and minimum size"
                : Environment.GetEnvironmentVariable("MEOWCHAN_GALLERY_INTEGRITY_GUI_ONLY") == "1"
                ? "gallery integrity: four EN/DE Light/Dark cells at actual scale, exact binding, summary/context/details and geometry"
                : Environment.GetEnvironmentVariable("MEOWCHAN_SOURCE_INTEGRITY_GUI_ONLY") == "1"
                ? "source integrity: focused Sources/Plugins consent and Experimental fixture at the requested actual scale"
                : Environment.GetEnvironmentVariable("MEOWCHAN_RATINGS_LATE_ERROR_ONLY") == "1"
                ? "ratings: targeted direct/menu late failures and detached tile refresh; exact keys and store reopen"
                : Environment.GetEnvironmentVariable("MEOWCHAN_MEDIA_RATINGS_ONLY") == "1"
                ? Environment.GetEnvironmentVariable("MEOWCHAN_RATINGS_PRESENTATION_ONLY") == "1"
                    ? "ratings: 12-cell real presentation/focus/error matrix and unchanged durable values"
                    : "ratings: real controls, exact identities, comparison, retry, detached shortcut, gallery freeze, manager focus and store reopen"
                : _archiveCleanupMode ? "archive cleanup: private preview, explicit confirmation, real deletion, durable invariants and controlled failures"
                : _lockedProfileMode ? "locked profile: readable error, Exit closed the window"
                : _noTrayMode ? "no tray: user close kept taskbar access, Exit closed the window"
                : _trayFailureMode ? "tray creation failure: startup succeeded, user close kept taskbar access, Exit closed the window"
                : _downloadSessionMode ? "real session: URL admission, fixture media, assigned path, native close and restart restoration"
                : _trayHostMode ? "private StatusNotifier host: independent tray settings, native registration/activation, hide/restore and host/watcher loss"
                : _trayLifecycleMode ? "16 rapid native window/tray lifetimes, normal close and profile lease reuse"
                : _visualMode ? "actual release showcase with original offline demo content, no watches or HTTP"
                : _selectionMode ? "native watch/archive multi-selection, focus-safe commands, batch export return, watch-only Delete and board admission modal"
                : _accessibilityMode ? "native accessibility fixture stages completed; external AT-SPI/Orca results are separate"
                : _boardAdmissionMode ? "board admission: local rule clearing, multi-board validation, retry, cancel, EN/DE, themes and minimum dialog layout"
                : _readingPositionMode ? "local reading position, explicit process restart and settings semantics"
                : _aboutLinksMode ? "About project and PayPal links reached the selected private browser fixture with new-window requests, intact URLs and clean runtime paths"
                : _windowGeometryMode ? "isolated native interactive resize; each edge/corner after pointer release and settled correction"
                : _videoContrastMode ? "real decoded video time, ended and corrupt-media error contrast in Light/Dark"
                : "startup, rendered thread/board bindings, background update, normal close";
            Console.WriteLine("SMOKE PASS: " + scenario + "; no binding errors.");
            return 0;
        }
        finally
        {
            if (Application.Current is DownloadSessionSmokeApp fixtureApp)
                fixtureApp.DisposeFixture();
            Log.CloseAndFlush();
            if (_archiveCleanupMode) ArchiveCleanupChecks.Cleanup(profile);
            if (!_savedSearchesOnly && (!_readingPositionMode || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MEOWCHAN_READING_TEST_PROFILE"))))
                Directory.Delete(showcaseRoot ?? profile, recursive: true);
        }
    }

    private static async Task RunAsync()
    {
        Window? window = null;
        try
        {
            IClassicDesktopStyleApplicationLifetime desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime
                ?? throw new InvalidOperationException("Desktop lifetime was not initialized.");
            window = desktop.MainWindow ?? throw new InvalidOperationException("No startup window was created.");
            window.Closing += (sender, e) => Console.WriteLine(
                $"SMOKE MainWindow.Closing observed: reason={e.CloseReason}, cancel at event={e.Cancel}, owned windows={window.OwnedWindows.Count}, enabled={window.IsEnabled}.");
            window.Closed += (sender, e) =>
            {
                _closed = true;
                Console.WriteLine("SMOKE MainWindow.Closed observed.");
            };
            await WaitUntilAsync(() => window.IsVisible && window.Bounds.Width > 0);
            Console.WriteLine("SMOKE render scaling: actual=" + window.RenderScaling.ToString("0.###", CultureInfo.InvariantCulture)
                + "; requested=" + (Environment.GetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR") ?? "unset") + ";");
            await CheckDesktopFitAsync(window);
            if (_lockedProfileMode)
            {
                if (window is not StartupErrorWindow)
                    throw new InvalidOperationException("A conflicting profile did not produce StartupErrorWindow.");
                await WaitUntilAsync(() => window.GetVisualDescendants().OfType<TextBlock>()
                    .Any(block => block.IsVisible && block.Text?.Contains("Another MeowChan instance", StringComparison.Ordinal) == true));
                CaptureWindow(window, "smoke-startup-error.png");
                Button exit = window.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "Exit");
                _checksPassed = true;
                exit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                return;
            }

            MainWindow mainWindow = window as MainWindow ?? throw new InvalidOperationException("MainWindow was not created.");
            // StartAsync returns the session's existing startup task, including plugin bootstrap.
            // Only the subsequent command-state propagation belongs to the five-second UI poll.
            Console.WriteLine("SMOKE awaiting desktop session startup; state=" + mainWindow.SessionHost.State + ".");
            await mainWindow.SessionHost.StartAsync();
            if (mainWindow.SessionHost.State != DesktopSessionState.Ready)
                throw new InvalidOperationException("Desktop session startup did not reach Ready: state="
                    + mainWindow.SessionHost.State + "; error=" + (mainWindow.SessionHost.StartupError ?? "no startup error recorded"));
            Console.WriteLine("SMOKE desktop session startup completed; state=Ready.");
            await WaitUntilAsync(() => mainWindow.FindControl<Button>("AddUrlButton")!.IsEnabled);
            if (mainWindow.UrlList.Count != 0 || mainWindow.BoardsList.Count != 0)
                throw new InvalidOperationException("The isolated session did not start empty.");
            Console.WriteLine("SMOKE MainWindow visible with empty isolated session.");
            if (Environment.GetEnvironmentVariable("MEOWCHAN_NATIVE_IME_ONLY") == "1")
            {
                await NativeImeChecks.RunAsync(mainWindow);
                _checksPassed = true; mainWindow.Close(); return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_GALLERY_INTEGRITY_GUI_ONLY") == "1")
            {
                await GalleryIntegrityChecks.RunAsync(mainWindow);
                _checksPassed = true; mainWindow.Close(); return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_SMOKE_ALPHA2_COPY_ONLY") == "1")
            {
                await Alpha2CopyChecks.RunAsync(mainWindow);
                _checksPassed = true; mainWindow.Close(); return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_SOURCE_FILTER_WAVE_ONLY") == "1")
            {
                await SourceFilterWaveChecks.RunAsync(mainWindow);
                _checksPassed = true; mainWindow.Close(); return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_SOURCE_INTEGRITY_GUI_ONLY") == "1")
            {
                await SourceIntegrityChecks.RunAsync(mainWindow);
                _checksPassed = true; mainWindow.Close(); return;
            }
            if (_archiveCleanupMode)
            {
                await ArchiveCleanupChecks.RunAsync(mainWindow);
                _checksPassed = true; mainWindow.Close(); return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_SUGGESTIONS_ONLY") == "1")
            {
                await SuggestionsPresentationChecks.RunAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_URSA_NATIVE_SUITE") == "groups")
            {
                await ReaderGroupChecks.RunAsync(mainWindow);
                _checksPassed = true; mainWindow.Close(); return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_MEDIA_FIT_BENCHMARK") == "1")
            {
                GalleryImageDecoder.DecodeStarted = request => Console.WriteLine("MEDIA_DECODE " + request.GetType().Name);
                try { await GalleryFitBenchmark.RunAsync(mainWindow); }
                finally { GalleryImageDecoder.DecodeStarted = null; }
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_MEDIA_RATINGS_ONLY") == "1")
            {
                await MediaRatingChecks.RunAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_SECTION_COMPARISON_ONLY") == "1")
            {
                await SectionComparisonChecks.RunAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            CommandRefreshChecks.Run();
            if (Environment.GetEnvironmentVariable("MEOWCHAN_TEMPLATE_COMPARISON") == "1")
            {
                await TemplateComparisonChecks.RunAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_ARCHIVE_COMPARISON") == "1")
            {
                await ArchiveComparisonChecks.RunAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (_savedSearchesOnly)
            {
                await SavedArchiveSearchChecks.RunAsync(mainWindow, Config.GetConfigDirectory());
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_FOOTER_NAVIGATION_ONLY") == "1")
            {
                await FooterNavigationChecks.RunAsync(mainWindow);
                await BrandNavigationChecks.RunAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_PICKER_LIFETIME_ONLY") == "1")
            {
                await SettingsPickerLifetimeChecks.RunAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_SETTINGS_LIFETIME_ONLY") == "1")
            {
                await SettingsPageChecks.RunLateSaveFailureAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_PATH_INPUT_ONLY") == "1")
            {
                await SettingsPathInputChecks.RunAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_NUMERIC_FOCUS_ONLY") == "1")
            {
                await SettingsAccessibilityChecks.RunAsync(mainWindow);
                await SettingsChecks.RunAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_MEDIA_PERFORMANCE_ONLY") == "1")
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
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_MEDIA_INTEGRATION_ONLY") == "1")
            {
                await MediaTileIntegrationChecks.RunAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_GALLERY_THEME_ONLY") == "1")
            {
                await GalleryViewerChecks.RunAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_GALLERY_FIT_ONLY") == "1")
            {
                await GalleryFitChecks.RunAsync(mainWindow);
                await GalleryViewerChecks.RunAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_MEDIA_PANEL_ONLY") == "1")
            {
                await MediaTilePanelChecks.RunAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_DIAGNOSTIC_FEEDBACK_ONLY") == "1")
            {
                await DiagnosticsFeedbackChecks.RunAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_PAGE_REFRESH_ONLY") == "1")
            {
                await DiagnosticsChecks.RunAsync(mainWindow);
                await SourcesChecks.RunAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_POST_BOOKMARK_TRANSITIONS_ONLY") == "1")
            {
                await PostBookmarkTransitionChecks.RunAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_POST_BOOKMARK_LOCALE_ONLY") == "1")
            {
                await PostBookmarkLocaleChecks.RunAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_POST_BOOKMARK_COMPARISON_ONLY") == "1")
            {
                await PostBookmarkComparisonChecks.RunAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_POST_BOOKMARKS_ONLY") == "1")
            {
                await PostBookmarkChecks.RunAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_COMMAND_CONTRAST_ONLY") == "1")
            {
                await CommandButtonContrastChecks.RunAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_ACTION_DYNAMIC_ONLY") == "1")
            {
                await ActionToolBarDynamicChecks.RunAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_ACTION_OVERFLOW_ONLY") == "1")
            {
                await ActionToolBarPresentationChecks.RunAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_DOWNLOAD_SEARCH_ONLY") == "1")
            {
                await DownloadSearchChecks.RunAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_SETTINGS_FOOTER_ONLY") == "1")
            {
                await SettingsPageChecks.RunFooterOverlapDiagnosticAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_UI_REFINEMENT_ONLY") == "1")
            {
                await ReaderLiveChecks.RunAsync(mainWindow);
                await ReaderRepliesChecks.RunAsync(mainWindow);
                await SettingsPageChecks.RunAsync(mainWindow);
                await DiagnosticsChecks.RunAsync(mainWindow);
                await SourcesChecks.RunAsync(mainWindow);
                await RulesPageChecks.RunAsync(mainWindow);
                await SettingsChecks.RunAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (_windowGeometryMode)
            {
                await NativeWindowMinimumChecks.RunAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_SHORTCUT_PRESENTATION_ONLY") == "1")
            {
                await ShortcutPresentationChecks.RunAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_POWERUSER_ONLY") == "1")
            {
                await PowerUserCommandChecks.RunAsync(mainWindow);
                await ShortcutRebindingChecks.RunAsync(mainWindow);
                await RulesPageChecks.RunAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (_readingPositionMode)
            {
                await ReadingPositionChecks.RunAsync(mainWindow, profile: Config.GetConfigDirectory());
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (_pluginsMode)
            {
                await PluginManagementChecks.RunAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (_videoContrastMode)
            {
                await VideoContrastChecks.RunAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (_aboutLinksMode)
            {
                await AboutLinkChecks.RunAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (_accessibilityMode)
            {
                await AccessibilityNativeChecks.RunAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (_boardAdmissionMode)
            {
                await BoardAdmissionChecks.RunAsync(mainWindow);
                await BoardAdmissionContractChecks.CheckPresentationMatrixAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (_selectionMode)
            {
                await ShellPresentationChecks.RunAsync(mainWindow);
                await SelectionChecks.RunAsync(mainWindow);
                await BoardAdmissionChecks.RunAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (_readerUxMode)
            {
                bool videoFixture = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MEOWCHAN_VIDEO_FIXTURE"));
                if (!_galleryCloseOnly)
                {
                    await WindowMinimumChecks.RunAsync(mainWindow);
                    await ReaderUxChecks.RunAsync(mainWindow);
                    if (videoFixture) await VideoGalleryChecks.RunAsync(mainWindow);
                }
                await GalleryCloseChecks.RunAsync(mainWindow, videoFixture,
                    () => _checksPassed = true, () => _closed);
                return;
            }
            if (_visualMode)
            {
                await ComparisonCaptures.RunAsync(mainWindow, visualReview: true);
                await ShowcaseCaptures.RunAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (_trayHostMode)
            {
                await TrayHostChecks.RunAsync(mainWindow);
                _checksPassed = true;
                // Exit, rather than the close-to-tray path.
                mainWindow.FindControl<MenuItem>("ExitMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                return;
            }
            if (_trayLifecycleMode)
            {
                await TrayLifecycleChecks.RunAsync(mainWindow);
                _checksPassed = true;
                mainWindow.Close();
                return;
            }
            if (_downloadSessionMode)
            {
                await DownloadSessionChecks.RunAsync(mainWindow, desktop);
                _checksPassed = true;
                desktop.Shutdown();
                return;
            }
            if (_noTrayMode || _trayFailureMode)
            {
                mainWindow.FindControl<Button>("WindowMinimizeButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => mainWindow.WindowState == WindowState.Minimized);
                if (!mainWindow.IsVisible || !mainWindow.ShowInTaskbar || _closed)
                    throw new InvalidOperationException("Minimize without a tray stranded or closed the window.");
                mainWindow.WindowState = WindowState.Normal;
                mainWindow.Activate();
                await WaitUntilAsync(() => mainWindow.WindowState == WindowState.Normal);
                Func<WindowCloseReason, bool> requestClose = mainWindow.PlatformImpl?.Closing
                    ?? throw new InvalidOperationException("The native close callback is missing.");
                bool cancelled = requestClose(WindowCloseReason.WindowClosing);
                await WaitUntilAsync(() => mainWindow.WindowState == WindowState.Minimized);
                if (!cancelled || !mainWindow.IsVisible || !mainWindow.ShowInTaskbar || _closed)
                    throw new InvalidOperationException("Closing without a tray stranded or closed the window.");
                Console.WriteLine("SMOKE native user close kept the window minimized and accessible in the taskbar.");
                mainWindow.WindowState = WindowState.Normal;
                mainWindow.Activate();
                await WaitUntilAsync(() => mainWindow.IsVisible && mainWindow.WindowState == WindowState.Normal);
                mainWindow.FindControl<Button>("WindowCloseButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => mainWindow.WindowState == WindowState.Minimized);
                if (!mainWindow.IsVisible || !mainWindow.ShowInTaskbar || _closed)
                    throw new InvalidOperationException("The custom close caption bypassed CloseToTray.");
                mainWindow.WindowState = WindowState.Normal;
                mainWindow.Activate();
                await WaitUntilAsync(() => mainWindow.IsVisible && mainWindow.WindowState == WindowState.Normal);
                MenuItem exit = mainWindow.FindControl<MenuItem>("ExitMenuItem")
                    ?? throw new InvalidOperationException("The window Exit menu item is missing.");
                MenuItem applicationMenu = mainWindow.FindControl<MenuItem>("WorkspaceMenuItem")
                    ?? throw new InvalidOperationException("The application menu is missing.");
                applicationMenu.IsSubMenuOpen = true;
                await WaitUntilAsync(() => exit.IsEffectivelyVisible && exit.IsEffectivelyEnabled);
                CaptureWindow(mainWindow, _noTrayMode ? "smoke-no-tray.png" : "smoke-tray-failure.png");
                _checksPassed = true;
                exit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                return;
            }

            await MediaRetryChecks.RunAsync(mainWindow);
            await ComparisonCaptures.RunAsync(mainWindow);
            await ClipboardChecks.RunAsync(mainWindow);
            await PresentationChecks.RunPreferencesAsync(mainWindow);
            await BrandNavigationChecks.RunAsync(mainWindow);
            await ReaderLiveChecks.RunAsync(mainWindow);
            await ReaderRepliesChecks.RunAsync(mainWindow);
            await PostBookmarkChecks.RunAsync(mainWindow);

            DesktopThreadEntry thread = SnapshotFixtures.Thread(123, "123", "Desktop smoke initial title");
            DesktopBoardEntry board = SnapshotFixtures.Board(124);
            using ThreadRow threadRow = new ThreadRow(thread);
            using BoardRow boardRow = new BoardRow(board);
            mainWindow.UrlList.Add(threadRow);
            mainWindow.BoardsList.Add(boardRow);
            await WaitUntilAsync(() => VisibleText(window, thread.Snapshot.Title));
            Console.WriteLine("SMOKE thread title binding rendered.");

            bool wrongThread = false;
            threadRow.PropertyChanged += (sender, args) => wrongThread |= !Dispatcher.UIThread.CheckAccess();
            await Task.Run(() =>
            {
                for (int index = 0; index < 1000; index++)
                {
                    DesktopThreadEntry update = thread with { Snapshot = thread.Snapshot with { Title = "Desktop smoke burst " + index } };
                    Dispatcher.UIThread.Post(() => threadRow.UpdateSnapshot(update));
                }
                DesktopThreadEntry final = thread with { Snapshot = thread.Snapshot with { Title = "Desktop smoke updated title" } };
                Dispatcher.UIThread.Post(() => threadRow.UpdateSnapshot(final));
            });
            await WaitUntilAsync(() => VisibleText(window, "Desktop smoke updated title"));
            if (wrongThread)
                throw new InvalidOperationException("A passive row notified bindings outside the desktop dispatcher.");
            Console.WriteLine("SMOKE dispatcher-projected snapshot burst retained its final value.");
            thread = thread with
            {
                Snapshot = thread.Snapshot with
                {
                    Title = "Desktop smoke updated title",
                    Activity = ThreadActivity.Downloading,
                    RequestProgress = new RequestPolicyProgress(RequestPolicyPhase.WaitingForAdmission, null, 0)
                }
            };
            threadRow.UpdateSnapshot(thread);
            await WaitUntilAsync(() => VisibleText(window, "Downloading · Waiting for request slot"));
            thread = thread with { Snapshot = thread.Snapshot with { RequestProgress = new RequestPolicyProgress(RequestPolicyPhase.Cooldown, TimeSpan.FromSeconds(30), 1) } };
            threadRow.UpdateSnapshot(thread);
            await WaitUntilAsync(() => VisibleText(window, "Downloading · Request cooldown (30 s)"));
            thread = thread with { Snapshot = thread.Snapshot with { RequestProgress = null } };
            threadRow.UpdateSnapshot(thread);
            await WaitUntilAsync(() => VisibleText(window, "Downloading"));
            threadRow.UpdateSnapshot(thread with
            {
                Snapshot = thread.Snapshot with
                {
                    Downloads = new DownloadCounts(3, 2),
                    Activity = ThreadActivity.Finished,
                    LastCheckFailure = new FailureInfo(FailureKind.Timeout, "Smoke retained check failure")
                }
            });
            if (threadRow.Status != "Check failed · Finished" || threadRow.Queued != 3 || threadRow.Active != 2
                || threadRow.ErrorDetails != "Smoke retained check failure")
                throw new InvalidOperationException("Typed queue counts or retained check failures were not projected.");
            Console.WriteLine("SMOKE request waiting/backoff rendered; typed queue counts and retained check failure projected.");
            await PresentationChecks.CheckDetailsAsync(mainWindow, threadRow);
            thread = thread with
            {
                Snapshot = thread.Snapshot with { SourceState = ThreadSourceState.Archived, Activity = ThreadActivity.Finished }
            };
            threadRow.UpdateSnapshot(thread);
            await WaitUntilAsync(() => VisibleText(window, "Archived · Finished"));
            Console.WriteLine("SMOKE terminal source state remained visible after media status changed.");
            CaptureWindow(window, "smoke-mainwindow.png");

            TabControl tabs = window.FindControl<TabControl>("DownloadsTabs")!;
            tabs.SelectedIndex = 1;
            await WaitUntilAsync(() => VisibleText(window, boardRow.NiceName));
            boardRow.UpdateSnapshot(board with
            {
                Snapshot = board.Snapshot with { RequestProgress = new RequestPolicyProgress(RequestPolicyPhase.Cooldown, TimeSpan.FromSeconds(12), 1) }
            });
            await WaitUntilAsync(() => VisibleText(window, "Request cooldown (12 s)"));
            await PresentationChecks.CheckBoardDetailsAsync(mainWindow, boardRow);
            boardRow.UpdateSnapshot(board);
            await WaitUntilAsync(() => VisibleText(window, "Monitoring"));
            Console.WriteLine("SMOKE board binding and request cooldown rendered.");

            string finalTitle = threadRow.Title;
            threadRow.Dispose();
            threadRow.UpdateSnapshot(thread with { Snapshot = thread.Snapshot with { Title = "Disposed row must not update" } });
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (threadRow.Title != finalTitle)
                throw new InvalidOperationException("A disposed row still accepted updates.");

            mainWindow.UrlList.Remove(threadRow);
            mainWindow.BoardsList.Remove(boardRow);
            await GridChecks.RunAsync(mainWindow);
            await SettingsPageChecks.RunAsync(mainWindow);
            await DiagnosticsChecks.RunAsync(mainWindow);
            await SourcesChecks.RunAsync(mainWindow);
            await BoardAdmissionChecks.RunAsync(mainWindow);
            await PauseChecks.RunAsync(mainWindow);
            await RulesPageChecks.RunAsync(mainWindow);
            await SettingsChecks.RunAsync(mainWindow);
            Console.WriteLine($"SMOKE after settings: owned windows={mainWindow.OwnedWindows.Count}, enabled={mainWindow.IsEnabled}, effectively enabled={mainWindow.IsEffectivelyEnabled}.");
            foreach (Window ownedWindow in mainWindow.OwnedWindows)
            {
                Console.WriteLine($"SMOKE remaining owned window: type={ownedWindow.GetType().Name}, visible={ownedWindow.IsVisible}, enabled={ownedWindow.IsEnabled}.");
            }
            _checksPassed = true;
            Console.WriteLine("SMOKE requesting close through the custom window caption.");
            mainWindow.FindControl<Button>("WindowCloseButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Console.WriteLine($"SMOKE caption close returned: closed={_closed}, visible={window.IsVisible}.");
        }
        catch (Exception exception)
        {
            _failure = exception;
            Console.Error.WriteLine("SMOKE check failed: " + exception);
            if ((_downloadSessionMode || _galleryCloseOnly)
                && Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime fixtureDesktop)
                fixtureDesktop.Shutdown(1);
            else if (window is MainWindow mainWindow && mainWindow.FindControl<MenuItem>("ExitMenuItem") is MenuItem exit)
                exit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            else if (window != null) window.Close();
            else if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown(1);
        }
    }

    private static bool VisibleText(Window window, string text) => window.GetVisualDescendants()
        .OfType<TextBlock>().Any(block => block.IsEffectivelyVisible && block.Text == text);

    private static async Task CheckDesktopFitAsync(Window window)
    {
        try
        {
            // Native window managers can clamp the requested startup size after Show.
            // Check physical pixels rather than comparing DIP bounds to screen pixels.
            await WaitUntilAsync(() =>
            {
                Screen? screen = window.Screens.ScreenFromWindow(window);
                if (screen == null || window.Bounds.Width <= 0 || window.Bounds.Height <= 0)
                    return false;
                double scale = window.RenderScaling;
                bool clientFits = window.Bounds.Width * scale <= screen.WorkingArea.Width + 1
                    && window.Bounds.Height * scale <= screen.WorkingArea.Height + 1;
                bool frameFits = window.FrameSize is not Size frame
                    || (frame.Width * scale <= screen.WorkingArea.Width + 1
                        && frame.Height * scale <= screen.WorkingArea.Height + 1);
                return clientFits && frameFits;
            });
        }
        finally
        {
            Screen? screen = window.Screens.ScreenFromWindow(window);
            Console.WriteLine("SMOKE desktop geometry: working-area=" + screen?.WorkingArea
                + "; window-position=" + window.Position + "; window-bounds=" + window.Bounds
                + "; frame-size=" + window.FrameSize + "; scale=" + window.RenderScaling
                + "; state=" + window.WindowState + ";");
        }
        Console.WriteLine("SMOKE desktop fit: client dimensions and reported native frame fit the working area (1 physical pixel rounding tolerance); screenshots exclude native decorations.");
    }

    internal static void CaptureWindow(Window window, string fileName)
    {
        window.UpdateLayout();
        string screenshotPath = Path.Combine(AppContext.BaseDirectory, fileName);
        double scale = window.RenderScaling;
        using RenderTargetBitmap screenshot = new RenderTargetBitmap(
            new PixelSize((int)Math.Ceiling(window.Bounds.Width * scale), (int)Math.Ceiling(window.Bounds.Height * scale)),
            new Vector(96 * scale, 96 * scale));
        screenshot.Render(window);
        screenshot.Save(screenshotPath, PngBitmapEncoderOptions.Default);
        Console.WriteLine("SMOKE window render: " + screenshotPath);
        Console.WriteLine("SMOKE capture metadata: " + System.Text.Json.JsonSerializer.Serialize(new
        {
            fileName, mode = Config.LoadConfig().WorkspaceMode, theme = window.ActualThemeVariant.ToString(),
            language = MeowChan.Desktop.Localization.Texts.Language, renderScaling = scale,
            clientDip = window.ClientSize.ToString(), boundsDip = window.Bounds.ToString(),
            imagePixels = screenshot.PixelSize.ToString(), window = window.GetType().Name
        }));
    }

    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Desktop state did not become ready within five seconds.");
            await Task.Delay(50);
        }
    }

    // Replace only tray creation; windows, rendering and the desktop lifetime stay native.
    private sealed class NoTrayPlatform : IWindowingPlatform
    {
        private readonly IWindowingPlatform _inner;
        private readonly bool _throwOnTrayCreation;

        public NoTrayPlatform(IWindowingPlatform inner, bool throwOnTrayCreation)
        {
            _inner = inner;
            _throwOnTrayCreation = throwOnTrayCreation;
        }

        public IWindowImpl CreateWindow() => _inner.CreateWindow();
        public void GetWindowsZOrder(ReadOnlySpan<IWindowImpl> windows, Span<long> zOrder) => _inner.GetWindowsZOrder(windows, zOrder);
        public ITopLevelImpl CreateEmbeddableTopLevel() => _inner.CreateEmbeddableTopLevel();
        public IWindowImpl CreateEmbeddableWindow() => _inner.CreateEmbeddableWindow();

        public ITrayIconImpl? CreateTrayIcon()
        {
            if (_throwOnTrayCreation)
                throw new InvalidOperationException("Smoke tray creation failure.");
            return null;
        }
    }

    private sealed class SmokeLogSink : ILogSink
    {
        public ConcurrentQueue<string> Errors { get; } = new ConcurrentQueue<string>();

        public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
            => Record(level, area, messageTemplate);

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
            => Record(level, area, messageTemplate + " " + string.Join(", ", propertyValues));

        private void Record(LogEventLevel level, string area, string message)
        {
            string line = $"AVALONIA [{level}] [{area}] {message}";
            Console.WriteLine(line);
            if (level >= LogEventLevel.Error || area == "Binding") Errors.Enqueue(line);
        }
    }
}

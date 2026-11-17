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
    // --locked-profile with MEOWCHAN_LOCKED_PROFILE_CAUSE=damaged-settings: instead of a conflicting lease, settings.json is
    // damaged next to a good backup, and the startup-error window must explain the situation in the saved language.
    private static bool _damagedSettingsCause;
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
    private static bool _shellLoadMode;
    // The selected Root/Branch scenario's run; null for the command-line modes and the normal scene.
    private static SmokeRun? _run;

    [STAThread]
    private static int Main(string[] args)
    {
        long entryTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        DateTimeOffset entryUtc = DateTimeOffset.UtcNow;
        if (args.Length == 1 && args[0] == "--startup-timing-contract")
        {
            string? error = SmokeSelectors.Check([], Environment.GetEnvironmentVariables()
                .Cast<System.Collections.DictionaryEntry>()
                .Select(entry => KeyValuePair.Create((string)entry.Key, entry.Value as string)),
                OperatingSystem.IsWindows(), out string scenario);
            if (error != null || scenario != "normal")
            {
                Console.Error.WriteLine("SMOKE CONFIGURATION ERROR: contract check requires no active selectors: " + (error ?? scenario));
                return 2;
            }
            StartupFirstFrameChecks.VerifyContracts();
            Console.WriteLine("Startup timing and selector contracts passed (no GUI).");
            return 0;
        }
        Environment.SetEnvironmentVariable("MEOWCHAN_DISABLE_UPDATE_CHECK", "1");
        // A run with an unknown, broken or second scenario must fail BEFORE a profile or window
        // exists: SmokeSelectors.Check below rejects every selector combination with more than one
        // owner, so a run never reports another helper's acceptance wording.
        string? registryError = SmokeScenarios.CheckRuns();
        if (registryError != null)
        {
            Console.Error.WriteLine("SMOKE CONFIGURATION ERROR: " + registryError);
            return 2;
        }
        // One JSON line per registry entry with its standalone run (scripts/run-desktop-smoke.py --registry);
        // no profile or window. error: the invocation SmokeSelectors.Check would reject, which the runner reports as failed.
        if (args is ["--list-scenarios"])
        {
            foreach (SmokeScenario entry in SmokeScenarios.All)
            {
                (string[] invocation, string[] environment, string? skip) = SmokeScenarios.Invocation(entry);
                string? invalid = SmokeSelectors.Check(invocation, environment.Select(pair => pair.Split('=', 2))
                    .Select(pair => KeyValuePair.Create(pair[0], (string?)pair[1])), false, out string expected);
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    id = entry.Id, kind = entry.Kind.ToString(), mode = invocation.Length == 0 ? SmokeSelectors.NormalMode : invocation[0][2..],
                    environment, scenario = expected, skip, error = invalid
                }));
            }
            return 0;
        }
        _savedSearchesOnly = Environment.GetEnvironmentVariable("MEOWCHAN_SAVED_SEARCHES_ONLY") == "1";
        string[] modes = SmokeScenarios.All.Where(entry => entry.Kind == SmokeScenarioKind.Mode && entry.Selector.Length != 0)
            .Select(entry => entry.Selector).ToArray();
        if (args.Length > 1 || (args.Length == 1 && !modes.Contains(args[0], StringComparer.Ordinal)))
        {
            Console.Error.WriteLine("Usage: MeowChan.DesktopSmoke [" + string.Join("|", modes) + "]");
            return 2;
        }
        // Unknown, misspelled or misplaced selectors would otherwise silently run the normal scene.
        string? selectorError = SmokeSelectors.Check(args, Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Select(entry => KeyValuePair.Create((string)entry.Key, entry.Value as string)),
            OperatingSystem.IsWindows(), out string selectorScenario, out SmokeScenario? selected);
        if (selectorError != null || selected == null)
        {
            Console.Error.WriteLine("SMOKE CONFIGURATION ERROR: " + (selectorError ?? "no registered scenario for " + selectorScenario));
            return 2;
        }
        _run = SmokeScenarios.Runs.GetValueOrDefault(selected.Id);

        if (_run?.Precondition?.Invoke() is string unavailable)
        {
            Console.Error.WriteLine("SMOKE CONFIGURATION ERROR: " + unavailable);
            return 2;
        }
        using StartupFirstFrameMeasurement? startupTiming = _run?.Measure?.Invoke(entryTimestamp, entryUtc);

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
        if (args.Contains("--shell-load", StringComparer.Ordinal)
            && Environment.GetEnvironmentVariable("MEOWCHAN_SHELL_LOAD_PRIVATE_PROFILE") is { Length: > 0 } loadProfile)
        {
            string owned = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "profile"));
            if (Path.GetFullPath(loadProfile) != owned) throw new IOException("Shell-load profile must belong to its private runner working directory.");
            profile = owned;
        }
        if (_readingPositionMode && Environment.GetEnvironmentVariable("MEOWCHAN_READING_TEST_PROFILE") is { Length: > 0 } readingProfile)
        {
            profile = Path.GetFullPath(readingProfile);
            if (Directory.Exists(profile) && !File.Exists(Path.Combine(profile, ".reading-smoke-profile")))
                throw new IOException("Reading smoke refuses an unmarked existing profile.");
            Directory.CreateDirectory(profile);
        }
        if (_savedSearchesOnly)
        {
            string savedProfile = Environment.GetEnvironmentVariable("MEOWCHAN_SAVED_SEARCH_TEST_PROFILE")
                ?? throw new InvalidOperationException("Saved-search restart smoke requires an explicit disposable test profile.");
            profile = Path.GetFullPath(savedProfile);
            if (Directory.Exists(profile) && !File.Exists(Path.Combine(profile, ".saved-search-smoke-profile")))
                throw new IOException("Saved-search smoke refuses an unmarked existing profile.");
            Directory.CreateDirectory(profile);
        }
        // Startup timing only: an explicit harness-owned profile kept for a later warm-start process.
        string? keptStartupProfile = StartupFirstFrameChecks.ResolveKeptProfile(startupTiming != null);
        if (keptStartupProfile != null) profile = keptStartupProfile;
        Directory.CreateDirectory(profile);
        Environment.SetEnvironmentVariable("OCHAN_PROFILE", null);
        Environment.SetEnvironmentVariable("MEOWCHAN_PROFILE", null);
        Environment.SetEnvironmentVariable("MEOWCHAN_PROFILE", profile);
        _lockedProfileMode = args.Contains("--locked-profile", StringComparer.Ordinal);
        _damagedSettingsCause = _lockedProfileMode
            && Environment.GetEnvironmentVariable("MEOWCHAN_LOCKED_PROFILE_CAUSE") == "damaged-settings";
        _noTrayMode = args.Contains("--no-tray", StringComparer.Ordinal);
        _trayFailureMode = args.Contains("--tray-failure", StringComparer.Ordinal);
        _downloadSessionMode = args.Contains("--download-session", StringComparer.Ordinal);
        _shellLoadMode = args.Contains("--shell-load", StringComparer.Ordinal);
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
                // The scenarios were written against the compact view; a real new profile now starts in Simple.
                WorkspaceMode = "Barebones",
                MinimizeToTray = _galleryCloseOnly || _noTrayMode || _trayFailureMode || _trayHostMode,
                CloseToTray = _noTrayMode || _trayFailureMode || _trayHostMode,
                DownloadsPaused = _selectionMode || (_shellLoadMode && ShellLoadFixture.Scenario == "paused"),
                SaveUrlsOnExit = true
            };
            if (_savedSearchesOnly)
            {
                config.WorkspaceMode = Environment.GetEnvironmentVariable("MEOWCHAN_SAVED_SEARCH_MODE") ?? "Advanced";
                config.Language = Environment.GetEnvironmentVariable("MEOWCHAN_SAVED_SEARCH_LANGUAGE") ?? "en";
                config.Theme = Environment.GetEnvironmentVariable("MEOWCHAN_SAVED_SEARCH_THEME") ?? "Dark";
                config.DownloadsPaused = true; config.SaveUrlsOnExit = false;
            }
            if (_galleryCloseOnly)
                config.WorkspaceMode = "Advanced";
            _run?.Configure?.Invoke(config);
            if (_damagedSettingsCause) config.Language = Environment.GetEnvironmentVariable("MEOWCHAN_LOCKED_PROFILE_LANGUAGE") ?? "en";
            if ((!_readingPositionMode && !_savedSearchesOnly && keptStartupProfile == null) || !File.Exists(Path.Combine(profile, "settings.json"))) config.SaveConfig();
            if (_damagedSettingsCause)
            {
                // A second save leaves the first generation as settings.json.bak; the primary then loses its second half.
                config.SaveConfig();
                string primary = Path.Combine(profile, "settings.json");
                byte[] good = File.ReadAllBytes(primary);
                File.WriteAllBytes(primary, good[..(good.Length / 2)]);
            }
            // Marker files are data too: initialize an owned empty profile first.
            if (_readingPositionMode && Environment.GetEnvironmentVariable("MEOWCHAN_READING_TEST_PROFILE") is { Length: > 0 })
                File.WriteAllText(Path.Combine(profile, ".reading-smoke-profile"), "Disposable reading-position test profile");
            if (_savedSearchesOnly)
                File.WriteAllText(Path.Combine(profile, ".saved-search-smoke-profile"), "Disposable saved-search acceptance profile");
            if (_savedSearchesOnly) SavedArchiveSearchChecks.PrepareAsync(profile).GetAwaiter().GetResult();
            if (_readingPositionMode && !File.Exists(Path.Combine(profile, "reading-fixture-ready")))
            {
                ReaderUxChecks.SeedAsync(profile).GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(profile, "reading-fixture-ready"), "ready");
            }
            if (_shellLoadMode) ShellLoadFixture.PrepareAsync(profile).GetAwaiter().GetResult();
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
            if (_run?.Seed != null) _run.Seed(profile).GetAwaiter().GetResult();
            using ProfileLease? conflictingLease = _lockedProfileMode && !_damagedSettingsCause ? ProfileLease.Acquire(profile) : null;
            Avalonia.Logging.Logger.Sink = sink;
            Console.WriteLine("SMOKE isolated profile: " + profile);
            AppBuilder appBuilder = (_run?.App != null ? _run.App()
                : _downloadSessionMode ? AppBuilder.Configure<DownloadSessionSmokeApp>()
                : _shellLoadMode ? AppBuilder.Configure<ShellLoadSmokeApp>()
                : _visualMode ? AppBuilder.Configure<ShowcaseSmokeApp>() : AppBuilder.Configure<App>())
                .UsePlatformDetect()
                .WithInterFont()
                .With(ReaderFonts.ManagerOptions());
            if (xvfbEnvironment)
            {
                Console.WriteLine("SMOKE isolated environment: " + (Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_KWIN") == "1" ? "headless KWin/XWayland" : "Xvfb/Openbox")
                    + "; X11 software rendering; tray fixture=" + (_run?.NativeTray == true ? "native Avalonia, no supplied host"
                        : _trayHostMode ? "private StatusNotifier watcher/host" : "no native tray"));
                appBuilder.With(new X11PlatformOptions
                {
                    RenderingMode = new X11RenderingMode[] { X11RenderingMode.Software }
                });
            }
            int exitCode;
            try
            {
                exitCode = appBuilder
                    .AfterPlatformServicesSetup(builder =>
                    {
                        if (!_lockedProfileMode)
                            ClipboardChecks.Install();
                        if ((xvfbEnvironment && !_trayHostMode && _run?.NativeTray != true) || _noTrayMode || _trayFailureMode)
                        {
                            IWindowingPlatform platform = AvaloniaLocator.Current.GetRequiredService<IWindowingPlatform>();
                            AvaloniaLocator.CurrentMutable.Bind<IWindowingPlatform>()
                                .ToConstant(new NoTrayPlatform(platform, _trayFailureMode));
                        }
                    })
                    .AfterSetup(builder => Dispatcher.UIThread.Post(async () => await RunAsync(), DispatcherPriority.Background))
                    .StartWithClassicDesktopLifetime(Array.Empty<string>(),
                        (_downloadSessionMode || _shellLoadMode) ? ShutdownMode.OnExplicitShutdown : ShutdownMode.OnMainWindowClose);
            }
            catch (Exception exception) when (SmokeStartupDiagnostics.TryGetUnknownCommand(exception, out string commandId))
            {
                string diagnostic = SmokeStartupDiagnostics.UnknownCommandRegistration(commandId);
                startupTiming?.Finish(false, diagnostic);
                Console.Error.WriteLine("SMOKE STARTUP FAILURE: " + diagnostic);
                return 1;
            }

            // StartupErrorWindow acknowledges an initialization failure by shutting
            // down with 2. Only this scenario deliberately creates that failure;
            // its readable conflict dialog, Exit action and close are checked below.
            int expectedExitCode = _lockedProfileMode ? 2 : 0;
            if (exitCode != expectedExitCode || !_checksPassed || !_closed || _failure != null || !sink.Errors.IsEmpty)
            {
                startupTiming?.Finish(false, $"exit={exitCode}, checks={_checksPassed}, closed={_closed}, errors={sink.Errors.Count}; {_failure}");
                Console.Error.WriteLine($"SMOKE FAILED: exit={exitCode}, checks={_checksPassed}, closed={_closed}, errors={sink.Errors.Count}; {_failure}");
                return 1;
            }

            if (!_lockedProfileMode)
            {
                using ProfileLease reacquired = ProfileLease.Acquire(profile);
                Console.WriteLine("SMOKE profile lease released after normal close.");
                _run?.AfterClose?.Invoke(profile, reacquired);
            }

            string scenario = selected.Pass!(Environment.GetEnvironmentVariable);
            startupTiming?.Finish(true);
            Console.WriteLine("SMOKE SCENARIO: " + selectorScenario);
            Console.WriteLine("SMOKE PASS: " + scenario + "; no binding errors.");
            return 0;
        }
        finally
        {
            if (Application.Current is DownloadSessionSmokeApp fixtureApp)
                fixtureApp.DisposeFixture();
            if (Application.Current is ShellLoadSmokeApp shellLoadApp)
                shellLoadApp.DisposeFixture();
            Log.CloseAndFlush();
            if (_archiveCleanupMode) ArchiveCleanupChecks.Cleanup(profile);
            if (!_savedSearchesOnly && keptStartupProfile == null
                && (!_readingPositionMode || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MEOWCHAN_READING_TEST_PROFILE"))))
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
            // Before the harness's own visibility wait: a run measuring startup itself must not start late.
            if (_run?.Stage == SmokeStage.BeforeVisible)
            {
                await RunSelectedAsync(SmokeStage.BeforeVisible, window as MainWindow
                    ?? throw new InvalidOperationException("Startup measurement did not create MainWindow."));
                return;
            }
            await WaitUntilAsync(() => window.IsVisible && window.Bounds.Width > 0);
            Console.WriteLine("SMOKE render scaling: actual=" + window.RenderScaling.ToString("0.###", CultureInfo.InvariantCulture)
                + "; requested=" + (Environment.GetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR") ?? "unset") + ";");
            await CheckDesktopFitAsync(window);
            if (_lockedProfileMode)
            {
                if (window is not StartupErrorWindow)
                    throw new InvalidOperationException("A conflicting profile did not produce StartupErrorWindow.");
                string expectedText = _damagedSettingsCause ? "settings.json.bak" : "Another MeowChan instance";
                await WaitUntilAsync(() => window.GetVisualDescendants().OfType<TextBlock>()
                    .Any(block => block.IsVisible && block.Text?.Contains(expectedText, StringComparison.Ordinal) == true));
                if (_damagedSettingsCause)
                {
                    string shown = window.GetVisualDescendants().OfType<TextBlock>().First(block => block.Text?.Contains(expectedText, StringComparison.Ordinal) == true).Text!;
                    string language = Environment.GetEnvironmentVariable("MEOWCHAN_LOCKED_PROFILE_LANGUAGE") ?? "en";
                    if (MeowChan.Desktop.Localization.Texts.Language != language)
                        throw new InvalidOperationException("The startup window did not use the saved language " + language + ".");
                    if (shown != MeowChan.Desktop.Localization.Texts.T("settings.startup.backup", "settings.json", "settings.json.bak", Config.GetConfigDirectory()))
                        throw new InvalidOperationException("The startup window did not show the localized settings text: " + shown);
                    if (shown.Contains("BackupRecovery", StringComparison.Ordinal) || shown.Contains("settings.startup", StringComparison.Ordinal))
                        throw new InvalidOperationException("The startup window shows a technical name.");
                }
                CaptureWindow(window, _damagedSettingsCause ? "smoke-startup-error-settings-"
                    + (Environment.GetEnvironmentVariable("MEOWCHAN_LOCKED_PROFILE_LANGUAGE") ?? "en") + ".png" : "smoke-startup-error.png");
                string exitLabel = MeowChan.Desktop.Localization.Texts.T("action.exit");
                Button exit = window.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == exitLabel);
                _checksPassed = true;
                exit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                return;
            }

            if (window is not MainWindow)
            {
                string startupText = string.Join("\n", window.GetVisualDescendants().OfType<TextBlock>()
                    .Select(block => block.Text).Where(text => !string.IsNullOrWhiteSpace(text)));
                if (SmokeStartupDiagnostics.TryGetUnknownCommand(startupText, out string commandId))
                    throw new InvalidOperationException(SmokeStartupDiagnostics.UnknownCommandRegistration(commandId));
            }
            MainWindow mainWindow = window as MainWindow ?? throw new InvalidOperationException("MainWindow was not created.");
            if (await RunSelectedAsync(SmokeStage.BeforeSession, mainWindow)) return;
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
            if (await RunSelectedAsync(SmokeStage.Ready, mainWindow)) return;
            if (_archiveCleanupMode)
            {
                await ArchiveCleanupChecks.RunAsync(mainWindow);
                _checksPassed = true; mainWindow.Close(); return;
            }
            CommandRefreshChecks.Run();
            if (await RunSelectedAsync(SmokeStage.AfterCommandRefresh, mainWindow)) return;
            if (_windowGeometryMode)
            {
                await NativeWindowMinimumChecks.RunAsync(mainWindow);
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
                if (Environment.GetEnvironmentVariable("MEOWCHAN_READER_MEDIA_INPUT_ONLY") == "1")
                {
                    await ReaderInteractionChecks.RunMediaActivationInputAsync(mainWindow);
                    _checksPassed = true; mainWindow.Close(); return;
                }
                string? readerAlternative = SmokeScenarios.ReaderAlternative(Environment.GetEnvironmentVariable);
                if (readerAlternative != null && readerAlternative != "gallery-close")
                {
                    await SmokeScenarios.RunReaderAlternativeAsync(readerAlternative, mainWindow);
                    _checksPassed = true;
                    mainWindow.Close();
                    return;
                }
                bool videoFixture = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MEOWCHAN_VIDEO_FIXTURE"));
                if (!_galleryCloseOnly)
                {
                    await RunOptionalCheckAsync("VideoLifecycleChecks");
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
                if (Environment.GetEnvironmentVariable("MEOWCHAN_SHELL_PREFERENCES_ONLY") == "1")
                {
                    await RunOptionalCheckAsync("ShellPreferenceCoalescingChecks", mainWindow);
                    mainWindow.FindControl<MenuItem>("ExitMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    await WaitUntilAsync(() => !mainWindow.IsVisible);
                    _checksPassed = true;
                    desktop.Shutdown();
                    return;
                }
                if (Environment.GetEnvironmentVariable("MEOWCHAN_SHELL_PROJECTION_ONLY") == "1")
                {
                    await RunOptionalCheckAsync("ShellProjectionReconciliationChecks", mainWindow);
                    mainWindow.FindControl<MenuItem>("ExitMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    await WaitUntilAsync(() => !mainWindow.IsVisible);
                    _checksPassed = true;
                    desktop.Shutdown();
                    return;
                }
                if (Environment.GetEnvironmentVariable("MEOWCHAN_SHUTDOWN_ORDER_ONLY") == "1")
                {
                    desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    await ShutdownOrderingChecks.RunAsync(mainWindow);
                    _checksPassed = true;
                    desktop.Shutdown();
                    return;
                }
                await DownloadSessionChecks.RunAsync(mainWindow, desktop);
                _checksPassed = true;
                desktop.Shutdown();
                return;
            }
            if (_shellLoadMode)
            {
                await ShellLoadChecks.RunAsync(mainWindow, desktop);
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
            await DownloadsFeedbackChecks.RunAsync(mainWindow);
            await ComparisonCaptures.RunAsync(mainWindow);
            await ClipboardChecks.RunAsync(mainWindow);
            await UrlDropChecks.RunAsync(mainWindow);
            await PresentationChecks.RunPreferencesAsync(mainWindow);
            await BrandNavigationChecks.RunAsync(mainWindow);
            await ReaderLiveChecks.RunAsync(mainWindow);
            await ReaderRepliesChecks.RunAsync(mainWindow);
            await PostBookmarkChecks.RunAsync(mainWindow);

            DesktopThreadEntry thread = SnapshotFixtures.Thread(123, "123", "Desktop smoke initial title");
            DesktopBoardEntry board = SnapshotFixtures.Board(124);
            await using ISyntheticShellRows synthetic = await CreateSyntheticShellRowsAsync(mainWindow, [thread], [board]);
            ThreadRow threadRow = synthetic.Threads[0];
            BoardRow boardRow = synthetic.Boards[0];
            await WaitUntilAsync(() => VisibleText(window, thread.Snapshot.Title));
            Console.WriteLine("SMOKE thread title binding rendered.");

            bool wrongThread = false;
            threadRow.PropertyChanged += (sender, args) => wrongThread |= !Dispatcher.UIThread.CheckAccess();
            await Task.Run(() =>
            {
                for (int index = 0; index < 1000; index++)
                {
                    DesktopThreadEntry update = thread with { Snapshot = thread.Snapshot with { Title = "Desktop smoke burst " + index } };
                    Dispatcher.UIThread.Post(() => synthetic.Update(update));
                }
                DesktopThreadEntry final = thread with { Snapshot = thread.Snapshot with { Title = "Desktop smoke updated title" } };
                Dispatcher.UIThread.Post(() => synthetic.Update(final));
            });
            await WaitUntilAsync(() => VisibleText(window, "Desktop smoke updated title"));
            await synthetic.WaitForAppliedAsync();
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
            await synthetic.UpdateAsync(thread);
            await WaitUntilAsync(() => VisibleText(window, "Downloading · Waiting for request slot"));
            thread = thread with { Snapshot = thread.Snapshot with { RequestProgress = new RequestPolicyProgress(RequestPolicyPhase.Cooldown, TimeSpan.FromSeconds(30), 1) } };
            await synthetic.UpdateAsync(thread);
            await WaitUntilAsync(() => VisibleText(window, "Downloading · Request cooldown (30 s)"));
            thread = thread with { Snapshot = thread.Snapshot with { RequestProgress = null } };
            await synthetic.UpdateAsync(thread);
            await WaitUntilAsync(() => VisibleText(window, "Downloading"));
            FailureInfo retained = new(FailureKind.Timeout, "Smoke retained check failure");
            await synthetic.UpdateAsync(thread with
            {
                Snapshot = thread.Snapshot with
                {
                    Downloads = new DownloadCounts(3, 2),
                    Activity = ThreadActivity.Finished,
                    LastCheckFailure = retained
                }
            });
            // One failure, one wording: row and inspector come from the shared presentation (headline first).
            if (threadRow.Status != "Check failed · Finished · Timed out" || threadRow.Queued != 3 || threadRow.Active != 2
                || threadRow.ErrorDetails != MeowChan.Desktop.DiagnosticFailurePresentation.Inspector(retained, MeowChan.Desktop.DiagnosticOperation.Thread)
                || !threadRow.ErrorDetails.StartsWith("Timed out" + Environment.NewLine, StringComparison.Ordinal)
                || !threadRow.ErrorDetails.EndsWith("Smoke retained check failure", StringComparison.Ordinal))
                throw new InvalidOperationException("Typed queue counts or retained check failures were not projected.");
            Console.WriteLine("SMOKE request waiting/backoff rendered; typed queue counts and retained check failure projected.");
            await PresentationChecks.CheckDetailsAsync(mainWindow, threadRow);
            thread = thread with
            {
                Snapshot = thread.Snapshot with { SourceState = ThreadSourceState.Archived, Activity = ThreadActivity.Finished }
            };
            await synthetic.UpdateAsync(thread);
            await WaitUntilAsync(() => VisibleText(window, "Archived · Finished"));
            Console.WriteLine("SMOKE terminal source state remained visible after media status changed.");
            CaptureWindow(window, "smoke-mainwindow.png");

            TabControl tabs = window.FindControl<TabControl>("DownloadsTabs")!;
            tabs.SelectedIndex = 1;
            await WaitUntilAsync(() => VisibleText(window, boardRow.NiceName));
            await synthetic.UpdateAsync(board with
            {
                Snapshot = board.Snapshot with { RequestProgress = new RequestPolicyProgress(RequestPolicyPhase.Cooldown, TimeSpan.FromSeconds(12), 1) }
            });
            await WaitUntilAsync(() => VisibleText(window, "Request cooldown (12 s)"));
            await PresentationChecks.CheckBoardDetailsAsync(mainWindow, boardRow);
            await synthetic.UpdateAsync(board);
            await WaitUntilAsync(() => VisibleText(window, "Watching"));
            Console.WriteLine("SMOKE board binding and request cooldown rendered.");

            string finalTitle = threadRow.Title;
            threadRow.Dispose();
            threadRow.UpdateSnapshot(thread with { Snapshot = thread.Snapshot with { Title = "Disposed row must not update" } });
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (threadRow.Title != finalTitle)
                throw new InvalidOperationException("A disposed row still accepted updates.");

            await synthetic.DisposeAsync();
            if (await RunSelectedAsync(SmokeStage.NormalHead, mainWindow)) return;
            await GridChecks.RunAsync(mainWindow);
            await SettingsPageChecks.RunAsync(mainWindow);
            await DiagnosticsChecks.RunAsync(mainWindow);
            await SourcesChecks.RunAsync(mainWindow);
            await BoardAdmissionChecks.RunAsync(mainWindow);
            await PauseChecks.RunAsync(mainWindow);
            await RulesPageChecks.RunAsync(mainWindow);
            await SettingsChecks.RunAsync(mainWindow);
            CloseNormalThroughCaption(mainWindow);
        }
        catch (Exception exception)
        {
            _failure = SmokeStartupDiagnostics.TryGetUnknownCommand(exception, out string commandId)
                ? new InvalidOperationException(SmokeStartupDiagnostics.UnknownCommandRegistration(commandId), exception)
                : exception;
            Console.Error.WriteLine("SMOKE check failed: " + _failure);
            if ((_downloadSessionMode || _shellLoadMode || _galleryCloseOnly)
                && Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime fixtureDesktop)
                fixtureDesktop.Shutdown(1);
            else if (window is MainWindow mainWindow && mainWindow.FindControl<MenuItem>("ExitMenuItem") is MenuItem exit)
                exit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            else if (window != null) window.Close();
            else if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown(1);
        }
    }

    // Shared by the complete manual normal run and its bounded CI head.
    private static void CloseNormalThroughCaption(MainWindow mainWindow)
    {
        Console.WriteLine($"SMOKE after settings: owned windows={mainWindow.OwnedWindows.Count}, enabled={mainWindow.IsEnabled}, effectively enabled={mainWindow.IsEffectivelyEnabled}.");
        foreach (Window ownedWindow in mainWindow.OwnedWindows)
        {
            Console.WriteLine($"SMOKE remaining owned window: type={ownedWindow.GetType().Name}, visible={ownedWindow.IsVisible}, enabled={ownedWindow.IsEnabled}.");
        }
        _checksPassed = true;
        Console.WriteLine("SMOKE requesting close through the custom window caption.");
        mainWindow.FindControl<Button>("WindowCloseButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Console.WriteLine($"SMOKE caption close returned: closed={_closed}, visible={mainWindow.IsVisible}.");
    }

    // Runs the selected Root/Branch scenario when this is its stage; true when it ran and closed the window.
    private static async Task<bool> RunSelectedAsync(SmokeStage stage, MainWindow mainWindow)
    {
        if (_run == null || _run.Stage != stage) return false;
        await _run.Run(mainWindow);
        if (stage == SmokeStage.NormalHead)
            CloseNormalThroughCaption(mainWindow);
        else
        {
            _checksPassed = true;
            mainWindow.Close();
        }
        return true;
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

    internal static void CaptureWindow(Window window, string fileName, string? directory = null)
    {
        window.UpdateLayout();
        string screenshotPath = Path.Combine(directory ?? AppContext.BaseDirectory, fileName);
        double scale = window.RenderScaling;
        using RenderTargetBitmap screenshot = new RenderTargetBitmap(
            new PixelSize((int)Math.Ceiling(window.Bounds.Width * scale), (int)Math.Ceiling(window.Bounds.Height * scale)),
            new Vector(96 * scale, 96 * scale));
        screenshot.Render(window);
        screenshot.Save(screenshotPath, PngBitmapEncoderOptions.Default);
        Console.WriteLine("SMOKE window render: " + screenshotPath);
        Console.WriteLine("SMOKE capture metadata: " + System.Text.Json.JsonSerializer.Serialize(new
        {
            fileName, mode = OwningMainWindow(window)?.SessionHost.Config.WorkspaceMode, theme = window.ActualThemeVariant.ToString(),
            language = MeowChan.Desktop.Localization.Texts.Language, renderScaling = scale,
            clientDip = window.ClientSize.ToString(), boundsDip = window.Bounds.ToString(),
            imagePixels = screenshot.PixelSize.ToString(), window = window.GetType().Name
        }));
    }

    // Dialogs can belong to an isolated host rather than the application's primary shell.
    // Never infer their mode from the default profile (or an unrelated lifetime MainWindow).
    // Ownerless fixture windows have no authoritative shell mode: report null, not a default.
    internal static MainWindow? OwningMainWindow(Window window)
    {
        for (WindowBase? owner = window; owner != null; owner = owner.Owner)
        {
            if (owner is MainWindow main)
                return main;
        }
        return null;
    }

    // Optional package smoke helpers are discovered at runtime so this exact Program.cs also compiles on
    // the release-HEAD measurement variant, where their candidate-only product internals do not exist.
    internal static async Task RunOptionalCheckAsync(string name, params object[] parameters)
    {
        Type helper = typeof(Program).Assembly.GetType("MeowChan.DesktopSmoke." + name)
            ?? throw new InvalidOperationException("The requested smoke helper is unavailable in this source variant: " + name);
        var run = helper.GetMethod("RunAsync", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The smoke helper has no RunAsync: " + name);
        await (Task)run.Invoke(null, parameters)!;
    }

    // The shared harness compiles without the candidate-only fixture. Measurement modes do not enter
    // this normal-scene path, just as they do not invoke the other optional candidate checks above.
    private static async Task<ISyntheticShellRows> CreateSyntheticShellRowsAsync(MainWindow window,
        DesktopThreadEntry[] threads, DesktopBoardEntry[] boards)
    {
        Type helper = typeof(Program).Assembly.GetType("MeowChan.DesktopSmoke.SyntheticShellRows")
            ?? throw new InvalidOperationException("The synthetic normal-scene fixture is unavailable in this source variant.");
        var create = helper.GetMethod("CreateAsync", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The synthetic fixture has no CreateAsync.");
        Task task = create.Invoke(null, new object[] { window, threads, boards }) as Task
            ?? throw new InvalidOperationException("The synthetic fixture did not return a Task.");
        await task;
        return task.GetType().GetProperty("Result")?.GetValue(task) as ISyntheticShellRows
            ?? throw new InvalidOperationException("The synthetic fixture did not implement the shared harness facade.");
    }

    // state: optional description of what the condition observes, printed and attached on timeout only.
    internal static Task WaitUntilAsync(Func<bool> condition, Func<string>? state = null,
        [System.Runtime.CompilerServices.CallerFilePath] string callerFile = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int callerLine = 0) =>
        WaitUntilAsync(condition, TimeSpan.FromSeconds(15), state, callerFile, callerLine);

    internal static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, Func<string>? state = null,
        [System.Runtime.CompilerServices.CallerFilePath] string callerFile = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int callerLine = 0)
    {
        DateTime deadline = DateTime.UtcNow.Add(timeout);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                string location = Path.GetFileName(callerFile) + ":" + callerLine;
                if (state == null) throw new TimeoutException($"Desktop condition timed out after fifteen seconds at {location}.");
                string observed;
                try { observed = state(); }
                catch (Exception failure) { observed = "state unavailable: " + failure.GetType().Name + ": " + failure.Message; }
                Console.Error.WriteLine("SMOKE wait timeout state at " + location + ": " + observed + "; " + SmokeLoad.Describe());
                throw new TimeoutException($"Desktop condition timed out after fifteen seconds at {location}; state: {observed}");
            }
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

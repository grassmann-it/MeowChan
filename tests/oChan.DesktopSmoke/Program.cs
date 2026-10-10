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
using oChan;
using oChan.Desktop;
using oChan.Downloader;
using oChan.Http;
using oChan.Interfaces;
using oChan.Persistence;
using Serilog;

namespace oChan.DesktopSmoke;

internal static class Program
{
    private static bool _checksPassed;
    private static bool _closed;
    private static Exception? _failure;
    private static bool _lockedProfileMode;
    private static bool _noTrayMode;
    private static bool _trayFailureMode;
    private static bool _downloadSessionMode;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 1 || (args.Length == 1 && args[0] is not ("--locked-profile" or "--no-tray" or "--tray-failure" or "--download-session")))
        {
            Console.Error.WriteLine("Usage: oChan.DesktopSmoke [--locked-profile|--no-tray|--tray-failure|--download-session]");
            return 2;
        }

        string profile = Path.Combine(Path.GetTempPath(), "ochan-desktop-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        Environment.SetEnvironmentVariable("OCHAN_PROFILE", profile);
        _lockedProfileMode = args.Contains("--locked-profile", StringComparer.Ordinal);
        _noTrayMode = args.Contains("--no-tray", StringComparer.Ordinal);
        _trayFailureMode = args.Contains("--tray-failure", StringComparer.Ordinal);
        _downloadSessionMode = args.Contains("--download-session", StringComparer.Ordinal);
        bool xvfbEnvironment = OperatingSystem.IsLinux()
            && Environment.GetEnvironmentVariable("OCHAN_DESKTOP_SMOKE_XVFB") == "1";
        SmokeLogSink sink = new SmokeLogSink();
        Log.Logger = new LoggerConfiguration().MinimumLevel.Debug()
            .WriteTo.Console(outputTemplate: "SMOKE APP [{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        try
        {
            Config config = new Config
            {
                DownloadPath = Path.Combine(profile, "Downloads"),
                MinimizeToTray = _noTrayMode || _trayFailureMode,
                SaveUrlsOnExit = true
            };
            config.SaveConfig();
            using ProfileLease? conflictingLease = _lockedProfileMode ? ProfileLease.Acquire(profile) : null;
            Avalonia.Logging.Logger.Sink = sink;
            Console.WriteLine("SMOKE isolated profile: " + profile);
            AppBuilder appBuilder = (_downloadSessionMode ? AppBuilder.Configure<DownloadSessionSmokeApp>() : AppBuilder.Configure<App>())
                .UsePlatformDetect()
                .WithInterFont();
            if (xvfbEnvironment)
            {
                Console.WriteLine("SMOKE Xvfb environment: X11 software rendering; no native tray host (tray fixture installed).");
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
                    if (xvfbEnvironment || _noTrayMode || _trayFailureMode)
                    {
                        IWindowingPlatform platform = AvaloniaLocator.Current.GetRequiredService<IWindowingPlatform>();
                        AvaloniaLocator.CurrentMutable.Bind<IWindowingPlatform>()
                            .ToConstant(new NoTrayPlatform(platform, _trayFailureMode));
                    }
                })
                .AfterSetup(builder => Dispatcher.UIThread.Post(async () => await RunAsync(), DispatcherPriority.Background))
                .StartWithClassicDesktopLifetime(Array.Empty<string>(),
                    _downloadSessionMode ? ShutdownMode.OnExplicitShutdown : ShutdownMode.OnMainWindowClose);

            if (exitCode != 0 || !_checksPassed || !_closed || _failure != null || !sink.Errors.IsEmpty)
            {
                Console.Error.WriteLine($"SMOKE FAILED: exit={exitCode}, checks={_checksPassed}, closed={_closed}, errors={sink.Errors.Count}; {_failure}");
                return 1;
            }

            if (!_lockedProfileMode)
            {
                using ProfileLease reacquired = ProfileLease.Acquire(profile);
                Console.WriteLine("SMOKE profile lease released after normal close.");
            }

            string scenario = _lockedProfileMode ? "locked profile: readable error, Exit closed the window"
                : _noTrayMode ? "no tray: user close kept taskbar access, Exit closed the window"
                : _trayFailureMode ? "tray creation failure: startup succeeded, user close kept taskbar access, Exit closed the window"
                : _downloadSessionMode ? "real session: URL admission, fixture media, assigned path, native close and restart restoration"
                : "startup, rendered thread/board bindings, background update, normal close";
            Console.WriteLine("SMOKE PASS: " + scenario + "; no binding errors.");
            return 0;
        }
        finally
        {
            if (Application.Current is DownloadSessionSmokeApp fixtureApp)
                fixtureApp.DisposeFixture();
            Log.CloseAndFlush();
            Directory.Delete(profile, recursive: true);
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
                    .Any(block => block.IsVisible && block.Text?.Contains("Another oChan instance", StringComparison.Ordinal) == true));
                CaptureWindow(window, "smoke-startup-error.png");
                Button exit = window.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "Exit");
                _checksPassed = true;
                exit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                return;
            }

            MainWindow mainWindow = window as MainWindow ?? throw new InvalidOperationException("MainWindow was not created.");
            await WaitUntilAsync(() => mainWindow.FindControl<Button>("AddUrlButton")!.IsEnabled);
            if (mainWindow.UrlList.Count != 0 || mainWindow.BoardsList.Count != 0)
                throw new InvalidOperationException("The isolated session did not start empty.");
            Console.WriteLine("SMOKE MainWindow visible with empty isolated session.");
            if (_downloadSessionMode)
            {
                await DownloadSessionChecks.RunAsync(mainWindow, desktop);
                _checksPassed = true;
                desktop.Shutdown();
                return;
            }
            if (_noTrayMode || _trayFailureMode)
            {
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

            await ComparisonCaptures.RunAsync(mainWindow);
            await ClipboardChecks.RunAsync(mainWindow);
            await PresentationChecks.RunPreferencesAsync(mainWindow);

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

            TabControl tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
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
            await SettingsChecks.RunAsync(mainWindow);
            Console.WriteLine($"SMOKE after settings: owned windows={mainWindow.OwnedWindows.Count}, enabled={mainWindow.IsEnabled}, effectively enabled={mainWindow.IsEffectivelyEnabled}.");
            foreach (Window ownedWindow in mainWindow.OwnedWindows)
            {
                Console.WriteLine($"SMOKE remaining owned window: type={ownedWindow.GetType().Name}, visible={ownedWindow.IsVisible}, enabled={ownedWindow.IsEnabled}.");
            }
            _checksPassed = true;
            Console.WriteLine("SMOKE requesting normal MainWindow.Close().");
            window.Close();
            Console.WriteLine($"SMOKE normal MainWindow.Close() returned: closed={_closed}, visible={window.IsVisible}.");
        }
        catch (Exception exception)
        {
            _failure = exception;
            Console.Error.WriteLine("SMOKE check failed: " + exception);
            if (_downloadSessionMode && Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime fixtureDesktop)
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
        screenshot.Save(screenshotPath);
        Console.WriteLine("SMOKE window render: " + screenshotPath);
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

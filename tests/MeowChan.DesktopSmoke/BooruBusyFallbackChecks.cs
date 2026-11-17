using System.ComponentModel;
using System.Net.Http;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Boards.Base;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Commands;
using MeowChan.Downloader;
using MeowChan.Http;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopSmoke;

// Root scenario "booru-busy-fallback": the real shell owns a hosted BooruPage while the real
// session pause command is held by a synthetic local watch. Service loss must hide/dispose the
// page immediately, but its ordinary Downloads fallback must wait for the pause owner to finish.
// The source is entirely in-memory and its HTTP client refuses every request.
internal static class BooruBusyFallbackChecks
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);
    private const string ThreadUrl = "https://busy-fallback.invalid/g/42";

    internal static async Task RunAsync(MainWindow owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        await RunHeldPauseAsync(owner, pauseFailure: false);
        await RunHeldPauseAsync(owner, pauseFailure: true);
        await RunClosingAsync(owner);
        Console.WriteLine("SMOKE booru busy fallback: real shell-hosted page; service loss hid and disposed the page immediately; the Downloads fallback waited for the real PauseButton await; the same route completed after a one-shot pause failure; closing while the await was pending never navigated to Downloads. Synthetic source and local provider binding made no network requests.");
    }

    private static async Task RunHeldPauseAsync(MainWindow owner, bool pauseFailure)
    {
        string profile = TemporaryWorkspacePaths.CreateDirectory(pauseFailure ? "booru-busy-failure-" : "booru-busy-guard-");
        string media = Path.Combine(profile, "media");
        new Config
        {
            DownloadPath = media,
            AutomaticUpdateChecks = false,
            SaveUrlsOnExit = true,
            FirstRunSetupVersion = 1,
            MinimumFreeSpaceGiB = 0,
            CloseToTray = false,
            MinimizeToTray = false,
            WorkspaceMode = "Advanced",
            MaxParallelDownloads = 1
        }.SaveConfig(profile);
        // Save into the empty profile first: even an empty media subfolder requires recovery without settings.
        Directory.CreateDirectory(media);

        PauseFixtureSource? source = null;
        DanbooruStub? stub = null;
        DanbooruTestBinding? binding = null;
        DesktopSessionHost? host = null;
        MainWindow? shell = null;
        try
        {
            source = new PauseFixtureSource(pauseFailure);
            stub = new DanbooruStub();
            binding = new DanbooruTestBinding(stub);
            host = new DesktopSessionHost(profile, new IImageBoard[] { source },
                sessionStoreFactory: (_, _) => new FixtureSessionStore(ThreadUrl))
            {
                BooruBindingForTests = () => binding!.Binding
            };
            shell = new MainWindow(host);
            shell.Show(owner);
            await host.StartAsync().WaitAsync(Wait);
            await Program.WaitUntilAsync(() => host.State == DesktopSessionState.Ready
                && shell.FindControl<Button>("AddUrlButton")!.IsEnabled
                && host.BooruServices != null
                && source.Thread.CheckEntered.Task.IsCompleted,
                () => "busy fallback startup did not reach ready/held-watch state");

            await OpenBooruPageAsync(shell);
            BooruPage page = shell.BooruPageForTests!;
            ContentControl pageHost = shell.FindControl<ContentControl>("BooruPageHost")!;
            Require(ReferenceEquals(pageHost.Content, page) && pageHost.IsVisible,
                "The busy fallback fixture did not use the shell as the Booru page host.");

            Button pause = shell.FindControl<Button>("PauseButton")!;
            Require(pause.IsVisible && pause.IsEffectivelyEnabled && ReferenceEquals(TopLevel.GetTopLevel(pause), shell),
                "The real shell PauseButton is not enabled on the shell top level.");
            pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => host.IsPauseTransitioning
                && source.Thread.CancellationObserved.Task.IsCompleted,
                () => "the real PauseButton did not enter its held session await");

            Task disable = Task.Run(() => binding!.SetEnabled(false));
            await disable.WaitAsync(Wait);
            await Program.WaitUntilAsync(() => host.BooruServices == null
                && shell.BooruPageForTests == null
                && !pageHost.IsVisible
                && IsBooruFallbackPending(shell),
                () => "service loss did not hide/dispose the hosted page and leave a pending fallback");

            // The availability callback has run while OnPauseClick still owns the shell busy gate.
            // A fallback here would be an illegal re-entrant navigation and would prove the regression.
            Require(shell.SelectedPage == MainWindow.WorkspacePage.Booru,
                "Booru disappeared by navigating away before the real pause await released.");
            Require(!pageHost.IsVisible && shell.BooruPageForTests == null && IsBooruFallbackPending(shell),
                "Service loss did not leave only the hidden shell host and pending ordinary route.");

            source.Thread.ReleasePause();
            await Program.WaitUntilAsync(() => !host.IsPauseTransitioning && host.IsPaused,
                () => "the real pause command did not settle after releasing its synthetic watch");
            await Program.WaitUntilAsync(() => shell.SelectedPage == MainWindow.WorkspacePage.Downloads
                && shell.FindControl<Control>("WorkspaceBody")!.IsVisible
                && !pageHost.IsVisible
                && !IsBooruFallbackPending(shell),
                () => "the pending Booru fallback did not complete through Downloads after pause busy ended");
            Require(!pageHost.IsVisible && shell.BooruPageForTests == null,
                "Completing the fallback revived a disposed Booru page.");
            Require(stub.Requests.IsEmpty,
                "The busy fallback route caused a provider request: " + string.Join(", ", stub.Requests));
            if (pauseFailure)
            {
                Require(source.PauseStopFailureObserved,
                    "The pause-error branch did not exercise the one-shot real watch-stop failure.");
                Require(host.IsPaused, "A pause failure unexpectedly restored the queue to running state.");
            }
        }
        finally
        {
            // An assertion can fail before the test releases the held monitor. Always drain it
            // before closing, otherwise fixture cleanup would hide the original failure in a timeout.
            source?.Thread.ReleasePause();
            if (shell != null && shell.IsVisible) await CloseAsync(shell);
            shell?.Dispose(); shell = null;
            if (host != null && !host.IsProfileReleased) await host.DisposeAsync().AsTask().WaitAsync(Wait);
            binding?.Dispose();
            stub?.Dispose();
            source?.Dispose();
            TryDelete(profile);
            owner.Activate();
        }
    }

    private static async Task RunClosingAsync(MainWindow owner)
    {
        string profile = TemporaryWorkspacePaths.CreateDirectory("booru-busy-closing-");
        string media = Path.Combine(profile, "media");
        new Config
        {
            DownloadPath = media,
            AutomaticUpdateChecks = false,
            SaveUrlsOnExit = true,
            FirstRunSetupVersion = 1,
            MinimumFreeSpaceGiB = 0,
            CloseToTray = false,
            MinimizeToTray = false,
            WorkspaceMode = "Advanced",
            MaxParallelDownloads = 1
        }.SaveConfig(profile);
        // Save into the empty profile first: even an empty media subfolder requires recovery without settings.
        Directory.CreateDirectory(media);

        PauseFixtureSource? source = null;
        DanbooruStub? stub = null;
        DanbooruTestBinding? binding = null;
        DesktopSessionHost? host = null;
        MainWindow? shell = null;
        try
        {
            source = new PauseFixtureSource(failStopOnce: false);
            stub = new DanbooruStub();
            binding = new DanbooruTestBinding(stub);
            host = new DesktopSessionHost(profile, new IImageBoard[] { source },
                sessionStoreFactory: (_, _) => new FixtureSessionStore(ThreadUrl))
            {
                BooruBindingForTests = () => binding!.Binding
            };
            shell = new MainWindow(host);
            shell.Show(owner);
            await host.StartAsync().WaitAsync(Wait);
            await Program.WaitUntilAsync(() => host.State == DesktopSessionState.Ready
                && shell.FindControl<Button>("AddUrlButton")!.IsEnabled
                && host.BooruServices != null
                && source.Thread.CheckEntered.Task.IsCompleted,
                () => "closing fallback startup did not reach ready/held-watch state");
            await OpenBooruPageAsync(shell);
            ContentControl pageHost = shell.FindControl<ContentControl>("BooruPageHost")!;

            Button pause = shell.FindControl<Button>("PauseButton")!;
            pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => host.IsPauseTransitioning
                && source.Thread.CancellationObserved.Task.IsCompleted,
                () => "closing fixture did not enter the real held pause await");

            await Task.Run(() => binding!.SetEnabled(false)).WaitAsync(Wait);
            await Program.WaitUntilAsync(() => host.BooruServices == null
                && shell.BooruPageForTests == null
                && !pageHost.IsVisible
                && IsBooruFallbackPending(shell),
                () => "closing fixture did not queue the unavailable Booru fallback");

            Task closing = CloseAsync(shell);
            await Program.WaitUntilAsync(() => ReadField<bool>(shell, "_isClosing"),
                () => "CloseApplicationAsync did not claim the closing lifetime");
            Require(shell.SelectedPage == MainWindow.WorkspacePage.Booru,
                "Closing changed to Downloads before the pending pause was released.");

            source.Thread.ReleasePause();
            await closing.WaitAsync(Wait);
            Require(shell.SelectedPage != MainWindow.WorkspacePage.Downloads,
                "Closing allowed a pending Booru fallback to navigate to Downloads after teardown began.");
            Require(!shell.IsVisible && shell.SelectedPage == MainWindow.WorkspacePage.Booru,
                "Closing did not finish on the original Booru route without an illicit fallback navigation.");
            Require(stub.Requests.IsEmpty,
                "The closing fallback route caused a provider request: " + string.Join(", ", stub.Requests));
        }
        finally
        {
            // An assertion can fail before the test releases the held monitor. Always drain it
            // before closing, otherwise fixture cleanup would hide the original failure in a timeout.
            source?.Thread.ReleasePause();
            if (shell != null && shell.IsVisible) await CloseAsync(shell);
            shell?.Dispose(); shell = null;
            if (host != null && !host.IsProfileReleased) await host.DisposeAsync().AsTask().WaitAsync(Wait);
            binding?.Dispose();
            stub?.Dispose();
            source?.Dispose();
            TryDelete(profile);
            owner.Activate();
        }
    }

    private static async Task OpenBooruPageAsync(MainWindow shell)
    {
        MenuItem workspace = shell.FindControl<MenuItem>("WorkspaceMenuItem")!;
        await Program.WaitUntilAsync(() => workspace.Items.OfType<MenuItem>().Any(item => item.IsVisible
                && item.Header as string == CommandCatalog.Label("booru.open")),
            () => "the Booru workspace command did not become visible");
        MenuItem entry = workspace.Items.OfType<MenuItem>().Single(item => item.IsVisible
            && item.Header as string == CommandCatalog.Label("booru.open"));
        workspace.IsSubMenuOpen = true;
        await SettleAsync(shell);
        Require(entry.IsVisible && entry.IsEffectivelyEnabled, "The Booru workspace command is not executable.");
        workspace.IsSubMenuOpen = false;
        entry.Command!.Execute(null);
        await Program.WaitUntilAsync(() => shell.SelectedPage == MainWindow.WorkspacePage.Booru
            && shell.BooruPageForTests is { } page
            && ReferenceEquals(shell.FindControl<ContentControl>("BooruPageHost")!.Content, page),
            () => "the shell did not host the Booru page through its real workspace route");
        await SettleAsync(shell);
    }

    private static bool IsBooruFallbackPending(MainWindow shell)
    {
        object navigation = typeof(MainWindow).GetField("_booruUnavailableNavigation",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(shell)!;
        return (bool)navigation.GetType().GetProperty("IsPending",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(navigation)!;
    }

    private static async Task CloseAsync(MainWindow shell)
    {
        MethodInfo close = typeof(MainWindow).GetMethod("CloseApplicationAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await ((Task)close.Invoke(shell, null)!).WaitAsync(Wait);
        await Program.WaitUntilAsync(() => !shell.IsVisible, () => "shell did not close");
    }

    private static async Task SettleAsync(Window shell)
    {
        await Dispatcher.UIThread.InvokeAsync(shell.UpdateLayout, DispatcherPriority.Background);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static T ReadField<T>(object target, string name) => (T)(target.GetType().GetField(name,
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(target)
        ?? throw new MissingFieldException(target.GetType().FullName, name));

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FixtureSessionStore(string url) : ISessionStore
    {
        public Task<SessionLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SessionLoadResult(new SessionSnapshot
            {
                ThreadUrls = new[] { url },
                DownloadDirectories = new Dictionary<string, string>(StringComparer.Ordinal)
            }, null, false));

        public Task SaveAsync(SessionSnapshot snapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class PauseFixtureSource : IImageBoard, IDisposable
    {
        private readonly HttpClient _client = new(new RejectNetworkHandler());
        private readonly PauseFixtureBoard _board;
        internal PauseFixtureThread Thread { get; }
        private IThread HostedThread { get; }

        internal bool PauseStopFailureObserved => HostedThread is FailingStopThread failing && failing.PauseStopFailureObserved;

        internal PauseFixtureSource(bool failStopOnce)
        {
            _board = new PauseFixtureBoard(this);
            Thread = new PauseFixtureThread(_board);
            HostedThread = failStopOnce ? new FailingStopThread(Thread) : Thread;
        }

        public string Name => "busy-fallback";
        public string NiceName => "Busy fallback fixture";
        public Uri BaseUri => new("https://busy-fallback.invalid/");
        public bool CanHandle(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.Host == BaseUri.Host;
        public bool IsThreadUri(Uri uri) => uri.AbsoluteUri == ThreadUrl;
        public bool IsBoardUri(Uri uri) => uri == _board.BoardUri;
        public IBoard GetBoard(Uri uri) => _board;
        public IThread GetThread(Uri uri) => HostedThread;
        public HttpClient GetHttpClient() => _client;
        public void Dispose()
        {
            _client.Dispose();
        }
    }

    private sealed class PauseFixtureBoard(PauseFixtureSource source) : BaseBoard
    {
        public override IImageBoard ImageBoard => source;
        public override string BoardCode => "g";
        public override string Name { get; protected set; } = "Busy fallback board";
        public override string NiceName => Name;
        public override Uri BoardUri => new(source.BaseUri, "g/");
        public override Task<IEnumerable<IThread>> GetThreadsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IEnumerable<IThread>>(Array.Empty<IThread>());
    }

    private sealed class PauseFixtureThread(PauseFixtureBoard board) : BaseThread
    {
        internal TaskCompletionSource CheckEntered { get; } = Signal();
        internal TaskCompletionSource CancellationObserved { get; } = Signal();
        private readonly TaskCompletionSource _releasePause = Signal();

        public override IBoard Board => board;
        public override string ThreadId => "42";
        public override string NiceName => "Busy fallback thread";
        public override Uri ThreadUri => new(ThreadUrl);

        public override async Task checkThreadAsync(DownloadQueue queue, CancellationToken cancellationToken = default)
        {
            CheckEntered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                await _releasePause.Task.ConfigureAwait(false);
            }
        }

        internal void ReleasePause() => _releasePause.TrySetResult();
        private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // The production Registry owns the actual BaseThread monitor. This narrow adapter only makes its
    // first stop fail once, so OnPauseClick's real catch/finally path is exercised without a product hook.
    private sealed class FailingStopThread(PauseFixtureThread inner) : IThread
    {
        private int _failNextStop = 1;
        public event PropertyChangedEventHandler? PropertyChanged
        {
            add => inner.PropertyChanged += value;
            remove => inner.PropertyChanged -= value;
        }
        internal bool PauseStopFailureObserved { get; private set; }
        public IBoard Board => inner.Board;
        public ThreadActivity Activity { get => inner.Activity; set => inner.Activity = value; }
        public FailureInfo? LastCheckFailure => inner.LastCheckFailure;
        public string ThreadId => inner.ThreadId;
        public string DownloadDirectory => inner.DownloadDirectory;
        public ThreadSourceState SourceState => inner.SourceState;
        public RequestPolicyProgress? RequestProgress => inner.RequestProgress;
        public IReadOnlyDictionary<string, string> MediaFailures => inner.MediaFailures;
        public string Title { get => inner.Title; set => inner.Title = value; }
        public string NiceName => inner.NiceName;
        public Uri ThreadUri => inner.ThreadUri;
        public string Status => inner.Status;
        public string Progress => inner.Progress;
        public int TotalMediaCount => inner.TotalMediaCount;
        public int DownloadedMediaCount => inner.DownloadedMediaCount;
        public string Url => inner.Url;
        public HashSet<string> DownloadedMedia { get => inner.DownloadedMedia; set => inner.DownloadedMedia = value; }
        public IDisposable RegisterRemovalSink(Action<IThread> sink) => inner.RegisterRemovalSink(sink);
        public ThreadSnapshot GetSnapshot(DownloadCounts downloads) => inner.GetSnapshot(downloads);
        public void StartMonitoring(DownloadQueue queue, string outputDirectory, TimeSpan interval) => inner.StartMonitoring(queue, outputDirectory, interval);
        public void UpdateMonitoringInterval(TimeSpan interval) => inner.UpdateMonitoringInterval(interval);
        public async Task StopMonitoringAsync()
        {
            await inner.StopMonitoringAsync().ConfigureAwait(false);
            if (Interlocked.Exchange(ref _failNextStop, 0) == 1)
            {
                PauseStopFailureObserved = true;
                throw new InvalidOperationException("synthetic pause stop failure");
            }
        }
        public Task EnqueueMediaDownloadsAsync(DownloadQueue queue, CancellationToken cancellationToken = default) =>
            inner.EnqueueMediaDownloadsAsync(queue, cancellationToken);
        public bool IsMediaDownloaded(string mediaIdentifier) => inner.IsMediaDownloaded(mediaIdentifier);
        public void MarkMediaAsDownloaded(string mediaIdentifier, string? destinationPath = null) => inner.MarkMediaAsDownloaded(mediaIdentifier, destinationPath);
        public void MarkMediaPending(string mediaIdentifier, string? destinationPath = null) => inner.MarkMediaPending(mediaIdentifier, destinationPath);
        public void ReportMediaFailure(string mediaIdentifier, Exception exception, string? destinationPath = null) => inner.ReportMediaFailure(mediaIdentifier, exception, destinationPath);
        public Task checkThreadAsync(DownloadQueue queue, CancellationToken cancellationToken = default) =>
            inner.checkThreadAsync(queue, cancellationToken);
        public void NotifyThreadRemoval(bool abort) => inner.NotifyThreadRemoval(abort);
    }

    private sealed class RejectNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The busy fallback fixture must not send HTTP.");
    }
}

using System.Net.Http;
using MeowChan.Admission;
using MeowChan.Boards.Base;
using MeowChan.Desktop;
using MeowChan.Desktop.Updates;
using MeowChan.Downloader;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class BoardWatcherRegistryTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task RestartRestoresWatcherNameAndIntervalsAndGlobalChangesOnlyAffectInheritedBoards()
    {
        using var profile = new TempDirectory();
        SaveConfig(profile.Path);
        var source = new FixtureSource();
        string customUrl = source.Custom.BoardUri.AbsoluteUri;
        string inheritedUrl = source.Inherited.BoardUri.AbsoluteUri;
        await using (DesktopSessionHost host = CreateHost(profile.Path, source))
        {
            await host.StartAsync();
            Assert.True(host.State == DesktopSessionState.Ready, host.StartupError);
            await host.ApplyBoardAdmissionAsync(customUrl, new BoardAdmissionPolicy([], null, " Recent photos ", 12));
            await host.ApplyBoardAdmissionAsync(inheritedUrl, new BoardAdmissionPolicy([], null, " ", null));
            Assert.Equal(TimeSpan.FromMinutes(12), await source.Custom.DelayAfterNextCheckAsync());
            Assert.Equal(TimeSpan.FromSeconds(70), await source.Inherited.DelayAfterNextCheckAsync());
            AssertBoardName(host, customUrl, "Recent photos");
            AssertBoardName(host, inheritedUrl, source.Inherited.NiceName);
        }

        var reopened = new FixtureSource();
        await using (DesktopSessionHost host = CreateHost(profile.Path, reopened))
        {
            await host.StartAsync();
            Assert.True(host.State == DesktopSessionState.Ready, host.StartupError);
            Assert.Equal(12, host.GetBoardAdmissionPolicy(customUrl).WatcherIntervalMinutes);
            Assert.Equal("Recent photos", host.GetBoardAdmissionPolicy(customUrl).WatcherName);
            Assert.Null(host.GetBoardAdmissionPolicy(inheritedUrl).WatcherIntervalMinutes);
            Assert.Equal(TimeSpan.FromMinutes(12), await reopened.Custom.FirstDelay.WaitAsync(Deadline));
            Assert.Equal(TimeSpan.FromSeconds(70), await reopened.Inherited.FirstDelay.WaitAsync(Deadline));
            Assert.Equal(TimeSpan.FromMinutes(12), await reopened.Custom.DelayAfterNextCheckAsync());
            Assert.Equal(TimeSpan.FromSeconds(70), await reopened.Inherited.DelayAfterNextCheckAsync());
            AssertBoardName(host, customUrl, "Recent photos");
            AssertBoardName(host, inheritedUrl, reopened.Inherited.NiceName);

            Config next = host.Config.Clone();
            next.RecheckTimer = 85;
            Assert.True(await host.ApplySettingsAsync(next));
            Assert.Equal(TimeSpan.FromMinutes(12), reopened.Custom.CurrentInterval);
            Assert.Equal(TimeSpan.FromSeconds(85), reopened.Inherited.CurrentInterval);
            Assert.Equal(TimeSpan.FromMinutes(12), await reopened.Custom.DelayAfterNextCheckAsync());
            Assert.Equal(TimeSpan.FromSeconds(85), await reopened.Inherited.DelayAfterNextCheckAsync());

            await host.ApplyBoardAdmissionAsync(customUrl, new BoardAdmissionPolicy([], null, null, null));
            Assert.Equal(TimeSpan.FromSeconds(85), reopened.Custom.CurrentInterval);
            Assert.Equal(TimeSpan.FromSeconds(85), await reopened.Custom.DelayAfterNextCheckAsync());
            AssertBoardName(host, customUrl, reopened.Custom.NiceName);
        }
    }

    [Fact]
    public async Task ClearingOwnIntervalDuringMetadataAdmissionKeepsTheNextDelayOnTheGlobalInterval()
    {
        using var profile = new TempDirectory();
        SaveConfig(profile.Path);
        var source = new FixtureSource();
        string boardUrl = source.Custom.BoardUri.AbsoluteUri;
        await using DesktopSessionHost host = CreateHost(profile.Path, source);
        await host.StartAsync();
        Assert.True(host.State == DesktopSessionState.Ready, host.StartupError);
        await host.ApplyBoardAdmissionAsync(boardUrl, new BoardAdmissionPolicy([], null, null, 12));
        Assert.Equal(TimeSpan.FromMinutes(12), await source.Custom.FirstDelay.WaitAsync(Deadline));

        CatalogCheck catalog = await source.Custom.HoldNextCheckAsync();
        var write = new IntervalWrite(TimeSpan.FromMinutes(12));
        source.Custom.BeforeIntervalUpdate = write.Hold;
        Task metadata = Task.Run(async () =>
        {
            DownloadQueue queue = Assert.IsType<DownloadQueue>(source.Custom.SessionQueue);
            using IDisposable slot = await queue.AcquireMetadataAsync(source.Name, DownloadPriority.Manual);
        });
        try
        {
            // If metadata admission attempts the old write, let it finish only after the clear.
            await Task.WhenAny(write.Started.Task, metadata).WaitAsync(Deadline);
            await host.ApplyBoardAdmissionAsync(boardUrl, new BoardAdmissionPolicy([], null, null, null));
            Assert.Null(host.GetBoardAdmissionPolicy(boardUrl).WatcherIntervalMinutes);
            write.Resume.TrySetResult();
            await metadata.WaitAsync(Deadline);
        }
        finally
        {
            write.Resume.TrySetResult();
            catalog.Resume.TrySetResult();
        }
        Assert.Equal(TimeSpan.FromSeconds(70), await catalog.Delay.Task.WaitAsync(Deadline));
    }

    [Fact]
    public async Task ChangingRecheckTimerDuringACatalogCheckPreservesTheNextOwnDelay()
    {
        using var profile = new TempDirectory();
        SaveConfig(profile.Path);
        var source = new FixtureSource();
        await using DesktopSessionHost host = CreateHost(profile.Path, source);
        await host.StartAsync();
        Assert.True(host.State == DesktopSessionState.Ready, host.StartupError);
        await host.ApplyBoardAdmissionAsync(source.Custom.BoardUri.AbsoluteUri, new BoardAdmissionPolicy([], null, null, 12));
        Assert.Equal(TimeSpan.FromMinutes(12), await source.Custom.FirstDelay.WaitAsync(Deadline));

        CatalogCheck catalog = await source.Custom.HoldNextCheckAsync();
        Config next = host.Config.Clone();
        next.RecheckTimer = 85;
        source.Custom.AfterIntervalUpdate = interval => catalog.FinishWhenIntervalIsSet(interval, TimeSpan.FromSeconds(next.RecheckTimer));
        try
        {
            Assert.True(await host.ApplySettingsAsync(next));
            Assert.Equal(TimeSpan.FromMinutes(12), await catalog.Delay.Task.WaitAsync(Deadline));
        }
        finally { catalog.Resume.TrySetResult(); }
    }

    private static void SaveConfig(string path) => new Config
    {
        DownloadPath = Path.Combine(path, "media"), AutomaticUpdateChecks = false,
        RecheckTimer = 70, MinimumFreeSpaceGiB = 0, FirstRunSetupVersion = 1, SaveUrlsOnExit = true
    }.SaveConfig(path);

    private static DesktopSessionHost CreateHost(string path, FixtureSource source) => OwnedUiDispatcher.CreateHost(() =>
        new DesktopSessionHost(path, [source],
            updateSessionFactory: (directory, config) => new UpdateSession(directory, config, disableEnvironment: () => "1")));

    private static void AssertBoardName(DesktopSessionHost host, string url, string expected)
    {
        using var row = new BoardRow(Assert.Single(host.GetView().Boards, entry => entry.Snapshot.Url == url));
        Assert.Equal(expected, row.NiceName);
    }

    private sealed class FixtureSource : IImageBoard
    {
        internal FixtureSource()
        {
            Custom = new(this, "p", "Photography");
            Inherited = new(this, "g", "Technology");
        }

        internal FixtureBoard Custom { get; }
        internal FixtureBoard Inherited { get; }
        public string Name => "watcher-fixture";
        public string NiceName => "Watcher fixture";
        public Uri BaseUri => new("https://watcher.test/");
        public bool CanHandle(Uri uri) => uri.Host == BaseUri.Host;
        public bool IsThreadUri(Uri uri) => false;
        public bool IsBoardUri(Uri uri) => uri == Custom.BoardUri || uri == Inherited.BoardUri;
        public IBoard GetBoard(Uri boardUri) => boardUri == Custom.BoardUri ? Custom : Inherited;
        public IThread GetThread(Uri threadUri) => throw new NotSupportedException();
        public HttpClient GetHttpClient() => throw new NotSupportedException();
    }

    private sealed class FixtureBoard(FixtureSource source, string code, string name) : BaseBoard, IBoard
    {
        private readonly object _sync = new();
        private int _checks;
        private (int Check, TaskCompletionSource<TimeSpan> Delay)? _awaited;
        private CatalogCheck? _nextHeldCheck;
        private readonly TaskCompletionSource<TimeSpan> _firstDelay = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<TimeSpan> FirstDelay => _firstDelay.Task;
        internal TimeSpan CurrentInterval => NextMonitoringInterval;
        internal Action<TimeSpan>? BeforeIntervalUpdate { get; set; }
        internal Action<TimeSpan>? AfterIntervalUpdate { get; set; }
        public override IImageBoard ImageBoard => source;
        public override string BoardCode => code;
        public override string Name { get; protected set; } = name;
        public override string NiceName => Name;
        public override Uri BoardUri => new(source.BaseUri, code + "/");

        public override async Task<IEnumerable<IThread>> GetThreadsAsync(CancellationToken cancellationToken = default)
        {
            CatalogCheck? held;
            lock (_sync)
            {
                _checks++;
                held = _nextHeldCheck;
                _nextHeldCheck = null;
                if (held != null) _awaited = (_checks - 1, held.Delay);
            }
            if (held != null)
            {
                held.Started.TrySetResult();
                await held.Resume.Task.WaitAsync(cancellationToken);
            }
            return [];
        }

        internal async Task<CatalogCheck> HoldNextCheckAsync()
        {
            var held = new CatalogCheck();
            lock (_sync) _nextHeldCheck = held;
            Assert.Contains(RequestCheck(), new[] { CheckRequestStatus.Scheduled, CheckRequestStatus.AlreadyScheduled });
            await held.Started.Task.WaitAsync(Deadline);
            return held;
        }

        // The delay the watcher really waits with after a check that began once the caller asked for one.
        internal async Task<TimeSpan> DelayAfterNextCheckAsync()
        {
            TaskCompletionSource<TimeSpan> delay = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync) _awaited = (_checks, delay);
            Assert.Contains(RequestCheck(), new[] { CheckRequestStatus.Scheduled, CheckRequestStatus.AlreadyScheduled });
            return await delay.Task.WaitAsync(Deadline);
        }

        protected override Task WaitForNextCheckAsync(TimeSpan interval, CancellationToken cancellationToken)
        {
            _firstDelay.TrySetResult(interval);
            lock (_sync)
                if (_awaited is { } awaited && _checks > awaited.Check) awaited.Delay.TrySetResult(interval);
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        void IBoard.UpdateMonitoringInterval(TimeSpan interval)
        {
            BeforeIntervalUpdate?.Invoke(interval);
            UpdateMonitoringInterval(interval);
            AfterIntervalUpdate?.Invoke(interval);
        }
    }

    private sealed class CatalogCheck
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<TimeSpan> Delay { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void FinishWhenIntervalIsSet(TimeSpan actual, TimeSpan expected)
        {
            if (actual != expected) return;
            Resume.TrySetResult();
            Delay.Task.WaitAsync(Deadline).GetAwaiter().GetResult();
        }
    }

    private sealed class IntervalWrite(TimeSpan original)
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Hold(TimeSpan interval)
        {
            if (interval == original && Started.TrySetResult())
                Resume.Task.WaitAsync(Deadline).GetAwaiter().GetResult();
        }
    }
}

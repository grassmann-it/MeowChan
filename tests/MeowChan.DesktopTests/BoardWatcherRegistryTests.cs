using System.Net.Http;
using MeowChan.Admission;
using MeowChan.Boards.Base;
using MeowChan.Desktop;
using MeowChan.Desktop.Updates;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class BoardWatcherRegistryTests
{
    [Fact]
    public async Task RestartRestoresWatcherNameAndIntervalsAndGlobalChangesOnlyAffectInheritedBoards()
    {
        using var profile = new TempDirectory();
        var config = new Config
        {
            DownloadPath = Path.Combine(profile.Path, "media"), AutomaticUpdateChecks = false,
            RecheckTimer = 70, MinimumFreeSpaceGiB = 0, FirstRunSetupVersion = 1, SaveUrlsOnExit = true
        };
        config.SaveConfig(profile.Path);
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
        internal TimeSpan CurrentInterval { get; private set; }
        public override IImageBoard ImageBoard => source;
        public override string BoardCode => code;
        public override string Name { get; protected set; } = name;
        public override string NiceName => Name;
        public override Uri BoardUri => new(source.BaseUri, code + "/");

        public override Task<IEnumerable<IThread>> GetThreadsAsync(CancellationToken cancellationToken = default)
        {
            lock (_sync) _checks++;
            return Task.FromResult<IEnumerable<IThread>>([]);
        }

        // The delay the watcher really waits with after a check that began once the caller asked for one.
        internal async Task<TimeSpan> DelayAfterNextCheckAsync()
        {
            TaskCompletionSource<TimeSpan> delay = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync) _awaited = (_checks, delay);
            Assert.Contains(RequestCheck(), new[] { CheckRequestStatus.Scheduled, CheckRequestStatus.AlreadyScheduled });
            return await delay.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }

        protected override Task WaitForNextCheckAsync(TimeSpan interval, CancellationToken cancellationToken)
        {
            lock (_sync)
                if (_awaited is { } awaited && _checks > awaited.Check) awaited.Delay.TrySetResult(interval);
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        void IBoard.StartMonitoring(int intervalInSeconds)
        {
            CurrentInterval = TimeSpan.FromSeconds(intervalInSeconds);
            StartMonitoring(intervalInSeconds);
        }

        void IBoard.UpdateMonitoringInterval(TimeSpan interval)
        {
            CurrentInterval = interval;
            UpdateMonitoringInterval(interval);
        }
    }
}

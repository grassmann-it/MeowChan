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
            Assert.Equal(720, source.Custom.StartedWithSeconds);
            Assert.Equal(70, source.Inherited.StartedWithSeconds);
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
            Assert.Equal(720, reopened.Custom.StartedWithSeconds);
            Assert.Equal(70, reopened.Inherited.StartedWithSeconds);
            AssertBoardName(host, customUrl, "Recent photos");
            AssertBoardName(host, inheritedUrl, reopened.Inherited.NiceName);

            Config next = host.Config.Clone();
            next.RecheckTimer = 85;
            Assert.True(await host.ApplySettingsAsync(next));
            Assert.Equal(TimeSpan.FromMinutes(12), reopened.Custom.CurrentInterval);
            Assert.Equal(TimeSpan.FromSeconds(85), reopened.Inherited.CurrentInterval);

            await host.ApplyBoardAdmissionAsync(customUrl, new BoardAdmissionPolicy([], null, null, null));
            Assert.Equal(TimeSpan.FromSeconds(85), reopened.Custom.CurrentInterval);
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
        internal int StartedWithSeconds { get; private set; }
        internal TimeSpan CurrentInterval { get; private set; }
        public override IImageBoard ImageBoard => source;
        public override string BoardCode => code;
        public override string Name { get; protected set; } = name;
        public override string NiceName => Name;
        public override Uri BoardUri => new(source.BaseUri, code + "/");

        public override Task<IEnumerable<IThread>> GetThreadsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IEnumerable<IThread>>([]);

        void IBoard.StartMonitoring(int intervalInSeconds)
        {
            StartedWithSeconds = intervalInSeconds;
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

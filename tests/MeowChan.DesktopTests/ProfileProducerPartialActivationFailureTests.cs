using System.Reflection;
using MeowChan.Boards.Base;
using MeowChan.Desktop;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

public sealed class ProfileProducerPartialActivationFailureTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private const string FailureMessage = "Synthetic second producer activation failure";
    private const string FirstUrl = "https://partial-activation.test/g/thread/101";
    private const string SecondUrl = "https://partial-activation.test/g/thread/102";

    [Fact]
    public async Task SecondProducerStartFailureDrainsStartedMonitorBeforeFailureCompletionAndLeaseRelease()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.ReachHeldRollbackAsync();

        // No Stop/BeginShutdown call has manufactured this cancellation. It came
        // from the activation rollback after the second provider start threw.
        Assert.False(fixture.Startup.IsCompleted);
        Assert.False(fixture.Scenario.MonitorStopped.Task.IsCompleted);
        Assert.False(fixture.Host.IsProfileReleased);
        fixture.AssertProfileHeld();

        fixture.Scenario.ReleaseDrain.TrySetResult();
        await fixture.Startup.WaitAsync(Deadline);
        await fixture.Scenario.MonitorStopped.Task.WaitAsync(Deadline);
        Assert.Equal(DesktopSessionState.RecoveryFailed, fixture.Host.State);
        Assert.Equal(ProfileAdmissionPhase.Stopping, fixture.Lease.Admission.Phase);
        Assert.Contains(FailureMessage, fixture.Host.StartupError);
        Assert.Equal(0, fixture.ReadyPublications);
        Assert.Empty(RegistryOf(fixture.Host).Threads);
        Assert.Equal(2, fixture.Scenario.StartAttempts);
        Assert.Equal(1, fixture.Scenario.Checks);
        await fixture.Lease.Admission.DrainAsync().WaitAsync(Deadline);

        Assert.Throws<InvalidOperationException>(() =>
        {
            using ProfileAdmissionToken unexpected = fixture.Lease.Admission.EnterOrdinaryWriter();
        });
        Assert.Throws<InvalidOperationException>(() => new WorkStateStore(fixture.Directory.Path, fixture.Lease));
        Assert.Throws<InvalidOperationException>(() => fixture.Work.CommitIntent(fixture.NextIntent()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Session.SaveAsync(new()).WaitAsync(Deadline));
        fixture.AssertStoresUnchanged();

        await fixture.Host.StopAsync().WaitAsync(Deadline);
        Assert.Equal(DesktopSessionState.Stopped, fixture.Host.State);
        Assert.Equal(ProfileAdmissionPhase.Stopped, fixture.Lease.Admission.Phase);
        Assert.False(fixture.Host.IsProfileReleased); // Engine stop is not OS-lease release.
        fixture.AssertProfileHeld();
        await fixture.Host.DisposeAsync().AsTask().WaitAsync(Deadline);
        Assert.True(fixture.Host.IsProfileReleased);
        using ProfileLease reacquired = ProfileLease.Acquire(fixture.Directory.Path);
        Assert.Equal(ProfileAdmissionPhase.Starting, reacquired.Admission.Phase);
        Assert.NotSame(fixture.Lease.Admission, reacquired.Admission);
    }

    [Theory]
    [InlineData("ordinary")]
    [InlineData("session")]
    [InlineData("work-constructor")]
    [InlineData("work-intent")]
    public async Task PartialActivationRollbackRejectsNewWritersBeforeStartedMonitorHasDrained(string writer)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.ReachHeldRollbackAsync();

        // Deliberately test BEFORE calling Host.StopAsync/BeginShutdown and before
        // releasing the descendant. Checking only after StartAsync returns misses
        // the open-kernel interval inside ActivatePreparedProfileProducersAsync.
        switch (writer)
        {
            case "ordinary":
                Assert.Throws<InvalidOperationException>(() =>
                {
                    // Dispose even if this negative assertion exposes an accepted token.
                    using ProfileAdmissionToken unexpected = fixture.Lease.Admission.EnterOrdinaryWriter();
                });
                break;
            case "session":
                await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Session.SaveAsync(new()).WaitAsync(Deadline));
                break;
            case "work-constructor":
                Assert.Throws<InvalidOperationException>(() => new WorkStateStore(fixture.Directory.Path, fixture.Lease));
                break;
            case "work-intent":
                Assert.Throws<InvalidOperationException>(() => fixture.Work.CommitIntent(fixture.NextIntent()));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(writer));
        }
        Assert.Equal(ProfileAdmissionPhase.Stopping, fixture.Lease.Admission.Phase);
        Assert.False(fixture.Lease.Admission.DrainAsync().IsCompleted);
        Assert.False(fixture.Startup.IsCompleted);
        Assert.False(fixture.Scenario.MonitorStopped.Task.IsCompleted);
        Assert.Equal(DesktopSessionState.Starting, fixture.Host.State);
        Assert.Equal(0, fixture.ReadyPublications);
        fixture.AssertStoresUnchanged();
        fixture.AssertProfileHeld();
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Registry RegistryOf(DesktopSessionHost host) =>
        (Registry)typeof(DesktopSessionHost).GetField("_registry", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;

    private sealed class Fixture : IAsyncDisposable
    {
        internal TempDirectory Directory { get; }
        internal ActivationScenario Scenario { get; }
        internal DesktopSessionHost Host { get; }
        internal ProfileLease Lease { get; }
        internal WorkStateStore Work { get; }
        internal SessionStore Session { get; }
        internal Task Startup { get; private set; } = Task.CompletedTask;
        private readonly SyntheticProvider _provider;
        private readonly WorkResource[] _beforeWork;
        private readonly byte[] _beforeSession;
        private int _readyPublications;
        internal int ReadyPublications => Volatile.Read(ref _readyPublications);

        private Fixture(TempDirectory directory, SyntheticProvider provider, ActivationScenario scenario)
        {
            Directory = directory;
            _provider = provider;
            Scenario = scenario;
            ProfileLease? capturedLease = null;
            WorkStateStore? capturedWork = null;
            Host = new(directory.Path, [provider], workStoreFactory: (path, owner) =>
            {
                capturedLease = owner;
                return capturedWork = new WorkStateStore(path, owner);
            });
            Lease = capturedLease!;
            Work = capturedWork!;
            Session = new(directory.Path, Lease);
            _beforeWork = Work.LoadResources().OrderBy(row => row.Url, StringComparer.Ordinal).ToArray();
            _beforeSession = File.ReadAllBytes(Path.Combine(directory.Path, "saved_urls.json"));
            scenario.ReadAdmissionPhase = () => Lease.Admission.Phase;
            scenario.ReadHostState = () => Host.State;
            Host.StateChanged += () =>
            {
                if (Host.State == DesktopSessionState.Ready) Interlocked.Increment(ref _readyPublications);
            };
        }

        internal static async Task<Fixture> CreateAsync()
        {
            TempDirectory directory = new();
            ActivationScenario scenario = new();
            SyntheticProvider provider = new(scenario);
            try
            {
                string media = Path.Combine(directory.Path, "media");
                new Config
                {
                    DownloadsPaused = false, SaveUrlsOnExit = true, AutomaticUpdateChecks = false,
                    RecheckTimer = 60, DownloadPath = media
                }.SaveConfig(directory.Path);
                using (ProfileLease seedLease = ProfileLease.Acquire(directory.Path))
                {
                    WorkStateStore seedWork = new(directory.Path, seedLease);
                    foreach (string url in new[] { FirstUrl, SecondUrl })
                        seedWork.CommitIntent(new WorkResource
                        {
                            Url = url, OutputRoot = media, Generation = 1,
                            Intent = WorkIntent.Watching, IsThread = true, Source = ThreadSourceStateValue.Active
                        });
                    await new SessionStore(directory.Path, seedLease).SaveAsync(new SessionSnapshot
                    {
                        ThreadUrls = [FirstUrl, SecondUrl],
                        DownloadDirectories = new() { [FirstUrl] = media, [SecondUrl] = media }
                    }).WaitAsync(Deadline);
                }
                return new(directory, provider, scenario);
            }
            catch
            {
                provider.Dispose();
                directory.Dispose();
                throw;
            }
        }

        internal async Task ReachHeldRollbackAsync()
        {
            Assert.Equal(0, Scenario.StartAttempts);
            Assert.Equal(ProfileAdmissionPhase.Starting, Lease.Admission.Phase);
            Startup = Host.StartAsync();
            await Scenario.CancellationObserved.Task.WaitAsync(Deadline);
            Assert.Equal(2, Scenario.StartAttempts);
            Assert.Equal(1, Scenario.Checks);
            Assert.True(Scenario.FailureAfterRunningSubset);
            // This is sampled immediately BEFORE the provider throws, not at
            // the later held rollback barrier where new writers must be sealed.
            Assert.Equal(ProfileAdmissionPhase.Ready, Scenario.AdmissionAtFailure);
            Assert.Equal(DesktopSessionState.Starting, Scenario.HostAtFailure);
            Assert.Equal(DesktopSessionState.Starting, Host.State);
            Assert.Equal(0, ReadyPublications);
            Assert.False(Startup.IsCompleted);
            Assert.False(Scenario.MonitorStopped.Task.IsCompleted);
            Assert.False(Host.IsPaused); // Internal rollback must not manufacture a user pause.
            Assert.Equal(0, _provider.Transport.Requests);
        }

        internal WorkResource NextIntent() => _beforeWork[0] with { Generation = 2, Intent = WorkIntent.Suppressed };
        internal void AssertStoresUnchanged()
        {
            Assert.Equal(_beforeWork, Work.LoadResources().OrderBy(row => row.Url, StringComparer.Ordinal).ToArray());
            Assert.Equal(_beforeSession, File.ReadAllBytes(Path.Combine(Directory.Path, "saved_urls.json")));
        }
        internal void AssertProfileHeld() => Assert.Throws<IOException>(() =>
        {
            using ProfileLease unexpected = ProfileLease.Acquire(Directory.Path);
        });

        public async ValueTask DisposeAsync()
        {
            // Open before awaiting cleanup, including when a writer assertion fails.
            Scenario.ReleaseDrain.TrySetResult();
            try
            {
                try { await Startup.WaitAsync(Deadline); }
                finally { await Host.DisposeAsync().AsTask().WaitAsync(Deadline); }
            }
            finally
            {
                _provider.Dispose();
                // Do not manually release a still-owned lease to hide a drain failure.
                if (Host.IsProfileReleased) Directory.Dispose();
            }
        }
    }

    private sealed class ActivationScenario
    {
        internal TaskCompletionSource CheckEntered { get; } = Signal();
        internal TaskCompletionSource CancellationObserved { get; } = Signal();
        internal TaskCompletionSource ReleaseDrain { get; } = Signal();
        internal TaskCompletionSource MonitorStopped { get; } = Signal();
        internal Func<ProfileAdmissionPhase> ReadAdmissionPhase { get; set; } = null!;
        internal Func<DesktopSessionState> ReadHostState { get; set; } = null!;
        internal ProfileAdmissionPhase AdmissionAtFailure { get; private set; }
        internal DesktopSessionState HostAtFailure { get; private set; }
        internal bool FailureAfterRunningSubset { get; private set; }
        private int _starts, _checks;
        internal int StartAttempts => Volatile.Read(ref _starts);
        internal int Checks => Volatile.Read(ref _checks);

        internal void BeforeStart()
        {
            if (Interlocked.Increment(ref _starts) == 1) return;
            // The public provider API is synchronous. This bounded barrier waits
            // only for our offline check, which does not acquire Registry._gate.
            CheckEntered.Task.WaitAsync(Deadline).GetAwaiter().GetResult();
            FailureAfterRunningSubset = Checks == 1;
            AdmissionAtFailure = ReadAdmissionPhase();
            HostAtFailure = ReadHostState();
            throw new IOException(FailureMessage);
        }

        internal async Task CheckAsync(CancellationToken token)
        {
            Interlocked.Increment(ref _checks);
            CheckEntered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                // Cancellation is not completion: hold a real descendant until
                // the test releases it, without polling or elapsed-time evidence.
                await ReleaseDrain.Task.WaitAsync(Deadline);
            }
        }
    }

    private sealed class SyntheticThread(IBoard board, Uri uri, ActivationScenario scenario) : BaseThread, IThread
    {
        private bool _started;
        public override IBoard Board => board;
        public override string ThreadId => uri.Segments[^1];
        public override string NiceName => ThreadId;
        public override Uri ThreadUri => uri;
        public override Task checkThreadAsync(DownloadQueue queue, CancellationToken cancellationToken = default)
            => scenario.CheckAsync(cancellationToken);
        void IThread.StartMonitoring(DownloadQueue queue, string outputDirectory, TimeSpan interval)
        {
            scenario.BeforeStart();
            _started = true;
            base.StartMonitoring(queue, outputDirectory, interval);
        }
        async Task IThread.StopMonitoringAsync()
        {
            await base.StopMonitoringAsync();
            if (_started) scenario.MonitorStopped.TrySetResult();
        }
    }

    private sealed class SyntheticBoard(IImageBoard provider) : BaseBoard
    {
        public override IImageBoard ImageBoard => provider;
        public override string BoardCode => "g";
        public override string Name { get; protected set; } = "Offline activation fixture";
        public override string NiceName => Name;
        public override Uri BoardUri => new(provider.BaseUri, "g/");
        public override Task<IEnumerable<IThread>> GetThreadsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IEnumerable<IThread>>([]);
    }

    private sealed class SyntheticProvider : IImageBoard, IDisposable
    {
        internal RejectNetworkTransport Transport { get; } = new();
        private readonly HttpClient _client;
        private readonly SyntheticBoard _board;
        private readonly Dictionary<string, IThread> _threads;
        internal SyntheticProvider(ActivationScenario scenario)
        {
            _client = new(Transport);
            _board = new(this);
            _threads = new[] { FirstUrl, SecondUrl }.ToDictionary(url => url,
                url => (IThread)new SyntheticThread(_board, new Uri(url), scenario), StringComparer.Ordinal);
        }
        public string Name => "partial-activation";
        public string NiceName => "Offline partial activation fixture";
        public Uri BaseUri => new("https://partial-activation.test/");
        public bool CanHandle(Uri uri) => uri.Scheme == "https" && uri.Host == BaseUri.Host;
        public bool IsThreadUri(Uri uri) => _threads.ContainsKey(uri.AbsoluteUri);
        public bool IsBoardUri(Uri uri) => uri == _board.BoardUri;
        public IBoard GetBoard(Uri uri) => _board;
        public IThread GetThread(Uri uri) => _threads[uri.AbsoluteUri];
        public HttpClient GetHttpClient() => _client;
        public void Dispose() => _client.Dispose();
    }

    private sealed class RejectNetworkTransport : HttpMessageHandler
    {
        private int _requests;
        internal int Requests => Volatile.Read(ref _requests);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            throw new InvalidOperationException("Offline fixture must not send HTTP.");
        }
    }
}

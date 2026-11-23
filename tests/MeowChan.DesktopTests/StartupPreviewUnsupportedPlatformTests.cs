using System.Collections.Concurrent;
using System.Net.Http;
using MeowChan.Boards.FourChan;
using MeowChan.Desktop;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

/// <summary>
/// Injects only the optional cache-read failure, not a platform or a UI dispatcher.
/// The ordinary authoritative SessionStore and host lifecycle remain in use.
/// </summary>
[Collection("Avalonia global state")]
public sealed class StartupPreviewUnsupportedPlatformTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private const string CurrentUrl = "https://boards.4chan.org/g/thread/124";
    private const string CachedUrl = "https://boards.4chan.org/cached/thread/123";
    private const string RestoredTitle = "Authoritative unsupported-preview fixture";
    private const string CachedTitle = "Historical cache only";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedPreviewDoesNotActivateCacheOrBypassOrdinaryRestore(bool failRestore)
    {
        await using Fixture fixture = new(failRestore: failRestore);
        DesktopSessionHost host = fixture.Host;
        int reads = 0;
        host.StartupPreviewReadForTests = token =>
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref reads);
            throw new PlatformNotSupportedException("Synthetic unsupported preview reader");
        };
        Task startup = host.StartAsync();
        try
        {
            // Entering the real store proves the optional failure did not stop startup.
            await fixture.Store.Entered.Task.WaitAsync(Deadline);
            Assert.False(startup.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref reads));
            Assert.Equal(DesktopSessionState.Starting, host.State);
            Assert.Equal(1, fixture.Store.Loads);
            Assert.Null(host.StartupPreview);
            Assert.Equal(0, fixture.PreviewNotifications);
            Assert.Empty(host.GetView().Threads);
            Assert.Empty(host.GetView().Boards);
            Assert.Empty(host.Projection.CheckableTargets);
            Assert.False(host.CanRequestCheck(new(DiagnosticTargetKind.Thread, -1)));
            Assert.False(host.CanRetryMissingMedia(-1));
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.AddAsync(CachedUrl).WaitAsync(Deadline));
            fixture.AssertUnchanged();
            fixture.AssertNoCacheActivation();

            fixture.Store.Release.TrySetResult();
            await startup.WaitAsync(Deadline);
            Assert.Same(startup, host.StartAsync());
            Assert.Equal(1, fixture.Store.Loads);
            Assert.Equal(0, fixture.Store.Saves);
            Assert.Null(host.StartupPreview);
            Assert.Equal(0, fixture.PreviewNotifications);
            Assert.False(host.Projection.IsStale, host.Projection.CaptureError);
            if (failRestore)
            {
                Assert.Equal(DesktopSessionState.RecoveryFailed, host.State);
                Assert.Equal(ObservedStore.FailureMessage, host.StartupError);
                Assert.DoesNotContain(DesktopSessionState.Ready, fixture.States);
                Assert.Empty(host.GetView().Threads);
                await Assert.ThrowsAsync<InvalidOperationException>(() => host.AddAsync(CachedUrl).WaitAsync(Deadline));
            }
            else
            {
                Assert.Equal(DesktopSessionState.Ready, host.State);
                Assert.Null(host.StartupError);
                DesktopThreadEntry thread = Assert.Single(host.GetView().Threads);
                Assert.Equal(CurrentUrl, thread.Snapshot.Url);
                Assert.Equal(RestoredTitle, thread.Snapshot.Title);
                Assert.True(host.IsPaused);
                Assert.Contains(CurrentUrl, fixture.Provider.ThreadUris);
            }
            Assert.Empty(host.GetView().Boards);
            fixture.AssertUnchanged();
            fixture.AssertNoCacheActivation();

            await host.DisposeAsync().AsTask().WaitAsync(Deadline);
            Assert.True(host.IsProfileReleased);
            fixture.AssertNoCacheActivation();
            Assert.Equal(fixture.SessionBytes, File.ReadAllBytes(fixture.SessionPath));
            using ProfileLease lease = ProfileLease.Acquire(fixture.ProfilePath);
            if (failRestore)
                fixture.AssertUnchanged(); // A real recovery failure must not checkpoint.
            else
            {
                StartupPreviewSnapshot checkpoint = new StartupPreviewStore(fixture.ProfilePath, lease).Load()!;
                StartupPreviewRow row = Assert.Single(checkpoint.Rows);
                Assert.Equal(RestoredTitle, row.Title);
                Assert.NotEqual(CachedTitle, row.Title);
            }
        }
        finally
        {
            fixture.ReleaseHeldGates();
        }
    }

    [Theory]
    [InlineData(false)] // The reader observes the actual canceled startup token.
    [InlineData(true)]  // Platform failure arrives after cancellation; the following token check must still win.
    public async Task StopDuringPreviewReadPreservesCancellationAndNeverEntersRestore(bool platformFailure)
    {
        await using Fixture fixture = new();
        DesktopSessionHost host = fixture.Host;
        TaskCompletionSource<CancellationToken> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int afterPreview = 0;
        host.StartupPreviewReadForTests = token =>
        {
            entered.TrySetResult(token);
            // Model an already-entered synchronous read. Only the fixture releases it.
            fixture.ReleaseRead.Task.GetAwaiter().GetResult();
            if (platformFailure) throw new PlatformNotSupportedException("Synthetic late unsupported preview reader");
            token.ThrowIfCancellationRequested();
            return null;
        };
        host.AfterStartupPreviewForTests = _ =>
        {
            Interlocked.Increment(ref afterPreview);
            return Task.CompletedTask;
        };
        Task startup = host.StartAsync();
        try
        {
            CancellationToken token = await entered.Task.WaitAsync(Deadline);
            Assert.True(token.CanBeCanceled);
            TaskCompletionSource canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = token.Register(() => canceled.TrySetResult());
            Task stop = host.StopAsync();
            await canceled.Task.WaitAsync(Deadline);
            Assert.True(token.IsCancellationRequested);
            Assert.False(startup.IsCompleted);
            Assert.False(stop.IsCompleted);
            Assert.Equal(DesktopSessionState.Stopping, host.State);
            Assert.Equal(0, fixture.Store.Loads);
            fixture.AssertUnchanged();

            fixture.ReleaseRead.TrySetResult();
            await startup.WaitAsync(Deadline);
            await stop.WaitAsync(Deadline);
            Assert.Equal(DesktopSessionState.Stopped, host.State);
            Assert.Equal(0, Volatile.Read(ref afterPreview));
            Assert.Equal(0, fixture.Store.Loads);
            Assert.False(fixture.Store.Entered.Task.IsCompleted);
            Assert.DoesNotContain(DesktopSessionState.Ready, fixture.States);
            Assert.DoesNotContain(DesktopSessionState.RecoveryFailed, fixture.States);
            Assert.Null(host.StartupPreview);
            Assert.Equal(0, fixture.PreviewNotifications);
            Assert.Empty(host.GetView().Threads);
            Assert.Empty(host.GetView().Boards);
            Assert.Empty(host.Projection.CheckableTargets);
            fixture.AssertUnchanged();
            fixture.AssertNoCacheActivation();
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.AddAsync(CachedUrl).WaitAsync(Deadline));

            await host.DisposeAsync().AsTask().WaitAsync(Deadline);
            Assert.True(host.IsProfileReleased);
            fixture.AssertUnchanged();
            fixture.AssertNoCacheActivation();
            using ProfileLease reacquired = ProfileLease.Acquire(fixture.ProfilePath);
        }
        finally
        {
            fixture.ReleaseHeldGates();
        }
    }

    [Fact]
    public async Task DisabledRetentionDoesNotInvokePreviewReaderOrAuthoritativeRestore()
    {
        await using Fixture fixture = new(retainSession: false);
        int reads = 0;
        fixture.Host.StartupPreviewReadForTests = _ =>
        {
            Interlocked.Increment(ref reads);
            throw new PlatformNotSupportedException("Disabled preview reader must not run");
        };
        await fixture.Host.StartAsync().WaitAsync(Deadline);
        Assert.Equal(DesktopSessionState.Ready, fixture.Host.State);
        Assert.Equal(0, Volatile.Read(ref reads));
        Assert.Equal(0, fixture.Store.Loads);
        Assert.Null(fixture.Host.StartupPreview);
        Assert.Equal(0, fixture.PreviewNotifications);
        Assert.Empty(fixture.Host.GetView().Threads);
        Assert.Empty(fixture.Host.GetView().Boards);
        fixture.AssertUnchanged();
        fixture.AssertNoCacheActivation();
        await fixture.Host.DisposeAsync().AsTask().WaitAsync(Deadline);
        Assert.True(fixture.Host.IsProfileReleased);
        fixture.AssertUnchanged();
        using ProfileLease reacquired = ProfileLease.Acquire(fixture.ProfilePath);
    }

    [Fact]
    public async Task UnexpectedReaderFailureIsNotHiddenByOptionalPlatformCatch()
    {
        await using Fixture fixture = new();
        const string failure = "Synthetic unexpected reader failure";
        fixture.Host.StartupPreviewReadForTests = _ => throw new InvalidOperationException(failure);
        await fixture.Host.StartAsync().WaitAsync(Deadline);
        Assert.Equal(DesktopSessionState.RecoveryFailed, fixture.Host.State);
        Assert.Equal(failure, fixture.Host.StartupError);
        Assert.Equal(0, fixture.Store.Loads);
        Assert.Null(fixture.Host.StartupPreview);
        Assert.Equal(0, fixture.PreviewNotifications);
        Assert.DoesNotContain(DesktopSessionState.Ready, fixture.States);
        fixture.AssertUnchanged();
        fixture.AssertNoCacheActivation();
        await fixture.Host.DisposeAsync().AsTask().WaitAsync(Deadline);
        Assert.True(fixture.Host.IsProfileReleased);
        fixture.AssertUnchanged();
        using ProfileLease reacquired = ProfileLease.Acquire(fixture.ProfilePath);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TempDirectory _profile = new();
        private readonly NoRequests _transport = new();
        private readonly HttpClient _client;
        private readonly byte[] _cacheBytes;
        private int _previewNotifications;
        internal string ProfilePath => _profile.Path;
        internal string SessionPath => Path.Combine(ProfilePath, "saved_urls.json");
        private string CachePath => Path.Combine(ProfilePath, "startup-preview.json");
        internal byte[] SessionBytes { get; }
        internal ObservedProvider Provider { get; }
        internal ObservedStore Store { get; }
        internal DesktopSessionHost Host { get; }
        internal int PreviewNotifications => Volatile.Read(ref _previewNotifications);
        internal ConcurrentQueue<DesktopSessionState> States { get; } = new();
        internal TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Fixture(bool failRestore = false, bool retainSession = true)
        {
            string media = Path.Combine(ProfilePath, "media");
            new Config { SaveUrlsOnExit = retainSession, DownloadsPaused = true,
                DownloadPath = media, AutomaticUpdateChecks = false }.SaveConfig(ProfilePath);
            using (ProfileLease lease = ProfileLease.Acquire(ProfilePath))
            {
                new SessionStore(ProfilePath, lease).SaveAsync(new SessionSnapshot
                {
                    ThreadUrls = [CurrentUrl],
                    DownloadDirectories = new() { [CurrentUrl] = media }
                }).GetAwaiter().GetResult();
                new StartupPreviewStore(ProfilePath, lease).Save(new(
                    new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero), 1, 0,
                    new[] { new StartupPreviewRow(false, "4chan", "cached", CachedTitle, 9, 8) }));
            }
            SessionBytes = File.ReadAllBytes(SessionPath);
            _cacheBytes = File.ReadAllBytes(CachePath);
            _client = new HttpClient(_transport, disposeHandler: false);
            Provider = new ObservedProvider(_client);
            ObservedStore? store = null;
            Host = OwnedUiDispatcher.CreateHost(() => new(ProfilePath, new IImageBoard[] { Provider },
                (directory, lease) => store = new ObservedStore(new SessionStore(directory, lease), failRestore)));
            Store = store!;
            Assert.Null(Host.Plugins);
            Host.StartupPreviewChanged += () => Interlocked.Increment(ref _previewNotifications);
            Host.StateChanged += () => States.Enqueue(Host.State);
        }

        internal void ReleaseHeldGates()
        {
            ReleaseRead.TrySetResult();
            Store.Release.TrySetResult();
        }

        internal void AssertUnchanged()
        {
            Assert.Equal(_cacheBytes, File.ReadAllBytes(CachePath));
            Assert.Equal(SessionBytes, File.ReadAllBytes(SessionPath));
            Assert.Equal(0, Store.Saves);
        }

        internal void AssertNoCacheActivation()
        {
            // Provider descriptor probing may construct /b/thread/123456; it is not a poll.
            Assert.DoesNotContain(CachedUrl, Provider.ThreadUris);
            Assert.Equal(0, Provider.Polls);
            Assert.Equal(0, _transport.Requests);
            Assert.Equal(0, Host.ReceivedMediaBytes);
            Assert.Equal(0, Host.Projection.MediaConcurrency.Active);
            Assert.Empty(Host.Projection.TargetWaitStates);
        }

        public async ValueTask DisposeAsync()
        {
            ReleaseHeldGates();
            await Host.DisposeAsync().AsTask().WaitAsync(Deadline);
            // A failed host drain retains the synthetic resources for diagnosis.
            _client.Dispose();
            _transport.Dispose();
            _profile.Dispose();
        }
    }

    private sealed class ObservedStore(SessionStore inner, bool failRestore) : ISessionStore
    {
        internal const string FailureMessage = "Synthetic authoritative restore failure after unsupported preview";
        private int _loads, _saves;
        internal int Loads => Volatile.Read(ref _loads);
        internal int Saves => Volatile.Read(ref _saves);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SessionLoadResult> LoadAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _loads);
            Entered.TrySetResult();
            await Release.Task;
            if (failRestore) throw new IOException(FailureMessage);
            return await inner.LoadAsync(cancellationToken);
        }

        public Task SaveAsync(SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _saves);
            return inner.SaveAsync(snapshot, cancellationToken);
        }
    }

    private sealed class ObservedProvider(HttpClient client) : FourChanImageBoard(client)
    {
        private int _polls;
        internal int Polls => Volatile.Read(ref _polls);
        internal ConcurrentQueue<string> ThreadUris { get; } = new();
        public override IThread GetThread(Uri uri)
        {
            ThreadUris.Enqueue(uri.AbsoluteUri);
            return new ObservedThread(base.GetBoard(new Uri(BaseUri, uri.Segments[1])), uri, this)
                { Title = RestoredTitle };
        }
        private sealed class ObservedThread(IBoard board, Uri uri, ObservedProvider provider) : FourChanThread(board, uri)
        {
            public override Task checkThreadAsync(DownloadQueue queue, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref provider._polls);
                throw new InvalidOperationException("Unsupported preview must not activate a provider poll.");
            }
        }
    }

    private sealed class NoRequests : HttpMessageHandler
    {
        private int _requests;
        internal int Requests => Volatile.Read(ref _requests);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            throw new InvalidOperationException("Unsupported-preview fixture attempted HTTP: " + request.RequestUri);
        }
    }
}

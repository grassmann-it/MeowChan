using System.Collections.Concurrent;
using System.Net.Http;
using MeowChan.Boards.FourChan;
using MeowChan.Desktop;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

/// <summary>
/// Cross-boundary regressions: cached text stays passive while a real SessionStore
/// load is held, then only the authoritative result may become watches/checkpoints.
/// These are host tests, not dispatcher, native-window or startup-latency measurements.
/// Basic cache validation and stop-at-preview coverage live in StartupPreviewHostTests.
/// </summary>
public sealed class StartupPreviewResponsivenessRegressionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static readonly DateTimeOffset Captured = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private const string OldUrl = "https://boards.4chan.org/cached/thread/123";
    private const string CachedBoardUrl = "https://boards.4chan.org/cached/";
    private const string CachedOldTitle = "Cached old title";
    private const string CachedOldBoardTitle = "Cached old board";
    private const string CurrentUrl = "https://boards.4chan.org/g/thread/124";
    private const string CurrentBoardUrl = "https://boards.4chan.org/g/";
    private const string RestoredTitle = "Authoritative fixture title";

    [Theory]
    [InlineData(false)] // No pause safety net: cached rows still must not start any work.
    [InlineData(true)]  // Real watches restore paused, independently of cached labels/counts.
    public async Task HeldAuthoritativeRestoreKeepsPreviewPassiveAndCheckpointsOnlyTheRealResult(bool restoreWatches)
    {
        await using Fixture fixture = new(restoreWatches, paused: restoreWatches);
        DesktopSessionHost host = fixture.Host;
        Task startup = host.StartAsync();
        try
        {
            await fixture.PreviewEntered.Task.WaitAsync(Deadline);
            Assert.False(startup.IsCompleted);
            Assert.Equal(0, fixture.Store.Loads);
            await AssertPassiveReadsCompleteAsync(fixture, DesktopSessionState.Starting);
            fixture.AssertCacheAndSessionUnchanged();

            fixture.ReleasePreview.TrySetResult();
            await fixture.Store.Entered.Task.WaitAsync(Deadline);
            Assert.False(startup.IsCompleted);
            Assert.Equal(1, fixture.Store.Loads);
            // The registry is inside LoadAsync here. Never force CaptureProjectionAsync:
            // UI predicates/GetView must read the last passive frame without waiting on it.
            await AssertPassiveReadsCompleteAsync(fixture, DesktopSessionState.Starting);
            fixture.AssertCacheAndSessionUnchanged();

            fixture.Store.Release.TrySetResult();
            await startup.WaitAsync(Deadline);
            Assert.Same(startup, host.StartAsync());
            Assert.Equal(DesktopSessionState.Ready, host.State);
            DesktopSessionProjection ready = host.Projection;
            Assert.Equal(DesktopSessionState.Ready, ready.View.State);
            Assert.False(ready.IsStale, ready.CaptureError);
            Assert.Equal(restoreWatches, ready.IsPaused);
            Assert.Equal(1, fixture.Store.Loads);
            Assert.Equal(0, fixture.Store.Saves);
            Assert.Equal(CachedOldTitle, host.StartupPreview!.Rows[0].Title); // separate historical channel
            Assert.DoesNotContain(ready.View.Threads, entry => entry.Snapshot.Url == OldUrl);
            if (restoreWatches)
            {
                DesktopThreadEntry thread = Assert.Single(ready.View.Threads);
                DesktopBoardEntry board = Assert.Single(ready.View.Boards);
                Assert.Equal(CurrentUrl, thread.Snapshot.Url);
                Assert.Equal(RestoredTitle, thread.Snapshot.Title);
                Assert.Equal(CurrentBoardUrl, board.Snapshot.Url);
                Assert.Equal(0, thread.Snapshot.KnownMedia);
                Assert.Equal(0, thread.Snapshot.CompletedMedia);
                Assert.Equal(0, thread.Snapshot.Downloads.Queued);
                Assert.Equal(0, thread.Snapshot.Downloads.Active);
                // A real paused watch is not a cached row: its check predicate follows
                // pause, and retry is unavailable because there is no missing real media.
                Assert.False(host.CanRequestCheck(new(DiagnosticTargetKind.Thread, thread.WatchId)));
                Assert.False(host.CanRetryMissingMedia(thread.WatchId));
                Assert.Contains(CurrentUrl, fixture.Provider.ThreadUris);
                Assert.Contains(CurrentBoardUrl, fixture.Provider.BoardUris);
            }
            else
            {
                Assert.Empty(ready.View.Threads);
                Assert.Empty(ready.View.Boards);
                Assert.Empty(ready.CheckableTargets);
                Assert.Empty(host.GetThreadWorkSnapshots());
            }
            fixture.AssertNoCacheActivation();
            fixture.AssertCacheAndSessionUnchanged();

            await host.DisposeAsync().AsTask().WaitAsync(Deadline);
            fixture.AssertNoCacheActivation(); // includes producer/queue drain, not a timing sample
            Assert.True(host.IsProfileReleased);
            Assert.Equal(fixture.SessionBytes, File.ReadAllBytes(fixture.SessionPath));
            using ProfileLease lease = ProfileLease.Acquire(fixture.ProfilePath);
            StartupPreviewSnapshot checkpoint = new StartupPreviewStore(fixture.ProfilePath, lease).Load()!;
            Assert.Equal(restoreWatches ? 1 : 0, checkpoint.ThreadCount);
            Assert.Equal(restoreWatches ? 1 : 0, checkpoint.BoardCount);
            Assert.Equal(restoreWatches ? 2 : 0, checkpoint.Rows.Count);
            Assert.DoesNotContain(checkpoint.Rows, row => row.Title == CachedOldTitle || row.Title == CachedOldBoardTitle);
            if (restoreWatches)
            {
                StartupPreviewRow thread = Assert.Single(checkpoint.Rows, row => !row.IsBoard);
                Assert.Equal(RestoredTitle, thread.Title);
                Assert.Equal(0, thread.KnownMedia);
                Assert.Equal(0, thread.CompletedMedia);
                Assert.All(checkpoint.Rows, row => Assert.Equal("g", row.Board));
            }
        }
        finally
        {
            // A failed assertion must not strand the host on either fixture-owned gate.
            fixture.ReleasePreview.TrySetResult();
            fixture.Store.Release.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false, true)] // Failure before shutdown: RecoveryFailed, never fake Ready.
    [InlineData(true, false)] // Stop cancels startup, but an entered real load still drains.
    [InlineData(true, true)]  // A late load failure must not replace Stopping with RecoveryFailed.
    public async Task EnteredRestoreFailureOrCancellationPreservesCacheAndNeverPublishesReady(bool stopDuringLoad, bool failLoad)
    {
        // Authoritative URLs are nonempty and downloads are unpaused. If cached text
        // became watch intent, pause would not hide the unwanted producer activation.
        await using Fixture fixture = new(restoreWatches: true, paused: false, failLoad: failLoad);
        DesktopSessionHost host = fixture.Host;
        ConcurrentQueue<DesktopSessionState> states = new();
        void ObserveState() => states.Enqueue(host.State);
        host.StateChanged += ObserveState;
        Task startup = host.StartAsync();
        try
        {
            await fixture.PreviewEntered.Task.WaitAsync(Deadline);
            fixture.ReleasePreview.TrySetResult();
            await fixture.Store.Entered.Task.WaitAsync(Deadline);
            Assert.False(startup.IsCompleted);
            await AssertPassiveReadsCompleteAsync(fixture, DesktopSessionState.Starting);

            Task stop = Task.CompletedTask;
            if (stopDuringLoad)
            {
                TaskCompletionSource canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Assert.True(fixture.StartupToken.CanBeCanceled);
                using CancellationTokenRegistration registration = fixture.StartupToken.Register(() => canceled.TrySetResult());
                stop = host.StopAsync();
                await canceled.Task.WaitAsync(Deadline); // actual shutdown-worker progress
                Assert.True(fixture.StartupToken.IsCancellationRequested);
                Assert.False(startup.IsCompleted);
                Assert.False(stop.IsCompleted); // Store gate is deliberately NOT bound to startup cancellation.
                await AssertPassiveReadsCompleteAsync(fixture, DesktopSessionState.Stopping);
                fixture.AssertCacheAndSessionUnchanged();
            }

            fixture.Store.Release.TrySetResult();
            // StartAsync reports failures via State/StartupError; it has no token overload
            // and does not return a canceled/faulted task for these lifecycle outcomes.
            await startup.WaitAsync(Deadline);
            if (stopDuringLoad) await stop.WaitAsync(Deadline);
            DesktopSessionState expected = stopDuringLoad ? DesktopSessionState.Stopped : DesktopSessionState.RecoveryFailed;
            Assert.Equal(expected, host.State);
            Assert.Equal(expected, host.GetView().State);
            Assert.False(host.Projection.IsStale, host.Projection.CaptureError);
            Assert.Equal(1, fixture.Store.Loads);
            Assert.Equal(0, fixture.Store.Saves);
            Assert.Same(startup, host.StartAsync());
            Assert.DoesNotContain(DesktopSessionState.Ready, states);
            Assert.Equal(CachedOldTitle, host.StartupPreview!.Rows[0].Title);
            if (stopDuringLoad) Assert.DoesNotContain(DesktopSessionState.RecoveryFailed, states);
            if (failLoad)
            {
                Assert.Contains(ControlledStore.FailureMessage, host.StartupError);
                Assert.Equal(host.StartupError, host.GetView().StartupError);
            }
            await AssertPassiveReadsCompleteAsync(fixture, expected, allowRetainedAuthoritative: stopDuringLoad && !failLoad);
            fixture.AssertCacheAndSessionUnchanged();

            await host.DisposeAsync().AsTask().WaitAsync(Deadline);
            Assert.Equal(DesktopSessionState.Stopped, host.State);
            Assert.True(host.IsProfileReleased);
            Assert.DoesNotContain(DesktopSessionState.Ready, states);
            if (stopDuringLoad) Assert.DoesNotContain(DesktopSessionState.RecoveryFailed, states);
            fixture.AssertNoCacheActivation();
            fixture.AssertCacheAndSessionUnchanged(); // neither failed nor interrupted restore checkpoints
            using ProfileLease lease = ProfileLease.Acquire(fixture.ProfilePath);
            Assert.Equal(Captured, new StartupPreviewStore(fixture.ProfilePath, lease).Load()!.CapturedAtUtc);
        }
        finally
        {
            fixture.ReleasePreview.TrySetResult();
            fixture.Store.Release.TrySetResult();
            host.StateChanged -= ObserveState;
        }
    }

    private static async Task AssertPassiveReadsCompleteAsync(Fixture fixture, DesktopSessionState expected, bool allowRetainedAuthoritative = false)
    {
        DesktopSessionHost host = fixture.Host;
        // Run the reads separately so a regression to a lock-taking accessor trips
        // the deadline rather than synchronously preventing gate release/cleanup.
        Task probe = Task.Run(() =>
        {
            Assert.Equal(expected, host.State);
            Assert.Equal(expected, host.GetView().State);
            DesktopSessionView view = host.GetView();
            if (allowRetainedAuthoritative)
            {
                // A successful entered restore may be the last complete capture before
                // projection admission closes. Retention is bounded to the authoritative
                // fixture identities; cache or any other identity is never acceptable.
                Assert.Equal(DesktopSessionState.Stopped, expected);
                Assert.InRange(view.Threads.Count, 0, 1);
                Assert.InRange(view.Boards.Count, 0, 1);
                Assert.InRange(host.Projection.CheckableTargets.Count, 0, 2);
                Assert.All(view.Threads, entry =>
                {
                    Assert.Equal(CurrentUrl, entry.Snapshot.Url);
                    Assert.Equal(RestoredTitle, entry.Snapshot.Title);
                });
                Assert.All(view.Boards, entry =>
                    Assert.Equal(CurrentBoardUrl, entry.Snapshot.Url));
                Assert.All(host.Projection.CheckableTargets, target =>
                {
                    Assert.True(target.Kind is DiagnosticTargetKind.Thread or DiagnosticTargetKind.Board);
                    Assert.Contains(target.WatchId, target.Kind == DiagnosticTargetKind.Thread
                        ? view.Threads.Select(entry => entry.WatchId)
                        : view.Boards.Select(entry => entry.WatchId));
                });
            }
            else
            {
                // Held restore, cache-only, and failed restore paths retain the strict
                // passive empty contract: no authoritative rows are admitted yet.
                Assert.Empty(view.Threads);
                Assert.Empty(view.Boards);
                Assert.Empty(host.Projection.CheckableTargets);
            }
            StartupPreviewSnapshot preview = Assert.IsType<StartupPreviewSnapshot>(host.StartupPreview);
            Assert.Equal(7, preview.ThreadCount);
            Assert.Equal(4, preview.BoardCount);
            Assert.Equal(2, preview.Rows.Count); // totals need not equal the bounded text slice
            Assert.Equal(CachedOldTitle, preview.Rows[0].Title);
            Assert.False(host.CanRequestCheck(new(DiagnosticTargetKind.Thread, -1)));
            Assert.False(host.CanRequestCheck(new(DiagnosticTargetKind.Board, -1)));
            Assert.False(host.CanRetryMissingMedia(-1));
            if (expected is DesktopSessionState.Starting or DesktopSessionState.RecoveryFailed)
                Assert.Empty(host.GetThreadWorkSnapshots());
            // A load accepted before Stop may still record authoritative work during
            // drain. That is not cache authority and must never activate a producer.
            Assert.All(host.GetThreadWorkSnapshots(), work => Assert.Equal(CurrentUrl, work.Url));
            fixture.AssertNoCacheActivation();
        });
        try
        {
            await probe.WaitAsync(Deadline);
        }
        catch
        {
            // WaitAsync only stops waiting; it does not cancel a synchronous accessor.
            // Let any blocked read finish before the caller can dispose its fixture.
            fixture.ReleasePreview.TrySetResult();
            fixture.Store.Release.TrySetResult();
            try { await probe; }
            catch (Exception) { /* Observe the probe; preserve the original failure/timeout. */ }
            throw;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.AddAsync(OldUrl).WaitAsync(Deadline));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.RemoveThreadAsync(-1).WaitAsync(Deadline));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.RetryMissingMediaAsync(-1).WaitAsync(Deadline));
        // Terminal admission must reject real retained identities too, not only a
        // synthetic invalid ID. This remains true even when a terminal frame retains
        // the last authoritative capture.
        foreach (DesktopThreadEntry thread in host.GetView().Threads)
        {
            Assert.False(host.CanRequestCheck(new(DiagnosticTargetKind.Thread, thread.WatchId)));
            Assert.False(host.CanRetryMissingMedia(thread.WatchId));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => host.RemoveThreadAsync(thread.WatchId).WaitAsync(Deadline));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => host.RetryMissingMediaAsync(thread.WatchId).WaitAsync(Deadline));
        }
        foreach (DiagnosticTarget target in host.Projection.CheckableTargets
            .Concat(host.GetView().Threads.Select(entry => new DiagnosticTarget(DiagnosticTargetKind.Thread, entry.WatchId)))
            .Concat(host.GetView().Boards.Select(entry => new DiagnosticTarget(DiagnosticTargetKind.Board, entry.WatchId)))
            .Distinct())
        {
            Assert.False(host.CanRequestCheck(target));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => host.RequestCheckAsync(target).WaitAsync(Deadline));
        }
        foreach (DesktopBoardEntry board in host.GetView().Boards)
        {
            Assert.False(host.CanRequestCheck(new(DiagnosticTargetKind.Board, board.WatchId)));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => host.RemoveBoardAsync(board.WatchId).WaitAsync(Deadline));
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TempDirectory _profile = new();
        private readonly NoRequests _transport = new();
        private readonly HttpClient _client;
        internal string ProfilePath => _profile.Path;
        internal string SessionPath => Path.Combine(ProfilePath, "saved_urls.json");
        private string CachePath => Path.Combine(ProfilePath, "startup-preview.json");
        internal byte[] SessionBytes { get; }
        private byte[] CacheBytes { get; }
        internal ObservedProvider Provider { get; }
        internal ControlledStore Store { get; }
        internal DesktopSessionHost Host { get; }
        internal CancellationToken StartupToken { get; private set; }
        internal TaskCompletionSource PreviewEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleasePreview { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Fixture(bool restoreWatches, bool paused, bool failLoad = false)
        {
            string media = Path.Combine(ProfilePath, "media");
            new Config { SaveUrlsOnExit = true, DownloadsPaused = paused, DownloadPath = media,
                AutomaticUpdateChecks = false }.SaveConfig(ProfilePath);
            using (ProfileLease lease = ProfileLease.Acquire(ProfilePath))
            {
                SessionSnapshot session = restoreWatches ? new SessionSnapshot
                {
                    ThreadUrls = [CurrentUrl], BoardUrls = [CurrentBoardUrl],
                    DownloadDirectories = new() { [CurrentUrl] = media, [CurrentBoardUrl] = media }
                } : new SessionSnapshot();
                new SessionStore(ProfilePath, lease).SaveAsync(session).GetAwaiter().GetResult();
                new StartupPreviewStore(ProfilePath, lease).Save(new(Captured, 7, 4,
                    new[] { new StartupPreviewRow(false, "4chan", "cached", CachedOldTitle, 99, 88),
                        new StartupPreviewRow(true, "4chan", "cached", CachedOldBoardTitle, 0, 0) }));
            }
            SessionBytes = File.ReadAllBytes(SessionPath);
            CacheBytes = File.ReadAllBytes(CachePath);
            _client = new HttpClient(_transport);
            Provider = new ObservedProvider(_client);
            ControlledStore? store = null;
            Host = new DesktopSessionHost(ProfilePath, new IImageBoard[] { Provider },
                (directory, lease) => store = new ControlledStore(new SessionStore(directory, lease), failLoad));
            Store = store!;
            Assert.Null(Host.Plugins); // injected provider set excludes plugin discovery/worker startup
            Host.AfterStartupPreviewForTests = async token =>
            {
                StartupToken = token;
                PreviewEntered.TrySetResult();
                await ReleasePreview.Task.WaitAsync(token);
            };
        }

        internal void AssertCacheAndSessionUnchanged()
        {
            Assert.Equal(CacheBytes, File.ReadAllBytes(CachePath));
            Assert.Equal(SessionBytes, File.ReadAllBytes(SessionPath));
            Assert.Equal(0, Store.Saves);
        }

        internal void AssertNoCacheActivation()
        {
            // Host admission-example capture may legitimately call GetThread/GetBoard
            // for /b/thread/123456. Object creation alone is NOT provider activation.
            Assert.DoesNotContain(OldUrl, Provider.ThreadUris);
            Assert.DoesNotContain(CachedBoardUrl, Provider.BoardUris);
            Assert.Equal(0, Provider.Polls);
            Assert.Equal(0, _transport.Requests);
            Assert.Equal(0, Host.ReceivedMediaBytes);
            Assert.Equal(0, Host.Projection.MediaConcurrency.Active);
            Assert.Empty(Host.Projection.TargetWaitStates);
        }

        public async ValueTask DisposeAsync()
        {
            ReleasePreview.TrySetResult();
            Store.Release.TrySetResult();
            await Host.DisposeAsync().AsTask().WaitAsync(Deadline);
            // If the host fails to drain, retain its synthetic directory for diagnosis
            // rather than deleting files that could still be owned by background work.
            _client.Dispose();
            _profile.Dispose();
        }
    }

    private sealed class ControlledStore(SessionStore inner, bool failLoad) : ISessionStore
    {
        internal const string FailureMessage = "Synthetic startup-preview authoritative restore failure";
        private int _loads, _saves;
        internal int Loads => Volatile.Read(ref _loads);
        internal int Saves => Volatile.Read(ref _saves);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SessionLoadResult> LoadAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _loads);
            Entered.TrySetResult();
            // Registry.RestoreAdmittedAsync currently calls LoadAsync() without its
            // startup token. Model already-entered noncancelable I/O, not a fictional overload.
            await Release.Task;
            if (failLoad) throw new IOException(FailureMessage);
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
        internal ConcurrentQueue<string> BoardUris { get; } = new();
        public override IThread GetThread(Uri uri)
        {
            ThreadUris.Enqueue(uri.AbsoluteUri);
            return new ObservedThread(base.GetBoard(new Uri(BaseUri, uri.Segments[1])), uri, this)
                { Title = RestoredTitle };
        }
        public override IBoard GetBoard(Uri uri)
        {
            BoardUris.Enqueue(uri.AbsoluteUri);
            return new ObservedBoard(this, uri);
        }
        private void UnexpectedPoll()
        {
            Interlocked.Increment(ref _polls);
            throw new InvalidOperationException("Display cache must not activate a provider poll.");
        }
        private sealed class ObservedThread(IBoard board, Uri uri, ObservedProvider provider) : FourChanThread(board, uri)
        {
            public override Task checkThreadAsync(DownloadQueue queue, CancellationToken cancellationToken = default)
            { provider.UnexpectedPoll(); return Task.CompletedTask; }
        }
        private sealed class ObservedBoard(ObservedProvider provider, Uri uri) : FourChanBoard(provider, uri)
        {
            public override Task<IEnumerable<IThread>> GetThreadsAsync(CancellationToken cancellationToken = default)
            { provider.UnexpectedPoll(); return Task.FromResult<IEnumerable<IThread>>(Array.Empty<IThread>()); }
        }
    }

    private sealed class NoRequests : HttpMessageHandler
    {
        private int _requests;
        internal int Requests => Volatile.Read(ref _requests);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            throw new InvalidOperationException("Startup-preview fixture attempted HTTP: " + request.RequestUri);
        }
    }
}

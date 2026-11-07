using System.ComponentModel;
using MeowChan.Downloader;
using MeowChan.Http;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.SourceAdapter.Tests;

// Intentionally in the non-IVT consumer assembly: startup authority comes only
// from Registry.StartAsync, never reflection, admission tokens or a host helper.
public sealed class PublicStandaloneStartupRaceTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(15);
    private const string BoardUrl = "https://startup-races.invalid/g/";

    [Theory]
    [InlineData(true, false)] // Cancellation alone must drain, including its internal Stop.
    [InlineData(false, true)] // Stop alone must cancel and join the real bootstrap work.
    [InlineData(true, true)] // Explicit Stop joins an already-cancelled bootstrap.
    public async Task CancelOrStopDuringLoadJoinsIgnoringStoreAndNeverActivates(bool cancel, bool stop)
    {
        using TemporaryProfile profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        using CancellationTokenSource cancellation = new();
        ProbeProvider provider = new();
        GatedSessionStore store = new(Snapshot(profile.Path));
        Registry registry = CreateRegistry(profile.Path, provider, store);
        Task? startup = null;
        Task? stopping = null;
        try
        {
            startup = registry.StartAsync(lease, cancellation.Token);
            await store.Entered.Task.WaitAsync(Watchdog);
            Assert.False(store.Returned.Task.IsCompleted);
            Assert.False(startup.IsCompleted);
            Assert.Equal(0, provider.Board.Starts);
            await Assert.ThrowsAsync<InvalidOperationException>(() => registry.AddAsync(BoardUrl).WaitAsync(Watchdog));

            // Cancel() returns after the startup cancellation callback; StopAsync
            // closes/seals synchronously. No sleep/poll/timeout establishes order.
            if (cancel) cancellation.Cancel();
            if (stop) stopping = registry.StopAsync();
            Assert.False(startup.IsCompleted);
            if (stopping != null) Assert.False(stopping.IsCompleted);
            Assert.False(store.Returned.Task.IsCompleted);
            Assert.Same(startup, registry.StartAsync(lease));
            Assert.Equal(1, store.Loads);
            Assert.Equal(0, provider.Board.Starts);

            // Independent public store admission proves the profile was sealed
            // BEFORE the ignoring Load returns, not merely this Registry closed.
            SessionStore independent = new(profile.Path, lease);
            await Assert.ThrowsAsync<InvalidOperationException>(() => independent.SaveAsync(new SessionSnapshot()).WaitAsync(Watchdog));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => registry.AddAsync(BoardUrl).WaitAsync(Watchdog));

            store.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup.WaitAsync(Watchdog));
            if (stopping != null) await stopping.WaitAsync(Watchdog);
            await store.LoadTask!.WaitAsync(Watchdog);
            Assert.True(startup.IsCanceled);
            Assert.True(store.Returned.Task.IsCompletedSuccessfully);
            Assert.Equal(0, provider.Board.Starts); // Cumulative: catches transient late activation too.
            Assert.Empty(registry.Boards);
            Assert.Empty(registry.Threads);

            Task retained = registry.StartAsync(lease);
            Assert.Same(startup, retained);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retained.WaitAsync(Watchdog));
            await registry.StopAsync().WaitAsync(Watchdog);
            Assert.Equal(1, store.Loads);
            Assert.Equal(0, provider.Board.Starts);
            await Assert.ThrowsAsync<InvalidOperationException>(() => independent.SaveAsync(new SessionSnapshot()).WaitAsync(Watchdog));
        }
        finally
        {
            await ReleaseAndJoinAsync(registry, store, startup, stopping);
        }
    }

    [Fact]
    public async Task PreCancelledFirstCallDoesNotClaimAndLaterValidStartWorks()
    {
        using TemporaryProfile profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        ProbeProvider provider = new();
        GatedSessionStore store = new(Snapshot(profile.Path));
        Registry registry = CreateRegistry(profile.Path, provider, store);
        Task? startup = null;
        try
        {
            // Capture both a synchronous rejection and a cancelled returned Task.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => (startup = registry.StartAsync(lease, cancellation.Token)).WaitAsync(Watchdog));
            Assert.Equal(0, store.Loads);
            Assert.Equal(0, provider.Board.Starts);
            Assert.False(store.Entered.Task.IsCompleted);
            Assert.False(File.Exists(Path.Combine(profile.Path, "archive.db")));

            startup = registry.StartAsync(lease);
            await store.Entered.Task.WaitAsync(Watchdog);
            Assert.False(startup.IsCompleted);
            Assert.Equal(1, store.Loads);
            Assert.Equal(0, provider.Board.Starts);
            store.Release.TrySetResult();
            await startup.WaitAsync(Watchdog);
            Assert.True(startup.IsCompletedSuccessfully);
            Assert.Same(provider.Board, Assert.Single(registry.Boards));
            Assert.Equal(1, provider.Board.Starts);
            Assert.Equal(AdmissionStatus.AlreadyPresent, (await registry.AddAsync(BoardUrl).WaitAsync(Watchdog)).Status);
        }
        finally
        {
            await ReleaseAndJoinAsync(registry, store, startup);
        }
    }

    [Fact]
    public async Task ClaimedLoadFaultIsRetainedAfterCleanupAndCannotRetry()
    {
        using TemporaryProfile profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        using CancellationTokenSource laterCancellation = new();
        ProbeProvider provider = new();
        IOException failure = new("Synthetic session load failure after startup claim.");
        GatedSessionStore store = new(Snapshot(profile.Path), failure);
        Registry registry = CreateRegistry(profile.Path, provider, store);
        Task? startup = null;
        try
        {
            startup = registry.StartAsync(lease);
            await store.Entered.Task.WaitAsync(Watchdog);
            Assert.False(startup.IsCompleted);
            Assert.Same(startup, registry.StartAsync(lease));
            store.Release.TrySetResult();
            IOException observed = await Assert.ThrowsAsync<IOException>(() => startup.WaitAsync(Watchdog));
            Assert.Same(failure, observed);
            Assert.True(startup.IsFaulted);
            await registry.StopAsync().WaitAsync(Watchdog);
            Assert.True(store.Returned.Task.IsCompletedSuccessfully);

            // A later cancelled token must not replace the retained original fault.
            laterCancellation.Cancel();
            Task retained = registry.StartAsync(lease, laterCancellation.Token);
            Assert.Same(startup, retained);
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => retained.WaitAsync(Watchdog)));
            Assert.Equal(1, store.Loads);
            Assert.Equal(0, provider.Board.Starts);
            Assert.Empty(registry.Boards);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => registry.AddAsync(BoardUrl).WaitAsync(Watchdog));
            SessionStore independent = new(profile.Path, lease);
            await Assert.ThrowsAsync<InvalidOperationException>(() => independent.SaveAsync(new SessionSnapshot()).WaitAsync(Watchdog));
        }
        finally
        {
            await ReleaseAndJoinAsync(registry, store, startup);
        }
    }

    [Fact]
    public async Task StopAfterSuccessfulStartLeavesLeaseWritableAndSuccessorRegistryOrdinary()
    {
        using TemporaryProfile profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        ProbeProvider provider = new();
        GatedSessionStore store = new(Snapshot(profile.Path));
        Registry registry = CreateRegistry(profile.Path, provider, store);
        Task? startup = null;
        try
        {
            startup = registry.StartAsync(lease);
            await store.Entered.Task.WaitAsync(Watchdog);
            store.Release.TrySetResult();
            await startup.WaitAsync(Watchdog);
            Assert.Equal(1, provider.Board.Starts);
            await registry.StopAsync().WaitAsync(Watchdog);
            Assert.Empty(registry.Boards);
            Assert.True(provider.Board.Stops > 0);
            Assert.Same(startup, registry.StartAsync(lease));
            Assert.True(startup.IsCompletedSuccessfully);
        }
        finally
        {
            await ReleaseAndJoinAsync(registry, store, startup);
        }

        // Even DisposeAsync must not dispose/seal the caller-owned successful lease.
        SessionStore independent = new(profile.Path, lease);
        await independent.SaveAsync(Snapshot(profile.Path)).WaitAsync(Watchdog);
        Assert.Equal(new[] { BoardUrl }, (await independent.LoadAsync().WaitAsync(Watchdog)).Snapshot.BoardUrls);

        ProbeProvider successorProvider = new();
        Registry successor = CreateRegistry(profile.Path, successorProvider, independent);
        try
        {
            // Deliberately NOT StartAsync: the already-Ready lease uses the public
            // ordinary path. This would reject if Stop reset it to Starting/sealed it.
            await successor.RestoreAsync().WaitAsync(Watchdog);
            Assert.Same(successorProvider.Board, Assert.Single(successor.Boards));
            Assert.Equal(1, successorProvider.Board.Starts);
            Assert.Equal(AdmissionStatus.AlreadyPresent, (await successor.AddAsync(BoardUrl).WaitAsync(Watchdog)).Status);
        }
        finally
        {
            await successor.StopAsync().WaitAsync(Watchdog);
            await successor.DisposeAsync().AsTask().WaitAsync(Watchdog);
        }
    }

    private static Registry CreateRegistry(string root, ProbeProvider provider, ISessionStore store)
        => new(new[] { provider }, new DownloadQueue(1, 1024 * 1024), root,
            TimeSpan.FromMinutes(1), 60, sessionStore: store);

    private static SessionSnapshot Snapshot(string root) => new()
    {
        BoardUrls = new[] { BoardUrl },
        DownloadDirectories = new Dictionary<string, string>(StringComparer.Ordinal) { [BoardUrl] = root }
    };

    private static async Task ReleaseAndJoinAsync(Registry registry, GatedSessionStore store,
        Task? startup, Task? stopping = null)
    {
        store.Release.TrySetResult(); // Unconditional, including failed assertions/watchdogs.
        Task stop = stopping ?? registry.StopAsync();
        // Expected startup/load failure is observed, not rethrown during cleanup.
        // Stop failure and watchdog expiry are NOT swallowed as a successful drain.
        await Task.WhenAll(ObserveOutcomeAsync(startup), stop).WaitAsync(Watchdog);
        // Also join the store itself: an incorrect implementation might have
        // abandoned it while completing Start/Stop, which must not leak fixture work.
        await ObserveOutcomeAsync(store.LoadTask).WaitAsync(Watchdog);
        await registry.DisposeAsync().AsTask().WaitAsync(Watchdog);
    }

    private static async Task ObserveOutcomeAsync(Task? task)
    {
        if (task == null) return;
        try { await task; }
        catch (Exception) { /* The test body asserts the startup's actual outcome. */ }
    }

    private sealed class GatedSessionStore(SessionSnapshot snapshot, Exception? failure = null) : ISessionStore
    {
        private int _loads;
        private Task<SessionLoadResult>? _loadTask;
        public int Loads => Volatile.Read(ref _loads);
        public Task<SessionLoadResult>? LoadTask => Volatile.Read(ref _loadTask);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<SessionLoadResult> LoadAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _loads);
            Task<SessionLoadResult> task = LoadCoreAsync();
            Volatile.Write(ref _loadTask, task);
            return task;
        }

        private async Task<SessionLoadResult> LoadCoreAsync()
        {
            Entered.TrySetResult();
            try
            {
                await Release.Task; // Intentionally ignores cancellation, even if later forwarded.
                if (failure != null) throw failure;
                return new SessionLoadResult(snapshot, null, false);
            }
            finally { Returned.TrySetResult(); }
        }

        public Task SaveAsync(SessionSnapshot value, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    // Synchronous public lifecycle counter: observing zero after joining Start/Stop
    // rules out even a short-lived activation, without waiting for a polling tick.
    private sealed class ProbeProvider : IImageBoard
    {
        public ProbeProvider() => Board = new ProbeBoard(this);
        public ProbeBoard Board { get; }
        public string Name => "startup-races";
        public string NiceName => Name;
        public Uri BaseUri => new("https://startup-races.invalid/");
        public bool CanHandle(Uri uri) => uri.Scheme == "https" && uri.Host == BaseUri.Host;
        public bool IsThreadUri(Uri uri) => false;
        public bool IsBoardUri(Uri uri) => uri.AbsoluteUri == BoardUrl;
        public IBoard GetBoard(Uri uri) => IsBoardUri(uri) ? Board : throw new ArgumentException("Unexpected board.");
        public IThread GetThread(Uri uri) => throw new NotSupportedException();
        public HttpClient GetHttpClient() => throw new InvalidOperationException("No network in startup race tests.");
    }

    private sealed class ProbeBoard(IImageBoard provider) : IBoard
    {
        private int _starts;
        private int _stops;
        public int Starts => Volatile.Read(ref _starts);
        public int Stops => Volatile.Read(ref _stops);
        public IImageBoard ImageBoard => provider;
        public string BoardCode => "g";
        public string Name => "Startup race board";
        public string NiceName => Name;
        public Uri BoardUri => new(BoardUrl);
        public RequestPolicyProgress? RequestProgress => null;
        public FailureInfo? LastCheckFailure => null;
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public Task<IEnumerable<IThread>> GetThreadsAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The inert monitor must not request threads.");
        public void StartMonitoring(int intervalInSeconds) => Interlocked.Increment(ref _starts);
        public void UpdateMonitoringInterval(TimeSpan interval) { }
        public void StopMonitoring() => Interlocked.Increment(ref _stops);
        public Task StopMonitoringAsync() { StopMonitoring(); return Task.CompletedTask; }
        public IDisposable RegisterDiscoverySink(Func<IThread, CancellationToken, ValueTask> sink) => new EmptyRegistration();
        public BoardSnapshot GetSnapshot(string downloadDirectory)
            => new(new ResourceId(provider.Name, BoardCode), BoardUrl, Name, downloadDirectory, null, null);
    }

    private sealed class EmptyRegistration : IDisposable { public void Dispose() { } }

    private sealed class TemporaryProfile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "meowchan-public-startup-races-" + Guid.NewGuid().ToString("N"));
        public TemporaryProfile() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

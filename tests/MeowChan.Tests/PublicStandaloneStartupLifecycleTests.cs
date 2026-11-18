using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.Tests;

// Existing core IVT is used solely for observation seams, phase and real drain.
// Every owner/epoch is installed by the public Registry.StartAsync implementation.
public sealed class PublicStandaloneStartupLifecycleTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static string Url(string id) => $"https://stub.test/g/thread/{id}";

    [Fact]
    public async Task StandaloneStartupBindsTheAdditiveBooruCatalogAndAllowsAnOlderProfileWithoutIt()
    {
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        using StubImageBoard provider = new();
        await using Registry registry = new([provider], new DownloadQueue(1, 1024 * 1024),
            profile.Path, TimeSpan.FromHours(1), 3600, persistSession: false);

        await registry.StartAsync(lease).WaitAsync(Deadline);

        Assert.NotNull(registry.BooruCatalog);
        Assert.Equal(Path.Combine(profile.Path, BooruCatalogStore.FileName), registry.BooruCatalog!.FilePath);
        Assert.False(File.Exists(registry.BooruCatalog.FilePath));
        await registry.StopAsync().WaitAsync(Deadline);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"schemaVersion\":99,\"posts\":[],\"watchers\":[]}")]
    public async Task StandaloneStartupFailsClosedForMalformedOrFutureBooruCatalog(string contents)
    {
        using TempDirectory profile = new();
        File.WriteAllText(Path.Combine(profile.Path, BooruCatalogStore.FileName), contents);
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        using StubImageBoard provider = new();
        await using Registry registry = new([provider], new DownloadQueue(1, 1024 * 1024),
            profile.Path, TimeSpan.FromHours(1), 3600, persistSession: false);

        await Assert.ThrowsAsync<InvalidDataException>(() => registry.StartAsync(lease).WaitAsync(Deadline));
        Assert.NotNull(registry.BooruCatalog);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopWinsRealReadyOrActivationGapAndJoinsBlockedBootstrap(bool activationGap)
    {
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        WorkStateStore work = new(profile.Path, lease);
        Seed(work, profile.Path, "123");
        using StubImageBoard provider = new();
        LifecycleThread thread = new(provider.Board, new Uri(Url("123")));
        provider.ThreadFactory = _ => thread;
        DownloadQueue queue = new(1, 1048576);
        await using Registry registry = new([provider], queue, profile.Path, TimeSpan.FromHours(1), 3600, workStore: work);
        AsyncGate gap = new();
        int hookCalls = 0;
        Func<Task> observeGap = async () =>
        {
            Interlocked.Increment(ref hookCalls);
            await gap.WaitAsync();
        };
        if (activationGap) registry.BeforeStandaloneStartupActivationForTests = observeGap;
        else registry.BeforeStandaloneStartupReadyForTests = observeGap;

        Task start = registry.StartAsync(lease);
        Task? stop = null;
        Task? kernelDrain = null;
        ProfileAdmissionPhase sealedPhase = activationGap ? ProfileAdmissionPhase.Stopping : ProfileAdmissionPhase.RecoveryFailed;
        try
        {
            await gap.Entered.Task.WaitAsync(Deadline);
            Assert.Equal(activationGap ? ProfileAdmissionPhase.Ready : ProfileAdmissionPhase.Starting, lease.Admission.Phase);
            Assert.Same(thread, Assert.Single(registry.Threads)); // Restore really ran, still suspended.
            Assert.Equal(0, thread.Starts);
            Assert.Equal(0, thread.Checks);
            Assert.False(start.IsCompleted);
            Assert.Same(start, registry.StartAsync(lease));
            await Assert.ThrowsAsync<InvalidOperationException>(() => registry.AddAsync(Url("124")).WaitAsync(Deadline));

            stop = registry.StopAsync(); // Sealing is synchronous, before awaiting the hook.
            Assert.Equal(sealedPhase, lease.Admission.Phase);
            Assert.False(start.IsCompleted);
            Assert.False(stop.IsCompleted);
            Assert.Equal(0, thread.Starts);
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => registry.AddAsync(Url("124")).WaitAsync(Deadline));
            Assert.Throws<InvalidOperationException>(() => work.CommitIntent(Resource(profile.Path, "124")));
            if (activationGap)
            {
                // An observation only: Stop already sealed Stopping. The installed
                // Ordinary epoch cannot be released while bootstrap is in the hook.
                kernelDrain = lease.Admission.DrainAsync();
                Assert.False(kernelDrain.IsCompleted);
            }
            // In the pre-Ready case the actual kernel state is RecoveryFailed;
            // DrainAsync requires Stopping. Never change phase just to observe it.
        }
        finally { gap.Release.TrySetResult(); }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(Deadline));
        await stop!.WaitAsync(Deadline);
        if (kernelDrain != null) await kernelDrain.WaitAsync(Deadline);
        Assert.Equal(sealedPhase, lease.Admission.Phase);
        Assert.Equal(1, Volatile.Read(ref hookCalls));
        Assert.Equal(0, thread.Starts);
        Assert.Equal(0, thread.Checks);
        Assert.Empty(registry.Threads);
        Assert.Equal((0, 0), queue.GetMetadataCapacitySnapshot());
        Assert.Same(start, registry.StartAsync(lease)); // Terminal cached cancellation, never reopens.
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => registry.RestoreAsync().WaitAsync(Deadline));
    }

    [Fact]
    public async Task ActivationFailureSealsBeforeAwaitingEarlierRealMonitorAndRetainsEpochUntilDrain()
    {
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        WorkStateStore work = new(profile.Path, lease);
        Seed(work, profile.Path, "123");
        Seed(work, profile.Path, "124");
        using StubImageBoard provider = new();
        AsyncGate runningCheck = new();
        TaskCompletionSource checkReturned = NewSignal();
        LifecycleThread first = new(provider.Board, new Uri(Url("123")))
        {
            OnCheckWithCancellation = async _ =>
            {
                // Deliberately ignore cancellation: this is accepted real work,
                // not a delayed cancellation notification or a fake ownership token.
                try { await runningCheck.WaitAsync(); }
                finally { checkReturned.TrySetResult(); }
            }
        };
        InvalidOperationException injectedFailure = new("Synthetic second monitor activation failure.");
        LifecycleThread second = new(provider.Board, new Uri(Url("124")))
        {
            BeforeStart = () =>
            {
                // StartMonitoring is synchronous. Wait on the real first check's
                // TCS before throwing; the timeout is only a deadlock watchdog.
                runningCheck.Entered.Task.WaitAsync(Deadline).GetAwaiter().GetResult();
                throw injectedFailure;
            }
        };
        provider.ThreadFactory = uri => uri.AbsoluteUri == Url("123") ? first : second;
        DownloadQueue queue = new(1, 1048576);
        await using Registry registry = new([provider], queue, profile.Path, TimeSpan.FromHours(1), 3600, workStore: work);
        TaskCompletionSource rollbackEntered = NewSignal();
        registry.BeforeProfileProducerFailureDrain = () =>
        {
            rollbackEntered.TrySetResult();
            return Task.CompletedTask;
        };
        Task start = registry.StartAsync(lease);
        Task? stop = null;
        Task? kernelDrain = null;
        try
        {
            await rollbackEntered.Task.WaitAsync(Deadline);
            await first.StopEntered.Task.WaitAsync(Deadline);
            Assert.True(runningCheck.Entered.Task.IsCompletedSuccessfully);
            Assert.Equal(1, first.Starts);
            Assert.Equal(1, first.Checks);
            Assert.Equal(1, second.Starts);
            Assert.Equal(0, second.Checks);
            Assert.Equal(ProfileAdmissionPhase.Stopping, lease.Admission.Phase);
            Assert.False(checkReturned.Task.IsCompleted);
            Assert.False(first.StopReturned.Task.IsCompleted);
            Assert.False(start.IsCompleted);
            Assert.Same(start, registry.StartAsync(lease));
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => registry.AddAsync(Url("125")).WaitAsync(Deadline));
            Assert.Throws<InvalidOperationException>(() => work.CommitIntent(Resource(profile.Path, "125")));
            kernelDrain = lease.Admission.DrainAsync();
            Assert.False(kernelDrain.IsCompleted); // The epoch still owns the live monitor.
            stop = registry.StopAsync();
            Assert.False(stop.IsCompleted);
            Assert.False(start.IsCompleted);
            Assert.False(kernelDrain.IsCompleted);
        }
        finally { runningCheck.Release.TrySetResult(); }

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() => start.WaitAsync(Deadline));
        Assert.Same(injectedFailure, failure);
        await stop!.WaitAsync(Deadline); // Start catch -> Stop must not join its own public task.
        await kernelDrain!.WaitAsync(Deadline);
        Assert.True(checkReturned.Task.IsCompletedSuccessfully);
        Assert.True(first.StopReturned.Task.IsCompletedSuccessfully);
        Assert.Equal(ProfileAdmissionPhase.Stopping, lease.Admission.Phase);
        Assert.Empty(registry.Threads);
        Assert.Equal(1, first.Starts);
        Assert.Equal(1, second.Starts);
        Assert.Equal((0, 0), queue.GetMetadataCapacitySnapshot());
        Assert.Same(start, registry.StartAsync(lease));
        InvalidOperationException retained = await Assert.ThrowsAsync<InvalidOperationException>(() => registry.StartAsync(lease).WaitAsync(Deadline));
        Assert.Same(failure, retained);
        await registry.StopAsync().WaitAsync(Deadline);
        Assert.Throws<InvalidOperationException>(() => work.CommitIntent(Resource(profile.Path, "125")));
    }

    [Fact]
    public async Task RequestCheckIsPausedDuringRealPartialActivationUntilStartSucceeds()
    {
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        WorkStateStore work = new(profile.Path, lease);
        Seed(work, profile.Path, "123");
        Seed(work, profile.Path, "124");
        using StubImageBoard provider = new();
        AsyncGate runningCheck = new();
        AsyncGate secondStart = new();
        TaskCompletionSource requestedWork = NewSignal();
        LifecycleThread first = new(provider.Board, new Uri(Url("123")));
        first.OnCheckWithCancellation = async cancellation =>
        {
            runningCheck.Entered.TrySetResult();
            // A real owned monitor waits for the public request signal; it never
            // performs request-triggered work on the RequestCheck calling thread.
            await first.CheckRequested.Task.WaitAsync(cancellation);
            requestedWork.TrySetResult();
            await runningCheck.Release.Task.WaitAsync(cancellation);
        };
        LifecycleThread second = new(provider.Board, new Uri(Url("124")))
        {
            BeforeStart = () =>
            {
                runningCheck.Entered.Task.WaitAsync(Deadline).GetAwaiter().GetResult();
                secondStart.WaitAsync().WaitAsync(Deadline).GetAwaiter().GetResult();
            }
        };
        provider.ThreadFactory = uri => uri.AbsoluteUri == Url("123") ? first : second;
        DownloadQueue queue = new(1, 1048576);
        await using Registry registry = new([provider], queue, profile.Path, TimeSpan.FromHours(1), 3600, workStore: work);
        Task start = registry.StartAsync(lease);
        try
        {
            await secondStart.Entered.Task.WaitAsync(Deadline);
            Assert.Equal(ProfileAdmissionPhase.Ready, lease.Admission.Phase); // Observation only.
            Assert.False(queue.IsPaused);
            Assert.Equal(2, registry.Threads.Count);
            Assert.Contains(first, registry.Threads);
            Assert.Equal(1, first.Starts);
            Assert.Equal(1, first.Checks);
            Assert.Equal(1, second.Starts);
            Assert.Equal(0, second.Checks);
            Assert.False(start.IsCompleted);
            Assert.False(secondStart.Release.Task.IsCompleted);
            Assert.Equal(CheckRequestStatus.Paused, registry.RequestCheck(first));
            Assert.Equal(0, first.CheckRequests);
            Assert.False(first.CheckRequested.Task.IsCompleted);
            Assert.False(requestedWork.Task.IsCompleted);
            Assert.Equal(1, first.Checks);

            secondStart.Release.TrySetResult();
            await start.WaitAsync(Deadline);
            Assert.Equal(CheckRequestStatus.Scheduled, registry.RequestCheck(first));
            await requestedWork.Task.WaitAsync(Deadline);
            Assert.Equal(1, first.CheckRequests);
            Assert.Equal(1, first.Checks); // Signal handled inside the original monitor.
        }
        finally
        {
            secondStart.Release.TrySetResult();
            runningCheck.Release.TrySetResult();
            try { await start.WaitAsync(Deadline); }
            finally { await registry.StopAsync().WaitAsync(Deadline); }
        }
        Assert.True(first.StopReturned.Task.IsCompletedSuccessfully);
        Assert.Empty(registry.Threads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalStartupStopKeepsCachedOutcomeWhenConcurrentDisposeRetiresCapturedCancellation(bool canceled)
    {
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        using CancellationTokenSource cancellation = new();
        using StubImageBoard provider = new();
        InvalidOperationException loadFailure = new("Synthetic terminal session load failure.");
        TerminalSessionStore session = new(canceled, cancellation, loadFailure);
        await using Registry registry = new([provider], new DownloadQueue(1, 1048576), profile.Path,
            TimeSpan.FromHours(1), 3600, sessionStore: session);
        Task start = registry.StartAsync(lease, cancellation.Token);
        if (canceled)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(Deadline));
        else
            Assert.Same(loadFailure, await Assert.ThrowsAsync<InvalidOperationException>(() => start.WaitAsync(Deadline)));
        Assert.Same(start, registry.StartAsync(lease));
        Assert.Equal(canceled, start.IsCanceled);
        Assert.Equal(!canceled, start.IsFaulted);
        Task cachedStop = registry.StopAsync();
        await cachedStop.WaitAsync(Deadline);

        AsyncGate capturedCancellation = new();
        int cancellationCalls = 0;
        registry.BeforeStandaloneStartupCancellationForTests = () =>
        {
            // Only T1 waits. T2 must traverse the same real Stop path to retire
            // the captured source; this seam changes no cancellation/owner state.
            if (Interlocked.Increment(ref cancellationCalls) == 1)
                capturedCancellation.WaitAsync().WaitAsync(Deadline).GetAwaiter().GetResult();
        };
        // Return a container so Task.Run does not unwrap and hide Stop task identity.
        Task<Task[]> firstStop = Task.Run(() => new[] { registry.StopAsync() });
        Task? disposal = null;
        try
        {
            await capturedCancellation.Entered.Task.WaitAsync(Deadline);
            Assert.False(firstStop.IsCompleted);
            disposal = registry.DisposeAsync().AsTask();
            await disposal.WaitAsync(Deadline); // T2 has cleared AND disposed the CTS.
            Assert.Equal(2, Volatile.Read(ref cancellationCalls));
            Assert.False(firstStop.IsCompleted);
            Assert.False(capturedCancellation.Release.Task.IsCompleted);
            capturedCancellation.Release.TrySetResult();
            Assert.Same(cachedStop, Assert.Single(await firstStop.WaitAsync(Deadline)));
            Assert.True(cachedStop.IsCompletedSuccessfully);
            Assert.Same(cachedStop, registry.StopAsync());
            Assert.Same(start, registry.StartAsync(lease));
            if (canceled)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(Deadline));
            else
                Assert.Same(loadFailure, await Assert.ThrowsAsync<InvalidOperationException>(() => start.WaitAsync(Deadline)));
        }
        finally
        {
            capturedCancellation.Release.TrySetResult();
            try { await firstStop.WaitAsync(Deadline); }
            finally
            {
                if (disposal != null) await disposal.WaitAsync(Deadline);
            }
        }
    }

    private sealed class TerminalSessionStore(bool canceled, CancellationTokenSource cancellation,
        InvalidOperationException failure) : ISessionStore
    {
        public Task<SessionLoadResult> LoadAsync(CancellationToken cancellationToken = default)
        {
            if (canceled)
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
            throw failure;
        }
        public Task SaveAsync(SessionSnapshot snapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static WorkResource Resource(string output, string id) => new()
    { Url = Url(id), OutputRoot = output, Generation = 1, IsThread = true, Intent = WorkIntent.Watching };
    private static void Seed(WorkStateStore work, string output, string id) => work.CommitIntent(Resource(output, id));

    private sealed class AsyncGate
    {
        public TaskCompletionSource Entered { get; } = NewSignal();
        public TaskCompletionSource Release { get; } = NewSignal();
        public async Task WaitAsync()
        {
            Entered.TrySetResult();
            await Release.Task;
        }
    }

    // Non-content IThread dispatch is intentional: this tests real public producer
    // activation and its failure path, not private content-monitor bootstrap.
    private sealed class LifecycleThread(IBoard board, Uri uri) : ProbeThread(board, uri), IThread, ICheckRequestSource
    {
        private int _checkRequests;
        public int CheckRequests => Volatile.Read(ref _checkRequests);
        public TaskCompletionSource CheckRequested { get; } = NewSignal();
        public new CheckRequestStatus RequestCheck()
        {
            Interlocked.Increment(ref _checkRequests);
            return CheckRequested.TrySetResult() ? CheckRequestStatus.Scheduled : CheckRequestStatus.AlreadyScheduled;
        }
        private int _starts;
        public int Starts => Volatile.Read(ref _starts);
        public Action? BeforeStart { get; init; }
        public TaskCompletionSource StopEntered { get; } = NewSignal();
        public TaskCompletionSource StopReturned { get; } = NewSignal();
        void IThread.StartMonitoring(DownloadQueue queue, string outputDirectory, TimeSpan interval)
        {
            Interlocked.Increment(ref _starts);
            BeforeStart?.Invoke();
            base.StartMonitoring(queue, outputDirectory, interval);
        }
        async Task IThread.StopMonitoringAsync()
        {
            StopEntered.TrySetResult();
            await base.StopMonitoringAsync();
            StopReturned.TrySetResult();
        }
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using MeowChan.Boards.FourChan;
using MeowChan.Desktop;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

/// <summary>
/// Actual Host -> Registry -> queue/work-store admission under a held command semaphore.
/// Only the provider's polling/check implementation is controlled; no projected action delegates.
/// No live network, plugin worker, source activation, grant change or implicit resume is permitted.
/// </summary>
public sealed class DiagnosticRetryHostTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private const string FirstUrl = "https://boards.4chan.org/g/thread/123";
    private const string SecondUrl = "https://boards.4chan.org/g/thread/456";
    private const string BoardUrl = "https://boards.4chan.org/g/";

    [Fact]
    public async Task HeldChecksPreserveTargetKindAndWatchOrderWithoutCallingWrongKindHandles()
    {
        await using Fixture f = await Fixture.CreateAsync(paused: false);
        DiagnosticTarget first = f.Target(FirstUrl), second = f.Target(SecondUrl);
        long board = (await f.Host.AddAsync(BoardUrl).WaitAsync(Deadline)).WatchId!.Value;
        await using HeldCommands gate = await HeldCommands.AcquireAsync(f.Host);
        Task<CheckRequestStatus> wrongThread = f.Host.RequestCheckAsync(new(DiagnosticTargetKind.Thread, board));
        await gate.WaitForWaitersAsync(1);
        Task<CheckRequestStatus> wrongBoard = f.Host.RequestCheckAsync(first with { Kind = DiagnosticTargetKind.Board });
        await gate.WaitForWaitersAsync(2);
        Task<CheckRequestStatus> one = f.Host.RequestCheckAsync(first);
        await gate.WaitForWaitersAsync(3);
        Task<CheckRequestStatus> two = f.Host.RequestCheckAsync(second);
        await gate.WaitForWaitersAsync(4);
        Task<CheckRequestStatus> boardCheck = f.Host.RequestCheckAsync(new(DiagnosticTargetKind.Board, board));
        await gate.WaitForWaitersAsync(5);
        Assert.Empty(f.Calls);
        Assert.All(new[] { wrongThread, wrongBoard, one, two, boardCheck }, task => Assert.False(task.IsCompleted));
        gate.Release();
        Assert.Equal(new[] { CheckRequestStatus.Stale, CheckRequestStatus.Stale, CheckRequestStatus.Scheduled,
            CheckRequestStatus.Scheduled, CheckRequestStatus.Scheduled },
            await Task.WhenAll(wrongThread, wrongBoard, one, two, boardCheck).WaitAsync(Deadline));
        Assert.Equal(new[] { "thread:123", "thread:456", "board:g" }, f.Calls.ToArray());
        Assert.Equal(first, f.Target(FirstUrl)); Assert.Equal(second, f.Target(SecondUrl));
        f.AssertNoTrustOrTransportEffects(expectedPaused: false);
    }

    [Fact]
    public async Task HeldRemoveThenReadmitThenOldCheckDoesNotRedirectToTheSameUrlNewLifetime()
    {
        await using Fixture f = await Fixture.CreateAsync(paused: false);
        DiagnosticTarget old = f.Target(FirstUrl);
        Assert.True(f.Host.CanRequestCheck(old));
        await using HeldCommands gate = await HeldCommands.AcquireAsync(f.Host);
        Task remove = f.Host.RemoveThreadAsync(old.WatchId);
        await gate.WaitForWaitersAsync(1);
        Task<DesktopAdmissionResult> readd = f.Host.AddAsync(FirstUrl);
        await gate.WaitForWaitersAsync(2);
        Task<CheckRequestStatus> stale = f.Host.RequestCheckAsync(old);
        await gate.WaitForWaitersAsync(3);
        gate.Release();
        await remove.WaitAsync(Deadline);
        DesktopAdmissionResult replacement = await readd.WaitAsync(Deadline);
        Assert.Equal(AdmissionStatus.Added, replacement.Status);
        Assert.NotEqual(old.WatchId, replacement.WatchId);
        Assert.Equal(CheckRequestStatus.Stale, await stale.WaitAsync(Deadline));
        Assert.Empty(f.Calls);
        DiagnosticTarget current = f.Target(FirstUrl);
        Assert.Equal(replacement.WatchId, current.WatchId);
        Assert.Equal(CheckRequestStatus.Scheduled, await f.Host.RequestCheckAsync(current).WaitAsync(Deadline));
        Assert.Equal(new[] { "thread:123" }, f.Calls.ToArray());
        f.AssertNoTrustOrTransportEffects(expectedPaused: false);
    }

    [Fact]
    public async Task HeldPublicPauseBeforeChecksWinsOverEarlierAdvisoryEligibilityForBothKinds()
    {
        await using Fixture f = await Fixture.CreateAsync(paused: false);
        DiagnosticTarget thread = f.Target(FirstUrl);
        DiagnosticTarget board = new(DiagnosticTargetKind.Board, (await f.Host.AddAsync(BoardUrl).WaitAsync(Deadline)).WatchId!.Value);
        Assert.True(f.Host.CanRequestCheck(thread)); Assert.True(f.Host.CanRequestCheck(board));
        await using HeldCommands gate = await HeldCommands.AcquireAsync(f.Host);
        Task pause = f.Host.SetPausedAsync(true);
        await gate.WaitForWaitersAsync(1);
        Task<CheckRequestStatus> one = f.Host.RequestCheckAsync(thread);
        await gate.WaitForWaitersAsync(2);
        Task<CheckRequestStatus> two = f.Host.RequestCheckAsync(board);
        await gate.WaitForWaitersAsync(3);
        Assert.Empty(f.Calls);
        gate.Release();
        await pause.WaitAsync(Deadline);
        Assert.Equal(new[] { CheckRequestStatus.Paused, CheckRequestStatus.Paused }, await Task.WhenAll(one, two).WaitAsync(Deadline));
        Assert.Empty(f.Calls);
        Assert.True(Config.LoadConfig(f.Directory.Path).DownloadsPaused);
        f.AssertNoTrustOrTransportEffects(expectedPaused: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleProjectionAfterEnqueueRejectsCheckOrMediaWithoutTouchingTheProviderOrWork(bool media)
    {
        await using Fixture f = await Fixture.CreateAsync(paused: media, seedMedia: media);
        DiagnosticTarget target = f.Target(FirstUrl);
        Assert.True(media ? f.Host.CanRetryMissingMedia(target.WatchId) : f.Host.CanRequestCheck(target));
        await using HeldCommands gate = await HeldCommands.AcquireAsync(f.Host);
        Task<int>? retry = media ? f.Host.RetryMissingMediaAsync(target.WatchId) : null;
        Task<CheckRequestStatus>? check = media ? null : f.Host.RequestCheckAsync(target);
        await gate.WaitForWaitersAsync(1);
        f.Provider.FailProjection = true;
        try
        {
            // Same real capture-failure technique as DesktopSessionAdmissionTests;
            // no manually manufactured DesktopSessionProjection or action callback.
            Assert.True((await f.Host.CaptureProjectionAsync().WaitAsync(Deadline)).IsStale);
            gate.Release();
            if (media) Assert.Equal(0, await retry!.WaitAsync(Deadline));
            else Assert.Equal(CheckRequestStatus.Stale, await check!.WaitAsync(Deadline));
            Assert.Empty(f.Calls);
            Assert.All(f.Store.LoadMedia(FirstUrl, 1), item => Assert.False(item.ManualAttempt));
            f.AssertNoTrustOrTransportEffects(expectedPaused: media);
        }
        finally { f.Provider.FailProjection = false; await f.Host.CaptureProjectionAsync().WaitAsync(Deadline); }
    }

    [Theory]
    [InlineData("succeeded")]
    [InlineData("excluded")]
    [InlineData("manual")]
    [InlineData("suppressed")]
    public async Task MediaEligibilityChangedAfterEnqueueUsesLiveRegistryWorkNotOldEligibleFrame(string change)
    {
        await using Fixture f = await Fixture.CreateAsync(paused: true, seedMedia: true);
        DiagnosticTarget target = f.Target(FirstUrl);
        Assert.True(f.Host.CanRetryMissingMedia(target.WatchId));
        await using HeldCommands gate = await HeldCommands.AcquireAsync(f.Host);
        Task<int> retry = f.Host.RetryMissingMediaAsync(target.WatchId);
        await gate.WaitForWaitersAsync(1);
        // Hold only publication, not registry cache construction or the command
        // gate: the advisory frame stays eligible while actual work changes.
        await using HeldCapture capture = await HeldCapture.AcquireAsync(f.Host);
        DesktopSessionProjection eligibleFrame = f.Host.Projection;
        MediaWork[] old = f.Store.LoadMedia(FirstUrl, 1).ToArray();
        if (change == "suppressed")
        {
            WorkResource resource = f.Store.LoadResources().Single(item => item.Url == FirstUrl);
            Assert.NotNull(f.Store.CommitState(resource with { Intent = WorkIntent.Suppressed }));
        }
        else foreach (MediaWork item in old)
            Assert.True(f.Store.CommitMedia(change switch
            {
                "succeeded" => item with { Status = MediaWorkStatus.Succeeded },
                "excluded" => item with { Status = MediaWorkStatus.Excluded },
                _ => item with { ManualAttempt = true }
            }));
        // Use a real provider invalidation to run the registry's normal dirty-work
        // cache path. Do not invoke private RefreshWorkCache or edit projections.
        f.Thread(target).Title = "Live eligibility changed: " + change;
        await UntilAsync(() => f.Host.GetThreadWorkSnapshots().Single(work => work.Url == FirstUrl) is { } live
            && (change == "suppressed" ? live.Intent == WorkIntent.Suppressed
                : live.Media.All(item => change == "manual" ? item.ManualAttempt
                    : item.Status == (change == "succeeded" ? MediaWorkStatus.Succeeded : MediaWorkStatus.Excluded))));
        Assert.Same(eligibleFrame, f.Host.Projection); // still the OLD eligible publication
        Assert.True(WorkStatusFormatter.Format(eligibleFrame.View.Threads.Single(entry => entry.WatchId == target.WatchId).Snapshot.Work!).CanRetry);
        // Queued waiter owns the next permit. Reacquiring behind it proves the
        // mutation body has returned before allowing its post-command capture.
        gate.Release();
        await gate.ReacquireAsync();
        capture.Release();
        Assert.Equal(0, await retry.WaitAsync(Deadline));
        Assert.Empty(f.Calls);
        Assert.All(f.Store.LoadMedia(SecondUrl, 1), item => Assert.False(item.ManualAttempt));
        if (change == "manual") Assert.All(f.Store.LoadMedia(FirstUrl, 1), item => Assert.Equal(MediaWorkStatus.NeedsIntervention, item.Status));
        f.AssertNoTrustOrTransportEffects(expectedPaused: true);
    }

    [Fact]
    public async Task MediaRetryWhileAlreadyPausedQueuesOnlyNamedMediaAndDuplicateIntentDoesNotUnpause()
    {
        await using Fixture f = await Fixture.CreateAsync(paused: true, seedMedia: true);
        DiagnosticTarget target = f.Target(FirstUrl);
        Assert.False(f.Host.CanRequestCheck(target));
        Assert.True(f.Host.CanRetryMissingMedia(target.WatchId)); // media has a DIFFERENT pause policy
        await using HeldCommands gate = await HeldCommands.AcquireAsync(f.Host);
        Task<int> first = f.Host.RetryMissingMediaAsync(target.WatchId, "a");
        await gate.WaitForWaitersAsync(1);
        await Assert.ThrowsAsync<DesktopSessionBusyException>(() => f.Host.RetryMissingMediaAsync(target.WatchId, "a"));
        Assert.Equal(1, f.Host.AcceptedCommandCount);
        Assert.False(first.IsCompleted);
        gate.Release();
        Assert.Equal(1, await first.WaitAsync(Deadline));
        MediaWork a = f.Store.LoadMedia(FirstUrl, 1).Single(item => item.MediaIdentifier == "a");
        Assert.True(a.ManualAttempt); Assert.Equal(MediaWorkStatus.Ready, a.Status);
        Assert.Equal(0, await f.Host.RetryMissingMediaAsync(target.WatchId, "a").WaitAsync(Deadline));
        Assert.Equal(1, await f.Host.RetryMissingMediaAsync(target.WatchId, "b").WaitAsync(Deadline));
        Assert.Equal(0, await f.Host.RetryMissingMediaAsync(target.WatchId).WaitAsync(Deadline));
        Assert.All(f.Store.LoadMedia(FirstUrl, 1), item => Assert.True(item.ManualAttempt));
        Assert.All(f.Store.LoadMedia(SecondUrl, 1), item => Assert.False(item.ManualAttempt));
        Assert.Empty(f.Calls);
        f.AssertNoTrustOrTransportEffects(expectedPaused: true);
    }

    [Fact]
    public async Task HeldMediaRetryForRemovedWatchCannotQueueReplacementOrAnotherWatchsMedia()
    {
        await using Fixture f = await Fixture.CreateAsync(paused: true, seedMedia: true);
        DiagnosticTarget old = f.Target(FirstUrl);
        await using HeldCommands gate = await HeldCommands.AcquireAsync(f.Host);
        Task remove = f.Host.RemoveThreadAsync(old.WatchId);
        await gate.WaitForWaitersAsync(1);
        Task<DesktopAdmissionResult> readd = f.Host.AddAsync(FirstUrl);
        await gate.WaitForWaitersAsync(2);
        Task<int> retry = f.Host.RetryMissingMediaAsync(old.WatchId);
        await gate.WaitForWaitersAsync(3);
        gate.Release();
        await remove.WaitAsync(Deadline);
        DesktopAdmissionResult replacement = await readd.WaitAsync(Deadline);
        Assert.NotEqual(old.WatchId, replacement.WatchId);
        Assert.Equal(0, await retry.WaitAsync(Deadline));
        Assert.Equal(replacement.WatchId, f.Target(FirstUrl).WatchId);
        Assert.All(f.Store.LoadMedia(FirstUrl, 1), item => Assert.False(item.ManualAttempt));
        Assert.All(f.Store.LoadMedia(SecondUrl, 1), item => Assert.False(item.ManualAttempt));
        Assert.Empty(f.Calls);
        f.AssertNoTrustOrTransportEffects(expectedPaused: true);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        long started = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(started) >= Deadline) throw new TimeoutException("Host regression fixture boundary did not arrive.");
            await Task.Delay(10);
        }
    }

    private sealed class HeldCommands(SemaphoreSlim gate) : IAsyncDisposable
    {
        private bool _held = true;
        internal static async Task<HeldCommands> AcquireAsync(DesktopSessionHost host)
        {
            var gate = (SemaphoreSlim)typeof(DesktopSessionHost).GetField("_commands", Private)!.GetValue(host)!;
            if (!await gate.WaitAsync(Deadline)) throw new TimeoutException("Host command gate remained occupied.");
            return new(gate);
        }
        internal Task WaitForWaitersAsync(int count) => UntilAsync(() =>
        {
            // Task.Run admission is NOT FIFO until a command actually reaches
            // SemaphoreSlim. Confirm its real async wait-list before enqueuing
            // the next operation. Fail loudly on a changed runtime boundary.
            FieldInfo head = typeof(SemaphoreSlim).GetField("m_asyncHead", Private)
                ?? throw new InvalidOperationException("Adapt test-only SemaphoreSlim async waiter observation for this runtime.");
            object? node = head.GetValue(gate);
            int waiting = 0;
            while (node != null)
            {
                waiting++;
                FieldInfo next = node.GetType().GetField("Next", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("SemaphoreSlim async TaskNode.Next is unavailable.");
                node = next.GetValue(node);
            }
            return waiting == count;
        });
        internal void Release() { if (_held) { _held = false; gate.Release(); } }
        internal async Task ReacquireAsync()
        {
            if (!await gate.WaitAsync(Deadline)) throw new TimeoutException("Queued host operation did not leave its command body.");
            _held = true;
        }
        public ValueTask DisposeAsync() { Release(); return ValueTask.CompletedTask; }
    }

    private sealed class HeldCapture(DesktopSessionHost host) : IAsyncDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private Task _holder = Task.CompletedTask;
        internal static async Task<HeldCapture> AcquireAsync(DesktopSessionHost host)
        {
            var result = new HeldCapture(host);
            result._holder = Task.Run(() => host.HoldCaptureLockForTest(result._release));
            try { await UntilAsync(() => host.CaptureLockHeldForTest); return result; }
            catch { result.Release(); await result.DisposeAsync(); throw; }
        }
        internal void Release() => _release.Set();
        public async ValueTask DisposeAsync() { Release(); await _holder.WaitAsync(Deadline); _release.Dispose(); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal TempDirectory Directory { get; } = new();
        internal MemoryWorkStateStore Store { get; } = new();
        internal ConcurrentQueue<string> Calls { get; } = new();
        private readonly NoNetwork _transport = new();
        private readonly HttpClient _client;
        internal ControlledProvider Provider { get; }
        internal DesktopSessionHost Host { get; }
        private Fixture(bool paused, bool seedMedia)
        {
            _client = new(_transport); Provider = new(_client, Calls);
            string output = Path.Combine(Directory.Path, "media");
            new Config { DownloadPath = output, DownloadsPaused = paused, AutomaticUpdateChecks = false,
                SaveUrlsOnExit = true, MinimumFreeSpaceGiB = 0 }.SaveConfig(Directory.Path);
            foreach (string url in new[] { FirstUrl, SecondUrl })
            {
                Store.CommitIntent(new WorkResource { Url = url, OutputRoot = output, Generation = 1,
                    Intent = WorkIntent.Watching, IsThread = true });
                if (seedMedia) foreach (string id in new[] { "a", "b" })
                    Assert.True(Store.CommitMedia(new MediaWork { ResourceUrl = url, Generation = 1, MediaIdentifier = id,
                        DestinationPath = Path.Combine(output, url == FirstUrl ? "123" : "456", id + ".jpg"),
                        DownloadUrl = "https://fixture.invalid/" + id + ".jpg", Status = MediaWorkStatus.NeedsIntervention,
                        Attempts = 2, HttpStatus = 429, Failure = "controlled prior failure" }));
            }
            Host = new(Directory.Path, new[] { Provider }, workStoreFactory: (_, _) => Store);
        }
        internal static async Task<Fixture> CreateAsync(bool paused, bool seedMedia = false)
        {
            var f = new Fixture(paused, seedMedia);
            try
            {
                await f.Host.StartAsync().WaitAsync(Deadline);
                Assert.Equal(DesktopSessionState.Ready, f.Host.State);
                Assert.Equal(2, f.Host.GetView().Threads.Count);
                Assert.False(f.Host.Projection.IsStale);
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        internal DiagnosticTarget Target(string url) => new(DiagnosticTargetKind.Thread,
            Host.GetView().Threads.Single(entry => entry.Snapshot.Url == url).WatchId);
        internal ControlledThread Thread(DiagnosticTarget target) => (ControlledThread)((Dictionary<long, IThread>)
            typeof(DesktopSessionHost).GetField("_threadHandles", Private)!.GetValue(Host)!)[target.WatchId];
        internal void AssertNoTrustOrTransportEffects(bool expectedPaused)
        {
            Assert.Equal(expectedPaused, Host.IsPaused);
            Assert.Equal(expectedPaused, Host.Config.DownloadsPaused);
            Assert.Empty(Host.Config.SourceMediaIntegrityPermissions);
            Assert.Empty(Config.LoadConfig(Directory.Path).SourceMediaIntegrityPermissions);
            Assert.Null(Host.Plugins); // injected provider path never starts/activates a plugin worker
            Assert.Equal(0, _transport.Requests);
        }
        public async ValueTask DisposeAsync()
        {
            try { await Host.DisposeAsync().AsTask().WaitAsync(Deadline); }
            finally { _client.Dispose(); Directory.Dispose(); }
        }
    }

    private sealed class ControlledProvider(HttpClient client, ConcurrentQueue<string> calls) : FourChanImageBoard(client)
    {
        internal volatile bool FailProjection;
        public override IBoard GetBoard(Uri uri) => new ControlledBoard(this, uri, calls);
        public override IThread GetThread(Uri uri)
        {
            Uri canonical = CanonicalizeThreadUri(uri);
            return new ControlledThread(GetBoard(new Uri(BaseUri, canonical.Segments[1])), canonical, calls);
        }
    }
    private sealed class ControlledThread(IBoard board, Uri uri, ConcurrentQueue<string> calls)
        : FourChanThread(board, uri), ICheckRequestSource
    {
        public override bool SupportsContent => false;
        public override Task checkThreadAsync(DownloadQueue queue, CancellationToken cancellationToken = default)
            => Task.CompletedTask; // real monitoring lifetime, inert deterministic provider
        CheckRequestStatus ICheckRequestSource.RequestCheck() { calls.Enqueue("thread:" + ThreadId); return CheckRequestStatus.Scheduled; }
    }
    private sealed class ControlledBoard(ControlledProvider provider, Uri uri, ConcurrentQueue<string> calls)
        : FourChanBoard(provider, uri), ICheckRequestSource
    {
        public override string NiceName => provider.FailProjection ? throw new ArgumentException("Controlled stale source projection") : base.NiceName;
        public override Task<IEnumerable<IThread>> GetThreadsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IEnumerable<IThread>>(Array.Empty<IThread>());
        CheckRequestStatus ICheckRequestSource.RequestCheck() { calls.Enqueue("board:" + BoardCode); return CheckRequestStatus.Scheduled; }
    }
    private sealed class NoNetwork : HttpMessageHandler
    {
        private int _requests;
        internal int Requests => Volatile.Read(ref _requests);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            throw new InvalidOperationException("Host retry regression must not send network requests: " + request.RequestUri);
        }
    }
}

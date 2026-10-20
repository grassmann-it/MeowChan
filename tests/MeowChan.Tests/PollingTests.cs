using MeowChan.Downloader;
using MeowChan.Interfaces;

namespace MeowChan.Tests;

public sealed class PollingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosedQueueGateEndsMonitorWithoutFailureBeforeWatchCancellation(bool board)
    {
        using TempDirectory directory = new();
        using StubImageBoard provider = new();
        DownloadQueue queue = new(1, 1024 * 1024);
        ProbeThread thread = new(provider.Board, new Uri("https://stub.test/g/thread/123"));
        // Force shutdown to win the race before the monitor gets its first execution slot.
        await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        try
        {
            if (board)
            {
                provider.Board.AttachSessionQueue(queue);
                provider.Board.StartMonitoring(60);
                await TestWait.UntilAsync(() => provider.Board.RequestCheck() == CheckRequestStatus.Stopping);
                Assert.Equal(0, provider.Board.Checks);
                Assert.Null(provider.Board.LastCheckFailure);
            }
            else
            {
                thread.StartMonitoring(queue, directory.Path, TimeSpan.FromSeconds(60));
                await TestWait.UntilAsync(() => thread.RequestCheck() == CheckRequestStatus.Stopping);
                Assert.Equal(0, thread.Checks);
                Assert.Null(thread.LastCheckFailure);
                Assert.NotEqual(ThreadActivity.Error, thread.Activity);
            }
        }
        finally
        {
            await thread.StopMonitoringAsync().WaitAsync(TestWait.Timeout);
            await provider.Board.StopMonitoringAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnrequestedProviderCancellationRemainsAnObservableSourceFailure(bool board)
    {
        using TempDirectory directory = new();
        using StubImageBoard provider = new();
        DownloadQueue queue = new(1, 1024 * 1024);
        ProbeThread thread = new(provider.Board, new Uri("https://stub.test/g/thread/123"));
        try
        {
            if (board)
            {
                provider.Board.AttachSessionQueue(queue);
                provider.Board.OnCheckWithCancellation = _ => throw new OperationCanceledException("unexpected provider cancellation");
                provider.Board.StartMonitoring(60);
                await TestWait.UntilAsync(() => provider.Board.Clock.Waits == 1);
                Assert.NotNull(provider.Board.LastCheckFailure);
            }
            else
            {
                thread.OnCheckWithCancellation = _ => throw new OperationCanceledException("unexpected provider cancellation");
                thread.StartMonitoring(queue, directory.Path, TimeSpan.FromSeconds(60));
                await TestWait.UntilAsync(() => thread.Clock.Waits == 1);
                Assert.NotNull(thread.LastCheckFailure);
                Assert.Equal(ThreadActivity.Error, thread.Activity);
            }
        }
        finally
        {
            await thread.StopMonitoringAsync().WaitAsync(TestWait.Timeout);
            await provider.Board.StopMonitoringAsync().WaitAsync(TestWait.Timeout);
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public async Task RemovingTheLifetimeCancelsAnActiveManualCheckAndItsQueuedSuccessor()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        DownloadQueue queue = new DownloadQueue(1, 1024 * 1024);
        ProbeThread thread = new ProbeThread(provider.Board, new Uri("https://stub.test/g/thread/123"));
        try
        {
            thread.StartMonitoring(queue, directory.Path, TimeSpan.FromHours(1));
            await TestWait.UntilAsync(() => thread.Clock.Waits == 1);
            thread.OnCheckWithCancellation = token => Task.Delay(Timeout.Infinite, token);
            Assert.Equal(CheckRequestStatus.Scheduled, thread.RequestCheck());
            await TestWait.UntilAsync(() => thread.Checks == 2);
            Assert.Equal(CheckRequestStatus.Scheduled, thread.RequestCheck());
            await thread.StopMonitoringAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(2, thread.Checks);
            Assert.Equal(1, thread.MaximumActive);
            Assert.Equal(CheckRequestStatus.Stopping, thread.RequestCheck());
        }
        finally
        {
            await thread.StopMonitoringAsync().WaitAsync(TestWait.Timeout);
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public async Task ExplicitChecksKeepTheProvidersMinimumRecheckInterval()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        DownloadQueue queue = new DownloadQueue(1, 1024 * 1024);
        MinimumIntervalThread thread = new MinimumIntervalThread(provider.Board);
        try
        {
            thread.StartMonitoring(queue, directory.Path, TimeSpan.FromHours(1));
            await thread.FirstWait.Task.WaitAsync(TestWait.Timeout);
            Assert.Equal(CheckRequestStatus.Scheduled, thread.RequestCheck());
            await TestWait.UntilAsync(() => thread.Checks == 2);
            Assert.True(thread.SecondCheckAt - thread.FirstCheckAt >= TimeSpan.FromMilliseconds(190));
        }
        finally
        {
            await thread.StopMonitoringAsync().WaitAsync(TestWait.Timeout);
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    private sealed class MinimumIntervalThread : ProbeThread
    {
        private readonly System.Diagnostics.Stopwatch _elapsed = System.Diagnostics.Stopwatch.StartNew();
        internal TaskCompletionSource FirstWait { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TimeSpan FirstCheckAt { get; private set; }
        internal TimeSpan SecondCheckAt { get; private set; }
        internal MinimumIntervalThread(IBoard board) : base(board, new Uri("https://stub.test/g/thread/123")) { }
        protected override TimeSpan MinimumRecheckInterval => TimeSpan.FromMilliseconds(200);
        public override async Task checkThreadAsync(DownloadQueue queue, CancellationToken cancellationToken = default)
        {
            if (Checks == 0) FirstCheckAt = _elapsed.Elapsed;
            else if (Checks == 1) SecondCheckAt = _elapsed.Elapsed;
            await base.checkThreadAsync(queue, cancellationToken);
        }
        protected override Task WaitForNextCheckAsync(TimeSpan interval, CancellationToken cancellationToken)
        {
            FirstWait.TrySetResult();
            return base.WaitForNextCheckAsync(interval, cancellationToken);
        }
    }

    [Fact]
    public async Task ManualThreadChecksWakeTheExistingLoopAndCoalesceWhileItIsBusy()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        DownloadQueue queue = new DownloadQueue(1, 1024 * 1024);
        ProbeThread thread = new ProbeThread(provider.Board, new Uri("https://stub.test/g/thread/123"));
        TaskCompletionSource release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            thread.StartMonitoring(queue, directory.Path, TimeSpan.FromHours(1));
            await TestWait.UntilAsync(() => thread.Clock.Waits == 1);
            thread.OnCheckWithCancellation = token => release.Task.WaitAsync(token);
            Assert.Equal(CheckRequestStatus.Scheduled, thread.RequestCheck());
            await TestWait.UntilAsync(() => thread.Checks == 2);
            Assert.Equal(CheckRequestStatus.Scheduled, thread.RequestCheck());
            Assert.All(Enumerable.Range(0, 32), _ => Assert.Equal(CheckRequestStatus.AlreadyScheduled, thread.RequestCheck()));
            release.TrySetResult();
            await TestWait.UntilAsync(() => thread.Checks == 3 && thread.Clock.Waits == 2);
            Assert.Equal(1, thread.MaximumActive);
            queue.SetPaused(true);
            Assert.Equal(CheckRequestStatus.Paused, thread.RequestCheck());
            await thread.StopMonitoringAsync().WaitAsync(TestWait.Timeout);
            Assert.Equal(CheckRequestStatus.Paused, thread.RequestCheck());
        }
        finally
        {
            release.TrySetResult();
            await thread.StopMonitoringAsync().WaitAsync(TestWait.Timeout);
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public async Task ManualBoardChecksUseDiscoveryAndDoNotOverlapOrWakeTheRemovedWatch()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        DownloadQueue queue = new DownloadQueue(1, 1024 * 1024);
        await using Registry registry = new Registry(new[] { provider }, queue, directory.Path, TimeSpan.FromHours(1), 3600);
        ProbeBoard board = provider.Board;
        await registry.AddAsync(board.BoardUri.AbsoluteUri);
        await TestWait.UntilAsync(() => board.Clock.Waits == 1);
        TaskCompletionSource<IEnumerable<IThread>> release = new TaskCompletionSource<IEnumerable<IThread>>(TaskCreationOptions.RunContinuationsAsynchronously);
        ProbeThread discovered = new ProbeThread(board, new Uri("https://stub.test/g/thread/124"));
        try
        {
            board.OnCheckWithCancellation = token => release.Task.WaitAsync(token);
            Assert.Equal(CheckRequestStatus.Scheduled, registry.RequestCheck(board));
            await TestWait.UntilAsync(() => board.Checks == 2);
            Assert.Equal(CheckRequestStatus.Scheduled, registry.RequestCheck(board));
            Assert.Equal(CheckRequestStatus.AlreadyScheduled, registry.RequestCheck(board));
            release.TrySetResult(new[] { discovered });
            await TestWait.UntilAsync(() => board.Checks == 3 && board.Clock.Waits == 2);
            Assert.Same(discovered, Assert.Single(registry.Threads));
            Assert.Equal(1, board.MaximumActive);
            await registry.RemoveBoardAsync(board).WaitAsync(TestWait.Timeout);
            Assert.Equal(CheckRequestStatus.Stale, registry.RequestCheck(board));
        }
        finally { release.TrySetResult(Array.Empty<IThread>()); }
    }

    [Fact]
    public async Task RegistryPausesBoardDiscoveryAndResumeUsesTheExistingWatch()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        DownloadQueue queue = new DownloadQueue(1, 1024 * 1024);
        await using Registry registry = new Registry(new[] { provider }, queue, directory.Path, TimeSpan.FromSeconds(60), 60);
        registry.SetPaused(true);
        Assert.True(registry.GetSnapshot().IsPaused);
        Assert.True(registry.GetView().IsPaused);
        await registry.AddAsync(provider.Board.BoardUri.AbsoluteUri);
        Assert.Equal(0, provider.Board.Checks);
        registry.SetPaused(false);
        await TestWait.UntilAsync(() => provider.Board.Clock.Waits == 1);
        Assert.Equal(1, provider.Board.Checks);
        registry.SetPaused(true);
        provider.Board.Clock.Advance();
        await registry.RemoveBoardAsync(provider.Board).WaitAsync(TestWait.Timeout);
        Assert.Equal(1, provider.Board.Checks);
    }

    [Fact]
    public async Task GlobalPauseCancelsActiveSourcesAndResumesTheSameWatchInstances()
    {
        using TempDirectory directory = new();
        using StubImageBoard provider = new();
        DownloadQueue queue = new(1, 1024 * 1024);
        ProbeThread thread = new(provider.Board, new Uri("https://stub.test/g/thread/123"));
        provider.ThreadFactory = _ => thread;
        TaskCompletionSource threadCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource boardCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        thread.OnCheckWithCancellation = async token =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { threadCancelled.TrySetResult(); }
        };
        provider.Board.OnCheckWithCancellation = async token =>
        {
            try { await Task.Delay(Timeout.Infinite, token); return Array.Empty<IThread>(); }
            finally { boardCancelled.TrySetResult(); }
        };
        await using Registry registry = new(new[] { provider }, queue, directory.Path, TimeSpan.FromSeconds(60), 60);
        await registry.AddAsync(thread.ThreadUri.AbsoluteUri);
        await registry.AddAsync(provider.Board.BoardUri.AbsoluteUri);
        await TestWait.UntilAsync(() => thread.Checks == 1 && provider.Board.Checks == 1);
        await registry.SetPausedAsync(true).WaitAsync(TestWait.Timeout);
        Assert.True(threadCancelled.Task.IsCompleted);
        Assert.True(boardCancelled.Task.IsCompleted);
        Assert.Equal(CheckRequestStatus.Paused, thread.RequestCheck());
        Assert.Equal(CheckRequestStatus.Paused, provider.Board.RequestCheck());
        Assert.Same(thread, Assert.Single(registry.Threads));
        Assert.Same(provider.Board, Assert.Single(registry.Boards));
        thread.OnCheckWithCancellation = null;
        provider.Board.OnCheckWithCancellation = null;
        await registry.SetPausedAsync(false);
        await TestWait.UntilAsync(() => thread.Checks == 2 && provider.Board.Checks == 2);
        Assert.Equal(1, thread.MaximumActive);
        Assert.Equal(1, provider.Board.MaximumActive);
    }

    [Fact]
    public async Task PausedThreadDoesNotStartInitialCheckAndCanStopWithoutResume()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        DownloadQueue queue = new DownloadQueue(1, 1024 * 1024);
        queue.SetPaused(true);
        ProbeThread thread = new ProbeThread(provider.Board, new Uri("https://stub.test/g/thread/123"));
        thread.StartMonitoring(queue, directory.Path, TimeSpan.FromSeconds(60));
        await thread.StopMonitoringAsync().WaitAsync(TestWait.Timeout);
        Assert.Equal(0, thread.Checks);
        thread.StartMonitoring(queue, directory.Path, TimeSpan.FromSeconds(60));
        queue.SetPaused(false);
        await TestWait.UntilAsync(() => thread.Clock.Waits == 1);
        Assert.Equal(1, thread.Checks);
        queue.SetPaused(true);
        thread.Clock.Advance();
        await thread.StopMonitoringAsync().WaitAsync(TestWait.Timeout);
        Assert.Equal(1, thread.Checks);
        await queue.StopAllAsync();
    }

    [Fact]
    public async Task ThreadIntervalChangeAppliesToTheNextWaitWithoutStartingAnotherCheck()
    {
        using TempDirectory directory = new();
        using StubImageBoard provider = new();
        DownloadQueue queue = new(1, 1024 * 1024);
        ProbeThread thread = new(provider.Board, new Uri("https://stub.test/g/thread/123"));
        try
        {
            thread.StartMonitoring(queue, directory.Path, TimeSpan.FromSeconds(60));
            await TestWait.UntilAsync(() => thread.Clock.Waits == 1);
            thread.UpdateMonitoringInterval(TimeSpan.FromSeconds(120));
            Assert.Equal(1, thread.Checks);
            Assert.Equal(new[] { TimeSpan.FromSeconds(60) }, thread.RequestedIntervals.ToArray());
            thread.Clock.Advance();
            await TestWait.UntilAsync(() => thread.Clock.Waits == 2);
            Assert.Equal(2, thread.Checks);
            Assert.Equal(new[] { TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120) }, thread.RequestedIntervals.ToArray());
        }
        finally
        {
            await thread.StopMonitoringAsync().WaitAsync(TestWait.Timeout);
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public async Task BoardIntervalChangeAppliesToTheNextWaitWithoutStartingAnotherCheck()
    {
        using StubImageBoard provider = new();
        ProbeBoard board = provider.Board;
        try
        {
            board.StartMonitoring(60);
            await TestWait.UntilAsync(() => board.Clock.Waits == 1);
            board.UpdateMonitoringInterval(TimeSpan.FromSeconds(120));
            Assert.Equal(1, board.Checks);
            Assert.Equal(new[] { TimeSpan.FromSeconds(60) }, board.RequestedIntervals.ToArray());
            board.Clock.Advance();
            await TestWait.UntilAsync(() => board.Clock.Waits == 2);
            Assert.Equal(2, board.Checks);
            Assert.Equal(new[] { TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120) }, board.RequestedIntervals.ToArray());
        }
        finally { await board.StopMonitoringAsync().WaitAsync(TestWait.Timeout); }
    }

    [Fact]
    public async Task MarkMediaPendingRemovesCompletionAndNotifiesTheVisibleProgress()
    {
        using StubImageBoard provider = new();
        ProbeThread thread = new(provider.Board, new Uri("https://stub.test/g/thread/123")) { TotalMediaCount = 1 };
        thread.MarkMediaAsDownloaded("123");
        Assert.Equal("Finished", thread.Status);
        TaskCompletionSource<string?> changed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        thread.PropertyChanged += (_, change) =>
        {
            if (!thread.IsMediaDownloaded("123")) changed.TrySetResult(change.PropertyName);
        };

        thread.MarkMediaPending("123");

        Assert.False(thread.IsMediaDownloaded("123"));
        Assert.Equal(0, thread.DownloadedMediaCount);
        Assert.Equal("Pending", thread.Status);
        Assert.Equal(string.Empty, await changed.Task.WaitAsync(TestWait.Timeout));
        thread.MarkMediaPending("123");
        Assert.False(thread.IsMediaDownloaded("123"));
        Assert.Equal(0, thread.DownloadedMediaCount);
        Assert.Equal("Pending", thread.Status);
    }

    [Fact]
    public async Task DownloadedMediaIsSynchronizedAndExposedAsASnapshot()
    {
        using StubImageBoard provider = new();
        ProbeThread thread = new(provider.Board, new Uri("https://stub.test/g/thread/123"));
        await Task.WhenAll(Enumerable.Range(0, 100).Select(index => Task.Run(() =>
        {
            thread.MarkMediaAsDownloaded(index.ToString());
            _ = thread.DownloadedMedia;
            Assert.True(thread.IsMediaDownloaded(index.ToString()));
        })));
        Assert.Equal(100, thread.DownloadedMediaCount);
        HashSet<string> snapshot = thread.DownloadedMedia;
        snapshot.Clear();
        Assert.Equal(100, thread.DownloadedMediaCount);
        HashSet<string> replacement = ["replacement"];
        thread.DownloadedMedia = replacement;
        replacement.Clear();
        Assert.True(thread.IsMediaDownloaded("replacement"));
    }

    [Fact]
    public async Task ThreadStartIsIdempotentAndSlowChecksDoNotOverlap()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard provider = new StubImageBoard();
        DownloadQueue queue = new DownloadQueue(1, 1024 * 1024);
        TaskCompletionSource release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ProbeThread thread = new ProbeThread(provider.Board, new Uri("https://stub.test/g/thread/123")) { OnCheck = () => release.Task };
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
                thread.StartMonitoring(queue, directory.Path, TimeSpan.FromMilliseconds(1)))));
            await TestWait.UntilAsync(() => thread.Checks == 1);
            Assert.Equal(0, thread.Clock.Waits);
            Assert.Equal(1, thread.MaximumActive);
            release.TrySetResult();
            await TestWait.UntilAsync(() => thread.Clock.Waits == 1);
            thread.Clock.Advance();
            await TestWait.UntilAsync(() => thread.Clock.Waits == 2);
            Assert.Equal(2, thread.Checks);
            Assert.Equal(1, thread.MaximumActive);
        }
        finally
        {
            release.TrySetResult();
            await thread.StopMonitoringAsync().WaitAsync(TestWait.Timeout);
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public async Task BoardStartIsIdempotentAndSlowChecksDoNotOverlap()
    {
        using StubImageBoard provider = new StubImageBoard();
        TaskCompletionSource<IEnumerable<IThread>> release = new TaskCompletionSource<IEnumerable<IThread>>(TaskCreationOptions.RunContinuationsAsynchronously);
        ProbeBoard board = provider.Board;
        board.OnCheck = () => release.Task;
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => board.StartMonitoring(1))));
            await TestWait.UntilAsync(() => board.Checks == 1);
            Assert.Equal(0, board.Clock.Waits);
            Assert.Equal(1, board.MaximumActive);
            release.TrySetResult([]);
            await TestWait.UntilAsync(() => board.Clock.Waits == 1);
            board.Clock.Advance();
            await TestWait.UntilAsync(() => board.Clock.Waits == 2);
            Assert.Equal(2, board.Checks);
            Assert.Equal(1, board.MaximumActive);
        }
        finally
        {
            release.TrySetResult([]);
            await board.StopMonitoringAsync().WaitAsync(TestWait.Timeout);
        }
    }
}

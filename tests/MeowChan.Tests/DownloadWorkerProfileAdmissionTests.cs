using System.Net;
using System.Security.Cryptography;
using MeowChan.Downloader;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.Tests;

// Source-only W26 regression coverage. All paths/providers are synthetic.
public sealed class DownloadWorkerProfileAdmissionTests
{
    private static readonly byte[] Payload = [2, 3, 5, 7];
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public void AdmissionLivesOnlyOnPrivateClonesAndIsCheckedAgainstActualLease()
    {
        using TempDirectory root = new();
        using TempDirectory otherRoot = new();
        using ProfileLease expected = ProfileLease.Acquire(root.Path);
        using ProfileLease foreign = ProfileLease.Acquire(otherRoot.Path);
        expected.Admission.CompleteStartup();
        foreign.Admission.CompleteStartup();
        using StubImageBoard board = new();
        DownloadItem item = Item(board, root.Path, expected);
        using ProfileAdmissionToken admission = expected.Admission.EnterOrdinaryWriter();
        DownloadItem execution = item.WithProfileWriteAdmission(admission);
        Assert.NotSame(item, execution);
        Assert.Throws<InvalidOperationException>(() => item.RequireProfileWriteAdmission(expected));
        Assert.Same(admission, execution.RequireProfileWriteAdmission(expected));
        Assert.Throws<InvalidOperationException>(() => execution.RequireProfileWriteAdmission(foreign));
        using ProfileAdmissionToken foreignAdmission = foreign.Admission.EnterOrdinaryWriter();
        Assert.Throws<InvalidOperationException>(() => item.WithProfileWriteAdmission(foreignAdmission)
            .RequireProfileWriteAdmission(expected));
        // All existing worker/queue clones retain exactly the same borrowed token.
        DownloadItem transport = execution.WithWorkAuthority("https://stub.test/g/thread/123", 4)
            .WithPublicationAuthorization(() => { }).WithPublicationFence(null).WithTransportLength(Payload.Length);
        Assert.Same(admission, transport.RequireProfileWriteAdmission(expected));
        admission.Dispose();
        Assert.Throws<InvalidOperationException>(() => transport.RequireProfileWriteAdmission(expected));
    }

    [Theory]
    [InlineData("startup")]
    [InlineData("exclusive")]
    [InlineData("unissued")]
    public async Task ProducerBoundaryRejectsEveryNonordinaryOrUnissuedRoleBeforeRestore(string role)
    {
        using TempDirectory root = new();
        using ProfileLease lease = ProfileLease.Acquire(root.Path);
        if (role != "startup") lease.Admission.CompleteStartup();
        using ProfileAdmissionToken token = role switch
        {
            "startup" => lease.Admission.EnterStartupWriter(),
            "exclusive" => await lease.Admission.EnterExclusiveAsync(),
            _ => ProfileAdmissionToken.Create(lease.Admission, ProfileAdmissionKind.Ordinary)
        };
        int requests = 0;
        using StubImageBoard board = Board(() => requests++);
        DownloadItem item = Item(board, root.Path, lease);
        // A valid legacy-style payload would be adopted and write a manifest if
        // Restore ran. Rejection must precede even that first writer, not just HTTP.
        Directory.CreateDirectory(Path.GetDirectoryName(item.DestinationPath)!);
        await File.WriteAllBytesAsync(item.DestinationPath, Payload);
        string[] before = Directory.GetFiles(Path.GetDirectoryName(item.DestinationPath)!).Order().ToArray();
        DownloadWorker worker = Worker(item);
        Assert.Throws<InvalidOperationException>(() => { _ = worker.ExecuteAdmittedAsync(lease, token, CancellationToken.None); });
        Assert.Equal(before, Directory.GetFiles(Path.GetDirectoryName(item.DestinationPath)!).Order().ToArray());
        Assert.Null(worker.CompletedResult);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task PublicProfileExecutionCannotReenterBehindPendingExclusiveEvenForRestore()
    {
        using TempDirectory root = new();
        using ProfileLease lease = ProfileLease.Acquire(root.Path);
        lease.Admission.CompleteStartup();
        using StubImageBoard board = new();
        DownloadItem item = Item(board, root.Path, lease);
        Directory.CreateDirectory(Path.GetDirectoryName(item.DestinationPath)!);
        await File.WriteAllBytesAsync(item.DestinationPath, Payload);
        using ProfileAdmissionToken blocker = lease.Admission.EnterOrdinaryWriter();
        Task<ProfileAdmissionToken> exclusive = lease.Admission.EnterExclusiveAsync().AsTask();
        try
        {
            Assert.False(exclusive.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => { _ = Worker(item).ExecuteAsync(CancellationToken.None); });
            Assert.Equal(new[] { item.DestinationPath }, Directory.GetFiles(Path.GetDirectoryName(item.DestinationPath)!));
        }
        finally
        {
            blocker.Dispose();
            using ProfileAdmissionToken granted = await exclusive;
        }
    }

    [Fact]
    public async Task AdmittedRestoreFinishesBehindPendingExclusiveWithoutFilteringOrHttp()
    {
        using TempDirectory root = new();
        using ProfileLease lease = ProfileLease.Acquire(root.Path);
        lease.Admission.CompleteStartup();
        int requests = 0;
        using StubImageBoard board = Board(() => requests++);
        DownloadItem item = Item(board, root.Path, lease);
        ProbeFilter filter = new();
        item.Filter = filter;
        Directory.CreateDirectory(Path.GetDirectoryName(item.DestinationPath)!);
        await File.WriteAllBytesAsync(item.DestinationPath, Payload);
        using ProfileAdmissionToken admission = lease.Admission.EnterOrdinaryWriter();
        Task<ProfileAdmissionToken> exclusive = lease.Admission.EnterExclusiveAsync().AsTask();
        try
        {
            DownloadWorker worker = Worker(item);
            await worker.ExecuteAdmittedAsync(lease, admission, CancellationToken.None).WaitAsync(Deadline);
            Assert.True(worker.CompletedResult!.Reused);
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(item.DestinationPath)!, ".meowchan-manifest.json")));
            Assert.False(filter.Acquired.Task.IsCompleted);
            Assert.Equal(0, requests);
            Assert.False(exclusive.IsCompleted); // Worker borrowed; it must not dispose the caller's token.
            Assert.Same(admission, item.WithProfileWriteAdmission(admission).RequireProfileWriteAdmission(lease));
        }
        finally
        {
            admission.Dispose();
            using ProfileAdmissionToken granted = await exclusive;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicCompatibilityOwnsAdmissionThroughFilterDisposalIncludingFailure(bool failCleanup)
    {
        using TempDirectory root = new();
        using ProfileLease lease = ProfileLease.Acquire(root.Path);
        lease.Admission.CompleteStartup();
        using StubImageBoard board = Board();
        ProbeFilter filter = new() { BlockDisposal = true, FailDisposal = failCleanup };
        DownloadItem item = Item(board, root.Path, lease);
        item.Filter = filter;
        DownloadWorker worker = Worker(item);
        Task execution = worker.ExecuteAsync(CancellationToken.None);
        Task<ProfileAdmissionToken>? exclusive = null;
        try
        {
            await filter.Disposing.Task.WaitAsync(Deadline);
            DownloadItem acquired = Assert.IsType<DownloadItem>(filter.AcquiringItem);
            DownloadItem committed = Assert.IsType<DownloadItem>(filter.CommittingItem);
            Assert.NotSame(item, acquired);
            Assert.NotSame(item, committed);
            ProfileAdmissionToken admission = acquired.RequireProfileWriteAdmission(lease);
            Assert.Same(admission, committed.RequireProfileWriteAdmission(lease));
            Assert.Throws<InvalidOperationException>(() => item.RequireProfileWriteAdmission(lease));
            exclusive = lease.Admission.EnterExclusiveAsync().AsTask();
            Assert.False(execution.IsCompleted);
            Assert.False(exclusive.IsCompleted);
            filter.ReleaseDisposal.TrySetResult();
            if (failCleanup) await Assert.ThrowsAsync<IOException>(() => execution);
            else await execution.WaitAsync(Deadline);
            Assert.Throws<InvalidOperationException>(() => acquired.RequireProfileWriteAdmission(lease));
            Assert.Equal(Payload, await File.ReadAllBytesAsync(item.DestinationPath));
        }
        finally
        {
            filter.ReleaseDisposal.TrySetResult();
            await ObserveAsync(execution);
            if (exclusive != null) { using ProfileAdmissionToken granted = await exclusive; }
        }
    }

    [Fact]
    public async Task QueueUsesItsAcceptedJobAfterMonitorUseEndsAndDrainsPhysicalFilterFinally()
    {
        using TempDirectory root = new();
        using ProfileLease lease = ProfileLease.Acquire(root.Path);
        lease.Admission.CompleteStartup();
        using StubImageBoard board = Board();
        using ProfileAdmissionToken admission = lease.Admission.EnterOrdinaryWriter();
        ProfileProducerEpoch epoch = new(admission);
        DownloadQueue queue = new(1, long.MaxValue);
        queue.BindProfileLease(lease);
        queue.InstallProfileEpoch(epoch);
        ProbeFilter filter = new() { BlockAcquisition = true, BlockDisposal = true };
        DownloadItem item = Item(board, root.Path, null);
        item.Filter = filter;
        Task<ProfileAdmissionToken>? exclusive = null;
        try
        {
            using (ProfileProducerEpoch.Use monitor = epoch.Enter())
                Assert.True(queue.TryEnqueue(item));
            await filter.Acquired.Task.WaitAsync(Deadline);
            Task drained = epoch.Close(); // No successor epoch or new Ordinary may be used now.
            exclusive = lease.Admission.EnterExclusiveAsync().AsTask();
            Assert.Same(lease, filter.AcquiringItem!.ContentProfileLease);
            Assert.Null(item.ContentProfileLease);
            Assert.Same(admission, filter.AcquiringItem.RequireProfileWriteAdmission(lease));
            Assert.False(drained.IsCompleted);
            filter.ReleaseAcquisition.TrySetResult();
            await filter.Disposing.Task.WaitAsync(Deadline);
            Assert.Same(admission, filter.CommittingItem!.RequireProfileWriteAdmission(lease));
            Assert.False(drained.IsCompleted);
            Assert.False(exclusive.IsCompleted);
            filter.ReleaseDisposal.TrySetResult();
            await drained.WaitAsync(Deadline); // Unlike queue logical idle, includes finalization and actual Use release.
            await queue.WaitForIdleAsync().WaitAsync(Deadline);
            Assert.True(item.Thread.IsMediaDownloaded(item.MediaIdentifier));
            Assert.Equal(Payload, await File.ReadAllBytesAsync(item.DestinationPath));
            Assert.DoesNotContain(Directory.GetFiles(Path.GetDirectoryName(item.DestinationPath)!), path => path.EndsWith(".part"));
            Assert.Throws<InvalidOperationException>(() => item.RequireProfileWriteAdmission(lease));
            queue.DetachProfileEpoch(epoch);
        }
        finally
        {
            filter.ReleaseAcquisition.TrySetResult();
            filter.ReleaseDisposal.TrySetResult();
            await queue.StopAllAsync();
            await epoch.Close();
            epoch.Dispose();
            if (exclusive != null) { using ProfileAdmissionToken granted = await exclusive; }
        }
    }

    [Fact]
    public async Task QueueBindingIsOneTimeAndRejectsSqliteMismatchInEitherOrder()
    {
        using TempDirectory first = new();
        using TempDirectory second = new();
        using ProfileLease lease = ProfileLease.Acquire(first.Path);
        using ProfileLease foreign = ProfileLease.Acquire(second.Path);
        WorkStateStore foreignStore = new(second.Path, foreign);
        DownloadQueue queue = new(1, long.MaxValue);
        DownloadQueue reverse = new(1, long.MaxValue);
        try
        {
            queue.BindProfileLease(lease);
            queue.BindProfileLease(lease); // Same actual reference is idempotent.
            Assert.Throws<InvalidOperationException>(() => queue.BindProfileLease(foreign));
            Assert.Throws<InvalidOperationException>(() => queue.ConfigureWorkState(foreignStore));
            reverse.ConfigureWorkState(foreignStore);
            Assert.Throws<InvalidOperationException>(() => reverse.BindProfileLease(lease));
            reverse.BindProfileLease(foreign);
        }
        finally { await queue.StopAllAsync(); await reverse.StopAllAsync(); }
    }

    [Fact]
    public async Task QueueBindingCannotBeIntroducedAfterFirstEpochOrJobEvenAfterDetach()
    {
        using TempDirectory root = new();
        using ProfileLease lease = ProfileLease.Acquire(root.Path);
        lease.Admission.CompleteStartup();
        using StubImageBoard board = Board();
        using ProfileAdmissionToken admission = lease.Admission.EnterOrdinaryWriter();
        ProfileProducerEpoch epoch = new(admission);
        DownloadQueue installed = new(1, long.MaxValue);
        DownloadQueue parked = new(1, long.MaxValue);
        try
        {
            installed.InstallProfileEpoch(epoch);
            installed.DetachProfileEpoch(epoch);
            Assert.Throws<InvalidOperationException>(() => installed.BindProfileLease(lease));
            await parked.SetPausedAsync(true);
            Assert.True(parked.TryEnqueue(Item(board, root.Path, null)));
            Assert.Throws<InvalidOperationException>(() => parked.BindProfileLease(lease));
        }
        finally
        {
            await installed.StopAllAsync(); await parked.StopAllAsync();
            await epoch.Close(); epoch.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoundQueueRetainsIdentityAcrossEpochReplacementAndRejectsForeignItemOrToken(bool foreignItem)
    {
        using TempDirectory root = new();
        using TempDirectory other = new();
        using ProfileLease lease = ProfileLease.Acquire(root.Path);
        using ProfileLease foreign = ProfileLease.Acquire(other.Path);
        lease.Admission.CompleteStartup(); foreign.Admission.CompleteStartup();
        int requests = 0;
        using StubImageBoard board = Board(() => requests++);
        ProfileProducerEpoch previous = new(lease.Admission.EnterOrdinaryWriter());
        ProfileProducerEpoch successor = new((foreignItem ? lease : foreign).Admission.EnterOrdinaryWriter());
        DownloadQueue queue = new(1, long.MaxValue);
        queue.BindProfileLease(lease);
        try
        {
            queue.InstallProfileEpoch(previous);
            queue.DetachProfileEpoch(previous);
            await previous.Close(); previous.Dispose();
            queue.BindProfileLease(lease); // Binding survives the empty epoch slot.
            Assert.Throws<InvalidOperationException>(() => queue.BindProfileLease(foreign));
            queue.InstallProfileEpoch(successor);
            DownloadItem item = Item(board, root.Path, foreignItem ? foreign : null);
            Assert.True(queue.TryEnqueue(item));
            await queue.WaitForIdleAsync().WaitAsync(Deadline);
            await successor.Close().WaitAsync(Deadline);
            Assert.Equal(0, requests);
            Assert.NotEmpty(item.Thread.MediaFailures);
            Assert.False(Directory.Exists(Path.GetDirectoryName(item.DestinationPath)));
            queue.DetachProfileEpoch(successor);
        }
        finally
        {
            await queue.StopAllAsync();
            await previous.Close(); previous.Dispose();
            await successor.Close(); successor.Dispose();
        }
    }

    [Fact]
    public async Task TokenBearingQueueWithoutActualLeaseFailsClosedWithRootBindingDiagnostic()
    {
        using TempDirectory root = new();
        using ProfileLease lease = ProfileLease.Acquire(root.Path);
        lease.Admission.CompleteStartup();
        int requests = 0;
        using StubImageBoard board = Board(() => requests++);
        using ProfileAdmissionToken admission = lease.Admission.EnterOrdinaryWriter();
        ProfileProducerEpoch epoch = new(admission);
        DownloadQueue queue = new(1, long.MaxValue);
        queue.InstallProfileEpoch(epoch);
        DownloadItem item = Item(board, root.Path, null);
        try
        {
            Assert.True(queue.TryEnqueue(item));
            await queue.WaitForIdleAsync().WaitAsync(Deadline);
            await epoch.Close().WaitAsync(Deadline);
            Assert.Equal(0, requests);
            Assert.False(Directory.Exists(Path.GetDirectoryName(item.DestinationPath)));
            Assert.Contains("Root must call BindProfileLease", Assert.Single(item.Thread.MediaFailures).Value);
            queue.DetachProfileEpoch(epoch);
        }
        finally
        {
            await queue.StopAllAsync();
            await epoch.Close();
            epoch.Dispose();
        }
    }

    [Fact]
    public async Task ExplicitActualConsumerLeaseSupportsOtherwiseUnboundMediaDescriptor()
    {
        using TempDirectory profile = new();
        using TempDirectory media = new(); // Media location is deliberately not profile identity.
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        lease.Admission.CompleteStartup();
        using StubImageBoard board = Board();
        DownloadItem item = Item(board, media.Path, null);
        ProbeFilter filter = new();
        item.Filter = filter;
        using ProfileAdmissionToken admission = lease.Admission.EnterOrdinaryWriter();
        await Worker(item).ExecuteAdmittedAsync(lease, admission, CancellationToken.None);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(item.DestinationPath));
        Assert.Null(item.ContentProfileLease);
        Assert.Same(lease, filter.AcquiringItem!.ContentProfileLease);
        admission.Dispose();
        using ProfileAdmissionToken exclusive = await lease.Admission.EnterExclusiveAsync();
        Assert.Throws<InvalidOperationException>(() => { _ = Worker(filter.AcquiringItem).ExecuteAsync(CancellationToken.None); });
        Assert.Throws<InvalidOperationException>(() => item.RequireProfileWriteAdmission(lease));
    }

    [Fact]
    public void MissingForeignAndReleasedAdmissionRejectBeforeAnyNetworkOrTargetWrite()
    {
        using TempDirectory root = new();
        using TempDirectory other = new();
        using ProfileLease lease = ProfileLease.Acquire(root.Path);
        using ProfileLease foreign = ProfileLease.Acquire(other.Path);
        lease.Admission.CompleteStartup();
        foreign.Admission.CompleteStartup();
        int requests = 0;
        using StubImageBoard board = Board(() => requests++);
        DownloadItem item = Item(board, root.Path, lease);
        DownloadWorker worker = Worker(item);
        using ProfileAdmissionToken foreignAdmission = foreign.Admission.EnterOrdinaryWriter();
        Assert.Throws<InvalidOperationException>(() => { _ = worker.ExecuteAdmittedAsync(lease, null, CancellationToken.None); });
        Assert.Throws<InvalidOperationException>(() => { _ = worker.ExecuteAdmittedAsync(lease, foreignAdmission, CancellationToken.None); });
        Assert.Throws<InvalidOperationException>(() => { _ = worker.ExecuteAdmittedAsync(foreign, foreignAdmission, CancellationToken.None); });
        using ProfileAdmissionToken released = lease.Admission.EnterOrdinaryWriter();
        released.Dispose();
        Assert.Throws<InvalidOperationException>(() => { _ = worker.ExecuteAdmittedAsync(lease, released, CancellationToken.None); });
        Assert.Throws<InvalidOperationException>(() => { _ = Worker(Item(board, root.Path, null))
            .ExecuteAdmittedAsync(null, foreignAdmission, CancellationToken.None); });
        Assert.False(Directory.Exists(Path.GetDirectoryName(item.DestinationPath)));
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task PublicCancellationBeforeDispatchDoesNotLeaveAnOwnerOrTokenOnTheItem()
    {
        using TempDirectory root = new();
        using ProfileLease lease = ProfileLease.Acquire(root.Path);
        lease.Admission.CompleteStartup();
        using StubImageBoard board = Board();
        DownloadItem item = Item(board, root.Path, lease);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => { _ = Worker(item).ExecuteAsync(cancelled.Token); });
        using ProfileAdmissionToken exclusive = await lease.Admission.EnterExclusiveAsync();
        Assert.Throws<InvalidOperationException>(() => item.RequireProfileWriteAdmission(lease));
        Assert.False(Directory.Exists(Path.GetDirectoryName(item.DestinationPath)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicProfileOwnerJoinsLateBodyOpenOrReadBeforeExclusive(bool opening)
    {
        using TempDirectory root = new();
        using ProfileLease lease = ProfileLease.Acquire(root.Path);
        lease.Admission.CompleteStartup();
        ManualDeadlineClock clock = new();
        DelayedContent content = new();
        LateReadStream body = new();
        using StubImageBoard board = new(new DelegateHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            { Content = opening ? content : new StreamContent(body) })));
        DownloadItem item = Item(board, root.Path, lease);
        DownloadWorker worker = new(item, new BandwidthLimiter(long.MaxValue), new MediaStore(),
            TimeSpan.FromSeconds(1), clock);
        Task execution = worker.ExecuteAsync(CancellationToken.None);
        Task<ProfileAdmissionToken>? exclusive = null;
        try
        {
            await (opening ? content.Started.Task : body.Started.Task).WaitAsync(Deadline);
            exclusive = lease.Admission.EnterExclusiveAsync().AsTask();
            clock.FireActiveTimers(); // Drives the real deadline callback, never a sleep-based ordering assertion.
            if (!opening) await body.Disposing.Task.WaitAsync(Deadline);
            Assert.False(execution.IsCompleted);
            Assert.False(exclusive.IsCompleted);
            if (opening) content.Body.TrySetResult(body);
            body.PendingRead.TrySetResult(0);
            await Assert.ThrowsAsync<TimeoutException>(() => execution.WaitAsync(Deadline));
            await body.Disposing.Task.WaitAsync(Deadline);
            Assert.False(File.Exists(item.DestinationPath));
        }
        finally
        {
            content.Body.TrySetResult(body);
            body.PendingRead.TrySetResult(0);
            try { await execution; } catch (TimeoutException) { }
            if (exclusive != null) { using ProfileAdmissionToken granted = await exclusive; }
        }
    }

    [Fact]
    public async Task AcquisitionOmissionReceivesTheExecutionCloneAndPublicOwnerReleasesOnFault()
    {
        using TempDirectory root = new();
        using ProfileLease lease = ProfileLease.Acquire(root.Path);
        lease.Admission.CompleteStartup();
        int requests = 0;
        using StubImageBoard board = Board(() => requests++);
        DownloadItem item = Item(board, root.Path, lease);
        DownloadItem? recordedItem = null;
        item.Filter = new ProbeFilter
        {
            OmitOnAcquire = executingItem =>
            {
                recordedItem = executingItem;
                executingItem.RequireProfileWriteAdmission(lease);
                Assert.NotSame(item, executingItem);
                throw new MediaFilterOmissionException(true, "synthetic_acquire_omission");
            }
        };
        MediaFilterOmissionException failure = await Assert.ThrowsAsync<MediaFilterOmissionException>(
            () => Worker(item).ExecuteAsync(CancellationToken.None));
        Assert.Equal("synthetic_acquire_omission", failure.ReasonCode);
        Assert.Equal(0, requests);
        Assert.NotNull(recordedItem);
        Assert.Throws<InvalidOperationException>(() => recordedItem!.RequireProfileWriteAdmission(lease));
        using ProfileAdmissionToken exclusive = await lease.Admission.EnterExclusiveAsync();
    }

    [Fact]
    public async Task UnboundStandaloneWorkerAndMediaStoreRemainUsable()
    {
        using TempDirectory root = new();
        int requests = 0;
        using StubImageBoard board = Board(() => requests++);
        DownloadItem item = Item(board, root.Path, null);
        await Worker(item).ExecuteAsync(CancellationToken.None);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(item.DestinationPath));
        MediaCommitResult? restored = await new MediaStore().TryRestoreAsync(item, CancellationToken.None);
        Assert.NotNull(restored);
        Assert.True(restored.Reused);
        Assert.Equal(1, requests);
    }

    private static DownloadWorker Worker(DownloadItem item) => new(item, new BandwidthLimiter(long.MaxValue), new MediaStore());
    private static DownloadItem Item(StubImageBoard board, string root, ProfileLease? lease) => new(
        new Uri("https://stub.test/media/456.bin"), Path.Combine(root, "media", "456.bin"), board,
        board.GetThread(new Uri("https://stub.test/g/thread/123")), "456", Payload.Length,
        Convert.ToBase64String(MD5.HashData(Payload))) { ContentProfileLease = lease };
    private static StubImageBoard Board(Action? request = null) => new(new DelegateHandler((_, _) =>
    {
        request?.Invoke();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });
    }));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task ObserveAsync(Task task)
    {
        try { await task; }
        catch (IOException) { } // Cleanup-failure variant asserts the actual failure in its normal path.
    }

    private sealed class LateReadStream : MemoryStream
    {
        public TaskCompletionSource Started { get; } = Signal();
        public TaskCompletionSource Disposing { get; } = Signal();
        public TaskCompletionSource<int> PendingRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            return new(PendingRead.Task); // Deliberately ignores cancellation and synchronous disposal.
        }
        public override ValueTask DisposeAsync()
        {
            Disposing.TrySetResult();
            return base.DisposeAsync();
        }
    }

    private sealed class DelayedContent : HttpContent
    {
        public TaskCompletionSource Started { get; } = Signal();
        public TaskCompletionSource<Stream> Body { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return Body.Task; // Deliberately ignores cancellation until the test releases actual ownership.
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override bool TryComputeLength(out long length) { length = Payload.Length; return true; }
    }

    private sealed class ManualDeadlineClock : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<DeadlineTimer> _timers = new();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            DeadlineTimer timer = new(this, callback, state);
            lock (_sync) _timers.Add(timer);
            return timer;
        }
        public void FireActiveTimers()
        {
            DeadlineTimer[] active;
            lock (_sync) active = _timers.ToArray();
            foreach (DeadlineTimer timer in active) timer.Fire();
        }
        private sealed class DeadlineTimer(ManualDeadlineClock owner, TimerCallback callback, object? state) : ITimer
        {
            public void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { lock (owner._sync) owner._timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class ProbeFilter : IOriginalMediaFilter, IOriginalMediaFilterLease
    {
        public bool BlockAcquisition { get; init; }
        public bool BlockDisposal { get; init; }
        public bool FailDisposal { get; init; }
        public Action<DownloadItem>? OmitOnAcquire { get; init; }
        public DownloadItem? AcquiringItem { get; private set; }
        public DownloadItem? CommittingItem { get; private set; }
        public TaskCompletionSource Acquired { get; } = Signal();
        public TaskCompletionSource ReleaseAcquisition { get; } = Signal();
        public TaskCompletionSource Disposing { get; } = Signal();
        public TaskCompletionSource ReleaseDisposal { get; } = Signal();
        public bool CanStart => true;
        public event Action? CapacityChanged { add { } remove { } }
        public async Task<IOriginalMediaFilterLease> AcquireAsync(DownloadItem item, CancellationToken cancellationToken)
        {
            AcquiringItem = item;
            OmitOnAcquire?.Invoke(item);
            Acquired.TrySetResult();
            if (BlockAcquisition) await ReleaseAcquisition.Task.WaitAsync(cancellationToken);
            return this;
        }
        public Task<MediaCommitResult> CommitAsync(DownloadItem item, Stream source, MediaStore store,
            Func<int, CancellationToken, Task> throttle, CancellationToken cancellationToken)
        {
            CommittingItem = item;
            return store.CommitAsync(item, source, cancellationToken, throttle);
        }
        public async ValueTask DisposeAsync()
        {
            Disposing.TrySetResult();
            if (BlockDisposal) await ReleaseDisposal.Task;
            if (FailDisposal) throw new IOException("synthetic filter cleanup failure");
        }
    }
}

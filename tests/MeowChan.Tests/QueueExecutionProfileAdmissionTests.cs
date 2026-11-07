using System.Net;
using System.Reflection;
using MeowChan.Archive;
using MeowChan.Boards.Base;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Rules;
using MeowChan.Storage;

namespace MeowChan.Tests;

// Integration, not a replacement worker/filter: real queue, worker, runtime callback and MediaStore.
public sealed class QueueExecutionProfileAdmissionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static readonly ResourceId Id = new("stub", "g", "123");
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task AcceptedQueueCloneRecordsIntoBoundRuntimeAndExclusiveJoinsCallbackAndPhysicalFinally()
    {
        using TempDirectory root = new();
        using TempDirectory foreignRoot = new();
        using ProfileLease lease = ProfileLease.Acquire(root.Path);
        using ProfileLease foreignLease = ProfileLease.Acquire(foreignRoot.Path);
        byte[] payload = [11, 17, 23, 31];
        int requests = 0;
        using StubImageBoard board = new(new DelegateHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        }));
        await using ThreadContentRuntime runtime = new(root.Path, lease);
        await runtime.InitializeAsync();
        TestThread thread = new(board.Board);
        await runtime.BindAsync(thread, root.Path, restored: true);
        ThreadContentContext context = (ThreadContentContext)typeof(BaseThread)
            .GetField("_content", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(thread)!;
        await context.CaptureAsync(new CapturedThreadSnapshot(Id, thread.Url, "synthetic queue",
            ThreadSourceState.Active, DateTimeOffset.UnixEpoch, root.Path,
            [new CapturedPost("123", 0, PostFieldPresence.Html, Html: "inert")]), default);
        await runtime.Rules.SaveAsync([new MediaRule(MediaRuleScope.Global)
            { ImageFilter = new(MediaImageFilterMode.Plugin, "synthetic", "integration") }]);
        CallbackFactory factory = new();
        runtime.OriginalFilters = factory;
        lease.Admission.CompleteStartup();
        foreignLease.Admission.CompleteStartup();
        DownloadItem descriptor;
        using (ProfileAdmissionToken monitor = lease.Admission.EnterOrdinaryWriter())
            descriptor = Assert.Single((await context.SelectAdmittedAsync(monitor, thread,
                [new DownloadItem(new Uri("https://stub.test/media/777.png"), Path.Combine(root.Path, "media", "777.png"),
                    board, thread, "777", originalFileName: "777.png")], default))!);
        // Old monitor has ended before the queue accepts its independent producer generation.
        using ProfileAdmissionToken oldGeneration = lease.Admission.EnterOrdinaryWriter();
        oldGeneration.Dispose();
        ProfileProducerEpoch epoch = new(lease.Admission.EnterOrdinaryWriter());
        DownloadQueue queue = new(1, long.MaxValue);
        queue.BindProfileLease(lease);
        queue.InstallProfileEpoch(epoch);
        using CancellationTokenSource abandon = new();
        Task<ProfileAdmissionToken>? exclusive = null;
        try
        {
            Assert.True(queue.TryEnqueue(descriptor));
            await factory.Entered.Task.WaitAsync(Deadline);
            DownloadItem execution = Assert.IsType<DownloadItem>(factory.Execution);
            Assert.NotSame(descriptor, execution);
            Assert.Same(epoch.Admission, execution.RequireProfileWriteAdmission(lease));
            Assert.Throws<InvalidOperationException>(() => descriptor.RequireProfileWriteAdmission(lease));
            Task drain = epoch.Close();
            exclusive = lease.Admission.EnterExclusiveAsync(abandon.Token).AsTask();
            Assert.False(drain.IsCompleted);
            Assert.False(exclusive.IsCompleted);
            int writes = 0;
            runtime.Catalog.BeforeWriteDispatchForTests = token =>
            {
                Assert.Same(epoch.Admission, token);
                Interlocked.Increment(ref writes);
            };
            ThreadContentStatus before = context.Snapshot;
            CandidateDecision decisionBefore = Assert.Single(await runtime.Catalog.GetDecisionsAsync(Id));
            using ProfileAdmissionToken foreign = foreignLease.Admission.EnterOrdinaryWriter();
            foreach (DownloadItem invalid in new[] { descriptor, execution.WithProfileWriteAdmission(foreign),
                execution.WithProfileWriteAdmission(oldGeneration) })
                await Assert.ThrowsAsync<InvalidOperationException>(() => factory.Record!(invalid, false, "must-not-persist", default));
            Assert.Equal(0, writes);
            Assert.Equal(before, context.Snapshot);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(decisionBefore),
                System.Text.Json.JsonSerializer.Serialize(Assert.Single(await runtime.Catalog.GetDecisionsAsync(Id))));
            // The actual worker resumes into the callback captured by actual SelectAdmittedAsync.
            // Re-entering Ordinary here would fail behind pending Exclusive and cannot satisfy the oracles.
            factory.ReleaseCallback.TrySetResult();
            await factory.Disposing.Task.WaitAsync(Deadline);
            Assert.Equal(1, writes);
            Assert.Equal(1, requests);
            Assert.Equal(payload, await File.ReadAllBytesAsync(descriptor.DestinationPath));
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(descriptor.DestinationPath)!, ".meowchan-manifest.json")));
            Assert.Equal(MediaSelectionOutcome.TechnicalOmission,
                Assert.Single(await runtime.Catalog.GetDecisionsAsync(Id)).Selection.Outcome);
            Assert.NotNull(context.Snapshot.SelectionFailure);
            Assert.Throws<IOException>(() =>
            {
                using FileStream conflicting = File.Open(descriptor.DestinationPath, FileMode.Open, FileAccess.Read, FileShare.None);
            });
            Assert.False(drain.IsCompleted); // bytes/manifest committed, actual FS lease in filter finally still held
            Assert.False(exclusive.IsCompleted);
            Assert.Same(epoch.Admission, factory.Committing!.RequireProfileWriteAdmission(lease));
            factory.ReleaseDisposal.TrySetResult();
            await drain.WaitAsync(Deadline);
            await queue.WaitForIdleAsync().WaitAsync(Deadline);
            queue.DetachProfileEpoch(epoch);
            epoch.Dispose();
            using ProfileAdmissionToken granted = await exclusive.WaitAsync(Deadline);
            Assert.Throws<InvalidOperationException>(() => execution.RequireProfileWriteAdmission(lease));
            Assert.True(thread.IsMediaDownloaded(descriptor.MediaIdentifier));
            using FileStream reopened = File.Open(descriptor.DestinationPath, FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.Equal(payload.Length, reopened.Length);
        }
        finally
        {
            factory.ReleaseCallback.TrySetResult();
            factory.ReleaseDisposal.TrySetResult();
            abandon.Cancel();
            await queue.StopAllAsync().WaitAsync(Deadline);
            await epoch.Close().WaitAsync(Deadline);
            epoch.Dispose();
            if (exclusive != null)
            {
                try { (await exclusive.WaitAsync(Deadline)).Dispose(); }
                catch (OperationCanceledException) { }
            }
        }
    }

    private sealed class CallbackFactory : IOriginalMediaFilterFactory, IOriginalMediaFilter, IOriginalMediaFilterLease
    {
        internal Func<DownloadItem, bool?, string, CancellationToken, Task>? Record;
        internal DownloadItem? Execution, Committing;
        private FileStream? _physicalLease;
        internal readonly TaskCompletionSource Entered = Signal(), ReleaseCallback = Signal(),
            Disposing = Signal(), ReleaseDisposal = Signal();
        public bool CanStart => true;
        public event Action? CapacityChanged { add { } remove { } }
        public IOriginalMediaFilter Create(DownloadItem item, CandidateDecision decision, Func<bool> isCurrent,
            Func<DownloadItem, bool?, string, CancellationToken, Task> recordOmission)
        { Record = recordOmission; return this; }
        public async Task<IOriginalMediaFilterLease> AcquireAsync(DownloadItem item, CancellationToken cancellationToken)
        {
            Execution = item;
            Entered.TrySetResult();
            await ReleaseCallback.Task.WaitAsync(cancellationToken);
            await Record!(item, false, "synthetic-queue-callback", cancellationToken);
            return this;
        }
        public async Task<MediaCommitResult> CommitAsync(DownloadItem item, Stream source, MediaStore store,
            Func<int, CancellationToken, Task> throttle, CancellationToken cancellationToken)
        {
            Committing = item;
            MediaCommitResult result = await store.CommitAsync(item, source, cancellationToken, throttle);
            // Real exclusive file handle owned by this accepted filter lease, not a fake FS-disposed flag.
            _physicalLease = File.Open(item.DestinationPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return result;
        }
        public async ValueTask DisposeAsync()
        {
            Disposing.TrySetResult();
            try { await ReleaseDisposal.Task; }
            finally { if (_physicalLease != null) await _physicalLease.DisposeAsync(); }
        }
    }

    private sealed class TestThread(IBoard board) : BaseThread
    {
        public override IBoard Board => board;
        public override string ThreadId => "123";
        public override string NiceName => Title;
        public override Uri ThreadUri => new("https://stub.test/g/thread/123");
        public override bool SupportsContent => true;
        public override Task checkThreadAsync(DownloadQueue queue, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}

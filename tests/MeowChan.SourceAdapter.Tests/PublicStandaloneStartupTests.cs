using System.Collections.Concurrent;
using MeowChan.Admission;
using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.OfficialSources.FourChan;
using MeowChan.Persistence;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Sources;
using MeowChan.PluginSdk;

namespace MeowChan.SourceAdapter.Tests;

// Acceptance contract for the NEW, explicitly authorized public StartAsync API.
// This assembly is deliberately not a libmeowchan friend assembly.
public sealed class PublicStandaloneStartupTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static string Url(string id) => $"https://boards.4chan.org/g/thread/{id}";

    [Theory]
    [InlineData("work", false)]
    [InlineData("work", true)]
    [InlineData("runtime", false)]
    [InlineData("runtime", true)]
    [InlineData("both-session", false)]
    [InlineData("both-session", true)]
    public async Task PublicStartRestoresCompositionAndPreservesUserPause(string composition, bool paused)
    {
        using TemporaryProfile profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        WorkStateStore? work = composition == "runtime" ? null : new(profile.Path, lease);
        await using ThreadContentRuntime? content = composition == "work" ? null : new(profile.Path, lease);
        SessionStore? session = composition == "both-session" ? new(profile.Path, lease) : null;
        if (work != null) Seed(work, profile.Path);
        if (session != null)
            await session.SaveAsync(new SessionSnapshot { ThreadUrls = [Url("123"), Url("90001"), Url("90002")] });
        // An already-initialized runtime remains supported, but does not make Ready.
        if (composition == "both-session") await content!.InitializeAsync();
        using FakeWorker worker = new();
        using PluginSourceImageBoard source = Source(worker);
        DownloadQueue queue = new(1, 1048576);
        queue.SetPaused(paused);
        await using Registry registry = new([source], queue, profile.Output, TimeSpan.FromHours(1), 3600,
            sessionStore: session, contentRuntime: content, workStore: work);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.AddAsync(Url("124")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.RestoreAsync());
        Assert.Empty(worker.Calls);
        Assert.Empty(registry.Threads);

        await registry.StartAsync(lease).WaitAsync(Deadline);
        Assert.Equal(paused, registry.IsPaused);
        Assert.Equal(paused, queue.IsPaused);
        string[] restored = work == null ? [] : [Url("123"), Url("125")];
        Assert.Equal(restored.Order(), registry.Threads.Select(thread => thread.Url).Order());
        Assert.DoesNotContain(registry.Threads, thread => thread.Url == Url("90001") || thread.Url == Url("90002"));
        Assert.True(File.Exists(Path.Combine(profile.Path, "archive.db")));
        if (content != null) Assert.True(content.IsInitialized);
        if (paused)
        {
            Assert.Empty(worker.Calls);
            await registry.SetPausedAsync(false);
        }
        foreach (string url in restored)
            await UntilAsync(() => registry.Threads.Single(thread => thread.Url == url).Title == "public startup capture");
        await registry.RestoreAsync(); // Start already restored; this is a no-op.
        Assert.Equal(restored.Length, registry.Threads.Count);
        AdmissionResult added = await registry.AddAsync(Url("124"));
        Assert.Equal(AdmissionStatus.Added, added.Status);
        await UntilAsync(() => added.Thread!.Title == "public startup capture");
        Assert.Equal(restored.Length + 1, registry.Threads.Count);
        Assert.All(worker.Calls, pair => Assert.Equal(1, pair.Value));
        Assert.Equal(restored.Length + 1, worker.Calls.Count);
        if (content != null)
        {
            await using ArchiveReader reader = await content.OpenReaderAsync();
            await UntilAsync(async () => (await reader.ReadThreadAsync(new ResourceId("4chan", "g", "124")))?.PostCount == 1);
            ArchivedThreadView view = (await reader.ReadThreadAsync(new ResourceId("4chan", "g", "124")))!;
            Assert.Equal("captured through public startup", Assert.Single(view.GetPosts()).Html);
        }
        await registry.StopAsync().WaitAsync(Deadline);
        // Normal Stop is Registry-local: an ordinary store write still works.
        if (work != null)
            Assert.Equal(WorkIntent.Watching, work.CommitIntent(Resource(profile.Output, "126", WorkIntent.Watching)).Intent);
    }

    [Fact]
    public async Task PendingStartSuspendsRestoreAndCachesTaskWhileCompetitorCannotPoisonOwner()
    {
        using TemporaryProfile profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        WorkStateStore work = new(profile.Path, lease);
        Seed(work, profile.Output);
        GatedSessionStore session = new(new SessionSnapshot { ThreadUrls = [Url("123")] });
        using FakeWorker worker = new();
        using PluginSourceImageBoard source = Source(worker);
        await using Registry registry = new([source], new DownloadQueue(1, 1048576), profile.Output,
            TimeSpan.FromHours(1), 3600, sessionStore: session, workStore: work);
        BoardAdmissionStore admission = new(profile.Path, lease);
        registry.ConfigureBoardAdmission(admission);
        worker.ObserveWatchCount = () => registry.Threads.Count;
        Task start = registry.StartAsync(lease);
        try
        {
            await session.Entered.Task.WaitAsync(Deadline);
            Assert.False(start.IsCompleted);
            Assert.Empty(worker.Calls);
            Assert.Empty(registry.Threads);
            Assert.Equal(1, session.Loads);
            Assert.Same(start, registry.StartAsync(lease));
            using CancellationTokenSource laterCaller = new();
            laterCaller.Cancel();
            Assert.Same(start, registry.StartAsync(lease, laterCaller.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => registry.AddAsync(Url("124")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => registry.RestoreAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => registry.SetPausedAsync(true));
            Assert.Throws<InvalidOperationException>(() => registry.ConfigureBoardAdmission(admission));
            await Assert.ThrowsAsync<InvalidOperationException>(() => PluginRegistryAdapter.RegisterBeforeRestoreAsync(registry, [source]).WaitAsync(Deadline));
            using TemporaryProfile other = new();
            using ProfileLease otherLease = ProfileLease.Acquire(other.Path);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await registry.StartAsync(otherLease));

            // Dispose the rejected Registry before releasing the winner: its Stop
            // must not seal or otherwise steal this lease's startup ownership.
            await using (Registry competitor = new([source], new DownloadQueue(1, 1048576), profile.Output,
                TimeSpan.FromHours(1), 3600, workStore: work))
            {
                await Assert.ThrowsAsync<InvalidOperationException>(async () => await competitor.StartAsync(lease));
            }
            Assert.False(start.IsCompleted);
            Assert.Empty(worker.Calls);
        }
        finally { session.Release.TrySetResult(); }
        await start.WaitAsync(Deadline);
        Assert.Same(start, registry.StartAsync(lease));
        Assert.Equal(1, session.Loads);
        Assert.Equal(new[] { Url("123"), Url("125") }, registry.Threads.Select(thread => thread.Url).Order());
        await UntilAsync(() => registry.Threads.All(thread => thread.Title == "public startup capture"));
        Assert.Equal(2, worker.Calls.Count);
        Assert.All(worker.WatchCountsAtRead, count => Assert.Equal(2, count));
        Assert.All(worker.Calls, pair => Assert.Equal(1, pair.Value));
        Assert.Equal(AdmissionStatus.Added, (await registry.AddAsync(Url("124"))).Status);
        await registry.StopAsync().WaitAsync(Deadline);
    }

    [Fact]
    public async Task RejectedUnboundCompetitorCanStartAnIndependentLease()
    {
        using TemporaryProfile profileA = new();
        using TemporaryProfile profileB = new();
        using ProfileLease leaseA = ProfileLease.Acquire(profileA.Path);
        using ProfileLease leaseB = ProfileLease.Acquire(profileB.Path);
        GatedSessionStore session = new(new SessionSnapshot());
        using FakeWorker worker = new();
        using PluginSourceImageBoard source = Source(worker);
        await using Registry owner = new([source], new DownloadQueue(1, 1048576), profileA.Output,
            TimeSpan.FromHours(1), 3600, sessionStore: session);
        // No bound stores or prior Restore: the competitor and its queue are fresh.
        await using Registry competitor = new([source], new DownloadQueue(1, 1048576), profileB.Output,
            TimeSpan.FromHours(1), 3600);
        Task ownerStart = owner.StartAsync(leaseA);
        Task? independentStart = null;
        try
        {
            await session.Entered.Task.WaitAsync(Deadline);
            Assert.False(ownerStart.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await competitor.StartAsync(leaseA));
            Assert.Empty(competitor.Threads);
            Assert.Empty(worker.Calls);
            Assert.False(File.Exists(Path.Combine(profileB.Path, "archive.db")));

            independentStart = competitor.StartAsync(leaseB);
            await independentStart.WaitAsync(Deadline);
            Assert.Same(independentStart, competitor.StartAsync(leaseB));
            Assert.False(ownerStart.IsCompleted); // A remains owned and gated independently.
            Assert.Equal(AdmissionStatus.Added, (await competitor.AddAsync(Url("124"))).Status);
            await UntilAsync(() => competitor.Threads.Single().Title == "public startup capture");
            Assert.Equal(1, worker.Calls["124"]);
            Assert.True(File.Exists(Path.Combine(profileB.Path, "archive.db")));
        }
        finally
        {
            session.Release.TrySetResult();
            try { await ownerStart.WaitAsync(Deadline); }
            finally
            {
                if (independentStart != null) await independentStart.WaitAsync(Deadline);
            }
        }
        Assert.Same(ownerStart, owner.StartAsync(leaseA));
        Assert.Equal(AdmissionStatus.Added, (await owner.AddAsync(Url("123"))).Status);
        await owner.StopAsync().WaitAsync(Deadline);
        await competitor.StopAsync().WaitAsync(Deadline);
    }

    [Theory]
    [InlineData("work")]
    [InlineData("session")]
    [InlineData("runtime")]
    [InlineData("admission")]
    public async Task SamePathPreviousLeaseComponentsRejectBeforeClaimWithoutPoisoningNewLease(string component)
    {
        using TemporaryProfile profile = new();
        WorkStateStore staleWork;
        SessionStore staleSession;
        BoardAdmissionStore staleAdmission;
        ThreadContentRuntime staleRuntime;
        using (ProfileLease previous = ProfileLease.Acquire(profile.Path))
        {
            staleWork = new(profile.Path, previous);
            staleSession = new(profile.Path, previous);
            staleAdmission = new(profile.Path, previous);
            staleRuntime = new(profile.Path, previous);
        }
        await using ThreadContentRuntime staleRuntimeOwner = staleRuntime;
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        using FakeWorker worker = new();
        using PluginSourceImageBoard source = Source(worker);
        // Work is deliberately first in lease discovery for the session/runtime
        // cases: checking only the first non-null built-in lease is insufficient.
        WorkStateStore? work = component == "work" ? staleWork
            : component == "admission" ? null : new(profile.Path, lease);
        await using (Registry mismatched = new([source], new DownloadQueue(1, 1048576), profile.Output,
            TimeSpan.FromHours(1), 3600, sessionStore: component == "session" ? staleSession : null,
            contentRuntime: component == "runtime" ? staleRuntime : null, workStore: work))
        {
            if (component == "admission") mismatched.ConfigureBoardAdmission(staleAdmission);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await mismatched.StartAsync(lease));
            Assert.Empty(mismatched.Threads);
            Assert.Empty(worker.Calls);
            Assert.False(staleRuntime.IsInitialized);
            Assert.False(File.Exists(Path.Combine(profile.Path, "archive.db")));
        }
        await using Registry valid = new([source], new DownloadQueue(1, 1048576), profile.Output,
            TimeSpan.FromHours(1), 3600, workStore: new WorkStateStore(profile.Path, lease));
        await valid.StartAsync(lease).WaitAsync(Deadline);
        Assert.Equal(AdmissionStatus.Added, (await valid.AddAsync(Url("124"))).Status);
        await valid.StopAsync().WaitAsync(Deadline);
    }

    [Fact]
    public async Task SuccessfulStopLeavesLeaseUsableByOrdinarySuccessorRegistry()
    {
        using TemporaryProfile profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        WorkStateStore work = new(profile.Path, lease);
        using FakeWorker worker = new();
        using PluginSourceImageBoard source = Source(worker);
        await using (Registry first = new([source], new DownloadQueue(1, 1048576), profile.Output,
            TimeSpan.FromHours(1), 3600, workStore: work))
        {
            await first.StartAsync(lease).WaitAsync(Deadline);
            Assert.Equal(AdmissionStatus.Added, (await first.AddAsync(Url("123"))).Status);
            await first.StopAsync().WaitAsync(Deadline);
        }
        await using Registry successor = new([source], new DownloadQueue(1, 1048576), profile.Output,
            TimeSpan.FromHours(1), 3600, workStore: work);
        await successor.RestoreAsync().WaitAsync(Deadline); // Ready lease, not another bootstrap.
        Assert.Equal(Url("123"), Assert.Single(successor.Threads).Url);
        Assert.Equal(AdmissionStatus.Added, (await successor.AddAsync(Url("124"))).Status);
        await successor.StopAsync().WaitAsync(Deadline);
    }

    [Fact]
    public async Task UnboundRegistryRetainsRestoreAddAndPauseBehaviorWithoutStart()
    {
        using TemporaryProfile profile = new();
        using FakeWorker worker = new();
        using PluginSourceImageBoard source = Source(worker);
        DownloadQueue queue = new(1, 1048576);
        queue.SetPaused(true);
        await using Registry registry = new([source], queue, profile.Output, TimeSpan.FromHours(1), 3600);
        await registry.RestoreAsync();
        AdmissionResult added = await registry.AddAsync(Url("123"));
        Assert.Equal(AdmissionStatus.Added, added.Status);
        Assert.True(registry.IsPaused);
        Assert.Empty(worker.Calls);
        await registry.RestoreAsync();
        Assert.Same(added.Thread, Assert.Single(registry.Threads));
        await registry.SetPausedAsync(false);
        await UntilAsync(() => added.Thread!.Title == "public startup capture");
        Assert.Equal(1, worker.Calls["123"]);
        Assert.False(File.Exists(Path.Combine(profile.Path, "archive.db")));
        await registry.StopAsync().WaitAsync(Deadline);
    }

    private static PluginSourceImageBoard Source(FakeWorker worker) => new(worker.Description, "4chan",
        _ => Task.FromResult<IPluginWorkerClient>(worker));

    private static WorkResource Resource(string output, string id, WorkIntent intent) => new()
    { Url = Url(id), OutputRoot = output, Generation = 1, IsThread = true, Intent = intent };

    private static void Seed(WorkStateStore work, string output)
    {
        work.CommitIntent(Resource(output, "123", WorkIntent.Watching));
        work.CommitIntent(Resource(output, "125", WorkIntent.Repairing));
        work.CommitIntent(Resource(output, "90001", WorkIntent.Suppressed));
        work.CommitIntent(Resource(output, "90002", WorkIntent.Completed));
    }

    private static Task UntilAsync(Func<bool> condition) => UntilAsync(() => Task.FromResult(condition()));
    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        using CancellationTokenSource timeout = new(Deadline);
        while (!await condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class GatedSessionStore(SessionSnapshot snapshot) : ISessionStore
    {
        private int _loads;
        public int Loads => Volatile.Read(ref _loads);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<SessionLoadResult> LoadAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _loads);
            Entered.TrySetResult();
            await Release.Task;
            return new(snapshot, null, false);
        }
        public Task SaveAsync(SessionSnapshot next, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeWorker : IPluginWorkerClient, IDisposable
    {
        public ConcurrentDictionary<string, int> Calls { get; } = new();
        public Func<int>? ObserveWatchCount { get; set; }
        public ConcurrentBag<int> WatchCountsAtRead { get; } = new();
        public PluginDescription Description { get; } = new FourChanPlugin().Describe();
        public PluginWorkerIdentity Identity { get; } = new("public-startup-test", "synthetic", "no-process");
        public PluginWorkerSnapshot Snapshot => new(Identity, "ready", null, null, null, Array.Empty<string>());
        public Task<ThreadReadResult> ReadThreadAsync(ThreadReadRequest request, PluginOperationContext operation,
            CancellationToken cancellationToken = default)
        {
            if (ObserveWatchCount != null) WatchCountsAtRead.Add(ObserveWatchCount());
            Calls.AddOrUpdate(request.Thread.ThreadId, 1, (_, value) => value + 1);
            return Task.FromResult<ThreadReadResult>(new MeowChan.PluginSdk.ThreadSnapshot(new SourceThreadSnapshot(
                request.Thread, new Uri(Url(request.Thread.ThreadId)), "public startup capture", SourceThreadState.Active,
                [new SourcePost(request.Thread.ThreadId, 0, SourceFieldPresence.Text,
                    Text: "captured through public startup", TextFormat: SourceTextFormat.PlainText)])));
        }
        public Task<CatalogReadResult> ReadCatalogAsync(CatalogReadRequest request, PluginOperationContext operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException("No catalog request expected.");
        public Task<ImageFilterResult> EvaluateAsync(ImageFilterRequest request, PluginOperationContext operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException("No filter request expected.");
        public Task<IReadOnlyList<FieldValidationError>> ValidateSettingsAsync(SettingsValidationRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<FieldValidationError>>([]);
        public Task RetryAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Dispose() { }
    }

    private sealed class TemporaryProfile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "meowchan-public-startup-" + Guid.NewGuid().ToString("N"));
        public string Output => System.IO.Path.Combine(Path, "media");
        public TemporaryProfile() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

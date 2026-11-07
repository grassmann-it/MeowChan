using System.Reflection;
using MeowChan.Archive;
using MeowChan.Boards.Base;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Rules;
using MeowChan.Storage;

namespace MeowChan.Tests;

// Synthetic profiles only; gates establish order, deadlines are watchdogs. No
// provider/decoder transport is used. Root runs this slice after shared API integration.
public sealed class ThreadContentAdmissionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static readonly ResourceId Id = new("stub", "g", "123");
    private static readonly string[] Operations = ["capture", "title", "source", "toggle", "select", "base-toggle", "bind"];
    public static IEnumerable<object[]> Cases => Operations.Select(op => new object[] { op });
    public static IEnumerable<object[]> PhaseCases => Operations.SelectMany(op =>
        new[] { new object[] { op, false }, new object[] { op, true } });

    [Theory, MemberData(nameof(PhaseCases))]
    public async Task CompatibleCallsOwnOneWriterAcrossEveryChildCommit(string op, bool ready)
    {
        await using Fixture f = await Fixture.OpenAsync();
        await f.PrepareAsync(op);
        if (ready) f.Lease.Admission.CompleteStartup();
        ProfileAdmissionToken? accepted = null;
        int dispatches = 0, commits = 0;
        f.Runtime.Catalog.BeforeWriteDispatchForTests = token =>
        {
            accepted ??= token;
            Assert.Same(accepted, token);
            Assert.Same(token, Assert.Single(Active(f.Lease)));
            Assert.Equal(ready ? ProfileAdmissionKind.Ordinary : ProfileAdmissionKind.Startup, token.Kind);
            dispatches++;
        };
        f.Runtime.Catalog.AfterWriteCommitForTests = token =>
        {
            Assert.Same(accepted, token);
            Assert.Same(token, Assert.Single(Active(f.Lease)));
            commits++;
        };
        await f.Invoke(op).WaitAsync(Deadline);
        Assert.True(dispatches > 0);
        Assert.Equal(dispatches, commits);
        if (op is "toggle" or "base-toggle") Assert.True(commits >= 2); // preference + forceLatest, same owner
        Assert.Empty(Active(f.Lease));
        Assert.Throws<InvalidOperationException>(() => f.Lease.RunStoreWrite(accepted!, () => true));
        Assert.Null(f.Context.Snapshot.CaptureFailure);
        Assert.Null(f.Context.Snapshot.SelectionFailure);
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task PendingAndActiveExclusiveBlockDirectCallsButAcceptedContextOwnerDrains(string op)
    {
        await using Fixture f = await Fixture.OpenAsync();
        await f.PrepareAsync(op);
        f.Lease.Admission.CompleteStartup();
        using ProfileAdmissionToken owner = f.Lease.Admission.EnterOrdinaryWriter();
        using CancellationTokenSource abandon = new();
        Task<ProfileAdmissionToken> pending = f.Lease.Admission.EnterExclusiveAsync(abandon.Token).AsTask();
        try
        {
            Assert.False(pending.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => { _ = f.Invoke(op); });
            int commits = 0;
            f.Runtime.Catalog.AfterWriteCommitForTests = token => { Assert.Same(owner, token); commits++; };
            await f.Invoke(op, owner).WaitAsync(Deadline);
            Assert.True(commits > 0);
            Assert.Same(owner, Assert.Single(Active(f.Lease)));
            Assert.False(pending.IsCompleted);
            owner.Dispose();
            using ProfileAdmissionToken exclusive = await pending.WaitAsync(Deadline);
            Assert.Throws<InvalidOperationException>(() => { _ = f.Invoke(op); });
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.InvokeBorrowed(op, exclusive));
        }
        finally
        {
            abandon.Cancel(); owner.Dispose();
            await ReleasePending(pending);
        }
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task BorrowedCallsRejectForeignDisposedUnissuedAndNullBeforeContextOrSqlMutation(string op)
    {
        await using Fixture f = await Fixture.OpenAsync();
        await f.PrepareAsync(op);
        await using Fixture other = await Fixture.OpenAsync();
        using ProfileAdmissionToken foreign = other.Lease.Admission.EnterStartupWriter();
        using ProfileAdmissionToken disposed = f.Lease.Admission.EnterStartupWriter(); disposed.Dispose();
        using ProfileAdmissionToken unissued = ProfileAdmissionToken.Create(f.Lease.Admission, ProfileAdmissionKind.Startup);
        ThreadContentStatus before = f.Context.Snapshot;
        string title = f.Thread.Title;
        int dispatched = 0;
        f.Runtime.Catalog.BeforeWriteDispatchForTests = _ => dispatched++;
        foreach (ProfileAdmissionToken bad in new[] { foreign, disposed, unissued })
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.InvokeBorrowed(op, bad));
        await Assert.ThrowsAsync<ArgumentNullException>(() => f.InvokeBorrowed(op, null!));
        Assert.Equal(0, dispatched);
        Assert.Equal(before, f.Context.Snapshot);
        Assert.Equal(title, f.Thread.Title);
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task CancelBeforeSqlDispatchReleasesCompatibleWholeCallOwner(string op)
    {
        await using Fixture f = await Fixture.OpenAsync();
        await f.PrepareAsync(op);
        f.Lease.Admission.CompleteStartup();
        using CancellationTokenSource cancel = new();
        ProfileAdmissionToken? accepted = null;
        int commits = 0;
        f.Runtime.Catalog.BeforeWriteDispatchForTests = token => { accepted = token; cancel.Cancel(); };
        f.Runtime.Catalog.AfterWriteCommitForTests = _ => commits++;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Invoke(op, ct: cancel.Token));
        Assert.NotNull(accepted);
        Assert.Equal(0, commits);
        Assert.Empty(Active(f.Lease));
        Assert.Throws<InvalidOperationException>(() => f.Lease.RunStoreWrite(accepted!, () => true));
    }

    [Fact]
    public async Task RuntimeInitializeBorrowsExactOwnerAndValidatesEvenCachedNoop()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using (ThreadContentRuntime starting = new(directory.Path, lease))
        {
            await starting.InitializeAsync();
            Assert.Same(lease, starting.ProfileLease);
            Assert.Empty(Active(lease));
        }
        lease.Admission.CompleteStartup();
        using ProfileAdmissionToken owner = lease.Admission.EnterOrdinaryWriter();
        using CancellationTokenSource abandon = new();
        Task<ProfileAdmissionToken> pending = lease.Admission.EnterExclusiveAsync(abandon.Token).AsTask();
        try
        {
            await using ThreadContentRuntime runtime = new(directory.Path, lease);
            Assert.Throws<InvalidOperationException>(() => { _ = runtime.InitializeAsync(); });
            await runtime.InitializeAdmittedAsync(owner).WaitAsync(Deadline);
            Assert.Same(owner, Assert.Single(Active(lease)));
            await runtime.InitializeAdmittedAsync(owner); // cached, still borrowed
            using ProfileAdmissionToken unissued = ProfileAdmissionToken.Create(lease.Admission, ProfileAdmissionKind.Ordinary);
            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.InitializeAdmittedAsync(unissued));
            owner.Dispose();
            using ProfileAdmissionToken exclusive = await pending.WaitAsync(Deadline);
            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.InitializeAdmittedAsync(exclusive));
            Assert.Throws<InvalidOperationException>(() => { _ = runtime.InitializeAsync(); });
        }
        finally { abandon.Cancel(); owner.Dispose(); await ReleasePending(pending); }
    }

    [Fact]
    public async Task DirectToggleDoesNotBorrowLiveMonitorAndOwnerSurvivesAwaitedSqlTail()
    {
        await using Fixture f = await Fixture.OpenAsync();
        f.Lease.Admission.CompleteStartup();
        using ProfileAdmissionToken monitor = f.Lease.Admission.EnterOrdinaryWriter();
        TaskCompletionSource entered = Signal(), finish = Signal();
        f.Thread.OnCheck = async (_, _) => { entered.TrySetResult(); await finish.Task; };
        DownloadQueue queue = new(1, 1L << 30);
        using ManualResetEventSlim releaseCommit = new();
        TaskCompletionSource committed = Signal();
        ProfileAdmissionToken? direct = null;
        Task? toggle = null;
        Task<ProfileAdmissionToken>? pending = null;
        using CancellationTokenSource abandon = new();
        try
        {
            f.Thread.StartMonitoringAdmitted(monitor, queue, f.Directory, TimeSpan.FromHours(1));
            await entered.Task.WaitAsync(Deadline);
            f.Runtime.Catalog.AfterWriteCommitForTests = token =>
            {
                direct = token;
                committed.TrySetResult();
                if (!releaseCommit.Wait(Deadline)) throw new TimeoutException("Content commit-tail gate.");
            };
            toggle = f.Thread.SetCaptureEnabledAsync(true);
            await committed.Task.WaitAsync(Deadline);
            Assert.NotSame(monitor, direct);
            Assert.Equal(2, Active(f.Lease).Length);
            pending = f.Lease.Admission.EnterExclusiveAsync(abandon.Token).AsTask();
            monitor.Dispose();
            Assert.False(pending.IsCompleted);
            Assert.False(toggle.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => { _ = f.Thread.SetCaptureEnabledAsync(false); });
            releaseCommit.Set();
            await toggle.WaitAsync(Deadline);
            using ProfileAdmissionToken exclusive = await pending.WaitAsync(Deadline);
        }
        finally
        {
            releaseCommit.Set(); finish.TrySetResult();
            await f.Thread.StopMonitoringAsync().WaitAsync(Deadline);
            await queue.StopAllAsync().WaitAsync(Deadline);
            if (toggle != null) await Observe(toggle);
            abandon.Cancel(); monitor.Dispose();
            if (pending != null) await ReleasePending(pending);
        }
    }

    [Fact]
    public async Task ExactMonitorGenerationDrainsAndStaleCancelledCallbacksNeverReadmitOrUseSuccessor()
    {
        await using Fixture f = await Fixture.OpenAsync();
        // No downloads: selection still writes a real durable exclusion.
        await f.Runtime.Rules.SaveAsync([new MediaRule(MediaRuleScope.Global,
            types: new MediaTypeSelection(MediaTypeSelectionMode.Selected))]);
        f.Lease.Admission.CompleteStartup();
        using ProfileAdmissionToken first = f.Lease.Admission.EnterOrdinaryWriter();
        ProfileProducerEpoch epoch = new(first);
        ProfileProducerEpoch.Use use = epoch.Enter();
        TaskCompletionSource entered = Signal(), continueWrites = Signal(), wrote = Signal(), finish = Signal();
        CancellationToken oldToken = default;
        Exception? checkError = null;
        DownloadQueue queue = new(1, 1L << 30);
        using CancellationTokenSource abandon = new();
        Task<ProfileAdmissionToken>? pending = null;
        Task? stop = null;
        int commits = 0;
        f.Thread.OnCheck = async (q, ct) =>
        {
            oldToken = ct;
            entered.TrySetResult();
            await continueWrites.Task;
            try
            {
                await f.Thread.ObserveAsync(f.Observation("generation-one"), ct);
                await f.Thread.SourceAsync(ThreadSourceState.Archived, ct);
                await f.Thread.SelectAndEnqueueAsync(q, f.Candidate(), ct);
            }
            catch (Exception error) { checkError = error; }
            wrote.TrySetResult();
            await finish.Task; // provider tail deliberately ignores cancellation
        };
        try
        {
            f.Thread.StartMonitoringAdmitted(use.Admission!, queue, f.Directory, TimeSpan.FromHours(1));
            await entered.Task.WaitAsync(Deadline);
            pending = f.Lease.Admission.EnterExclusiveAsync(abandon.Token).AsTask();
            f.Runtime.Catalog.AfterWriteCommitForTests = token => { Assert.Same(first, token); commits++; };
            Assert.Throws<InvalidOperationException>(() => { _ = f.Thread.SetCaptureEnabledAsync(true); });
            continueWrites.TrySetResult();
            await wrote.Task.WaitAsync(Deadline);
            Assert.Null(checkError);
            Assert.True(commits >= 3); // title, source, selection; disabled capture stays disabled
            Assert.False(pending.IsCompleted);
            Task drain = epoch.Close();
            stop = f.Thread.StopMonitoringAsync();
            Assert.True(oldToken.IsCancellationRequested);
            Assert.False(stop.IsCompleted);
            Assert.False(drain.IsCompleted);
            Assert.False(pending.IsCompleted);
            finish.TrySetResult();
            await stop.WaitAsync(Deadline);
            use.Dispose();
            await drain.WaitAsync(Deadline);
            epoch.Dispose();
            using (ProfileAdmissionToken exclusive = await pending.WaitAsync(Deadline))
                Assert.Equal(ProfileAdmissionKind.Exclusive, exclusive.Kind);
            using ProfileAdmissionToken next = f.Lease.Admission.EnterOrdinaryWriter();
            TaskCompletionSource successorEntered = Signal();
            CancellationToken nextToken = default;
            f.Thread.OnCheck = (_, ct) => { nextToken = ct; successorEntered.TrySetResult(); return Task.CompletedTask; };
            f.Thread.StartMonitoringAdmitted(next, queue, f.Directory, TimeSpan.FromHours(1));
            await successorEntered.Task.WaitAsync(Deadline);
            f.Runtime.Catalog.BeforeWriteDispatchForTests = _ => throw new InvalidOperationException("Stale callback dispatched SQL.");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Thread.ObserveAsync(f.Observation("stale"), oldToken));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Thread.SourceAsync(ThreadSourceState.NotFound, oldToken));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Thread.SelectAndEnqueueAsync(queue, f.Candidate(), oldToken));
            Assert.Equal("generation-one", f.Thread.Title);
            Assert.Same(next, Assert.Single(Active(f.Lease)));
            // A current callback still maps to the successor's token, not the stale map.
            f.Runtime.Catalog.BeforeWriteDispatchForTests = token => Assert.Same(next, token);
            await f.Thread.ObserveAsync(f.Observation("successor"), nextToken);
            Assert.Equal("successor", f.Thread.Title);
            await f.Thread.StopMonitoringAsync().WaitAsync(Deadline);
        }
        finally
        {
            continueWrites.TrySetResult(); finish.TrySetResult();
            await f.Thread.StopMonitoringAsync().WaitAsync(Deadline);
            await queue.StopAllAsync().WaitAsync(Deadline);
            use.Dispose(); await epoch.Close().WaitAsync(Deadline); epoch.Dispose();
            abandon.Cancel();
            if (pending != null) await ReleasePending(pending);
        }
    }

    [Fact]
    public async Task ExplicitChildScopeBorrowsOnlyKnownGenerationAndRejectsRetiredWrongOrCancelledInvocations()
    {
        await using Fixture f = await Fixture.OpenAsync();
        await f.Runtime.Rules.SaveAsync([new MediaRule(MediaRuleScope.Global,
            types: new MediaTypeSelection(MediaTypeSelectionMode.Selected))]);
        f.Lease.Admission.CompleteStartup();
        using ProfileAdmissionToken owner = f.Lease.Admission.EnterOrdinaryWriter();
        DownloadQueue queue = new(1, 1L << 30);
        TaskCompletionSource entered = Signal(), finish = Signal();
        CancellationToken original = default;
        f.Thread.OnCheck = async (_, ct) => { original = ct; entered.TrySetResult(); await finish.Task; };
        using CancellationTokenSource sourceDisable = new();
        using CancellationTokenSource abandon = new();
        Task<ProfileAdmissionToken>? pending = null;
        CancellationTokenSource? lifetime = null;
        IDisposable? scope = null;
        try
        {
            f.Thread.StartMonitoringAdmitted(owner, queue, f.Directory, TimeSpan.FromHours(1));
            await entered.Task.WaitAsync(Deadline);
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(original, sourceDisable.Token);
            CancellationToken child = lifetime.Token;
            scope = f.Thread.BindChild(original, child);
            int dispatches = 0;
            f.Runtime.Catalog.BeforeWriteDispatchForTests = token => { Assert.Same(owner, token); dispatches++; };
            pending = f.Lease.Admission.EnterExclusiveAsync(abandon.Token).AsTask();
            Assert.False(pending.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                f.Thread.ObserveChildAsync(scope, f.Observation("wrong child"), default));
            AdmissionThread wrongThread = new(f.Thread.Board);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                wrongThread.ObserveChildAsync(scope, f.Observation("wrong thread"), child));
            Assert.Equal(0, dispatches);
            await f.Thread.ObserveChildAsync(scope, f.Observation("linked generation"), child);
            await f.Thread.SourceChildAsync(scope, ThreadSourceState.Archived, child);
            await f.Thread.SelectChildAsync(scope, queue, f.Candidate(), child);
            Assert.True(dispatches >= 3);
            Assert.Null(f.Context.Snapshot.CaptureFailure);
            Assert.Null(f.Context.Snapshot.SelectionFailure);
            Assert.Same(owner, Assert.Single(Active(f.Lease)));
            Assert.False(pending.IsCompleted);
            int savedDispatches = dispatches;
            // Source disable is independent of the original monitor's cancellation.
            sourceDisable.Cancel();
            Assert.False(original.IsCancellationRequested);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                f.Thread.ObserveChildAsync(scope, f.Observation("disabled"), child));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                f.Thread.SourceChildAsync(scope, ThreadSourceState.NotFound, child));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                f.Thread.SelectChildAsync(scope, queue, f.Candidate(), child));
            Assert.Equal(savedDispatches, dispatches);
            scope.Dispose(); lifetime.Dispose();
            // A normally completed linked scope need not have a cancelled child.
            // Explicit retirement must reject it even before original monitor stop.
            using CancellationTokenSource completedLifetime = CancellationTokenSource.CreateLinkedTokenSource(original);
            CancellationToken completedChild = completedLifetime.Token;
            using IDisposable retired = f.Thread.BindChild(original, completedChild);
            retired.Dispose(); completedLifetime.Dispose();
            Assert.False(completedChild.IsCancellationRequested);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                f.Thread.ObserveChildAsync(retired, f.Observation("retired"), completedChild));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                f.Thread.SourceChildAsync(retired, ThreadSourceState.NotFound, completedChild));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                f.Thread.SelectChildAsync(retired, queue, f.Candidate(), completedChild));
            Assert.Equal(savedDispatches, dispatches);
            finish.TrySetResult();
            await f.Thread.StopMonitoringAsync().WaitAsync(Deadline);
            Assert.ThrowsAny<OperationCanceledException>(() => f.Thread.BindChild(original, completedChild));
            owner.Dispose();
            using (ProfileAdmissionToken exclusive = await pending.WaitAsync(Deadline)) { }
            using ProfileAdmissionToken successor = f.Lease.Admission.EnterOrdinaryWriter();
            TaskCompletionSource nextEntered = Signal();
            f.Thread.OnCheck = (_, _) => { nextEntered.TrySetResult(); return Task.CompletedTask; };
            f.Thread.StartMonitoringAdmitted(successor, queue, f.Directory, TimeSpan.FromHours(1));
            await nextEntered.Task.WaitAsync(Deadline);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                f.Thread.ObserveChildAsync(retired, f.Observation("successor theft"), completedChild));
            Assert.Equal("linked generation", f.Thread.Title);
            Assert.Same(successor, Assert.Single(Active(f.Lease)));
            Assert.Equal(savedDispatches, dispatches);
            await f.Thread.StopMonitoringAsync().WaitAsync(Deadline);
        }
        finally
        {
            scope?.Dispose(); lifetime?.Dispose(); finish.TrySetResult();
            await f.Thread.StopMonitoringAsync().WaitAsync(Deadline);
            await queue.StopAllAsync().WaitAsync(Deadline);
            abandon.Cancel(); owner.Dispose();
            if (pending != null) await ReleasePending(pending);
        }
    }

    [Fact]
    public async Task ExplicitChildScopeDoesNotInventMonitorAuthorityForStandaloneChecks()
    {
        await using Fixture f = await Fixture.OpenAsync();
        f.Lease.Admission.CompleteStartup();
        using CancellationTokenSource child = new();
        using IDisposable scope = f.Thread.BindChild(default, child.Token);
        Assert.Empty(Active(f.Lease));
        int commits = 0;
        f.Runtime.Catalog.AfterWriteCommitForTests = token =>
        {
            Assert.Equal(ProfileAdmissionKind.Ordinary, token.Kind);
            Assert.Same(token, Assert.Single(Active(f.Lease)));
            commits++;
        };
        await f.Thread.ObserveChildAsync(scope, f.Observation("direct linked check"), child.Token);
        Assert.Equal(1, commits);
        Assert.Empty(Active(f.Lease));
        using ProfileAdmissionToken exclusive = await f.Lease.Admission.EnterExclusiveAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            f.Thread.ObserveChildAsync(scope, f.Observation("no borrowed authority"), child.Token));
        Assert.Equal("direct linked check", f.Thread.Title);
        Assert.Equal(1, commits);
    }

    [Theory]
    [InlineData("callback")]
    [InlineData("missing")]
    [InlineData("bypass-excluded")]
    [InlineData("bypass-commit")]
    public async Task FilterRecordUsesExecutionJobAfterMonitorOwnerEndedNotDescriptorOrNewOwner(string mode)
    {
        await using Fixture f = await Fixture.OpenAsync();
        await using Fixture other = await Fixture.OpenAsync();
        await f.Runtime.Rules.SaveAsync([new MediaRule(MediaRuleScope.Global) {
            ImageFilter = new(MediaImageFilterMode.Plugin, "synthetic", "filter") }]);
        RecordingFactory factory = new();
        if (mode == "callback") f.Runtime.OriginalFilters = factory;
        f.Lease.Admission.CompleteStartup();
        other.Lease.Admission.CompleteStartup();
        using ProfileAdmissionToken monitor = f.Lease.Admission.EnterOrdinaryWriter();
        DownloadItem descriptor = Assert.Single((await f.Context.SelectAdmittedAsync(monitor, f.Thread, [f.Candidate()], default))!);
        Assert.Same(f.Lease, descriptor.ContentProfileLease);
        Assert.Throws<InvalidOperationException>(() => descriptor.RequireProfileWriteAdmission(f.Lease));
        // Restored/retried descriptors also retain the actual runtime lease, not a path guess.
        descriptor = f.Thread.BindRestoredMediaFilter(descriptor);
        Assert.Same(f.Lease, descriptor.ContentProfileLease);
        if (mode.StartsWith("bypass", StringComparison.Ordinal))
        {
            await f.Runtime.Rules.SaveAsync([new MediaRule(MediaRuleScope.Global,
                types: mode == "bypass-excluded" ? new(MediaTypeSelectionMode.Selected) : new MediaTypeSelection(MediaTypeSelectionMode.All)) {
                ImageFilter = MediaImageFilterRule.Disabled }]);
            descriptor = f.Thread.BindRestoredMediaFilter(descriptor, explicitRetry: true);
        }
        using ProfileAdmissionToken job = f.Lease.Admission.EnterOrdinaryWriter();
        DownloadItem execution = descriptor.WithProfileWriteAdmission(job);
        Assert.NotSame(descriptor, execution);
        monitor.Dispose(); // the queued job intentionally outlives its monitor
        using CancellationTokenSource abandon = new();
        Task<ProfileAdmissionToken> pending = f.Lease.Admission.EnterExclusiveAsync(abandon.Token).AsTask();
        int commits = 0;
        f.Runtime.Catalog.BeforeWriteDispatchForTests = token => Assert.Same(job, token);
        f.Runtime.Catalog.AfterWriteCommitForTests = token => { Assert.Same(job, token); commits++; };
        async Task Execute(DownloadItem item)
        {
            if (mode == "callback")
                await factory.Record!(item, false, "synthetic_omission", default);
            else if (mode == "bypass-commit")
            {
                await using IOriginalMediaFilterLease filter = await descriptor.Filter!.AcquireAsync(item, default);
                using MemoryStream bytes = new([1, 2, 3, 4]);
                await filter.CommitAsync(item, bytes, new MediaStore(), (_, _) => Task.CompletedTask, default);
            }
            else
                await descriptor.Filter!.AcquireAsync(item, default);
        }
        try
        {
            Assert.False(pending.IsCompleted);
            if (mode != "bypass-commit")
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => Execute(descriptor));
                using ProfileAdmissionToken foreign = other.Lease.Admission.EnterOrdinaryWriter();
                await Assert.ThrowsAsync<InvalidOperationException>(() => Execute(descriptor.WithProfileWriteAdmission(foreign)));
                using ProfileAdmissionToken unissued = ProfileAdmissionToken.Create(f.Lease.Admission, ProfileAdmissionKind.Ordinary);
                await Assert.ThrowsAsync<InvalidOperationException>(() => Execute(descriptor.WithProfileWriteAdmission(unissued)));
                Assert.Equal(0, commits);
            }
            if (mode is "missing" or "bypass-excluded")
                await Assert.ThrowsAsync<MediaFilterOmissionException>(() => Execute(execution));
            else await Execute(execution).WaitAsync(Deadline);
            Assert.Equal(1, commits);
            Assert.Same(job, Assert.Single(Active(f.Lease)));
            Assert.False(pending.IsCompleted);
            CandidateDecision saved = Assert.Single(await f.Runtime.Catalog.GetDecisionsAsync(Id));
            Assert.Equal(mode == "bypass-commit" ? MediaSelectionOutcome.Selected
                : mode == "bypass-excluded" ? MediaSelectionOutcome.Excluded : MediaSelectionOutcome.TechnicalOmission,
                saved.Selection.Outcome);
            job.Dispose();
            using ProfileAdmissionToken exclusive = await pending.WaitAsync(Deadline);
            if (mode is "callback" or "missing")
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => Execute(execution)); // released job, no successor fallback
                await Assert.ThrowsAsync<InvalidOperationException>(() => Execute(descriptor.WithProfileWriteAdmission(exclusive)));
                Assert.Equal(1, commits);
            }
        }
        finally { abandon.Cancel(); job.Dispose(); monitor.Dispose(); await ReleasePending(pending); }
    }

    private sealed class RecordingFactory : IOriginalMediaFilterFactory
    {
        internal Func<DownloadItem, bool?, string, CancellationToken, Task>? Record { get; private set; }
        public IOriginalMediaFilter Create(DownloadItem item, CandidateDecision decision, Func<bool> isCurrent,
            Func<DownloadItem, bool?, string, CancellationToken, Task> recordOmission)
        {
            Record = recordOmission;
            return new RecordingFilter();
        }
        private sealed class RecordingFilter : IOriginalMediaFilter
        {
            public bool CanStart => true;
            public event Action? CapacityChanged { add { } remove { } }
            public Task<IOriginalMediaFilterLease> AcquireAsync(DownloadItem item, CancellationToken cancellationToken)
                => throw new InvalidOperationException("This fixture invokes the captured job callback directly.");
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ProfileAdmissionToken[] Active(ProfileLease lease)
    {
        object sync = Field(lease.Admission, "_sync")!;
        lock (sync) return ((IEnumerable<ProfileAdmissionToken>)Field(lease.Admission, "_active")!).ToArray();
    }
    private static object? Field(object owner, string name) => owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner);
    private static async Task Observe(Task task)
    {
        try { await task.WaitAsync(Deadline); }
        catch when (task.IsCompleted) { /* Outcome belongs to the assertion, but observe teardown. */ }
    }
    private static async Task ReleasePending(Task<ProfileAdmissionToken> task)
    {
        try { (await task.WaitAsync(Deadline)).Dispose(); }
        catch (OperationCanceledException) { }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TempDirectory _directory = new();
        private readonly StubImageBoard _source = new(new DelegateHandler((_, _) =>
            throw new InvalidOperationException("Admission fixture must never send network requests.")));
        internal string Directory => _directory.Path;
        internal ProfileLease Lease { get; }
        internal ThreadContentRuntime Runtime { get; }
        internal AdmissionThread Thread { get; }
        internal ThreadContentContext Context => (ThreadContentContext)typeof(BaseThread)
            .GetField("_content", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Thread)!;
        private Fixture()
        {
            Lease = ProfileLease.Acquire(Directory);
            Runtime = new(Directory, Lease);
            Thread = new(_source.Board);
        }
        internal static async Task<Fixture> OpenAsync()
        {
            Fixture f = new();
            try
            {
                await f.Runtime.InitializeAsync();
                await f.Runtime.BindAsync(f.Thread, f.Directory, restored: true);
                await f.Context.CaptureAsync(f.Observation("buffered"), default);
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        internal CapturedThreadSnapshot Observation(string title) => new(Id, Thread.Url, title,
            ThreadSourceState.Active, DateTimeOffset.UtcNow, Directory,
            [new CapturedPost("123", 0, PostFieldPresence.Html, Html: "synthetic post")]);
        internal DownloadItem Candidate() => new(new Uri("https://stub.test/media/777.png"),
            Path.Combine(Directory, "media", "777.png"), _source, Thread, "777", originalFileName: "777.png");
        internal Task PrepareAsync(string op) => op == "capture"
            ? Context.SetCaptureEnabledAsync(true, default) : Task.CompletedTask;
        internal Task Invoke(string op, ProfileAdmissionToken? admission = null, CancellationToken ct = default)
        {
            bool borrowed = admission != null;
            return op switch
            {
                "capture" => borrowed ? Context.CaptureAdmittedAsync(admission!, Observation("capture"), ct)
                    : Context.CaptureAsync(Observation("capture"), ct),
                "title" => borrowed ? Context.CaptureAdmittedAsync(admission!, Observation("title"), ct)
                    : Thread.ObserveAsync(Observation("title"), ct),
                "source" => borrowed ? Context.RecordSourceStateAdmittedAsync(admission!, ThreadSourceState.Archived, ct)
                    : Context.RecordSourceStateAsync(ThreadSourceState.Archived, ct),
                "toggle" => borrowed ? Context.SetCaptureEnabledAdmittedAsync(admission!, true, ct)
                    : Context.SetCaptureEnabledAsync(true, ct),
                "base-toggle" => borrowed ? Thread.SetCaptureEnabledAdmittedAsync(admission!, true, ct)
                    : Thread.SetCaptureEnabledAsync(true, ct),
                "select" => borrowed ? Context.SelectAdmittedAsync(admission!, Thread, [Candidate()], ct)
                    : Context.SelectAsync(Thread, [Candidate()], ct),
                "bind" => borrowed ? Runtime.BindAdmittedAsync(admission!, new AdmissionThread(_source.Board), Directory, true, ct)
                    : Runtime.BindAsync(new AdmissionThread(_source.Board), Directory, true, ct),
                _ => throw new ArgumentOutOfRangeException(nameof(op))
            };
        }
        // Async trampoline also captures synchronous rejection; null must mean an
        // invalid borrowed token, never selection of the compatibility overload.
        internal async Task InvokeBorrowed(string op, ProfileAdmissionToken admission)
        {
            if (admission != null) { await Invoke(op, admission); return; }
            switch (op)
            {
                case "capture": case "title": await Context.CaptureAdmittedAsync(admission!, Observation("invalid"), default); break;
                case "source": await Context.RecordSourceStateAdmittedAsync(admission!, ThreadSourceState.Archived, default); break;
                case "toggle": await Context.SetCaptureEnabledAdmittedAsync(admission!, true, default); break;
                case "base-toggle": await Thread.SetCaptureEnabledAdmittedAsync(admission!, true); break;
                case "select": await Context.SelectAdmittedAsync(admission!, Thread, [Candidate()], default); break;
                case "bind": await Runtime.BindAdmittedAsync(admission!, new AdmissionThread(_source.Board), Directory, true); break;
            }
        }
        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync(); Lease.Dispose(); _source.Dispose(); _directory.Dispose();
        }
    }

    private sealed class AdmissionThread(IBoard board) : BaseThread
    {
        public override IBoard Board => board;
        public override string ThreadId => "123";
        public override string NiceName => Title;
        public override Uri ThreadUri => new("https://stub.test/g/thread/123");
        public override bool SupportsContent => true;
        internal Func<DownloadQueue, CancellationToken, Task>? OnCheck { get; set; }
        public override Task checkThreadAsync(DownloadQueue queue, CancellationToken cancellationToken = default)
            => OnCheck?.Invoke(queue, cancellationToken) ?? Task.CompletedTask;
        internal IDisposable BindChild(CancellationToken original, CancellationToken child)
            => BindContentCancellationScope(original, child);
        internal async Task ObserveChildAsync(IDisposable scope, CapturedThreadSnapshot snapshot, CancellationToken ct)
            => await CaptureContentAsync(snapshot, ct, (ContentCancellationScope)scope);
        internal async Task SourceChildAsync(IDisposable scope, ThreadSourceState source, CancellationToken ct)
            => await CaptureSourceStateAsync(source, ct, (ContentCancellationScope)scope);
        internal async Task SelectChildAsync(IDisposable scope, DownloadQueue queue, DownloadItem item, CancellationToken ct)
            => await EnqueueKnownMediaAsync(queue, new Dictionary<string, DownloadItem> { [item.MediaIdentifier] = item },
                ct, (ContentCancellationScope)scope);
        internal Task ObserveAsync(CapturedThreadSnapshot? snapshot, CancellationToken ct = default) => CaptureContentAsync(snapshot, ct);
        internal Task SourceAsync(ThreadSourceState source, CancellationToken ct) => CaptureSourceStateAsync(source, ct);
        internal Task SelectAndEnqueueAsync(DownloadQueue queue, DownloadItem item, CancellationToken ct)
            => EnqueueKnownMediaAsync(queue, new Dictionary<string, DownloadItem> { [item.MediaIdentifier] = item }, ct);
    }
}

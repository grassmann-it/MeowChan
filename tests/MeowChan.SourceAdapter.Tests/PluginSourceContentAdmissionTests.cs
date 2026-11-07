using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using MeowChan.Archive;
using MeowChan.Boards.Base;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Sources;
using MeowChan.PluginSdk;
using MeowChan.Rules;

namespace MeowChan.SourceAdapter.Tests;

// Real Registry -> PluginSourceThread -> linked source lifetime -> Catalog.
// Only the worker result is synthetic. No copied admission or provider implementation.
// Reflection observes/calls existing internal seams; no new friend-assembly grant.
public sealed class PluginSourceContentAdmissionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static readonly ResourceId Id = new("4chan", "g", "123");
    private const string Url = "https://boards.4chan.org/g/thread/123";
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task AcceptedPluginResultCommitsWithOriginalOwnerBehindExclusiveThenRegistryDrains(
        bool captureEnabled, bool notFound)
    {
        ReadGate response = new(notFound ? "not-found" : "accepted", notFound);
        await using Fixture f = await Fixture.OpenAsync(captureEnabled, response);
        Registry registry = f.NewRegistry();
        BaseThread thread = await AddAsync(registry);
        await response.Entered.Task.WaitAsync(Deadline);
        object accepted = Assert.Single(Active(f.Admission));
        Assert.Equal("Ordinary", Property(accepted, "Kind")!.ToString());
        Assert.NotEqual(MonitorToken(thread), response.Token); // real linked worker CT, not monitor CT
        Assert.False(response.Token.IsCancellationRequested);
        Assert.True(response.Operation!.Generation > 0);

        using CancellationTokenSource abandon = new();
        Task pending = ReserveExclusive(f.Admission, abandon.Token);
        ConcurrentQueue<object> dispatches = new(), commits = new();
        TaskCompletionSource committed = Signal();
        using ManualResetEventSlim releaseCommit = new();
        SetHook(f.Content.Catalog, "BeforeWriteDispatchForTests", token => dispatches.Enqueue(token));
        SetHook(f.Content.Catalog, "AfterWriteCommitForTests", token =>
        {
            commits.Enqueue(token);
            if (commits.Count == 1)
            {
                committed.TrySetResult();
                if (!releaseCommit.Wait(Deadline)) throw new TimeoutException("Plugin commit tail was not released.");
            }
        });
        using Completion settled = new(thread);
        try
        {
            Assert.False(pending.IsCompleted);
            // A separate compatible call cannot sneak into the accepted generation.
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await thread.SetCaptureEnabledAsync(!captureEnabled));
            Assert.Empty(dispatches);
            response.Release.TrySetResult();
            await committed.Task.WaitAsync(Deadline); // pre-fix: re-admission fails, this watchdog expires
            Assert.False(pending.IsCompleted);
            Assert.Same(accepted, Assert.Single(Active(f.Admission)));
            Assert.All(commits, token => Assert.Same(accepted, token));
            releaseCommit.Set();
            await settled.Task.WaitAsync(Deadline);
            await registry.StopAsync().WaitAsync(Deadline); // real counted monitor/queue/epoch drain
            Assert.Null(thread.LastCheckFailure);
            Assert.NotEmpty(commits);
            Assert.Equal(dispatches.Count, commits.Count);
            Assert.All(dispatches, token => Assert.Same(accepted, token));
            Assert.All(commits, token => Assert.Same(accepted, token));
            if (!notFound) Assert.True(commits.Count >= 2); // content/title and real durable media selection
            using IDisposable exclusive = await ExclusiveResult(pending);
            Assert.Equal("Exclusive", Property(exclusive, "Kind")!.ToString());
            Assert.Same(exclusive, Assert.Single(Active(f.Admission)));
            Assert.DoesNotContain(accepted, Active(f.Admission));
            ArchivedThreadView stored = await f.ReadAsync();
            Assert.Equal(captureEnabled, stored.Summary.CaptureEnabled);
            if (notFound)
            {
                Assert.Equal(ThreadSourceState.NotFound, stored.Summary.SourceState);
                Assert.Equal(0, stored.PostCount);
            }
            else
            {
                Assert.Equal("accepted", stored.Summary.Title);
                Assert.Equal(captureEnabled ? 1 : 0, stored.PostCount);
                Assert.Equal(captureEnabled, stored.Summary.LastCapturedAt.HasValue);
                if (captureEnabled) Assert.Equal("accepted body", Assert.Single(stored.GetPosts()).Html);
                var decision = Assert.Single(await f.Content.Catalog.GetDecisionsAsync(Id));
                Assert.Equal(MediaSelectionOutcome.Excluded, decision.Selection.Outcome);
            }
            Assert.Equal(1, f.Reads);
            Assert.Equal(0, f.HttpRequests);
        }
        finally
        {
            releaseCommit.Set(); response.Release.TrySetResult(); abandon.Cancel();
            await registry.StopAsync().WaitAsync(Deadline);
            await ReleaseReservation(pending);
            SetHook(f.Content.Catalog, "BeforeWriteDispatchForTests", null);
            SetHook(f.Content.Catalog, "AfterWriteCommitForTests", null);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledOrDisabledLatePluginResultCannotCommitOrAttachSuccessor(bool disableSource)
    {
        ReadGate old = new("stale"), next = new("successor");
        await using Fixture f = await Fixture.OpenAsync(true, old, next);
        Registry first = f.NewRegistry();
        BaseThread oldThread = await AddAsync(first);
        await old.Entered.Task.WaitAsync(Deadline);
        object firstOwner = Assert.Single(Active(f.Admission));
        ConcurrentQueue<object> commits = new();
        SetHook(f.Content.Catalog, "AfterWriteCommitForTests", token => commits.Enqueue(token));
        using CancellationTokenSource abandon = new();
        Task pending = ReserveExclusive(f.Admission, abandon.Token);
        Task? stopping = null;
        try
        {
            Assert.False(pending.IsCompleted);
            if (disableSource)
            {
                f.Source.SetEnabled(false);
                Assert.True(old.Token.IsCancellationRequested);
                f.Source.SetEnabled(true); // replacement lifetime must not rehabilitate the old result
            }
            stopping = first.StopAsync();
            await old.Cancelled.Task.WaitAsync(Deadline);
            Assert.True(old.Token.IsCancellationRequested);
            Assert.False(stopping.IsCompleted); // worker deliberately ignores cancellation until Release
            Assert.False(pending.IsCompleted);
            Assert.Same(firstOwner, Assert.Single(Active(f.Admission)));
            old.Release.TrySetResult();
            await stopping.WaitAsync(Deadline);
            using (IDisposable exclusive = await ExclusiveResult(pending))
                Assert.Same(exclusive, Assert.Single(Active(f.Admission)));
            Assert.Empty(commits);
            ArchivedThreadView before = await f.ReadAsync();
            Assert.Equal(0, before.PostCount);
            Assert.NotEqual("stale", before.Summary.Title);

            // New real Registry + monitor generation over the SAME runtime/profile/source.
            // Preparation writes happen before the second worker gate, not under the assertion hook.
            SetHook(f.Content.Catalog, "AfterWriteCommitForTests", null);
            Registry successor = f.NewRegistry();
            BaseThread nextThread = await AddAsync(successor);
            await next.Entered.Task.WaitAsync(Deadline);
            object nextOwner = Assert.Single(Active(f.Admission));
            Assert.NotSame(firstOwner, nextOwner);
            Assert.False(next.Token.IsCancellationRequested);
            Assert.NotEqual(old.Token, next.Token);
            SetHook(f.Content.Catalog, "AfterWriteCommitForTests", token => commits.Enqueue(token));
            Task nextPending = ReserveExclusive(f.Admission, abandon.Token);
            using Completion settled = new(nextThread);
            try
            {
                // Retained old callback token is rejected at the real PluginSourceThread read gate.
                // It cannot acquire a worker, a fresh admission, or the successor's authority.
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    oldThread.checkThreadAsync(f.Queues[0], old.Token).WaitAsync(Deadline));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    nextThread.checkThreadAsync(f.Queues[1], old.Token).WaitAsync(Deadline));
                Assert.Equal(2, f.Reads);
                Assert.Empty(commits);
                Assert.False(nextPending.IsCompleted);
                Assert.Same(nextOwner, Assert.Single(Active(f.Admission)));
                next.Release.TrySetResult();
                await settled.Task.WaitAsync(Deadline);
                await successor.StopAsync().WaitAsync(Deadline);
                Assert.Null(nextThread.LastCheckFailure);
                Assert.NotEmpty(commits);
                Assert.All(commits, token => Assert.Same(nextOwner, token));
                using IDisposable exclusive = await ExclusiveResult(nextPending);
                ArchivedThreadView stored = await f.ReadAsync();
                Assert.Equal("successor", stored.Summary.Title);
                Assert.Equal("successor body", Assert.Single(stored.GetPosts()).Html);
                Assert.Equal(0, f.HttpRequests);
            }
            finally
            {
                next.Release.TrySetResult(); abandon.Cancel();
                await successor.StopAsync().WaitAsync(Deadline);
                await ReleaseReservation(nextPending);
            }
        }
        finally
        {
            old.Release.TrySetResult(); next.Release.TrySetResult(); abandon.Cancel();
            if (stopping != null) await stopping.WaitAsync(Deadline);
            await first.StopAsync().WaitAsync(Deadline);
            await ReleaseReservation(pending);
            SetHook(f.Content.Catalog, "AfterWriteCommitForTests", null);
        }
    }

    private static async Task<BaseThread> AddAsync(Registry registry)
    {
        AdmissionResult added = await registry.AddAsync(Url).WaitAsync(Deadline);
        Assert.Equal(AdmissionStatus.Added, added.Status);
        BaseThread thread = Assert.IsAssignableFrom<BaseThread>(added.Thread);
        Assert.Equal("MeowChan.PluginHost.Sources.PluginSourceThread", thread.GetType().FullName);
        return thread;
    }

    private sealed class ReadGate(string title, bool notFound = false)
    {
        internal TaskCompletionSource Entered { get; } = Signal();
        internal TaskCompletionSource Release { get; } = Signal();
        internal TaskCompletionSource Cancelled { get; } = Signal();
        internal CancellationToken Token { get; private set; }
        internal PluginOperationContext? Operation { get; private set; }
        internal async Task<ThreadReadResult> ReadAsync(ThreadReadRequest request, PluginOperationContext operation, CancellationToken token)
        {
            Token = token;
            Operation = operation;
            using CancellationTokenRegistration registration = token.Register(() => Cancelled.TrySetResult());
            Entered.TrySetResult();
            await Release.Task.WaitAsync(Deadline); // adversarial result ignores cancellation, bounded watchdog
            if (notFound) return new ThreadNotFound(request.Thread);
            return new MeowChan.PluginSdk.ThreadSnapshot(new SourceThreadSnapshot(request.Thread,
                new Uri(Url), title, SourceThreadState.Active,
                [new SourcePost("123", 0, SourceFieldPresence.Text | SourceFieldPresence.Attachments,
                    Text: title + " body", TextFormat: SourceTextFormat.PlainText,
                    Attachments: [new SourceAttachment(0, "777", Url: new Uri("https://i.4cdn.org/g/777.png"),
                        OriginalFileName: "777.png", Extension: ".png")])]));
        }
    }

    // Event-driven completion, not timing/polling. On failure also complete so assertions
    // report LastCheckFailure rather than hanging until a later periodic check.
    private sealed class Completion : IDisposable
    {
        private readonly BaseThread _thread;
        private readonly TaskCompletionSource _done = Signal();
        internal Task Task => _done.Task;
        internal Completion(BaseThread thread) { _thread = thread; thread.PropertyChanged += Changed; }
        private void Changed(object? sender, PropertyChangedEventArgs args)
        {
            if (_thread.Activity is ThreadActivity.Finished or ThreadActivity.NotFound or ThreadActivity.Error
                || _thread.LastCheckFailure != null) _done.TrySetResult();
        }
        public void Dispose() => _thread.PropertyChanged -= Changed;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SourceAdapterTests.TemporaryProfile _profile = new();
        private readonly DenyHttp _deny = new();
        private readonly HttpClient _http;
        private readonly ReadGate[] _responses;
        private readonly List<Registry> _registries = new();
        private int _reads;
        internal int Reads => Volatile.Read(ref _reads);
        internal int HttpRequests => Volatile.Read(ref _deny.Requests);
        internal ProfileLease Lease { get; }
        internal object Admission => Property(Lease, "Admission")!;
        internal ThreadContentRuntime Content { get; }
        internal PluginSourceImageBoard Source { get; }
        internal List<DownloadQueue> Queues { get; } = new();
        private Fixture(ReadGate[] responses)
        {
            _responses = responses;
            Lease = ProfileLease.Acquire(_profile.Path);
            Content = new(_profile.Path, Lease);
            _http = new HttpClient(_deny);
            SourceAdapterTests.TestWorker worker = new((request, operation, token) =>
            {
                int index = Interlocked.Increment(ref _reads) - 1;
                if (index >= responses.Length) throw new InvalidOperationException("Unexpected worker recheck.");
                return responses[index].ReadAsync(request, operation, token);
            });
            Source = new(worker.Description, "4chan", _ => Task.FromResult<IPluginWorkerClient>(worker), _http);
        }
        internal static async Task<Fixture> OpenAsync(bool captureEnabled, params ReadGate[] responses)
        {
            Fixture f = new(responses);
            try
            {
                await f.Content.InitializeAsync().WaitAsync(Deadline);
                await f.Content.Rules.SaveAsync([new MediaRule(MediaRuleScope.Global,
                    types: new MediaTypeSelection(MediaTypeSelectionMode.Selected))]).WaitAsync(Deadline);
                if (!captureEnabled)
                    await f.Content.Catalog.RegisterThreadAsync(new ArchiveThreadRegistration(Id, Url, "",
                        f._profile.Path, false)).WaitAsync(Deadline);
                Call(f.Admission, "CompleteStartup");
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        internal Registry NewRegistry()
        {
            DownloadQueue queue = new(1, 1048576);
            Queues.Add(queue);
            // Deliberately runtime-only: exercises BoundProfileLease's real runtime fallback.
            Registry registry = new([Source], queue, _profile.Path, TimeSpan.FromHours(1), 3600,
                contentRuntime: Content);
            _registries.Add(registry);
            return registry;
        }
        internal async Task<ArchivedThreadView> ReadAsync()
        {
            await using ArchiveReader reader = await Content.OpenReaderAsync().WaitAsync(Deadline);
            return Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Id).WaitAsync(Deadline));
        }
        public async ValueTask DisposeAsync()
        {
            foreach (ReadGate gate in _responses) gate.Release.TrySetResult();
            foreach (Registry registry in _registries) await registry.DisposeAsync().AsTask().WaitAsync(Deadline);
            foreach (DownloadQueue queue in Queues) await queue.StopAllAsync().WaitAsync(Deadline);
            Source.Dispose(); _http.Dispose();
            await Content.DisposeAsync(); Lease.Dispose(); _profile.Dispose();
        }
    }

    private sealed class DenyHttp : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Requests);
            throw new InvalidOperationException("Plugin admission regression must never send HTTP.");
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static object? Property(object owner, string name) => owner.GetType().GetProperty(name, Hidden)!.GetValue(owner);
    private static object Field(object owner, string name) => owner.GetType().GetField(name, Hidden)!.GetValue(owner)!;
    private static object? Call(object owner, string name, params object?[] arguments)
    {
        try { return owner.GetType().GetMethod(name, Hidden)!.Invoke(owner, arguments); }
        catch (TargetInvocationException error) when (error.InnerException != null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static object[] Active(object admission)
    {
        lock (Field(admission, "_sync")) return ((IEnumerable)Field(admission, "_active")).Cast<object>().ToArray();
    }
    private static CancellationToken MonitorToken(BaseThread thread) =>
        ((CancellationTokenSource)typeof(BaseThread).GetField("_monitoringCancellation", Hidden)!.GetValue(thread)!).Token;
    private static Task ReserveExclusive(object admission, CancellationToken token)
        => (Task)Call(Call(admission, "EnterExclusiveAsync", token)!, "AsTask")!;
    private static async Task<IDisposable> ExclusiveResult(Task task)
    {
        await task.WaitAsync(Deadline);
        return Assert.IsAssignableFrom<IDisposable>(Property(task, "Result"));
    }
    private static async Task ReleaseReservation(Task task)
    {
        try { (await ExclusiveResult(task)).Dispose(); }
        catch (OperationCanceledException) { }
    }
    private static void SetHook(ArchiveCatalog catalog, string name, Action<object>? callback)
    {
        PropertyInfo property = typeof(ArchiveCatalog).GetProperty(name, Hidden)!;
        if (callback == null) { property.SetValue(catalog, null); return; }
        ParameterExpression token = Expression.Parameter(property.PropertyType.GenericTypeArguments[0], "token");
        Delegate hook = Expression.Lambda(property.PropertyType,
            Expression.Invoke(Expression.Constant(callback), Expression.Convert(token, typeof(object))), token).Compile();
        property.SetValue(catalog, hook);
    }
}

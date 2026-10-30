using System.Net;
using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Sources;
using MeowChan.PluginSdk;
using MeowChan.OfficialSources.FourChan;

namespace MeowChan.SourceAdapter.Tests;

public sealed class SourceAdapterTests
{
    [Theory]
    [InlineData("https://boards.4chan.org/g/thread/123/title#p124", "https://boards.4chan.org/g/thread/123")]
    [InlineData("http://boards.4channel.org/g/thread/123/?test=1", "https://boards.4chan.org/g/thread/123")]
    [InlineData("https://boards.4chan.org/g/", "https://boards.4chan.org/g/")]
    public void RoutingAndConstructionNeverAcquireWorker(string input, string expected)
    {
        int calls = 0;
        using PluginSourceImageBoard source = new(new FourChanPlugin().Describe(), "4chan", _ => { calls++; throw new Exception("No IPC in routing"); });
        Uri uri = new(input);
        Assert.True(source.CanHandle(uri));
        string actual = source.IsThreadUri(uri) ? source.GetThread(uri).Url : source.GetBoard(uri).BoardUri.AbsoluteUri;
        Assert.Equal(expected, actual);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("https://boards.4chan.org.evil/g/thread/123")]
    [InlineData("https://user@boards.4chan.org/g/thread/123")]
    [InlineData("https://boards.4chan.org:8443/g/thread/123")]
    [InlineData("https://boards.4chan.org/g/thread/12%2f3")]
    public void InvalidRoutesDoNotOwnTheResource(string input)
    {
        PluginSourceRouter router = new(new FourChanPlugin().Describe().Sources[0]);
        Assert.False(router.TryResolve(new Uri(input), out _, out _, out _, out _));
    }

    [Fact]
    public async Task RemovedWatchCannotApplyLateWorkerResult()
    {
        using TemporaryProfile profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ThreadContentRuntime content = new(profile.Path, lease);
        await content.InitializeAsync();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken pendingToken = default;
        TestWorker worker = new(async (request, operation, token) =>
        {
            pendingToken = token;
            entered.TrySetResult();
            await release.Task; // Adversarial worker ignores cancellation.
            return Snapshot(request.Thread, "late");
        });
        using PluginSourceImageBoard source = new(worker.Description, "4chan", _ => Task.FromResult<IPluginWorkerClient>(worker));
        DownloadQueue queue = new(1, 1048576);
        await using Registry registry = new(new[] { source }, queue, profile.Path + "/media", TimeSpan.FromHours(1), 3600,
            contentRuntime: content, workStore: new WorkStateStore(profile.Path, lease));
        AdmissionResult admitted = await registry.AddAsync("https://boards.4chan.org/g/thread/123");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task removal = registry.RemoveThreadAsync(admitted.Thread!);
        await WaitUntilAsync(() => pendingToken.IsCancellationRequested);
        release.SetResult();
        await removal.WaitAsync(TimeSpan.FromSeconds(10));
        await using ArchiveReader reader = await content.OpenReaderAsync();
        ArchivedThreadView? view = await reader.ReadThreadAsync(new ResourceId("4chan", "g", "123"));
        Assert.Equal(0, view!.PostCount);
        Assert.True(worker.LastOperation!.Generation > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureAndDeferredNeverBecomeSourceEnd(bool deferred)
    {
        using TemporaryProfile profile = new();
        int calls = 0;
        TestWorker worker = new((request, _, _) => { Interlocked.Increment(ref calls); return Task.FromResult<ThreadReadResult>(deferred
            ? new ThreadDeferred(request.Thread, DateTimeOffset.UtcNow.AddMinutes(1), "cooldown", 429)
            : new ThreadFailure(request.Thread, new FailureDetail("bad_json", "Invalid source JSON"))); });
        using PluginSourceImageBoard source = new(worker.Description, "4chan", _ => Task.FromResult<IPluginWorkerClient>(worker));
        DownloadQueue queue = new(1, 1048576);
        await using Registry registry = new(new[] { source }, queue, profile.Path, TimeSpan.FromHours(1), 3600);
        AdmissionResult admitted = await registry.AddAsync("https://boards.4chan.org/g/thread/123");
        await WaitUntilAsync(() => deferred ? Volatile.Read(ref calls) > 0 : admitted.Thread!.LastCheckFailure != null);
        if (deferred) Assert.Null(admitted.Thread!.LastCheckFailure);
        Assert.Equal(ThreadSourceState.Active, admitted.Thread!.SourceState);
        Assert.Single(registry.Threads);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(5, false)]
    [InlineData(25, false)]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(25, true)]
    public async Task HealthyDeferredStartupAutomaticallyContinuesEverySameSourceWatch(int count, bool repairing)
    {
        using TemporaryProfile profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        WorkStateStore work = new(profile.Path, lease);
        for (int index = 0; index < count; index++)
            work.CommitIntent(new WorkResource { Url = $"https://boards.4chan.org/g/thread/{123 + index}",
                OutputRoot = profile.Path, Generation = 1, IsThread = true,
                Intent = repairing ? WorkIntent.Repairing : WorkIntent.Watching });
        work.CommitIntent(new WorkResource { Url = "https://boards.4chan.org/g/thread/90001", OutputRoot = profile.Path,
            Generation = 1, IsThread = true, Intent = WorkIntent.Suppressed });
        work.CommitIntent(new WorkResource { Url = "https://boards.4chan.org/g/thread/90002", OutputRoot = profile.Path,
            Generation = 1, IsThread = true, Intent = WorkIntent.Completed });
        System.Collections.Concurrent.ConcurrentDictionary<string, int> calls = new();
        using HttpClient transport = new(new AdmissionTransport(() => { }));
        using MeowChan.PluginHost.Http.PluginHostHttp http = new(transport, new MeowChan.Http.RequestPolicyState(), new FourChanPlugin().Describe().Sources);
        await http.SendAsync(new("4chan", HostHttpMethod.Get, new Uri("https://a.4cdn.org/g/catalog.json")));
        TestWorker worker = new(async (request, _, token) =>
        {
            calls.AddOrUpdate(request.Thread.ThreadId, 1, (_, value) => value + 1);
            HostHttpResult result = await http.SendAsync(new("4chan", HostHttpMethod.Get, new Uri("https://a.4cdn.org/g/catalog.json")), token);
            return result is HostHttpDeferred deferred
                ? new ThreadDeferred(request.Thread, deferred.RetryAt, deferred.ReasonCode)
                : Snapshot(request.Thread, "resumed");
        });
        using PluginSourceImageBoard source = new(worker.Description, "4chan", _ => Task.FromResult<IPluginWorkerClient>(worker));
        DownloadQueue queue = new(1, 1048576);
        queue.SetPaused(true);
        await using Registry registry = new(new[] { source }, queue, profile.Path, TimeSpan.FromSeconds(60), 3600, workStore: work);
        await registry.RestoreAsync();
        Assert.Empty(calls);
        queue.SetPaused(false);
        using (CancellationTokenSource deadline = new(TimeSpan.FromSeconds(45)))
            while (!(calls.Count == count && registry.Threads.All(thread => thread.Title == "captured"))) await Task.Delay(20, deadline.Token);
        Assert.All(registry.Threads, thread => Assert.Null(thread.LastCheckFailure));
        Assert.Equal(count, registry.Threads.Count());
        Assert.Equal((0, 0), queue.GetMetadataCapacitySnapshot());
    }

    [Fact]
    public async Task TwentyFiveSameSourceWatchesProgressThroughRealHostHttpAdmissionWithoutManualChecks()
    {
        using TemporaryProfile profile = new();
        var description = new FourChanPlugin().Describe();
        List<TimeSpan> sent = new();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        using HttpClient transport = new(new AdmissionTransport(() => { lock (sent) sent.Add(elapsed.Elapsed); }));
        using MeowChan.PluginHost.Http.PluginHostHttp http = new(transport, new MeowChan.Http.RequestPolicyState(), description.Sources);
        int attempts = 0;
        TestWorker worker = new(async (request, _, token) =>
        {
            Interlocked.Increment(ref attempts);
            HostHttpResult result = await http.SendAsync(new("4chan", HostHttpMethod.Get,
                new Uri($"https://a.4cdn.org/g/thread/{request.Thread.ThreadId}.json")), token);
            return result switch
            {
                HostHttpDeferred deferred => new ThreadDeferred(request.Thread, deferred.RetryAt, deferred.ReasonCode),
                HostHttpResponse => Snapshot(request.Thread, "resumed"),
                _ => throw new InvalidOperationException("Unexpected host failure")
            };
        });
        using PluginSourceImageBoard source = new(worker.Description, "4chan", _ => Task.FromResult<IPluginWorkerClient>(worker));
        DownloadQueue queue = new(1, 1048576);
        await using Registry registry = new(new[] { source }, queue, profile.Path, TimeSpan.FromSeconds(60), 3600);
        for (int index = 0; index < 25; index++)
            await registry.AddAsync($"https://boards.4chan.org/g/thread/{123 + index}");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(45));
        while (registry.Threads.Any(thread => thread.Title != "captured"))
            await Task.Delay(20, timeout.Token);
        Assert.All(registry.Threads, thread => Assert.Null(thread.LastCheckFailure));
        lock (sent)
        {
            Assert.Equal(25, sent.Count);
            for (int index = 1; index < sent.Count; index++)
                Assert.True(sent[index] - sent[index - 1] >= TimeSpan.FromMilliseconds(900));
        }
        Assert.InRange(attempts, 25, 700); // Bounded collisions, not a tight immediate retry loop.
        Assert.Equal((0, 0), queue.GetMetadataCapacitySnapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenericAttemptFailureSurvivesCachedManualRetry(bool timeout)
    {
        using TemporaryProfile profile = new();
        PluginDescription description = new FourChanPlugin().Describe();
        description = description with { Sources = new[] { description.Sources[0] with {
            HttpHosts = new[] { new HttpHostDeclaration("metadata.example", HttpHostPurpose.Metadata) } } } };
        int sends = 0;
        using HttpClient transport = new(new FailureTransport(() => Interlocked.Increment(ref sends), timeout));
        using MeowChan.PluginHost.Http.PluginHostHttp http = new(transport, new MeowChan.Http.RequestPolicyState(), description.Sources);
        int calls = 0;
        TestWorker worker = new(async (request, _, token) =>
        {
            Interlocked.Increment(ref calls);
            HostHttpResult result = await http.SendAsync(new("4chan", HostHttpMethod.Get, new Uri("https://metadata.example/thread.json")), token);
            return result is HostHttpDeferred deferred
                ? new ThreadDeferred(request.Thread, deferred.RetryAt, deferred.ReasonCode)
                : throw new InvalidOperationException("Expected retry cooldown deferral");
        }, description);
        using PluginSourceImageBoard source = new(description, "4chan", _ => Task.FromResult<IPluginWorkerClient>(worker));
        DownloadQueue queue = new(1, 1048576);
        await using Registry registry = new(new[] { source }, queue, profile.Path, TimeSpan.FromMilliseconds(20), 3600);
        var admitted = await registry.AddAsync("https://boards.4chan.org/g/thread/123");
        await WaitUntilAsync(() => admitted.Thread!.LastCheckFailure != null);
        var failure = admitted.Thread!.LastCheckFailure;
        Assert.Equal(1, sends);
        await Assert.ThrowsAnyAsync<Exception>(() => admitted.Thread.checkThreadAsync(queue));
        Assert.Same(failure, admitted.Thread.LastCheckFailure);
        Assert.Equal(1, calls);
        await Task.Delay(1300); // Exceeds generic ~1s cooldown, not the plugin 10s minimum.
        Assert.Equal(1, sends);
        Assert.Equal(1, calls);
    }

    private sealed class FailureTransport(Action sent, bool timeout) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            sent();
            if (timeout) throw new TimeoutException("synthetic metadata header timeout");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request });
        }
    }

    private sealed class AdmissionTransport(Action sent) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            sent();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent("{}") });
        }
    }

    internal static ThreadReadResult Snapshot(SourceThreadId id, string text) => new PluginSdk.ThreadSnapshot(new SourceThreadSnapshot(id,
        new Uri($"https://boards.4chan.org/{id.BoardId}/thread/{id.ThreadId}"), "captured", SourceThreadState.Active,
        new[] { new SourcePost(id.ThreadId, 0, SourceFieldPresence.Text, Text: text, TextFormat: SourceTextFormat.PlainText) }));

    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    internal sealed class TestWorker(Func<ThreadReadRequest, PluginOperationContext, CancellationToken, Task<ThreadReadResult>> read, PluginDescription? description = null, Func<CatalogReadRequest, PluginOperationContext, CancellationToken, Task<CatalogReadResult>>? catalog = null) : IPluginWorkerClient
    {
        public PluginDescription Description { get; } = description ?? new FourChanPlugin().Describe();
        public PluginWorkerIdentity Identity { get; } = new("test", "build", "config");
        public PluginWorkerSnapshot Snapshot => new(Identity, "ready", null, null, null, Array.Empty<string>());
        public PluginOperationContext? LastOperation { get; private set; }
        public Task<ThreadReadResult> ReadThreadAsync(ThreadReadRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default)
        { LastOperation = operation; return read(request, operation, cancellationToken); }
        public Task<CatalogReadResult> ReadCatalogAsync(CatalogReadRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default) => catalog?.Invoke(request, operation, cancellationToken) ?? throw new NotSupportedException();
        public Task<ImageFilterResult> EvaluateAsync(ImageFilterRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FieldValidationError>> ValidateSettingsAsync(SettingsValidationRequest request, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<FieldValidationError>>(Array.Empty<FieldValidationError>());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task RetryAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    internal sealed class TemporaryProfile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "meowchan-adapter-" + Guid.NewGuid().ToString("N"));
        public TemporaryProfile() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}

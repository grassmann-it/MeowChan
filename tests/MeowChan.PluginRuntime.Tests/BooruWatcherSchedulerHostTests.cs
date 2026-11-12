using System.Net;
using System.Text;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Http;
using MeowChan.Persistence;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Http;
using MeowChan.PluginHost.Sources.Booru;
using MeowChan.PluginSdk;

namespace MeowChan.PluginRuntime.Tests;

/// <summary>
/// B5 acceptance through the real host path: scheduler → <see cref="BooruWatcherSearchPort"/> →
/// <see cref="BooruSearchService"/> → worker → <see cref="BooruHostServices"/> → <see cref="BooruHostHttp"/>
/// → in-process stub provider. The worker double behaves like the Danbooru plugin (metadata only through
/// host HTTP). Time is a manual clock; nothing leaves the process.
/// </summary>
public sealed class BooruWatcherSchedulerHostTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    [Fact]
    public async Task TwoWatchersProduceRegularButPacedRequestsWithoutParallelism()
    {
        await using Harness harness = new();
        harness.Provider.Publish("cat", 100);
        harness.Provider.Publish("dog", 500);
        string cat = harness.Scheduler.AddWatcher("cat").Watcher!.WatcherId;
        string dog = harness.Scheduler.AddWatcher("dog").Watcher!.WatcherId;

        Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, (await harness.RunNextAsync())!.Kind);
        Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, (await harness.RunNextAsync())!.Kind);
        Assert.Null(await harness.RunNextAsync());
        Assert.Equal(2, harness.Provider.RequestCount);

        harness.Provider.Publish("cat", Enumerable.Range(101, 50).Select(id => (long)id).ToArray());
        harness.Provider.Publish("dog", Enumerable.Range(501, 45).Select(id => (long)id).ToArray());
        harness.Clock.Advance(Interval);
        DanbooruWatcherCheckSummary first = (await harness.RunNextAsync())!;
        DanbooruWatcherCheckSummary second = (await harness.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.Completed, first.Kind);
        Assert.Equal(DanbooruWatcherCheckKind.Completed, second.Kind);
        Assert.Equal(95, harness.Admission.Count);
        Assert.All(harness.Admission.PerPost.Values, count => Assert.Equal(1, count));
        Assert.Equal(2 + 3 + 3, harness.Provider.RequestCount);

        harness.Clock.Advance(Interval);
        await harness.RunNextAsync();
        await harness.RunNextAsync();
        Assert.Null(await harness.RunNextAsync());
        Assert.Equal(10, harness.Provider.RequestCount);

        Assert.Equal(1, harness.Provider.MaxConcurrent);
        DateTimeOffset[] starts = harness.Provider.Requests.Select(request => request.At).ToArray();
        for (int index = 1; index < starts.Length; index++)
            Assert.True(starts[index] - starts[index - 1] >= BooruHostHttp.MinimumStartSpacing,
                $"Request {index} started {(starts[index] - starts[index - 1]).TotalMilliseconds} ms after the previous one.");
        foreach (string query in new[] { "cat", "dog" })
        {
            DateTimeOffset[] firstPages = harness.Provider.Requests.Where(request => request.Query == query && request.Page == 1)
                .Select(request => request.At).ToArray();
            Assert.Equal(3, firstPages.Length);
            for (int index = 1; index < firstPages.Length; index++) Assert.True(firstPages[index] - firstPages[index - 1] >= Interval);
        }
        Assert.All(harness.Provider.Requests, request => Assert.Equal(20, request.Limit));
        Assert.All(new[] { cat, dog }, id => Assert.Equal(DanbooruWatcherActivity.Scheduled, harness.Scheduler.GetStatus(id)!.Activity));
    }

    [Fact]
    public async Task Http429PausesOnlyFurtherRequestsUntilTheCooldownEnds()
    {
        await using Harness harness = new();
        harness.Provider.Publish("cat", 100);
        harness.Provider.Publish("dog", 500);
        harness.Provider.Respond = (request, index) => index == 0
            ? Status(HttpStatusCode.TooManyRequests, retryAfterSeconds: 120)
            : null;
        string cat = harness.Scheduler.AddWatcher("cat").Watcher!.WatcherId;
        string dog = harness.Scheduler.AddWatcher("dog").Watcher!.WatcherId;
        DateTimeOffset start = harness.Clock.GetUtcNow();

        DanbooruWatcherCheckSummary deferred = (await harness.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.Deferred, deferred.Kind);
        Assert.Equal(start + TimeSpan.FromSeconds(120), deferred.RetryAtUtc);
        string throttled = harness.Provider.Requests[0].Query == "cat" ? cat : dog;
        string other = throttled == cat ? dog : cat;
        Assert.Equal(DanbooruWatcherState.Cooldown, harness.Scheduler.GetStatus(throttled)!.State);
        Assert.Equal(DanbooruWatcherState.Active, harness.Scheduler.GetStatus(other)!.State);
        Assert.Equal(DanbooruWatcherActivity.Cooldown, harness.Scheduler.GetStatus(other)!.Activity);

        harness.Clock.Advance(TimeSpan.FromSeconds(119));
        Assert.Null(await harness.RunNextAsync());
        Assert.Equal("provider_cooldown", (await harness.Scheduler.CheckNowAsync(other)).Code);
        Assert.Equal(1, harness.Provider.RequestCount);

        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.NotNull(await harness.RunNextAsync());
        Assert.NotNull(await harness.RunNextAsync());
        Assert.Equal(3, harness.Provider.RequestCount);
        Assert.All(harness.Scheduler.GetSnapshot().Watchers, w => Assert.Equal(DanbooruWatcherState.Active, w.State));
    }

    [Fact]
    public async Task Repeated503IsRetriedOnceByTheHostThenDefersTheWatcherWithTheFloor()
    {
        await using Harness harness = new();
        harness.Provider.Respond = (_, _) => Status(HttpStatusCode.ServiceUnavailable);
        string id = harness.Scheduler.AddWatcher("cat").Watcher!.WatcherId;
        DateTimeOffset start = harness.Clock.GetUtcNow();
        DanbooruWatcherCheckSummary deferred = (await harness.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.Deferred, deferred.Kind);
        Assert.Equal(2, harness.Provider.RequestCount);
        Assert.True(harness.Provider.Requests[1].At - harness.Provider.Requests[0].At >= BooruHostHttp.RetryDelay);
        Assert.True(deferred.RetryAtUtc >= harness.Clock.GetUtcNow() + DanbooruWatcherSchedulerOptions.DefaultMinimumDeferral
            - TimeSpan.FromSeconds(1));
        Assert.Equal(DanbooruWatcherState.Cooldown, harness.Scheduler.GetStatus(id)!.State);
        harness.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Null(await harness.RunNextAsync());
        Assert.Equal(2, harness.Provider.RequestCount);
        _ = start;
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "access-denied")]
    [InlineData(HttpStatusCode.Found, "http-redirect")]
    [InlineData(HttpStatusCode.Gone, "page-limit")]
    public async Task NonRetryableProviderAnswersStopTheWatcherVisiblyWithoutRetry(HttpStatusCode status, string code)
    {
        await using Harness harness = new();
        harness.Provider.Respond = (_, _) => Status(status);
        string id = harness.Scheduler.AddWatcher("cat").Watcher!.WatcherId;
        DanbooruWatcherCheckSummary failed = (await harness.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.Failed, failed.Kind);
        Assert.Equal(code, failed.Code);
        Assert.Equal(1, harness.Provider.RequestCount);
        Assert.Equal(DanbooruWatcherActivity.Error, harness.Scheduler.GetStatus(id)!.Activity);
        harness.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Null(await harness.RunNextAsync());
        Assert.Equal(1, harness.Provider.RequestCount);
    }

    [Fact]
    public async Task UnavailableWorkerSendsNothingAndKeepsTheWatcher()
    {
        await using Harness harness = new(workerAvailable: false);
        string id = harness.Scheduler.AddWatcher("cat").Watcher!.WatcherId;
        DanbooruWatcherCheckSummary result = (await harness.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.SourceUnavailable, result.Kind);
        Assert.Equal(0, harness.Provider.RequestCount);
        Assert.Equal(DanbooruWatcherState.Active, harness.Scheduler.GetStatus(id)!.State);
    }

    [Fact]
    public async Task PausedAndRemovedWatchersSendNothingThroughTheHost()
    {
        await using Harness harness = new();
        harness.Provider.Publish("cat", 100);
        string paused = harness.Scheduler.AddWatcher("cat").Watcher!.WatcherId;
        string removed = harness.Scheduler.AddWatcher("dog").Watcher!.WatcherId;
        harness.Scheduler.Pause(paused);
        harness.Scheduler.Remove(removed);
        for (int day = 0; day < 3; day++)
        {
            harness.Clock.Advance(TimeSpan.FromDays(1));
            Assert.Null(await harness.RunNextAsync());
        }
        Assert.Equal("watcher_removed", (await harness.Scheduler.CheckNowAsync(removed)).Code);
        Assert.Equal(0, harness.Provider.RequestCount);

        // Owner decision: an explicit check of the paused watcher is one request; it stays paused.
        Task<DanbooruWatcherCheckSummary> manual = harness.Scheduler.CheckNowAsync(paused);
        await harness.RunNextAsync();
        Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, (await manual).Kind);
        Assert.Equal(DanbooruWatcherState.Paused, harness.Scheduler.GetStatus(paused)!.State);
        harness.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Null(await harness.RunNextAsync());
        Assert.Equal(1, harness.Provider.RequestCount);
    }

    private static HttpResponseMessage Status(HttpStatusCode status, int? retryAfterSeconds = null)
    {
        HttpResponseMessage response = new(status) { Content = new StringContent("synthetic") };
        if (retryAfterSeconds is { } seconds) response.Headers.TryAddWithoutValidation("Retry-After", seconds.ToString());
        if ((int)status is >= 300 and < 400) response.Headers.Location = new Uri("https://elsewhere.invalid/");
        return response;
    }

    [Fact]
    public async Task MultiPageCheckSharesOneJobResourceAcrossAllPagesAndEachCheckGetsItsOwn()
    {
        await using Harness harness = new();
        harness.Provider.Publish("cat", 100);
        harness.Scheduler.AddWatcher("cat");
        Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, (await harness.RunNextAsync())!.Kind);
        PluginOperationContext baseline = Assert.Single(harness.Worker.Operations);
        Assert.StartsWith("booru-job:", baseline.ResourceId, StringComparison.Ordinal);

        // 70 new posts: pages 1-3 are full, page 4 reaches the known post; four requests in one check.
        harness.Provider.Publish("cat", Enumerable.Range(101, 70).Select(id => (long)id).ToArray());
        harness.Clock.Advance(Interval);
        DanbooruWatcherCheckSummary multi = (await harness.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.Completed, multi.Kind);
        Assert.Equal(4, multi.PagesRead);
        IReadOnlyList<PluginOperationContext> all = harness.Worker.Operations;
        PluginOperationContext[] second = all.Skip(1).ToArray();
        Assert.Equal(4, second.Length);
        Assert.Equal(1 + 4, harness.Provider.RequestCount);
        // One typed operation context for the whole check (same resource id, same generation) ...
        Assert.Single(second.Select(operation => operation.ResourceId).Distinct());
        Assert.All(second, operation => Assert.Equal(1, operation.Generation));
        Assert.All(second, operation => Assert.Equal(second[0], operation));
        // ... which is different from the previous check's job.
        Assert.NotEqual(baseline.ResourceId, second[0].ResourceId);

        // The job is closed when the check ends: the old resource id can no longer send.
        HostHttpResult late = await harness.Services.SendHttpAsync(new HostHttpRequest("danbooru", HostHttpMethod.Get,
            new("https://danbooru.donmai.us/posts.json?tags=cat&page=1&limit=20"), new Dictionary<string, string>()),
            second[0], default);
        Assert.Equal("booru-job", Assert.IsType<HostHttpFailure>(late).Failure.Code);

        // A third check is another job again.
        harness.Provider.Publish("cat", 171);
        harness.Clock.Advance(Interval);
        Assert.Equal(DanbooruWatcherCheckKind.Completed, (await harness.RunNextAsync())!.Kind);
        PluginOperationContext third = harness.Worker.Operations.Last();
        Assert.NotEqual(second[0].ResourceId, third.ResourceId);
        Assert.NotEqual(baseline.ResourceId, third.ResourceId);
    }

    private static SourceDescription DanbooruSource() => new("danbooru", "Danbooru", new("https://danbooru.donmai.us/"),
        SourceCapabilities.PostSearch | SourceCapabilities.OriginalMedia | SourceCapabilities.Previews, [],
        [new("danbooru.donmai.us", HttpHostPurpose.Metadata), new("cdn.donmai.us", HttpHostPurpose.OriginalMedia | HttpHostPurpose.PreviewMedia)],
        new("1", []));

    /// <summary>Everything one test needs, on a private profile directory.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "meowchan-b5-host-" + Guid.NewGuid().ToString("N"));
        private readonly ProfileLease _lease;
        private readonly HttpClient _transport;
        private readonly BooruHostHttp _http;

        internal Harness(bool workerAvailable = true)
        {
            Directory.CreateDirectory(_root);
            _lease = ProfileLease.Acquire(_root);
            Catalog = new BooruCatalogStore(_lease.ProfileDirectory, _lease);
            Catalog.Read();
            _lease.Admission.CompleteStartup();
            Provider = new StubProvider(Clock);
            _transport = new HttpClient(Provider, disposeHandler: false);
            _http = new BooruHostHttp(_transport, new RequestPolicyState(Clock), [DanbooruSource()], productVersion: "1.1.0");
            BooruHostServices services = new(new NoInner(), _http);
            PluginLikeWorker worker = new(services);
            Worker = worker;
            Services = services;
            BooruWatcherSearchPort port = new(new BooruSearchService(services), services,
                _ => Task.FromResult<IPluginWorkerClient?>(workerAvailable ? worker : null), Clock);
            Scheduler = new DanbooruWatcherScheduler(Catalog, port, Admission, Clock,
                new DanbooruWatcherSchedulerOptions { Interval = Interval, CheckStartSpacing = TimeSpan.Zero });
            Scheduler.Load();
        }

        internal PumpClock Clock { get; } = new(new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero));
        internal StubProvider Provider { get; }
        internal PluginLikeWorker Worker { get; }
        internal BooruHostServices Services { get; }
        internal BooruCatalogStore Catalog { get; }
        internal RecordingAdmission Admission { get; } = new();
        internal DanbooruWatcherScheduler Scheduler { get; }

        /// <summary>Runs one scheduler step while advancing the clock to each pending host wait (pacing, retry).</summary>
        internal async Task<DanbooruWatcherCheckSummary?> RunNextAsync()
        {
            Task<DanbooruWatcherCheckSummary?> step = Scheduler.RunNextAsync();
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (!step.IsCompleted)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("The scheduler step did not finish.");
                await Task.Delay(5);
                if (!step.IsCompleted && Provider.InFlight == 0) Clock.AdvanceToNextDelay();
            }
            return await step;
        }

        public async ValueTask DisposeAsync()
        {
            await Scheduler.StopAsync();
            _http.Dispose();
            _transport.Dispose();
            _lease.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Manual clock whose test pump can jump to the next pending delay. Per-attempt response deadlines
    /// (timers whose state is a <see cref="CancellationTokenSource"/>) are never jumped to.
    /// </summary>
    internal sealed class PumpClock(DateTimeOffset start) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<Entry> _timers = new();
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Entry entry = new(this, callback, state);
            lock (_gate) { entry.Schedule(dueTime); _timers.Add(entry); }
            return entry;
        }

        internal void Advance(TimeSpan amount)
        {
            lock (_gate) _now += amount;
            FireDue();
        }

        internal void AdvanceToNextDelay()
        {
            lock (_gate)
            {
                Entry? next = _timers.Where(timer => !timer.Disposed && timer.Due is not null && timer.State is not CancellationTokenSource)
                    .OrderBy(timer => timer.Due).FirstOrDefault();
                if (next?.Due is not { } due) return;
                if (due > _now) _now = due;
            }
            FireDue();
        }

        private void FireDue()
        {
            Entry[] due;
            lock (_gate)
            {
                due = _timers.Where(timer => !timer.Disposed && timer.Due is { } at && at <= _now).ToArray();
                _timers.RemoveAll(timer => timer.Disposed);
            }
            foreach (Entry timer in due) timer.Fire();
        }

        private sealed class Entry(PumpClock owner, TimerCallback callback, object? state) : ITimer
        {
            internal object? State => state;
            internal bool Disposed { get; private set; }
            internal DateTimeOffset? Due { get; private set; }
            internal void Schedule(TimeSpan dueTime) =>
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + (dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime);
            internal void Fire()
            {
                lock (owner._gate)
                {
                    if (Disposed || Due is not { } at || at > owner._now) return;
                    Due = null;
                }
                callback(state);
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) { lock (owner._gate) { if (Disposed) return false; Schedule(dueTime); return true; } }
            public void Dispose() { lock (owner._gate) Disposed = true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    /// <summary>In-process provider: Danbooru-shaped JSON pages, newest first, for /posts.json only.</summary>
    internal sealed class StubProvider(TimeProvider clock) : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, List<long>> _posts = new(StringComparer.Ordinal);
        private int _inFlight;

        internal List<(string Query, int Page, int Limit, DateTimeOffset At)> Requests { get; } = new();
        internal int MaxConcurrent { get; private set; }
        internal int InFlight => Volatile.Read(ref _inFlight);
        internal int RequestCount { get { lock (_gate) return Requests.Count; } }
        internal Func<HttpRequestMessage, int, HttpResponseMessage?>? Respond { get; set; }

        internal void Publish(string query, params long[] ids)
        {
            lock (_gate)
            {
                if (!_posts.TryGetValue(query, out List<long>? list)) _posts[query] = list = new();
                list.AddRange(ids);
                list.Sort((left, right) => right.CompareTo(left));
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int concurrent = Interlocked.Increment(ref _inFlight);
            try
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Null(request.Headers.Authorization);
                Assert.False(request.Headers.Contains("Cookie"));
                Dictionary<string, string> query = request.RequestUri!.Query.TrimStart('?').Split('&')
                    .Select(part => part.Split('=', 2)).ToDictionary(part => part[0], part => Uri.UnescapeDataString(part[1]));
                string tags = query["tags"];
                int page = int.Parse(query["page"]);
                int limit = int.Parse(query["limit"]);
                int index;
                lock (_gate)
                {
                    MaxConcurrent = Math.Max(MaxConcurrent, concurrent);
                    index = Requests.Count;
                    Requests.Add((tags, page, limit, clock.GetUtcNow()));
                }
                if (Respond?.Invoke(request, index) is { } scripted)
                {
                    scripted.RequestMessage = request;
                    return Task.FromResult(scripted);
                }
                long[] slice;
                lock (_gate)
                    slice = (_posts.TryGetValue(tags, out List<long>? list) ? list : new()).Skip((page - 1) * limit).Take(limit).ToArray();
                string body = JsonSerializer.Serialize(slice.Select(id => new { id }));
                HttpResponseMessage response = new(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    RequestMessage = request
                };
                return Task.FromResult(response);
            }
            finally { Interlocked.Decrement(ref _inFlight); }
        }
    }

    /// <summary>Behaves like the Danbooru plugin for search: one host GET per page, statuses classified.</summary>
    internal sealed class PluginLikeWorker(BooruHostServices services) : IPluginWorkerClient
    {
        private readonly List<PluginOperationContext> _operations = new();
        internal IReadOnlyList<PluginOperationContext> Operations { get { lock (_operations) return _operations.ToArray(); } }

        public PluginDescription Description { get; } = new("fake", "fake", "1", SdkContract.Version, "fake", [DanbooruSource()], [], new("1", []), []);
        public PluginWorkerIdentity Identity { get; } = new("fake", "1", "1");
        public PluginWorkerSnapshot Snapshot => new(Identity, "ready", null, null, null, []);

        public async Task<BooruSearchResult> SearchBooruPostsAsync(BooruSearchRequest request, PluginOperationContext operation,
            CancellationToken cancellationToken = default)
        {
            lock (_operations) _operations.Add(operation);
            Uri url = new($"https://danbooru.donmai.us/posts.json?tags={Uri.EscapeDataString(request.Query)}&page={request.Page}&limit={request.Limit}");
            HostHttpResult result = await services.SendHttpAsync(new HostHttpRequest(request.SourceId, HostHttpMethod.Get, url,
                new Dictionary<string, string> { ["Accept"] = "application/json" }), operation, cancellationToken);
            switch (result)
            {
                case HostHttpDeferred deferred:
                    return new BooruSearchDeferred(request.SourceId, deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode);
                case HostHttpFailure failure:
                    return new BooruSearchFailure(request.SourceId, failure.Failure);
            }
            HostHttpResponse response = (HostHttpResponse)result;
            switch (response.StatusCode)
            {
                case 200: break;
                case 502 or 503:
                    return new BooruSearchDeferred(request.SourceId, DateTimeOffset.UtcNow.AddYears(-1), "provider-unavailable", response.StatusCode);
                case 401 or 403:
                    return new BooruSearchFailure(request.SourceId, new FailureDetail("access-denied", "denied", response.StatusCode));
                case 410:
                    return new BooruSearchFailure(request.SourceId, new FailureDetail("page-limit", "gone", response.StatusCode));
                default:
                    return new BooruSearchFailure(request.SourceId, new FailureDetail("http-" + response.StatusCode, "status", response.StatusCode));
            }
            using JsonDocument document = JsonDocument.Parse(response.Body);
            BooruPost[] posts = document.RootElement.EnumerateArray()
                .Select(item => new BooruPost(new SourcePostId(request.SourceId, item.GetProperty("id").GetInt64().ToString()), BooruFieldPresence.None))
                .ToArray();
            return new BooruSearchSnapshot(new BooruSearchPage(request.SourceId, request.Query, request.Page, posts, posts.Length < request.Limit));
        }

        public Task<BooruPostResult> ReadBooruPostAsync(BooruPostRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<CatalogReadResult> ReadCatalogAsync(CatalogReadRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ThreadReadResult> ReadThreadAsync(ThreadReadRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImageFilterResult> EvaluateAsync(ImageFilterRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RetryAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FieldValidationError>> ValidateSettingsAsync(SettingsValidationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoInner : IPluginHostServices
    {
        public Task<HostHttpResult> SendHttpAsync(HostHttpRequest request, PluginOperationContext operation, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Booru metadata must not reach the ordinary host path.");
        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, PluginOperationContext operation, CancellationToken cancellationToken)
            => throw new InvalidOperationException();
    }

    /// <summary>Admission double counting per post (the B4 identity).</summary>
    internal sealed class RecordingAdmission : IDanbooruWatcherAdmissionPort
    {
        private readonly object _gate = new();
        internal Dictionary<string, int> PerPost { get; } = new(StringComparer.Ordinal);
        internal int Count { get { lock (_gate) return PerPost.Count; } }

        public Task<DanbooruWatcherAdmissionResult> AdmitAsync(BooruPostClaim post, string watcherId, string query, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                bool first = !PerPost.ContainsKey(post.PostId);
                PerPost[post.PostId] = PerPost.GetValueOrDefault(post.PostId) + 1;
                return Task.FromResult(first ? DanbooruWatcherAdmissionResult.Queued : DanbooruWatcherAdmissionResult.AlreadyQueued);
            }
        }
    }
}

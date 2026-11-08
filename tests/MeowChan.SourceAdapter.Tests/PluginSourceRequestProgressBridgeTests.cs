using System.ComponentModel;
using System.Net;
using System.Reflection;
using MeowChan.Downloader;
using MeowChan.Http;
using MeowChan.Interfaces;
using MeowChan.OfficialSources.FourChan;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Http;
using MeowChan.PluginHost.Sources;
using MeowChan.PluginSdk;

namespace MeowChan.SourceAdapter.Tests;

// Real source adapters + policy + synthetic handlers. Reflection only reaches the
// internal history correlation/retirement seam, as in MetadataHistoryTests; not IPC coverage.
public sealed class PluginSourceRequestProgressBridgeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private const string Secret = "secret-token-body-path";
    private static readonly Type HistoryType = typeof(PluginHostHttp).GetNestedType("MetadataHistory", BindingFlags.NonPublic)!;
    private const BindingFlags Internal = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
    private static object? CurrentHistory => HistoryType.GetProperty("Current", Internal)!.GetValue(null);
    private static IDisposable Resume(PluginOperationContext operation) =>
        (IDisposable)HistoryType.GetMethod("Resume", Internal)!.Invoke(null, new object[] { operation })!;
    private static Action<RequestPolicyProgress> CaptureReporter() =>
        HistoryType.GetMethod("ReportProgress", Internal)!.CreateDelegate<Action<RequestPolicyProgress>>(CurrentHistory!);

    public static IEnumerable<object[]> ProgressPaths()
    {
        foreach (string path in new[] { "thread", "catalog", "cursor", "page" })
            foreach (bool detached in new[] { false, true })
                yield return new object[] { path, detached };
    }

    [Theory]
    [MemberData(nameof(ProgressPaths))]
    public async Task ProgressReachesOnlyOwningWatchAcrossBodyRedirectAndCatalogPages(string path, bool detached)
    {
        int pages = path is "cursor" or "page" ? 2 : 1;
        Gate[] bodies = Enumerable.Range(0, pages).Select(_ => new Gate()).ToArray();
        PluginDescription description = Description();
        SourceFixture? target = null;
        using Probe probe = new((request, _) =>
        {
            // This runs synchronously at dispatch, before the policy's Sending notification.
            AssertPhase(target!, RequestPolicyPhase.WaitingForAdmission);
            Assert.Null(target!.OtherProgress);
            int page = int.Parse(request.RequestUri!.Segments[^1], System.Globalization.CultureInfo.InvariantCulture);
            if (request.RequestUri.AbsolutePath.StartsWith("/redirect/", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri($"https://second.example/body/{page}") },
                    Content = new StringContent("")
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new GatedContent(bodies[page]) });
        });
        using HttpClient transport = new(probe);
        using PluginHostHttp host = new(transport, new RequestPolicyState(), description.Sources);
        PluginOperationContext? first = null;
        int workerCalls = 0;
        SourceAdapterTests.TestWorker worker = Worker(description, async (cursor, page, operation, token) =>
        {
            if (Interlocked.Increment(ref workerCalls) == 1) first = operation;
            else Assert.Same(first, operation);
            int index = cursor != null || page != null ? 1 : 0;
            HostHttpRequest request = Request($"/redirect/{index}");
            if (!detached) return await host.SendAsync(request, token);
            return await Detached(async () =>
            {
                Assert.Null(CurrentHistory);
                using (Resume(operation))
                {
                    Assert.NotNull(CurrentHistory);
                    return await host.SendAsync(request, token);
                }
            });
        }, path);
        await using SourceFixture fixture = new(path != "thread", worker);
        target = fixture;
        using CancellationTokenSource caller = new();
        Task checking = fixture.CheckAsync(caller.Token);
        try
        {
            for (int page = 0; page < pages; page++)
            {
                await bodies[page].Entered.Task.WaitAsync(Deadline);
                AssertPhase(fixture, RequestPolicyPhase.Sending);
                Assert.Equal(1, fixture.Progress!.Attempt);
                Assert.Null(fixture.OtherProgress);
                bodies[page].Release.TrySetResult();
            }
            await checking.WaitAsync(Deadline);
            Assert.Null(fixture.Progress);
            Assert.Null(fixture.SnapshotProgress);
            Assert.Null(fixture.OtherProgress);
            Assert.Equal(pages, workerCalls);
            Assert.Equal(2 * pages, probe.Requests);
            Assert.Equal(fixture.Url, first!.ResourceId);
            Assert.Equal(0, fixture.MediaRequests);
        }
        finally
        {
            caller.Cancel();
            foreach (Gate body in bodies) body.Release.TrySetResult();
            await DrainAsync(checking);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialNoSendProgressIsVisibleWhileWaitingAndClearedBeforeCachedRetry(bool catalog)
    {
        PluginDescription description = Description();
        using Probe probe = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(1)) },
            Content = new StringContent("")
        }));
        using HttpClient transport = new(probe);
        using PluginHostHttp host = new(transport, new RequestPolicyState(), description.Sources);
        Assert.IsType<HostHttpDeferred>(await host.SendAsync(Request()).WaitAsync(Deadline));
        Gate waiting = new();
        int workerCalls = 0;
        SourceAdapterTests.TestWorker worker = Worker(description, async (_, _, _, token) =>
        {
            Interlocked.Increment(ref workerCalls);
            HostHttpDeferred result = Assert.IsType<HostHttpDeferred>(await host.SendAsync(Request(), token));
            waiting.Entered.TrySetResult();
            await waiting.Release.Task.WaitAsync(token);
            return result;
        });
        await using SourceFixture fixture = new(catalog, worker);
        try
        {
            fixture.Start();
            await waiting.Entered.Task.WaitAsync(Deadline);
            AssertPhase(fixture, RequestPolicyPhase.WaitingForAdmission);
            Assert.Equal(1, fixture.Progress!.Attempt);
            Assert.Null(fixture.OtherProgress);
            Assert.Equal(1, probe.Requests); // only the other operation's synthetic priming send
            waiting.Release.TrySetResult();
            // The read gate fences return/retirement of the monitored observation.
            // Apply the timeout outside the assertion: a stuck read is not a deferred result.
            Exception first = await Assert.ThrowsAnyAsync<Exception>(() => fixture.CheckAsync()).WaitAsync(Deadline);
            Exception cached = await Assert.ThrowsAnyAsync<Exception>(() => fixture.CheckAsync()).WaitAsync(Deadline);
            Assert.Same(first, cached);
            await fixture.StopAsync().WaitAsync(Deadline);
            Assert.Null(fixture.Progress);
            Assert.Null(fixture.SnapshotProgress);
            Assert.Null(fixture.Failure);
            Assert.Equal(1, workerCalls);
            Assert.Equal(1, probe.Requests);
        }
        finally
        {
            waiting.Release.TrySetResult();
            await fixture.StopAsync().WaitAsync(Deadline);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingQueuedCheckCannotClearActiveChecksProgress(bool catalog)
    {
        Gate body = new();
        PluginDescription description = Description();
        using Probe probe = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new GatedContent(body) }));
        using HttpClient transport = new(probe);
        using PluginHostHttp host = new(transport, new RequestPolicyState(), description.Sources);
        int workerCalls = 0;
        SourceAdapterTests.TestWorker worker = Worker(description, (_, _, _, token) =>
        {
            Interlocked.Increment(ref workerCalls);
            return host.SendAsync(Request(), token);
        });
        await using SourceFixture fixture = new(catalog, worker);
        using CancellationTokenSource caller = new();
        using CancellationTokenSource queuedCaller = new();
        Task active = fixture.CheckAsync(caller.Token);
        Task? queued = null;
        try
        {
            await body.Entered.Task.WaitAsync(Deadline);
            AssertPhase(fixture, RequestPolicyPhase.Sending);
            RequestPolicyProgress progress = fixture.Progress!;
            queued = fixture.CheckAsync(queuedCaller.Token);
            Assert.False(queued.IsCompleted);
            queuedCaller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued!).WaitAsync(Deadline);
            Assert.Same(progress, fixture.Progress);
            Assert.Same(progress, fixture.SnapshotProgress);
            Assert.Null(fixture.OtherProgress);
            Assert.Equal(1, workerCalls);
            Assert.Equal(1, probe.Requests);
            body.Release.TrySetResult();
            await active.WaitAsync(Deadline);
            Assert.Null(fixture.Progress);
            Assert.Null(fixture.SnapshotProgress);
        }
        finally
        {
            caller.Cancel();
            queuedCaller.Cancel();
            body.Release.TrySetResult();
            try { if (queued != null) await DrainAsync(queued); }
            finally { await DrainAsync(active); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerStopDuringBodyClearsProgressWithoutAddingFailure(bool catalog)
    {
        Gate body = new();
        PluginDescription description = Description();
        using Probe probe = new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new GatedContent(body) }));
        using HttpClient transport = new(probe);
        using PluginHostHttp host = new(transport, new RequestPolicyState(), description.Sources);
        SourceAdapterTests.TestWorker worker = Worker(description, (_, _, _, token) => host.SendAsync(Request(), token));
        await using SourceFixture fixture = new(catalog, worker);
        try
        {
            fixture.Start();
            await body.Entered.Task.WaitAsync(Deadline);
            AssertPhase(fixture, RequestPolicyPhase.Sending);
            Assert.True(body.ReadToken.CanBeCanceled);
            Assert.False(body.Release.Task.IsCompleted);
            await fixture.StopAsync().WaitAsync(Deadline);
            await body.CancellationObserved.Task.WaitAsync(Deadline);
            Assert.True(body.ReadToken.IsCancellationRequested);
            Assert.False(body.Release.Task.IsCompleted); // Stop must cancel the read, not release the fixture.
            Assert.Null(fixture.Progress);
            Assert.Null(fixture.SnapshotProgress);
            Assert.Null(fixture.Failure);
            Assert.Null(fixture.OtherProgress);
            Assert.Equal(1, probe.Requests);
            Assert.Equal(0, fixture.MediaRequests);
        }
        finally
        {
            body.Release.TrySetResult();
            await fixture.StopAsync().WaitAsync(Deadline);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledLiveObserverAndLateSdkResultCannotPublishNewProgress(bool catalog)
    {
        Gate workerReturn = new();
        PluginDescription description = Description();
        using Probe probe = new((_, _) => Task.FromResult(Ok()));
        using HttpClient transport = new(probe);
        using PluginHostHttp host = new(transport, new RequestPolicyState(), description.Sources);
        Action<RequestPolicyProgress>? report = null;
        SourceAdapterTests.TestWorker worker = Worker(description, async (_, _, _, token) =>
        {
            report = CaptureReporter();
            HostHttpResult result = await host.SendAsync(Request(), token);
            workerReturn.Entered.TrySetResult();
            await workerReturn.Release.Task; // deliberately return an SDK success after caller cancellation
            return result;
        });
        await using SourceFixture fixture = new(catalog, worker);
        using CancellationTokenSource caller = new();
        Task checking = fixture.CheckAsync(caller.Token);
        try
        {
            await workerReturn.Entered.Task.WaitAsync(Deadline);
            AssertPhase(fixture, RequestPolicyPhase.Sending);
            RequestPolicyProgress beforeCancel = fixture.Progress!;
            caller.Cancel();
            report!(new RequestPolicyProgress(RequestPolicyPhase.Cooldown, TimeSpan.FromDays(1), 99));
            Assert.Same(beforeCancel, fixture.Progress);
            workerReturn.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checking.WaitAsync(Deadline));
            Assert.Null(fixture.Progress);
            report(new RequestPolicyProgress(RequestPolicyPhase.Sending, null, 99));
            Assert.Null(fixture.Progress);
            Assert.Null(fixture.OtherProgress);
            Assert.Equal(1, probe.Requests);
        }
        finally
        {
            caller.Cancel();
            workerReturn.Release.TrySetResult();
            await DrainAsync(checking);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetiredReporterAndEqualValuedUnboundContextsCannotTouchSuccessor(bool catalog)
    {
        Gate successorBody = new();
        PluginDescription description = Description();
        using Probe probe = new((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/successor"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new GatedContent(successorBody) } : Ok()));
        using HttpClient transport = new(probe);
        using PluginHostHttp host = new(transport, new RequestPolicyState(), description.Sources);
        Action<RequestPolicyProgress>? retiredReport = null;
        PluginOperationContext? retiredOperation = null, successorOperation = null;
        int calls = 0;
        SourceAdapterTests.TestWorker worker = Worker(description, (_, _, operation, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                retiredReport = CaptureReporter();
                retiredOperation = operation;
                return host.SendAsync(Request(), token);
            }
            successorOperation = operation;
            return host.SendAsync(Request("/successor"), token);
        });
        await using SourceFixture fixture = new(catalog, worker);
        await fixture.CheckAsync().WaitAsync(Deadline);
        Assert.Null(fixture.Progress);
        using CancellationTokenSource caller = new();
        Task successor = fixture.CheckAsync(caller.Token);
        try
        {
            await successorBody.Entered.Task.WaitAsync(Deadline);
            AssertPhase(fixture, RequestPolicyPhase.Sending);
            Assert.Equal(retiredOperation, successorOperation); // same resource + generation, different invocation
            Assert.NotSame(retiredOperation, successorOperation);
            RequestPolicyProgress active = fixture.Progress!;
            retiredReport!(new RequestPolicyProgress(RequestPolicyPhase.Cooldown, TimeSpan.FromDays(1), 99));
            Assert.Same(active, fixture.Progress);
            await Detached(async () =>
            {
                using (Resume(retiredOperation!))
                {
                    Assert.Null(CurrentHistory);
                    Assert.IsType<HostHttpResponse>(await host.SendAsync(Request("/retired")));
                }
                using (Resume(successorOperation! with { }))
                {
                    Assert.Null(CurrentHistory);
                    Assert.IsType<HostHttpResponse>(await host.SendAsync(Request("/unbound")));
                }
                return true;
            }).WaitAsync(Deadline);
            Assert.Same(active, fixture.Progress);
            Assert.Null(fixture.OtherProgress);
            successorBody.Release.TrySetResult();
            await successor.WaitAsync(Deadline);
            Assert.Null(fixture.Progress);
            retiredReport(new RequestPolicyProgress(RequestPolicyPhase.Sending, null, 99));
            Assert.Null(fixture.Progress);
            Assert.Equal(4, probe.Requests);
        }
        finally
        {
            caller.Cancel();
            successorBody.Release.TrySetResult();
            await DrainAsync(successor);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidSdkResultClearsProgressWithoutMaskingValidationError(bool catalog)
    {
        PluginDescription description = Description();
        using Probe probe = new((_, _) => Task.FromResult(Ok()));
        using HttpClient transport = new(probe);
        using PluginHostHttp host = new(transport, new RequestPolicyState(), description.Sources);
        SourceAdapterTests.TestWorker worker = new(async (request, _, token) =>
        {
            await host.SendAsync(Request(), token);
            return new ThreadDeferred(request.Thread with { ThreadId = "999" }, DateTimeOffset.UtcNow.AddHours(1), "cooldown", 429);
        }, description, async (request, _, token) =>
        {
            await host.SendAsync(Request(), token);
            return new CatalogDeferred(request.Board with { BoardId = "other" }, DateTimeOffset.UtcNow.AddHours(1), "cooldown", 429);
        });
        await using SourceFixture fixture = new(catalog, worker);
        PluginWorkerException failure = await Assert.ThrowsAsync<PluginWorkerException>(() => fixture.CheckAsync().WaitAsync(Deadline));
        Assert.Equal("invalid_source_result", failure.Code);
        Assert.Null(fixture.Progress);
        Assert.Null(fixture.SnapshotProgress);
        Assert.Equal(1, probe.Requests);
    }

    [Theory]
    [InlineData(false, "dns")]
    [InlineData(true, "dns")]
    [InlineData(false, "plain-io")]
    [InlineData(true, "plain-io")]
    [InlineData(false, "ended-403")]
    [InlineData(true, "ended-403")]
    [InlineData(false, "unrequested-cancel")]
    [InlineData(true, "unrequested-cancel")]
    public async Task ReturnedSdkFailureUsesSanitizedHostEvidenceAndRetainsSdkCode(bool catalog, string mode)
    {
        Exception bodyError = mode switch
        {
            "dns" => new HttpRequestException(HttpRequestError.NameResolutionError, Secret),
            "plain-io" => new IOException("DNS TLS timeout " + Secret),
            "ended-403" => new HttpIOException(HttpRequestError.ResponseEnded, Secret),
            _ => new OperationCanceledException(Secret)
        };
        PluginDescription description = Description();
        using Probe probe = new((_, _) => Task.FromResult(new HttpResponseMessage(mode == "ended-403" ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
            { Content = new StreamContent(new FailingStream(bodyError)) }));
        using HttpClient transport = new(probe);
        using PluginHostHttp host = new(transport, new RequestPolicyState(), description.Sources);
        SourceAdapterTests.TestWorker worker = Worker(description, async (_, _, _, token) =>
        {
            // The SDK failure still contains its original broad code/raw body failure text.
            return Assert.IsType<HostHttpFailure>(await host.SendAsync(Request("/body?token=" + Secret), token));
        });
        await using SourceFixture fixture = new(catalog, worker);
        FailureInfo failure = await fixture.ObserveFailureAsync();
        Assert.Equal(mode is "dns" or "ended-403" ? FailureKind.Network : FailureKind.Unknown, failure.Kind);
        Assert.Equal(mode == "dns" ? FailureNetworkStage.NameResolution : (FailureNetworkStage?)null, failure.NetworkStage);
        Assert.Equal(mode == "ended-403" ? HttpStatusCode.Forbidden : (HttpStatusCode?)null, failure.HttpStatusCode);
        // Classification comes from the correlated typed observation; the explicit
        // SDK producer code remains visible instead of being replaced by a host code.
        Assert.Equal(mode == "unrequested-cancel" ? "http-timeout" : "http-network", failure.DiagnosticCode);
        Assert.DoesNotContain(Secret, failure.ToString());
        Assert.Equal("Metadata request failed.", failure.Message);
        Assert.Null(fixture.Progress);
        Assert.Null(fixture.SnapshotProgress);
        Assert.Same(failure, fixture.SnapshotFailure);
        Assert.Equal(1, probe.Requests);
        Assert.Equal(0, fixture.MediaRequests);
    }

    private static void AssertPhase(SourceFixture fixture, RequestPolicyPhase phase)
    {
        Assert.NotNull(fixture.Progress);
        Assert.Equal(phase, fixture.Progress!.Phase);
        Assert.Same(fixture.Progress, fixture.SnapshotProgress);
    }

    private static PluginDescription Description()
    {
        PluginDescription description = new FourChanPlugin().Describe();
        return description with { Sources = new[] { description.Sources[0] with
        {
            HttpHosts = new[] { new HttpHostDeclaration("metadata.example", HttpHostPurpose.Metadata),
                new HttpHostDeclaration("second.example", HttpHostPurpose.Metadata) }
        } } };
    }

    private static HostHttpRequest Request(string path = "/metadata") =>
        new("4chan", HostHttpMethod.Get, new Uri("https://metadata.example" + path));
    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK) { Content = new StringContent("{}") };
    private static Task<T> Detached<T>(Func<Task<T>> action)
    {
        using (ExecutionContext.SuppressFlow()) return Task.Run(action);
    }
    private static async Task DrainAsync(Task task)
    {
        try { await task.WaitAsync(Deadline); }
        catch (OperationCanceledException) { }
    }

    private static SourceAdapterTests.TestWorker Worker(PluginDescription description,
        Func<string?, int?, PluginOperationContext, CancellationToken, Task<HostHttpResult>> read, string? path = null) =>
        new(async (request, operation, token) => (await read(null, null, operation, token)) switch
        {
            HostHttpDeferred wait => new ThreadDeferred(request.Thread, wait.RetryAt, wait.ReasonCode, wait.HttpStatusCode),
            HostHttpFailure failure => new ThreadFailure(request.Thread, failure.Failure),
            _ => SourceAdapterTests.Snapshot(request.Thread, "synthetic")
        }, description, async (request, operation, token) => (await read(request.Cursor, request.Page, operation, token)) switch
        {
            HostHttpDeferred wait => new CatalogDeferred(request.Board, wait.RetryAt, wait.ReasonCode, wait.HttpStatusCode),
            HostHttpFailure failure => new CatalogFailure(request.Board, failure.Failure),
            _ => new CatalogSnapshot(new SourceCatalogSnapshot(request.Board, Array.Empty<SourceThreadReference>(),
                !((path is "cursor" or "page") && request.Cursor == null && request.Page == null),
                NextCursor: path == "cursor" && request.Cursor == null ? "next" : null,
                NextPage: path == "page" && request.Page == null ? 2 : null))
        });

    private sealed class SourceFixture : IAsyncDisposable
    {
        private readonly SourceAdapterTests.TemporaryProfile _profile = new();
        private readonly DenyHttp _mediaHandler = new();
        private readonly HttpClient _media;
        private readonly PluginSourceImageBoard _source;
        private readonly DownloadQueue _queue = new(1, 1048576);
        private readonly IThread? _thread, _otherThread;
        private readonly IBoard? _board, _otherBoard;
        private readonly bool _catalog;
        internal SourceFixture(bool catalog, SourceAdapterTests.TestWorker worker)
        {
            _catalog = catalog;
            _media = new(_mediaHandler);
            _source = new(worker.Description, "4chan", _ => Task.FromResult<IPluginWorkerClient>(worker), _media);
            if (catalog)
            {
                _board = _source.GetBoard(new Uri("https://boards.4chan.org/g/"));
                _otherBoard = _source.GetBoard(new Uri("https://boards.4chan.org/pol/"));
            }
            else
            {
                _thread = _source.GetThread(new Uri("https://boards.4chan.org/g/thread/123"));
                _otherThread = _source.GetThread(new Uri("https://boards.4chan.org/g/thread/124"));
            }
        }
        internal string Url => _catalog ? _board!.BoardUri.AbsoluteUri : _thread!.Url;
        internal RequestPolicyProgress? Progress => _catalog ? _board!.RequestProgress : _thread!.RequestProgress;
        internal RequestPolicyProgress? OtherProgress => _catalog ? _otherBoard!.RequestProgress : _otherThread!.RequestProgress;
        internal RequestPolicyProgress? SnapshotProgress => _catalog ? _board!.GetSnapshot(_profile.Path).RequestProgress
            : _thread!.GetSnapshot(new DownloadCounts(0, 0)).RequestProgress;
        internal FailureInfo? Failure => _catalog ? _board!.LastCheckFailure : _thread!.LastCheckFailure;
        internal FailureInfo? SnapshotFailure => _catalog ? _board!.GetSnapshot(_profile.Path).LastCheckFailure
            : _thread!.GetSnapshot(new DownloadCounts(0, 0)).LastCheckFailure;
        internal int MediaRequests => _mediaHandler.Requests;
        internal Task CheckAsync(CancellationToken token = default) => _catalog ? _board!.GetThreadsAsync(token) : _thread!.checkThreadAsync(_queue, token);
        internal Task StopAsync() => _catalog ? _board!.StopMonitoringAsync() : _thread!.StopMonitoringAsync();
        internal void Start()
        {
            if (_catalog) _board!.StartMonitoring(3600);
            else _thread!.StartMonitoring(_queue, _profile.Path, TimeSpan.FromHours(1));
        }
        internal async Task<FailureInfo> ObserveFailureAsync()
        {
            INotifyPropertyChanged target = _catalog ? _board! : _thread!;
            TaskCompletionSource<FailureInfo> reported = new(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed(object? sender, PropertyChangedEventArgs args)
            {
                if (Failure is FailureInfo failure) reported.TrySetResult(failure);
            }
            target.PropertyChanged += Changed;
            try
            {
                Start();
                FailureInfo result = await reported.Task.WaitAsync(Deadline);
                await StopAsync().WaitAsync(Deadline);
                return result;
            }
            finally { target.PropertyChanged -= Changed; }
        }
        public async ValueTask DisposeAsync()
        {
            try { await StopAsync().WaitAsync(Deadline); }
            finally
            {
                try { await _queue.StopAllAsync().WaitAsync(Deadline); }
                finally { _source.Dispose(); _media.Dispose(); _mediaHandler.Dispose(); _profile.Dispose(); }
            }
        }
    }

    private sealed class Gate
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken ReadToken { get; set; }
    }
    private sealed class GatedContent(Gate gate) : HttpContent
    {
        // Model a streaming transport body, not HttpContent's default buffering path,
        // which may serialize through the tokenless overload before returning a stream.
        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new GatedReadStream(gate));
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return CreateContentReadStreamAsync();
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => WriteAsync(stream, CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) => WriteAsync(stream, token);
        private async Task WriteAsync(Stream stream, CancellationToken token)
        {
            await using GatedReadStream body = new(gate);
            await body.CopyToAsync(stream, token);
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
    private sealed class GatedReadStream(Gate gate) : Stream
    {
        private readonly byte[] _payload = "{}"u8.ToArray();
        private int _offset;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (buffer.IsEmpty || _offset == _payload.Length) return 0;
            gate.ReadToken = token;
            gate.Entered.TrySetResult();
            try { await gate.Release.Task.WaitAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                gate.CancellationObserved.TrySetResult();
                throw;
            }
            int count = Math.Min(buffer.Length, _payload.Length - _offset);
            _payload.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class Probe(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Requests);
            return send(request, token);
        }
    }
    private sealed class DenyHttp : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Requests);
            throw new InvalidOperationException("Synthetic source progress tests must never send media HTTP.");
        }
    }
    private sealed class FailingStream(Exception failure) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw failure;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => ValueTask.FromException<int>(failure);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

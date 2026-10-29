using System.Net;
using MeowChan.Archive;
using MeowChan.Boards.Base;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Sources;
using MeowChan.PluginSdk;
using MeowChan.OfficialSources.FourChan;

namespace MeowChan.SourceAdapter.Tests;

/// <summary>
/// MU-05 acceptance through public provider, registry, session and content interfaces.
/// Every restart constructs new owners over a temporary profile; no live HTTP/worker is used.
/// Title is the existing runtime/display contract, not an assumed new raw-title API.
/// </summary>
public sealed class ThreadTitleRecoveryTests
{
    [Theory]
    [InlineData("Captured subject – café 😀")]
    [InlineData("Unknown")]
    [InlineData("Thread 123")]
    public async Task CapturedTitleReachesRuntimeAndRegistrySnapshotAfterOfflineColdRestart(string title)
    {
        using SourceAdapterTests.TemporaryProfile profile = new();
        FixtureSource online = new("title-a", request => Observe(request, title, SourceThreadState.Active, Opening("Different OP fallback")));
        IThread original;
        await using (Session first = await Session.OpenAsync(profile.Path, online))
        {
            original = await first.AddAndSettleAsync(online);
            AssertTitle(first, original, title);
            ArchivedThreadView stored = await first.ReadAsync(online.Id);
            Assert.Equal(title, stored.Summary.Title);
            Assert.NotNull(stored.Summary.LastCapturedAt);
            Assert.Equal("Different OP fallback", Assert.Single(stored.GetPosts()).Html);
        }

        // Keep an active durable watch: completed terminal watches legitimately leave the registry.
        // Restarted source checks fail locally; the title must already be present before that check finishes.
        for (int restart = 0; restart < 2; restart++)
        {
            FixtureSource offline = FixtureSource.Offline("title-a");
            await using Session next = await Session.OpenAsync(profile.Path, offline);
            IThread restored = Assert.Single(next.Registry.Threads);
            Assert.NotSame(original, restored);
            AssertTitle(next, restored, title);
            await next.WaitForOfflineFailureAsync(restored);
            Assert.Equal(1, offline.Reads);
            AssertTitle(next, restored, title);
            Assert.Equal(title, (await next.ReadAsync(offline.Id)).Summary.Title);
            Assert.Equal(0, next.HttpRequests);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public async Task MissingSubjectUsesCleanOpeningPostAndRecoversItOfflineWithoutSavingItAsSubject(string? title)
    {
        const string html = "<p>Opening &amp; café 😀</p>\n<div>second&nbsp; line</div><script>not a title</script>";
        const string expected = "Opening & café 😀 second line";
        using SourceAdapterTests.TemporaryProfile profile = new();
        FixtureSource online = new("title-a", request => Observe(request, title, SourceThreadState.Active,
            // Deliberately put a reply first: response-array order is not opening-post identity.
            new SourcePost("124", 1, SourceFieldPresence.Text, Text: "REPLY MUST NOT WIN"), Opening(html)));
        await using (Session first = await Session.OpenAsync(profile.Path, online))
        {
            IThread thread = await first.AddAndSettleAsync(online);
            AssertTitle(first, thread, expected);
            ArchivedThreadView stored = await first.ReadAsync(online.Id);
            Assert.NotEqual(expected, stored.Summary.Title);
            Assert.Null(stored.GetPosts().Single(post => post.PostId == "123").Subject);
            Assert.Equal(html, stored.GetPosts().Single(post => post.PostId == "123").Html);
        }
        FixtureSource offline = FixtureSource.Offline("title-a");
        await using Session next = await Session.OpenAsync(profile.Path, offline);
        IThread restored = Assert.Single(next.Registry.Threads);
        AssertTitle(next, restored, expected);
        await next.WaitForOfflineFailureAsync(restored);
        AssertTitle(next, restored, expected);
        Assert.NotEqual(expected, (await next.ReadAsync(offline.Id)).Summary.Title);
        Assert.Equal(0, next.HttpRequests);
    }

    [Theory]
    [InlineData("no posts")]
    [InlineData("reply only")]
    [InlineData("whitespace OP")]
    [InlineData("missing OP text")]
    public async Task WithoutOpeningContentUsesSourceBoardThreadIdentityNotAnArbitraryReply(string scenario)
    {
        using SourceAdapterTests.TemporaryProfile profile = new();
        SourcePost[] posts = scenario switch
        {
            "no posts" => Array.Empty<SourcePost>(),
            "reply only" => new[] { new SourcePost("124", 0, SourceFieldPresence.Text, Text: "REPLY MUST NOT WIN") },
            "whitespace OP" => new[] { Opening(" \r\n\t ") },
            _ => new[] { new SourcePost("123", 0, SourceFieldPresence.None) }
        };
        FixtureSource source = new("title-a", request => Observe(request, null, SourceThreadState.Active, posts));
        await using Session session = await Session.OpenAsync(profile.Path, source);
        IThread thread = await session.AddAndSettleAsync(source);
        Assert.False(string.IsNullOrWhiteSpace(thread.Title));
        Assert.NotEqual("Unknown", thread.Title);
        Assert.DoesNotContain("REPLY", thread.Title);
        Assert.Contains(source.Id.Source, thread.Title);
        Assert.Contains(source.Id.Board, thread.Title);
        Assert.Contains(source.Id.Thread!, thread.Title);
        AssertTitle(session, thread, thread.Title);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MediaOnlyAndExplicitlyDisabledCaptureRetainTitleAcrossOfflineRestartWithoutEnablingCapture(bool captureInitially)
    {
        const string title = "Metadata survives without full text";
        using SourceAdapterTests.TemporaryProfile profile = new();
        FixtureSource online = new("title-a", request => Observe(request, title, SourceThreadState.Active, Opening("Private fixture OP")));
        await using (Session first = await Session.OpenAsync(profile.Path, online))
        {
            if (!captureInitially)
                await first.Content.Catalog.RegisterThreadAsync(new ArchiveThreadRegistration(online.Id,
                    online.Url, "", Path.Combine(profile.Path, "media"), false));
            IThread thread = await first.AddAndSettleAsync(online);
            if (captureInitially) await Assert.IsAssignableFrom<BaseThread>(thread).SetCaptureEnabledAsync(false);
            AssertTitle(first, thread, title);
            AssertCaptureOff(first, thread);
            ArchivedThreadView stored = await first.ReadAsync(online.Id);
            Assert.Equal(title, stored.Summary.Title);
            Assert.False(stored.Summary.CaptureEnabled);
            Assert.Equal(captureInitially ? 1 : 0, stored.PostCount);
            Assert.Equal(captureInitially, stored.Summary.LastCapturedAt.HasValue);
        }
        for (int restart = 0; restart < 2; restart++)
        {
            FixtureSource offline = FixtureSource.Offline("title-a");
            await using Session next = await Session.OpenAsync(profile.Path, offline);
            IThread restored = Assert.Single(next.Registry.Threads);
            AssertTitle(next, restored, title);
            AssertCaptureOff(next, restored);
            await next.WaitForOfflineFailureAsync(restored);
            AssertTitle(next, restored, title);
            ArchivedThreadView stored = await next.ReadAsync(offline.Id);
            Assert.Equal(title, stored.Summary.Title);
            Assert.False(stored.Summary.CaptureEnabled);
            Assert.Equal(captureInitially ? 1 : 0, stored.PostCount);
            Assert.Equal(captureInitially, stored.Summary.LastCapturedAt.HasValue);
            Assert.Equal(0, next.HttpRequests);
        }
    }

    [Fact]
    public async Task SameBoardAndThreadOnDifferentSourcesRecoverTheirOwnTitlesOffline()
    {
        using SourceAdapterTests.TemporaryProfile profile = new();
        FixtureSource a = new("title-a", request => Observe(request, "Subject from A", SourceThreadState.Active, Opening("A OP")));
        FixtureSource b = new("title-b", request => Observe(request, "Subject from B", SourceThreadState.Active, Opening("B OP")));
        await using (Session first = await Session.OpenAsync(profile.Path, a, b))
        {
            await first.AddAndSettleAsync(a);
            await first.AddAndSettleAsync(b);
            Assert.Equal(2, first.Registry.Threads.Count);
        }
        FixtureSource offlineA = FixtureSource.Offline("title-a"), offlineB = FixtureSource.Offline("title-b");
        await using Session next = await Session.OpenAsync(profile.Path, offlineA, offlineB);
        Assert.Equal(2, next.Registry.Threads.Count);
        foreach (FixtureSource source in new[] { offlineA, offlineB })
        {
            IThread thread = next.Registry.Threads.Single(thread => thread.Board.ImageBoard.Name == source.Id.Source);
            await next.WaitForOfflineFailureAsync(thread);
            string expected = source == offlineA ? "Subject from A" : "Subject from B";
            AssertTitle(next, thread, expected);
            Assert.Equal(expected, (await next.ReadAsync(source.Id)).Summary.Title);
            Assert.Equal(1, source.Reads);
        }
        Assert.Equal(0, next.HttpRequests);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(" \t\r\n ", false)]
    [InlineData(null, true)]
    public async Task MissingOrFailedObservationCannotOverwriteKnownTrueTitle(string? missingTitle, bool failure)
    {
        using SourceAdapterTests.TemporaryProfile profile = new();
        FixtureSource source = new("title-a", request => Observe(request, "Stored true subject", SourceThreadState.Active, Opening("Old OP")));
        await using Session session = await Session.OpenAsync(profile.Path, source);
        IThread thread = await session.AddAndSettleAsync(source);
        await thread.StopMonitoringAsync();
        source.Response = request => failure ? OfflineFailure(request)
            : Observe(request, missingTitle, SourceThreadState.Active, Opening("New OP is not a subject"));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        if (failure)
            await Assert.ThrowsAsync<PluginWorkerException>(() => thread.checkThreadAsync(session.Queue, timeout.Token));
        else
            await thread.checkThreadAsync(session.Queue, timeout.Token);
        Assert.Equal(2, source.Reads);
        AssertTitle(session, thread, "Stored true subject");
        ArchivedThreadView stored = await session.ReadAsync(source.Id);
        Assert.Equal("Stored true subject", stored.Summary.Title);
        Assert.Equal(failure ? "Old OP" : "New OP is not a subject", Assert.Single(stored.GetPosts()).Html);
    }

    [Fact]
    public async Task LaterRealSubjectReplacesOpeningFallbackInRuntimeAndDurableMetadata()
    {
        using SourceAdapterTests.TemporaryProfile profile = new();
        FixtureSource source = new("title-a", request => Observe(request, null, SourceThreadState.Active, Opening("Opening fallback")));
        await using Session session = await Session.OpenAsync(profile.Path, source);
        IThread thread = await session.AddAndSettleAsync(source);
        AssertTitle(session, thread, "Opening fallback");
        await thread.StopMonitoringAsync();
        source.Response = request => Observe(request, "New actual subject", SourceThreadState.Active, Opening("Opening fallback"));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await thread.checkThreadAsync(session.Queue, timeout.Token);
        Assert.Same(thread, Assert.Single(session.Registry.Threads));
        AssertTitle(session, thread, "New actual subject");
        Assert.Equal("New actual subject", (await session.ReadAsync(source.Id)).Summary.Title);
    }

    private static SourcePost Opening(string text) => new("123", 0, SourceFieldPresence.Text, Text: text);
    private static ThreadReadResult Observe(ThreadReadRequest request, string? title, SourceThreadState state, params SourcePost[] posts)
        => new MeowChan.PluginSdk.ThreadSnapshot(new SourceThreadSnapshot(request.Thread,
            new Uri($"https://{request.Thread.SourceId}.test/g/thread/123"), title, state, posts));
    private static ThreadReadResult OfflineFailure(ThreadReadRequest request)
        => new ThreadFailure(request.Thread, new FailureDetail("fixture_offline", "No network in title acceptance fixture"));

    private static void AssertTitle(Session session, IThread thread, string expected)
    {
        Assert.Equal(expected, thread.Title);
        // Exercise exactly the projection consumed by session/UI clients, not just the archive summary.
        Assert.Equal(expected, session.Registry.GetSnapshot().Threads.Single(snapshot => snapshot.Url == thread.Url).Title);
    }

    private static void AssertCaptureOff(Session session, IThread thread)
    {
        ThreadContentStatus? status = session.Registry.GetSnapshot().Threads.Single(snapshot => snapshot.Url == thread.Url).Content;
        Assert.NotNull(status);
        Assert.False(status.CaptureEnabled);
    }

    private sealed class FixtureSource
    {
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public ResourceId Id { get; }
        public string Url => $"https://{Id.Source}.test/g/thread/123";
        public PluginDescription Description { get; }
        public Func<ThreadReadRequest, ThreadReadResult> Response { get; set; }
        public FixtureSource(string name, Func<ThreadReadRequest, ThreadReadResult> response)
        {
            Id = new ResourceId(name, "g", "123");
            Response = response;
            PluginDescription original = new FourChanPlugin().Describe();
            SourceDescription source = original.Sources[0] with
            {
                SourceId = name, Name = name, BaseUri = new Uri($"https://{name}.test/"),
                Routes = new[] { new UrlRoute(SourceResourceKind.Board, $"{name}.test", "/{board}/", true),
                    new UrlRoute(SourceResourceKind.Thread, $"{name}.test", "/{board}/thread/{thread}", true) },
                HttpHosts = new[] { new HttpHostDeclaration($"{name}.test", HttpHostPurpose.Metadata) }
            };
            Description = original with { PluginId = name + "-plugin", Sources = new[] { source } };
        }
        public static FixtureSource Offline(string name) => new(name, OfflineFailure);
        public IPluginWorkerClient CreateWorker() => new SourceAdapterTests.TestWorker((request, _, _) =>
        {
            Interlocked.Increment(ref _reads);
            return Task.FromResult(Response(request));
        }, Description);
    }

    private sealed class Session : IAsyncDisposable
    {
        private readonly ProfileLease _lease;
        private readonly HttpClient _http;
        private readonly DenyHttp _denyHttp = new();
        private readonly PluginSourceImageBoard[] _adapters;
        public ThreadContentRuntime Content { get; }
        public DownloadQueue Queue { get; } = new(1, 1048576);
        public Registry Registry { get; }
        public int HttpRequests => _denyHttp.Requests;
        private Session(string profile, FixtureSource[] sources)
        {
            _lease = ProfileLease.Acquire(profile);
            Content = new ThreadContentRuntime(profile, _lease);
            _http = new HttpClient(_denyHttp);
            _adapters = sources.Select(source => new PluginSourceImageBoard(source.Description, source.Id.Source,
                _ => Task.FromResult(source.CreateWorker()), _http)).ToArray();
            Registry = new Registry(_adapters, Queue, Path.Combine(profile, "media"), TimeSpan.FromHours(1), 3600,
                new SessionStore(profile, _lease), true, contentRuntime: Content, workStore: new WorkStateStore(profile, _lease));
        }
        public static async Task<Session> OpenAsync(string profile, params FixtureSource[] sources)
        {
            Session session = new(profile, sources);
            try
            {
                await session.Content.InitializeAsync();
                await session.Registry.RestoreAsync();
                return session;
            }
            catch { await session.DisposeAsync(); throw; }
        }
        public async Task<IThread> AddAndSettleAsync(FixtureSource source)
        {
            AdmissionResult result = await Registry.AddAsync(source.Url);
            Assert.Equal(AdmissionStatus.Added, result.Status);
            Assert.NotNull(result.Thread);
            await SettleAsync(result.Thread);
            Assert.Null(result.Thread.LastCheckFailure);
            return result.Thread;
        }
        public async Task SettleAsync(IThread thread) => await SourceAdapterTests.WaitUntilAsync(() =>
            thread.Activity == ThreadActivity.NoMedia || thread.LastCheckFailure != null);
        public async Task WaitForOfflineFailureAsync(IThread thread)
        {
            await SourceAdapterTests.WaitUntilAsync(() => thread.LastCheckFailure != null);
            Assert.Equal("No network in title acceptance fixture", thread.LastCheckFailure!.Message);
            Assert.Equal(ThreadSourceState.Active, thread.SourceState);
        }
        public async Task<ArchivedThreadView> ReadAsync(ResourceId id)
        {
            await using ArchiveReader reader = await Content.OpenReaderAsync();
            return Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(id));
        }
        public async ValueTask DisposeAsync()
        {
            try { await Registry.DisposeAsync(); }
            finally
            {
                try { await Content.DisposeAsync(); }
                finally
                {
                    foreach (PluginSourceImageBoard adapter in _adapters) adapter.Dispose();
                    _http.Dispose();
                    _lease.Dispose();
                }
            }
        }
    }

    private sealed class DenyHttp : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            throw new HttpRequestException("Title tests must never fetch media or a live website", null, HttpStatusCode.ServiceUnavailable);
        }
    }
}

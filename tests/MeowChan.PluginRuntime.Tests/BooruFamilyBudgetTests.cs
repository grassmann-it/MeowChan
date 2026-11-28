using System.Globalization;
using System.Net;
using System.Reflection;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Http;
using MeowChan.Persistence;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Http;
using MeowChan.PluginHost.Secrets;
using MeowChan.PluginHost.Sources.Booru;
using MeowChan.PluginSdk;

namespace MeowChan.PluginRuntime.Tests;

public sealed class BooruFamilyBudgetTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task E621ChallengeIsVisibleAndResettableFromItsSibling()
    {
        ManualClock clock = new();
        RequestPolicyState policy = new(clock);
        SourceDescription e621 = Source("e621", "e621.net");
        SourceDescription e926 = Source("e926", "e926.net");
        bool challenge = true;
        using Transport handler = new(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent(challenge ? "<html>challenge</html>" : "{}", null,
                challenge ? "text/html" : "application/json")
        });
        using HttpClient transport = new(handler);
        using BooruHostHttp firstHost = new(transport, policy, [e621]);
        using BooruHostHttp siblingHost = new(transport, policy, [e926]);
        BooruJob first = new(clock);
        BooruJob sibling = new(clock);
        try
        {
            Assert.Equal("http-challenge", Assert.IsType<HostHttpFailure>(
                await firstHost.SendAsync(Get(e621), e621, first, default).WaitAsync(Bound)).Failure.Code);
            DateTimeOffset expected = clock.GetUtcNow() + BooruHostHttp.MinimumCooldown;
            Assert.True(firstHost.TryGetCooldown("e621", out DateTimeOffset firstUntil));
            Assert.True(siblingHost.TryGetCooldown("e926", out DateTimeOffset siblingUntil));
            Assert.Equal(expected, firstUntil);
            Assert.Equal(expected, siblingUntil);
            Assert.Equal("http-challenge", Assert.IsType<HostHttpFailure>(
                await siblingHost.SendAsync(Get(e926), e926, sibling, default).WaitAsync(Bound)).Failure.Code);
            Assert.Equal(0, sibling.Sends);

            Assert.True(siblingHost.ResetCooldown("e926"));
            Assert.False(firstHost.TryGetCooldown("e621", out _));
            Assert.False(siblingHost.TryGetCooldown("e926", out _));
            challenge = false;
            clock.Advance(BooruHostHttp.MinimumStartSpacing);
            Assert.Equal(200, Assert.IsType<HostHttpResponse>(
                await siblingHost.SendAsync(Get(e926), e926, sibling, default).WaitAsync(Bound)).StatusCode);
            Assert.Equal(1, sibling.Sends);
        }
        finally
        {
            first.Close();
            sibling.Close();
        }
    }

    [Theory]
    [InlineData(429, "application/json")]
    [InlineData(200, "text/html")]
    [InlineData(200, "application/octet-stream")]
    public async Task LateE621ResponseCannotSetFamilyFenceAfterSiteGenerationChanges(int status, string mediaType)
    {
        ManualClock clock = new();
        RequestPolicyState policy = new(clock);
        SourceDescription e621 = Source("e621", "e621.net");
        SourceDescription e926 = Source("e926", "e926.net");
        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        BooruHostHttp.SourceGeneration generation = new(0, 0, ended.Task);
        int checksUntilSwitch = 0;
        using Transport handler = new(request =>
        {
            if (request.RequestUri!.Host == "e926.net") return new(HttpStatusCode.OK);
            checksUntilSwitch = mediaType == "application/octet-stream" ? 2 : 1;
            return new((HttpStatusCode)status)
            {
                Headers = { RetryAfter = new(TimeSpan.FromSeconds(86400)) },
                Content = new StringContent("<html>challenge</html>", null, mediaType)
            };
        });
        using HttpClient transport = new(handler);
        using BooruHostHttp first = new(transport, policy, [e621], maximumCooldownProvider: null,
            sourceAdmission: new(new object(), _ =>
            {
                BooruHostHttp.SourceGeneration observed = generation;
                // The last check before status handling sees the old generation as current.
                if (checksUntilSwitch > 0 && --checksUntilSwitch == 0)
                {
                    generation = new(0, 2, null);
                    ended.SetResult();
                }
                return observed;
            }));
        using BooruHostHttp sibling = new(transport, policy, [e926]);
        BooruJob firstJob = new(clock);
        BooruJob siblingJob = new(clock);
        try
        {
            Assert.Equal("source_unavailable", Assert.IsType<HostHttpFailure>(
                await first.SendAsync(Get(e621), e621, firstJob, default).WaitAsync(Bound)).Failure.Code);
            Assert.True(ended.Task.IsCompletedSuccessfully);
            Assert.Equal(1, firstJob.Sends);
            Assert.True(firstJob.IsOpen);
            Assert.Null(firstJob.CooldownStop);
            Assert.False(sibling.TryGetCooldown("e926", out _));

            clock.Advance(BooruHostHttp.MinimumStartSpacing);
            Assert.Equal(200, Assert.IsType<HostHttpResponse>(
                await sibling.SendAsync(Get(e926), e926, siblingJob, default).WaitAsync(Bound)).StatusCode);
            Assert.Equal(1, siblingJob.Sends);
        }
        finally
        {
            firstJob.Close();
            siblingJob.Close();
        }
    }

    [Fact]
    public void E621FamilyGateIsDisposedAfterItsLastOwnerAndResetFinish()
    {
        RequestPolicyState policy = new();
        using HttpClient transport = new();
        using BooruHostHttp first = new(transport, policy, [Source("e621", "e621.net")]);
        using BooruHostHttp sibling = new(transport, policy, [Source("e926", "e926.net")]);
        var field = typeof(BooruHostHttp).GetField("_familyState", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var family = Assert.IsType<BooruHostHttp.SourceState>(field.GetValue(first));
        Assert.Same(family, field.GetValue(sibling));
        SemaphoreSlim originalGate = family.SendGate;

        first.Dispose();
        Assert.True(sibling.ResetCooldown("e926"));
        using var reset = sibling.BeginCooldownReset("e926");
        Assert.NotNull(reset);
        sibling.Dispose();
        Assert.True(reset.Commit());
        Assert.Throws<ObjectDisposedException>(() => originalGate.Wait(0));

        using BooruHostHttp reopened = new(transport, policy, [Source("e926", "e926.net")]);
        Assert.NotSame(originalGate, family.SendGate);
        Assert.True(reopened.ResetCooldown("e926"));
    }

    [Theory]
    [InlineData("cooldown")]
    [InlineData("challenge")]
    [InlineData("pacing")]
    public async Task ReplacementHostKeepsTheFamilyBudgetOnTheSamePolicy(string fence)
    {
        ManualClock clock = new();
        RequestPolicyState policy = new(clock);
        SourceDescription e621 = Source("e621", "e621.net", spacing: 3000);
        SourceDescription e926 = Source("e926", "e926.net");
        using Transport handler = new(request => request.RequestUri!.Host != "e621.net" ? new(HttpStatusCode.OK) : fence switch
        {
            "cooldown" => new(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(TimeSpan.FromSeconds(90)) } },
            "challenge" => new(HttpStatusCode.OK) { Content = new StringContent("<html>", null, "text/html") },
            _ => new(HttpStatusCode.OK)
        });
        using HttpClient transport = new(handler);
        BooruJob firstJob = new(clock);
        BooruJob reopenedJob = new(clock);
        try
        {
            DateTimeOffset started = clock.GetUtcNow();
            using (BooruHostHttp first = new(transport, policy, [e621]))
            {
                await first.SendAsync(Get(e621), e621, firstJob, default).WaitAsync(Bound);
                Assert.Equal(1, firstJob.Sends);
            }

            using BooruHostHttp reopened = new(transport, policy, [e926]);
            if (fence == "pacing") Assert.False(reopened.TryGetCooldown("e926", out _));
            else
            {
                Assert.True(reopened.TryGetCooldown("e926", out DateTimeOffset until));
                Assert.Equal(started.AddSeconds(fence == "cooldown" ? 90 : 60), until);
                HostHttpResult blocked = await reopened.SendAsync(Get(e926), e926, reopenedJob, default).WaitAsync(Bound);
                if (fence == "cooldown") Assert.Equal(until, Assert.IsType<HostHttpDeferred>(blocked).RetryAt);
                else Assert.Equal("http-challenge", Assert.IsType<HostHttpFailure>(blocked).Failure.Code);
                Assert.Equal(0, reopenedJob.Sends);
                Assert.True(reopened.ResetCooldown("e926"));
            }

            Task<HostHttpResult> pending = reopened.SendAsync(Get(e926), e926, reopenedJob, default);
            await clock.WaitForTimerAsync(TimeSpan.FromSeconds(3), Bound);
            Assert.Equal(0, reopenedJob.Sends);
            clock.Advance(TimeSpan.FromSeconds(3));
            Assert.Equal(200, Assert.IsType<HostHttpResponse>(await pending.WaitAsync(Bound)).StatusCode);
            Assert.Equal(1, reopenedJob.Sends);
        }
        finally
        {
            firstJob.Close();
            reopenedJob.Close();
        }
    }

    [Fact]
    public async Task AccountInvalidatedAtAdmissionDoesNotConsumeSendBudgetOrPacing()
    {
        DirectoryInfo profile = Directory.CreateTempSubdirectory("meowchan-account-admission-");
        try
        {
            using ProfileLease lease = ProfileLease.Acquire(profile.FullName);
            ManualClock clock = new();
            SourceDescription source = Source("danbooru", "danbooru.donmai.us");
            BooruAccountBinding accounts = new(lease, new BooruSecretStoreTests.FakeStore());
            BooruAccountScope scope = BooruAccountScope.Create("danbooru-plugin", "first", source.SourceId, "default", source.BaseUri);
            int sends = 0;
            using Transport handler = new(_ => { sends++; return new(HttpStatusCode.OK); });
            using HttpClient transport = new(handler);
            bool invalidateOnAdmission = false;
            bool firstAdmission = true;
            using BooruHostHttp host = new(transport, new RequestPolicyState(clock), [source],
                maximumCooldownProvider: () =>
                {
                    if (firstAdmission) invalidateOnAdmission = true;
                    return BooruHostHttp.DefaultMaximumCooldown;
                },
                sourceAdmission: new(new object(), _ =>
                {
                    if (invalidateOnAdmission)
                    {
                        invalidateOnAdmission = false;
                        firstAdmission = false;
                        accounts.InvalidateOperations();
                    }
                    return new BooruHostHttp.SourceGeneration(0, 0, null);
                }));
            BooruHostServices services = new(new UnusedServices(), host, accounts, _ => scope);
            BooruJob job = services.OpenJob();
            try
            {
                BooruSearchRequest search = new(source.SourceId, "wolf", 1, 12);
                PluginOperationContext stale = services.BindOperation(job.Operation, search, source, default);
                Assert.Equal("source_unavailable", Assert.IsType<HostHttpFailure>(
                    await services.SendHttpAsync(Get(source), stale, default).WaitAsync(Bound)).Failure.Code);
                Assert.Equal(0, job.Sends);
                Assert.Equal(0, sends);

                PluginOperationContext current = services.BindOperation(job.Operation, search, source, default);
                Assert.Equal(200, Assert.IsType<HostHttpResponse>(
                    await services.SendHttpAsync(Get(source), current, default).WaitAsync(Bound)).StatusCode);
                Assert.Equal(1, job.Sends);
                Assert.Equal(1, sends);
            }
            finally { services.CloseJob(job); }
        }
        finally { profile.Delete(recursive: true); }
    }

    [Fact]
    public async Task SeparateHostsTabsWatcherAndRetryShareSpacingAndRetryAfter()
    {
        ManualClock clock = new();
        RequestPolicyState policy = new(clock);
        SourceDescription e621 = Source("e621", "e621.net", spacing: 3000);
        SourceDescription e926 = Source("e926", "e926.net");
        SourceDescription alias = Source("safe-mirror", "www.e926.net");
        SourceDescription danbooru = Source("danbooru", "danbooru.donmai.us");
        List<(string Host, DateTimeOffset At, string Agent)> seen = [];
        using Transport transportHandler = new(request =>
        {
            seen.Add((request.RequestUri!.Host, clock.GetUtcNow(), request.Headers.UserAgent.ToString()));
            return seen.Count switch
            {
                2 => new(HttpStatusCode.ServiceUnavailable),
                3 => new(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new(TimeSpan.FromSeconds(90)) } },
                _ => new(HttpStatusCode.OK)
            };
        });
        using HttpClient transport = new(transportHandler);
        using BooruHostHttp firstHost = new(transport, policy, [e621, danbooru]);
        using BooruHostHttp secondHost = new(transport, policy, [e926, alias]);
        BooruHostServices firstServices = new(new UnusedServices(), firstHost);
        BooruHostServices secondServices = new(new UnusedServices(), secondHost);
        BooruSearchService firstSearch = new(firstServices);
        BooruSearchService secondSearch = new(secondServices);
        await using EndpointWorker firstWorker = new(firstServices, e621);
        await using EndpointWorker secondWorker = new(secondServices, e926);
        await using EndpointWorker aliasWorker = new(secondServices, alias);
        IDanbooruWatcherSearchPort watcher = new BooruWatcherSearchPort(secondSearch, secondServices,
            _ => Task.FromResult<IPluginWorkerClient?>(aliasWorker), alias.SourceId, clock);
        using IDanbooruWatcherSearchSession check = watcher.OpenCheck();

        DateTimeOffset start = clock.GetUtcNow();
        Assert.IsType<BooruSearchSnapshot>(await firstSearch.SearchAsync(firstWorker, e621.SourceId, "wolf", 1).WaitAsync(Bound));
        Task<BooruSearchResult> tab = secondSearch.SearchAsync(secondWorker, e926.SourceId, "wolf", 1);
        await clock.WaitForTimerAsync(TimeSpan.FromSeconds(3), Bound);
        clock.Advance(TimeSpan.FromSeconds(3));
        await clock.WaitForTimerAsync(TimeSpan.FromSeconds(2), Bound);
        Task<DanbooruWatcherSearchOutcome> watching = check.SearchAsync("wolf", null, default);
        Assert.False(watching.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(2));

        DateTimeOffset until = start.AddSeconds(95);
        BooruSearchDeferred tabResult = Assert.IsType<BooruSearchDeferred>(await tab.WaitAsync(Bound));
        Assert.Equal(429, tabResult.HttpStatusCode);
        Assert.Equal(until, tabResult.RetryAt);
        DanbooruWatcherSearchDeferred watcherResult = Assert.IsType<DanbooruWatcherSearchDeferred>(await watching.WaitAsync(Bound));
        Assert.Equal(until, watcherResult.RetryAtUtc);
        Assert.Equal(until, watcher.GetProviderCooldownUntilUtc());
        Assert.Equal(until, Assert.IsType<BooruSearchDeferred>(
            await firstSearch.SearchAsync(firstWorker, e621.SourceId, "wolf", 1).WaitAsync(Bound)).RetryAt);
        Assert.Equal(new[] { ("e621.net", start), ("e926.net", start.AddSeconds(3)), ("e926.net", start.AddSeconds(5)) },
            seen.Select(entry => (entry.Host, entry.At)).ToArray());
        Assert.All(seen, entry =>
        {
            Assert.StartsWith("MeowChan/", entry.Agent);
            Assert.Contains("https://github.com/grassmann-it/MeowChan/issues", entry.Agent, StringComparison.Ordinal);
        });

        await using EndpointWorker danbooruWorker = new(firstServices, danbooru);
        Assert.IsType<BooruSearchSnapshot>(
            await firstSearch.SearchAsync(danbooruWorker, danbooru.SourceId, "cat", 1).WaitAsync(Bound));
        clock.Advance(TimeSpan.FromSeconds(90));
        Assert.IsType<DanbooruWatcherSearchSnapshot>(await check.SearchAsync("wolf", null, default).WaitAsync(Bound));
        Assert.Equal("www.e926.net", seen[^1].Host);
        Assert.Equal(until, seen[^1].At);
    }

    [Theory]
    [InlineData("danbooru", "danbooru.donmai.us", "cat  -dog\trating:g,s", "cat -dog rating:g,s", 20)]
    [InlineData("e621", "e621.net", "wolf\tblue_sky  artist rating:s", "wolf blue_sky artist rating:s", 12)]
    [InlineData("e926", "e926.net", "wolf\tblue_sky  artist rating:s", "wolf blue_sky artist rating:s", 12)]
    public async Task RestoredWatcherKeepsSourceQueryLimitAndHostCursor(string sourceId, string hostname,
        string input, string canonical, int limit)
    {
        DateTimeOffset now = new(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);
        DirectoryInfo directory = Directory.CreateTempSubdirectory("meowchan-watcher-query-");
        try
        {
            using (ProfileLease lease = ProfileLease.Acquire(directory.FullName))
            {
                BooruCatalogStore catalog = new(lease.ProfileDirectory, lease);
                catalog.Read();
                lease.Admission.CompleteStartup();
                Assert.True(DanbooruWatcher.TryCreate("watcher", input, now, out DanbooruWatcher? created, out _,
                    pageSize: limit, pageBudget: 1, sourceId: sourceId));
                DanbooruWatcher baseline = Observe(created!, 20, isEnd: true);
                DanbooruWatcher partial = Observe(baseline, 100, isEnd: false);
                Assert.Equal("100", partial.CursorPostId);
                catalog.UpsertWatcher(DanbooruWatcherRecordMapper.ToRecord(partial));
            }

            using ProfileLease reopened = ProfileLease.Acquire(directory.FullName);
            BooruCatalogStore store = new(reopened.ProfileDirectory, reopened);
            BooruWatcherRecord record = Assert.Single(store.Read().Watchers);
            DanbooruWatcher restored = DanbooruWatcherRecordMapper.FromRecord(record, sourceId);
            Assert.Equal(sourceId, restored.SourceId);
            Assert.Equal(canonical, restored.CanonicalQuery);
            Assert.Equal(limit, restored.PageSize);
            Assert.Equal("100", restored.CursorPostId);

            ManualClock clock = new();
            SourceDescription source = Source(sourceId, hostname);
            List<Uri> targets = [];
            using Transport transportHandler = new(request =>
            {
                targets.Add(request.RequestUri!);
                return new(HttpStatusCode.OK);
            });
            using HttpClient transport = new(transportHandler);
            using BooruHostHttp host = new(transport, new RequestPolicyState(clock), [source]);
            BooruHostServices services = new(new UnusedServices(), host);
            BooruSearchService search = new(services);
            await using EndpointWorker worker = new(services, source);
            IDanbooruWatcherSearchPort port = new BooruWatcherSearchPort(search, services,
                _ => Task.FromResult<IPluginWorkerClient?>(worker), sourceId, clock);
            using IDanbooruWatcherSearchSession check = port.OpenCheck();

            Assert.Equal(limit, port.PageSize);
            Assert.IsType<BooruSearchSnapshot>(await search.SearchAsync(worker, sourceId, input, page: 1).WaitAsync(Bound));
            BooruSearchFailure denied = Assert.IsType<BooruSearchFailure>(
                await search.SearchAsync(worker, sourceId, canonical + " id:<100", page: 1).WaitAsync(Bound));
            Assert.Equal(BooruSearchService.InvalidQueryCode, denied.Failure.Code);
            Task<DanbooruWatcherSearchOutcome> continuation = check.SearchAsync(restored.CanonicalQuery, restored.CursorPostId, default);
            await clock.WaitForTimerAsync(BooruHostHttp.MinimumStartSpacing, Bound);
            clock.Advance(BooruHostHttp.MinimumStartSpacing);
            Assert.IsType<DanbooruWatcherSearchSnapshot>(await continuation.WaitAsync(Bound));
            string encoded = Uri.EscapeDataString(canonical);
            Assert.Equal(new[] { $"https://{hostname}/posts.json?tags={encoded}&page=1&limit={limit}",
                $"https://{hostname}/posts.json?tags={encoded}&page=b100&limit={limit}" },
                targets.Select(target => target.AbsoluteUri).ToArray());

            IDanbooruWatcherSearchPort disabled = new BooruWatcherSearchPort(search, services,
                _ => Task.FromResult<IPluginWorkerClient?>(null), sourceId, clock);
            using IDanbooruWatcherSearchSession disabledCheck = disabled.OpenCheck();
            Assert.IsType<DanbooruWatcherSearchUnavailable>(await disabledCheck.SearchAsync(canonical, null, default));

            if (sourceId != DanbooruWatcher.ProviderId)
            {
                using BooruHostHttp reduced = new(transport, new RequestPolicyState(clock), [Source(sourceId, hostname, tags: 2)]);
                BooruHostServices reducedServices = new(new UnusedServices(), reduced);
                IDanbooruWatcherSearchPort restricted = new BooruWatcherSearchPort(new BooruSearchService(reducedServices),
                    reducedServices, _ => Task.FromResult<IPluginWorkerClient?>(worker), sourceId, clock);
                using IDanbooruWatcherSearchSession restrictedCheck = restricted.OpenCheck();
                DanbooruWatcherSearchFailed outcome = Assert.IsType<DanbooruWatcherSearchFailed>(
                    await restrictedCheck.SearchAsync(restored.CanonicalQuery, restored.CursorPostId, default).WaitAsync(Bound));
                Assert.Equal("watcher_query_out_of_policy", outcome.Code);
            }
            Assert.Equal(2, targets.Count);
        }
        finally { directory.Delete(recursive: true); }

        DanbooruWatcher Observe(DanbooruWatcher watcher, int id, bool isEnd)
        {
            DanbooruWatcherTransition begin = DanbooruWatcher.BeginCheck(watcher, now);
            Assert.True(begin.Accepted);
            DanbooruWatcherTransition applied = DanbooruWatcher.ApplyPage(begin.Watcher,
                new(watcher.SourceId, watcher.CanonicalQuery, 1, [id.ToString(CultureInfo.InvariantCulture)], isEnd), now);
            Assert.True(applied.Accepted);
            return applied.Watcher;
        }
    }

    private static HostHttpRequest Get(SourceDescription source) => new(source.SourceId, HostHttpMethod.Get,
        new Uri(source.BaseUri, "posts.json?tags=wolf&page=1&limit=12"));

    private static SourceDescription Source(string id, string hostname, int spacing = 1000, int tags = 3) =>
        new(id, id, new($"https://{hostname}/"), SourceCapabilities.PostSearch, [],
            [new(hostname, HttpHostPurpose.Metadata)], new("1", []), PostOrderKind.DecimalPostId)
        {
            BooruProfile = new(
                [
                    new(BooruEndpointKind.Search, "/posts.json", [new("tags", BooruParameterKind.TagQuery, true),
                        new("page", BooruParameterKind.PageOrBeforeCursor, true), new("limit", BooruParameterKind.Limit, true)]),
                    new(BooruEndpointKind.Post, "/posts/{post}.json", [])
                ],
                new(tags, ["s", "q", "e"]), [], [BooruRating.General, BooruRating.Questionable, BooruRating.Explicit], 50, 12,
                MinimumRequestSpacingMilliseconds: spacing)
        };

    private sealed class Transport(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class UnusedServices : IPluginHostServices
    {
        public Task<HostHttpResult> SendHttpAsync(HostHttpRequest request, PluginOperationContext operation, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count,
            PluginOperationContext operation, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class EndpointWorker(BooruHostServices services, SourceDescription source) : IPluginWorkerClient
    {
        public PluginDescription Description { get; } = new("family-fixture", "Fixture", "1", SdkContract.Version, "Fixture", [source], [], new("1", []), []);
        public PluginWorkerIdentity Identity { get; } = new("family-fixture", "1", "1");
        public PluginWorkerSnapshot Snapshot => new(Identity, "ready", null, null, null, []);

        public async Task<BooruSearchResult> SearchBooruPostsAsync(BooruSearchRequest request, PluginOperationContext operation,
            CancellationToken cancellationToken = default)
        {
            string page = request.BeforePostId is null ? request.Page.ToString(CultureInfo.InvariantCulture) : "b" + request.BeforePostId;
            string limit = request.Limit.ToString(CultureInfo.InvariantCulture);
            Uri uri = new(source.BaseUri, $"posts.json?tags={Uri.EscapeDataString(request.Query)}&page={page}&limit={limit}");
            HostHttpResult result = await services.SendHttpAsync(new(request.SourceId, HostHttpMethod.Get, uri), operation, cancellationToken);
            return result switch
            {
                HostHttpResponse { StatusCode: 200 } => new BooruSearchSnapshot(new(request.SourceId, request.Query, request.Page, [], true)),
                HostHttpDeferred deferred => new BooruSearchDeferred(request.SourceId, deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode),
                HostHttpFailure failure => new BooruSearchFailure(request.SourceId, failure.Failure),
                _ => throw new InvalidOperationException("The fixture received an unexpected HTTP result.")
            };
        }

        public Task<CatalogReadResult> ReadCatalogAsync(CatalogReadRequest request, PluginOperationContext operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ThreadReadResult> ReadThreadAsync(ThreadReadRequest request, PluginOperationContext operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ImageFilterResult> EvaluateAsync(ImageFilterRequest request, PluginOperationContext operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RetryAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FieldValidationError>> ValidateSettingsAsync(SettingsValidationRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

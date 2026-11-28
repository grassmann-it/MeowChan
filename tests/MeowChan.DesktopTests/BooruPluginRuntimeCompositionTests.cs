using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Updates;
using MeowChan.Downloader;
using MeowChan.Persistence;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Installation;
using MeowChan.PluginHost.Secrets;
using MeowChan.PluginHost.Sources.Booru;
using MeowChan.PluginSdk;
using MeowChan.Secrets;
using MeowChan.Tests.Shared;
using Microsoft.Extensions.Time.Testing;

namespace MeowChan.DesktopTests;

// G1 through the production plugin runtime: the real PluginSession compiles/describes the Danbooru plugin, starts it
// in a real worker process with the booru host services, and the session composes from that binding. Until the
// plugin is delivered with the app (B8 packaging), it is installed as the user would: Danbooru.cs in the profile's
// plugins folder. All traffic goes to the in-memory DanbooruStub through the session's transport seam.
[Collection("Delivered plugin hosts")]
public sealed class BooruPluginRuntimeCompositionTests
{
    private const string PluginId = "meowchan.official.danbooru";
    private const string DanbooruManifest = "MeowChan.Plugin.Danbooru.plugin.json";
    internal const string ReleasedProfileVariable = "MEOWCHAN_RELEASED110_DANBOORU_PROFILE";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(180);

    [Fact]
    public async Task PluginSessionShutsDownWhenTheOptionalTransportFactoryReturnsNull()
    {
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        Config config = new();
        DownloadQueue queue = new(config.MaxParallelDownloads, config.BandwidthLimiter);
        try
        {
            PluginSession session = new(profile.Path, lease, config, queue, Serilog.Core.Logger.None,
                transportFactory: static () => null!);
            await session.DisposeAsync();
        }
        finally { await queue.StopAllAsync(); }
    }

    [Fact]
    public async Task PluginSessionShutdownCancelsAnonymousDanbooruRetryWithoutAdvancingTime()
    {
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        using AccountTransport network = new(retryFirst: true);
        FakeTimeProvider clock = new();
        StageDanbooru(profile.Path);
        Config config = new();
        DownloadQueue queue = new(config.MaxParallelDownloads, config.BandwidthLimiter);
        try
        {
            await using PluginSession session = new(profile.Path, lease, config, queue, Serilog.Core.Logger.None,
                transportFactory: network.Client, httpClock: clock);
            await session.StartAsync(CancellationToken.None).WaitAsync(Deadline);
            BooruProviderBinding binding = Assert.IsType<BooruProviderBinding>(session.Booru);
            BooruJob job = binding.Services.OpenJob();
            try
            {
                DateTimeOffset started = clock.GetUtcNow();
                BooruSearchRequest search = new(binding.Source.SourceId, "cat", 1, 10);
                PluginOperationContext context = binding.Services.BindOperation(job.Operation, search, binding.Source, CancellationToken.None);
                Assert.Null(context.BooruCall!.Account);
                Task<HostHttpResult> pending = binding.Services.SendHttpAsync(new(search.SourceId, HostHttpMethod.Get,
                    new(binding.Source.BaseUri, "posts.json?tags=cat&page=1&limit=10")), context, CancellationToken.None);
                Assert.Equal("danbooru.donmai.us", Assert.Single(network.Requests).Url.Host);
                Assert.False(pending.IsCompleted);

                TimeSpan bound = TimeSpan.FromSeconds(10);
                await session.DisposeAsync().AsTask().WaitAsync(bound);
                Assert.Equal("source_unavailable", Assert.IsType<HostHttpFailure>(await pending.WaitAsync(bound)).Failure.Code);
                Assert.Equal(started, clock.GetUtcNow());
                Assert.True(job.IsOpen);
                Assert.Equal(1, job.Sends);
                Assert.Single(network.Requests);
            }
            finally { binding.Services.CloseJob(job); }
        }
        finally { await queue.StopAllAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AConfirmedDanbooruAccountIsSentOnlyWithTheDesktopOptIn(bool enabled)
    {
        const string accountSwitch = "MeowChan.EnableBooruAccountSending";
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        using AccountTransport network = new();
        FakeTimeProvider clock = new();
        StageDanbooru(profile.Path);
        Config config = new();
        DownloadQueue queue = new(config.MaxParallelDownloads, config.BandwidthLimiter);
        try
        {
            await using PluginSession session = new(profile.Path, lease, config, queue, Serilog.Core.Logger.None,
                transportFactory: network.Client, httpClock: clock);
            AccountStore store = new();
            AppContext.TryGetSwitch(accountSwitch, out bool previous);
            try
            {
                AppContext.SetSwitch(accountSwitch, enabled);
                session.ConfigureBooruAccounts(lease, () => store);
            }
            finally { AppContext.SetSwitch(accountSwitch, previous); }
            await session.StartAsync(CancellationToken.None).WaitAsync(Deadline);

            BooruProviderBinding binding = Assert.IsType<BooruProviderBinding>(session.Booru);
            string revision = Assert.IsType<string>(session.RunningExecutionRevision(PluginId));
            BooruAccountScope scope = BooruAccountScope.Create(PluginId, revision, binding.Source.SourceId,
                BooruPostKey.DefaultInstance, binding.Source.BaseUri);
            BooruAccountBinding selection = new(lease, store);
            Assert.Equal(BooruAccountState.Ready,
                (await selection.ConfirmAsync(scope, "fixture-user", "fixture-key"u8.ToArray())).State);

            var worker = await binding.GetWorkerAsync(CancellationToken.None).WaitAsync(Deadline);
            Assert.NotNull(worker);
            BooruSearchService search = new(binding.Services);
            Assert.IsType<BooruSearchSnapshot>(await search.SearchAsync(worker, binding.Source.SourceId, "cat", 1).WaitAsync(Deadline));
            var request = Assert.Single(network.Requests);
            Assert.Equal("danbooru.donmai.us", request.Url.Host);
            Assert.Equal(enabled ? new AuthenticationHeaderValue("Basic", Convert.ToBase64String("fixture-user:fixture-key"u8)) : null,
                request.Authorization);
            Assert.Equal(enabled ? 1 : 0, store.Reads);

            BooruJob job = binding.Services.OpenJob();
            try
            {
                BooruSearchRequest searchRequest = new(binding.Source.SourceId, "cat", 1, 10);
                PluginOperationContext context = binding.Services.BindOperation(job.Operation, searchRequest,
                    binding.Source, CancellationToken.None);
                if (!enabled)
                {
                    Assert.Null(context.BooruCall!.Account);
                    return;
                }

                BooruAccountBinding.Operation operation = Assert.IsType<BooruAccountBinding.Operation>(context.BooruCall!.Account);
                Assert.True(operation.IsCurrent());
                Assert.False(operation.GenerationEnded.IsCompleted);
                clock.Advance(TimeSpan.FromSeconds(1));
                Task arrived = network.HoldNextResponse();
                Task<HostHttpResult> inFlight = binding.Services.SendHttpAsync(new(binding.Source.SourceId, HostHttpMethod.Get,
                    new(binding.Source.BaseUri, "posts.json?tags=cat&page=1&limit=10")), context, CancellationToken.None);
                TimeSpan bound = TimeSpan.FromSeconds(10);
                await arrived.WaitAsync(bound);
                Assert.Equal(request.Authorization, network.Requests.Last().Authorization);
                Assert.False(inFlight.IsCompleted);

                ValueTask disposal = session.DisposeAsync();
                try
                {
                    Assert.True(lease.IsHeld);
                    Assert.False(operation.IsCurrent());
                    Assert.True(operation.GenerationEnded.IsCompletedSuccessfully);
                    Assert.Equal("source_unavailable", Assert.IsType<HostHttpFailure>(await inFlight.WaitAsync(bound)).Failure.Code);
                }
                finally { await disposal; }
            }
            finally { binding.Services.CloseJob(job); }
        }
        finally { await queue.StopAllAsync(); }
    }

    [Theory]
    [InlineData(PluginId, "danbooru.donmai.us", true)]
    [InlineData(PluginId, "other.invalid", false)]
    [InlineData("fixture.local.plugin", "danbooru.donmai.us", false)]
    [InlineData("fixture.local.plugin", "other.invalid", false)]
    public void DanbooruAccountScopeRequiresTheOfficialPluginAndCanonicalMetadataOrigin(string pluginId, string host, bool admitted)
    {
        SourceDescription source = new("danbooru", "Danbooru", new($"https://{host}/"), SourceCapabilities.PostSearch,
            [], [new(host, HttpHostPurpose.Metadata)], new("1", []), PostOrderKind.DecimalPostId);

        BooruAccountScope? scope = PluginSession.CreateBooruAccountScope(pluginId, "revision", source);

        if (admitted) Assert.Equal("https://danbooru.donmai.us", Assert.IsType<BooruAccountScope>(scope).Origin);
        else Assert.Null(scope);
    }

    [Fact]
    public async Task TheRealPluginWorkerServesTheWindowAndADisabledPluginComposesNothingAfterRestart()
    {
        using TempDirectory profile = new();
        using DanbooruStub stub = new();
        DanbooruStubPost post = stub.Add(new(7001, Color: 0xFFE0A030));
        stub.Publish("cat_ears", post.Id);
        Configure(profile.Path); // a fresh profile directory first gets its settings, then the local plugin
        StageDanbooru(profile.Path);

        DesktopSessionHost host = Create(profile.Path, stub);
        try
        {
            await host.StartAsync().WaitAsync(Deadline);
            Assert.True(host.State == DesktopSessionState.Ready, host.StartupError);
            Assert.NotNull(host.Plugins!.Booru);
            Assert.DoesNotContain(host.Plugins.Sources, source => source.Name == "danbooru"); // never a chan board adapter
            PluginProfileSnapshot plugins = await host.Plugins.ProfileStore.ReadAsync();
            PluginActivationSnapshot danbooru = Assert.Single(plugins.Plugins, plugin => plugin.PluginId == PluginId);
            Assert.NotNull(danbooru.Active);
            Assert.True(danbooru.Enabled);
            Assert.False(host.Plugins.DanbooruStartupRepairRequired);
            await BooruCompositionHostTests.WaitAsync(() => host.BooruServices != null, "the composition from the real plugin");
            Assert.Empty(stub.Requests);
            BooruWorkbenchServices services = host.BooruServices!;
            BooruWorkspaceContent content = Assert.IsType<BooruWorkspaceContent>(host.BooruContent);
            await host.SaveBooruContentAsync(new(content.Snapshot, BooruPostClaimRating.General, ""));

            BooruCallResult<BooruResultPage> result = await services.Search.SearchAsync("cat_ears", 1, BooruSearchModel.PageSize, CancellationToken.None).WaitAsync(Deadline);
            Assert.True(result.Status == BooruCallStatus.Ok, result.Status + " " + result.Code);
            BooruPostView found = Assert.Single(result.Value!.Posts);
            Assert.Equal("7001", found.PostId);
            Assert.Contains(host.Plugins.Runtime.GetSnapshots(), worker => worker.ProcessId is > 0);
            Assert.Equal(post.Preview, await services.Previews.LoadPreviewAsync(found, CancellationToken.None).WaitAsync(Deadline));
            Assert.Equal(BooruLoadStatus.Queued,
                (await services.Queue.LoadOriginalAsync(found, BooruLoadOrigin.Search, CancellationToken.None).WaitAsync(Deadline)).Status);
            await BooruCompositionHostTests.WaitAsync(async () => (await services.Queue.GetLocalStatesAsync(new[] { "7001" }, CancellationToken.None))
                .GetValueOrDefault("7001") == BooruLocalState.Loaded, "the original from the real worker path");
            Assert.Equal(1, stub.OriginalRequests);
            Assert.Equal(1, stub.MetadataRequests);
            Assert.Empty(stub.Violations);

            // Disabling the plugin hides the window at once and stops the worker path.
            plugins = await host.Plugins.ProfileStore.ReadAsync();
            await host.Plugins.ProfileStore.SetEnabledAsync(PluginId, false, plugins.Revision);
            Assert.DoesNotContain(host.ActiveBooruServices, active => active.Search.ProviderId == services.Search.ProviderId);
            Assert.Equal(BooruCallStatus.Unavailable,
                (await services.Search.SearchAsync("cat_ears", 1, BooruSearchModel.PageSize, CancellationToken.None).WaitAsync(Deadline)).Status);
            Assert.Equal(1, stub.MetadataRequests);
        }
        finally { await StopAsync(host); }

        DesktopSessionHost restarted = Create(profile.Path, stub);
        try
        {
            await restarted.StartAsync().WaitAsync(Deadline);
            Assert.True(restarted.State == DesktopSessionState.Ready, restarted.StartupError);
            Assert.NotNull(restarted.Plugins!.Booru);
            await Task.Delay(500);
            Assert.Null(restarted.BooruComposition);
            Assert.DoesNotContain(restarted.ActiveBooruServices, active => active.Search.ProviderId == "danbooru");
            Assert.Equal(1, stub.MetadataRequests);
        }
        finally { await StopAsync(restarted); }
    }

    [Released110DanbooruProfileFact]
    public async Task ReleasedDanbooruMovesToTheCurrentDeliveredBuildAndKeepsTheOldBuildForRollback()
    {
        string releasedProfile = Environment.GetEnvironmentVariable(ReleasedProfileVariable)!;
        var releasedStore = new PluginProfileStore(releasedProfile);
        PluginProfileSnapshot released = await releasedStore.ReadAsync();
        PluginActivationSnapshot old = Danbooru(released);
        PluginReleaseSnapshot prior = Assert.IsType<PluginReleaseSnapshot>(old.Active);
        string oldManifest = Assert.Single(old.Candidates, item => item.CandidateId == prior.CandidateId).CandidatePath!;
        Assert.Equal(DanbooruManifest, Path.GetFileName(oldManifest));
        Assert.Equal(ManifestAssemblyHash(oldManifest), prior.Artifact.AssemblySha256);
        Assert.True(File.Exists(prior.Artifact.AssemblyPath), prior.Artifact.AssemblyPath);
        Assert.Null(old.Pinned);
        Assert.Empty(released.Grants);
        Assert.True(old.Enabled);

        string newManifest = Path.Combine(AppContext.BaseDirectory, "plugins", DanbooruManifest);
        string assemblyHash = ManifestAssemblyHash(newManifest);
        Assert.NotEqual(oldManifest, newManifest);
        Assert.NotEqual(prior.Artifact.AssemblySha256, assemblyHash);
        using TempDirectory profile = new();
        using DanbooruStub stub = new();
        Configure(profile.Path);
        var store = new PluginProfileStore(profile.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(store.StorePath)!);
        File.Copy(releasedStore.StorePath, store.StorePath);

        string? revision = null;
        for (int start = 0; start < 2; start++)
        {
            DesktopSessionHost host = Create(profile.Path, stub);
            try
            {
                await host.StartAsync().WaitAsync(Deadline);
                Assert.True(host.State == DesktopSessionState.Ready, host.StartupError);
                await BooruCompositionHostTests.WaitAsync(() => host.BooruServices != null, "Booru services after the packaged upgrade");
                PluginActivationSnapshot current = Danbooru(await host.Plugins!.ProfileStore.ReadAsync());
                PluginReleaseSnapshot active = Assert.IsType<PluginReleaseSnapshot>(current.Active);
                Assert.Equal(newManifest, Assert.Single(current.Candidates, item => item.CandidateId == active.CandidateId).CandidatePath);
                Assert.Equal(assemblyHash, active.Artifact.AssemblySha256);
                Assert.Equal(active.CandidateId, current.SelectedCandidateId);
                Assert.NotEqual(prior.ExecutionRevision, active.ExecutionRevision);
                Assert.Equal(revision ??= active.ExecutionRevision, active.ExecutionRevision);
                Assert.Equal(prior.ExecutionRevision, current.LastGood!.ExecutionRevision);
                Assert.Equal(SdkJson.Serialize(prior.Description), SdkJson.Serialize(active.Description));
                Assert.Equal(SdkJson.Serialize(prior.Configuration), SdkJson.Serialize(active.Configuration));
                Assert.Null(current.Pinned);
                Assert.False(host.Plugins.DanbooruStartupRepairRequired);
                Assert.Empty(stub.Requests);
            }
            finally { await StopAsync(host); }
        }

        PluginProfileSnapshot upgraded = await store.ReadAsync();
        await store.SelectRollbackAsync(PluginId, upgraded.Revision);
        DesktopSessionHost rolledBack = Create(profile.Path, stub);
        try
        {
            await rolledBack.StartAsync().WaitAsync(Deadline);
            Assert.True(rolledBack.State == DesktopSessionState.Ready, rolledBack.StartupError);
            PluginActivationSnapshot current = Danbooru(await rolledBack.Plugins!.ProfileStore.ReadAsync());
            Assert.Equal(prior.ExecutionRevision, current.Active!.ExecutionRevision);
            Assert.Equal(prior.ExecutionRevision, current.Pinned!.ExecutionRevision);
            Assert.Empty(stub.Requests);
        }
        finally { await StopAsync(rolledBack); }
    }

    private static PluginActivationSnapshot Danbooru(PluginProfileSnapshot state) =>
        Assert.Single(state.Plugins, plugin => plugin.PluginId == PluginId);

    private static void StageDanbooru(string profile)
    {
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "plugins", DanbooruManifest))) return;
        string plugins = Path.Combine(profile, "plugins");
        Directory.CreateDirectory(plugins);
        File.Copy(DanbooruTestPlugin.SourcePath, Path.Combine(plugins, "Danbooru.cs"));
    }

    private static string ManifestAssemblyHash(string file)
    {
        Assert.True(File.Exists(file), file);
        return JsonSerializer.Deserialize<PluginBundleManifest>(File.ReadAllBytes(file))!.AssemblySha256;
    }

    private static void Configure(string profile) => new Config
    {
        DownloadPath = Path.Combine(profile, "media"), DownloadsPaused = false, SaveUrlsOnExit = true,
        AutomaticUpdateChecks = false, MinimizeToTray = false, CloseToTray = false,
        MaxParallelDownloads = 1, RecheckTimer = 3600, MinimumFreeSpaceGiB = 0
    }.SaveConfig(profile);

    private static DesktopSessionHost Create(string profile, DanbooruStub stub)
    {
        Directory.CreateDirectory(Path.Combine(profile, "media"));
        return new DesktopSessionHost(profile, providers: null,
            updateSessionFactory: (path, config) => new UpdateSession(path, config, stub.Client(), disableEnvironment: () => "1"),
            pluginTransportFactory: stub.Client);
    }

    private static async Task StopAsync(DesktopSessionHost host)
    {
        await host.StopAsync().WaitAsync(Deadline);
        await host.DisposeAsync();
    }

    private sealed class AccountTransport(bool retryFirst = false) : HttpMessageHandler
    {
        private TaskCompletionSource? _holdNextResponse;
        private int _retry = retryFirst ? 1 : 0;
        internal System.Collections.Concurrent.ConcurrentQueue<(Uri Url, AuthenticationHeaderValue? Authorization)> Requests { get; } = new();
        internal HttpClient Client() => new(this, disposeHandler: false);
        internal Task HoldNextResponse() => (_holdNextResponse = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue((request.RequestUri!, request.Headers.Authorization));
            if (Interlocked.Exchange(ref _retry, 0) == 1)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    { Headers = { RetryAfter = new(TimeSpan.FromSeconds(30)) } };
            if (Interlocked.Exchange(ref _holdNextResponse, null) is { } held)
            {
                held.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", null, "application/json") };
        }
    }

    private sealed class AccountStore : IBooruSecretStore
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _secrets = new();
        internal int Reads;

        public SecretStoreStatus Write(string target, byte[] secret, CancellationToken cancellationToken)
        {
            _secrets[target] = secret.ToArray();
            return SecretStoreStatus.Available;
        }

        public SecretStoreRead Read(string target, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Reads);
            return _secrets.TryGetValue(target, out byte[]? secret)
                ? new(SecretStoreStatus.Available, secret.ToArray()) : new(SecretStoreStatus.Missing);
        }

        public SecretStoreStatus Delete(string target, CancellationToken cancellationToken) =>
            _secrets.TryRemove(target, out _) ? SecretStoreStatus.Available : SecretStoreStatus.Missing;
    }
}

public sealed class Released110DanbooruProfileFactAttribute : FactAttribute
{
    public Released110DanbooruProfileFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(BooruPluginRuntimeCompositionTests.ReleasedProfileVariable)))
            Skip = "Requires a fresh profile created by the v1.1.0 release and a staged current Danbooru bundle.";
    }
}

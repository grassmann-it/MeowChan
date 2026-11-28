using System.Collections.Concurrent;
using System.Net;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Updates;
using MeowChan.Downloader;
using MeowChan.Persistence;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Sources.Booru;
using MeowChan.PluginSdk;
using Microsoft.Extensions.Time.Testing;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class BooruSourceActivationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task E621SiteSwitchCancelsPendingRetryAndReleasesTheFamilyGate(bool reenable)
    {
        TimeSpan bound = TimeSpan.FromSeconds(10);
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        using CountingTransport network = new(retryFirstE621: true);
        FakeTimeProvider clock = new();
        await StageE621FamilyAsync(profile.Path);

        Config config = new();
        DownloadQueue queue = new(config.MaxParallelDownloads, config.BandwidthLimiter);
        try
        {
            await using PluginSession session = new(profile.Path, lease, config, queue, Serilog.Core.Logger.None,
                transportFactory: network.Client, httpClock: clock);
            await session.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(180));
            Assert.True(session.SetBooruSiteEnabled("e621", true));
            BooruProviderBinding e621 = session.BooruSources["e621"];
            BooruProviderBinding e926 = session.BooruSources["e926"];
            BooruJob firstJob = e621.Services.OpenJob();
            BooruJob siblingJob = e926.Services.OpenJob();
            try
            {
                DateTimeOffset started = clock.GetUtcNow();
                Task<HostHttpResult> first = Send(e621, firstJob);
                Assert.Equal("e621.net", Assert.Single(network.Hosts));
                Assert.False(first.IsCompleted);
                Task<HostHttpResult> sibling = Send(e926, siblingJob);
                Assert.False(sibling.IsCompleted);
                clock.Advance(TimeSpan.FromSeconds(1)); // Normal family pacing, with 29s left on the retry timer.

                Assert.True(session.SetBooruSiteEnabled("e621", false));
                if (reenable) Assert.True(session.SetBooruSiteEnabled("e621", true));

                Assert.Equal("source_unavailable", Assert.IsType<HostHttpFailure>(await first.WaitAsync(bound)).Failure.Code);
                Assert.Equal(200, Assert.IsType<HostHttpResponse>(await sibling.WaitAsync(bound)).StatusCode);
                Assert.Equal(started.AddSeconds(1), clock.GetUtcNow());
                clock.Advance(TimeSpan.FromSeconds(30));
                Assert.Equal(new[] { "e621.net", "e926.net" }, network.Hosts);
                Assert.Equal(1, firstJob.Sends);
                Assert.Equal(1, siblingJob.Sends);
            }
            finally
            {
                e621.Services.CloseJob(firstJob);
                e926.Services.CloseJob(siblingJob);
            }
        }
        finally { await queue.StopAllAsync(); }

        static Task<HostHttpResult> Send(BooruProviderBinding binding, BooruJob job)
        {
            BooruSearchRequest search = new(binding.Source.SourceId, "cat", 1, 10);
            PluginOperationContext context = binding.Services.BindOperation(job.Operation, search, binding.Source, CancellationToken.None);
            return binding.Services.SendHttpAsync(new(search.SourceId, HostHttpMethod.Get,
                new(binding.Source.BaseUri, "posts.json?tags=cat&page=1&limit=10")), context, CancellationToken.None);
        }
    }

    [Fact]
    public async Task BoundE621OperationKeepsItsGenerationBeforeTheFirstHttpCallback()
    {
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        using CountingTransport network = new(retryFirstE621: true);
        FakeTimeProvider clock = new();
        await StageE621FamilyAsync(profile.Path);
        Config config = new();
        DownloadQueue queue = new(config.MaxParallelDownloads, config.BandwidthLimiter);
        try
        {
            await using PluginSession session = new(profile.Path, lease, config, queue, Serilog.Core.Logger.None,
                transportFactory: network.Client, httpClock: clock);
            await session.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(180));
            Assert.True(session.SetBooruSiteEnabled("e621", true));
            BooruProviderBinding e621 = session.BooruSources["e621"];
            BooruHostServices services = session.BooruSources["e926"].Services;
            BooruJob job = services.OpenJob();
            try
            {
                BooruSearchRequest search = new("e621", "cat", 1, 10);
                HostHttpRequest request = new("e621", HostHttpMethod.Get,
                    new(e621.Source.BaseUri, "posts.json?tags=cat&page=1&limit=10"));
                PluginOperationContext stale = services.BindOperation(job.Operation, search, e621.Source, CancellationToken.None);
                Assert.True(session.SetBooruSiteEnabled("e621", false));
                PluginOperationContext boundWhileOff = services.BindOperation(job.Operation, search, e621.Source, CancellationToken.None);
                Assert.True(session.SetBooruSiteEnabled("e621", true));

                TimeSpan bound = TimeSpan.FromSeconds(10);
                Assert.Equal("source_unavailable", Assert.IsType<HostHttpFailure>(
                    await services.SendHttpAsync(request, stale, CancellationToken.None).WaitAsync(bound)).Failure.Code);
                Assert.Equal("source_unavailable", Assert.IsType<HostHttpFailure>(
                    await services.SendHttpAsync(request, boundWhileOff, CancellationToken.None).WaitAsync(bound)).Failure.Code);
                Assert.Empty(network.Hosts);
                Assert.Equal(0, job.Sends);

                PluginOperationContext current = services.BindOperation(job.Operation, search, e621.Source, CancellationToken.None);
                Task<HostHttpResult> pending = services.SendHttpAsync(request, current, CancellationToken.None);
                Assert.Equal("e621.net", Assert.Single(network.Hosts));
                Assert.False(pending.IsCompleted);
                Assert.True(session.SetBooruSiteEnabled("e621", true));
                clock.Advance(TimeSpan.FromSeconds(30));
                Assert.Equal(200, Assert.IsType<HostHttpResponse>(await pending.WaitAsync(bound)).StatusCode);
                Assert.Equal(new[] { "e621.net", "e621.net" }, network.Hosts);
                Assert.Equal(2, job.Sends);
            }
            finally { services.CloseJob(job); }
        }
        finally { await queue.StopAllAsync(); }
    }

    [Fact]
    public void SiteSwitchesControlTransportAndPersistAcrossTabRestore() => BooruComponentDispatcher.Run(async () =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using TempDirectory profile = new();
        using CountingTransport network = new();
        string plugins = Path.Combine(profile.Path, "plugins");
        string media = Path.Combine(profile.Path, "media");
        new Config
        {
            DownloadPath = media, FirstRunSetupVersion = 1, AutomaticUpdateChecks = false,
            RestoreBooruSearchTabs = true, WorkspaceMode = "Advanced"
        }.SaveConfig(profile.Path);
        Directory.CreateDirectory(plugins);
        Directory.CreateDirectory(media);
        List<string> fixtureSources = new();
        string delivered = Path.Combine(AppContext.BaseDirectory, "plugins");
        if (!File.Exists(Path.Combine(delivered, "MeowChan.Plugin.E621.plugin.json")))
            fixtureSources.Add("DescribeSource(\"e621\", \"e621.net\"), DescribeSource(\"e926\", \"e926.net\")");
        if (!File.Exists(Path.Combine(delivered, "MeowChan.Plugin.Gelbooru.plugin.json")))
            fixtureSources.Add("DescribeSource(\"gelbooru\", \"gelbooru.com\"), DescribeSource(\"safebooru\", \"safebooru.org\")");
        if (fixtureSources.Count > 0)
            await File.WriteAllTextAsync(Path.Combine(plugins, "TestBooru.cs"),
                FixturePlugin.Replace("SOURCE_DECLARATIONS", string.Join(", ", fixtureSources), StringComparison.Ordinal));

        DesktopSessionHost Create() => new(profile.Path, providers: null,
            updateSessionFactory: (path, config) => new UpdateSession(path, config, network.Client(), disableEnvironment: () => "1"),
            pluginTransportFactory: network.Client);

        await using (DesktopSessionHost host = Create())
        await using (MainWindow window = new(host))
        {
            window.Show();
            window.PlatformImpl!.Activated?.Invoke();
            await window.StartupTimingRestoreTask;
            BooruReviewTestPlatform.Resize(window, 1920);
            await WaitForSourceAsync(host, "safebooru");
            Assert.Contains(host.ActiveBooruServices, services => services.Search.ProviderId == "e926");
            Assert.DoesNotContain(host.ActiveBooruServices, services => services.Search.ProviderId is "e621" or "gelbooru");
            Assert.Empty(network.Hosts);

            await SearchFromSiblingWorkerAsync(host, "e926", "e621");
            await SearchFromSiblingWorkerAsync(host, "safebooru", "gelbooru");
            Assert.Empty(network.Hosts);

            BooruWorkspaceContent content = Assert.IsType<BooruWorkspaceContent>(host.BooruContent);
            await host.SaveBooruContentAsync(new(BooruPostClaimRating.Sensitive, "", []), content.Snapshot.Revision);
            Button navigation = FindButton(window, "SourcesNavigationButton");
            await BooruTestSupport.WaitAsync(() => navigation.IsEffectivelyVisible && navigation.IsEffectivelyEnabled, "Plugins navigation");
            Click(navigation);
            window.UpdateLayout();
            SourcesPage sources = Assert.Single(window.GetVisualDescendants().OfType<SourcesPage>(), page => page.IsEffectivelyVisible);
            await sources.ActivateAsync();
            await ToggleAsync(sources, "e621", enable: true);
            await WaitForSourceAsync(host, "e621");
            BooruWorkbenchServices e621 = Source(host, "e621");
            Assert.Equal(BooruCallStatus.Ok, (await e621.Search.SearchAsync("cat", 1, 10, CancellationToken.None)).Status);
            Assert.Equal("e621.net", Assert.Single(network.Hosts));

            await ToggleAsync(sources, "gelbooru", enable: true);
            await WaitForSourceAsync(host, "gelbooru");
            BooruWorkbenchServices gelbooru = Source(host, "gelbooru");
            Assert.Equal(BooruCallStatus.Ok, (await gelbooru.Search.SearchAsync("cat", 1, 10, CancellationToken.None)).Status);
            Assert.Equal(new[] { "e621.net", "gelbooru.com" }, network.Hosts);

            // A sibling worker shares the host transport, outside the disabled site's workbench lifetime.
            BooruProviderBinding sibling = host.Plugins!.BooruSources["safebooru"];
            IPluginWorkerClient? worker = await sibling.GetWorkerAsync(CancellationToken.None);
            Assert.NotNull(worker);
            BooruSearchService search = new(sibling.Services);
            Task entered = network.HoldNextGelbooru();
            Task<BooruSearchResult> pending = search.SearchAsync(worker, "gelbooru", "dog", 1);
            try
            {
                await entered.WaitAsync(TimeSpan.FromSeconds(10));
                await ToggleAsync(sources, "gelbooru", enable: false);
            }
            finally { network.ReleaseGelbooru(); }
            Assert.IsType<BooruSearchFailure>(await pending);

            Assert.DoesNotContain(host.ActiveBooruServices, services => services.Search.ProviderId == "gelbooru");
            await AssertStalePortsUnavailableAsync(gelbooru);
            Assert.Equal(new[] { "e621.net", "gelbooru.com", "gelbooru.com" }, network.Hosts);

            host.CreateBooruSearchTabStore().Write(new(0, [new("e621", null, null, "cat"), new("gelbooru", null, null, "cat")]));
        }

        await using DesktopSessionHost restarted = Create();
        await restarted.StartAsync();
        await WaitForSourceAsync(restarted, "e621");
        IReadOnlyList<BooruWorkbenchServices> active = restarted.ActiveBooruServices;
        using BooruPage page = new(Source(restarted, "e621"), active);
        page.RestoreSearchTabs(restarted.CreateBooruSearchTabStore(), restarted.Config);
        BooruSearchTabs tabs = page.View.SearchTabs;
        Assert.Equal("e621", page.Model.Search.View.ProviderId);
        Assert.NotEqual(BooruSearchStatus.Unavailable, page.Model.Search.View.Status);
        Assert.True(tabs.Select(tabs.Ids[1]));
        Assert.Equal("gelbooru", page.Model.Search.View.ProviderId);
        Assert.Equal(BooruSearchStatus.Unavailable, page.Model.Search.View.Status);
        await page.Model.Search.SubmitAsync();
        Assert.Equal(new[] { "e621.net", "gelbooru.com", "gelbooru.com" }, network.Hosts);
    });

    private static async Task StageE621FamilyAsync(string profile)
    {
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "plugins", "MeowChan.Plugin.E621.plugin.json"))) return;
        string plugins = Path.Combine(profile, "plugins");
        Directory.CreateDirectory(plugins);
        await File.WriteAllTextAsync(Path.Combine(plugins, "TestBooru.cs"), FixturePlugin.Replace("SOURCE_DECLARATIONS",
            "DescribeSource(\"e621\", \"e621.net\"), DescribeSource(\"e926\", \"e926.net\")", StringComparison.Ordinal));
    }

    private static Task WaitForSourceAsync(DesktopSessionHost host, string sourceId) =>
        BooruTestSupport.WaitAsync(() => host.ActiveBooruServices.Any(services => services.Search.ProviderId == sourceId), sourceId);

    private static BooruWorkbenchServices Source(DesktopSessionHost host, string sourceId) =>
        Assert.Single(host.ActiveBooruServices, services => services.Search.ProviderId == sourceId);

    private static async Task SearchFromSiblingWorkerAsync(DesktopSessionHost host, string sibling, string disabledSource)
    {
        BooruProviderBinding binding = host.Plugins!.BooruSources[sibling];
        IPluginWorkerClient? worker = await binding.GetWorkerAsync(CancellationToken.None);
        Assert.NotNull(worker);
        BooruSearchService search = new(binding.Services);
        Assert.IsType<BooruSearchFailure>(await search.SearchAsync(worker, disabledSource, "cat", 1));
    }

    private static async Task AssertStalePortsUnavailableAsync(BooruWorkbenchServices services)
    {
        CancellationToken token = CancellationToken.None;
        Uri origin = services.Source!.BaseUri;
        BooruPostView post = BooruPostView.From(new BooruPost(new(services.Search.ProviderId, "41"),
            BooruFieldPresence.Rating | BooruFieldPresence.Media, new Uri(origin, "/posts/41"),
            Rating: BooruRating.General, RawRating: "g", Media:
            [new(BooruMediaRole.Original, new Uri(origin, "/41.png"), "png"),
                new(BooruMediaRole.Sample, new Uri(origin, "/sample/41.png"), "png"),
                new(BooruMediaRole.Preview, new Uri(origin, "/preview/41.png"), "png")]));

        Assert.Equal(BooruCallStatus.Unavailable, (await services.Search.SearchAsync("cat", 1, 10, token)).Status);
        Assert.Equal(BooruCallStatus.Unavailable, (await services.Search.ReadPostAsync("41", token)).Status);
        Assert.Null(await services.Previews.LoadPreviewAsync(post, token));
        IBooruSamplePort samples = Assert.IsAssignableFrom<IBooruSamplePort>(services.Previews);
        Assert.Null((await samples.LoadSampleAsync(post, 1024 * 1024, 1024 * 1024, TimeSpan.FromSeconds(5), token)).Bytes);
        Assert.Equal(BooruCodes.SourceUnavailable, (await services.Queue.LoadOriginalAsync(post, BooruLoadOrigin.Search, token)).Code);
        Assert.Equal(BooruCodes.SourceUnavailable, (await services.Watchers.CreateAsync("cat", token)).Code);
    }

    private static async Task ToggleAsync(SourcesPage page, string sourceId, bool enable)
    {
        page.SelectSource(sourceId);
        Button button = FindButton(page, "SourcePluginEnabled");
        Assert.True(button.IsEffectivelyVisible && button.IsEffectivelyEnabled);
        Assert.Equal(Texts.T(enable ? "sources.site.enable" : "sources.site.disable"), AutomationName(button));
        Click(button);
        await BooruTestSupport.WaitAsync(() => button.IsEffectivelyEnabled &&
            AutomationName(button) == Texts.T(enable ? "sources.site.disable" : "sources.site.enable"), "the saved site switch");
        TextBlock summary = Assert.Single(page.GetVisualDescendants().OfType<TextBlock>(),
            text => ControlAutomationPeer.CreatePeerForElement(text).GetAutomationId() == "SourceWatchSummary");
        Assert.Equal(Texts.T(enable ? "sources.noObservation" : "sources.inactiveHint"), summary.Text);
    }

    private static string AutomationName(Button button) => ControlAutomationPeer.CreatePeerForElement(button).GetName();

    private static Button FindButton(Control root, string id) => Assert.Single(root.GetVisualDescendants().OfType<Button>(),
        button => ControlAutomationPeer.CreatePeerForElement(button).GetAutomationId() == id);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private sealed class CountingTransport(bool retryFirstE621 = false) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _gelbooruEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gelbooruReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _holdGelbooru;
        private int _retryE621 = retryFirstE621 ? 1 : 0;

        internal ConcurrentQueue<string> Hosts { get; } = new();
        internal HttpClient Client() => new(this, disposeHandler: false);

        internal Task HoldNextGelbooru()
        {
            Interlocked.Exchange(ref _holdGelbooru, 1);
            return _gelbooruEntered.Task;
        }

        internal void ReleaseGelbooru() => _gelbooruReleased.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string host = request.RequestUri!.Host;
            Hosts.Enqueue(host);
            if (host == "e621.net" && Interlocked.Exchange(ref _retryE621, 0) == 1)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    { Headers = { RetryAfter = new(TimeSpan.FromSeconds(30)) } };
            if (host == "gelbooru.com" && Interlocked.Exchange(ref _holdGelbooru, 0) == 1)
            {
                _gelbooruEntered.TrySetResult();
                await _gelbooruReleased.Task.WaitAsync(cancellationToken);
            }
            string body = host == "gelbooru.com" ? "{\"@attributes\":{\"count\":0},\"post\":[]}" : "{\"posts\":[]}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, null, "application/json") };
        }
    }

    private const string FixturePlugin = """
        using System;
        using System.Linq;
        using System.Threading;
        using System.Threading.Tasks;
        using MeowChan.PluginSdk;

        public sealed class TestBooru : IBooruSourcePlugin
        {
            private static readonly SettingsSchema Settings = new("1", []);
            private static readonly SourceDescription[] Sources = [SOURCE_DECLARATIONS];

            private static SourceDescription DescribeSource(string id, string host) => new(id, id, new Uri("https://" + host + "/"),
                SourceCapabilities.PostSearch | SourceCapabilities.OriginalMedia, [],
                [new(host, HttpHostPurpose.Metadata | HttpHostPurpose.OriginalMedia)], Settings, PostOrderKind.DecimalPostId)
            {
                BooruProfile = new(
                    [new(BooruEndpointKind.Search, "/posts.json",
                        [new("tags", BooruParameterKind.TagQuery), new("page", BooruParameterKind.PageOrBeforeCursor),
                         new("limit", BooruParameterKind.Limit)]), new(BooruEndpointKind.Post, "/posts/{post}.json", [])],
                    new(2, ["s"]), [BooruTagCategory.General], [BooruRating.General], 100, 100)
            };

            public PluginDescription Describe() => new("fixture.booru.sources", "Booru test sources", "1.0.0", SdkContract.Version,
                "Test source switches", Sources, [], Settings, []);

            public async Task<BooruSearchResult> SearchPostsAsync(BooruSearchRequest request, PluginCallContext context,
                CancellationToken cancellationToken = default)
            {
                SourceDescription source = Sources.Single(source => source.SourceId == request.SourceId);
                Uri uri = new(source.BaseUri, FormattableString.Invariant(
                    $"posts.json?tags={Uri.EscapeDataString(request.Query)}&page={request.Page}&limit={request.Limit}"));
                HostHttpResult response = await context.Http.SendAsync(new(request.SourceId, HostHttpMethod.Get, uri), cancellationToken);
                return response is HostHttpResponse
                    ? new BooruSearchSnapshot(new(request.SourceId, request.Query, request.Page, [], true))
                    : new BooruSearchFailure(request.SourceId, new FailureDetail("fixture-http", "The HTTP request was refused."));
            }

            public Task<BooruPostResult> ReadPostAsync(BooruPostRequest request, PluginCallContext context,
                CancellationToken cancellationToken = default) => Task.FromResult<BooruPostResult>(new BooruPostNotFound(request.Post, 404));
        }
        """;
}

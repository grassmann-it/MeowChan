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
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Sources.Booru;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class BooruSourceActivationTests
{
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

            await ToggleAsync(sources, "gelbooru", enable: false);
            Assert.DoesNotContain(host.ActiveBooruServices, services => services.Search.ProviderId == "gelbooru");
            await AssertStalePortsUnavailableAsync(gelbooru);
            Assert.Equal(new[] { "e621.net", "gelbooru.com" }, network.Hosts);

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
        Assert.Equal(new[] { "e621.net", "gelbooru.com" }, network.Hosts);
    });

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

    private sealed class CountingTransport : HttpMessageHandler
    {
        internal ConcurrentQueue<string> Hosts { get; } = new();
        internal HttpClient Client() => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string host = request.RequestUri!.Host;
            Hosts.Enqueue(host);
            string body = host == "gelbooru.com" ? "{\"@attributes\":{\"count\":0},\"post\":[]}" : "{\"posts\":[]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, null, "application/json") });
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

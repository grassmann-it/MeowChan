using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Updates;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

// G1 through the production plugin runtime: the real PluginSession compiles/describes the Danbooru plugin, starts it
// in a real worker process with the booru host services, and the session composes from that binding. Until the
// plugin is delivered with the app (B8 packaging), it is installed as the user would: Danbooru.cs in the profile's
// plugins folder. All traffic goes to the in-memory DanbooruStub through the session's transport seam.
[Collection("Delivered plugin hosts")]
public sealed class BooruPluginRuntimeCompositionTests
{
    private const string PluginId = "meowchan.official.danbooru";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(180);

    [Fact]
    public async Task TheRealPluginWorkerServesTheWindowAndADisabledPluginComposesNothingAfterRestart()
    {
        using TempDirectory profile = new();
        using DanbooruStub stub = new();
        DanbooruStubPost post = stub.Add(new(7001, Color: 0xFFE0A030));
        stub.Publish("cat_ears", post.Id);
        Configure(profile.Path); // a fresh profile directory first gets its settings, then the local plugin
        bool delivered = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "plugins"))
            && Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "plugins"), "MeowChan.Plugin.Danbooru.plugin.json").Length > 0;
        if (!delivered)
        {
            Directory.CreateDirectory(Path.Combine(profile.Path, "plugins"));
            File.Copy(DanbooruTestPlugin.SourcePath, Path.Combine(profile.Path, "plugins", "Danbooru.cs"));
        }

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
            if (!danbooru.Enabled) await host.Plugins.ProfileStore.SetEnabledAsync(PluginId, true, plugins.Revision);
            await BooruCompositionHostTests.WaitAsync(() => host.BooruServices != null, "the composition from the real plugin");
            Assert.Empty(stub.Requests);
            BooruWorkbenchServices services = host.BooruServices!;

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
            Assert.Null(host.BooruServices);
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
            Assert.Null(restarted.BooruServices);
            Assert.Equal(1, stub.MetadataRequests);
        }
        finally { await StopAsync(restarted); }
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
}

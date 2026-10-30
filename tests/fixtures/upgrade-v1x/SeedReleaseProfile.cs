using System.Net;
using System.Reflection;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Persistence;
using MeowChan.Interfaces;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopTests;

// Compiled only into an isolated checkout of the specified old release by prepare.py.
public sealed class R102SeedReleaseProfile
{
    [Fact]
    public async Task CreateOwnedOldReleaseProfile()
    {
        string root = Path.GetFullPath(Environment.GetEnvironmentVariable("MEOWCHAN_UPGRADE_SEED_ROOT")!);
        string version = Environment.GetEnvironmentVariable("MEOWCHAN_UPGRADE_SEED_VERSION")!;
        Assert.StartsWith(version, typeof(Config).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
        string profile = Path.Combine(root, "profile");
        Assert.False(Directory.Exists(profile));
        new Config { DownloadPath = Path.Combine(profile, "media"), VideoVolume = 31, VideoStartMuted = true,
            GalleryContextOpen = true, DownloadsPaused = true, AutomaticUpdateChecks = false }.SaveConfig(profile);
        PluginProfileSnapshot? state = null;
        for (int start = 0; start < 2; start++)
        {
            await using var host = new DesktopSessionHost(profile, pluginTransportFactory: () => new HttpClient(new Offline()));
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(90));
            Assert.Equal(DesktopSessionState.Ready, host.State);
            Assert.Empty(host.Plugins!.Warnings);
            var store = host.Plugins.ProfileStore;
            state = await store.ReadAsync();
            if (start == 0)
            {
                state = await store.SetEnabledAsync("meowchan.official.8kun", false, state.Revision);
                state = await store.SetGrantAsync("meowchan.example.image-dimensions", "dimensions", new("4chan", "g"),
                    FilterDataAccess.OriginalImage, state.Revision);
            }
            else state = await store.SelectRollbackAsync("meowchan.official.4chan", state.Revision);
        }
        using (ProfileLease lease = ProfileLease.Acquire(profile))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
            ResourceId id = new("4chan", "g", "123");
            string media = Path.Combine(profile, "media", "4chan", "g", "123");
            await catalog.RegisterThreadAsync(new(id, "https://boards.4chan.org/g/thread/123", "Old release title", media, true));
            await catalog.CaptureAsync(new(id, "https://boards.4chan.org/g/thread/123", "Old release title", ThreadSourceState.Archived,
                DateTimeOffset.UtcNow, media, new[] { new CapturedPost("123", 0, PostFieldPresence.Html, Html: "<p>Offline upgrade fixture</p>") }));
        }
        await File.WriteAllTextAsync(Path.Combine(root, "receipt.json"), JsonSerializer.Serialize(new
        {
            schema = "meowchan.upgrade-v1x-fixture.v1", version,
            productAssembly = typeof(Config).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
            originalInstall = AppContext.BaseDirectory, profile,
            plugins = state!.Plugins.Select(p => new { p.PluginId, p.Enabled, p.SelectedCandidateId,
                pin = p.Pinned?.ExecutionRevision, available = p.Active != null }),
            grants = state.Grants
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => throw new InvalidOperationException("Old-release fixture must never request the network: " + request.RequestUri?.Host);
    }
}

using System.Net;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class Release102UpgradeFactAttribute : FactAttribute
{
    public Release102UpgradeFactAttribute()
    { if (Environment.GetEnvironmentVariable("MEOWCHAN_UPGRADE_1X_FIXTURE") == null) Skip = "Requires a fresh owned old-release fixture from tests/fixtures/upgrade-v1x/prepare.py."; }
}
[Collection("Delivered plugin hosts")]
public sealed class Release102UpgradeTests
{
    [Release102UpgradeFact]
    public async Task OldReleaseChoicesArchiveAndAvailabilitySurviveNewInstallAndOfflineRestarts()
    {
        string root = Path.GetFullPath(Environment.GetEnvironmentVariable("MEOWCHAN_UPGRADE_1X_FIXTURE")!);
        using JsonDocument receipt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "receipt.json")));
        var old = receipt.RootElement;
        Assert.Equal("meowchan.upgrade-v1x-fixture.v1", old.GetProperty("schema").GetString());
        string profile = Path.Combine(root, "profile");
        Assert.Equal(profile, old.GetProperty("profile").GetString());
        Assert.False(File.Exists(Path.Combine(root, "current-result.json")), "Generate a fresh fixture; this profile was already exercised.");
        var missing = new List<string>();
        for (int restart = 0; restart < 2; restart++)
        {
            await using var host = new DesktopSessionHost(profile, pluginTransportFactory: () => new HttpClient(new Offline()));
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(90));
            Assert.Equal(DesktopSessionState.Ready, host.State);
            Assert.Equal(31, host.Config.VideoVolume); Assert.True(host.Config.VideoStartMuted);
            Assert.True(host.Config.DownloadsPaused); Assert.Equal("Manual", host.Config.GalleryPlaybackMode);
            Assert.False(host.Config.GalleryOverlayControls); Assert.False(host.Config.GalleryMediaFocus);
            var current = await host.Plugins!.ProfileStore.ReadAsync();
            foreach (JsonElement entry in old.GetProperty("plugins").EnumerateArray())
            {
                var now = Assert.Single(current.Plugins, p => p.PluginId == entry.GetProperty("PluginId").GetString());
                Assert.Equal(entry.GetProperty("Enabled").GetBoolean(), now.Enabled);
                Assert.Equal(entry.GetProperty("SelectedCandidateId").GetString(), now.SelectedCandidateId);
                Assert.Equal(entry.GetProperty("pin").GetString(), now.Pinned?.ExecutionRevision);
                if (entry.GetProperty("available").GetBoolean() && now.Active == null)
                    missing.Add(now.PluginId + ": " + now.Problem);
            }
            Assert.Equal(old.GetProperty("grants").GetArrayLength(), current.Grants.Count);
            Assert.True(JsonElement.DeepEquals(old.GetProperty("grants"), JsonSerializer.SerializeToElement(current.Grants)));
            await using ArchiveReader reader = await host.OpenArchiveReaderAsync();
            var thread = await reader.ReadThreadAsync(new("4chan", "g", "123"));
            Assert.NotNull(thread); Assert.Equal("Old release title", thread.Summary.Title);
            Assert.Contains("Offline upgrade fixture", thread.GetPosts()[0].Html);
        }
        await File.WriteAllTextAsync(Path.Combine(root, "current-result.json"), JsonSerializer.Serialize(new
        { completed = true, choicesAndArchivePreserved = true, originalVersion = old.GetProperty("version").GetString(),
            unavailable = missing.Distinct().ToArray(), availabilityPreserved = missing.Count == 0 }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(missing.Count == 0, "Install-path identity needs explicit review; no silent selection migration: " + string.Join("; ", missing.Distinct()));
    }
    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => throw new InvalidOperationException("Upgrade fixture forbids network access.");
    }
}

using System.Net;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Activation;

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
        IReadOnlyList<BundledCandidateRepair> repairRows = Array.Empty<BundledCandidateRepair>();
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
            string statePath = Path.Combine(profile, "plugin-state", "state.json");
            byte[] beforePreview = await File.ReadAllBytesAsync(statePath);
            var preview = await host.Plugins.ProfileStore.PreviewBundledRepairsAsync();
            repairRows = preview.Rows;
            foreach (JsonElement entry in old.GetProperty("plugins").EnumerateArray())
            {
                var now = Assert.Single(current.Plugins, p => p.PluginId == entry.GetProperty("PluginId").GetString());
                if (entry.GetProperty("available").GetBoolean() && now.Active is null && entry.GetProperty("pin").ValueKind == JsonValueKind.Null)
                {
                    var row = Assert.Single(repairRows, r => r.PluginId == now.PluginId);
                    Assert.False(row.CanConfirm);
                    Assert.Equal("unproven-history", row.BlockedReason);
                }
            }
            Assert.Equal(beforePreview, await File.ReadAllBytesAsync(statePath));
            Assert.Equal(old.GetProperty("grants").GetArrayLength(), current.Grants.Count);
            Assert.True(JsonElement.DeepEquals(old.GetProperty("grants"), JsonSerializer.SerializeToElement(current.Grants)));
            await using ArchiveReader reader = await host.OpenArchiveReaderAsync();
            var thread = await reader.ReadThreadAsync(new("4chan", "g", "123"));
            Assert.NotNull(thread); Assert.Equal("Old release title", thread.Summary.Title);
            Assert.Contains("Offline upgrade fixture", thread.GetPosts()[0].Html);
        }
        await File.WriteAllTextAsync(Path.Combine(root, "current-result.json"), JsonSerializer.Serialize(new
        { completed = true, choicesAndArchivePreserved = true, originalVersion = old.GetProperty("version").GetString(),
            unavailable = missing.Distinct().ToArray(), availabilityPreserved = missing.Count == 0,
            repairPreview = repairRows, releaseBlocker = missing.Count != 0 }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(missing.Count == 0, "Install-path identity needs explicit review; no silent selection migration: " + string.Join("; ", missing.Distinct()));
    }
    [Fact]
    public async Task SourcePolicyRejectsChangedSavedAnchorArtifactAndLifecycleInsteadOfTrustingSavedActive()
    {
        using TempDirectory profile = new();
        new Config { DownloadPath = Path.Combine(profile.Path, "media"), DownloadsPaused = true,
            AutomaticUpdateChecks = false }.SaveConfig(profile.Path);
        using DesktopPluginFixtures network = new() { RejectAll = true };
        await using DesktopSessionHost host = new(profile.Path, pluginTransportFactory: network.Client);
        await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(90));
        Assert.Equal(DesktopSessionState.Ready, host.State);
        var plugins = host.Plugins!;
        var state = await plugins.ProfileStore.ReadAsync();
        const string id = "meowchan.source.kohlchan";
        string url = DesktopPluginFixtures.ThreadUrl("kohlchan");
        Guid session = Guid.NewGuid();
        Assert.NotNull(plugins.ObserveExperimentalSourceActivation(session, profile.Path, url, state, true).Target);
        Func<PluginActivationSnapshot, PluginActivationSnapshot>[] alterations =
        [
            p => p with { SelectedCandidateId = "untrusted-saved-anchor" },
            p => p with { Active = p.Active! with { CandidateId = "untrusted-target" } },
            p => p with { Active = p.Active! with { Artifact = p.Active.Artifact with { ExecutionRevision = "untrusted-execution" } } },
            p => p with { Active = p.Active! with { Artifact = p.Active.Artifact with { CompilationFingerprint = "untrusted-compilation" } } },
            p => p with { Active = p.Active! with { Artifact = p.Active.Artifact with { AssemblySha256 = new string('0', 64) } } },
            p => p with { Enabled = !p.Enabled },
            p => p with { LifecycleRevision = p.LifecycleRevision + 1 }
        ];
        foreach (var alter in alterations)
        {
            var untrusted = state with { Plugins = state.Plugins.Select(p => p.PluginId == id ? alter(p) : p).ToArray() };
            Assert.Null(plugins.ObserveExperimentalSourceActivation(session, profile.Path, url, untrusted, true).Target);
        }
        Assert.Null(plugins.ObserveExperimentalSourceActivation(session, profile.Path, url, state, false).Target);
        Assert.Empty(network.Requests);
        Assert.Empty(plugins.Runtime.GetSnapshots());
    }

    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => throw new InvalidOperationException("Upgrade fixture forbids network access.");
    }
}

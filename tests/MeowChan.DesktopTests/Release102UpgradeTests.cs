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
    public async Task OldReleaseExplicitNewBundledSelectionPreservesHistoryAcrossTwoOfflineRestarts()
    {
        string root = Path.GetFullPath(Environment.GetEnvironmentVariable("MEOWCHAN_UPGRADE_1X_FIXTURE")!);
        using JsonDocument receipt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "receipt.json")));
        var old = receipt.RootElement;
        Assert.Equal("meowchan.upgrade-v1x-fixture.v1", old.GetProperty("schema").GetString());
        string profile = Path.Combine(root, "profile");
        Assert.Equal(profile, old.GetProperty("profile").GetString());
        Assert.False(File.Exists(Path.Combine(root, "current-result.json")), "Generate a fresh fixture; this profile was already exercised.");
        string statePath = Path.Combine(profile, "plugin-state", "state.json");
        byte[] originalV1 = await File.ReadAllBytesAsync(statePath);
        var selected = new Dictionary<string, BundledCandidateReselection>();
        BundledCandidateReselection[] blocked = [];
        PluginProfileSnapshot baseline;
        byte[] immutableBackup;
        byte[] immutableDigest;
        byte[] firstPreSelection;
        await using (var host = new DesktopSessionHost(profile, pluginTransportFactory: () => new HttpClient(new Offline())))
        {
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(90));
            Assert.Equal(DesktopSessionState.Ready, host.State);
            var store = Assert.IsType<PluginProfileStore>(host.Plugins!.ProfileStore);
            baseline = await store.ReadAsync();
            using (var originalEnvelope = JsonDocument.Parse(originalV1))
            {
                var originalProfile = MeowChan.PluginSdk.SdkJson.Deserialize<PluginProfileSnapshot>(
                    originalEnvelope.RootElement.GetProperty("payload").GetBytesFromBase64());
                AssertPreserved(originalProfile, baseline);
            }
            foreach (JsonElement entry in old.GetProperty("plugins").EnumerateArray())
            {
                var now = Assert.Single(baseline.Plugins, p => p.PluginId == entry.GetProperty("PluginId").GetString());
                Assert.Equal(entry.GetProperty("Enabled").GetBoolean(), now.Enabled);
                Assert.Equal(entry.GetProperty("SelectedCandidateId").GetString(), now.SelectedCandidateId);
                Assert.Equal(entry.GetProperty("pin").GetString(), now.Pinned?.ExecutionRevision);
            }
            immutableBackup = await File.ReadAllBytesAsync(store.PreV2BackupPath);
            immutableDigest = await File.ReadAllBytesAsync(store.PreV2BackupSha256Path);
            Assert.Equal(originalV1, immutableBackup);
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(immutableBackup)),
                System.Text.Encoding.UTF8.GetString(immutableDigest).Trim(), ignoreCase: true);
            byte[] beforePreview = await File.ReadAllBytesAsync(statePath);
            var repairs = await store.PreviewBundledRepairsAsync();
            foreach (JsonElement entry in old.GetProperty("plugins").EnumerateArray())
            {
                var now = Assert.Single(baseline.Plugins, p => p.PluginId == entry.GetProperty("PluginId").GetString());
                if (entry.GetProperty("available").GetBoolean() && now.Active is null && now.Pinned is null)
                {
                    var row = Assert.Single(repairs.Rows, r => r.PluginId == now.PluginId);
                    Assert.False(row.CanConfirm);
                    Assert.Equal("unproven-history", row.BlockedReason);
                }
            }
            var preview = await store.PreviewBundledReselectionsAsync();
            Assert.Equal(baseline.Revision, preview.ProfileRevision);
            Assert.Equal(beforePreview, await File.ReadAllBytesAsync(statePath));
            Assert.Equal(preview.Rows.ToArray(), (await store.PreviewBundledReselectionsAsync()).Rows.ToArray());
            Assert.NotEmpty(preview.Rows);
            blocked = preview.Rows.Where(r => !r.CanConfirm).ToArray();
            Assert.NotEmpty(blocked); // The genuine fixture deliberately pins 4chan and grants dimensions rights.
            Assert.Contains(blocked, r => r.PluginId == "meowchan.official.4chan");
            Assert.Contains(blocked, r => r.PluginId == "meowchan.example.image-dimensions");
            foreach (var row in blocked)
            {
                Assert.Contains(row.BlockedReason, new[] { "pinned", "inherited-grants", "source-owner-conflict" });
                await Assert.ThrowsAnyAsync<Exception>(() => store.ConfirmBundledReselectionAsync(row, preview.ProfileRevision));
                Assert.Equal(beforePreview, await File.ReadAllBytesAsync(statePath));
            }
            firstPreSelection = beforePreview;
            foreach (var offered in preview.Rows.Where(r => r.CanConfirm))
            {
                var now = Assert.Single(baseline.Plugins, p => p.PluginId == offered.PluginId);
                Assert.Null(now.Pinned);
                Assert.DoesNotContain(baseline.Grants, g => g.PluginId == offered.PluginId);
                Assert.Equal(now.SelectedCandidateId, offered.OldCandidateId);
                Assert.NotEqual(offered.OldCandidateId, offered.NewCandidateId);
                var target = Assert.Single(now.Candidates, c => c.CandidateId == offered.NewCandidateId);
                Assert.True(target.IsPresent);
                Assert.Null(target.Problem);
                Assert.Equal(target.CandidatePath, offered.NewPath);
                Assert.Equal(target.Artifact!.ExecutionRevision, offered.NewExecutionRevision);
                var retained = now.Pending ?? now.LastGood ?? now.Active;
                Assert.NotNull(retained);
                Assert.Equal(retained.ExecutionRevision, offered.OldExecutionRevision);
                Assert.Equal(now.Candidates.SingleOrDefault(c => c.CandidateId == offered.OldCandidateId)?.CandidatePath,
                    offered.OldPath);
                Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(retained.Configuration, MeowChan.PluginSdk.SdkJson.CreateOptions()),
                    JsonDocument.Parse(offered.ConfigurationJson!).RootElement));
                // Each confirmation consumes a revision; obtain a fresh frozen row, never reuse stale approval.
                var fresh = await store.PreviewBundledReselectionsAsync();
                var row = Assert.Single(fresh.Rows, r => r.PluginId == offered.PluginId);
                Assert.Equal(offered, row);
                byte[] beforeConfirm = await File.ReadAllBytesAsync(statePath);
                await Assert.ThrowsAnyAsync<Exception>(() => store.ConfirmBundledReselectionAsync(
                    row with { NewPath = row.NewPath + ".unapproved" }, fresh.ProfileRevision));
                await Assert.ThrowsAnyAsync<Exception>(() => store.ConfirmBundledReselectionAsync(
                    row with { ConfigurationJson = "{}" }, fresh.ProfileRevision));
                Assert.Equal(beforeConfirm, await File.ReadAllBytesAsync(statePath));
                var confirmed = await store.ConfirmBundledReselectionAsync(row, fresh.ProfileRevision);
                var pending = Assert.Single(confirmed.Plugins, p => p.PluginId == row.PluginId);
                Assert.Equal(row.NewCandidateId, pending.SelectedCandidateId);
                Assert.Equal(row.NewCandidateId, pending.Pending?.CandidateId);
                Assert.Equal(row.NewExecutionRevision, pending.Pending?.ExecutionRevision);
                Assert.Equal(row.ConfigurationJson, JsonSerializer.Serialize(pending.Pending!.Configuration,
                    MeowChan.PluginSdk.SdkJson.CreateOptions()));
                // A separate exact pre-selection backup is required, not a refreshed v1 backup.
                Assert.Equal(firstPreSelection, await File.ReadAllBytesAsync(store.PreReselectionBackupPath));
                Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(firstPreSelection)),
                    (await File.ReadAllTextAsync(store.PreReselectionBackupPath + ".sha256")).Trim(), ignoreCase: true);
                Assert.Equal(immutableBackup, await File.ReadAllBytesAsync(store.PreV2BackupPath));
                Assert.Equal(immutableDigest, await File.ReadAllBytesAsync(store.PreV2BackupSha256Path));
                AssertNoInventedHistory(beforePreview, await File.ReadAllBytesAsync(statePath));
                AssertPreserved(baseline, confirmed);
                selected.Add(row.PluginId, row);
            }
            Assert.NotEmpty(selected);
        }
        // Both of these are subsequent startups, not the preview/confirmation startup.
        for (int restart = 0; restart < 2; restart++)
        {
            await using var host = new DesktopSessionHost(profile, pluginTransportFactory: () => new HttpClient(new Offline()));
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(90));
            Assert.Equal(DesktopSessionState.Ready, host.State);
            Assert.Equal(31, host.Config.VideoVolume); Assert.True(host.Config.VideoStartMuted);
            Assert.True(host.Config.DownloadsPaused); Assert.Equal("Manual", host.Config.GalleryPlaybackMode);
            Assert.False(host.Config.GalleryOverlayControls); Assert.False(host.Config.GalleryMediaFocus);
            var currentStore = Assert.IsType<PluginProfileStore>(host.Plugins!.ProfileStore);
            var current = await currentStore.ReadAsync();
            AssertNoInventedHistory(originalV1, await File.ReadAllBytesAsync(statePath));
            AssertPreserved(baseline, current);
            Assert.True(JsonElement.DeepEquals(old.GetProperty("grants"), JsonSerializer.SerializeToElement(current.Grants)));
            foreach (var (id, row) in selected)
            {
                var now = Assert.Single(current.Plugins, p => p.PluginId == id);
                Assert.Equal(row.NewCandidateId, now.SelectedCandidateId);
                Assert.NotNull(now.Active);
                Assert.Equal(row.NewCandidateId, now.Active.CandidateId);
                Assert.Equal(row.NewExecutionRevision, now.Active.ExecutionRevision);
                Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(row.ConfigurationJson!).RootElement,
                    JsonSerializer.SerializeToElement(now.Active.Configuration, MeowChan.PluginSdk.SdkJson.CreateOptions())));
            }
            foreach (var row in blocked)
            {
                var before = Assert.Single(baseline.Plugins, p => p.PluginId == row.PluginId);
                var now = Assert.Single(current.Plugins, p => p.PluginId == row.PluginId);
                Assert.Equal(before.SelectedCandidateId, now.SelectedCandidateId);
                Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(before.Pinned), JsonSerializer.SerializeToElement(now.Pinned)));
            }
            Assert.Equal(immutableBackup, await File.ReadAllBytesAsync(currentStore.PreV2BackupPath));
            Assert.Equal(firstPreSelection, await File.ReadAllBytesAsync(currentStore.PreReselectionBackupPath));
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(firstPreSelection)),
                (await File.ReadAllTextAsync(currentStore.PreReselectionBackupPath + ".sha256")).Trim(), ignoreCase: true);
            Assert.Equal(immutableDigest, await File.ReadAllBytesAsync(currentStore.PreV2BackupSha256Path));
            await using ArchiveReader reader = await host.OpenArchiveReaderAsync();
            var thread = await reader.ReadThreadAsync(new("4chan", "g", "123"));
            Assert.NotNull(thread); Assert.Equal("Old release title", thread.Summary.Title);
            Assert.Contains("Offline upgrade fixture", thread.GetPosts()[0].Html);
        }
        await File.WriteAllTextAsync(Path.Combine(root, "current-result.json"), JsonSerializer.Serialize(new
        { completed = true, originalVersion = old.GetProperty("version").GetString(),
            explicitNewSelections = selected.Values, intentionallyBlocked = blocked, subsequentStartups = 2,
            scope = "Genuine old production seed + current test host, not published-package or UI acceptance" },
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void AssertNoInventedHistory(byte[] priorEnvelope, byte[] currentEnvelope)
    {
        using var prior = JsonDocument.Parse(priorEnvelope);
        using var current = JsonDocument.Parse(currentEnvelope);
        using var priorPayload = JsonDocument.Parse(prior.RootElement.GetProperty("payload").GetBytesFromBase64());
        using var currentPayload = JsonDocument.Parse(current.RootElement.GetProperty("payload").GetBytesFromBase64());
        var priorProfile = priorPayload.RootElement.TryGetProperty("profile", out var profile) ? profile : priorPayload.RootElement;
        var oldIds = priorProfile.GetProperty("plugins").EnumerateArray()
            .Select(p => p.GetProperty("selectedCandidateId").GetString()).ToHashSet();
        if (currentPayload.RootElement.TryGetProperty("provenance", out var provenance))
            foreach (var proof in provenance.EnumerateArray())
                Assert.DoesNotContain(proof.GetProperty("candidateId").GetString(), oldIds);
        if (currentPayload.RootElement.TryGetProperty("bindings", out var bindings))
            foreach (var binding in bindings.EnumerateArray())
                Assert.DoesNotContain(binding.GetProperty("selectedCandidateId").GetString(), oldIds);
    }

    private static void AssertPreserved(PluginProfileSnapshot before, PluginProfileSnapshot after)
    {
        Assert.Equal(before.Grants.ToArray(), after.Grants.ToArray());
        Assert.Equal(before.SelectedSourceOwners.OrderBy(p => p.Key).ToArray(), after.SelectedSourceOwners.OrderBy(p => p.Key).ToArray());
        foreach (var prior in before.Plugins)
        {
            var now = Assert.Single(after.Plugins, p => p.PluginId == prior.PluginId);
            Assert.Equal(prior.Enabled, now.Enabled);
            Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(prior.Pinned), JsonSerializer.SerializeToElement(now.Pinned)));
        }
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

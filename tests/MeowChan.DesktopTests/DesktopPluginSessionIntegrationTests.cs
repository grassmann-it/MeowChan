using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.Admission;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Updates;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Installation;
using MeowChan.PluginSdk;
using MeowChan.Rules;
using MeowChan.Storage;
using Xunit.Abstractions;

namespace MeowChan.DesktopTests;

[Collection("Delivered plugin hosts")]
public sealed class DesktopPluginSessionIntegrationTests(ITestOutputHelper output)
{
    private const string FilterId = "meowchan.example.image-dimensions";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(45);

    [Theory]
    [InlineData("meowchan.source.kohlchan", "kohlchan", true)]
    [InlineData("meowchan.source.wizchan", "wizchan", true)]
    [InlineData("meowchan.source.endchan", "endchan", true)]
    [InlineData("meowchan.source.leftypol", "leftypol", true)]
    [InlineData("meowchan.source.2ch", "2ch", true)]
    [InlineData("meowchan.source.uboachan", "uboachan", true)]
    [InlineData("meowchan.source.zzzchan", "zzzchan", true)]
    [InlineData("meowchan.source.sushichan", "sushichan", true)]
    [InlineData("meowchan.source.sevenchan", "sevenchan", true)]
    [InlineData("meowchan.source.bbwchan", "bbwchan", true)]
    [InlineData("meowchan.source.anonimageboards", "anonimageboards", true)]
    [InlineData("meowchan.source.eightchanmoe", "eightchanmoe", true)]
    [InlineData("meowchan.source.gurocx", "gurocx", true)]
    [InlineData("meowchan.source.fchan", "fchan", true)]
    [InlineData("meowchan.source.capybarachan", "capybarachan", true)]
    [InlineData("meowchan.source.4chan", "4chan", false)]
    [InlineData("meowchan.source.8kun", "8kun", false)]
    [InlineData("meowchan.source.lainchan", "lainchan", false)]
    [InlineData("test.experimental", "kohlchan", false)]
    [InlineData("MEOWCHAN.SOURCE.KOHLCHAN", "kohlchan", false)]
    public void ExperimentalActivationPolicyUsesOnlyExactHostOwnedIdentities(string pluginId, string sourceId, bool eligible)
    {
        Assert.Equal(eligible, ExperimentalSourceActivationPolicy.IsDisabledByDefault(pluginId));
        Assert.Equal(!eligible, ExperimentalSourceActivationPolicy.InitiallyEnabled(pluginId));
        Assert.Equal(eligible, ExperimentalSourceActivationPolicy.IsEligible(pluginId, sourceId));
        Assert.False(ExperimentalSourceActivationPolicy.IsEligible(pluginId, "other-source"));
        Assert.False(ExperimentalSourceActivationPolicy.IsEligible(pluginId, sourceId.ToUpperInvariant()));
    }

    [Fact]
    public void EngineFamilyPluginsAreOnByDefaultAndGateOnlyTheirExperimentalSites()
    {
        // The family plugin is the master switch (on by default); each experimental site has its own switch.
        foreach (string family in new[] { ChanSourceFamilies.Vichan, ChanSourceFamilies.Jschan })
        {
            Assert.False(ExperimentalSourceActivationPolicy.IsDisabledByDefault(family));
            Assert.True(ExperimentalSourceActivationPolicy.InitiallyEnabled(family));
            Assert.True(ChanSourceFamilies.IsFamily(family));
        }
        foreach (string site in new[] { "leftypol", "sushichan", "uboachan", "wizchan" })
        {
            Assert.True(ExperimentalSourceActivationPolicy.IsEligible(ChanSourceFamilies.Vichan, site));
            Assert.False(ChanSourceFamilies.DefaultEnabled(ChanSourceFamilies.Vichan, site));
            Assert.False(ExperimentalSourceActivationPolicy.IsEligible(ChanSourceFamilies.Jschan, site));
        }
        Assert.True(ExperimentalSourceActivationPolicy.IsEligible(ChanSourceFamilies.Jschan, "zzzchan"));
        Assert.False(ChanSourceFamilies.DefaultEnabled(ChanSourceFamilies.Jschan, "zzzchan"));
        Assert.False(ExperimentalSourceActivationPolicy.IsEligible(ChanSourceFamilies.Vichan, "zzzchan"));
        // Lainchan was never an opt-in source; it stays on by default as a family site.
        Assert.False(ExperimentalSourceActivationPolicy.IsEligible(ChanSourceFamilies.Vichan, "lainchan"));
        Assert.True(ChanSourceFamilies.DefaultEnabled(ChanSourceFamilies.Vichan, "lainchan"));
        // A site a family build adds beyond the host's table starts off; other plugins have no site switch.
        Assert.False(ChanSourceFamilies.DefaultEnabled(ChanSourceFamilies.Vichan, "newsite"));
        Assert.False(ChanSourceFamilies.IsFamily("meowchan.source.kohlchan"));
        // Strict provider-hash policy is a property of the source and follows it into the family.
        foreach ((string owner, string source, bool strict) in new[] { (ChanSourceFamilies.Vichan, "uboachan", true),
            (ChanSourceFamilies.Vichan, "sushichan", true), (ChanSourceFamilies.Jschan, "zzzchan", true),
            (ChanSourceFamilies.Vichan, "wizchan", false), (ChanSourceFamilies.Vichan, "leftypol", false) })
        {
            Assert.Equal(strict, ExperimentalSourceActivationPolicy.IsStrictExperimentalSource(owner, source));
            Assert.Equal(!strict, ExperimentalSourceActivationPolicy.RequestsMismatchPermission(owner, source));
        }
        // Every replaced single-site plugin maps to the family that serves its unchanged source ID.
        Assert.Equal(6, ChanSourceFamilies.Replaced.Count);
        Assert.All(ChanSourceFamilies.Replaced, pair => Assert.Equal("meowchan.source." + pair.Value.SourceId, pair.Key));
        Assert.All(ChanSourceFamilies.Replaced, pair => Assert.Equal(pair.Value.Family, ChanSourceFamilies.Successors[pair.Key]));
    }

    [Theory]
    // Existing exact-provider policies remain strict. The new 1.0.3 adapters
    // are still MeowChan-maintained experimental sources, but that product
    // label must not silently change their separate mismatch-permission policy.
    [InlineData("2ch", true)] [InlineData("uboachan", true)]
    [InlineData("zzzchan", true)] [InlineData("sushichan", true)]
    [InlineData("sevenchan", false)] [InlineData("bbwchan", false)]
    [InlineData("anonimageboards", false)] [InlineData("eightchanmoe", false)]
    [InlineData("gurocx", false)] [InlineData("fchan", false)] [InlineData("capybarachan", false)]
    [InlineData("leftypol", false)] [InlineData("kohlchan", false)]
    public void ExperimentalMaturityDoesNotImplyAMismatchPermission(string source, bool strict)
    {
        string owner = "meowchan.source." + source;
        Assert.Equal(strict, ExperimentalSourceActivationPolicy.IsStrictExperimentalSource(owner, source));
        Assert.Equal(!strict, ExperimentalSourceActivationPolicy.RequestsMismatchPermission(owner, source));
        Assert.False(ExperimentalSourceActivationPolicy.IsStrictExperimentalSource("other-owner", source));
        Assert.False(ExperimentalSourceActivationPolicy.RequestsMismatchPermission("other-owner", source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshPluginIdentitiesRemainDisabledAndLocallyRoutableAcrossRestartWithoutChangingConsent(bool savedConsent)
    {
        using TempDirectory profile = new();
        Configure(profile.Path);
        SourceMediaIntegrityPermission? permission = savedConsent ? new("meowchan.source.kohlchan", "kohlchan", true, 7) : null;
        if (permission is not null)
        {
            Config config = Config.LoadConfig(profile.Path);
            config.SourceMediaIntegrityPermissions[SourceMediaIntegrityPermission.CreateKey(permission.PluginOwnerId, permission.SourceId)] = permission;
            config.SaveConfig(profile.Path);
        }
        for (int restart = 0; restart < 2; restart++)
        {
            using DesktopPluginFixtures fixture = new() { RejectAll = true };
            DesktopSessionHost host = Create(profile.Path, fixture);
            try
            {
                PluginProfileSnapshot state = await StartAndInspectPayloadAsync(host, fixture);
                Assert.Empty(state.Grants);
                if (permission is null) Assert.Empty(host.Config.SourceMediaIntegrityPermissions);
                else Assert.Equal(permission, Assert.Single(host.Config.SourceMediaIntegrityPermissions).Value);
                Assert.All(host.GetSourceMediaIntegrityPolicies(), policy =>
                    Assert.Equal(savedConsent && policy.Snapshot.PluginOwnerId == "meowchan.source.kohlchan"
                        && policy.Snapshot.SourceId == "kohlchan", policy.IsGranted));
                Assert.All(host.Plugins!.Sources.Where(source => source.Name is "kohlchan" or "wizchan" or "endchan"),
                    source => Assert.False(source.Enabled));
                Assert.Empty(fixture.Requests);
                Assert.Equal(0, fixture.Unexpected);
                Assert.Empty(host.Plugins.Runtime.GetSnapshots());
            }
            finally { await StopAndVerifyAsync(host); }
        }
    }

    [Fact]
    public async Task DeliveredSourcesAndFilterUseTheRealDesktopSessionThenRestoreTwiceOffline()
    {
        using TempDirectory profile = new();
        using DesktopPluginFixtures fixture = new();
        Configure(profile.Path);
        DesktopSessionHost host = Create(profile.Path, fixture);
        Dictionary<string, string> revisions;
        try
        {
            PluginProfileSnapshot installed = await StartAndInspectPayloadAsync(host, fixture);
            installed = await EnableFixtureSourcesAsync(host, installed);
            revisions = installed.Plugins.ToDictionary(plugin => plugin.PluginId, plugin => plugin.Active!.ExecutionRevision);
            foreach (string source in DesktopPluginFixtures.Sources)
                installed = await host.Plugins!.ProfileStore.SetGrantAsync(FilterId, "dimensions", new(source, "demo"),
                    FilterDataAccess.CandidateMetadata | FilterDataAccess.OriginalImage, installed.Revision);
            await host.SaveRulesAsync(new[] { new MediaRule(MediaRuleScope.Global)
            {
                ImageFilter = new(MediaImageFilterMode.Plugin, FilterId, "dimensions", parameters: new Dictionary<string, string>
                { ["minimumWidth"] = "4", ["orientation"] = "landscape" })
            } }, host.MediaRules.Revision);
            foreach (string source in DesktopPluginFixtures.Sources)
                Assert.Equal(AdmissionStatus.Added, (await host.AddAsync(DesktopPluginFixtures.ThreadUrl(source))).Status);
            await host.SetPausedAsync(false);
            await WaitAsync(() => Completed(host), () => JsonSerializer.Serialize(host.GetView()));

            PluginWorkerSnapshot[] workers = host.Plugins!.Runtime.GetSnapshots().ToArray();
            Assert.Equal(3, workers.Length); // Two delivered sources plus the delivered dimensions filter.
            Assert.All(workers, worker => Assert.True(worker.ProcessId is > 0));
            Assert.Contains(workers, worker => worker.Identity.ExecutionRevision == revisions[FilterId]);
            Assert.Equal(4, fixture.MediaRequests);
            Assert.Equal(6, fixture.Requests.Count);
            Assert.All(fixture.Requests.GroupBy(uri => uri), group => Assert.Single(group));
            Assert.Equal(0, fixture.Unexpected);
            await AssertArchiveAsync(host, fixture.Landscape);
            await host.SetPausedAsync(true); // Pause persists without changing raw watch or media identities.
        }
        finally { await StopAndVerifyAsync(host); }

        // Inspect the durable filter decisions using the ordinary archive store after releasing Desktop's lease.
        using (ProfileLease lease = ProfileLease.Acquire(profile.Path))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
            foreach (string source in DesktopPluginFixtures.Sources)
            {
                IReadOnlyList<CandidateDecision> decisions = await catalog.GetDecisionsAsync(new(source, "demo", "123"));
                Assert.Equal(2, decisions.Count);
                CandidateDecision excluded = Assert.Single(decisions, decision => decision.Selection.Reason == MediaSelectionReason.FilterNoMatch);
                Assert.Equal("123-1", excluded.Metadata.Key.MediaId);
                Assert.Equal("orientation_mismatch", excluded.Selection.FilterReason);
            }
        }
        for (int restart = 0; restart < 2; restart++)
        {
            using DesktopPluginFixtures offline = new() { RejectAll = true };
            DesktopSessionHost restored = Create(profile.Path, offline);
            try
            {
                PluginProfileSnapshot installed = await StartAndInspectPayloadAsync(restored, offline, kohlchanEnabled: true);
                Assert.True(restored.IsPaused);
                Assert.Equal(2, installed.Grants.Count);
                Assert.All(installed.Plugins, plugin => Assert.Equal(revisions[plugin.PluginId], plugin.Active!.ExecutionRevision));
                Assert.True(Completed(restored));
                Assert.Empty(restored.Plugins!.Runtime.GetSnapshots()); // Startup Describe processes have already exited.
                await AssertArchiveAsync(restored, fixture.Landscape);
                Assert.Empty(offline.Requests);
            }
            finally { await StopAndVerifyAsync(restored); }
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        foreach (string source in DesktopPluginFixtures.Sources)
        {
            ArchiveHtmlExportResult exported = await ArchiveHtmlExporter.ExportAsync(reader, new(source, "demo", "123"), Path.Combine(profile.Path, "export-" + source));
            Assert.Equal(2, exported.PostCount);
            Assert.Equal(1, exported.FileCount);
            Assert.Equal(ArchiveExportOmissionReason.ExcludedByRule, Assert.Single(exported.Omissions).Reason);
        }
    }

    [Fact]
    public async Task DeliveredSourceCatalogsArePreviewedThroughDesktopWithoutCreatingWatchesOrMedia()
    {
        using TempDirectory profile = new();
        using DesktopPluginFixtures fixture = new();
        Configure(profile.Path);
        DesktopSessionHost host = Create(profile.Path, fixture);
        try
        {
            PluginProfileSnapshot installed = await StartAndInspectPayloadAsync(host, fixture);
            await EnableFixtureSourcesAsync(host, installed);
            await host.SetPausedAsync(false);
            foreach (string source in DesktopPluginFixtures.Sources)
            {
                BoardAdmissionPreview preview = await host.PreviewBoardAdmissionAsync(DesktopPluginFixtures.Origin(source) + "/demo/").WaitAsync(Deadline);
                BoardCatalogThread thread = Assert.Single(preview.Threads);
                Assert.Equal("123", thread.Id);
                Assert.Equal(DesktopPluginFixtures.ThreadUrl(source), thread.Url);
            }
            Assert.Empty(host.GetView().Threads);
            Assert.Empty(host.GetView().Boards);
            Assert.Empty(host.GetThreadWorkSnapshots());
            Assert.Equal(2, fixture.Requests.Count);
            Assert.Equal(0, fixture.MediaRequests);
            Assert.Equal(0, fixture.Unexpected);
            Assert.Equal(2, host.Plugins!.Runtime.GetSnapshots().Count);
        }
        finally { await StopAndVerifyAsync(host); }
    }

    [Fact]
    public async Task DesktopShutdownCancelsDeliveredSourceHostHttpAndReclaimsItsWorker()
    {
        using TempDirectory profile = new();
        using DesktopPluginFixtures fixture = new() { HoldMetadata = true };
        Configure(profile.Path);
        DesktopSessionHost host = Create(profile.Path, fixture);
        try
        {
            await StartAndInspectPayloadAsync(host, fixture);
            await host.SetPausedAsync(false);
            Assert.Equal(AdmissionStatus.Added, (await host.AddAsync(DesktopPluginFixtures.ThreadUrl("lainchan"))).Status);
            await fixture.MetadataStarted.Task.WaitAsync(Deadline);
            Assert.True(Assert.Single(host.Plugins!.Runtime.GetSnapshots()).ProcessId is > 0);
        }
        finally { await StopAndVerifyAsync(host); }
        Assert.Equal(1, fixture.Cancelled);
        Assert.Equal(0, fixture.MediaRequests);
        Assert.Equal(0, fixture.Unexpected);
        using ProfileLease reopened = ProfileLease.Acquire(profile.Path);
        Assert.Single(new WorkStateStore(profile.Path, reopened).LoadResources());
    }

    // Decision #130 (16.11.2026): an in-place update of a bundled plugin in this app's own plugins folder keeps the plugin's
    // existing size/MD5 exception, grants, owner choices and settings without a repair review (endchan/kohlchan on Tom's
    // profile). A pure executable change and a display-only declaration change (a renamed label) both qualify. Relocation and
    // any widening declaration still need review; those are pinned at store level (PluginProfileStoreTests.InPlaceTrust).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InPlaceShippedUpdateThroughDesktopHostCarriesExactRightsWithoutReview(bool declarationChange)
    {
        using TempDirectory profile = new();
        Configure(profile.Path);
        Config config = Config.LoadConfig(profile.Path);
        SourceMediaIntegrityPermission permission = new("meowchan.source.kohlchan", "kohlchan", true, 7);
        config.SourceMediaIntegrityPermissions[SourceMediaIntegrityPermission.CreateKey(permission.PluginOwnerId, permission.SourceId)] = permission;
        config.SaveConfig(profile.Path);

        PluginProfileSnapshot baseline;
        using (DesktopPluginFixtures fixture = new() { RejectAll = true })
        {
            DesktopSessionHost host = Create(profile.Path, fixture);
            try
            {
                await host.StartAsync().WaitAsync(Deadline);
                Assert.True(host.State == DesktopSessionState.Ready, host.StartupError);
                baseline = await EnableKohlchanAsync(host);
                Assert.True(Plugin(baseline).Enabled);
                Assert.NotNull(Plugin(baseline).Active);
            }
            finally { await StopAndVerifyAsync(host); }
        }

        using (ShippedKohlchanVariant variant = ShippedKohlchanVariant.Create(declarationChange))
        {
            for (int restart = 0; restart < 2; restart++)
            {
                // Independent DesktopSessionHost lifetimes in one test process, not OS-process restarts.
                using DesktopPluginFixtures offline = new();
                DesktopSessionHost updated = Create(profile.Path, offline);
                try
                {
                    await updated.StartAsync().WaitAsync(Deadline);
                    Assert.True(updated.State == DesktopSessionState.Ready, updated.StartupError);
                    PluginProfileStore store = Assert.IsType<PluginProfileStore>(updated.Plugins!.ProfileStore);
                    PluginProfileSnapshot active = await store.ReadAsync();
                    PluginActivationSnapshot plugin = Plugin(active);
                    Assert.NotNull(plugin.Active);
                    Assert.Null(plugin.Problem);
                    Assert.Null(plugin.Pending);
                    Assert.NotEqual(Plugin(baseline).Active!.ExecutionRevision, plugin.Active!.ExecutionRevision);
                    Assert.Equal(Plugin(baseline).SelectedCandidateId, plugin.SelectedCandidateId);
                    Assert.Equal(variant.DeclaredName, plugin.Active.Description.Name);
                    Assert.True(plugin.Enabled);
                    Assert.Equal(SdkJson.Serialize(Plugin(baseline).Active!.Configuration), SdkJson.Serialize(plugin.Active.Configuration));
                    Assert.Equal(SdkJson.Serialize(baseline.Grants), SdkJson.Serialize(active.Grants));
                    Assert.Equal(SdkJson.Serialize(baseline.SelectedSourceOwners), SdkJson.Serialize(active.SelectedSourceOwners));
                    Assert.Equal(permission, Config.LoadConfig(profile.Path).SourceMediaIntegrityPermissions[SourceMediaIntegrityPermission.CreateKey(permission.PluginOwnerId, permission.SourceId)]);
                    // Nothing waits for "Review this repair".
                    Assert.DoesNotContain((await store.PreviewBundledRepairsAsync()).Rows, row => row.PluginId == KohlchanPluginId);

                    await updated.SetPausedAsync(false).WaitAsync(Deadline);
                    BoardAdmissionPreview preview = await updated.PreviewBoardAdmissionAsync(DesktopPluginFixtures.Origin("kohlchan") + "/demo/").WaitAsync(Deadline);
                    Assert.Equal("123", Assert.Single(preview.Threads).Id);
                    Assert.Contains(updated.Plugins.Runtime.GetSnapshots(), worker => worker.ProcessId is > 0
                        && worker.Identity.ExecutionRevision == plugin.Active.ExecutionRevision);
                    Assert.NotEmpty(offline.Requests);
                }
                finally { await StopAndVerifyAsync(updated); }
            }
        }
    }

    private const string KohlchanPluginId = "meowchan.source.kohlchan";

    private static PluginActivationSnapshot Plugin(PluginProfileSnapshot state) =>
        Assert.Single(state.Plugins, plugin => plugin.PluginId == KohlchanPluginId);

    private static async Task<PluginProfileSnapshot> EnableKohlchanAsync(DesktopSessionHost host)
    {
        PluginProfileSnapshot state = await host.Plugins!.ProfileStore.ReadAsync();
        PluginActivationSnapshot plugin = Plugin(state);
        Assert.NotNull(plugin.Active);
        if (!plugin.Enabled) state = await host.Plugins.ProfileStore.SetEnabledAsync(KohlchanPluginId, true, state.Revision);
        return state;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class ShippedKohlchanVariant : IDisposable
    {
        private readonly string manifestPath;
        private readonly string assemblyPath;
        private readonly byte[] originalManifest;
        private readonly byte[] originalAssembly;
        private bool restored;
        internal string DeclaredName { get; }

        private ShippedKohlchanVariant(string manifestPath, string assemblyPath, byte[] originalManifest, byte[] originalAssembly,
            string declaredName)
        {
            this.manifestPath = manifestPath; this.assemblyPath = assemblyPath;
            this.originalManifest = originalManifest; this.originalAssembly = originalAssembly;
            DeclaredName = declaredName;
        }

        internal static ShippedKohlchanVariant Create(bool declarationChange)
        {
            string root = Path.Combine(AppContext.BaseDirectory, "plugins");
            string manifestPath = Path.Combine(root, "MeowChan.Plugin.Kohlchan.plugin.json");
            Assert.True(File.Exists(manifestPath), "The delivered Kohlchan manifest is required for this host-boundary probe.");
            byte[] originalManifest = File.ReadAllBytes(manifestPath);
            PluginBundleManifest manifest = JsonSerializer.Deserialize<PluginBundleManifest>(originalManifest)!;
            string assemblyPath = Path.GetFullPath(Path.Combine(root, manifest.AssemblyRelativePath));
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Assert.StartsWith(fullRoot, assemblyPath, StringComparison.Ordinal);
            byte[] originalAssembly = File.ReadAllBytes(assemblyPath);
            byte[] changedAssembly;
            if (!declarationChange) changedAssembly = originalAssembly.Concat(new byte[] { 0x4d }).ToArray();
            else
            {
                // User-facing literals live in the managed #US heap (UTF-16), while a
                // compiler may also retain the identifier in the UTF-8 metadata heap.
                try { changedAssembly = ReplaceAll(originalAssembly, Encoding.Unicode.GetBytes("Kohlchan"), Encoding.Unicode.GetBytes("KohlCHan")); }
                catch (InvalidDataException) { changedAssembly = ReplaceAll(originalAssembly, Encoding.UTF8.GetBytes("Kohlchan"), Encoding.UTF8.GetBytes("KohlCHan")); }
            }
            Assert.False(originalAssembly.AsSpan().SequenceEqual(changedAssembly));
            string declaredName = declarationChange ? "KohlCHan" : "Kohlchan";
            ShippedKohlchanVariant variant = new(manifestPath, assemblyPath, originalManifest, originalAssembly, declaredName);
            try
            {
                File.WriteAllBytes(assemblyPath, changedAssembly);
                using JsonDocument document = JsonDocument.Parse(originalManifest);
                System.Text.Json.Nodes.JsonObject json = System.Text.Json.Nodes.JsonNode.Parse(document.RootElement.GetRawText())!.AsObject();
                string? assemblyHashName = json.Select(pair => pair.Key)
                    .SingleOrDefault(name => string.Equals(name, nameof(PluginBundleManifest.AssemblySha256), StringComparison.OrdinalIgnoreCase));
                Assert.NotNull(assemblyHashName);
                json[assemblyHashName!] = Hash(changedAssembly);
                File.WriteAllText(manifestPath, json.ToJsonString());
                return variant;
            }
            catch
            {
                variant.Restore();
                throw;
            }
        }

        private static byte[] ReplaceAll(byte[] source, byte[] search, byte[] replacement)
        {
            if (search.Length == 0 || search.Length != replacement.Length) throw new ArgumentException("Replacement shape differs.");
            byte[] result = source.ToArray();
            int count = 0;
            for (int offset = 0; offset <= result.Length - search.Length; offset++)
            {
                if (!result.AsSpan(offset, search.Length).SequenceEqual(search)) continue;
                replacement.CopyTo(result.AsSpan(offset, replacement.Length));
                count++; offset += search.Length - 1;
            }
            if (count == 0) throw new InvalidDataException("The delivered assembly did not contain the expected declaration string.");
            return result;
        }

        private void Restore()
        {
            if (restored) return;
            File.WriteAllBytes(assemblyPath, originalAssembly);
            File.WriteAllBytes(manifestPath, originalManifest);
            restored = true;
        }

        public void Dispose() => Restore();
    }

    private static void Configure(string profile) => new Config
    {
        DownloadPath = Path.Combine(profile, "media"), DownloadsPaused = true, SaveUrlsOnExit = true,
        AutomaticUpdateChecks = false, MaxParallelDownloads = 2, RecheckTimer = 3600, MinimumFreeSpaceGiB = 0
    }.SaveConfig(profile);

    private static DesktopSessionHost Create(string profile, DesktopPluginFixtures fixture) => new(profile,
        updateSessionFactory: (path, config) => new UpdateSession(path, config, fixture.Client(), disableEnvironment: () => "1"),
        pluginTransportFactory: fixture.Client);

    private static async Task<PluginProfileSnapshot> EnableFixtureSourcesAsync(DesktopSessionHost host, PluginProfileSnapshot state)
    {
        // Exercise the ordinary strict enable path; fixture acquisition grants no source-integrity exception.
        foreach (string source in DesktopPluginFixtures.Sources)
        {
            string pluginId = source == "lainchan" ? ChanSourceFamilies.Vichan : "meowchan.source." + source;
            if (!state.Plugins.Single(plugin => plugin.PluginId == pluginId).Enabled)
                state = await host.Plugins!.ProfileStore.SetEnabledAsync(pluginId, true, state.Revision);
        }
        Assert.Empty(host.Config.SourceMediaIntegrityPermissions);
        Assert.All(host.GetSourceMediaIntegrityPolicies(), policy => Assert.False(policy.IsGranted));
        return state;
    }

    private async Task<PluginProfileSnapshot> StartAndInspectPayloadAsync(DesktopSessionHost host, DesktopPluginFixtures fixture,
        bool kohlchanEnabled = false)
    {
        string app = AppContext.BaseDirectory;
        string suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        Assert.True(File.Exists(Path.Combine(app, "plugin-worker", "MeowChan.PluginWorker" + suffix)));
        Assert.True(File.Exists(Path.Combine(app, "plugin-compiler", "MeowChan.PluginCompiler" + suffix)));
        string[] manifests = Directory.GetFiles(Path.Combine(app, "plugins"), "*.plugin.json");
        // All seven named experimental adapters ship. AnonImageboards and GuroCx are documented technical blocks that register no source route.
        // Danbooru ships as the official post-search source (B8 packaging); it is not a chan board adapter.
        // The vichan and jschan family bundles replace the six single-site bundles of 1.0.x.
        Assert.Equal(20, manifests.Length);
        foreach (string name in new[] { "Lainchan", "Leftypol", "SushiChan", "Uboachan", "Wizchan", "Zzzchan" })
            Assert.DoesNotContain(manifests, path => Path.GetFileName(path) == "MeowChan.Plugin." + name + ".plugin.json");
        foreach (string name in new[] { "SevenChan", "BbwChan", "AnonImageboards", "EightchanMoe", "GuroCx", "Fchan", "CapybaraChan", "Danbooru", "VichanFamily", "JschanFamily" })
            Assert.Contains(manifests, path => Path.GetFileName(path) == "MeowChan.Plugin." + name + ".plugin.json");
        string cache = Path.Combine(host.ProfileDirectory, "plugin-cache", "artifacts");
        int priorArtifacts = Directory.Exists(cache) ? Directory.GetDirectories(cache).Length : 0;
        Stopwatch startup = Stopwatch.StartNew();
        try { await host.StartAsync().WaitAsync(Deadline); }
        finally
        {
            output.WriteLine("DESKTOP_PLUGIN_STARTUP " + JsonSerializer.Serialize(new
            { elapsedMilliseconds = startup.ElapsedMilliseconds, priorArtifacts, manifests = manifests.Length,
                state = host.State.ToString(), warningCount = host.Plugins?.Warnings.Count, host.StartupError }));
        }
        Assert.True(host.State == DesktopSessionState.Ready, host.StartupError);
        Assert.NotNull(host.Plugins);
        Assert.Empty(host.Plugins.Warnings);
        // A booru post-search source is served by the booru host services, never as a chan board adapter.
        Assert.Equal("2ch,4chan,8kun,bbwchan,capybarachan,eightchanmoe,endchan,fchan,kohlchan,lainchan,leftypol,sevenchan,sushichan,uboachan,wizchan,zzzchan", string.Join(",", host.Plugins.Sources.Select(source => source.Name).Order()));
        AssertNewSourceRouting(host);
        Assert.Empty(fixture.Requests);
        Assert.Equal(0, fixture.Unexpected);
        PluginProfileSnapshot profile = await host.Plugins.ProfileStore.ReadAsync();
        // 19 delivered chan/filter plugins (two of them engine families) plus the official Danbooru post-search plugin.
        Assert.Equal(20, profile.Plugins.Count);
        foreach (PluginActivationSnapshot plugin in profile.Plugins)
        {
            bool expectedEnabled = ExperimentalSourceActivationPolicy.InitiallyEnabled(plugin.PluginId)
                || (kohlchanEnabled && plugin.PluginId == "meowchan.source.kohlchan");
            Assert.Equal(expectedEnabled, plugin.Enabled);
            Assert.NotNull(plugin.Active);
            Assert.Null(plugin.Problem);
            PluginCandidateSnapshot candidate = Assert.Single(plugin.Candidates);
            Assert.Contains(candidate.CandidatePath, manifests);
            PluginBundleManifest manifest = JsonSerializer.Deserialize<PluginBundleManifest>(await File.ReadAllTextAsync(candidate.CandidatePath!))!;
            string delivered = Path.Combine(app, "plugins", manifest.AssemblyRelativePath);
            string expectedHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(delivered)));
            Assert.Equal(expectedHash, plugin.Active.Artifact.AssemblySha256, ignoreCase: true);
            Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(plugin.Active.Artifact.AssemblyPath))), ignoreCase: true);
        }
        foreach (var source in host.Plugins.Sources)
        {
            // Delivered source IDs and plugin owner IDs are distinct identities.
            string owner = source.Name switch
            {
                "4chan" => "meowchan.official.4chan",
                "8kun" => "meowchan.official.8kun",
                "lainchan" or "wizchan" or "leftypol" or "uboachan" or "sushichan" => ChanSourceFamilies.Vichan,
                "zzzchan" => ChanSourceFamilies.Jschan,
                "kohlchan" => "meowchan.source.kohlchan",
                "endchan" => "meowchan.source.endchan",
                "2ch" => "meowchan.source.2ch",
                "sevenchan" => "meowchan.source.sevenchan",
                "bbwchan" => "meowchan.source.bbwchan",
                "eightchanmoe" => "meowchan.source.eightchanmoe",
                "fchan" => "meowchan.source.fchan",
                "capybarachan" => "meowchan.source.capybarachan",
                _ => throw new InvalidOperationException("Unexpected delivered source: " + source.Name)
            };
            // A family site is used while its plugin and its own switch are on; Lainchan's switch is on by default.
            Assert.Equal(profile.Plugins.Single(plugin => plugin.PluginId == owner).Enabled
                && host.Plugins.SiteSwitches.IsEnabled(owner, source.Name), source.Enabled);
        }
        return profile;
    }

    private static void AssertNewSourceRouting(DesktopSessionHost host)
    {
        // Use the actual delivered descriptions, after worker Describe(), through the production router.
        // Routing itself remains passive and must not need metadata or a production worker.
        foreach (var route in new[]
        {
            (Source: "kohlchan", Input: "https://kohlchan.net/demo/", Kind: SourceResourceKind.Board, Canonical: "https://kohlchan.net/demo/"),
            (Source: "kohlchan", Input: "https://kohlchan.net/demo/res/123.html", Kind: SourceResourceKind.Thread, Canonical: "https://kohlchan.net/demo/res/123.html"),
            (Source: "wizchan", Input: "https://wizchan.org/demo/", Kind: SourceResourceKind.Board, Canonical: "https://wizchan.org/demo/"),
            (Source: "wizchan", Input: "https://wizchan.org/demo/index.html", Kind: SourceResourceKind.Board, Canonical: "https://wizchan.org/demo/"),
            (Source: "wizchan", Input: "https://wizchan.org/demo/res/123.html#p124", Kind: SourceResourceKind.Thread, Canonical: "https://wizchan.org/demo/res/123.html"),
            (Source: "endchan", Input: "https://endchan.net/demo/", Kind: SourceResourceKind.Board, Canonical: "https://endchan.net/demo/"),
            (Source: "endchan", Input: "https://endchan.org/demo/", Kind: SourceResourceKind.Board, Canonical: "https://endchan.net/demo/"),
            (Source: "endchan", Input: "https://endchan.org/demo/res/123.html#124", Kind: SourceResourceKind.Thread, Canonical: "https://endchan.net/demo/res/123.html")
        })
        {
            var source = host.Plugins!.Sources.Single(source => source.Name == route.Source);
            Assert.True(source.Router.TryResolve(new Uri(route.Input), out var kind, out string board, out string? thread, out Uri? canonical));
            Assert.Equal(route.Kind, kind);
            Assert.Equal("demo", board);
            Assert.Equal(route.Kind == SourceResourceKind.Thread ? "123" : null, thread);
            Assert.Equal(route.Canonical, canonical!.AbsoluteUri);
        }
        foreach (var source in host.Plugins!.Sources.Where(source => source.Name is "wizchan" or "endchan"))
        {
            foreach (string input in new[]
            {
                "https://www." + source.BaseUri.Host + "/demo/res/123.html",
                source.BaseUri + "demo/res/not-decimal.html",
                source.BaseUri + "demo/res/123.html/title",
                source.BaseUri + "demo/res/123.html/",
                source.BaseUri + "demo/other/123.html"
            })
                Assert.False(source.Router.TryResolve(new Uri(input), out _, out _, out _, out _), input);
        }
        var wizchan = host.Plugins.Sources.Single(source => source.Name == "wizchan");
        Assert.True(wizchan.Router.TryResolve(new Uri("https://wizchan.org/K%C3%BChl%2BBoard/res/%31%32%33.html#p124"),
            out var opaqueKind, out string opaqueBoard, out string? opaqueThread, out Uri? opaqueCanonical));
        Assert.Equal(SourceResourceKind.Thread, opaqueKind);
        Assert.Equal("Kühl+Board", opaqueBoard);
        Assert.Equal("123", opaqueThread);
        Assert.Equal("https://wizchan.org/K%C3%BChl%2BBoard/res/123.html", opaqueCanonical!.AbsoluteUri);
        var endchan = host.Plugins.Sources.Single(source => source.Name == "endchan");
        Assert.False(endchan.Router.TryResolve(new Uri("https://endchan.net/demo+other/res/123.html"), out _, out _, out _, out _));
        foreach (string hostName in new[] { "endchan.gg", "endchan.eu" })
            Assert.False(endchan.Router.TryResolve(new Uri("https://" + hostName + "/demo/res/123.html"), out _, out _, out _, out _));
        Assert.Empty(host.Plugins.Runtime.GetSnapshots());
    }

    private static bool Completed(DesktopSessionHost host)
    {
        IReadOnlyList<ThreadWorkSnapshot> work = host.GetThreadWorkSnapshots();
        return work.Count == 2 && work.All(resource => resource.Media.Count == 2
            && resource.Media.Count(item => item.Status == MediaWorkStatus.Succeeded) == 1
            && resource.Media.Count(item => item.Status == MediaWorkStatus.Excluded) == 1);
    }

    private static async Task AssertArchiveAsync(DesktopSessionHost host, byte[] expected)
    {
        await using ArchiveReader reader = await host.OpenArchiveReaderAsync();
        foreach (string source in DesktopPluginFixtures.Sources)
        {
            ArchivedThreadView view = (await reader.ReadThreadAsync(new(source, "demo", "123")))!;
            Assert.NotNull(view);
            Assert.Equal(new[] { "123", "124" }, view.GetPosts().Select(post => post.PostId));
            Assert.Equal(source == "lainchan" ? ThreadSourceState.Active : ThreadSourceState.Archived, view.Summary.SourceState);
            CapturedAttachment[] attachments = view.GetPosts()[0].Attachments!.ToArray();
            Assert.Equal(new[] { "123-0", "123-1" }, attachments.Select(attachment => attachment.MediaId));
            Assert.Equal(expected, await File.ReadAllBytesAsync(attachments[0].PlannedPath!));
            Assert.False(File.Exists(attachments[1].PlannedPath));
            MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(view.Summary.MediaDirectory);
            MediaAssetReference saved = Assert.Single(manifest.GetPage());
            Assert.Equal("123-0", saved.MediaIdentifier);
            Assert.Equal("original", saved.Variant);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), saved.Sha256, ignoreCase: true);
        }
    }

    private static async Task StopAndVerifyAsync(DesktopSessionHost host)
    {
        int[] pids = host.Plugins?.Runtime.GetSnapshots().Where(worker => worker.ProcessId.HasValue).Select(worker => worker.ProcessId!.Value).ToArray() ?? Array.Empty<int>();
        await host.DisposeAsync().AsTask().WaitAsync(Deadline);
        Assert.Equal(DesktopSessionState.Stopped, host.State);
        Assert.All(pids, pid => Assert.False(IsAlive(pid), "Owned plugin worker still exists after Desktop shutdown: " + pid));
        if (host.Plugins != null) Assert.Empty(host.Plugins.Runtime.GetSnapshots());
        foreach (string directory in new[] { "plugin-workers", "filter-stages" })
        {
            string path = Path.Combine(host.ProfileDirectory, directory);
            if (Directory.Exists(path)) Assert.Empty(Directory.EnumerateFileSystemEntries(path));
        }
    }

    private static bool IsAlive(int pid)
    {
        try { using Process process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static async Task WaitAsync(Func<bool> condition, Func<string> diagnostic)
    {
        using CancellationTokenSource timeout = new(Deadline);
        try { while (!condition()) await Task.Delay(25, timeout.Token); }
        catch (OperationCanceledException) { Assert.Fail("Desktop plugin workflow did not settle: " + diagnostic()); }
    }
}

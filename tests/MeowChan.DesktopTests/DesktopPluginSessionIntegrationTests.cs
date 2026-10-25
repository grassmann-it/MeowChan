using System.Diagnostics;
using System.Security.Cryptography;
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

public sealed class DesktopPluginSessionIntegrationTests(ITestOutputHelper output)
{
    private const string FilterId = "meowchan.example.image-dimensions";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(45);

    [Theory]
    [InlineData("meowchan.source.kohlchan", "kohlchan", true)]
    [InlineData("meowchan.source.wizchan", "wizchan", true)]
    [InlineData("meowchan.source.endchan", "endchan", true)]
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
            string pluginId = "meowchan.source." + source;
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
        Assert.Equal(11, manifests.Length);
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
        Assert.Equal(new[] { "4chan", "8kun", "endchan", "kohlchan", "lainchan", "wizchan" }, host.Plugins.Sources.Select(source => source.Name).Order());
        AssertNewSourceRouting(host);
        Assert.Empty(fixture.Requests);
        Assert.Equal(0, fixture.Unexpected);
        PluginProfileSnapshot profile = await host.Plugins.ProfileStore.ReadAsync();
        Assert.Equal(11, profile.Plugins.Count);
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
                "lainchan" => "meowchan.source.lainchan",
                "kohlchan" => "meowchan.source.kohlchan",
                "wizchan" => "meowchan.source.wizchan",
                "endchan" => "meowchan.source.endchan",
                _ => throw new InvalidOperationException("Unexpected delivered source: " + source.Name)
            };
            Assert.Equal(profile.Plugins.Single(plugin => plugin.PluginId == owner).Enabled, source.Enabled);
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

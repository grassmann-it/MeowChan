using MeowChan.Interfaces;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Updates;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Installation;
using MeowChan.PluginSdk;
using MeowChan.Rules;
using MeowChan.Storage;
using Xunit.Abstractions;

namespace MeowChan.DesktopTests;

// Actual delivered parsers run in the real worker processes. Only the final HTTP transport
// is synthetic. Offline below is a fresh DesktopSessionHost in the same test process,
// not an independent OS-process restart, GUI exercise, package or live-site acceptance.
[Collection("Delivered plugin hosts")]
public sealed class SushiZzzWaveIntegrationTests(ITestOutputHelper output)
{
    private const string FilterId = "meowchan.example.image-dimensions";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    [Theory]
    [InlineData("sushichan")]
    [InlineData("zzzchan")]
    public async Task DeliveredWorkerCatalogAndFilterPersistStrictBindingsAndRestoreOffline(string source)
    {
        using TempDirectory profile = new();
        using SushiZzzWaveFixtures fixture = new(source);
        Configure(profile.Path);
        DesktopSessionHost host = Create(profile.Path, fixture);
        MediaAttachmentBinding saved;
        string executionRevision;
        try
        {
            await StartAsync(host);
            await ActivateStrictAsync(host, fixture);
            var state = await host.Plugins!.ProfileStore.ReadAsync();
            executionRevision = state.Plugins.Single(plugin => plugin.PluginId == Owner(source)).Active!.ExecutionRevision;
            await AssertDeliveredAsync(host, Owner(source));
            await AssertDeliveredAsync(host, FilterId);
            state = await host.Plugins.ProfileStore.SetGrantAsync(FilterId, "dimensions", new(source, "demo"),
                FilterDataAccess.CandidateMetadata | FilterDataAccess.OriginalImage, state.Revision);
            await host.SaveRulesAsync(new[] { new MediaRule(MediaRuleScope.Global) {
                ImageFilter = new(MediaImageFilterMode.Plugin, FilterId, "dimensions", parameters: new Dictionary<string, string>
                    { ["minimumWidth"] = "4", ["orientation"] = "landscape" }) } }, host.MediaRules.Revision);
            await host.SetPausedAsync(false);

            var catalog = await host.PreviewBoardAdmissionAsync(fixture.BoardUrl).WaitAsync(Deadline);
            Assert.Equal("123", Assert.Single(catalog.Threads).Id);
            Assert.Equal(fixture.ThreadUrl, catalog.Threads[0].Url);
            Assert.Empty(host.GetView().Threads);
            Assert.Empty(host.GetView().Boards);
            Assert.Equal(0, fixture.MediaRequests);
            Assert.Equal(AdmissionStatus.Added, (await host.AddAsync(fixture.ThreadUrl)).Status);
            await WaitAsync(() => Completed(host), () => JsonSerializer.Serialize(host.GetThreadWorkSnapshots()));
            await host.SetPausedAsync(true);

            Assert.Equal(2, fixture.MediaRequests); // The filter stage and final acquisition share each original.
            Assert.Equal(4, fixture.Requests.Count); // Catalog + thread + two originals, no hidden fetches.
            Assert.All(fixture.Requests.GroupBy(url => url), requests => Assert.Single(requests));
            Assert.Equal(0, fixture.Unexpected);
            PluginWorkerSnapshot[] workers = host.Plugins.Runtime.GetSnapshots().ToArray();
            Assert.Equal(2, workers.Length);
            Assert.All(workers, worker => Assert.True(worker.ProcessId is > 0));
            Assert.Contains(workers, worker => worker.Identity.ExecutionRevision == executionRevision);
            Assert.Contains(workers, worker => worker.Identity.ExecutionRevision == state.Plugins.Single(plugin => plugin.PluginId == FilterId).Active!.ExecutionRevision);
            saved = await AssertArchiveAsync(host, fixture);
        }
        finally { await StopAsync(host); }

        using (ProfileLease lease = ProfileLease.Acquire(profile.Path))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
            var decisions = await catalog.GetDecisionsAsync(Resource(source));
            Assert.Equal(2, decisions.Count);
            CandidateDecision excluded = Assert.Single(decisions, row => row.Selection.Reason == MediaSelectionReason.FilterNoMatch);
            Assert.Equal("123-1", excluded.Metadata.Key.MediaId);
            Assert.Equal("orientation_mismatch", excluded.Selection.FilterReason);
        }
        using SushiZzzWaveFixtures offline = new(source) { RejectAll = true };
        DesktopSessionHost restored = Create(profile.Path, offline);
        try
        {
            await StartAsync(restored);
            Assert.True(restored.IsPaused);
            Assert.True(Completed(restored));
            var plugin = (await restored.Plugins!.ProfileStore.ReadAsync()).Plugins.Single(row => row.PluginId == Owner(source));
            Assert.True(plugin.Enabled);
            Assert.Equal(executionRevision, plugin.Active!.ExecutionRevision);
            Assert.Equal(saved, await AssertArchiveAsync(restored, offline));
            Assert.Empty(restored.Plugins.Runtime.GetSnapshots());
            Assert.Empty(offline.Requests);
            Assert.Equal(0, offline.Unexpected);
            Assert.Empty(restored.Config.SourceMediaIntegrityPermissions);
        }
        finally { await StopAsync(restored); }
    }

    [Theory]
    [InlineData("sushichan")]
    [InlineData("zzzchan")]
    public async Task HandEditedGrantCannotPermitCorruptOriginalForStrictExperimentalSource(string source)
    {
        using TempDirectory profile = new();
        using SushiZzzWaveFixtures fixture = new(source) { CorruptOriginal = true };
        Configure(profile.Path);
        Config config = Config.LoadConfig(profile.Path);
        config.SourceMediaIntegrityPermissions[SourceMediaIntegrityPermission.CreateKey(Owner(source), source)] = new(Owner(source), source, true, 7);
        config.SaveConfig(profile.Path);
        DesktopSessionHost host = Create(profile.Path, fixture);
        try
        {
            await StartAsync(host);
            await ActivateStrictAsync(host, fixture, hasHandEditedGrant: true);
            Assert.Equal(AdmissionStatus.Added, (await host.AddAsync(fixture.ThreadUrl)).Status);
            await host.SetPausedAsync(false);
            await WaitAsync(() => {
                var work = host.GetThreadWorkSnapshots().SelectMany(thread => thread.Media).ToArray();
                return work.Length == 2 && work.Any(row => row.MediaIdentifier == "123-0" && row.Failure != null)
                    && work.Any(row => row.MediaIdentifier == "123-1" && row.Status == MediaWorkStatus.Succeeded);
            }, () => JsonSerializer.Serialize(host.GetThreadWorkSnapshots()));
            await host.SetPausedAsync(true);
            Assert.Equal(2, fixture.MediaRequests);
            Assert.Equal(0, fixture.Unexpected);
            await using ArchiveReader reader = await host.OpenArchiveReaderAsync();
            var thread = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Resource(source)));
            var manifest = await MediaAssetReader.OpenAsync(thread.Summary.MediaDirectory);
            Assert.Equal("123-1", Assert.Single(manifest.GetPage()).MediaIdentifier);
            Assert.Null(MediaAttachmentResolver.Resolve(manifest, Resource(source), "123", thread.GetPosts()[0].Attachments![0]));
            var accepted = Assert.IsType<ResolvedMediaAttachment>(MediaAttachmentResolver.Resolve(manifest, Resource(source), "123", thread.GetPosts()[0].Attachments![1]));
            Assert.Equal(MediaAcceptanceKind.Strict, accepted.Binding!.Acceptance.Kind);
            Assert.False(Policy(host, source).IsGranted);
            Assert.False(Policy(host, source).Snapshot.AllowProviderSizeMd5Mismatch);
        }
        finally { await StopAsync(host); }
    }

    private static async Task ActivateStrictAsync(DesktopSessionHost host, SushiZzzWaveFixtures fixture, bool hasHandEditedGrant = false)
    {
        string source = fixture.Source;
        var plugin = (await host.Plugins!.ProfileStore.ReadAsync()).Plugins.Single(row => row.PluginId == Owner(source));
        Assert.False(plugin.Enabled);
        var projection = await host.ProjectExperimentalSourceActivationAsync(fixture.ThreadUrl);
        Assert.Equal(ExperimentalSourceActivationProjectionStatus.ConfirmationRequired, projection.Status);
        var target = Assert.IsType<ExperimentalSourceActivationTarget>(projection.Target);
        Assert.Equal(source, target.SourceId);
        Assert.False(target.RequestsMismatchPermission);
        Assert.False(target.IsGranted);
        Assert.Equal(SourceMaturity.Experimental, Policy(host, source).Maturity);
        Assert.Empty(fixture.Requests);
        Assert.Empty(host.Plugins.Runtime.GetSnapshots());
        var result = await host.ActivateAndAllowExperimentalSourceAsync(target).WaitAsync(Deadline);
        Assert.Equal(ExperimentalSourceActivationStatus.Succeeded, result.Status);
        Assert.True(result.ActualState!.IsEnabled);
        Assert.True(result.ActualState.IsActive);
        Assert.False(result.ActualState.IsEffective);
        Assert.False(result.ActualState.IsGranted);
        Assert.False(Policy(host, source).Snapshot.AllowProviderSizeMd5Mismatch);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.SetSourceMediaIntegrityPermissionAsync(
            Owner(source), source, true, Policy(host, source).Snapshot.Revision));
        Assert.False(Policy(host, source).IsGranted);
        if (!hasHandEditedGrant) Assert.Empty(host.Config.SourceMediaIntegrityPermissions);
        else
        {
            var raw = Assert.Single(host.Config.SourceMediaIntegrityPermissions).Value;
            Assert.True(raw.AllowProviderSizeMd5Mismatch); // Retained input is inert, never runtime authority.
            Assert.Equal(7, raw.Revision);
        }
        Assert.Empty(fixture.Requests);
    }

    private static async Task<MediaAttachmentBinding> AssertArchiveAsync(DesktopSessionHost host, SushiZzzWaveFixtures fixture)
    {
        await using ArchiveReader reader = await host.OpenArchiveReaderAsync();
        var thread = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Resource(fixture.Source)));
        Assert.Equal(ThreadSourceState.Active, thread.Summary.SourceState);
        Assert.Equal(new[] { "123", "124" }, thread.GetPosts().Select(post => post.PostId));
        var attachments = thread.GetPosts()[0].Attachments!;
        Assert.Equal(new[] { "123-0", "123-1" }, attachments.Select(row => row.MediaId));
        Assert.Equal(new[] { 0, 1 }, attachments.Select(row => row.Position));
        Assert.Equal(fixture.OriginalUrl(0), attachments[0].Url);
        Assert.Equal((long)fixture.Landscape.Length, attachments[0].ExpectedSize);
        if (fixture.Source == "sushichan") Assert.Equal(Convert.ToBase64String(MD5.HashData(fixture.Landscape)), attachments[0].ExpectedMd5);
        else Assert.Equal(Hash(fixture.Landscape), attachments[0].ExpectedSha256, ignoreCase: true);
        var snapshot = await MediaAssetReader.OpenAsync(thread.Summary.MediaDirectory);
        var resolved = Assert.IsType<ResolvedMediaAttachment>(MediaAttachmentResolver.Resolve(snapshot, Resource(fixture.Source), "123", attachments[0]));
        Assert.False(resolved.IsLegacy);
        var binding = Assert.IsType<MediaAttachmentBinding>(resolved.Binding);
        Assert.Equal(attachments[0].AttachmentRevisionKey, binding.Reference.RevisionKey);
        Assert.Equal(Resource(fixture.Source), binding.Reference.Resource);
        Assert.Equal("123", binding.Reference.PostId);
        Assert.Equal(0, binding.Reference.Position);
        Assert.Equal(MediaAcceptanceKind.Strict, binding.Acceptance.Kind);
        Assert.True(ProviderIntegrityAssessment.Assess(binding.Provider, binding.Actual).IsStrictMatch);
        Assert.Equal(Hash(fixture.Landscape), binding.Actual.Sha256, ignoreCase: true);
        Assert.Equal(fixture.Landscape.Length, binding.Actual.Length);
        await using (Stream verified = await snapshot.OpenVerifiedReadAsync(binding.AssetId))
        {
            using MemoryStream bytes = new(); await verified.CopyToAsync(bytes);
            Assert.Equal(fixture.Landscape, bytes.ToArray());
        }
        Assert.Null(MediaAttachmentResolver.Resolve(snapshot, Resource(fixture.Source), "123", attachments[1]));
        Assert.Single(snapshot.GetPage());
        var gallery = GallerySelection.Build(thread, [snapshot]);
        Assert.Empty(gallery.Warnings);
        GalleryEntry entry = Assert.Single(gallery.Entries);
        Assert.False(entry.Unassigned);
        Assert.Equal("123", entry.Post!.PostId);
        Assert.Equal(0, entry.Attachment!.Position);
        Assert.Equal(binding, entry.AttachmentResolution!.Binding);
        return binding;
    }

    private async Task StartAsync(DesktopSessionHost host)
    {
        Stopwatch timer = Stopwatch.StartNew();
        await host.StartAsync().WaitAsync(Deadline);
        output.WriteLine($"SOURCE_WAVE_HOST_STARTUP {host.State} {timer.ElapsedMilliseconds}ms {host.StartupError}");
        Assert.True(host.State == DesktopSessionState.Ready, host.StartupError);
        Assert.Empty(host.Plugins!.Warnings);
    }
    private static async Task AssertDeliveredAsync(DesktopSessionHost host, string owner)
    {
        var plugin = (await host.Plugins!.ProfileStore.ReadAsync()).Plugins.Single(row => row.PluginId == owner);
        var candidate = Assert.Single(plugin.Candidates);
        var bundle = JsonSerializer.Deserialize<PluginBundleManifest>(await File.ReadAllTextAsync(candidate.CandidatePath!))!;
        string delivered = Path.Combine(AppContext.BaseDirectory, "plugins", bundle.AssemblyRelativePath);
        string hash = Hash(await File.ReadAllBytesAsync(delivered));
        Assert.Equal(hash, plugin.Active!.Artifact.AssemblySha256, ignoreCase: true);
        Assert.Equal(hash, Hash(await File.ReadAllBytesAsync(plugin.Active.Artifact.AssemblyPath)), ignoreCase: true);
    }
    private static string Owner(string source) => "meowchan.source." + source;
    private static ResourceId Resource(string source) => new(source, "demo", "123");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static SourceMediaIntegrityPolicyView Policy(DesktopSessionHost host, string source) =>
        Assert.Single(host.GetSourceMediaIntegrityPolicies(), row => row.Snapshot.SourceId == source);
    private static bool Completed(DesktopSessionHost host) => host.GetThreadWorkSnapshots() is { Count: 1 } work
        && work[0].Media.Count == 2 && work[0].Media.Count(row => row.Status == MediaWorkStatus.Succeeded) == 1
        && work[0].Media.Count(row => row.Status == MediaWorkStatus.Excluded) == 1;
    private static void Configure(string profile) => new Config {
        DownloadPath = Path.Combine(profile, "media"), DownloadsPaused = true, SaveUrlsOnExit = true,
        AutomaticUpdateChecks = false, MaxParallelDownloads = 2, RecheckTimer = 3600, MinimumFreeSpaceGiB = 0
    }.SaveConfig(profile);
    private static DesktopSessionHost Create(string profile, SushiZzzWaveFixtures fixture) => new(profile,
        updateSessionFactory: (path, config) => new UpdateSession(path, config, fixture.Client(), disableEnvironment: () => "1"),
        pluginTransportFactory: fixture.Client);
    private static async Task WaitAsync(Func<bool> condition, Func<string> diagnostic)
    {
        using CancellationTokenSource timeout = new(Deadline);
        try { while (!condition()) await Task.Delay(25, timeout.Token); }
        catch (OperationCanceledException) { Assert.Fail("Source wave work did not settle: " + diagnostic()); }
    }
    private static async Task StopAsync(DesktopSessionHost host)
    {
        int[] processes = host.Plugins?.Runtime.GetSnapshots().Where(row => row.ProcessId.HasValue).Select(row => row.ProcessId!.Value).ToArray() ?? [];
        await host.DisposeAsync().AsTask().WaitAsync(Deadline);
        Assert.Equal(DesktopSessionState.Stopped, host.State);
        Assert.Empty(host.Plugins!.Runtime.GetSnapshots());
        foreach (int id in processes)
            try { using Process process = Process.GetProcessById(id); Assert.True(process.HasExited, "Worker remains alive: " + id); }
            catch (ArgumentException) { }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Persistence;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Installation;
using MeowChan.PluginSdk;
using MeowChan.Rules;
using MeowChan.Storage;

namespace MeowChan;

public partial class MainWindow
{
    internal async Task CompleteApphostPluginWorkflowSmokeAsync(ApphostPluginWorkflowSmoke fixture)
    {
        if (fixture.SourceActivationPhase != null) { await CompleteApphostSourceActivationSmokeAsync(fixture); return; }
        const string filterId = "meowchan.example.image-dimensions";
        var descriptor = fixture.Descriptor;
        List<string> checks = new();
        bool completed = false;
        string? failure = null;
        object? sourceCompilation = null;
        Dictionary<string, string> revisions = new();
        int[] workerPids = Array.Empty<int>();
#pragma warning disable IL3000
        var managedAssemblies = new[] { typeof(App).Assembly, typeof(Registry).Assembly }
            .Select(assembly => new { name = assembly.GetName().Name, path = assembly.Location,
                moduleVersionId = assembly.ManifestModule.ModuleVersionId.ToString("D"),
                sha256 = string.IsNullOrEmpty(assembly.Location) ? null
                    : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant() }).ToArray();
#pragma warning restore IL3000
        try
        {
            await _restoreTask;
            RequireWorkflow(_restoreCompleted && IsVisible && _session.State == DesktopSessionState.Ready, _session.StartupError ?? "Plugin session not ready.");
            RequireWorkflow(Math.Abs(RenderScaling - descriptor.ExpectedScale) < .01, "Fixture display scale differs.");
            var plugins = _session.Plugins ?? throw new InvalidOperationException("Provider injection bypassed PluginSession.");
            RequireWorkflow(plugins.Warnings.Count == 0, string.Join("; ", plugins.Warnings));
            string app = AppContext.BaseDirectory;
            foreach (string helper in new[] { "plugin-worker/MeowChan.PluginWorker", "plugin-compiler/MeowChan.PluginCompiler" })
                RequireWorkflow(File.Exists(Path.Combine(app, helper)), "Delivered helper missing: " + helper);
            string[] manifests = Directory.GetFiles(Path.Combine(app, "plugins"), "*.plugin.json");
            PluginProfileSnapshot installed = await plugins.ProfileStore.ReadAsync();
            RequireWorkflow(manifests.Length == 24, "Delivered manifest inventory differs.");
            RequireApphostBundledInventory(plugins, installed);
            // This worker/download fixture explicitly enables its delivered sources through
            // the Host lifecycle. It does not grant a provider-integrity exception.
            installed = await EnableApphostFixtureSourcesAsync(plugins, installed);
            foreach (PluginActivationSnapshot plugin in installed.Plugins)
            {
                RequireWorkflow(plugin.Enabled == ApphostFixturePluginEnabled(plugin.PluginId) && plugin.Active != null && plugin.Problem == null && plugin.Candidates.Count == 1, "Plugin activation failed: " + plugin.PluginId);
                var candidate = plugin.Candidates.Single();
                RequireWorkflow(manifests.Contains(candidate.CandidatePath), "Activation did not use delivered manifest.");
                var manifest = JsonSerializer.Deserialize<PluginBundleManifest>(await File.ReadAllTextAsync(candidate.CandidatePath!))!;
                string hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(app, "plugins", manifest.AssemblyRelativePath))));
                RequireWorkflow(hash.Equals(plugin.Active!.Artifact.AssemblySha256, StringComparison.OrdinalIgnoreCase)
                    && hash == Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(plugin.Active.Artifact.AssemblyPath))), "Cached plugin differs from delivered bytes.");
                revisions.Add(plugin.PluginId, plugin.Active.ExecutionRevision);
            }
            checks.Add("delivered-plugins-activated");
            if (descriptor.Phase == "download")
            {
                string compilerSource = Path.Combine(descriptor.Profile, "offline-compiler-probe.cs");
                string deliveredSource = Path.Combine(app, "plugins", "sources", "Lainchan.cs");
                File.Copy(deliveredSource, compilerSource);
                var compiled = await plugins.CompilationService.BuildAsync(compilerSource);
                RequireWorkflow(compiled.Succeeded && !compiled.CacheHit, "Delivered compiler did not compile uncached source: " + string.Join("; ", compiled.Diagnostics));
                checks.Add("delivered-compiler-uncached");
                PluginArtifact compiledArtifact = compiled.Artifact!;
                var description = await plugins.DescribeTestAsync(compiledArtifact);
                RequireWorkflow(description.PluginId == "meowchan.source.lainchan" && description.SdkVersion == SdkContract.Version
                    && description.Sources.Count == 1 && description.Sources[0].SourceId == "lainchan", "Compiled delivered source did not describe the expected plugin.");
                checks.Add("delivered-plugin-source-described");
                var cached = await plugins.CompilationService.BuildAsync(compilerSource);
                RequireWorkflow(cached.Succeeded && cached.CacheHit && cached.Artifact is { } cachedArtifact
                    && cachedArtifact.ExecutionRevision == compiledArtifact.ExecutionRevision
                    && cachedArtifact.AssemblySha256 == compiledArtifact.AssemblySha256, "Unchanged delivered plugin source did not reuse its build.");
                checks.Add("delivered-plugin-source-cache-hit");
                sourceCompilation = new
                {
                    source = "plugins/sources/Lainchan.cs", pluginId = description.PluginId, sdkVersion = description.SdkVersion,
                    sourceSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(compilerSource))).ToLowerInvariant(),
                    executionRevision = compiledArtifact.ExecutionRevision, assemblySha256 = compiledArtifact.AssemblySha256,
                    uncachedBuild = !compiled.CacheHit, describedInWorker = true, cachedBuild = cached.CacheHit,
                    acquisition = "Shipped prebuilt bundles; compiled source is described in a separate test worker."
                };
                foreach (string source in ApphostPluginFixtures.WorkflowSources)
                    installed = await plugins.ProfileStore.SetGrantAsync(filterId, "dimensions", new(source, "demo"),
                        FilterDataAccess.CandidateMetadata | FilterDataAccess.OriginalImage, installed.Revision);
                await _session.SaveRulesAsync(new[] { new MediaRule(MediaRuleScope.Global)
                {
                    ImageFilter = new(MediaImageFilterMode.Plugin, filterId, "dimensions", parameters: new Dictionary<string, string>
                    { ["minimumWidth"] = "4", ["orientation"] = "landscape" })
                } }, _session.MediaRules.Revision);
                foreach (string source in ApphostPluginFixtures.WorkflowSources)
                    RequireWorkflow((await _session.AddAsync(ApphostPluginFixtures.ThreadUrl(source))).Status == AdmissionStatus.Added, "Plugin URL admission failed.");
                await _session.SetPausedAsync(false);
                await WaitWorkflowAsync(() => PluginWorkflowCompleted(ApphostPluginFixtures.WorkflowSources), "delivered sources/filter/download");
                var workers = plugins.Runtime.GetSnapshots();
                RequireWorkflow(workers.Count == 5 && workers.All(worker => worker.ProcessId is > 0)
                    && ApphostPluginFixtures.WorkflowSources.Select(source => revisions["meowchan.source." + source])
                        .Append(revisions[filterId]).ToHashSet(StringComparer.Ordinal)
                        .SetEquals(workers.Select(worker => worker.Identity.ExecutionRevision)), "Delivered source/filter worker missing.");
                RequireWorkflow(fixture.Http.Requests.Count == 12 && fixture.Http.MediaRequests == 8
                    && fixture.Http.Requests.GroupBy(uri => uri).All(group => group.Count() == 1), "Fixture requests differ or media was downloaded twice.");
                checks.Add("real-source-and-filter-workers");
                await _session.SetPausedAsync(true);
            }
            else
            {
                string compilerSource = Path.Combine(descriptor.Profile, "offline-compiler-probe.cs");
                var cached = await plugins.CompilationService.BuildAsync(compilerSource);
                RequireWorkflow(cached.Succeeded && cached.CacheHit, "Offline restart did not reuse the compiled delivered plugin source.");
                PluginArtifact cachedArtifact = cached.Artifact!;
                sourceCompilation = new
                {
                    source = "plugins/sources/Lainchan.cs",
                    sourceSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(compilerSource))).ToLowerInvariant(),
                    executionRevision = cachedArtifact.ExecutionRevision, assemblySha256 = cachedArtifact.AssemblySha256,
                    cachedBuild = cached.CacheHit
                };
                checks.Add("delivered-plugin-source-offline-cache-hit");
                RequireWorkflow(_session.IsPaused && PluginWorkflowCompleted(ApphostPluginFixtures.WorkflowSources) && installed.Grants.Count == 4, "Paused work/filter grants were not restored.");
                RequireWorkflow(plugins.Runtime.GetSnapshots().Count == 0 && fixture.Http.Requests.IsEmpty && _session.ReceivedMediaBytes == 0, "Paused offline restart attempted work.");
                checks.Add("paused-offline-restore");
            }
            await using (ArchiveReader reader = await _session.OpenArchiveReaderAsync())
            {
                foreach (string source in ApphostPluginFixtures.WorkflowSources)
                {
                    var view = (await reader.ReadThreadAsync(new(source, "demo", "123")))!;
                    RequireWorkflow(view != null && view.GetPosts().Select(post => post.PostId).SequenceEqual(new[] { "123", "124" }), "Archived posts differ.");
                    var attachments = view!.GetPosts()[0].Attachments!.ToArray();
                    RequireWorkflow(attachments.Length == 2 && File.ReadAllBytes(attachments[0].PlannedPath!).SequenceEqual(fixture.Http.Landscape)
                        && !File.Exists(attachments[1].PlannedPath), "Filter match/no-match output differs.");
                    var media = await MediaAssetReader.OpenAsync(view.Summary.MediaDirectory);
                    var saved = media.GetPage().Single();
                    RequireWorkflow(saved.MediaIdentifier == "123-0" && saved.Sha256.Equals(Convert.ToHexString(SHA256.HashData(fixture.Http.Landscape)), StringComparison.OrdinalIgnoreCase), "Archive media inventory differs.");
                }
            }
            checks.Add("archive-verified");
            workerPids = plugins.Runtime.GetSnapshots().Select(worker => worker.ProcessId!.Value).ToArray();
            await CloseWorkflowNormallyAsync();
            RequireWorkflow(_session.State == DesktopSessionState.Stopped && !IsVisible && _allowClose, "Normal shutdown incomplete.");
            RequireWorkflow(plugins.Runtime.GetSnapshots().Count == 0 && workerPids.All(pid => !PluginWorkflowProcessAlive(pid)), "Plugin worker survived shutdown.");
            foreach (string directory in new[] { "plugin-workers", "filter-stages" })
            {
                string path = Path.Combine(descriptor.Profile, directory);
                RequireWorkflow(!Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any(), "Plugin staging survived shutdown.");
            }
            checks.Add("normal-shutdown");
            using (ProfileLease lease = ProfileLease.Acquire(descriptor.Profile))
            {
                await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(descriptor.Profile, lease);
                foreach (string source in ApphostPluginFixtures.WorkflowSources)
                {
                    var decisions = await catalog.GetDecisionsAsync(new(source, "demo", "123"));
                    var excluded = decisions.Where(decision => decision.Selection.Reason == MediaSelectionReason.FilterNoMatch).ToArray();
                    RequireWorkflow(decisions.Count == 2 && excluded.Length == 1 && excluded[0].Metadata.Key.MediaId == "123-1"
                        && excluded[0].Selection.FilterReason == "orientation_mismatch", "Durable filter decision differs.");
                }
            }
            checks.Add("filter-match-and-no-match-durable"); checks.Add("lease-released");
            RequireWorkflow(fixture.Http.Unexpected == 0, "Unexpected fixture request.");
            completed = true;
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            File.WriteAllText(Path.Combine(descriptor.Profile, "apphost-workflow.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, completed, failure, phase = descriptor.Phase, checks, revisions, workerPids,
                managedAssemblies, sourceCompilation, processPath = Environment.ProcessPath,
                processSha256 = Environment.ProcessPath == null ? null
                    : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Environment.ProcessPath))).ToLowerInvariant(),
                processId = Environment.ProcessId, renderScaling = RenderScaling, appBaseDirectory = AppContext.BaseDirectory,
                profile = descriptor.Profile, workingDirectory = Environment.CurrentDirectory,
                assemblyInformationalVersion = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                requests = fixture.Http.Requests.ToArray(), unexpectedRequests = fixture.Http.Unexpected,
                scope = "Published apphost, providers:null, delivered PluginSession sources/filter/helpers, terminal offline fixture HTTP, explicit fixture production worker capacity 5; no live-source claim."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    // Exact current delivery contract, not an enabled-plugin count: 16 chan adapters,
    // one post-search booru provider, five filters and two route-less blocked scaffolds.
    internal static readonly string[] ApphostBundledPluginIds =
    {
        "meowchan.official.4chan", "meowchan.official.8kun", "meowchan.official.danbooru",
        "meowchan.source.lainchan", "meowchan.source.kohlchan", "meowchan.source.wizchan",
        "meowchan.source.endchan", "meowchan.example.image-dimensions", "meowchan.filter.file-rules",
        "meowchan.filter.hash-blocklist", "meowchan.filter.animation-format", "meowchan.filter.png-metadata",
        "meowchan.source.leftypol", "meowchan.source.2ch", "meowchan.source.uboachan",
        "meowchan.source.zzzchan", "meowchan.source.sushichan", "meowchan.source.sevenchan",
        "meowchan.source.bbwchan", "meowchan.source.anonimageboards", "meowchan.source.capybarachan",
        "meowchan.source.eightchanmoe", "meowchan.source.fchan", "meowchan.source.gurocx",
    };
    internal static readonly string[] ApphostDefaultOffPluginIds =
    {
        "meowchan.source.kohlchan", "meowchan.source.wizchan", "meowchan.source.endchan",
        "meowchan.source.leftypol", "meowchan.source.2ch", "meowchan.source.uboachan",
        "meowchan.source.zzzchan", "meowchan.source.sushichan", "meowchan.source.sevenchan",
        "meowchan.source.bbwchan", "meowchan.source.anonimageboards", "meowchan.source.eightchanmoe",
        "meowchan.source.gurocx", "meowchan.source.fchan", "meowchan.source.capybarachan",
    };
    internal static readonly string[] ApphostChanSourceIds = { "2ch", "4chan", "8kun", "bbwchan", "capybarachan", "eightchanmoe", "endchan", "fchan", "kohlchan", "lainchan", "leftypol", "sevenchan", "sushichan", "uboachan", "wizchan", "zzzchan" };

    internal static bool ApphostFixturePluginEnabled(string id) =>
        !ApphostDefaultOffPluginIds.Contains(id, StringComparer.Ordinal)
        || id is "meowchan.source.kohlchan" or "meowchan.source.wizchan" or "meowchan.source.endchan";

    internal static void RequireApphostBundledInventory(PluginSession plugins, PluginProfileSnapshot installed)
    {
        RequireWorkflow(installed.Plugins.Select(plugin => plugin.PluginId).Order().SequenceEqual(ApphostBundledPluginIds.Order()),
            "Delivered plugin identity inventory differs.");
        RequireWorkflow(plugins.Sources.Select(source => source.Name).Order().SequenceEqual(ApphostChanSourceIds),
            "Delivered chan source inventory differs.");
        RequireWorkflow(plugins.Booru != null, "Delivered Danbooru binding missing.");
        foreach (var plugin in installed.Plugins)
        {
            var description = plugin.Active?.Description;
            RequireWorkflow(description != null, "Delivered declaration unavailable: " + plugin.PluginId);
            bool blocked = plugin.PluginId is "meowchan.source.anonimageboards" or "meowchan.source.gurocx";
            RequireWorkflow((description!.DeclarationStatus == PluginDeclarationStatus.Blocked) == blocked,
                "Delivered declaration status differs: " + plugin.PluginId);
            if (blocked)
                RequireWorkflow(description.Sources.Count == 0 && description.Filters.Count == 0,
                    "Blocked scaffold registered capabilities: " + plugin.PluginId);
            else if (plugin.PluginId == "meowchan.official.danbooru")
                RequireWorkflow(description.Sources.Count == 1 && description.Sources[0].SourceId == "danbooru"
                    && description.Sources[0].Capabilities.HasFlag(SourceCapabilities.PostSearch) && description.Filters.Count == 0,
                    "Delivered booru declaration differs.");
            else if (plugin.PluginId.StartsWith("meowchan.filter.", StringComparison.Ordinal) || plugin.PluginId == "meowchan.example.image-dimensions")
                RequireWorkflow(description.Sources.Count == 0 && description.Filters.Count == 1, "Delivered filter declaration differs.");
            else
                RequireWorkflow(description.Sources.Count == 1 && !description.Sources[0].Capabilities.HasFlag(SourceCapabilities.PostSearch)
                    && description.Filters.Count == 0, "Delivered chan declaration differs.");
        }
    }

    private static async Task<PluginProfileSnapshot> EnableApphostFixtureSourcesAsync(PluginSession plugins, PluginProfileSnapshot installed)
    {
        foreach (string owner in new[] { "meowchan.source.kohlchan", "meowchan.source.wizchan", "meowchan.source.endchan" })
            if (!installed.Plugins.Single(plugin => plugin.PluginId == owner).Enabled)
                installed = await plugins.ProfileStore.SetEnabledAsync(owner, true, installed.Revision);
        return installed;
    }

    private bool PluginWorkflowCompleted(IReadOnlyCollection<string> expectedSourceIds)
    {
        var work = _session.GetThreadWorkSnapshots();
        var expectedUrls = expectedSourceIds.Select(ApphostPluginFixtures.ThreadUrl).ToHashSet(StringComparer.Ordinal);
        return work.Count == expectedUrls.Count && expectedUrls.SetEquals(work.Select(resource => resource.Url))
            && work.All(resource => resource.Media.Count == 2
            && resource.Media.Count(media => media.Status == MediaWorkStatus.Succeeded) == 1
            && resource.Media.Count(media => media.Status == MediaWorkStatus.Excluded) == 1);
    }
    private static bool PluginWorkflowProcessAlive(int pid)
    {
        try { using Process process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
}

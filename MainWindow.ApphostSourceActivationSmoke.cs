using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Plugins;
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
    private sealed record AuthorCandidateEvidence(string PluginId, string DeliveredSource, string LocalSource,
        string CandidateId, string SourceSha256, string AssemblySha256, string ExecutionRevision,
        bool CacheHit, string ActiveCandidateId, string ActiveExecutionRevision);
    private static readonly JsonSerializerOptions AuthorJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };

    internal async Task CompleteApphostSourceActivationSmokeAsync(ApphostPluginWorkflowSmoke fixture)
    {
        var descriptor = fixture.Descriptor;
        string phase = fixture.SourceActivationPhase!;
        string evidencePath = Path.Combine(descriptor.Profile, "apphost-workflow.json");
        List<string> checks = new();
        List<AuthorCandidateEvidence> candidates = new();
        bool completed = false;
        string? failure = null;
        int[] workerPids = Array.Empty<int>();
        Dictionary<string, string> revisions = new();
        bool? startupPaused = null;
        string[] startupWarnings = Array.Empty<string>();
        PluginProfileSnapshot? startupProfile = null;
#pragma warning disable IL3000
        var managedAssemblies = new[] { typeof(App).Assembly, typeof(Registry).Assembly }.Select(assembly => new
        {
            name = assembly.GetName().Name, path = assembly.Location,
            moduleVersionId = assembly.ManifestModule.ModuleVersionId.ToString("D"),
            sha256 = string.IsNullOrEmpty(assembly.Location) ? null : AuthorHash(assembly.Location)
        }).ToArray();
#pragma warning restore IL3000
        try
        {
            await _restoreTask;
            RequireWorkflow(_restoreCompleted && IsVisible && _session.State == DesktopSessionState.Ready,
                _session.StartupError ?? "Source activation session is not ready.");
            RequireWorkflow(Math.Abs(RenderScaling - descriptor.ExpectedScale) < .01, "Source activation scaling differs.");
            var plugins = _session.Plugins ?? throw new InvalidOperationException("Source activation bypassed PluginSession.");
            startupPaused = _session.IsPaused;
            startupWarnings = plugins.Warnings.ToArray();
            PluginProfileSnapshot installed = await plugins.ProfileStore.ReadAsync();
            startupProfile = installed;
            RequireAuthorStartupState(installed, startupWarnings, startupPaused.Value, phase);
            foreach (var plugin in installed.Plugins)
            {
                RequireWorkflow(plugin.Enabled && plugin.Active != null, "Source activation inventory is unavailable: " + plugin.PluginId);
                revisions[plugin.PluginId] = plugin.Active!.ExecutionRevision;
            }
            if (phase == "install")
            {
                string lainchan = Path.Combine(plugins.PluginDirectory, "Author Lainchan source.cs");
                File.Copy(Path.Combine(AppContext.BaseDirectory, "plugins", "sources", "Lainchan.cs"), lainchan);
                string filter = await WriteAuthorFilterSourceAsync(plugins.PluginDirectory);
                foreach ((string local, string delivered, string pluginId) in new[]
                {
                    (lainchan, "plugins/sources/Lainchan.cs", "meowchan.source.lainchan"),
                    (filter, "plugins/sources/ImageDimensionsFilter.cs", AuthorFilterId)
                })
                {
                    PluginActivationSnapshot original = installed.Plugins.Single(item => item.PluginId == pluginId);
                    var inventory = await plugins.Inventory.ScanAsync(plugins.PluginDirectory);
                    PluginInstallationCandidate candidate = inventory.Candidates.Single(item => ApphostWorkflowSmoke.SamePath(item.Path, local));
                    RequireWorkflow(candidate.Kind == PluginCandidateKind.Source && candidate.Diagnostics.Count == 0, "Installed .cs candidate is not inventory-visible.");
                    var built = await plugins.CompilationService.BuildAsync(local);
                    RequireWorkflow(built.Succeeded && !built.CacheHit && built.Artifact != null, "Author source did not compile uncached: " + string.Join("; ", built.Diagnostics));
                    PluginArtifact artifact = built.Artifact!;
                    PluginDescription description = await plugins.DescribeTestAsync(artifact);
                    RequireWorkflow(description.PluginId == pluginId && description.SdkVersion == SdkContract.Version, "Author .cs identity differs.");
                    PluginConfiguration config = PluginSettingsMigration.Migrate(null, description).Configuration!;
                    await using (IPluginWorkerClient worker = await plugins.StartTestWorkerAsync(artifact, config))
                    {
                        RequireWorkflow((await worker.ValidateSettingsAsync(new(SettingScope.Plugin, config.PluginSettings), CancellationToken.None)).Count == 0, "Author plugin settings test failed.");
                        foreach (var source in config.SourceSettings)
                            RequireWorkflow((await worker.ValidateSettingsAsync(new(SettingScope.Source, source.Value, SourceId: source.Key), CancellationToken.None)).Count == 0, "Author source settings test failed.");
                        if (pluginId == "meowchan.source.lainchan")
                        {
                            object probe = await PluginProbe.ReadAsync(description, ApphostPluginFixtures.ThreadUrl("lainchan"), worker, new("author-probe", 1), CancellationToken.None);
                            RequireWorkflow(probe is MeowChan.PluginSdk.ThreadSnapshot thread && thread.Snapshot.Posts.Count == 2, "Author source probe did not return normalized posts.");
                        }
                    }
                    installed = await plugins.ProfileStore.ObserveCandidateAsync(new(candidate.CandidateId, pluginId, artifact, description, CandidatePath: local), installed.Revision);
                    RequireWorkflow(installed.Plugins.Single(item => item.PluginId == pluginId).Active!.ExecutionRevision == original.Active!.ExecutionRevision,
                        "Build/test implicitly activated the author candidate.");
                    installed = await plugins.ProfileStore.SelectCandidateAsync(pluginId, candidate.CandidateId, installed.Revision);
                    var selected = installed.Plugins.Single(item => item.PluginId == pluginId);
                    RequireWorkflow(selected.Active!.CandidateId == original.Active!.CandidateId && selected.Pending is { } pending && pending.CandidateId == candidate.CandidateId
                        && pending.ExecutionRevision == artifact.ExecutionRevision, "Explicit selection did not preserve active and stage the exact author build.");
                    candidates.Add(new(pluginId, delivered, Path.GetRelativePath(descriptor.Profile, local).Replace('\\', '/'),
                        candidate.CandidateId, AuthorHash(local), artifact.AssemblySha256.ToLowerInvariant(), artifact.ExecutionRevision,
                        built.CacheHit, selected.Active.CandidateId, selected.Active.ExecutionRevision));
                }
                RequireWorkflow(_session.GetThreadWorkSnapshots().Count == 0 && fixture.Http.MediaRequests == 0 && fixture.Http.Requests.Count == 1,
                    "Isolated author test/probe created production work or original downloads.");
                await using (ArchiveReader reader = await _session.OpenArchiveReaderAsync())
                    foreach (string source in ApphostPluginFixtures.AuthorSources)
                        RequireWorkflow(await reader.ReadThreadAsync(new(source, "demo", "123")) == null, "Isolated author probe archived posts.");
                RequireWorkflow(plugins.Runtime.GetSnapshots().Count == 0, "Test worker survived author validation.");
                checks.AddRange(new[] { "author-source-installed", "author-build-test-probe-isolated", "author-explicit-pending-selection" });
            }
            else
            {
                using var prior = JsonDocument.Parse(await File.ReadAllTextAsync(evidencePath));
                var expected = JsonSerializer.Deserialize<AuthorCandidateEvidence[]>(prior.RootElement.GetProperty("candidates").GetRawText(), AuthorJson)!;
                RequireWorkflow(expected.Length == 2, "Prior author source proofs are incomplete.");
                foreach (var previous in expected)
                {
                    var active = installed.Plugins.Single(item => item.PluginId == previous.PluginId).Active!;
                    string local = Path.Combine(descriptor.Profile, previous.LocalSource);
                    var cached = await plugins.CompilationService.BuildAsync(local);
                    RequireWorkflow(cached.Succeeded && cached.CacheHit && cached.Artifact?.ExecutionRevision == previous.ExecutionRevision,
                        "Restart did not reuse the exact author source cache.");
                    RequireWorkflow(active.CandidateId == previous.CandidateId && active.ExecutionRevision == previous.ExecutionRevision
                        && active.Artifact.AssemblySha256.Equals(previous.AssemblySha256, StringComparison.OrdinalIgnoreCase)
                        && AuthorHash(local) == previous.SourceSha256 && AuthorHash(active.Artifact.AssemblyPath) == previous.AssemblySha256,
                        "Restart activated a different candidate, revision, or source/assembly byte set.");
                    candidates.Add(previous with { CacheHit = true, ActiveCandidateId = active.CandidateId, ActiveExecutionRevision = active.ExecutionRevision });
                }
                checks.Add("author-source-cache-hit-on-restart");
                if (phase == "acquire")
                {
                    await ConfigureAuthorFilterThroughTemplateAsync();
                    foreach (string source in ApphostPluginFixtures.AuthorSources)
                        installed = await plugins.ProfileStore.SetGrantAsync(AuthorFilterId, "dimensions", new(source, "demo"),
                            FilterDataAccess.CandidateMetadata | FilterDataAccess.OriginalImage, installed.Revision);
                    foreach (string source in ApphostPluginFixtures.AuthorSources)
                        RequireWorkflow((await _session.AddAsync(ApphostPluginFixtures.ThreadUrl(source))).Status == AdmissionStatus.Added, "Author source URL admission failed.");
                    await _session.SetPausedAsync(false);
                    await WaitWorkflowAsync(() => PluginWorkflowCompleted(ApphostPluginFixtures.AuthorSources), "compiled author source/filter acquisition");
                    var workers = plugins.Runtime.GetSnapshots();
                    RequireWorkflow(workers.Count == 3 && workers.All(worker => worker.ProcessId is > 0)
                        && candidates.All(candidate => workers.Any(worker => worker.Identity.ExecutionRevision == candidate.ExecutionRevision))
                        && workers.Any(worker => worker.Identity.ExecutionRevision == revisions["meowchan.source.kohlchan"]),
                        "Production did not execute both exact compiled author source/filter revisions.");
                    RequireWorkflow(fixture.Http.Requests.Count == 6 && fixture.Http.MediaRequests == 4
                        && fixture.Http.Requests.GroupBy(uri => uri).All(group => group.Count() == 1), "Author production HTTP/media request set differs.");
                    checks.Add("author-production-workers-download-filter");
                    await _session.SetPausedAsync(true);
                }
                else
                {
                    RequireWorkflow(PluginWorkflowCompleted(ApphostPluginFixtures.AuthorSources) && installed.Grants.Count == 2 && plugins.Runtime.GetSnapshots().Count == 0
                        && fixture.Http.Requests.IsEmpty && _session.ReceivedMediaBytes == 0, "Author offline restart attempted production/network work.");
                    var filter = _session.MediaRules.Rules.Single(rule => rule.Scope == MediaRuleScope.Global).ImageFilter;
                    RequireWorkflow(filter.PluginId == AuthorFilterId && filter.Parameters.Count == 4
                        && filter.Parameters["minimumWidth"] == "5" && filter.Parameters["minimumHeight"] == "0"
                        && filter.Parameters["orientation"] == "landscape" && filter.Parameters["aspectRatioTolerance"] == "0",
                        "Offline restart did not restore exact template-applied filter parameters.");
                    checks.Add("author-paused-offline-restart");
                }
                await using (ArchiveReader reader = await _session.OpenArchiveReaderAsync())
                    foreach (string source in ApphostPluginFixtures.AuthorSources)
                    {
                        var view = await reader.ReadThreadAsync(new(source, "demo", "123"));
                        RequireWorkflow(view != null && view.GetPosts().Select(post => post.PostId).SequenceEqual(new[] { "123", "124" }), "Author archive posts differ.");
                        var attachments = view!.GetPosts()[0].Attachments!.ToArray();
                        RequireWorkflow(attachments.Length == 2 && File.ReadAllBytes(attachments[0].PlannedPath!).SequenceEqual(fixture.Http.Landscape)
                            && !File.Exists(attachments[1].PlannedPath), "Author Match/NoMatch media differs.");
                        var media = await MediaAssetReader.OpenAsync(view.Summary.MediaDirectory);
                        var saved = media.GetPage().Single();
                        RequireWorkflow(saved.MediaIdentifier == "123-0" && saved.Sha256.Equals(Convert.ToHexString(SHA256.HashData(fixture.Http.Landscape)), StringComparison.OrdinalIgnoreCase), "Author archived original hash differs.");
                    }
                checks.Add("author-archive-verified");
            }
            workerPids = plugins.Runtime.GetSnapshots().Select(worker => worker.ProcessId!.Value).ToArray();
            await CloseWorkflowNormallyAsync();
            RequireWorkflow(_session.State == DesktopSessionState.Stopped && !IsVisible && _allowClose
                && plugins.Runtime.GetSnapshots().Count == 0 && workerPids.All(pid => !PluginWorkflowProcessAlive(pid)), "Author workflow shutdown left a session or worker.");
            foreach (string directory in new[] { "plugin-workers", "filter-stages" })
            {
                string path = Path.Combine(descriptor.Profile, directory);
                RequireWorkflow(!Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any(), "Author worker/staging files survived.");
            }
            using (ProfileLease lease = ProfileLease.Acquire(descriptor.Profile))
                if (phase != "install")
                {
                    await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(descriptor.Profile, lease);
                    foreach (string source in ApphostPluginFixtures.AuthorSources)
                    {
                        var decisions = await catalog.GetDecisionsAsync(new(source, "demo", "123"));
                        var excluded = decisions.Where(decision => decision.Selection.Reason == MediaSelectionReason.FilterNoMatch).ToArray();
                        RequireWorkflow(decisions.Count == 2 && excluded.Length == 1 && excluded[0].Metadata.Key.MediaId == "123-1"
                            && excluded[0].Selection.FilterReason == "below_minimum_dimensions", "Author durable filter decision differs.");
                    }
                    checks.Add("author-durable-filter-decisions");
                }
            checks.Add("author-normal-shutdown-and-lease");
            RequireWorkflow(fixture.Http.Unexpected == 0, "Author fixture made unexpected requests.");
            completed = true;
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(new
            {
                schemaVersion = 1, completed, failure, phase = "source-activation", sourcePhase = phase, checks, candidates,
                startup = new { paused = startupPaused, warnings = startupWarnings, plugins = startupProfile?.Plugins.Select(plugin => new
                {
                    plugin.PluginId, plugin.Enabled, plugin.Problem, plugin.SelectedCandidateId,
                    activePluginName = plugin.Active?.Description.Name, activeCandidateId = plugin.Active?.CandidateId, activeExecutionRevision = plugin.Active?.ExecutionRevision,
                    pendingCandidateId = plugin.Pending?.CandidateId,
                    presentCandidateIds = plugin.Candidates.Where(candidate => candidate.IsPresent).Select(candidate => candidate.CandidateId).ToArray()
                }).ToArray() },
                revisions, workerPids, managedAssemblies, profile = descriptor.Profile, workingDirectory = Environment.CurrentDirectory,
                processId = Environment.ProcessId, processPath = Environment.ProcessPath,
                processSha256 = Environment.ProcessPath == null ? null : AuthorHash(Environment.ProcessPath),
                renderScaling = RenderScaling, appBaseDirectory = AppContext.BaseDirectory,
                requests = fixture.Http.Requests.ToArray(), unexpectedRequests = fixture.Http.Unexpected,
                scope = "Same owned profile, delivered .cs bytes, explicit candidate selection, actual startup activation and production workers; terminal fixture HTTP, no live-source claim."
            }, AuthorJson));
        }
    }
    internal static void RequireAuthorStartupState(PluginProfileSnapshot installed, IReadOnlyList<string> warnings, bool isPaused, string phase)
    {
        RequireWorkflow(isPaused, "Source activation startup is not paused.");
        RequireWorkflow(phase is "install" or "acquire" or "offline", "Unknown author startup phase.");
        const string selectedAdvisory = "Multiple candidates exist; the selected candidate is retained.";
        string[] authorIds = { "meowchan.source.lainchan", AuthorFilterId };
        List<string> expectedWarnings = new();
        foreach (var plugin in installed.Plugins)
        {
            RequireWorkflow(plugin.Enabled && plugin.Active != null, "Source activation inventory is unavailable: " + plugin.PluginId);
            if (phase != "install" && authorIds.Contains(plugin.PluginId, StringComparer.Ordinal))
            {
                RequireWorkflow(plugin.Problem == selectedAdvisory && plugin.SelectedCandidateId == plugin.Active!.CandidateId
                    && plugin.Pending == null && plugin.Candidates.Count == 2
                    && plugin.Candidates.All(candidate => candidate.IsPresent && candidate.Problem == null),
                    "Author selection advisory/state differs: " + plugin.PluginId);
                expectedWarnings.Add(plugin.Active!.Description.Name + ": " + selectedAdvisory);
            }
            else RequireWorkflow(plugin.Problem == null, "Unexpected author startup problem: " + plugin.PluginId + ": " + plugin.Problem);
        }
        RequireWorkflow(phase == "install" || expectedWarnings.Count == 2
            && authorIds.All(id => installed.Plugins.Count(plugin => plugin.PluginId == id) == 1), "Both explicitly selected author candidates must be present.");
        RequireWorkflow(warnings.OrderBy(value => value, StringComparer.Ordinal).SequenceEqual(expectedWarnings.OrderBy(value => value, StringComparer.Ordinal)),
            "Author startup warning inventory differs: " + string.Join(" | ", warnings));
    }
    private static string AuthorHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}

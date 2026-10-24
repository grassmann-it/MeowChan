using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Updates;
using MeowChan.Persistence;
using MeowChan.PluginHost.Installation;
using MeowChan.Rules;
using MeowChan.Storage;
using Xunit.Abstractions;

namespace MeowChan.DesktopTests;

public sealed class LiveSourceAcceptanceTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["4chan"] = "4chan", ["8kun"] = "8kun", ["lainchan"] = "Lainchan",
        ["kohlchan"] = "Kohlchan", ["wizchan"] = "Wizchan", ["endchan"] = "Endchan"
    };
    private sealed record Manifest(int SchemaVersion, LiveSourceSelection[] Sources);

    [LiveSourcesFact]
    public async Task DeliveredSourcesReachLiveHttpOneOriginalAndFreshOfflineHost()
    {
        string manifestPath = Path.GetFullPath(Environment.GetEnvironmentVariable("MEOWCHAN_LIVE_SOURCES_MANIFEST")!);
        byte[] manifestBytes = await File.ReadAllBytesAsync(manifestPath);
        Manifest manifest = JsonSerializer.Deserialize<Manifest>(manifestBytes, Json)!;
        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal(6, manifest.Sources.Length);
        Assert.True(Names.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(manifest.Sources.Select(source => source.SourceId)));
        string evidence = Path.GetFullPath(Environment.GetEnvironmentVariable("MEOWCHAN_LIVE_SOURCES_EVIDENCE")
            ?? throw new InvalidOperationException("An explicit new technical-evidence directory is required."));
        Assert.False(Directory.Exists(evidence) || File.Exists(evidence) || new DirectoryInfo(evidence).LinkTarget != null);
        Directory.CreateDirectory(evidence);
        IReadOnlySet<string> selectedIds = SelectSourceIds(Environment.GetEnvironmentVariable("MEOWCHAN_LIVE_SOURCES_ONLY"));
        List<LiveSourceResult> results = new();
        foreach (LiveSourceSelection selection in manifest.Sources.Where(source => selectedIds.Contains(source.SourceId)))
        {
            LiveSourceResult result = await RunSourceAsync(selection);
            results.Add(result);
            string serialized = JsonSerializer.Serialize(result, Json);
            await File.WriteAllTextAsync(Path.Combine(evidence, selection.SourceId + ".json"), serialized);
            output.WriteLine("LIVE_SOURCE_RESULT " + serialized);
        }
        await File.WriteAllTextAsync(Path.Combine(evidence, "summary.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, completedUtc = DateTimeOffset.UtcNow,
            manifestSha256 = Hash(manifestBytes), testAssemblySha256 = Hash(File.ReadAllBytes(typeof(LiveSourceAcceptanceTests).Assembly.Location)),
            applicationAssemblySha256 = Hash(File.ReadAllBytes(typeof(DesktopSessionHost).Assembly.Location)),
            coreAssemblySha256 = Hash(File.ReadAllBytes(typeof(Registry).Assembly.Location)),
            selectedSourceIds = manifest.Sources.Where(source => selectedIds.Contains(source.SourceId)).Select(source => source.SourceId).ToArray(),
            allSixSourcesRequested = selectedIds.Count == 6,
            allSixPassed = selectedIds.Count == 6 && results.Count == 6 && results.All(result => result.Passed),
            passed = results.All(result => result.Passed), results,
            scope = "Real delivered parsers and production HTTP/downloader/archive; one selected original per source. Offline is a fresh DesktopSessionHost in the same test process. Other media may be blocked by test budget; no claim all thread media completed. Private profiles and content are deleted."
        }, Json));
        Assert.True(results.All(result => result.Passed), "Some live phases did not pass; inspect the technical per-source reports.");
    }

    private static async Task<LiveSourceResult> RunSourceAsync(LiveSourceSelection selection)
    {
        LiveSourceResult result = new(selection.SourceId, selection.PluginId, selection.BoardId, selection.ThreadId, selection.MediaId)
        { ExpectedMd5Base64 = selection.ExpectedMd5Base64 };
        string profile = Path.Combine(Path.GetTempPath(), "meowchan-live-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        DesktopSessionHost? host = null, offlineHost = null;
        LiveSourceAuditTransport? live = null, offline = null;
        Dictionary<string, string>? revisions = null;
        string stage = "startup";
        try
        {
            new Config
            {
                DownloadPath = Path.Combine(profile, "media"), DownloadsPaused = true, SaveUrlsOnExit = true,
                AutomaticUpdateChecks = false, MinimizeToTray = false, CloseToTray = false,
                MaxParallelDownloads = 1, RecheckTimer = 3600, MinimumFreeSpaceGiB = 0
            }.SaveConfig(profile);
            live = new(selection);
            host = Create(profile, live);
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(45));
            Assert.Equal(DesktopSessionState.Ready, host.State);
            var plugins = host.Plugins!;
            Assert.Empty(plugins.Warnings);
            foreach ((string id, string name) in Names)
                result.DescribedNames[id] = Assert.Single(plugins.Sources, source => source.Name == id).NiceName;
            Assert.Equal(Names.OrderBy(row => row.Key), result.DescribedNames.OrderBy(row => row.Key));
            var installed = await plugins.ProfileStore.ReadAsync();
            Assert.Equal(7, installed.Plugins.Count);
            revisions = installed.Plugins.ToDictionary(plugin => plugin.PluginId, plugin => plugin.Active!.ExecutionRevision);
            var active = Assert.Single(installed.Plugins, plugin => plugin.PluginId == selection.PluginId).Active!;
            Assert.Equal(Names[selection.SourceId], active.Description.Name);
            result.ExecutionRevision = active.ExecutionRevision;
            result.ActiveAssemblySha256 = active.Artifact.AssemblySha256;
            var candidate = Assert.Single(installed.Plugins.Single(plugin => plugin.PluginId == selection.PluginId).Candidates);
            byte[] bundleBytes = await File.ReadAllBytesAsync(candidate.CandidatePath!);
            PluginBundleManifest bundle = JsonSerializer.Deserialize<PluginBundleManifest>(bundleBytes)!;
            string delivered = Path.Combine(AppContext.BaseDirectory, "plugins", bundle.AssemblyRelativePath);
            result.BundleManifestSha256 = Hash(bundleBytes);
            result.DeliveredAssemblySha256 = Hash(await File.ReadAllBytesAsync(delivered));
            Assert.Equal(result.DeliveredAssemblySha256, active.Artifact.AssemblySha256, ignoreCase: true);
            Assert.Equal(result.DeliveredAssemblySha256, Hash(await File.ReadAllBytesAsync(active.Artifact.AssemblyPath)), ignoreCase: true);
            var adapter = Assert.Single(plugins.Sources, source => source.Name == selection.SourceId);
            Assert.True(adapter.IsBoardUri(new Uri(selection.BoardUrl)));
            Assert.True(adapter.IsThreadUri(new Uri(selection.ThreadUrl)));
            Assert.Equal(0, live.ActualNetworkSends);
            result.Outcomes[stage] = "PASS";
            live.BeforeOriginal = async token =>
            {
                await ReadSelectedAsync(host, selection, token);
                token.ThrowIfCancellationRequested();
                result.ThreadCaptured = true;
                result.Outcomes["thread"] = "PASS";
                result.SelectedParsedAttachmentMatched = true;
            };
            await host.SaveRulesAsync(new[] { new MediaRule(MediaRuleScope.Global,
                types: MediaTypeSelection.All,
                sizeLimit: new(MediaSizeLimitMode.Limited, selection.ExpectedBytes)) }, host.MediaRules.Revision);
            await host.SetPausedAsync(false);
            stage = "catalog";
            try
            {
                using CancellationTokenSource catalogDeadline = new(TimeSpan.FromSeconds(30));
                var catalog = await host.PreviewBoardAdmissionAsync(selection.BoardUrl, catalogDeadline.Token);
                result.CatalogThreadCount = catalog.Threads.Count;
                result.CatalogContainsSelectedThread = catalog.Threads.Any(thread => thread.Id == selection.ThreadId);
                foreach (var item in catalog.Threads)
                {
                    Assert.True(adapter.Router.TryResolve(new Uri(item.Url), out var kind, out string board, out string? thread, out Uri? canonical));
                    Assert.Equal(MeowChan.PluginSdk.SourceResourceKind.Thread, kind);
                    Assert.Equal(selection.BoardId, board);
                    Assert.Equal(item.Id, thread);
                    Assert.Equal(item.Url, canonical!.AbsoluteUri);
                }
                result.Outcomes[stage] = catalog.Threads.Count == 0 ? "catalog-readable-but-empty" : "PASS";
            }
            catch (Exception error) { result.Outcomes[stage] = Classify("catalog", error, live); }
            Assert.Empty(host.GetView().Threads);
            Assert.Empty(host.GetView().Boards);
            await RunThreadPhaseAfterCatalogAsync(result, live.Audit.ToArray(), async () =>
            {
                stage = "thread-and-original";
                Assert.Equal(AdmissionStatus.Added, (await AdmitInitialThreadAsync(selection.SourceId, () => host.AddAsync(selection.ThreadUrl))).Status);
                await WaitForSelectedAsync(host, selection, live);
                await host.SetPausedAsync(true);
                result.ThreadCaptured = true;
                result.Outcomes["thread"] = "PASS";
                result.OriginalBytes = live.OriginalBytes;
                result.OriginalSha256 = live.OriginalSha256;
                Assert.Equal(selection.ExpectedBytes, result.OriginalBytes);
                if (selection.ExpectedMd5Base64 != null) Assert.Equal(selection.ExpectedMd5Base64, live.OriginalMd5Base64);
                Assert.Equal(1, live.Audit.Count(row => row.Kind == "original" && row.Status == 200));
                result.Outcomes["original"] = "PASS";
                stage = "archive";
                result.ArchiveSha256 = await VerifyArchiveAsync(host, selection, result.OriginalSha256!);
                result.Outcomes[stage] = "PASS";
                result.CapturedPostCount = await CountPostsAsync(host, selection);
                var sourceWorker = Assert.Single(plugins.Runtime.GetSnapshots(), worker => worker.Identity.ExecutionRevision == active.ExecutionRevision);
                Assert.True(sourceWorker.ProcessId is > 0);
                result.ProductionWorkerObserved = true;
                result.ProductionWorkerProcessId = sourceWorker.ProcessId;
            });
        }
        catch (Exception error)
        {
            result.Outcomes[stage] = live == null ? error.GetType().Name : Classify(stage, error, live);
            if (host?.State == DesktopSessionState.Ready)
                try
                {
                    using CancellationTokenSource bounded = new(TimeSpan.FromSeconds(5));
                    await ReadSelectedAsync(host, selection, bounded.Token);
                    result.ThreadCaptured = true;
                    result.SelectedParsedAttachmentMatched = true;
                    result.Outcomes["thread"] = "PASS";
                    result.CapturedPostCount = await CountPostsAsync(host, selection);
                }
                catch { result.Outcomes.TryAdd("thread", "not-captured-or-selected-metadata-mismatch"); }
        }
        finally
        {
            live?.CancelPending();
            if (host != null)
            {
                try { if (host.State == DesktopSessionState.Ready) await host.SetPausedAsync(true).WaitAsync(TimeSpan.FromSeconds(10)); }
                catch { result.CleanupErrors.Add("pause-failed"); }
                try { await StopAsync(host); } catch (Exception error) { result.CleanupErrors.Add("live-host:" + error.GetType().Name); }
            }
            if (live != null)
            {
                result.Http = live.Audit.ToArray();
                result.NetworkSendAttempts = live.ActualNetworkSends;
                result.OriginalBytes = live.OriginalBytes;
                result.OriginalSha256 = live.OriginalSha256;
                result.ReceivedMd5Base64 = live.OriginalMd5Base64;
                if (!result.Outcomes.ContainsKey("original") && result.Http.Any(row => row.Kind == "original"))
                    result.Outcomes["original"] = live.OriginalSha256 == null ? Classify("original", new LiveHarnessLimitException("original-not-completed"), live)
                        : "http-200-but-production-integrity-or-archive-not-verified";
            }
        }
        // A fresh host restores the same private profile with a terminal reject-all transport. No network fallback exists.
        try
        {
            if (result.ThreadCaptured && revisions != null && result.CleanupErrors.Count == 0)
            {
                offline = new(selection) { RejectAll = true };
                offlineHost = Create(profile, offline);
                await offlineHost.StartAsync().WaitAsync(TimeSpan.FromSeconds(45));
                Assert.Equal(DesktopSessionState.Ready, offlineHost.State);
                Assert.True(offlineHost.IsPaused);
                var restored = await offlineHost.Plugins!.ProfileStore.ReadAsync();
                Assert.Equal(revisions.OrderBy(row => row.Key), restored.Plugins.ToDictionary(plugin => plugin.PluginId, plugin => plugin.Active!.ExecutionRevision).OrderBy(row => row.Key));
                await ReadSelectedAsync(offlineHost, selection, CancellationToken.None);
                Assert.Equal(result.CapturedPostCount, await CountPostsAsync(offlineHost, selection));
                if (result.ArchiveSha256 != null)
                {
                    Assert.Equal(result.ArchiveSha256, await VerifyArchiveAsync(offlineHost, selection, result.ArchiveSha256));
                    result.Outcomes["offline"] = "PASS";
                }
                else result.Outcomes["offline"] = "partial-metadata-only";
                Assert.Empty(offline.Audit);
                Assert.Equal(0, offline.ActualNetworkSends);
                Assert.Empty(offlineHost.Plugins.Runtime.GetSnapshots());
            }
            else result.Outcomes["offline"] = "NOT_REQUESTED:no-captured-thread-or-cleanup-failure";
        }
        catch (Exception error) { result.Outcomes["offline"] = error.GetType().Name; }
        finally
        {
            offline?.CancelPending();
            if (offlineHost != null)
                try { await StopAsync(offlineHost); } catch (Exception error) { result.CleanupErrors.Add("offline-host:" + error.GetType().Name); }
            if (offline != null) result.OfflineHttp = offline.Audit.ToArray();
            try { live?.Dispose(); offline?.Dispose(); } catch (Exception error) { result.CleanupErrors.Add("transport-dispose:" + error.GetType().Name); }
            if ((host == null || host.State == DesktopSessionState.Stopped) && (offlineHost == null || offlineHost.State == DesktopSessionState.Stopped))
                try { Directory.Delete(profile, recursive: true); } catch (Exception error) { result.CleanupErrors.Add("profile-delete:" + error.GetType().Name); }
            else result.CleanupErrors.Add("profile-not-deleted:host-not-stopped");
            result.PrivateProfileDeleted = !Directory.Exists(profile);
            result.FinishedUtc = DateTimeOffset.UtcNow;
        }
        foreach (string phase in new[] { "startup", "catalog", "thread", "original", "archive", "offline" })
            result.Outcomes.TryAdd(phase, "NOT_REQUESTED:preceding-phase-unavailable");
        result.BlockedOtherMediaAttempts = result.Http.Count(row => row.Kind == "unselected" && row.Outcome.StartsWith("harness-limit:"));
        result.Passed = result.Outcomes.Values.All(value => value == "PASS") && result.SelectedParsedAttachmentMatched
            && result.ProductionWorkerObserved && result.PrivateProfileDeleted && result.CleanupErrors.Count == 0
            && result.NetworkSendAttempts == 3
            && result.Http.Count(row => row.Kind == "catalog" && row.Status.HasValue) == 1
            && result.Http.Count(row => row.Kind == "thread" && row.Status.HasValue) == 1
            && result.Http.Count(row => row.Kind == "original" && row.Status.HasValue) == 1 && result.OfflineHttp.Length == 0;
        return result;
    }

    private static DesktopSessionHost Create(string profile, LiveSourceAuditTransport transport) => new(profile, providers: null,
        updateSessionFactory: (path, config) => new UpdateSession(path, config, new HttpClient(new RejectUpdates()), disableEnvironment: () => "1"),
        pluginTransportFactory: transport.Client);

    private static async Task<CapturedAttachment> ReadSelectedAsync(DesktopSessionHost host, LiveSourceSelection selection, CancellationToken token)
    {
        await using ArchiveReader reader = await host.OpenArchiveReaderAsync(token);
        var view = await reader.ReadThreadAsync(new(selection.SourceId, selection.BoardId, selection.ThreadId), token);
        Assert.NotNull(view);
        var post = Assert.Single(view.GetPosts(), post => post.PostId == selection.PostId);
        var attachment = Assert.Single(post.Attachments!, attachment => attachment.MediaId == selection.MediaId && attachment.Variant == "original");
        Assert.Equal(selection.Position, attachment.Position);
        Assert.Equal(selection.OriginalUrl, attachment.Url);
        Assert.Equal(selection.ExpectedBytes, attachment.ExpectedSize);
        if (selection.ExpectedMd5Base64 != null) Assert.Equal(selection.ExpectedMd5Base64, attachment.ExpectedMd5);
        Assert.False(attachment.IsDeleted);
        return attachment;
    }

    private static async Task<int> CountPostsAsync(DesktopSessionHost host, LiveSourceSelection selection)
    {
        await using ArchiveReader reader = await host.OpenArchiveReaderAsync();
        return (await reader.ReadThreadAsync(new(selection.SourceId, selection.BoardId, selection.ThreadId)))!.GetPosts().Count;
    }

    private static async Task<string> VerifyArchiveAsync(DesktopSessionHost host, LiveSourceSelection selection, string hash)
    {
        CapturedAttachment attachment = await ReadSelectedAsync(host, selection, CancellationToken.None);
        Assert.NotNull(attachment.PlannedPath);
        byte[] bytes = await File.ReadAllBytesAsync(attachment.PlannedPath);
        Assert.Equal(selection.ExpectedBytes, bytes.LongLength);
        Assert.True(HasImageSignature(bytes, selection.Extension));
        Assert.Equal(hash, Hash(bytes), ignoreCase: true);
        await using ArchiveReader reader = await host.OpenArchiveReaderAsync();
        var view = (await reader.ReadThreadAsync(new(selection.SourceId, selection.BoardId, selection.ThreadId)))!;
        var media = await MediaAssetReader.OpenAsync(view.Summary.MediaDirectory);
        var saved = Assert.Single(media.GetPage());
        Assert.Equal(selection.MediaId, saved.MediaIdentifier);
        Assert.Equal("original", saved.Variant);
        Assert.Equal(selection.OriginalUrl, saved.SourceUrl);
        Assert.Equal(hash, saved.Sha256, ignoreCase: true);
        await using Stream verified = await media.OpenVerifiedReadAsync(saved.AssetId);
        using MemoryStream restored = new();
        await verified.CopyToAsync(restored);
        Assert.Equal(selection.ExpectedBytes, restored.Length);
        Assert.Equal(hash, Hash(restored.ToArray()), ignoreCase: true);
        return Hash(bytes);
    }

    private static async Task WaitForSelectedAsync(DesktopSessionHost host, LiveSourceSelection selection, LiveSourceAuditTransport transport)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(45));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (host.GetThreadWorkSnapshots().SelectMany(thread => thread.Media)
                .Any(media => media.MediaIdentifier == selection.MediaId && media.Variant == "original" && media.Status == MediaWorkStatus.Succeeded)) return;
            if (transport.Audit.Any(row => row.Kind is "thread" or "original" && row.Outcome != "http-200"))
                throw new LiveHarnessLimitException("selected-pipeline-not-completed-see-http-audit");
            await Task.Delay(100, deadline.Token);
        }
    }

    internal static async Task RunThreadPhaseAfterCatalogAsync(LiveSourceResult result,
        IReadOnlyList<LiveHttpAudit> audit, Func<Task> phase)
    {
        LiveHttpAudit[] catalog = audit.Where(row => row.Kind == "catalog").ToArray();
        if (!result.Outcomes.TryGetValue("catalog", out string? outcome) || outcome != "PASS"
            || catalog.Length != 1 || catalog[0].Status != 200 || catalog[0].Outcome != "http-200")
        {
            foreach (string name in new[] { "thread", "original", "archive" })
                result.Outcomes[name] = "NOT_REQUESTED:catalog-not-passed";
            return; // Only this phase helper returns; RunSourceAsync still stops hosts and deletes its profile.
        }
        await phase();
    }

    internal static IReadOnlySet<string> SelectSourceIds(string? selector)
    {
        if (selector == null) return Names.Keys.ToHashSet(StringComparer.Ordinal);
        string[] requested = selector.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (requested.Length == 0 || requested.Distinct(StringComparer.Ordinal).Count() != requested.Length
            || requested.Any(id => !Names.ContainsKey(id)))
            throw new ArgumentException("Source selector must explicitly name a nonempty unique subset of the six sources.");
        return requested.ToHashSet(StringComparer.Ordinal);
    }

    internal static async Task<T> AdmitInitialThreadAsync<T>(string sourceId, Func<Task<T>> admit,
        TimeProvider? clock = null, CancellationToken token = default)
    {
        // Catalog and thread share API admission. The harness deliberately disables periodic rechecks;
        // respect the one-second minimum before the FIRST thread action instead of provoking Deferred.
        if (sourceId is "4chan" or "8kun")
            await Task.Delay(TimeSpan.FromSeconds(1), clock ?? TimeProvider.System, token);
        token.ThrowIfCancellationRequested();
        return await admit();
    }

    internal static bool HasImageSignature(byte[] bytes, string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        ".jpg" or ".jpeg" => bytes.Length >= 5 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255
            && bytes[^2] == 255 && bytes[^1] == 217,
        _ => false
    };

    private static string Classify(string stage, Exception error, LiveSourceAuditTransport transport)
    {
        var relevant = transport.Audit.LastOrDefault(row => (stage == "catalog" ? row.Kind == "catalog" : row.Kind is "thread" or "original") && row.Outcome != "http-200");
        return (relevant?.Outcome == "http-status" ? "http-" + relevant.Status : relevant?.Outcome) ?? (error is LiveHarnessLimitException limit ? "harness-limit:" + limit.Code
            : error is OperationCanceledException or TimeoutException ? "timeout-or-cancelled" : "assertion-or-pipeline:" + error.GetType().Name);
    }

    private static async Task StopAsync(DesktopSessionHost host)
    {
        int[] pids = host.Plugins?.Runtime.GetSnapshots().Where(worker => worker.ProcessId.HasValue).Select(worker => worker.ProcessId!.Value).ToArray() ?? [];
        await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(25));
        Assert.Equal(DesktopSessionState.Stopped, host.State);
        Assert.Empty(host.Plugins!.Runtime.GetSnapshots());
        foreach (int pid in pids)
        {
            try { using Process process = Process.GetProcessById(pid); Assert.True(process.HasExited); }
            catch (ArgumentException) { }
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private sealed class RejectUpdates : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new LiveHarnessLimitException("updates-disabled");
    }
}

public sealed class LiveSourcesFactAttribute : FactAttribute
{
    public LiveSourcesFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MEOWCHAN_LIVE_SOURCES_MANIFEST")))
            Skip = "Opt-in live acceptance requires the explicit six-source manifest; no network requested.";
    }
}

internal sealed record LiveSourceResult(string SourceId, string PluginId, string BoardId, string ThreadId, string MediaId)
{
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset FinishedUtc { get; set; }
    public Dictionary<string, string> Outcomes { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> DescribedNames { get; } = new(StringComparer.Ordinal);
    public string? BundleManifestSha256 { get; set; }
    public string? DeliveredAssemblySha256 { get; set; }
    public string? ActiveAssemblySha256 { get; set; }
    public string? ExecutionRevision { get; set; }
    public int TestProcessId { get; } = Environment.ProcessId;
    public int? ProductionWorkerProcessId { get; set; }
    public bool ProductionWorkerObserved { get; set; }
    public int CatalogThreadCount { get; set; }
    public bool CatalogContainsSelectedThread { get; set; }
    public bool ThreadCaptured { get; set; }
    public bool SelectedParsedAttachmentMatched { get; set; }
    public int CapturedPostCount { get; set; }
    public long OriginalBytes { get; set; }
    public string? ExpectedMd5Base64 { get; set; }
    public string? ReceivedMd5Base64 { get; set; }
    public string? OriginalSha256 { get; set; }
    public string? ArchiveSha256 { get; set; }
    public LiveHttpAudit[] Http { get; set; } = [];
    public LiveHttpAudit[] OfflineHttp { get; set; } = [];
    public int NetworkSendAttempts { get; set; }
    public int BlockedOtherMediaAttempts { get; set; }
    public List<string> CleanupErrors { get; } = [];
    public bool PrivateProfileDeleted { get; set; }
    public bool Passed { get; set; }
}

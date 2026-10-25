using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Updates;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.PluginHost.Installation;
using MeowChan.PluginSdk;
using MeowChan.Rules;
using MeowChan.Storage;
using Xunit.Abstractions;

namespace MeowChan.DesktopTests;

[Collection("Delivered plugin hosts")]
public sealed class NewSourceWaveLiveAcceptanceTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    [NewSourceWaveLiveFact]
    public async Task FiveNewDeliveredSourcesReachBoundedLiveOriginalAndFreshOfflineHost()
    {
        byte[] input = await File.ReadAllBytesAsync(Path.GetFullPath(Environment.GetEnvironmentVariable("MEOWCHAN_NEW_WAVE_LIVE_MANIFEST")!));
        var manifest = JsonSerializer.Deserialize<NewSourceWaveLiveManifest>(input, Json)!;
        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal(5, manifest.Sources.Length);
        Assert.True(NewSourceWaveLiveSelection.SourceIds.ToHashSet(StringComparer.Ordinal).SetEquals(manifest.Sources.Select(row => row.SourceId)));
        foreach (var source in manifest.Sources) source.Validate();
        string evidence = Path.GetFullPath(Environment.GetEnvironmentVariable("MEOWCHAN_NEW_WAVE_LIVE_EVIDENCE")
            ?? throw new InvalidOperationException("A new explicit technical-evidence directory is required."));
        Assert.False(Directory.Exists(evidence) || File.Exists(evidence) || new DirectoryInfo(evidence).LinkTarget != null);
        Directory.CreateDirectory(evidence);
        string? selector = Environment.GetEnvironmentVariable("MEOWCHAN_NEW_WAVE_LIVE_ONLY");
        var selected = selector == null ? NewSourceWaveLiveSelection.SourceIds : selector.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(selected);
        Assert.Equal(selected.Length, selected.Distinct(StringComparer.Ordinal).Count());
        Assert.All(selected, id => Assert.Contains(id, NewSourceWaveLiveSelection.SourceIds));
        List<Result> results = [];
        foreach (var selection in manifest.Sources.Where(row => selected.Contains(row.SourceId, StringComparer.Ordinal)))
        {
            Result result = await RunAsync(selection);
            results.Add(result);
            string report = JsonSerializer.Serialize(result, Json);
            await File.WriteAllTextAsync(Path.Combine(evidence, selection.SourceId + ".json"), report);
            output.WriteLine("NEW_SOURCE_WAVE_LIVE_RESULT " + report);
        }
        await File.WriteAllTextAsync(Path.Combine(evidence, "summary.json"), JsonSerializer.Serialize(new {
            schemaVersion = 1, completedUtc = DateTimeOffset.UtcNow, manifestSha256 = Hash(input),
            testAssemblySha256 = Hash(await File.ReadAllBytesAsync(typeof(NewSourceWaveLiveAcceptanceTests).Assembly.Location)),
            applicationAssemblySha256 = Hash(await File.ReadAllBytesAsync(typeof(DesktopSessionHost).Assembly.Location)),
            coreAssemblySha256 = Hash(await File.ReadAllBytesAsync(typeof(MediaStore).Assembly.Location)),
            selectedSourceIds = selected, allFiveRequested = selected.Length == 5,
            allFivePassed = selected.Length == 5 && results.Count == 5 && results.All(row => row.Passed),
            passed = results.All(row => row.Passed), maximumPhysicalSendsPerSource = NewSourceWaveLiveTransport.MaximumSendAttempts,
            maximumOriginalBytesPerBody = NewSourceWaveLiveTransport.MaximumOriginalBytes, results,
            scope = "Only the five new sources. Real delivered source worker, HostHttp/live physical HTTP hops, production queue/integrity/archive, passive Reader/Gallery projection and verified HTML export. At most one selected original per source; Leftypol allows a second body only after actual strict size/MD5 rejection and explicit host consent+retry. Offline is a fresh paused DesktopSessionHost in the same test process. No native rendering, independent OS restart, all-media completion or historical six-source reacceptance claim. Private profiles/content deleted."
        }, Json));
        Assert.True(results.All(row => row.Passed), "Inspect the source-bound reports; no failed phase is promoted to PASS.");
    }

    private static async Task<Result> RunAsync(NewSourceWaveLiveSelection selection)
    {
        Result result = new(selection.SourceId);
        string profile = Path.Combine(Path.GetTempPath(), "mc-newwave-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        DesktopSessionHost? host = null, restored = null;
        NewSourceWaveLiveTransport? live = null, offline = null;
        Dictionary<string, string>? revisions = null;
        string phase = "startup";
        try
        {
            new Config { DownloadPath = Path.Combine(profile, "media"), DownloadsPaused = true, SaveUrlsOnExit = true,
                AutomaticUpdateChecks = false, MinimizeToTray = false, CloseToTray = false,
                MaxParallelDownloads = 1, RecheckTimer = 3600, MinimumFreeSpaceGiB = 0 }.SaveConfig(profile);
            live = new(selection);
            host = Create(profile, live);
            await StartAsync(host);
            var plugins = host.Plugins!;
            var installed = await plugins.ProfileStore.ReadAsync();
            result.PluginCount = installed.Plugins.Count;
            result.SourceCount = plugins.Sources.Count;
            result.EditableSourceCount = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "plugins"), "*.cs", SearchOption.AllDirectories).Length;
            Assert.Equal(16, result.PluginCount); Assert.Equal(11, result.SourceCount); Assert.Equal(14, result.EditableSourceCount);
            foreach (string id in NewSourceWaveLiveSelection.SourceIds)
            {
                Assert.False(installed.Plugins.Single(row => row.PluginId == "meowchan.source." + id).Enabled);
                Assert.Equal(SourceMaturity.Experimental, plugins.Sources.Single(row => row.Name == id).Description.Maturity);
            }
            Assert.Empty(host.Config.SourceMediaIntegrityPermissions);
            Assert.Equal(0, live.ActualNetworkSends);
            // Strict-first is intentional, including Leftypol. This ordinary enable path grants no exception.
            installed = await plugins.ProfileStore.SetEnabledAsync(selection.PluginId, true, installed.Revision);
            revisions = installed.Plugins.ToDictionary(row => row.PluginId, row => row.Active!.ExecutionRevision);
            var active = installed.Plugins.Single(row => row.PluginId == selection.PluginId).Active!;
            result.ExecutionRevision = active.ExecutionRevision;
            var candidate = Assert.Single(installed.Plugins.Single(row => row.PluginId == selection.PluginId).Candidates);
            byte[] manifestBytes = await File.ReadAllBytesAsync(candidate.CandidatePath!);
            var bundle = JsonSerializer.Deserialize<PluginBundleManifest>(manifestBytes)!;
            result.BundleManifestSha256 = Hash(manifestBytes);
            result.DeliveredAssemblySha256 = Hash(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "plugins", bundle.AssemblyRelativePath)));
            Assert.Equal(result.DeliveredAssemblySha256, active.Artifact.AssemblySha256, ignoreCase: true);
            Assert.Equal(result.DeliveredAssemblySha256, Hash(await File.ReadAllBytesAsync(active.Artifact.AssemblyPath)), ignoreCase: true);
            var adapter = plugins.Sources.Single(row => row.Name == selection.SourceId);
            Assert.True(adapter.IsBoardUri(new Uri(selection.BoardUrl)));
            Assert.True(adapter.IsThreadUri(new Uri(selection.ThreadUrl)));
            foreach (string url in selection.OriginalHops)
                Assert.Contains(adapter.Description.HttpHosts, row => row.Host == new Uri(url).IdnHost && row.Purpose.HasFlag(HttpHostPurpose.OriginalMedia));
            foreach (string url in new[] { selection.CatalogUrl, selection.ThreadMetadataUrl })
                Assert.Contains(adapter.Description.HttpHosts, row => row.Host == new Uri(url).IdnHost && row.Purpose.HasFlag(HttpHostPurpose.Metadata));
            Assert.False(Policy(host, selection).IsGranted);
            result.Phases[phase] = "PASS";
            live.BeforeOriginal = async token => { await SelectedAsync(host, selection, token); result.SelectedAttachmentMatched = true; };
            // This is only a metadata candidate-selection ceiling. The transport separately
            // caps actual bytes; a provider hint is never converted into an exact size.
            long metadataLimit = selection.ProviderExpectedSize ?? NewSourceWaveLiveTransport.MaximumOriginalBytes;
            await host.SaveRulesAsync(new[] { new MediaRule(MediaRuleScope.Global,
                types: new MediaTypeSelection(MediaTypeSelectionMode.Selected, [selection.Extension]),
                sizeLimit: new(MediaSizeLimitMode.Limited, metadataLimit)) }, host.MediaRules.Revision);
            await host.SetPausedAsync(false);
            phase = "catalog";
            using (CancellationTokenSource timeout = new(Deadline))
            {
                var catalog = await host.PreviewBoardAdmissionAsync(selection.BoardUrl, timeout.Token);
                result.CatalogThreadCount = catalog.Threads.Count;
                Assert.Contains(catalog.Threads, row => row.Id == selection.ThreadId);
                foreach (var row in catalog.Threads)
                {
                    Assert.True(adapter.Router.TryResolve(new Uri(row.Url), out var kind, out string board, out string? id, out var canonical));
                    Assert.Equal(SourceResourceKind.Thread, kind); Assert.Equal(selection.BoardId, board);
                    Assert.Equal(row.Id, id); Assert.Equal(row.Url, canonical!.AbsoluteUri);
                }
            }
            Assert.Empty(host.GetView().Threads); Assert.Empty(host.GetView().Boards);
            result.Phases[phase] = "PASS";
            phase = "thread-and-original";
            Assert.Equal(AdmissionStatus.Added, (await host.AddAsync(selection.ThreadUrl)).Status);
            MediaWork work = await WaitAsync(host, selection);
            await host.SetPausedAsync(true);
            CapturedAttachment attachment = await SelectedAsync(host, selection, CancellationToken.None);
            result.Phases["thread"] = "PASS";
            var first = Assert.Single(live.OriginalBodies);
            var assessment = ProviderIntegrityAssessment.Assess(Provider(attachment), Actual(first));
            result.StrictAssessment = assessment;
            if (work.Status != MediaWorkStatus.Succeeded)
            {
                Assert.True(selection.AllowConsentRetry, "A strict source rejected its selected original.");
                Assert.Equal(MediaWorkStatus.NeedsIntervention, work.Status);
                Assert.NotNull(work.Failure); result.StrictFailure = work.Failure;
                Assert.True(assessment.SizeMismatch || assessment.Md5Mismatch);
                Assert.False(assessment.Sha256Mismatch || assessment.Sha1Mismatch);
                var before = await SnapshotAsync(host, selection);
                Assert.Null(MediaAttachmentResolver.Resolve(before, selection.Resource, selection.PostId, attachment));
                Assert.False(File.Exists(attachment.PlannedPath));
                phase = "explicit-consent-and-retry";
                var policy = Policy(host, selection);
                await host.SetSourceMediaIntegrityPermissionAsync(selection.PluginId, selection.SourceId, true, policy.Snapshot.Revision);
                var granted = Policy(host, selection);
                Assert.True(granted.IsGranted && granted.IsActive && granted.Snapshot.AllowProviderSizeMd5Mismatch);
                Assert.All(host.GetSourceMediaIntegrityPolicies().Where(row => row.Snapshot.SourceId != selection.SourceId), row => Assert.False(row.IsGranted));
                result.ConsentRevision = granted.Snapshot.Revision;
                live.BeforeOriginal = async token => {
                    Assert.Equal(attachment, await SelectedAsync(host, selection, token));
                    Assert.Equal(granted.Snapshot, Policy(host, selection).Snapshot);
                };
                live.ArmExplicitConsentRetry();
                ThreadWorkSnapshot thread = Assert.Single(host.GetThreadWorkSnapshots(), row => row.Url == selection.ThreadUrl);
                if (thread.Intent == WorkIntent.Completed)
                    Assert.Equal(1, await host.RetryCompletedMediaAsync(selection.ThreadUrl, selection.MediaId));
                else
                    Assert.Equal(1, await host.RetryMissingMediaAsync(Assert.Single(host.GetView().Threads, row => row.Snapshot.Id == selection.Resource).WatchId, selection.MediaId));
                await host.SetPausedAsync(false);
                var retried = await WaitAsync(host, selection, work.LastAttemptAt);
                Assert.Equal(MediaWorkStatus.Succeeded, retried.Status);
                Assert.Equal(2, live.OriginalBodies.Length);
                Assert.Equal(first.Sha256, live.OriginalBodies[1].Sha256);
                Assert.Equal(first.Bytes, live.OriginalBodies[1].Bytes);
                result.StrictOutcome = "actual-provider-mismatch-rejected-then-explicit-consent-retry";
            }
            else
            {
                Assert.True(assessment.IsStrictMatch);
                result.StrictOutcome = selection.SourceId == "leftypol" ? "current-original-strictly-matches-no-consent-needed" : "strict-match";
                Assert.False(Policy(host, selection).IsGranted);
            }
            await host.SetPausedAsync(true);
            result.Phases["original"] = "PASS";
            var worker = Assert.Single(plugins.Runtime.GetSnapshots(), row => row.Identity.ExecutionRevision == active.ExecutionRevision);
            Assert.True(worker.ProcessId is > 0); result.WorkerProcessId = worker.ProcessId;
            phase = "archive-gallery-export";
            result.Binding = await VerifyAsync(host, selection, first, profile, "live", result.ConsentRevision);
            result.Phases["archive"] = "PASS";
            if (result.ConsentRevision is long consent)
            {
                await host.SetSourceMediaIntegrityPermissionAsync(selection.PluginId, selection.SourceId, false, consent);
                Assert.False(Policy(host, selection).IsGranted);
                Assert.Equal(result.Binding, await VerifyAsync(host, selection, first, profile, "revoked", consent));
            }
            Assert.InRange(live.ActualNetworkSends, 3, NewSourceWaveLiveTransport.MaximumSendAttempts);
            int bodies = result.ConsentRevision == null ? 1 : 2;
            Assert.Equal(bodies, live.OriginalBodies.Length);
            Assert.Equal(2 + selection.OriginalHops.Length * bodies, live.ActualNetworkSends);
            await StopAsync(host); host = null;
            phase = "fresh-offline-host";
            offline = new(selection) { RejectAll = true };
            restored = Create(profile, offline);
            await StartAsync(restored);
            Assert.True(restored.IsPaused);
            Assert.False(Policy(restored, selection).IsGranted);
            var resumed = await restored.Plugins!.ProfileStore.ReadAsync();
            Assert.Equal(revisions.OrderBy(row => row.Key), resumed.Plugins.ToDictionary(row => row.PluginId, row => row.Active!.ExecutionRevision).OrderBy(row => row.Key));
            Assert.Equal(result.Binding, await VerifyAsync(restored, selection, first, profile, "offline", result.ConsentRevision));
            Assert.Empty(restored.Plugins.Runtime.GetSnapshots()); Assert.Empty(offline.Audit); Assert.Equal(0, offline.ActualNetworkSends);
            result.Phases["offline"] = "PASS";
        }
        catch (Exception error)
        {
            result.FailedPhase = phase;
            result.Failure = error.GetType().Name + ": " + error.Message;
        }
        finally
        {
            live?.CancelPending(); offline?.CancelPending();
            foreach (var current in new[] { host, restored }.Where(row => row != null))
                try { await StopAsync(current!); } catch (Exception error) { result.CleanupErrors.Add(error.GetType().Name); }
            if (live != null) { result.Http = live.Audit.ToArray(); result.PhysicalSendAttempts = live.ActualNetworkSends; }
            if (offline != null) result.OfflineHttp = offline.Audit.ToArray();
            try { live?.Dispose(); offline?.Dispose(); } catch (Exception error) { result.CleanupErrors.Add(error.GetType().Name); }
            if (result.CleanupErrors.Count == 0)
                try { Directory.Delete(profile, recursive: true); } catch (Exception error) { result.CleanupErrors.Add(error.GetType().Name); }
            result.PrivateProfileDeleted = !Directory.Exists(profile);
            result.Passed = result.Failure == null && result.SelectedAttachmentMatched && result.WorkerProcessId is > 0
                && result.Phases.Count == 6 && result.Phases.Values.All(value => value == "PASS")
                && result.PrivateProfileDeleted && result.CleanupErrors.Count == 0 && result.OfflineHttp.Length == 0;
        }
        return result;
    }

    private static async Task<CapturedAttachment> SelectedAsync(DesktopSessionHost host, NewSourceWaveLiveSelection selection, CancellationToken token)
    {
        await using ArchiveReader reader = await host.OpenArchiveReaderAsync(token);
        var thread = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(selection.Resource, token));
        CapturedPost? post = null;
        for (int offset = 0; offset < thread.PostCount; offset += 500)
            post ??= thread.GetPosts(offset, 500).SingleOrDefault(row => row.PostId == selection.PostId);
        Assert.NotNull(post);
        var attachment = Assert.Single(post.Attachments!, row => row.MediaId == selection.MediaId && row.Variant == "original");
        Assert.Equal(selection.Position, attachment.Position); Assert.False(attachment.IsDeleted);
        Assert.Equal(selection.OriginalUrl, attachment.Url);
        Assert.Equal(selection.ProviderExpectedSize, attachment.ExpectedSize);
        Assert.Equal(selection.ProviderExpectedMd5Base64, attachment.ExpectedMd5);
        Assert.Equal(selection.ProviderExpectedSha1Base64, attachment.ExpectedSha1);
        Assert.Equal(selection.ProviderExpectedSha256, attachment.ExpectedSha256, ignoreCase: true);
        Assert.Equal(selection.ProviderSizeHint, attachment.ProviderSizeHint);
        if (selection.SourceId == "2ch") Assert.Null(attachment.ExpectedSize);
        return attachment;
    }
    private static async Task<MediaAttachmentBinding> VerifyAsync(DesktopSessionHost host, NewSourceWaveLiveSelection selection,
        NewSourceWaveLiveHttp body, string profile, string phase, long? consent)
    {
        CapturedAttachment attachment = await SelectedAsync(host, selection, CancellationToken.None);
        var snapshot = await SnapshotAsync(host, selection);
        var resolved = Assert.IsType<ResolvedMediaAttachment>(MediaAttachmentResolver.Resolve(snapshot, selection.Resource, selection.PostId, attachment));
        Assert.False(resolved.IsLegacy);
        var binding = Assert.IsType<MediaAttachmentBinding>(resolved.Binding);
        Assert.Equal(Provider(attachment), binding.Provider); Assert.Equal(selection.Resource, binding.Reference.Resource);
        Assert.Equal(selection.PostId, binding.Reference.PostId); Assert.Equal(selection.Position, binding.Reference.Position);
        Assert.Equal(attachment.AttachmentRevisionKey, binding.Reference.RevisionKey);
        Assert.Equal(body.Bytes, binding.Actual.Length); Assert.Equal(body.Sha256, binding.Actual.Sha256, ignoreCase: true);
        Assert.Equal(body.Md5, binding.Actual.Md5);
        if (selection.ProviderExpectedSha1Base64 != null) Assert.Equal(body.Sha1, binding.Actual.Sha1);
        Assert.Equal(consent == null ? MediaAcceptanceKind.Strict : MediaAcceptanceKind.ExplicitProviderMismatch, binding.Acceptance.Kind);
        if (consent != null) {
            Assert.Equal(consent, binding.Acceptance.PolicyRevision); Assert.Equal(selection.PluginId, binding.Acceptance.PluginOwnerId);
            Assert.Equal(selection.SourceId, binding.Acceptance.SourceId); Assert.Equal(profile, binding.Acceptance.ProfileId);
        }
        await using Stream verified = await snapshot.OpenVerifiedReadAsync(binding.AssetId);
        using MemoryStream bytes = new(); await verified.CopyToAsync(bytes);
        Assert.Equal(body.Bytes, bytes.Length); Assert.Equal(body.Sha256, Hash(bytes.ToArray()), ignoreCase: true);
        Assert.True(ImageSignature(bytes.ToArray(), selection.Extension));
        await using ArchiveReader reader = await host.OpenArchiveReaderAsync();
        var thread = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(selection.Resource));
        var entry = Assert.Single(GallerySelection.Build(thread, [snapshot]).Entries, row => row.Post?.PostId == selection.PostId && row.Attachment?.Position == selection.Position);
        Assert.Equal(binding, entry.AttachmentResolution!.Binding); Assert.False(entry.Unassigned);
        var export = await ArchiveHtmlExporter.ExportAsync(reader, selection.Resource, Path.Combine(profile, "export-" + phase));
        string file = Path.Combine(export.Directory, "media", body.Sha256 + selection.Extension.ToLowerInvariant());
        Assert.Equal(body.Sha256, Hash(await File.ReadAllBytesAsync(file)), ignoreCase: true);
        Assert.Contains("media/" + Path.GetFileName(file), await File.ReadAllTextAsync(export.IndexPath));
        return binding;
    }
    private static async Task<MediaAssetSnapshot> SnapshotAsync(DesktopSessionHost host, NewSourceWaveLiveSelection selection)
    {
        await using ArchiveReader reader = await host.OpenArchiveReaderAsync();
        var thread = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(selection.Resource));
        return await MediaAssetReader.OpenAsync(thread.Summary.MediaDirectory);
    }
    private static MediaProviderMetadata Provider(CapturedAttachment attachment) => new(attachment.MediaId!, attachment.Variant,
        attachment.Url!, attachment.OriginalFileName, attachment.ExpectedSize, attachment.ExpectedMd5, attachment.ExpectedSha256)
        { ExpectedSha1 = attachment.ExpectedSha1, ProviderSizeHint = attachment.ProviderSizeHint };
    private static MediaActualIntegrity Actual(NewSourceWaveLiveHttp body) => new(body.Bytes, body.Md5!, body.Sha256!) { Sha1 = body.Sha1 };
    private static SourceMediaIntegrityPolicyView Policy(DesktopSessionHost host, NewSourceWaveLiveSelection selection) =>
        Assert.Single(host.GetSourceMediaIntegrityPolicies(), row => row.Snapshot.SourceId == selection.SourceId && row.Snapshot.PluginOwnerId == selection.PluginId);
    private static DesktopSessionHost Create(string profile, NewSourceWaveLiveTransport transport) => new(profile,
        updateSessionFactory: (path, config) => new UpdateSession(path, config, new HttpClient(new RejectUpdates()), disableEnvironment: () => "1"),
        pluginTransportFactory: transport.Client);
    private static async Task StartAsync(DesktopSessionHost host)
    {
        await host.StartAsync().WaitAsync(Deadline); Assert.True(host.State == DesktopSessionState.Ready, host.StartupError);
        Assert.Empty(host.Plugins!.Warnings);
    }
    private static async Task<MediaWork> WaitAsync(DesktopSessionHost host, NewSourceWaveLiveSelection selection, DateTimeOffset? after = null)
    {
        using CancellationTokenSource deadline = new(Deadline);
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            var work = host.GetThreadWorkSnapshots().Where(row => row.Url == selection.ThreadUrl).SelectMany(row => row.Media)
                .SingleOrDefault(row => row.MediaIdentifier == selection.MediaId && row.Variant == "original");
            if (work != null && (after == null || work.LastAttemptAt > after)
                && (work.Status == MediaWorkStatus.Succeeded || work.Status == MediaWorkStatus.NeedsIntervention))
            {
                var watch = host.GetView().Threads.SingleOrDefault(row => row.Snapshot.Id == selection.Resource);
                if (work.Status == MediaWorkStatus.Succeeded || watch == null || watch.Snapshot.Downloads.Active == 0) return work;
            }
            await Task.Delay(50, deadline.Token);
        }
    }
    private static async Task StopAsync(DesktopSessionHost host)
    {
        int[] processes = host.Plugins?.Runtime.GetSnapshots().Where(row => row.ProcessId.HasValue).Select(row => row.ProcessId!.Value).ToArray() ?? [];
        await host.DisposeAsync().AsTask().WaitAsync(Deadline); Assert.Equal(DesktopSessionState.Stopped, host.State);
        if (host.Plugins != null) Assert.Empty(host.Plugins.Runtime.GetSnapshots());
        foreach (int id in processes) try { using Process process = Process.GetProcessById(id); Assert.True(process.HasExited); } catch (ArgumentException) { }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool ImageSignature(byte[] bytes, string extension) => extension.ToLowerInvariant() switch {
        ".png" => bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        ".jpg" or ".jpeg" => bytes.Length >= 5 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255 && bytes[^2] == 255 && bytes[^1] == 217,
        _ => false };
    private sealed class RejectUpdates : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new NewSourceWaveLiveLimitException("updates-disabled"); }
    private sealed class Result(string sourceId)
    {
        public string SourceId { get; } = sourceId;
        public int TestProcessId { get; } = Environment.ProcessId;
        public bool Passed { get; set; }
        public Dictionary<string, string> Phases { get; } = new(StringComparer.Ordinal);
        public string? FailedPhase { get; set; }
        public string? Failure { get; set; }
        public string? StrictOutcome { get; set; }
        public string? StrictFailure { get; set; }
        public ProviderIntegrityAssessment? StrictAssessment { get; set; }
        public long? ConsentRevision { get; set; }
        public int PluginCount { get; set; }
        public int SourceCount { get; set; }
        public int EditableSourceCount { get; set; }
        public int CatalogThreadCount { get; set; }
        public bool SelectedAttachmentMatched { get; set; }
        public int? WorkerProcessId { get; set; }
        public string? ExecutionRevision { get; set; }
        public string? BundleManifestSha256 { get; set; }
        public string? DeliveredAssemblySha256 { get; set; }
        public MediaAttachmentBinding? Binding { get; set; }
        public int PhysicalSendAttempts { get; set; }
        public NewSourceWaveLiveHttp[] Http { get; set; } = [];
        public NewSourceWaveLiveHttp[] OfflineHttp { get; set; } = [];
        public List<string> CleanupErrors { get; } = [];
        public bool PrivateProfileDeleted { get; set; }
    }
}

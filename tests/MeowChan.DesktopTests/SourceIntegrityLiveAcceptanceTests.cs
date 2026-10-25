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

public sealed class SourceIntegrityLiveAcceptanceTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private sealed record Manifest(int SchemaVersion, LiveSourceSelection[] Sources);
    private static readonly string[] Sources = ["kohlchan", "wizchan", "endchan"];

    [SourceIntegrityLiveFact]
    public async Task ThreeDeliveredSourcesRequireRealHostConsentBeforeSameOriginalAcceptance()
    {
        byte[] input = await File.ReadAllBytesAsync(Path.GetFullPath(
            Environment.GetEnvironmentVariable("MEOWCHAN_INTEGRITY_LIVE_MANIFEST")!));
        Manifest manifest = JsonSerializer.Deserialize<Manifest>(input, Json)!;
        Assert.Equal(1, manifest.SchemaVersion);
        LiveSourceSelection[] selections = Sources.Select(id => Assert.Single(manifest.Sources, row => row.SourceId == id)).ToArray();
        string evidence = Path.GetFullPath(Environment.GetEnvironmentVariable("MEOWCHAN_INTEGRITY_LIVE_EVIDENCE")
            ?? throw new InvalidOperationException("A new explicit evidence directory is required."));
        Assert.False(Directory.Exists(evidence) || File.Exists(evidence) || new DirectoryInfo(evidence).LinkTarget != null);
        Directory.CreateDirectory(evidence);
        List<Result> results = [];
        foreach (LiveSourceSelection selection in selections)
        {
            Result result = await RunAsync(selection);
            results.Add(result);
            await File.WriteAllTextAsync(Path.Combine(evidence, selection.SourceId + ".json"), JsonSerializer.Serialize(result, Json));
            output.WriteLine("SOURCE_INTEGRITY_LIVE_RESULT " + JsonSerializer.Serialize(result, Json));
        }
        await File.WriteAllTextAsync(Path.Combine(evidence, "summary.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, passed = results.All(row => row.Passed), manifestSha256 = Hash(input), results,
            testAssemblySha256 = Hash(await File.ReadAllBytesAsync(typeof(SourceIntegrityLiveAcceptanceTests).Assembly.Location)),
            applicationAssemblySha256 = Hash(await File.ReadAllBytesAsync(typeof(DesktopSessionHost).Assembly.Location)),
            coreAssemblySha256 = Hash(await File.ReadAllBytesAsync(typeof(MediaStore).Assembly.Location)),
            actualNetworkSends = results.Sum(row => row.ActualNetworkSends), maximumNetworkSends = 12,
            scope = "Delivered parser/worker, production HostHttp/downloader/archive, actual Desktop permission setter, strict reject then same-original opt-in; verified local/resolver/Gallery selection/HTML bytes. Offline is a fresh paused DesktopSessionHost in the same OS test process. Separate Core fixture proves actual OS restart. No decoded/rendered Gallery or deployed transformation/provider authenticity claim."
        }, Json));
        Assert.True(results.All(row => row.Passed), "Inspect the new source-bound reports; historical failures remain unchanged.");
    }

    private static async Task<Result> RunAsync(LiveSourceSelection selection)
    {
        Result result = new(selection.SourceId);
        string profile = Path.Combine(Path.GetTempPath(), "mci-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        DesktopSessionHost? host = null, offlineHost = null;
        SourceIntegrityLiveTransport? live = null, offline = null;
        string stage = "startup";
        try
        {
            Assert.Equal("meowchan.source." + selection.SourceId, selection.PluginId);
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
            Assert.True(host.IsPaused);
            Assert.Empty(host.Plugins!.Warnings);
            var installed = await host.Plugins.ProfileStore.ReadAsync();
            Assert.False(installed.Plugins.Single(plugin => plugin.PluginId == selection.PluginId).Enabled);
            Assert.Equal(0, live.ActualNetworkSends);
            Assert.Empty(host.GetThreadWorkSnapshots());
            // Preserve this probe's strict-first contract: manual Host enable grants
            // no exception. The separate activation smoke exercises combined consent.
            installed = await host.Plugins.ProfileStore.SetEnabledAsync(selection.PluginId, true, installed.Revision);
            var active = Assert.Single(installed.Plugins, plugin => plugin.PluginId == selection.PluginId).Active!;
            var candidate = Assert.Single(installed.Plugins.Single(plugin => plugin.PluginId == selection.PluginId).Candidates);
            byte[] bundleBytes = await File.ReadAllBytesAsync(candidate.CandidatePath!);
            PluginBundleManifest bundle = JsonSerializer.Deserialize<PluginBundleManifest>(bundleBytes)!;
            string delivered = Path.Combine(AppContext.BaseDirectory, "plugins", bundle.AssemblyRelativePath);
            result.ExecutionRevision = active.ExecutionRevision;
            result.AssemblySha256 = Hash(await File.ReadAllBytesAsync(delivered));
            Assert.Equal(result.AssemblySha256, active.Artifact.AssemblySha256, ignoreCase: true);
            Assert.Equal(result.AssemblySha256, Hash(await File.ReadAllBytesAsync(active.Artifact.AssemblyPath)), ignoreCase: true);
            result.BundleManifestSha256 = Hash(bundleBytes);
            SourceMediaIntegrityPolicyView initial = Policy(host, selection);
            Assert.Equal(SourceMaturity.Experimental, initial.Maturity);
            Assert.True(initial.IsActive);
            Assert.False(initial.IsGranted);
            Assert.False(initial.Snapshot.AllowProviderSizeMd5Mismatch);
            Assert.All(host.GetSourceMediaIntegrityPolicies(), row => Assert.False(row.IsGranted));
            result.InitialPolicy = initial.Snapshot;
            Assert.Equal(0, live.ActualNetworkSends);
            live.BeforeStrictOriginal = async token => { await SelectedAsync(host, selection, token); };
            await host.SaveRulesAsync([new MediaRule(MediaRuleScope.Global,
                types: selection.SourceId == "endchan" ? MediaTypeSelection.All
                    : new(MediaTypeSelectionMode.Selected, [selection.Extension]),
                sizeLimit: new(MediaSizeLimitMode.Limited, selection.ExpectedBytes))], host.MediaRules.Revision);
            stage = "catalog";
            await host.SetPausedAsync(false);
            using (CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30)))
            {
                var catalog = await host.PreviewBoardAdmissionAsync(selection.BoardUrl, deadline.Token);
                Assert.Contains(catalog.Threads, thread => thread.Id == selection.ThreadId);
                result.CatalogContainsSelectedThread = true;
            }
            stage = "strict-default";
            Assert.Equal(AdmissionStatus.Added, (await host.AddAsync(selection.ThreadUrl)).Status);
            MediaWork strict = await WaitForAsync(host, selection, succeeded: false);
            Assert.NotNull(strict.Failure);
            Assert.Equal(MediaWorkStatus.NeedsIntervention, strict.Status);
            Assert.Equal(1, strict.Attempts);
            Assert.Null(strict.HttpStatus);
            const string providerRejection = "Provider size or MD5 differs without current source permission and exact references.";
            Assert.Equal(providerRejection, strict.Failure);
            DesktopThreadEntry failedWatch = Assert.Single(host.GetView().Threads, row => row.Snapshot.Id == Resource(selection));
            result.StrictQueueCounts = failedWatch.Snapshot.Downloads;
            Assert.Equal(0, failedWatch.Snapshot.Downloads.Active);
            Assert.Equal(0, failedWatch.Snapshot.Downloads.Queued);
            Assert.True(failedWatch.Snapshot.MediaFailures.TryGetValue(selection.MediaId, out FailureInfo? failure));
            Assert.NotNull(failure);
            Assert.Equal(FailureKind.InvalidData, failure.Kind);
            Assert.Equal(providerRejection, failure.Message);
            result.StrictFailureInfo = failure;
            Assert.NotNull(live.StrictOriginalSha256);
            CapturedAttachment attachment = await SelectedAsync(host, selection, CancellationToken.None);
            Assert.NotNull(attachment.PlannedPath);
            Assert.False(File.Exists(attachment.PlannedPath));
            MediaAssetSnapshot strictSnapshot = await SnapshotAsync(host, selection);
            Assert.Empty(strictSnapshot.AttachmentBindings);
            Assert.Equal(0, strictSnapshot.Count);
            result.StrictFailure = strict;
            result.StrictZeroBindings = true;
            MediaProviderMetadata provider = Provider(attachment);
            ProviderIntegrityAssessment assessment = ProviderIntegrityAssessment.Assess(provider,
                new(live.OriginalBytes, live.OriginalMd5Base64!, live.StrictOriginalSha256!));
            Assert.True(assessment.SizeMismatch || assessment.Md5Mismatch);
            Assert.False(assessment.Sha256Mismatch);
            Assert.False(Policy(host, selection).Snapshot.AllowProviderSizeMd5Mismatch);

            stage = "explicit-host-consent-and-retry";
            SourceMediaIntegrityPolicyView before = Policy(host, selection);
            await host.SetSourceMediaIntegrityPermissionAsync(selection.PluginId, selection.SourceId, true, before.Snapshot.Revision);
            SourceMediaIntegrityPolicyView granted = Policy(host, selection);
            Assert.True(granted.IsGranted && granted.IsActive && granted.Snapshot.AllowProviderSizeMd5Mismatch);
            Assert.True(granted.Snapshot.Revision > before.Snapshot.Revision);
            Assert.All(host.GetSourceMediaIntegrityPolicies().Where(row => row.Snapshot.SourceId != selection.SourceId),
                row => Assert.False(row.IsGranted));
            result.AcceptancePolicy = granted.Snapshot;
            live.ArmSecondAfterObservedStrictFailure(async token =>
            {
                Assert.Equal(granted.Snapshot, Policy(host, selection).Snapshot);
                Assert.True(Policy(host, selection).IsActive);
                Assert.Equal(attachment, await SelectedAsync(host, selection, token));
            });
            ThreadWorkSnapshot current = Assert.Single(host.GetThreadWorkSnapshots(), thread => thread.Url == selection.ThreadUrl);
            if (current.Intent == WorkIntent.Completed)
            {
                result.RetryKind = "completed-from-saved-metadata";
                Assert.Equal(1, await host.RetryCompletedMediaAsync(selection.ThreadUrl, selection.MediaId));
            }
            else
            {
                result.RetryKind = "active-watch-missing-media";
                DesktopThreadEntry watch = Assert.Single(host.GetView().Threads, thread => thread.Snapshot.Url == selection.ThreadUrl);
                Assert.Equal(1, await host.RetryMissingMediaAsync(watch.WatchId, selection.MediaId));
            }
            await WaitForAsync(host, selection, succeeded: true, afterAttempt: strict.LastAttemptAt);
            await host.SetPausedAsync(true);
            Assert.True(live.SameOriginalBodies);
            Assert.Equal(4, live.ActualNetworkSends);
            result.AcceptedBinding = await VerifyAsync(host, selection, live.StrictOriginalSha256!, profile, "live");
            Assert.Equal(granted.Snapshot.Revision, result.AcceptedBinding.Acceptance.PolicyRevision);
            result.ProductionWorkerProcessId = Assert.Single(host.Plugins.Runtime.GetSnapshots(),
                worker => worker.Identity.ExecutionRevision == active.ExecutionRevision).ProcessId;
            Assert.True(result.ProductionWorkerProcessId is > 0);

            stage = "revoke-and-local-read";
            await host.SetSourceMediaIntegrityPermissionAsync(selection.PluginId, selection.SourceId, false, granted.Snapshot.Revision);
            Assert.False(Policy(host, selection).IsGranted);
            Assert.Equal(result.AcceptedBinding, await VerifyAsync(host, selection, live.StrictOriginalSha256!, profile, "revoked"));
            await StopAsync(host);
            host = null;
            stage = "fresh-offline-host";
            offline = new(selection) { RejectAll = true };
            offlineHost = Create(profile, offline);
            await offlineHost.StartAsync().WaitAsync(TimeSpan.FromSeconds(45));
            Assert.True(offlineHost.IsPaused);
            Assert.False(Policy(offlineHost, selection).IsGranted);
            var restarted = await offlineHost.Plugins!.ProfileStore.ReadAsync();
            Assert.Equal(active.ExecutionRevision,
                Assert.Single(restarted.Plugins, plugin => plugin.PluginId == selection.PluginId).Active!.ExecutionRevision);
            Assert.Equal(result.AcceptedBinding, await VerifyAsync(offlineHost, selection, live.StrictOriginalSha256!, profile, "offline"));
            Assert.Equal(0, offline.ActualNetworkSends);
            Assert.Empty(offline.Denials);
            Assert.Empty(offline.StrictAudit);
            Assert.Empty(offline.AcceptedAudit);
            Assert.Empty(offlineHost.Plugins.Runtime.GetSnapshots());
            result.OfflineVerified = true;
            result.Passed = true;
        }
        catch (Exception error)
        {
            result.FailedStage = stage; result.Failure = error.ToString();
            if (host != null)
                try
                {
                    // Passive copies before disposal preserve the last durable media/capture state on timeout.
                    result.LastObservedState = JsonSerializer.SerializeToElement(new
                    {
                        host.State, host.IsPaused,
                        work = host.GetThreadWorkSnapshots().Where(row => row.Url == selection.ThreadUrl).ToArray(),
                        capture = host.GetView().Threads.Where(row => row.Snapshot.Id == Resource(selection)).ToArray()
                    }, Json);
                }
                catch (Exception diagnosticError) { result.DiagnosticFailure = diagnosticError.ToString(); }
        }
        finally
        {
            live?.CancelPending(); offline?.CancelPending();
            foreach (DesktopSessionHost? session in new[] { host, offlineHost })
                if (session != null)
                    try { await StopAsync(session); }
                    catch (Exception error) { result.CleanupErrors.Add(error.ToString()); }
            if (live != null)
            {
                result.StrictHttp = live.StrictAudit; result.AcceptedHttp = live.AcceptedAudit;
                result.Denials = live.Denials.ToArray(); result.ActualNetworkSends = live.ActualNetworkSends;
                result.BothBodiesEqual = live.SameOriginalBodies;
            }
            if (offline != null) result.OfflineAttempts = offline.StrictAudit.Length + offline.AcceptedAudit.Length + offline.Denials.Count;
            live?.Dispose(); offline?.Dispose();
            if ((host == null || host.State == DesktopSessionState.Stopped) && (offlineHost == null || offlineHost.State == DesktopSessionState.Stopped))
                try { Directory.Delete(profile, true); } catch (Exception error) { result.CleanupErrors.Add(error.ToString()); }
            result.PrivateProfileDeleted = !Directory.Exists(profile);
            result.Passed &= result.PrivateProfileDeleted && result.CleanupErrors.Count == 0 && result.ActualNetworkSends == 4
                && result.StrictHttp.Length == 3 && result.StrictHttp.All(row => row.Status == 200 && row.Outcome == "http-200")
                && new[] { "catalog", "thread", "original" }.All(kind => result.StrictHttp.Count(row => row.Kind == kind) == 1)
                && result.AcceptedHttp.Length == 1 && result.AcceptedHttp[0].Kind == "original"
                && result.AcceptedHttp[0].Status == 200 && result.AcceptedHttp[0].Outcome == "http-200" && result.Denials.Length == 0
                && result.OfflineAttempts == 0 && result.BothBodiesEqual;
            if (!result.Passed && result.Failure == null)
            {
                result.FailedStage = "final-audit-and-cleanup";
                result.Failure = "Exact 3+1 HTTP-200 audit, empty denial inventory or clean private shutdown was not satisfied.";
            }
        }
        return result;
    }

    private static SourceMediaIntegrityPolicyView Policy(DesktopSessionHost host, LiveSourceSelection selection)
        => Assert.Single(host.GetSourceMediaIntegrityPolicies(), row => row.Snapshot.PluginOwnerId == selection.PluginId
            && row.Snapshot.SourceId == selection.SourceId);
    private static DesktopSessionHost Create(string profile, SourceIntegrityLiveTransport transport) => new(profile, providers: null,
        updateSessionFactory: (path, config) => new UpdateSession(path, config, new HttpClient(new RejectUpdates()), disableEnvironment: () => "1"),
        pluginTransportFactory: transport.Client);
    private static ResourceId Resource(LiveSourceSelection selection) => new(selection.SourceId, selection.BoardId, selection.ThreadId);
    private static MediaProviderMetadata Provider(CapturedAttachment attachment) => new(attachment.MediaId!, attachment.Variant,
        attachment.Url!, attachment.OriginalFileName, attachment.ExpectedSize, attachment.ExpectedMd5, attachment.ExpectedSha256);

    private static async Task<CapturedAttachment> SelectedAsync(DesktopSessionHost host, LiveSourceSelection selection, CancellationToken token)
    {
        await using ArchiveReader reader = await host.OpenArchiveReaderAsync(token);
        var view = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Resource(selection), token));
        CapturedPost post = Assert.Single(view.GetPosts(), row => row.PostId == selection.PostId);
        CapturedAttachment attachment = Assert.Single(post.Attachments!, row => row.Position == selection.Position);
        Assert.Equal(selection.MediaId, attachment.MediaId); Assert.Equal("original", attachment.Variant);
        Assert.Equal(selection.OriginalUrl, attachment.Url); Assert.Equal(selection.ExpectedBytes, attachment.ExpectedSize);
        Assert.Equal(selection.ExpectedMd5Base64, attachment.ExpectedMd5); Assert.False(attachment.IsDeleted);
        Assert.Equal(MediaRevisionKeys.CreateAttachmentKey(Resource(selection), post.PostId, attachment.Position, Provider(attachment)),
            attachment.AttachmentRevisionKey);
        return attachment;
    }
    private static async Task<MediaAssetSnapshot> SnapshotAsync(DesktopSessionHost host, LiveSourceSelection selection)
    {
        await using ArchiveReader reader = await host.OpenArchiveReaderAsync();
        var view = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Resource(selection)));
        return await MediaAssetReader.OpenAsync(view.Summary.MediaDirectory);
    }
    private static async Task<MediaWork> WaitForAsync(DesktopSessionHost host, LiveSourceSelection selection, bool succeeded,
        DateTimeOffset? afterAttempt = null)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(45));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            MediaWork? work = host.GetThreadWorkSnapshots().Where(thread => thread.Url == selection.ThreadUrl)
                .SelectMany(thread => thread.Media).SingleOrDefault(row => row.MediaIdentifier == selection.MediaId && row.Variant == "original");
            if (afterAttempt != null && (work?.LastAttemptAt == null || work.LastAttemptAt <= afterAttempt))
            { await Task.Delay(50, deadline.Token); continue; }
            if (work != null && work.Status == MediaWorkStatus.Succeeded)
            { Assert.True(succeeded, "Strict default unexpectedly accepted this original."); return work; }
            if (work?.Failure != null && work.LastAttemptAt != null)
            {
                Assert.False(succeeded, "Explicit retry did not complete: " + work.Failure);
                // Durable failure precedes worker retirement; use the existing deadline, never another retry.
                DesktopThreadEntry? watched = host.GetView().Threads.SingleOrDefault(row => row.Snapshot.Id == Resource(selection));
                if (watched?.Snapshot.Downloads is { Active: 0, Queued: 0 }) return work;
            }
            await Task.Delay(50, deadline.Token);
        }
    }
    private static async Task<MediaAttachmentBinding> VerifyAsync(DesktopSessionHost host, LiveSourceSelection selection,
        string sha, string profile, string phase)
    {
        CapturedAttachment attachment = await SelectedAsync(host, selection, CancellationToken.None);
        MediaAssetSnapshot snapshot = await SnapshotAsync(host, selection);
        ResolvedMediaAttachment resolved = Assert.IsType<ResolvedMediaAttachment>(
            MediaAttachmentResolver.Resolve(snapshot, Resource(selection), selection.PostId, attachment));
        MediaAttachmentBinding binding = Assert.IsType<MediaAttachmentBinding>(resolved.Binding);
        Assert.False(resolved.IsLegacy);
        Assert.Equal(Provider(attachment), binding.Provider);
        Assert.Equal(sha, binding.Actual.Sha256); Assert.Equal(sha, resolved.Asset.Sha256, ignoreCase: true);
        Assert.Equal(MediaAcceptanceKind.ExplicitProviderMismatch, binding.Acceptance.Kind);
        ProviderIntegrityAssessment assessment = ProviderIntegrityAssessment.Assess(binding.Provider, binding.Actual);
        Assert.True(assessment.SizeMismatch || assessment.Md5Mismatch); Assert.False(assessment.Sha256Mismatch);
        Assert.Equal(assessment.SizeMismatch, binding.Acceptance.SizeMismatch);
        Assert.Equal(assessment.Md5Mismatch, binding.Acceptance.Md5Mismatch);
        Assert.Equal(profile, binding.Acceptance.ProfileId); Assert.Equal(selection.PluginId, binding.Acceptance.PluginOwnerId);
        Assert.Equal(selection.SourceId, binding.Acceptance.SourceId);
        await using (Stream verified = await snapshot.OpenVerifiedReadAsync(binding.AssetId))
        {
            using MemoryStream bytes = new(); await verified.CopyToAsync(bytes);
            Assert.Equal(binding.Actual.Length, bytes.Length); Assert.Equal(sha, Hash(bytes.ToArray()));
            Assert.Equal(binding.Actual.Md5, Convert.ToBase64String(MD5.HashData(bytes.ToArray())));
        }
        await using ArchiveReader reader = await host.OpenArchiveReaderAsync();
        var thread = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Resource(selection)));
        GalleryEntry gallery = Assert.Single(GallerySelection.Build(thread, [snapshot]).Entries,
            row => row.Post?.PostId == selection.PostId && row.Attachment?.Position == selection.Position);
        Assert.Equal(binding.AssetId, gallery.Asset.AssetId); Assert.Equal(binding, gallery.AttachmentResolution!.Binding);
        ArchiveHtmlExportResult export = await ArchiveHtmlExporter.ExportAsync(reader, Resource(selection), Path.Combine(profile, "export-" + phase));
        string selected = Path.Combine(export.Directory, "media", sha + Path.GetExtension(resolved.Asset.Path).ToLowerInvariant());
        Assert.True(File.Exists(selected));
        Assert.Equal(sha, Hash(await File.ReadAllBytesAsync(selected)));
        Assert.Equal(binding.Actual.Length, new FileInfo(selected).Length);
        Assert.Contains("media/" + Path.GetFileName(selected), await File.ReadAllTextAsync(export.IndexPath));
        return binding;
    }
    private static async Task StopAsync(DesktopSessionHost host)
    {
        int[] pids = host.Plugins?.Runtime.GetSnapshots().Where(row => row.ProcessId.HasValue).Select(row => row.ProcessId!.Value).ToArray() ?? [];
        await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(25));
        Assert.Equal(DesktopSessionState.Stopped, host.State); Assert.Empty(host.Plugins!.Runtime.GetSnapshots());
        foreach (int pid in pids)
            try { using Process process = Process.GetProcessById(pid); Assert.True(process.HasExited); } catch (ArgumentException) { }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private sealed class RejectUpdates : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new LiveHarnessLimitException("updates-disabled"); }
    private sealed class Result(string sourceId)
    {
        public string SourceId { get; } = sourceId;
        public bool Passed { get; set; }
        public string? FailedStage { get; set; }
        public string? Failure { get; set; }
        public JsonElement? LastObservedState { get; set; }
        public string? DiagnosticFailure { get; set; }
        public string? ExecutionRevision { get; set; }
        public string? AssemblySha256 { get; set; }
        public string? BundleManifestSha256 { get; set; }
        public SourceMediaIntegrityPolicySnapshot? InitialPolicy { get; set; }
        public SourceMediaIntegrityPolicySnapshot? AcceptancePolicy { get; set; }
        public MediaWork? StrictFailure { get; set; }
        public FailureInfo? StrictFailureInfo { get; set; }
        public MeowChan.Downloader.DownloadCounts? StrictQueueCounts { get; set; }
        public string? RetryKind { get; set; }
        public bool StrictZeroBindings { get; set; }
        public bool CatalogContainsSelectedThread { get; set; }
        public MediaAttachmentBinding? AcceptedBinding { get; set; }
        public int? ProductionWorkerProcessId { get; set; }
        public bool BothBodiesEqual { get; set; }
        public bool OfflineVerified { get; set; }
        public bool PrivateProfileDeleted { get; set; }
        public int ActualNetworkSends { get; set; }
        public int OfflineAttempts { get; set; }
        public LiveHttpAudit[] StrictHttp { get; set; } = [];
        public LiveHttpAudit[] AcceptedHttp { get; set; } = [];
        public LiveHttpAudit[] Denials { get; set; } = [];
        public List<string> CleanupErrors { get; } = [];
    }
}

public sealed class SourceIntegrityLiveFactAttribute : FactAttribute
{
    public SourceIntegrityLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MEOWCHAN_INTEGRITY_LIVE_GO") != "1"
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MEOWCHAN_INTEGRITY_LIVE_MANIFEST")))
            Skip = "Requires the separate explicit twelve-GET live GO and source manifest.";
    }
}

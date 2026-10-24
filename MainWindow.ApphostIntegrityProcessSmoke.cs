using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Downloader;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.PluginHost.Installation;
using MeowChan.Storage;

namespace MeowChan;

public partial class MainWindow
{
    internal async Task CompleteApphostIntegrityProcessSmokeAsync(ApphostIntegrityProcessSmoke fixture)
    {
        const string owner = ApphostIntegrityProcessSmoke.Owner, source = ApphostIntegrityProcessSmoke.Source;
        var descriptor = fixture.Descriptor;
        var resource = new ResourceId(source, "demo", "123");
        bool completed = false;
        string? failure = null;
        object? proof = null;
        object? initialPolicy = null, finalPolicy = null, pluginProof = null;
        int[] workerPids = Array.Empty<int>();
        long? acceptedPolicyRevision = null;
#pragma warning disable IL3000
        var managedAssemblies = new[] { typeof(App).Assembly, typeof(Registry).Assembly }.Select(assembly => new {
            name = assembly.GetName().Name, path = assembly.Location,
            moduleVersionId = assembly.ManifestModule.ModuleVersionId.ToString("D"),
            sha256 = string.IsNullOrEmpty(assembly.Location) ? null : PolicyHash(File.ReadAllBytes(assembly.Location)) }).ToArray();
#pragma warning restore IL3000
        SourceMediaIntegrityPolicyView Policy() => _session.GetSourceMediaIntegrityPolicies().Single(row => row.Snapshot.PluginOwnerId == owner && row.Snapshot.SourceId == source);
        MediaWork? Work() => _session.GetThreadWorkSnapshots().Where(row => row.Url == ApphostIntegrityProcessSmoke.ThreadUrl)
            .SelectMany(row => row.Media).SingleOrDefault(row => row.Variant == "original");
        async Task<(ArchivedThreadView Thread, CapturedAttachment Attachment, MediaAssetSnapshot Snapshot)> ReadAsync()
        {
            await using var reader = await _session.OpenArchiveReaderAsync();
            var thread = await reader.ReadThreadAsync(resource) ?? throw new InvalidDataException("Missing captured fixture.");
            return (thread, thread.GetPosts().Single(row => row.PostId == "123").Attachments!.Single(),
                await MediaAssetReader.OpenAsync(thread.Summary.MediaDirectory));
        }
        try
        {
            await _restoreTask;
            RequireWorkflow(_restoreCompleted && IsVisible && _session.State == DesktopSessionState.Ready && _session.IsPaused,
                _session.StartupError ?? "Production policy session not ready and paused.");
            RequireWorkflow(Math.Abs(RenderScaling - descriptor.ExpectedScale) < .01, "Policy proof scale differs.");
            var plugins = _session.Plugins ?? throw new InvalidOperationException("Production PluginSession missing.");
            RequireWorkflow(plugins.Warnings.Count == 0, string.Join("; ", plugins.Warnings));
            var installed = await plugins.ProfileStore.ReadAsync();
            var selectedPlugin = installed.Plugins.Single(row => row.PluginId == owner);
            var active = selectedPlugin.Active ?? throw new InvalidDataException("Delivered policy source inactive.");
            var candidate = selectedPlugin.Candidates.Single();
            var manifest = JsonSerializer.Deserialize<PluginBundleManifest>(await File.ReadAllTextAsync(candidate.CandidatePath!))!;
            string delivered = Path.Combine(AppContext.BaseDirectory, "plugins", manifest.AssemblyRelativePath);
            string assemblySha = PolicyHash(await File.ReadAllBytesAsync(delivered));
            RequireWorkflow(assemblySha.Equals(active.Artifact.AssemblySha256, StringComparison.OrdinalIgnoreCase)
                && assemblySha == PolicyHash(await File.ReadAllBytesAsync(active.Artifact.AssemblyPath)), "Policy source differs from delivered bytes.");
            pluginProof = new { owner, source, active.ExecutionRevision, assemblySha256 = assemblySha,
                bundleManifestSha256 = PolicyHash(await File.ReadAllBytesAsync(candidate.CandidatePath!)) };
            var initial = Policy(); initialPolicy = initial;
            RequireWorkflow(initial.IsActive && initial.Snapshot.ProfileId == descriptor.Profile, "Wrong active profile authority.");
            RequireWorkflow(ApphostWorkflowSmoke.SamePath(_session.Config.DownloadPath, descriptor.DownloadRoot)
                && _session.Config.SaveUrlsOnExit && !_session.Config.AutomaticUpdateChecks, "Restored config escaped the same owned production profile.");
            RequireWorkflow(initial.IsGranted == (fixture.Phase == "revoke") && initial.Snapshot.AllowProviderSizeMd5Mismatch == (fixture.Phase == "revoke"),
                "Fresh production authority differs from the preceding durable grant/revoke.");
            if (fixture.Phase == "acquire")
            {
                RequireWorkflow(_session.GetSourceMediaIntegrityPolicies().All(row => !row.IsGranted), "Fresh profile not strict.");
                RequireWorkflow((await _session.AddAsync(ApphostIntegrityProcessSmoke.ThreadUrl)).Status == AdmissionStatus.Added, "Fixture admission failed.");
                await _session.SetPausedAsync(false);
                await WaitWorkflowAsync(() => Work() is { Status: MediaWorkStatus.NeedsIntervention, Failure: not null }
                    && _session.GetView().Threads.Single().Snapshot.Downloads is { Active: 0, Queued: 0 }, "strict provider rejection");
                var strict = Work()!;
                RequireWorkflow(strict.Failure == "Provider size or MD5 differs without current source permission and exact references."
                    && strict.Attempts == 1 && strict.HttpStatus == null, "Strict failure did not come from integrity enforcement.");
                var rejected = await ReadAsync();
                RequireWorkflow(rejected.Snapshot.AttachmentBindings.Count == 0 && rejected.Snapshot.Count == 0
                    && !File.Exists(rejected.Attachment.PlannedPath), "Strict rejection published an asset or binding.");
                await _session.SetSourceMediaIntegrityPermissionAsync(owner, source, true, initial.Snapshot.Revision);
                var granted = Policy(); acceptedPolicyRevision = granted.Snapshot.Revision;
                RequireWorkflow(granted.IsGranted && granted.Snapshot.AllowProviderSizeMd5Mismatch && granted.Snapshot.Revision > initial.Snapshot.Revision,
                    "Explicit real Host command did not grant permission.");
                var saved = Config.LoadConfig(descriptor.Profile).SourceMediaIntegrityPermissions[SourceMediaIntegrityPermission.CreateKey(owner, source)];
                RequireWorkflow(saved.AllowProviderSizeMd5Mismatch && saved.Revision == granted.Snapshot.Revision, "Host grant not durably saved.");
                var threadWork = _session.GetThreadWorkSnapshots().Single();
                int retried = threadWork.Intent == WorkIntent.Completed
                    ? await _session.RetryCompletedMediaAsync(ApphostIntegrityProcessSmoke.ThreadUrl, rejected.Attachment.MediaId)
                    : await _session.RetryMissingMediaAsync(_session.GetView().Threads.Single().WatchId, rejected.Attachment.MediaId);
                RequireWorkflow(retried == 1, "No exact explicit retry admitted.");
                await WaitWorkflowAsync(() => Work()?.Status == MediaWorkStatus.Succeeded, "granted production downloader retry");
                await _session.SetPausedAsync(true);
                RequireWorkflow(fixture.Http.Requests.Count == 3 && fixture.Http.Requests.Count(uri => uri == ApphostIntegrityProcessSmoke.OriginalUrl) == 2,
                    "Expected one thread and identical strict/granted originals.");
                RequireWorkflow(plugins.Runtime.GetSnapshots().Any(row => row.Identity.ExecutionRevision == active.ExecutionRevision && row.ProcessId is > 0),
                    "Real delivered source worker was not observed.");
            }
            else RequireWorkflow(fixture.Http.Requests.IsEmpty && plugins.Runtime.GetSnapshots().Count == 0 && _session.ReceivedMediaBytes == 0,
                "Offline startup attempted HTTP or production work.");

            var captured = await ReadAsync();
            var provider = new MediaProviderMetadata(captured.Attachment.MediaId!, captured.Attachment.Variant, captured.Attachment.Url!,
                captured.Attachment.OriginalFileName, captured.Attachment.ExpectedSize, captured.Attachment.ExpectedMd5, captured.Attachment.ExpectedSha256);
            var board = plugins.Sources.Single(row => row.Name == source);
            var threadHandle = board.GetThread(new Uri(ApphostIntegrityProcessSmoke.ThreadUrl));
            DownloadItem Item(MediaProviderMetadata metadata, string post = "123") => new(new Uri(metadata.Url), captured.Attachment.PlannedPath!, board,
                threadHandle, metadata.MediaId, metadata.ExpectedSize, metadata.ExpectedMd5, metadata.OriginalFileName) {
                ExpectedSha256 = metadata.ExpectedSha256, AcquisitionRevisionKey = MediaRevisionKeys.CreateAcquisitionKey(resource, metadata),
                AttachmentReferences = new[] { new AttachmentRevisionReference(resource, post, 0,
                    MediaRevisionKeys.CreateAttachmentKey(resource, post, 0, metadata)) } };
            RequireWorkflow(ReferenceEquals(Item(provider).MediaIntegrityPolicy, _session.GetOrCreateSourceMediaIntegrityPolicy(owner, source)),
                "DownloadItem did not inherit the actual production adapter policy.");
            // This seam publishes a later local acquisition with the actual adapter's production policy.
            // The archived post remains on A, proving that current-media lookup cannot replace exact history.
            var laterProvider = provider with { ExpectedSize = provider.ExpectedSize + 1 };
            byte[] laterBytes = fixture.Http.Payload.Concat(new byte[] { 42 }).ToArray();
            if (fixture.Phase == "acquire")
            {
                using var bytes = new MemoryStream(laterBytes, writable: false);
                await new MediaStore().CommitAsync(Item(laterProvider), bytes, CancellationToken.None);
                captured = await ReadAsync();
            }
            var bound = MediaAttachmentResolver.Resolve(captured.Snapshot, resource, "123", captured.Attachment)
                ?? throw new InvalidDataException("Exact accepted history did not resolve.");
            var binding = bound.Binding ?? throw new InvalidDataException("Acceptance binding missing.");
            RequireWorkflow(!bound.IsLegacy && !bound.Asset.IsCurrent && binding.Provider == provider
                && binding.AcquisitionRevisionKey == MediaRevisionKeys.CreateAcquisitionKey(resource, provider)
                && captured.Attachment.AttachmentRevisionKey == MediaRevisionKeys.CreateAttachmentKey(resource, "123", 0, provider)
                && binding.Acceptance.Kind == MediaAcceptanceKind.ExplicitProviderMismatch && binding.Acceptance.SizeMismatch
                && binding.Acceptance.Md5Mismatch && binding.Acceptance.ProfileId == descriptor.Profile
                && binding.Acceptance.PluginOwnerId == owner && binding.Acceptance.SourceId == source,
                "Exact provider/acquisition/attachment/actual/acceptance identity differs.");
            RequireWorkflow(acceptedPolicyRevision == null || binding.Acceptance.PolicyRevision == acceptedPolicyRevision,
                "Acceptance did not record the exact explicit Host grant revision.");
            RequireWorkflow(binding.Actual.Sha256 == PolicyHash(fixture.Http.Payload)
                && binding.Actual.Length == fixture.Http.Payload.Length
                && binding.Actual.Md5 == Convert.ToBase64String(MD5.HashData(fixture.Http.Payload)), "Accepted local digest differs.");
            var current = captured.Snapshot.GetPage().Single(row => row.IsCurrent);
            RequireWorkflow(current.AssetId != binding.AssetId && current.Sha256.Equals(PolicyHash(laterBytes), StringComparison.OrdinalIgnoreCase), "Later current revision missing.");
            await using (var bytes = await captured.Snapshot.OpenVerifiedReadAsync(binding.AssetId))
            { using var copy = new MemoryStream(); await bytes.CopyToAsync(copy); RequireWorkflow(copy.ToArray().SequenceEqual(fixture.Http.Payload), "Verified accepted bytes differ."); }
            var restored = await new MediaStore().TryRestoreAsync(Item(provider), CancellationToken.None);
            RequireWorkflow(restored is { Reused: true } && restored.AssetId == binding.AssetId, "Exact restore required fresh acceptance.");
            var gallery = GallerySelection.Build(captured.Thread, new[] { captured.Snapshot }).Entries.Single();
            RequireWorkflow(gallery.Asset.AssetId == binding.AssetId && gallery.AttachmentResolution!.Binding == binding, "Gallery replaced historical binding.");
            await using (var reader = await _session.OpenArchiveReaderAsync())
            {
                var export = await ArchiveHtmlExporter.ExportAsync(reader, resource, Path.Combine(descriptor.Root, "export-policy-" + fixture.Phase));
                string selected = Path.Combine(export.Directory, "media", PolicyHash(fixture.Http.Payload) + Path.GetExtension(bound.Asset.Path).ToLowerInvariant());
                RequireWorkflow(export.FileCount == 1 && export.Omissions.Count == 0 && File.Exists(selected)
                    && (await File.ReadAllBytesAsync(selected)).SequenceEqual(fixture.Http.Payload)
                    && (await File.ReadAllTextAsync(export.IndexPath)).Contains("media/" + Path.GetFileName(selected), StringComparison.Ordinal), "Exact HTML export differs.");
            }
            if (fixture.Phase == "revoke")
                await _session.SetSourceMediaIntegrityPermissionAsync(owner, source, false, Policy().Snapshot.Revision);
            if (fixture.Phase != "acquire")
            {
                var freshProvider = provider with { ExpectedSize = provider.ExpectedSize + 2 };
                foreach (var fresh in new[] { Item(freshProvider), Item(provider, "124") })
                {
                    RequireWorkflow(await new MediaStore().TryRestoreAsync(fresh, CancellationToken.None) == null, "New revision/reference inherited accepted bytes.");
                    bool rejected = false;
                    try { using var bytes = new MemoryStream(fixture.Http.Payload, writable: false); await new MediaStore().CommitAsync(fresh, bytes, CancellationToken.None); }
                    catch (InvalidDataException) { rejected = true; }
                    RequireWorkflow(rejected, "Revoked production policy accepted a new mismatch reference.");
                }
                var retained = await ReadAsync();
                RequireWorkflow(retained.Snapshot.AttachmentBindings.Count == 2 && retained.Snapshot.Count == 2, "Denied revision mutated manifest inventory.");
                RequireWorkflow(fixture.Http.Requests.IsEmpty && fixture.Http.Unexpected == 0, "Offline phase attempted HTTP.");
            }
            proof = new { binding, attachment = captured.Attachment, currentAssetId = current.AssetId,
                currentSha256 = PolicyHash(laterBytes), galleryAssetId = gallery.Asset.AssetId,
                exportSha256 = PolicyHash(fixture.Http.Payload), exactRestoreWithoutNewAcceptance = true,
                manifestSha256 = PolicyHash(await File.ReadAllBytesAsync(Path.Combine(captured.Snapshot.Directory, ".meowchan-manifest.json"))) };
            finalPolicy = Policy();
            workerPids = plugins.Runtime.GetSnapshots().Where(row => row.ProcessId.HasValue).Select(row => row.ProcessId!.Value).ToArray();
            await CloseWorkflowNormallyAsync();
            RequireWorkflow(_session.State == DesktopSessionState.Stopped && !IsVisible && _allowClose && plugins.Runtime.GetSnapshots().Count == 0
                && workerPids.All(pid => !PluginWorkflowProcessAlive(pid)), "Normal shutdown left a session/worker.");
            using (ProfileLease lease = ProfileLease.Acquire(descriptor.Profile)) { }
            var durablePermission = Config.LoadConfig(descriptor.Profile).SourceMediaIntegrityPermissions[SourceMediaIntegrityPermission.CreateKey(owner, source)];
            RequireWorkflow(durablePermission.AllowProviderSizeMd5Mismatch == (fixture.Phase == "acquire"), "Normal shutdown did not retain the durable permission.");
            RequireWorkflow(fixture.Http.Unexpected == 0, "Unexpected HTTP attempt.");
            completed = true;
        }
        catch (Exception error) { failure = error.ToString(); throw; }
        finally
        {
            File.WriteAllText(Path.Combine(descriptor.Profile, "apphost-policy-process.json"), JsonSerializer.Serialize(new {
                schemaVersion = 1, completed, failure, phase = fixture.Phase, profile = descriptor.Profile,
                ownerToken = descriptor.OwnerToken, processId = Environment.ProcessId, workerPids, initialPolicy, finalPolicy, pluginProof, proof,
                normalShutdown = completed, profileLeaseReleased = completed, requests = fixture.Http.Requests.ToArray(),
                unexpectedRequests = fixture.Http.Unexpected, renderScaling = RenderScaling,
                processPath = Environment.ProcessPath, processSha256 = Environment.ProcessPath == null ? null : PolicyHash(File.ReadAllBytes(Environment.ProcessPath)),
                appBaseDirectory = AppContext.BaseDirectory, workingDirectory = Environment.CurrentDirectory, managedAssemblies,
                scope = "Extracted production apphost/config/Host grant/PluginSource policy. Terminal fixture HTTP for strict/retry; later current revision and denied revisions use production MediaStore with actual source adapter policy. Exact resolver/Gallery selection/HTML bytes; no decoded gallery or live-source claim."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    private static string PolicyHash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

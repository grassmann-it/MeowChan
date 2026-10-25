using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Updates;
using MeowChan.Downloader;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

// Two separately invoked OS test processes. Both transports are terminal synthetic
// fixtures, never live provider evidence. Startup may run network-free Describe workers.
[Collection("Delivered plugin hosts")]
public sealed class SourceWaveProcessRestartTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [SourceWaveProcessFact]
    public async Task DeliveredSha1AndSizeHintBindingsSurviveAnActualOfflineProcessRestart()
    {
        string root = Path.GetFullPath(Environment.GetEnvironmentVariable("MEOWCHAN_WAVE_PROCESS_ROOT")!);
        string token = Environment.GetEnvironmentVariable("MEOWCHAN_WAVE_PROCESS_TOKEN")!;
        string phase = Environment.GetEnvironmentVariable("MEOWCHAN_WAVE_PROCESS_PHASE")!;
        Assert.True(phase is "acquire" or "offline");
        Assert.Equal(token, await File.ReadAllTextAsync(Path.Combine(root, ".wave-process-owner")));
        string profile = Path.Combine(root, "profile"), receipt = Path.Combine(root, phase + ".json");
        Assert.False(File.Exists(receipt));
        if (phase == "acquire")
        {
            Assert.False(Directory.Exists(profile));
            Directory.CreateDirectory(profile);
            new Config
            {
                DownloadPath = Path.Combine(profile, "media"), DownloadsPaused = true, SaveUrlsOnExit = true,
                AutomaticUpdateChecks = false, MinimizeToTray = false, CloseToTray = false,
                MaxParallelDownloads = 2, RecheckTimer = 3600, MinimumFreeSpaceGiB = 0
            }.SaveConfig(profile);
        }
        else
        {
            Assert.True(Directory.Exists(profile));
            using var prior = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "acquire.json")));
            Assert.True(prior.RootElement.GetProperty("completed").GetBoolean());
            Assert.Equal(token, prior.RootElement.GetProperty("token").GetString());
            Assert.Equal(profile, prior.RootElement.GetProperty("profile").GetString());
            Assert.NotEqual(Environment.ProcessId, prior.RootElement.GetProperty("processId").GetInt32());
        }

        using MakabaUboachanWaveFixtures fixture = new() { RejectAll = phase == "offline" };
        DesktopSessionHost host = new(profile, providers: null,
            updateSessionFactory: (path, config) => new UpdateSession(path, config, fixture.Client(), disableEnvironment: () => "1"),
            pluginTransportFactory: fixture.Client);
        List<object> proofs = [], plugins = [];
        List<WorkerIdentity> workers = [];
        try
        {
            await host.StartAsync().WaitAsync(Deadline);
            Assert.Equal(DesktopSessionState.Ready, host.State);
            Assert.True(host.IsPaused);
            Assert.Empty(host.Plugins!.Warnings);
            Assert.Empty(fixture.Requests);
            RememberWorkers(host, workers);
            if (phase == "acquire")
            {
                var installed = await host.Plugins.ProfileStore.ReadAsync();
                foreach (string source in MakabaUboachanWaveFixtures.Sources)
                {
                    string owner = "meowchan.source." + source;
                    Assert.False(Assert.Single(installed.Plugins, row => row.PluginId == owner).Enabled);
                    installed = await host.Plugins.ProfileStore.SetEnabledAsync(owner, true, installed.Revision);
                    Assert.Equal(AdmissionStatus.Added, (await host.AddAsync(MakabaUboachanWaveFixtures.ThreadUrl(source))).Status);
                }
                await host.SetPausedAsync(false);
                using CancellationTokenSource timeout = new(Deadline);
                while (!Settled(host)) await Task.Delay(25, timeout.Token);
                await host.SetPausedAsync(true);
                RememberWorkers(host, workers);
                Assert.Equal(8, fixture.Requests.Count); // 2 JSON, 2 hk→su original redirects, 4 PNGs.
                Assert.All(fixture.Requests.GroupBy(url => url), group => Assert.Single(group));
            }
            Assert.Empty(host.Config.SourceMediaIntegrityPermissions);
            foreach (string source in MakabaUboachanWaveFixtures.Sources)
            {
                var installed = Assert.Single((await host.Plugins.ProfileStore.ReadAsync()).Plugins,
                    row => row.PluginId == "meowchan.source." + source);
                Assert.True(installed.Enabled);
                Assert.NotNull(installed.Active);
                var active = installed.Active!;
                Assert.Equal(active.Artifact.AssemblySha256, Hash(await File.ReadAllBytesAsync(active.Artifact.AssemblyPath)), ignoreCase: true);
                if (phase == "acquire")
                    Assert.Contains(host.Plugins.Runtime.GetSnapshots(), row => row.Identity.ExecutionRevision == active.ExecutionRevision && row.ProcessId is > 0);
                plugins.Add(new { source, active.ExecutionRevision, assemblySha256 = active.Artifact.AssemblySha256 });
                proofs.Add(await VerifyAsync(host, fixture, source, root, phase));
            }
            Assert.Equal(0, fixture.Unexpected);
            if (phase == "offline")
            {
                Assert.Empty(fixture.Requests);
                Assert.Equal(0, host.ReceivedMediaBytes);
            }
            RememberWorkers(host, workers);
        }
        finally
        {
            RememberWorkers(host, workers);
            await host.DisposeAsync().AsTask().WaitAsync(Deadline);
            Assert.Equal(DesktopSessionState.Stopped, host.State);
            if (host.Plugins != null) Assert.Empty(host.Plugins.Runtime.GetSnapshots());
            foreach (var worker in workers)
                try
                {
                    using Process process = Process.GetProcessById(worker.Pid);
                    Assert.True(process.HasExited || process.StartTime.ToUniversalTime().Ticks != worker.StartUtcTicks,
                        "Original worker survived normal host disposal: " + worker.Pid);
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, phase, token, profile, completed = true, processId = Environment.ProcessId,
            processStartUtcTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
            networkNamespace = new FileInfo("/proc/self/ns/net").LinkTarget,
            declaredSourceCommit = Environment.GetEnvironmentVariable("MEOWCHAN_WAVE_PROCESS_SOURCE_COMMIT"),
            testAssemblySha256 = Hash(await File.ReadAllBytesAsync(typeof(SourceWaveProcessRestartTests).Assembly.Location)),
            applicationAssemblySha256 = Hash(await File.ReadAllBytesAsync(typeof(DesktopSessionHost).Assembly.Location)),
            coreAssemblySha256 = Hash(await File.ReadAllBytesAsync(typeof(MediaStore).Assembly.Location)),
            fixtureRequests = fixture.Requests.ToArray(), fixture.Unexpected, actualNetworkSends = 0,
            workerIdentities = workers, profileLeaseReleased = true, plugins, proofs,
            scope = "Production DesktopHost/delivered worker/queue/archive with synthetic PNGs and terminal metadata. Two actual OS processes; offline HTTP attempts must be zero. Network-free Describe workers are permitted. No live provider or rendered GUI claim."
        }, Json));
    }

    private static bool Settled(DesktopSessionHost host) => MakabaUboachanWaveFixtures.Sources.All(source =>
    {
        var work = host.GetThreadWorkSnapshots().SingleOrDefault(row => row.Url == MakabaUboachanWaveFixtures.ThreadUrl(source));
        return work?.Media is { Count: 2 } media && media.All(row => row.Status == MediaWorkStatus.Succeeded)
            && host.GetView().Threads.Single(row => row.Snapshot.Id == new ResourceId(source, "demo", "123")).Snapshot.Downloads is { Active: 0, Queued: 0 };
    });

    private static async Task<object> VerifyAsync(DesktopSessionHost host, MakabaUboachanWaveFixtures fixture, string source, string root, string phase)
    {
        ResourceId resource = new(source, "demo", "123");
        await using ArchiveReader reader = await host.OpenArchiveReaderAsync();
        var thread = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(resource));
        var attachments = Assert.Single(thread.GetPosts(), row => row.PostId == "123").Attachments!.OrderBy(row => row.Position).ToArray();
        Assert.Equal(2, attachments.Length);
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(thread.Summary.MediaDirectory);
        string manifestPath = Path.Combine(snapshot.Directory, ".meowchan-manifest.json");
        string beforeManifestSha = Hash(await File.ReadAllBytesAsync(manifestPath));
        Assert.Equal(2, snapshot.Count);
        Assert.Equal(2, snapshot.AttachmentBindings.Count);
        var gallery = GallerySelection.Build(thread, [snapshot]);
        Assert.Equal(2, gallery.Entries.Count);
        var work = Assert.Single(host.GetThreadWorkSnapshots(), row => row.Url == MakabaUboachanWaveFixtures.ThreadUrl(source));
        Assert.All(work.Media, row => { Assert.Equal(MediaWorkStatus.Succeeded, row.Status); Assert.Equal(1, row.Attempts); });
        var board = Assert.Single(host.Plugins!.Sources, row => row.Name == source);
        var threadHandle = board.GetThread(new Uri(MakabaUboachanWaveFixtures.ThreadUrl(source)));
        List<object> attachmentProofs = [];
        for (int slot = 0; slot < 2; slot++)
        {
            var attachment = attachments[slot];
            MediaProviderMetadata provider = new(attachment.MediaId!, attachment.Variant, attachment.Url!, attachment.OriginalFileName,
                attachment.ExpectedSize, attachment.ExpectedMd5, attachment.ExpectedSha256)
            { ExpectedSha1 = attachment.ExpectedSha1, ProviderSizeHint = attachment.ProviderSizeHint };
            var resolved = Assert.IsType<ResolvedMediaAttachment>(MediaAttachmentResolver.Resolve(snapshot, resource, "123", attachment));
            var binding = Assert.IsType<MediaAttachmentBinding>(resolved.Binding);
            Assert.False(resolved.IsLegacy);
            Assert.Equal(provider, binding.Provider);
            Assert.Equal(MediaRevisionKeys.CreateAcquisitionKey(resource, provider), binding.AcquisitionRevisionKey);
            Assert.Equal(MediaRevisionKeys.CreateAttachmentKey(resource, "123", slot, provider), attachment.AttachmentRevisionKey);
            Assert.Equal(MediaAcceptanceKind.Strict, binding.Acceptance.Kind);
            Assert.Equal(Convert.ToBase64String(SHA1.HashData(fixture.Originals[slot])), binding.Actual.Sha1);
            Assert.Equal(Hash(fixture.Originals[slot]), binding.Actual.Sha256);
            Assert.True(ProviderIntegrityAssessment.Assess(provider, binding.Actual).IsStrictMatch);
            var journal = Assert.Single(work.Media, row => row.MediaIdentifier == provider.MediaId);
            Assert.Equal(provider.ExpectedSha1, journal.ExpectedSha1);
            Assert.Equal(provider.ProviderSizeHint, journal.ProviderSizeHint);
            Assert.Equal(provider.ExpectedSize, journal.ExpectedSize);
            Assert.Equal(provider.ExpectedMd5, journal.ExpectedMd5);
            Assert.Equal(binding.AcquisitionRevisionKey, journal.AcquisitionRevisionKey);
            Assert.Equal(binding.AssetId, journal.CommittedAssetId);
            if (source == "2ch")
            {
                Assert.Null(provider.ExpectedSize);
                Assert.Equal(new MediaProviderSizeHint(slot == 0 ? "24.50" : "0", "Кб", MediaSizePrecision.Unspecified), provider.ProviderSizeHint);
            }
            else if (slot == 1)
            {
                Assert.Null(provider.ExpectedMd5);
                Assert.Equal(Convert.ToBase64String(SHA1.HashData(fixture.Originals[slot])), provider.ExpectedSha1);
            }
            bool extended = source == "2ch" || slot == 1;
            Assert.StartsWith(extended ? "att-v2:" : "att-v1:", attachment.AttachmentRevisionKey);
            Assert.StartsWith(extended ? "acq-v2:" : "acq-v1:", binding.AcquisitionRevisionKey);
            // Real production adapter supplies the policy; no fixture/test policy is substituted.
            DownloadItem item = new(new Uri(provider.Url), attachment.PlannedPath!, board, threadHandle, provider.MediaId,
                provider.ExpectedSize, provider.ExpectedMd5, provider.OriginalFileName)
            {
                ExpectedSha256 = provider.ExpectedSha256, ExpectedSha1 = provider.ExpectedSha1, ProviderSizeHint = provider.ProviderSizeHint,
                AcquisitionRevisionKey = binding.AcquisitionRevisionKey, AttachmentReferences = [binding.Reference]
            };
            Assert.Same(host.GetOrCreateSourceMediaIntegrityPolicy("meowchan.source." + source, source), item.MediaIntegrityPolicy);
            var restored = Assert.IsType<MediaCommitResult>(await new MediaStore().TryRestoreAsync(item, CancellationToken.None));
            Assert.True(restored.Reused);
            Assert.Equal(binding.AssetId, restored.AssetId);
            await using Stream verified = await snapshot.OpenVerifiedReadAsync(binding.AssetId);
            using MemoryStream bytes = new(); await verified.CopyToAsync(bytes);
            Assert.Equal(fixture.Originals[slot], bytes.ToArray());
            var entry = Assert.Single(gallery.Entries, row => row.Attachment?.Position == slot);
            Assert.Equal(binding, entry.AttachmentResolution!.Binding);
            attachmentProofs.Add(new { attachment, binding, galleryEntryKey = entry.EntryKey, restoredAssetId = restored.AssetId, bytesSha256 = Hash(bytes.ToArray()) });
        }
        var exported = await ArchiveHtmlExporter.ExportAsync(reader, resource, Path.Combine(root, "export-" + phase + "-" + source));
        Assert.Empty(exported.Omissions);
        Assert.Equal(2, exported.FileCount);
        string[] exportedHashes = (await Task.WhenAll(Directory.GetFiles(Path.Combine(exported.Directory, "media")).Select(async file => Hash(await File.ReadAllBytesAsync(file))))).Order().ToArray();
        Assert.Equal(fixture.Originals.Select(Hash).Order().ToArray(), exportedHashes);
        Assert.Equal(beforeManifestSha, Hash(await File.ReadAllBytesAsync(manifestPath)));
        return new { source, attachments = attachmentProofs, mediaWork = work.Media.OrderBy(row => row.MediaIdentifier).ToArray(),
            manifestSha256 = Hash(await File.ReadAllBytesAsync(Path.Combine(snapshot.Directory, ".meowchan-manifest.json"))), exportedHashes };
    }

    private sealed record WorkerIdentity(int Pid, long StartUtcTicks);
    private static void RememberWorkers(DesktopSessionHost host, List<WorkerIdentity> workers)
    {
        foreach (int pid in host.Plugins?.Runtime.GetSnapshots().Where(row => row.ProcessId.HasValue).Select(row => row.ProcessId!.Value) ?? [])
            try
            {
                using Process process = Process.GetProcessById(pid);
                WorkerIdentity identity = new(pid, process.StartTime.ToUniversalTime().Ticks);
                if (!workers.Contains(identity)) workers.Add(identity);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public sealed class SourceWaveProcessFactAttribute : FactAttribute
{
    public SourceWaveProcessFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("MEOWCHAN_WAVE_PROCESS_PROOF") != "1")
            Skip = "Requires the explicit owned two-process source-wave driver; never runs acquisition in ordinary CI.";
    }
}

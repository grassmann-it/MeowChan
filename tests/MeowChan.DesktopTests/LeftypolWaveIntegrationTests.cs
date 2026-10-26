using System.Diagnostics;
using System.Security.Cryptography;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Updates;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.PluginSdk;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

// Production HostHttp/worker/downloader/store path with synthetic metadata and valid PNGs.
// A fresh offline host is in the same OS test process; GallerySelection is not a rendered GUI.
[Collection("Delivered plugin hosts")]
public sealed class LeftypolWaveIntegrationTests
{
    private const string Plugin = "meowchan.source.leftypol";
    private static readonly ResourceId Thread = new("leftypol", "demo", "123");
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(45);

    [Fact]
    public async Task ExperimentalActivationIsExplicitPassiveAndSourceScoped()
    {
        using TempDirectory profile = new();
        using LeftypolWaveFixtures fixture = new() { RejectAll = true };
        await using DesktopSessionHost host = await StartAsync(profile.Path, fixture);
        Assert.False(Assert.Single((await host.Plugins!.ProfileStore.ReadAsync()).Plugins, row => row.PluginId == Plugin).Enabled);
        var projection = await host.ProjectExperimentalSourceActivationAsync(LeftypolWaveFixtures.ThreadUrl);
        Assert.Equal(ExperimentalSourceActivationProjectionStatus.ConfirmationRequired, projection.Status);
        var target = Assert.IsType<ExperimentalSourceActivationTarget>(projection.Target);
        Assert.Equal(Plugin, target.PluginOwnerId);
        Assert.Equal("leftypol", target.SourceId);
        Assert.True(target.RequestsMismatchPermission);
        Assert.False(target.IsEnabled);
        Assert.False(target.IsGranted);
        Assert.Empty(fixture.Requests);
        Assert.Empty(host.Plugins.Runtime.GetSnapshots());
        var activated = await host.ActivateAndAllowExperimentalSourceAsync(target).WaitAsync(Deadline);
        Assert.Equal(ExperimentalSourceActivationStatus.Succeeded, activated.Status);
        Assert.True(activated.ActualState!.IsEffective);
        Assert.True(Policy(host).IsGranted);
        Assert.All(host.GetSourceMediaIntegrityPolicies().Where(row => row.Snapshot.SourceId != "leftypol"), row => Assert.False(row.IsGranted));
        Assert.Empty(fixture.Requests);
        Assert.Empty(host.GetThreadWorkSnapshots());
    }

    [Fact]
    public async Task StrictSizeAndMd5RejectThenExplicitRetryPreservesOriginalsAcrossOfflineHost()
    {
        using TempDirectory profile = new();
        using LeftypolWaveFixtures fixture = new();
        DesktopSessionHost? host = null;
        try
        {
            host = await StartAsync(profile.Path, fixture);
            var installed = await host.Plugins!.ProfileStore.ReadAsync();
            Assert.False(Assert.Single(installed.Plugins, row => row.PluginId == Plugin).Enabled);
            installed = await host.Plugins.ProfileStore.SetEnabledAsync(Plugin, true, installed.Revision);
            // A direct store mutation updates live authority, but bypasses the host command's capture boundary.
            DesktopSessionProjection enabledFrame = await host.CaptureProjectionAsync().WaitAsync(Deadline);
            Assert.False(enabledFrame.IsStale, enabledFrame.CaptureError);
            string revision = Assert.Single(installed.Plugins, row => row.PluginId == Plugin).Active!.ExecutionRevision;
            Assert.Equal(SourceMaturity.Experimental, Policy(host).Maturity);
            Assert.True(Policy(host).IsActive);
            Assert.False(Policy(host).IsGranted); // Ordinary enable is never integrity consent.
            Assert.Empty(fixture.Requests);
            await host.SetPausedAsync(false);
            var catalog = await host.PreviewBoardAdmissionAsync(LeftypolWaveFixtures.BoardUrl);
            Assert.Contains(catalog.Threads, row => row.Id == "123");
            Assert.Equal(AdmissionStatus.Added, (await host.AddAsync(LeftypolWaveFixtures.ThreadUrl)).Status);
            MediaWork[] rejected = await WaitAsync(host, MediaWorkStatus.NeedsIntervention);
            Assert.All(rejected, row =>
            {
                Assert.Equal(1, row.Attempts);
                Assert.Null(row.HttpStatus);
                Assert.Equal("Provider size or MD5 differs without current source permission and exact references.", row.Failure);
                Assert.False(File.Exists(row.DestinationPath));
            });
            var strict = await ReadAsync(host);
            Assert.Equal(2, strict.Attachments.Length);
            Assert.Empty(strict.Assets.AttachmentBindings);
            Assert.Equal(0, strict.Assets.Count);
            for (int i = 0; i < 2; i++)
            {
                AssertProvider(strict.Attachments[i], fixture, i);
                Assert.Equal(1, fixture.OriginalRequests(i));
            }
            Assert.Equal(4, fixture.Requests.Count); // Catalog, thread and exactly two originals.
            Assert.True(Assert.Single(host.Plugins.Runtime.GetSnapshots(), row => row.Identity.ExecutionRevision == revision).ProcessId is > 0);

            var before = Policy(host);
            await host.SetSourceMediaIntegrityPermissionAsync(Plugin, "leftypol", true, before.Snapshot.Revision);
            long grantRevision = Policy(host).Snapshot.Revision;
            Assert.True(Policy(host).IsGranted);
            fixture.RetryArmed = true;
            for (int i = 0; i < 2; i++)
            {
                ThreadWorkSnapshot work = Assert.Single(host.GetThreadWorkSnapshots(), row => row.Url == LeftypolWaveFixtures.ThreadUrl);
                if (work.Intent == WorkIntent.Completed)
                    Assert.Equal(1, await host.RetryCompletedMediaAsync(LeftypolWaveFixtures.ThreadUrl, LeftypolWaveFixtures.Ids[i]));
                else
                {
                    var watch = Assert.Single(host.GetView().Threads, row => row.Snapshot.Id == Thread);
                    Assert.Equal(1, await host.RetryMissingMediaAsync(watch.WatchId, LeftypolWaveFixtures.Ids[i]));
                }
            }
            await WaitAsync(host, MediaWorkStatus.Succeeded);
            await host.SetPausedAsync(true);
            await VerifyAsync(host, fixture, profile.Path, grantRevision, "accepted");
            Assert.Equal(6, fixture.Requests.Count);
            Assert.All(Enumerable.Range(0, 2), i => Assert.Equal(2, fixture.OriginalRequests(i)));
            await host.SetSourceMediaIntegrityPermissionAsync(Plugin, "leftypol", false, grantRevision);
            await VerifyAsync(host, fixture, profile.Path, grantRevision, "revoked");
            await StopAsync(host);
            host = null;

            using LeftypolWaveFixtures offline = new() { RejectAll = true };
            host = await StartAsync(profile.Path, offline, writeConfig: false);
            Assert.True(host.IsPaused);
            Assert.False(Policy(host).IsGranted);
            Assert.Equal(revision, Assert.Single((await host.Plugins!.ProfileStore.ReadAsync()).Plugins, row => row.PluginId == Plugin).Active!.ExecutionRevision);
            await VerifyAsync(host, fixture, profile.Path, grantRevision, "offline");
            Assert.Empty(offline.Requests);
            Assert.Empty(host.Plugins.Runtime.GetSnapshots());

            // Explicit provider permission never bypasses verification of local committed bytes.
            await host.SetSourceMediaIntegrityPermissionAsync(Plugin, "leftypol", true, Policy(host).Snapshot.Revision);
            Assert.True(Policy(host).IsGranted);
            var saved = await ReadAsync(host);
            var asset = Assert.Single(saved.Assets.GetPage(), row => row.MediaIdentifier == LeftypolWaveFixtures.Ids[0]);
            byte[] changed = await File.ReadAllBytesAsync(asset.Path);
            changed[^1] ^= 1; // Same length, so this specifically checks SHA256 validation.
            await File.WriteAllBytesAsync(asset.Path, changed);
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
            {
                await using Stream ignored = await saved.Assets.OpenVerifiedReadAsync(asset.AssetId);
            });
            Assert.Empty(offline.Requests);
        }
        finally { if (host != null) await StopAsync(host); }
    }

    private static SourceMediaIntegrityPolicyView Policy(DesktopSessionHost host) => Assert.Single(host.GetSourceMediaIntegrityPolicies(),
        row => row.Snapshot.PluginOwnerId == Plugin && row.Snapshot.SourceId == "leftypol");

    private static async Task<DesktopSessionHost> StartAsync(string profile, LeftypolWaveFixtures fixture, bool writeConfig = true)
    {
        if (writeConfig) new Config
        {
            DownloadPath = Path.Combine(profile, "media"), DownloadsPaused = true, SaveUrlsOnExit = true,
            AutomaticUpdateChecks = false, MinimizeToTray = false, CloseToTray = false,
            MaxParallelDownloads = 1, RecheckTimer = 3600, MinimumFreeSpaceGiB = 0
        }.SaveConfig(profile);
        DesktopSessionHost host = new(profile, providers: null,
            updateSessionFactory: (path, config) => new UpdateSession(path, config, fixture.Client(), disableEnvironment: () => "1"),
            pluginTransportFactory: fixture.Client);
        try
        {
            await host.StartAsync().WaitAsync(Deadline);
            Assert.Equal(DesktopSessionState.Ready, host.State);
            Assert.Empty(host.Plugins!.Warnings);
            return host;
        }
        catch { await host.DisposeAsync(); throw; }
    }

    private static async Task<MediaWork[]> WaitAsync(DesktopSessionHost host, MediaWorkStatus expected)
    {
        using CancellationTokenSource deadline = new(Deadline);
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            MediaWork[] work = host.GetThreadWorkSnapshots().Where(row => row.Url == LeftypolWaveFixtures.ThreadUrl)
                .SelectMany(row => row.Media).Where(row => row.Variant == "original").ToArray();
            if (work.Length == 2 && work.All(row => row.Status == expected))
            {
                var watch = host.GetView().Threads.SingleOrDefault(row => row.Snapshot.Id == Thread);
                if (watch == null || watch.Snapshot.Downloads is { Active: 0, Queued: 0 }) return work;
            }
            await Task.Delay(50, deadline.Token);
        }
    }

    private static async Task<(CapturedAttachment[] Attachments, MediaAssetSnapshot Assets)> ReadAsync(DesktopSessionHost host)
    {
        await using ArchiveReader reader = await host.OpenArchiveReaderAsync();
        var thread = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Thread));
        var post = Assert.Single(thread.GetPosts(), row => row.PostId == "123");
        return (post.Attachments!.OrderBy(row => row.Position).ToArray(), await MediaAssetReader.OpenAsync(thread.Summary.MediaDirectory));
    }

    private static void AssertProvider(CapturedAttachment attachment, LeftypolWaveFixtures fixture, int index)
    {
        Assert.Equal(LeftypolWaveFixtures.Ids[index], attachment.MediaId);
        Assert.Equal(index, attachment.Position);
        Assert.Equal("original", attachment.Variant);
        Assert.Equal(LeftypolWaveFixtures.OriginalUrl(index), attachment.Url);
        Assert.Equal(fixture.ProviderSize(index), attachment.ExpectedSize);
        Assert.Equal(fixture.ProviderMd5(index), attachment.ExpectedMd5);
        Assert.Null(attachment.ExpectedSha256);
        Assert.Equal("same-name.png", attachment.OriginalFileName);
    }

    private static async Task VerifyAsync(DesktopSessionHost host, LeftypolWaveFixtures fixture, string profile, long revision, string phase)
    {
        var saved = await ReadAsync(host);
        Assert.Equal(2, saved.Assets.Count);
        Assert.Equal(2, saved.Assets.AttachmentBindings.Count);
        await using ArchiveReader reader = await host.OpenArchiveReaderAsync();
        var thread = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(Thread));
        var gallery = GallerySelection.Build(thread, [saved.Assets]);
        Assert.Equal(2, gallery.Entries.Count);
        var export = await ArchiveHtmlExporter.ExportAsync(reader, Thread, Path.Combine(profile, "export-" + phase));
        for (int i = 0; i < 2; i++)
        {
            CapturedAttachment attachment = saved.Attachments[i];
            AssertProvider(attachment, fixture, i);
            var resolved = Assert.IsType<ResolvedMediaAttachment>(MediaAttachmentResolver.Resolve(saved.Assets, Thread, "123", attachment));
            var binding = Assert.IsType<MediaAttachmentBinding>(resolved.Binding);
            Assert.Equal(fixture.ProviderSize(i), binding.Provider.ExpectedSize);
            Assert.Equal(fixture.ProviderMd5(i), binding.Provider.ExpectedMd5);
            Assert.Equal(fixture.Originals[i].LongLength, binding.Actual.Length);
            Assert.Equal(Convert.ToBase64String(MD5.HashData(fixture.Originals[i])), binding.Actual.Md5);
            Assert.Equal(Hash(fixture.Originals[i]), binding.Actual.Sha256);
            Assert.Equal(MediaAcceptanceKind.ExplicitProviderMismatch, binding.Acceptance.Kind);
            Assert.Equal(i == 0, binding.Acceptance.SizeMismatch);
            Assert.Equal(i == 1, binding.Acceptance.Md5Mismatch);
            Assert.Equal(revision, binding.Acceptance.PolicyRevision);
            Assert.Equal(Plugin, binding.Acceptance.PluginOwnerId);
            Assert.Equal("leftypol", binding.Acceptance.SourceId);
            Assert.Equal(profile, binding.Acceptance.ProfileId);
            Assert.False(ProviderIntegrityAssessment.Assess(binding.Provider, binding.Actual).Sha256Mismatch);
            await using Stream verified = await saved.Assets.OpenVerifiedReadAsync(binding.AssetId);
            using MemoryStream bytes = new();
            await verified.CopyToAsync(bytes);
            Assert.Equal(fixture.Originals[i], bytes.ToArray());
            Assert.Contains(gallery.Entries, row => row.Asset.AssetId == binding.AssetId && row.AttachmentResolution?.Binding == binding);
            Assert.Equal(fixture.Originals[i], await File.ReadAllBytesAsync(Path.Combine(export.Directory, "media", Hash(fixture.Originals[i]) + ".png")));
        }
    }

    private static async Task StopAsync(DesktopSessionHost host)
    {
        int[] pids = host.Plugins?.Runtime.GetSnapshots().Where(row => row.ProcessId.HasValue).Select(row => row.ProcessId!.Value).ToArray() ?? [];
        await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(25));
        Assert.Equal(DesktopSessionState.Stopped, host.State);
        Assert.Empty(host.Plugins!.Runtime.GetSnapshots());
        foreach (int pid in pids)
            try { using Process process = Process.GetProcessById(pid); Assert.True(process.HasExited); } catch (ArgumentException) { }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

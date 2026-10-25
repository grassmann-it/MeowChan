using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Admission;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Updates;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginSdk;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

// Actual delivered worker processes, IPC, queue and archive; HTTP remains
// synthetic/terminal. A fresh DesktopSessionHost is not an OS-process restart.
[Collection("Delivered plugin hosts")]
public sealed class MakabaUboachanWaveIntegrationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);
    private static readonly string[] NewSources = ["2ch", "leftypol", "uboachan", "zzzchan", "sushichan"];

    [Theory]
    [InlineData("2ch")]
    [InlineData("uboachan")]
    public async Task DeliveredCatalogOriginalsAndProvenanceRestoreThroughAFreshOfflineHost(string source)
    {
        using TempDirectory profile = new();
        Configure(profile.Path);
        using MakabaUboachanWaveFixtures fixture = new();
        DesktopSessionHost host = Create(profile.Path, fixture);
        try
        {
            await StartAsync(host, fixture, fresh: true);
            await ActivateStrictAsync(host, fixture, source);
            // Catalog reads share the ordinary paused metadata queue.
            await host.SetPausedAsync(false);
            BoardAdmissionPreview preview = await host.PreviewBoardAdmissionAsync(MakabaUboachanWaveFixtures.BoardUrl(source)).WaitAsync(Deadline);
            Assert.Equal(MakabaUboachanWaveFixtures.ThreadUrl(source), Assert.Single(preview.Threads).Url);
            Assert.Empty(host.GetView().Threads);
            Assert.Empty(host.GetThreadWorkSnapshots());
            Assert.Equal(AdmissionStatus.Added, (await host.AddAsync(MakabaUboachanWaveFixtures.ThreadUrl(source))).Status);
            await host.SetPausedAsync(false);
            await WaitAsync(() => Settled(host, source, expectedFailure: false), host);
            var worker = Assert.Single(host.Plugins!.Runtime.GetSnapshots());
            Assert.True(worker.ProcessId is > 0);
            var installed = await host.Plugins!.ProfileStore.ReadAsync();
            Assert.Equal(installed.Plugins.Single(plugin => plugin.PluginId == "meowchan.source." + source).Active!.ExecutionRevision,
                worker.Identity.ExecutionRevision);
            await AssertArchiveAsync(host, fixture, source);
            Assert.Equal(source == "2ch" ? 6 : 4, fixture.Requests.Count);
            Assert.All(fixture.Requests.GroupBy(uri => uri), group => Assert.Single(group));
            if (source == "2ch")
            {
                Assert.Equal(2, fixture.Requests.Count(uri => uri.StartsWith("https://2ch.hk/demo/src/", StringComparison.Ordinal)));
                Assert.Equal(2, fixture.Requests.Count(uri => uri.StartsWith("https://2ch.su/demo/src/", StringComparison.Ordinal)));
            }
            Assert.Equal(0, fixture.Unexpected);
            await host.SetPausedAsync(true);
        }
        finally { await StopAsync(host); }

        using MakabaUboachanWaveFixtures offline = new() { RejectAll = true };
        DesktopSessionHost restored = Create(profile.Path, offline);
        try
        {
            await StartAsync(restored, offline, fresh: false);
            Assert.True(restored.IsPaused);
            Assert.True(Assert.Single(restored.Plugins!.Sources, row => row.Name == source).Enabled);
            Assert.True(Settled(restored, source, expectedFailure: false));
            await AssertArchiveAsync(restored, fixture, source);
            Assert.Empty(offline.Requests);
            Assert.Empty(restored.Plugins!.Runtime.GetSnapshots());
            Assert.Empty(restored.Config.SourceMediaIntegrityPermissions);
        }
        finally { await StopAsync(restored); }
    }

    [Theory]
    [InlineData("2ch")]
    [InlineData("uboachan")]
    public async Task ProviderMd5OrSha1MismatchNeverPublishesTheCorruptExpectation(string source)
    {
        using TempDirectory profile = new();
        Configure(profile.Path);
        using MakabaUboachanWaveFixtures fixture = new() { CorruptExpectedHash = true };
        DesktopSessionHost host = Create(profile.Path, fixture);
        try
        {
            await StartAsync(host, fixture, fresh: true);
            await ActivateStrictAsync(host, fixture, source);
            Assert.Equal(AdmissionStatus.Added, (await host.AddAsync(MakabaUboachanWaveFixtures.ThreadUrl(source))).Status);
            await host.SetPausedAsync(false);
            await WaitAsync(() => Settled(host, source, expectedFailure: true), host);
            MediaWork rejected = Assert.Single(host.GetThreadWorkSnapshots().Single().Media, item => item.MediaIdentifier == "123-1");
            Assert.NotNull(rejected.Failure);
            Assert.Contains(source == "2ch" ? "MD5" : "SHA1", rejected.Failure, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(rejected.DestinationPath));
            await using ArchiveReader reader = await host.OpenArchiveReaderAsync();
            var view = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(new(source, "demo", "123")));
            MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(view.Summary.MediaDirectory);
            Assert.Equal("123-0", Assert.Single(manifest.GetPage()).MediaIdentifier);
            CapturedAttachment rejectedAttachment = Assert.Single(view.GetPosts()[0].Attachments!, row => row.Position == 1);
            Assert.Null(MediaAttachmentResolver.Resolve(manifest, view.Summary.Id, "123", rejectedAttachment));
            Assert.Empty(host.Config.SourceMediaIntegrityPermissions);
            Assert.Equal(0, fixture.Unexpected);
        }
        finally { await StopAsync(host); }
    }

    [Fact]
    public async Task MakabaOriginalRedirectCannotReachAForeignHost()
    {
        using TempDirectory profile = new();
        Configure(profile.Path);
        using MakabaUboachanWaveFixtures fixture = new() { OriginalRedirectHost = "foreign.invalid" };
        DesktopSessionHost host = Create(profile.Path, fixture);
        try
        {
            await StartAsync(host, fixture, fresh: true);
            await ActivateStrictAsync(host, fixture, "2ch");
            Assert.Equal(AdmissionStatus.Added, (await host.AddAsync(MakabaUboachanWaveFixtures.ThreadUrl("2ch"))).Status);
            await host.SetPausedAsync(false);
            await WaitAsync(() => host.GetThreadWorkSnapshots().SingleOrDefault()?.Media is { Count: 2 } media
                && media.All(item => item.LastAttemptAt != null && item.Failure != null), host);
            Assert.All(host.GetThreadWorkSnapshots().Single().Media, item => Assert.False(File.Exists(item.DestinationPath)));
            Assert.DoesNotContain(fixture.Requests, uri => uri.Contains("foreign.invalid", StringComparison.Ordinal));
            Assert.Equal(0, fixture.Unexpected);
            await using ArchiveReader reader = await host.OpenArchiveReaderAsync();
            var view = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(new("2ch", "demo", "123")));
            Assert.Empty((await MediaAssetReader.OpenAsync(view.Summary.MediaDirectory)).GetPage());
        }
        finally { await StopAsync(host); }
    }

    [Fact]
    public async Task MakabaMetadataCannotFollowRedirectToAnOriginalOnlyHost()
    {
        using TempDirectory profile = new();
        Configure(profile.Path);
        using MakabaUboachanWaveFixtures fixture = new() { RedirectMetadataToOriginalOnlyHost = true };
        DesktopSessionHost host = Create(profile.Path, fixture);
        try
        {
            await StartAsync(host, fixture, fresh: true);
            await ActivateStrictAsync(host, fixture, "2ch");
            await host.SetPausedAsync(false);
            var failure = await Assert.ThrowsAsync<PluginWorkerException>(() => host.PreviewBoardAdmissionAsync(MakabaUboachanWaveFixtures.BoardUrl("2ch")).WaitAsync(Deadline));
            Assert.Equal("http-redirect", failure.Code);
            Assert.Equal("https://2ch.hk/demo/catalog.json", Assert.Single(fixture.Requests));
            Assert.Equal(0, fixture.Unexpected);
            Assert.Empty(host.GetView().Threads);
            Assert.Empty(host.GetThreadWorkSnapshots());
        }
        finally { await StopAsync(host); }
    }

    private static async Task StartAsync(DesktopSessionHost host, MakabaUboachanWaveFixtures fixture, bool fresh)
    {
        await host.StartAsync().WaitAsync(Deadline);
        Assert.True(host.State == DesktopSessionState.Ready, host.StartupError);
        Assert.NotNull(host.Plugins);
        var plugins = host.Plugins!;
        Assert.Empty(plugins.Warnings);
        var persisted = await plugins.ProfileStore.ReadAsync();
        foreach (string source in NewSources)
        {
            var adapter = Assert.Single(plugins.Sources, row => row.Name == source);
            Assert.Equal(SourceMaturity.Experimental, adapter.Description.Maturity);
            var plugin = Assert.Single(persisted.Plugins, row => row.PluginId == "meowchan.source." + source);
            Assert.NotNull(plugin.Active);
            Assert.Null(plugin.Problem);
            if (fresh) { Assert.False(adapter.Enabled); Assert.False(plugin.Enabled); }
        }
        Assert.Empty(host.Config.SourceMediaIntegrityPermissions);
        Assert.Empty(fixture.Requests);
        Assert.Empty(plugins.Runtime.GetSnapshots());
    }

    private static async Task ActivateStrictAsync(DesktopSessionHost host, MakabaUboachanWaveFixtures fixture, string source)
    {
        var projection = await host.ProjectExperimentalSourceActivationAsync(MakabaUboachanWaveFixtures.ThreadUrl(source));
        Assert.Equal(ExperimentalSourceActivationProjectionStatus.ConfirmationRequired, projection.Status);
        var target = Assert.IsType<ExperimentalSourceActivationTarget>(projection.Target);
        Assert.False(target.RequestsMismatchPermission);
        Assert.False(target.IsGranted);
        var result = await host.ActivateAndAllowExperimentalSourceAsync(target).WaitAsync(Deadline);
        Assert.Equal(ExperimentalSourceActivationStatus.Succeeded, result.Status);
        var actual = Assert.IsType<ExperimentalSourceActivationActualState>(result.ActualState);
        Assert.True(actual.IsEnabled);
        Assert.True(actual.IsActive);
        Assert.False(actual.IsGranted);
        SourceMediaIntegrityPolicyView policy = Assert.Single(host.GetSourceMediaIntegrityPolicies(), row => row.Snapshot.SourceId == source);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.SetSourceMediaIntegrityPermissionAsync(target.PluginOwnerId, source, true, policy.Snapshot.Revision));
        Assert.Empty(host.Config.SourceMediaIntegrityPermissions);
        Assert.Empty(fixture.Requests);
        Assert.Empty(host.Plugins!.Runtime.GetSnapshots());
    }

    private static async Task AssertArchiveAsync(DesktopSessionHost host, MakabaUboachanWaveFixtures fixture, string source)
    {
        await using ArchiveReader reader = await host.OpenArchiveReaderAsync();
        var thread = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(new(source, "demo", "123")));
        Assert.Equal(new[] { "123", "124" }, thread.GetPosts().Select(row => row.PostId));
        CapturedAttachment[] attachments = thread.GetPosts()[0].Attachments!.ToArray();
        Assert.Equal(new[] { "123-0", "123-1" }, attachments.Select(row => row.MediaId));
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(thread.Summary.MediaDirectory);
        Assert.Equal(2, manifest.Count);
        Assert.Equal(2, GallerySelection.Build(thread, [manifest]).Entries.Count);
        for (int slot = 0; slot < attachments.Length; slot++)
        {
            CapturedAttachment attachment = attachments[slot];
            var resolved = Assert.IsType<ResolvedMediaAttachment>(MediaAttachmentResolver.Resolve(manifest, thread.Summary.Id, "123", attachment));
            var binding = Assert.IsType<MediaAttachmentBinding>(resolved.Binding);
            Assert.False(resolved.IsLegacy);
            Assert.Equal(MediaAcceptanceKind.Strict, binding.Acceptance.Kind);
            Assert.True(ProviderIntegrityAssessment.Assess(binding.Provider, binding.Actual).IsStrictMatch);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(fixture.Originals[slot])), binding.Actual.Sha256, ignoreCase: true);
            Assert.Equal(Convert.ToBase64String(SHA1.HashData(fixture.Originals[slot])), binding.Actual.Sha1);
            if (source == "2ch")
            {
                Assert.Null(attachment.ExpectedSize);
                Assert.Equal(new MediaProviderSizeHint(slot == 0 ? "24.50" : "0", "Кб", MediaSizePrecision.Unspecified), attachment.ProviderSizeHint);
                Assert.Equal(attachment.ProviderSizeHint, binding.Provider.ProviderSizeHint);
                Assert.Equal(Convert.ToBase64String(MD5.HashData(fixture.Originals[slot])), attachment.ExpectedMd5);
            }
            else if (slot == 0) { Assert.NotNull(attachment.ExpectedMd5); Assert.Null(attachment.ExpectedSha1); }
            else
            {
                Assert.Null(attachment.ExpectedMd5);
                Assert.Equal(Convert.ToBase64String(SHA1.HashData(fixture.Originals[slot])), attachment.ExpectedSha1);
                Assert.Equal(attachment.ExpectedSha1, binding.Provider.ExpectedSha1);
            }
            await using Stream verified = await manifest.OpenVerifiedReadAsync(binding.AssetId);
            using MemoryStream bytes = new();
            await verified.CopyToAsync(bytes);
            Assert.Equal(fixture.Originals[slot], bytes.ToArray());
            GalleryEntry entry = Assert.Single(GallerySelection.Build(thread, [manifest]).Entries, row => row.Attachment?.Position == slot);
            Assert.Equal(binding, entry.AttachmentResolution!.Binding);
        }
    }

    private static bool Settled(DesktopSessionHost host, string source, bool expectedFailure)
    {
        var work = host.GetThreadWorkSnapshots().SingleOrDefault(row => row.Url == MakabaUboachanWaveFixtures.ThreadUrl(source));
        return work?.Media is { Count: 2 } media && media.Single(row => row.MediaIdentifier == "123-0").Status == MediaWorkStatus.Succeeded
            && (expectedFailure ? media.Single(row => row.MediaIdentifier == "123-1") is { LastAttemptAt: not null, Failure: not null }
                : media.All(row => row.Status == MediaWorkStatus.Succeeded));
    }

    private static void Configure(string profile) => new Config
    {
        DownloadPath = Path.Combine(profile, "media"), DownloadsPaused = true, SaveUrlsOnExit = true,
        AutomaticUpdateChecks = false, MinimumFreeSpaceGiB = 0, MaxParallelDownloads = 2, RecheckTimer = 3600
    }.SaveConfig(profile);
    private static DesktopSessionHost Create(string profile, MakabaUboachanWaveFixtures fixture) => new(profile,
        updateSessionFactory: (path, config) => new UpdateSession(path, config, fixture.Client(), disableEnvironment: () => "1"),
        pluginTransportFactory: fixture.Client);
    private static async Task WaitAsync(Func<bool> condition, DesktopSessionHost host)
    {
        using CancellationTokenSource deadline = new(Deadline);
        try { while (!condition()) await Task.Delay(25, deadline.Token); }
        catch (OperationCanceledException) { Assert.Fail("Makaba/Uboachan workflow did not settle: " + JsonSerializer.Serialize(host.GetThreadWorkSnapshots())); }
    }
    private static async Task StopAsync(DesktopSessionHost host)
    {
        int[] pids = host.Plugins?.Runtime.GetSnapshots().Where(row => row.ProcessId.HasValue).Select(row => row.ProcessId!.Value).ToArray() ?? [];
        await host.DisposeAsync().AsTask().WaitAsync(Deadline);
        Assert.Equal(DesktopSessionState.Stopped, host.State);
        foreach (int pid in pids)
        {
            try { using Process process = Process.GetProcessById(pid); Assert.True(process.HasExited, "Owned source worker survived shutdown: " + pid); }
            catch (ArgumentException) { }
        }
    }
}

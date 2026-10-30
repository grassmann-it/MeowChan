using System.Diagnostics;
using System.Text.Json;
using MeowChan.Admission;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Updates;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Installation;
using MeowChan.PluginSdk;
using MeowChan.Storage;
using Xunit.Abstractions;

namespace MeowChan.DesktopTests;

// Controlled current-build startup failures, not a reproduction of the reported
// Windows 1.0.0 -> 1.0.1 Leftypol incident. No installation payload is mutated.
[Collection("Delivered plugin hosts")]
public sealed class StartupIsolationIntegrationTests(ITestOutputHelper output)
{
    private const string FailedPlugin = "meowchan.source.lainchan";
    private const string HealthyPlugin = "meowchan.source.kohlchan";
    private const string FilterPlugin = "meowchan.example.image-dimensions";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(45);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableSelectedCandidateKeepsOtherSourcesArchiveWatchesAndExplicitChoices(bool missing)
    {
        using TempDirectory profile = new();
        new Config
        {
            DownloadPath = Path.Combine(profile.Path, "media"), DownloadsPaused = true,
            SaveUrlsOnExit = true, AutomaticUpdateChecks = false, RecheckTimer = 3600,
            MaxParallelDownloads = 2, MinimumFreeSpaceGiB = 0
        }.SaveConfig(profile.Path);
        PluginProfileSnapshot saved;
        string selected;
        string candidatePath;
        string[] sourceNames;
        using (DesktopPluginFixtures fixture = new())
        {
            DesktopSessionHost host = Create(profile.Path, fixture);
            try
            {
                await StartAsync(host, "seed");
                Assert.Empty(host.Plugins!.Warnings);
                sourceNames = host.Plugins.Sources.Select(source => source.Name).Order().ToArray();
                saved = await host.Plugins.ProfileStore.ReadAsync();
                saved = await host.Plugins.ProfileStore.SetEnabledAsync(HealthyPlugin, true, saved.Revision);
                // A separate explicit off choice must not be reset by another source's failure.
                saved = await host.Plugins.ProfileStore.SetEnabledAsync("meowchan.official.8kun", false, saved.Revision);
                saved = await host.Plugins.ProfileStore.SetGrantAsync(FilterPlugin, "dimensions", new("kohlchan", "demo"),
                    FilterDataAccess.CandidateMetadata, saved.Revision);
                foreach (string source in DesktopPluginFixtures.Sources)
                    Assert.Equal(AdmissionStatus.Added, (await host.AddAsync(DesktopPluginFixtures.ThreadUrl(source)).WaitAsync(Deadline)).Status);
                await host.SetPausedAsync(false).WaitAsync(Deadline);
                await WaitAsync(() => host.GetThreadWorkSnapshots().Count == 2
                    && host.GetThreadWorkSnapshots().All(work => work.Media.Count == 2
                        && work.Media.All(media => media.Status == MediaWorkStatus.Succeeded)), host);
                await host.SetPausedAsync(true).WaitAsync(Deadline);
                await AssertArchiveAsync(host, fixture.Landscape);
                Assert.Equal(4, fixture.MediaRequests);
                Assert.Equal(0, fixture.Unexpected);

                // Seed an explicit alternate selection through the ordinary profile store using
                // the real shipped artifact/description (not a fake source implementation).
                // Its path is profile-local; only that candidate is removed/corrupted below.
                candidatePath = Path.Combine(host.Plugins.PluginDirectory, "SelectedSource.cs");
                File.Copy(Path.Combine(AppContext.BaseDirectory, "plugins", "sources", "Lainchan.cs"), candidatePath);
                PluginInstallationCandidate candidate = Assert.Single((await host.Plugins.Inventory.ScanAsync(host.Plugins.PluginDirectory)).Candidates);
                selected = candidate.CandidateId;
                PluginReleaseSnapshot release = saved.Plugins.Single(plugin => plugin.PluginId == FailedPlugin).Active!;
                saved = await host.Plugins.ProfileStore.ObserveCandidateAsync(new(selected, FailedPlugin,
                    release.Artifact, release.Description, CandidatePath: candidatePath), saved.Revision);
                saved = await host.Plugins.ProfileStore.SelectCandidateAsync(FailedPlugin, selected, saved.Revision);
            }
            finally { await StopAsync(host); }
        }
        if (missing) File.Delete(candidatePath);
        else await File.WriteAllTextAsync(candidatePath, "this is deliberately invalid C# source;\n");

        // Two ordinary restarts prove startup failure neither resets persisted choices nor
        // silently falls back to the still-delivered good candidate on a later startup.
        for (int restart = 0; restart < 2; restart++)
        {
            using DesktopPluginFixtures fixture = new();
            DesktopSessionHost host = Create(profile.Path, fixture);
            try
            {
                await StartAsync(host, (missing ? "missing" : "invalid") + "-" + restart);
                Assert.True(host.IsPaused);
                Assert.Null(host.StartupError); // An isolated plugin problem is not global recovery failure.
                Assert.Equal(sourceNames, host.Plugins!.Sources.Select(source => source.Name).Order());
                Assert.Empty(fixture.Requests); // Startup/restoration never probes a live service.
                Assert.Empty(host.Plugins.Runtime.GetSnapshots());
                PluginProfileSnapshot current = await host.Plugins.ProfileStore.ReadAsync();
                Assert.Equal(saved.Plugins.Select(plugin => (plugin.PluginId, plugin.Enabled)).OrderBy(item => item.PluginId),
                    current.Plugins.Select(plugin => (plugin.PluginId, plugin.Enabled)).OrderBy(item => item.PluginId));
                Assert.Equal(saved.Grants, current.Grants);
                Assert.Equal(saved.SelectedSourceOwners.OrderBy(pair => pair.Key), current.SelectedSourceOwners.OrderBy(pair => pair.Key));
                Assert.All(current.Plugins.Where(plugin => plugin.PluginId != FailedPlugin), plugin =>
                {
                    Assert.Null(plugin.Problem);
                    Assert.Equal(saved.Plugins.Single(old => old.PluginId == plugin.PluginId).Active!.ExecutionRevision,
                        plugin.Active!.ExecutionRevision);
                });
                PluginActivationSnapshot failed = Assert.Single(current.Plugins, plugin => plugin.Problem != null);
                Assert.Equal(FailedPlugin, failed.PluginId);
                Assert.True(failed.Enabled); // Saved user intent survives; effective availability is separate.
                Assert.Equal(selected, failed.SelectedCandidateId);
                Assert.Null(failed.Active);
                // A saved pending selection is not executable authority; Active must stay null.
                if (failed.Pending != null) Assert.Equal(selected, failed.Pending.CandidateId);
                Assert.Null(failed.Pinned);
                Assert.NotNull(failed.LastGood);
                PluginCandidateSnapshot unavailable = failed.Candidates.Single(candidate => candidate.CandidateId == selected);
                Assert.Equal(!missing, unavailable.IsPresent);
                Assert.False(string.IsNullOrWhiteSpace(unavailable.Problem));
                Assert.Contains(failed.Candidates, candidate => candidate.CandidateId != selected && candidate.IsPresent && candidate.Problem == null);
                Assert.False(host.Plugins.Sources.Single(source => source.Name == "lainchan").Enabled);
                Assert.True(host.Plugins.Sources.Single(source => source.Name == "kohlchan").Enabled);
                Assert.False(host.Plugins.Sources.Single(source => source.Name == "8kun").Enabled);
                Assert.Contains(host.GetView().Warnings, warning => warning.Contains(failed.Problem!, StringComparison.Ordinal));
                // Kohlchan's synthetic response is archived, so its completed watch is
                // intentionally absent from the active list. Its durable work must survive.
                DesktopThreadEntry watch = Assert.Single(host.GetView().Threads);
                Assert.Equal(new ResourceId("lainchan", "demo", "123"), watch.Snapshot.Id);
                Assert.Equal(DesktopPluginFixtures.ThreadUrl("lainchan"), watch.Snapshot.Url);
                Assert.Equal(DesktopPluginFixtures.Sources.Select(DesktopPluginFixtures.ThreadUrl).Order(),
                    host.GetThreadWorkSnapshots().Select(work => work.Url).Order());
                Assert.All(host.GetThreadWorkSnapshots(), work =>
                {
                    Assert.Equal(2, work.Media.Count);
                    Assert.All(work.Media, media => Assert.Equal(MediaWorkStatus.Succeeded, media.Status));
                });
                await AssertArchiveAsync(host, fixture.Landscape);

                // Real healthy-source worker + host HTTP fixture remains usable while the
                // failed source has only its last declaration, never an executable fallback.
                await host.SetPausedAsync(false).WaitAsync(Deadline);
                using CancellationTokenSource previewTimeout = new(Deadline);
                BoardAdmissionPreview preview = await host.PreviewBoardAdmissionAsync("https://kohlchan.net/demo/", previewTimeout.Token).WaitAsync(Deadline);
                Assert.Equal("123", Assert.Single(preview.Threads).Id);
                Assert.Single(fixture.Requests);
                Assert.Equal("https://kohlchan.net/demo/catalog.json", fixture.Requests.Single());
                Assert.All(host.Plugins.Runtime.GetSnapshots(), worker => Assert.Equal(
                    current.Plugins.Single(plugin => plugin.PluginId == HealthyPlugin).Active!.ExecutionRevision, worker.Identity.ExecutionRevision));
                Assert.Single(host.Plugins.Runtime.GetSnapshots());
                Assert.Equal(0, fixture.Unexpected);
                Assert.Equal(0, fixture.MediaRequests);
                await host.SetPausedAsync(true).WaitAsync(Deadline);
            }
            finally { await StopAsync(host); }
            using ProfileLease lease = ProfileLease.Acquire(profile.Path);
            Assert.Equal(2, new WorkStateStore(profile.Path, lease).LoadResources().Count);
        }
    }

    private static DesktopSessionHost Create(string profile, DesktopPluginFixtures fixture) => new(profile,
        updateSessionFactory: (path, config) => new UpdateSession(path, config, fixture.Client(), disableEnvironment: () => "1"),
        pluginTransportFactory: fixture.Client);

    private async Task StartAsync(DesktopSessionHost host, string phase)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        try { await host.StartAsync().WaitAsync(Deadline); }
        finally
        {
            output.WriteLine("MU_STARTUP_ISOLATION " + JsonSerializer.Serialize(new
            {
                phase, elapsedMilliseconds = elapsed.ElapsedMilliseconds, state = host.State.ToString(),
                host.StartupError, warningCount = host.Plugins?.Warnings.Count
            }));
        }
        Assert.True(host.State == DesktopSessionState.Ready, host.StartupError);
    }

    private static async Task AssertArchiveAsync(DesktopSessionHost host, byte[] expected)
    {
        await using ArchiveReader reader = await host.OpenArchiveReaderAsync();
        foreach (string source in DesktopPluginFixtures.Sources)
        {
            ArchivedThreadView view = (await reader.ReadThreadAsync(new(source, "demo", "123")))!;
            Assert.NotNull(view);
            Assert.Equal(new[] { "123", "124" }, view.GetPosts().Select(post => post.PostId));
            Assert.Equal(expected, await File.ReadAllBytesAsync(view.GetPosts()[0].Attachments![0].PlannedPath!));
        }
    }

    private static async Task WaitAsync(Func<bool> condition, DesktopSessionHost host)
    {
        using CancellationTokenSource timeout = new(Deadline);
        try { while (!condition()) await Task.Delay(25, timeout.Token); }
        catch (OperationCanceledException) { Assert.Fail("Isolated fixture work did not settle: " + JsonSerializer.Serialize(host.GetView())); }
    }

    private static async Task StopAsync(DesktopSessionHost host)
    {
        int[] pids = host.Plugins?.Runtime.GetSnapshots().Where(worker => worker.ProcessId.HasValue)
            .Select(worker => worker.ProcessId!.Value).ToArray() ?? [];
        await host.DisposeAsync().AsTask().WaitAsync(Deadline);
        Assert.Equal(DesktopSessionState.Stopped, host.State);
        Assert.Empty(host.Plugins!.Runtime.GetSnapshots());
        Assert.All(pids, pid =>
        {
            try { using Process process = Process.GetProcessById(pid); Assert.True(process.HasExited, "Fixture worker survived shutdown: " + pid); }
            catch (ArgumentException) { /* The owned worker has exited and been reaped. */ }
        });
    }
}

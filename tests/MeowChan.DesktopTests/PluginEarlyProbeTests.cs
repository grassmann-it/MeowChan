using System.Collections.Concurrent;
using MeowChan.Desktop;
using MeowChan.Persistence;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace MeowChan.DesktopTests;

// The read-only plugin probe starts with the host (AP-1). It must never write the plugin profile, must keep
// activation after the catalog phase, and must end with every describe worker reaped on any early exit.
[Collection("Delivered plugin hosts")]
public sealed class PluginEarlyProbeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(90);

    [Fact]
    public void ConstructorFailureAfterTheProbeBeganReapsItsWorkersBeforeReleasingTheProfile()
    {
        using TempDirectory profile = new();
        Configure(profile.Path);
        bool sawWorker = false;
        IOException failure = Assert.Throws<IOException>(() => new DesktopSessionHost(profile.Path,
            sessionStoreFactory: (directory, _) =>
            {
                // Runs after BeginProbe: fail only once a describe worker is really running.
                sawWorker = SpinWait.SpinUntil(() => WorkerWorkspaces(directory).Length != 0, Deadline);
                throw new IOException("controlled session store failure");
            },
            pluginTransportFactory: () => new HttpClient(new Offline())));
        Assert.Equal("controlled session store failure", failure.Message);
        Assert.True(sawWorker, "The probe did not reach a describe worker before the constructor failed.");
        AssertProbeLeftNothingRunning(profile.Path);
        Assert.False(File.Exists(StatePath(profile.Path)), "The read-only probe wrote the plugin profile.");
        using ProfileLease reacquired = ProfileLease.Acquire(profile.Path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposingTheHostDuringTheProbeCancelsItAndReapsItsWorkers(bool started)
    {
        using TempDirectory profile = new();
        Configure(profile.Path);
        DesktopSessionHost host = new(profile.Path, pluginTransportFactory: () => new HttpClient(new Offline()));
        Task? startup = null;
        try
        {
            if (started)
            {
                // Startup holds before the plugin phase, so only the early probe is running.
                host.AfterCatalogReadableForTests = ct => Task.Delay(Timeout.InfiniteTimeSpan, ct);
                startup = host.StartAsync();
            }
            Assert.True(SpinWait.SpinUntil(() => WorkerWorkspaces(profile.Path).Length != 0, Deadline),
                "The probe did not reach a describe worker.");
            Task probe = host.Plugins!.StartupProbeForTests!;
            Assert.False(probe.IsCompleted);
        }
        finally { await host.DisposeAsync().AsTask().WaitAsync(Deadline); }
        Assert.True(host.Plugins!.StartupProbeForTests!.IsCompleted);
        if (startup != null) await startup.WaitAsync(Deadline);
        Assert.NotEqual(DesktopSessionState.Ready, host.State);
        AssertProbeLeftNothingRunning(profile.Path);
        Assert.False(File.Exists(StatePath(profile.Path)), "A cancelled probe wrote the plugin profile.");
        using ProfileLease reacquired = ProfileLease.Acquire(profile.Path);
    }

    [Fact]
    public async Task TheProbeWritesNoProfileAndActivationStillFollowsTheCatalogPhase()
    {
        using TempDirectory profile = new();
        Configure(profile.Path);
        Capture sink = new();
        using Logger logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using DesktopSessionHost host = new(profile.Path, logger: logger, pluginTransportFactory: () => new HttpClient(new Offline()));
        host.AfterCatalogReadableForTests = ct => release.Task.WaitAsync(ct);
        Task startup = host.StartAsync();
        // The complete probe (import and describe of every delivered candidate) runs while startup holds.
        await host.Plugins!.StartupProbeForTests!.WaitAsync(Deadline);
        Assert.Equal(DesktopSessionState.Starting, host.State);
        Assert.False(File.Exists(StatePath(profile.Path)), "The read-only probe wrote the plugin profile.");
        Assert.NotEmpty(Directory.GetDirectories(Path.Combine(profile.Path, "plugin-cache", "artifacts")));
        Assert.Empty(host.Plugins.Sources);
        Assert.DoesNotContain(Phases(sink), phase => phase is "activation" or "adapters-routing" or "total");
        Assert.Empty(WorkerWorkspaces(profile.Path)); // describe workers end inside the probe
        release.SetResult();
        await startup.WaitAsync(Deadline);
        Assert.True(host.State == DesktopSessionState.Ready, host.StartupError);
        Assert.True(File.Exists(StatePath(profile.Path)));
        Assert.NotEmpty(host.Plugins.Sources);
        // Unchanged order after the probe: provenance and activation, then adapters, once each.
        string[] phases = Phases(sink).Where(phase => phase is "probe" or "probe-wait" or "activation" or "adapters-routing" or "total").ToArray();
        Assert.Equal(new[] { "probe", "probe-wait", "activation", "adapters-routing", "total" }, phases);
    }

    private static void AssertProbeLeftNothingRunning(string profile)
    {
        Assert.Empty(WorkerWorkspaces(profile));
        Assert.Empty(ProcessesInside(profile));
    }

    private static string StatePath(string profile) => Path.Combine(profile, "plugin-state", "state.json");

    private static string[] WorkerWorkspaces(string profile)
    {
        string root = Path.Combine(profile, "plugin-workers");
        return Directory.Exists(root) ? Directory.GetDirectories(root, "worker-*") : [];
    }

    // Live processes whose working directory lies inside the profile (Linux only; elsewhere the workspace check stands).
    private static int[] ProcessesInside(string profile)
    {
        if (!OperatingSystem.IsLinux()) return [];
        string prefix = Path.GetFullPath(profile).TrimEnd('/') + "/";
        List<int> found = new();
        foreach (string entry in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(entry), out int id)) continue;
            try
            {
                if (new FileInfo(Path.Combine(entry, "cwd")).LinkTarget is { } cwd && (cwd + "/").StartsWith(prefix, StringComparison.Ordinal))
                    found.Add(id);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return found.ToArray();
    }

    private static string[] Phases(Capture sink) => sink.Events
        .Where(item => item.Properties.ContainsKey("PluginStartupCompleted") && item.Properties.TryGetValue("PluginStartupCandidate", out var candidate)
            && candidate is ScalarValue { Value: null })
        .Select(item => (string)((ScalarValue)item.Properties["PluginStartupPhase"]).Value!).ToArray();

    private static void Configure(string profile) => new Config
    {
        DownloadPath = Path.Combine(profile, "media"), DownloadsPaused = true, SaveUrlsOnExit = true,
        AutomaticUpdateChecks = false, RecheckTimer = 3600, MinimumFreeSpaceGiB = 0
    }.SaveConfig(profile);

    private sealed class Capture : ILogEventSink
    {
        internal ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }

    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("offline test transport"));
    }
}

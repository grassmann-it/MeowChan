using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using MeowChan.Http;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Http;
using MeowChan.PluginHost.Runtime;
using MeowChan.PluginRuntime.Fixture;
using MeowChan.PluginSdk;
using SdkThreadSnapshot = MeowChan.PluginSdk.ThreadSnapshot;

namespace MeowChan.PluginRuntime.Tests;

public sealed class RuntimeProcessTests
{
    private static string Root
    {
        get
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "MeowChan.sln"))) directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("Repository fixture root missing.");
        }
    }
    private static string Dotnet => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "/usr/share/dotnet/dotnet";
    private static PluginConfiguration Configuration => new("config1", new("1", []), new Dictionary<string, SettingsSnapshot> { ["fixture"] = new("1", []) });
    private static PluginOperationContext Operation => new("fixture/b/1", 7);
    private static ThreadReadRequest Thread(string id = "1") => new(new("fixture", "b", id));
    private static PluginArtifact Artifact(IReadOnlyList<PluginArtifactResource>? resources = null, string? root = null)
    {
        string path = typeof(FixturePlugin).Assembly.Location;
        return new("compile1", "build1", path, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), "sdk1", "compiler1", resources ?? [], root);
    }
    private static PluginWorkerLaunch Launch(string workspace, string? fault = null)
    {
        string worker = Path.Combine(Root, "MeowChan.PluginWorker/bin/Release/net10.0/MeowChan.PluginWorker.dll");
        string fixture = Path.Combine(Root, "tests/MeowChan.PluginRuntime.Fixture/bin/Release/net10.0/MeowChan.PluginRuntime.Fixture.dll");
        return new(Dotnet, fault == null ? [worker] : [fixture, fault], workspace);
    }
    [Fact]
    public async Task RealWorkerDescribesReadsSourceAndUsesDuplexHostServices()
    {
        using Workspace workspace = new();
        await using PluginWorkerManager manager = new();
        Services services = new();
        await using IPluginWorkerClient worker = await manager.StartAsync(Launch(workspace.Path), Artifact(), Configuration, services);
        Assert.Equal("fixture", worker.Description.PluginId);
        SdkThreadSnapshot snapshot = Assert.IsType<SdkThreadSnapshot>(await worker.ReadThreadAsync(Thread("http"), Operation));
        Assert.Equal("host response", snapshot.Snapshot.Posts.Single().Text);
        Assert.Equal(Operation, services.Observed);
        CatalogSnapshot catalog = Assert.IsType<CatalogSnapshot>(await worker.ReadCatalogAsync(new(new("fixture", "b")), Operation));
        Assert.Single(catalog.Snapshot.Threads);
        Assert.Empty(await worker.ValidateSettingsAsync(new(SettingScope.Plugin, new("1", []))));
    }
    [Fact]
    public async Task DescribeRequiresNoConfiguredSourceAndNoHostServices()
    {
        using Workspace workspace = new(); await using PluginWorkerManager manager = new();
        PluginDescription description = await manager.DescribeAsync(Launch(workspace.Path), Artifact());
        Assert.Equal("fixture", description.PluginId); Assert.Empty(Directory.GetDirectories(workspace.Path));
    }
    [Fact]
    public async Task FilterReadsExactBytesWithMatchingRevisionsAndRejectsRevokedGrant()
    {
        using Workspace workspace = new(); await using PluginWorkerManager manager = new(); Services services = new();
        await using IPluginWorkerClient worker = await manager.StartAsync(Launch(workspace.Path), Artifact(), Configuration, services);
        ImageFilterRequest request = new("bytes", new(new(new("fixture", "b", "1"), "media"), "1", 0, new("https://fixture.test/a.png"), MimeType: "image/png"),
            "input1", "media1", "asset1", 4, Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3, 4 })), "rule1", new("1", []), "build1", "config1", "grant1");
        PluginOperationContext operation = new("fixture/b/1", 7, "rule1", "grant1");
        Assert.Equal(ImageFilterOutcome.Match, (await worker.EvaluateAsync(request, operation)).Outcome);
        PluginWorkerException exception = await Assert.ThrowsAsync<PluginWorkerException>(() => worker.EvaluateAsync(request, operation with { GrantRevision = "grant2" }));
        Assert.Equal("revision", exception.Code);
        Assert.Equal(1, services.ImageReads);
    }
    [Theory]
    [InlineData("oversize")][InlineData("json")][InlineData("closed")][InlineData("version")]
    public async Task InvalidChildProtocolFailsAndCleansOwnedWorkspace(string mode)
    {
        using Workspace workspace = new(); await using PluginWorkerManager manager = new();
        await Assert.ThrowsAnyAsync<Exception>(() => manager.StartAsync(Launch(workspace.Path, mode), Artifact(), Configuration, new Services()));
        Assert.Empty(Directory.GetDirectories(workspace.Path));
    }
    [Fact]
    public async Task GenerationMismatchCannotBecomeThreadEnd()
    {
        using Workspace workspace = new(); await using PluginWorkerManager manager = new();
        await using IPluginWorkerClient worker = await manager.StartAsync(Launch(workspace.Path, "generation"), Artifact(), Configuration, new Services());
        PluginWorkerException exception = await Assert.ThrowsAsync<PluginWorkerException>(() => worker.ReadThreadAsync(Thread(), Operation));
        Assert.Equal("protocol", exception.Code);
    }
    [Fact]
    public async Task CancelCooperativeCallRejectsResultAndKeepsWorker()
    {
        using Workspace workspace = new(); await using PluginWorkerManager manager = new();
        await using IPluginWorkerClient worker = await manager.StartAsync(Launch(workspace.Path), Artifact(), Configuration, new Services());
        string instance = worker.Identity.InstanceId;
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.ReadThreadAsync(Thread("cancel"), Operation, cancellation.Token));
        Assert.IsType<SdkThreadSnapshot>(await worker.ReadThreadAsync(Thread(), Operation with { Generation = 8 }));
        Assert.Equal(instance, worker.Identity.InstanceId);
    }
    [Fact]
    public async Task UncooperativeTimeoutKillsProcessAndExplicitRetryUsesNewInstance()
    {
        using Workspace workspace = new();
        await using PluginWorkerManager manager = new(new() { OperationTimeout = TimeSpan.FromMilliseconds(200), CancellationGrace = TimeSpan.FromMilliseconds(100) });
        await using IPluginWorkerClient worker = await manager.StartAsync(Launch(workspace.Path), Artifact(), Configuration, new Services());
        int pid = worker.Snapshot.ProcessId!.Value; string instance = worker.Identity.InstanceId;
        PluginWorkerException exception = await Assert.ThrowsAsync<PluginWorkerException>(() => worker.ReadThreadAsync(Thread("hang"), Operation));
        Assert.Equal("timeout", exception.Code);
        await WaitExited(pid);
        await worker.RetryAsync();
        Assert.NotEqual(instance, worker.Identity.InstanceId);
        Assert.IsType<SdkThreadSnapshot>(await worker.ReadThreadAsync(Thread(), Operation));
    }
    [Fact]
    public async Task WorkerCrashAndWrongSourceDataCannotBecomeNotFound()
    {
        using Workspace workspace = new(); await using PluginWorkerManager manager = new();
        await using IPluginWorkerClient worker = await manager.StartAsync(Launch(workspace.Path), Artifact(), Configuration, new Services());
        PluginWorkerException invalid = await Assert.ThrowsAsync<PluginWorkerException>(() => worker.ReadThreadAsync(Thread("wrong"), Operation));
        Assert.Equal("validation", invalid.Code);
        await Assert.ThrowsAsync<PluginWorkerException>(() => worker.ReadThreadAsync(Thread("crash"), Operation));
        Assert.IsType<SdkThreadSnapshot>(await worker.ReadThreadAsync(Thread(), Operation));
    }
    [Fact]
    public async Task StdoutIsPrivateAndLogsStayWithinConfiguredRing()
    {
        using Workspace workspace = new(); await using PluginWorkerManager manager = new(new() { MaximumLogBytes = 4096 });
        await using IPluginWorkerClient worker = await manager.StartAsync(Launch(workspace.Path), Artifact(), Configuration, new Services());
        Assert.IsType<SdkThreadSnapshot>(await worker.ReadThreadAsync(Thread("stdout"), Operation));
        Assert.IsType<SdkThreadSnapshot>(await worker.ReadThreadAsync(Thread("logs"), Operation));
        Assert.InRange(worker.Snapshot.Logs.Sum(line => System.Text.Encoding.UTF8.GetByteCount(line)), 1, 4096);
    }
    [Fact]
    public async Task ResourcesAreHashCheckedAndConfinedToBundle()
    {
        using Workspace workspace = new(); string data = Path.Combine(workspace.Path, "data.txt"); await File.WriteAllTextAsync(data, "verified resource");
        PluginArtifactResource resource = new("fixture-data", "data.txt", Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(data))));
        await using PluginWorkerManager manager = new();
        await using IPluginWorkerClient worker = await manager.StartAsync(Launch(workspace.Path), Artifact([resource], workspace.Path), Configuration, new Services());
        Assert.Equal("verified resource", Assert.IsType<SdkThreadSnapshot>(await worker.ReadThreadAsync(Thread("resource"), Operation)).Snapshot.Posts.Single().Text);
        await Assert.ThrowsAsync<PluginWorkerException>(() => manager.StartAsync(Launch(workspace.Path), Artifact([resource with { RelativePath = "../escape" }], workspace.Path), Configuration, new Services()));
        await File.WriteAllTextAsync(data, "changed");
        await Assert.ThrowsAsync<PluginWorkerException>(() => manager.StartAsync(Launch(workspace.Path), Artifact([resource], workspace.Path), Configuration, new Services()));
    }
    [Fact]
    public async Task IdleWorkerEvictsForAnotherSourceAndProcessCountStaysBounded()
    {
        using Workspace workspace = new(); await using PluginWorkerManager manager = new(new() { MaximumProductionWorkers = 1 });
        await using IPluginWorkerClient first = await manager.StartAsync(Launch(workspace.Path), Artifact(), Configuration, new Services());
        int firstPid = first.Snapshot.ProcessId!.Value;
        await using IPluginWorkerClient second = await manager.StartAsync(Launch(workspace.Path), Artifact(), Configuration, new Services());
        await WaitExited(firstPid);
        Assert.Equal("idle", first.Snapshot.State);
        Assert.IsType<SdkThreadSnapshot>(await second.ReadThreadAsync(Thread(), Operation));
        Assert.IsType<SdkThreadSnapshot>(await first.ReadThreadAsync(Thread(), Operation));
        Assert.Single(manager.GetSnapshots(), snapshot => snapshot.ProcessId != null);
    }
    [Fact]
    public async Task TemporaryWorkspaceLimitKillsWorkerAndShutdownRemovesItsFiles()
    {
        using Workspace workspace = new(); await using PluginWorkerManager manager = new(new() { MaximumTemporaryBytes = 1024 * 1024 });
        IPluginWorkerClient worker = await manager.StartAsync(Launch(workspace.Path), Artifact(), Configuration, new Services());
        PluginWorkerException exception = await Assert.ThrowsAsync<PluginWorkerException>(() => worker.ReadThreadAsync(Thread("temp"), Operation));
        Assert.Equal("temporary-limit", exception.Code);
        Assert.Equal(0, manager.GetTemporaryBytes());
        Assert.Empty(Directory.GetDirectories(workspace.Path));
        await worker.DisposeAsync(); Assert.Empty(Directory.GetDirectories(workspace.Path));
    }
    [Fact]
    public async Task CooldownReturnsDeferredWithoutHoldingOtherWorkerAdmission()
    {
        using Workspace workspace = new();
        await using PluginWorkerManager manager = new(new() { MaximumConcurrentCalls = 1 });
        using HttpClient transport = new(new StaticHandler());
        using PluginHostHttp http = new(transport, new RequestPolicyState(), new FixturePlugin().Describe().Sources);
        await using IPluginWorkerClient worker = await manager.StartAsync(Launch(workspace.Path), Artifact(), Configuration, new Services(http));
        Stopwatch watch = Stopwatch.StartNew();
        ThreadDeferred deferred = Assert.IsType<ThreadDeferred>(await worker.ReadThreadAsync(Thread("http"), Operation));
        Assert.True(deferred.RetryAt > DateTimeOffset.UtcNow.AddSeconds(20)); Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        await using IPluginWorkerClient other = await manager.StartAsync(Launch(workspace.Path), Artifact(), Configuration, new Services());
        Assert.IsType<SdkThreadSnapshot>(await other.ReadThreadAsync(Thread(), Operation));
    }
    [Fact]
    public async Task SerialRequestsCanFollowReplyBeforeWorkerContinuationCompletes()
    {
        using Workspace workspace = new(); await using PluginWorkerManager manager = new();
        await using IPluginWorkerClient worker = await manager.StartAsync(Launch(workspace.Path), Artifact(), Configuration, new Services());
        for (int iteration = 0; iteration < 100; iteration++)
            Assert.IsType<SdkThreadSnapshot>(await worker.ReadThreadAsync(Thread(), Operation));
    }
    [Fact]
    public async Task OversizedResultIsExplicitFailureAndDoesNotTruncateOrEndThread()
    {
        using Workspace workspace = new(); await using PluginWorkerManager manager = new();
        await using IPluginWorkerClient worker = await manager.StartAsync(Launch(workspace.Path), Artifact(), Configuration, new Services());
        PluginWorkerException failure = await Assert.ThrowsAsync<PluginWorkerException>(() => worker.ReadThreadAsync(Thread("large"), Operation));
        Assert.Equal("frame-too-large", failure.Code);
    }
    [Fact]
    public async Task ManagedHeapFailureHasEvidenceAndLeavesOtherWorkersUsable()
    {
        using Workspace workspace = new(); await using PluginWorkerManager manager = new();
        await using IPluginWorkerClient worker = await manager.StartAsync(Launch(workspace.Path), Artifact(), Configuration, new Services());
        PluginWorkerException failure = await Assert.ThrowsAsync<PluginWorkerException>(() => worker.ReadThreadAsync(Thread("memory"), Operation));
        Assert.Equal("memory-limit", failure.Code);
        await using IPluginWorkerClient other = await manager.StartAsync(Launch(workspace.Path), Artifact(), Configuration, new Services());
        Assert.IsType<SdkThreadSnapshot>(await other.ReadThreadAsync(Thread(), Operation));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OwnChildTreeIsCleanedEvenWhenWorkerParentCrashes(bool crash)
    {
        using Workspace workspace = new(); await using PluginWorkerManager manager = new();
        IPluginWorkerClient worker = await manager.StartAsync(Launch(workspace.Path), Artifact(), Configuration, new Services());
        int child = int.Parse(Assert.IsType<SdkThreadSnapshot>(await worker.ReadThreadAsync(Thread("spawn"), Operation)).Snapshot.Posts.Single().Text!);
        if (crash) await Assert.ThrowsAsync<PluginWorkerException>(() => worker.ReadThreadAsync(Thread("crash"), Operation));
        await worker.DisposeAsync();
        Assert.Empty(Directory.GetDirectories(workspace.Path));
        await WaitExited(child);
    }
    [Fact]
    public async Task RestartBudgetRequiresExplicitRetryAfterThreeAutomaticRestarts()
    {
        using Workspace workspace = new(); await using PluginWorkerManager manager = new();
        await using IPluginWorkerClient worker = await manager.StartAsync(Launch(workspace.Path), Artifact(), Configuration, new Services());
        for (int crash = 0; crash < 4; crash++)
            await Assert.ThrowsAsync<PluginWorkerException>(() => worker.ReadThreadAsync(Thread("crash"), Operation));
        PluginWorkerException blocked = await Assert.ThrowsAsync<PluginWorkerException>(() => worker.ReadThreadAsync(Thread(), Operation));
        Assert.Equal("restart-limit", blocked.Code);
        Assert.Equal("intervention-required", worker.Snapshot.State);
        await worker.RetryAsync();
        Assert.IsType<SdkThreadSnapshot>(await worker.ReadThreadAsync(Thread(), Operation));
    }
    [Fact]
    public async Task CancellationWhileWaitingForWorkerCapacityRemovesTheQueuedStart()
    {
        using Workspace workspace = new(); await using PluginWorkerManager manager = new(new() { MaximumProductionWorkers = 1 });
        await using IPluginWorkerClient first = await manager.StartAsync(Launch(workspace.Path), Artifact(), Configuration, new Services());
        using CancellationTokenSource runningCancellation = new();
        Task<ThreadReadResult> running = first.ReadThreadAsync(Thread("cancel"), Operation, runningCancellation.Token);
        using CancellationTokenSource queuedCancellation = new(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.StartAsync(Launch(workspace.Path), Artifact(), Configuration, new Services(), cancellationToken: queuedCancellation.Token));
        Assert.Single(manager.GetSnapshots());
        runningCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.IsType<SdkThreadSnapshot>(await first.ReadThreadAsync(Thread(), Operation));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FinishingOrCrashingCallCancelsItsOutstandingHostServices(bool crash)
    {
        using Workspace workspace = new(); await using PluginWorkerManager manager = new();
        CancellableHttp http = new();
        await using IPluginWorkerClient worker = await manager.StartAsync(Launch(workspace.Path), Artifact(), Configuration, new Services(http));
        Task<ThreadReadResult> call = worker.ReadThreadAsync(Thread(crash ? "background-crash" : "background"), Operation);
        await http.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (crash) await Assert.ThrowsAsync<PluginWorkerException>(() => call);
        else Assert.IsType<SdkThreadSnapshot>(await call);
        await http.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }
    [Fact]
    public async Task SharedTemporaryBudgetIsEnforcedEvenWithAnEmptyWorkerWorkspace()
    {
        using Workspace workspace = new();
        long externalTemporaryBytes = 0;
        await using PluginWorkerManager manager = new(new() { MaximumTemporaryBytes = 1024, ExternalTemporaryBytes = () => Interlocked.Read(ref externalTemporaryBytes) });
        await using IPluginWorkerClient worker = await manager.StartAsync(Launch(workspace.Path), Artifact(), Configuration, new Services());
        Assert.Equal(0, manager.GetTemporaryBytes());
        // Introduce the shared overflow only after startup; the monitor also runs during the handshake.
        Interlocked.Exchange(ref externalTemporaryBytes, 1025);
        PluginWorkerException failure = await Assert.ThrowsAsync<PluginWorkerException>(() => worker.ReadThreadAsync(Thread("cancel"), Operation));
        Assert.Equal("temporary-limit", failure.Code);
    }
    [Fact]
    public async Task RepeatedBackgroundServicesDoNotLeakWorkerServiceSlots()
    {
        using Workspace workspace = new(); await using PluginWorkerManager manager = new();
        CancellableHttp http = new();
        await using IPluginWorkerClient worker = await manager.StartAsync(Launch(workspace.Path), Artifact(), Configuration, new Services(http));
        for (int attempt = 0; attempt < 12; attempt++)
            Assert.IsType<SdkThreadSnapshot>(await worker.ReadThreadAsync(Thread("background"), Operation));
        Assert.Equal(12, http.RequestCount);
    }
    [Fact]
    public async Task ConcurrentDescribeAndTestStartUseConsistentAdmissionOrder()
    {
        using Workspace workspace = new(); await using PluginWorkerManager manager = new(new() { MaximumConcurrentStarts = 1 });
        PluginWorkerLaunch delayed = Launch(workspace.Path, "delayed");
        delayed = delayed with { Arguments = delayed.Arguments.Concat([workspace.Path]).ToArray() };
        Task<IPluginWorkerClient> first = manager.StartAsync(delayed, Artifact(), Configuration, new Services());
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!File.Exists(Path.Combine(workspace.Path, "handshake.wait"))) await Task.Delay(10, timeout.Token);
        Task<PluginDescription> describe = manager.DescribeAsync(Launch(workspace.Path), Artifact(), timeout.Token);
        Task<IPluginWorkerClient> test = manager.StartAsync(Launch(workspace.Path), Artifact(), Configuration, new Services(), testWorker: true, cancellationToken: timeout.Token);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "handshake.release"), "release", timeout.Token);
        await Task.WhenAll(first, describe, test).WaitAsync(timeout.Token);
        await (await first).DisposeAsync(); await (await test).DisposeAsync();
    }
    [Fact]
    public async Task CapacityEvictionOfCrashedWorkerPreservesRestartDelay()
    {
        using Workspace workspace = new(); await using PluginWorkerManager manager = new(new() { MaximumProductionWorkers = 1 });
        await using IPluginWorkerClient first = await manager.StartAsync(Launch(workspace.Path), Artifact(), Configuration, new Services());
        await Assert.ThrowsAsync<PluginWorkerException>(() => first.ReadThreadAsync(Thread("crash"), Operation));
        await using IPluginWorkerClient second = await manager.StartAsync(Launch(workspace.Path), Artifact(), Configuration, new Services());
        Stopwatch elapsed = Stopwatch.StartNew();
        Assert.IsType<SdkThreadSnapshot>(await first.ReadThreadAsync(Thread(), Operation));
        Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(1));
    }
    [Fact]
    public async Task MalformedSettingsDiagnosticsAreTypedValidationErrors()
    {
        using Workspace workspace = new(); await using PluginWorkerManager manager = new();
        await using IPluginWorkerClient worker = await manager.StartAsync(Launch(workspace.Path), Artifact(), Configuration, new Services());
        PluginWorkerException error = await Assert.ThrowsAsync<PluginWorkerException>(() => worker.ValidateSettingsAsync(new(SettingScope.Plugin, new("1", []), SourceId: "malformed")));
        Assert.Equal("validation", error.Code);
    }
    [Fact]
    public async Task ConcurrentDisposalWaitsForTheSameOwnedCleanup()
    {
        using Workspace workspace = new();
        await using PluginWorkerManager manager = new();
        DrainingHttp http = new();
        IPluginWorkerClient worker = await manager.StartAsync(Launch(workspace.Path), Artifact(), Configuration, new Services(http));
        Task<ThreadReadResult> call = worker.ReadThreadAsync(Thread("http"), Operation);
        await http.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task first = worker.DisposeAsync().AsTask();
        await http.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Task second = worker.DisposeAsync().AsTask();
            Assert.Same(first, second);
            Assert.False(second.IsCompleted);
            Task managerFirst = manager.DisposeAsync().AsTask();
            Task managerSecond = manager.DisposeAsync().AsTask();
            Assert.Same(managerFirst, managerSecond);
            Assert.False(managerSecond.IsCompleted);
            http.Release.TrySetResult();
            await Task.WhenAll(first, second, managerFirst, managerSecond).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(manager.GetSnapshots());
            Assert.Empty(Directory.GetDirectories(workspace.Path));
        }
        finally { http.Release.TrySetResult(); await worker.DisposeAsync(); }
        await Assert.ThrowsAnyAsync<Exception>(() => call);
    }

    [Fact]
    public async Task QueuedPluginStartsAsSoonAsBusyWorkerBecomesIdle()
    {
        using Workspace workspace = new(); await using PluginWorkerManager manager = new(new() { MaximumProductionWorkers = 1 });
        CancellableHttp http = new();
        await using IPluginWorkerClient first = await manager.StartAsync(Launch(workspace.Path), Artifact(), Configuration, new Services(http));
        using CancellationTokenSource cancellation = new();
        Task<ThreadReadResult> call = first.ReadThreadAsync(Thread("http"), Operation, cancellation.Token);
        await http.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<IPluginWorkerClient> queued = manager.StartAsync(Launch(workspace.Path), Artifact(), Configuration, new Services());
        Assert.False(queued.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        await using IPluginWorkerClient second = await queued.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsType<SdkThreadSnapshot>(await second.ReadThreadAsync(Thread(), Operation));
        Assert.Equal("idle", first.Snapshot.State);
    }
    private sealed class CancellableHttp : IHostHttp
    {
        public int RequestCount;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<HostHttpResult> SendAsync(HostHttpRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref RequestCount);
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); throw new InvalidOperationException(); }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
        }
    }
    private sealed class DrainingHttp : IHostHttp
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<HostHttpResult> SendAsync(HostHttpRequest request, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); throw new InvalidOperationException(); }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); await Release.Task; throw; }
        }
    }
    private static async Task WaitExited(int pid)
    {
        try { using Process process = Process.GetProcessById(pid); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (ArgumentException) { }
    }
    private sealed class Workspace : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "meowchan-runtime-tests-" + Guid.NewGuid().ToString("N"));
        public Workspace() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
    private sealed class Services(IHostHttp? http = null) : IPluginHostServices
    {
        public PluginOperationContext? Observed { get; private set; }
        public int ImageReads { get; private set; }
        public Task<HostHttpResult> SendHttpAsync(HostHttpRequest request, PluginOperationContext operation, CancellationToken cancellationToken)
        {
            Observed = operation;
            return http?.SendAsync(request, cancellationToken) ?? Task.FromResult<HostHttpResult>(new HostHttpResponse(200, request.Url, new Dictionary<string, string>(), "host response"u8.ToArray()));
        }
        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, PluginOperationContext operation, CancellationToken cancellationToken)
        { ImageReads++; return Task.FromResult(new ImageAssetChunk(offset, [1, 2, 3, 4], true)); }
    }
    private sealed class StaticHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = new(HttpStatusCode.TooManyRequests) { RequestMessage = request };
            response.Headers.RetryAfter = new(System.TimeSpan.FromSeconds(30)); return Task.FromResult(response);
        }
    }
}

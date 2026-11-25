using System.Security.Cryptography;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Runtime;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopTests;

public sealed class WorkerConnectionTests
{
    [Fact]
    public async Task HostServiceExceptionReturnsOnlyTheFixedWorkerFailure()
    {
        const string marker = "fixture-detail-739";
        string app = AppContext.BaseDirectory;
        string suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        string workerExecutable = Path.Combine(app, "plugin-worker", "MeowChan.PluginWorker" + suffix);
        string assembly = Path.Combine(app, "plugins", "assemblies", "MeowChan.Plugin.Danbooru.dll");
        string hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(assembly)));
        PluginArtifact artifact = new("compile1", "build1", assembly, hash, "sdk1", "compiler1", []);
        SettingsSnapshot settings = new("1", []);
        PluginConfiguration configuration = new("config1", settings, new Dictionary<string, SettingsSnapshot> { ["danbooru"] = settings });

        using TempDirectory workspace = new();
        await using PluginWorkerManager manager = new();
        await using IPluginWorkerClient worker = await manager.StartAsync(new(workerExecutable, [], workspace.Path),
            artifact, configuration, new ThrowingServices(marker));

        PluginWorkerException failure = await Assert.ThrowsAsync<PluginWorkerException>(() =>
            worker.SearchBooruPostsAsync(new("danbooru", "cat_ears", 1, 1), new("fixture", 1)));

        Assert.Equal("host-service", failure.Code);
        Assert.Equal("The host rejected the service request.", failure.Message);
        Assert.DoesNotContain(marker, failure.Message, StringComparison.Ordinal);
        Assert.Contains(worker.Snapshot.Logs, line => line.Contains("System.InvalidOperationException: " + marker, StringComparison.Ordinal));
    }

    private sealed class ThrowingServices(string marker) : IPluginHostServices
    {
        public Task<HostHttpResult> SendHttpAsync(HostHttpRequest request, PluginOperationContext operation, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(marker);

        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, PluginOperationContext operation,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Persistence;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Runtime;
using MeowChan.PluginHost.Taggers;
using MeowChan.PluginRuntime.Fixture;
using MeowChan.PluginSdk;
using MeowChan.Tags;
using MeowChan.TestSupport;

namespace MeowChan.PluginRuntime.Tests;

public sealed class LocalTaggerTests
{
    [Fact]
    public async Task RejectedReplacementLeavesVerifiedModelUsableAfterProfileRestartAndThroughRealWorker()
    {
        using Workspace workspace = new();
        string profile = Path.Combine(workspace.Path, "profile");
        string original = WritePackage(workspace.Path, "original", [204]);
        string replacement = WritePackage(workspace.Path, "bad-hash", [203]);
        string approvedHash = Hash(File.ReadAllBytes(original));
        using (ProfileLease lease = ProfileLease.Acquire(profile))
        {
            LocalTaggerModelStore models = new(lease);
            Assert.Null(await models.VerifyInstalledAsync(CancellationToken.None));
            LocalTaggerModelPreview preview = await models.PreviewAsync(original, CancellationToken.None);
            Assert.Equal(new FileInfo(original).Length + 1, preview.Package.RequiredBytes);
            await models.ImportAsync(preview, CancellationToken.None);

            LocalTaggerModelPreview invalid = await models.PreviewAsync(replacement, CancellationToken.None);
            LocalTaggerModelException failure = await Assert.ThrowsAsync<LocalTaggerModelException>(
                () => models.ImportAsync(invalid, CancellationToken.None));
            Assert.Equal(LocalTaggerModelIssue.Integrity, failure.Issue);
        }

        using ProfileLease restarted = ProfileLease.Acquire(profile);
        LocalTaggerModelStore reloaded = new(restarted);
        LocalTaggerModelPackage? installed = await reloaded.VerifyInstalledAsync(CancellationToken.None);
        Assert.Equal(approvedHash, installed?.ManifestSha256);
        Assert.Single(Directory.EnumerateDirectories(Path.GetDirectoryName(installed!.DirectoryPath)!));

        TagSuggestionStore suggestions = new(restarted);
        await using PluginWorkerManager manager = new();
        LocalTaggerWorker worker = CreateWorker(workspace.Path, reloaded, suggestions, manager, Approve(approvedHash));
        TaggerJobItem item = TestImage(255);
        TaggerJobResult unavailable = await RunAsync(worker, new string('0', 64), item, CancellationToken.None);
        Assert.Equal(TaggerJobStatus.ModelUnavailable, unavailable.Status);
        Assert.Empty(suggestions.Load().Runs);

        TaggerJobResult result = await RunAsync(worker, approvedHash, item, CancellationToken.None);
        Assert.Equal(TaggerJobStatus.Completed, result.Status);
        Assert.Equal(1, result.Tagged);
        Assert.Equal(1, result.Suggestions);
        StoredTagSuggestion suggestion = Assert.Single(suggestions.Load().PendingFor(item.MediaSha256, 0));
        Assert.Equal("test_tag", suggestion.Name);
        Assert.Equal(0.8, suggestion.Confidence);
        Assert.Equal(FixturePlugin.ModelId, suggestion.Provenance.ModelId);
        Assert.Equal(FixturePlugin.Revision, suggestion.Provenance.ModelRevision);
        Assert.Equal(FixturePlugin.ModelSha256, suggestion.Provenance.ModelSha256);
        Assert.Equal("fixture", suggestion.Provenance.PluginId);

        TaggerJobResult resumed = await RunAsync(worker, approvedHash, item, CancellationToken.None);
        Assert.Equal(1, resumed.Skipped);
        Assert.Single(suggestions.Load().Runs);

        string path = Path.Combine(installed.DirectoryPath, LocalTaggerModelManifest.WeightsFileName);
        File.WriteAllBytes(path, [203]);
        await Assert.ThrowsAsync<LocalTaggerModelException>(() => reloaded.VerifyInstalledAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AnApprovedManifestAloneDoesNotAuthorizeAnAdapterOrItsLicense()
    {
        using Workspace workspace = new();
        using ProfileLease lease = ProfileLease.Acquire(Path.Combine(workspace.Path, "profile"));
        LocalTaggerModelStore models = new(lease);
        string path = WritePackage(workspace.Path, "candidate", [204]);
        LocalTaggerModelPreview preview = await models.PreviewAsync(path, CancellationToken.None);
        await models.ImportAsync(preview, CancellationToken.None);
        string manifestHash = preview.Package.ManifestSha256;
        TagSuggestionStore suggestions = new(lease);
        await using PluginWorkerManager manager = new();
        LocalTaggerExecutionApproval approval = Approve(manifestHash);
        foreach (LocalTaggerExecutionApproval mismatch in new[]
        {
            approval with { AdapterSha256 = new string('0', 64) },
            approval with { ModelLicense = "wrong-license" },
            approval with { LicenseEvidenceSha256 = string.Empty }
        })
        {
            LocalTaggerWorker worker = CreateWorker(workspace.Path, models, suggestions, manager, mismatch);
            TaggerJobResult result = await RunAsync(worker, manifestHash, TestImage(255), CancellationToken.None);
            Assert.Equal(TaggerJobStatus.ModelUnavailable, result.Status);
            Assert.Empty(manager.GetSnapshots());
            Assert.False(Directory.Exists(Path.Combine(workspace.Path, "workers")));
        }
        Assert.Empty(suggestions.Load().Runs);
    }

    [Fact]
    public async Task PreviewPinsManifestAndChecksSizeAndVersion()
    {
        using Workspace workspace = new();
        using ProfileLease lease = ProfileLease.Acquire(Path.Combine(workspace.Path, "profile"));
        LocalTaggerModelStore models = new(lease);
        string path = WritePackage(workspace.Path, "candidate", [204]);
        LocalTaggerModelPreview preview = await models.PreviewAsync(path, CancellationToken.None);
        File.AppendAllText(path, "\n");
        LocalTaggerModelException substituted = await Assert.ThrowsAsync<LocalTaggerModelException>(
            () => models.ImportAsync(preview, CancellationToken.None));
        Assert.Equal(LocalTaggerModelIssue.Integrity, substituted.Issue);
        Assert.Null(await models.VerifyInstalledAsync(CancellationToken.None));

        WritePackage(workspace.Path, "candidate", [204], sizeBytes: models.Settings.MaximumWeightsBytes + 1);
        LocalTaggerModelException oversized = await Assert.ThrowsAsync<LocalTaggerModelException>(
            () => models.PreviewAsync(path, CancellationToken.None));
        Assert.Equal(LocalTaggerModelIssue.Limit, oversized.Issue);

        WritePackage(workspace.Path, "candidate", [204], version: 2);
        await Assert.ThrowsAsync<InvalidDataException>(() => models.PreviewAsync(path, CancellationToken.None));

        WritePackage(workspace.Path, "candidate", [204], sizeBytes: 2);
        LocalTaggerModelPreview wrongSize = await models.PreviewAsync(path, CancellationToken.None);
        LocalTaggerModelException mismatch = await Assert.ThrowsAsync<LocalTaggerModelException>(
            () => models.ImportAsync(wrongSize, CancellationToken.None));
        Assert.Equal(LocalTaggerModelIssue.Integrity, mismatch.Issue);
        Assert.Null(await models.VerifyInstalledAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CancellingNonCooperativeWorkerLeavesNoResultsAndNextWorkerCanRun()
    {
        using Workspace workspace = new();
        using ProfileLease lease = ProfileLease.Acquire(Path.Combine(workspace.Path, "profile"));
        LocalTaggerModelStore models = new(lease);
        string path = WritePackage(workspace.Path, "candidate", [204]);
        LocalTaggerModelPreview preview = await models.PreviewAsync(path, CancellationToken.None);
        await models.ImportAsync(preview, CancellationToken.None);
        TagSuggestionStore suggestions = new(lease);
        await using PluginWorkerManager manager = new(new PluginWorkerOptions { CancellationGrace = TimeSpan.FromMilliseconds(100) });
        LocalTaggerWorker worker = CreateWorker(workspace.Path, models, suggestions, manager, Approve(preview.Package.ManifestSha256));
        using CancellationTokenSource cancel = new(TimeSpan.FromSeconds(20));
        Task<TaggerJobResult> pending = RunAsync(worker, preview.Package.ManifestSha256, TestImage(255, width: 2), cancel.Token);
        using Process process = Process.GetProcessById(await WaitForPidAsync(Path.Combine(workspace.Path, "workers"), cancel.Token));
        await cancel.CancelAsync();
        TaggerJobResult cancelled = await pending;
        Assert.Equal(TaggerJobStatus.Cancelled, cancelled.Status);
        Assert.Empty(suggestions.Load().Runs);
        Assert.True(process.HasExited);
        Assert.Empty(manager.GetSnapshots());

        TaggerJobResult next = await RunAsync(worker, preview.Package.ManifestSha256, TestImage(255), CancellationToken.None);
        Assert.Equal(1, next.Tagged);
    }

    private static async Task<int> WaitForPidAsync(string workspace, CancellationToken token)
    {
        using PeriodicTimer poll = new(TimeSpan.FromMilliseconds(20));
        while (await poll.WaitForNextTickAsync(token))
        {
            if (!Directory.Exists(workspace)) continue;
            string? marker = Directory.EnumerateDirectories(workspace)
                .Select(directory => Path.Combine(directory, "fixture-tagger-hang")).FirstOrDefault(File.Exists);
            if (marker != null && int.TryParse(await File.ReadAllTextAsync(marker, token), out int pid)) return pid;
        }
        throw new InvalidOperationException("Fixture never reached the worker.");
    }

    private static LocalTaggerWorker CreateWorker(string root, LocalTaggerModelStore models, TagSuggestionStore suggestions,
        PluginWorkerManager manager, LocalTaggerExecutionApproval approval)
        => new(models, suggestions, manager, new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "/usr/share/dotnet/dotnet",
            [BuiltTestBinaries.WorkerAssembly()], Path.Combine(root, "workers")), approval);

    private static LocalTaggerExecutionApproval Approve(string manifestHash) =>
        new(manifestHash, FixtureAdapter().AssemblySha256, FixturePlugin.License, "MIT", Hash([204, 1]));

    private static PluginArtifact FixtureAdapter()
    {
        string assembly = typeof(FixturePlugin).Assembly.Location;
        return new("fixture-compile", "fixture-execution", assembly, Hash(File.ReadAllBytes(assembly)), "sdk", "compiler", []);
    }

    private static Task<TaggerJobResult> RunAsync(LocalTaggerWorker worker, string manifestSha256, TaggerJobItem item, CancellationToken token)
    {
        PluginConfiguration configuration = new("tagger-config", new("1", []),
            new Dictionary<string, SettingsSnapshot> { ["fixture"] = new("1", []), ["danbooru"] = new("1", []) });
        return worker.RunAsync(FixtureAdapter(), configuration, FixturePlugin.TaggerKey, manifestSha256, [item], new("1", []), cancellationToken: token);
    }

    private static TaggerJobItem TestImage(byte image, int width = 1)
    {
        byte[] bytes = [image];
        return new(Hash(bytes), bytes.Length, TaggerMediaKind.Image, "image/png",
            (offset, count, _) => Task.FromResult(bytes.AsSpan((int)offset, count).ToArray()), PixelWidth: width, PixelHeight: 1);
    }

    private static string WritePackage(string root, string name, byte[] weights, long sizeBytes = 1, int version = 1)
    {
        string directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        LocalTaggerModelManifest manifest = new(LocalTaggerModelManifest.FormatId, version, "MeowChan test fixture",
            FixturePlugin.ModelId, FixturePlugin.Revision, sizeBytes, FixturePlugin.ModelSha256, FixturePlugin.License,
            [new("test_tag", TaggerTagCategory.General)],
            new("input", "scores", "NCHW", 1, 1, "RGB", "stretch", "nearest", null, 1.0 / 255, [0, 0, 0], [1, 1, 1], "identity"));
        string path = Path.Combine(directory, LocalTaggerModelManifest.FileName);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(manifest, LocalTaggerModelManifest.JsonOptions));
        File.WriteAllBytes(Path.Combine(directory, LocalTaggerModelManifest.WeightsFileName), weights);
        return path;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class Workspace : IDisposable
    {
        internal string Path { get; } = Directory.CreateTempSubdirectory("meowchan-b6-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

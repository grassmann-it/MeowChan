using System.Net;
using System.Security.Cryptography;
using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Runtime;
using MeowChan.PluginHost.Sources;
using MeowChan.PluginSdk;
using MeowChan.Storage;
using MeowChan.SourceAdapter.Fixture;

namespace MeowChan.SourceAdapter.Tests;
public sealed class VariantArchiveTests
{
    [Fact]
    public async Task RealWorkerCollidingPairsKeepRawIdentityBytesAndRestartState()
    {
        using SourceAdapterTests.TemporaryProfile profile = new();
        string assembly = typeof(VariantSource).Assembly.Location;
        PluginArtifact artifact = new("variant", "variant", assembly, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly))), "sdk", "compiler", Array.Empty<PluginArtifactResource>());
        DirectoryInfo root = new(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "MeowChan.sln"))) root = root.Parent!;
        PluginWorkerLaunch launch = new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "/usr/share/dotnet/dotnet",
            new[] { Path.Combine(root.FullName, "MeowChan.PluginWorker/bin/Release/net10.0/MeowChan.PluginWorker.dll") }, Path.Combine(profile.Path, "workers"));
        PluginDescription description = new VariantSource().Describe();
        PluginConfiguration configuration = new("one", new("1", Array.Empty<SettingValue>()),
            new Dictionary<string, SettingsSnapshot> { ["variants"] = new("1", Array.Empty<SettingValue>()) });
        string mediaRoot = Path.Combine(profile.Path, "media");
        await using (PluginWorkerManager manager = new())
        {
            await using IPluginWorkerClient worker = await manager.StartAsync(launch, artifact, configuration, new NoServices());
            using HttpClient media = new(new Images());
            using PluginSourceImageBoard source = new(description, "variants", _ => Task.FromResult(worker), media);
            using ProfileLease lease = ProfileLease.Acquire(profile.Path);
            await using ThreadContentRuntime content = new(profile.Path, lease);
            await content.InitializeAsync();
            DownloadQueue queue = new(2, 1048576);
            await using Registry registry = new(new[] { source }, queue, mediaRoot, TimeSpan.FromHours(1), 3600,
                new SessionStore(profile.Path, lease), true, contentRuntime: content, workStore: new WorkStateStore(profile.Path, lease));
            await registry.RestoreAsync();
            AdmissionResult admitted = await registry.AddAsync("https://variants.test/b/thread/1");
            await SourceAdapterTests.WaitUntilAsync(() => admitted.Thread!.DownloadedMediaCount == 2);
            Assert.Equal(2, admitted.Thread!.TotalMediaCount);
        }
        for (int run = 0; run < 2; run++)
        {
            using ProfileLease lease = ProfileLease.Acquire(profile.Path);
            await using ThreadContentRuntime content = new(profile.Path, lease);
            await content.InitializeAsync();
            using PluginSourceImageBoard source = new(description, "variants", _ => throw new Exception("No worker on terminal restore"));
            DownloadQueue queue = new(2, 1048576);
            WorkStateStore work = new(profile.Path, lease);
            await using Registry registry = new(new[] { source }, queue, mediaRoot, TimeSpan.FromHours(1), 3600,
                new SessionStore(profile.Path, lease), true, contentRuntime: content, workStore: work);
            await registry.RestoreAsync();
            WorkResource thread = work.LoadResources().Single();
            IReadOnlyList<MediaWork> saved = work.LoadMedia(thread.Url, thread.Generation);
            Assert.Equal(2, saved.Count);
            Assert.All(saved, item => Assert.Equal(MediaWorkStatus.Succeeded, item.Status));
            Assert.Contains(saved, item => item.MediaIdentifier == "x--preview" && item.Variant == "original");
            Assert.Contains(saved, item => item.MediaIdentifier == "x" && item.Variant == "preview");
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        ArchivedThreadView view = (await reader.ReadThreadAsync(new ResourceId("variants", "b", "1")))!;
        CapturedAttachment[] captured = view.GetPosts().Single().Attachments!.ToArray();
        Assert.NotEqual(captured[0].PlannedPath, captured[1].PlannedPath);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(captured[0].PlannedPath!));
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, await File.ReadAllBytesAsync(captured[1].PlannedPath!));
        MediaAssetSnapshot assets = await MediaAssetReader.OpenAsync(view.Summary.MediaDirectory);
        Assert.Equal(2, assets.Count);
        Assert.Contains(assets.GetPage(), asset => asset.MediaIdentifier == "x--preview" && asset.Variant == "original");
        Assert.Contains(assets.GetPage(), asset => asset.MediaIdentifier == "x" && asset.Variant == "preview");
    }
    private sealed class NoServices : IPluginHostServices
    {
        public Task<HostHttpResult> SendHttpAsync(HostHttpRequest request, PluginOperationContext operation, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, PluginOperationContext operation, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class Images : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.Contains("original", StringComparison.Ordinal) ? new byte[] { 1, 2, 3, 4 } : new byte[] { 5, 6, 7, 8 }), RequestMessage = request });
    }
}

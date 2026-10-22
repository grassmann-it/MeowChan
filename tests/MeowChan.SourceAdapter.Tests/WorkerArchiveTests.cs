using System.Net;
using System.Security.Cryptography;
using System.Text;
using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Runtime;
using MeowChan.PluginHost.Sources;
using MeowChan.PluginSdk;
using MeowChan.OfficialSources.FourChan;

namespace MeowChan.SourceAdapter.Tests;

public sealed class WorkerArchiveTests
{
    [Fact]
    public async Task RealOfficialWorkerFeedsNormalQueueThenRestartAndProviderFreeReader()
    {
        using SourceAdapterTests.TemporaryProfile profile = new();
        byte[] image = Encoding.UTF8.GetBytes("fixture image original bytes");
        string mediaRoot = Path.Combine(profile.Path, "media");
        string expected = Path.Combine(mediaRoot, "4chan", "g", "123", "456.jpg");
        string sourceAssembly = typeof(FourChanPlugin).Assembly.Location;
        PluginArtifact artifact = new("official-fixture", "official-fixture", sourceAssembly,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourceAssembly))), "sdk-fixture", "compiler-fixture", Array.Empty<PluginArtifactResource>());
        PluginConfiguration configuration = new("config1", new("1", Array.Empty<SettingValue>()),
            new Dictionary<string, SettingsSnapshot> { ["4chan"] = new("1", Array.Empty<SettingValue>()) });
        string workerPath = Path.Combine(RepositoryRoot(), "MeowChan.PluginWorker", "bin", "Release", "net10.0", "MeowChan.PluginWorker.dll");
        PluginWorkerLaunch launch = new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "/usr/share/dotnet/dotnet", new[] { workerPath }, Path.Combine(profile.Path, "workers"));
        FixtureServices services = new(image);
        await using (PluginWorkerManager manager = new())
        {
            await using IPluginWorkerClient worker = await manager.StartAsync(launch, artifact, configuration, services);
            Assert.NotNull(worker.Snapshot.ProcessId);
            using HttpClient media = new(new MediaHandler(image));
            using PluginSourceImageBoard source = new(worker.Description, "4chan", _ => Task.FromResult(worker), media);
            using ProfileLease lease = ProfileLease.Acquire(profile.Path);
            await using ThreadContentRuntime content = new(profile.Path, lease);
            await content.InitializeAsync();
            DownloadQueue queue = new(1, 1048576);
            await using Registry registry = new(new[] { source }, queue, mediaRoot, TimeSpan.FromMinutes(10), 600,
                new SessionStore(profile.Path, lease), true, contentRuntime: content, workStore: new WorkStateStore(profile.Path, lease));
            await registry.RestoreAsync();
            AdmissionResult admitted = await registry.AddAsync("https://boards.4channel.org/g/thread/123/title#p124");
            Assert.Equal(AdmissionStatus.Added, admitted.Status);
            await SourceAdapterTests.WaitUntilAsync(() => admitted.Thread!.DownloadedMediaCount == 1);
            Assert.Equal(image, await File.ReadAllBytesAsync(expected));
            Assert.Equal(ThreadSourceState.Archived, admitted.Thread!.SourceState);
            await using ArchiveReader reader = await content.OpenReaderAsync();
            ArchivedThreadView view = (await reader.ReadThreadAsync(new ResourceId("4chan", "g", "123")))!;
            Assert.Equal(new[] { "123", "124" }, view.GetPosts().Select(post => post.PostId));
            Assert.Equal("456", view.GetPosts()[0].Attachments![0].MediaId);
            Assert.Contains("fixture", view.GetPosts()[0].Html);
            Assert.Equal(1, services.ThreadReads);
            Assert.True(services.LastOperation!.Generation > 0);
        }
        int callsAtStop = services.ThreadReads;
        // Opening the same persisted terminal profile twice cannot poll an unavailable source or reset the end time.
        DateTimeOffset? firstEnd = null;
        for (int restart = 0; restart < 2; restart++)
        {
            using ProfileLease lease = ProfileLease.Acquire(profile.Path);
            await using ThreadContentRuntime content = new(profile.Path, lease);
            await content.InitializeAsync();
            using PluginSourceImageBoard offline = new(new FourChanPlugin().Describe(), "4chan", _ => throw new InvalidOperationException("Terminal restore must not acquire a worker."));
            DownloadQueue queue = new(1, 1048576);
            WorkStateStore work = new(profile.Path, lease);
            await using Registry registry = new(new[] { offline }, queue, mediaRoot, TimeSpan.FromMinutes(10), 600,
                new SessionStore(profile.Path, lease), true, contentRuntime: content, workStore: work);
            await registry.RestoreAsync();
            WorkResource state = work.LoadResources().Single(resource => resource.IsThread);
            firstEnd ??= state.EndObservedAt;
            Assert.Equal(firstEnd, state.EndObservedAt);
            Assert.Equal(1, state.Generation);
            Assert.Equal(image, await File.ReadAllBytesAsync(expected));
        }
        Assert.Equal(callsAtStop, services.ThreadReads);
        await using (ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path))
        {
            ArchivedThreadView view = (await reader.ReadThreadAsync(new ResourceId("4chan", "g", "123")))!;
            Assert.Equal(2, view.PostCount);
            Assert.Equal(expected, view.GetPosts()[0].Attachments![0].PlannedPath);
        }
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "MeowChan.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Fixture repository missing.");
    }

    private sealed class FixtureServices(byte[] media) : IPluginHostServices
    {
        public int ThreadReads;
        public PluginOperationContext? LastOperation;
        public Task<HostHttpResult> SendHttpAsync(HostHttpRequest request, PluginOperationContext operation, CancellationToken cancellationToken)
        {
            Assert.Equal("https://a.4cdn.org/g/thread/123.json", request.Url.AbsoluteUri);
            Interlocked.Increment(ref ThreadReads); LastOperation = operation;
            string json = "{\"posts\":[{\"no\":123,\"sub\":\"fixture\",\"com\":\"fixture &lt;safe&gt;\",\"archived\":1,\"tim\":456,\"ext\":\".jpg\",\"filename\":\"original\",\"fsize\":" + media.Length +
                ",\"md5\":\"" + Convert.ToBase64String(MD5.HashData(media)) + "\"},{\"no\":124,\"com\":\"reply\"}]}";
            return Task.FromResult<HostHttpResult>(new HostHttpResponse(200, request.Url, new Dictionary<string, string>(), Encoding.UTF8.GetBytes(json)));
        }
        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, PluginOperationContext operation, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class MediaHandler(byte[] image) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://i.4cdn.org/g/456.jpg", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(image), RequestMessage = request });
        }
    }
}

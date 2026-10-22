using System.Security.Cryptography;
using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.Persistence;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Sources;
using MeowChan.PluginSdk;
using MeowChan.OfficialSources.EightKun;

namespace MeowChan.SourceAdapter.Tests;
public sealed class LegacySourceMigrationTests
{
    [Fact]
    public async Task EightKunAdoptsLegacyMediaIdFilenameWithoutNetwork()
    {
        using SourceAdapterTests.TemporaryProfile profile = new();
        byte[] bytes = new byte[] { 1, 3, 3, 7 };
        string root = Path.Combine(profile.Path, "media");
        Directory.CreateDirectory(Path.Combine(root, "g", "123"));
        await File.WriteAllBytesAsync(Path.Combine(root, "g", "123", "456.jpg"), bytes);
        PluginDescription description = new EightKunPlugin().Describe();
        SourceAdapterTests.TestWorker worker = new((request, _, _) => Task.FromResult<ThreadReadResult>(new PluginSdk.ThreadSnapshot(
            new SourceThreadSnapshot(request.Thread, new Uri("https://8kun.top/g/res/123.html"), "legacy", SourceThreadState.Archived,
                new[] { new SourcePost("123", 0, SourceFieldPresence.Attachments, Attachments:
                    new[] { new SourceAttachment(0, "456", Url: new Uri("https://nerv.8kun.top/file_store/456.jpg"),
                        OriginalFileName: "user-original.jpg", Extension: ".jpg", ExpectedSize: bytes.Length, ExpectedMd5: Convert.ToBase64String(MD5.HashData(bytes))) }) }))), description);
        using HttpClient media = new(new NoNetwork());
        using PluginSourceImageBoard source = new(description, "8kun", _ => Task.FromResult<IPluginWorkerClient>(worker), media);
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ThreadContentRuntime content = new(profile.Path, lease);
        await content.InitializeAsync();
        DownloadQueue queue = new(1, 1048576);
        await using Registry registry = new(new[] { source }, queue, root, TimeSpan.FromHours(1), 3600,
            contentRuntime: content, workStore: new WorkStateStore(profile.Path, lease));
        AdmissionResult result = await registry.AddAsync("https://8kun.top/g/res/123.html");
        await SourceAdapterTests.WaitUntilAsync(() => result.Thread!.DownloadedMediaCount == 1);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(root, "8kun", "g", "123", "456.jpg")));
    }
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new Exception("Verified 0.9 legacy media must be adopted without downloading again.");
    }
}

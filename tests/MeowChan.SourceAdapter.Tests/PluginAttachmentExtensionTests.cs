using System.Net;
using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.OfficialSources.FourChan;
using MeowChan.Persistence;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Sources;
using MeowChan.PluginSdk;

namespace MeowChan.SourceAdapter.Tests;

public sealed class PluginAttachmentExtensionTests
{
    // Extension is optional in the SDK; the host falls back to the URL path, which may have no
    // extension or a percent-escaped one. Only that attachment is rejected, never the snapshot.
    [Theory]
    [InlineData("https://i.4cdn.org/g/789")]
    [InlineData("https://i.4cdn.org/g/789.j%20g")]
    public async Task AttachmentWithoutUsableExtensionIsSkippedAndMarkedWithoutFailingTheSnapshot(string badUrl)
    {
        using SourceAdapterTests.TemporaryProfile profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ThreadContentRuntime content = new(profile.Path, lease);
        await content.InitializeAsync();
        PluginDescription description = new FourChanPlugin().Describe();
        SourceAdapterTests.TestWorker worker = new((request, _, _) => Task.FromResult<ThreadReadResult>(
            new PluginSdk.ThreadSnapshot(new SourceThreadSnapshot(request.Thread,
                new Uri("https://boards.4chan.org/g/thread/123"), "Extension fixture", SourceThreadState.Active,
                [new SourcePost("123", 0, SourceFieldPresence.Text | SourceFieldPresence.Attachments, Text: "body", TextFormat: SourceTextFormat.PlainText,
                    Attachments: [new SourceAttachment(0, "456", Url: new Uri("https://i.4cdn.org/g/456.png"), Extension: ".png"),
                        new SourceAttachment(1, "789", Url: new Uri(badUrl))])]))), description);
        using HttpClient media = new(new Images());
        using PluginSourceImageBoard source = new(description, "4chan", _ => Task.FromResult<IPluginWorkerClient>(worker), media);
        DownloadQueue queue = new(1, 1048576);
        await using Registry registry = new(new[] { source }, queue, Path.Combine(profile.Path, "media"),
            TimeSpan.FromHours(1), 3600, contentRuntime: content, workStore: new WorkStateStore(profile.Path, lease));
        await registry.StartAsync(lease);
        AdmissionResult admission = await registry.AddAsync("https://boards.4chan.org/g/thread/123");
        await SourceAdapterTests.WaitUntilAsync(() => admission.Thread!.DownloadedMediaCount == 1 || admission.Thread.LastCheckFailure != null);

        Assert.Null(admission.Thread!.LastCheckFailure);
        Assert.Equal(1, admission.Thread.DownloadedMediaCount);
        KeyValuePair<string, string> failure = Assert.Single(admission.Thread.MediaFailures);
        Assert.Equal("789", failure.Key);
        Assert.StartsWith("Rejected attachment metadata: ", failure.Value);
        Assert.Equal(FailureKind.InvalidData, admission.Thread.GetSnapshot(default).MediaFailures["789"].Kind);
        await using ArchiveReader reader = await content.OpenReaderAsync();
        ArchivedThreadView view = (await reader.ReadThreadAsync(new ResourceId("4chan", "g", "123")))!;
        CapturedAttachment[] attachments = Assert.Single(view.GetPosts()).Attachments!.ToArray();
        Assert.Equal(2, attachments.Length);
        Assert.NotNull(attachments[0].PlannedPath);
        Assert.Null(attachments[1].PlannedPath);
        Assert.Equal(badUrl, attachments[1].Url);
    }

    private sealed class Images : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]), RequestMessage = request });
    }
}

using System.Net;
using System.Security.Cryptography;
using System.Text;
using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Sources;
using MeowChan.PluginSdk;
using MeowChan.OfficialSources.FourChan;
using MeowChan.Storage;

namespace MeowChan.SourceAdapter.Tests;

public sealed class OpaqueIdentityTests
{
    [Fact]
    public async Task OpaqueIdentitiesLongExtensionsAndOrdinalsSurviveMediaCommitAndTwoRestores()
    {
        using SourceAdapterTests.TemporaryProfile profile = new();
        const string sourceId = "opaque.雪", boardId = "NUL";
        string threadId = new('t', 200);
        ResourceId id = new(sourceId, boardId, threadId);
        PluginDescription description = Description(sourceId);
        PluginSourceRouter router = new(description.Sources[0]);
        Uri canonical = router.Canonical(SourceResourceKind.Thread, boardId, threadId);
        string extension = "." + new string('e', 32);
        string[] mediaIds = { "../雪", "NUL", "~opaque-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("../雪"))).ToLowerInvariant(), "safe" };
        string[] variants = { "original", "poster/雪", "original", "original" };
        SourceAttachment[] attachments = mediaIds.Select((mediaId, index) => new SourceAttachment(index, mediaId, variants[index],
            new Uri("https://opaque.test/media/" + index), Extension: extension, ExpectedSize: 1)).ToArray();
        SourceAdapterTests.TestWorker worker = new((request, _, _) => Task.FromResult<ThreadReadResult>(new PluginSdk.ThreadSnapshot(
            new SourceThreadSnapshot(request.Thread, canonical, "opaque fixture", SourceThreadState.Archived,
                new[] { new SourcePost("middle", (long)int.MaxValue + 1, SourceFieldPresence.Attachments, Attachments: attachments),
                    new SourcePost("last", long.MaxValue, SourceFieldPresence.Text, Text: "last"),
                    new SourcePost("first", int.MaxValue, SourceFieldPresence.Text, Text: "first") }))), description);
        string mediaRoot = Path.Combine(profile.Path, "media");
        string? mediaDirectory = null;
        using (ProfileLease lease = ProfileLease.Acquire(profile.Path))
        {
            await using ThreadContentRuntime content = new(profile.Path, lease);
            await content.InitializeAsync();
            using HttpClient media = new(new Images());
            using PluginSourceImageBoard source = new(description, sourceId, _ => Task.FromResult<IPluginWorkerClient>(worker), media);
            DownloadQueue queue = new(2, 1048576);
            await using Registry registry = new(new[] { source }, queue, mediaRoot, TimeSpan.FromHours(1), 3600,
                new SessionStore(profile.Path, lease), true, contentRuntime: content, workStore: new WorkStateStore(profile.Path, lease));
            await registry.RestoreAsync();
            AdmissionResult admitted = await registry.AddAsync(canonical.AbsoluteUri);
            Assert.Equal(AdmissionStatus.Added, admitted.Status);
            await SourceAdapterTests.WaitUntilAsync(() => admitted.Thread!.DownloadedMediaCount == attachments.Length);
            mediaDirectory = admitted.Thread!.DownloadDirectory;
            Assert.Equal(boardId, admitted.Thread.Board.BoardCode);
            Assert.Equal(threadId, admitted.Thread.ThreadId);
            Assert.All(Path.GetRelativePath(mediaRoot, mediaDirectory).Split(Path.DirectorySeparatorChar), segment => Assert.StartsWith("~opaque-", segment));
        }
        for (int run = 0; run < 2; run++)
        {
            using ProfileLease lease = ProfileLease.Acquire(profile.Path);
            await using ThreadContentRuntime content = new(profile.Path, lease);
            await content.InitializeAsync();
            using PluginSourceImageBoard source = new(description, sourceId, _ => throw new Exception("Terminal restore cannot acquire a worker"));
            DownloadQueue queue = new(2, 1048576);
            WorkStateStore work = new(profile.Path, lease);
            await using Registry registry = new(new[] { source }, queue, mediaRoot, TimeSpan.FromHours(1), 3600,
                new SessionStore(profile.Path, lease), true, contentRuntime: content, workStore: work);
            await registry.RestoreAsync();
            WorkResource resource = Assert.Single(work.LoadResources());
            Assert.Equal(canonical.AbsoluteUri, resource.Url);
            IReadOnlyList<MediaWork> saved = work.LoadMedia(resource.Url, resource.Generation);
            Assert.Equal(attachments.Length, saved.Count);
            foreach (SourceAttachment attachment in attachments)
                Assert.Contains(saved, item => item.MediaIdentifier == attachment.MediaId && item.Variant == attachment.Variant
                    && item.Status == MediaWorkStatus.Succeeded);
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        ArchivedThreadView view = (await reader.ReadThreadAsync(id))!;
        Assert.Equal(id, view.Summary.Id);
        Assert.Equal(mediaDirectory, view.Summary.MediaDirectory);
        Assert.Equal(new[] { "first", "middle", "last" }, view.GetPosts().Select(post => post.PostId));
        Assert.Equal(new[] { (long)int.MaxValue, (long)int.MaxValue + 1, long.MaxValue }, view.GetPosts().Select(post => post.Ordinal));
        CapturedAttachment[] captured = view.GetPosts()[1].Attachments!.ToArray();
        Assert.Equal(attachments.Length, captured.Select(item => item.PlannedPath).Distinct(StringComparer.Ordinal).Count());
        for (int index = 0; index < attachments.Length; index++)
        {
            Assert.Equal(mediaIds[index], captured[index].MediaId);
            Assert.Equal(variants[index], captured[index].Variant);
            Assert.Equal(mediaDirectory, Path.GetDirectoryName(captured[index].PlannedPath));
            Assert.Equal(extension, Path.GetExtension(captured[index].PlannedPath));
            Assert.Equal(new[] { (byte)index }, await File.ReadAllBytesAsync(captured[index].PlannedPath!));
        }
        MediaAssetSnapshot assets = await MediaAssetReader.OpenAsync(mediaDirectory!);
        foreach (SourceAttachment attachment in attachments)
            Assert.Contains(assets.GetPage(), asset => asset.MediaIdentifier == attachment.MediaId && asset.Variant == attachment.Variant);
        Assert.Equal(attachments.Length, assets.Count);
        // Plugin support never changes the legacy public path validation contract.
        Assert.Throws<ArgumentException>(() => StorageLayout.GetThreadDirectory(mediaRoot, sourceId, boardId, threadId));
        Assert.Throws<ArgumentException>(() => StorageLayout.GetMediaPath(mediaRoot, "safe", "b", "1", "safe", extension));
    }

    [Fact]
    public async Task OpaqueBoardAdmissionAndRegistrySnapshotsUseTheSamePhysicalDirectory()
    {
        using SourceAdapterTests.TemporaryProfile profile = new();
        PluginDescription description = Description("safe-source");
        SourceAdapterTests.TestWorker worker = new((_, _, _) => throw new NotSupportedException(), description,
            (request, _, _) => Task.FromResult<CatalogReadResult>(new CatalogSnapshot(new SourceCatalogSnapshot(request.Board,
                Array.Empty<SourceThreadReference>(), true))));
        using PluginSourceImageBoard source = new(description, "safe-source", _ => Task.FromResult<IPluginWorkerClient>(worker));
        DownloadQueue queue = new(1, 1048576);
        await using Registry registry = new(new[] { source }, queue, profile.Path, TimeSpan.FromHours(1), 3600);
        const string boardId = "../board/雪";
        AdmissionResult admitted = await registry.AddAsync(source.Router.Canonical(SourceResourceKind.Board, boardId).AbsoluteUri);
        Assert.Equal(AdmissionStatus.Added, admitted.Status);
        IBoard board = admitted.Board!;
        Assert.Equal(boardId, board.BoardCode);
        string directory = registry.GetDownloadDirectory(board);
        Assert.StartsWith("~opaque-", Path.GetFileName(directory));
        Assert.Equal(Path.Combine(profile.Path, "safe-source"), Path.GetDirectoryName(directory));
        Assert.Equal(directory, registry.GetView().Boards[board].DownloadDirectory);
    }

    [Theory]
    [InlineData("4chan")]
    [InlineData("8kun")]
    [InlineData("lainchan")]
    [InlineData("kohlchan")]
    public void ExistingSafeSourceDirectoriesStayUnchanged(string sourceId)
    {
        using PluginSourceImageBoard source = new(Description(sourceId), sourceId, _ => throw new Exception("Routing is passive"));
        IThread thread = source.GetThread(new Uri("https://opaque.test/b/thread/123"));
        Assert.Equal(StorageLayout.GetThreadDirectory("Downloads", sourceId, "b", "123"), thread.DownloadDirectory);
    }

    [Fact]
    public async Task OpaqueBoardCannotAdoptAnUnrelatedLegacyFileOutsideTheDownloadRoot()
    {
        using SourceAdapterTests.TemporaryProfile profile = new();
        PluginDescription description = Description("4chan");
        Uri canonical = new PluginSourceRouter(description.Sources[0]).Canonical(SourceResourceKind.Thread, "../escape", "123");
        string unrelated = Path.Combine(profile.Path, "escape", "123", "legacy.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(unrelated)!);
        await File.WriteAllBytesAsync(unrelated, new byte[] { 99 });
        SourceAdapterTests.TestWorker worker = new((request, _, _) => Task.FromResult<ThreadReadResult>(new PluginSdk.ThreadSnapshot(
            new SourceThreadSnapshot(request.Thread, canonical, "opaque legacy", SourceThreadState.Archived,
                new[] { new SourcePost("1", 0, SourceFieldPresence.Attachments, Attachments: new[]
                { new SourceAttachment(0, "image", "original", new Uri("https://opaque.test/media/0"), "legacy.jpg", ".jpg", 1) }) }))), description);
        using HttpClient media = new(new Images());
        using PluginSourceImageBoard source = new(description, "4chan", _ => Task.FromResult<IPluginWorkerClient>(worker), media);
        DownloadQueue queue = new(1, 1048576);
        await using Registry registry = new(new[] { source }, queue, Path.Combine(profile.Path, "media"), TimeSpan.FromHours(1), 3600);
        AdmissionResult admitted = await registry.AddAsync(canonical.AbsoluteUri);
        await SourceAdapterTests.WaitUntilAsync(() => admitted.Thread!.DownloadedMediaCount == 1);
        Assert.Equal(new byte[] { 0 }, await File.ReadAllBytesAsync(Path.Combine(admitted.Thread!.DownloadDirectory, "image.jpg")));
        Assert.Equal(new byte[] { 99 }, await File.ReadAllBytesAsync(unrelated));
    }

    private static PluginDescription Description(string sourceId)
    {
        PluginDescription original = new FourChanPlugin().Describe();
        return original with { Sources = new[] { original.Sources[0] with
        {
            SourceId = sourceId, BaseUri = new Uri("https://opaque.test/"), PostOrder = PostOrderKind.StableOrdinal,
            Capabilities = SourceCapabilities.ThreadRead | SourceCapabilities.BoardCatalog | SourceCapabilities.PostCapture | SourceCapabilities.OriginalMedia | SourceCapabilities.Previews,
            Routes = new[] { new UrlRoute(SourceResourceKind.Board, "opaque.test", "/{board}/", true),
                new UrlRoute(SourceResourceKind.Thread, "opaque.test", "/{board}/thread/{thread}", true) },
            HttpHosts = new[] { new HttpHostDeclaration("opaque.test", HttpHostPurpose.Metadata | HttpHostPurpose.OriginalMedia | HttpHostPurpose.PreviewMedia) }
        } } };
    }

    private sealed class Images : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(new[] { byte.Parse(request.RequestUri!.Segments.Last()) }), RequestMessage = request });
    }
}

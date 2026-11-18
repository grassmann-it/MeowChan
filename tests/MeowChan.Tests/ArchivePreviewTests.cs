using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.Tests;

public sealed class ArchivePreviewTests
{
    private static readonly ResourceId Id = new("custom", "b", "42");

    [Fact]
    public async Task ReadsFirstCapturedPostAndAttachmentWithoutOpeningMedia()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        string absentMediaPath = Path.Combine(directory.Path, "absent", "image.jpg");
        await catalog.CaptureAsync(new CapturedThreadSnapshot(Id, "https://example.test/b/thread/42", "title",
            ThreadSourceState.Archived, DateTimeOffset.UtcNow, directory.Path,
            [
                new CapturedPost("later", 2, PostFieldPresence.Html, Html: "second"),
                new CapturedPost("first", 1, PostFieldPresence.Html | PostFieldPresence.Attachments,
                    Html: "<b>First &amp; text</b> " + new string('x', 600),
                    Attachments: [new CapturedAttachment(0, "image", PlannedPath: absentMediaPath)])
            ]));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        IReadOnlyDictionary<ResourceId, ArchiveThreadPreview> previews = await reader.ReadThreadPreviewsAsync([Id, Id]);

        ArchiveThreadPreview preview = Assert.Single(previews).Value;
        Assert.Equal("first", preview.FirstPostId);
        Assert.StartsWith("First & text", preview.FirstCapturedText);
        Assert.Equal(400, preview.FirstCapturedText.Length);
        Assert.Equal("image", preview.FirstAttachment?.MediaId);
        Assert.Equal(absentMediaPath, preview.FirstAttachment?.PlannedPath);
        Assert.False(Directory.Exists(Path.GetDirectoryName(absentMediaPath)));
    }

    [Fact]
    public async Task MissingThreadsAndEmptyRegistrationsAreHandledWithoutFullThreadRead()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        await catalog.RegisterThreadAsync(new ArchiveThreadRegistration(Id, "https://example.test/b/thread/42",
            "title", directory.Path, false));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        IReadOnlyDictionary<ResourceId, ArchiveThreadPreview> previews = await reader.ReadThreadPreviewsAsync(
            [Id, Id with { Thread = "missing" }]);

        ArchiveThreadPreview preview = Assert.Single(previews).Value;
        Assert.Null(preview.FirstPostId);
        Assert.Equal("", preview.FirstCapturedText);
        Assert.Null(preview.FirstAttachment);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadThreadPreviewsAsync(
            Enumerable.Repeat(Id, 101).ToArray()));
    }

    private static async Task<ArchiveThreadPreview> PreviewOfAsync(string directory, params CapturedAttachment[] attachments)
    {
        using ProfileLease lease = ProfileLease.Acquire(directory);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory, lease);
        await catalog.CaptureAsync(new CapturedThreadSnapshot(Id, "https://example.test/b/thread/42", "title",
            ThreadSourceState.Archived, DateTimeOffset.UtcNow, directory,
            [
                new CapturedPost("reply", 2, PostFieldPresence.Html | PostFieldPresence.Attachments, Html: "reply",
                    Attachments: [new CapturedAttachment(0, "reply-image")]),
                new CapturedPost("op", 1, PostFieldPresence.Html | PostFieldPresence.Attachments, Html: "opening",
                    Attachments: attachments)
            ]));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory);
        return (await reader.ReadThreadPreviewsAsync([Id]))[Id];
    }

    [Fact]
    public async Task AnOpeningPostWithSeveralMediaListsThemByPositionAndTheFirstIsThePreview()
    {
        using TempDirectory directory = new();
        // Stored out of order on purpose: the preview order is the attachment position, not insertion order.
        ArchiveThreadPreview preview = await PreviewOfAsync(directory.Path,
            new CapturedAttachment(2, "m-2", OriginalFileName: "IMG_4332.jpeg"),
            new CapturedAttachment(0, "m-0", OriginalFileName: "IMG_4328.jpeg"),
            new CapturedAttachment(1, "m-1", OriginalFileName: "IMG_4327.jpeg"));

        Assert.Equal("op", preview.FirstPostId);
        Assert.Equal(new[] { 0, 1, 2 }, preview.Attachments.Select(item => item.Position));
        Assert.Equal(new[] { "IMG_4328.jpeg", "IMG_4327.jpeg", "IMG_4332.jpeg" }, preview.Attachments.Select(item => item.OriginalFileName));
        Assert.Equal(preview.Attachments[0], preview.FirstAttachment);
        Assert.Equal("m-0", preview.FirstAttachment?.MediaId);
    }

    [Fact]
    public async Task AnOpeningPostWithOneMediumKeepsExactlyThatMedium()
    {
        using TempDirectory directory = new();
        ArchiveThreadPreview preview = await PreviewOfAsync(directory.Path, new CapturedAttachment(0, "only", OriginalFileName: "one.png"));

        CapturedAttachment only = Assert.Single(preview.Attachments);
        Assert.Equal("only", only.MediaId);
        Assert.Equal(only, preview.FirstAttachment);
    }

    [Fact]
    public async Task AnOpeningPostWithoutMediaHasNoPreviewMediaEvenWhenAReplyHasSome()
    {
        using TempDirectory directory = new();
        ArchiveThreadPreview preview = await PreviewOfAsync(directory.Path);

        Assert.Equal("op", preview.FirstPostId);
        Assert.Null(preview.FirstAttachment);
        Assert.Empty(preview.Attachments);
    }

    [Fact]
    public async Task PreviewAttachmentsAreBoundedPerThread()
    {
        using TempDirectory directory = new();
        ArchiveThreadPreview preview = await PreviewOfAsync(directory.Path,
            Enumerable.Range(0, 40).Select(index => new CapturedAttachment(index, "m-" + index)).ToArray());

        Assert.Equal(32, preview.Attachments.Count);
        Assert.Equal(Enumerable.Range(0, 32), preview.Attachments.Select(item => item.Position));
    }
}

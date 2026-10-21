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
}

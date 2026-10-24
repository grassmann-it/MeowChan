using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.Tests;

public sealed class CommittedMediaConsumerTests
{
    private static readonly ResourceId Id = new("fixture", "g", "42");
    private static readonly byte[] Bytes = [1, 4, 9, 16];

    [Fact]
    public async Task GalleryAndExportUseOneExactAssetWithDifferentPerPostAcceptanceProvenance()
    {
        using TempDirectory root = new();
        using ProfileLease lease = ProfileLease.Acquire(root.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(root.Path, lease);
        var fixture = await CreateAsync(root.Path, catalog);
        GalleryEntry[] entries = GallerySelection.Build(fixture.Thread, [fixture.Manifest]).Entries.ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Single(entries.Select(entry => entry.Asset.AssetId).Distinct());
        Assert.Equal(MediaAcceptanceKind.ExplicitProviderMismatch, entries[0].AttachmentResolution!.Binding!.Acceptance.Kind);
        Assert.Equal(MediaAcceptanceKind.Strict, entries[1].AttachmentResolution!.Binding!.Acceptance.Kind);
        Assert.Equal(Bytes.Length, entries[0].Asset.Length);
        Assert.Equal(Bytes.Length + 17, entries[0].Attachment!.ExpectedSize);
        Assert.Equal(fixture.Attachment.ExpectedMd5, entries[0].AttachmentResolution!.Binding!.Provider.ExpectedMd5);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(root.Path);
        ArchiveHtmlExportResult export = await ArchiveHtmlExporter.ExportAsync(reader, Id, Path.Combine(root.Path, "export"));
        Assert.Empty(export.Omissions);
        Assert.Equal(1, export.FileCount);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(Path.Combine(export.Directory, "media")))));
        string html = await File.ReadAllTextAsync(export.IndexPath);
        Assert.Equal(1, html.Split("Saved with source-specific permission").Length - 1);
        Assert.Contains("Saved file: 4 bytes", html);
        Assert.Contains("Size: 21 bytes", html);
        Assert.Contains(fixture.Attachment.ExpectedMd5!, html);
    }

    [Theory]
    [InlineData("url")]
    [InlineData("post")]
    [InlineData("position")]
    [InlineData("size")]
    [InlineData("sha256")]
    public async Task NewUnboundRevisionNeverInheritsAnOlderAcceptedAsset(string change)
    {
        using TempDirectory root = new();
        using ProfileLease lease = ProfileLease.Acquire(root.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(root.Path, lease);
        var fixture = await CreateAsync(root.Path, catalog);
        CapturedAttachment next = fixture.Attachment with { AttachmentRevisionKey = null };
        string postId = change == "post" ? "45" : "43";
        next = change switch
        {
            "url" => next with { Url = "https://fixture.invalid/new.png" },
            "position" => next with { Position = 2 },
            "size" => next with { ExpectedSize = 1000 },
            "sha256" => next with { ExpectedSha256 = new string('a', 64) },
            _ => next
        };
        await catalog.CaptureAsync(new(Id, "https://fixture.invalid/g/thread/42", "changed", ThreadSourceState.Archived,
            DateTimeOffset.UtcNow, fixture.Media, [new(postId, 0, PostFieldPresence.All, Attachments: [next])]));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(root.Path);
        ArchivedThreadView updated = (await reader.ReadThreadAsync(Id))!;
        GalleryThreadResult gallery = GallerySelection.Build(updated, [fixture.Manifest]);
        CapturedAttachment unbound = updated.GetPosts().Single(post => post.PostId == postId)
            .Attachments!.Single(attachment => attachment.Position == next.Position);
        Assert.NotNull(unbound.AttachmentRevisionKey);
        Assert.NotEqual(fixture.Attachment.AttachmentRevisionKey, unbound.AttachmentRevisionKey);
        Assert.Null(MediaAttachmentResolver.Resolve(fixture.Manifest, Id, postId, unbound));
        Assert.DoesNotContain(gallery.Entries, entry => entry.Post?.PostId == postId
            && entry.Attachment?.Position == next.Position);
        if (change == "position")
        {
            // Adding a position retains the earlier observation at its original position.
            GalleryEntry retained = Assert.Single(gallery.Entries, entry => entry.Post?.PostId == postId
                && entry.Attachment?.Position == fixture.Attachment.Position);
            Assert.Equal(fixture.Attachment.AttachmentRevisionKey, retained.Attachment!.AttachmentRevisionKey);
            Assert.Equal(fixture.Attachment.AttachmentRevisionKey, retained.AttachmentResolution!.Binding!.Reference.RevisionKey);
        }
        Assert.Contains(gallery.Entries, entry => entry.Post?.PostId == "44");
        ArchiveHtmlExportResult export = await ArchiveHtmlExporter.ExportAsync(reader, Id, Path.Combine(root.Path, "export"));
        Assert.Contains(export.Omissions, omission => omission.PostId == postId && omission.Reason == ArchiveExportOmissionReason.DifferentVersion);
    }

    [Fact]
    public async Task HistoricalAcceptedRevisionStaysBoundAfterASeparateCurrentAssetIsPublished()
    {
        using TempDirectory root = new();
        using ProfileLease lease = ProfileLease.Acquire(root.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(root.Path, lease);
        var fixture = await CreateAsync(root.Path, catalog);
        string original = fixture.Manifest.GetPage().Single().AssetId;
        using StubImageBoard board = new();
        byte[] replacement = [9, 8, 7, 6, 5];
        DownloadItem item = new(new Uri("https://stub.test/new.png"), Path.Combine(fixture.Media, "media.png"), board,
            board.GetThread(new Uri("https://stub.test/g/thread/42")), "777", expectedSize: replacement.Length,
            expectedMd5: Convert.ToBase64String(MD5.HashData(replacement)));
        using MemoryStream body = new(replacement);
        await new MediaStore().CommitAsync(item, body, CancellationToken.None);
        MediaAssetSnapshot current = await MediaAssetReader.OpenAsync(fixture.Media);
        GalleryEntry[] associated = GallerySelection.Build(fixture.Thread, [current]).Entries.Where(entry => entry.Post != null).ToArray();
        Assert.Equal(2, associated.Length);
        Assert.All(associated, entry => { Assert.Equal(original, entry.Asset.AssetId); Assert.True(entry.Historical); });
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(root.Path);
        ArchiveHtmlExportResult export = await ArchiveHtmlExporter.ExportAsync(reader, Id, Path.Combine(root.Path, "export"));
        Assert.Empty(export.Omissions);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(Path.Combine(export.Directory, "media")))));
    }

    [Fact]
    public async Task AcceptedBindingDoesNotBypassLocalExportShaVerification()
    {
        using TempDirectory root = new();
        using ProfileLease lease = ProfileLease.Acquire(root.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(root.Path, lease);
        var fixture = await CreateAsync(root.Path, catalog);
        await File.WriteAllBytesAsync(fixture.Manifest.GetPage().Single().Path, [4, 3, 2, 1]);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(root.Path);
        ArchiveHtmlExportResult export = await ArchiveHtmlExporter.ExportAsync(reader, Id, Path.Combine(root.Path, "export"));
        Assert.Equal(0, export.FileCount);
        Assert.Equal(2, export.Omissions.Count);
        Assert.All(export.Omissions, omission => Assert.Equal(ArchiveExportOmissionReason.VerificationFailed, omission.Reason));
    }

    private static async Task<(string Media, MediaAssetSnapshot Manifest, ArchivedThreadView Thread, CapturedAttachment Attachment)>
        CreateAsync(string root, ArchiveCatalog catalog)
    {
        string media = Path.Combine(root, "media");
        Directory.CreateDirectory(media);
        await File.WriteAllBytesAsync(Path.Combine(media, "media.png"), Bytes);
        object entry = new { MediaIdentifier = "777", Variant = "original", SourceUrl = "https://fixture.invalid/media.png",
            FileName = "media.png", Length = Bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant(),
            Md5 = Convert.ToBase64String(MD5.HashData(Bytes)), OriginalFileName = "media.png" };
        await File.WriteAllTextAsync(Path.Combine(media, ".meowchan-manifest.json"),
            JsonSerializer.Serialize(new { Version = 1, Entries = new Dictionary<string, object> { ["media.png"] = entry } }));
        CapturedAttachment attachment = new(0, "777", Url: "https://fixture.invalid/media.png", OriginalFileName: "media.png",
            PlannedPath: Path.Combine(media, "obsolete-path.png"), ExpectedSize: Bytes.Length + 17,
            ExpectedMd5: Convert.ToBase64String(MD5.HashData(new byte[] { 2, 7, 1 })));
        CapturedPost[] posts = [new("43", 0, PostFieldPresence.All, Attachments: [attachment]),
            new("44", 1, PostFieldPresence.All, Attachments: [attachment with { ExpectedSize = Bytes.Length, ExpectedMd5 = Convert.ToBase64String(MD5.HashData(Bytes)) }])];
        MediaAssetSnapshot manifest = await CommittedMediaTestFixture.BindAsync(media, Id, posts, allowMismatch: true);
        await catalog.CaptureAsync(new(Id, "https://fixture.invalid/g/thread/42", "fixture", ThreadSourceState.Archived,
            DateTimeOffset.UtcNow, media, posts));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(root);
        ArchivedThreadView thread = (await reader.ReadThreadAsync(Id))!;
        return (media, manifest, thread, thread.GetPosts().Single(post => post.PostId == "43").Attachments!.Single());
    }
}

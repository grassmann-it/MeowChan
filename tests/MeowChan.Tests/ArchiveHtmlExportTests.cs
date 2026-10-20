using System.Security.Cryptography;
using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Rules;
using MeowChan.Storage;

namespace MeowChan.Tests;

public sealed class ArchiveHtmlExportTests
{
    private static readonly ResourceId Id = new("4chan", "g", "123");
    private static readonly byte[] Bytes = [1, 4, 9, 16];
    private static readonly string Md5 = Convert.ToBase64String(MD5.HashData(Bytes));

    [Fact]
    public async Task ExportIsIndependentAndInertWithMatchingMediaLocalQuotesAndVisibleOmissions()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        string media = await SaveAsync(directory.Path);
        await catalog.CaptureAsync(Snapshot(media));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        string destination = Path.Combine(directory.Path, "export");
        ArchiveHtmlExportResult result = await ArchiveHtmlExporter.ExportAsync(reader, Id, destination);
        Assert.Equal(2, result.PostCount);
        Assert.Equal(1, result.FileCount);
        Assert.Collection(result.Omissions, issue => Assert.Equal(ArchiveExportOmissionReason.Unavailable, issue.Reason),
            issue => Assert.Equal(ArchiveExportOmissionReason.DifferentVersion, issue.Reason));
        string moved = Path.Combine(directory.Path, "moved folder");
        Directory.Move(destination, moved);
        Directory.Delete(media, true);
        string html = await File.ReadAllTextAsync(Path.Combine(moved, "index.html"));
        Assert.Contains("href=\"#p-313234\"", html);
        Assert.Contains("Post not captured", html);
        Assert.Contains("Other thread, outside this export", html);
        Assert.Contains("External reference", html);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("secret-script", html);
        Assert.DoesNotContain(directory.Path, html);
        Assert.DoesNotContain("https://", html);
        Assert.Contains("Content-Security-Policy", html);
        Assert.Contains("Removed at source", html);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(Path.Combine(moved, "media")))));
        Assert.True(File.Exists(Path.Combine(moved, "style.css")));
        Assert.Empty(Directory.GetDirectories(directory.Path, ".meowchan-export-*"));
    }

    [Fact]
    public async Task PreservedRulesAndTextOnlyChoiceExplainIntentWithoutReevaluation()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        string media = await SaveAsync(directory.Path);
        await catalog.CaptureAsync(Snapshot(media));
        MediaRuleStore rules = new(directory.Path, lease);
        await rules.LoadAsync();
        await rules.SaveAsync(new[] { new MediaRule(MediaRuleScope.Global, types: new MediaTypeSelection(MediaTypeSelectionMode.Selected)) });
        await catalog.GetOrCreateDecisionAsync(new CandidateMetadata(new CandidateKey(Id, "456"),
            new Uri("https://stub.test/456.bin"), Path.Combine(media, "456.bin"), Bytes.Length, Md5, extension: ".bin"), rules);
        await rules.SaveAsync(Array.Empty<MediaRule>());
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        ArchiveHtmlExportResult excluded = await ArchiveHtmlExporter.ExportAsync(reader, Id, Path.Combine(directory.Path, "excluded"));
        Assert.Equal(0, excluded.FileCount);
        Assert.Equal(2, excluded.Omissions.Count(issue => issue.Reason == ArchiveExportOmissionReason.ExcludedByRule));
        ArchiveHtmlExportResult text = await ArchiveHtmlExporter.ExportAsync(reader, Id, Path.Combine(directory.Path, "text"), new(false));
        Assert.All(text.Omissions, issue => Assert.Equal(ArchiveExportOmissionReason.MediaNotIncluded, issue.Reason));
        Assert.Equal(0, text.FileCount);
    }

    [Fact]
    public async Task CorruptSavedBytesAreOmittedWithoutRepairOrFalseMediaLink()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        string media = await SaveAsync(directory.Path);
        await catalog.CaptureAsync(Snapshot(media));
        string file = Path.Combine(media, "456.bin");
        await File.WriteAllBytesAsync(file, new byte[] { 0, 0, 0, 0 });
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        ArchiveHtmlExportResult result = await ArchiveHtmlExporter.ExportAsync(reader, Id, Path.Combine(directory.Path, "export"));
        Assert.Equal(0, result.FileCount);
        Assert.Contains(result.Omissions, issue => issue.Reason == ArchiveExportOmissionReason.VerificationFailed);
        Assert.DoesNotContain("href=\"media/", await File.ReadAllTextAsync(result.IndexPath));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, await File.ReadAllBytesAsync(file));
    }

    [Fact]
    public async Task CancellationAfterStagingAndPublicationCollisionLeaveExistingExportsIntact()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        await catalog.CaptureAsync(Snapshot(directory.Path));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        string existing = Path.Combine(directory.Path, "existing");
        Directory.CreateDirectory(existing);
        await File.WriteAllTextAsync(Path.Combine(existing, "index.html"), "KEEP");
        await Assert.ThrowsAsync<IOException>(() => ArchiveHtmlExporter.ExportAsync(reader, Id, existing));
        using CancellationTokenSource cancellation = new();
        string cancelled = Path.Combine(directory.Path, "cancelled");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ArchiveHtmlExporter.ExportAsync(reader, Id, cancelled,
            progress: new InlineProgress(_ => cancellation.Cancel()), cancellationToken: cancellation.Token));
        Assert.False(Directory.Exists(cancelled));
        Assert.Empty(Directory.GetDirectories(directory.Path, ".meowchan-export-*"));
        string collision = Path.Combine(directory.Path, "collision");
        await Assert.ThrowsAsync<IOException>(() => ArchiveHtmlExporter.ExportAsync(reader, Id, collision,
            progress: new InlineProgress(_ => { Directory.CreateDirectory(collision); File.WriteAllText(Path.Combine(collision, "index.html"), "KEEP2"); })));
        Assert.Equal("KEEP", await File.ReadAllTextAsync(Path.Combine(existing, "index.html")));
        Assert.Equal("KEEP2", await File.ReadAllTextAsync(Path.Combine(collision, "index.html")));
        Assert.Empty(Directory.GetDirectories(directory.Path, ".meowchan-export-*"));
    }

    [Fact]
    public async Task MediaOnlyThreadExportsCurrentGalleryWithoutInventingPosts()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        string media = await SaveAsync(directory.Path);
        await catalog.RegisterThreadAsync(new ArchiveThreadRegistration(Id, "https://boards.4chan.org/g/thread/123", "Media only", media, false));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        ArchiveHtmlExportResult result = await ArchiveHtmlExporter.ExportAsync(reader, Id, Path.Combine(directory.Path, "gallery"));
        Assert.Equal(0, result.PostCount);
        Assert.Equal(1, result.FileCount);
        Assert.Contains("No post text was captured", await File.ReadAllTextAsync(result.IndexPath));
    }

    [Fact]
    public async Task ExportUsesMatchingRetainedRevisionAndImmutableCaptureDespiteLaterChanges()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        string media = await SaveAsync(directory.Path);
        await catalog.CaptureAsync(Snapshot(media));
        await SaveAsync(directory.Path, new byte[] { 8, 8, 8, 8 });
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        bool changed = false;
        ArchiveHtmlExportResult result = await ArchiveHtmlExporter.ExportAsync(reader, Id, Path.Combine(directory.Path, "revision"),
            progress: new InlineProgress(_ =>
            {
                if (changed) return;
                changed = true;
                catalog.CaptureAsync(Snapshot(media) with { Posts = new[] { new CapturedPost("125", 2, PostFieldPresence.All, Html: "new capture") } })
                    .GetAwaiter().GetResult();
            }));
        Assert.Equal(2, result.PostCount);
        Assert.Equal(3, (await reader.ReadThreadAsync(Id))!.PostCount);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(Path.Combine(result.Directory, "media")))));
        Assert.DoesNotContain("new capture", await File.ReadAllTextAsync(result.IndexPath));
    }

    [Fact]
    public async Task OutputWriteFailureRemovesStagingAndNeverPublishesSuccess()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        await catalog.CaptureAsync(Snapshot(directory.Path));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        string output = Path.Combine(directory.Path, "failed");
        Exception? failure = await Record.ExceptionAsync(() => ArchiveHtmlExporter.ExportAsync(reader, Id, output,
            progress: new InlineProgress(_ =>
            {
                string stage = Assert.Single(Directory.GetDirectories(directory.Path, ".meowchan-export-*"));
                Directory.CreateDirectory(Path.Combine(stage, "index.html"));
            })));
        Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        Assert.False(Directory.Exists(output));
        Assert.Empty(Directory.GetDirectories(directory.Path, ".meowchan-export-*"));
    }

    [Fact]
    public async Task ExportPreservesFullObservedTextBeyondTheReaderDisplayLimit()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        string body = new string('λ', 70_000) + " full-tail-marker";
        await catalog.CaptureAsync(Snapshot(directory.Path) with { Posts = new[] { new CapturedPost("123", 0, PostFieldPresence.All, Html: body) } });
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        ArchiveHtmlExportResult result = await ArchiveHtmlExporter.ExportAsync(reader, Id, Path.Combine(directory.Path, "long"), new(false));
        Assert.Contains(body, System.Net.WebUtility.HtmlDecode(await File.ReadAllTextAsync(result.IndexPath)));
    }

    private sealed class InlineProgress(Action<ArchiveHtmlExportProgress> report) : IProgress<ArchiveHtmlExportProgress>
    { public void Report(ArchiveHtmlExportProgress value) => report(value); }
    private static CapturedThreadSnapshot Snapshot(string media) => new(Id, "https://boards.4chan.org/g/thread/123", "<Title> & stuff",
        ThreadSourceState.Archived, DateTimeOffset.UtcNow, media,
        new[] {
            new CapturedPost("123", 0, PostFieldPresence.All, Html: "&lt;img src=x onerror=alert(1)&gt;<script>secret-script</script>\n<a href='#p124'>&gt;&gt;124</a><a href='#p999'>&gt;&gt;999</a><a href='/a/thread/400#p400'>&gt;&gt;400</a><a href='https://external.test/#p9'>&gt;&gt;9</a>",
                Attachments: new[] { new CapturedAttachment(0, "456", OriginalFileName: "../<evil>.bin", ExpectedSize: Bytes.Length, ExpectedMd5: Md5),
                    new CapturedAttachment(1, "missing"), new CapturedAttachment(2, "456", ExpectedMd5: Convert.ToBase64String(MD5.HashData(new byte[] { 5, 5, 5, 5 }))) }),
            new CapturedPost("124", 1, PostFieldPresence.All, Html: "retained", IsDeleted: true)
        });
    private static async Task<string> SaveAsync(string root, byte[]? payload = null)
    {
        payload ??= Bytes;
        using StubImageBoard board = new();
        DownloadItem item = new(new Uri("https://stub.test/media/456.bin"), Path.Combine(root, "media", "456.bin"),
            board, board.GetThread(new Uri("https://stub.test/g/thread/123")), "456", expectedSize: payload.Length, expectedMd5: Convert.ToBase64String(MD5.HashData(payload)));
        using MemoryStream source = new(payload, false);
        MediaCommitResult result = await new MediaStore().CommitAsync(item, source, CancellationToken.None);
        return Path.GetDirectoryName(result.Path)!;
    }
}

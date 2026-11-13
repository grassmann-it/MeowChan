using System.Net;
using System.Security.Cryptography;
using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.Tests;

public sealed class HtmlExportLocalizationRenderingTests
{
    private static readonly ResourceId First = new("4chan", "g", "123");
    private static readonly ResourceId Second = new("4chan", "a", "400");
    private static readonly byte[] Bytes = [1, 4, 9, 16];
    private static readonly string Md5 = Convert.ToBase64String(MD5.HashData(Bytes));
    private const string Title = "<fixture> & source title";
    private const string Body = "Original source text: Saved media";
    private const string FileName = "../<fixture>&.bin";

    [Theory]
    [InlineData("en", "Export result")]
    [InlineData("de", "Exportergebnis")]
    [InlineData("es", "Resultado de la exportación")]
    [InlineData("fr", "Résultat de l’exportation")]
    [InlineData("ru", "Результат экспорта")]
    [InlineData("cs", "Výsledek exportu")]
    [InlineData("it", "Risultato dell'esportazione")]
    [InlineData("ja", "エクスポート結果")]
    [InlineData("ko", "내보내기 결과")]
    [InlineData("pl", "Wynik eksportu")]
    [InlineData("pt-BR", "Resultado da exportação")]
    [InlineData("zh-Hans", "导出结果")]
    public async Task ThreadFrameIsLocalizedWithoutChangingSourceDataOrVerifiedMedia(string language, string heading)
    {
        using TempDirectory directory = new();
        string profile = Path.Combine(directory.Path, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        string media = await SaveMediaAsync(directory.Path);
        await catalog.CaptureAsync(Snapshot(First, media));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        ArchiveHtmlExportResult result = await ArchiveHtmlExporter.ExportAsync(reader, First,
            Path.Combine(directory.Path, "export"), new() { Language = language });
        string html = await File.ReadAllTextAsync(result.IndexPath);
        string decoded = WebUtility.HtmlDecode(html);
        HtmlExportLocalization text = new(language);

        Assert.Contains("<html lang=\"" + language + "\">", html);
        Assert.Contains("<h2>" + heading + "</h2>", decoded);
        Assert.Contains(text.Get(HtmlExportText.Retention), decoded);
        Assert.Contains(text.Get(HtmlExportText.ExternalThreads), decoded);
        Assert.Contains(text.Get(HtmlExportText.Removed), decoded);
        Assert.Contains(text.Get(HtmlExportText.PostNotCaptured), decoded);
        Assert.Contains(text.Get(HtmlExportText.OtherThread), decoded);
        Assert.Contains(text.Get(HtmlExportText.ExternalReference), decoded);
        Assert.Contains(text.Get(HtmlExportText.SourceInfo), decoded);
        Assert.Contains(text.Get(HtmlExportText.Unspecified), decoded);
        Assert.Contains(WebUtility.HtmlDecode(text.Html(HtmlExportText.SavedFile, Bytes.Length)), decoded);
        Assert.Contains(WebUtility.HtmlDecode(text.Html(HtmlExportText.ResultCounts, 2, 1, 2)), decoded);
        Assert.Contains(Body, decoded); // Not a document-wide find-and-replace of source text.
        Assert.Contains(WebUtility.HtmlEncode(Title), html);
        Assert.Contains(WebUtility.HtmlEncode(FileName), html);
        Assert.Contains("Fixture author &amp; text", html);
        Assert.Contains("href=\"#p-313234\"", html);
        Assert.Contains("Content-Security-Policy", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("fixture-script", html);
        // The only address in the page is the fixed credit link of the About dialog, never captured content.
        Assert.Equal(1, html.Split("https://").Length - 1);
        Assert.Contains("<a href=\"https://grassmann-it.de\" rel=\"noopener noreferrer\">grassmann-it.de</a>", html);
        Assert.DoesNotContain(directory.Path, html);
        Assert.Equal(2, result.PostCount);
        Assert.Equal(1, result.FileCount);
        Assert.Collection(result.Omissions,
            item => Assert.Equal(ArchiveExportOmissionReason.Unavailable, item.Reason),
            item => Assert.Equal(ArchiveExportOmissionReason.NoMediaIdentity, item.Reason));
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(Path.Combine(result.Directory, "media")))));
        Assert.False((await ArchiveHtmlExporter.InspectAsync(result.Directory, First)).RequiresBackup);
        Assert.Equal(language, result.Metadata!.Options.Language);
    }

    [Theory]
    [InlineData("en", "MeowChan collection", "Collection start page")]
    [InlineData("de", "MeowChan-Sammlung", "Startseite der Sammlung")]
    [InlineData("es", "Colección de MeowChan", "Página inicial de la colección")]
    [InlineData("fr", "Collection MeowChan", "Page d’accueil de la collection")]
    [InlineData("ru", "Коллекция MeowChan", "Начальная страница коллекции")]
    public async Task CollectionIndexAndMemberPagesShareLanguageAndKeepOfflineLinks(string language, string title, string navigation)
    {
        using TempDirectory directory = new();
        string profile = Path.Combine(directory.Path, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(Snapshot(First, profile));
        await catalog.CaptureAsync(Snapshot(Second, profile));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        ArchiveCollectionHtmlExportResult result = await ArchiveCollectionHtmlExporter.ExportAsync(reader,
            [First, Second], Path.Combine(directory.Path, "collection"), new(false) { Language = language });
        string index = WebUtility.HtmlDecode(await File.ReadAllTextAsync(result.IndexPath));
        HtmlExportLocalization text = new(language);

        Assert.Contains("<html lang=\"" + language + "\">", index);
        Assert.Contains("<title>" + title + "</title>", index);
        Assert.Contains(text.Get(HtmlExportText.CollectionIntro), index);
        Assert.Contains(WebUtility.HtmlDecode(text.Html(HtmlExportText.CollectionCounts, 2, 4, 0, 6)), index);
        Assert.Contains(Title, index);
        Assert.Equal(2, result.ThreadCount);
        Assert.Equal(4, result.PostCount);
        Assert.Equal(0, result.FileCount);
        Assert.Equal(6, result.Omissions.Count);
        Assert.All(result.Omissions, omission => Assert.Equal(ArchiveExportOmissionReason.MediaNotIncluded, omission.Reason));
        foreach (ArchiveCollectionMember member in result.Metadata!.Members)
        {
            string page = await File.ReadAllTextAsync(Path.Combine(result.Directory, member.PagePath));
            Assert.Contains("<html lang=\"" + language + "\">", page);
            Assert.Contains("<nav><a href=\"../index.html\">" + navigation + "</a></nav>", WebUtility.HtmlDecode(page));
            Assert.Contains(member.PagePath + "#p-" + (member.Thread == First ? "313233" : "343030"), index);
            Assert.Contains(text.Get(HtmlExportText.OmissionMediaNotIncluded), WebUtility.HtmlDecode(page));
        }
        string first = await File.ReadAllTextAsync(Path.Combine(result.Directory, result.Metadata.Members[0].PagePath));
        Assert.Contains("href=\"" + Path.GetFileName(result.Metadata.Members[1].PagePath) + "#p-343030\"", first);
        Assert.False((await ArchiveCollectionHtmlExporter.InspectAsync(result.Directory, [First, Second])).RequiresBackup);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ru")]
    public async Task MediaOnlyAndMissingManifestFramesAreLocalizedButWarningsKeepDiagnosticContract(string language)
    {
        using TempDirectory directory = new();
        string profile = Path.Combine(directory.Path, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.RegisterThreadAsync(new ArchiveThreadRegistration(First, "https://archive.test/g/thread/123", Title, profile, false));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        ArchiveHtmlExportResult result = await ArchiveHtmlExporter.ExportAsync(reader, First,
            Path.Combine(directory.Path, "media-only"), new() { Language = language });
        HtmlExportLocalization text = new(language);
        string html = WebUtility.HtmlDecode(await File.ReadAllTextAsync(result.IndexPath));
        Assert.Contains(text.Get(HtmlExportText.SavedMedia), html);
        Assert.Contains(text.Get(HtmlExportText.GalleryDescription), html);
        Assert.Contains(text.Get(HtmlExportText.NoPosts), html);
        Assert.Contains(text.Get(HtmlExportText.WarningManifestUnavailable), html);
        Assert.DoesNotContain("A saved media manifest is unavailable.", html);
        Assert.Contains("A saved media manifest is unavailable.", result.Warnings);
        Assert.Equal(0, result.PostCount);
        Assert.Equal(0, result.FileCount);
        ArchiveHtmlExportResult noMedia = await ArchiveHtmlExporter.ExportAsync(reader, First,
            Path.Combine(directory.Path, "no-media"), new(false) { Language = language });
        Assert.Contains(text.Get(HtmlExportText.GalleryOmitted), WebUtility.HtmlDecode(await File.ReadAllTextAsync(noMedia.IndexPath)));
    }

    [Fact]
    public async Task ExplicitUpdateUsesNewLanguageAndRemainsInspectableWithBackup()
    {
        using TempDirectory directory = new();
        string profile = Path.Combine(directory.Path, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(Snapshot(First, profile));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        ArchiveHtmlExportResult original = await ArchiveHtmlExporter.ExportAsync(reader, First,
            Path.Combine(directory.Path, "export"), new(false));
        await File.WriteAllTextAsync(Path.Combine(original.Directory, "notes.txt"), "KEEP");
        ArchiveExportInspection before = await ArchiveHtmlExporter.InspectAsync(original.Directory, First);
        Assert.True(before.RequiresBackup);
        Assert.Contains("notes.txt", before.ExtraPaths);
        ArchiveHtmlExportResult updated = await ArchiveHtmlExporter.UpdateAsync(reader, First, original.Directory,
            original.Metadata!.ExportId, original.Metadata.Generation, new(false) { Language = "es" },
            confirmPreserveBackup: true, expectedObservedFingerprint: before.ObservedFingerprint);
        Assert.Equal(2, updated.Metadata!.Generation);
        Assert.Equal("es", updated.Metadata.Options.Language);
        Assert.False((await ArchiveHtmlExporter.InspectAsync(updated.Directory, First)).RequiresBackup);
        Assert.Contains("<html lang=\"es\">", await File.ReadAllTextAsync(updated.IndexPath));
        Assert.NotNull(updated.BackupDirectory);
        Assert.True(Directory.Exists(updated.BackupDirectory));
        Assert.Equal("KEEP", await File.ReadAllTextAsync(Path.Combine(updated.BackupDirectory!, "notes.txt")));
        Assert.Contains("<html lang=\"en\">", await File.ReadAllTextAsync(Path.Combine(updated.BackupDirectory!, "index.html")));
    }

    private static CapturedThreadSnapshot Snapshot(ResourceId id, string media)
    {
        string postId = id == First ? "123" : "400";
        return new CapturedThreadSnapshot(id,
            id == First ? "https://archive.test/g/thread/123" : "https://archive.test/a/thread/400", Title,
            ThreadSourceState.Archived, DateTimeOffset.UtcNow, media,
            [new CapturedPost(postId, 0, PostFieldPresence.All, Author: "Fixture author & text", Html: Body
                + "<a href='#p124'>&gt;&gt;124</a><a href='#p999'>&gt;&gt;999</a>"
                + "<a href='/a/thread/400#p400'>&gt;&gt;400</a><a href='https://external.test/#p9'>&gt;&gt;9</a>"
                + "<script>fixture-script</script>", Attachments:
                [new CapturedAttachment(0, "456", Url: "https://archive.test/media/456.bin", OriginalFileName: FileName,
                    ExpectedSize: Bytes.Length, ExpectedMd5: Md5), new CapturedAttachment(1, "missing"),
                    new CapturedAttachment(2, null, IsDeleted: true)]),
             new CapturedPost("124", 1, PostFieldPresence.All, Html: "retained fixture", IsDeleted: true)]);
    }

    private static async Task<string> SaveMediaAsync(string root)
    {
        using StubImageBoard board = new();
        DownloadItem item = new(new Uri("https://archive.test/media/456.bin"), Path.Combine(root, "media", "456.bin"),
            board, board.GetThread(new Uri("https://archive.test/g/thread/123")), "456", expectedSize: Bytes.Length, expectedMd5: Md5);
        using MemoryStream source = new(Bytes, false);
        MediaCommitResult committed = await new MediaStore().CommitAsync(item, source, CancellationToken.None);
        string directory = Path.GetDirectoryName(committed.Path)!;
        await CommittedMediaTestFixture.BindAsync(directory, First, Snapshot(First, directory).Posts);
        return directory;
    }
}

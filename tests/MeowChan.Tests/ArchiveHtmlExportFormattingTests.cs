using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.Tests;

/// <summary>
/// I13 in the written export, not only in the formatter: a real captured post goes through the whole export and the
/// page on disk carries the same greentext, spoilers, code, external links and struck-through dead references as the
/// reader, with the note in the chosen language. ArchiveHtmlExportTests covers the formatter itself.
/// </summary>
public sealed class ArchiveHtmlExportFormattingTests
{
    private static readonly ResourceId Id = new("4chan", "g", "500");

    private static CapturedThreadSnapshot Snapshot(string media) => new(Id, "https://boards.4chan.org/g/thread/500", "Formatting",
        ThreadSourceState.Archived, DateTimeOffset.UnixEpoch, media,
        new[]
        {
            new CapturedPost("500", 0, PostFieldPresence.All, Html:
                "&gt;be me<br>"
                + "<s>hidden words</s><br>"
                + "<pre class=\"prettyprint\">int x = 1;</pre>"
                + "see <a href=\"https://example.org/manual\">the manual</a><br>"
                + "<a href=\"#p501\" class=\"quotelink\">&gt;&gt;501</a> is here<br>"
                + "<a href=\"#p999\" class=\"quotelink\">&gt;&gt;999</a> is gone"),
            new CapturedPost("501", 1, PostFieldPresence.All, Html: "plain answer"),
        });

    private static async Task<ArchiveHtmlExportResult> ExportAsync(string root, string language)
    {
        string media = Path.Combine(root, "media", "500");
        Directory.CreateDirectory(media);
        using ProfileLease lease = ProfileLease.Acquire(root);
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(root, lease))
            await catalog.CaptureAsync(Snapshot(media));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(root);
        return await ArchiveHtmlExporter.ExportAsync(reader, Id,
            Path.Combine(root, "export-" + language), new ArchiveHtmlExportOptions { Language = language });
    }

    [Fact]
    public async Task TheWrittenPageCarriesGreentextSpoilersCodeAndExternalLinksLikeTheReader()
    {
        using TempDirectory directory = new();
        ArchiveHtmlExportResult export = await ExportAsync(directory.Path, "en");
        string html = await File.ReadAllTextAsync(export.IndexPath);

        Assert.Contains("<span class=\"greentext\">&gt;be me</span>", html);
        Assert.Contains("<span class=\"spoiler\" tabindex=\"0\">hidden words</span>", html);
        Assert.Contains("<span class=\"code\">int x = 1;</span>", html);
        Assert.Contains("<span class=\"link\">the manual</span>", html);
        // The address itself is never written into the page: the export stays offline and inert.
        Assert.DoesNotContain("https://example.org", html);
        Assert.DoesNotContain("<script", html);

        // The classes the body uses are the ones the style sheet defines.
        string css = await File.ReadAllTextAsync(Path.Combine(export.Directory, "style.css"));
        foreach (string rule in new[] { ".greentext", ".spoiler", ".code", ".link", ".dead", ".note" })
            Assert.Contains(rule, css);
    }

    [Fact]
    public async Task AReferenceToAPostThatWasNeverCapturedIsStruckThroughInTheTextAndTheLiveOneIsNot()
    {
        using TempDirectory directory = new();
        string html = await File.ReadAllTextAsync((await ExportAsync(directory.Path, "en")).IndexPath);

        Assert.Contains("<s class=\"dead\">&gt;&gt;999</s> <span class=\"note\">(Post not captured)</span>", html);
        // The reference to the captured post 501 keeps its plain text in the body and its link below it.
        Assert.Contains("&gt;&gt;501 is here", html);
        Assert.DoesNotContain("<s class=\"dead\">&gt;&gt;501</s>", html);
        Assert.Contains("href=\"#p-353031\"", html);
    }

    [Fact]
    public async Task TheNoteFollowsTheExportLanguageAndNeverLeaksAnEnglishSentence()
    {
        using TempDirectory directory = new();
        string german = await File.ReadAllTextAsync((await ExportAsync(directory.Path, "de")).IndexPath);

        Assert.Contains("<s class=\"dead\">&gt;&gt;999</s> <span class=\"note\">(Post nicht erfasst)</span>", german);
        Assert.DoesNotContain("(Post not captured)", german);
        Assert.Contains("lang=\"de\"", german);
        // The captured text itself is never translated.
        Assert.Contains("hidden words", german);
    }
}

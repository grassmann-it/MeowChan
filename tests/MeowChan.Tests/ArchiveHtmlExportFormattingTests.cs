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

    // Light/dark without script: three radio inputs precede the page wrapper, "System" (prefers-color-scheme) is the
    // default, the header labels are their click targets and the sheet re-declares the palette for the checked input.
    [Fact]
    public async Task ThePageCarriesAScriptFreeThemeSwitchThatDefaultsToTheSystemScheme()
    {
        using TempDirectory directory = new();
        ArchiveHtmlExportResult export = await ExportAsync(directory.Path, "de");
        string html = await File.ReadAllTextAsync(export.IndexPath);
        string css = await File.ReadAllTextAsync(Path.Combine(export.Directory, "style.css"));

        int body = html.IndexOf("<body>", StringComparison.Ordinal), page = html.IndexOf("<div class=\"page\">", StringComparison.Ordinal);
        string inputs = html[(body + "<body>".Length)..page];
        Assert.StartsWith("<input class=\"toggle\" type=\"radio\" name=\"theme\" id=\"theme-system\" checked aria-label=\"Farbschema: Systemvorgabe\">"
            + "<input class=\"toggle\" type=\"radio\" name=\"theme\" id=\"theme-light\" aria-label=\"Farbschema: Hell\">"
            + "<input class=\"toggle\" type=\"radio\" name=\"theme\" id=\"theme-dark\" aria-label=\"Farbschema: Dunkel\">", inputs);
        foreach (string id in new[] { "theme-system", "theme-light", "theme-dark" })
        {
            Assert.Contains("<label for=\"" + id + "\">", html);
            Assert.Contains("#" + id + ":checked~.page label[for=" + id + "]", css);
            Assert.Contains("#" + id + ":focus-visible~.page label[for=" + id + "]", css);
        }
        Assert.Contains(".page,#theme-dark:checked~.page{color-scheme:dark;", css);
        Assert.Contains("@media (prefers-color-scheme:light){html{background:#e3dfd8}.page{color-scheme:light;", css);
        Assert.Contains("#theme-light:checked~.page{color-scheme:light;", css);
        // The inputs stay focusable (never display:none) and need no :has() support.
        Assert.Contains(".toggle{position:fixed;top:0;left:0;width:1px;height:1px;margin:0;opacity:0;pointer-events:none}", css);
        Assert.DoesNotContain(":has(", css);
        Assert.DoesNotContain("<script", html);
    }

    // About dialog (same checkbox technique as the theme switch): thread facts the exporter already knows, the writing
    // MeowChan version, the fixed credit link and the logo pair with the app's photographer credit, in the export language.
    [Theory]
    [InlineData("en", "Source and board", "Collected and exported with MeowChan ", "MeowChan by ", "Simba and Mila, the MeowChan cats", "Close")]
    [InlineData("de", "Quelle und Board", "Gesammelt und exportiert mit MeowChan ", "MeowChan von ", "Simba und Mila, die MeowChan-Katzen", "Schließen")]
    public async Task TheAboutDialogShowsThreadFactsVersionAndCreditsInTheExportLanguage(string language, string source,
        string collected, string madeBy, string alt, string close)
    {
        using TempDirectory directory = new();
        ArchiveHtmlExportResult export = await ExportAsync(directory.Path, language);
        string html = await File.ReadAllTextAsync(export.IndexPath);
        HtmlExportLocalization text = new(language);

        Assert.Contains("<label class=\"button about-button\" for=\"about-open\" aria-hidden=\"true\">" + text.Html(HtmlExportText.About) + "</label>", html);
        Assert.Contains("<dt>" + System.Net.WebUtility.HtmlEncode(source) + "</dt><dd>4chan /g/</dd>", html);
        Assert.Contains("<dt>" + text.Html(HtmlExportText.AboutThreadNumber) + "</dt><dd>500</dd>", html);
        Assert.Contains("<dt>" + text.Html(HtmlExportText.AboutThreadTitle) + "</dt><dd>Formatting</dd>", html);
        Assert.Contains("<dt>" + text.Html(HtmlExportText.AboutPosts) + "</dt><dd>2</dd>", html);
        Assert.Contains("<dt>" + text.Html(HtmlExportText.AboutMedia) + "</dt><dd>0</dd>", html);
        // The fixture posts carry no time: shown as unspecified, never invented.
        Assert.Contains("<dt>" + text.Html(HtmlExportText.AboutFirstPost) + "</dt><dd>" + text.Html(HtmlExportText.Unspecified) + "</dd>", html);
        Assert.Contains(System.Net.WebUtility.HtmlEncode(collected + HtmlExportChrome.Version), html);
        Assert.Matches("^[0-9]+\\.[0-9]+\\.[0-9]+", HtmlExportChrome.Version);
        Assert.Contains(System.Net.WebUtility.HtmlEncode(madeBy) + "<a href=\"https://grassmann-it.de\" rel=\"noopener noreferrer\">grassmann-it.de</a>", html);
        Assert.Contains("<img class=\"cats\" loading=\"lazy\" src=\"meowchan-cats.png\" width=\"300\" height=\"150\" alt=\"" + System.Net.WebUtility.HtmlEncode(alt) + "\">", html);
        Assert.Contains(text.Html(HtmlExportText.PhotoCredit, "Alexander Huxel · Webentwicklung Huxel"), html);
        Assert.Contains("<label class=\"button about-close\" for=\"about-open\">" + System.Net.WebUtility.HtmlEncode(close) + "</label>", html);
        Assert.DoesNotContain("@", html.Replace("@media", ""));
        Assert.True(File.Exists(Path.Combine(export.Directory, "meowchan-cats.png")));
        string css = await File.ReadAllTextAsync(Path.Combine(export.Directory, "style.css"));
        Assert.Contains("#about-open:checked~.page .about-layer{display:flex}", css);
        int print = css.IndexOf("@media print{", StringComparison.Ordinal);
        Assert.Contains(".controls,.about-layer,#about-open:checked~.page .about-layer{display:none}", css[print..]);
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

using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.Tests;

/// <summary>
/// Pins the security properties of the written HTML export after the redesign and theme switch (review 1.1.0, HTML
/// export section): the exact default-src 'none' CSP in every page, no script anywhere, a style sheet without url(),
/// @import or @font-face, every captured value encoded, external links as inert spans without href, and every href/src
/// limited to SHA-256 media names with whitelisted extensions, hex post anchors, the own style sheet and the fixed
/// collection navigation. Hostile content in every captured field must stay text.
/// </summary>
public sealed partial class ArchiveHtmlExportSecurityTests
{
    private static readonly ResourceId First = new("4chan", "g", "700");
    private static readonly ResourceId Second = new("4chan", "a", "701");
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
    private const string Csp = "default-src 'none'; style-src 'self'; img-src 'self'; media-src 'self'; base-uri 'none'; form-action 'none'";
    private const string Hostile = "\"><script>alert(1)</script><img src=x onerror=alert(2)>";

    [GeneratedRegex("<script", RegexOptions.IgnoreCase)] private static partial Regex ScriptTag();
    [GeneratedRegex("\\s(href|src|srcset|action|formaction|poster|data)=\"([^\"]*)\"", RegexOptions.IgnoreCase)] private static partial Regex UrlAttribute();
    [GeneratedRegex("\\son[a-z]+\\s*=", RegexOptions.IgnoreCase)] private static partial Regex EventHandler();
    // Real tags only: captured text is encoded, so it never contains a raw '<' or '>'.
    [GeneratedRegex("<[a-zA-Z][^<>]*>")] private static partial Regex Tag();
    [GeneratedRegex("<(iframe|object|embed|form|base|frame|svg|math|template)\\b", RegexOptions.IgnoreCase)] private static partial Regex ActiveElement();
    [GeneratedRegex("^(#p-[0-9a-f]+|(\\.\\./)?media/[0-9a-f]{64}\\.(png|jpg|jpeg|gif|webp|bmp|webm|mp4|bin)|(\\.\\./)?style\\.css|(\\.\\./)?meowchan-cats\\.png|https://grassmann-it\\.de)$")] private static partial Regex ThreadPageUrl();
    [GeneratedRegex("^([0-9a-f]{64}\\.html)?#p-[0-9a-f]+$|^\\.\\./index\\.html$")] private static partial Regex CollectionNavigationUrl();
    [GeneratedRegex("^threads/[0-9a-f]{64}\\.html(#p-[0-9a-f]+)?$|^style\\.css$|^meowchan-cats\\.png$|^https://grassmann-it\\.de$")] private static partial Regex CollectionIndexUrl();

    private static CapturedThreadSnapshot Snapshot(ResourceId id, string media) => new(id, "https://boards.4chan.org/" + id.Board + "/thread/" + id.Thread,
        "Title " + Hostile, ThreadSourceState.Archived, DateTimeOffset.UnixEpoch, media,
        new[]
        {
            new CapturedPost(id.Thread!, 0, PostFieldPresence.All, Subject: "Subject " + Hostile, Author: "Author " + Hostile,
                Html: "&gt;green " + System.Net.WebUtility.HtmlEncode(Hostile) + "<br>"
                    + "<script>body-script</script><iframe src=\"https://evil.test/\"></iframe>"
                    + "<a href=\"javascript:alert(3)\" onclick=\"alert(4)\" style=\"color:red\">javascript link</a><br>"
                    + "<a href=\"https://evil.test/track\">https://good.example/</a><br>"
                    + "<a href=\"#p999\" class=\"quotelink\">&gt;&gt;999</a> gone",
                Attachments: new[] { new CapturedAttachment(0, "777", Url: "https://stub.test/media/777.png",
                    OriginalFileName: "\"><svg onload=alert(5)>.png", ExpectedSize: Png.Length, ExpectedMd5: Convert.ToBase64String(MD5.HashData(Png))) }),
            new CapturedPost("2", 1, PostFieldPresence.All, Html: "<a href=\"#p" + id.Thread + "\" class=\"quotelink\">&gt;&gt;" + id.Thread + "</a> reply"),
        });

    private static async Task<string> SaveAsync(string root, ResourceId id)
    {
        using StubImageBoard board = new();
        DownloadItem item = new(new Uri("https://stub.test/media/777.png"), Path.Combine(root, "media", id.Board, "777.png"),
            board, board.GetThread(new Uri("https://stub.test/g/thread/700")), "777", expectedSize: Png.Length,
            expectedMd5: Convert.ToBase64String(MD5.HashData(Png)));
        using MemoryStream source = new(Png, false);
        MediaCommitResult result = await new MediaStore().CommitAsync(item, source, CancellationToken.None);
        string directory = Path.GetDirectoryName(result.Path)!;
        await CommittedMediaTestFixture.BindAsync(directory, id, Snapshot(id, directory).Posts);
        return directory;
    }

    private const string CreditLink = "<a href=\"https://grassmann-it.de\" rel=\"noopener noreferrer\">grassmann-it.de</a>";

    private static void AssertInertPage(string html, Regex allowedUrls, string catsPath)
    {
        // About dialog: the one external href is our fixed credit link (with rel), the one extra image is the bundled
        // cat pair under its fixed name; nothing else may point outside the folder.
        Assert.Equal(1, html.Split("=\"https://").Length - 1);
        Assert.Equal(1, html.Split(CreditLink).Length - 1);
        Assert.Equal(1, html.Split(" src=\"" + catsPath + "\"").Length - 1);
        Assert.Contains("<input class=\"toggle\" type=\"checkbox\" id=\"about-open\"", html);
        // Exactly one CSP meta tag with exactly today's policy, ahead of the style sheet link.
        string meta = "<meta http-equiv=\"Content-Security-Policy\" content=\"" + Csp + "\">";
        Assert.Equal(1, html.Split("Content-Security-Policy").Length - 1);
        int csp = html.IndexOf(meta, StringComparison.Ordinal);
        Assert.True(csp > 0 && csp < html.IndexOf("<link rel=\"stylesheet\"", StringComparison.Ordinal));
        Assert.DoesNotMatch(ScriptTag(), html);
        Assert.DoesNotMatch(ActiveElement(), html);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evil.test", html);
        int urls = 0;
        foreach (Match tag in Tag().Matches(html))
        {
            // Attribute names only: an encoded value (alt text of a hostile file name) may contain "onload=" as text.
            string names = Regex.Replace(tag.Value, "\"[^\"]*\"", "\"\"");
            Assert.DoesNotMatch(EventHandler(), names);
            Assert.DoesNotContain(" style=", names);
            foreach (Match match in UrlAttribute().Matches(tag.Value))
            {
                Assert.True(match.Groups[1].Value is "href" or "src", "Unexpected URL attribute: " + match.Value);
                Assert.Matches(allowedUrls, match.Groups[2].Value);
                urls++;
            }
        }
        Assert.True(urls > 0);
    }

    private static void AssertEncodedFields(string html)
    {
        string encoded = System.Net.WebUtility.HtmlEncode(Hostile);
        Assert.Contains("Title " + encoded, html);
        Assert.Contains("Subject " + encoded, html);
        Assert.Contains("Author " + encoded, html);
        // The visible text of an external link is kept as an inert span; its address is never written.
        Assert.Contains("<span class=\"link\">https://good.example/</span>", html);
        Assert.Contains(System.Net.WebUtility.HtmlEncode("\"><svg onload=alert(5)>.png"), html);
    }

    [Fact]
    public async Task TheRedesignedThreadPageKeepsTheCspNoScriptAndOnlyWhitelistedLocalUrls()
    {
        using TempDirectory directory = new();
        string profile = Path.Combine(directory.Path, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(Snapshot(First, await SaveAsync(directory.Path, First)));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        ArchiveHtmlExportResult result = await ArchiveHtmlExporter.ExportAsync(reader, First, Path.Combine(directory.Path, "export"));
        string html = await File.ReadAllTextAsync(result.IndexPath);

        Assert.Equal(1, result.FileCount);
        AssertInertPage(html, ThreadPageUrl(), "meowchan-cats.png");
        AssertEncodedFields(html);
        Assert.Matches("<img loading=\"lazy\" src=\"media/[0-9a-f]{64}\\.png\"", html);
        Assert.Contains("href=\"style.css\"", html);
        Assert.Equal(ArchiveHtmlExporter.Css, await File.ReadAllTextAsync(Path.Combine(result.Directory, "style.css")));
        await AssertCatsAsync(result.Directory, result.Metadata!.Files.Select(file => file.Path));
    }

    [Fact]
    public async Task EveryCollectionPageKeepsTheCspNoScriptAndOnlyWhitelistedLocalUrls()
    {
        using TempDirectory directory = new();
        string profile = Path.Combine(directory.Path, "profile");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.CaptureAsync(Snapshot(First, await SaveAsync(Path.Combine(directory.Path, "one"), First)));
        await catalog.CaptureAsync(Snapshot(Second, await SaveAsync(Path.Combine(directory.Path, "two"), Second)));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
        ArchiveCollectionHtmlExportResult result = await ArchiveCollectionHtmlExporter.ExportAsync(reader, [First, Second],
            Path.Combine(directory.Path, "export"));

        string index = await File.ReadAllTextAsync(result.IndexPath);
        AssertInertPage(index, CollectionIndexUrl(), "meowchan-cats.png");
        Assert.Contains("Title " + System.Net.WebUtility.HtmlEncode(Hostile), index);
        foreach (ArchiveCollectionMember member in result.Metadata!.Members)
        {
            string page = await File.ReadAllTextAsync(Path.Combine(result.Directory, member.PagePath));
            AssertInertPage(page, new Regex(ThreadPageUrl() + "|" + CollectionNavigationUrl()), "../meowchan-cats.png");
            AssertEncodedFields(page);
            Assert.Contains("href=\"../style.css\"", page);
        }
        Assert.Equal(ArchiveHtmlExporter.Css, await File.ReadAllTextAsync(Path.Combine(result.Directory, "style.css")));
        await AssertCatsAsync(result.Directory, result.Metadata.Files.Select(file => file.Path));
    }

    // The cat pair is a fixed, small PNG next to index.html, recorded in the export inventory like every other file.
    private static async Task AssertCatsAsync(string directory, IEnumerable<string> inventory)
    {
        byte[] cats = await File.ReadAllBytesAsync(Path.Combine(directory, "meowchan-cats.png"));
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, cats[..4]);
        Assert.InRange(cats.Length, 1, 128 * 1024);
        Assert.Contains("meowchan-cats.png", inventory);
    }

    // The style sheet loads nothing: no url(), @import or @font-face (fonts are local family names only), no
    // expression() or behaviour hooks, no external scheme; the theme switch is pure selectors.
    [Fact]
    public void TheStyleSheetLoadsNoResourceAndRunsNoCode()
    {
        string css = ArchiveHtmlExporter.Css;
        foreach (string forbidden in new[] { "url(", "@import", "@font-face", "expression(", "behavior", "-moz-binding", "image-set(", "src(", "//", "\\" })
            Assert.DoesNotContain(forbidden, css, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Csp, ArchiveHtmlExporter.ContentSecurityPolicy);
    }
}

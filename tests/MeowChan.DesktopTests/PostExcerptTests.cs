using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Quote previews and reply cards show the same excerpt of a post; only the limits differ.
[Collection("Localization")]
public sealed class PostExcerptTests : IDisposable
{
    public PostExcerptTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    [Fact]
    public void HeaderNamesNumberAuthorAndDateAndBodyIsCutToTheGivenLimits()
    {
        DateTimeOffset when = new(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);
        CapturedPost post = new("123", 1, PostFieldPresence.All, when, null, "Anon", "<p>" + string.Join("<br>", Enumerable.Range(1, 9).Select(n => "line " + n)) + "</p>");

        (string header, string body) = ArchivePresentation.PostExcerpt(post, 3, 200);
        Assert.Equal(string.Join(" · ", Texts.T("reader.postNumber", "123"), "Anon", when.ToLocalTime().ToString("g", Texts.Culture)), header);
        Assert.Equal(3, body.Split('\n').Length);
        Assert.EndsWith("…", body);

        Assert.True(ArchivePresentation.PostExcerpt(post, 8, 200).Body.Split('\n').Length > 3);
        CapturedPost imageOnly = new("6", 0, PostFieldPresence.All, Html: "", Attachments: new[] { new CapturedAttachment(0, "m", OriginalFileName: "cat.png") });
        Assert.Equal("cat.png", ArchivePresentation.PostExcerpt(imageOnly, 3, 50).Body);
        Assert.Equal(Texts.T("reader.postNumber", "5"), ArchivePresentation.PostExcerpt(new("5", 0, PostFieldPresence.All), 3, 50).Header);
    }
}

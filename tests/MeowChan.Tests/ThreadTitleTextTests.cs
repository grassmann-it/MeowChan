using System.Globalization;
using MeowChan.Archive;

namespace MeowChan.Tests;

public sealed class ThreadTitleTextTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData(" \r\n\t ", "")]
    [InlineData("<b>Hello &amp; world</b><br>next\nline<script>hidden()</script>", "Hello & world next line")]
    [InlineData("A\u202E\u0001 B &lt;literal&gt;", "A B <literal>")]
    public void HtmlBecomesInertSingleLine(string? html, string expected) =>
        Assert.Equal(expected, ThreadTitleText.FromPlainText(ArchiveText.PlainText(html)));

    [Fact]
    public void ShorteningPreservesCombiningCharactersAndJoinedEmoji()
    {
        const string grapheme = "👩🏽‍💻";
        string text = new string('x', ThreadTitleText.MaximumTextElements - 1) + grapheme + "e\u0301";
        string title = ThreadTitleText.FromPlainText(text);
        Assert.Equal(new string('x', ThreadTitleText.MaximumTextElements - 1) + grapheme + "…", title);
        Assert.Equal(ThreadTitleText.MaximumTextElements + 1, new StringInfo(title).LengthInTextElements);
        Assert.Equal("e\u0301 " + grapheme, ThreadTitleText.FromPlainText("e\u0301 " + grapheme));
    }

    [Fact]
    public void PlainCatalogTextIsNotParsedAgainAsHtml() =>
        Assert.Equal("<b>literal &amp; text</b>", ThreadTitleText.FromPlainText("<b>literal &amp; text</b>"));

    [Fact]
    public void PartialSnapshotFirstReplyIsNotAnOpeningPost()
    {
        ResourceId id = new("source", "b", "42");
        Assert.Null(ThreadTitleText.OpeningHtml(id, [new("43", 0, PostFieldPresence.Html, Html: "reply")]));
        Assert.Null(ThreadTitleText.OpeningHtml(id, [new("42", 0, PostFieldPresence.None, Html: "absent field")]));
        Assert.Equal("OP", ThreadTitleText.OpeningHtml(id, [new("43", 0, PostFieldPresence.Html, Html: "reply"),
            new("42", 10, PostFieldPresence.Html, Html: "OP")]));
    }
}

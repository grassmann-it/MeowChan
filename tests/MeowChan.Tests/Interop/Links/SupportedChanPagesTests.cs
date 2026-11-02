using System.Text;
using MeowChan.Interop.Links;

namespace MeowChan.Tests.Interop.Links;

public sealed class SupportedChanPagesTests
{
    [Theory]
    [InlineData("https://boards.4chan.org/g/", true)]
    [InlineData("https://8kun.top/test_1/", true)]
    [InlineData("https://boards.4chan.org/g/thread/123", false)]
    [InlineData("https://boards.4chan.org/g/thread/123#p456", false)]
    [InlineData("https://8kun.top/test/res/123.html", false)]
    [InlineData("https://8kun.top/test/res/123.html#456", false)]
    public void CanonicalPagesArePassiveAndPreserved(string url, bool board)
    {
        PlainUrlText core = PlainUrlText.ForSupportedChanPages();
        byte[] bytes = Encoding.UTF8.GetBytes(url);
        LinkTextResult result = core.Parse(bytes);
        PassiveLink link = Assert.Single(result.Links);
        Assert.Equal(board ? PassiveLinkKind.Source : PassiveLinkKind.Post, link.Kind);
        Assert.Equal(url, link.Text);
        Assert.True(link.RequiresExplicitConfirmation);
        Assert.Empty(result.Issues);
        Assert.Equal(url + "\n", Encoding.UTF8.GetString(core.Export(bytes).Utf8));
    }

    [Theory]
    [InlineData("https://unknown.test/g/thread/123")]
    [InlineData("https://boards.4chan.org.evil.test/g/thread/123")]
    [InlineData("https://evilboards.4chan.org/g/thread/123")]
    [InlineData("https://8kun.top.evil.test/test/res/123.html")]
    [InlineData("https://boards.4chan.org/g/thread/123?opaque=SECRET")]
    [InlineData("https://8kun.top/test/res/123.html?X-Amz-Signature=SECRET")]
    [InlineData("https://boards.4chan.org/g/thread/123/SECRET")]
    [InlineData("https://8kun.top/test/res/SECRET.html")]
    [InlineData("https://boards.4chan.org/g/thread/123#SECRET")]
    [InlineData("https://8kun.top/test/res/123.html#SECRET")]
    [InlineData("https://boards.4chan.org/g/#p123")]
    [InlineData("https://8kun.top/test/#123")]
    [InlineData("https://boards.4chan.org/g/thread/123#123")]
    [InlineData("https://8kun.top/test/res/123.html#p123")]
    [InlineData("http://boards.4chan.org/g/thread/123")]
    [InlineData("HTTPS://boards.4chan.org/g/thread/123")]
    [InlineData("https://BOARDS.4chan.org/g/thread/123")]
    [InlineData("https://boards.4chan.org.:443/g/thread/123")]
    [InlineData("https://boards.4chan.org:443/g/thread/123")]
    [InlineData("https://boards.4channel.org/g/thread/123")]
    [InlineData("https://boards.4chan.org/g/catalog")]
    [InlineData("https://8kun.top/test/index.html")]
    [InlineData("https://boards.4chan.org/G/thread/123")]
    [InlineData("https://boards.4chan.org/猫/thread/123")]
    [InlineData("https://boards.4chan.org/%67/thread/123")]
    [InlineData("https://boards.4chan.org/g/../g/thread/123")]
    [InlineData("https://boards.4chan.org/g/thread/123/")]
    [InlineData("https://boards.4chan.org/g/thread/123#p")]
    [InlineData("https://8kun.top/test/res/123.html#")]
    public void NoncanonicalOrUnprovenUrlsAreExcludedWithoutLeaking(string url)
    {
        PlainUrlText core = PlainUrlText.ForSupportedChanPages();
        byte[] bytes = Encoding.UTF8.GetBytes(url);
        LinkTextResult result = core.Parse(bytes);
        Assert.Empty(result.Links);
        Assert.Equal(new LinkIssue(1, LinkIssueCode.SafetyUnknown), Assert.Single(result.Issues));
        Assert.DoesNotContain(url, result.ToString());
        Assert.DoesNotContain("SECRET", result.ToString());
        Assert.Empty(core.Export(bytes).Utf8);
    }

    [Fact]
    public void GrammarIdentifierBudgetsAreEnforced()
    {
        PlainUrlText core = PlainUrlText.ForSupportedChanPages();
        foreach (string url in new[] {
            "https://boards.4chan.org/" + new string('a', 257) + "/",
            "https://8kun.top/test/res/" + new string('1', 257) + ".html",
            "https://boards.4chan.org/g/thread/1#p" + new string('1', 257) })
            Assert.Empty(core.Parse(Encoding.UTF8.GetBytes(url)).Links);
    }
}

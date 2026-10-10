using oChan.Desktop;

namespace oChan.DesktopTests;

public sealed class ArchivePresentationTests
{
    [Fact]
    public void SourceMarkupBecomesInactiveReadableText()
    {
        const string source = "<style>body { color:red }</style><script>throw 1</script><p>Hello &amp; welcome<br>&gt;&gt;123</p><a href='https://invalid.test'>quote</a><img src='https://invalid.test/x'>";
        Assert.Equal("Hello & welcome\n>>123\nquote", ArchivePresentation.PlainText(source));
        Assert.Equal("<script>literal markup</script>", ArchivePresentation.PlainText("&lt;script&gt;literal markup&lt;/script&gt;"));
        Assert.Equal(string.Empty, ArchivePresentation.PlainText(null));
    }
}

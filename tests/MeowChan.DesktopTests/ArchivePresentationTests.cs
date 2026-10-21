using MeowChan.Desktop;
using MeowChan.Archive;

namespace MeowChan.DesktopTests;

public sealed class ArchivePresentationTests
{
    [Fact]
    public void InlineReplyRowReportsCountAndBoundedLoadedPage()
    {
        ArchivePostRow row = new("123", "#123", "", "", Array.Empty<ArchiveAttachmentRow>());
        List<string> changes = new();
        row.PropertyChanged += (_, eventArgs) => changes.Add(eventArgs.PropertyName!);
        row.SetBacklinkCount(3);
        Assert.Equal(3, row.BacklinkCount);
        Assert.Contains("3", row.BacklinksLabel);
        Assert.True(row.HasMoreReplies);
        Assert.Contains(nameof(row.BacklinksLabel), changes);
        row.SetReplies([new ArchiveQuoteRow(new ArchiveQuote(
            new ArchivePostId(new MeowChan.ResourceId("4chan", "g", "123"), "124"), null, ">>123", null), true)], 3, 0);
        Assert.True(row.RepliesLoaded);
        Assert.Single(row.Replies);
        Assert.True(row.HasMoreReplies);
        Assert.Equal("1 / 3", row.RepliesPageLabel);
    }

    [Fact]
    public void SourceMarkupBecomesInactiveReadableText()
    {
        const string source = "<style>body { color:red }</style><script>throw 1</script><p>Hello &amp; welcome<br>&gt;&gt;123</p><a href='https://invalid.test'>quote</a><img src='https://invalid.test/x'>";
        Assert.Equal("Hello & welcome\n>>123\nquote", ArchivePresentation.PlainText(source));
        Assert.Equal("<script>literal markup</script>", ArchivePresentation.PlainText("&lt;script&gt;literal markup&lt;/script&gt;"));
        Assert.Equal(string.Empty, ArchivePresentation.PlainText(null));
    }
}

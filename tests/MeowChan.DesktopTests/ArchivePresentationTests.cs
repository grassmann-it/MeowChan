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
    public void ReplyStateOnlyCollapsesConfirmedSuccessfulZero()
    {
        ArchivePostRow row = new("123", "#123", "", "", Array.Empty<ArchiveAttachmentRow>());
        Assert.True(row.CanExpandReplies); // Count has not arrived yet.
        row.SetRepliesError("local index unavailable");
        Assert.False(row.HasConfirmedNoReplies);
        row.SetBacklinkCount(0);
        Assert.True(row.CanExpandReplies); // An error must never become silent zero.
        row.SetRepliesError(null);
        Assert.True(row.HasConfirmedNoReplies);
        Assert.False(row.CanExpandReplies);
        row.SetBacklinkCount(1);
        Assert.True(row.CanExpandReplies);
    }

    [Fact]
    public void RetryingPageErrorKeepsZeroCountExpanderVisibleUntilLoadingCompletes()
    {
        ArchivePostRow row = new("123", "#123", "", "", Array.Empty<ArchiveAttachmentRow>());
        row.SetBacklinkCount(0);
        row.SetRepliesError("local page unavailable");
        List<string> changes = new();
        row.PropertyChanged += (_, eventArgs) => changes.Add(eventArgs.PropertyName!);
        row.SetRepliesLoading(true);
        Assert.Contains(nameof(row.CanExpandReplies), changes);
        Assert.Contains(nameof(row.HasConfirmedNoReplies), changes);
        changes.Clear();
        row.SetRepliesError(null);
        Assert.True(row.CanExpandReplies);
        Assert.False(row.HasConfirmedNoReplies);
        Assert.Contains(nameof(row.CanExpandReplies), changes);
        Assert.Contains(nameof(row.HasConfirmedNoReplies), changes);
        changes.Clear();
        row.SetReplies(Array.Empty<ArchiveQuoteRow>(), 0, 0);
        Assert.True(row.CanExpandReplies);
        changes.Clear();
        row.SetRepliesLoading(false);
        Assert.True(row.HasConfirmedNoReplies);
        Assert.False(row.CanExpandReplies);
        Assert.Contains(nameof(row.CanExpandReplies), changes);
        Assert.Contains(nameof(row.HasConfirmedNoReplies), changes);
    }

    [Fact]
    public void CompactBacklinkKeepsFullIdentityAndDistinguishesForeignThread()
    {
        MeowChan.ResourceId thread = new("4chan", "g", "123");
        ArchiveQuote quote = new(new ArchivePostId(thread, "124"), null, ">>123", null);
        ArchiveQuoteRow local = new(quote, true, thread);
        Assert.Equal("#124", local.Label);
        Assert.Contains("/g/", local.AccessibleLabel);
        Assert.Contains("123", local.AccessibleLabel);
        ArchiveQuoteRow foreign = new(quote, true, new("4chan", "g", "999"));
        Assert.Equal(foreign.AccessibleLabel, foreign.Label);
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

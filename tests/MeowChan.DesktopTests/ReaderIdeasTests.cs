using MeowChan.Archive;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// Reader ideas I1, I3, I6 and I2: the pure rules (preview text, motion policy, unread line, fold, inline replies).
public sealed class ReaderIdeasTests
{
    [Fact]
    public void QuotePreviewKeepsTheFirstLinesAndAttachesTheEllipsisToTheLastWord()
    {
        Assert.Equal("short\ntext", ArchivePage.ShortenQuotePreview("short\ntext"));
        string many = string.Join("\n", Enumerable.Range(1, 12).Select(number => "line " + number));
        string shortened = ArchivePage.ShortenQuotePreview(many);
        Assert.Equal(ArchivePage.QuotePreviewMaxLines, shortened.Split('\n').Length);
        Assert.EndsWith("line 8…", shortened);
        string longLine = new('x', ArchivePage.QuotePreviewMaxCharacters + 50);
        string cut = ArchivePage.ShortenQuotePreview(longLine);
        Assert.Equal(ArchivePage.QuotePreviewMaxCharacters + 1, cut.Length);
        Assert.EndsWith("x…", cut);
        string emoji = string.Concat(Enumerable.Repeat("a😀", 300)); // Never cut a surrogate pair in half.
        Assert.False(char.IsHighSurrogate(ArchivePage.ShortenQuotePreview(emoji)[^2]));
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("1", null, true)]
    [InlineData("true", null, true)]
    [InlineData("0", null, false)]
    [InlineData(null, "off", true)]
    [InlineData(null, "OFF", true)]
    [InlineData(null, "on", false)]
    public void ReducedMotionFollowsTheEnvironment(string? reduced, string? motion, bool expected) =>
        Assert.Equal(expected, ReaderMotion.IsReduced(name => name switch
        {
            "MEOWCHAN_REDUCED_MOTION" => reduced,
            "MEOWCHAN_MOTION" => motion,
            _ => null
        }));

    [Fact]
    public void HighlightLastsFourHundredMilliseconds() => Assert.Equal(400, ReaderMotion.Highlight.TotalMilliseconds);

    [Fact]
    public void UnreadLineShowsOnlyOnTheRowThatCarriesACount()
    {
        ArchivePostRow plain = new("1", "", "text", Array.Empty<ArchiveAttachmentRow>());
        ArchivePostRow one = plain with { UnreadCount = 1 }, many = plain with { UnreadCount = 6 };
        Assert.False(plain.HasUnreadLine);
        Assert.True(one.HasUnreadLine && many.HasUnreadLine);
        Assert.Equal(MeowChan.Desktop.Localization.Texts.Count("reader.unreadLine", 1), one.UnreadLabel);
        Assert.Equal(MeowChan.Desktop.Localization.Texts.Count("reader.unreadLine", 6), many.UnreadLabel);
        Assert.Contains("6", many.UnreadLabel);
        Assert.NotEqual(one.UnreadLabel.Replace("1", "{0}"), many.UnreadLabel.Replace("6", "{0}")); // Singular and plural differ.
    }
    [Fact]
    public void FoldShowsEighteenOfMoreThanTwentyFourLinesAndReportsExpansion()
    {
        Assert.Equal(24, ReaderBodyTextBlock.FoldThresholdLines);
        Assert.Equal(18, ReaderBodyTextBlock.FoldVisibleLines);
        List<bool> reported = [];
        ArchivePostRow row = new("1", "", "text", Array.Empty<ArchiveAttachmentRow>()) { StartExpanded = true, ExpandedChanged = reported.Add };
        Assert.True(row.Expanded);
        Assert.Empty(reported); // Starting expanded is not a change.
        row.Expanded = false;
        row.Expanded = false;
        row.Expanded = true;
        Assert.Equal([false, true], reported);
    }
    [Fact]
    public void ReplyToggleAppearsForRepliesAndForAFailedLoadOnly()
    {
        ArchivePostRow row = new("1", "", "text", Array.Empty<ArchiveAttachmentRow>());
        Assert.False(row.HasReplyToggle); // No count yet.
        row.SetBacklinkCount(0);
        Assert.False(row.HasReplyToggle); // A confirmed zero has nothing to open.
        row.SetBacklinkCount(3);
        Assert.True(row.HasReplyToggle);
        Assert.Contains("3", row.BacklinkCountLabel);
        ArchivePostRow failed = new("2", "", "text", Array.Empty<ArchiveAttachmentRow>());
        failed.SetRepliesError("boom");
        Assert.True(failed.HasReplyToggle); // A retry stays possible.
        Assert.Equal(5, ArchivePage.InlineReplyPageSize);
    }

    [Fact]
    public void ReplyCardShowsTheHeaderOrTheReferenceAndOnlyARealExcerpt()
    {
        ArchivePostId origin = new(new ResourceId("4chan", "g", "100"), "101");
        ArchiveQuote quote = new(origin, new ArchivePostId(origin.Thread, "100"), ">>100", null);
        ArchiveQuoteRow plain = new(quote, true, origin.Thread);
        Assert.Equal(plain.Label, plain.ReplyCardHeader);
        Assert.False(plain.HasReplyExcerpt);
        ArchiveQuoteRow card = plain with { ReplyHeader = "No. 101 · anon", ReplyExcerpt = "first lines" };
        Assert.Equal("No. 101 · anon", card.ReplyCardHeader);
        Assert.True(card.HasReplyExcerpt);
        string cut = ArchivePage.ShortenText(string.Join("\n", Enumerable.Range(1, 9).Select(n => "line " + n)), ArchivePage.ReplyCardMaxLines, ArchivePage.ReplyCardMaxCharacters);
        Assert.Equal("line 1\nline 2\nline 3\nline 4…", cut);
    }
}

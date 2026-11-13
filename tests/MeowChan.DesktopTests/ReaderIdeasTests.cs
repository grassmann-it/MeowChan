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
}

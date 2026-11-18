using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

public sealed class ReaderBodyFormattingTests
{
    private static readonly ResourceId Thread = new("4chan", "g", "100");

    private static ArchiveQuoteRow Quote(string label) => new(new ArchiveQuote(
        new ArchivePostId(Thread, "105"), new ArchivePostId(Thread, label.TrimStart('>')), label, null));

    private static string Join(ReaderBodyDocument document) =>
        string.Concat(document.Segments.Select(segment => document.Text.Substring(segment.Start, segment.Length)));

    [Fact]
    public void PlainBodyStaysOneUnformattedRun()
    {
        ReaderBodyDocument document = ReaderBodyFormatting.Format("Just text.\nSecond line.", []);
        Assert.False(document.IsFormatted);
        Assert.Single(document.Segments);
        Assert.False(document.HasUnplacedQuotes);
    }

    [Fact]
    public void GreentextLinesAndInlineReferencesCoverTheUnchangedText()
    {
        ArchiveQuoteRow first = Quote(">>101"), second = Quote(">>1012");
        string text = ">>101\nreply text\n>be me\n>>1012 and >>101 again\n>quoting >>101 inside greentext";
        ReaderBodyDocument document = ReaderBodyFormatting.Format(text, [first, second]);
        Assert.True(document.IsFormatted);
        Assert.Equal(text, Join(document)); // Selection, copy and find offsets keep the plain text.
        ReaderBodySegment[] quotes = document.Segments.Where(segment => segment.Kind == ReaderBodySegmentKind.Quote).ToArray();
        Assert.Equal(new[] { ">>101", ">>1012", ">>101", ">>101" }, quotes.Select(segment => text.Substring(segment.Start, segment.Length)));
        Assert.Same(second, quotes[1].Quote); // Longest label first: never ">>101" + "2".
        Assert.Same(first, quotes[2].Quote);
        string[] green = document.Segments.Where(segment => segment.Kind == ReaderBodySegmentKind.Greentext)
            .Select(segment => text.Substring(segment.Start, segment.Length)).ToArray();
        Assert.Equal(new[] { ">be me\n", ">quoting ", " inside greentext" }, green);
        Assert.False(document.HasUnplacedQuotes);
    }

    [Theory]
    [InlineData(">implying", true)]
    [InlineData("> spaced", true)]
    [InlineData(">>123 reply", false)]
    [InlineData(">>>/g/ board link", false)]
    [InlineData(">>", true)]
    [InlineData("text > other", false)]
    [InlineData("", false)]
    public void GreentextIsALineStartingWithOneQuoteMark(string line, bool greentext)
    {
        Assert.Equal(greentext, ReaderBodyFormatting.IsGreentextLine(line));
    }

    [Fact]
    public void ReferencesNeedAWordBoundaryAndUnplacedOnesStayVisibleAsButtons()
    {
        ArchiveQuoteRow quote = Quote(">>101"), missing = Quote(">>999");
        ReaderBodyDocument document = ReaderBodyFormatting.Format("see >>101x and >>1010", [quote, missing]);
        Assert.DoesNotContain(document.Segments, segment => segment.Kind == ReaderBodySegmentKind.Quote);
        Assert.True(document.HasUnplacedQuotes);
        Assert.True(ReaderBodyFormatting.Format(">>101.", [quote]).Segments.Any(segment => segment.Kind == ReaderBodySegmentKind.Quote));
    }

    [Fact]
    public void PostRowBuildsItsDocumentFromBodyAndQuotes()
    {
        ArchivePostRow row = new("105", "", ">>101\nok", Array.Empty<ArchiveAttachmentRow>()) { Quotes = [Quote(">>101")] };
        Assert.True(row.BodyDocument.IsFormatted);
        Assert.Same(row.BodyDocument, row.BodyDocument);
        Assert.False(row.HasUnplacedQuotes);
        Assert.Equal(row.Body, row.BodyDocument.Text);
    }

    [Fact]
    public void ReferenceToAnUncapturedPostIsDeadAndGetsItsNoteBehindTheLabel()
    {
        ArchiveQuoteRow live = Quote(">>101"), dead = Quote(">>999") with { IsDead = true };
        string text = "see >>101 and >>999 now";
        ReaderBodyDocument document = ReaderBodyFormatting.Format(text, [live, dead], deadNote: " (not captured)");
        Assert.Equal("see >>101 and >>999 (not captured) now", document.Text);
        Assert.Equal(string.Concat(document.Segments.Select(segment => document.Text.Substring(segment.Start, segment.Length))), document.Text);
        ReaderBodySegment deadSegment = Assert.Single(document.Segments, segment => segment.Kind == ReaderBodySegmentKind.DeadQuote);
        Assert.Equal(">>999", document.Text.Substring(deadSegment.Start, deadSegment.Length));
        Assert.Same(dead, deadSegment.Quote); // A click still reports that the post is not captured.
        ReaderBodySegment note = Assert.Single(document.Segments, segment => segment.Kind == ReaderBodySegmentKind.DeadNote);
        Assert.Equal(" (not captured)", document.Text.Substring(note.Start, note.Length));
        Assert.Null(note.Quote);
        Assert.Single(document.Segments, segment => segment.Kind == ReaderBodySegmentKind.Quote); // The live reference is untouched.
        Assert.False(document.HasUnplacedQuotes);
        // The same text without a note keeps the plain body exactly.
        Assert.Equal(text, ReaderBodyFormatting.Format(text, [live, dead]).Text);
    }

    [Fact]
    public void SpoilerCodeAndLinkRangesStyleTheTextWithoutChangingIt()
    {
        string text = "a secret b code c https://example.org/x. d";
        ArchiveTextMark[] marks = [new(ArchiveTextMarkKind.Spoiler, 2, 6), new(ArchiveTextMarkKind.Code, 11, 4)];
        ReaderBodyDocument document = ReaderBodyFormatting.Format(text, [], marks);
        Assert.Equal(text, document.Text);
        Assert.True(document.IsFormatted && document.HasSpoilers);
        string Styled(ArchiveTextStyle style) => string.Concat(document.Segments.Where(segment => (segment.Style & style) != 0)
            .Select(segment => document.Text.Substring(segment.Start, segment.Length)));
        Assert.Equal("secret", Styled(ArchiveTextStyle.Spoiler));
        Assert.Equal("code", Styled(ArchiveTextStyle.Code));
        Assert.Equal("https://example.org/x", Styled(ArchiveTextStyle.Link)); // The sentence dot stays outside.
        Assert.False(ReaderBodyFormatting.Format("no marks here", [], []).IsFormatted);
    }

    [Fact]
    public void LinkThatASpoilerCutsKeepsOneTargetForEverySegment()
    {
        // B8-R6: the reader shows https://example.org/private once the spoiler is revealed; every part must open exactly that.
        string text = "go https://example.org/private now";
        int spoilerAt = text.IndexOf("private", StringComparison.Ordinal);
        ReaderBodyDocument document = ReaderBodyFormatting.Format(text, [], [new(ArchiveTextMarkKind.Spoiler, spoilerAt, 7)]);
        ReaderBodySegment[] parts = document.Segments.Where(segment => segment.Link != null).ToArray();
        Assert.Equal(2, parts.Length);
        Assert.All(parts, part => Assert.Equal("https://example.org/private", part.Link!.OriginalString));
        Assert.All(parts, part => Assert.True(part.LinkSpansSpoiler));
        Assert.All(parts, part => Assert.True(part.LinkNeedsReveal)); // The visible half waits for the reveal as well.
        Assert.Equal("https://example.org/private", string.Concat(parts.Select(part => document.Text.Substring(part.Start, part.Length))));
        Assert.Null(ReaderExternalLink.ForActivation(parts[0], spoilersRevealed: false, selectionActive: false));
        Assert.Equal("https://example.org/private", ReaderExternalLink.ForActivation(parts[0], true, false)?.OriginalString);
        Assert.Equal("https://example.org/private", ReaderExternalLink.ForActivation(parts[1], true, false)?.OriginalString);
        Assert.Null(ReaderExternalLink.ForKeyboard(document, 0, 0, spoilersRevealed: false));
        Assert.Equal("https://example.org/private", ReaderExternalLink.ForKeyboard(document, 0, 0, true)?.OriginalString);
    }

    [Fact]
    public void LinkWithoutSpoilerStaysOpenWhileAnotherLinkHidesItsOwn()
    {
        string text = "https://a.example/ https://b.example/x";
        ReaderBodyDocument document = ReaderBodyFormatting.Format(text, [], [new(ArchiveTextMarkKind.Spoiler, 29, 9)]);
        ReaderBodySegment first = Assert.Single(document.Segments, segment => segment.Link?.Host == "a.example");
        Assert.False(first.LinkNeedsReveal);
        Assert.NotNull(ReaderExternalLink.ForActivation(first, spoilersRevealed: false, selectionActive: false));
    }

    [Fact]
    public void AnchorWithWordsAndAnAddressLinksOnlyTheAddress()
    {
        // B8-R6: the anchor text is "see https://x.example/p": no address as a whole, but the address inside stays a link.
        string text = "pre see https://x.example/p post";
        ReaderBodyDocument document = ReaderBodyFormatting.Format(text, [], [new(ArchiveTextMarkKind.Link, 4, 23)]);
        ReaderBodySegment link = Assert.Single(document.Segments, segment => segment.Link != null);
        Assert.Equal("https://x.example/p", document.Text.Substring(link.Start, link.Length));
        Assert.Equal("https://x.example/p", link.Link!.OriginalString);
        Assert.True((link.Style & ArchiveTextStyle.Link) != 0);
        Assert.All(document.Segments.Where(segment => segment != link), segment => Assert.Equal(0, (int)(segment.Style & ArchiveTextStyle.Link)));
    }

    [Fact]
    public void AnchorWhoseTextIsAnAddressOpensItsWholeText()
    {
        string text = "x https://x.example/a/b y";
        ReaderBodyDocument document = ReaderBodyFormatting.Format(text, [], [new(ArchiveTextMarkKind.Link, 2, 21)]);
        ReaderBodySegment link = Assert.Single(document.Segments, segment => segment.Link != null);
        Assert.Equal("https://x.example/a/b", link.Link!.OriginalString);
        Assert.Equal(2, link.Start);
        Assert.Equal(21, link.Length);
    }

    [Fact]
    public void ReferenceInsideASpoilerKeepsBothTheLinkAndTheHiddenStyle()
    {
        ArchiveQuoteRow quote = Quote(">>101");
        ReaderBodyDocument document = ReaderBodyFormatting.Format("x >>101 y", [quote], [new(ArchiveTextMarkKind.Spoiler, 0, 9)]);
        ReaderBodySegment link = Assert.Single(document.Segments, segment => segment.Kind == ReaderBodySegmentKind.Quote);
        Assert.True(link.IsSpoiler);
        Assert.All(document.Segments, segment => Assert.True(segment.IsSpoiler));
    }

    [Fact]
    public void PostRowMarksDeadReferencesWithTheCatalogNote()
    {
        ArchivePostRow row = new("105", "", ">>101\nok", Array.Empty<ArchiveAttachmentRow>())
        { Quotes = [Quote(">>101") with { IsDead = true }] };
        Assert.Contains(row.BodyDocument.Segments, segment => segment.Kind == ReaderBodySegmentKind.DeadNote);
        Assert.StartsWith(">>101 (", row.BodyDocument.Text, StringComparison.Ordinal);
        Assert.False(row.SpoilersRevealed);
        row.SpoilersRevealed = true;
        Assert.True(row.SpoilersRevealed);
    }

    private static readonly ResourceId OtherThread = new("4chan", "g", "200");

    // P3: the target of the reference decides the marker.
    private static ArchiveQuoteRow QuoteTo(string label, ResourceId thread, string postId) => new(new ArchiveQuote(
        new ArchivePostId(Thread, "105"), new ArchivePostId(thread, postId), label, null));

    [Fact]
    public void MarkerStandsBehindTheReferenceAndTheTextBetweenStaysIntact()
    {
        ArchiveQuoteRow op = QuoteTo(">>100", Thread, "100"), plain = Quote(">>101");
        string marker = Texts.T("reader.quote.opMarker");
        Assert.NotEmpty(marker);
        ReaderBodyDocument document = ReaderBodyFormatting.Format("see >>100 and >>101.", [op, plain]);
        Assert.Equal("see >>100 " + marker + " and >>101.", document.Text);
        Assert.Equal(document.Text, Join(document));
        ReaderBodySegment quote = Assert.Single(document.Segments, segment => ReferenceEquals(segment.Quote, op));
        Assert.Equal(">>100", document.Text.Substring(quote.Start, quote.Length));
        ReaderBodySegment mark = Assert.Single(document.Segments, segment => segment.Kind == ReaderBodySegmentKind.QuoteMarker);
        Assert.Equal(" " + marker, document.Text.Substring(mark.Start, mark.Length));
        Assert.Equal(quote.End, mark.Start);
        Assert.Null(mark.Quote); // Neither hover nor click addresses the marker.
        Assert.Single(document.Segments, segment => ReferenceEquals(segment.Quote, plain)); // The unmarked reference is untouched.
        Assert.False(document.HasUnplacedQuotes);
    }

    [Fact]
    public void LongestLabelWinsAndEachMarkerFollowsItsOwnReference()
    {
        ArchiveQuoteRow shortOp = QuoteTo(">>100", Thread, "100"), longer = QuoteTo(">>1001", OtherThread, "1001");
        ReaderBodyDocument document = ReaderBodyFormatting.Format(">>1001 then >>100", [shortOp, longer]);
        string cross = Texts.T("reader.quote.crossMarker"), op = Texts.T("reader.quote.opMarker");
        Assert.Equal(">>1001 " + cross + " then >>100 " + op, document.Text);
        Assert.Equal(2, document.Segments.Count(segment => segment.Kind == ReaderBodySegmentKind.QuoteMarker));
    }

    [Fact]
    public void DeadReferenceGetsItsMarkerBeforeTheNote()
    {
        ArchiveQuoteRow dead = QuoteTo(">>100", Thread, "100") with { IsDead = true };
        string marker = Texts.T("reader.quote.opMarker");
        ReaderBodyDocument document = ReaderBodyFormatting.Format("x >>100 y", [dead], deadNote: " (gone)");
        Assert.Equal("x >>100 " + marker + " (gone) y", document.Text);
        Assert.Equal(document.Text, Join(document));
        ReaderBodySegment[] kinds = document.Segments.SkipWhile(segment => segment.Kind != ReaderBodySegmentKind.DeadQuote).Take(3).ToArray();
        Assert.Equal([ReaderBodySegmentKind.DeadQuote, ReaderBodySegmentKind.QuoteMarker, ReaderBodySegmentKind.DeadNote], kinds.Select(segment => segment.Kind));
    }

    [Fact]
    public void MarkerInsideASpoilerIsHiddenWithIt()
    {
        ReaderBodyDocument document = ReaderBodyFormatting.Format("x >>100 y", [QuoteTo(">>100", Thread, "100")], [new(ArchiveTextMarkKind.Spoiler, 0, 7)]);
        ReaderBodySegment mark = Assert.Single(document.Segments, segment => segment.Kind == ReaderBodySegmentKind.QuoteMarker);
        Assert.True(mark.IsSpoiler);
        Assert.Equal(document.Text, Join(document));
    }

    [Fact]
    public void CopiedBodyTextCarriesTheMarkerLikeAnyDisplayedText()
    {
        ArchivePostRow row = new("105", "", ">>100\nok", Array.Empty<ArchiveAttachmentRow>()) { Quotes = [QuoteTo(">>100", Thread, "100")] };
        Assert.StartsWith(">>100 " + Texts.T("reader.quote.opMarker") + "\n", row.BodyDocument.Text, StringComparison.Ordinal);
    }
}

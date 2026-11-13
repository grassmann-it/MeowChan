using MeowChan.Archive;
using MeowChan.Desktop;

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
}

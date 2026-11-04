using System.Text;
using MeowChan.Interop.Links;

namespace MeowChan.Tests.Interop.Links;

public sealed class PlainUrlTextHostileInputRegressionTests
{
    private const string Page = "https://boards.4chan.org/g/thread/1";
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static void AssertRejected(byte[] input, int line, LinkIssueCode code)
    {
        PlainUrlText core = PlainUrlText.ForSupportedChanPages();
        LinkTextResult parsed = core.Parse(input);
        Assert.Empty(parsed.Links);
        Assert.Equal(new[] { new LinkIssue(line, code) }, parsed.Issues);
        LinkTextExport exported = core.Export(input);
        Assert.Empty(exported.Utf8);
        Assert.Equal(parsed.Issues, exported.Issues);
    }

    [Theory]
    [InlineData("https://boards.4chan.org/g/thread/%31")]
    [InlineData("https://boards.4chan.org/g/thread/1#p%32")]
    [InlineData("https://boards.4chan.org/g/thread/１")]
    [InlineData("https://boards.4chan.org/g/thread/١")]
    [InlineData("https://boards.4chan.org/g/thread/+1")]
    [InlineData("https://boards.4chan.org/g/thread/1;confirmed=true")]
    [InlineData("https://8kun.top/test/res/1.HTML")]
    [InlineData("https://8kun.top/test/res/1%2ehtml")]
    [InlineData("https://8kun.top/test/res/1.html#１")]
    [InlineData("https://8kun.top/test/res/1.html,https://8kun.top/test/res/2.html")]
    public void FactoryDoesNotDecodeDigitsFoldSuffixesOrSplitRecords(string url) =>
        AssertRejected(Bytes(url), 1, LinkIssueCode.SafetyUnknown);

    [Theory]
    [InlineData("\uFFFD")]
    [InlineData("\u200B")]
    [InlineData("\uFEFF")]
    public void ValidUtf8EmbeddedFormatOrReplacementCharacterIsNotDecoderFailureOrFraming(string suffix) =>
        AssertRejected(Bytes(Page + suffix), 1, LinkIssueCode.SafetyUnknown);

    [Theory]
    [InlineData("\0")]
    [InlineData("\u001B")]
    [InlineData("\u007F")]
    [InlineData("\u009F")]
    public void LiteralControlsInsideCanonicalPageAreNotRemovedBeforeFactory(string control) =>
        AssertRejected(Bytes(Page + control + "#p2"), 1, LinkIssueCode.InvalidUrl);

    [Fact]
    public void StrictDecodePrecedesLineOverflowEvenAfterTooManyPhysicalRecords()
    {
        byte[] input = Bytes(string.Concat(Enumerable.Repeat("\n", PlainUrlText.MaxLines + 1)))
            .Concat(new byte[] { 0xf5, 0x80, 0x80, 0x80 }).ToArray();
        AssertRejected(input, 0, LinkIssueCode.InvalidUtf8);
    }

    [Fact]
    public void InitialBomConsumesByteBudgetBeforeItIsRemovedAsFraming()
    {
        // ASCII newlines are valid UTF-8; the three-byte BOM alone tips this
        // otherwise in-budget payload over the byte gate, before line inspection.
        byte[] input = Bytes("\uFEFF" + new string('\n', PlainUrlText.MaxBytes - 2));
        Assert.Equal(PlainUrlText.MaxBytes + 1, input.Length);
        AssertRejected(input, 0, LinkIssueCode.ByteLimit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void LengthGatePrecedesCredentialInspectionButOnlyForOverlongRecord(int excess)
    {
        const string prefix = "https://user@boards.4chan.org/";
        string url = prefix + new string('a', PlainUrlText.MaxLineCharacters - prefix.Length + excess);
        AssertRejected(Bytes(url), 1, excess == 0 ? LinkIssueCode.Credentials : LinkIssueCode.LineLength);
    }

    [Fact]
    public void FactoryExportKeepsSelectionOrderZeroIdentifiersAndExactSpellingAcrossRejectedLookalikes()
    {
        // Zero and underscore-only board spellings are accepted grammar, not
        // assertions that these pages or posts exist at a live source.
        string[] urls = ["https://8kun.top/_/res/0.html#0", "https://boards.4chan.org/_/",
            "https://boards.4chan.org/_/thread/0#p0", "https://8kun.top/_/"];
        byte[] input = Bytes(urls[0] + "\r" + urls[1] + "\r\n" + Page + "%31\n"
            + urls[2] + "\n" + urls[3] + "\r" + urls[0]);
        PlainUrlText core = PlainUrlText.ForSupportedChanPages();
        LinkTextResult parsed = core.Parse(input);
        Assert.Equal(urls, parsed.Links.Select(link => link.Text));
        Assert.Equal(new[] { 1, 2, 4, 5 }, parsed.Links.Select(link => link.Line));
        Assert.Equal(new[] { PassiveLinkKind.Post, PassiveLinkKind.Source, PassiveLinkKind.Post,
            PassiveLinkKind.Source }, parsed.Links.Select(link => link.Kind));
        Assert.All(parsed.Links, link => Assert.True(link.RequiresExplicitConfirmation));
        LinkIssue[] issues = [new(3, LinkIssueCode.SafetyUnknown), new(6, LinkIssueCode.Duplicate)];
        Assert.Equal(issues, parsed.Issues);
        LinkTextExport exported = core.Export(input);
        Assert.Equal(Bytes(string.Join("\n", urls) + "\n"), exported.Utf8);
        Assert.Equal(issues, exported.Issues);
        Assert.Equal(exported.Utf8, core.Export(exported.Utf8).Utf8);
        Assert.Empty(core.Export(exported.Utf8).Issues);
    }
}

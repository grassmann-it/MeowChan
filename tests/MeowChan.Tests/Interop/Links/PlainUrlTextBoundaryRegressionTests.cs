using System.Reflection;
using System.Text;
using MeowChan.Interop.Links;

namespace MeowChan.Tests.Interop.Links;

public sealed class PlainUrlTextBoundaryRegressionTests
{
    private const string First = "https://example.test/first";
    private const string Second = "https://example.test/second";

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    // Exact synthetic fixtures only, never a general host/query safety policy.
    private static PlainUrlText For(params string[] urls)
    {
        HashSet<string> fixtures = new(urls, StringComparer.Ordinal);
        return new(fixtures.Contains);
    }

    private static void AssertGlobalFailure(PlainUrlText core, byte[] input, int line, LinkIssueCode code)
    {
        LinkTextResult parsed = core.Parse(input);
        Assert.Empty(parsed.Links);
        Assert.Equal(new[] { new LinkIssue(line, code) }, parsed.Issues);
        LinkTextExport exported = core.Export(input);
        Assert.Empty(exported.Utf8);
        Assert.Equal(parsed.Issues, exported.Issues);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    public void InitialBomAndEachTerminalFormExportExactBomlessUtf8(string terminator)
    {
        const string url = "https://example.test/猫,一?public=二,三#\U0001F431";
        byte[] input = Bytes("\uFEFF" + url + terminator);
        PlainUrlText core = For(url);
        LinkTextResult parsed = core.Parse(input);
        Assert.Equal(new PassiveLink(1, url, PassiveLinkKind.Unknown), Assert.Single(parsed.Links));
        Assert.Empty(parsed.Issues);
        LinkTextExport exported = core.Export(input);
        Assert.Equal(Bytes(url + "\n"), exported.Utf8);
        Assert.Empty(exported.Issues);
        Assert.Equal(exported.Utf8, core.Export(exported.Utf8).Utf8);
    }

    [Fact]
    public void OnlyOneInitialBomIsFramingAndLaterBomPrefixedLinesAreNotRepaired()
    {
        byte[] input = Bytes("\uFEFF\uFEFF" + First + "\r\n" + Second + "\r\uFEFF" + First);
        PlainUrlText core = For(First, Second);
        LinkTextResult parsed = core.Parse(input);
        Assert.Equal(new PassiveLink(2, Second, PassiveLinkKind.Unknown), Assert.Single(parsed.Links));
        Assert.Equal(new[] { new LinkIssue(1, LinkIssueCode.InvalidUrl), new LinkIssue(3, LinkIssueCode.InvalidUrl) }, parsed.Issues);
        Assert.Equal(Bytes(Second + "\n"), core.Export(input).Utf8);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\uFEFF")]
    public void EmptyPayloadAndFramingOnlyPayloadHaveNoRecords(string input)
    {
        PlainUrlText core = new(_ => throw new InvalidOperationException("must not inspect an absent record"));
        Assert.Empty(core.Parse(Bytes(input)).Links);
        Assert.Empty(core.Parse(Bytes(input)).Issues);
        Assert.Empty(core.Export(Bytes(input)).Utf8);
        Assert.Empty(core.Export(Bytes(input)).Issues);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    public void AStandaloneTerminatorStillDelimitsOneEmptyInvalidRecord(string input)
    {
        PlainUrlText core = For(First);
        AssertGlobalFailure(core, Bytes(input), 1, LinkIssueCode.InvalidUrl);
    }

    [Fact]
    public void MixedSeparatorsKeepPhysicalLinesAndOrderedValueFreeIssues()
    {
        byte[] input = Bytes("\uFEFF" + First + "\r\n\r" + First + "\n" + Second + "\r\n" + Second + "\r");
        PlainUrlText core = For(First, Second);
        LinkTextResult parsed = core.Parse(input);
        Assert.Equal(new[] { new PassiveLink(1, First, PassiveLinkKind.Unknown), new PassiveLink(4, Second, PassiveLinkKind.Unknown) }, parsed.Links);
        LinkIssue[] expected = [new(2, LinkIssueCode.InvalidUrl), new(3, LinkIssueCode.Duplicate), new(5, LinkIssueCode.Duplicate)];
        Assert.Equal(expected, parsed.Issues);
        LinkTextExport exported = core.Export(input);
        Assert.Equal(Bytes(First + "\n" + Second + "\n"), exported.Utf8);
        Assert.Equal(expected, exported.Issues);
    }

    [Fact]
    public void ParserAndExporterInspectOnlyTheExplicitlySelectedByteSpan()
    {
        byte[] selected = Bytes(First);
        byte[] surrounded = new byte[] { 0xff }.Concat(selected).Append((byte)0xff).ToArray();
        PlainUrlText core = For(First);
        LinkTextResult parsed = core.Parse(surrounded.AsSpan(1, selected.Length));
        Assert.Equal(new PassiveLink(1, First, PassiveLinkKind.Unknown), Assert.Single(parsed.Links));
        Assert.Empty(parsed.Issues);
        LinkTextExport exported = core.Export(surrounded.AsSpan(1, selected.Length));
        Assert.Equal(Bytes(First + "\n"), exported.Utf8);
        Assert.Empty(exported.Issues);
        AssertGlobalFailure(core, surrounded, 0, LinkIssueCode.InvalidUtf8);
    }

    [Theory]
    [InlineData("E282")]
    [InlineData("80")]
    [InlineData("C080")]
    [InlineData("E080AF")]
    [InlineData("F08080AF")]
    [InlineData("EDA080")]
    [InlineData("F4908080")]
    [InlineData("E228A1")]
    public void InvalidUtf8AfterAValidLineDiscardsEverythingBeforeAnyCallback(string invalidHex)
    {
        int calls = 0;
        PlainUrlText core = new(_ => { calls++; return true; }, _ => { calls++; return PassiveLinkKind.Post; });
        byte[] input = Bytes("\uFEFF" + First + "\n").Concat(Convert.FromHexString(invalidHex)).ToArray();
        AssertGlobalFailure(core, input, 0, LinkIssueCode.InvalidUtf8);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void ByteOverflowTakesPrecedenceOverInvalidUtf8WithoutCallingCollaborators()
    {
        int calls = 0;
        PlainUrlText core = new(_ => { calls++; return true; }, _ => { calls++; return PassiveLinkKind.Source; });
        byte[] input = new byte[PlainUrlText.MaxBytes + 1];
        input[0] = 0xff;
        AssertGlobalFailure(core, input, 0, LinkIssueCode.ByteLimit);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void LineLengthUsesUtf16UnitsNotUtf8BytesOrUnicodeScalars(int excess)
    {
        const string prefix = "https://example.test/";
        string url = prefix + "猫" + new string('x', PlainUrlText.MaxLineCharacters - prefix.Length - 3) + "\U0001F431" + new string('x', excess);
        Assert.Equal(PlainUrlText.MaxLineCharacters + excess, url.Length);
        Assert.True(Bytes(url).Length > PlainUrlText.MaxLineCharacters);
        int calls = 0;
        PlainUrlText core = new(value => { calls++; return value == url; });
        if (excess == 0)
        {
            LinkTextResult parsed = core.Parse(Bytes(url));
            Assert.Equal(url, Assert.Single(parsed.Links).Text);
            Assert.Empty(parsed.Issues);
            Assert.Equal(Bytes(url + "\n"), core.Export(Bytes(url)).Utf8);
            Assert.Equal(2, calls);
        }
        else
        {
            AssertGlobalFailure(core, Bytes(url), 1, LinkIssueCode.LineLength);
            Assert.Equal(0, calls);
        }
    }

    [Fact]
    public void OverlongIndividualLineDoesNotDiscardLaterValidLines()
    {
        string overlong = "https://example.test/" + new string('x', PlainUrlText.MaxLineCharacters);
        byte[] input = Bytes(First + "\n" + overlong + "\n" + Second);
        PlainUrlText core = For(First, Second);
        LinkTextResult parsed = core.Parse(input);
        Assert.Equal(new[] { 1, 3 }, parsed.Links.Select(link => link.Line));
        Assert.Equal(new[] { First, Second }, parsed.Links.Select(link => link.Text));
        Assert.Equal(new[] { new LinkIssue(2, LinkIssueCode.LineLength) }, parsed.Issues);
        LinkTextExport exported = core.Export(input);
        Assert.Equal(Bytes(First + "\n" + Second + "\n"), exported.Utf8);
        Assert.Equal(parsed.Issues, exported.Issues);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    public void ExactPhysicalLineBudgetAcceptsTerminalSeparatorButAnExtraRecordResetsResult(string separator)
    {
        string[] urls = Enumerable.Range(1, PlainUrlText.MaxLines).Select(i => "https://example.test/" + i).ToArray();
        PlainUrlText core = For(urls);
        byte[] input = Bytes(string.Join(separator, urls) + separator);
        LinkTextResult parsed = core.Parse(input);
        Assert.Equal(urls, parsed.Links.Select(link => link.Text));
        Assert.Equal(Enumerable.Range(1, PlainUrlText.MaxLines), parsed.Links.Select(link => link.Line));
        Assert.Empty(parsed.Issues);
        Assert.Equal(Bytes(string.Join("\n", urls) + "\n"), core.Export(input).Utf8);
        // A second terminal separator introduces a real empty line at MaxLines + 1.
        AssertGlobalFailure(core, input.Concat(Bytes(separator)).ToArray(), PlainUrlText.MaxLines + 1, LinkIssueCode.LineLimit);
    }

    [Fact]
    public void LineCountOverflowReplacesEarlierAcceptedLinksAndPerLineIssues()
    {
        string prefix = First + "\n" + First + "\n";
        string rest = string.Concat(Enumerable.Repeat("# not a directive\n", PlainUrlText.MaxLines - 1));
        AssertGlobalFailure(For(First), Bytes(prefix + rest), PlainUrlText.MaxLines + 1, LinkIssueCode.LineLimit);
    }

    private static string[] FullByteBudgetUrls(bool growLastLine) => Enumerable.Range(0, 128).Select(i =>
    {
        string prefix = "https://example.test/" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + "/";
        int length = PlainUrlText.MaxLineCharacters - 1 + (growLastLine && i == 127 ? 1 : 0);
        return prefix + new string('x', length - prefix.Length);
    }).ToArray();

    [Fact]
    public void ExactByteBudgetIncludingFinalLfIsAcceptedAndExportedByteForByte()
    {
        string[] urls = FullByteBudgetUrls(growLastLine: false);
        byte[] input = Bytes(string.Join("\n", urls) + "\n");
        Assert.Equal(PlainUrlText.MaxBytes, input.Length);
        PlainUrlText core = For(urls);
        Assert.Equal(urls, core.Parse(input).Links.Select(link => link.Text));
        Assert.Empty(core.Parse(input).Issues);
        LinkTextExport exported = core.Export(input);
        Assert.Equal(input, exported.Utf8);
        Assert.Empty(exported.Issues);
    }

    [Fact]
    public void AddingExportTerminatorAtFullByteBudgetFailsWithoutPartialOutput()
    {
        string[] urls = FullByteBudgetUrls(growLastLine: true);
        byte[] input = Bytes(string.Join("\r", urls));
        Assert.Equal(PlainUrlText.MaxBytes, input.Length);
        PlainUrlText core = For(urls);
        Assert.Equal(urls, core.Parse(input).Links.Select(link => link.Text));
        Assert.Empty(core.Parse(input).Issues);
        LinkTextExport exported = core.Export(input);
        Assert.Empty(exported.Utf8);
        Assert.Equal(new[] { new LinkIssue(0, LinkIssueCode.ByteLimit) }, exported.Issues);
    }

    [Fact]
    public void CommaSeparatedUrlsAreOneUnprovenRecordNotTwoAdmissions()
    {
        string combined = First + "," + Second;
        List<string> inspected = new();
        PlainUrlText core = new(value => { inspected.Add(value); return value == First || value == Second; });
        byte[] input = Bytes(combined + "\n" + First);
        LinkTextResult parsed = core.Parse(input);
        Assert.Equal(new[] { combined, First }, inspected);
        Assert.Equal(new PassiveLink(2, First, PassiveLinkKind.Unknown), Assert.Single(parsed.Links));
        Assert.Equal(new[] { new LinkIssue(1, LinkIssueCode.SafetyUnknown) }, parsed.Issues);
        LinkTextExport exported = core.Export(input);
        Assert.Equal(Bytes(First + "\n"), exported.Utf8);
        Assert.Equal(parsed.Issues, exported.Issues);
    }

    [Fact]
    public void AttestedCommaRecordIsPreservedAsOneOriginalUrl()
    {
        string combined = First + "," + Second;
        PlainUrlText core = For(combined);
        LinkTextResult parsed = core.Parse(Bytes(combined));
        Assert.Equal(new PassiveLink(1, combined, PassiveLinkKind.Unknown), Assert.Single(parsed.Links));
        Assert.Empty(parsed.Issues);
        Assert.Equal(Bytes(combined + "\n"), core.Export(Bytes(combined)).Utf8);
    }

    [Fact]
    public void OrdinalIdentityPreservesCasePortsDotSegmentsEscapesAndUnicodeForms()
    {
        string[] urls = [
            "https://example.test/a", "HTTPS://EXAMPLE.test/a", "https://example.test:443/a",
            "https://example.test/x/../a", "https://example.test/%61", "https://example.test/%6a",
            "https://example.test/%6A", "https://example.test/é", "https://example.test/e\u0301",
            "https://example.test/a#public", "https://example.test/a?public=1"
        ];
        PlainUrlText core = For(urls);
        byte[] input = Bytes(string.Join("\n", urls) + "\n" + urls[1]);
        LinkTextResult parsed = core.Parse(input);
        Assert.Equal(urls, parsed.Links.Select(link => link.Text));
        Assert.Equal(new[] { new LinkIssue(urls.Length + 1, LinkIssueCode.Duplicate) }, parsed.Issues);
        Assert.Equal(Bytes(string.Join("\n", urls) + "\n"), core.Export(input).Utf8);
    }

    [Theory]
    [InlineData("\u00A0")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    [InlineData("\u0085")]
    public void UnicodeWhitespaceIsNotASeparatorOrSomethingToTrim(string whitespace)
    {
        int calls = 0;
        PlainUrlText core = new(_ => { calls++; return true; });
        AssertGlobalFailure(core, Bytes(First + whitespace + Second), 1, LinkIssueCode.InvalidUrl);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("https://example.test/%00")]
    [InlineData("https://example.test/?public=%1f")]
    [InlineData("https://example.test/#%7F")]
    [InlineData("https://example.test/%0D%0a")]
    public void EncodedControlsInAnyUrlComponentNeverReachPolicy(string url)
    {
        int calls = 0;
        PlainUrlText core = new(_ => { calls++; return true; });
        AssertGlobalFailure(core, Bytes(url), 1, LinkIssueCode.InvalidUrl);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("https://:SECRET@example.test/a")]
    [InlineData("https://user%3ASECRET@example.test/a")]
    [InlineData("https://@example.test:443/a")]
    public void CredentialRejectionPrecedesBothAttestationAndClassification(string url)
    {
        int calls = 0;
        PlainUrlText core = new(_ => { calls++; return true; }, _ => { calls++; return PassiveLinkKind.Media; });
        AssertGlobalFailure(core, Bytes(url), 1, LinkIssueCode.Credentials);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void ClassifierAloneCannotAttestSensitiveUrlsOrTurnThemIntoPublicIdentities()
    {
        int classified = 0;
        PlainUrlText core = new(classifier: _ => { classified++; return PassiveLinkKind.Post; });
        AssertGlobalFailure(core, Bytes("https://example.test/thread/1?opaque=SECRET"), 1, LinkIssueCode.SafetyUnknown);
        Assert.Equal(0, classified);
    }

    [Theory]
    [InlineData("https://boards.4chan.org/g/thread/123", "https://user:SECRET@boards.4chan.org/g/thread/123")]
    [InlineData("https://8kun.top/test/res/123.html", "https://user:SECRET@8kun.top/test/res/123.html")]
    public void RejectedSensitiveVariantsStayRejectedRatherThanBecomingDuplicatesOfPublicPage(string page, string credentials)
    {
        string[] rejected = [page + "?", page + "?opaque=SECRET", page + "?signature=SECRET",
            page + "/SECRET", page + "#SECRET", credentials];
        byte[] input = Bytes(string.Join("\n", rejected) + "\n" + page + "\n" + rejected[1] + "\n" + page);
        PlainUrlText core = PlainUrlText.ForSupportedChanPages();
        LinkTextResult parsed = core.Parse(input);
        Assert.Equal(new PassiveLink(7, page, PassiveLinkKind.Post), Assert.Single(parsed.Links));
        LinkIssue[] expected = [new(1, LinkIssueCode.SafetyUnknown), new(2, LinkIssueCode.SafetyUnknown),
            new(3, LinkIssueCode.SafetyUnknown), new(4, LinkIssueCode.SafetyUnknown), new(5, LinkIssueCode.SafetyUnknown),
            new(6, LinkIssueCode.Credentials), new(8, LinkIssueCode.SafetyUnknown), new(9, LinkIssueCode.Duplicate)];
        Assert.Equal(expected, parsed.Issues);
        LinkTextExport exported = core.Export(input);
        Assert.Equal(Bytes(page + "\n"), exported.Utf8);
        Assert.Equal(expected, exported.Issues);
        foreach (LinkIssue issue in parsed.Issues)
        {
            Assert.DoesNotContain("SECRET", issue.ToString());
            foreach (string url in rejected) Assert.DoesNotContain(url, issue.ToString());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChanGrammarAllowsExact256UnitIdentifiersAndRejectsEach257thUnit(bool eightKun)
    {
        string host = eightKun ? "https://8kun.top/" : "https://boards.4chan.org/";
        string board = new('a', 256);
        string thread = new('1', 256);
        string anchor = new('2', 256);
        string ThreadUrl(string b, string t, string a) => host + b + (eightKun ? "/res/" : "/thread/") + t + (eightKun ? ".html#" : "#p") + a;
        string boardUrl = host + board + "/";
        string page = ThreadUrl(board, thread, anchor);
        PlainUrlText core = PlainUrlText.ForSupportedChanPages();
        byte[] input = Bytes(boardUrl + "\r\n" + page);
        LinkTextResult parsed = core.Parse(input);
        Assert.Equal(new[] { new PassiveLink(1, boardUrl, PassiveLinkKind.Source), new PassiveLink(2, page, PassiveLinkKind.Post) }, parsed.Links);
        Assert.Empty(parsed.Issues);
        Assert.All(parsed.Links, link => Assert.True(link.RequiresExplicitConfirmation));
        Assert.Equal(Bytes(boardUrl + "\n" + page + "\n"), core.Export(input).Utf8);
        foreach (string url in new[] { host + board + "a/", ThreadUrl(board, thread + "1", anchor), ThreadUrl(board, thread, anchor + "2") })
            AssertGlobalFailure(core, Bytes(url), 1, LinkIssueCode.SafetyUnknown);
    }

    [Fact]
    public void NumericAnchorsAndLeadingZerosAreInertOriginalSpellingsNotPostExistenceProof()
    {
        string[] urls = ["https://boards.4chan.org/g/thread/1", "https://boards.4chan.org/g/thread/01",
            "https://boards.4chan.org/g/thread/1#p2", "https://boards.4chan.org/g/thread/1#p02",
            "https://8kun.top/test/res/1.html", "https://8kun.top/test/res/01.html",
            "https://8kun.top/test/res/1.html#2", "https://8kun.top/test/res/1.html#02"];
        PlainUrlText core = PlainUrlText.ForSupportedChanPages();
        byte[] input = Bytes(string.Join("\n", urls));
        LinkTextResult parsed = core.Parse(input);
        Assert.Equal(urls, parsed.Links.Select(link => link.Text));
        Assert.Empty(parsed.Issues);
        Assert.All(parsed.Links, link => { Assert.Equal(PassiveLinkKind.Post, link.Kind); Assert.True(link.RequiresExplicitConfirmation); });
        Assert.Equal(Bytes(string.Join("\n", urls) + "\n"), core.Export(input).Utf8);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(-1)]
    [InlineData(999)]
    public void EveryClassifierKindRemainsPassiveAndInvalidEnumFallsBackWithoutAuthority(int classifiedKind)
    {
        int classifications = 0;
        PlainUrlText core = new(value => value == First, _ => { classifications++; return (PassiveLinkKind)classifiedKind; });
        byte[] input = Bytes(First + "\n" + First);
        LinkTextResult parsed = core.Parse(input);
        PassiveLink link = Assert.Single(parsed.Links);
        Assert.Equal(Enum.IsDefined((PassiveLinkKind)classifiedKind) ? (PassiveLinkKind)classifiedKind : PassiveLinkKind.Unknown, link.Kind);
        Assert.True(link.RequiresExplicitConfirmation);
        Assert.Equal(1, classifications); // Duplicate is not classified a second time.
        Assert.Equal(new[] { new LinkIssue(2, LinkIssueCode.Duplicate) }, parsed.Issues);
        Assert.Equal(Bytes(First + "\n"), core.Export(input).Utf8);
    }

    [Fact]
    public void ExportReattestsOriginalSelectionRatherThanReusingPriorParseAsConsent()
    {
        bool allow = true;
        int classified = 0;
        PlainUrlText core = new(value => allow && value == First, _ => { classified++; return PassiveLinkKind.Post; });
        byte[] input = Bytes(First);
        PassiveLink earlier = Assert.Single(core.Parse(input).Links);
        allow = false;
        LinkTextExport exported = core.Export(input);
        Assert.Empty(exported.Utf8);
        Assert.Equal(new[] { new LinkIssue(1, LinkIssueCode.SafetyUnknown) }, exported.Issues);
        Assert.Equal(1, classified);
        Assert.Equal(First, earlier.Text);
        Assert.True(earlier.RequiresExplicitConfirmation);
    }

    [Theory]
    [InlineData("autoStart=true")]
    [InlineData("admissionToken=SECRET")]
    [InlineData("{\"kind\":\"Post\",\"confirmed\":true,\"token\":\"SECRET\"}")]
    [InlineData("# confirmed")]
    public void ForeignDirectiveAndTokenTextCannotConfirmTheFollowingCanonicalLink(string directive)
    {
        const string url = "https://8kun.top/test/res/123.html#456";
        byte[] input = Bytes(directive + "\n" + url);
        PlainUrlText core = PlainUrlText.ForSupportedChanPages();
        LinkTextResult parsed = core.Parse(input);
        PassiveLink link = Assert.Single(parsed.Links);
        Assert.Equal(new PassiveLink(2, url, PassiveLinkKind.Post), link);
        Assert.True(link.RequiresExplicitConfirmation);
        Assert.Equal(new[] { new LinkIssue(1, LinkIssueCode.InvalidUrl) }, parsed.Issues);
        Assert.Equal(Bytes(url + "\n"), core.Export(input).Utf8);
    }

    [Fact]
    public void InterchangeResultsExposeOnlyDataAndConfirmationRequirementNotAdmissionTokens()
    {
        // Pin the existing result surface, not a host/store admission or execution test.
        static (string, Type)[] Properties(Type type) => type
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(property => property.GetMethod is { IsPublic: true } or { IsAssembly: true })
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => (property.Name, property.PropertyType)).ToArray();
        Assert.Equal(new (string, Type)[] { ("Kind", typeof(PassiveLinkKind)), ("Line", typeof(int)),
            ("RequiresExplicitConfirmation", typeof(bool)), ("Text", typeof(string)) }, Properties(typeof(PassiveLink)));
        Assert.Equal(new (string, Type)[] { ("Issues", typeof(IReadOnlyList<LinkIssue>)),
            ("Links", typeof(IReadOnlyList<PassiveLink>)) }, Properties(typeof(LinkTextResult)));
        Assert.Equal(new (string, Type)[] { ("Issues", typeof(IReadOnlyList<LinkIssue>)),
            ("Utf8", typeof(byte[])) }, Properties(typeof(LinkTextExport)));
        Assert.Equal(new (string, Type)[] { ("Code", typeof(LinkIssueCode)), ("Line", typeof(int)) }, Properties(typeof(LinkIssue)));
    }
}

using System.Text;
using System.Text.RegularExpressions;
using MeowChan.PluginSdk;

namespace MeowChan.PluginSdk.Tests;

public sealed class RouteIdentifierValidationTests
{
    [Fact]
    public void LegacyJsonAndConstructorKeepOpaqueIdentifiersAndTheExistingDeconstruction()
    {
        UrlRoute route = SdkJson.Deserialize<UrlRoute>(Encoding.UTF8.GetBytes("""
            {"kind":"Thread","host":"source.test","pathTemplate":"/{board}/thread/{thread}","isCanonical":true}
            """));
        (SourceResourceKind kind, string host, string path, bool canonical, bool slash, bool title, bool lowercase) = route;
        Assert.Equal(RouteIdentifierKind.PortableSegment, route.BoardIdentifierKind);
        Assert.Equal(RouteIdentifierKind.PortableSegment, route.ThreadIdentifierKind);
        Assert.Equal(route, new UrlRoute(kind, host, path, canonical, slash, title, lowercase));
        Assert.Empty(ValidateSnapshot(route, "board-name", "opaque/id"));
    }

    [Fact]
    public void OptionalIdentifierAlphabetsRoundTripWithoutChangingCanonicalAliases()
    {
        UrlRoute route = StrictRoute() with { AllowTitleSegment = true, LowercaseBoardId = true };
        UrlRoute copy = SdkJson.Deserialize<UrlRoute>(SdkJson.Serialize(route));
        Assert.Equal(route, copy);
        Assert.Empty(ValidateSnapshot(copy, "Board_9", "000123"));
    }

    [Theory]
    [InlineData("Board_9", "000123", true)]
    [InlineData("b", "abc", false)]
    [InlineData("board-name", "123", false)]
    [InlineData("board/name", "123", false)]
    [InlineData("café", "123", false)]
    [InlineData("b", "١٢٣", false)]
    [InlineData("b", "１２３", false)]
    [InlineData("b", "+123", false)]
    [InlineData("b", "1.0", false)]
    [InlineData("b", "%31", false)]
    public void DeclaredAlphabetsApplyToTheIdentityWithoutLiteralAffixes(string board, string thread, bool valid)
    {
        IReadOnlyList<ValidationIssue> issues = ValidateSnapshot(StrictRoute(), board, thread);
        Assert.Equal(valid, issues.Count == 0);
        if (!valid)
        {
            Assert.Contains(issues, issue => issue.Code == "invalid_route_identity");
            Assert.Contains(issues, issue => issue.Code == "noncanonical_url");
        }
    }

    [Fact]
    public void DecimalGrammarKeepsLeadingZeroesAndDoesNotParseIntoMachineIntegers()
    {
        Assert.Empty(ValidateSnapshot(StrictRoute(), "b", new string('9', SdkLimits.MaximumIdentifierLength)));
        SourceThreadId id = new SourceThreadId("strict", "b", "000123");
        SourceDescription source = Source("strict", StrictRoute());
        ThreadReadResult wrong = new ThreadSnapshot(TestContracts.Snapshot() with
        {
            Thread = id, Url = new Uri("https://source.test/b/res/123.html")
        });
        Assert.Contains(SdkValidation.ValidateThreadResult(new ThreadReadRequest(id), wrong, source),
            issue => issue.Code == "noncanonical_url");
    }

    [Fact]
    public void InvalidRequestedIdentitiesAreRejectedEvenWithoutSnapshotUrls()
    {
        SourceDescription threadSource = Source("strict", StrictRoute());
        SourceThreadId thread = new SourceThreadId("strict", "b", "abc");
        Assert.Contains(SdkValidation.ValidateThreadResult(new ThreadReadRequest(thread), new ThreadNotFound(thread, 404), threadSource),
            issue => issue.Path == "request.thread.threadId" && issue.Code == "invalid_route_identity");
        UrlRoute boardRoute = new UrlRoute(SourceResourceKind.Board, "source.test", "/{board}/", true)
            { BoardIdentifierKind = RouteIdentifierKind.AsciiWord };
        SourceBoardId board = new SourceBoardId("strict", "board-name");
        CatalogReadResult result = new CatalogSnapshot(new SourceCatalogSnapshot(board, [], true));
        Assert.Contains(SdkValidation.ValidateCatalogResult(new CatalogReadRequest(board), result, Source("strict", boardRoute)),
            issue => issue.Path == "request.board.boardId" && issue.Code == "invalid_route_identity");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnknownIdentifierKindsAreDescriptionErrors(bool board)
    {
        UrlRoute route = board ? StrictRoute() with { BoardIdentifierKind = (RouteIdentifierKind)99 }
            : StrictRoute() with { ThreadIdentifierKind = (RouteIdentifierKind)99 };
        Assert.Contains(SdkValidation.ValidateDescription(Plugin("strict", route)), issue => issue.Code == "unknown_value");
    }

    [Theory]
    [InlineData(RouteIdentifierKind.Decimal, "latest", false)]
    [InlineData(RouteIdentifierKind.Decimal, "123", true)]
    [InlineData(RouteIdentifierKind.Decimal, "%31%32%33", true)]
    [InlineData(RouteIdentifierKind.AsciiWord, "latest", true)]
    [InlineData(RouteIdentifierKind.AsciiWord, "latest-news", false)]
    [InlineData(RouteIdentifierKind.PortableSegment, "latest-news", true)]
    public void LiteralRoutesConflictOnlyWithAllowedVariableIdentities(RouteIdentifierKind kind, string literal, bool conflict)
    {
        UrlRoute thread = new UrlRoute(SourceResourceKind.Thread, "source.test", "/{board}/thread/{thread}", true)
            { ThreadIdentifierKind = kind };
        UrlRoute board = new UrlRoute(SourceResourceKind.Board, "source.test", "/{board}/thread/" + literal, true);
        AssertConflict(thread, board, conflict);
    }

    [Theory]
    [InlineData(RouteIdentifierKind.AsciiWord, false)]
    [InlineData(RouteIdentifierKind.PortableSegment, true)]
    public void BoardAlphabetAlsoParticipatesInRouteConflicts(RouteIdentifierKind kind, bool conflict)
    {
        UrlRoute first = new UrlRoute(SourceResourceKind.Thread, "source.test", "/{board}/thread/{thread}", true)
            { BoardIdentifierKind = kind };
        UrlRoute second = new UrlRoute(SourceResourceKind.Thread, "source.test", "/prefix-{board}/thread/{thread}", true);
        AssertConflict(first, second, conflict);
    }

    [Fact]
    public void VariableAffixIntersectionMatchesAnIndependentFiniteLanguageOracle()
    {
        string[] templates = ["{thread}", "a{thread}", "1{thread}", "-{thread}", "{thread}a", "{thread}0", "{thread}ab", "1a{thread}"];
        List<string> words = new List<string>();
        List<string> previous = [""];
        for (int length = 1; length <= 4; length++)
        {
            previous = previous.SelectMany(prefix => "01ab-".Select(character => prefix + character)).ToList();
            words.AddRange(previous);
        }
        List<(UrlRoute Route, HashSet<string> Words)> languages = new();
        foreach (string template in templates)
        foreach (RouteIdentifierKind kind in Enum.GetValues<RouteIdentifierKind>())
        {
            string alphabet = kind switch { RouteIdentifierKind.Decimal => "[01]+", RouteIdentifierKind.AsciiWord => "[01ab]+", _ => "[01ab-]+" };
            Regex oracle = new Regex("^" + Regex.Escape(template).Replace("\\{thread}", alphabet, StringComparison.Ordinal) + "$");
            UrlRoute route = new UrlRoute(SourceResourceKind.Thread, "source.test", "/{board}/thread/" + template, true)
                { ThreadIdentifierKind = kind };
            languages.Add((route, words.Where(word => oracle.IsMatch(word)).ToHashSet(StringComparer.Ordinal)));
        }
        // These affixes have at most two prefix and two suffix characters; any
        // intersection has a witness of length <= 4, without unbounded sampling.
        foreach ((UrlRoute first, HashSet<string> firstWords) in languages)
        foreach ((UrlRoute second, HashSet<string> secondWords) in languages)
            AssertConflict(first, second, firstWords.Overlaps(secondWords));
    }

    private static UrlRoute StrictRoute() => new UrlRoute(SourceResourceKind.Thread, "source.test", "/{board}/res/{thread}.html", true)
        { BoardIdentifierKind = RouteIdentifierKind.AsciiWord, ThreadIdentifierKind = RouteIdentifierKind.Decimal };

    private static SourceDescription Source(string id, UrlRoute route) => TestContracts.Source(id) with
    {
        Capabilities = route.Kind == SourceResourceKind.Thread ? SourceCapabilities.ThreadRead | SourceCapabilities.PostCapture : SourceCapabilities.BoardCatalog,
        Routes = [route]
    };

    private static PluginDescription Plugin(string id, UrlRoute route) => TestContracts.Plugin(Source(id, route)) with { PluginId = id + ".plugin" };

    private static IReadOnlyList<ValidationIssue> ValidateSnapshot(UrlRoute route, string board, string thread)
    {
        SourceThreadId id = new SourceThreadId("strict", board, thread);
        string path = route.PathTemplate.Replace("{board}", Uri.EscapeDataString(route.LowercaseBoardId ? board.ToLowerInvariant() : board), StringComparison.Ordinal)
            .Replace("{thread}", Uri.EscapeDataString(thread), StringComparison.Ordinal);
        SourceThreadSnapshot snapshot = TestContracts.Snapshot() with { Thread = id, Url = new Uri("https://source.test" + path) };
        return SdkValidation.ValidateThreadResult(new ThreadReadRequest(id), new ThreadSnapshot(snapshot), Source("strict", route));
    }

    private static void AssertConflict(UrlRoute first, UrlRoute second, bool conflict)
    {
        IReadOnlyList<ValidationIssue> issues = SdkValidation.ValidateDescriptions([Plugin("first", first), Plugin("second", second)]);
        Assert.DoesNotContain(issues, issue => issue.Code != "route_conflict");
        Assert.True(conflict == issues.Any(issue => issue.Code == "route_conflict"),
            $"{first.PathTemplate}/{first.ThreadIdentifierKind} versus {second.PathTemplate}/{second.ThreadIdentifierKind}: expected conflict={conflict}.");
    }
}

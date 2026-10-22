using MeowChan.PluginSdk;

namespace MeowChan.PluginSdk.Tests;

public sealed class DescriptionValidationTests
{
    [Fact]
    public void IndependentSourceWithAnEmptySettingsSchemaIsValid()
        => Assert.Empty(SdkValidation.ValidateDescription(TestContracts.Plugin()));

    [Fact]
    public void DuplicatePluginAndSourceIdentitiesRequireExplicitResolution()
    {
        IReadOnlyList<ValidationIssue> issues = SdkValidation.ValidateDescriptions([TestContracts.Plugin(), TestContracts.Plugin()]);
        Assert.Contains(issues, issue => issue.Code == "duplicate_plugin");
        Assert.Contains(issues, issue => issue.Code == "duplicate_source");
    }

    [Fact]
    public void RouteConflictsAreDetectedAcrossDifferentSourcesAndResourceKinds()
    {
        PluginDescription first = TestContracts.Plugin();
        PluginDescription second = TestContracts.Plugin(TestContracts.Source("other")) with { PluginId = "other.plugin" };
        Assert.Contains(SdkValidation.ValidateDescriptions([first, second]), issue => issue.Code == "route_conflict");
    }

    [Fact]
    public void OptionalTitleAliasesConflictWithAnotherSourcesLongerExplicitRoute()
    {
        SourceDescription firstSource = TestContracts.Source() with
        {
            Routes = [new UrlRoute(SourceResourceKind.Board, "source.test", "/{board}/", true),
                new UrlRoute(SourceResourceKind.Thread, "source.test", "/{board}/res/{thread}", true, AllowTitleSegment: true)]
        };
        SourceDescription secondSource = TestContracts.Source("other") with
        {
            Routes = [new UrlRoute(SourceResourceKind.Board, "source.test", "/{board}/catalog", true),
                new UrlRoute(SourceResourceKind.Thread, "source.test", "/{board}/res/{thread}/details", true)]
        };
        IReadOnlyList<ValidationIssue> issues = SdkValidation.ValidateDescriptions([TestContracts.Plugin(firstSource),
            TestContracts.Plugin(secondSource) with { PluginId = "other.plugin" }]);
        Assert.Contains(issues, issue => issue.Code == "route_conflict");
    }

    [Theory]
    [InlineData("/{board}/thread/{thread}", true, false, "/{board}/thread/{thread}/", false, true)]
    [InlineData("/{board}/thread/{thread}/", true, false, "/{board}/thread/{thread}", false, true)]
    [InlineData("/{board}/thread/{thread}", false, false, "/{board}/thread/{thread}/", false, false)]
    [InlineData("/{board}/thread/{thread}/", false, false, "/{board}/thread/{thread}/", false, true)]
    [InlineData("/{board}/thread/{thread}/", true, true, "/{board}/thread/{thread}/details/", false, true)]
    [InlineData("/{board}/thread/{thread}/", false, true, "/{board}/thread/{thread}/details/", false, true)]
    [InlineData("/{board}/thread/{thread}/", false, true, "/{board}/thread/{thread}/details", false, false)]
    [InlineData("/{board}/thread/{thread}", false, true, "/{board}/thread/{thread}/details/", false, false)]
    public void SlashAndTitleAliasesRespectBothAcceptedAndDisjointPaths(string firstPath, bool firstSlash,
        bool firstTitle, string secondPath, bool secondSlash, bool conflict)
    {
        PluginDescription first = RoutePlugin("first", firstPath, firstSlash, firstTitle);
        PluginDescription second = RoutePlugin("second", secondPath, secondSlash);
        Assert.Empty(SdkValidation.ValidateDescription(first));
        Assert.Empty(SdkValidation.ValidateDescription(second));
        Assert.Equal(conflict, SdkValidation.ValidateDescriptions([first, second]).Any(issue => issue.Code == "route_conflict"));
        Assert.Equal(conflict, SdkValidation.ValidateDescriptions([second, first]).Any(issue => issue.Code == "route_conflict"));
    }

    [Theory]
    [InlineData("/{board}/thread/{thread}", "/{board}/%74hread/{thread}", "/b/thread/123")]
    [InlineData("/{board}/thread/{thread}.html", "/{board}/thread/{thread}%2Ehtml", "/b/thread/123.html")]
    [InlineData("/prefix{board}/thread/{thread}", "/pre%66ix{board}/thread/{thread}", "/prefixb/thread/123")]
    [InlineData("/{board}/café/{thread}", "/{board}/caf%C3%A9/{thread}", "/b/caf%C3%A9/123")]
    public void UriEquivalentLiteralSegmentsAndAffixesConflict(string firstPath, string secondPath, string canonicalPath)
    {
        PluginDescription first = RoutePlugin("first", firstPath);
        PluginDescription second = RoutePlugin("second", secondPath);
        foreach (PluginDescription plugin in new[] { first, second })
        {
            Assert.Empty(SdkValidation.ValidateDescription(plugin));
            SourceThreadId id = new SourceThreadId(plugin.Sources[0].SourceId, "b", "123");
            SourceThreadSnapshot snapshot = TestContracts.Snapshot() with
            {
                Thread = id, Url = new Uri("https://source.test" + canonicalPath)
            };
            Assert.Empty(SdkValidation.ValidateThreadResult(new ThreadReadRequest(id), new ThreadSnapshot(snapshot), plugin.Sources[0]));
        }
        Assert.Contains(SdkValidation.ValidateDescriptions([first, second]), issue => issue.Code == "route_conflict");
    }

    [Theory]
    [InlineData("/{board}/thread/{thread}", "/{board}/%2574hread/{thread}")]
    [InlineData("/{board}/thread/{thread}", "/{board}/Thread/{thread}")]
    [InlineData("/{board}/a%2Fb/{thread}", "/{board}/a/b/{thread}")]
    [InlineData("/{board}/thread/{thread}.json", "/{board}/thread/{thread}.html")]
    public void DistinctEscapedLiteralsCaseAndSuffixesRemainDisjoint(string firstPath, string secondPath)
        => Assert.Empty(SdkValidation.ValidateDescriptions([RoutePlugin("first", firstPath), RoutePlugin("second", secondPath)]));

    [Theory]
    [InlineData("%2e")]
    [InlineData(".%2E")]
    [InlineData("%2E%2e")]
    public void EncodedLiteralDotSegmentsCannotDisappearDuringUriNormalization(string segment)
        => Assert.Contains(SdkValidation.ValidateDescription(RoutePlugin("first", "/{board}/" + segment + "/{thread}")),
            issue => issue.Code == "invalid_template");

    [Fact]
    public void ExplicitNullTemplateReportsAnIssueWithoutCrashingConflictValidation()
    {
        SourceDescription invalid = TestContracts.Source() with
        {
            Routes = [new UrlRoute(SourceResourceKind.Thread, "source.test", null!, true)]
        };
        IReadOnlyList<ValidationIssue> issues = SdkValidation.ValidateDescriptions([TestContracts.Plugin(invalid),
            TestContracts.Plugin(TestContracts.Source("other")) with { PluginId = "other.plugin" }]);
        Assert.Contains(issues, issue => issue.Code == "invalid_template");
    }

    [Fact]
    public void OversizedRouteCollectionIsRejectedWithoutEverEnumeratingIt()
    {
        SourceDescription source = TestContracts.Source() with { Routes = new CountOnlyRoutes(20000) };
        IReadOnlyList<ValidationIssue> issues = SdkValidation.ValidateDescriptions([TestContracts.Plugin(source),
            TestContracts.Plugin(TestContracts.Source("other")) with { PluginId = "other.plugin" }]);
        Assert.Contains(issues, issue => issue.Code == "too_large");
    }

    [Fact]
    public void IndividuallyValidDescriptionsCannotExceedTheCombinedRouteBudget()
    {
        List<PluginDescription> descriptions = new List<PluginDescription>();
        for (int index = 0; index < 21; index++)
        {
            string host = "source" + index + ".test";
            List<UrlRoute> routes = new List<UrlRoute>
            {
                new UrlRoute(SourceResourceKind.Board, host, "/{board}/", true),
                new UrlRoute(SourceResourceKind.Thread, host, "/{board}/thread/{thread}", true)
            };
            for (int alias = 0; alias < 48; alias++)
                routes.Add(new UrlRoute(SourceResourceKind.Thread, host, "/{board}/alias" + alias + "/{thread}"));
            PluginDescription description = TestContracts.Plugin(TestContracts.Source("source" + index, host) with { Routes = routes })
                with { PluginId = "plugin" + index };
            Assert.Empty(SdkValidation.ValidateDescription(description));
            descriptions.Add(description);
        }
        Assert.Contains(SdkValidation.ValidateDescriptions(descriptions), issue => issue.Code == "too_large");
    }

    [Fact]
    public void UnverifiedSdkVersionAndExecutableRouteSyntaxAreRejected()
    {
        PluginDescription version = TestContracts.Plugin() with { SdkVersion = "2.0.0" };
        Assert.Contains(SdkValidation.ValidateDescription(version), issue => issue.Code == "incompatible_sdk");
        SourceDescription badRoute = TestContracts.Source() with
        {
            Routes = [new UrlRoute(SourceResourceKind.Thread, "*.source.test", "/{board}/thread/{thread}?script=*", true)]
        };
        IReadOnlyList<ValidationIssue> issues = SdkValidation.ValidateDescription(TestContracts.Plugin(badRoute));
        Assert.Contains(issues, issue => issue.Code == "invalid_host");
        Assert.Contains(issues, issue => issue.Code == "invalid_template");
    }

    [Fact]
    public void InformationReferencesMustBelongToADeclaredSource()
    {
        PluginDescription plugin = TestContracts.Plugin() with
        {
            Information = [new InformationEntry("status", "Source status", InformationKind.Value, "Cached",
                AsOf: DateTimeOffset.UtcNow, SourceId: "undeclared")]
        };
        Assert.Contains(SdkValidation.ValidateDescription(plugin), issue => issue.Code == "unknown_source");
    }

    private sealed class CountOnlyRoutes(int count) : IReadOnlyList<UrlRoute>
    {
        public int Count => count;
        public UrlRoute this[int index] => throw new InvalidOperationException("Oversized routes must never be visited.");
        public IEnumerator<UrlRoute> GetEnumerator() => throw new InvalidOperationException("Oversized routes must never be enumerated.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static PluginDescription RoutePlugin(string id, string path, bool slash = true, bool title = false)
        => TestContracts.Plugin(TestContracts.Source(id) with
        {
            Capabilities = SourceCapabilities.ThreadRead | SourceCapabilities.PostCapture,
            Routes = [new UrlRoute(SourceResourceKind.Thread, "source.test", path, true, slash, title)]
        }) with { PluginId = id + ".plugin" };
}

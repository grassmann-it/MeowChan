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
}

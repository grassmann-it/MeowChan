using System.Reflection;
using System.Text;
using MeowChan.PluginSdk;

namespace MeowChan.PluginSdk.Tests;

/// <summary>The declarative booru source profile (endpoints, tag syntax, paging, access, pacing) and its validation.</summary>
public sealed class BooruSourceProfileTests
{
    // Gelbooru-engine shaped: one path for search and post, fixed API selectors, a 0-based page and a cursor term.
    internal static BooruSourceProfile EngineProfile() => new(
        [
            new(BooruEndpointKind.Search, "/index.php",
            [
                new("page", BooruParameterKind.Fixed, true, "dapi"), new("s", BooruParameterKind.Fixed, true, "post"),
                new("q", BooruParameterKind.Fixed, true, "index"), new("json", BooruParameterKind.Fixed, true, "1"),
                new("tags", BooruParameterKind.TagQuery, true), new("pid", BooruParameterKind.PageIndex),
                new("limit", BooruParameterKind.Limit)
            ]),
            new(BooruEndpointKind.Post, "/index.php",
            [
                new("page", BooruParameterKind.Fixed, true, "dapi"), new("s", BooruParameterKind.Fixed, true, "post"),
                new("q", BooruParameterKind.Fixed, true, "index"), new("json", BooruParameterKind.Fixed, true, "1"),
                new("id", BooruParameterKind.PostId, true)
            ])
        ],
        new BooruQueryRules(2, ["general", "sensitive", "questionable", "explicit"], BeforeCursorPrefix: "id:<"),
        [], [BooruRating.General, BooruRating.Sensitive, BooruRating.Questionable, BooruRating.Explicit], 5, 100);

    private static SourceDescription Source(BooruSourceProfile? profile, PostOrderKind order = PostOrderKind.DecimalPostId) => new("booru", "Synthetic booru",
        new Uri("https://booru.test/"), SourceCapabilities.PostSearch | SourceCapabilities.OriginalMedia, [],
        [new HttpHostDeclaration("booru.test", HttpHostPurpose.Metadata), new HttpHostDeclaration("original.test", HttpHostPurpose.OriginalMedia)],
        TestContracts.EmptySchema, order) { BooruProfile = profile };

    private static IReadOnlyList<ValidationIssue> Validate(BooruSourceProfile profile, PostOrderKind order = PostOrderKind.DecimalPostId)
        => SdkValidation.ValidateBooruProfile(Source(profile, order));

    [Fact]
    public void AValidProfileValidatesAloneAndInsideItsDescription()
    {
        Assert.Empty(Validate(EngineProfile()));
        Assert.Empty(SdkValidation.ValidateDescription(TestContracts.Plugin(Source(EngineProfile())) with { SdkVersion = "1.1.0" }));
    }

    [Fact]
    public void ADescriptionWithoutProfileKeepsItsExactJson()
    {
        string json = Encoding.UTF8.GetString(SdkJson.Serialize(Source(null)));
        Assert.DoesNotContain("booruProfile", json, StringComparison.Ordinal);
        SourceDescription roundTrip = SdkJson.Deserialize<SourceDescription>(Encoding.UTF8.GetBytes(json));
        Assert.Null(roundTrip.BooruProfile);
    }

    [Fact]
    public void AProfileRoundTripsThroughJson()
    {
        SourceDescription source = Source(EngineProfile());
        SourceDescription roundTrip = SdkJson.Deserialize<SourceDescription>(SdkJson.Serialize(source));
        Assert.NotNull(roundTrip.BooruProfile);
        Assert.Equal(SdkJson.Serialize(source), SdkJson.Serialize(roundTrip));
        Assert.Empty(SdkValidation.ValidateBooruProfile(roundTrip));
    }

    [Fact]
    public void TheProfileIsInitOnlyAndKeepsTheReleasedSourceConstructor()
    {
        ConstructorInfo constructor = Assert.Single(typeof(SourceDescription).GetConstructors(), c => c.GetParameters().Length == 8);
        Assert.Equal(typeof(PostOrderKind), constructor.GetParameters()[7].ParameterType);
        MethodInfo setter = typeof(SourceDescription).GetProperty(nameof(SourceDescription.BooruProfile))!.SetMethod!;
        Assert.Contains(setter.ReturnParameter.GetRequiredCustomModifiers(), modifier => modifier.FullName == "System.Runtime.CompilerServices.IsExternalInit");
    }

    [Fact]
    public void OnlyAPostSearchSourceDeclaresAProfile()
    {
        SourceDescription chan = TestContracts.Plugin().Sources[0] with { BooruProfile = EngineProfile() };
        Assert.Contains(SdkValidation.ValidateDescription(TestContracts.Plugin(chan)), issue => issue.Code == "unexpected_profile");
        Assert.Contains(SdkValidation.ValidateBooruProfile(Source(null)), issue => issue.Code == "missing");
    }

    public static TheoryData<string, Func<BooruSourceProfile, BooruSourceProfile>> InvalidProfiles() => new()
    {
        { "invalid_endpoint", p => p with { Endpoints = [p.Endpoints[0]] } },
        { "invalid_endpoint", p => p with { Endpoints = [p.Endpoints[0], p.Endpoints[0], p.Endpoints[1]] } },
        { "invalid_template", p => p with { Endpoints = [p.Endpoints[0] with { PathTemplate = "/posts/{post}.json" }, p.Endpoints[1]] } },
        { "invalid_template", p => p with { Endpoints = [p.Endpoints[0] with { PathTemplate = "/a/../index.php" }, p.Endpoints[1]] } },
        { "invalid_template", p => p with { Endpoints = [p.Endpoints[0] with { PathTemplate = "https://booru.test/index.php" }, p.Endpoints[1]] } },
        { "invalid_template", p => p with { Endpoints = [p.Endpoints[0] with { PathTemplate = "/index.php?x=1" }, p.Endpoints[1]] } },
        { "invalid_name", p => p with { Endpoints = [p.Endpoints[0] with { Parameters = [.. p.Endpoints[0].Parameters, new("API_KEY", BooruParameterKind.Limit)] }, p.Endpoints[1]] } },
        { "duplicate_name", p => p with { Endpoints = [p.Endpoints[0] with { Parameters = [.. p.Endpoints[0].Parameters, new("tags", BooruParameterKind.Limit)] }, p.Endpoints[1]] } },
        { "invalid_value", p => p with { Endpoints = [p.Endpoints[0] with { Parameters = [.. p.Endpoints[0].Parameters, new("mode", BooruParameterKind.Fixed)] }, p.Endpoints[1]] } },
        { "invalid_value", p => p with { Endpoints = [p.Endpoints[0] with { Parameters = [.. p.Endpoints[0].Parameters, new("mode", BooruParameterKind.Fixed, FixedValue: "a&b=c")] }, p.Endpoints[1]] } },
        { "unexpected_value", p => p with { Endpoints = [p.Endpoints[0] with { Parameters = [.. p.Endpoints[0].Parameters, new("mode", BooruParameterKind.Limit, FixedValue: "1")] }, p.Endpoints[1]] } },
        { "invalid_endpoint", p => p with { Endpoints = [p.Endpoints[0] with { Parameters = [.. p.Endpoints[0].Parameters, new("id", BooruParameterKind.PostId)] }, p.Endpoints[1]] } },
        { "invalid_endpoint", p => p with { Endpoints = [p.Endpoints[0], p.Endpoints[1] with { Parameters = [.. p.Endpoints[1].Parameters, new("tags", BooruParameterKind.TagQuery)] }] } },
        { "invalid_endpoint", p => p with { Endpoints = [p.Endpoints[0], p.Endpoints[1] with { PathTemplate = "/posts/{post}.json" }] } },
        { "invalid_limit", p => p with { MaximumLimit = 101 } },
        { "invalid_page", p => p with { MaximumPage = 0 } },
        { "invalid_spacing", p => p with { MinimumRequestSpacingMilliseconds = -1 } },
        { "invalid_limit", p => p with { Query = p.Query with { MaximumTagTerms = 9 } } },
        { "invalid_value", p => p with { Query = p.Query with { RatingValues = ["Safe"] } } },
        { "invalid_value", p => p with { Query = p.Query with { BeforeCursorPrefix = "rating:" } } },
        { "invalid_value", p => p with { Query = p.Query with { BeforeCursorPrefix = "id<" } } },
        { "duplicate_value", p => p with { Ratings = [BooruRating.General, BooruRating.General] } },
        { "unknown_value", p => p with { Authentication = (BooruAuthentication)1 } },
        { "ambiguous_cursor", p => p with { Endpoints = [p.Endpoints[0] with { Parameters = [.. p.Endpoints[0].Parameters.Where(q => q.Name != "pid"), new("pid", BooruParameterKind.PageOrBeforeCursor)] }, p.Endpoints[1]] } },
        { "cursor_unsupported", p => p with { Query = p.Query with { BeforeCursorPrefix = null } } }
    };

    [Theory]
    [MemberData(nameof(InvalidProfiles))]
    public void InvalidProfilesAreRefused(string code, Func<BooruSourceProfile, BooruSourceProfile> change)
        => Assert.Contains(Validate(change(EngineProfile())), issue => issue.Code == code);

    [Fact]
    public void AnOpaqueOrderSourceNeedsNoCursor()
        => Assert.Empty(Validate(EngineProfile() with { Query = EngineProfile().Query with { BeforeCursorPrefix = null } }, PostOrderKind.StableOrdinal));
}

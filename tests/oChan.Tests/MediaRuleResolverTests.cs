using oChan.Rules;

namespace oChan.Tests;

public sealed class MediaRuleResolverTests
{
    private static readonly ResourceId Target = new ResourceId("4chan", "g", "123");

    [Fact]
    public void OverridesResolvePerFieldWithIndependentOrigins()
    {
        MediaRuleScope source = MediaRuleScope.ForSource("4chan");
        MediaRuleScope board = MediaRuleScope.ForBoard("4chan", "g");
        MediaRuleScope thread = MediaRuleScope.ForThread("4chan", "g", "123");
        MediaRuleResolver resolver = Resolver(
            new MediaRule(MediaRuleScope.Global, types: Types("jpg", "webm"), sizeLimit: Limit(100)),
            new MediaRule(source, sizeLimit: Limit(80)),
            new MediaRule(board, types: Types("jpg")),
            new MediaRule(thread, sizeLimit: MediaSizeLimit.Unlimited));

        EffectiveMediaRule effective = resolver.Resolve(Target);

        Assert.Equal(new[] { ".jpg" }, effective.Types.Extensions);
        Assert.Equal(board, effective.TypesOrigin.Scope);
        Assert.Equal(MediaSizeLimitMode.Unlimited, effective.SizeLimit.Mode);
        Assert.Equal(thread, effective.SizeLimitOrigin.Scope);
        Assert.Equal("fixture", effective.Revision);
        Assert.Equal("fixture", effective.TypesOrigin.Revision);
        Assert.Equal(MediaSelectionOutcome.Selected, resolver.Evaluate(Target, "JPG", 1000).Outcome);
        Assert.Equal(MediaSelectionReason.TypeExcluded, resolver.Evaluate(Target, "webm", 1).Reason);
        Assert.Equal(source, resolver.Resolve(MediaRuleScope.ForBoard("4chan", "a")).SizeLimitOrigin.Scope);
    }

    [Fact]
    public void DisabledRulesFallBackAndInheritDoesNotOverride()
    {
        MediaRuleScope board = MediaRuleScope.ForBoard("4chan", "g");
        MediaRuleResolver resolver = Resolver(
            new MediaRule(MediaRuleScope.Global, types: Types("png"), sizeLimit: Limit(100)),
            new MediaRule(MediaRuleScope.ForSource("4chan"), enabled: false, types: Types(), sizeLimit: Limit(0)),
            new MediaRule(board));

        EffectiveMediaRule effective = resolver.Resolve(Target);

        Assert.Equal(MediaRuleScope.Global, effective.TypesOrigin.Scope);
        Assert.Equal(MediaRuleScope.Global, effective.SizeLimitOrigin.Scope);
        Assert.Equal(MediaSelectionOutcome.Selected, resolver.Evaluate(Target, ".png", 100).Outcome);
        Assert.Equal(MediaSelectionReason.SizeExceeded, resolver.Evaluate(Target, ".png", 101).Reason);
    }

    [Fact]
    public void EmptySelectionExcludesEverythingButExplicitAllOverridesIt()
    {
        MediaRuleResolver resolver = Resolver(
            new MediaRule(MediaRuleScope.Global, types: Types()),
            new MediaRule(MediaRuleScope.ForBoard("4chan", "g"), types: MediaTypeSelection.All));

        Assert.Equal(MediaSelectionOutcome.Selected, resolver.Evaluate(Target, null, 100).Outcome);
        Assert.Equal(MediaSelectionReason.TypeExcluded, resolver.Evaluate(new ResourceId("4chan", "a", "1"), "jpg", 1).Reason);
        Assert.Equal(MediaSelectionReason.TypeExcluded, resolver.Evaluate(new ResourceId("4chan", "a", "1"), null, 1).Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingExtensionOnlyMatchesAll(string? extension)
    {
        Assert.Equal(MediaSelectionOutcome.Selected, new MediaRuleResolver(MediaRuleSet.Default).Evaluate(Target, extension, 1).Outcome);
        Assert.Equal(MediaSelectionReason.TypeExcluded, Resolver(new MediaRule(MediaRuleScope.Global, types: Types("jpg")))
            .Evaluate(Target, extension, 1).Reason);
    }

    [Fact]
    public void UnknownSizeIsAllowedAndReportedDespiteSizeLimit()
    {
        MediaRuleResolver resolver = Resolver(new MediaRule(MediaRuleScope.Global, sizeLimit: Limit(0)));

        MediaSelectionResult result = resolver.Evaluate(Target, ".webm", null);

        Assert.Equal(MediaSelectionOutcome.Selected, result.Outcome);
        Assert.Equal(MediaSelectionReason.AllowedUnknownSize, result.Reason);
        Assert.Null(result.SizeBytes);
        Assert.Equal(0, result.EffectiveRule.SizeLimit.Bytes);
        Assert.Equal(MediaSelectionOutcome.Selected, resolver.Evaluate(Target, ".webm", 0).Outcome);
        Assert.Equal(MediaSelectionReason.SizeExceeded, resolver.Evaluate(Target, ".webm", 1).Reason);
        Assert.Throws<ArgumentOutOfRangeException>(() => resolver.Evaluate(Target, ".jpg", -1));
    }

    [Fact]
    public void ExtensionsAreNormalizedAndSnapshotsDefensivelyOwnTheirCollections()
    {
        string[] input = new[] { " JPG ", ".jpg", "PnG" };
        MediaTypeSelection selection = new MediaTypeSelection(MediaTypeSelectionMode.Selected, input);
        MediaRule[] rules = new[] { new MediaRule(MediaRuleScope.Global, types: selection) };
        MediaRuleSet set = new MediaRuleSet("first", rules);
        input[0] = "webm";
        rules[0] = new MediaRule(MediaRuleScope.Global, types: MediaTypeSelection.All);

        Assert.Equal(new[] { ".jpg", ".png" }, set.Rules[0].Types.Extensions);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)selection.Extensions).Add(".webm"));
        Assert.Throws<NotSupportedException>(() => ((IList<MediaRule>)set.Rules).Clear());
    }

    [Theory]
    [InlineData("*.jpg")]
    [InlineData("image/jpeg")]
    [InlineData("file.jpg")]
    [InlineData("../jpg")]
    [InlineData("..jpg")]
    [InlineData(".")]
    public void InvalidExtensionDoesNotSilentlyBecomeARule(string extension)
    {
        Assert.Throws<ArgumentException>(() => Types(extension));
    }

    [Fact]
    public void SourceAndBoardScopesDoNotLeakAcrossIdentities()
    {
        MediaRuleResolver resolver = Resolver(new MediaRule(MediaRuleScope.ForBoard("4chan", "g"), types: Types()));

        Assert.Equal(MediaSelectionOutcome.Excluded, resolver.Evaluate(Target, "jpg", 1).Outcome);
        Assert.Equal(MediaSelectionOutcome.Selected, resolver.Evaluate(new ResourceId("8kun", "g", "123"), "jpg", 1).Outcome);
        Assert.Equal(MediaSelectionOutcome.Selected, resolver.Evaluate(new ResourceId("4chan", "a", "123"), "jpg", 1).Outcome);
    }

    [Fact]
    public void GlobalAndSourcePreviewsNeedNoInventedBoard()
    {
        MediaRuleResolver resolver = Resolver(
            new MediaRule(MediaRuleScope.Global, types: Types("jpg")),
            new MediaRule(MediaRuleScope.ForSource("4chan"), types: Types("png")));

        Assert.Equal(new[] { ".jpg" }, resolver.Resolve(MediaRuleScope.Global).Types.Extensions);
        Assert.Equal(new[] { ".png" }, resolver.Resolve(MediaRuleScope.ForSource("4chan")).Types.Extensions);
    }

    [Fact]
    public void InvalidScopesDuplicateScopesAndContradictoryFieldsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => MediaRuleScope.ForThread("4chan", "g", ""));
        Assert.Throws<ArgumentException>(() => new MediaRuleScope(MediaRuleScopeKind.Global, "4chan"));
        Assert.Throws<ArgumentException>(() => new MediaTypeSelection(MediaTypeSelectionMode.All, new[] { "jpg" }));
        Assert.Throws<ArgumentException>(() => new MediaSizeLimit(MediaSizeLimitMode.Unlimited, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => Limit(-1));
        Assert.Throws<ArgumentException>(() => Resolver(new MediaRule(MediaRuleScope.Global), new MediaRule(MediaRuleScope.Global)));
    }

    private static MediaRuleResolver Resolver(params MediaRule[] rules) => new MediaRuleResolver(new MediaRuleSet("fixture", rules));
    private static MediaTypeSelection Types(params string[] extensions) => new MediaTypeSelection(MediaTypeSelectionMode.Selected, extensions);
    private static MediaSizeLimit Limit(long bytes) => new MediaSizeLimit(MediaSizeLimitMode.Limited, bytes);
}

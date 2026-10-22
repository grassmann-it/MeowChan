using MeowChan.PluginSdk;

namespace MeowChan.PluginSdk.Tests;

public sealed class HostAndFilterValidationTests
{
    [Fact]
    public void MetadataHttpRejectsUndeclaredRedirectHostsCredentialHeadersAndWrongMethods()
    {
        HostHttpRequest request = new HostHttpRequest("synthetic", (HostHttpMethod)99,
            new Uri("https://source.test/b/thread/123.json"), new Dictionary<string, string> { ["Cookie"] = "private" });
        IReadOnlyList<ValidationIssue> requestIssues = SdkValidation.ValidateHttpRequest(request, TestContracts.Source());
        Assert.Contains(requestIssues, issue => issue.Code == "header_not_allowed");
        Assert.Contains(requestIssues, issue => issue.Code == "unknown_value");
        HostHttpResult redirect = new HostHttpResponse(200, new Uri("https://unapproved.test/thread"),
            new Dictionary<string, string>(), Array.Empty<byte>());
        Assert.Contains(SdkValidation.ValidateHttpResult(request with { Method = HostHttpMethod.Get, Headers = null },
            redirect, TestContracts.Source()), issue => issue.Code == "host_not_allowed");
    }

    [Fact]
    public void DecompressedMetadataLimitAndHeadOr304BodyRulesAreEnforced()
    {
        HostHttpRequest request = new HostHttpRequest("synthetic", HostHttpMethod.Get, TestContracts.ThreadUrl);
        HostHttpResponse oversized = new HostHttpResponse(200, TestContracts.ThreadUrl,
            new Dictionary<string, string>(), new byte[SdkLimits.MaximumMetadataBytes + 1]);
        Assert.Contains(SdkValidation.ValidateHttpResult(request, oversized, TestContracts.Source()), issue => issue.Code == "too_large");
        Assert.Empty(SdkValidation.ValidateHttpResult(request, oversized with { Body = new byte[SdkLimits.MaximumMetadataBytes] }, TestContracts.Source()));
        HostHttpResponse invalid304 = oversized with { StatusCode = 304, Body = [1] };
        Assert.Contains(SdkValidation.ValidateHttpResult(request, invalid304, TestContracts.Source()), issue => issue.Code == "unexpected_body");
        Assert.Contains(SdkValidation.ValidateHttpResult(request with { Method = HostHttpMethod.Head },
            invalid304 with { StatusCode = 200 }, TestContracts.Source()), issue => issue.Code == "unexpected_body");
    }

    [Fact]
    public void MediaHostPermissionDoesNotGrantMetadataHttpPermission()
    {
        SourceDescription source = TestContracts.Source() with
        {
            HttpHosts = [new HttpHostDeclaration("source.test", HttpHostPurpose.OriginalMedia)]
        };
        Assert.Contains(SdkValidation.ValidateHttpRequest(new HostHttpRequest("synthetic", HostHttpMethod.Get,
            TestContracts.ThreadUrl), source), issue => issue.Code == "host_not_allowed");
    }

    [Fact]
    public void FilterRequestRoundTripRetainsActualByteIdentityRuleAndGrantRevisions()
    {
        ImageFilterRequest original = TestContracts.FilterRequest();
        ImageFilterRequest copy = SdkJson.Deserialize<ImageFilterRequest>(SdkJson.Serialize(original));
        Assert.Empty(SdkValidation.ValidateImageFilterRequest(copy, TestContracts.Filter()));
        Assert.Equal(original.Candidate, copy.Candidate);
        Assert.Equal(original.OriginalSha256, copy.OriginalSha256);
        Assert.Equal(original.GrantRevision, copy.GrantRevision);
        Assert.Equal(original.EffectiveRuleRevision, copy.EffectiveRuleRevision);
        Assert.Equal(original.AssetToken, copy.AssetToken);
    }

    [Fact]
    public void FilterCannotReceiveUnboundedBytesOrLoseRevisionAndGrantIdentity()
    {
        ImageFilterRequest invalid = TestContracts.FilterRequest() with
        {
            OriginalSize = SdkLimits.MaximumImageBytes + 1, OriginalSha256 = "not-a-hash", GrantRevision = "",
            InputRevision = "", FilterKey = "other"
        };
        IReadOnlyList<ValidationIssue> issues = SdkValidation.ValidateImageFilterRequest(invalid, TestContracts.Filter());
        Assert.Contains(issues, issue => issue.Code == "image_size_limit");
        Assert.Contains(issues, issue => issue.Code == "invalid_hash");
        Assert.Contains(issues, issue => issue.Code == "invalid_identity" && issue.Path == "request.grantRevision");
        Assert.Contains(issues, issue => issue.Code == "wrong_filter");
    }

    [Theory]
    [InlineData(ImageFilterOutcome.Match)]
    [InlineData(ImageFilterOutcome.NoMatch)]
    [InlineData(ImageFilterOutcome.Unavailable)]
    [InlineData(ImageFilterOutcome.Timeout)]
    [InlineData(ImageFilterOutcome.Error)]
    public void TechnicalOmissionsRemainDifferentFromANegativeRuleDecision(ImageFilterOutcome outcome)
    {
        ImageFilterResult result = SdkJson.Deserialize<ImageFilterResult>(SdkJson.Serialize(new ImageFilterResult(outcome, "explained")));
        Assert.Equal(outcome, result.Outcome);
        Assert.Empty(SdkValidation.ValidateImageFilterResult(result));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(0, 65537)]
    [InlineData(101, 1)]
    public void ImageReadIsBoundedToTheLeaseAndChunkBudget(long offset, int count)
        => Assert.NotEmpty(SdkValidation.ValidateImageRead("asset-token", offset, count, 100));

    [Fact]
    public void ImageReadAtTheEndAndFullSizeChunksAreValid()
    {
        Assert.Empty(SdkValidation.ValidateImageRead("asset-token", 100, 1, 100));
        Assert.Empty(SdkValidation.ValidateImageRead("asset-token", 0, SdkLimits.MaximumImageChunkBytes, SdkLimits.MaximumImageBytes));
    }
}

using System.Text.Json.Nodes;
using MeowChan.PluginSdk;
using MeowChan.Plugins.Examples.Uboachan;

namespace MeowChan.ExamplePlugins.Tests;

public sealed class UboachanPluginTests
{
    private const string Thread = """
        {"posts":[
         {"no":123,"resto":0,"time":1700000000,"sub":"Synthetic subject","name":"Synthetic author","com":"<p>Inert text</p>",
          "tim":"file-0","ext":".png","filename":"","fsize":2187,"md5":"2ekqYfL9A3THOfl+KA127w==",
          "extra_files":[{"tim":"file-1","ext":".webm","filename":"video","fsize":100,"md5":"AAAAAAAAAAAAAAAAAAAAAA=="}]},
         {"no":124,"resto":123,"com":""},
         {"no":125,"resto":123,"extra_files":[{"tim":"extra-only","ext":".jpg","md5":"AAAAAAAAAAAAAAAAAAAAAA=="}]}]}
        """;
    private static MakabaUboachanHarness Harness(string body = Thread, int status = 200) => new(new UboachanPlugin(), body, status);

    [Fact]
    public void PassiveDescriptionHasDistinctIdentityAndOnlyItsHost()
    {
        var description = new UboachanPlugin().Describe(); var source = Assert.Single(description.Sources);
        Assert.Equal("meowchan.source.uboachan", description.PluginId); Assert.Equal("uboachan", source.SourceId);
        Assert.Empty(SdkValidation.ValidateDescription(description)); Assert.Equal(PostOrderKind.DecimalPostId, source.PostOrder);
        Assert.Equal(new HttpHostDeclaration("uboachan.net", HttpHostPurpose.Metadata | HttpHostPurpose.OriginalMedia), Assert.Single(source.HttpHosts));
        Assert.Single(typeof(UboachanPlugin).Assembly.GetExportedTypes(), t => typeof(IPlugin).IsAssignableFrom(t));
        Assert.DoesNotContain(typeof(UboachanPlugin).Assembly.GetReferencedAssemblies(), a => a.Name is "libmeowchan" or "MeowChan.PluginHost" or "Avalonia");
    }

    [Fact]
    public async Task MainAndExtraFilesKeepStableSlotsIdsAndMeasuredExpectations()
    {
        var h = Harness(); var result = Assert.IsType<ThreadSnapshot>(await h.ReadThread());
        Assert.Empty(SdkValidation.ValidateThreadResult(h.ThreadRequest, result, h.Source));
        Assert.Empty(SdkValidation.ValidateThreadResult(h.ThreadRequest, SdkJson.Deserialize<ThreadReadResult>(SdkJson.Serialize<ThreadReadResult>(result)), h.Source));
        var op = result.Snapshot.Posts[0]; Assert.Equal(SourceTextFormat.Html, op.TextFormat); Assert.Equal("<p>Inert text</p>", op.Text);
        Assert.Equal(new[] { 0, 1 }, op.Attachments!.Select(a => a.Position)); Assert.Equal(new[] { "123-0", "123-1" }, op.Attachments.Select(a => a.MediaId));
        Assert.Equal("https://uboachan.net/demo/src/file-0.png", op.Attachments[0].Url!.AbsoluteUri);
        Assert.Equal("2ekqYfL9A3THOfl+KA127w==", op.Attachments[0].ExpectedMd5); Assert.Equal(2187, op.Attachments[0].ExpectedSize);
        Assert.Null(result.Snapshot.Posts[1].Attachments); Assert.False(result.Snapshot.Posts[1].Fields.HasFlag(SourceFieldPresence.Attachments));
        Assert.Equal(1, Assert.Single(result.Snapshot.Posts[2].Attachments!).Position);
        Assert.Equal("125-1", result.Snapshot.Posts[2].Attachments![0].MediaId);
        Assert.Equal("https://uboachan.net/demo/res/123.json", Assert.Single(h.Http.Requests).Url.AbsoluteUri);
    }

    [Fact]
    public async Task MixedSha1IsNeverMislabeledOrSilentlyDiscardedBeforeContractIntegration()
    {
        var root = JsonNode.Parse(Thread)!; root["posts"]![0]!["extra_files"]![0]!["md5"] = "A918lr4HttaA+bkeVBeaLkvvwVc=";
        var failure = Assert.IsType<ThreadFailure>(await Harness(root.ToJsonString()).ReadThread());
        Assert.Equal("unsupported_integrity_contract", failure.Failure.Code);
    }

    [Theory]
    [InlineData("bad digest")][InlineData("")][InlineData("AQIDBA==")]
    public async Task UnknownOrMalformedProviderHashFailsTheWholeObservation(string md5)
    {
        var root = JsonNode.Parse(Thread)!; root["posts"]![0]!["md5"] = md5;
        Assert.Equal("invalid_payload", Assert.IsType<ThreadFailure>(await Harness(root.ToJsonString()).ReadThread()).Failure.Code);
    }

    [Fact]
    public async Task EmptyFilenameAndOpaqueTokensDoNotDetermineIdentity()
    {
        var first = Assert.IsType<ThreadSnapshot>(await Harness().ReadThread()).Snapshot.Posts[0].Attachments![0];
        var root = JsonNode.Parse(Thread)!; root["posts"]![0]!["filename"] = "renamed"; root["posts"]![0]!["tim"] = "replacement";
        var second = Assert.IsType<ThreadSnapshot>(await Harness(root.ToJsonString()).ReadThread()).Snapshot.Posts[0].Attachments![0];
        Assert.Equal(first.MediaId, second.MediaId); Assert.NotEqual(first.Url, second.Url);
        root["posts"]![0]!["filename"] = "";
        Assert.IsType<ThreadSnapshot>(await Harness(root.ToJsonString()).ReadThread());
    }

    [Fact]
    public async Task CatalogFlattensPagesWithoutInferringCoverageOrThreadEnd()
    {
        var h = Harness("""[{"page":1,"threads":[{"no":123,"sub":"日本語","com":"<b>text</b>","time":1700000000,"replies":2,"images":1}]},{"page":2,"threads":[{"no":"999999999999999999999999999999"}]}]""");
        var result = Assert.IsType<CatalogSnapshot>(await h.ReadCatalog()); Assert.False(result.Snapshot.IsComplete);
        Assert.Equal(2, result.Snapshot.Threads.Count); Assert.Equal("日本語", result.Snapshot.Threads[0].Title);
        Assert.Equal("https://uboachan.net/demo/res/123.html", result.Snapshot.Threads[0].Url.AbsoluteUri);
        Assert.Empty(SdkValidation.ValidateCatalogResult(h.CatalogRequest, result, h.Source));
        Assert.Equal("https://uboachan.net/demo/catalog.json", Assert.Single(h.Http.Requests).Url.AbsoluteUri);
    }

    [Theory]
    [InlineData("{bad")][InlineData("{\"posts\":{}}")][InlineData("{\"posts\":[{\"no\":123},{\"no\":123}]}")]
    [InlineData("{\"posts\":[{\"no\":123,\"resto\":999}]}")]
    [InlineData("{\"posts\":[{\"no\":123,\"tim\":\"../escape\",\"ext\":\".png\"}]}")]
    [InlineData("{\"posts\":[{\"no\":123,\"tim\":null,\"ext\":\".png\"}]}")]
    public async Task BadPayloadDoesNotProducePartialSnapshot(string body) => Assert.IsType<ThreadFailure>(await Harness(body).ReadThread());

    [Theory]
    [InlineData(403)][InlineData(410)][InlineData(429)][InlineData(500)][InlineData(503)][InlineData(206)]
    public async Task Non200IsNotAnEndSignal(int status)
    {
        var h = Harness("{}", status); Assert.IsType<ThreadFailure>(await h.ReadThread()); Assert.IsType<CatalogFailure>(await h.ReadCatalog());
    }

    [Fact]
    public async Task ConditionalRequestsRequireBaselineAndPreserveItsValidators()
    {
        var h = Harness("{}", 304); Assert.IsType<ThreadFailure>(await h.ReadThread()); Assert.IsType<CatalogFailure>(await h.ReadCatalog());
        var baseline = new SourceValidators(EntityTag: "W/\"old\"");
        var thread = h.ThreadRequest with { HasSnapshot = true, Validators = baseline };
        Assert.Equal(baseline, Assert.IsType<ThreadUnchanged>(await h.Plugin.ReadThreadAsync(thread, h.Context)).Validators);
        var catalog = h.CatalogRequest with { HasSnapshot = true, Validators = baseline };
        Assert.Equal(baseline, Assert.IsType<CatalogUnchanged>(await h.Plugin.ReadCatalogAsync(catalog, h.Context)).Validators);
        Assert.Equal(baseline, h.Http.Requests.Last().Validators);
    }

    [Fact]
    public async Task Exact404AndUnexpectedFinalEndpointRemainSeparate()
    {
        var h = Harness("{}", 404); Assert.IsType<ThreadNotFound>(await h.ReadThread()); Assert.IsType<CatalogFailure>(await h.ReadCatalog());
        h.Http.FinalUri = new Uri("https://unrelated.test/demo/res/123.json"); Assert.IsType<ThreadFailure>(await h.ReadThread());
    }

    [Fact]
    public async Task DeferralBudgetsAndCancellationDoNotStartPluginRetries()
    {
        var h = Harness(); h.Http.Result = new HostHttpDeferred(DateTimeOffset.UtcNow.AddMinutes(1), "rate_limit", 429);
        Assert.IsType<ThreadDeferred>(await h.ReadThread()); Assert.Single(h.Http.Requests);
        h = Harness(); h.Http.Body = new byte[SdkLimits.MaximumMetadataBytes + 1]; Assert.Equal("metadata_too_large", Assert.IsType<ThreadFailure>(await h.ReadThread()).Failure.Code);
        h = Harness(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.ReadThread(cancellation.Token)); Assert.Empty(h.Http.Requests);
        h = Harness(); Assert.IsType<CatalogFailure>(await h.Plugin.ReadCatalogAsync(h.CatalogRequest with { Cursor = "unsupported" }, h.Context)); Assert.Empty(h.Http.Requests);
    }
}

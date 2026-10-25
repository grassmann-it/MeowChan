using System.Text.Json.Nodes;
using MeowChan.PluginSdk;
using MeowChan.Plugins.Examples.Dvach;

namespace MeowChan.ExamplePlugins.Tests;

public sealed class DvachPluginTests
{
    private const string Thread = """
        {"current_thread":123,"is_closed":1,"threads":[{"posts":[
         {"board":"demo","num":123,"parent":0,"timestamp":1700000000,"subject":"Synthetic subject","name":"Synthetic author","comment":"<p>Inert text</p>","files":[
          {"path":"/demo/src/123/opaque.png","name":"opaque.png","fullname":"original image.png","displayname":"short.png","md5":"d0b04abd386c83f00df0b06b6583d073"},
          {"path":"/demo/src/123/opaque.webm","name":"opaque.webm","fullname":"movie.webm","md5":"00000000000000000000000000000000"}]},
         {"board":"demo","num":124,"parent":123,"comment":"","files":null},
         {"num":125,"parent":123,"files":[]}]}]}
        """;
    private static MakabaUboachanHarness Harness(string body = Thread, int status = 200) => new(new DvachPlugin(), body, status);

    [Fact]
    public void DescriptionIsPassiveSdkOnlyAndSeparatesMetadataAndMediaHosts()
    {
        var plugin = new DvachPlugin(); var description = plugin.Describe(); var source = Assert.Single(description.Sources);
        Assert.Empty(SdkValidation.ValidateDescription(description));
        Assert.Equal("meowchan.source.2ch", description.PluginId); Assert.Equal("2ch", source.SourceId);
        Assert.Equal(PostOrderKind.DecimalPostId, source.PostOrder);
        Assert.Equal("2ch.su", Assert.Single(source.Routes, r => r.Kind == SourceResourceKind.Thread && r.IsCanonical).Host);
        Assert.Contains(source.Routes, r => r.Host == "2ch.hk" && r.Kind == SourceResourceKind.Thread && !r.IsCanonical);
        Assert.Contains(source.HttpHosts, h => h.Host == "2ch.hk" && h.Purpose.HasFlag(HttpHostPurpose.Metadata));
        Assert.Equal(HttpHostPurpose.OriginalMedia, Assert.Single(source.HttpHosts, h => h.Host == "2ch.su").Purpose);
        Assert.Single(typeof(DvachPlugin).Assembly.GetExportedTypes(), t => typeof(IPlugin).IsAssignableFrom(t));
        Assert.DoesNotContain(typeof(DvachPlugin).Assembly.GetReferencedAssemblies(), a => a.Name is "libmeowchan" or "MeowChan.PluginHost" or "Avalonia");
    }

    [Fact]
    public async Task ParsesPostsSlotsHexMd5AndPreservesUnknownNullFiles()
    {
        var h = Harness(); var result = Assert.IsType<ThreadSnapshot>(await h.ReadThread());
        Assert.Empty(SdkValidation.ValidateThreadResult(h.ThreadRequest, result, h.Source));
        var copy = SdkJson.Deserialize<ThreadReadResult>(SdkJson.Serialize<ThreadReadResult>(result));
        Assert.Empty(SdkValidation.ValidateThreadResult(h.ThreadRequest, copy, h.Source));
        Assert.Equal(new[] { "123", "124", "125" }, result.Snapshot.Posts.Select(p => p.PostId));
        Assert.Equal(SourceThreadState.Active, result.Snapshot.SourceState); // closed is not proof of archival.
        var op = result.Snapshot.Posts[0]; Assert.Equal("<p>Inert text</p>", op.Text); Assert.Equal(SourceTextFormat.Html, op.TextFormat);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), op.PostedAt);
        Assert.Equal(new[] { 0, 1 }, op.Attachments!.Select(a => a.Position));
        Assert.Equal(new[] { "123-0", "123-1" }, op.Attachments!.Select(a => a.MediaId));
        Assert.Equal("original image.png", op.Attachments[0].OriginalFileName);
        Assert.Equal(Convert.ToBase64String(Convert.FromHexString("d0b04abd386c83f00df0b06b6583d073")), op.Attachments[0].ExpectedMd5);
        Assert.Equal("https://2ch.hk/demo/src/123/opaque.png", op.Attachments[0].Url!.AbsoluteUri);
        Assert.Null(result.Snapshot.Posts[1].Attachments); Assert.False(result.Snapshot.Posts[1].Fields.HasFlag(SourceFieldPresence.Attachments));
        Assert.Empty(result.Snapshot.Posts[2].Attachments!); Assert.True(result.Snapshot.Posts[2].Fields.HasFlag(SourceFieldPresence.Attachments));
        Assert.Equal("https://2ch.hk/demo/res/123.json", Assert.Single(h.Http.Requests).Url.AbsoluteUri);
    }

    [Fact]
    public async Task RoundedSizeNeverBecomesAnExactExpectedByteLengthBeforeContractIntegration()
    {
        var root = JsonNode.Parse(Thread)!; root["threads"]![0]!["posts"]![0]!["files"]![0]!["size"] = 24;
        var failure = Assert.IsType<ThreadFailure>(await Harness(root.ToJsonString()).ReadThread());
        Assert.Equal("unsupported_integrity_contract", failure.Failure.Code);
    }

    [Fact]
    public async Task StableMediaIdentityDoesNotDependOnProviderNameOrRedirectHost()
    {
        var first = Assert.IsType<ThreadSnapshot>(await Harness().ReadThread()).Snapshot.Posts[0].Attachments![0];
        var root = JsonNode.Parse(Thread)!; var file = root["threads"]![0]!["posts"]![0]!["files"]![0]!;
        file["fullname"] = "new name.png"; file["path"] = "/demo/src/123/replacement.png";
        var next = Assert.IsType<ThreadSnapshot>(await Harness(root.ToJsonString()).ReadThread()).Snapshot.Posts[0].Attachments![0];
        Assert.Equal(first.MediaId, next.MediaId); Assert.NotEqual(first.Url, next.Url);
    }

    [Fact]
    public async Task CatalogKeepsIdsUnicodeHtmlAndIncompleteCoverage()
    {
        var h = Harness("""{"threads":[{"num":123,"board":"demo","subject":"日本語","comment":"<b>text</b>","timestamp":1700000000,"files_count":2},{"num":"999999999999999999999999999999"}]}""");
        var result = Assert.IsType<CatalogSnapshot>(await h.ReadCatalog()); Assert.False(result.Snapshot.IsComplete);
        Assert.Empty(SdkValidation.ValidateCatalogResult(h.CatalogRequest, result, h.Source));
        Assert.Equal("999999999999999999999999999999", result.Snapshot.Threads[1].Id.ThreadId);
        Assert.Equal("日本語", result.Snapshot.Threads[0].Title); Assert.Equal(SourceTextFormat.Html, result.Snapshot.Threads[0].OpeningTextFormat);
        Assert.Equal("https://2ch.hk/demo/catalog.json", Assert.Single(h.Http.Requests).Url.AbsoluteUri);
    }

    [Theory]
    [InlineData("//evil.test/a.png")][InlineData("/foreign/src/123/a.png")][InlineData("/demo/src/../a.png")]
    [InlineData("/demo/src/123/%2e%2e")][InlineData("/demo/src/123/a.png?x=1")][InlineData("/demo/src/123/a%2fb.png")]
    public async Task UntrustedMediaPathsFailTheWholeObservation(string path)
    {
        var root = JsonNode.Parse(Thread)!; root["threads"]![0]!["posts"]![0]!["files"]![0]!["path"] = path;
        Assert.Equal("invalid_payload", Assert.IsType<ThreadFailure>(await Harness(root.ToJsonString()).ReadThread()).Failure.Code);
    }

    [Theory]
    [InlineData("{bad")][InlineData("{\"threads\":[]}")][InlineData("{\"threads\":[{\"posts\":[{\"num\":123,\"parent\":999}]}]}")]
    [InlineData("{\"threads\":[{\"posts\":[{\"num\":123},{\"num\":123}]}]}")]
    public async Task InvalidShapeOrIdentityIsNotPartialSuccess(string body) => Assert.IsType<ThreadFailure>(await Harness(body).ReadThread());

    [Theory]
    [InlineData(403)][InlineData(410)][InlineData(429)][InlineData(500)][InlineData(503)][InlineData(206)]
    public async Task HttpFailuresNeverBecomeEndSignals(int status)
    {
        var h = Harness("{}", status); Assert.IsType<ThreadFailure>(await h.ReadThread()); Assert.IsType<CatalogFailure>(await h.ReadCatalog());
    }

    [Fact]
    public async Task Exact404AndConditionalBaselineAreScopedToRequestedEndpoint()
    {
        var h = Harness("{}", 404); Assert.IsType<ThreadNotFound>(await h.ReadThread()); Assert.IsType<CatalogFailure>(await h.ReadCatalog());
        h.Http.FinalUri = new Uri("https://2ch.su/demo/res/123.json"); Assert.IsType<ThreadFailure>(await h.ReadThread());
        h = Harness("{}", 304); Assert.IsType<ThreadFailure>(await h.ReadThread());
        var request = h.ThreadRequest with { HasSnapshot = true, Validators = new SourceValidators(EntityTag: "W/\"old\"") };
        var unchanged = Assert.IsType<ThreadUnchanged>(await h.Plugin.ReadThreadAsync(request, h.Context)); Assert.Equal(request.Validators, unchanged.Validators);
        var catalog = h.CatalogRequest with { HasSnapshot = true, Validators = request.Validators };
        Assert.Equal(request.Validators, Assert.IsType<CatalogUnchanged>(await h.Plugin.ReadCatalogAsync(catalog, h.Context)).Validators);
    }

    [Fact]
    public async Task DeferralLimitsAndCancellationStayHostOwned()
    {
        var h = Harness(); h.Http.Result = new HostHttpDeferred(DateTimeOffset.UtcNow.AddMinutes(1), "rate_limit", 429);
        Assert.IsType<ThreadDeferred>(await h.ReadThread()); Assert.Single(h.Http.Requests);
        h = Harness(); h.Http.Body = new byte[SdkLimits.MaximumMetadataBytes + 1]; Assert.IsType<ThreadFailure>(await h.ReadThread());
        h = Harness(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.ReadThread(cancelled.Token)); Assert.Empty(h.Http.Requests);
        h = Harness(); using var during = new CancellationTokenSource(); h.Http.OnSend = during.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.ReadThread(during.Token));
        h = Harness(); Assert.IsType<CatalogFailure>(await h.Plugin.ReadCatalogAsync(h.CatalogRequest with { Page = 1 }, h.Context)); Assert.Empty(h.Http.Requests);
    }
}

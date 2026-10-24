using System.Text;
using System.Text.Json.Nodes;
using MeowChan.PluginSdk;
using MeowChan.Plugins.Examples.Kohlchan;
using MeowChan.Plugins.Examples.Wizchan;
using MeowChan.Plugins.Examples.Endchan;

namespace MeowChan.ExamplePlugins.Tests;

public sealed class ExperimentalSourceMetadataTests
{
    [Theory]
    [InlineData("kohlchan")]
    [InlineData("wizchan")]
    [InlineData("endchan")]
    public void ExperimentalIsPassiveSourceMetadataAndSurvivesDescriptionRoundtrip(string sourceId)
    {
        PluginDescription description = Plugin(sourceId).Describe();
        SourceDescription source = Assert.Single(description.Sources);
        Assert.Equal("meowchan.source." + sourceId, description.PluginId);
        Assert.Equal(sourceId, source.SourceId);
        Assert.Equal(SourceMaturity.Experimental, source.Maturity);
        Assert.Empty(SdkValidation.ValidateDescription(description));
        Assert.Empty(description.Settings.Fields);
        Assert.Empty(source.Settings.Fields); // Permission belongs to the host, not plugin settings.
        Assert.Equal(SourceCapabilities.ThreadRead | SourceCapabilities.BoardCatalog | SourceCapabilities.PostCapture | SourceCapabilities.OriginalMedia, source.Capabilities);
        HttpHostDeclaration grant = Assert.Single(source.HttpHosts);
        Assert.Equal(source.BaseUri.Host, grant.Host);
        Assert.Equal(HttpHostPurpose.Metadata | HttpHostPurpose.OriginalMedia, grant.Purpose);
        PluginDescription copy = SdkJson.Deserialize<PluginDescription>(SdkJson.Serialize<PluginDescription>(description));
        Assert.Equal(SourceMaturity.Experimental, Assert.Single(copy.Sources).Maturity);
        Assert.Empty(SdkValidation.ValidateDescription(copy));
    }

    [Theory]
    [InlineData("kohlchan", 4845L)]
    [InlineData("wizchan", 90011L)]
    [InlineData("endchan", 33552L)]
    public async Task ExperimentalParserRetainsDeclaredSizeAndChecksumAsExpectations(string sourceId, long reportedBytes)
    {
        ISourcePlugin plugin = Plugin(sourceId);
        JsonNode payload = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", sourceId + "-thread.json")))!;
        JsonNode file = sourceId == "wizchan" ? payload["posts"]![0]! : payload["files"]![0]!;
        file[sourceId == "wizchan" ? "fsize" : "size"] = reportedBytes;
        if (sourceId == "wizchan") file["md5"] = "AAAAAAAAAAAAAAAAAAAAAA==";
        Http http = new(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        SettingsSnapshot empty = new("1", Array.Empty<SettingValue>());
        NoServices none = new();
        PluginCallContext context = new(new PluginConfiguration("fixture", empty,
            new Dictionary<string, SettingsSnapshot> { [sourceId] = empty }), http, none, none, none);
        ThreadReadRequest request = new(new SourceThreadId(sourceId, "demo", "123"));
        ThreadSnapshot result = Assert.IsType<ThreadSnapshot>(await plugin.ReadThreadAsync(request, context));
        SourceAttachment attachment = result.Snapshot.Posts[0].Attachments![0];
        Assert.Equal(reportedBytes, attachment.ExpectedSize);
        Assert.Equal(sourceId == "wizchan" ? "AAAAAAAAAAAAAAAAAAAAAA==" : null, attachment.ExpectedMd5);
        Assert.Null(attachment.ExpectedSha256);
        Assert.Equal("123-0", attachment.MediaId);
        Assert.Equal(0, attachment.Position);
        Assert.Equal(plugin.Describe().Sources[0].BaseUri.Host, attachment.Url!.Host);
        Assert.Empty(SdkValidation.ValidateThreadResult(request, result, plugin.Describe().Sources[0]));
        ThreadSnapshot copy = Assert.IsType<ThreadSnapshot>(SdkJson.Deserialize<ThreadReadResult>(SdkJson.Serialize<ThreadReadResult>(result)));
        Assert.Equal(reportedBytes, copy.Snapshot.Posts[0].Attachments![0].ExpectedSize);
        Assert.Equal(attachment.ExpectedMd5, copy.Snapshot.Posts[0].Attachments![0].ExpectedMd5);
        HostHttpRequest sent = Assert.Single(http.Requests);
        Assert.Equal("/demo/res/123.json", sent.Url.AbsolutePath);
        Assert.Equal(HostHttpMethod.Get, sent.Method);
    }

    private static ISourcePlugin Plugin(string source) => source switch
    {
        "kohlchan" => new KohlchanPlugin(),
        "wizchan" => new WizchanPlugin(),
        "endchan" => new EndchanPlugin(),
        _ => throw new ArgumentException("Unknown source", nameof(source))
    };

    private sealed class Http(byte[] body) : IHostHttp
    {
        internal List<HostHttpRequest> Requests { get; } = new();
        public Task<HostHttpResult> SendAsync(HostHttpRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult<HostHttpResult>(new HostHttpResponse(200, request.Url, new Dictionary<string, string>(), body));
        }
    }

    private sealed class NoServices : IImageAssetReader, IPluginResources, IPluginLog
    {
        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Parser must not read original bytes.");
        public string? ResolvePath(string logicalName) => throw new InvalidOperationException("No plugin resources.");
        public void Write(PluginLogLevel level, string message) => throw new InvalidOperationException("No content logging.");
    }
}

using System.Text;
using MeowChan.PluginSdk;
using MeowChan.PluginSdk.Consumer;

SyntheticPlugin plugin = new SyntheticPlugin();
PluginDescription description = plugin.Describe();
SdkValidation.ThrowIfInvalid(SdkValidation.ValidateDescription(description));
SettingsSnapshot empty = new SettingsSnapshot("1", Array.Empty<SettingValue>());
PluginConfiguration configuration = new PluginConfiguration("config-1", empty,
    new Dictionary<string, SettingsSnapshot> { [SyntheticPlugin.SourceId] = empty });
SdkValidation.ThrowIfInvalid(SdkValidation.ValidateConfiguration(description, configuration));
PluginCallContext context = new PluginCallContext(configuration, new SyntheticHttp(), new NoImages(), new NoResources(), new StderrLog());
SourceDescription source = description.Sources[0];
CatalogReadRequest catalogRequest = new CatalogReadRequest(new SourceBoardId(SyntheticPlugin.SourceId, "demo"));
CatalogReadResult catalog = await plugin.ReadCatalogAsync(catalogRequest, context);
SdkValidation.ThrowIfInvalid(SdkValidation.ValidateCatalogResult(catalogRequest, catalog, source));
ThreadReadRequest request = new ThreadReadRequest(new SourceThreadId(SyntheticPlugin.SourceId, "demo", "123"));
ThreadReadResult result = await plugin.ReadThreadAsync(request, context);
ThreadReadResult copy = SdkJson.Deserialize<ThreadReadResult>(SdkJson.Serialize(result));
SdkValidation.ThrowIfInvalid(SdkValidation.ValidateThreadResult(request, copy, source));
if (copy is not ThreadSnapshot thread) throw new InvalidDataException("Expected a synthetic thread snapshot.");
SourceAttachment metadata = thread.Snapshot.Posts[0].Attachments![0];
if (metadata.ExpectedSha1 != Convert.ToBase64String(new byte[20]) || metadata.ExpectedSize != null
    || metadata.ProviderSizeHint != new SourceSizeHint("2.4e1", "KiB"))
    throw new InvalidDataException("Provider SHA-1 and raw non-authoritative size hint did not round-trip.");
Console.WriteLine($"SDK {SdkContract.Version}: {thread.Snapshot.Posts.Count} captured posts, "
    + $"{thread.Snapshot.Posts.Sum(post => post.Attachments?.Count ?? 0)} attachments; no network or Core dependency.");

internal sealed class SyntheticHttp : IHostHttp
{
    public Task<HostHttpResult> SendAsync(HostHttpRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string json = request.Url.AbsolutePath.EndsWith("/catalog.json", StringComparison.Ordinal)
            ? """[{"id":"123","title":"Synthetic thread"}]"""
            : """{"posts":[{"id":"123","order":0,"text":"Opening post","files":["one","two"]},{"id":"124","order":1,"text":"Reply"}],"archived":false}""";
        HostHttpResult response = new HostHttpResponse(200, request.Url, new Dictionary<string, string>(), Encoding.UTF8.GetBytes(json),
            new SourceValidators(EntityTag: "\"synthetic-1\""));
        return Task.FromResult(response);
    }
}

internal sealed class NoImages : IImageAssetReader
{
    public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("The synthetic source has no image-data grant.");
}

internal sealed class NoResources : IPluginResources
{
    public string? ResolvePath(string logicalName) => null;
}

internal sealed class StderrLog : IPluginLog
{
    public void Write(PluginLogLevel level, string message) => Console.Error.WriteLine($"{level}: {message}");
}

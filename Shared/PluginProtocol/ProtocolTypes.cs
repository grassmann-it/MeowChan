using System.Text.Encodings.Web;
using System.Text.Json;
using MeowChan.PluginSdk;

namespace MeowChan.PluginProtocol;

internal static class ProtocolVersion
{
    public const int Major = 1;
    public const int MaximumFrameBytes = 16 * 1024 * 1024;
    internal const string TaggerBatch = "tagger.batch";
    public static readonly string[] Operations = ["describe", "catalog", "thread", "booru.search", "booru.post", "filter", "settings", "http", "image", "cancel"];
    internal static readonly string[] SupportedOperations = [.. Operations, TaggerBatch];
}

internal sealed record WireEnvelope(int Version, string Direction, string Operation, string RequestId,
    string WorkerInstance, string BuildRevision, string ConfigurationRevision,
    string? ResourceId, long? Generation, string? RuleRevision, string? GrantRevision,
    string? ParentRequestId, JsonElement Payload);
internal sealed record HelloRequest(string[] RequiredOperations);
internal sealed record HelloResponse(string[] Operations);
internal sealed record InitializeRequest(string AssemblyPath, Dictionary<string, string> ResourcePaths,
    PluginConfiguration Configuration, bool ValidateConfiguration = true);
internal sealed record WireFailure(string Code, string Message);
internal sealed record ImageReadRequest(string AssetToken, long Offset, int Count);

internal static class WireJson
{
    // Frame size only: SdkJson bytes elsewhere are hashed and compared, so they keep the default escaping.
    // Reading does not depend on the escape form; plugins are trusted code and frames are never HTML.
    public static readonly JsonSerializerOptions Options = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = SdkJson.CreateOptions();
        options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        return options;
    }
    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
    public static T Read<T>(JsonElement value) => value.Deserialize<T>(Options)
        ?? throw new JsonException("A protocol payload must not be null.");
}

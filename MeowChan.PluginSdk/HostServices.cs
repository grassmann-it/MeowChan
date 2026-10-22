using System.Text.Json.Serialization;

namespace MeowChan.PluginSdk;

public enum HostHttpMethod { Get, Head }

/// <summary>
/// Metadata HTTP only. The host validates destinations, redirects and allowed headers, and
/// applies the same pacing/retry/cancellation policy to production and test calls. Plugins
/// do not add retry loops. Original downloads belong to the host media queue.
/// </summary>
public interface IHostHttp
{
    Task<HostHttpResult> SendAsync(HostHttpRequest request, CancellationToken cancellationToken = default);
}

public sealed record HostHttpRequest(string SourceId, HostHttpMethod Method, Uri Url,
    IReadOnlyDictionary<string, string>? Headers = null, SourceValidators? Validators = null);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(HostHttpResponse), "response")]
[JsonDerivedType(typeof(HostHttpDeferred), "deferred")]
[JsonDerivedType(typeof(HostHttpFailure), "failure")]
public abstract record HostHttpResult;

/// <summary>Body is the bounded, decompressed entity body. HEAD/304 responses have no body.</summary>
public sealed record HostHttpResponse(int StatusCode, Uri FinalUri,
    IReadOnlyDictionary<string, string> Headers, byte[] Body, SourceValidators? Validators = null) : HostHttpResult;

public sealed record HostHttpDeferred(DateTimeOffset RetryAt, string ReasonCode,
    int? HttpStatusCode = null) : HostHttpResult;
public sealed record HostHttpFailure(FailureDetail Failure) : HostHttpResult;

/// <summary>The worker binds this service to the current request, grant, revisions and lease.</summary>
public interface IImageAssetReader
{
    Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count,
        CancellationToken cancellationToken = default);
}

public sealed record ImageAssetChunk(long Offset, byte[] Bytes, bool IsEnd);

/// <summary>Resolves only verified resources in this plugin's own bundle, never arbitrary files.</summary>
public interface IPluginResources
{
    string? ResolvePath(string logicalName);
}

public enum PluginLogLevel { Debug, Information, Warning, Error }

/// <summary>The host keeps bounded logs; image content, secrets and full HTTP bodies are excluded.</summary>
public interface IPluginLog
{
    void Write(PluginLogLevel level, string message);
}

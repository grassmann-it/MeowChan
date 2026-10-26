using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Contracts;

/// <summary>Caller-owned resource lifetime; cancellation invalidates outstanding responses.</summary>
public sealed record PluginOperationContext(string ResourceId, long Generation,
    string? RuleRevision = null, string? GrantRevision = null);

public sealed record PluginWorkerIdentity(string InstanceId, string ExecutionRevision, string ConfigurationRevision);
public sealed record PluginWorkerLaunch(string ExecutablePath, IReadOnlyList<string> Arguments, string WorkingDirectory);
public sealed record PluginWorkerSnapshot(PluginWorkerIdentity Identity, string State, int? ProcessId,
    string? FailureCode, string? FailureMessage, IReadOnlyList<string> Logs);

public sealed record PluginWorkerOptions
{
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan CancellationGrace { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public int MaximumProductionWorkers { get; init; } = 4;
    public int MaximumTestWorkers { get; init; } = 1;
    public int MaximumConcurrentStarts { get; init; } = 2;
    public int MaximumConcurrentCalls { get; init; } = 4;
    public int MaximumQueuedCalls { get; init; } = 128;
    public int MaximumLogBytes { get; init; } = 1024 * 1024;
    public long HeapLimitBytes { get; init; } = 256L * 1024 * 1024;
    public long SourceHeapLimitBytes { get; init; } = 256L * 1024 * 1024;
    public long FilterHeapLimitBytes { get; init; } = 512L * 1024 * 1024;
    public long MaximumTemporaryBytes { get; init; } = 512L * 1024 * 1024;
    // Accepted before process creation and held until confirmed process/workspace cleanup.
    // Null partitions the shared limit across configured workers, capped at 64 MiB each.
    public long? WorkspaceByteQuota { get; init; }
    public Func<long>? ExternalTemporaryBytes { get; init; }
}

/// <summary>Services are invoked only for a live correlated call; implementations enforce grants.</summary>
public interface IPluginHostServices
{
    Task<HostHttpResult> SendHttpAsync(HostHttpRequest request, PluginOperationContext operation,
        CancellationToken cancellationToken);
    Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count,
        PluginOperationContext operation, CancellationToken cancellationToken);
}

public interface IPluginWorkerClient : IAsyncDisposable
{
    PluginDescription Description { get; }
    PluginWorkerIdentity Identity { get; }
    PluginWorkerSnapshot Snapshot { get; }
    Task<CatalogReadResult> ReadCatalogAsync(CatalogReadRequest request, PluginOperationContext operation,
        CancellationToken cancellationToken = default);
    Task<ThreadReadResult> ReadThreadAsync(ThreadReadRequest request, PluginOperationContext operation,
        CancellationToken cancellationToken = default);
    Task<ImageFilterResult> EvaluateAsync(ImageFilterRequest request, PluginOperationContext operation,
        CancellationToken cancellationToken = default);
    Task RetryAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FieldValidationError>> ValidateSettingsAsync(SettingsValidationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class PluginWorkerException : Exception
{
    public string Code { get; }
    public PluginWorkerException(string code, string message, Exception? innerException = null)
        : base(message, innerException) => Code = code;
}

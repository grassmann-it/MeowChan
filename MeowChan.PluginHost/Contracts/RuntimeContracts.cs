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

public sealed class PluginWorkerException : Exception, MeowChan.IFailureInfoProvider
{
    public string Code { get; }

    /// <summary>Transient reported evidence; never grants retry or scheduling authority.</summary>
    public MeowChan.FailureInfo FailureInfo { get; }

    public PluginWorkerException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        FailureInfo = ProjectFailure(code, Message, null, null);
    }

    private PluginWorkerException(FailureDetail failure, Exception? innerException)
        : base(failure.Message, innerException)
    {
        Code = failure.Code;
        FailureInfo = ProjectFailure(Code, Message, failure.HttpStatusCode, failure.RetryAt);
    }

    /// <summary>
    /// Retains source failure evidence after the existing SDK result validation.
    /// The reported retry time is preserved, not replaced by the scheduler's minimum delay.
    /// </summary>
    /// <exception cref="ArgumentNullException">The failure detail is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The HTTP status is outside the SDK's 100–599 range.</exception>
    public static PluginWorkerException FromFailure(FailureDetail failure, Exception? innerException = null)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure.HttpStatusCode is < 100 or > 599)
            throw new ArgumentOutOfRangeException(nameof(failure), "HTTP status must be between 100 and 599.");
        return new PluginWorkerException(failure, innerException);
    }

    private static MeowChan.FailureInfo ProjectFailure(string code, string message,
        int? httpStatusCode, DateTimeOffset? retryAt)
    {
        // Only explicit status and exact known producer codes are evidence. Never parse
        // messages, HTTP-looking code suffixes, or inner exceptions into a network cause.
        MeowChan.FailureKind kind = httpStatusCode != null ? MeowChan.FailureKind.Http : code switch
        {
            "http-timeout" => MeowChan.FailureKind.Timeout,
            "http-network" => MeowChan.FailureKind.Network,
            "invalid_metadata" or "invalid_source_result" or "http-encoding" => MeowChan.FailureKind.InvalidData,
            _ => MeowChan.FailureKind.Unknown
        };
        // In particular, worker "timeout"/"unresponsive" may be CPU or IPC failures;
        // the SDK does not carry typed DNS/connect/TLS evidence for either constructor.
        return new MeowChan.FailureInfo(kind, message,
            httpStatusCode is int status ? (System.Net.HttpStatusCode)status : null)
        {
            DiagnosticCode = code,
            RetryAt = retryAt
        };
    }
}

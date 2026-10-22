using MeowChan.PluginHost.Contracts;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Runtime;

internal sealed class PluginWorkerClient : IPluginWorkerClient
{
    private readonly PluginWorkerManager _manager;
    private readonly PluginWorkerLaunch _launch;
    private readonly PluginArtifact _artifact;
    private readonly PluginConfiguration _configuration;
    private readonly IPluginHostServices _services;
    private readonly PluginWorkerOptions _options;
    private readonly AsyncAdmission _serial;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime;
    private readonly Queue<DateTimeOffset> _restarts = new();
    private WorkerConnection? _connection;
    private PluginWorkerSnapshot? _lastSnapshot;
    private bool _hadFailure;
    private bool _disposed;
    private int _busy;
    private string _state = "starting";
    private PluginWorkerIdentity _identity;
    public bool IsTestWorker { get; }
    internal long GetTemporaryBytes() => _connection?.GetTemporaryBytes() ?? 0;
    public bool HasConnection => _connection != null;
    public DateTimeOffset LastUsed { get; private set; } = DateTimeOffset.UtcNow;
    public PluginDescription Description { get; private set; } = null!;
    public PluginWorkerIdentity Identity => _identity;
    public PluginWorkerSnapshot Snapshot => _connection is WorkerConnection connection
        ? connection.Snapshot with { State = _state }
        : new(_identity, _state, null, _lastSnapshot?.FailureCode, _lastSnapshot?.FailureMessage, _lastSnapshot?.Logs ?? []);

    public PluginWorkerClient(PluginWorkerManager manager, PluginWorkerLaunch launch, PluginArtifact artifact,
        PluginConfiguration configuration, IPluginHostServices services, PluginWorkerOptions options,
        bool testWorker, CancellationToken shutdown)
    {
        _manager = manager; _launch = launch; _artifact = artifact; _configuration = configuration;
        _services = services; _options = options; IsTestWorker = testWorker;
        _serial = new(1, options.MaximumQueuedCalls);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        _identity = new("pending", artifact.ExecutionRevision, configuration.Revision);
    }
    internal async Task InitializeAsync(CancellationToken token)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        Interlocked.Increment(ref _busy);
        try { await EnsureConnectionAsync(linked.Token).ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref _busy); }
    }
    private async Task<WorkerConnection> EnsureConnectionAsync(CancellationToken token)
    {
        await _connectionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_connection is { IsAlive: true }) return _connection;
            if (_connection != null)
            {
                _lastSnapshot = _connection.Snapshot;
                await _connection.DisposeAsync().ConfigureAwait(false);
                _connection = null; _hadFailure = true;
            }
            if (_hadFailure)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                while (_restarts.TryPeek(out DateTimeOffset first) && now - first > TimeSpan.FromMinutes(10)) _restarts.Dequeue();
                if (_restarts.Count >= 3) { _state = "intervention-required"; throw new PluginWorkerException("restart-limit", "Three worker restarts failed within ten minutes. Explicit retry is required."); }
                int attempt = _restarts.Count;
                _restarts.Enqueue(now);
                _state = "restart-delay";
                await Task.Delay(TimeSpan.FromSeconds(attempt == 0 ? 1 : attempt == 1 ? 5 : 30), token).ConfigureAwait(false);
            }
            _state = "waiting-for-worker";
            _connection = await _manager.StartConnectionAsync(this, _launch, _artifact, _configuration, _services, _options, IsTestWorker, token).ConfigureAwait(false);
            Description = _connection.Description; _identity = _connection.Identity;
            _state = "ready"; _hadFailure = false;
            return _connection;
        }
        catch { if (_state != "intervention-required") _state = "unavailable"; throw; }
        finally { _connectionGate.Release(); }
    }

    public async Task RetryAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        using IDisposable serial = await _serial.EnterAsync(linked.Token).ConfigureAwait(false);
        await _connectionGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (_connection != null) { await _connection.DisposeAsync().ConfigureAwait(false); _connection = null; }
            _restarts.Clear(); _hadFailure = false;
        }
        finally { _connectionGate.Release(); }
        await InitializeAsync(linked.Token).ConfigureAwait(false);
    }

    public async Task<CatalogReadResult> ReadCatalogAsync(CatalogReadRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default)
    {
        SourceDescription source = Source(request.Board.SourceId, SourceCapabilities.BoardCatalog);
        CatalogReadResult result = await CallAsync<CatalogReadRequest, CatalogReadResult>("catalog", request, operation, cancellationToken).ConfigureAwait(false);
        Validate(SdkValidation.ValidateCatalogResult(request, result, source)); return result;
    }
    public async Task<ThreadReadResult> ReadThreadAsync(ThreadReadRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default)
    {
        SourceDescription source = Source(request.Thread.SourceId, SourceCapabilities.ThreadRead);
        ThreadReadResult result = await CallAsync<ThreadReadRequest, ThreadReadResult>("thread", request, operation, cancellationToken).ConfigureAwait(false);
        Validate(SdkValidation.ValidateThreadResult(request, result, source)); return result;
    }
    public async Task<ImageFilterResult> EvaluateAsync(ImageFilterRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default)
    {
        FilterDescription filter = Description.Filters.SingleOrDefault(value => value.Key == request.FilterKey)
            ?? throw new PluginWorkerException("capability", "Unknown filter capability.");
        if (request.PluginBuildRevision != Identity.ExecutionRevision || request.ConfigurationRevision != Identity.ConfigurationRevision
            || operation.GrantRevision != request.GrantRevision || operation.RuleRevision != request.EffectiveRuleRevision)
            throw new PluginWorkerException("revision", "Filter input does not match the active execution, rule or grant revision.");
        Validate(SdkValidation.ValidateImageFilterRequest(request, filter));
        ImageFilterResult result = await CallAsync<ImageFilterRequest, ImageFilterResult>("filter", request, operation, cancellationToken).ConfigureAwait(false);
        Validate(SdkValidation.ValidateImageFilterResult(result)); return result;
    }
    public async Task<IReadOnlyList<FieldValidationError>> ValidateSettingsAsync(SettingsValidationRequest request, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<FieldValidationError> errors = await CallAsync<SettingsValidationRequest, IReadOnlyList<FieldValidationError>>("settings", request, null, cancellationToken).ConfigureAwait(false);
        if (errors.Count > 1024 || errors.Any(error => error == null || error.FieldKey.Length > 256 || error.Code.Length > 256 || error.Message.Length > 2048))
            throw new PluginWorkerException("validation", "Unbounded or invalid settings diagnostics.");
        return errors;
    }
    private SourceDescription Source(string id, SourceCapabilities capability)
    {
        SourceDescription source = Description.Sources.SingleOrDefault(value => value.SourceId == id)
            ?? throw new PluginWorkerException("capability", "Unknown source capability.");
        if (!source.Capabilities.HasFlag(capability)) throw new PluginWorkerException("capability", "Operation not declared by the source.");
        return source;
    }
    private static void Validate(IReadOnlyList<ValidationIssue> errors)
    {
        if (errors.Count > 0) throw new PluginWorkerException("validation", string.Join("; ", errors.Take(8).Select(error => error.Path + ": " + error.Message)));
    }
    private async Task<TResponse> CallAsync<TRequest, TResponse>(string operation, TRequest request, PluginOperationContext? context, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (context != null && (string.IsNullOrWhiteSpace(context.ResourceId) || context.ResourceId.Length > 1024 || context.Generation < 0))
            throw new ArgumentException("A valid resource identity and generation are required.", nameof(context));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        using IDisposable serial = await _serial.EnterAsync(linked.Token).ConfigureAwait(false);
        Interlocked.Increment(ref _busy);
        try
        {
            WorkerConnection connection = await EnsureConnectionAsync(linked.Token).ConfigureAwait(false);
            _state = "waiting-for-call";
            using IDisposable admitted = await _manager.Calls.EnterAsync(linked.Token).ConfigureAwait(false);
            _state = "running";
            TResponse result = await connection.InvokeAsync<TRequest, TResponse>(operation, request, context, _options.OperationTimeout, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            LastUsed = DateTimeOffset.UtcNow;
            if (_state != "intervention-required") _state = _connection is { IsAlive: true } ? "ready" : "unavailable";
            Interlocked.Decrement(ref _busy);
        }
    }
    internal async Task<bool> EvictIfIdleAsync(TimeSpan idle)
    {
        if (_disposed || Volatile.Read(ref _busy) != 0 || DateTimeOffset.UtcNow - LastUsed < idle || !await _connectionGate.WaitAsync(0).ConfigureAwait(false)) return false;
        try
        {
            if (Volatile.Read(ref _busy) != 0 || _connection == null) return false;
            _lastSnapshot = _connection.Snapshot;
            await _connection.DisposeAsync().ConfigureAwait(false); _connection = null; _state = "idle";
            return true;
        }
        finally { _connectionGate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _connectionGate.WaitAsync().ConfigureAwait(false);
        try { if (_connection != null) { _lastSnapshot = _connection.Snapshot; await _connection.DisposeAsync().ConfigureAwait(false); _connection = null; } _state = "stopped"; }
        finally { _connectionGate.Release(); _manager.Remove(this); }
    }
}

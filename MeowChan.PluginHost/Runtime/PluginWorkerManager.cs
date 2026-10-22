using MeowChan.PluginHost.Contracts;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Runtime;

/// <summary>One owner per host session for production/test processes and fair bounded calls.</summary>
public sealed class PluginWorkerManager : IAsyncDisposable
{
    private readonly PluginWorkerOptions _options;
    private readonly AsyncAdmission _production;
    private readonly AsyncAdmission _tests;
    private readonly AsyncAdmission _starts;
    internal readonly AsyncAdmission Calls;
    private readonly object _sync = new();
    private readonly HashSet<PluginWorkerClient> _clients = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _eviction;
    private bool _disposed;

    public PluginWorkerManager(PluginWorkerOptions? options = null)
    {
        _options = options ?? new();
        ValidateOptions(_options);
        _production = new(_options.MaximumProductionWorkers, _options.MaximumQueuedCalls);
        _tests = new(_options.MaximumTestWorkers, _options.MaximumQueuedCalls);
        _starts = new(_options.MaximumConcurrentStarts, _options.MaximumQueuedCalls);
        Calls = new(_options.MaximumConcurrentCalls, _options.MaximumQueuedCalls);
        _eviction = EvictLoopAsync();
    }

    public async Task<IPluginWorkerClient> StartAsync(PluginWorkerLaunch launch, PluginArtifact artifact,
        PluginConfiguration configuration, IPluginHostServices services, bool testWorker = false,
        CancellationToken cancellationToken = default, bool filterWorker = false)
    {
        PluginWorkerClient client;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_clients.Count >= _options.MaximumQueuedCalls + _options.MaximumProductionWorkers + _options.MaximumTestWorkers)
                throw new PluginWorkerException("capacity", "The number of registered worker revisions is bounded.");
            client = new(this, launch, artifact, configuration, services, _options with { HeapLimitBytes = filterWorker ? _options.FilterHeapLimitBytes : _options.SourceHeapLimitBytes }, testWorker, _shutdown.Token);
            _clients.Add(client);
        }
        try { await client.InitializeAsync(cancellationToken).ConfigureAwait(false); return client; }
        catch { await client.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async Task<PluginDescription> DescribeAsync(PluginWorkerLaunch launch, PluginArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        using IDisposable starting = await _starts.EnterAsync(lifetime.Token).ConfigureAwait(false);
        IDisposable process = await _tests.EnterAsync(lifetime.Token).ConfigureAwait(false);
        PluginConfiguration configuration = new("describe", new("1", []), new Dictionary<string, SettingsSnapshot>());
        await using WorkerConnection connection = await WorkerConnection.StartAsync(launch, artifact, configuration,
            NoHostServices.Instance, _options, process, lifetime.Token, describeOnly: true).ConfigureAwait(false);
        return connection.Description;
    }

    public IReadOnlyList<PluginWorkerSnapshot> GetSnapshots()
    {
        lock (_sync) return _clients.Select(client => client.Snapshot).ToArray();
    }

    internal async Task<WorkerConnection> StartConnectionAsync(PluginWorkerClient owner, PluginWorkerLaunch launch,
        PluginArtifact artifact, PluginConfiguration configuration, IPluginHostServices services,
        PluginWorkerOptions options, bool testWorker, CancellationToken token)
    {
        // Evict an idle revision when a different plugin needs capacity. Busy workers retain
        // their leases. Serial calls enqueue only once at the global FIFO, so one source
        // cannot flood the front of the shared call queue.
        PluginWorkerClient[] idle;
        lock (_sync) idle = _clients.Where(client => !ReferenceEquals(client, owner) && client.IsTestWorker == testWorker)
            .OrderBy(client => client.LastUsed).ToArray();
        int active = idle.Count(client => client.HasConnection) + (owner.HasConnection ? 1 : 0);
        int maximum = testWorker ? _options.MaximumTestWorkers : _options.MaximumProductionWorkers;
        if (active >= maximum)
            foreach (PluginWorkerClient candidate in idle)
                if (await candidate.EvictIfIdleAsync(TimeSpan.Zero).ConfigureAwait(false)) break;
        IDisposable process = await (testWorker ? _tests : _production).EnterAsync(token).ConfigureAwait(false);
        try
        {
            using IDisposable starting = await _starts.EnterAsync(token).ConfigureAwait(false);
            return await WorkerConnection.StartAsync(launch, artifact, configuration, services, options, process, token).ConfigureAwait(false);
        }
        catch { process.Dispose(); throw; }
    }
    internal void Remove(PluginWorkerClient client) { lock (_sync) _clients.Remove(client); }

    private async Task EvictLoopAsync()
    {
        try
        {
            TimeSpan interval = TimeSpan.FromMilliseconds(Math.Clamp(_options.IdleTimeout.TotalMilliseconds / 2, 100, 30000));
            using PeriodicTimer timer = new(interval);
            while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
            {
                PluginWorkerClient[] clients;
                lock (_sync) clients = _clients.ToArray();
                foreach (PluginWorkerClient client in clients) await client.EvictIfIdleAsync(_options.IdleTimeout).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        PluginWorkerClient[] clients;
        lock (_sync) { if (_disposed) return; _disposed = true; clients = _clients.ToArray(); }
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(clients.Select(async client => await client.DisposeAsync().ConfigureAwait(false))).ConfigureAwait(false);
        await _eviction.ConfigureAwait(false);
        _shutdown.Dispose();
    }
    private static void ValidateOptions(PluginWorkerOptions options)
    {
        if (options.MaximumProductionWorkers is < 1 or > 8 || options.MaximumTestWorkers is < 1 or > 2
            || options.MaximumConcurrentStarts is < 1 or > 4 || options.MaximumConcurrentCalls is < 1 or > 8
            || options.MaximumQueuedCalls is < 1 or > 1024 || options.MaximumLogBytes is < 1024 or > 1024 * 1024
            || options.HeapLimitBytes < 64L * 1024 * 1024 || options.HeapLimitBytes > 2L * 1024 * 1024 * 1024
            || options.SourceHeapLimitBytes < 128L * 1024 * 1024 || options.SourceHeapLimitBytes > 1024L * 1024 * 1024
            || options.FilterHeapLimitBytes < 256L * 1024 * 1024 || options.FilterHeapLimitBytes > 2048L * 1024 * 1024
            || options.MaximumTemporaryBytes < 1 || options.MaximumTemporaryBytes > 2L * 1024 * 1024 * 1024
            || options.HandshakeTimeout <= TimeSpan.Zero || options.OperationTimeout <= TimeSpan.Zero
            || options.CancellationGrace <= TimeSpan.Zero || options.IdleTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid or unbounded plugin runtime limits.");
    }
    private sealed class NoHostServices : IPluginHostServices
    {
        public static readonly NoHostServices Instance = new();
        public Task<HostHttpResult> SendHttpAsync(HostHttpRequest request, PluginOperationContext operation, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Describe does not access host services.");
        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, PluginOperationContext operation, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Describe does not access host services.");
    }
}

/// <summary>Convenience entrypoints share one process/call budget. Session owners may instead own a manager.</summary>
public static class PluginWorkerFactory
{
    private static readonly PluginWorkerManager Shared = new();
    public static Task<IPluginWorkerClient> StartAsync(PluginWorkerLaunch launch, PluginArtifact artifact,
        PluginConfiguration configuration, IPluginHostServices services, bool testWorker = false,
        CancellationToken cancellationToken = default, bool filterWorker = false) => Shared.StartAsync(launch, artifact, configuration, services, testWorker, cancellationToken, filterWorker);
    public static Task<PluginDescription> DescribeAsync(PluginWorkerLaunch launch, PluginArtifact artifact,
        CancellationToken cancellationToken = default) => Shared.DescribeAsync(launch, artifact, cancellationToken);
}

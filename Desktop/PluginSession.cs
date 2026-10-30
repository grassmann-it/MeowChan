using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Downloader;
using MeowChan.Http;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Compilation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Http;
using MeowChan.PluginHost.Installation;
using MeowChan.PluginHost.Runtime;
using MeowChan.PluginHost.Sources;
using MeowChan.PluginHost.Filters;
using MeowChan.Persistence;
using MeowChan.Archive;
using MeowChan.PluginSdk;
using Serilog;

namespace MeowChan.Desktop;

/// <summary>One profile's optional process runtime. Registry objects and archive stores never cross IPC.</summary>
internal sealed partial class PluginSession : IAsyncDisposable
{
    private readonly object _state = new object();
    private readonly Dictionary<string, PluginActivationSnapshot> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IPluginWorkerClient> _clients = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> _clientGates = new(StringComparer.Ordinal);
    private readonly List<IDisposable> _services = new();
    private readonly HashSet<Task> _draining = new();
    private readonly FilterStageCoordinator _filterStages;
    private readonly Dictionary<string, PluginHostHttp> _pluginHttp = new(StringComparer.Ordinal);
    private readonly HttpClient _transport;
    private readonly RequestPolicyState _httpPolicy = new RequestPolicyState();
    private readonly DownloadQueue _queue;
    private readonly ILogger _logger;
    private readonly string _shippedDirectory;
    private readonly string[] _repairRestrictedSourceIds;
    private readonly PluginWorkerLaunch _launch;
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
    private readonly Dictionary<PluginSourceImageBoard, string> _sourceOwners = new();
    private string[] _warnings = Array.Empty<string>();
    private bool _disposed;

    internal IPluginProfileStore ProfileStore { get; }
    internal IPluginCompilationService CompilationService { get; }
    internal IPluginInstallationInventory Inventory { get; }
    internal PluginWorkerManager Runtime { get; }
    internal string PluginDirectory { get; }
    internal IReadOnlyList<PluginSourceImageBoard> Sources { get; private set; } = Array.Empty<PluginSourceImageBoard>();
    internal IReadOnlyList<string> Warnings => Volatile.Read(ref _warnings);

    internal PluginSession(string profileDirectory, ProfileLease profileLease, Config config, DownloadQueue queue, ILogger logger,
        Func<HttpClient>? transportFactory = null,
        Func<string, string, SourceMediaIntegrityPolicyService>? sourceMediaIntegrityPolicyFactory = null,
        SemaphoreSlim? sourcePermissionCommitGate = null)
    {
        _queue = queue; _logger = logger;
        _sourceMediaIntegrityPolicyFactory = sourceMediaIntegrityPolicyFactory;
        string applicationDirectory = PayloadDirectory();
        PluginDirectory = Path.Combine(profileDirectory, "plugins");
        _shippedDirectory = Path.Combine(applicationDirectory, "plugins");
        _repairRestrictedSourceIds = config.SourceMediaIntegrityPermissions
            .Where(pair => pair.Value.AllowProviderSizeMd5Mismatch).Select(pair => pair.Value.SourceId).Distinct(StringComparer.Ordinal).ToArray();
        string compilerDirectory = Path.Combine(applicationDirectory, "plugin-compiler");
        string executableSuffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        CompilationService = new PluginCompilationService(new PluginCompilerOptions(
            Path.Combine(compilerDirectory, "MeowChan.PluginCompiler" + executableSuffix), compilerDirectory,
            Path.Combine(profileDirectory, "plugin-cache")));
        Inventory = new PluginInstallationInventory();
        ProfileStore = new PluginProfileStore(profileDirectory, ExperimentalSourceActivationPolicy.InitiallyEnabled, () =>
        {
            lock (_state)
                return Sources.Where(source => source.MediaIntegrityPolicy is SourceMediaIntegrityPolicyService policy && policy.GetView().IsGranted)
                    .Select(source => source.Name).Concat(_repairRestrictedSourceIds).Distinct(StringComparer.Ordinal).ToArray();
        }, sourcePermissionCommitGate);
        Runtime = new PluginWorkerManager(new PluginWorkerOptions
        {
            MaximumProductionWorkers = config.PluginMaximumProductionWorkers,
            IdleTimeout = TimeSpan.FromMinutes(config.PluginWorkerIdleMinutes),
            SourceHeapLimitBytes = (long)config.PluginSourceHeapMiB * 1024 * 1024,
            FilterHeapLimitBytes = (long)config.PluginFilterHeapMiB * 1024 * 1024,
            ExternalTemporaryBytes = () => _filterStages?.Snapshot.StagedBytes ?? 0
        });
        _filterStages = new FilterStageCoordinator(profileDirectory, profileLease,
            new FilterStageOptions { WorkerTemporaryBytes = Runtime.GetTemporaryBytes });
        string working = Path.Combine(profileDirectory, "plugin-workers");
        _launch = new PluginWorkerLaunch(Path.Combine(applicationDirectory, "plugin-worker", "MeowChan.PluginWorker" + executableSuffix),
            Array.Empty<string>(), working);
        // The optional factory only replaces network transport; this session owns the returned client.
        _transport = transportFactory?.Invoke() ?? PluginHostHttp.CreateTransport();
        ProfileStore.LifecycleChanged += OnLifecycleChanged;
        ProfileStore.GrantChanged += OnGrantChanged;
    }

    private static string PayloadDirectory()
    {
        DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);
        if (OperatingSystem.IsMacOS() && directory.Name == "MacOS" && directory.Parent?.Name == "Contents"
            && directory.Parent.Parent?.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) == true)
        {
            string payload = Path.Combine(directory.Parent.FullName, "Resources", "application");
            if (Directory.Exists(payload)) return payload;
        }
        return directory.FullName;
    }

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        bool succeeded = false;
        try
        {
            await StartCoreAsync(cancellationToken).ConfigureAwait(false);
            succeeded = true;
        }
        finally { LogStartupPhase("total", null, started, succeeded); }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        CancellationToken token = linked.Token;
        Directory.CreateDirectory(PluginDirectory);
        Directory.CreateDirectory(_launch.WorkingDirectory);
        PluginProfileSnapshot previous = await MeasureStartupAsync("profile-read", null, () => ProfileStore.ReadAsync(token)).ConfigureAwait(false);
        PluginInventory shipped = await MeasureStartupAsync("inventory-shipped", null, () => Inventory.ScanAsync(_shippedDirectory, token)).ConfigureAwait(false);
        PluginInventory local = await MeasureStartupAsync("inventory-local", null, () => Inventory.ScanAsync(PluginDirectory, token)).ConfigureAwait(false);
        List<string> warnings = shipped.Diagnostics.Concat(local.Diagnostics).Select(item => item.Message).ToList();
        List<PluginCandidateObservation> observations = new();
        List<DeliveredCandidateProvenance> delivered = new();
        HashSet<string> shippedIds = shipped.Candidates.Where(item => item.Kind == PluginCandidateKind.PrebuiltManifest)
            .Select(item => item.CandidateId).ToHashSet(StringComparer.Ordinal);
        foreach (PluginInstallationCandidate candidate in shipped.Candidates.Where(item => item.Kind == PluginCandidateKind.PrebuiltManifest)
            .Concat(local.Candidates))
        {
            token.ThrowIfCancellationRequested();
            PluginActivationSnapshot? previousOwner = previous.Plugins.FirstOrDefault(plugin =>
                plugin.Candidates.Any(item => item.CandidateId == candidate.CandidateId));
            PluginCandidateObservation observation = await PluginStartupDiagnostics.ObserveAsync(candidate, previousOwner,
                ct => candidate.Kind == PluginCandidateKind.PrebuiltManifest
                    ? ((IPluginBundleInstaller)CompilationService).ImportBundleAsync(candidate.Path, ct)
                    : CompilationService.BuildAsync(candidate.Path, cancellationToken: ct),
                DescribeTestAsync, _logger, token).ConfigureAwait(false);
            if (shippedIds.Contains(candidate.CandidateId) && observation is { Problem: null, Artifact: not null, Description: not null })
            {
                try
                {
                    delivered.Add(await PluginInstallationInventory.RecordDeliveredAsync(_shippedDirectory, candidate,
                        observation.Artifact, observation.Description, token).ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
                {
                    observation = observation with { Problem = "Delivered provenance validation failed: " + exception.Message };
                }
            }
            observations.Add(observation);
            if (observation.Problem is not null) warnings.Add(observation.Problem);
        }
        PluginProfileSnapshot activated = await MeasureStartupAsync("activation", null,
            () => ProfileStore.ApplyBundledStartupAsync(observations, delivered, _repairRestrictedSourceIds, previous.Revision, token)).ConfigureAwait(false);
        foreach (PluginActivationSnapshot activation in activated.Plugins)
            PluginStartupDiagnostics.LogActivationProblem(_logger, activation);
        long adaptersStarted = Stopwatch.GetTimestamp();
        lock (_state) _grants = activated;
        List<(PluginActivationSnapshot Activation, PluginDescription Description)> descriptions = activated.Plugins
            .Select(plugin => (Activation: plugin, Description: plugin.Active?.Description ?? plugin.LastGood?.Description))
            .Where(item => item.Description != null).Select(item => (item.Activation, item.Description!)).ToList();
        // Activation owns explicit conflict choices. Keep an unavailable source's last declaration
        // for restoration, without starting its old implementation as an implicit fallback.
        List<PluginSourceImageBoard> sources = new();
        foreach ((PluginActivationSnapshot activation, PluginDescription description) in descriptions)
        {
            lock (_state)
            {
                _active[activation.PluginId] = activation;
                _clientGates[activation.PluginId] = new SemaphoreSlim(1, 1);
            }
            if (activation.Problem != null) warnings.Add(description.Name + ": " + activation.Problem);
            foreach (SourceDescription source in description.Sources)
            {
                string[] owners = descriptions.Where(item => item.Description.Sources.Any(value => value.SourceId == source.SourceId))
                    .Select(item => item.Activation.PluginId).Distinct(StringComparer.Ordinal).ToArray();
                if (owners.Length > 1 && (!activated.SelectedSourceOwners.TryGetValue(source.SourceId, out string? selected) || selected != activation.PluginId)) continue;
                HttpClient media = PluginOriginalMediaHttp.CreateClient(_transport, source, _httpPolicy, _logger);
                _services.Add(media);
                PluginSourceImageBoard adapter = new(description, source.SourceId, ct => GetWorkerAsync(activation.PluginId, ct), media, _logger, _queue)
                { MediaIntegrityPolicy = _sourceMediaIntegrityPolicyFactory?.Invoke(activation.PluginId, source.SourceId) };
                if (!activation.Enabled || activation.Active == null) adapter.SetEnabled(false);
                sources.Add(adapter);
                lock (_state) _sourceOwners.Add(adapter, activation.PluginId);
            }
        }
        IReadOnlyList<ValidationIssue> conflicts = SdkValidation.ValidateDescriptions(descriptions.Select(item => item.Description).ToArray());
        foreach (ValidationIssue conflict in conflicts.Where(issue => issue.Code == "route_conflict")) warnings.Add(conflict.Message);
        // A conflicting declaration must never win through registry array order.
        PluginSourceImageBoard[] conflicting = sources.Where(source => sources.Any(other => !ReferenceEquals(other, source)
            && other.SupportedHosts.Intersect(source.SupportedHosts, StringComparer.OrdinalIgnoreCase).Any() && RouteConflict(source, other))).ToArray();
        foreach (PluginSourceImageBoard source in conflicting)
        {
            warnings.Add(source.NiceName + ": conflicting URL ownership; source unavailable.");
            sources.Remove(source);
            lock (_state) _sourceOwners.Remove(source);
            source.Dispose();
        }
        lock (_state)
        {
            Sources = sources.AsReadOnly();
            foreach (PluginSourceImageBoard source in Sources)
                SetSourceMediaIntegrityActive(source, IsSourceMediaIntegrityActive(source));
        }
        Volatile.Write(ref _warnings, warnings.Distinct(StringComparer.Ordinal).ToArray());
        LogStartupPhase("adapters-routing", null, adaptersStarted, true);
    }

    private Task<T> MeasureStartupAsync<T>(string phase, string? candidate, Func<Task<T>> operation) =>
        PluginStartupDiagnostics.MeasureAsync(_logger, phase, candidate, operation);

    private void LogStartupPhase(string phase, string? candidate, long started, bool succeeded) =>
        PluginStartupDiagnostics.LogPhase(_logger, phase, candidate, started, succeeded);

    private static bool RouteConflict(PluginSourceImageBoard left, PluginSourceImageBoard right)
    {
        PluginDescription Wrapper(PluginSourceImageBoard source) => new(source.Name, source.NiceName, "1.0.0", SdkContract.Version,
            "routing", new[] { source.Description }, Array.Empty<FilterDescription>(), new SettingsSchema("1", Array.Empty<SettingDefinition>()), Array.Empty<InformationEntry>());
        return SdkValidation.ValidateDescriptions(new[] { Wrapper(left), Wrapper(right) }).Any(issue => issue.Code == "route_conflict");
    }

    internal Task<PluginDescription> DescribeTestAsync(PluginArtifact artifact, CancellationToken cancellationToken = default) =>
        Runtime.DescribeAsync(_launch, artifact, cancellationToken);

    internal async Task<IPluginWorkerClient> StartTestWorkerAsync(PluginArtifact artifact, PluginConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        PluginDescription description = await DescribeTestAsync(artifact, cancellationToken).ConfigureAwait(false);
        PluginHostHttp http = new(_transport, _httpPolicy, description.Sources);
        try
        {
            IPluginWorkerClient client = await Runtime.StartAsync(_launch, artifact, configuration,
                new HostServices(http, _filterStages, description.PluginId), testWorker: true,
                cancellationToken: cancellationToken, filterWorker: description.Filters.Count > 0).ConfigureAwait(false);
            return new TestWorker(client, http);
        }
        catch { http.Dispose(); throw; }
    }

    private async Task<IPluginWorkerClient> GetWorkerAsync(string pluginId, CancellationToken cancellationToken)
    {
        SemaphoreSlim gate;
        lock (_state) gate = _clientGates[pluginId];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PluginActivationSnapshot activation;
            lock (_state)
            {
                activation = _active[pluginId];
                if (_disposed || !activation.Enabled || activation.Active == null)
                    throw new PluginWorkerException("source_unavailable", activation.Problem ?? "Plugin is disabled.");
                if (_clients.TryGetValue(pluginId, out IPluginWorkerClient? current)) return current;
            }
            PluginReleaseSnapshot release = activation.Active;
            IPluginWorkerClient client = await Runtime.StartAsync(_launch, release.Artifact, release.Configuration,
                CreateServices(release.Description), cancellationToken: cancellationToken, filterWorker: release.Description.Filters.Count > 0).ConfigureAwait(false);
            bool stale;
            lock (_state)
            {
                stale = _disposed || !_active[pluginId].Enabled || _active[pluginId].LifecycleRevision != activation.LifecycleRevision;
                if (!stale) _clients[pluginId] = client;
            }
            if (stale) { await client.DisposeAsync().ConfigureAwait(false); throw new OperationCanceledException("Plugin activation changed.", cancellationToken); }
            return client;
        }
        finally { gate.Release(); }
    }

    private IPluginHostServices CreateServices(PluginDescription description)
    {
        PluginHostHttp http;
        lock (_state)
        {
            if (!_pluginHttp.TryGetValue(description.PluginId, out http!))
            {
                http = new PluginHostHttp(_transport, _httpPolicy, description.Sources);
                _pluginHttp.Add(description.PluginId, http);
                _services.Add(http);
            }
        }
        return new HostServices(http, _filterStages, description.PluginId);
    }

    private void OnLifecycleChanged(PluginLifecycleChange change)
    {
        IPluginWorkerClient? client = null;
        lock (_state)
        {
            if (_disposed || !_active.TryGetValue(change.PluginId, out PluginActivationSnapshot? activation)) return;
            _active[change.PluginId] = activation with { Enabled = change.Enabled, LifecycleRevision = change.LifecycleRevision };
            foreach (KeyValuePair<PluginSourceImageBoard, string> item in _sourceOwners.Where(item => item.Value == change.PluginId))
            {
                bool enabled = change.Enabled && activation.Active != null;
                // Invalidate before returning from the synchronous lifecycle notification.
                SetSourceMediaIntegrityActive(item.Key, enabled);
                item.Key.SetEnabled(enabled);
            }
            if (!change.Enabled) _clients.Remove(change.PluginId, out client);
        }
        if (client != null)
        {
            Task draining = client.DisposeAsync().AsTask();
            lock (_state) _draining.Add(draining);
            _ = draining.ContinueWith(completed => { lock (_state) _draining.Remove(completed); }, TaskScheduler.Default);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_state)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (PluginSourceImageBoard source in Sources)
                if (source.MediaIntegrityPolicy is SourceMediaIntegrityPolicyService policy) policy.Retire();
        }
        ProfileStore.LifecycleChanged -= OnLifecycleChanged;
        ProfileStore.GrantChanged -= OnGrantChanged;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        foreach (PluginSourceImageBoard source in Sources) source.Dispose();
        await Runtime.DisposeAsync().ConfigureAwait(false);
        await _filterStages.DisposeAsync().ConfigureAwait(false);
        if (CompilationService is IAsyncDisposable compiler) await compiler.DisposeAsync().ConfigureAwait(false);
        Task[] draining;
        lock (_state) draining = _draining.ToArray();
        await Task.WhenAll(draining).ConfigureAwait(false);
        foreach (IDisposable service in _services) service.Dispose();
        _transport.Dispose();
        _lifetime.Dispose();
        foreach (SemaphoreSlim gate in _clientGates.Values) gate.Dispose();
    }

    private sealed class HostServices(PluginHostHttp http, FilterStageCoordinator stages, string pluginId) : IPluginHostServices
    {
        public Task<HostHttpResult> SendHttpAsync(HostHttpRequest request, PluginOperationContext operation, CancellationToken cancellationToken) => http.SendAsync(request, cancellationToken);
        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, PluginOperationContext operation, CancellationToken cancellationToken) =>
            stages.ReadImageAsync(pluginId, assetToken, offset, count, operation, cancellationToken);
    }

    private sealed class TestWorker(IPluginWorkerClient client, PluginHostHttp http) : IPluginWorkerClient
    {
        public PluginDescription Description => client.Description;
        public PluginWorkerIdentity Identity => client.Identity;
        public PluginWorkerSnapshot Snapshot => client.Snapshot;
        public Task<CatalogReadResult> ReadCatalogAsync(CatalogReadRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default) => client.ReadCatalogAsync(request, operation, cancellationToken);
        public Task<ThreadReadResult> ReadThreadAsync(ThreadReadRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default) => client.ReadThreadAsync(request, operation, cancellationToken);
        public Task<ImageFilterResult> EvaluateAsync(ImageFilterRequest request, PluginOperationContext operation, CancellationToken cancellationToken = default) => client.EvaluateAsync(request, operation, cancellationToken);
        public Task RetryAsync(CancellationToken cancellationToken = default) => client.RetryAsync(cancellationToken);
        public Task<IReadOnlyList<FieldValidationError>> ValidateSettingsAsync(SettingsValidationRequest request, CancellationToken cancellationToken = default) => client.ValidateSettingsAsync(request, cancellationToken);
        public async ValueTask DisposeAsync() { try { await client.DisposeAsync().ConfigureAwait(false); } finally { http.Dispose(); } }
    }
}

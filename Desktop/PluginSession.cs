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
    // The read-only startup probe, begun at most once before StartAsync (see BeginProbe).
    private Task<StartupProbe>? _probe;
    private CancellationTokenSource? _probeCancellation;

    internal IPluginProfileStore ProfileStore { get; }
    internal IPluginCompilationService CompilationService { get; }
    internal IPluginInstallationInventory Inventory { get; }
    internal PluginWorkerManager Runtime { get; }
    internal string PluginDirectory { get; }
    internal IReadOnlyList<PluginSourceImageBoard> Sources { get; private set; } = Array.Empty<PluginSourceImageBoard>();
    internal IReadOnlyList<string> Warnings => Volatile.Read(ref _warnings);
    // Observation only: the early probe's completion, never its result.
    internal Task? StartupProbeForTests { get { lock (_state) return _probe; } }

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
            // Startup describes use the test-worker admission; the runtime contract allows two.
            MaximumTestWorkers = StartupProbeLanes,
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
        using MeowChan.Diagnostics.PerformanceDiagnostics.Measurement span = PluginStartupDiagnostics.Span("total");
        try
        {
            await StartCoreAsync(cancellationToken).ConfigureAwait(false);
            succeeded = true;
        }
        finally { LogStartupPhase("total", null, started, succeeded); }
    }

    /// <summary>What the read-only probe observed; activation consumes it unchanged and in inventory order.</summary>
    private sealed record StartupProbe(PluginProfileSnapshot Previous, PluginInstallationCandidate[] Ordered,
        HashSet<string> ShippedIds, PluginCandidateObservation[] Probed, List<string> Warnings);

    /// <summary>
    /// Starts the read-only part of plugin startup (profile read, inventory, import, describe) in the background,
    /// at most once, so it overlaps the window construction instead of waiting for it. It never writes the plugin
    /// profile, activates, grants or routes anything: StartAsync awaits it where the probe ran before and then
    /// performs provenance, activation and adapters unchanged. The probe ends with the session lifetime or
    /// <paramref name="cancellationToken"/>; DisposeAsync waits until it has (describe workers reaped).
    /// </summary>
    internal void BeginProbe(CancellationToken cancellationToken)
    {
        lock (_state)
        {
            if (_disposed || _probe != null) return;
            CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
            _probeCancellation = source;
            // Never on the caller's (UI) thread: even the synchronous prefix reads files.
            _probe = Task.Run(() => ProbeAsync(source.Token), CancellationToken.None);
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        CancellationToken token = linked.Token;
        Task<StartupProbe>? early;
        CancellationTokenSource? earlyCancellation;
        lock (_state) { early = _probe; earlyCancellation = _probeCancellation; }
        StartupProbe probe;
        if (early is null) probe = await ProbeAsync(token).ConfigureAwait(false);
        else
        {
            // This start's token also ends the early probe, and startup returns only after the probe has
            // really stopped, as when it ran inline: no describe worker or cache write outlives StartAsync.
            Task<StartupProbe> pending = early;
            using (token.Register(static source => CancelQuietly((CancellationTokenSource)source!), earlyCancellation))
                probe = await MeasureStartupAsync("probe-wait", null, () => pending).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        (PluginProfileSnapshot previous, PluginInstallationCandidate[] ordered, HashSet<string> shippedIds,
            PluginCandidateObservation[] probed, List<string> warnings) = probe;
        List<PluginCandidateObservation> observations = new();
        List<DeliveredCandidateProvenance> delivered = new();
        MeowChan.Diagnostics.PerformanceDiagnostics.Measurement provenance = PluginStartupDiagnostics.Span("provenance");
        for (int index = 0; index < ordered.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            PluginInstallationCandidate candidate = ordered[index];
            PluginCandidateObservation observation = probed[index];
            if (shippedIds.Contains(candidate.CandidateId) && observation is { Problem: null, Artifact: not null, Description: not null })
            {
                try
                {
                    using MeowChan.Diagnostics.PerformanceDiagnostics.Measurement proof = PluginStartupDiagnostics.Span("provenance-candidate",
                        MeowChan.Diagnostics.PerformanceDiagnostics.Enabled ? Path.GetFileName(candidate.Path) : null);
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
        provenance.Dispose();
        PluginStartupDiagnostics.RecordResources("plugin.startup.activation", "begin");
        PluginProfileSnapshot activated = await MeasureStartupAsync("activation", null,
            () => ProfileStore.ApplyBundledStartupAsync(observations, delivered, _repairRestrictedSourceIds, previous.Revision, token)).ConfigureAwait(false);
        PluginStartupDiagnostics.RecordResources("plugin.startup.activation", "end");
        foreach (PluginActivationSnapshot activation in activated.Plugins)
            PluginStartupDiagnostics.LogActivationProblem(_logger, activation);
        long adaptersStarted = Stopwatch.GetTimestamp();
        using MeowChan.Diagnostics.PerformanceDiagnostics.Measurement adapters = PluginStartupDiagnostics.Span("adapters-routing");
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
                // A booru post-search source is served by the booru host services. It is never a chan board adapter,
                // so it must not appear in the chan source registry, URL routing or board/thread watch paths.
                if (source.Capabilities.HasFlag(SourceCapabilities.PostSearch))
                {
                    RegisterBooruSource(activation, description, source, descriptions, activated);
                    continue;
                }
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

    private static void CancelQuietly(CancellationTokenSource source)
    {
        try { source.Cancel(); }
        catch (ObjectDisposedException) { } // The session was disposed; its lifetime already ended the probe.
    }

    // Read-only: creates the plugin/worker directories and fills plugin-cache (its own area), never the profile.
    private async Task<StartupProbe> ProbeAsync(CancellationToken token)
    {
        PluginStartupDiagnostics.RecordResources("plugin.startup.probe", "begin");
        StartupProbe probe = await MeasureStartupAsync("probe", null, () => ProbeCoreAsync(token)).ConfigureAwait(false);
        PluginStartupDiagnostics.RecordResources("plugin.startup.probe", "end");
        return probe;
    }

    private async Task<StartupProbe> ProbeCoreAsync(CancellationToken token)
    {
        Directory.CreateDirectory(PluginDirectory);
        Directory.CreateDirectory(_launch.WorkingDirectory);
        PluginProfileSnapshot previous = await MeasureStartupAsync("profile-read", null, () => ProfileStore.ReadAsync(token)).ConfigureAwait(false);
        PluginInventory shipped = await MeasureStartupAsync("inventory-shipped", null, () => Inventory.ScanAsync(_shippedDirectory, token)).ConfigureAwait(false);
        PluginInventory local = await MeasureStartupAsync("inventory-local", null, () => Inventory.ScanAsync(PluginDirectory, token)).ConfigureAwait(false);
        List<string> warnings = shipped.Diagnostics.Concat(local.Diagnostics).Select(item => item.Message).ToList();
        HashSet<string> shippedIds = shipped.Candidates.Where(item => item.Kind == PluginCandidateKind.PrebuiltManifest)
            .Select(item => item.CandidateId).ToHashSet(StringComparer.Ordinal);
        PluginInstallationCandidate[] ordered = shipped.Candidates.Where(item => item.Kind == PluginCandidateKind.PrebuiltManifest)
            .Concat(local.Candidates).ToArray();
        // Probes overlap in bounded lanes; observations, provenance and activation keep inventory order.
        SemaphoreSlim lanes = new(StartupProbeLanes, StartupProbeLanes);
        using IPluginBundleImportBatch imports = ((IPluginBundleInstaller)CompilationService).BeginImportBatch();
        using MeowChan.Diagnostics.PerformanceDiagnostics.Measurement pass = PluginStartupDiagnostics.Span("probe-pass");
        PluginCandidateObservation[] probed = await Task.WhenAll(ordered.Select(async candidate =>
        {
            await lanes.WaitAsync(token).ConfigureAwait(false);
            try
            {
                PluginActivationSnapshot? previousOwner = previous.Plugins.FirstOrDefault(plugin =>
                    plugin.Candidates.Any(item => item.CandidateId == candidate.CandidateId));
                return await PluginStartupDiagnostics.ObserveAsync(candidate, previousOwner,
                    ct => candidate.Kind == PluginCandidateKind.PrebuiltManifest
                        ? imports.ImportBundleAsync(candidate.Path, ct)
                        : CompilationService.BuildAsync(candidate.Path, cancellationToken: ct),
                    (artifact, ct) => DescribeStartupAsync(previous, candidate.CandidateId, artifact, ct), _logger, token).ConfigureAwait(false);
            }
            finally { lanes.Release(); }
        })).ConfigureAwait(false);
        imports.Dispose(); // The shared toolchain result serves this probe pass only.
        return new(previous, ordered, shippedIds, probed, warnings);
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

    // Measured 11.11.2026: two lanes roughly halve the first-start describe phase; more were not measured.
    private const int StartupProbeLanes = 2;

    private Task<PluginDescription> DescribeStartupAsync(PluginProfileSnapshot previous, string candidateId,
        PluginArtifact artifact, CancellationToken cancellationToken)
    {
        if (PluginStartupDiagnostics.ReusableDescription(previous, candidateId, artifact) is not { } known)
            return DescribeTestAsync(artifact, cancellationToken);
        _logger.Debug("Plugin startup description reused candidate {PluginStartupCandidateId}", candidateId);
        return Task.FromResult(known);
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
        // A post-search plugin runs with the booru host services that the desktop composition also uses.
        if (TryGetBooruServices(description.PluginId, out IPluginHostServices booru)) return booru;
        return new HostServices(GetPluginHttp(description), _filterStages, description.PluginId);
    }

    private PluginHostHttp GetPluginHttp(PluginDescription description)
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
        return http;
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
        NotifyBooruLifecycle(change.PluginId);
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
        // An early probe that StartAsync never awaited (or abandoned) ends here, before the runtime and
        // compiler go away: describe workers are reaped and no cache write outlives the session.
        Task<StartupProbe>? probe;
        lock (_state) probe = _probe;
        if (probe != null)
        {
            try { await probe.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception exception) { _logger.Debug(exception, "The plugin startup probe ended with a failure during disposal."); }
        }
        _probeCancellation?.Dispose();
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
        // Pass the typed operation through: post-search requests resolve the booru job bound
        // to it for the whole operation and fail closed without one.
        public Task<HostHttpResult> SendHttpAsync(HostHttpRequest request, PluginOperationContext operation, CancellationToken cancellationToken) => http.SendAsync(request, operation, cancellationToken);
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

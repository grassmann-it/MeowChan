using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Desktop;

/// <summary>
/// One immutable, fully captured session state. The presentation layer reads only this reference;
/// it never waits for queue, store or registry locks. Epoch identifies the owning host and Version is
/// strictly increasing within it, so a result from an older host or an older capture is recognizable.
/// </summary>
internal sealed record DesktopSessionProjection(long Epoch, long Version, DesktopSessionView View,
    bool IsPaused, bool IsPauseTransitioning, long CapturedAtTimestamp, string? CaptureError)
{
    /// <summary>True when the last capture failed and View still holds the last complete data.</summary>
    public bool IsStale => CaptureError != null;
    public (int Configured, int Effective, int Active, int DeferredSlots) MediaConcurrency { get; init; }
    public IReadOnlyList<long> HighPriorityWatches { get; init; } = System.Array.Empty<long>();
    public IReadOnlyList<string> AdmissionExamples { get; init; } = Array.AsReadOnly(Array.Empty<string>());
    public IReadOnlyList<DiagnosticTarget> CheckableTargets { get; init; } = Array.AsReadOnly(Array.Empty<DiagnosticTarget>());
    public IReadOnlyList<ProviderDescriptor> Providers { get; init; } = Array.AsReadOnly(Array.Empty<ProviderDescriptor>());
    public IReadOnlyList<MeowChan.Downloader.DownloadTargetWaitState> TargetWaitStates { get; init; }
        = Array.AsReadOnly(Array.Empty<MeowChan.Downloader.DownloadTargetWaitState>());
    public IReadOnlyList<SourceMediaIntegrityPolicyView> SourcePolicies { get; init; }
        = Array.AsReadOnly(Array.Empty<SourceMediaIntegrityPolicyView>());

    /// <summary>
    /// True when <paramref name="current"/> differs from <paramref name="previous"/> in something other than
    /// progress counters: lifecycle, pause, staleness, warnings, the set of watches, or a watch's activity,
    /// source state or failure indication. Such changes must not wait behind the progress spacing.
    /// </summary>
    public static bool RequiresPrompt(DesktopSessionProjection previous, DesktopSessionProjection current)
    {
        if (previous.Epoch != current.Epoch || previous.IsPaused != current.IsPaused
            || previous.IsPauseTransitioning != current.IsPauseTransitioning || previous.CaptureError != current.CaptureError
            || previous.View.State != current.View.State || previous.View.StartupError != current.View.StartupError
            || !previous.View.Warnings.SequenceEqual(current.View.Warnings, StringComparer.Ordinal)
            || previous.MediaConcurrency.Configured != current.MediaConcurrency.Configured
            || previous.MediaConcurrency.Effective != current.MediaConcurrency.Effective
            || !previous.HighPriorityWatches.SequenceEqual(current.HighPriorityWatches)
            || previous.View.Threads.Count != current.View.Threads.Count || previous.View.Boards.Count != current.View.Boards.Count)
            return true;
        for (int index = 0; index < current.View.Threads.Count; index++)
        {
            DesktopThreadEntry before = previous.View.Threads[index], after = current.View.Threads[index];
            if (before.WatchId != after.WatchId || before.Snapshot.Activity != after.Snapshot.Activity
                || before.Snapshot.SourceState != after.Snapshot.SourceState
                || before.Snapshot.MediaFailures.Count != after.Snapshot.MediaFailures.Count
                || before.Snapshot.LastCheckFailure != after.Snapshot.LastCheckFailure
                || before.Snapshot.MediaFailures.Any(pair => !after.Snapshot.MediaFailures.TryGetValue(pair.Key, out var next) || pair.Value != next)
                || before.Snapshot.Work?.Intent != after.Snapshot.Work?.Intent
                || !WorkFailures(before.Snapshot.Work).SequenceEqual(WorkFailures(after.Snapshot.Work))) return true;
        }
        for (int index = 0; index < current.View.Boards.Count; index++)
        {
            DesktopBoardEntry before = previous.View.Boards[index], after = current.View.Boards[index];
            if (before.WatchId != after.WatchId || before.Snapshot.LastCheckFailure != after.Snapshot.LastCheckFailure)
                return true;
        }
        return false;
    }
    private static IEnumerable<(string Id, string Variant, long Generation, string? Acquisition, string? Failure, int? HttpStatus,
        MeowChan.Persistence.MediaWorkStatus Status)> WorkFailures(ThreadWorkSnapshot? work)
        => (work?.Media ?? Array.Empty<MeowChan.Persistence.MediaWork>())
            .Where(item => item.Failure != null || item.Status is MeowChan.Persistence.MediaWorkStatus.NeedsIntervention or MeowChan.Persistence.MediaWorkStatus.Expired)
            .OrderBy(item => item.MediaIdentifier, StringComparer.Ordinal).ThenBy(item => item.Variant, StringComparer.Ordinal)
            .ThenBy(item => item.Generation).ThenBy(item => item.AcquisitionRevisionKey, StringComparer.Ordinal)
            .ThenBy(item => item.Failure, StringComparer.Ordinal).ThenBy(item => item.HttpStatus).ThenBy(item => item.Status)
            .Select(item => (item.MediaIdentifier, item.Variant, item.Generation, item.AcquisitionRevisionKey, item.Failure, item.HttpStatus, item.Status));

}

/// <summary>Counters for diagnostics and tests (PF-00 measurement contract). Values are monotonic.</summary>
internal readonly record struct DesktopProjectionStats(long Captures, long CoalescedRequests, long Failures,
    long LastCaptureTicks, long MaxCaptureTicks);

internal sealed partial class DesktopSessionHost
{
    private static long s_nextEpoch;
    private readonly object _viewCapture = new();
    private readonly object _captureRequests = new();
    private readonly long _epoch = Interlocked.Increment(ref s_nextEpoch);
    private TaskCompletionSource<DesktopSessionProjection>? _pendingCapture;
    private Task _captureLoop = Task.CompletedTask;
    private Task _lifecycleNotification = Task.CompletedTask;
    private bool _captureRunning;
    private bool _captureAdmissionClosed;
    private long _projectionVersion;
    private volatile bool _projectionClosed;
    private long _captureCount, _coalescedRequests, _captureFailures, _lastCaptureTicks, _maxCaptureTicks;
    private DesktopSessionProjection? _published;
    public event Action? ProjectionPublished;
    internal DesktopSessionProjection Projection => Volatile.Read(ref _published) ?? InitialProjection();
    internal DesktopProjectionStats ProjectionStats => new(Interlocked.Read(ref _captureCount),
        Interlocked.Read(ref _coalescedRequests), Interlocked.Read(ref _captureFailures),
        Interlocked.Read(ref _lastCaptureTicks), Interlocked.Read(ref _maxCaptureTicks));

    internal bool CaptureLockHeldForTest => Monitor.IsEntered(_viewCapture) || _captureLockHeld;
    private volatile bool _captureLockHeld;
    internal void HoldCaptureLockForTest(ManualResetEventSlim release)
    {
        lock (_viewCapture)
        {
            _captureLockHeld = true;
            try { release.Wait(); } finally { _captureLockHeld = false; }
        }
    }

    private DesktopSessionProjection InitialProjection() => new(_epoch, 0,
        new DesktopSessionView(Array.AsReadOnly(Array.Empty<DesktopThreadEntry>()),
            Array.AsReadOnly(Array.Empty<DesktopBoardEntry>()), Array.AsReadOnly(Array.Empty<string>()),
            DesktopSessionState.Starting, null),
        false, false, Stopwatch.GetTimestamp(), null);

    private void RequestProjection() => _ = CaptureProjectionAsync();

    /// <summary>All callers, including forced startup/command captures, share one worker and one pending batch.</summary>
    internal Task<DesktopSessionProjection> CaptureProjectionAsync()
    {
        lock (_captureRequests)
        {
            if (_captureAdmissionClosed) return Task.FromResult(Projection);
            if (_pendingCapture == null)
                _pendingCapture = new(TaskCreationOptions.RunContinuationsAsynchronously);
            else Interlocked.Increment(ref _coalescedRequests);
            Task<DesktopSessionProjection> result = _pendingCapture.Task;
            if (!_captureRunning)
            {
                _captureRunning = true;
                _captureLoop = Task.Run(RunProjectionLoop);
            }
            return result;
        }
    }

    private void RunProjectionLoop()
    {
        while (true)
        {
            TaskCompletionSource<DesktopSessionProjection> batch;
            lock (_captureRequests)
            {
                if (_pendingCapture == null) { _captureRunning = false; return; }
                batch = _pendingCapture;
                _pendingCapture = null;
            }
            // CaptureAndPublish retains last complete data on errors. Every waiter completes with that
            // same visible stale frame; no failed fire-and-forget task or separate forced-capture queue.
            batch.TrySetResult(CaptureAndPublish());
        }
    }

    private DesktopSessionProjection CaptureAndPublish()
    {
        DesktopSessionProjection result;
        long started = Stopwatch.GetTimestamp();
        long request = MeowChan.Diagnostics.PerformanceDiagnostics.NextId();
        using var measurement = MeowChan.Diagnostics.PerformanceDiagnostics.Measure("host.projection.capture", request, epoch: _epoch);
        lock (_viewCapture)
        {
            DesktopSessionProjection previous = Projection;
            try
            {
                DesktopSessionView view = GetViewCore();
                DiagnosticTarget[] checkable;
                lock (_projection)
                    checkable = _threadHandles.Where(pair => pair.Value is MeowChan.Interfaces.ICheckRequestSource)
                        .Select(pair => new DiagnosticTarget(DiagnosticTargetKind.Thread, pair.Key))
                        .Concat(_boardHandles.Where(pair => pair.Value is MeowChan.Interfaces.ICheckRequestSource)
                            .Select(pair => new DiagnosticTarget(DiagnosticTargetKind.Board, pair.Key))).ToArray();
                KeyValuePair<long, MeowChan.Interfaces.IThread>[] handles;
                lock (_projection) handles = _threadHandles.ToArray();
                long[] highPriority = handles.Where(pair => _downloadQueue.GetUserPriority(pair.Value) == MeowChan.Downloader.UserDownloadPriority.High)
                    .Select(pair => pair.Key).OrderBy(id => id).ToArray();
                ProviderDescriptor[] providers = _registry.GetProviderDescriptors().Select(provider => provider with
                { SupportedHosts = Array.AsReadOnly(provider.SupportedHosts.ToArray()) }).ToArray();
                result = new DesktopSessionProjection(_epoch, 0, view, _registry.IsPaused,
                    IsPauseTransitioning, Stopwatch.GetTimestamp(), null)
                {
                    MediaConcurrency = _downloadQueue.GetConcurrencySnapshot(),
                    HighPriorityWatches = Array.AsReadOnly(highPriority),
                    CheckableTargets = Array.AsReadOnly(checkable),
                    Providers = Array.AsReadOnly(providers),
                    AdmissionExamples = CaptureAdmissionExamples(providers, previous),
                    TargetWaitStates = Array.AsReadOnly(_downloadQueue.TargetWaitStates.ToArray()),
                    SourcePolicies = CaptureSourceMediaIntegrityPolicies()
                };
            }
            catch (Exception exception)
            {
                Interlocked.Increment(ref _captureFailures);
                _logger.Error(exception, "Capturing the desktop session projection failed.");
                string message = $"Session view could not be refreshed: {exception.Message}";
                result = previous with
                {
                    View = previous.View with { Warnings = Array.AsReadOnly(previous.View.Warnings
                        .Where(warning => !warning.StartsWith("Session view could not be refreshed:", StringComparison.Ordinal))
                        .Append(message).ToArray()) },
                    IsPauseTransitioning = IsPauseTransitioning, CaptureError = exception.Message
                };
            }
            // Lifecycle is authoritative at publication time, including shutdown accepted during a slow capture.
            lock (_lifecycle)
            {
                if (_projectionClosed) return Projection;
                result = result with { Version = ++_projectionVersion,
                    View = result.View with { State = _state, StartupError = _startupError } };
                Volatile.Write(ref _published, result);
            }
            long elapsed = Stopwatch.GetTimestamp() - started;
            Interlocked.Increment(ref _captureCount);
            Interlocked.Exchange(ref _lastCaptureTicks, elapsed);
            long max;
            while (elapsed > (max = Interlocked.Read(ref _maxCaptureTicks))
                && Interlocked.CompareExchange(ref _maxCaptureTicks, elapsed, max) != max) { }
            MeowChan.Diagnostics.PerformanceDiagnostics.Record("host.projection.publish", result.IsStale ? "stale" : "complete",
                request, epoch: _epoch, revision: result.Version, value: elapsed);
        }
        NotifyProjectionObservers();
        return result;
    }

    // Parser/core access stays on the existing single capture worker. Reuse examples until the
    // registered source identities/hosts change; ordinary progress captures do no parser work.
    private IReadOnlyList<string> CaptureAdmissionExamples(IReadOnlyList<ProviderDescriptor> providers,
        DesktopSessionProjection previous)
    {
        if (previous.Version > 0 && previous.Providers.Count == providers.Count
            && providers.Select((provider, index) => provider.SourceId == previous.Providers[index].SourceId
                && provider.BaseUri == previous.Providers[index].BaseUri
                && provider.SupportedHosts.SequenceEqual(previous.Providers[index].SupportedHosts, StringComparer.OrdinalIgnoreCase)).All(equal => equal))
            return previous.AdmissionExamples;
        var examples = new List<string>();
        foreach (ProviderDescriptor provider in providers)
        {
            foreach (string host in provider.SupportedHosts.Prepend(provider.BaseUri.Host).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (string path in new[] { "/b/thread/123456", "/b/res/123456.html", "/b/" })
                {
                    try
                    {
                        string candidate = new UriBuilder(provider.BaseUri.Scheme, host) { Path = path }.Uri.AbsoluteUri;
                        if (_registry.ClassifyAdmission(candidate) is not { } accepted) continue;
                        examples.Add(accepted.Url);
                        break;
                    }
                    catch (Exception error) when (error is ArgumentException or UriFormatException)
                    {
                        // These invented paths are optional repair hints. A custom source may reject
                        // one even when it supports real watches. Skip only that invalid example;
                        // errors in GetViewCore or any other core capture still produce CaptureError.
                        _logger.Debug(error, "Source {SourceId} rejected synthetic admission example {Host}{Path}.",
                            provider.SourceId, host, path);
                        MeowChan.Diagnostics.PerformanceDiagnostics.Record("host.admission.example", "invalid",
                            watch: provider.SourceId, asset: host, epoch: _epoch);
                    }
                }
            }
        }
        return Array.AsReadOnly(examples.ToArray());
    }

    // A lifecycle transition must be visible even while a persistence-bound capture is held.
    private void PublishLifecycleState(bool notifyOnWorker = false)
    {
        lock (_lifecycle)
        {
            if (_projectionClosed) return;
            DesktopSessionProjection previous = Projection;
            Volatile.Write(ref _published, previous with { Version = ++_projectionVersion,
                View = previous.View with { State = _state, StartupError = _startupError } });
        }
        if (notifyOnWorker) _lifecycleNotification = Task.Run(NotifyProjectionObservers);
        else NotifyProjectionObservers();
    }

    private async Task CloseProjectionAsync()
    {
        Task capture;
        lock (_captureRequests)
        {
            _captureAdmissionClosed = true;
            capture = _captureLoop;
        }
        await capture.ConfigureAwait(false);
    }

    private void NotifyProjectionObservers()
    {
        NotifyStateObservers();
        Action? observers = ProjectionPublished;
        if (observers == null) return;
        foreach (Action observer in observers.GetInvocationList())
        {
            try { observer(); }
            catch (Exception exception) { _logger.Error(exception, "Desktop projection observer failed."); }
        }
    }
}

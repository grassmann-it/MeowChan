using System.Diagnostics;
using System.Text.Json;
using Avalonia.Threading;
using MeowChan.Diagnostics;

namespace MeowChan.DesktopSmoke;

internal sealed record Distribution(int Count, double Median, double P95, double Maximum, double Mean, double StdDev)
{
    internal static Distribution From(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        if (sorted.Length == 0) return new(0, 0, 0, 0, 0, 0);
        double mean = sorted.Average();
        return new(sorted.Length, sorted[(int)Math.Ceiling(sorted.Length * .5) - 1], sorted[(int)Math.Ceiling(sorted.Length * .95) - 1],
            sorted[^1], mean, Math.Sqrt(sorted.Average(value => (value - mean) * (value - mean))));
    }
}
internal sealed record ResourceSample(long Timestamp, double CpuMilliseconds, long AllocatedBytes, long HeapBytes,
    long WorkingSetBytes, long PrivateBytes, int Gen0, int Gen1, int Gen2, int OsThreads, int Handles,
    int PoolThreads, long PoolPending, long PoolCompleted, int PoolAvailableWorkers, int PoolAvailableIo,
    int PoolMaxWorkers, int PoolMaxIo, long TransferBytes, long Commits, long CommittedBytes);
internal sealed record NavigationSample(long Request, string Asset, long IntentAt, long AcceptedAt, long ReadyAt,
    double ReadyMilliseconds, bool IdentityCorrect);
internal sealed record ShellPhase(string Name, long StartedAt, long FinishedAt, Distribution PingLagMilliseconds,
    double MaxUiGapMilliseconds, Distribution NavigationMilliseconds, long TransferBytes, long CommitCount,
    long CommittedBytes, double TransferBytesPerSecond, double CommitsPerSecond, NavigationSample[] Navigation);

/// <summary>Callable harness measurement boundary. Dedicated sampler + UI ping threads; IO never runs on dispatcher.
/// UI render-ready timings are not presentation proof; separate rendered-pixel checks run outside measured phases.</summary>
internal sealed class ShellLoadMeasurement : IDisposable
{
    private readonly PerformanceDiagnostics.DiagnosticRun _run;
    private readonly UiResponsivenessProbe _probe;
    private readonly Thread _sampler;
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly ShellLoadTransport _transport;
    private readonly HeldCommitStore _store;
    private readonly List<ResourceSample> _resources = new();
    private readonly object _sync = new();
    private long _sequence, _lastAnswer, _resourceDrops, _sampleErrors;
    private int _disposed;
    private string _stage = "startup";
    internal IReadOnlyList<UiPingSample> PingSamples => _probe.Samples;
    internal List<ShellPhase> Phases { get; } = new();
    internal List<object> PixelChecks { get; } = new();
    internal string? Failure { get; set; }

    internal ShellLoadMeasurement(ShellLoadTransport transport, HeldCommitStore store)
    {
        _transport = transport; _store = store;
        string source = Environment.GetEnvironmentVariable("MEOWCHAN_SHELL_LOAD_SOURCE_HASH") ?? "unbound-direct-run";
        _run = PerformanceDiagnostics.BeginRun(Environment.GetEnvironmentVariable("MEOWCHAN_SHELL_LOAD_RUN_ID") ?? Guid.NewGuid().ToString("N"),
            source, ShellLoadFixture.Hash, capacity: 8192);
        _lastAnswer = Stopwatch.GetTimestamp();
        _probe = new(action => Dispatcher.UIThread.Post(action, DispatcherPriority.Normal), TimeSpan.FromMilliseconds(20),
            TimeSpan.FromSeconds(10), report => PerformanceDiagnostics.Record("ui.hard-stall", "probe-thread-stack", value: (long)report.UnansweredMilliseconds),
            onAnswer: sample =>
            {
                Interlocked.Exchange(ref _sequence, sample.Sequence);
                Interlocked.Exchange(ref _lastAnswer, sample.ExecutedAt);
                PerformanceDiagnostics.Record("ui.ping", "executed", revision: sample.Sequence, value: sample.ExecutedAt - sample.SentAt);
            });
        _sampler = new Thread(SampleLoop) { IsBackground = true, Name = "PF00 resource and watchdog heartbeat writer" };
        _probe.Start(); _sampler.Start();
    }

    internal async Task<ShellPhase> MeasureAsync(string name, TimeSpan duration, Func<Task<NavigationSample>> interact)
    {
        Volatile.Write(ref _stage, name);
        long start = Stopwatch.GetTimestamp();
        long bytes = _transport.Bytes, commits = _store.Successes, durable = _store.CommittedBytes;
        List<NavigationSample> navigation = new();
        PerformanceDiagnostics.Record("harness.phase", name + ".begin");
        while (Stopwatch.GetElapsedTime(start) < duration)
        {
            navigation.Add(await interact());
            await Task.Delay(40);
        }
        long end = Stopwatch.GetTimestamp();
        UiPingSample[] pings = _probe.Samples.Where(sample => sample.ExecutedAt >= start && sample.ExecutedAt <= end).ToArray();
        // Include both phase boundaries so late/absent answers remain visible.
        long previous = start, maxGap = 0;
        foreach (UiPingSample ping in pings) { maxGap = Math.Max(maxGap, ping.ExecutedAt - previous); previous = ping.ExecutedAt; }
        maxGap = Math.Max(maxGap, end - previous);
        double seconds = Stopwatch.GetElapsedTime(start, end).TotalSeconds;
        ShellPhase phase = new(name, start, end, Distribution.From(pings.Select(ping => ping.LagMilliseconds)),
            maxGap * 1000d / Stopwatch.Frequency, Distribution.From(navigation.Select(value => value.ReadyMilliseconds)),
            _transport.Bytes - bytes, _store.Successes - commits, _store.CommittedBytes - durable,
            (_transport.Bytes - bytes) / seconds, (_store.Successes - commits) / seconds, navigation.ToArray());
        Phases.Add(phase);
        PerformanceDiagnostics.Record("harness.phase", name + ".end", value: end - start);
        return phase;
    }

    private void SampleLoop()
    {
        using Process process = Process.GetCurrentProcess();
        string? heartbeat = Environment.GetEnvironmentVariable("MEOWCHAN_SHELL_LOAD_HEARTBEAT");
        int iteration = 0;
        long Safe(Func<long> read)
        {
            try { return read(); } catch (Exception error) when (error is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
            { Interlocked.Increment(ref _sampleErrors); return -1; }
        }
        while (!_stop.IsSet)
        {
            try
            {
                process.Refresh();
                ThreadPool.GetAvailableThreads(out int availableWorkers, out int availableIo);
                ThreadPool.GetMaxThreads(out int maxWorkers, out int maxIo);
                ResourceSample sample = new(Stopwatch.GetTimestamp(), process.TotalProcessorTime.TotalMilliseconds,
                    GC.GetTotalAllocatedBytes(precise: false), GC.GetTotalMemory(forceFullCollection: false),
                    Safe(() => process.WorkingSet64), Safe(() => process.PrivateMemorySize64), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
                    (int)Safe(() => process.Threads.Count), (int)Safe(() => process.HandleCount), ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount,
                    ThreadPool.CompletedWorkItemCount, availableWorkers, availableIo, maxWorkers, maxIo,
                    _transport.Bytes, _store.Successes, _store.CommittedBytes);
                lock (_sync)
                {
                    if (_resources.Count == 1024) { _resources.RemoveAt(0); _resourceDrops++; }
                    _resources.Add(sample);
                }
                if (heartbeat != null) AtomicJson(heartbeat, new { pid = Environment.ProcessId,
                    uiSequence = Interlocked.Read(ref _sequence), lastUiAt = Interlocked.Read(ref _lastAnswer),
                    timestamp = Stopwatch.GetTimestamp(), frequency = Stopwatch.Frequency, stage = Volatile.Read(ref _stage),
                    sample, diagnostics = _probe.Summarize() });
                // Bounded raw evidence survives a hung UI or hard kill; no dump or screenshot in timed loops.
                if (heartbeat != null && iteration++ % 10 == 0)
                {
                    ResourceSample[] current;
                    lock (_sync) current = _resources.ToArray();
                    AtomicJson(Path.Combine(Path.GetDirectoryName(heartbeat)!, "measurement-progress.json"), new {
                        diagnostics = _run.Snapshot(), pings = _probe.Samples, resources = current,
                        resourceDrops = _resourceDrops, sampleErrors = _sampleErrors, stage = Volatile.Read(ref _stage) });
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
            { Interlocked.Increment(ref _sampleErrors); }
            if (_stop.Wait(TimeSpan.FromMilliseconds(100))) break;
        }
    }

    internal static void AtomicJson(string path, object value)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value));
        File.Move(temporary, path, overwrite: true);
    }

    internal void WriteReport()
    {
        string? path = Environment.GetEnvironmentVariable("MEOWCHAN_SHELL_LOAD_REPORT");
        if (path == null) return;
        ResourceSample[] resources;
        lock (_sync) resources = _resources.ToArray();
        AtomicJson(path, new { schema = 1, fixtureVersion = ShellLoadFixture.Version, fixtureHash = ShellLoadFixture.Hash,
            scenario = ShellLoadFixture.Scenario, conditionalStoreCapability = _store.GetType().GetInterfaces().Any(type => type.Name == "IConditionalWorkStateStore"), holdMilliseconds = ShellLoadFixture.HoldMilliseconds,
            requestedCadenceMilliseconds = Environment.GetEnvironmentVariable("MEOWCHAN_SHELL_LOAD_CADENCE_MS") ?? "default",
            sourceHash = Environment.GetEnvironmentVariable("MEOWCHAN_SHELL_LOAD_SOURCE_HASH") ?? "unbound-direct-run",
            assemblyHash = Environment.GetEnvironmentVariable("MEOWCHAN_SHELL_LOAD_ASSEMBLY_HASH") ?? "unbound-direct-run",
            binding = "source inventory and assembly SHA256 recorded by runner; compiler provenance must be verified by build owner",
            phases = Phases, pixelChecks = PixelChecks, resources, resourceDrops = _resourceDrops, sampleErrors = _sampleErrors,
            pingSummary = _probe.Summarize(), pings = _probe.Samples, diagnostics = _run.Snapshot(), failure = Failure,
            successfulCommits = _store.Successes, committedBytes = _store.CommittedBytes,
            transferBytes = _transport.Bytes, metadataRequests = _transport.MetadataRequests, mediaRequests = _transport.MediaRequests,
            limits = "bounded synthetic local fixture; no Windows/USB evidence, no cold OS-cache claim; render-ready is not physical presentation" });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Set();
        if (_sampler.Join(TimeSpan.FromSeconds(2))) _stop.Dispose();
        _probe.Dispose();
        try { WriteReport(); } finally { _run.Dispose(); }
    }
}

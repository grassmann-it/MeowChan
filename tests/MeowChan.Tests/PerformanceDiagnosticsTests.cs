using MeowChan.Diagnostics;

namespace MeowChan.Tests;

[CollectionDefinition("PerformanceDiagnostics", DisableParallelization = true)]
public sealed class PerformanceDiagnosticsCollection { }

[Collection("PerformanceDiagnostics")]
public sealed class PerformanceDiagnosticsTests
{
    [Fact]
    public void RingRetainsLatestBoundedEventsAndCountsEvictions()
    {
        using var run = PerformanceDiagnostics.BeginRun("run", "source", "fixture", 16);
        for (int index = 0; index < 40; index++)
            PerformanceDiagnostics.Record("phase", "ok", index, "watch", "asset", 3, 7, index * 2);
        var result = run.Snapshot();
        Assert.Equal(16, result.Events.Length);
        Assert.Equal(24, result.Dropped);
        Assert.Equal(24, result.Events[0].Request);
        Assert.Equal(39, result.Events[^1].Request);
        Assert.All(result.Events, item => { Assert.True(item.Timestamp >= result.StartedAt); Assert.Equal(3, item.Epoch); Assert.Equal(7, item.Revision); });
        Assert.Equal("source", result.SourceHash);
        Assert.Equal("fixture", result.FixtureHash);
    }

    [Fact]
    public void ConcurrentWritersAccountForEveryAttemptWithoutUnboundedQueue()
    {
        using var run = PerformanceDiagnostics.BeginRun("concurrent", "source", "fixture", 16);
        Parallel.For(0, 10000, index => PerformanceDiagnostics.Record("parallel", request: index));
        var result = run.Snapshot();
        Assert.Equal(10000, result.Events.LongLength + result.Dropped);
        Assert.True(result.Events.Length <= 16);
        Assert.True(result.Events.Zip(result.Events.Skip(1)).All(pair => pair.First.Sequence < pair.Second.Sequence));
    }

    [Fact]
    public void LateScopeCannotWriteIntoNewRunAndDisposeIsIdempotent()
    {
        var old = PerformanceDiagnostics.BeginRun("old", "source", "fixture");
        var scope = PerformanceDiagnostics.Measure("scope");
        old.Dispose(); old.Dispose();
        using var current = PerformanceDiagnostics.BeginRun("current", "source", "fixture");
        scope.Dispose(); scope.Dispose();
        Assert.Empty(current.Snapshot().Events);
        Assert.Single(old.Snapshot().Events);
        PerformanceDiagnostics.Record("new");
        Assert.Single(current.Snapshot().Events);
    }

    [Fact]
    public void DisabledMeasurementDoesNotAllocateAndActiveMeasurementUsesTicks()
    {
        // Warm the relevant methods before checking steady disabled behavior.
        using (PerformanceDiagnostics.Measure("disabled")) { }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1000; index++) using (PerformanceDiagnostics.Measure("disabled")) { }
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
        using var run = PerformanceDiagnostics.BeginRun("active", "source", "fixture");
        using (PerformanceDiagnostics.Measure("work", 42)) { }
        var events = run.Snapshot().Events;
        Assert.Equal(new[] { "begin", "end" }, events.Select(item => item.Outcome));
        Assert.Equal(42, events[1].Request);
        Assert.True(events[1].Value >= 0);
    }

    [Fact]
    public void CapacityAndStringLengthsAreBounded()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PerformanceDiagnostics.BeginRun("bad", "", "", 15));
        using var run = PerformanceDiagnostics.BeginRun("valid", "source", "fixture", 16);
        PerformanceDiagnostics.Record(new string('x', 10000), asset: new string('y', 10000));
        Assert.Equal(256, run.Snapshot().Events[0].Phase.Length);
        Assert.Equal(256, run.Snapshot().Events[0].Asset!.Length);
        Assert.Throws<InvalidOperationException>(() => PerformanceDiagnostics.BeginRun("nested", "", ""));
    }
}

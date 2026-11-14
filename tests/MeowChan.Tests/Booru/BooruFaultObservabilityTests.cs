using System.Collections.Concurrent;
using MeowChan.Booru;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using TempDirectory = MeowChan.Tests.TempDirectory;

namespace MeowChan.Tests.Booru;

/// <summary>Deterministic fault seams for the Booru scheduler/lifecycle observability contract.</summary>
public sealed class BooruFaultObservabilityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    private static DanbooruWatcherSchedulerOptions Options() => new()
    {
        Interval = TimeSpan.FromMinutes(30),
        CheckStartSpacing = TimeSpan.Zero
    };

    [Fact]
    public async Task TransientTransportFenceGetterFailureIsLoggedAndKeepsTheSchedulerFailClosed()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        search.Publish("cat", 100);
        using LogCapture log = new();
        DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        scheduler.Load();
        string watcherId = scheduler.AddWatcher("cat").Watcher!.WatcherId;
        Assert.Equal(DanbooruWatcherCheckKind.BaselineEstablished, (await scheduler.RunNextAsync())!.Kind);

        search.Publish("cat", 101);
        clock.Advance(Options().Interval);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        search.Hold = async (_, _, cancellationToken) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        };
        Task<DanbooruWatcherCheckSummary?> running = scheduler.RunNextAsync();
        await entered.Task.WaitAsync(TestWait.Timeout);

        search.TransportCooldownFailure = new InvalidOperationException("https://provider.invalid/posts?token=secret");
        search.TransportCooldownFailureOnce = true;
        await scheduler.StopAsync();
        DanbooruWatcherCheckSummary? cancelledResult = await running;
        Assert.NotNull(cancelledResult);
        DanbooruWatcherCheckSummary cancelled = cancelledResult!;

        Assert.Equal(DanbooruWatcherCheckKind.Cancelled, cancelled.Kind);
        Assert.Equal("provider_cooldown_unavailable", scheduler.GetSnapshot().FaultCode);
        Assert.Equal("scheduler_stopped", (await scheduler.CheckNowAsync(watcherId)).Code);
        Assert.Equal(2, search.CallCount); // baseline plus the one cancelled page; no retry was admitted

        LogEvent warning = Assert.Single(log.Events, item => item.Level == LogEventLevel.Warning
            && item.MessageTemplate.Text.Contains("provider cooldown is unavailable", StringComparison.Ordinal));
        Assert.Equal("transport_fence_read", Scalar(warning, "Reason"));
        Assert.Equal(nameof(InvalidOperationException), Scalar(warning, "ExceptionType"));
        Assert.DoesNotContain("token=secret", warning.MessageTemplate.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TransientMaximumCooldownGetterFailureIsLoggedAndRejectsBeforeAnyProviderRequest()
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        using LogCapture log = new();
        DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        scheduler.Load();
        scheduler.AddWatcher("cat");
        search.ProviderCooldownMaximumFailure = new InvalidOperationException("query=cat&token=secret");

        DanbooruWatcherCheckSummary rejected = (await scheduler.RunNextAsync())!;
        Assert.Equal(DanbooruWatcherCheckKind.Rejected, rejected.Kind);
        Assert.Equal("provider_cooldown_unavailable", rejected.Code);
        Assert.Equal("provider_cooldown_unavailable", scheduler.GetSnapshot().FaultCode);
        Assert.Equal(0, search.CallCount);

        LogEvent warning = Assert.Single(log.Events, item => item.Level == LogEventLevel.Warning
            && item.MessageTemplate.Text.Contains("provider cooldown is unavailable", StringComparison.Ordinal));
        Assert.Equal("maximum_cooldown_read", Scalar(warning, "Reason"));
        Assert.Equal(nameof(InvalidOperationException), Scalar(warning, "ExceptionType"));
        Assert.DoesNotContain("token=secret", warning.MessageTemplate.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FatalCooldownGetterFailurePropagates(bool maximumGetter)
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        SchedulerClock clock = new(T0);
        FakeDanbooruSearch search = new(clock);
        DanbooruWatcherScheduler scheduler = new(profile.Catalog, search, new CountingAdmission(), clock, Options());
        scheduler.Load();
        scheduler.AddWatcher("cat");
        if (maximumGetter)
            search.ProviderCooldownMaximumFailure = new OutOfMemoryException("fatal maximum getter");
        else
            search.TransportCooldownFailure = new OutOfMemoryException("fatal transport getter");

        Assert.Throws<OutOfMemoryException>(() => scheduler.GetSnapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetachedTerminalReleaseFaultIsLoggedWithReasonAndTypeWithoutExceptionMessage(bool disposeFailure)
    {
        using TempDirectory directory = new();
        await using SchedulerProfile profile = new(directory.Path);
        using LogCapture log = new();
        BooruProducerLifecycle lifecycle = new(profile.Lease.Admission, disposeFailure
            ? () => throw new IOException("https://provider.invalid/posts?token=secret")
            : null);
        lifecycle.Open();
        BooruProducerUse use = lifecycle.TryEnter()!;
        using CancellationTokenRegistration callback = disposeFailure ? default : use.Cut.Register(() =>
            throw new InvalidOperationException("https://provider.invalid/posts?token=secret"));

        lifecycle.RequireRecovery();
        Task stopping = lifecycle.StopAsync();
        use.Dispose();
        await Assert.ThrowsAnyAsync<Exception>(() => stopping.WaitAsync(TestWait.Timeout));
        await TestWait.UntilAsync(
            () => log.Events.Any(item => item.Level == LogEventLevel.Warning
                && Scalar(item, "Reason") == "terminal_release"),
            "detached terminal release warning");

        LogEvent warning = Assert.Single(log.Events, item => item.Level == LogEventLevel.Warning
            && Scalar(item, "Reason") == "terminal_release");
        Assert.Equal(disposeFailure ? nameof(IOException) : nameof(InvalidOperationException),
            Scalar(warning, "ExceptionType"));
        Assert.Null(warning.Exception);
        Assert.DoesNotContain("token=secret", warning.MessageTemplate.Text, StringComparison.Ordinal);
    }

    private static string? Scalar(LogEvent logEvent, string name) =>
        logEvent.Properties.TryGetValue(name, out LogEventPropertyValue? value)
            && value is ScalarValue scalar ? scalar.Value?.ToString() : null;

    private sealed class LogCapture : IDisposable
    {
        private readonly ILogger _previous = Log.Logger;
        private readonly ILogger _current;

        internal ConcurrentQueue<LogEvent> Events { get; } = new();

        internal LogCapture()
        {
            _current = new LoggerConfiguration().MinimumLevel.Verbose()
                .WriteTo.Sink(new CaptureSink(Events)).CreateLogger();
            Log.Logger = _current;
        }

        public void Dispose()
        {
            Log.Logger = _previous;
            (_current as IDisposable)?.Dispose();
        }

        private sealed class CaptureSink(ConcurrentQueue<LogEvent> events) : ILogEventSink
        {
            public void Emit(LogEvent logEvent) => events.Enqueue(logEvent);
        }
    }
}

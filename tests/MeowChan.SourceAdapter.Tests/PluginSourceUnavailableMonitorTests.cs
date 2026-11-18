using System.Collections.Concurrent;
using System.ComponentModel;
using MeowChan.Downloader;
using MeowChan.Interfaces;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Sources;
using MeowChan.PluginSdk;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace MeowChan.SourceAdapter.Tests;

// A disabled/unavailable source plugin is an expected state: watched threads show a neutral activity, are not
// re-checked, log no error/stack trace, and resume on their own when the source is enabled again.
public sealed class PluginSourceUnavailableMonitorTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private const string ThreadUrl = "https://boards.4chan.org/g/thread/123";

    private sealed class ListSink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTime end = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException("Condition not reached.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task DisabledSourceIsNeitherCheckedNorAnErrorAndResumesWhenEnabled()
    {
        using SourceAdapterTests.TemporaryProfile profile = new();
        ListSink sink = new();
        using Logger logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        int reads = 0;
        SourceAdapterTests.TestWorker worker = new((request, _, _) =>
        {
            Interlocked.Increment(ref reads);
            return Task.FromResult<ThreadReadResult>(new ThreadNotFound(request.Thread, 404));
        });
        using PluginSourceImageBoard source = new(worker.Description, "4chan",
            _ => Task.FromResult<IPluginWorkerClient>(worker), null, logger);
        source.SetEnabled(false);
        DownloadQueue queue = new(1, 1048576);
        IThread thread = source.GetThread(new Uri(ThreadUrl));
        ConcurrentQueue<ThreadActivity> activities = new();
        void Changed(object? sender, PropertyChangedEventArgs args) => activities.Enqueue(thread.Activity);
        thread.PropertyChanged += Changed;
        try
        {
            thread.StartMonitoring(queue, profile.Path, TimeSpan.FromMilliseconds(20));
            await WaitForAsync(() => thread.Activity == ThreadActivity.SourceUnavailable);
            // Explicit check requests only re-test the source; they never reach the worker or flip the state.
            for (int i = 0; i < 5; i++)
            {
                ((MeowChan.Boards.Base.BaseThread)thread).RequestCheck();
                await Task.Delay(40);
            }
            Assert.Equal(0, Volatile.Read(ref reads));
            Assert.Equal(ThreadActivity.SourceUnavailable, thread.Activity);
            Assert.Null(thread.LastCheckFailure);
            Assert.DoesNotContain(activities, activity => activity is ThreadActivity.Checking or ThreadActivity.Error);
            Assert.DoesNotContain(sink.Events, e => e.Level >= LogEventLevel.Error || e.Exception != null);
            // One line for the source's transition; per-thread state changes stay at debug level.
            Assert.Single(sink.Events, e => e.Level >= LogEventLevel.Information);

            source.SetEnabled(true);
            await WaitForAsync(() => thread.SourceState == ThreadSourceState.NotFound);
            Assert.Equal(1, Volatile.Read(ref reads));
            Assert.NotEqual(ThreadActivity.Error, thread.Activity);
            Assert.NotEqual(ThreadActivity.SourceUnavailable, thread.Activity);
            Assert.Null(thread.LastCheckFailure);
            // The source logged exactly its two transitions (disabled, available again). The thread's own NotFound after the resumed check is a
            // normal Information-level activity line of the thread (BaseThread), not part of the source's transition lines.
            Assert.Equal(2, sink.Events.Count(e => e.Level >= LogEventLevel.Information && e.Exception == null
                && e.RenderMessage().StartsWith("Source ", StringComparison.Ordinal)));
        }
        finally
        {
            thread.PropertyChanged -= Changed;
            try { await thread.StopMonitoringAsync().WaitAsync(Deadline); }
            finally { await queue.StopAllAsync().WaitAsync(Deadline); }
        }
    }

    [Fact]
    public async Task SourceDisabledDuringCheckBecomesUnavailableNotErrorAndResumes()
    {
        using SourceAdapterTests.TemporaryProfile profile = new();
        ListSink sink = new();
        using Logger logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        int reads = 0;
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SourceAdapterTests.TestWorker worker = new(async (request, _, token) =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return new ThreadNotFound(request.Thread, 404);
        });
        using PluginSourceImageBoard source = new(worker.Description, "4chan",
            _ => Task.FromResult<IPluginWorkerClient>(worker), null, logger);
        DownloadQueue queue = new(1, 1048576);
        IThread thread = source.GetThread(new Uri(ThreadUrl));
        try
        {
            thread.StartMonitoring(queue, profile.Path, TimeSpan.FromMilliseconds(20));
            await entered.Task.WaitAsync(Deadline);
            source.SetEnabled(false);
            await WaitForAsync(() => thread.Activity == ThreadActivity.SourceUnavailable);
            Assert.Null(thread.LastCheckFailure);
            Assert.DoesNotContain(sink.Events, e => e.Level >= LogEventLevel.Error || e.Exception != null);
            Assert.Equal(1, Volatile.Read(ref reads));

            source.SetEnabled(true);
            await WaitForAsync(() => thread.SourceState == ThreadSourceState.NotFound);
            Assert.Equal(2, Volatile.Read(ref reads));
            Assert.Null(thread.LastCheckFailure);
        }
        finally
        {
            try { await thread.StopMonitoringAsync().WaitAsync(Deadline); }
            finally { await queue.StopAllAsync().WaitAsync(Deadline); }
        }
    }
}

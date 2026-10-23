using MeowChan.OfficialSources.FourChan;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Sources;
using MeowChan.PluginSdk;

namespace MeowChan.SourceAdapter.Tests;

public sealed class SourceLifetimeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task DisabledSourceDisposesRepeatedlyWithoutAcquiringWorker()
    {
        int acquisitions = 0;
        using PluginSourceImageBoard source = new(new FourChanPlugin().Describe(), "4chan", _ =>
        {
            acquisitions++;
            throw new InvalidOperationException("Disabled source must not acquire a worker.");
        });
        source.SetEnabled(false);
        source.SetEnabled(false);
        PluginWorkerException unavailable = await Assert.ThrowsAsync<PluginWorkerException>(() => ReadAsync(source));
        Assert.Equal("source_unavailable", unavailable.Code);
        source.Dispose();
        source.Dispose();
        Assert.False(source.Enabled);
        Assert.Equal(0, acquisitions);
        Assert.Throws<ObjectDisposedException>(() => source.SetEnabled(true));
    }

    [Fact]
    public async Task ReenableUsesFreshLifetimeAndRejectsLateDisabledResult()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken first = default, second = default;
        int reads = 0;
        SourceAdapterTests.TestWorker worker = new((_, _, _) => throw new NotSupportedException(), catalog: async (request, _, token) =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                first = token;
                entered.TrySetResult();
                await release.Task; // Deliberately ignores cancellation until released.
            }
            else second = token;
            return Empty(request);
        });
        using PluginSourceImageBoard source = new(worker.Description, "4chan", _ => Task.FromResult<IPluginWorkerClient>(worker));
        Task oldRead = ReadAsync(source);
        try
        {
            await entered.Task.WaitAsync(Deadline);
            source.SetEnabled(false);
            Assert.True(first.IsCancellationRequested);
            source.SetEnabled(true);
            await ReadAsync(source).WaitAsync(Deadline);
            Assert.False(second.IsCancellationRequested);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldRead.WaitAsync(Deadline));
        source.Dispose();
        Assert.False(source.Enabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeCanOverlapBlockedCancellationWithoutOwningItsLifetime(bool disableFirst)
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cancelling = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim releaseCallback = new();
        SourceAdapterTests.TestWorker worker = new((_, _, _) => throw new NotSupportedException(), catalog: async (request, _, token) =>
        {
            using CancellationTokenRegistration registration = token.Register(() =>
            {
                cancelling.TrySetResult();
                if (!releaseCallback.Wait(Deadline)) throw new TimeoutException("Cancellation callback was not released.");
            });
            entered.TrySetResult();
            await releaseRead.Task;
            return Empty(request);
        });
        using PluginSourceImageBoard source = new(worker.Description, "4chan", _ => Task.FromResult<IPluginWorkerClient>(worker));
        Task pending = ReadAsync(source);
        Task? disabling = null, disposing = null;
        try
        {
            await entered.Task.WaitAsync(Deadline);
            disabling = Task.Run(() => { if (disableFirst) source.SetEnabled(false); else source.Dispose(); });
            await cancelling.Task.WaitAsync(Deadline);
            // The first stop owns the old generation and is inside a callback.
            // Another Dispose must neither take it nor wait on that callback/lock.
            disposing = Task.Run(source.Dispose);
            await disposing.WaitAsync(Deadline);
            Assert.False(source.Enabled);
        }
        finally
        {
            releaseCallback.Set();
            releaseRead.TrySetResult();
            try
            {
                if (disabling != null) await disabling.WaitAsync(Deadline);
                if (disposing != null) await disposing.WaitAsync(Deadline);
            }
            finally { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Deadline)); }
        }
        source.Dispose();
    }

    private static Task ReadAsync(PluginSourceImageBoard source) =>
        source.GetBoard(new Uri("https://boards.4chan.org/g/")).GetThreadsAsync();

    private static CatalogReadResult Empty(CatalogReadRequest request) =>
        new CatalogSnapshot(new SourceCatalogSnapshot(request.Board, Array.Empty<SourceThreadReference>(), true));
}

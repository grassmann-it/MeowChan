using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class VideoOperationGateTests
{
    [Fact]
    public async Task CancelledWaiterCannotReleaseNonCooperativeNativeWork()
    {
        VideoOperationGate gate = new();
        await gate.WaitAsync();
        using CancellationTokenSource stop = new();
        Task cancelled = gate.WaitAsync(stop.Token);
        Task successor = gate.WaitAsync();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.False(successor.IsCompleted);
        gate.Release();
        await successor.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release();
        await gate.WaitAsync(); // Late stale commands can still acquire after owner cleanup.
        gate.Release();
    }

    [Fact]
    public async Task WaitersSerializeInAdmissionOrder()
    {
        VideoOperationGate gate = new();
        await gate.WaitAsync();
        Task first = gate.WaitAsync(), second = gate.WaitAsync();
        gate.Release();
        await first;
        Assert.False(second.IsCompleted);
        gate.Release();
        await second;
        gate.Release();
        Assert.Throws<InvalidOperationException>(gate.Release);
    }

    [Fact]
    public async Task AlreadyCancelledAdmissionDoesNotTakeTheGate()
    {
        VideoOperationGate gate = new();
        using CancellationTokenSource stop = new();
        stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => { _ = gate.WaitAsync(stop.Token); });
        await gate.WaitAsync();
        gate.Release();
    }
}

namespace MeowChan.PluginRuntime.Tests;

// MS-R11: the timer wait of the test clock tolerates a due time that is off by a rounding tick, offers a range for delays with
// jitter, and names the registered delays when it times out instead of failing without a hint.
public sealed class ManualClockTests
{
    [Fact]
    public void TimerLookupToleratesARoundingTickAndOffersARange()
    {
        ManualClock clock = new();
        using ITimer timer = clock.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(2) + TimeSpan.FromTicks(3), Timeout.InfiniteTimeSpan);
        Assert.True(clock.HasTimer(TimeSpan.FromSeconds(2)));
        Assert.False(clock.HasTimer(TimeSpan.FromSeconds(2), TimeSpan.Zero));
        Assert.False(clock.HasTimer(TimeSpan.FromSeconds(3)));
        Assert.True(clock.HasTimerBetween(TimeSpan.FromSeconds(1.6), TimeSpan.FromSeconds(2.4)));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(clock.HasTimer(TimeSpan.FromSeconds(1)));
        timer.Dispose();
        Assert.False(clock.HasTimerBetween(TimeSpan.Zero, TimeSpan.MaxValue));
    }

    [Fact]
    public async Task ATimeoutNamesTheDelaysThatWereRegistered()
    {
        ManualClock clock = new();
        using ITimer timer = clock.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
        TimeoutException timeout = await Assert.ThrowsAsync<TimeoutException>(
            () => clock.WaitForTimerAsync(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(50)));
        Assert.Contains("00:00:05", timeout.Message);
        await clock.WaitForTimerBetweenAsync(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(5));
    }
}

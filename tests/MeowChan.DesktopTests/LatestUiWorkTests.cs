using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class LatestUiWorkTests
{
    private sealed record Request(int Version);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task ContinuousVisibleInvalidationsFinishActiveAndRunOnlyNewestFollowUp()
    {
        TaskCompletionSource release = Signal();
        List<int> started = [];
        List<CancellationToken> tokens = [];
        LatestUiWork<Request> work = new(async (request, token) =>
        {
            started.Add(request.Version); tokens.Add(token);
            if (request.Version == 0) await release.Task;
        });
        Task drain = work.Request(new(0));
        for (int i = 1; i <= 1000; i++) Assert.Same(drain, work.Request(new(i)));
        Assert.Equal(new[] { 0 }, started);
        Assert.True(work.HasPending);
        Assert.False(tokens[0].IsCancellationRequested);
        Assert.Equal(0, work.Cancellations);
        release.SetResult(); await drain;
        Assert.Equal(new[] { 0, 1000 }, started);
        Assert.Equal(2, work.Starts);
        Assert.Equal(999, work.Replacements);
        Assert.False(work.IsRunning);
        Assert.False(work.HasPending);
    }

    [Fact]
    public async Task RepeatedCoverageCancelsOnceAndDoesNotReleaseNonCooperativeIoSlot()
    {
        TaskCompletionSource release = Signal();
        List<int> started = [];
        CancellationToken firstToken = default;
        LatestUiWork<Request> work = new(async (request, token) =>
        {
            started.Add(request.Version);
            if (request.Version == 1) { firstToken = token; await release.Task; }
        });
        Task drain = work.Request(new(1));
        work.Request(new(2));
        for (int i = 0; i < 50; i++) work.Cancel();
        Assert.True(firstToken.IsCancellationRequested);
        Assert.Equal(1, work.Cancellations);
        Assert.True(work.IsRunning);
        Assert.False(work.HasPending);
        Assert.Same(drain, work.Request(new(3))); // Uncover while the old OS call still runs.
        Assert.Equal(new[] { 1 }, started);
        release.SetResult(); await drain;
        Assert.Equal(new[] { 1, 3 }, started);
        Assert.Equal(2, work.Starts);
    }

    [Fact]
    public async Task QuerySwitchClearsOldFollowUpAndLateFailureStillAllowsLatestQuery()
    {
        TaskCompletionSource release = Signal();
        List<int> started = [];
        LatestUiWork<Request> work = new(async (request, _) =>
        {
            started.Add(request.Version);
            if (request.Version == 1) { await release.Task; throw new IOException("late old query failure"); }
        });
        Task drain = work.Request(new(1));
        work.Request(new(2)); work.Cancel(); work.Request(new(3));
        release.SetResult();
        await Assert.ThrowsAsync<IOException>(() => drain);
        Assert.Equal(new[] { 1, 3 }, started);
        Assert.False(work.IsRunning);
        await work.Request(new(4));
        Assert.Equal(new[] { 1, 3, 4 }, started);
    }

    [Fact]
    public async Task LifetimeCancellationDrainsOwnershipAndDropsPending()
    {
        using CancellationTokenSource lifetime = new();
        TaskCompletionSource release = Signal();
        int starts = 0;
        LatestUiWork<Request> work = new(async (_, _) => { starts++; await release.Task; }, lifetime.Token);
        Task drain = work.Request(new(1)); work.Request(new(2));
        lifetime.Cancel();
        Assert.True(work.IsRunning);
        release.SetResult(); await drain;
        await work.Request(new(3));
        Assert.Equal(1, starts);
        Assert.False(work.HasPending);
    }
}

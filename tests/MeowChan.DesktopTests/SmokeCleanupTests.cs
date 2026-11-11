using MeowChan.DesktopSmoke;

namespace MeowChan.DesktopTests;

public sealed class SmokeCleanupTests
{
    [Fact]
    public async Task SuccessfulCleanupRunsExactlyOnceWithoutAReport()
    {
        int calls = 0;
        await SmokeCleanup.RunAsync(null, () => { calls++; return Task.CompletedTask; }, _ => Assert.Fail("No failure expected."));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CleanupFailureAfterAPassingCheckRemainsAFailure()
    {
        var cleanup = new InvalidOperationException("cleanup");
        Exception? reported = null;
        Exception actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SmokeCleanup.RunAsync(null, () => Task.FromException(cleanup), ex => reported = ex));
        Assert.Same(cleanup, actual);
        Assert.Same(cleanup, reported);
    }

    [Fact]
    public async Task CleanupFailureCannotReplaceThePrimaryCheckFailure()
    {
        var primary = new InvalidOperationException("check failed first");
        var cleanup = new TimeoutException("restore timed out");
        Exception? reported = null;
        Exception actual = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            try { throw primary; }
            finally { await SmokeCleanup.RunAsync(primary, () => Task.FromException(cleanup), ex => reported = ex); }
        });
        Assert.Same(primary, actual);
        Assert.Same(cleanup, reported);
    }

    [Fact]
    public async Task BrokenDiagnosticCannotReplacePrimaryOrCleanupFailure()
    {
        var primary = new InvalidOperationException("primary");
        var cleanup = new TimeoutException("cleanup");
        await SmokeCleanup.RunAsync(primary, () => throw cleanup, _ => throw new Exception("diagnostic"));
        Exception actual = await Assert.ThrowsAsync<TimeoutException>(() =>
            SmokeCleanup.RunAsync(null, () => throw cleanup, _ => throw new Exception("diagnostic")));
        Assert.Same(cleanup, actual);
    }

    [Fact]
    public void BrokenScreenshotIsOnlyADiagnosticFailure()
    {
        SmokeCleanup.Diagnose(() => throw new InvalidOperationException("no renderer"));
    }
}

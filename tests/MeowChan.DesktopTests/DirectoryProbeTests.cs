using System.Diagnostics;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class DirectoryProbeTests
{
    [Fact]
    public async Task ExistingAndMissingFoldersAreReported()
    {
        string folder = Directory.CreateTempSubdirectory("meowchan-probe").FullName;
        try
        {
            Assert.Equal(DirectoryProbeResult.Exists, await DirectoryProbe.CheckAsync(folder));
            Assert.Equal(DirectoryProbeResult.Missing, await DirectoryProbe.CheckAsync(Path.Combine(folder, "absent")));
        }
        finally { Directory.Delete(folder); }
    }

    [Fact]
    public async Task ABlockedCheckStopsWaitingAfterTheTimeoutWithoutBlockingTheCaller()
    {
        using ManualResetEventSlim release = new();
        Stopwatch clock = Stopwatch.StartNew();
        Task<DirectoryProbeResult> probe = DirectoryProbe.CheckAsync("/unreachable-share", TimeSpan.FromMilliseconds(100),
            exists: _ => { release.Wait(TimeSpan.FromSeconds(30)); return true; });
        Assert.Equal(DirectoryProbeResult.Unknown, await probe);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
        release.Set();
    }

    [Fact]
    public async Task CancellationEndsTheWait()
    {
        using ManualResetEventSlim release = new();
        using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DirectoryProbe.CheckAsync("/unreachable-share",
            TimeSpan.FromSeconds(30), cancel.Token, _ => { release.Wait(TimeSpan.FromSeconds(30)); return true; }));
        release.Set();
    }
}

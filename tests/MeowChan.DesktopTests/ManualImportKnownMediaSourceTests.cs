using MeowChan.Desktop;
using MeowChan.Import;

namespace MeowChan.DesktopTests;

// The import dialog builds what the library holds once per session, not on every change of the selection.
public sealed class ManualImportKnownMediaSourceTests
{
    [Fact]
    public async Task ManyChecksShareOneBuildUntilInvalidated()
    {
        int builds = 0;
        using ManualImportKnownMediaSource source = new(_ => { Interlocked.Increment(ref builds); return Task.FromResult(ManualImportKnownMedia.Empty); });
        for (int index = 0; index < 5; index++) Assert.Same(ManualImportKnownMedia.Empty, await source.GetAsync(default));
        Assert.Equal(1, builds);
        source.Invalidate();
        await source.GetAsync(default);
        Assert.Equal(2, builds);
        Assert.Equal(2, source.Builds);
    }

    [Fact]
    public async Task CancellingACheckDoesNotCancelTheSharedBuild()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int builds = 0;
        using ManualImportKnownMediaSource source = new(async _ => { Interlocked.Increment(ref builds); await release.Task; return ManualImportKnownMedia.Empty; });
        using CancellationTokenSource first = new();
        Task<ManualImportKnownMedia> waiting = source.GetAsync(first.Token);
        await first.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Task<ManualImportKnownMedia> second = source.GetAsync(default);
        release.SetResult();
        Assert.Same(ManualImportKnownMedia.Empty, await second);
        Assert.Equal(1, builds);
    }

    [Fact]
    public async Task AFailedBuildIsRetriedAndClosingTheDialogStopsTheBuild()
    {
        int builds = 0;
        ManualImportKnownMediaSource source = new(_ =>
            ++builds == 1 ? throw new IOException("busy") : Task.FromResult(ManualImportKnownMedia.Empty));
        await Assert.ThrowsAsync<IOException>(() => source.GetAsync(default));
        Assert.Same(ManualImportKnownMedia.Empty, await source.GetAsync(default));
        Assert.Equal(2, builds);
        source.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => source.GetAsync(default));
    }
}

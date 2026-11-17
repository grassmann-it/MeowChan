using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Updates;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class CtsResourceOwnerTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public async Task FiftyCompletedTagWarmupsDisposeEverySource()
    {
        ConcurrentBag<WaitHandle> handles = new();
        using GalleryTagPanelSuggestions suggestions = new(build: (_, token) => handles.Add(token.WaitHandle));
        for (int i = 0; i < 50; i++)
        {
            suggestions.Warm(new PrivateTagStoreSnapshot(1, "snapshot-" + i, [], [], []));
            await suggestions.WarmTask.WaitAsync(TimeSpan.FromSeconds(30));
        }
        Assert.Equal(50, handles.Count);
        foreach (WaitHandle handle in handles) Assert.Throws<ObjectDisposedException>(() => handle.WaitOne(0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TagWarmupDisposalWaitsForNonCooperativeWork(bool fail)
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim release = new();
        CancellationToken token = default;
        bool released = false, handleUsable = false;
        using GalleryTagPanelSuggestions suggestions = new(build: (_, value) =>
        {
            token = value;
            entered.SetResult();
            released = release.Wait(TimeSpan.FromSeconds(30));
            handleUsable = token.WaitHandle.WaitOne(0);
            if (fail) throw new IOException("controlled build failure");
        });
        suggestions.Warm(new PrivateTagStoreSnapshot(1, "snapshot", [], [], []));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var source = (CancellationTokenSource)typeof(GalleryTagPanelSuggestions).GetField("_warming", Fields)!.GetValue(suggestions)!;
        try
        {
            suggestions.Dispose();
            suggestions.Dispose();
            Assert.True(token.IsCancellationRequested);
            Assert.True(token.WaitHandle.WaitOne(0));
            Assert.False(suggestions.WarmTask.IsCompleted);
        }
        finally { release.Set(); }
        await suggestions.WarmTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(released, "Controlled warm-up was not released.");
        Assert.True(handleUsable, "The worker must still be able to use its cancelled token after Dispose.");
        Assert.Throws<ObjectDisposedException>(() => source.Token);
    }

    [Fact]
    public async Task DecodeQueueDisposesOnlyAfterItsActiveWorkerExits()
    {
        using GalleryDecodeQueue queue = new();
        TaskCompletionSource pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        Task work = queue.Replace(value => { token = value; return pending.Task; }, debounce: false);
        var source = (CancellationTokenSource)typeof(GalleryDecodeQueue).GetField("_active", Fields)!.GetValue(queue)!;
        Task completion = queue.Completion;
        queue.Dispose();
        queue.Dispose();
        Assert.True(token.IsCancellationRequested);
        Assert.True(token.WaitHandle.WaitOne(0));
        Assert.False(work.IsCompleted);
        Assert.Throws<ObjectDisposedException>(() => { _ = queue.Replace(_ => Task.CompletedTask, false); });
        pending.SetResult();
        await completion.WaitAsync(TimeSpan.FromSeconds(30));
        await work;
        Assert.Throws<ObjectDisposedException>(() => source.Token);
    }

    [Fact]
    public void ArchiveMediaCountGateIsDisposedOnlyAfterTheScanDrains() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        TokenApplication.Bind();
        ArchivePage page = new(_ => Task.FromResult<ArchiveReader>(null!), (_, _) => Task.CompletedTask, _ => { });
        var gate = (SemaphoreSlim)typeof(ArchivePage).GetField("_archiveMediaCountGate", Fields)!.GetValue(page)!;
        TaskCompletionSource scan = new();
        typeof(ArchivePage).GetProperty("ArchiveMediaCountTask", Fields)!.SetValue(page, scan.Task);
        Assert.True(gate.Wait(0));
        page.EndLifetime();
        Assert.Equal(0, gate.Release()); // EndLifetime must not dispose a gate still owned by filesystem work.
        scan.SetResult();
        Dispatcher.UIThread.RunJobs();
        Assert.Throws<ObjectDisposedException>(() => gate.Wait(0));
        page.EndLifetime();
    });

    [Fact]
    public async Task UpdateServiceDisposesItsGateButNotTheCallerOwnedTransport()
    {
        using RecordingHandler handler = new();
        using HttpClient client = new(handler);
        using UpdateService service = new(client, new UpdateStateStore(Environment.CurrentDirectory),
            new UpdateServiceOptions { CurrentVersion = SemanticVersion.Parse("1.0.0") });
        service.Dispose();
        service.Dispose();
        Assert.False(handler.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.GetStateAsync());
        using HttpResponseMessage response = await client.GetAsync("https://example.invalid/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UpdateSessionDisposesItsLifetimeAfterDraining()
    {
        using RecordingHandler handler = new();
        await using UpdateSession session = new(Environment.CurrentDirectory, new Config(), new HttpClient(handler),
            disableEnvironment: () => "1", currentVersion: SemanticVersion.Parse("1.0.0"));
        var lifetime = (CancellationTokenSource)typeof(UpdateSession).GetField("_lifetime", Fields)!.GetValue(session)!;
        await session.DisposeAsync();
        await session.DisposeAsync();
        Assert.True(handler.Disposed);
        Assert.Throws<ObjectDisposedException>(() => lifetime.Token);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}

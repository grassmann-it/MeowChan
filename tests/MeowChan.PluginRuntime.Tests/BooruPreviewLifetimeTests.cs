using System.Net;
using MeowChan.PluginHost.Http;
using MeowChan.PluginSdk;

namespace MeowChan.PluginRuntime.Tests;

// Composition teardown disposes the preview path while thumbnails may still be queued behind its request gate.
public sealed class BooruPreviewLifetimeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task DisposePreventsQueuedPreviewsFromStartingHttpAndLeavesTheTransportUsable()
    {
        using HeldPreviews handler = new();
        using HttpClient transport = new(handler);
        using BooruPreviewHttp previews = new(transport, Source(), productVersion: "1.1.0");
        using CancellationTokenSource deadline = new(Deadline);
        try
        {
            // Fill both request slots, then leave a third preview waiting at the gate.
            Task<byte[]?> first = previews.LoadAsync(new("https://cdn.example.test/first.png"), deadline.Token);
            Task<byte[]?> second = previews.LoadAsync(new("https://cdn.example.test/second.png"), deadline.Token);
            Task<byte[]?> queued = previews.LoadAsync(new("https://cdn.example.test/queued.png"), deadline.Token);
            Assert.Equal(BooruPreviewHttp.MaximumConcurrentRequests, previews.RequestCount);
            Assert.False(queued.IsCompleted);

            previews.Dispose(); // what BooruSessionComposition.StopAsync does
            handler.Release.TrySetResult();
            await Task.WhenAll(first, second, queued).WaitAsync(deadline.Token);

            Assert.Null(await queued);
            Assert.Equal(BooruPreviewHttp.MaximumConcurrentRequests, previews.RequestCount);
            Assert.Equal(BooruPreviewHttp.MaximumConcurrentRequests, handler.Requests);

            // In-flight bytes were not cached after disposal, and later loads never reach the transport.
            Assert.Null(await previews.LoadAsync(new("https://cdn.example.test/first.png"), deadline.Token));
            Assert.Null(await previews.LoadAsync(new("https://cdn.example.test/later.png"), deadline.Token));
            Assert.Equal(BooruPreviewHttp.MaximumConcurrentRequests, handler.Requests);

            // A second dispose is harmless, and the borrowed transport stays usable.
            previews.Dispose();
            using HttpResponseMessage response = await transport.GetAsync("https://cdn.example.test/borrowed.png", deadline.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally { handler.Release.TrySetResult(); }
    }

    [Fact]
    public async Task CancellingAQueuedPreviewAfterDisposeStillThrowsOnlyTheCallersCancellation()
    {
        using HeldPreviews handler = new();
        using HttpClient transport = new(handler);
        using BooruPreviewHttp previews = new(transport, Source(), productVersion: "1.1.0");
        using CancellationTokenSource deadline = new(Deadline);
        using CancellationTokenSource caller = new();
        try
        {
            Task<byte[]?> first = previews.LoadAsync(new("https://cdn.example.test/a.png"), deadline.Token);
            Task<byte[]?> second = previews.LoadAsync(new("https://cdn.example.test/b.png"), deadline.Token);
            Task<byte[]?> queued = previews.LoadAsync(new("https://cdn.example.test/c.png"), caller.Token);
            previews.Dispose();
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(deadline.Token));
            handler.Release.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(deadline.Token);
            Assert.Equal(BooruPreviewHttp.MaximumConcurrentRequests, handler.Requests);
        }
        finally { handler.Release.TrySetResult(); }
    }

    private static SourceDescription Source() => new("example-booru", "Example booru", new("https://booru.example.test/"),
        SourceCapabilities.PostSearch, [], [new("cdn.example.test", HttpHostPurpose.PreviewMedia)], new("1", []));

    private sealed class HeldPreviews : HttpMessageHandler
    {
        private int _requests;

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Requests => Volatile.Read(ref _requests);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            await Release.Task.WaitAsync(cancellationToken);
            return new(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])
            };
        }
    }
}

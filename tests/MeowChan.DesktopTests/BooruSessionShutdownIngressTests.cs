using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

// F4: the session owns the synchronous ingress cut. These are escaped production ports from a real host
// composition; the provider is the in-memory Danbooru stub and the queue-drain callback is the only stop seam.
[Collection("Avalonia global state")]
public sealed class BooruSessionShutdownIngressTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task BeginShutdownSynchronouslyRevokesEscapedPortsBeforeQueueDrainAndKeepsAcceptedWorkOwned()
    {
        using TempDirectory profile = new();
        using DanbooruStub stub = new();
        DanbooruStubPost acceptedPost = stub.Add(new(8101));
        DanbooruStubPost rejectedPost = stub.Add(new(8102));
        using DanbooruTestBinding binding = new(stub);

        DesktopSessionHost? host = null;
        TaskCompletionSource<bool> beforeQueueDrain = Signal<bool>();
        TaskCompletionSource queueDrainRelease = Signal();
        try
        {
            host = await BooruCompositionHostTests.StartAsync(
                profile.Path, stub, () => binding.Binding, paused: true);
            BooruSessionComposition composition = Assert.IsType<BooruSessionComposition>(host.BooruComposition);
            BooruWorkbenchServices services = Assert.IsType<BooruWorkbenchServices>(host.BooruServices);

            // Keep every escaped production port before the session cut. Search and post are deliberately both
            // exercised because they share the same public search port but different host operations.
            IBooruSearchPort search = services.Search;
            IBooruQueuePort originals = services.Queue;
            IBooruPreviewPort previews = services.Previews;
            IBooruWatcherPort watchers = services.Watchers;

            BooruCallResult<BooruPostView> acceptedRead = await search.ReadPostAsync(
                acceptedPost.PostId, CancellationToken.None).WaitAsync(Deadline);
            Assert.Equal(BooruCallStatus.Ok, acceptedRead.Status);
            BooruPostView accepted = Assert.IsType<BooruPostView>(acceptedRead.Value);
            BooruCallResult<BooruPostView> rejectedRead = await search.ReadPostAsync(
                rejectedPost.PostId, CancellationToken.None).WaitAsync(Deadline);
            Assert.Equal(BooruCallStatus.Ok, rejectedRead.Status);
            BooruPostView rejected = Assert.IsType<BooruPostView>(rejectedRead.Value);

            // The first request is accepted by the real shared queue but held there by the paused profile. It must
            // remain owned by that queue until the asynchronous engine drain, even though the UI ports are revoked.
            Assert.Equal(BooruLoadStatus.Queued,
                (await originals.LoadOriginalAsync(accepted, BooruLoadOrigin.OneShot, CancellationToken.None))
                .Status);
            Assert.Equal(1, composition.Originals.ActiveCount);
            Assert.Contains(composition.Originals.GetPendingOriginals(), post => post.PostId == acceptedPost.PostId);

            int metadataBeforeCut = stub.MetadataRequests;
            int searchJobsBeforeCut = binding.Worker.SearchCalls;
            int postJobsBeforeCut = binding.Worker.PostCalls;
            int originalRequestsBeforeCut = stub.OriginalRequests;
            int previewRequestsBeforeCut = stub.PreviewRequests;
            string catalogPath = Path.Combine(profile.Path, BooruCatalogStore.FileName);

            // This is the real host's post-Booru/pre-queue boundary. Blocking here proves that the assertions below
            // happen while background stop/drain is still incomplete, rather than after StopAsync happened to win.
            host.AfterBooruStoppedForTests = queueStopping =>
            {
                beforeQueueDrain.TrySetResult(queueStopping);
                queueDrainRelease.Task.GetAwaiter().GetResult();
            };

            Task shutdown = host.BeginShutdown();

            // No await is allowed between BeginShutdown and invoking the escaped ports. ClosePortIngress must have
            // already cut every mutation path synchronously; the accepted first job is not a reason to reject or
            // cancel this assertion window.
            Task<BooruCallResult<BooruResultPage>> searchAfterCut = search.SearchAsync(
                "shutdown_ingress", 1, BooruSearchModel.PageSize, CancellationToken.None);
            Task<BooruCallResult<BooruPostView>> postAfterCut = search.ReadPostAsync(
                rejectedPost.PostId, CancellationToken.None);
            Task<BooruLoadResult> originalAfterCut = originals.LoadOriginalAsync(
                rejected, BooruLoadOrigin.OneShot, CancellationToken.None);
            Task<byte[]?> previewAfterCut = previews.LoadPreviewAsync(rejected, CancellationToken.None);
            Task<BooruWatcherCommandResult> watcherAfterCut = watchers.CreateAsync(
                "shutdown_ingress", CancellationToken.None);

            Assert.False(await beforeQueueDrain.Task.WaitAsync(Deadline),
                "Booru drain must finish before the shared queue begins stopping.");
            byte[] catalogAtBooruDrain = File.ReadAllBytes(catalogPath);
            await Task.WhenAll(searchAfterCut, postAfterCut, originalAfterCut, previewAfterCut, watcherAfterCut)
                .WaitAsync(Deadline);

            // The search/post assertions intentionally cover the provider-job boundary, not just the queue gate.
            // They also make a stale escaped search handle observable even if it would otherwise return a harmless page.
            Assert.Equal(BooruCallStatus.Unavailable, searchAfterCut.Result.Status);
            Assert.Equal(BooruCallStatus.Unavailable, postAfterCut.Result.Status);
            Assert.Equal((BooruLoadStatus.Failed, BooruCodes.SourceUnavailable),
                (originalAfterCut.Result.Status, originalAfterCut.Result.Code));
            Assert.Null(previewAfterCut.Result);
            Assert.Equal((BooruWatcherCommandStatus.Failed, BooruCodes.SourceUnavailable),
                (watcherAfterCut.Result.Status, watcherAfterCut.Result.Code));

            Assert.Equal(metadataBeforeCut, stub.MetadataRequests);
            Assert.Equal(searchJobsBeforeCut, binding.Worker.SearchCalls);
            Assert.Equal(postJobsBeforeCut, binding.Worker.PostCalls);
            Assert.Equal(originalRequestsBeforeCut, stub.OriginalRequests);
            Assert.Equal(previewRequestsBeforeCut, stub.PreviewRequests);

            // The rejected calls did not append a post, finding, watcher or queue intent. The snapshot is taken
            // after the host's Booru stop has finished, so only the accepted queue job remains in this boundary.
            byte[] catalogAfterCut = File.ReadAllBytes(catalogPath);
            Assert.Equal(catalogAtBooruDrain, catalogAfterCut);
            Assert.Single(composition.Catalog.Read().Posts, post => post.PostId == acceptedPost.PostId);
            Assert.DoesNotContain(composition.Catalog.Read().Posts, post => post.PostId == rejectedPost.PostId);
            Assert.Equal(1, composition.Originals.ActiveCount);
            Assert.Contains(composition.Originals.GetPendingOriginals(), post => post.PostId == acceptedPost.PostId);

            // Repeated session stop calls share the same drain and remain blocked at the explicit queue barrier.
            Task repeatedBeginShutdown = host.BeginShutdown();
            Task repeatedStop = host.StopAsync();
            queueDrainRelease.TrySetResult();
            await Task.WhenAll(shutdown, repeatedBeginShutdown, repeatedStop).WaitAsync(Deadline);

            Assert.Equal(0, composition.Originals.ActiveCount);
            Assert.Contains(composition.Originals.GetPendingOriginals(), post => post.PostId == acceptedPost.PostId);
            await host.DisposeAsync();
            host = null;
        }
        finally
        {
            queueDrainRelease.TrySetResult();
            if (host is not null)
            {
                try { await host.StopAsync().WaitAsync(Deadline); }
                catch { /* preserve the assertion that caused cleanup */ }
                try { await host.DisposeAsync(); }
                catch { }
            }
        }
    }

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> Signal<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

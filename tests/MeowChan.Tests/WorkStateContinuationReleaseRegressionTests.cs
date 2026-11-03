using MeowChan.Persistence;

namespace MeowChan.Tests;

public sealed class WorkStateContinuationReleaseRegressionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private const string Url = "https://stub.test/g/thread/42";

    [Theory]
    [InlineData("media", false)]
    [InlineData("media", true)]
    [InlineData("intent", false)]
    [InlineData("intent", true)]
    [InlineData("state", false)]
    [InlineData("state", true)]
    public async Task AcceptedOuterUseContinuesAfterBorrowedTokenDispose(string path, bool shutdown)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        WorkStateStore store = new(directory.Path, lease);
        WorkResource original = store.CommitIntent(new()
        {
            Url = Url, OutputRoot = directory.Path, Generation = 1, IsThread = true,
            Intent = WorkIntent.Watching, Source = ThreadSourceStateValue.Active
        });
        MediaWork media = new()
        {
            ResourceUrl = Url, Generation = 1, MediaIdentifier = "image",
            DestinationPath = Path.Combine(directory.Path, "image.png"),
            DownloadUrl = "https://stub.test/image.png", Status = MediaWorkStatus.Ready
        };
        Assert.True(store.CommitMedia(media));
        WorkResource expectedResource = path switch
        {
            "intent" => original with { Generation = 2, Intent = WorkIntent.Repairing },
            "state" => original with { CapturePending = true },
            _ => original
        };
        MediaWork expectedMedia = path == "media" ? media with { Attempts = 1 } : media;
        WorkStateAccess access = WorkStateAccess.For(store);
        lease.Admission.CompleteStartup();
        using ProfileAdmissionToken accepted = lease.Admission.EnterOrdinaryWriter();
        using CancellationTokenSource cancellation = new();
        using ManualResetEventSlim continueOuter = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ProfileAdmissionToken>? exclusive = null;
        Task? drain = null;
        int observerCalls = 0;

        object? InvokeAccess() => path switch
        {
            "media" => access.TryCommitMedia(expectedMedia, media, accepted),
            "intent" => access.CommitIntentUnderFence(expectedResource, accepted),
            "state" => access.CommitStateUnderFence(expectedResource, accepted),
            _ => throw new ArgumentOutOfRangeException(nameof(path))
        };
        void InvokeStore()
        {
            switch (path)
            {
                case "media": store.TryCommitMediaAdmitted(expectedMedia, media, accepted); break;
                case "intent": store.CommitIntentUnderFence(expectedResource, accepted); break;
                case "state": store.CommitStateUnderFence(expectedResource, accepted); break;
                default: throw new ArgumentOutOfRangeException(nameof(path));
            }
        }
        void AssertRows(WorkResource resource, MediaWork row)
        {
            // Passive reads open fresh SQLite connections, not a cached access result.
            Assert.Equal(resource, Assert.Single(store.LoadResources()));
            Assert.Equal(row, Assert.Single(store.LoadMedia(Url, 1)));
            if (resource.Generation != 1) Assert.Empty(store.LoadMedia(Url, resource.Generation));
        }

        store.AdmittedWriteObserver = () =>
        {
            // The fixture has only one admitted worker. Guard also prevents a
            // second callback from blocking on a hypothetical nested wrapper.
            if (Interlocked.Increment(ref observerCalls) != 1) return;
            entered.TrySetResult();
            Assert.True(continueOuter.Wait(Deadline), "Test did not release the accepted outer use.");
        };
        Task<object?> worker = Task.Run(() =>
        {
            using IDisposable fence = access.Enter(Url);
            return InvokeAccess();
        });
        try
        {
            await entered.Task.WaitAsync(Deadline);
            Assert.False(worker.IsCompleted);
            if (shutdown)
            {
                lease.Admission.BeginShutdown();
                drain = lease.Admission.DrainAsync();
            }
            else exclusive = lease.Admission.EnterExclusiveAsync(cancellation.Token).AsTask();
            Task barrier = drain ?? exclusive!;
            Assert.False(barrier.IsCompleted);

            accepted.Dispose();
            // No further observation callbacks are needed. The already executing
            // callback still owns its local wait until continueOuter is signalled.
            store.AdmittedWriteObserver = null;
            Assert.False(barrier.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => { InvokeAccess(); });
            Assert.Throws<InvalidOperationException>(InvokeStore);
            Assert.Throws<InvalidOperationException>(() => lease.Admission.EnterOrdinaryWriter());
            AssertRows(original, media);
            Assert.Equal(1, Volatile.Read(ref observerCalls));
            Assert.False(worker.IsCompleted);
            Assert.False(barrier.IsCompleted);

            continueOuter.Set();
            object? result = await worker.WaitAsync(Deadline);
            if (path == "media") Assert.True(Assert.IsType<bool>(result));
            else Assert.Equal(expectedResource, Assert.IsType<WorkResource>(result));
            // Last-use retirement must grant Exclusive or finish shutdown drain.
            if (shutdown) await drain!.WaitAsync(Deadline);
            else (await exclusive!.WaitAsync(Deadline)).Dispose();
            AssertRows(expectedResource, expectedMedia);
            Assert.Throws<InvalidOperationException>(() => { InvokeAccess(); });
            Assert.Throws<InvalidOperationException>(InvokeStore);
            AssertRows(expectedResource, expectedMedia);
            if (shutdown) lease.Admission.CompleteShutdown();
        }
        finally
        {
            // Never leave the accepted worker gated on assertion failure. Events
            // and the profile lease outlive the observed worker completion.
            continueOuter.Set();
            accepted.Dispose();
            cancellation.Cancel();
            try { await worker.WaitAsync(Deadline); }
            finally
            {
                store.AdmittedWriteObserver = null;
                if (exclusive != null)
                {
                    try { (await exclusive.WaitAsync(Deadline)).Dispose(); }
                    catch (OperationCanceledException) { }
                }
            }
        }
    }
}

using System.Diagnostics;
using Avalonia.Threading;
using MeowChan.Desktop;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// Focused lifecycle/dispatcher smoke. The native stop is a controlled fake; real codec/frame
/// acceptance remains VideoGalleryChecks/VideoContrastChecks and the pinned apphost playback probe.
/// The root-owned reader-ux hook awaits RunAsync on the dispatcher before the existing video checks.
/// No profile, original fixture, network, native runtime or real session is changed here.
/// </summary>
internal static class VideoLifecycleChecks
{
    internal static async Task RunAsync()
    {
        if (!Dispatcher.UIThread.CheckAccess()) throw new InvalidOperationException("Run the video lifecycle smoke on the dispatcher.");
        await CheckCommandAuthorizationAsync();
        TimeSpan deadline = TimeSpan.FromSeconds(15);
        TaskCompletionSource nativeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource diagnosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource heartbeat = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim nativeReturn = new(false);
        // Only this fixture shortens the diagnostic threshold; production remains five seconds.
        VideoCleanupWatch watch = new("smoke.video.cleanup", deadline: Stopwatch.Frequency / 10);
        watch.Pending += _ => diagnosed.TrySetResult();
        int streamsReleased = 0, enginesReleased = 0;
        VideoPlaybackLifetime lifetime = new(_ =>
        {
            watch.Phase("native-stop");
            nativeEntered.TrySetResult();
            nativeReturn.Wait(); // A real blocking native call, modeled on the same production worker.
            watch.Phase("stream-dispose");
            Interlocked.Increment(ref streamsReleased);
            return Task.CompletedTask;
        }, () => { Interlocked.Increment(ref enginesReleased); return Task.CompletedTask; }, watch);
        var show = lifetime.AcceptShow(CancellationToken.None)
            ?? throw new InvalidOperationException("The fixture's show was not accepted.");
        lifetime.FinishShow(show);
        Task stop = lifetime.StopAsync();
        Task dispose = lifetime.DisposeAsync();
        try
        {
            await nativeEntered.Task.WaitAsync(deadline);
            Dispatcher.UIThread.Post(() => heartbeat.TrySetResult());
            await heartbeat.Task.WaitAsync(deadline);
            await diagnosed.Task.WaitAsync(deadline);
            for (int i = 0; i < 1000; i++)
            {
                Require(ReferenceEquals(dispose, lifetime.DisposeAsync()), "Repeated dispose did not share the real drain.");
                Require(ReferenceEquals(dispose, lifetime.StopAsync()), "Stop during disposal skipped the real drain.");
            }
            VideoCleanupEvidence evidence = watch.Evidence;
            Require(evidence.Pending && evidence.Overdue && evidence.OwnershipRetained && evidence.Phase == "native-stop",
                "The independent deadman lost ownership or did not identify the pending native stop.");
            Require(!stop.IsCompleted && !dispose.IsCompleted && streamsReleased == 0 && enginesReleased == 0,
                "A cleanup caller observed success or released an input/engine before native return.");
            Require(!lifetime.IsCurrent(show.Generation) && lifetime.AcceptShow(CancellationToken.None) == null,
                "A stale callback or a post-dispose show remained admissible.");
            nativeReturn.Set();
            await dispose.WaitAsync(deadline);
            Require(stop.IsCompletedSuccessfully && streamsReleased == 1 && enginesReleased == 1,
                "Actual native return did not complete exactly one drain.");
            Require(!watch.Evidence.OwnershipRetained, "Cleanup still owns resources after a successful release.");
            Console.WriteLine("SMOKE video lifecycle: held native stop, live dispatcher, independent pending diagnosis, coalesced stop/dispose and true release passed (controlled native fake).");
        }
        finally
        {
            nativeReturn.Set();
            await dispose.WaitAsync(deadline);
        }
    }
    private sealed record CommandTarget(long Generation)
    {
        internal int Commands;
        internal bool Released;
        internal void Invoke()
        {
            Require(!Released, "A command reached a released fake native player.");
            Interlocked.Increment(ref Commands);
        }
    }

    private static async Task CheckCommandAuthorizationAsync()
    {
        TimeSpan deadline = TimeSpan.FromSeconds(15);
        using SemaphoreSlim operations = new(1, 1);
        TaskCompletionSource cleanupEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource allowCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CommandTarget? playback = null;
        VideoPlaybackLifetime lifetime = new(async _ =>
        {
            cleanupEntered.TrySetResult();
            await allowCleanup.Task; // Retiring playback stays present; commands can win the native lock.
            await operations.WaitAsync();
            try { if (playback != null) playback.Released = true; playback = null; }
            finally { operations.Release(); }
        }, () => Task.CompletedTask);
        var first = lifetime.AcceptShow(CancellationToken.None)
            ?? throw new InvalidOperationException("The command fixture's first show was not accepted.");
        CommandTarget old = new(first.Generation);
        playback = old;
        lifetime.FinishShow(first);
        Task stopping = lifetime.StopAsync();
        VideoPlaybackLifetime.ShowRequest? next = null;
        Task? resuming = null;
        try
        {
            await cleanupEntered.Task.WaitAsync(deadline);
            VideoPlaybackLifetime.ShowRequest accepted = lifetime.AcceptShow(CancellationToken.None)
                ?? throw new InvalidOperationException("The command fixture's resume was not accepted.");
            next = accepted;
            CommandTarget replacement = new(accepted.Generation);
            async Task ResumeAsync()
            {
                try
                {
                    await accepted.PreviousCleanup.WaitAsync(accepted.Token);
                    await operations.WaitAsync();
                    try { if (lifetime.IsCurrent(accepted.Generation)) playback = replacement; }
                    finally { operations.Release(); }
                }
                catch (OperationCanceledException) when (accepted.Token.IsCancellationRequested) { }
                finally { lifetime.FinishShow(accepted); }
            }
            resuming = ResumeAsync();
            Require(lifetime.IsCurrent(accepted.Generation) && !resuming.IsCompleted && ReferenceEquals(playback, old),
                "The command smoke did not establish current resume B with retiring playback A.");
            // This is the exact production command adapter, including the existing semaphore.
            await VideoPlaybackCommands.RunAsync(operations, lifetime, accepted.Generation, () => playback,
                static target => target.Generation, static target => target.Invoke()).WaitAsync(deadline);
            Require(old.Commands == 0 && !old.Released && !stopping.IsCompleted,
                "A command for resume B operated on retiring playback A while cleanup was held.");
            allowCleanup.TrySetResult();
            await resuming.WaitAsync(deadline);
            await VideoPlaybackCommands.RunAsync(operations, lifetime, first.Generation, () => playback,
                static target => target.Generation, static target => target.Invoke()).WaitAsync(deadline);
            await VideoPlaybackCommands.RunAsync(operations, lifetime, accepted.Generation, () => playback,
                static target => target.Generation, static target => target.Invoke()).WaitAsync(deadline);
            Require(old.Released && old.Commands == 0 && replacement.Commands == 1,
                "Fresh commands did not target only the resumed playback after true cleanup.");
            Console.WriteLine("SMOKE video command authorization: Suspend/Resume, current B with held retiring A, rejected old target, and fresh B-only command passed (production adapter, fake native targets).");
        }
        finally
        {
            allowCleanup.TrySetResult();
            if (resuming != null) await resuming.WaitAsync(deadline);
            else if (next != null) lifetime.FinishShow(next);
            await lifetime.DisposeAsync().WaitAsync(deadline);
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}

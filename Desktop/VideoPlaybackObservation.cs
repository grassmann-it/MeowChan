using System;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Diagnostics;

namespace MeowChan.Desktop;

/// <summary>
/// An observer owns the native-operation gate just like a command. Selection can advance
/// while it waits: never label a retired player's clock/frames as the selected successor.
/// Cancellation removes a queued observation, but never releases an admitted native call.
/// </summary>
internal static class VideoPlaybackObservation
{
    internal static async Task<TResult> RunAsync<TPlayback, TResult>(SemaphoreSlim operations,
        VideoPlaybackLifetime lifetime, long generation, Func<TPlayback?> currentPlayback,
        Func<TPlayback, long> playbackGeneration, Func<TPlayback, TResult> observe,
        Func<string, TResult> unavailable, CancellationToken cancellationToken = default) where TPlayback : class
    {
        using var scheduled = PerformanceDiagnostics.Measure("video.observer", revision: generation);
        return await Task.Run(async () =>
        {
            using (PerformanceDiagnostics.Measure("video.observer.operations-wait", revision: generation))
                await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (lifetime.HasCleanupFailure) return unavailable("CleanupFailed");
                TPlayback? playback = currentPlayback();
                if (playback == null) return unavailable("Stopped");
                if (playbackGeneration(playback) != generation || !lifetime.IsCurrent(generation))
                    return unavailable("Retired");
                TResult evidence;
                using (PerformanceDiagnostics.Measure("video.observer.native-read", revision: generation))
                    evidence = observe(playback);
                // Stop/AcceptShow invalidate admission without waiting for native reads to return.
                // Do not publish a now-retired read as evidence for the new selection.
                return lifetime.IsCurrent(generation) ? evidence : unavailable("Retired");
            }
            finally { operations.Release(); }
        }, cancellationToken).ConfigureAwait(false);
    }
}

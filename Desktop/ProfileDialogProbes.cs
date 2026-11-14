using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Persistence;
using Serilog;

namespace MeowChan.Desktop;

/// <summary>
/// Classifies the folder typed into "New profile" without ever touching the file system on the calling (UI) thread: each request
/// cancels the one before, waits for a short pause in typing, then classifies on the thread pool and hands the result back through
/// <c>post</c>. A result that was overtaken by a newer request is dropped. Pure of any control, so the timing is testable.
/// </summary>
internal sealed class ProfileFolderKindProbe
{
    /// <summary>Fixed pause in typing before a folder is looked at (a UI timing, not a size or count limit).</summary>
    internal static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(250);

    private readonly Func<string, ProfileFolderKind> _classify;
    private readonly TimeSpan _delay;
    private readonly Action<Action> _post;
    private readonly Action<string, ProfileFolderKind> _completed;
    private readonly ILogger _logger;
    private CancellationTokenSource? _current;

    /// <param name="classify">The file system check, run on a thread-pool thread.</param>
    /// <param name="delay">The pause in typing; <see cref="DefaultDelay"/> in the dialog.</param>
    /// <param name="post">Marshals the result to the UI thread (<c>Dispatcher.UIThread.Post</c>).</param>
    /// <param name="completed">Receives the folder that was classified and its kind, on the UI thread.</param>
    internal ProfileFolderKindProbe(Func<string, ProfileFolderKind> classify, TimeSpan delay, Action<Action> post,
        Action<string, ProfileFolderKind> completed, ILogger? logger = null)
    {
        _classify = classify ?? throw new ArgumentNullException(nameof(classify));
        _delay = delay; _post = post ?? throw new ArgumentNullException(nameof(post));
        _completed = completed ?? throw new ArgumentNullException(nameof(completed));
        _logger = logger ?? Log.Logger;
    }

    /// <summary>Starts a classification of <paramref name="folder"/> and supersedes every earlier one. Returns at once.</summary>
    internal void Request(string folder)
    {
        CancellationTokenSource source = new();
        // A source without timers or linked tokens needs no Dispose; disposing would race with the waiting task's token.
        Interlocked.Exchange(ref _current, source)?.Cancel();
        CancellationToken token = source.Token;
        _ = Task.Run(() => RunAsync(folder, token));
    }

    /// <summary>Drops the pending classification (the folder is no longer one to look at, or the dialog closed).</summary>
    internal void Cancel() => Interlocked.Exchange(ref _current, null)?.Cancel();

    private async Task RunAsync(string folder, CancellationToken token)
    {
        try
        {
            if (_delay > TimeSpan.Zero) await Task.Delay(_delay, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // A dedicated thread: on a stalled network path the check can block for a long time, and a thread-pool thread held that way
            // would starve everything else that runs on the pool (including the continuation that notices the stall).
            ProfileFolderKind kind = await Task.Factory.StartNew(() => _classify(folder), CancellationToken.None, TaskCreationOptions.LongRunning,
                TaskScheduler.Default).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;
            _post(() => { if (!token.IsCancellationRequested) _completed(folder, kind); });
        }
        catch (OperationCanceledException) { /* Superseded or closed. */ }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The label under the field is a convenience: a failure keeps the previous one and is only logged.
            _logger.Warning(error, "The profile folder could not be classified.");
        }
    }
}

/// <summary>What a row of "Switch profile" knows about its profile folder once it was looked at.</summary>
internal enum ProfileRowStatus
{
    /// <summary>The folder exists and no other window holds the profile.</summary>
    Ready,
    /// <summary>The folder no longer exists.</summary>
    Missing,
    /// <summary>Another window holds the profile.</summary>
    InUse,
    /// <summary>The folder did not answer in time (an offline network drive, a stalled mount); nothing is known.</summary>
    Unknown
}

/// <summary>
/// Looks at the folders of the listed profiles without blocking the dialog: every folder is probed on the thread pool, a few at a
/// time, and a folder that does not answer within <see cref="Timeout"/> is reported as <see cref="ProfileRowStatus.Unknown"/> while
/// its stalled probe is left to finish on its own (on a dedicated thread, so stalled shares never starve the thread pool). The rows are
/// on screen before the first answer.
/// </summary>
internal static class ProfileRowProbe
{
    /// <summary>Fixed ceiling: how long one folder may take before its row says "status unknown" (a UI patience, not a size or count limit).</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    /// <summary>Fixed ceiling: folders probed at the same time, so stalled network drives cannot occupy every thread-pool thread.</summary>
    internal const int Parallelism = 16;

    /// <summary>The file system questions for one listed profile folder (blocking; never call it on the UI thread).</summary>
    internal static ProfileRowStatus Probe(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return ProfileRowStatus.Missing;
            return ProfileLease.IsInUse(folder) ? ProfileRowStatus.InUse : ProfileRowStatus.Ready;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return ProfileRowStatus.Unknown; }
    }

    /// <summary>
    /// Probes <paramref name="folders"/> and calls <paramref name="report"/> (from a thread-pool thread, once per index) as each answer
    /// arrives. Completes when every folder has answered or timed out, or when <paramref name="token"/> is cancelled.
    /// </summary>
    internal static async Task RunAsync(IReadOnlyList<string> folders, Func<string, ProfileRowStatus> probe, int parallelism, TimeSpan timeout,
        Action<int, ProfileRowStatus> report, CancellationToken token)
    {
        // Not disposed: cancelled probes may still release it after this method returned, and it holds no wait handle.
        SemaphoreSlim gate = new(Math.Max(1, parallelism));
        Task[] tasks = new Task[folders.Count];
        for (int index = 0; index < tasks.Length; index++)
        {
            int at = index;
            tasks[at] = Task.Run(() => OneAsync(folders[at], at, probe, timeout, report, gate, token));
        }
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* Superseded or closed. */ }
    }

    private static async Task OneAsync(string folder, int index, Func<string, ProfileRowStatus> probe, TimeSpan timeout,
        Action<int, ProfileRowStatus> report, SemaphoreSlim gate, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        ProfileRowStatus status;
        try
        {
            // A dedicated thread, not the pool: a probe stalled on an offline share keeps its thread until the share answers, and
            // pool threads held that way would starve the pool, including the timers and continuations that detect the stall.
            Task<ProfileRowStatus> work = Task.Factory.StartNew(() => probe(folder), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Task finished = await Task.WhenAny(work, Task.Delay(timeout, token)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (ReferenceEquals(finished, work)) status = await work.ConfigureAwait(false);
            else
            {
                status = ProfileRowStatus.Unknown;
                // The stalled probe keeps its thread until the share answers; its eventual fault is observed and dropped.
                _ = work.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
            }
        }
        finally { gate.Release(); }
        report(index, status);
    }
}

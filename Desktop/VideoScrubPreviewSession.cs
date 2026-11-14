using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Diagnostics;
using MeowChan.Storage;
using Serilog;

namespace MeowChan.Desktop;

/// <summary>One scrub thumbnail: its 5 % slot, the slot's position as a fraction, and a PNG within the scrub bounds.</summary>
internal sealed record VideoScrubFrame(int Slot, double Fraction, byte[] Png);

/// <summary>
/// Lazy scrub thumbnails of one recorded video while it is shown (owner: the player, one session per opened video).
/// <list type="bullet">
/// <item>Nothing is decoded until a slot is asked for; each slot is decoded at most once per session and kept in memory
/// (at most <see cref="VideoScrubSlots.Count"/> thumbnails), on disk through <see cref="VideoScrubPreviewCache"/>.</item>
/// <item>Concurrent requests for one slot share one load. A request whose callers have all left before its native decode
/// began is dropped, so quick hovering only decodes where the pointer rests (latest wins); a decode that already began
/// finishes and fills the cache.</item>
/// <item>Before a stored thumbnail is used the original is opened verified (hashed once, then file identity); a decoded one
/// is published and stored only if the original still verifies after the decode. An original that is missing or changed makes
/// the whole session unavailable, for good: at that cut every held thumbnail is dropped, both <see cref="TryGetReady"/> and
/// <see cref="GetSlotAsync"/> return nothing from then on, and loads still in flight neither publish nor store. A slot whose
/// decode failed or timed out stays empty for this session. Cancellation never records a failure.</item>
/// <item><see cref="DisposeAsync"/> cancels outstanding work and waits for it; callers still waiting see cancellation, and
/// a native decode that ignores cancellation keeps only the shared decode gate occupied until it returns.</item>
/// </list>
/// </summary>
internal sealed class VideoScrubPreviewSession : IAsyncDisposable
{
    // A decoder gets this long past its own timeout to honor cancellation before the session stops waiting for it.
    internal static readonly TimeSpan NativeGrace = TimeSpan.FromSeconds(2);
    private readonly VideoScrubPreviewCache _cache;
    private readonly MediaAssetSnapshot _manifest;
    private readonly VerifiedFileMemo _verifiedFiles;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _verifyGate = new(1, 1); // one hash of this original at a time; later opens hit the memo
    // Linearizes the final store/publish with the session-wide unavailable cut. A frame cannot be stored after a
    // verification failure has won this gate, even if the decode completed at the same time.
    private readonly SemaphoreSlim _commitGate = new(1, 1);
    private readonly object _lock = new();
    private readonly byte[]?[] _frames = new byte[]?[VideoScrubSlots.Count];
    private readonly bool[] _failed = new bool[VideoScrubSlots.Count];
    private readonly Work?[] _work = new Work?[VideoScrubSlots.Count];
    private readonly HashSet<Work> _live = new();
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _unavailable, _disposing;
    private int _decodes;

    private sealed class Work(int slot, CancellationTokenSource cancel)
    {
        internal int Slot { get; } = slot;
        internal CancellationTokenSource Cancel { get; } = cancel;
        internal Task<byte[]?> Task { get; set; } = System.Threading.Tasks.Task.FromResult<byte[]?>(null);
        internal int Waiters;
        internal bool Started;
    }

    internal VideoScrubPreviewSession(VideoScrubPreviewCache cache, MediaAssetSnapshot manifest, MediaAssetReference asset, VerifiedFileMemo verifiedFiles)
    {
        _cache = cache; _manifest = manifest; Asset = asset; _verifiedFiles = verifiedFiles;
    }

    internal MediaAssetReference Asset { get; }

    /// <summary>True once the original failed verification; no slot of this session returns a thumbnail any more, held ones included.</summary>
    internal bool IsUnavailable { get { lock (_lock) return _unavailable; } }

    /// <summary>Native decodes this session started (diagnostics and tests).</summary>
    internal int DecodeCount => Volatile.Read(ref _decodes);

    /// <summary>The thumbnail of <paramref name="fraction"/> if this session already holds it (for an immediate hover update).</summary>
    internal bool TryGetReady(double fraction, out VideoScrubFrame? frame)
    {
        int slot = VideoScrubSlots.FromFraction(fraction);
        lock (_lock)
        {
            byte[]? png = _disposing || _unavailable ? null : _frames[slot];
            frame = png == null ? null : new VideoScrubFrame(slot, VideoScrubSlots.Fraction(slot), png);
            return frame != null;
        }
    }

    /// <summary>The thumbnail for a seek-bar fraction (0..1); see <see cref="GetSlotAsync"/>.</summary>
    internal Task<VideoScrubFrame?> GetAsync(double fraction, TimeSpan duration, CancellationToken token) =>
        GetSlotAsync(VideoScrubSlots.FromFraction(fraction), duration, token);

    /// <summary>
    /// The thumbnail of <paramref name="slot"/>, or null when there is none (unknown duration, unavailable original, failed slot).
    /// <paramref name="duration"/> is the player's media length; it only places the decode and is not part of the cache identity.
    /// Cancel <paramref name="token"/> when the pointer leaves the slot. Throws <see cref="ObjectDisposedException"/> after disposal.
    /// </summary>
    internal async Task<VideoScrubFrame?> GetSlotAsync(int slot, TimeSpan duration, CancellationToken token)
    {
        VideoScrubSlots.Validate(slot);
        token.ThrowIfCancellationRequested();
        Work work;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposing, this);
            if (_unavailable) return null;
            if (_frames[slot] is { } ready) return new VideoScrubFrame(slot, VideoScrubSlots.Fraction(slot), ready);
            if (_failed[slot] || duration <= TimeSpan.Zero) return null;
            work = _work[slot] ??= Start(slot, duration);
            work.Waiters++;
        }
        try
        {
            byte[]? png = await work.Task.WaitAsync(token).ConfigureAwait(false);
            if (png == null) return null;
            // Published before the cut, but the original was lost since: nothing is handed out after it.
            lock (_lock) if (_unavailable) return null;
            return new VideoScrubFrame(slot, VideoScrubSlots.Fraction(slot), png);
        }
        finally { Leave(work); }
    }

    private Work Start(int slot, TimeSpan duration)
    {
        Work work = new(slot, CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
        _live.Add(work);
        work.Task = Task.Run(() => LoadAsync(work, duration), CancellationToken.None);
        return work;
    }

    private void Leave(Work work)
    {
        bool cancel = false;
        lock (_lock)
        {
            // The last caller left before the native decode began: drop the queued work and detach it, so a new
            // request for the slot starts fresh instead of joining a load that is being canceled.
            if (--work.Waiters == 0 && !work.Started && !work.Task.IsCompleted)
            {
                cancel = true;
                if (_work[work.Slot] == work) _work[work.Slot] = null;
            }
        }
        if (cancel)
            try { work.Cancel.Cancel(); }
            catch (ObjectDisposedException) { /* The load completed in between. */ }
    }

    private async Task<byte[]?> LoadAsync(Work work, TimeSpan duration)
    {
        CancellationToken token = work.Cancel.Token;
        VideoScrubFrameKey key = VideoScrubFrameKey.For(Asset, work.Slot);
        VideoScrubPreviewOptions options = _cache.Options;
        SemaphoreSlim gate = _cache.DecodeGate;
        long request = PerformanceDiagnostics.NextId();
        bool gateHeld = false, opening = false, abandoned = false;
        Task<byte[]>? decoding = null;
        PerformanceDiagnostics.Record("gallery.scrub", "accepted", request, asset: Asset.AssetId, value: work.Slot);
        try
        {
            lock (_lock) if (_unavailable) return null;
            // A stored thumbnail needs no native gate, only a verified original.
            opening = true;
            Stream video = await OpenVerifiedAsync(token).ConfigureAwait(false);
            opening = false;
            byte[]? png;
            await using (video.ConfigureAwait(false))
                png = await _cache.ReadAsync(key, token).ConfigureAwait(false);
            if (png != null)
            {
                PerformanceDiagnostics.Record("gallery.scrub", "verified-hit", request, asset: Asset.AssetId, value: work.Slot);
                return Publish(work, png);
            }
            using (PerformanceDiagnostics.Measure("gallery.scrub.queue-wait", request, asset: Asset.AssetId))
                await gate.WaitAsync(token).ConfigureAwait(false);
            gateHeld = true;
            lock (_lock)
            {
                // Every caller left while this queued (Leave cancels right after its lock): treat it as canceled, never as failed.
                abandoned = work.Waiters == 0;
                if (abandoned || token.IsCancellationRequested) throw new OperationCanceledException(token);
                if (_unavailable) return null; // lost while this queued: no native decode for a lost original
                work.Started = true;
            }
            using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
            bounded.CancelAfter(options.DecodeTimeout);
            // Another session for the same bytes may have stored it while this one queued.
            png = await _cache.ReadAsync(key, bounded.Token).ConfigureAwait(false);
            if (png != null) return Publish(work, png);
            opening = true;
            video = await OpenVerifiedAsync(bounded.Token).ConfigureAwait(false);
            opening = false;
            Interlocked.Increment(ref _decodes);
            using (PerformanceDiagnostics.Measure("gallery.scrub.decode", request, asset: Asset.AssetId))
            {
                // The decoder owns the verified handle from here on, including failures. One that throws before returning a
                // task may not have taken it: release it here (a second dispose of the handle is harmless).
                try
                {
                    decoding = _cache.Decoder.DecodeAsync(video, VideoScrubSlots.Start(work.Slot, duration),
                        VideoScrubPreviewOptions.ThumbnailMaximumWidth, VideoScrubPreviewOptions.ThumbnailMaximumHeight, bounded.Token);
                }
                catch
                {
                    await video.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
                png = await decoding.WaitAsync(options.DecodeTimeout + NativeGrace, token).ConfigureAwait(false);
            }
            if (!VideoScrubPreviewCache.Valid(png)) throw new InvalidDataException("The scrub thumbnail exceeds its safe limits.");
            // The cut after the decode: the original must still verify (file identity, else a full hash), so a file changed while it
            // was decoded never yields a frame stored under the recorded revision; a session lost meanwhile publishes nothing.
            opening = true;
            await (await OpenVerifiedAsync(bounded.Token).ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
            opening = false;
            return await CommitAsync(work, key, png, bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || abandoned)
        {
            PerformanceDiagnostics.Record("gallery.scrub", "canceled", request, asset: Asset.AssetId, value: work.Slot);
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            bool original = opening && exception is not OperationCanceledException;
            PerformanceDiagnostics.Record("gallery.scrub", original ? "unavailable" : "failed", request, asset: Asset.AssetId, value: work.Slot);
            Log.Warning(exception, original
                ? "The recorded video could not be verified; it has no scrub previews while it is shown."
                : "Could not generate a video scrub preview; the seek bar works without it.");
            if (original)
                await MarkUnavailableAsync().ConfigureAwait(false);
            else
            {
                lock (_lock) _failed[work.Slot] = true;
            }
            return null;
        }
        finally
        {
            if (gateHeld)
            {
                // A native decode that outlived its timeout keeps the shared gate until it actually returns.
                if (decoding is { IsCompleted: false })
                    _ = decoding.ContinueWith(task => { _ = task.Exception; gate.Release(); }, CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                else gate.Release();
            }
            lock (_lock)
            {
                if (_work[work.Slot] == work) _work[work.Slot] = null;
                _live.Remove(work);
            }
            work.Cancel.Dispose();
        }
    }

    /// <summary>
    /// Stores and publishes a completed decode as one session commit. The gate is also used when an integrity failure
    /// closes the session, so a late decode cannot pass the availability check and then write after that cut.
    /// </summary>
    private async Task<byte[]?> CommitAsync(Work work, VideoScrubFrameKey key, byte[] png, CancellationToken token)
    {
        await _commitGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_lock)
                if (_unavailable || _disposing) return null;
            try { await _cache.StoreAsync(key, png, token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The thumbnail is still shown for this session; only its persistence failed.
                Log.Warning(exception, "Could not store a video scrub preview; it stays available while the video is shown.");
            }
            return Publish(work, png);
        }
        finally { _commitGate.Release(); }
    }

    private async Task MarkUnavailableAsync()
    {
        await _commitGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                // The cut: from here on no slot hands out a thumbnail; Publish and every reader check it under this lock.
                _unavailable = true;
                Array.Clear(_frames);
            }
        }
        finally { _commitGate.Release(); }
    }

    /// <summary>Holds and returns <paramref name="png"/>, or null once the session lost its original or is being disposed.</summary>
    private byte[]? Publish(Work work, byte[] png)
    {
        lock (_lock)
        {
            if (_unavailable || _disposing) return null;
            _frames[work.Slot] = png;
            return png;
        }
    }

    private async Task<Stream> OpenVerifiedAsync(CancellationToken token)
    {
        await _verifyGate.WaitAsync(token).ConfigureAwait(false);
        try { return await _manifest.OpenSessionVerifiedReadAsync(Asset.AssetId, _verifiedFiles, token).ConfigureAwait(false); }
        finally { _verifyGate.Release(); }
    }

    /// <summary>Cancels outstanding loads, waits for them and releases the thumbnails. Idempotent; concurrent calls share one disposal.</summary>
    public async ValueTask DisposeAsync()
    {
        bool first;
        Task[] pending;
        lock (_lock)
        {
            first = !_disposing;
            _disposing = true;
            pending = first ? _live.Select(work => (Task)work.Task).ToArray() : Array.Empty<Task>();
        }
        if (!first)
        {
            await _disposed.Task.ConfigureAwait(false);
            return;
        }
        _lifetime.Cancel();
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        catch (Exception) { /* Canceled and failed loads already settled their own state. */ }
        lock (_lock)
        {
            Array.Clear(_frames);
            Array.Clear(_work);
        }
        _verifyGate.Dispose();
        _commitGate.Dispose();
        _lifetime.Dispose();
        _disposed.TrySetResult();
    }
}

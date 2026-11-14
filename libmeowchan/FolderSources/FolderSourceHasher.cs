using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.FolderSources;

/// <summary>Who asked for a hash. A higher priority is served first; only a user action hashes files above the size limit.</summary>
internal enum FolderSourceHashPriority { Background = 0, View = 1, UserAction = 2 }

/// <summary>What one hash request ended with.</summary>
internal enum FolderSourceHashOutcome
{
    /// <summary>The bytes were hashed now and recorded.</summary>
    Hashed,
    /// <summary>The item already had its digest.</summary>
    AlreadyHashed,
    /// <summary>The item, its folder source or its file is gone (or marked missing).</summary>
    Missing,
    /// <summary>The file no longer has the length or write time the catalog knows: a rescan decides what it is now.</summary>
    Changed,
    /// <summary>Larger than the configured limit for automatic hashing; a user action may still hash it.</summary>
    TooLarge,
    /// <summary>The file could not be opened or read (in use, no permission, a link).</summary>
    Unreadable,
    /// <summary>
    /// The file's content is not on this device (an online-only cloud placeholder, W3-08): the background pass never reads it, since
    /// reading would download it; the gallery or a user action may.
    /// </summary>
    NotLocal
}

internal sealed record FolderSourceHashResult(FolderSourceHashOutcome Outcome, FolderSourceItem? Item);

/// <summary>Counts of the hashing work of one session, for the status line.</summary>
internal sealed record FolderSourceHashStatus(int Pending, long HashedThisSession, bool BackgroundRunning, bool Paused);

/// <summary>
/// Lazy SHA-256 of folder source files (PLAN-POWER-TAGGEN §2.2). One job per file reads it at most once at a time; every request
/// for the same file joins that job, and the job is cancelled, even in the middle of reading, as soon as no request waits for it
/// any more (W3-02). Jobs wait for one of <see cref="Parallelism"/> slots, and a waiting user action is served before a waiting
/// view (the opened gallery), which is served before the background pass; the waiting views are bounded by
/// <see cref="ViewQueueLimit"/>, the oldest giving way to the newest. Tile previews never hash (they open manifests with
/// <see cref="Storage.MediaAssetReader.OpenForPreviewAsync"/>). The background pass hashes the rest in catalog order, only while
/// it is not paused and the user has been idle for <see cref="IdleDelay"/>. Files are opened read-only with
/// <see cref="FileShare.Read"/>, links are refused, and a digest is recorded only when length and write time were the same
/// before and after reading, so a file that changes while it is read is never given a wrong identity. Digests are committed in
/// batches (W3-06): a view or a user action commits at once, the background pass every <see cref="CommitBatch"/> digests or
/// after <see cref="CommitDelay"/>.
/// </summary>
internal sealed class FolderSourceHasher : IAsyncDisposable
{
    internal const int DefaultParallelism = 2, MinimumParallelism = 1, ParallelismCeiling = 8;
    internal const int DefaultMaxFileGiB = 8, MinimumMaxFileGiB = 1, MaxFileGiBCeiling = 1_024;
    /// <summary>Fixed: how long the user must have been idle before the background pass reads again.</summary>
    internal static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(3);
    /// <summary>Fixed: background digests committed in one transaction at most, and the longest a background digest waits for it.</summary>
    internal const int CommitBatch = 100;
    internal static readonly TimeSpan CommitDelay = TimeSpan.FromSeconds(1);
    /// <summary>Waiting view jobs by default: the gallery window of 16 files on each side and the opened one.</summary>
    internal const int DefaultViewQueueLimit = 33;
    private const int BufferBytes = 1024 * 1024;

    private sealed class Job(Guid item, FolderSourceHashPriority priority, CancellationTokenSource cancel)
    {
        internal Guid Item { get; } = item;
        internal FolderSourceHashPriority Priority = priority; // under _sync
        internal CancellationTokenSource Cancel { get; } = cancel;
        internal int Waiters; // under _sync
        internal long Order; // under _sync
        internal bool HoldsSlot; // under _sync
        internal TaskCompletionSource Granted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<FolderSourceHashResult> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record PendingDigest(FolderSourceDigest Digest, TaskCompletionSource<bool>? Recorded);

    private readonly FolderSourceCatalog _catalog;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Job> _running = new();
    private readonly List<Job> _queue = new();
    private readonly Dictionary<Guid, PendingDigest> _pending = new();
    private readonly HashSet<Guid> _committing = new();
    private readonly SemaphoreSlim _commitGate = new(1, 1);
    private readonly Timer _commitTimer;
    private bool _commitArmed;
    private readonly CancellationTokenSource _lifetime = new();
    private int _parallelism = DefaultParallelism;
    private int _viewQueueLimit = DefaultViewQueueLimit;
    private int _active;
    private long _order;
    private long _maxFileBytes = (long)DefaultMaxFileGiB * 1024 * 1024 * 1024;
    private long _hashed;
    private long _lastActivityTicks = DateTime.UtcNow.Ticks - TimeSpan.FromHours(1).Ticks;
    private bool _paused;
    private Task? _background;
    private CancellationTokenSource? _backgroundStop;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal FolderSourceHasher(FolderSourceCatalog catalog, Func<DateTimeOffset>? clock = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _commitTimer = new Timer(_ => _ = CommitPendingAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Files read at the same time (a setting).</summary>
    internal int Parallelism
    {
        get => Volatile.Read(ref _parallelism);
        set { Volatile.Write(ref _parallelism, Math.Clamp(value, MinimumParallelism, ParallelismCeiling)); lock (_sync) GrantLocked(); }
    }

    /// <summary>
    /// Most view jobs that wait for a slot; a newer one makes the oldest give way. Derived from the gallery window setting
    /// (both sides and the opened file), never below 1.
    /// </summary>
    internal int ViewQueueLimit
    {
        get => Volatile.Read(ref _viewQueueLimit);
        set => Volatile.Write(ref _viewQueueLimit, Math.Max(1, value));
    }

    /// <summary>Largest file hashed without a user action (a setting, in bytes).</summary>
    internal long MaxFileBytes
    {
        get => Interlocked.Read(ref _maxFileBytes);
        set => Interlocked.Exchange(ref _maxFileBytes, Math.Clamp(value, (long)MinimumMaxFileGiB << 30, (long)MaxFileGiBCeiling << 30));
    }

    /// <summary>Raised after digests were committed (on a worker; observers coalesce).</summary>
    internal event Action? Hashed;

    /// <summary>Test seam: replaces how a file's bytes are read and hashed.</summary>
    internal Func<string, FolderSourceItem, CancellationToken, Task<(FolderSourceHashOutcome, string?)>>? HashFileForTests { get; set; }

    internal FolderSourceHashStatus Status
    {
        get { lock (_sync) return new(_running.Count, Interlocked.Read(ref _hashed), _background is { IsCompleted: false }, _paused); }
    }

    /// <summary>Jobs waiting for a slot right now (tests, diagnostics).</summary>
    internal int WaitingForSlot { get { lock (_sync) return _queue.Count; } }

    /// <summary>The user did something: the background pass waits for <see cref="IdleDelay"/> before reading again.</summary>
    internal void NoteActivity() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

    internal bool Paused
    {
        get { lock (_sync) return _paused; }
        set { lock (_sync) _paused = value; Wake(); }
    }

    /// <summary>
    /// Hashes one item now (or joins the running job for it). Never throws for the file's state: the outcome says what
    /// happened. Throws only when the catalog itself cannot be read or written. Cancelling <paramref name="token"/> ends this
    /// request; the read itself stops once no request waits for it.
    /// </summary>
    internal Task<FolderSourceHashResult> HashAsync(Guid item, FolderSourceHashPriority priority, CancellationToken token = default)
    {
        if (token.IsCancellationRequested) return Task.FromCanceled<FolderSourceHashResult>(token);
        Job job;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_lifetime.IsCancellationRequested, this);
            // A user action may read above the size limit, so it never joins a job that will refuse to; any other request joins.
            if (!_running.TryGetValue(item, out Job? running) || running.Cancel.IsCancellationRequested
                || priority == FolderSourceHashPriority.UserAction && running.Priority != FolderSourceHashPriority.UserAction)
            {
                job = new Job(item, priority, CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
                _running[item] = job;
                // A real thread pool start: the catalog lookup, the stat and the open never run on the caller's (UI) thread.
                _ = Task.Run(() => RunJobAsync(job));
            }
            else
            {
                job = running;
                if (priority > job.Priority) job.Priority = priority; // A waiting job moves up in the queue.
            }
            job.Waiters++;
        }
        return WaitForAsync(job, token);
    }

    private async Task<FolderSourceHashResult> WaitForAsync(Job job, CancellationToken token)
    {
        // No token registration of our own: the wait's own registration may end this method before ours ran.
        try { return await job.Done.Task.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested && !job.Done.Task.IsCompleted)
        {
            Leave(job);
            throw;
        }
    }

    // A request stopped waiting: the last one leaving cancels the job (its slot wait or its read).
    private void Leave(Job job)
    {
        bool cancel;
        lock (_sync) cancel = --job.Waiters <= 0 && !job.Done.Task.IsCompleted;
        if (cancel) CancelJob(job);
    }

    private static void CancelJob(Job job)
    {
        try { job.Cancel.Cancel(); }
        catch (ObjectDisposedException) { /* The job has just ended. */ }
    }

    private async Task RunJobAsync(Job job)
    {
        try { job.Done.TrySetResult(await RunAsync(job).ConfigureAwait(false)); }
        catch (OperationCanceledException error) { job.Done.TrySetCanceled(error.CancellationToken); }
        catch (Exception error)
        {
            job.Done.TrySetException(error);
            _ = job.Done.Task.Exception; // Observed here too: every waiter may have left already.
        }
        finally
        {
            lock (_sync)
                if (_running.TryGetValue(job.Item, out Job? current) && ReferenceEquals(current, job)) _running.Remove(job.Item);
            job.Cancel.Dispose();
        }
    }

    private FolderSourceHashPriority PriorityOf(Job job) { lock (_sync) return job.Priority; }

    private async Task<FolderSourceHashResult> RunAsync(Job job)
    {
        CancellationToken token = job.Cancel.Token;
        Guid itemId = job.Item;
        // A digest of this item that is not committed yet is committed first, so the item is not read a second time.
        bool uncommitted;
        lock (_sync) uncommitted = _pending.ContainsKey(itemId) || _committing.Contains(itemId);
        if (uncommitted) await CommitPendingAsync().ConfigureAwait(false);
        (FolderSourceItem Item, FolderSourceRoot Root)? found = _catalog.Find(itemId);
        if (found is not { } pair || pair.Item.State == FolderSourceItemState.Missing) return new(FolderSourceHashOutcome.Missing, found?.Item);
        if (pair.Item.State == FolderSourceItemState.Hashed) return new(FolderSourceHashOutcome.AlreadyHashed, pair.Item);
        if (PriorityOf(job) != FolderSourceHashPriority.UserAction && pair.Item.Length > MaxFileBytes) return new(FolderSourceHashOutcome.TooLarge, pair.Item);
        await AcquireAsync(job, token).ConfigureAwait(false);
        (FolderSourceHashOutcome outcome, string? sha) result;
        try
        {
            token.ThrowIfCancellationRequested(); // Granted while the last request was leaving: nothing is read.
            string path = FolderSourceIdentity.AbsolutePath(pair.Root.Path, pair.Item.RelativePath);
            result = HashFileForTests is { } fake ? await fake(path, pair.Item, token).ConfigureAwait(false)
                : await HashFileAsync(pair.Root.Path, pair.Item, PriorityOf(job) != FolderSourceHashPriority.Background, token).ConfigureAwait(false);
        }
        finally { Release(job); }
        if (result.outcome != FolderSourceHashOutcome.Hashed || result.sha == null) return new(result.outcome, pair.Item);
        FolderSourceItem hashed = pair.Item with { Sha256 = result.sha, State = FolderSourceItemState.Hashed };
        // Only a view or a user action waits for its digest to be committed; the background pass goes on reading.
        if (!await RecordAsync(new(itemId, pair.Item.Length, pair.Item.ModifiedTicks, result.sha), PriorityOf(job) != FolderSourceHashPriority.Background)
                .ConfigureAwait(false))
        {
            // A scan changed the row meanwhile: report what the catalog says now.
            (FolderSourceItem Item, FolderSourceRoot Root)? now = _catalog.Find(itemId);
            return now is { Item.State: FolderSourceItemState.Hashed } current ? new(FolderSourceHashOutcome.AlreadyHashed, current.Item)
                : new(FolderSourceHashOutcome.Changed, now?.Item ?? pair.Item);
        }
        return new(FolderSourceHashOutcome.Hashed, hashed);
    }

    /// <summary>
    /// Reads the whole file once and returns its SHA-256, or why not. The file and every folder between it and the root must be
    /// plain (no link); length and write time must match the catalog before and after reading. <paramref name="token"/> stops the
    /// read between two blocks.
    /// </summary>
    internal static async Task<(FolderSourceHashOutcome Outcome, string? Sha256)> HashFileAsync(string rootPath, FolderSourceItem item, CancellationToken token) =>
        await HashFileAsync(rootPath, item, allowRecall: true, token).ConfigureAwait(false);

    /// <summary>
    /// <see cref="HashFileAsync(string, FolderSourceItem, CancellationToken)"/>; a file whose content is not on this device (a cloud
    /// placeholder) is only read when <paramref name="allowRecall"/>, else the outcome is <see cref="FolderSourceHashOutcome.NotLocal"/>.
    /// </summary>
    internal static async Task<(FolderSourceHashOutcome Outcome, string? Sha256)> HashFileAsync(string rootPath, FolderSourceItem item, bool allowRecall,
        CancellationToken token)
    {
        string path = FolderSourceIdentity.AbsolutePath(rootPath, item.RelativePath);
        try
        {
            token.ThrowIfCancellationRequested();
            if (!PlainPath(rootPath, item.RelativePath)) return (FolderSourceHashOutcome.Unreadable, null);
            FileInfo before = new(path);
            if (!before.Exists) return (FolderSourceHashOutcome.Missing, null);
            if (before.Length != item.Length || before.LastWriteTimeUtc.Ticks != item.ModifiedTicks) return (FolderSourceHashOutcome.Changed, null);
            if (!allowRecall && FolderSourceEntries.NeedsRecall(before.Attributes)) return (FolderSourceHashOutcome.NotLocal, null);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
            long total = 0;
            try
            {
                await using FileStream stream = new(path, new FileStreamOptions
                {
                    Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read, BufferSize = 0,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });
                if (stream.Length != item.Length) return (FolderSourceHashOutcome.Changed, null);
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferBytes), token).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    total += read;
                    token.ThrowIfCancellationRequested();
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            FileInfo after = new(path);
            if (total != item.Length || !after.Exists || after.Length != item.Length || after.LastWriteTimeUtc.Ticks != item.ModifiedTicks)
                return (FolderSourceHashOutcome.Changed, null);
            return (FolderSourceHashOutcome.Hashed, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return (FolderSourceHashOutcome.Missing, null); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return (FolderSourceHashOutcome.Unreadable, null); }
    }

    // True when the file and each folder between it and the root exist as plain entries (none is a link or junction; a cloud
    // placeholder is plain, see FolderSourceEntries).
    private static bool PlainPath(string rootPath, string relativePath)
    {
        string current = rootPath;
        DirectoryInfo root = new(current);
        if (FolderSourceEntries.IsLink(root, root.Attributes)) return false;
        string[] segments = relativePath.Split('/');
        for (int index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            FileSystemInfo info = index == segments.Length - 1 ? new FileInfo(current) : new DirectoryInfo(current);
            if (!info.Exists) return true; // Reported as missing by the caller.
            if (FolderSourceEntries.IsLink(info, info.Attributes)) return false;
        }
        return true;
    }

    // ---- Priority slots ----

    private async Task AcquireAsync(Job job, CancellationToken token)
    {
        List<Job> evicted;
        lock (_sync)
        {
            job.Order = _order++;
            _queue.Add(job);
            evicted = EvictViewsLocked();
            GrantLocked();
        }
        foreach (Job gone in evicted) CancelJob(gone);
        try { await job.Granted.Task.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            lock (_sync)
            {
                _queue.Remove(job);
                // Granted while the wait was being cancelled: give the slot back.
                if (job.HoldsSlot) { job.HoldsSlot = false; _active--; GrantLocked(); }
            }
            throw;
        }
    }

    private void Release(Job job)
    {
        lock (_sync)
        {
            if (!job.HoldsSlot) return;
            job.HoldsSlot = false;
            _active--;
            GrantLocked();
        }
    }

    // The oldest waiting views give way once more than ViewQueueLimit wait (they are cancelled outside the lock).
    private List<Job> EvictViewsLocked()
    {
        List<Job> evicted = new();
        int views = _queue.Count(candidate => candidate.Priority == FolderSourceHashPriority.View);
        while (views > ViewQueueLimit)
        {
            Job oldest = _queue.Where(candidate => candidate.Priority == FolderSourceHashPriority.View).MinBy(candidate => candidate.Order)!;
            _queue.Remove(oldest);
            evicted.Add(oldest);
            views--;
        }
        return evicted;
    }

    private void GrantLocked()
    {
        while (_active < Parallelism && _queue.Count > 0)
        {
            int best = 0;
            for (int index = 1; index < _queue.Count; index++)
                if (_queue[index].Priority > _queue[best].Priority
                    || _queue[index].Priority == _queue[best].Priority && _queue[index].Order < _queue[best].Order) best = index;
            Job granted = _queue[best];
            _queue.RemoveAt(best);
            if (granted.Cancel.IsCancellationRequested) continue; // Its request left; its wait ends by itself.
            _active++;
            granted.HoldsSlot = true;
            granted.Granted.TrySetResult();
        }
    }

    private bool ForegroundWaiting()
    {
        lock (_sync) return _queue.Any(job => job.Priority > FolderSourceHashPriority.Background);
    }

    // ---- Batched commits ----

    // True when the digest was recorded. A view or a user action (urgent) waits for its commit; the background pass only queues it.
    private async Task<bool> RecordAsync(FolderSourceDigest digest, bool urgent)
    {
        PendingDigest pending = new(digest, urgent ? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) : null);
        bool commitNow;
        lock (_sync)
        {
            _pending[digest.Item] = pending;
            commitNow = urgent || _pending.Count >= CommitBatch;
            if (!commitNow && !_commitArmed)
            {
                _commitArmed = true;
                try { _commitTimer.Change(CommitDelay, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
            }
        }
        if (commitNow) _ = CommitPendingAsync(); // Never throws: failures reach the waiting digests.
        return pending.Recorded == null || await pending.Recorded.Task.ConfigureAwait(false);
    }

    /// <summary>Commits every digest not committed yet (the background pass's batch); returns once they are written or failed.</summary>
    internal async Task FlushAsync() => await CommitPendingAsync().ConfigureAwait(false);

    private async Task CommitPendingAsync()
    {
        await _commitGate.WaitAsync().ConfigureAwait(false);
        PendingDigest[] batch = Array.Empty<PendingDigest>();
        try
        {
            lock (_sync)
            {
                batch = _pending.Values.ToArray();
                _pending.Clear();
                foreach (PendingDigest pending in batch) _committing.Add(pending.Digest.Item);
                _commitArmed = false;
            }
            if (batch.Length == 0) return;
            IReadOnlyList<bool> recorded;
            try
            {
                recorded = await Task.Run(() => _catalog.RecordHashes(batch.Select(pending => pending.Digest).ToArray(), _clock())).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // A view or a user action learns that the catalog failed; background digests are read again by a later pass.
                foreach (PendingDigest pending in batch) pending.Recorded?.TrySetException(error);
                return;
            }
            int count = recorded.Count(value => value);
            Interlocked.Add(ref _hashed, count);
            for (int index = 0; index < batch.Length; index++) batch[index].Recorded?.TrySetResult(recorded[index]);
            if (count > 0) { try { Hashed?.Invoke(); } catch (Exception) { /* Observers never fail a hash. */ } }
        }
        finally
        {
            lock (_sync) foreach (PendingDigest pending in batch) _committing.Remove(pending.Digest.Item);
            _commitGate.Release();
        }
    }

    // ---- Background pass ----

    /// <summary>Starts (or restarts) the low-priority pass over every unhashed item; it ends when nothing is left.</summary>
    internal void StartBackground()
    {
        lock (_sync)
        {
            if (_lifetime.IsCancellationRequested || _background is { IsCompleted: false }) { Wake(); return; }
            _backgroundStop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            CancellationToken token = _backgroundStop.Token;
            _background = Task.Run(() => RunBackgroundAsync(token));
        }
    }

    /// <summary>Stops the background pass, also in the middle of the files it is reading now, and commits what it hashed.</summary>
    internal async Task StopBackgroundAsync()
    {
        Task? running;
        lock (_sync)
        {
            _backgroundStop?.Cancel();
            running = _background;
        }
        Wake();
        if (running != null) { try { await running.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        await CommitPendingAsync().ConfigureAwait(false);
    }

    private void Wake()
    {
        TaskCompletionSource wake;
        lock (_sync) { wake = _wake; _wake = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        wake.TrySetResult();
    }

    private async Task WaitUntilAllowedAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Task wake;
            bool paused;
            lock (_sync) { wake = _wake.Task; paused = _paused; }
            TimeSpan idle = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastActivityTicks));
            if (!paused && idle >= IdleDelay && !ForegroundWaiting()) return;
            TimeSpan wait = paused ? Timeout.InfiniteTimeSpan : idle < IdleDelay ? IdleDelay - idle : TimeSpan.FromMilliseconds(200);
            try { await wake.WaitAsync(wait, token).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
    }

    private async Task RunBackgroundAsync(CancellationToken token)
    {
        long cursor = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await WaitUntilAllowedAsync(token).ConfigureAwait(false);
                int width = Parallelism;
                (IReadOnlyList<FolderSourceItem> items, long next) = _catalog.NextUnhashed(width * 4, MaxFileBytes, cursor);
                if (items.Count == 0) break;
                cursor = next;
                for (int offset = 0; offset < items.Count; offset += width)
                {
                    await WaitUntilAllowedAsync(token).ConfigureAwait(false);
                    Task[] round = items.Skip(offset).Take(width)
                        .Select(item => HashAsync(item.Id, FolderSourceHashPriority.Background, token)).ToArray();
                    try { await Task.WhenAll(round).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                    catch (OperationCanceledException) { /* A job that gave way to newer views is read by a later pass. */ }
                }
            }
            await CommitPendingAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ObjectDisposedException
            or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        { /* The catalog is unavailable; the next start or rescan tries again. */ }
    }

    public async ValueTask DisposeAsync()
    {
        await StopBackgroundAsync().ConfigureAwait(false);
        Task[] running;
        lock (_sync)
        {
            if (_lifetime.IsCancellationRequested) return;
            _lifetime.Cancel(); // Cancels every job: waiting ones leave the queue, reading ones stop between two blocks.
            running = _running.Values.Select(job => (Task)job.Done.Task).ToArray();
        }
        try { await Task.WhenAll(running).ConfigureAwait(false); }
        catch (Exception) { /* Cancelled or failed requests end here; their callers already observed them. */ }
        // What the background pass hashed is kept: the profile is still leased while the session stops.
        await CommitPendingAsync().ConfigureAwait(false);
        await _commitTimer.DisposeAsync().ConfigureAwait(false);
    }
}

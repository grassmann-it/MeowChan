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
    Unreadable
}

internal sealed record FolderSourceHashResult(FolderSourceHashOutcome Outcome, FolderSourceItem? Item);

/// <summary>Counts of the hashing work of one session, for the status line.</summary>
internal sealed record FolderSourceHashStatus(int Pending, long HashedThisSession, bool BackgroundRunning, bool Paused);

/// <summary>
/// Lazy SHA-256 of folder source files (PLAN-POWER-TAGGEN §2.2). A file is hashed at most once per request at a time; requests
/// wait for one of <see cref="Parallelism"/> slots, and a waiting user action is served before a waiting view (gallery,
/// thumbnail), which is served before the background pass. The background pass hashes the rest in catalog order, only while
/// it is not paused and the user has been idle for <see cref="IdleDelay"/>. Files are opened read-only with
/// <see cref="FileShare.Read"/>, links are refused, and a digest is recorded only when length and write time were the same
/// before and after reading, so a file that changes while it is read is never given a wrong identity.
/// </summary>
internal sealed class FolderSourceHasher : IAsyncDisposable
{
    internal const int DefaultParallelism = 2, MinimumParallelism = 1, ParallelismCeiling = 8;
    internal const int DefaultMaxFileGiB = 8, MinimumMaxFileGiB = 1, MaxFileGiBCeiling = 1_024;
    /// <summary>Fixed: how long the user must have been idle before the background pass reads again.</summary>
    internal static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(3);
    private const int BufferBytes = 1024 * 1024;

    private readonly FolderSourceCatalog _catalog;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, (Task<FolderSourceHashResult> Task, FolderSourceHashPriority Priority)> _running = new();
    private readonly List<(FolderSourceHashPriority Priority, long Order, TaskCompletionSource Granted)> _waiting = new();
    private readonly CancellationTokenSource _lifetime = new();
    private int _parallelism = DefaultParallelism;
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
    }

    /// <summary>Files read at the same time (a setting).</summary>
    internal int Parallelism
    {
        get => Volatile.Read(ref _parallelism);
        set { Volatile.Write(ref _parallelism, Math.Clamp(value, MinimumParallelism, ParallelismCeiling)); lock (_sync) GrantLocked(); }
    }

    /// <summary>Largest file hashed without a user action (a setting, in bytes).</summary>
    internal long MaxFileBytes
    {
        get => Interlocked.Read(ref _maxFileBytes);
        set => Interlocked.Exchange(ref _maxFileBytes, Math.Clamp(value, (long)MinimumMaxFileGiB << 30, (long)MaxFileGiBCeiling << 30));
    }

    /// <summary>Raised after a digest was recorded (on a worker; observers coalesce).</summary>
    internal event Action? Hashed;

    /// <summary>Test seam: replaces how a file's bytes are read and hashed.</summary>
    internal Func<string, FolderSourceItem, CancellationToken, Task<(FolderSourceHashOutcome, string?)>>? HashFileForTests { get; set; }

    internal FolderSourceHashStatus Status
    {
        get { lock (_sync) return new(_running.Count, Interlocked.Read(ref _hashed), _background is { IsCompleted: false }, _paused); }
    }

    /// <summary>The user did something: the background pass waits for <see cref="IdleDelay"/> before reading again.</summary>
    internal void NoteActivity() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

    internal bool Paused
    {
        get { lock (_sync) return _paused; }
        set { lock (_sync) _paused = value; Wake(); }
    }

    /// <summary>
    /// Hashes one item now (or joins the running request for it). Never throws for the file's state: the outcome says what
    /// happened. Throws only when the catalog itself cannot be read or written.
    /// </summary>
    internal Task<FolderSourceHashResult> HashAsync(Guid item, FolderSourceHashPriority priority, CancellationToken token = default)
    {
        Task<FolderSourceHashResult> task;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_lifetime.IsCancellationRequested, this);
            // A request joins a running one of at least its priority; a more urgent one does not wait behind a background read.
            if (_running.TryGetValue(item, out var running) && running.Priority >= priority) task = running.Task;
            else
            {
                TaskCompletionSource<FolderSourceHashResult> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
                task = started.Task;
                _running[item] = (task, priority);
                _ = RunAndPublishAsync(item, priority, started);
            }
        }
        return task.WaitAsync(token);
    }

    private async Task RunAndPublishAsync(Guid item, FolderSourceHashPriority priority, TaskCompletionSource<FolderSourceHashResult> started)
    {
        await Task.Yield();
        try { started.TrySetResult(await RunAsync(item, priority).ConfigureAwait(false)); }
        catch (OperationCanceledException error) { started.TrySetCanceled(error.CancellationToken); }
        catch (Exception error) { started.TrySetException(error); }
        finally
        {
            lock (_sync)
                if (_running.TryGetValue(item, out var current) && ReferenceEquals(current.Task, started.Task)) _running.Remove(item);
        }
    }

    private async Task<FolderSourceHashResult> RunAsync(Guid itemId, FolderSourceHashPriority priority)
    {
        {
            CancellationToken token = _lifetime.Token;
            (FolderSourceItem Item, FolderSourceRoot Root)? found = _catalog.Find(itemId);
            if (found is not { } pair || pair.Item.State == FolderSourceItemState.Missing) return new(FolderSourceHashOutcome.Missing, found?.Item);
            if (pair.Item.State == FolderSourceItemState.Hashed) return new(FolderSourceHashOutcome.AlreadyHashed, pair.Item);
            if (priority != FolderSourceHashPriority.UserAction && pair.Item.Length > MaxFileBytes) return new(FolderSourceHashOutcome.TooLarge, pair.Item);
            await AcquireAsync(priority, token).ConfigureAwait(false);
            (FolderSourceHashOutcome outcome, string? sha) result;
            try
            {
                string path = FolderSourceIdentity.AbsolutePath(pair.Root.Path, pair.Item.RelativePath);
                result = HashFileForTests is { } fake ? await fake(path, pair.Item, token).ConfigureAwait(false)
                    : await HashFileAsync(pair.Root.Path, pair.Item, token).ConfigureAwait(false);
            }
            finally { Release(); }
            if (result.outcome != FolderSourceHashOutcome.Hashed || result.sha == null) return new(result.outcome, pair.Item);
            bool recorded = await Task.Run(() => _catalog.RecordHash(itemId, pair.Item.Length, pair.Item.ModifiedTicks, result.sha, _clock(), token), token)
                .ConfigureAwait(false);
            if (!recorded)
            {
                // A scan changed the row meanwhile: report what the catalog says now.
                (FolderSourceItem Item, FolderSourceRoot Root)? now = _catalog.Find(itemId);
                return now is { Item.State: FolderSourceItemState.Hashed } current ? new(FolderSourceHashOutcome.AlreadyHashed, current.Item)
                    : new(FolderSourceHashOutcome.Changed, now?.Item ?? pair.Item);
            }
            Interlocked.Increment(ref _hashed);
            try { Hashed?.Invoke(); } catch (Exception) { /* Observers never fail a hash. */ }
            return new(FolderSourceHashOutcome.Hashed, pair.Item with { Sha256 = result.sha, State = FolderSourceItemState.Hashed });
        }
    }

    /// <summary>
    /// Reads the whole file once and returns its SHA-256, or why not. The file and every folder between it and the root must be
    /// plain (no link); length and write time must match the catalog before and after reading.
    /// </summary>
    internal static async Task<(FolderSourceHashOutcome Outcome, string? Sha256)> HashFileAsync(string rootPath, FolderSourceItem item, CancellationToken token)
    {
        string path = FolderSourceIdentity.AbsolutePath(rootPath, item.RelativePath);
        try
        {
            if (!PlainPath(rootPath, item.RelativePath)) return (FolderSourceHashOutcome.Unreadable, null);
            FileInfo before = new(path);
            if (!before.Exists) return (FolderSourceHashOutcome.Missing, null);
            if (before.Length != item.Length || before.LastWriteTimeUtc.Ticks != item.ModifiedTicks) return (FolderSourceHashOutcome.Changed, null);
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

    // True when the file and each folder between it and the root exist as plain entries (none is a link or junction).
    private static bool PlainPath(string rootPath, string relativePath)
    {
        string current = rootPath;
        if ((new DirectoryInfo(current).Attributes & FileAttributes.ReparsePoint) != 0) return false;
        string[] segments = relativePath.Split('/');
        for (int index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            FileSystemInfo info = index == segments.Length - 1 ? new FileInfo(current) : new DirectoryInfo(current);
            if (!info.Exists) return true; // Reported as missing by the caller.
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        }
        return true;
    }

    // ---- Priority slots ----

    private Task AcquireAsync(FolderSourceHashPriority priority, CancellationToken token)
    {
        TaskCompletionSource granted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            _waiting.Add((priority, _order++, granted));
            GrantLocked();
        }
        return granted.Task.WaitAsync(token);
    }

    private void Release()
    {
        lock (_sync)
        {
            _active--;
            GrantLocked();
        }
    }

    private void GrantLocked()
    {
        while (_active < Parallelism && _waiting.Count > 0)
        {
            int best = 0;
            for (int index = 1; index < _waiting.Count; index++)
                if (_waiting[index].Priority > _waiting[best].Priority
                    || _waiting[index].Priority == _waiting[best].Priority && _waiting[index].Order < _waiting[best].Order) best = index;
            TaskCompletionSource granted = _waiting[best].Granted;
            _waiting.RemoveAt(best);
            _active++;
            granted.TrySetResult();
        }
    }

    private bool ForegroundWaiting()
    {
        lock (_sync) return _waiting.Any(item => item.Priority > FolderSourceHashPriority.Background);
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

    /// <summary>Stops the background pass after the files it is reading now.</summary>
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
                if (items.Count == 0) return;
                cursor = next;
                for (int offset = 0; offset < items.Count; offset += width)
                {
                    await WaitUntilAllowedAsync(token).ConfigureAwait(false);
                    Task[] round = items.Skip(offset).Take(width)
                        .Select(item => HashAsync(item.Id, FolderSourceHashPriority.Background, token)).ToArray();
                    try { await Task.WhenAll(round).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                }
            }
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
            _lifetime.Cancel();
            running = _running.Values.Select(item => (Task)item.Task).ToArray();
            foreach ((_, _, TaskCompletionSource granted) in _waiting) granted.TrySetCanceled();
            _waiting.Clear();
        }
        try { await Task.WhenAll(running).ConfigureAwait(false); }
        catch (Exception) { /* Cancelled or failed requests end here; their callers already observed them. */ }

    }
}

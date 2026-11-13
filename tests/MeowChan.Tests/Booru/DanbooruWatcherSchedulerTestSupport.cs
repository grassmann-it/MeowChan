using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Downloader;
using MeowChan.Persistence;

namespace MeowChan.Tests.Booru;

/// <summary>Deterministic clock; timers fire only on <see cref="Advance"/>. Infinite timers never fire.</summary>
internal sealed class SchedulerClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<Entry> _timers = new();
    private DateTimeOffset _now;

    internal SchedulerClock(DateTimeOffset start) => _now = start;

    /// <summary>Optional probe run on every read of the time, outside the clock's own lock (tests that need to park a caller).</summary>
    internal Action? OnGetUtcNow { get; set; }

    public override DateTimeOffset GetUtcNow()
    {
        OnGetUtcNow?.Invoke();
        lock (_gate) return _now;
    }

    internal int PendingTimers { get { lock (_gate) return _timers.Count(timer => !timer.Disposed && timer.Due != null); } }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Entry entry = new(this, callback, state);
        lock (_gate)
        {
            entry.Schedule(dueTime);
            _timers.Add(entry);
        }
        if (dueTime == TimeSpan.Zero) ThreadPool.QueueUserWorkItem(_ => entry.Fire());
        return entry;
    }

    internal void Advance(TimeSpan amount)
    {
        Entry[] due;
        lock (_gate)
        {
            _now += amount;
            due = _timers.Where(timer => !timer.Disposed && timer.Due is { } at && at <= _now).ToArray();
            _timers.RemoveAll(timer => timer.Disposed);
        }
        foreach (Entry timer in due) timer.Fire();
    }

    private sealed class Entry(SchedulerClock owner, TimerCallback callback, object? state) : ITimer
    {
        internal bool Disposed { get; private set; }
        internal DateTimeOffset? Due { get; private set; }

        internal void Schedule(TimeSpan dueTime) =>
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + (dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime);

        internal void Fire()
        {
            lock (owner._gate)
            {
                if (Disposed || Due is not { } at || at > owner._now) return;
                Due = null;
            }
            callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                if (Disposed) return false;
                Schedule(dueTime);
                return true;
            }
        }

        public void Dispose() { lock (owner._gate) Disposed = true; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

/// <summary>
/// In-memory Danbooru search, per query, newest first: the newest page, or the posts below a cursor
/// (Danbooru <c>page=b&lt;id&gt;</c>), 20 per page. Posts published between calls appear on the newest page
/// only, exactly like a live provider; a cursor page never shifts. Records every call with the clock time
/// and the number of concurrently running calls. It never opens a socket.
/// </summary>
internal sealed class FakeDanbooruSearch(TimeProvider clock) : IDanbooruWatcherSearchPort
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<long>> _posts = new(StringComparer.Ordinal);
    private readonly Queue<Func<string, string?, DanbooruWatcherSearchOutcome?>> _script = new();
    private int _concurrent;

    internal List<(string Query, string? Before, DateTimeOffset At)> Calls { get; } = new();
    internal int MaxConcurrent { get; private set; }
    internal int Cancelled;
    internal DateTimeOffset? TransportCooldown { get; set; }
    internal Func<string, string?, CancellationToken, Task>? Hold { get; set; }
    internal Func<long, BooruPostClaim>? ClaimFactory { get; set; }

    public int PageSize => 20;

    public DateTimeOffset? GetProviderCooldownUntilUtc() =>
        TransportCooldown is { } until && until > clock.GetUtcNow() ? until : null;

    internal int CallCount { get { lock (_gate) return Calls.Count; } }

    internal string?[] Cursors { get { lock (_gate) return Calls.Select(call => call.Before).ToArray(); } }

    internal void Publish(string query, params long[] ids)
    {
        lock (_gate)
        {
            if (!_posts.TryGetValue(query, out List<long>? list)) _posts[query] = list = new();
            list.AddRange(ids);
            list.Sort((left, right) => right.CompareTo(left));
        }
    }

    internal void Next(Func<string, string?, DanbooruWatcherSearchOutcome?> outcome) { lock (_gate) _script.Enqueue(outcome); }

    /// <summary>Number of check sessions opened / disposed, and the session index of every call.</summary>
    internal int SessionsOpened { get { lock (_gate) return _sessionsOpened; } }
    internal int SessionsDisposed { get { lock (_gate) return _sessionsDisposed; } }
    internal List<int> CallSessions { get; } = new();
    private int _sessionsOpened;
    private int _sessionsDisposed;

    internal bool FailOpen { get; set; }

    public IDanbooruWatcherSearchSession OpenCheck()
    {
        if (FailOpen) throw new InvalidOperationException("The search path could not open a check.");
        int index;
        lock (_gate) index = ++_sessionsOpened;
        return new Session(this, index);
    }

    private sealed class Session(FakeDanbooruSearch owner, int index) : IDanbooruWatcherSearchSession
    {
        private bool _disposed;
        public Task<DanbooruWatcherSearchOutcome> SearchAsync(string providerQuery, string? beforePostId, CancellationToken cancellationToken)
            => owner.SearchAsync(index, providerQuery, beforePostId, cancellationToken);
        public void Dispose()
        {
            lock (owner._gate)
            {
                if (_disposed) return;
                _disposed = true;
                owner._sessionsDisposed++;
            }
        }
    }

    private async Task<DanbooruWatcherSearchOutcome> SearchAsync(int session, string providerQuery, string? beforePostId, CancellationToken cancellationToken)
    {
        Func<string, string?, DanbooruWatcherSearchOutcome?>? scripted = null;
        lock (_gate)
        {
            Calls.Add((providerQuery, beforePostId, clock.GetUtcNow()));
            CallSessions.Add(session);
            _concurrent++;
            MaxConcurrent = Math.Max(MaxConcurrent, _concurrent);
            if (_script.Count > 0) scripted = _script.Dequeue();
        }
        try
        {
            if (Hold is { } hold)
            {
                try { await hold(providerQuery, beforePostId, cancellationToken); }
                catch (OperationCanceledException) { Interlocked.Increment(ref Cancelled); throw; }
            }
            if (beforePostId is not null && !DanbooruPostReference.IsValidPostId(beforePostId))
                return new DanbooruWatcherSearchFailed("invalid_cursor");
            if (scripted?.Invoke(providerQuery, beforePostId) is { } result) return result;
            long[] slice;
            lock (_gate)
            {
                IEnumerable<long> all = _posts.TryGetValue(providerQuery, out List<long>? list) ? list : new();
                if (beforePostId is not null)
                {
                    long before = long.Parse(beforePostId, System.Globalization.CultureInfo.InvariantCulture);
                    all = all.Where(id => id < before);
                }
                slice = all.Take(PageSize).ToArray();
            }
            BooruPostClaim[] claims = slice.Select(id => (ClaimFactory ?? MinimalClaim)(id)).ToArray();
            return new DanbooruWatcherSearchSnapshot(providerQuery, beforePostId, claims, slice.Length < PageSize);
        }
        finally
        {
            lock (_gate) _concurrent--;
        }
    }

    internal static BooruPostClaim MinimalClaim(long id) =>
        new("danbooru", id.ToString(System.Globalization.CultureInfo.InvariantCulture), BooruPostClaimPresence.None);
}

/// <summary>Counting admission double; a post is "admitted" once per post id like the B4 identity.</summary>
internal sealed class CountingAdmission : IDanbooruWatcherAdmissionPort
{
    private readonly object _gate = new();
    internal Dictionary<string, int> PerPost { get; } = new(StringComparer.Ordinal);
    internal List<(string PostId, string WatcherId)> Findings { get; } = new();
    internal HashSet<string> Queued { get; } = new(StringComparer.Ordinal);
    internal Func<string, CancellationToken, Task>? Hold { get; set; }
    internal Func<string, Exception?>? Fail { get; set; }

    public async Task<DanbooruWatcherAdmissionResult> AdmitAsync(BooruPostClaim post, string watcherId, string query,
        CancellationToken cancellationToken)
    {
        if (Hold is { } hold) await hold(post.PostId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (Fail?.Invoke(post.PostId) is { } failure) throw failure;
        lock (_gate)
        {
            PerPost[post.PostId] = PerPost.GetValueOrDefault(post.PostId) + 1;
            if (!Findings.Contains((post.PostId, watcherId))) Findings.Add((post.PostId, watcherId));
            return Queued.Add(post.PostId) ? DanbooruWatcherAdmissionResult.Queued : DanbooruWatcherAdmissionResult.AlreadyQueued;
        }
    }

    internal int QueuedCount { get { lock (_gate) return Queued.Count; } }
}

/// <summary>One profile with the B3 catalog in Ready phase; optionally the real B4 queue path.</summary>
internal sealed class SchedulerProfile : IAsyncDisposable
{
    private readonly ProfileLease _lease;

    internal SchedulerProfile(string root, bool withQueue = false, SchedulerCdn? cdn = null)
    {
        ProfileDirectory = Path.Combine(root, "profile");
        OutputRoot = Path.Combine(root, "media");
        Directory.CreateDirectory(ProfileDirectory);
        _lease = ProfileLease.Acquire(ProfileDirectory);
        ProfileDirectory = _lease.ProfileDirectory;
        Catalog = new BooruCatalogStore(ProfileDirectory, _lease);
        if (withQueue)
        {
            Work = new WorkStateStore(ProfileDirectory, _lease);
            Queue = new DownloadQueue(1, 1L << 30);
            Queue.ConfigureWorkState(Work);
        }
        Catalog.Read();
        _lease.Admission.CompleteStartup();
        if (withQueue)
            Originals = new BooruOriginalQueue(Queue!, Catalog, OutputRoot,
                new HttpClient(cdn ?? throw new ArgumentNullException(nameof(cdn)), disposeHandler: false));
    }

    internal string ProfileDirectory { get; }
    internal string OutputRoot { get; }
    internal ProfileLease Lease => _lease;
    internal BooruCatalogStore Catalog { get; }
    internal WorkStateStore? Work { get; }
    internal DownloadQueue? Queue { get; }
    internal BooruOriginalQueue? Originals { get; }

    public async ValueTask DisposeAsync()
    {
        try { if (Queue != null) await Queue.StopAllAsync().WaitAsync(TestWait.Timeout); }
        finally { _lease.Dispose(); }
    }
}

/// <summary>In-memory CDN for originals (no socket).</summary>
internal sealed class SchedulerCdn : HttpMessageHandler
{
    internal ConcurrentDictionary<string, byte[]> Responses { get; } = new(StringComparer.Ordinal);
    internal ConcurrentQueue<string> Requests { get; } = new();

    internal static string OriginalUrl(string postId) => $"https://cdn.donmai.us/original/{postId}.png";

    internal BooruPostClaim Claim(long id)
    {
        string postId = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        byte[] bytes = Enumerable.Range(0, 2048).Select(index => (byte)(index * 7 + id)).ToArray();
        Responses[OriginalUrl(postId)] = bytes;
        return new BooruPostClaim("danbooru", postId, BooruPostClaimPresence.Media | BooruPostClaimPresence.Tags,
            PostUrl: $"https://danbooru.donmai.us/posts/{postId}",
            Tags: [new BooruPostClaimTag("cat", BooruPostClaimTagCategory.General)],
            Media: [new BooruPostClaimMedia(BooruPostClaimMediaRole.Original, OriginalUrl(postId), ".png", bytes.Length,
                ExpectedMd5: Convert.ToBase64String(MD5.HashData(bytes)))]);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string url = request.RequestUri!.AbsoluteUri;
        Requests.Enqueue(url);
        if (!Responses.TryGetValue(url, out byte[]? body))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        ByteArrayContent content = new(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
}

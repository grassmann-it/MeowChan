using System.Collections.Immutable;
using MeowChan.Desktop.Booru;
using MeowChan.PluginSdk;
using SkiaSharp;

namespace MeowChan.Tests.Shared;

// Fakes for the ports of the Danbooru window. They stand in for the host search service, the
// download queue connection and the watcher scheduler, which are wired elsewhere; nothing here
// makes a network request, and every fake records the calls it received so tests can prove that
// a paused or removed watcher, an idle window or an invalid query sent nothing.

/// <summary>Manual clock: the models only read the time, so cooldowns are advanced explicitly.</summary>
internal sealed class BooruManualClock : TimeProvider
{
    private DateTimeOffset _now;
    internal BooruManualClock(DateTimeOffset? start = null) => _now = start ?? new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    internal void Advance(TimeSpan by) => _now += by;
}

internal static class BooruUiFixtures
{
    internal const string Source = "danbooru";

    internal static BooruPostView Post(string id, BooruOriginalAccess original = BooruOriginalAccess.Available,
        BooruRating rating = BooruRating.General, string[]? tags = null, bool preview = true, int score = 12,
        bool deleted = false, bool banned = false, string? rawRating = null)
    {
        List<BooruMediaVariant> media = new();
        if (preview) media.Add(new BooruMediaVariant(BooruMediaRole.Preview, new Uri($"https://cdn.donmai.us/180x180/{id}.jpg"), "jpg", null, 180, 180));
        if (original != BooruOriginalAccess.NotOffered)
            media.Add(new BooruMediaVariant(BooruMediaRole.Original,
                original == BooruOriginalAccess.Available ? new Uri($"https://cdn.donmai.us/original/{id}.png") : null,
                "png", 1_234_567, 1920, 1080));
        string[] names = tags ?? new[] { "cat", "blue_eyes", "sky" };
        BooruTag[] tagList = names.Select((name, index) => new BooruTag(name, index switch
        {
            0 => BooruTagCategory.General, 1 => BooruTagCategory.General, _ => BooruTagCategory.Artist
        })).ToArray();
        return BooruPostView.From(new BooruPost(new BooruPostId(Source, id), BooruFieldPresence.All,
            new Uri($"https://danbooru.donmai.us/posts/{id}"), new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero),
            rating, rawRating ?? rating.ToString()[..1].ToLowerInvariant(), score, 3, tagList, media, null, deleted, banned));
    }

    internal static BooruResultPage Page(string query, int page, bool isEnd, params BooruPostView[] posts) =>
        new(query, page, posts.ToImmutableArray(), isEnd);

    internal static BooruWatcherView Watcher(string id, string query, BooruWatcherStatus status = BooruWatcherStatus.Active,
        BooruManualClock? clock = null, DateTimeOffset? cooldownUntil = null, string? errorCode = null,
        int hits = 0, int queued = 0, bool baseline = true)
    {
        DateTimeOffset now = clock?.GetUtcNow() ?? new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
        return new BooruWatcherView(id, query, status, now - TimeSpan.FromDays(2),
            baseline ? now - TimeSpan.FromMinutes(20) : null,
            status == BooruWatcherStatus.Active ? now + TimeSpan.FromMinutes(40) : null,
            cooldownUntil, errorCode, hits, queued, baseline);
    }

    /// <summary>A small deterministic PNG so previews render without any request.</summary>
    internal static byte[] Png(int seed)
    {
        using SKBitmap bitmap = new(96, 96);
        using SKCanvas canvas = new(bitmap);
        SKColor[] palette = { new(0x4F, 0x8F, 0xD6), new(0xD6, 0x7F, 0x4F), new(0x5F, 0xB0, 0x7A), new(0xB0, 0x6F, 0xC9) };
        canvas.Clear(palette[Math.Abs(seed) % palette.Length]);
        using SKPaint paint = new() { Color = SKColors.White.WithAlpha(150), IsAntialias = true };
        canvas.DrawCircle(48, 40, 22, paint);
        canvas.DrawRect(24, 66, 48, 14, paint);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }
}

internal sealed class FakeBooruSearchPort : IBooruSearchPort
{
    private readonly object _gate = new();
    internal Func<string, int, CancellationToken, Task<BooruCallResult<BooruResultPage>>>? OnSearch { get; set; }
    internal Func<string, CancellationToken, Task<BooruCallResult<BooruPostView>>>? OnPost { get; set; }
    internal List<(string Query, int Page)> Searches { get; } = new();
    internal List<string> PostReads { get; } = new();

    public string ProviderId => BooruUiFixtures.Source;
    public string ProviderName => "Danbooru";

    internal int Requests { get { lock (_gate) return Searches.Count + PostReads.Count; } }

    public Task<BooruCallResult<BooruResultPage>> SearchAsync(string canonicalQuery, int page, CancellationToken cancellationToken)
    {
        lock (_gate) Searches.Add((canonicalQuery, page));
        return OnSearch?.Invoke(canonicalQuery, page, cancellationToken)
            ?? Task.FromResult(BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(canonicalQuery, page, true)));
    }

    public Task<BooruCallResult<BooruPostView>> ReadPostAsync(string postId, CancellationToken cancellationToken)
    {
        lock (_gate) PostReads.Add(postId);
        return OnPost?.Invoke(postId, cancellationToken)
            ?? Task.FromResult(BooruCallResult<BooruPostView>.Ok(BooruUiFixtures.Post(postId)));
    }
}

internal sealed class FakeBooruQueuePort : IBooruQueuePort
{
    private readonly object _gate = new();
    internal Func<BooruPostView, BooruLoadOrigin, Task<BooruLoadResult>>? OnLoad { get; set; }
    internal Dictionary<string, BooruLocalState> States { get; } = new(StringComparer.Ordinal);
    internal List<(string PostId, BooruLoadOrigin Origin)> Loads { get; } = new();
    internal int StateReads { get; private set; }

    public event Action? LocalStatesChanged;

    public async Task<BooruLoadResult> LoadOriginalAsync(BooruPostView post, BooruLoadOrigin origin, CancellationToken cancellationToken)
    {
        lock (_gate) Loads.Add((post.PostId, origin));
        return OnLoad is null ? new BooruLoadResult(BooruLoadStatus.Queued) : await OnLoad(post, origin).ConfigureAwait(false);
    }

    public Task<IReadOnlyDictionary<string, BooruLocalState>> GetLocalStatesAsync(IReadOnlyList<string> postIds, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            StateReads++;
            return Task.FromResult<IReadOnlyDictionary<string, BooruLocalState>>(
                postIds.Where(States.ContainsKey).ToDictionary(id => id, id => States[id], StringComparer.Ordinal));
        }
    }

    internal void RaiseLocalStatesChanged() => LocalStatesChanged?.Invoke();
}

internal sealed class FakeBooruPreviewPort : IBooruPreviewPort
{
    private int _requests;
    internal Func<BooruPostView, byte[]?>? OnPreview { get; set; }
    internal int Requests => Volatile.Read(ref _requests);

    public Task<byte[]?> LoadPreviewAsync(BooruPostView post, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requests);
        int seed = int.TryParse(post.PostId, out int parsed) ? parsed : 0;
        return Task.FromResult(OnPreview is null ? BooruUiFixtures.Png(seed) : OnPreview(post));
    }
}

/// <summary>
/// In-memory watcher scheduler stand-in with the transitions the domain defines: pause, resume and
/// removal change the state; a check is one counted call. <see cref="Calls"/> proves which commands
/// reached the scheduler and <see cref="Checks"/> how many provider checks it would have made.
/// </summary>
internal sealed class FakeBooruWatcherPort : IBooruWatcherPort
{
    private readonly object _gate = new();
    private readonly BooruManualClock _clock;
    private readonly List<BooruWatcherView> _watchers = new();
    private int _next = 1;

    internal FakeBooruWatcherPort(BooruManualClock clock) => _clock = clock;

    internal List<string> Calls { get; } = new();
    internal int Checks { get; private set; }
    internal Func<string, BooruWatcherCommandResult>? OnCheck { get; set; }
    internal Func<string, BooruWatcherCommandResult>? OnCreate { get; set; }
    internal Func<IReadOnlyList<BooruWatcherView>>? OnList { get; set; }
    internal Func<string, BooruWatcherCommandResult>? OnRemove { get; set; }

    public event Action? Changed;

    internal IReadOnlyList<BooruWatcherView> Snapshot { get { lock (_gate) return _watchers.ToArray(); } }

    internal void Seed(params BooruWatcherView[] watchers)
    {
        lock (_gate) _watchers.AddRange(watchers);
    }

    internal void RaiseChanged() => Changed?.Invoke();

    private void Record(string call) { lock (_gate) Calls.Add(call); }

    public Task<IReadOnlyList<BooruWatcherView>> ListAsync(CancellationToken cancellationToken)
    {
        Record("list");
        lock (_gate) return Task.FromResult(OnList?.Invoke() ?? (IReadOnlyList<BooruWatcherView>)_watchers.ToArray());
    }

    public Task<BooruWatcherCommandResult> CreateAsync(string canonicalQuery, CancellationToken cancellationToken)
    {
        Record("create:" + canonicalQuery);
        if (OnCreate?.Invoke(canonicalQuery) is { } custom) return Task.FromResult(custom);
        lock (_gate)
        {
            // New watchers start "from now": no check has happened, no backfill, no hits.
            _watchers.Add(new BooruWatcherView("w" + _next++, canonicalQuery, BooruWatcherStatus.Active, _clock.GetUtcNow(),
                null, _clock.GetUtcNow() + TimeSpan.FromMinutes(30), null, null, 0, 0, false));
        }
        return Task.FromResult(BooruWatcherCommandResult.Success());
    }

    public Task<BooruWatcherCommandResult> CheckNowAsync(string watcherId, CancellationToken cancellationToken)
    {
        Record("check:" + watcherId);
        lock (_gate)
        {
            Checks++;
            if (OnCheck?.Invoke(watcherId) is { } custom) return Task.FromResult(custom);
            Update(watcherId, watcher => watcher with
            {
                LastCheckedAt = _clock.GetUtcNow(),
                NextCheckAt = watcher.Status == BooruWatcherStatus.Paused ? null : _clock.GetUtcNow() + TimeSpan.FromMinutes(30),
                BaselineEstablished = true, HitCount = watcher.HitCount + 2, QueuedCount = watcher.QueuedCount + 2
            });
        }
        return Task.FromResult(BooruWatcherCommandResult.Success(2, 2));
    }

    public Task<BooruWatcherCommandResult> PauseAsync(string watcherId, CancellationToken cancellationToken)
    {
        Record("pause:" + watcherId);
        lock (_gate) Update(watcherId, watcher => watcher with { Status = BooruWatcherStatus.Paused, NextCheckAt = null });
        return Task.FromResult(BooruWatcherCommandResult.Success());
    }

    public Task<BooruWatcherCommandResult> ResumeAsync(string watcherId, CancellationToken cancellationToken)
    {
        Record("resume:" + watcherId);
        lock (_gate) Update(watcherId, watcher => watcher with
        {
            Status = BooruWatcherStatus.Active, NextCheckAt = _clock.GetUtcNow() + TimeSpan.FromMinutes(30)
        });
        return Task.FromResult(BooruWatcherCommandResult.Success());
    }

    public Task<BooruWatcherCommandResult> RemoveAsync(string watcherId, CancellationToken cancellationToken)
    {
        Record("remove:" + watcherId);
        if (OnRemove?.Invoke(watcherId) is { } custom) return Task.FromResult(custom);
        lock (_gate) _watchers.RemoveAll(watcher => watcher.WatcherId == watcherId);
        return Task.FromResult(BooruWatcherCommandResult.Success());
    }

    private void Update(string id, Func<BooruWatcherView, BooruWatcherView> update)
    {
        int index = _watchers.FindIndex(watcher => watcher.WatcherId == id);
        if (index >= 0) _watchers[index] = update(_watchers[index]);
    }
}

/// <summary>The four fakes and the clock, bundled the way production bundles its ports.</summary>
internal sealed class BooruUiHarness
{
    internal BooruUiHarness()
    {
        Clock = new BooruManualClock();
        Search = new FakeBooruSearchPort();
        Queue = new FakeBooruQueuePort();
        Previews = new FakeBooruPreviewPort();
        Watchers = new FakeBooruWatcherPort(Clock);
        Services = new BooruWorkbenchServices(Search, Queue, Watchers, Previews, Clock);
    }

    internal BooruManualClock Clock { get; }
    internal FakeBooruSearchPort Search { get; }
    internal FakeBooruQueuePort Queue { get; }
    internal FakeBooruPreviewPort Previews { get; }
    internal FakeBooruWatcherPort Watchers { get; }
    internal BooruWorkbenchServices Services { get; }

    /// <summary>Serves a deterministic result set: ids are page*100+index+1 ("101", "102", ... "201").</summary>
    internal FakeBooruSearchPort Serve(int pages = 2, int perPage = 3)
    {
        Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
            BooruUiFixtures.Page(query, page, page >= pages,
                Enumerable.Range(0, perPage).Select(index => BooruUiFixtures.Post((page * 100 + index + 1).ToString())).ToArray())));
        return Search;
    }

    /// <summary>Every provider-facing request the fakes received (searches, post reads, loads, watcher checks).</summary>
    internal int ProviderRequests => Search.Requests + Queue.Loads.Count + Watchers.Checks;
}

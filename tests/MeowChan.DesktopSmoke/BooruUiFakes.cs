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

/// <summary>The media shapes of the browse-to-download pipeline; see <see cref="BooruUiFixtures.Variant"/>.</summary>
internal enum BooruFixtureKind
{
    /// <summary>jpg 3000x2000 with preview, sample and original.</summary>
    Image,
    /// <summary>png 800x600 whose sample address equals the original: the plugin then offers no sample (Danbooru's fallback).</summary>
    SampleIsOriginal,
    /// <summary>webp 1920x1080 with a preview but no sample.</summary>
    NoSample,
    /// <summary>Animated gif without a sample.</summary>
    Gif,
    /// <summary>mp4 original and mp4 sample: neither is an image, only the preview is a still.</summary>
    Video,
    /// <summary>zip original and webm sample: neither is an image, only the preview is a still.</summary>
    Ugoira,
    /// <summary>Deleted by the provider: no original or sample address, the preview address survives.</summary>
    Deleted,
    /// <summary>Banned by the provider: only the claim of an original, no address of any kind.</summary>
    Banned,
    /// <summary>Neither deleted nor banned, yet no address of any kind (the file is gone).</summary>
    MissingFile
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
    internal static byte[] Png(int seed) => Render(seed, 96, 96, SKEncodedImageFormat.Png);

    /// <summary>The same picture as a JPEG (quality 80).</summary>
    internal static byte[] Jpeg(int seed, int width = 96, int height = 96) => Render(seed, width, height, SKEncodedImageFormat.Jpeg);

    /// <summary>The same picture as a WebP (quality 80).</summary>
    internal static byte[] WebP(int seed, int width = 96, int height = 96) => Render(seed, width, height, SKEncodedImageFormat.Webp);

    /// <summary>A deterministic sample-sized image (default 640x360), distinct from the preview of the same seed.</summary>
    internal static byte[] Sample(int seed, int width = 640, int height = 360) =>
        Render(seed + 1000, width, height, SKEncodedImageFormat.Png);

    private static byte[] Render(int seed, int width, int height, SKEncodedImageFormat format)
    {
        float scaleX = width / 96f, scaleY = height / 96f;
        using SKBitmap bitmap = new(width, height);
        using SKCanvas canvas = new(bitmap);
        SKColor[] palette = { new(0x4F, 0x8F, 0xD6), new(0xD6, 0x7F, 0x4F), new(0x5F, 0xB0, 0x7A), new(0xB0, 0x6F, 0xC9) };
        canvas.Clear(palette[Math.Abs(seed) % palette.Length]);
        using SKPaint paint = new() { Color = SKColors.White.WithAlpha(150), IsAntialias = true };
        canvas.DrawCircle(48 * scaleX, 40 * scaleY, 22 * Math.Min(scaleX, scaleY), paint);
        canvas.DrawRect(24 * scaleX, 66 * scaleY, 48 * scaleX, 14 * scaleY, paint);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(format, format == SKEncodedImageFormat.Png ? 90 : 80);
        return data.ToArray();
    }

    /// <summary>
    /// One post per media shape of the pipeline, built like the Danbooru plugin maps a post (see the fixture
    /// <c>search-variants.json</c>): a sample only when its address differs from the original, preview and sample
    /// carry an address and nothing else, a withheld original keeps its claim but no address.
    /// </summary>
    internal static BooruPostView Variant(string id, BooruFixtureKind kind, BooruRating? rating = null, int score = 12)
    {
        Uri CdnUri(string path) => new($"https://cdn.donmai.us/{path}");
        BooruMediaVariant PreviewOf() => new(BooruMediaRole.Preview, CdnUri($"180x180/{id}.jpg"));
        BooruMediaVariant SampleOf(string ext) => new(BooruMediaRole.Sample, CdnUri($"sample/sample-{id}.{ext}"));
        BooruMediaVariant OriginalOf(string ext, long size, int width, int height, bool withAddress = true) =>
            new(BooruMediaRole.Original, withAddress ? CdnUri($"original/{id}.{ext}") : null, "." + ext, size, width, height);
        BooruMediaVariant[] media = kind switch
        {
            BooruFixtureKind.Image => new[] { OriginalOf("jpg", 2_400_000, 3000, 2000), SampleOf("jpg"), PreviewOf() },
            BooruFixtureKind.SampleIsOriginal => new[] { OriginalOf("png", 150_000, 800, 600), PreviewOf() },
            BooruFixtureKind.NoSample => new[] { OriginalOf("webp", 300_000, 1920, 1080), PreviewOf() },
            BooruFixtureKind.Gif => new[] { OriginalOf("gif", 40_960, 320, 240), PreviewOf() },
            BooruFixtureKind.Video => new[] { OriginalOf("mp4", 5_242_880, 1280, 720), SampleOf("mp4"), PreviewOf() },
            BooruFixtureKind.Ugoira => new[] { OriginalOf("zip", 3_145_728, 1000, 1000), SampleOf("webm"), PreviewOf() },
            BooruFixtureKind.Deleted => new[] { OriginalOf("jpg", 345_678, 1600, 1200, withAddress: false), PreviewOf() },
            BooruFixtureKind.Banned => new[] { OriginalOf("png", 222_222, 1200, 1200, withAddress: false) },
            _ => new[] { OriginalOf("jpg", 99_999, 1024, 768, withAddress: false) }
        };
        BooruRating shown = rating ?? kind switch
        {
            BooruFixtureKind.Deleted => BooruRating.Questionable, BooruFixtureKind.Banned => BooruRating.Explicit,
            _ => BooruRating.General
        };
        BooruTag[] tags = { new("solo", BooruTagCategory.General), new("sky", BooruTagCategory.General), new("artist_one", BooruTagCategory.Artist) };
        return BooruPostView.From(new BooruPost(new BooruPostId(Source, id), BooruFieldPresence.All,
            new Uri($"https://danbooru.donmai.us/posts/{id}"), new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero),
            shown, shown.ToString()[..1].ToLowerInvariant(), score, 3, tags, media, $"https://example.invalid/art/{id}",
            kind == BooruFixtureKind.Deleted, kind == BooruFixtureKind.Banned));
    }

    /// <summary>
    /// The shapes of <c>search-page20-first.json</c>/<c>-second.json</c>: ids count down from <paramref name="topId"/>,
    /// and every fourth post (by id) is a no-sample, a sample-is-original, a rating-s image or a plain image.
    /// </summary>
    internal static BooruPostView[] FullPage(int topId, int count = 20) => Enumerable.Range(0, count).Select(offset =>
    {
        int id = topId - offset;
        return (id % 4) switch
        {
            0 => Variant(id.ToString(), BooruFixtureKind.Image),
            1 => Variant(id.ToString(), BooruFixtureKind.SampleIsOriginal),
            2 => Variant(id.ToString(), BooruFixtureKind.Image, BooruRating.Sensitive),
            _ => Variant(id.ToString(), BooruFixtureKind.NoSample)
        };
    }).ToArray();
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
    internal List<string> Retries { get; } = new();
    internal List<string> Cancellations { get; } = new();
    public Task<BooruLoadResult> RetryOriginalAsync(string postId, CancellationToken cancellationToken)
    {
        lock (_gate) Retries.Add(postId);
        return Task.FromResult(new BooruLoadResult(BooruLoadStatus.Queued));
    }
    public Task<bool> CancelOriginalAsync(string postId, CancellationToken cancellationToken)
    {
        lock (_gate) Cancellations.Add(postId);
        return Task.FromResult(true);
    }
    internal Dictionary<string, BooruLocalState> States { get; } = new(StringComparer.Ordinal);
    internal List<(string PostId, BooruLoadOrigin Origin)> Loads { get; } = new();
    /// <summary>The query each load carried as provenance (null for a one-shot), parallel to <see cref="Loads"/>.</summary>
    internal List<string?> LoadQueries { get; } = new();
    internal int StateReads { get; private set; }

    public event Action? LocalStatesChanged;

    public Task<BooruLoadResult> LoadOriginalAsync(BooruPostView post, BooruLoadOrigin origin, CancellationToken cancellationToken) =>
        LoadOriginalAsync(post, origin, null, cancellationToken);

    public async Task<BooruLoadResult> LoadOriginalAsync(BooruPostView post, BooruLoadOrigin origin, string? query, CancellationToken cancellationToken)
    {
        lock (_gate) { Loads.Add((post.PostId, origin)); LoadQueries.Add(query); }
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
    /// <summary>Routes the default answer through the scripted CDN (429/503 fence, missing files, held answers); <see cref="OnPreview"/> wins when set.</summary>
    internal FakeBooruCdn? Cdn { get; set; }
    /// <summary>Calls that reached the port (including ones the <see cref="Cdn"/> fence refused).</summary>
    internal int Requests => Volatile.Read(ref _requests);

    public Task<byte[]?> LoadPreviewAsync(BooruPostView post, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requests);
        int seed = int.TryParse(post.PostId, out int parsed) ? parsed : 0;
        if (OnPreview is not null) return Task.FromResult(OnPreview(post));
        return Cdn is null ? Task.FromResult<byte[]?>(BooruUiFixtures.Png(seed))
            : Cdn.LoadAsync(post.PostId, BooruImageRole.Preview, BooruUiFixtures.Png(seed), cancellationToken);
    }
}

internal enum BooruImageRole { Preview, Sample }

internal enum FakeCdnOutcomeKind { Image, Missing, RateLimited, ServiceUnavailable, Oversized, WrongContentType, Redirect }

/// <summary>
/// One scripted answer of <see cref="FakeBooruCdn"/>. Every kind except <see cref="FakeCdnOutcomeKind.Image"/> reaches the
/// port as "no bytes", as the host path reduces it; the kind and the retry delay stay visible in
/// <see cref="FakeBooruCdn.Answers"/> and drive the fence. The byte-level versions of the same cases are in
/// tests/Shared/BooruCdnFixtures.cs.
/// </summary>
internal sealed record FakeCdnOutcome(FakeCdnOutcomeKind Kind, byte[]? Bytes = null, TimeSpan? RetryAfter = null)
{
    internal static FakeCdnOutcome Image(byte[] bytes) => new(FakeCdnOutcomeKind.Image, bytes);
    internal static FakeCdnOutcome Missing() => new(FakeCdnOutcomeKind.Missing);
    internal static FakeCdnOutcome RateLimited(TimeSpan? retryAfter = null) => new(FakeCdnOutcomeKind.RateLimited, null, retryAfter);
    internal static FakeCdnOutcome ServiceUnavailable(TimeSpan? retryAfter = null) => new(FakeCdnOutcomeKind.ServiceUnavailable, null, retryAfter);
    internal static FakeCdnOutcome Oversized() => new(FakeCdnOutcomeKind.Oversized);
    internal static FakeCdnOutcome WrongContentType() => new(FakeCdnOutcomeKind.WrongContentType);
    internal static FakeCdnOutcome Redirect() => new(FakeCdnOutcomeKind.Redirect);
}

/// <summary>
/// The CDN behind the preview and sample fakes: scripted per post and role, with the media fence the host path will
/// have. After a 429 or 503 every request is refused without reaching the "network" until
/// max(60 s, Retry-After) of the manual clock has passed (<see cref="FenceUntil"/>). Nothing here makes a request.
/// </summary>
internal sealed class FakeBooruCdn
{
    internal static readonly TimeSpan FenceFloor = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly Dictionary<(string PostId, BooruImageRole Role), FakeCdnOutcome> _script = new();
    private readonly Dictionary<BooruImageRole, FakeCdnOutcome> _defaults = new();
    private readonly Dictionary<(string PostId, BooruImageRole Role), TaskCompletionSource> _holds = new();
    private int _inFlight;
    private int _maxInFlight;

    internal FakeBooruCdn(TimeProvider clock) => _clock = clock;

    /// <summary>Requests that reached the CDN, in order.</summary>
    internal List<(string PostId, BooruImageRole Role)> Requests { get; } = new();
    /// <summary>Requests the fence refused before they reached the CDN.</summary>
    internal List<(string PostId, BooruImageRole Role)> Fenced { get; } = new();
    /// <summary>What the CDN answered each request that reached it.</summary>
    internal List<(string PostId, BooruImageRole Role, FakeCdnOutcomeKind Kind)> Answers { get; } = new();
    internal DateTimeOffset? FenceUntil { get; private set; }
    internal int MaxInFlight { get { lock (_gate) return _maxInFlight; } }

    /// <summary>Scripts one post and role.</summary>
    internal FakeBooruCdn Script(string postId, BooruImageRole role, FakeCdnOutcome outcome)
    {
        lock (_gate) _script[(postId, role)] = outcome;
        return this;
    }

    /// <summary>Scripts every post of a role that has no script of its own.</summary>
    internal FakeBooruCdn ScriptAll(BooruImageRole role, FakeCdnOutcome outcome)
    {
        lock (_gate) _defaults[role] = outcome;
        return this;
    }

    /// <summary>The next request for this post and role waits until the returned gate is completed (or its caller cancels).</summary>
    internal TaskCompletionSource Hold(string postId, BooruImageRole role)
    {
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _holds[(postId, role)] = gate;
        return gate;
    }

    internal void ClearFence() { lock (_gate) FenceUntil = null; }

    /// <summary>The bytes, or null for every failure kind and for a request the fence refused.</summary>
    internal async Task<byte[]?> LoadAsync(string postId, BooruImageRole role, byte[] defaultBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FakeCdnOutcome outcome;
        TaskCompletionSource? hold;
        lock (_gate)
        {
            if (FenceUntil is { } until && _clock.GetUtcNow() < until)
            {
                Fenced.Add((postId, role));
                return null;
            }
            Requests.Add((postId, role));
            outcome = _script.TryGetValue((postId, role), out FakeCdnOutcome? scripted) ? scripted
                : _defaults.TryGetValue(role, out FakeCdnOutcome? fallback) ? fallback : FakeCdnOutcome.Image(defaultBytes);
            hold = _holds.Remove((postId, role), out TaskCompletionSource? gate) ? gate : null;
            _maxInFlight = Math.Max(_maxInFlight, ++_inFlight);
        }
        try
        {
            if (hold is not null) await hold.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                Answers.Add((postId, role, outcome.Kind));
                if (outcome.Kind is FakeCdnOutcomeKind.RateLimited or FakeCdnOutcomeKind.ServiceUnavailable)
                    FenceUntil = _clock.GetUtcNow() + (outcome.RetryAfter is { } wait && wait > FenceFloor ? wait : FenceFloor);
            }
            return outcome.Kind == FakeCdnOutcomeKind.Image ? outcome.Bytes : null;
        }
        finally
        {
            lock (_gate) _inFlight--;
        }
    }
}

/// <summary>
/// Sample bytes for the post viewer, behind <see cref="IBooruSamplePort"/> like the production adapter. A post without a
/// still-image sample address (none declared, which includes a sample that equals the original, or a video or ugoira
/// sample) gets null and no request, as the real port decides it (<see cref="BooruHostSamplePort.HasImageSample"/>). The
/// default bytes are <see cref="BooruUiFixtures.Sample"/>.
/// </summary>
internal sealed class FakeSamplePort : IBooruSamplePort
{
    private readonly object _gate = new();
    private int _requests;
    internal Func<BooruPostView, byte[]?>? OnSample { get; set; }
    /// <summary>Routes the default answer through the scripted CDN; <see cref="OnSample"/> wins when set.</summary>
    internal FakeBooruCdn? Cdn { get; set; }
    /// <summary>Calls for a post that has a sample address, in order.</summary>
    internal List<string> Loaded { get; } = new();
    /// <summary>Calls for a post without a sample address (answered null, nothing requested).</summary>
    internal List<string> WithoutSample { get; } = new();
    internal int Requests => Volatile.Read(ref _requests);

    public Task<byte[]?> LoadSampleAsync(BooruPostView post, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requests);
        bool hasSample = BooruHostSamplePort.HasImageSample(post);
        lock (_gate) (hasSample ? Loaded : WithoutSample).Add(post.PostId);
        if (!hasSample) return Task.FromResult<byte[]?>(null);
        if (OnSample is not null) return Task.FromResult(OnSample(post));
        int seed = int.TryParse(post.PostId, out int parsed) ? parsed : 0;
        return Cdn is null ? Task.FromResult<byte[]?>(BooruUiFixtures.Sample(seed))
            : Cdn.LoadAsync(post.PostId, BooruImageRole.Sample, BooruUiFixtures.Sample(seed), cancellationToken);
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
    internal BooruCatalogUsageView? CatalogUsage { get; set; }
    internal Func<string, BooruWatcherCommandResult>? OnCheck { get; set; }
    internal Func<string, BooruWatcherCommandResult>? OnCreate { get; set; }
    internal Func<IReadOnlyList<BooruWatcherView>>? OnList { get; set; }
    internal Func<string, BooruWatcherCommandResult>? OnRemove { get; set; }

    public event Action? Changed;
    public event Action<BooruWatcherCheckNotice>? CheckQueued;

    internal void RaiseCheckQueued(BooruWatcherCheckNotice notice) => CheckQueued?.Invoke(notice);

    internal IReadOnlyList<BooruWatcherView> Snapshot { get { lock (_gate) return _watchers.ToArray(); } }

    internal void Seed(params BooruWatcherView[] watchers)
    {
        lock (_gate) _watchers.AddRange(watchers);
    }

    internal void RaiseChanged() => Changed?.Invoke();

    private void Record(string call) { lock (_gate) Calls.Add(call); }

    public Task<BooruCatalogUsageView?> GetCatalogUsageAsync(CancellationToken cancellationToken) =>
        Task.FromResult(CatalogUsage);

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

/// <summary>Records what the window would open or copy; no browser or clipboard is touched.</summary>
internal sealed class FakeBooruLinks
{
    private readonly object _gate = new();
    internal FakeBooruLinks() => Actions = new BooruLinkActions(
        url => { lock (_gate) Opened.Add(url); return OnOpen?.Invoke(url) ?? Task.CompletedTask; },
        url => { lock (_gate) Copied.Add(url); return OnCopy?.Invoke(url) ?? Task.CompletedTask; });

    internal BooruLinkActions Actions { get; }
    internal List<string> Opened { get; } = new();
    internal List<string> Copied { get; } = new();
    internal Func<string, Task>? OnOpen { get; set; }
    internal Func<string, Task>? OnCopy { get; set; }
}

/// <summary>The four fakes and the clock, bundled the way production bundles its ports.</summary>
internal sealed class BooruUiHarness
{
    internal BooruUiHarness()
    {
        Clock = new BooruManualClock();
        Search = new FakeBooruSearchPort();
        Queue = new FakeBooruQueuePort();
        Cdn = new FakeBooruCdn(Clock);
        Previews = new FakeBooruPreviewPort { Cdn = Cdn };
        Samples = new FakeSamplePort { Cdn = Cdn };
        Watchers = new FakeBooruWatcherPort(Clock);
        Links = new FakeBooruLinks();
        Services = new BooruWorkbenchServices(Search, Queue, Watchers, Previews, Clock, Links.Actions, Samples);
    }

    internal FakeBooruLinks Links { get; }

    internal BooruManualClock Clock { get; }
    internal FakeBooruSearchPort Search { get; }
    internal FakeBooruQueuePort Queue { get; }
    internal FakeBooruPreviewPort Previews { get; }
    /// <summary>The sample port of <see cref="Services"/>.</summary>
    internal FakeSamplePort Samples { get; }
    /// <summary>The CDN behind <see cref="Previews"/> and <see cref="Samples"/>: scripted failures, the 429/503 fence.</summary>
    internal FakeBooruCdn Cdn { get; }
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

    /// <summary>
    /// Serves full pages of 20 in the shapes of <c>search-page20-first.json</c> and <c>-second.json</c>: page 1 holds ids
    /// 9220 down to 9201, page 2 ids 9200 down to 9181, and so on; the last of <paramref name="pages"/> is the end.
    /// </summary>
    internal FakeBooruSearchPort ServeFullPages(int pages = 2)
    {
        Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
            BooruUiFixtures.Page(query, page, page >= pages, BooruUiFixtures.FullPage(9220 - (page - 1) * 20))));
        return Search;
    }

    /// <summary>Every provider-facing request the fakes received (searches, post reads, loads, watcher checks).</summary>
    internal int ProviderRequests => Search.Requests + Queue.Loads.Count + Watchers.Checks;
}

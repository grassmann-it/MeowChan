using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using MeowChan.Booru;
using MeowChan.Persistence;

namespace MeowChan.Archive;

/// <summary>Why a local Danbooru post was admitted. Findings are retained independently of watcher lifetime.</summary>
internal enum BooruFindingKind
{
    Search,
    OneShot,
    Watcher
}

/// <summary>
/// The durable intent for an original download. A finding alone only explains why a post was observed;
/// this field records what was actually requested, so a cancelled or unavailable original is never
/// silently re-admitted after a restart, and an unavailable original is never treated as pending.
/// </summary>
internal enum BooruOriginalIntent
{
    /// <summary>No original was requested yet; a finding may still exist for observation only.</summary>
    None,
    /// <summary>
    /// The original was explicitly requested and has not completed. Written ahead of the shared queue's
    /// admission; a refusal replaces it with <see cref="NotAdmitted"/>, so a pending record is either an
    /// accepted job or one whose admission a process crash interrupted.
    /// </summary>
    Requested,
    /// <summary>The user explicitly cancelled the original; it is not recovered automatically.</summary>
    Cancelled,
    /// <summary>No acceptable original exists (missing, sample-only, forbidden host, missing claims).</summary>
    Unavailable,
    /// <summary>The shared download queue refused the job; a retry needs a new explicit request.</summary>
    NotAdmitted,
    /// <summary>The original is committed and linked to a local asset.</summary>
    Linked
}

/// <summary>A durable local observation connecting a provider post to a search or watcher.</summary>
internal sealed record BooruFinding(
    string FindingId,
    BooruFindingKind Kind,
    string? WatcherId,
    string? Query,
    DateTimeOffset FoundAtUtc);

/// <summary>Which provider fields were present in the persisted observation.</summary>
[Flags]
internal enum BooruPostClaimPresence
{
    None = 0,
    CreatedAt = 1,
    Rating = 2,
    Score = 4,
    FavoriteCount = 8,
    Tags = 16,
    Media = 32,
    SourceClaim = 64,
    IsDeleted = 128,
    IsBanned = 256,
    All = CreatedAt | Rating | Score | FavoriteCount | Tags | Media | SourceClaim | IsDeleted | IsBanned
}

/// <summary>Normalized provider rating retained separately from any user rating.</summary>
internal enum BooruPostClaimRating
{
    Unknown,
    General,
    Sensitive,
    Questionable,
    Explicit
}

/// <summary>Provider tag categories. These are not private MeowChan tags.</summary>
internal enum BooruPostClaimTagCategory
{
    Unknown,
    General,
    Artist,
    Copyright,
    Character,
    Meta
}

/// <summary>Provider media-variant roles. A preview or sample never satisfies an original.</summary>
internal enum BooruPostClaimMediaRole
{
    Original,
    Sample,
    Preview
}

/// <summary>Exact provider spelling of a source tag; it is never a private tag.</summary>
internal sealed record BooruPostClaimTag(string Name, BooruPostClaimTagCategory Category);

/// <summary>
/// A provider media claim. URLs are inert text at this storage boundary and must be validated by
/// the download/admission layer before a request is made.
/// </summary>
internal sealed record BooruPostClaimMedia(
    BooruPostClaimMediaRole Role,
    string? Url,
    string? Extension = null,
    long? ExpectedSize = null,
    int? Width = null,
    int? Height = null,
    string? ExpectedMd5 = null);

/// <summary>
/// Provider metadata in a core-library shape. Keeping this DTO here avoids making the independently
/// consumable libmeowchan package depend on the optional PluginSdk assembly. Host code maps validated
/// SDK results into this claim before persistence.
/// </summary>
internal sealed record BooruPostClaim(
    string SourceId,
    string PostId,
    BooruPostClaimPresence Fields,
    string? PostUrl = null,
    DateTimeOffset? CreatedAtUtc = null,
    BooruPostClaimRating Rating = BooruPostClaimRating.Unknown,
    string? RawRating = null,
    int? Score = null,
    int? FavoriteCount = null,
    IReadOnlyList<BooruPostClaimTag>? Tags = null,
    IReadOnlyList<BooruPostClaimMedia>? Media = null,
    string? SourceClaim = null,
    bool IsDeleted = false,
    bool IsBanned = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? InstanceId = null)
{
    internal BooruPostKey Identity => new(SourceId, InstanceId ?? BooruPostKey.DefaultInstance, PostId);
    public IReadOnlyList<BooruPostClaimTag> Tags { get; init; } = Tags ?? Array.Empty<BooruPostClaimTag>();
    public IReadOnlyList<BooruPostClaimMedia> Media { get; init; } = Media ?? Array.Empty<BooruPostClaimMedia>();
}

/// <summary>Durable provider claims plus the optional local asset link.</summary>
internal sealed record BooruPostRecord(
    string SourceId,
    string PostId,
    BooruPostClaim Post,
    DateTimeOffset FirstSeenAtUtc,
    DateTimeOffset LastSeenAtUtc,
    [property: JsonRequired] string? LocalAssetId = null,
    [property: JsonRequired] string? LocalPath = null,
    IReadOnlyList<BooruFinding>? Findings = null,
    BooruOriginalIntent OriginalIntent = BooruOriginalIntent.None,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? InstanceId = null)
{
    internal BooruPostKey Identity => new(SourceId, InstanceId ?? BooruPostKey.DefaultInstance, PostId);
    internal BooruMediaKey OriginalIdentity => BooruMediaKey.Original(Identity);
    // Every schema-1 writer has written the asset link and the findings. A missing key is therefore an incomplete
    // record, not an older one, and must not silently turn a linked original back into a post to download again.
    [JsonRequired]
    public IReadOnlyList<BooruFinding> Findings { get; init; } = Findings ?? Array.Empty<BooruFinding>();

    /// <summary>True when an explicitly requested original is still awaiting a committed local asset.</summary>
    internal bool HasPendingOriginal => LocalAssetId == null && OriginalIntent == BooruOriginalIntent.Requested;
}

/// <summary>
/// Restart-safe watcher projection. The scheduler owns the domain transition; this record is the
/// storage boundary and intentionally contains no provider-specific executable state.
/// <para>
/// Restart contract (B2 MUST-2): the flat fields <see cref="NextPage"/>, <see cref="CursorBeforePostId"/>,
/// <see cref="CheckStartHighWaterMark"/>, <see cref="ResumeState"/>, <see cref="PageSize"/>,
/// <see cref="PageBudget"/>, <see cref="PagesCheckedInRun"/> and <see cref="BudgetLimited"/> carry exactly
/// the traversal state of <c>DanbooruWatcher</c> / <c>DanbooruWatcherProgress</c> under the same names.
/// <see cref="NextPage"/> is the page ordinal of the traversal (1 = the newest page); every later page is
/// requested below <see cref="CursorBeforePostId"/>, the lowest post id the traversal has seen.
/// All of them are additive: a record written before they existed (<c>NextPage == 0</c>, all new fields
/// absent) stays readable. A record that claims an in-flight traversal (<c>NextPage &gt; 0</c>) must carry
/// every field needed to resume it; otherwise the store rejects it fail-closed instead of letting a
/// rebuilt watcher silently declare a partial catch-up complete.
/// </para>
/// <para>
/// <see cref="CheckStartHighWaterMarkCaptured"/> exists because a captured fence may legitimately be
/// <c>null</c> (a watcher whose empty baseline had no high-water mark when the check began); the flag
/// separates that from a lost fence.
/// </para>
/// <para>
/// Contract boundary: <c>DanbooruWatcherRecordMapper</c> is the production projection. This type and
/// <see cref="BooruCatalogStore"/> only guarantee that a conforming mapper can store the domain state
/// losslessly and that inconsistent records are refused; they do not construct or interpret domain objects.
/// </para>
/// <para>
/// Observation state: <see cref="HighWaterMark"/>, <see cref="NextPage"/>, <see cref="BaselineEstablished"/> and
/// <see cref="KnownPostIds"/> were written by every schema-1 writer and are therefore required keys. A record
/// missing one of them is incomplete and fails closed: a lost high-water mark would make an established watcher
/// (whose known ids are compacted below the mark) treat every older post as new after a restart.
/// </para>
/// </summary>
internal sealed record BooruWatcherRecord(
    string WatcherId,
    string SourceId,
    string Query,
    string State,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastCheckedAtUtc = null,
    DateTimeOffset? CooldownUntilUtc = null,
    string? ErrorCode = null,
    [property: JsonRequired] string? HighWaterMark = null,
    [property: JsonRequired] int NextPage = 0,
    [property: JsonRequired] bool BaselineEstablished = false,
    IReadOnlyList<string>? KnownPostIds = null,
    string? CursorBeforePostId = null,
    string? CheckStartHighWaterMark = null,
    bool CheckStartHighWaterMarkCaptured = false,
    string? ResumeState = null,
    int? PageSize = null,
    int? PageBudget = null,
    int PagesCheckedInRun = 0,
    bool BudgetLimited = false)
{
    [JsonRequired]
    public IReadOnlyList<string> KnownPostIds { get; init; } = KnownPostIds ?? Array.Empty<string>();
}

/// <summary>Versioned additive Booru catalog. The file contains no private-tag data.</summary>
internal sealed record BooruCatalogSnapshot(
    int SchemaVersion,
    IReadOnlyList<BooruPostRecord> Posts,
    IReadOnlyList<BooruWatcherRecord> Watchers)
{
    // Retain the original schema for Danbooru-only profiles until they actually contain a scoped identity.
    internal const int CurrentSchemaVersion = 1;
    internal const int ScopedIdentitySchemaVersion = 2;
    internal static BooruCatalogSnapshot Empty => new(CurrentSchemaVersion,
        Array.Empty<BooruPostRecord>(), Array.Empty<BooruWatcherRecord>());

    // Posts by identity, made the first time a lookup asks and kept for as long as this (immutable) snapshot lives; the first lookup
    // costs one pass, every later one is a dictionary hit instead of a scan over thousands of posts.
    private static readonly ConditionalWeakTable<BooruCatalogSnapshot, Dictionary<BooruPostKey, BooruPostRecord>> ByIdentity = new();

    /// <summary>One post of this snapshot by identity, or null (the first one of equal identity, as a scan would find).</summary>
    internal BooruPostRecord? TryGetPost(string sourceId, string postId) => TryGetPost(BooruPostKey.FromLegacy(sourceId, postId));

    internal BooruPostRecord? TryGetPost(BooruPostKey identity)
    {
        Dictionary<BooruPostKey, BooruPostRecord> index = ByIdentity.GetValue(this, snapshot =>
        {
            Dictionary<BooruPostKey, BooruPostRecord> made = new(snapshot.Posts.Count);
            foreach (BooruPostRecord record in snapshot.Posts) made.TryAdd(record.Identity, record);
            return made;
        });
        return index.GetValueOrDefault(identity);
    }
}

/// <summary>Current size of the catalog file against its configured write limit (for a visible warning).</summary>
internal sealed record BooruCatalogUsage(long DocumentBytes, int PostCount, int WatcherCount, long LimitBytes)
{
    /// <summary>Share of the limit at which the UI warns that the catalog is filling up.</summary>
    internal const double WarningFraction = 0.75;

    internal double Fraction => LimitBytes <= 0 ? 1 : (double)DocumentBytes / LimitBytes;
    internal bool NearLimit => Fraction >= WarningFraction;
}

/// <summary>Read/write counters of one store instance (measurement in tests; never used for decisions).</summary>
internal readonly record struct BooruCatalogStoreCounters(long FullReads, long Writes, long CachedReads);

/// <summary>
/// Profile-owned additive storage for Booru claims and watchers. The file is deliberately separate
/// from the archive/session stores so an old profile can be opened without rewriting existing data.
/// Every mutation is guarded by the exact profile lease and its store-writer admission; reads are
/// passive but still use the live lease. Invalid or newer data fails closed instead of becoming an
/// empty catalog.
/// <para>
/// Caching: the last validated, frozen (immutable) snapshot is kept together with the identity of the
/// file it came from (device, inode, size, modification and change time on Unix; volume, file id, size,
/// last-write and change time on Windows). A read reuses it only while the file on disk still has exactly
/// that identity; any other change underneath (another writer, a restored copy, damage) makes the next read
/// parse and validate the file again, failing closed as before. A same-size rewrite within the file
/// system's timestamp resolution can still go unseen: external writers while MeowChan runs are not
/// supported (the profile lease excludes a second instance, and the store itself only replaces the file
/// by rename). The store's own successful write replaces the cache with the snapshot it wrote; a failed
/// write drops it. Because the cached snapshot was validated, a mutation validates only the records it
/// changes (identity uniqueness is kept by the indexes); that is the same rule set the full validation
/// applies per record, so the written document satisfies exactly the old fail-closed checks.
/// </para>
/// <para>
/// Size: writes are bounded by <see cref="DocumentLimitBytes"/> (user setting, default
/// <see cref="DefaultDocumentLimitBytes"/>). Reads are bounded only by the technical ceiling
/// <see cref="MaximumDocumentBytes"/>, so lowering the setting below the current size never makes an
/// existing catalog unreadable; it only stops further growth.
/// </para>
/// </summary>
internal sealed class BooruCatalogStore
{
    internal const string FileName = "booru-catalog.json";
    /// <summary>
    /// Technical ceiling for reading and for the configurable write limit. The whole document is held in memory
    /// and every mutation rewrites and flushes it, so a larger catalog makes each watcher page and download
    /// noticeably slower; the ceiling keeps that bounded and keeps a damaged file from exhausting memory.
    /// </summary>
    internal const int MaximumDocumentBytes = 128 * 1024 * 1024;
    /// <summary>Default write limit (the fixed limit before it became a setting).</summary>
    internal const int DefaultDocumentLimitBytes = 32 * 1024 * 1024;
    /// <summary>Smallest accepted write limit.</summary>
    internal const int MinimumDocumentLimitBytes = 8 * 1024 * 1024;
    // The remaining bounds validate provider claims and records; they are not growth limits a user can reach by
    // normal use (a post has one claim, at most a few findings per search or watcher, and watcher id lists are
    // compacted by the mapper), so they stay fixed technical limits.
    internal const int MaximumClaimTextLength = 16 * 1024;
    internal const int MaximumTags = 10_000;
    internal const int MaximumMediaVariants = 16;
    internal const int MaximumFindings = 10_000;
    internal const int MaximumKnownPostIds = 100_000;
    /// <summary>
    /// Sanity bound for <see cref="BooruWatcherRecord.NextPage"/>, the page ordinal of a traversal. It is not a
    /// paging limit: every page after the first is requested below the stored cursor, never by number, so a
    /// catch-up may span any number of checks. One million pages of 20 posts exceeds any real backlog; the
    /// bound only keeps a corrupt record from overflowing the counter. Records written before cursor paging
    /// (next page at most 6) stay valid and continue below their stored cursor.
    /// </summary>
    internal const int MaximumWatcherPage = 1_000_000;

    private static readonly ConditionalWeakTable<BooruCatalogSnapshot, Dictionary<string, IReadOnlyDictionary<string, (int Hits, int Loaded)>>>
        WatcherCounts = new();

    private readonly object _sync = new();
    private readonly ProfileLease _lease;
    private readonly string _path;
    private readonly JsonSerializerOptions _json;
    private Cache? _cache;
    private long _documentLimitBytes = DefaultDocumentLimitBytes;
    private long _fullReads;
    private long _writes;
    private long _cachedReads;
    private bool _sweptTemporaries;

    internal BooruCatalogStore(string profileDirectory, ProfileLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        string directory = ProfileLease.NormalizeDirectory(profileDirectory);
        lease.EnsureMatches(directory);
        _lease = lease;
        _path = Path.Combine(lease.ProfileDirectory, FileName);
        _json = CreateJsonOptions();
    }

    internal string FilePath => _path;
    internal ProfileLease ProfileLease => _lease;

    /// <summary>Configured write limit in bytes, between <see cref="MinimumDocumentLimitBytes"/> and <see cref="MaximumDocumentBytes"/>.</summary>
    internal long DocumentLimitBytes
    {
        get => Interlocked.Read(ref _documentLimitBytes);
        set
        {
            if (value is < MinimumDocumentLimitBytes or > MaximumDocumentBytes)
                throw new ArgumentOutOfRangeException(nameof(value), "The Booru catalog limit is outside its supported range.");
            Interlocked.Exchange(ref _documentLimitBytes, value);
        }
    }

    internal BooruCatalogStoreCounters Counters => new(Interlocked.Read(ref _fullReads), Interlocked.Read(ref _writes),
        Interlocked.Read(ref _cachedReads));

    // These are instance-only test observers. They never grant authority and run while the
    // accepted store-writer token is pinned, immediately around the actual replacement.
    internal Action<ProfileAdmissionToken>? BeforeAdmittedMutationForTests { get; set; }
    internal Action<ProfileAdmissionToken>? AfterAdmittedMutationForTests { get; set; }

    /// <summary>Passively reads the current catalog under the live profile lease. The snapshot is immutable.</summary>
    internal BooruCatalogSnapshot Read() => _lease.RunExclusive(() =>
    {
        lock (_sync) return LoadLocked().Snapshot;
    });

    /// <summary>One stored post by identity (indexed lookup), or null.</summary>
    internal BooruPostRecord? TryGetPost(string sourceId, string postId) => TryGetPost(BooruPostKey.FromLegacy(sourceId, postId));

    internal BooruPostRecord? TryGetPost(BooruPostKey identity) => _lease.RunExclusive(() =>
    {
        lock (_sync)
        {
            Cache cache = LoadLocked();
            return cache.Posts.TryGetValue(identity, out int index) ? cache.Snapshot.Posts[index] : null;
        }
    });

    /// <summary>Size of the catalog file, its counts and the configured write limit.</summary>
    internal BooruCatalogUsage GetUsage() => _lease.RunExclusive(() =>
    {
        lock (_sync)
        {
            Cache cache = LoadLocked();
            return new BooruCatalogUsage(cache.DocumentBytes, cache.Snapshot.Posts.Count, cache.Snapshot.Watchers.Count,
                DocumentLimitBytes);
        }
    });

    /// <summary>
    /// Findings by watcher: per watcher id, the number of posts of <paramref name="sourceId"/> it found and how many of
    /// them are linked to a local asset. Computed once per catalog version (snapshot instance) and then reused.
    /// </summary>
    internal static IReadOnlyDictionary<string, (int Hits, int Loaded)> WatcherFindingCounts(BooruCatalogSnapshot snapshot,
        string sourceId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Dictionary<string, IReadOnlyDictionary<string, (int Hits, int Loaded)>> bySource = WatcherCounts.GetValue(snapshot,
            _ => new Dictionary<string, IReadOnlyDictionary<string, (int Hits, int Loaded)>>(StringComparer.Ordinal));
        lock (bySource)
        {
            if (bySource.TryGetValue(sourceId, out IReadOnlyDictionary<string, (int Hits, int Loaded)>? known)) return known;
            Dictionary<string, (int Hits, int Loaded)> counts = new(StringComparer.Ordinal);
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (BooruPostRecord post in snapshot.Posts)
            {
                if (post.SourceId != sourceId) continue;
                seen.Clear();
                foreach (BooruFinding finding in post.Findings)
                {
                    if (finding.Kind != BooruFindingKind.Watcher || finding.WatcherId is not { } watcherId || !seen.Add(watcherId))
                        continue;
                    (int found, int loaded) = counts.GetValueOrDefault(watcherId);
                    counts[watcherId] = (found + 1, loaded + (post.LocalAssetId is null ? 0 : 1));
                }
            }
            bySource[sourceId] = counts;
            return counts;
        }
    }

    internal BooruPostRecord UpsertPost(BooruPostClaim post, DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(post);
        ValidatePostClaim(post);
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(cancellationToken);
        return UpsertPostAdmitted(post, observedAtUtc, admission, cancellationToken);
    }

    /// <summary>Upserts a claim while the caller retains the exact accepted profile reservation.</summary>
    internal BooruPostRecord UpsertPostAdmitted(BooruPostClaim post, DateTimeOffset observedAtUtc,
        ProfileAdmissionToken admission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(post);
        ValidatePostClaim(post);
        return _lease.RunStoreWrite(admission, () =>
        {
            lock (_sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Cache cache = LoadLocked();
                int index = IndexOf(cache, post.Identity);
                BooruPostRecord next = Merged(cache, index, FreezeClaim(post), observedAtUtc.ToUniversalTime());
                return WritePostLocked(cache, index, next, admission, cancellationToken);
            }
        });
    }

    /// <summary>
    /// Adds a finding without duplicating an active queue identity. The same provider post may retain
    /// a search, one-shot and any number of watcher findings.
    /// </summary>
    internal BooruPostRecord AddFinding(string sourceId, string postId, BooruFindingKind kind,
        string? watcherId, string? query, DateTimeOffset foundAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateFindingArguments(BooruPostKey.FromLegacy(sourceId, postId), kind, watcherId, query);
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(cancellationToken);
        return AddFindingAdmitted(sourceId, postId, kind, watcherId, query, foundAtUtc, admission, cancellationToken);
    }

    internal BooruPostRecord AddFindingAdmitted(string sourceId, string postId, BooruFindingKind kind,
        string? watcherId, string? query, DateTimeOffset foundAtUtc, ProfileAdmissionToken admission,
        CancellationToken cancellationToken = default)
    {
        ValidateFindingArguments(BooruPostKey.FromLegacy(sourceId, postId), kind, watcherId, query);
        return _lease.RunStoreWrite(admission, () =>
        {
            lock (_sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Cache cache = LoadLocked();
                int index = IndexOf(cache, BooruPostKey.FromLegacy(sourceId, postId));
                if (index < 0) throw new InvalidOperationException("A provider post must be stored before its finding.");
                BooruPostRecord current = cache.Snapshot.Posts[index];
                BooruPostRecord next = WithFinding(current, kind, watcherId, query, foundAtUtc.ToUniversalTime());
                // An identical finding only widens the observation range; nothing to write when that is unchanged.
                if (next.Findings.Count == current.Findings.Count && next.FirstSeenAtUtc == current.FirstSeenAtUtc
                    && next.LastSeenAtUtc == current.LastSeenAtUtc)
                    return next;
                return WritePostLocked(cache, index, next, admission, cancellationToken);
            }
        });
    }

    /// <summary>
    /// One explicit observation in a single durable write: the claim is merged (as <see cref="UpsertPost"/>), the
    /// finding is added (as <see cref="AddFinding"/>) and, if <paramref name="decideIntent"/> returns a value, the
    /// original intent is set (as <see cref="SetOriginalIntent(BooruPostKey, BooruOriginalIntent, CancellationToken)"/>) on the merged record. The callback runs inside
    /// the store's write lock: it must be fast, must not take other locks and must not call the store. A crash
    /// leaves either the previous document or all three changes, never a claim whose request is missing.
    /// </summary>
    internal BooruPostRecord RecordObservation(BooruPostClaim post, DateTimeOffset observedAtUtc, BooruFindingKind kind,
        string? watcherId, string? query, Func<BooruPostRecord, BooruOriginalIntent?>? decideIntent = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(post);
        ValidatePostClaim(post);
        ValidateFindingArguments(post.Identity, kind, watcherId, query);
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(cancellationToken);
        return _lease.RunStoreWrite(admission, () =>
        {
            lock (_sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Cache cache = LoadLocked();
                int index = IndexOf(cache, post.Identity);
                DateTimeOffset observed = observedAtUtc.ToUniversalTime();
                BooruPostRecord next = WithFinding(Merged(cache, index, FreezeClaim(post), observed), kind, watcherId, query, observed);
                if (decideIntent?.Invoke(next) is BooruOriginalIntent intent)
                {
                    if (!Enum.IsDefined(intent)) throw new ArgumentOutOfRangeException(nameof(decideIntent));
                    RejectLinkedWithoutAsset(next, intent);
                    next = next with { OriginalIntent = intent };
                }
                return WritePostLocked(cache, index, next, admission, cancellationToken);
            }
        });
    }

    internal BooruPostRecord LinkLocalAsset(string sourceId, string postId, string assetId,
        string? localPath = null, CancellationToken cancellationToken = default)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(cancellationToken);
        return LinkLocalAssetAdmitted(BooruPostKey.FromLegacy(sourceId, postId), assetId, localPath, admission, cancellationToken);
    }

    internal BooruPostRecord LinkLocalAssetAdmitted(string sourceId, string postId, string assetId,
        string? localPath, ProfileAdmissionToken admission, CancellationToken cancellationToken = default) =>
        LinkLocalAssetAdmitted(BooruPostKey.FromLegacy(sourceId, postId), assetId, localPath, admission, cancellationToken);

    internal BooruPostRecord LinkLocalAssetAdmitted(BooruPostKey identity, string assetId,
        string? localPath, ProfileAdmissionToken admission, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(identity);
        ValidateRequiredText(assetId, nameof(assetId), 512);
        ValidateOptionalText(localPath, nameof(localPath), 4096);
        return _lease.RunStoreWrite(admission, () =>
        {
            lock (_sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Cache cache = LoadLocked();
                int index = IndexOf(cache, identity);
                if (index < 0) throw new InvalidOperationException("A provider post must be stored before its asset link.");
                // A committed link is a terminal intent; anything else would look pending after a restart.
                BooruPostRecord next = cache.Snapshot.Posts[index] with
                {
                    LocalAssetId = assetId,
                    LocalPath = localPath,
                    OriginalIntent = BooruOriginalIntent.Linked
                };
                return WritePostLocked(cache, index, next, admission, cancellationToken);
            }
        });
    }

    /// <summary>
    /// Records the durable intent for an original download. A cancelled or unavailable original must
    /// survive a restart so it is never silently re-admitted, and a queue refusal stays visible.
    /// </summary>
    internal BooruPostRecord SetOriginalIntent(string sourceId, string postId, BooruOriginalIntent intent,
        CancellationToken cancellationToken = default) =>
        SetOriginalIntent(BooruPostKey.FromLegacy(sourceId, postId), intent, cancellationToken);

    internal BooruPostRecord SetOriginalIntent(BooruPostKey identity, BooruOriginalIntent intent,
        CancellationToken cancellationToken = default)
    {
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(cancellationToken);
        return SetOriginalIntentAdmitted(identity, intent, admission, cancellationToken);
    }

    internal BooruPostRecord SetOriginalIntentAdmitted(string sourceId, string postId, BooruOriginalIntent intent,
        ProfileAdmissionToken admission, CancellationToken cancellationToken = default) =>
        SetOriginalIntentAdmitted(BooruPostKey.FromLegacy(sourceId, postId), intent, admission, cancellationToken);

    internal BooruPostRecord SetOriginalIntentAdmitted(BooruPostKey identity, BooruOriginalIntent intent,
        ProfileAdmissionToken admission, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(identity);
        if (!Enum.IsDefined(intent)) throw new ArgumentOutOfRangeException(nameof(intent));
        return _lease.RunStoreWrite(admission, () =>
        {
            lock (_sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Cache cache = LoadLocked();
                int index = IndexOf(cache, identity);
                if (index < 0) throw new InvalidOperationException("A provider post must be stored before its download intent.");
                BooruPostRecord current = cache.Snapshot.Posts[index];
                RejectLinkedWithoutAsset(current, intent);
                if (current.OriginalIntent == intent) return current;
                return WritePostLocked(cache, index, current with { OriginalIntent = intent }, admission, cancellationToken,
                    stateChangeOnly: true);
            }
        });
    }

    internal BooruWatcherRecord UpsertWatcher(BooruWatcherRecord watcher,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        ValidateWatcher(watcher);
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(cancellationToken);
        return UpsertWatcherAdmitted(watcher, admission, cancellationToken);
    }

    internal BooruWatcherRecord UpsertWatcherAdmitted(BooruWatcherRecord watcher,
        ProfileAdmissionToken admission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        ValidateWatcher(watcher);
        return _lease.RunStoreWrite(admission, () =>
        {
            lock (_sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Cache cache = LoadLocked();
                BooruWatcherRecord canonical = FreezeWatcher(watcher);
                int index = cache.Watchers.TryGetValue(canonical.WatcherId, out int found) ? found : -1;
                if (index >= 0) EnsureWatcherSuccessor(cache.Snapshot.Watchers[index], canonical);
                return WriteWatcherLocked(cache, index, canonical, admission, cancellationToken);
            }
        });
    }

    /// <summary>Marks a watcher removed; its post findings and local assets remain untouched.</summary>
    internal bool MarkWatcherRemoved(string watcherId, CancellationToken cancellationToken = default)
    {
        ValidateRequiredText(watcherId, nameof(watcherId), 128);
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(cancellationToken);
        return MarkWatcherRemovedAdmitted(watcherId, admission, cancellationToken);
    }

    internal bool MarkWatcherRemovedAdmitted(string watcherId, ProfileAdmissionToken admission,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredText(watcherId, nameof(watcherId), 128);
        return _lease.RunStoreWrite(admission, () =>
        {
            lock (_sync)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Cache cache = LoadLocked();
                if (!cache.Watchers.TryGetValue(watcherId, out int index)) return false;
                BooruWatcherRecord removed = FreezeWatcher(cache.Snapshot.Watchers[index] with
                {
                    State = "Removed",
                    CooldownUntilUtc = null,
                    ErrorCode = null,
                    // Mirrors DanbooruWatcher.Remove: observation stops and traversal progress is dropped;
                    // known ids, high-water mark and the watcher's configured limits remain.
                    NextPage = 0,
                    CursorBeforePostId = null,
                    CheckStartHighWaterMark = null,
                    CheckStartHighWaterMarkCaptured = false,
                    PagesCheckedInRun = 0,
                    BudgetLimited = false
                });
                WriteWatcherLocked(cache, index, removed, admission, cancellationToken);
                return true;
            }
        });
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            // Ambiguous schema, intent or claim values must not be silently accepted and rewritten.
            AllowDuplicateProperties = false,
            MaxDepth = 64,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            // Constructor parameters without a default (identities, timestamps, collections, media URLs) must be
            // present; a missing key is an incomplete record and fails closed instead of binding a default. Keys
            // added later (the watcher restart fields, the original intent) have defaults and stay optional.
            // Repeated keys are refused before deserialization (JsonDuplicateProperties in Parse).
            RespectRequiredConstructorParameters = true,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    /// <summary>
    /// A validated, frozen snapshot plus its lookup indexes. The indexes belong to exactly one live cache entry; a
    /// successful write extends them for the next entry, a failed write discards the whole entry.
    /// </summary>
    private sealed class Cache
    {
        internal Cache(BooruCatalogSnapshot snapshot, long documentBytes, RegularProfileFileIdentity? identity)
        {
            Snapshot = snapshot;
            DocumentBytes = documentBytes;
            Identity = identity;
            Posts = new Dictionary<BooruPostKey, int>(snapshot.Posts.Count);
            for (int index = 0; index < snapshot.Posts.Count; index++)
                Posts[snapshot.Posts[index].Identity] = index;
            Watchers = new Dictionary<string, int>(snapshot.Watchers.Count, StringComparer.Ordinal);
            for (int index = 0; index < snapshot.Watchers.Count; index++)
                Watchers[snapshot.Watchers[index].WatcherId] = index;
        }

        private Cache(BooruCatalogSnapshot snapshot, long documentBytes, RegularProfileFileIdentity? identity,
            Dictionary<BooruPostKey, int> posts, Dictionary<string, int> watchers)
        {
            Snapshot = snapshot;
            DocumentBytes = documentBytes;
            Identity = identity;
            Posts = posts;
            Watchers = watchers;
        }

        internal BooruCatalogSnapshot Snapshot { get; }
        internal long DocumentBytes { get; }
        /// <summary>Null: this entry is not reusable (missing file, or the file changed while it was read).</summary>
        internal RegularProfileFileIdentity? Identity { get; }
        internal Dictionary<BooruPostKey, int> Posts { get; }
        internal Dictionary<string, int> Watchers { get; }

        /// <summary>The entry after a successful write; this entry is no longer used afterwards.</summary>
        internal Cache Next(BooruCatalogSnapshot snapshot, long documentBytes, RegularProfileFileIdentity? identity,
            BooruPostKey? addedPostKey, string? addedWatcherId)
        {
            if (addedPostKey is { } key) Posts.Add(key, snapshot.Posts.Count - 1);
            if (addedWatcherId is not null) Watchers.Add(addedWatcherId, snapshot.Watchers.Count - 1);
            return new Cache(snapshot, documentBytes, identity, Posts, Watchers);
        }
    }

    /// <summary>Caller holds the lease monitor and <see cref="_sync"/>.</summary>
    private Cache LoadLocked()
    {
        if (!_sweptTemporaries)
        {
            _sweptTemporaries = true;
            SweepOrphanedTemporaries();
        }
        RegularProfileFileIdentity? before = RegularProfileFile.TryGetIdentity(_path);
        if (before is { } current && _cache is { Identity: { } cached } hit && cached == current)
        {
            Interlocked.Increment(ref _cachedReads);
            return hit;
        }

        _cache = null;
        byte[] bytes;
        try { bytes = ReadBounded(_path); }
        catch (FileNotFoundException) { return new Cache(BooruCatalogSnapshot.Empty, 0, null); }
        Interlocked.Increment(ref _fullReads);
        BooruCatalogSnapshot snapshot = Parse(bytes);
        // Only an entry whose file did not change while it was read may be reused.
        RegularProfileFileIdentity? after = RegularProfileFile.TryGetIdentity(_path);
        Cache loaded = new(snapshot, bytes.Length, before is { } stable && after == stable ? stable : null);
        if (loaded.Identity is not null) _cache = loaded;
        return loaded;
    }

    private static int IndexOf(Cache cache, BooruPostKey identity) =>
        cache.Posts.TryGetValue(identity, out int index) ? index : -1;

    /// <summary>The stored record with <paramref name="claim"/> merged in, or a new record (index -1).</summary>
    private static BooruPostRecord Merged(Cache cache, int index, BooruPostClaim claim, DateTimeOffset observed)
    {
        if (index < 0) return new(claim.SourceId, claim.PostId, claim, observed, observed, InstanceId: claim.InstanceId);
        BooruPostRecord current = cache.Snapshot.Posts[index];
        return current with
        {
            // Provider search and single-post responses may expose different
            // presence subsets. Do not let a less complete later observation
            // erase fields already retained from a richer claim.
            Post = MergeClaims(current.Post, claim),
            FirstSeenAtUtc = Earliest(current.FirstSeenAtUtc, observed),
            LastSeenAtUtc = Latest(current.LastSeenAtUtc, observed)
        };
    }

    private static BooruPostRecord WithFinding(BooruPostRecord current, BooruFindingKind kind, string? watcherId,
        string? query, DateTimeOffset observed)
    {
        string findingId = FindingIdentity(current.Identity, kind, watcherId, query);
        BooruPostRecord widened = current with
        {
            FirstSeenAtUtc = Earliest(current.FirstSeenAtUtc, observed),
            LastSeenAtUtc = Latest(current.LastSeenAtUtc, observed)
        };
        if (current.Findings.Any(item => item.FindingId == findingId)) return widened;
        if (current.Findings.Count >= MaximumFindings)
            throw new InvalidDataException("The Booru post has too many findings.");
        return widened with { Findings = FreezeFindings(current.Findings.Append(new(findingId, kind, watcherId, query, observed))) };
    }

    private static void RejectLinkedWithoutAsset(BooruPostRecord record, BooruOriginalIntent intent)
    {
        // Linked is only ever reached through LinkLocalAssetAdmitted, which records the committed asset in the
        // same write. Claiming it here without an asset is a caller error, not corrupt storage.
        if (intent == BooruOriginalIntent.Linked && record.LocalAssetId == null)
            throw new ArgumentException("A linked original needs a committed local asset; use LinkLocalAsset.", nameof(intent));
    }

    /// <summary>Replaces (or appends, index -1) one post and writes the document. Caller holds the write lock.</summary>
    private BooruPostRecord WritePostLocked(Cache cache, int index, BooruPostRecord next, ProfileAdmissionToken admission,
        CancellationToken cancellationToken, bool stateChangeOnly = false)
    {
        BooruPostRecord frozen = FreezePostRecord(next);
        // The cached snapshot is validated; the changed record is validated by the same per-record rules, and the
        // index keeps the identity unique (a new identity is appended, a known one replaced in place).
        ValidatePostRecord(frozen);
        BooruPostRecord[] posts = CopyWith(cache.Snapshot.Posts, index, frozen);
        int schema = frozen.Identity.IsLegacyDanbooru ? cache.Snapshot.SchemaVersion : BooruCatalogSnapshot.ScopedIdentitySchemaVersion;
        WriteLocked(cache, cache.Snapshot with { SchemaVersion = schema, Posts = Array.AsReadOnly(posts) },
            index < 0 ? frozen.Identity : null, null, admission, cancellationToken, stateChangeOnly);
        return frozen;
    }

    /// <summary>Replaces (or appends, index -1) one watcher and writes the document. Caller holds the write lock.</summary>
    private BooruWatcherRecord WriteWatcherLocked(Cache cache, int index, BooruWatcherRecord canonical,
        ProfileAdmissionToken admission, CancellationToken cancellationToken)
    {
        ValidateStoredWatcher(canonical);
        BooruWatcherRecord[] watchers = CopyWith(cache.Snapshot.Watchers, index, canonical);
        WriteLocked(cache, cache.Snapshot with { Watchers = Array.AsReadOnly(watchers) },
            null, index < 0 ? canonical.WatcherId : null, admission, cancellationToken,
            // Removing a watcher only swaps its state and drops traversal progress.
            stateChangeOnly: index >= 0 && canonical.State == "Removed");
        return canonical;
    }

    internal const string FullMessage = "The Booru catalog has reached its configured size limit.";

    /// <summary>
    /// A write that would grow the catalog past its configured size limit; nothing was written. The type stays
    /// <see cref="InvalidDataException"/> (existing callers report it as a persistence failure); the sizes are in Data.
    /// </summary>
    private static InvalidDataException CatalogFull(long documentBytes, long limitBytes)
    {
        InvalidDataException full = new(FullMessage);
        full.Data["documentBytes"] = documentBytes;
        full.Data["limitBytes"] = limitBytes;
        return full;
    }

    private static T[] CopyWith<T>(IReadOnlyList<T> current, int index, T value)
    {
        T[] result = new T[index < 0 ? current.Count + 1 : current.Count];
        for (int item = 0; item < current.Count; item++) result[item] = current[item];
        result[index < 0 ? current.Count : index] = value;
        return result;
    }

    /// <summary>
    /// A write that only changes the state of an existing record (cancel a download, remove a watcher) may exceed the
    /// previous document size by the few bytes a longer state name takes, even above the limit; it cannot add content.
    /// </summary>
    internal const int StateChangeSlackBytes = 4096;

    // Neither a lowered-limit exception nor state-change slack may produce a file that ReadBounded cannot reopen.
    internal static long WriteCeiling(long previousDocumentBytes, long configuredLimitBytes, bool stateChangeOnly) =>
        Math.Min(MaximumDocumentBytes,
            Math.Max(configuredLimitBytes, previousDocumentBytes) + (stateChangeOnly ? StateChangeSlackBytes : 0));

    private void WriteLocked(Cache previous, BooruCatalogSnapshot snapshot, BooruPostKey? addedPostKey, string? addedWatcherId,
        ProfileAdmissionToken admission, CancellationToken cancellationToken, bool stateChangeOnly = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, _json);
        long limit = DocumentLimitBytes;
        // The limit stops growth only: a write that does not enlarge the document (pause, resume, a shrinking
        // rewrite) stays possible even when the document is already above a limit that was lowered afterwards, and a
        // pure state change (cancel, remove) gets a small slack because a longer state name is a few bytes larger.
        long allowed = WriteCeiling(previous.DocumentBytes, limit, stateChangeOnly);
        if (bytes.Length > allowed) throw CatalogFull(bytes.Length, limit);

        string directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}.tmp");
        // Whatever happens from here, the cached entry may no longer describe the file.
        _cache = null;
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       81920, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            BeforeAdmittedMutationForTests?.Invoke(admission);
            File.Move(temporary, _path, overwrite: true);
            Interlocked.Increment(ref _writes);
            AfterAdmittedMutationForTests?.Invoke(admission);
            _cache = previous.Next(snapshot, bytes.Length, RegularProfileFile.TryGetIdentity(_path), addedPostKey, addedWatcherId);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Deletes temporary catalog files that a crash between creating the temporary and moving it over the catalog left
    /// behind (up to one document each). Runs once per store on its first load, under the lease monitor, so no write of
    /// this profile is in flight. Only the exact <c>.booru-catalog.json.&lt;32 hex&gt;.tmp</c> regular files are removed.
    /// </summary>
    private void SweepOrphanedTemporaries()
    {
        string directory = Path.GetDirectoryName(_path)!;
        string prefix = $".{FileName}.";
        try
        {
            foreach (string candidate in Directory.EnumerateFiles(directory, prefix + "*.tmp"))
            {
                string name = Path.GetFileName(candidate);
                if (!IsTemporaryName(name, prefix)) continue;
                try
                {
                    if ((File.GetAttributes(candidate) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0)
                        File.Delete(candidate);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool IsTemporaryName(string name, string prefix)
    {
        const string suffix = ".tmp";
        if (name.Length != prefix.Length + 32 + suffix.Length
            || !name.StartsWith(prefix, StringComparison.Ordinal)
            || !name.EndsWith(suffix, StringComparison.Ordinal))
            return false;
        foreach (char value in name.AsSpan(prefix.Length, 32))
            if (!char.IsAsciiHexDigitLower(value) && !char.IsAsciiDigit(value)) return false;
        return true;
    }

    private BooruCatalogSnapshot Parse(byte[] bytes)
    {
        if (bytes.Length > MaximumDocumentBytes)
            throw new InvalidDataException("The Booru catalog exceeds its bounded size.");
        try
        {
            // Fails closed like any other damage: the serializer keeps the last of two equal names, so a second "posts"
            // would replace the whole catalog with an empty one, and this store has no backup to fall back to.
            if (JsonDuplicateProperties.Any(bytes, _json.MaxDepth))
                throw new InvalidDataException("The Booru catalog repeats a property.");
            // The DTOs retain caller-friendly null-to-empty normalization for programmatic construction. That
            // initializer runs before the serializer's object is validated, however, so an explicit JSON null in
            // one of the schema collections would otherwise look exactly like a valid empty array. Inspect the raw
            // document first; missing keys keep their existing required/additive-key semantics below.
            using (JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = _json.MaxDepth }))
                RejectExplicitNullCollections(document.RootElement);
            BooruCatalogSnapshot value = JsonSerializer.Deserialize<BooruCatalogSnapshot>(bytes, _json)
                ?? throw new InvalidDataException("The Booru catalog is empty.");
            ValidateSnapshot(value);
            return FreezeSnapshot(value);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException
            or OverflowException or FormatException)
        {
            throw new InvalidDataException("The Booru catalog is malformed.", exception);
        }
    }

    private static void RejectExplicitNullCollections(JsonElement root)
    {
        RejectExplicitNullCollection(root, "posts");
        RejectExplicitNullCollection(root, "watchers");
        if (root.ValueKind != JsonValueKind.Object) return;

        if (root.TryGetProperty("posts", out JsonElement posts) && posts.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement post in posts.EnumerateArray())
            {
                if (post.ValueKind != JsonValueKind.Object) continue;
                RejectExplicitNullCollection(post, "findings");
                if (post.TryGetProperty("post", out JsonElement claim) && claim.ValueKind == JsonValueKind.Object)
                {
                    RejectExplicitNullCollection(claim, "tags");
                    RejectExplicitNullCollection(claim, "media");
                }
            }
        }

        if (root.TryGetProperty("watchers", out JsonElement watchers) && watchers.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement watcher in watchers.EnumerateArray())
            {
                if (watcher.ValueKind == JsonValueKind.Object)
                    RejectExplicitNullCollection(watcher, "knownPostIds");
            }
        }
    }

    private static void RejectExplicitNullCollection(JsonElement owner, string propertyName)
    {
        if (owner.ValueKind == JsonValueKind.Object
            && owner.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.Null)
            throw new InvalidDataException($"The Booru catalog contains an explicit null collection: {propertyName}.");
    }

    private static void ValidateSnapshot(BooruCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SchemaVersion is not (BooruCatalogSnapshot.CurrentSchemaVersion or BooruCatalogSnapshot.ScopedIdentitySchemaVersion))
            throw new InvalidDataException("The Booru catalog schema is not supported.");
        if (snapshot.Posts is null || snapshot.Watchers is null)
            throw new InvalidDataException("The Booru catalog collections are missing.");

        HashSet<BooruPostKey> postKeys = new();
        foreach (BooruPostRecord? post in snapshot.Posts)
        {
            if (post is null) throw new InvalidDataException("The Booru catalog contains a null post.");
            ValidatePostRecord(post);
            if (!postKeys.Add(post.Identity) || snapshot.SchemaVersion == BooruCatalogSnapshot.CurrentSchemaVersion && !post.Identity.IsLegacyDanbooru)
                throw new InvalidDataException("The Booru catalog contains an invalid or duplicate post identity.");
        }

        HashSet<string> watcherKeys = new(StringComparer.Ordinal);
        foreach (BooruWatcherRecord? watcher in snapshot.Watchers)
        {
            if (watcher is null) throw new InvalidDataException("The Booru catalog contains a null watcher.");
            ValidateStoredWatcher(watcher);
            if (!watcherKeys.Add(watcher.WatcherId))
                throw new InvalidDataException("The Booru catalog contains duplicate watchers.");
        }
    }

    /// <summary>The per-record watcher rules of <see cref="ValidateSnapshot"/> (identity uniqueness is checked by the caller).</summary>
    private static void ValidateStoredWatcher(BooruWatcherRecord watcher)
    {
        ValidateWatcher(watcher);
        string[] canonical = CanonicalIds(watcher.KnownPostIds);
        if (!watcher.KnownPostIds.SequenceEqual(canonical, StringComparer.Ordinal))
            throw new InvalidDataException("The Booru catalog contains non-canonical watcher post ids.");
    }

    private static void ValidatePostRecord(BooruPostRecord post)
    {
        if (post.Post is null) throw new InvalidDataException("The Booru catalog contains a post without a claim.");
        ValidateIdentity(post.Identity);
        ValidatePostClaim(post.Post);
        if (post.Identity != post.Post.Identity)
            throw new InvalidDataException("The Booru catalog contains a mismatched post identity.");
        if (post.FirstSeenAtUtc > post.LastSeenAtUtc)
            throw new InvalidDataException("The Booru catalog contains an invalid observation range.");
        ValidateOptionalText(post.LocalAssetId, nameof(post.LocalAssetId), 512);
        ValidateOptionalText(post.LocalPath, nameof(post.LocalPath), 4096);
        if (post.LocalAssetId is null && post.LocalPath is not null)
            throw new InvalidDataException("The Booru catalog contains a local path without an asset identity.");
        if (!Enum.IsDefined(post.OriginalIntent))
            throw new InvalidDataException("The Booru catalog contains an invalid original download intent.");
        if (post.OriginalIntent == BooruOriginalIntent.Linked && post.LocalAssetId is null)
            throw new InvalidDataException("The Booru catalog claims a linked original without an asset identity.");
        if (post.OriginalIntent is not (BooruOriginalIntent.Linked or BooruOriginalIntent.None)
            && post.LocalAssetId is not null)
            throw new InvalidDataException("The Booru catalog keeps a completed link with an unfinished intent.");
        if (post.Findings is null || post.Findings.Count > MaximumFindings)
            throw new InvalidDataException("The Booru catalog contains invalid post findings.");
        HashSet<string> findings = new(StringComparer.Ordinal);
        foreach (BooruFinding? finding in post.Findings)
        {
            if (finding is null) throw new InvalidDataException("The Booru catalog contains a null finding.");
            ValidateFindingArguments(post.Identity, finding.Kind, finding.WatcherId, finding.Query);
            string expected = FindingIdentity(post.Identity, finding.Kind, finding.WatcherId, finding.Query);
            if (!findings.Add(finding.FindingId) || finding.FindingId != expected)
                throw new InvalidDataException("The Booru catalog contains duplicate or invalid findings.");
        }
    }

    private static void ValidatePostClaim(BooruPostClaim post)
    {
        ArgumentNullException.ThrowIfNull(post);
        ValidateIdentity(post.Identity);
        if ((post.Fields & ~BooruPostClaimPresence.All) != 0)
            throw new ArgumentException("The Booru post claim contains unknown fields.", nameof(post));
        if (post.Identity.IsLegacyDanbooru)
        {
            ValidateOptionalText(post.PostUrl, nameof(post.PostUrl), MaximumClaimTextLength);
            if (post.PostUrl is not null && (!DanbooruPostReference.TryParse(post.PostUrl, out DanbooruPostReference? reference, out _)
                || reference is null || reference.PostId != post.PostId))
                throw new ArgumentException("The Booru post URL does not match its provider identity.", nameof(post));
        }
        else ValidateMediaUrl(post.PostUrl);
        ValidateProviderText(post.RawRating, nameof(post.RawRating), 128);
        ValidateProviderText(post.SourceClaim, nameof(post.SourceClaim), MaximumClaimTextLength);
        if (!Enum.IsDefined(post.Rating)) throw new ArgumentException("The Booru post rating is invalid.", nameof(post));
        if (post.Score is < 0 || post.FavoriteCount is < 0)
            throw new ArgumentException("The Booru post counters are invalid.", nameof(post));
        if (post.Tags is null || post.Tags.Count > MaximumTags || post.Media is null || post.Media.Count > MaximumMediaVariants)
            throw new ArgumentException("The Booru post claims exceed their bounds.", nameof(post));

        BooruPostClaimPresence fields = post.Fields;
        if (!Has(fields, BooruPostClaimPresence.CreatedAt) && post.CreatedAtUtc is not null
            || !Has(fields, BooruPostClaimPresence.Rating) && (post.Rating != BooruPostClaimRating.Unknown || post.RawRating is not null)
            || !Has(fields, BooruPostClaimPresence.Score) && post.Score is not null
            || !Has(fields, BooruPostClaimPresence.FavoriteCount) && post.FavoriteCount is not null
            || !Has(fields, BooruPostClaimPresence.Tags) && post.Tags.Count != 0
            || !Has(fields, BooruPostClaimPresence.Media) && post.Media.Count != 0
            || !Has(fields, BooruPostClaimPresence.SourceClaim) && post.SourceClaim is not null
            || !Has(fields, BooruPostClaimPresence.IsDeleted) && post.IsDeleted
            || !Has(fields, BooruPostClaimPresence.IsBanned) && post.IsBanned)
            throw new ArgumentException("The Booru post claim has values without corresponding presence fields.", nameof(post));

        HashSet<string> tagNames = new(StringComparer.Ordinal);
        foreach (BooruPostClaimTag? tag in post.Tags)
        {
            if (tag is null || !Enum.IsDefined(tag.Category))
                throw new ArgumentException("The Booru post contains an invalid tag.", nameof(post));
            ValidateRequiredTag(tag.Name, nameof(tag.Name));
            if (!tagNames.Add(tag.Name))
                throw new ArgumentException("The Booru post contains duplicate tags.", nameof(post));
        }

        HashSet<BooruPostClaimMediaRole> mediaRoles = new();
        foreach (BooruPostClaimMedia? media in post.Media)
        {
            if (media is null || !Enum.IsDefined(media.Role) || !mediaRoles.Add(media.Role))
                throw new ArgumentException("The Booru post contains invalid or duplicate media variants.", nameof(post));
            ValidateMediaUrl(media.Url);
            ValidateOptionalText(media.Extension, nameof(media.Extension), 32);
            if (media.Extension is not null && (!media.Extension.StartsWith(".", StringComparison.Ordinal)
                || media.Extension.Length == 1 || !media.Extension[1..].All(char.IsAsciiLetterOrDigit)))
                throw new ArgumentException("The Booru media extension is invalid.", nameof(post));
            ValidateOptionalText(media.ExpectedMd5, nameof(media.ExpectedMd5), 128);
            if (media.ExpectedMd5 is not null)
            {
                try
                {
                    if (Convert.FromBase64String(media.ExpectedMd5).Length != 16)
                        throw new FormatException();
                }
                catch (FormatException exception)
                {
                    throw new ArgumentException("The Booru media checksum is invalid.", nameof(post), exception);
                }
            }
            if (media.ExpectedSize is < 0 || media.Width is < 0 || media.Height is < 0)
                throw new ArgumentException("The Booru media claim dimensions are invalid.", nameof(post));
        }
    }

    private static void ValidateWatcher(BooruWatcherRecord watcher)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        ValidateRequiredText(watcher.WatcherId, nameof(watcher.WatcherId), 128);
        if (watcher.WatcherId.Any(char.IsControl) || watcher.SourceId != DanbooruPostReference.ProviderId)
            throw new ArgumentException("The Booru watcher record is invalid.", nameof(watcher));
        ValidateRequiredText(watcher.Query, nameof(watcher.Query), 1024);
        if (!DanbooruTagQuery.TryParse(watcher.Query, out DanbooruTagQuery? query, out _)
            || query is null || query.ProviderQuery != watcher.Query)
            throw new ArgumentException("The Booru watcher query is not canonical.", nameof(watcher));
        // Enum.TryParse also accepts numeric strings, whitespace and comma lists for non-flags enums.
        // Durable names must be exact: successor checks rely on the canonical Removed spelling.
        if (!Enum.TryParse(watcher.State, ignoreCase: false, out DanbooruWatcherState state)
            || !Enum.IsDefined(state) || watcher.State != state.ToString())
            throw new ArgumentException("The Booru watcher state is invalid.", nameof(watcher));
        ValidateOptionalText(watcher.ErrorCode, nameof(watcher.ErrorCode), 128);
        ValidateOptionalText(watcher.HighWaterMark, nameof(watcher.HighWaterMark), 18);
        if (watcher.HighWaterMark is not null && !IsValidPostId(watcher.HighWaterMark))
            throw new ArgumentException("The Booru watcher high-water mark is invalid.", nameof(watcher));
        if (watcher.NextPage is < 0 or > MaximumWatcherPage || watcher.KnownPostIds is null
            || watcher.KnownPostIds.Count > MaximumKnownPostIds)
            throw new ArgumentException("The Booru watcher record is invalid.", nameof(watcher));
        ValidateWatcherObservationState(watcher, CanonicalIds(watcher.KnownPostIds));
        ValidateWatcherRestartState(watcher, state);
        if (state == DanbooruWatcherState.Cooldown && watcher.CooldownUntilUtc is null
            || state != DanbooruWatcherState.Cooldown && watcher.CooldownUntilUtc is not null)
            throw new ArgumentException("The Booru watcher cooldown state is invalid.", nameof(watcher));
        if (state is DanbooruWatcherState.Error or DanbooruWatcherState.Cooldown
            && string.IsNullOrWhiteSpace(watcher.ErrorCode)
            || state is not (DanbooruWatcherState.Error or DanbooruWatcherState.Cooldown)
            && watcher.ErrorCode is not null)
            throw new ArgumentException("The Booru watcher error state is invalid.", nameof(watcher));
    }

    /// <summary>
    /// The high-water mark is the highest post id any applied page contained, and only an applied page establishes
    /// the baseline (<c>DanbooruWatcher.ApplyPage</c>). Known ids therefore never exceed the mark and never exist
    /// without it, and a mark implies an established baseline. A record breaking this has lost or corrupted its
    /// fence and must not be resumed.
    /// </summary>
    private static void ValidateWatcherObservationState(BooruWatcherRecord watcher, string[] canonicalKnownIds)
    {
        if (watcher.HighWaterMark is not null && !watcher.BaselineEstablished)
            throw new ArgumentException("The Booru watcher has a high-water mark without a baseline.", nameof(watcher));
        if (canonicalKnownIds.Length == 0) return;
        if (!watcher.BaselineEstablished || watcher.HighWaterMark is null
            || DanbooruPostReference.ComparePostIds(canonicalKnownIds[^1], watcher.HighWaterMark) > 0)
            throw new ArgumentException("The Booru watcher known ids exceed its high-water mark.", nameof(watcher));
    }

    /// <summary>
    /// Guard for replacing a stored watcher. Watcher ids are unique per creation, so identity is immutable;
    /// <c>Removed</c> is terminal in the domain; and the observation fence only moves forward. A regressing write
    /// (stale in-memory copy, mapper defect, reused id) is refused, so a durable high-water mark is never lowered or
    /// erased by a later write. Caller holds the write lock; nothing is written when this throws.
    /// </summary>
    private static void EnsureWatcherSuccessor(BooruWatcherRecord current, BooruWatcherRecord next)
    {
        if (current.SourceId != next.SourceId || current.Query != next.Query || current.CreatedAtUtc != next.CreatedAtUtc)
            throw new InvalidOperationException("The Booru watcher identity cannot change.");
        if (current.State == nameof(DanbooruWatcherState.Removed) && next.State != nameof(DanbooruWatcherState.Removed))
            throw new InvalidOperationException("A removed Booru watcher cannot be revived.");
        if (current.BaselineEstablished && !next.BaselineEstablished)
            throw new InvalidOperationException("The Booru watcher baseline cannot be dropped.");
        if (current.HighWaterMark is not null && (next.HighWaterMark is null
            || DanbooruPostReference.ComparePostIds(next.HighWaterMark, current.HighWaterMark) < 0))
            throw new InvalidOperationException("The Booru watcher high-water mark cannot move backwards.");
    }

    /// <summary>
    /// Validates the restart-critical traversal fields against the invariants of the watcher domain
    /// (bounded pages/budget, descending cursor, fixed check-start fence, resume target). The rules are
    /// stated on the storage fields only; no domain object is built or consulted.
    /// </summary>
    private static void ValidateWatcherRestartState(BooruWatcherRecord watcher, DanbooruWatcherState state)
    {
        static ArgumentException Invalid(string reason, string parameterName) =>
            new($"The Booru watcher restart state is invalid: {reason}.", parameterName);
        const string name = "watcher";

        DanbooruWatcherState? resume = null;
        if (watcher.ResumeState is not null)
        {
            if (!Enum.TryParse(watcher.ResumeState, ignoreCase: false, out DanbooruWatcherState parsed)
                || parsed is not (DanbooruWatcherState.Active or DanbooruWatcherState.Paused)
                || watcher.ResumeState != parsed.ToString())
                throw Invalid("resume state", name);
            resume = parsed;
        }
        if (watcher.PageSize is < 1 or > DanbooruWatcher.MaximumPageSize)
            throw Invalid("page size", name);
        if (watcher.PageBudget is < 1 or > DanbooruWatcher.MaximumPageBudget)
            throw Invalid("page budget", name);
        if (watcher.PagesCheckedInRun < 0)
            throw Invalid("pages checked", name);
        ValidateOptionalText(watcher.CursorBeforePostId, nameof(watcher.CursorBeforePostId), 18);
        ValidateOptionalText(watcher.CheckStartHighWaterMark, nameof(watcher.CheckStartHighWaterMark), 18);
        if (watcher.CursorBeforePostId is not null && !IsValidPostId(watcher.CursorBeforePostId))
            throw Invalid("cursor", name);
        if (watcher.CheckStartHighWaterMark is not null && !IsValidPostId(watcher.CheckStartHighWaterMark))
            throw Invalid("check-start fence", name);
        if (watcher.CheckStartHighWaterMark is not null && !watcher.CheckStartHighWaterMarkCaptured)
            throw Invalid("check-start fence without capture flag", name);

        // A resume target is only meaningful as Active or Paused and must agree with those states.
        if (resume is { } target && (state == DanbooruWatcherState.Active && target != DanbooruWatcherState.Active
            || state == DanbooruWatcherState.Paused && target != DanbooruWatcherState.Paused))
            throw Invalid("state and resume state disagree", name);

        if (state == DanbooruWatcherState.Removed || watcher.NextPage == 0)
        {
            // No traversal in flight (the domain clears all of it on completion and on Remove).
            if (state == DanbooruWatcherState.Checking)
                throw Invalid("a checking watcher always has a requested page", name);
            if (watcher.NextPage != 0 || watcher.CursorBeforePostId is not null
                || watcher.CheckStartHighWaterMark is not null || watcher.CheckStartHighWaterMarkCaptured
                || watcher.PagesCheckedInRun != 0 || watcher.BudgetLimited)
                throw Invalid("traversal fields without an in-flight traversal", name);
            return;
        }

        // NextPage > 0: every field required to resume the traversal must be present.
        if (resume is null || watcher.PageSize is null || watcher.PageBudget is null
            || !watcher.CheckStartHighWaterMarkCaptured)
            throw Invalid("in-flight traversal is missing resume state, limits or check-start fence", name);
        int pageBudget = watcher.PageBudget.Value;
        if (watcher.PagesCheckedInRun >= pageBudget || watcher.PagesCheckedInRun > watcher.NextPage - 1)
            throw Invalid("pages checked exceed the page budget or the traversed pages", name);
        if (watcher.PagesCheckedInRun > 0 && state != DanbooruWatcherState.Checking)
            throw Invalid("a run counter outside a running check", name);

        if (watcher.NextPage == 1)
        {
            // Check begun, but no page applied yet: nothing to continue below.
            if (watcher.CursorBeforePostId is not null || watcher.BudgetLimited)
                throw Invalid("cursor or budget limit before any page was applied", name);
            // BeginCheck captures the current high-water mark. Until the first page is applied neither
            // value can change; accepting a lost or older fence would misclassify already observed posts.
            if (watcher.CheckStartHighWaterMark != watcher.HighWaterMark)
                throw Invalid("first-page check-start fence differs from the high-water mark", name);
        }
        else
        {
            if (!watcher.BaselineEstablished)
                throw Invalid("continuation page before the baseline was established", name);
            if (watcher.CursorBeforePostId is null)
                throw Invalid("continuation page without a cursor", name);
            if (watcher.HighWaterMark is null
                || DanbooruPostReference.ComparePostIds(watcher.CursorBeforePostId, watcher.HighWaterMark) > 0)
                throw Invalid("cursor above the high-water mark", name);
        }

        if (watcher.CheckStartHighWaterMark is not null)
        {
            if (watcher.HighWaterMark is null
                || DanbooruPostReference.ComparePostIds(watcher.CheckStartHighWaterMark, watcher.HighWaterMark) > 0)
                throw Invalid("check-start fence above the high-water mark", name);
            // A page reaching the fence completes the traversal, so a partial cursor is still above it.
            if (watcher.CursorBeforePostId is not null
                && DanbooruPostReference.ComparePostIds(watcher.CursorBeforePostId, watcher.CheckStartHighWaterMark) <= 0)
                throw Invalid("cursor at or below the check-start fence", name);
        }

        if (watcher.BudgetLimited
            && (watcher.NextPage < pageBudget + 1 || watcher.PagesCheckedInRun != 0
                || state is not (DanbooruWatcherState.Active or DanbooruWatcherState.Paused)
                || resume != state))
            throw Invalid("inconsistent budget limit", name);
    }

    private static void ValidateFindingArguments(BooruPostKey identity, BooruFindingKind kind,
        string? watcherId, string? query)
    {
        ValidateIdentity(identity);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (kind == BooruFindingKind.Watcher)
        {
            ValidateRequiredText(watcherId!, nameof(watcherId), 128);
            if (watcherId!.Any(char.IsControl)) throw new ArgumentException("The watcher identity is invalid.", nameof(watcherId));
        }
        else if (watcherId is not null)
            throw new ArgumentException("Only watcher findings may carry a watcher identity.", nameof(watcherId));

        if (identity.IsLegacyDanbooru) ValidateOptionalQuery(query);
        else ValidateOptionalText(query, nameof(query), 1024);
    }

    private static void ValidateOptionalQuery(string? query)
    {
        if (query is null) return;
        ValidateRequiredText(query, nameof(query), 1024);
        if (!DanbooruTagQuery.TryParse(query, out DanbooruTagQuery? parsed, out _)
            || parsed is null || parsed.ProviderQuery != query)
            throw new ArgumentException("The Booru finding query is not canonical.", nameof(query));
    }

    private static string[] CanonicalIds(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        List<string> values = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string? id in ids)
        {
            ValidateRequiredText(id!, nameof(ids), 18);
            if (!IsValidPostId(id) || !seen.Add(id!))
                throw new ArgumentException("The Booru watcher contains an invalid or duplicate post id.", nameof(ids));
            values.Add(id!);
        }
        values.Sort(Comparer<string>.Create(DanbooruPostReference.ComparePostIds));
        return values.ToArray();
    }

    private static void ValidateIdentity(BooruPostKey identity)
    {
        if (!identity.IsValid) throw new ArgumentException("The Booru post identity is invalid.", nameof(identity));
    }

    private static bool IsValidPostId(string? postId) => DanbooruPostReference.IsValidPostId(postId);

    private static void ValidateMediaUrl(string? url)
    {
        ValidateOptionalText(url, nameof(url), MaximumClaimTextLength);
        if (url is null) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0
            || !uri.IsDefaultPort || uri.Fragment.Length != 0)
            throw new ArgumentException("The Booru media URL is invalid.", nameof(url));
    }

    private static void ValidateRequiredTag(string value, string parameterName)
    {
        ValidateRequiredText(value, parameterName, MaximumClaimTextLength);
        if (value.Any(char.IsWhiteSpace))
            throw new ArgumentException("The Booru tag contains whitespace.", parameterName);
    }

    private static void ValidateRequiredText(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength || value.Any(char.IsControl))
            throw new ArgumentException("The supplied text is invalid or exceeds its bound.", parameterName);
    }

    private static void ValidateOptionalText(string? value, string parameterName, int maximumLength)
    {
        if (value is not null) ValidateRequiredText(value, parameterName, maximumLength);
    }

    // Provider free text is inert metadata, not an identity. A flagged empty value is a real observation and
    // must remain distinct from null and from an absent field (including when it clears an earlier claim).
    private static void ValidateProviderText(string? value, string parameterName, int maximumLength)
    {
        if (value is not null && (value.Length > maximumLength || value.Any(char.IsControl)))
            throw new ArgumentException("The provider text is invalid or exceeds its bound.", parameterName);
    }

    private static bool Has(BooruPostClaimPresence value, BooruPostClaimPresence flag) => (value & flag) != 0;

    private static BooruPostClaim MergeClaims(BooruPostClaim previous, BooruPostClaim incoming) =>
        new(
            previous.SourceId,
            previous.PostId,
            previous.Fields | incoming.Fields,
            incoming.PostUrl ?? previous.PostUrl,
            Has(incoming.Fields, BooruPostClaimPresence.CreatedAt) ? incoming.CreatedAtUtc : previous.CreatedAtUtc,
            Has(incoming.Fields, BooruPostClaimPresence.Rating) ? incoming.Rating : previous.Rating,
            Has(incoming.Fields, BooruPostClaimPresence.Rating) ? incoming.RawRating : previous.RawRating,
            Has(incoming.Fields, BooruPostClaimPresence.Score) ? incoming.Score : previous.Score,
            Has(incoming.Fields, BooruPostClaimPresence.FavoriteCount) ? incoming.FavoriteCount : previous.FavoriteCount,
            Has(incoming.Fields, BooruPostClaimPresence.Tags) ? incoming.Tags : previous.Tags,
            Has(incoming.Fields, BooruPostClaimPresence.Media) ? incoming.Media : previous.Media,
            Has(incoming.Fields, BooruPostClaimPresence.SourceClaim) ? incoming.SourceClaim : previous.SourceClaim,
            Has(incoming.Fields, BooruPostClaimPresence.IsDeleted) ? incoming.IsDeleted : previous.IsDeleted,
            Has(incoming.Fields, BooruPostClaimPresence.IsBanned) ? incoming.IsBanned : previous.IsBanned,
            previous.InstanceId);

    private static DateTimeOffset Earliest(DateTimeOffset left, DateTimeOffset right) =>
        left <= right ? left : right;

    private static DateTimeOffset Latest(DateTimeOffset left, DateTimeOffset right) =>
        left >= right ? left : right;

    private static string FindingIdentity(BooruPostKey identity, BooruFindingKind kind, string? watcherId, string? query) =>
        string.Join("|", identity.SourceId, identity.PostId, kind.ToString(), watcherId ?? string.Empty, query ?? string.Empty);

    private static BooruPostClaim FreezeClaim(BooruPostClaim post) => post with
    {
        InstanceId = post.InstanceId == BooruPostKey.DefaultInstance ? null : post.InstanceId,
        Tags = Array.AsReadOnly(post.Tags.Select(tag => tag with { }).ToArray()),
        Media = Array.AsReadOnly(post.Media.Select(media => media with { }).ToArray())
    };

    private static BooruPostRecord FreezePostRecord(BooruPostRecord post) => post with
    {
        InstanceId = post.InstanceId == BooruPostKey.DefaultInstance ? null : post.InstanceId,
        Post = FreezeClaim(post.Post),
        Findings = FreezeFindings(post.Findings)
    };

    private static IReadOnlyList<BooruFinding> FreezeFindings(IEnumerable<BooruFinding> findings) =>
        Array.AsReadOnly(findings.Select(finding => finding with { }).ToArray());

    private static IReadOnlyList<BooruPostRecord> FreezePosts(IEnumerable<BooruPostRecord> posts) =>
        Array.AsReadOnly(posts.Select(FreezePostRecord).ToArray());

    private static BooruWatcherRecord FreezeWatcher(BooruWatcherRecord watcher) => watcher with
    {
        KnownPostIds = Array.AsReadOnly(CanonicalIds(watcher.KnownPostIds))
    };

    private static IReadOnlyList<BooruWatcherRecord> FreezeWatchers(IEnumerable<BooruWatcherRecord> watchers) =>
        Array.AsReadOnly(watchers.Select(FreezeWatcher).ToArray());

    private static BooruCatalogSnapshot FreezeSnapshot(BooruCatalogSnapshot snapshot) => new(
        snapshot.SchemaVersion,
        FreezePosts(snapshot.Posts),
        FreezeWatchers(snapshot.Watchers));

    private static byte[] ReadBounded(string path)
    {
        using FileStream stream = RegularProfileFile.OpenRead(path);
        if (stream.Length > MaximumDocumentBytes)
            throw new InvalidDataException("The Booru catalog exceeds its bounded size.");
        using MemoryStream contents = new();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (contents.Length + read > MaximumDocumentBytes)
                throw new InvalidDataException("The Booru catalog exceeds its bounded size.");
            contents.Write(buffer, 0, read);
        }
        return contents.ToArray();
    }
}

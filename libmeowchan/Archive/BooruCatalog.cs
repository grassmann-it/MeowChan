using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    bool IsBanned = false)
{
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
    string? LocalAssetId = null,
    string? LocalPath = null,
    IReadOnlyList<BooruFinding>? Findings = null)
{
    public IReadOnlyList<BooruFinding> Findings { get; init; } = Findings ?? Array.Empty<BooruFinding>();
}

/// <summary>
/// Restart-safe watcher projection. The scheduler owns the domain transition; this record is the
/// storage boundary and intentionally contains no provider-specific executable state.
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
    string? HighWaterMark = null,
    int NextPage = 0,
    bool BaselineEstablished = false,
    IReadOnlyList<string>? KnownPostIds = null)
{
    public IReadOnlyList<string> KnownPostIds { get; init; } = KnownPostIds ?? Array.Empty<string>();
}

/// <summary>Versioned additive Booru catalog. The file contains no private-tag data.</summary>
internal sealed record BooruCatalogSnapshot(
    int SchemaVersion,
    IReadOnlyList<BooruPostRecord> Posts,
    IReadOnlyList<BooruWatcherRecord> Watchers)
{
    internal const int CurrentSchemaVersion = 1;
    internal static BooruCatalogSnapshot Empty => new(CurrentSchemaVersion,
        Array.Empty<BooruPostRecord>(), Array.Empty<BooruWatcherRecord>());
}

/// <summary>
/// Profile-owned additive storage for Booru claims and watchers. The file is deliberately separate
/// from the archive/session stores so an old profile can be opened without rewriting existing data.
/// Every mutation is guarded by the exact profile lease and its store-writer admission; reads are
/// passive but still use the live lease. Invalid or newer data fails closed instead of becoming an
/// empty catalog.
/// </summary>
internal sealed class BooruCatalogStore
{
    internal const string FileName = "booru-catalog.json";
    internal const int MaximumDocumentBytes = 32 * 1024 * 1024;
    internal const int MaximumClaimTextLength = 16 * 1024;
    internal const int MaximumTags = 10_000;
    internal const int MaximumMediaVariants = 16;
    internal const int MaximumFindings = 10_000;
    internal const int MaximumKnownPostIds = 100_000;
    internal const int MaximumWatcherPage = 6;

    private readonly object _sync = new();
    private readonly ProfileLease _lease;
    private readonly string _path;
    private readonly JsonSerializerOptions _json;

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

    // These are instance-only test observers. They never grant authority and run while the
    // accepted store-writer token is pinned, immediately around the actual replacement.
    internal Action<ProfileAdmissionToken>? BeforeAdmittedMutationForTests { get; set; }
    internal Action<ProfileAdmissionToken>? AfterAdmittedMutationForTests { get; set; }

    /// <summary>Passively reads the current catalog under the live profile lease.</summary>
    internal BooruCatalogSnapshot Read() => _lease.RunExclusive(ReadCore);

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
            cancellationToken.ThrowIfCancellationRequested();
            BooruCatalogSnapshot snapshot = ReadLocked();
            BooruPostClaim frozenPost = FreezeClaim(post);
            List<BooruPostRecord> posts = snapshot.Posts.ToList();
            int index = posts.FindIndex(item => item.SourceId == frozenPost.SourceId && item.PostId == frozenPost.PostId);
            DateTimeOffset observed = observedAtUtc.ToUniversalTime();
            BooruPostRecord next = index < 0
                ? new(frozenPost.SourceId, frozenPost.PostId, frozenPost, observed, observed)
                : FreezePostRecord(posts[index] with
                {
                    // Provider search and single-post responses may expose different
                    // presence subsets. Do not let a less complete later observation
                    // erase fields already retained from a richer claim.
                    Post = MergeClaims(posts[index].Post, frozenPost),
                    FirstSeenAtUtc = Earliest(posts[index].FirstSeenAtUtc, observed),
                    LastSeenAtUtc = Latest(posts[index].LastSeenAtUtc, observed)
                });
            if (index < 0) posts.Add(next); else posts[index] = next;
            WriteLocked(snapshot with { Posts = FreezePosts(posts) }, admission, cancellationToken);
            return next;
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
        ValidateFindingArguments(sourceId, postId, kind, watcherId, query);
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(cancellationToken);
        return AddFindingAdmitted(sourceId, postId, kind, watcherId, query, foundAtUtc, admission, cancellationToken);
    }

    internal BooruPostRecord AddFindingAdmitted(string sourceId, string postId, BooruFindingKind kind,
        string? watcherId, string? query, DateTimeOffset foundAtUtc, ProfileAdmissionToken admission,
        CancellationToken cancellationToken = default)
    {
        ValidateFindingArguments(sourceId, postId, kind, watcherId, query);
        return _lease.RunStoreWrite(admission, () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            BooruCatalogSnapshot snapshot = ReadLocked();
            List<BooruPostRecord> posts = snapshot.Posts.ToList();
            int index = posts.FindIndex(item => item.SourceId == sourceId && item.PostId == postId);
            if (index < 0) throw new InvalidOperationException("A provider post must be stored before its finding.");
            BooruPostRecord current = posts[index];
            string findingId = FindingIdentity(sourceId, postId, kind, watcherId, query);
            DateTimeOffset observed = foundAtUtc.ToUniversalTime();
            if (current.Findings.Any(item => item.FindingId == findingId))
            {
                BooruPostRecord observedAgain = FreezePostRecord(current with
                {
                    FirstSeenAtUtc = Earliest(current.FirstSeenAtUtc, observed),
                    LastSeenAtUtc = Latest(current.LastSeenAtUtc, observed)
                });
                if (observedAgain.FirstSeenAtUtc != current.FirstSeenAtUtc
                    || observedAgain.LastSeenAtUtc != current.LastSeenAtUtc)
                {
                    posts[index] = observedAgain;
                    WriteLocked(snapshot with { Posts = FreezePosts(posts) }, admission, cancellationToken);
                }
                return observedAgain;
            }

            if (current.Findings.Count >= MaximumFindings)
                throw new InvalidDataException("The Booru post has too many findings.");
            List<BooruFinding> findings = current.Findings.ToList();
            findings.Add(new(findingId, kind, watcherId, query, observed));
            BooruPostRecord next = FreezePostRecord(current with
            {
                FirstSeenAtUtc = Earliest(current.FirstSeenAtUtc, observed),
                LastSeenAtUtc = Latest(current.LastSeenAtUtc, observed),
                Findings = FreezeFindings(findings)
            });
            posts[index] = next;
            WriteLocked(snapshot with { Posts = FreezePosts(posts) }, admission, cancellationToken);
            return next;
        });
    }

    internal BooruPostRecord LinkLocalAsset(string sourceId, string postId, string assetId,
        string? localPath = null, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(sourceId, postId);
        ValidateRequiredText(assetId, nameof(assetId), 512);
        ValidateOptionalText(localPath, nameof(localPath), 4096);
        using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(cancellationToken);
        return LinkLocalAssetAdmitted(sourceId, postId, assetId, localPath, admission, cancellationToken);
    }

    internal BooruPostRecord LinkLocalAssetAdmitted(string sourceId, string postId, string assetId,
        string? localPath, ProfileAdmissionToken admission, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(sourceId, postId);
        ValidateRequiredText(assetId, nameof(assetId), 512);
        ValidateOptionalText(localPath, nameof(localPath), 4096);
        return _lease.RunStoreWrite(admission, () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            BooruCatalogSnapshot snapshot = ReadLocked();
            List<BooruPostRecord> posts = snapshot.Posts.ToList();
            int index = posts.FindIndex(item => item.SourceId == sourceId && item.PostId == postId);
            if (index < 0) throw new InvalidOperationException("A provider post must be stored before its asset link.");
            BooruPostRecord next = FreezePostRecord(posts[index] with { LocalAssetId = assetId, LocalPath = localPath });
            posts[index] = next;
            WriteLocked(snapshot with { Posts = FreezePosts(posts) }, admission, cancellationToken);
            return next;
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
            cancellationToken.ThrowIfCancellationRequested();
            BooruCatalogSnapshot snapshot = ReadLocked();
            List<BooruWatcherRecord> watchers = snapshot.Watchers.ToList();
            BooruWatcherRecord canonical = FreezeWatcher(watcher);
            int index = watchers.FindIndex(item => item.WatcherId == canonical.WatcherId);
            if (index < 0) watchers.Add(canonical); else watchers[index] = canonical;
            WriteLocked(snapshot with { Watchers = FreezeWatchers(watchers) }, admission, cancellationToken);
            return canonical;
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
            cancellationToken.ThrowIfCancellationRequested();
            BooruCatalogSnapshot snapshot = ReadLocked();
            List<BooruWatcherRecord> watchers = snapshot.Watchers.ToList();
            int index = watchers.FindIndex(item => item.WatcherId == watcherId);
            if (index < 0) return false;
            watchers[index] = FreezeWatcher(watchers[index] with
            {
                State = "Removed",
                CooldownUntilUtc = null,
                ErrorCode = null
            });
            WriteLocked(snapshot with { Watchers = FreezeWatchers(watchers) }, admission, cancellationToken);
            return true;
        });
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            MaxDepth = 64,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    private BooruCatalogSnapshot ReadCore()
    {
        lock (_sync)
        {
            try { return Parse(ReadBounded(_path)); }
            catch (FileNotFoundException) { return BooruCatalogSnapshot.Empty; }
        }
    }

    private BooruCatalogSnapshot ReadLocked() => ReadCore();

    private void WriteLocked(BooruCatalogSnapshot snapshot, ProfileAdmissionToken admission,
        CancellationToken cancellationToken)
    {
        ValidateSnapshot(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, _json);
        if (bytes.Length > MaximumDocumentBytes)
            throw new InvalidDataException("The Booru catalog exceeds its bounded size.");

        string directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}.tmp");
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
            AfterAdmittedMutationForTests?.Invoke(admission);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private BooruCatalogSnapshot Parse(byte[] bytes)
    {
        if (bytes.Length > MaximumDocumentBytes)
            throw new InvalidDataException("The Booru catalog exceeds its bounded size.");
        try
        {
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

    private static void ValidateSnapshot(BooruCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SchemaVersion != BooruCatalogSnapshot.CurrentSchemaVersion)
            throw new InvalidDataException("The Booru catalog schema is not supported.");
        if (snapshot.Posts is null || snapshot.Watchers is null)
            throw new InvalidDataException("The Booru catalog collections are missing.");

        HashSet<string> postKeys = new(StringComparer.Ordinal);
        foreach (BooruPostRecord? post in snapshot.Posts)
        {
            if (post is null) throw new InvalidDataException("The Booru catalog contains a null post.");
            ValidatePostRecord(post);
            if (!postKeys.Add(PostKey(post.SourceId, post.PostId)))
                throw new InvalidDataException("The Booru catalog contains an invalid or duplicate post identity.");
        }

        HashSet<string> watcherKeys = new(StringComparer.Ordinal);
        foreach (BooruWatcherRecord? watcher in snapshot.Watchers)
        {
            if (watcher is null) throw new InvalidDataException("The Booru catalog contains a null watcher.");
            ValidateWatcher(watcher);
            string[] canonical = CanonicalIds(watcher.KnownPostIds);
            if (!watcher.KnownPostIds.SequenceEqual(canonical, StringComparer.Ordinal))
                throw new InvalidDataException("The Booru catalog contains non-canonical watcher post ids.");
            if (!watcherKeys.Add(watcher.WatcherId))
                throw new InvalidDataException("The Booru catalog contains duplicate watchers.");
        }
    }

    private static void ValidatePostRecord(BooruPostRecord post)
    {
        if (post.Post is null) throw new InvalidDataException("The Booru catalog contains a post without a claim.");
        ValidateIdentity(post.SourceId, post.PostId);
        ValidatePostClaim(post.Post);
        if (post.SourceId != post.Post.SourceId || post.PostId != post.Post.PostId)
            throw new InvalidDataException("The Booru catalog contains a mismatched post identity.");
        if (post.FirstSeenAtUtc > post.LastSeenAtUtc)
            throw new InvalidDataException("The Booru catalog contains an invalid observation range.");
        ValidateOptionalText(post.LocalAssetId, nameof(post.LocalAssetId), 512);
        ValidateOptionalText(post.LocalPath, nameof(post.LocalPath), 4096);
        if (post.LocalAssetId is null && post.LocalPath is not null)
            throw new InvalidDataException("The Booru catalog contains a local path without an asset identity.");
        if (post.Findings is null || post.Findings.Count > MaximumFindings)
            throw new InvalidDataException("The Booru catalog contains invalid post findings.");
        HashSet<string> findings = new(StringComparer.Ordinal);
        foreach (BooruFinding? finding in post.Findings)
        {
            if (finding is null) throw new InvalidDataException("The Booru catalog contains a null finding.");
            ValidateFindingArguments(post.SourceId, post.PostId, finding.Kind, finding.WatcherId, finding.Query);
            string expected = FindingIdentity(post.SourceId, post.PostId, finding.Kind, finding.WatcherId, finding.Query);
            if (!findings.Add(finding.FindingId) || finding.FindingId != expected)
                throw new InvalidDataException("The Booru catalog contains duplicate or invalid findings.");
        }
    }

    private static void ValidatePostClaim(BooruPostClaim post)
    {
        ArgumentNullException.ThrowIfNull(post);
        ValidateIdentity(post.SourceId, post.PostId);
        if ((post.Fields & ~BooruPostClaimPresence.All) != 0)
            throw new ArgumentException("The Booru post claim contains unknown fields.", nameof(post));
        ValidateOptionalText(post.PostUrl, nameof(post.PostUrl), MaximumClaimTextLength);
        if (post.PostUrl is not null && (!DanbooruPostReference.TryParse(post.PostUrl, out DanbooruPostReference? reference, out _)
            || reference is null || reference.PostId != post.PostId))
            throw new ArgumentException("The Booru post URL does not match its provider identity.", nameof(post));
        ValidateOptionalText(post.RawRating, nameof(post.RawRating), 128);
        ValidateOptionalText(post.SourceClaim, nameof(post.SourceClaim), MaximumClaimTextLength);
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
        if (!Enum.TryParse(watcher.State, ignoreCase: false, out DanbooruWatcherState state)
            || !Enum.IsDefined(state))
            throw new ArgumentException("The Booru watcher state is invalid.", nameof(watcher));
        ValidateOptionalText(watcher.ErrorCode, nameof(watcher.ErrorCode), 128);
        ValidateOptionalText(watcher.HighWaterMark, nameof(watcher.HighWaterMark), 18);
        if (watcher.HighWaterMark is not null && !IsValidPostId(watcher.HighWaterMark))
            throw new ArgumentException("The Booru watcher high-water mark is invalid.", nameof(watcher));
        if (watcher.NextPage is < 0 or > MaximumWatcherPage || watcher.KnownPostIds is null
            || watcher.KnownPostIds.Count > MaximumKnownPostIds)
            throw new ArgumentException("The Booru watcher record is invalid.", nameof(watcher));
        _ = CanonicalIds(watcher.KnownPostIds);
        if (state == DanbooruWatcherState.Cooldown && watcher.CooldownUntilUtc is null
            || state != DanbooruWatcherState.Cooldown && watcher.CooldownUntilUtc is not null)
            throw new ArgumentException("The Booru watcher cooldown state is invalid.", nameof(watcher));
        if (state is DanbooruWatcherState.Error or DanbooruWatcherState.Cooldown
            && string.IsNullOrWhiteSpace(watcher.ErrorCode)
            || state is not (DanbooruWatcherState.Error or DanbooruWatcherState.Cooldown)
            && watcher.ErrorCode is not null)
            throw new ArgumentException("The Booru watcher error state is invalid.", nameof(watcher));
    }

    private static void ValidateFindingArguments(string sourceId, string postId, BooruFindingKind kind,
        string? watcherId, string? query)
    {
        ValidateIdentity(sourceId, postId);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (kind == BooruFindingKind.Watcher)
        {
            ValidateRequiredText(watcherId!, nameof(watcherId), 128);
            if (watcherId!.Any(char.IsControl)) throw new ArgumentException("The watcher identity is invalid.", nameof(watcherId));
        }
        else if (watcherId is not null)
            throw new ArgumentException("Only watcher findings may carry a watcher identity.", nameof(watcherId));

        ValidateOptionalQuery(query);
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

    private static void ValidateIdentity(string sourceId, string postId)
    {
        ValidateRequiredText(sourceId, nameof(sourceId), 128);
        if (sourceId != DanbooruPostReference.ProviderId || !IsValidPostId(postId))
            throw new ArgumentException("Only valid Danbooru post identities are supported.", nameof(postId));
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
            Has(incoming.Fields, BooruPostClaimPresence.IsBanned) ? incoming.IsBanned : previous.IsBanned);

    private static DateTimeOffset Earliest(DateTimeOffset left, DateTimeOffset right) =>
        left <= right ? left : right;

    private static DateTimeOffset Latest(DateTimeOffset left, DateTimeOffset right) =>
        left >= right ? left : right;

    private static string PostKey(string sourceId, string postId) => sourceId + "\0" + postId;

    private static string FindingIdentity(string sourceId, string postId, BooruFindingKind kind,
        string? watcherId, string? query) => string.Join("|", sourceId, postId, kind.ToString(), watcherId ?? string.Empty, query ?? string.Empty);

    private static BooruPostClaim FreezeClaim(BooruPostClaim post) => post with
    {
        Tags = Array.AsReadOnly(post.Tags.Select(tag => tag with { }).ToArray()),
        Media = Array.AsReadOnly(post.Media.Select(media => media with { }).ToArray())
    };

    private static BooruPostRecord FreezePostRecord(BooruPostRecord post) => post with
    {
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

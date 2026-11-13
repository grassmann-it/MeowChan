using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Storage;

namespace MeowChan.Gallery;

/// <summary>
/// How a downloaded Danbooru medium is named inside the media library. A provider post is not an archived chan
/// thread: it only borrows the <see cref="ResourceId"/> shape (source, board, thread) so favorites, ratings, the
/// media index and the gallery can key it, exactly as the Booru download owner already names it
/// (<c>danbooru</c> / <c>posts</c> / post id). The identity is never written into the archive catalog.
/// </summary>
internal static class BooruMediaIdentity
{
    internal static string Source => DanbooruPostReference.ProviderId;
    internal const string Board = BooruOriginalQueue.PostsBoardCode;

    internal static ResourceId Thread(string postId) => new(Source, Board, postId);

    /// <summary>The media identifier the queue gave the original: the manifest record must carry exactly this.</summary>
    internal static string MediaIdentifier(string postId) => $"booru-{Source}-{postId}";

    internal static bool IsBooruThread(ResourceId? thread) =>
        thread is { Thread: { } post } && thread.Source == Source && thread.Board == Board && DanbooruPostReference.IsValidPostId(post);

    internal static bool TryGetPostId(ResourceId? thread, out string postId)
    {
        postId = thread?.Thread ?? string.Empty;
        return IsBooruThread(thread);
    }

    /// <summary>
    /// The page of the post at the provider. A stored provider URL is only used when it is the strict canonical
    /// post page of this very post; anything else (other host, query, text) is replaced by the canonical address.
    /// </summary>
    internal static Uri PostUrl(string postId, string? stored)
    {
        DanbooruPostReference.TryCreateFromId(postId, out DanbooruPostReference? canonical, out _);
        Uri fallback = canonical?.CanonicalUrl ?? new Uri($"https://{DanbooruPostReference.MetadataHost}/posts/{postId}");
        if (stored != null && DanbooruPostReference.TryParse(stored, out DanbooruPostReference? parsed, out _)
            && parsed!.PostId == postId && stored.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return parsed.CanonicalUrl;
        return fallback;
    }
}

/// <summary>What the library knows about one stored variant of a provider post.</summary>
internal enum BooruMediaVariantState
{
    /// <summary>Saved on this computer and linked to the post.</summary>
    Stored,
    /// <summary>The post is linked to a saved original, but the file or its record is not readable now.</summary>
    LocalFileMissing,
    /// <summary>The provider listed an original, but nothing was downloaded.</summary>
    NotDownloaded,
    /// <summary>The provider listed the variant; MeowChan never saves it and never substitutes it for the original.</summary>
    ProviderClaimOnly,
    /// <summary>The provider listed no original (restricted or removed).</summary>
    NotOffered
}

internal sealed record BooruMediaVariant(BooruPostClaimMediaRole Role, BooruMediaVariantState State, string? Extension,
    long? ExpectedSize, int? Width, int? Height);

/// <summary>Where one finding stands today. A removed watcher keeps its findings; only the observation ended.</summary>
internal enum BooruMediaWatcherStatus { NotWatcher, Observing, Removed }

internal sealed record BooruMediaFinding(BooruFindingKind Kind, string? WatcherId, string? Query, DateTimeOffset FoundAtUtc,
    BooruMediaWatcherStatus Watcher, string? WatcherState);

/// <summary>
/// Everything the catalog says about one downloaded provider post, as data for the media page and the gallery.
/// Derived from local files only; it never contacts the provider. Source tags keep the provider spelling and
/// category, and are never private MeowChan tags.
/// </summary>
internal sealed record BooruMediaDetails(
    string PostId, Uri PostUrl, string? LocalAssetId,
    bool RatingKnown, BooruPostClaimRating Rating, string? RawRating,
    int? Score, int? FavoriteCount, DateTimeOffset? CreatedAtUtc,
    bool IsDeleted, bool IsBanned, string? SourceClaim,
    IReadOnlyList<BooruPostClaimTag> Tags,
    IReadOnlyList<BooruMediaVariant> Variants,
    IReadOnlyList<BooruMediaFinding> Findings,
    DateTimeOffset FirstSeenAtUtc, DateTimeOffset LastSeenAtUtc)
{
    internal BooruMediaVariant Original => Variants.First(variant => variant.Role == BooruPostClaimMediaRole.Original);
    internal int WatcherFindings => Findings.Count(finding => finding.Kind == BooruFindingKind.Watcher);
}

/// <summary>Pure read-side rules: no IO except the manifests the entry builder is handed.</summary>
internal static class BooruMediaProjection
{
    private static readonly BooruPostClaimTagCategory[] CategoryOrder =
    {
        BooruPostClaimTagCategory.Artist, BooruPostClaimTagCategory.Copyright, BooruPostClaimTagCategory.Character,
        BooruPostClaimTagCategory.General, BooruPostClaimTagCategory.Meta, BooruPostClaimTagCategory.Unknown
    };

    /// <summary>The claim, findings and watcher standing of one post. <paramref name="localFilePresent"/> is what the manifest said.</summary>
    internal static BooruMediaDetails Describe(BooruPostRecord record, IReadOnlyList<BooruWatcherRecord> watchers, bool localFilePresent)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(watchers);
        BooruPostClaim claim = record.Post;
        bool Has(BooruPostClaimPresence field) => (claim.Fields & field) != 0;
        BooruPostClaimMedia? original = claim.Media.FirstOrDefault(media => media.Role == BooruPostClaimMediaRole.Original);
        List<BooruMediaVariant> variants = new()
        {
            new(BooruPostClaimMediaRole.Original,
                record.LocalAssetId != null ? localFilePresent ? BooruMediaVariantState.Stored : BooruMediaVariantState.LocalFileMissing
                : original?.Url != null ? BooruMediaVariantState.NotDownloaded : BooruMediaVariantState.NotOffered,
                original?.Extension, original?.ExpectedSize, original?.Width, original?.Height)
        };
        foreach (BooruPostClaimMediaRole role in new[] { BooruPostClaimMediaRole.Sample, BooruPostClaimMediaRole.Preview })
            if (claim.Media.FirstOrDefault(media => media.Role == role) is { } other)
                variants.Add(new(role, BooruMediaVariantState.ProviderClaimOnly, other.Extension, other.ExpectedSize, other.Width, other.Height));

        Dictionary<string, BooruWatcherRecord> byId = watchers.ToDictionary(watcher => watcher.WatcherId, StringComparer.Ordinal);
        BooruMediaFinding[] findings = record.Findings.OrderBy(finding => finding.FoundAtUtc).ThenBy(finding => finding.FindingId, StringComparer.Ordinal)
            .Select(finding =>
            {
                if (finding.Kind != BooruFindingKind.Watcher || finding.WatcherId == null)
                    return new BooruMediaFinding(finding.Kind, finding.WatcherId, finding.Query, finding.FoundAtUtc, BooruMediaWatcherStatus.NotWatcher, null);
                bool known = byId.TryGetValue(finding.WatcherId, out BooruWatcherRecord? watcher);
                bool removed = !known || string.Equals(watcher!.State, "Removed", StringComparison.Ordinal);
                return new BooruMediaFinding(finding.Kind, finding.WatcherId, finding.Query, finding.FoundAtUtc,
                    removed ? BooruMediaWatcherStatus.Removed : BooruMediaWatcherStatus.Observing, known ? watcher!.State : null);
            }).ToArray();

        BooruPostClaimTag[] tags = claim.Tags.OrderBy(tag => Array.IndexOf(CategoryOrder, tag.Category))
            .ThenBy(tag => tag.Name, StringComparer.Ordinal).ToArray();
        return new BooruMediaDetails(record.PostId, BooruMediaIdentity.PostUrl(record.PostId, claim.PostUrl), record.LocalAssetId,
            Has(BooruPostClaimPresence.Rating) && claim.Rating != BooruPostClaimRating.Unknown, claim.Rating, claim.RawRating,
            Has(BooruPostClaimPresence.Score) ? claim.Score : null, Has(BooruPostClaimPresence.FavoriteCount) ? claim.FavoriteCount : null,
            Has(BooruPostClaimPresence.CreatedAt) ? claim.CreatedAtUtc : null,
            claim.IsDeleted, claim.IsBanned, Has(BooruPostClaimPresence.SourceClaim) ? claim.SourceClaim : null,
            Array.AsReadOnly(tags), Array.AsReadOnly(variants.ToArray()), Array.AsReadOnly(findings),
            record.FirstSeenAtUtc, record.LastSeenAtUtc);
    }

    /// <summary>Details of one post by id, or null when the catalog no longer holds it.</summary>
    internal static BooruMediaDetails? Find(BooruCatalogSnapshot catalog, string postId, bool localFilePresent = true)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        BooruPostRecord? record = catalog.Posts.FirstOrDefault(post => post.SourceId == BooruMediaIdentity.Source && post.PostId == postId);
        return record == null ? null : Describe(record, catalog.Watchers, localFilePresent);
    }

    /// <summary>The media directory a post's saved original lives in, from the catalog's own local link; null when none is linked.</summary>
    internal static string? DirectoryOf(BooruPostRecord record) =>
        record.LocalAssetId != null && record.LocalPath is { Length: > 0 } path && Path.IsPathFullyQualified(path)
            ? Path.GetDirectoryName(path) : null;

    /// <summary>
    /// Index entries of every post that has a saved original, in post order. A post whose file or manifest record
    /// cannot be read becomes a missing entry (never invented as present). Posts that were only seen or are still
    /// waiting for their download are no local media and have no entry. Unreadable manifests (IO errors) are counted.
    /// </summary>
    internal static async Task<(IReadOnlyList<MediaLibraryEntry> Entries, int Unreadable)> EntriesAsync(BooruCatalogSnapshot catalog,
        Func<string, CancellationToken, Task<MediaAssetSnapshot>> openManifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(openManifest);
        Result result = await EntriesAsync(catalog, new MediaManifestCache(openManifest, identity: false), null, cancellationToken).ConfigureAwait(false);
        return (result.Entries, result.Unreadable);
    }

    /// <summary>The provider entries, the number of unreadable media folders and the manifests the entries were derived from.</summary>
    internal sealed record Result(IReadOnlyList<MediaLibraryEntry> Entries, int Unreadable, IReadOnlyList<MediaLibraryManifestState> Manifests);

    /// <summary>
    /// As above, through <paramref name="manifests"/>. A saved post whose media folder, local asset and manifest file identity
    /// are the same as in <paramref name="previous"/> keeps its previous entry (with the catalog's current first/last seen
    /// times) without the manifest being opened: equal identity means equal bytes, so the lookup would find the same record.
    /// </summary>
    internal static async Task<Result> EntriesAsync(BooruCatalogSnapshot catalog, MediaManifestCache manifests, MediaLibrarySnapshot? previous,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(manifests);
        Dictionary<string, MediaLibraryManifestState> priorManifests = new(StringComparer.Ordinal);
        Dictionary<string, MediaLibraryEntry> priorEntries = new(StringComparer.Ordinal);
        if (previous != null && manifests.UsesIdentity)
        {
            foreach (MediaLibraryManifestState state in previous.ProviderManifests) priorManifests.TryAdd(state.Directory, state);
            foreach (MediaLibraryEntry entry in previous.Entries)
                if (entry.BooruPostId is { } id) priorEntries.TryAdd(id, entry);
        }
        List<MediaLibraryEntry> entries = new();
        Dictionary<string, MediaLibraryManifestState> states = new(StringComparer.Ordinal);
        int unreadable = 0;
        foreach (BooruPostRecord record in catalog.Posts.Where(post => post.SourceId == BooruMediaIdentity.Source && post.LocalAssetId != null)
            .OrderBy(post => post, Comparer<BooruPostRecord>.Create((left, right) => DanbooruPostReference.ComparePostIds(left.PostId, right.PostId))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!DanbooruPostReference.IsValidPostId(record.PostId)) continue;
            MediaAssetReference? asset = null;
            MediaAssetSnapshot? manifest = null;
            string? directory = DirectoryOf(record);
            if (directory != null)
            {
                if (priorManifests.TryGetValue(directory, out MediaLibraryManifestState? prior) && prior is { Generation: not null, Identity: { } identity }
                    && priorEntries.TryGetValue(record.PostId, out MediaLibraryEntry? kept)
                    && kept is { Presence: MediaLibraryPresence.Present } && kept.Directory == directory && kept.AssetId == record.LocalAssetId
                    && manifests.Probe(directory) == identity)
                {
                    entries.Add(kept.FirstSeenAt == record.FirstSeenAtUtc && kept.LastCapturedAt == record.LastSeenAtUtc ? kept
                        : kept with { FirstSeenAt = record.FirstSeenAtUtc, LastCapturedAt = record.LastSeenAtUtc });
                    states.TryAdd(directory, prior);
                    continue;
                }
                try
                {
                    MediaManifestRead read = await manifests.ReadAsync(directory, cancellationToken).ConfigureAwait(false);
                    manifest = read.Snapshot;
                    states.TryAdd(directory, new MediaLibraryManifestState(directory, manifest.Generation) { Identity = read.Identity });
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { unreadable++; }
                if (manifest is { Status: MediaManifestStatus.Available }
                    && manifest.FindById(record.LocalAssetId!) is { } found && IsTheOriginalOf(found, record.PostId)) asset = found;
            }
            entries.Add(EntryOf(record, directory, asset));
        }
        return new(entries, unreadable, states.Values.ToArray());
    }

    // The manifest record must be exactly the original the queue saved for this post, not any file in that folder.
    internal static bool IsTheOriginalOf(MediaAssetReference asset, string postId) =>
        asset.Variant == BooruOriginalDownloadPlan.Variant && asset.MediaIdentifier == BooruMediaIdentity.MediaIdentifier(postId);

    private static MediaLibraryEntry EntryOf(BooruPostRecord record, string? directory, MediaAssetReference? asset)
    {
        string postId = record.PostId;
        ResourceId thread = BooruMediaIdentity.Thread(postId);
        string title = "Danbooru #" + postId;
        string? localName = record.LocalPath == null ? null : Path.GetFileName(record.LocalPath);
        if (asset == null || directory == null)
            return new MediaLibraryEntry(thread, title, 0, record.FirstSeenAtUtc, record.LastSeenAtUtc, null, null, null, null, null,
                null, null, null, null, BooruMediaIdentity.MediaIdentifier(postId), BooruOriginalDownloadPlan.Variant, localName,
                SavedMediaTypes.Classify(localName ?? string.Empty), MediaLibraryPresence.Missing, false, false) { BooruPostId = postId };
        string name = asset.OriginalFileName ?? Path.GetFileName(asset.Path);
        return new MediaLibraryEntry(thread, title, 0, record.FirstSeenAtUtc, record.LastSeenAtUtc, null, null, null, null, null,
            directory, asset.AssetId, asset.Sha256.ToLowerInvariant(), asset.Length, asset.MediaIdentifier, asset.Variant, name,
            SavedMediaTypes.Classify(name), MediaLibraryPresence.Present, Unassigned: true, Historical: !asset.IsCurrent) { BooruPostId = postId };
    }
}

/// <summary>
/// Turns an index entry of a downloaded provider post back into the ordinary gallery entry: the saved original
/// of exactly this post, in its manifest, as a file without a chan post. An entry whose catalog link, manifest
/// record or bytes no longer agree with the index is left out rather than guessed.
/// </summary>
internal static class BooruMediaGallery
{
    internal static async Task<GalleryEntry?> ResolveAsync(MediaLibraryEntry entry, BooruCatalogSnapshot? catalog,
        Func<string, CancellationToken, Task<MediaAssetSnapshot>> openManifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(openManifest);
        if (entry.BooruPostId is not { } postId || catalog == null || entry.Presence != MediaLibraryPresence.Present
            || entry.Directory == null || entry.AssetId == null) return null;
        BooruPostRecord? record = catalog.Posts.FirstOrDefault(post => post.SourceId == BooruMediaIdentity.Source && post.PostId == postId);
        if (record == null || record.LocalAssetId != entry.AssetId
            || !string.Equals(BooruMediaProjection.DirectoryOf(record), entry.Directory, StringComparison.Ordinal)) return null;
        MediaAssetSnapshot manifest;
        try { manifest = await openManifest(entry.Directory, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
        if (manifest.Status != MediaManifestStatus.Available || manifest.FindById(entry.AssetId) is not { } asset
            || !BooruMediaProjection.IsTheOriginalOf(asset, postId)
            || !string.Equals(asset.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase) || asset.Length != entry.Length
            || asset.IsCurrent == entry.Historical) return null;
        return new GalleryEntry(new GalleryAssetKey(entry.Thread, manifest.Directory, asset.AssetId), manifest, asset, null, null,
            !asset.IsCurrent, true) { ThreadUrl = BooruMediaIdentity.PostUrl(postId, record.Post.PostUrl).AbsoluteUri };
    }
}

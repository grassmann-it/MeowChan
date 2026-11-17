using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop.Booru;

/// <summary>What the search field holds: a tag query, or a single post link/ID for a one-shot.</summary>
internal enum BooruInputMode { Tags, Post }

/// <summary>Whether a provider original can be handed to the download queue. A sample never stands in.</summary>
internal enum BooruOriginalAccess
{
    /// <summary>The provider offers an original with a URL.</summary>
    Available,
    /// <summary>The provider lists no original variant for this post.</summary>
    NotOffered,
    /// <summary>The provider withholds the original URL, or the post is deleted/banned.</summary>
    Restricted
}

/// <summary>
/// What the provider offers between the thumbnail and the original. <see cref="OriginalOnly"/> is the case the plugin
/// reports as "no sample": it drops the sample address when it equals the original (small images), so the only
/// full-size picture is the original. The viewer must not stretch the preview in its place without saying so, and the
/// sample path never fetches the original (originals go through the download queue only).
/// </summary>
internal enum BooruSampleAvailability
{
    /// <summary>A sample address is declared; <see cref="IBooruSamplePort"/> may fetch it.</summary>
    Provided,
    /// <summary>No separate sample, but an original with an address exists (the sample is the original).</summary>
    OriginalOnly,
    /// <summary>Neither a sample nor a usable original address (withdrawn, restricted, or no file).</summary>
    None
}

/// <summary>
/// UI-facing, immutable projection of one validated provider post. <see cref="Post"/> keeps the
/// host-validated SDK post so the queue port can admit it without a second provider read.
/// Everything shown here is a provider claim: the rating is the provider's, never the user's.
/// </summary>
internal sealed record BooruPostView
{
    private BooruPostView(BooruPost post) => Post = post;

    internal BooruPost Post { get; }
    internal string SourceId => Post.Id.SourceId;
    internal string PostId => Post.Id.PostId;
    internal Uri? PostUrl { get; private init; }
    internal Uri? PreviewUrl { get; private init; }
    /// <summary>The declared sample address (preview-media host, same path as previews), or null when none is offered.</summary>
    internal Uri? SampleUrl { get; private init; }
    /// <summary>The sample's file extension: the declared one, else the address path's (the plugin declares none); the dot is not guaranteed.</summary>
    internal string? SampleExtension { get; private init; }
    internal BooruSampleAvailability SampleAvailability { get; private init; }
    /// <summary>The provider's free-text "source" claim: inert text, never a fetch target or a link.</summary>
    internal string? SourceClaim { get; private init; }
    internal BooruRating Rating { get; private init; }
    internal string? RawRating { get; private init; }
    internal int? Score { get; private init; }
    internal int? FavoriteCount { get; private init; }
    internal DateTimeOffset? CreatedAt { get; private init; }
    internal ImmutableArray<BooruTag> Tags { get; private init; } = ImmutableArray<BooruTag>.Empty;
    internal BooruMediaVariant? Original { get; private init; }
    internal BooruOriginalAccess OriginalAccess { get; private init; }
    internal bool IsDeleted { get; private init; }
    internal bool IsBanned { get; private init; }

    internal static BooruPostView From(BooruPost post)
    {
        ArgumentNullException.ThrowIfNull(post);
        BooruFieldPresence fields = post.Fields;
        BooruMediaVariant? original = post.Media?.FirstOrDefault(media => media.Role == BooruMediaRole.Original);
        BooruMediaVariant? preview = post.Media?.FirstOrDefault(media => media.Role == BooruMediaRole.Preview);
        BooruMediaVariant? sample = post.Media?.FirstOrDefault(media => media.Role == BooruMediaRole.Sample);
        bool withdrawn = post.IsDeleted || post.IsBanned;
        BooruOriginalAccess access = withdrawn ? BooruOriginalAccess.Restricted
            : original is null ? BooruOriginalAccess.NotOffered
            : original.Url is null ? BooruOriginalAccess.Restricted
            : BooruOriginalAccess.Available;
        return new BooruPostView(post)
        {
            PostUrl = post.PostUrl,
            PreviewUrl = preview?.Url,
            SampleUrl = sample?.Url,
            SampleExtension = sample?.Url is null ? null : sample.Extension ?? Path.GetExtension(sample.Url.AbsolutePath),
            SampleAvailability = sample?.Url is not null ? BooruSampleAvailability.Provided
                : original?.Url is not null && !withdrawn ? BooruSampleAvailability.OriginalOnly
                : BooruSampleAvailability.None,
            SourceClaim = fields.HasFlag(BooruFieldPresence.SourceClaim) && !string.IsNullOrWhiteSpace(post.SourceClaim)
                ? post.SourceClaim : null,
            Rating = fields.HasFlag(BooruFieldPresence.Rating) ? post.Rating : BooruRating.Unknown,
            RawRating = fields.HasFlag(BooruFieldPresence.Rating) ? post.RawRating : null,
            Score = fields.HasFlag(BooruFieldPresence.Score) ? post.Score : null,
            FavoriteCount = fields.HasFlag(BooruFieldPresence.FavoriteCount) ? post.FavoriteCount : null,
            CreatedAt = fields.HasFlag(BooruFieldPresence.CreatedAt) ? post.CreatedAt : null,
            Tags = fields.HasFlag(BooruFieldPresence.Tags) && post.Tags is { } tags
                ? tags.ToImmutableArray() : ImmutableArray<BooruTag>.Empty,
            Original = original,
            OriginalAccess = access,
            IsDeleted = post.IsDeleted,
            IsBanned = post.IsBanned
        };
    }
}

/// <summary>Where a "Load original" request comes from; the queue adapter keeps the provenance.</summary>
internal enum BooruLoadOrigin { Search, OneShot }

/// <summary>Per-row result of the explicit "Load original" action.</summary>
internal enum BooruRowLoad
{
    Idle, Handing, Queued, AlreadyQueued, AlreadyLoaded, NoOriginal, Restricted, Offline, Failed
}

/// <summary>What the local library/queue already knows about a provider post.</summary>
internal enum BooruLocalState
{
    Unknown, NotLoaded, Queued, Loaded,
    /// <summary>The queue's last outcome for this post was a failure (download, link or admission); an explicit retry may re-admit it.</summary>
    Failed
}

internal enum BooruPreviewState { None, Loading, Ready, Unavailable }

/// <summary>Immutable row snapshot; every change produces a new row so views never see torn state.</summary>
internal sealed record BooruResultRowView(
    BooruPostView Post,
    BooruRowLoad Load = BooruRowLoad.Idle,
    string? LoadCode = null,
    BooruPreviewState Preview = BooruPreviewState.None,
    byte[]? PreviewBytes = null)
{
    internal bool CanLoad => Post.OriginalAccess == BooruOriginalAccess.Available
        && Load is BooruRowLoad.Idle or BooruRowLoad.Offline or BooruRowLoad.Failed;

    /// <summary>True while the queue owns a cancellable admission or download request.</summary>
    internal bool CanCancel => Post.OriginalAccess == BooruOriginalAccess.Available
        && Load is BooruRowLoad.Handing or BooruRowLoad.Queued or BooruRowLoad.AlreadyQueued;

    /// <summary>True when the stored claim may be explicitly re-admitted after a visible failure.</summary>
    internal bool CanRetry => Post.OriginalAccess == BooruOriginalAccess.Available
        && Load is BooruRowLoad.Offline or BooruRowLoad.Failed;
}

internal enum BooruNoticeKind { Info, Success, Warning, Error }

/// <summary>A localizable message: the view renders <see cref="Key"/> with <see cref="Args"/> in the live language.</summary>
internal sealed record BooruNotice(BooruNoticeKind Kind, string Key, object?[] Args)
{
    internal BooruNotice(BooruNoticeKind kind, string key) : this(kind, key, Array.Empty<object?>()) { }
}

internal enum BooruWatcherStatus { Active, Paused, Checking, Cooldown, Error, Removed }

/// <summary>
/// UI projection of one stored watcher. Mapped from the domain aggregate (or storage record) by the
/// adapter that owns the scheduler; the UI only shows it and issues the five row commands plus the host cooldown reset.
/// <paramref name="LatestPostId"/> is the newest post id the watcher has seen (its high-water mark), if any.
/// </summary>
internal sealed record BooruWatcherView(
    string WatcherId,
    string Query,
    BooruWatcherStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastCheckedAt = null,
    DateTimeOffset? NextCheckAt = null,
    DateTimeOffset? CooldownUntil = null,
    string? ErrorCode = null,
    int HitCount = 0,
    int QueuedCount = 0,
    bool BaselineEstablished = false,
    bool BudgetLimited = false,
    string? LatestPostId = null);

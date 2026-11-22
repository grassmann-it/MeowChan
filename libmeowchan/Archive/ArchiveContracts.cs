using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Interfaces;

namespace MeowChan.Archive;

[Flags]
public enum PostFieldPresence
{
    None = 0, PostedAt = 1, Subject = 2, Author = 4, Html = 8, IsDeleted = 16,
    Attachments = 32, All = PostedAt | Subject | Author | Html | IsDeleted | Attachments
}

/// <summary>
/// Source metadata only, never a media completion record. A deleted attachment with no MediaId
/// marks the previously captured attachment at Position; no media bytes are removed.
/// </summary>
public sealed record CapturedAttachment(int Position, string? MediaId, string Variant = "original",
    string? Url = null, string? OriginalFileName = null, string? PlannedPath = null,
    long? ExpectedSize = null, string? ExpectedMd5 = null, bool IsDeleted = false)
{
    /// <summary>Provider-reported pixel width. Null is retained for legacy or non-image captures.</summary>
    public int? Width { get; init; }
    /// <summary>Provider-reported pixel height. Null is retained for legacy or non-image captures.</summary>
    public int? Height { get; init; }
    public string? ExpectedSha256 { get; init; }
    public string? ExpectedSha1 { get; init; }
    public MeowChan.Storage.MediaProviderSizeHint? ProviderSizeHint { get; init; }
    /// <summary>Host-computed observed revision. Null denotes legacy, unbound provenance.</summary>
    public string? AttachmentRevisionKey { get; init; }
}

/// <summary>
/// Fields distinguishes missing fields from explicitly delivered empty/null values. Ordinal is
/// a nonnegative 64-bit provider order hint; providers with opaque identities should keep it stable across snapshots.
/// The built-in 4chan/8kun sources use their monotonically increasing decimal post identities
/// for archive order because their response-array positions shift when posts disappear.
/// </summary>
public sealed record CapturedPost(string PostId, long Ordinal, PostFieldPresence Fields,
    DateTimeOffset? PostedAt = null, string? Subject = null, string? Author = null, string? Html = null,
    bool IsDeleted = false, IReadOnlyList<CapturedAttachment>? Attachments = null);

public sealed record CapturedThreadSnapshot(ResourceId Id, string Url, string Title,
    ThreadSourceState SourceState, DateTimeOffset CapturedAt, string MediaDirectory,
    IReadOnlyList<CapturedPost> Posts)
{
    // Persist the source's inert ordering contract with durable final snapshots as well as captures.
    [System.Text.Json.Serialization.JsonInclude]
    internal bool? DecimalPostOrder { get; init; }
}

public sealed record ArchiveThreadRegistration(ResourceId Id, string Url, string Title,
    string MediaDirectory, bool CaptureEnabled, ThreadSourceState SourceState = ThreadSourceState.Active);

public sealed record ArchiveThreadSummary(ResourceId Id, string Url, string Title, string MediaDirectory,
    ThreadSourceState SourceState, bool CaptureEnabled, DateTimeOffset FirstSeenAt,
    DateTimeOffset? LastCapturedAt, int PostCount, long Revision);

public sealed record ArchiveThreadPage(IReadOnlyList<ArchiveThreadSummary> Items, int TotalCount, int Offset)
{
    /// <summary>
    /// For a relevance-sorted search with a text: the match tier of each item (parallel to <see cref="Items"/>); else null.
    /// The tier is kept out of <see cref="ArchiveThreadSummary"/> so summaries stay comparable with fresh catalog reads.
    /// </summary>
    public IReadOnlyList<ArchiveSearchMatchTier>? MatchTiers { get; init; }

    /// <summary>With <see cref="MatchTiers"/>: the number of matches of the whole result per tier, indexed by tier.</summary>
    public IReadOnlyList<int>? MatchTierCounts { get; init; }

    /// <summary>A page of an already frozen, ordered ranked result (tiers kept when the result has them).</summary>
    internal static ArchiveThreadPage FromRanked(IReadOnlyList<ArchiveRankedThread> all, int offset, int limit, bool tiered)
    {
        ArchiveRankedThread[] page = all.Skip(offset).Take(limit).ToArray();
        int[] counts = new int[3];
        if (tiered) foreach (ArchiveRankedThread item in all) counts[(int)item.Tier!.Value]++;
        return new ArchiveThreadPage(Array.AsReadOnly(page.Select(item => item.Summary).ToArray()), all.Count, offset)
        {
            MatchTiers = tiered ? Array.AsReadOnly(page.Select(item => item.Tier!.Value).ToArray()) : null,
            MatchTierCounts = tiered ? Array.AsReadOnly(counts) : null
        };
    }
}

/// <summary>One frozen search match with its tier (null unless the search ranks by tier).</summary>
internal sealed record ArchiveRankedThread(ArchiveThreadSummary Summary, ArchiveSearchMatchTier? Tier);

/// <summary>Metadata captured before a thread is removed from the catalog. Media files remain the caller's responsibility.</summary>
public sealed record ArchiveDeletedThread(ArchiveThreadSummary Summary, IReadOnlyList<string> MediaDirectories);

/// <summary>Read-only deletion preview. Shared directories are also registered to unselected threads.</summary>
public sealed record ArchiveDeletionPlan(IReadOnlyList<ArchiveDeletedThread> Threads,
    IReadOnlyList<string> SharedMediaDirectories);

/// <summary>A complete immutable thread read transaction, with bounded access to its retained posts.</summary>
public sealed class ArchivedThreadView
{
    private readonly CapturedPost[] _posts;
    internal ArchivedThreadView(ArchiveThreadSummary summary, IEnumerable<CapturedPost> posts, IEnumerable<string>? mediaDirectories = null,
        bool? decimalPostOrder = null)
    {
        Summary = summary;
        DecimalPostOrder = decimalPostOrder;
        MediaDirectories = Array.AsReadOnly((mediaDirectories ?? Array.Empty<string>()).ToArray());
        _posts = ArchivePostOrder.Sort(summary.Id, posts.Select(ArchiveCaptureMerge.CopyPost), decimalPostOrder);
    }
    public ArchiveThreadSummary Summary { get; }
    // Preserve the stored nullable contract for passive native export; sorting's legacy
    // fallback is an interpretation, not a reason to rewrite the captured contract.
    internal bool? DecimalPostOrder { get; }
    public IReadOnlyList<string> MediaDirectories { get; }
    public int PostCount => _posts.Length;
    public IReadOnlyList<CapturedPost> GetPosts(int offset = 0, int limit = 100)
    {
        ArchiveValidation.ValidatePage(offset, limit);
        int count = offset >= _posts.Length ? 0 : Math.Min(limit, _posts.Length - offset);
        return Array.AsReadOnly(count == 0 ? Array.Empty<CapturedPost>() : _posts.AsSpan(offset, count).ToArray());
    }
    /// <summary>Zero-based index in this immutable read, or -1 when the post was not captured.</summary>
    public int FindPostIndex(string postId)
    {
        ArgumentNullException.ThrowIfNull(postId);
        // One lookup table per immutable read, built on first use: the reader asks per scroll and per quote.
        return (_postIndex ??= new Lazy<Dictionary<string, int>>(BuildPostIndex)).Value.TryGetValue(postId, out int index) ? index : -1;
    }
    private Lazy<Dictionary<string, int>>? _postIndex;
    private Dictionary<string, int> BuildPostIndex()
    {
        Dictionary<string, int> index = new(_posts.Length, StringComparer.Ordinal);
        for (int position = 0; position < _posts.Length; position++) index.TryAdd(_posts[position].PostId, position); // First one wins, as before.
        return index;
    }
    internal IReadOnlyList<CapturedPost> Posts => _posts;
    private ThreadMediaPositions? _mediaPositions;
    internal ThreadMediaPositions MediaPositions => System.Threading.LazyInitializer.EnsureInitialized(
        ref _mediaPositions, () => new ThreadMediaPositions(_posts));
}

/// <summary>
/// Combines successful observations without losing previously observed posts or missing fields.
/// Also usable for one pending retry snapshot after a storage failure; it does not queue revisions.
/// </summary>
public static class ArchiveCaptureMerge
{
    public static CapturedThreadSnapshot Merge(CapturedThreadSnapshot previous, CapturedThreadSnapshot next)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);
        if (previous.Id != next.Id) throw new ArgumentException("Snapshots must identify the same thread.", nameof(next));
        Dictionary<string, CapturedPost> posts = previous.Posts.ToDictionary(post => post.PostId, CopyPost, StringComparer.Ordinal);
        foreach (CapturedPost post in next.Posts)
            posts[post.PostId] = posts.TryGetValue(post.PostId, out CapturedPost? old) ? MergePost(old, post) : CopyPost(post);
        bool? order = next.DecimalPostOrder ?? previous.DecimalPostOrder;
        return next with { Posts = Array.AsReadOnly(ArchivePostOrder.Sort(next.Id, posts.Values, order)), DecimalPostOrder = order };
    }

    public static CapturedPost MergePost(CapturedPost previous, CapturedPost next)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);
        if (previous.PostId != next.PostId) throw new ArgumentException("Posts must have the same identity.", nameof(next));
        bool Has(PostFieldPresence field) => (next.Fields & field) != 0;
        IReadOnlyList<CapturedAttachment>? attachments = previous.Attachments;
        if (attachments == null && (previous.Fields & PostFieldPresence.Attachments) == 0)
            attachments = Array.Empty<CapturedAttachment>();
        if (Has(PostFieldPresence.Attachments))
        {
            Dictionary<int, CapturedAttachment> merged = (attachments ?? Array.Empty<CapturedAttachment>())
                .ToDictionary(attachment => attachment.Position);
            foreach (CapturedAttachment attachment in next.Attachments ?? Array.Empty<CapturedAttachment>())
            {
                if (attachment.IsDeleted && attachment.MediaId == null && merged.TryGetValue(attachment.Position, out CapturedAttachment? old))
                    merged[attachment.Position] = old with { IsDeleted = true };
                else if (merged.TryGetValue(attachment.Position, out CapturedAttachment? oldAttachment))
                    merged[attachment.Position] = PreserveDimensions(oldAttachment, attachment);
                else merged[attachment.Position] = attachment;
            }
            // Null is an observation, not deletion of already known attachment metadata.
            attachments = next.Attachments == null && merged.Count == 0 ? null
                : Array.AsReadOnly(merged.Values.OrderBy(attachment => attachment.Position).ToArray());
        }
        return new CapturedPost(next.PostId, next.Ordinal, previous.Fields | next.Fields,
            Has(PostFieldPresence.PostedAt) ? next.PostedAt : previous.PostedAt,
            Has(PostFieldPresence.Subject) ? next.Subject : previous.Subject,
            Has(PostFieldPresence.Author) ? next.Author : previous.Author,
            Has(PostFieldPresence.Html) ? next.Html : previous.Html,
            Has(PostFieldPresence.IsDeleted) ? next.IsDeleted : previous.IsDeleted,
            attachments == null ? null : Array.AsReadOnly(attachments.ToArray()));
    }

    private static CapturedAttachment PreserveDimensions(CapturedAttachment previous, CapturedAttachment next)
    {
        // Dimensions are optional provider observations. A later legacy/partial capture must
        // not erase dimensions already recorded for the same media occurrence; fresh values
        // still replace them. Do not carry dimensions across a changed media identity.
        if (!string.Equals(previous.MediaId, next.MediaId, StringComparison.Ordinal)
            || !string.Equals(previous.Variant, next.Variant, StringComparison.Ordinal)
            || !string.Equals(previous.Url, next.Url, StringComparison.Ordinal))
            return next;
        return next with { Width = next.Width ?? previous.Width, Height = next.Height ?? previous.Height };
    }

    internal static CapturedPost CopyPost(CapturedPost post)
    {
        bool Has(PostFieldPresence field) => (post.Fields & field) != 0;
        return post with
        {
            PostedAt = Has(PostFieldPresence.PostedAt) ? post.PostedAt : null,
            Subject = Has(PostFieldPresence.Subject) ? post.Subject : null,
            Author = Has(PostFieldPresence.Author) ? post.Author : null,
            Html = Has(PostFieldPresence.Html) ? post.Html : null,
            IsDeleted = Has(PostFieldPresence.IsDeleted) && post.IsDeleted,
            Attachments = Has(PostFieldPresence.Attachments) && post.Attachments == null ? null
                : Array.AsReadOnly((Has(PostFieldPresence.Attachments)
                    ? post.Attachments! : Array.Empty<CapturedAttachment>()).ToArray())
        };
    }
}

internal static class ArchivePostOrder
{
    private static readonly IComparer<string> DecimalIdentityOrder = Comparer<string>.Create(CompareDecimalIdentity);

    internal static CapturedPost[] Sort(ResourceId id, IEnumerable<CapturedPost> posts, bool? decimalPostOrder = null)
    {
        CapturedPost[] items = posts.ToArray();
        // This is a source contract, not an assumption that every provider's numeric IDs
        // are chronological. Opaque/custom providers retain control through Ordinal.
        if ((decimalPostOrder ?? (id.Source is "4chan" or "8kun"))
            && items.All(post => post.PostId.Length > 0 && post.PostId.All(character => character is >= '0' and <= '9')))
            return items.OrderBy(post => post.PostId, DecimalIdentityOrder).ToArray();
        return items.OrderBy(post => post.Ordinal).ThenBy(post => post.PostId, StringComparer.Ordinal).ToArray();
    }

    private static int CompareDecimalIdentity(string? first, string? second)
    {
        ReadOnlySpan<char> firstDigits = first.AsSpan().TrimStart('0');
        ReadOnlySpan<char> secondDigits = second.AsSpan().TrimStart('0');
        int comparison = firstDigits.Length.CompareTo(secondDigits.Length);
        if (comparison == 0) comparison = firstDigits.SequenceCompareTo(secondDigits);
        return comparison != 0 ? comparison : string.CompareOrdinal(first, second);
    }
}

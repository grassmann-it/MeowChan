using System;
using System.Collections.Generic;
using System.Linq;
using oChan.Interfaces;

namespace oChan.Archive;

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
    long? ExpectedSize = null, string? ExpectedMd5 = null, bool IsDeleted = false);

/// <summary>
/// Fields distinguishes missing fields from explicitly delivered empty/null values. Ordinal is
/// a provider order hint; providers with opaque identities should keep it stable across snapshots.
/// The built-in 4chan/8kun sources use their monotonically increasing decimal post identities
/// for archive order because their response-array positions shift when posts disappear.
/// </summary>
public sealed record CapturedPost(string PostId, int Ordinal, PostFieldPresence Fields,
    DateTimeOffset? PostedAt = null, string? Subject = null, string? Author = null, string? Html = null,
    bool IsDeleted = false, IReadOnlyList<CapturedAttachment>? Attachments = null);

public sealed record CapturedThreadSnapshot(ResourceId Id, string Url, string Title,
    ThreadSourceState SourceState, DateTimeOffset CapturedAt, string MediaDirectory,
    IReadOnlyList<CapturedPost> Posts);

public sealed record ArchiveThreadRegistration(ResourceId Id, string Url, string Title,
    string MediaDirectory, bool CaptureEnabled, ThreadSourceState SourceState = ThreadSourceState.Active);

public sealed record ArchiveThreadSummary(ResourceId Id, string Url, string Title, string MediaDirectory,
    ThreadSourceState SourceState, bool CaptureEnabled, DateTimeOffset FirstSeenAt,
    DateTimeOffset? LastCapturedAt, int PostCount, long Revision);

public sealed record ArchiveThreadPage(IReadOnlyList<ArchiveThreadSummary> Items, int TotalCount, int Offset);

/// <summary>A complete immutable thread read transaction, with bounded access to its retained posts.</summary>
public sealed class ArchivedThreadView
{
    private readonly CapturedPost[] _posts;
    internal ArchivedThreadView(ArchiveThreadSummary summary, IEnumerable<CapturedPost> posts, IEnumerable<string>? mediaDirectories = null)
    {
        Summary = summary;
        MediaDirectories = Array.AsReadOnly((mediaDirectories ?? Array.Empty<string>()).ToArray());
        _posts = ArchivePostOrder.Sort(summary.Id, posts.Select(ArchiveCaptureMerge.CopyPost));
    }
    public ArchiveThreadSummary Summary { get; }
    public IReadOnlyList<string> MediaDirectories { get; }
    public int PostCount => _posts.Length;
    public IReadOnlyList<CapturedPost> GetPosts(int offset = 0, int limit = 100)
    {
        ArchiveValidation.ValidatePage(offset, limit);
        return Array.AsReadOnly(_posts.Skip(offset).Take(limit).ToArray());
    }
    /// <summary>Zero-based index in this immutable read, or -1 when the post was not captured.</summary>
    public int FindPostIndex(string postId)
    {
        ArgumentNullException.ThrowIfNull(postId);
        return Array.FindIndex(_posts, post => post.PostId == postId);
    }
    internal IReadOnlyList<CapturedPost> Posts => _posts;
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
        return next with { Posts = Array.AsReadOnly(ArchivePostOrder.Sort(next.Id, posts.Values)) };
    }

    public static CapturedPost MergePost(CapturedPost previous, CapturedPost next)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);
        if (previous.PostId != next.PostId) throw new ArgumentException("Posts must have the same identity.", nameof(next));
        bool Has(PostFieldPresence field) => (next.Fields & field) != 0;
        IReadOnlyList<CapturedAttachment> attachments = previous.Attachments ?? Array.Empty<CapturedAttachment>();
        if (Has(PostFieldPresence.Attachments))
        {
            Dictionary<int, CapturedAttachment> merged = attachments.ToDictionary(attachment => attachment.Position);
            foreach (CapturedAttachment attachment in next.Attachments ?? Array.Empty<CapturedAttachment>())
            {
                if (attachment.IsDeleted && attachment.MediaId == null && merged.TryGetValue(attachment.Position, out CapturedAttachment? old))
                    merged[attachment.Position] = old with { IsDeleted = true };
                else merged[attachment.Position] = attachment;
            }
            attachments = Array.AsReadOnly(merged.Values.OrderBy(attachment => attachment.Position).ToArray());
        }
        return new CapturedPost(next.PostId, next.Ordinal, previous.Fields | next.Fields,
            Has(PostFieldPresence.PostedAt) ? next.PostedAt : previous.PostedAt,
            Has(PostFieldPresence.Subject) ? next.Subject : previous.Subject,
            Has(PostFieldPresence.Author) ? next.Author : previous.Author,
            Has(PostFieldPresence.Html) ? next.Html : previous.Html,
            Has(PostFieldPresence.IsDeleted) ? next.IsDeleted : previous.IsDeleted,
            Array.AsReadOnly(attachments.ToArray()));
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
            Attachments = Array.AsReadOnly((Has(PostFieldPresence.Attachments)
                ? post.Attachments ?? Array.Empty<CapturedAttachment>() : Array.Empty<CapturedAttachment>()).ToArray())
        };
    }
}

internal static class ArchivePostOrder
{
    private static readonly IComparer<string> DecimalIdentityOrder = Comparer<string>.Create(CompareDecimalIdentity);

    internal static CapturedPost[] Sort(ResourceId id, IEnumerable<CapturedPost> posts)
    {
        CapturedPost[] items = posts.ToArray();
        // This is a source contract, not an assumption that every provider's numeric IDs
        // are chronological. Opaque/custom providers retain control through Ordinal.
        if ((id.Source is "4chan" or "8kun")
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

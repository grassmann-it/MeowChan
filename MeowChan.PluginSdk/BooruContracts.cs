using System.Text.Json.Serialization;

namespace MeowChan.PluginSdk;

/// <summary>Opaque provider post id. The same PostId on two sources identifies two different posts.</summary>
public sealed record SourcePostId(string SourceId, string PostId);

/// <summary>Variant roles are never interchangeable: a preview or sample never satisfies an original.</summary>
public enum BooruMediaRole { Original, Sample, Preview }

public enum BooruTagCategory { Unknown, General, Artist, Copyright, Character, Meta }

/// <summary>The provider's rating of a post, never the user's rating.</summary>
public enum BooruRating { Unknown, General, Sensitive, Questionable, Explicit }

/// <summary>
/// Absent field is not a null value is not an empty value: only a flagged field is an observation,
/// and an unflagged field must stay unset.
/// </summary>
[Flags]
public enum BooruFieldPresence
{
    None = 0, CreatedAt = 1, Rating = 2, Score = 4, FavoriteCount = 8, Tags = 16, Media = 32,
    SourceClaim = 64, IsDeleted = 128, IsBanned = 256,
    All = CreatedAt | Rating | Score | FavoriteCount | Tags | Media | SourceClaim | IsDeleted | IsBanned
}

/// <summary>Exact provider spelling of a provider tag, never a private tag.</summary>
public sealed record BooruTag(string Name, BooruTagCategory Category);

/// <summary>
/// Metadata only, all provider claims: no path, completion or download decision. Url null means the
/// provider does not offer this variant or restricts it. MD5 is base64 of 16 bytes.
/// </summary>
public sealed record BooruMediaVariant(BooruMediaRole Role, Uri? Url, string? Extension = null,
    long? ExpectedSize = null, int? Width = null, int? Height = null, string? ExpectedMd5 = null);

/// <summary>
/// A booru post is its own object, never a board or a thread. Values are provider claims.
/// SourceClaim is the provider's free-text "source" and stays inert text: never a fetch target.
/// </summary>
public sealed record BooruPost(SourcePostId Id, BooruFieldPresence Fields, Uri? PostUrl = null,
    DateTimeOffset? CreatedAt = null, BooruRating Rating = BooruRating.Unknown, string? RawRating = null,
    int? Score = null, int? FavoriteCount = null, IReadOnlyList<BooruTag>? Tags = null,
    IReadOnlyList<BooruMediaVariant>? Media = null, string? SourceClaim = null, bool IsDeleted = false,
    bool IsBanned = false);

/// <summary>Query is the already host-validated provider query and never contains private tags. Page is 1-based.</summary>
public sealed record BooruSearchRequest(string SourceId, string Query, int Page, int Limit);

/// <summary>IsEnd states that the provider reported no further page, never that the site was fully observed.</summary>
public sealed record BooruSearchPage(string SourceId, string Query, int Page, IReadOnlyList<BooruPost> Posts,
    bool IsEnd);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(BooruSearchSnapshot), "snapshot")]
[JsonDerivedType(typeof(BooruSearchDeferred), "deferred")]
[JsonDerivedType(typeof(BooruSearchFailure), "failure")]
public abstract record BooruSearchResult;

public sealed record BooruSearchSnapshot(BooruSearchPage Page) : BooruSearchResult;
public sealed record BooruSearchDeferred(string SourceId, DateTimeOffset RetryAt, string ReasonCode,
    int? HttpStatusCode = null) : BooruSearchResult;
public sealed record BooruSearchFailure(string SourceId, FailureDetail Failure) : BooruSearchResult;

public sealed record BooruPostRequest(SourcePostId Post);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(BooruPostSnapshot), "snapshot")]
[JsonDerivedType(typeof(BooruPostNotFound), "notFound")]
[JsonDerivedType(typeof(BooruPostDeferred), "deferred")]
[JsonDerivedType(typeof(BooruPostFailure), "failure")]
public abstract record BooruPostResult;

public sealed record BooruPostSnapshot(BooruPost Post) : BooruPostResult;
public sealed record BooruPostNotFound(SourcePostId Post, int? HttpStatusCode = null) : BooruPostResult;
public sealed record BooruPostDeferred(SourcePostId Post, DateTimeOffset RetryAt, string ReasonCode,
    int? HttpStatusCode = null) : BooruPostResult;
public sealed record BooruPostFailure(SourcePostId Post, FailureDetail Failure) : BooruPostResult;

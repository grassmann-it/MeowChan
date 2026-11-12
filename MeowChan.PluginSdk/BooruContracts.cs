using System.Text.Json.Serialization;

namespace MeowChan.PluginSdk;

/// <summary>Opaque provider post id. The same PostId on two sources identifies two different posts.</summary>
public sealed record BooruPostId(string SourceId, string PostId);

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
public sealed record BooruPost(BooruPostId Id, BooruFieldPresence Fields, Uri? PostUrl = null,
    DateTimeOffset? CreatedAt = null, BooruRating Rating = BooruRating.Unknown, string? RawRating = null,
    int? Score = null, int? FavoriteCount = null, IReadOnlyList<BooruTag>? Tags = null,
    IReadOnlyList<BooruMediaVariant>? Media = null, string? SourceClaim = null, bool IsDeleted = false,
    bool IsBanned = false);

/// <summary>
/// Query is the already host-validated provider query and never contains private tags. Page is 1-based.
/// BeforePostId is the optional cursor: "the posts of this query with an id lower than BeforePostId",
/// newest first. A cursor request uses Page 1; the cursor alone selects the position.
/// </summary>
/// <remarks>
/// Only a source that declares <see cref="PostOrderKind.DecimalPostId"/> receives a cursor, so a plugin that
/// does not know the field never sees one. The value is a post id the host already received from the same
/// source: a positive decimal without leading zeros. Plugins validate it and refuse one they cannot serve.
/// </remarks>
public sealed record BooruSearchRequest(string SourceId, string Query, int Page, int Limit,
    string? BeforePostId = null);

/// <summary>
/// IsEnd states that the provider reported no further page, never that the site was fully observed.
/// NextBeforePostId is the cursor that continues this query below this page: the lowest post id of the
/// page. A decimal-ordered source names it on every nonempty page that is not final; a final or empty
/// page and a source without decimal order have none.
/// </summary>
public sealed record BooruSearchPage(string SourceId, string Query, int Page, IReadOnlyList<BooruPost> Posts,
    bool IsEnd, string? NextBeforePostId = null);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(BooruSearchSnapshot), "snapshot")]
[JsonDerivedType(typeof(BooruSearchDeferred), "deferred")]
[JsonDerivedType(typeof(BooruSearchFailure), "failure")]
public abstract record BooruSearchResult;

public sealed record BooruSearchSnapshot(BooruSearchPage Page) : BooruSearchResult;
public sealed record BooruSearchDeferred(string SourceId, DateTimeOffset RetryAt, string ReasonCode,
    int? HttpStatusCode = null) : BooruSearchResult;
public sealed record BooruSearchFailure(string SourceId, FailureDetail Failure) : BooruSearchResult;

public sealed record BooruPostRequest(BooruPostId Post);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(BooruPostSnapshot), "snapshot")]
[JsonDerivedType(typeof(BooruPostNotFound), "notFound")]
[JsonDerivedType(typeof(BooruPostDeferred), "deferred")]
[JsonDerivedType(typeof(BooruPostFailure), "failure")]
public abstract record BooruPostResult;

public sealed record BooruPostSnapshot(BooruPost Post) : BooruPostResult;
public sealed record BooruPostNotFound(BooruPostId Post, int? HttpStatusCode = null) : BooruPostResult;
public sealed record BooruPostDeferred(BooruPostId Post, DateTimeOffset RetryAt, string ReasonCode,
    int? HttpStatusCode = null) : BooruPostResult;
public sealed record BooruPostFailure(BooruPostId Post, FailureDetail Failure) : BooruPostResult;

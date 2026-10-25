using System.Text.Json.Serialization;

namespace MeowChan.PluginSdk;

public sealed record SourceBoardId(string SourceId, string BoardId);
public sealed record SourceThreadId(string SourceId, string BoardId, string ThreadId);
public sealed record SourceMediaId(SourceThreadId Thread, string MediaId, string Variant = "original");

public sealed record SourceValidators(string? EntityTag = null, DateTimeOffset? LastModified = null,
    string? Revision = null);

public sealed record CatalogReadRequest(SourceBoardId Board, string? Cursor = null, int? Page = null,
    SourceValidators? Validators = null, bool HasSnapshot = false);

public sealed record ThreadReadRequest(SourceThreadId Thread, SourceValidators? Validators = null,
    bool HasSnapshot = false);

public sealed record SourceThreadReference(SourceThreadId Id, Uri Url, string? Title = null,
    string? OpeningText = null, SourceTextFormat OpeningTextFormat = SourceTextFormat.PlainText,
    DateTimeOffset? CreatedAt = null, int? ReplyCount = null, int? AttachmentCount = null);

/// <summary>IsComplete describes this board observation, not whether omitted threads have ended.</summary>
public sealed record SourceCatalogSnapshot(SourceBoardId Board, IReadOnlyList<SourceThreadReference> Threads,
    bool IsComplete, string? NextCursor = null, int? NextPage = null, SourceValidators? Validators = null);

public enum SourceThreadState { Active, Archived }
public enum SourceTextFormat { PlainText, Html }

[Flags]
public enum SourceFieldPresence
{
    None = 0, PostedAt = 1, Subject = 2, Author = 4, Text = 8, IsDeleted = 16,
    Attachments = 32, All = PostedAt | Subject | Author | Text | IsDeleted | Attachments
}

/// <summary>
/// Missing posts or fields do not delete earlier captures. Title null means not supplied.
/// Archived includes the final available posts; the host owns capture time and lifecycle.
/// </summary>
public sealed record SourceThreadSnapshot(SourceThreadId Thread, Uri Url, string? Title,
    SourceThreadState SourceState, IReadOnlyList<SourcePost> Posts, SourceValidators? Validators = null);

/// <summary>
/// Ordinal is a stable nonnegative order hint, not the index in the latest partial response.
/// DecimalPostId sources are ordered by arbitrary-length decimal post identity instead.
/// Text is inert source content with an explicit format; the host sanitizes rendered HTML.
/// </summary>
public sealed record SourcePost(string PostId, long Ordinal, SourceFieldPresence Fields,
    DateTimeOffset? PostedAt = null, string? Subject = null, string? Author = null,
    string? Text = null, SourceTextFormat TextFormat = SourceTextFormat.Html, bool IsDeleted = false,
    IReadOnlyList<SourceAttachment>? Attachments = null);

/// <summary>
/// Metadata only: no path, completion or watch decision. A deleted attachment without MediaId
/// marks the previously captured position and never requests removal of saved bytes.
/// MD5 is base64 of 16 bytes; SHA-256 is 64 hexadecimal characters.
/// </summary>
public sealed record SourceAttachment(int Position, string? MediaId, string Variant = "original",
    Uri? Url = null, string? OriginalFileName = null, string? Extension = null,
    long? ExpectedSize = null, string? ExpectedMd5 = null, string? ExpectedSha256 = null,
    string? MimeType = null, bool IsDeleted = false)
{
    /// <summary>Provider checksum, base64 of exactly 20 bytes. Never a local content identity.</summary>
    public string? ExpectedSha1 { get; init; }
    /// <summary>A non-authoritative provider size annotation, never an exact byte expectation.</summary>
    public SourceSizeHint? ProviderSizeHint { get; init; }
}

public enum SourceSizePrecision { Unspecified = 0, Rounded = 1, Truncated = 2 }
/// <summary>Raw numeric text, original provider unit label and explicitly stated precision.</summary>
public sealed record SourceSizeHint(string RawValue, string UnitLabel,
    SourceSizePrecision Precision = SourceSizePrecision.Unspecified);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CatalogSnapshot), "snapshot")]
[JsonDerivedType(typeof(CatalogUnchanged), "unchanged")]
[JsonDerivedType(typeof(CatalogDeferred), "deferred")]
[JsonDerivedType(typeof(CatalogFailure), "failure")]
public abstract record CatalogReadResult;

public sealed record CatalogSnapshot(SourceCatalogSnapshot Snapshot) : CatalogReadResult;
public sealed record CatalogUnchanged(SourceBoardId Board, SourceValidators Validators) : CatalogReadResult;
public sealed record CatalogDeferred(SourceBoardId Board, DateTimeOffset RetryAt, string ReasonCode,
    int? HttpStatusCode = null) : CatalogReadResult;
public sealed record CatalogFailure(SourceBoardId Board, FailureDetail Failure) : CatalogReadResult;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ThreadSnapshot), "snapshot")]
[JsonDerivedType(typeof(ThreadUnchanged), "unchanged")]
[JsonDerivedType(typeof(ThreadNotFound), "notFound")]
[JsonDerivedType(typeof(ThreadDeferred), "deferred")]
[JsonDerivedType(typeof(ThreadFailure), "failure")]
public abstract record ThreadReadResult;

public sealed record ThreadSnapshot(SourceThreadSnapshot Snapshot) : ThreadReadResult;
public sealed record ThreadUnchanged(SourceThreadId Thread, SourceValidators Validators) : ThreadReadResult;
public sealed record ThreadNotFound(SourceThreadId Thread, int? HttpStatusCode = null) : ThreadReadResult;
public sealed record ThreadDeferred(SourceThreadId Thread, DateTimeOffset RetryAt, string ReasonCode,
    int? HttpStatusCode = null) : ThreadReadResult;
public sealed record ThreadFailure(SourceThreadId Thread, FailureDetail Failure) : ThreadReadResult;

public sealed record FailureDetail(string Code, string Message, int? HttpStatusCode = null,
    DateTimeOffset? RetryAt = null);

using System;
using System.Collections.Generic;
using System.Globalization;
using MeowChan.Archive;
using MeowChan.Booru;

namespace MeowChan.Desktop.Localization;

/// <summary>
/// Maps the stable machine codes of the booru host, plugin and domain code to catalog texts
/// (<see cref="BooruStatusCatalog"/>). The English diagnostic message of a failure is never shown to the
/// user. An unknown code still produces a catalog text (with the code appended), never a raw message.
/// </summary>
internal static class BooruMessages
{
    private static readonly IReadOnlyDictionary<string, string> FailureKeys = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["invalid_query"] = "booru.failure.invalid_query",
        ["invalid_page"] = "booru.failure.invalid_page",
        ["capability"] = "booru.failure.capability",
        ["booru-source"] = "booru.failure.booru-source",
        ["booru-job"] = "booru.failure.booru-job",
        ["http-request"] = "booru.failure.http-request",
        ["booru-budget"] = "booru.failure.booru-budget",
        ["http-redirect"] = "booru.failure.http-redirect",
        ["redirect"] = "booru.failure.http-redirect",
        ["http-timeout"] = "booru.failure.http-timeout",
        ["metadata-too-large"] = "booru.failure.metadata-too-large",
        ["http-encoding"] = "booru.failure.http-encoding",
        ["http-network"] = "booru.failure.http-network",
        ["invalid-request"] = "booru.failure.invalid-request",
        ["post-id"] = "booru.failure.postId",
        ["post_id"] = "booru.failure.postId",
        ["invalid_post"] = "booru.failure.postId",
        ["wrong-post"] = "booru.failure.wrong-post",
        ["malformed"] = "booru.failure.malformed",
        ["too-large"] = "booru.failure.too-large",
        ["not-json"] = "booru.failure.not-json",
        ["not-found"] = "booru.failure.not-found",
        ["http-410"] = "booru.failure.http-410",
        ["page-limit"] = "booru.failure.page-limit",
        ["access-denied"] = "booru.failure.access-denied",
        ["too-many-posts"] = "booru.failure.too-many-posts",
        ["invalid-cursor"] = "booru.failure.invalid-cursor",
        ["invalid_cursor"] = "booru.failure.invalid-cursor",
        ["unexpected-order"] = "booru.failure.unexpected-order"
    };

    private static readonly IReadOnlyDictionary<string, string> DeferredKeys = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["booru-cooldown"] = "booru.deferred.booru-cooldown",
        ["provider-throttled"] = "booru.deferred.provider-throttled",
        ["provider-unavailable"] = "booru.deferred.provider-unavailable"
    };

    /// <summary>Text for a <c>FailureDetail.Code</c>. Unlisted <c>http-NNN</c> codes show their status.</summary>
    internal static string Failure(string? code)
    {
        if (code is not null && FailureKeys.TryGetValue(code, out string? key)) return Texts.T(key);
        if (code is not null && code.StartsWith("http-", StringComparison.Ordinal)
            && int.TryParse(code.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out int status))
            return Texts.T("booru.failure.httpStatus", status);
        return Texts.T("booru.failure.unknown", string.IsNullOrWhiteSpace(code) ? "?" : code);
    }

    /// <summary>Text for a postponed request (<c>ReasonCode</c> plus the time of the next possible attempt).</summary>
    internal static string Deferred(string? reasonCode, DateTimeOffset retryAt)
    {
        string key = reasonCode is not null && DeferredKeys.TryGetValue(reasonCode, out string? known) ? known : "booru.deferred.unknown";
        return Texts.T(key, retryAt.ToLocalTime());
    }

    internal static string WatcherState(DanbooruWatcherState state) => Named("booru.watcher.state.", state, "booru.watcher.state.Unknown");

    /// <summary>Text for a watcher transition rejection code or a stored watcher error code.</summary>
    internal static string WatcherProblem(string? code)
    {
        string key = "booru.watcher.problem." + code;
        if (!string.IsNullOrEmpty(code) && BuiltInCatalog.English.ContainsKey(key) && code != "unknown") return Texts.T(key);
        if (code is "provider-throttled" or "provider-unavailable" or "booru-cooldown") return Failure(code);
        return Texts.T("booru.watcher.problem.unknown", string.IsNullOrWhiteSpace(code) ? "?" : code);
    }

    internal static string WatcherCreate(DanbooruWatcherCreateError error) =>
        error == DanbooruWatcherCreateError.None ? string.Empty : Named("booru.watcher.create.", error, "booru.watcher.create.unknown");

    internal static string QueryProblem(DanbooruTagQueryError error) => error switch
    {
        DanbooruTagQueryError.None => string.Empty,
        DanbooruTagQueryError.NullInput => Texts.T("booru.query.problem.Empty"),
        _ => Named("booru.query.problem.", error, "booru.failure.invalid_query")
    };

    internal static string PostReferenceProblem(DanbooruPostReferenceError error) =>
        error == DanbooruPostReferenceError.None ? string.Empty : Named("booru.post.problem.", error, "booru.post.problem.unknown");

    internal static string OriginalProblem(BooruOriginalSelectionError error) =>
        error == BooruOriginalSelectionError.None ? string.Empty : Named("booru.original.problem.", error, "booru.original.problem.unknown");

    /// <summary>Provider rating label. Works for the SDK and the archive rating enums (same member names).</summary>
    internal static string Rating<TRating>(TRating rating) where TRating : struct, Enum => Named("booru.rating.", rating, "booru.rating.Unknown");

    internal static string TagCategory<TCategory>(TCategory category) where TCategory : struct, Enum =>
        Named("booru.tagCategory.", category, "booru.tagCategory.Unknown");

    internal static string MediaRole<TRole>(TRole role) where TRole : struct, Enum => Named("booru.mediaRole.", role, "booru.mediaRole.Other");

    internal static string Finding(BooruFindingKind kind) => Named("booru.finding.", kind, "booru.field.absent");

    private static string Named<TEnum>(string prefix, TEnum value, string fallbackKey) where TEnum : struct, Enum
    {
        string key = prefix + value.ToString();
        if (BuiltInCatalog.English.ContainsKey(key)) return Texts.T(key);
        return Texts.T(fallbackKey, value.ToString());
    }
}

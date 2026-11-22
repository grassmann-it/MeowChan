namespace MeowChan.PluginSdk;

/// <summary>Which typed booru request an endpoint serves.</summary>
public enum BooruEndpointKind { Search, Post }

/// <summary>
/// Value kind of a declared query parameter. The host checks every value of an outgoing metadata request
/// against its kind before the request is sent; a parameter that is not declared is refused.
/// </summary>
public enum BooruParameterKind
{
    /// <summary>Exactly <see cref="BooruQueryParameter.FixedValue"/>.</summary>
    Fixed,
    /// <summary>The host-validated provider query, optionally followed by the declared cursor term.</summary>
    TagQuery,
    /// <summary>A 1-based page number up to the profile's maximum page.</summary>
    PageNumber,
    /// <summary>A 0-based page index (engines that count pages from zero), below the profile's maximum page.</summary>
    PageIndex,
    /// <summary>A 1-based page number, or <c>b</c> followed by a post id (the posts below that id).</summary>
    PageOrBeforeCursor,
    /// <summary>The page size, from 1 to the profile's maximum limit.</summary>
    Limit,
    /// <summary>A positive decimal post id without leading zeros, at most 18 digits.</summary>
    PostId
}

/// <summary>One declared query parameter of a booru endpoint. Names are lower-case ASCII.</summary>
public sealed record BooruQueryParameter(string Name, BooruParameterKind Kind, bool Required = false,
    string? FixedValue = null);

/// <summary>
/// One metadata endpoint on the source's declared metadata hosts. <c>PathTemplate</c> is an exact absolute path;
/// a post endpoint may name the post id once as <c>{post}</c> inside the path instead of a
/// <see cref="BooruParameterKind.PostId"/> parameter.
/// </summary>
public sealed record BooruEndpoint(BooruEndpointKind Kind, string PathTemplate,
    IReadOnlyList<BooruQueryParameter> Parameters);

/// <summary>
/// How the source is accessed. Only public, anonymous access exists: credentials are a host decision that is
/// not part of this SDK version, and a plugin never sends them itself.
/// </summary>
public enum BooruAuthentication { None }

/// <summary>
/// Restricted literal tag syntax of a source: whitespace-separated tag terms, an optional leading <c>-</c> for an
/// exclusion, at most one <c>rating:</c> term with one of <c>RatingValues</c>, and, for a source that pages by a
/// cursor term, one <c>BeforeCursorPrefix</c> term (for example <c>id:&lt;</c>) that only the host adds.
/// Other meta terms, boolean or grouped expressions and wildcards are refused.
/// </summary>
public sealed record BooruQueryRules(int MaximumTagTerms, IReadOnlyList<string> RatingValues,
    bool AllowsExclusion = true, string? BeforeCursorPrefix = null);

/// <summary>
/// Declarative post-search profile of one source (SDK 1.1.0, additive): the only metadata endpoints and query
/// parameters the host sends for it, its tag syntax, which tag categories and ratings the provider reports, its
/// paging limits, its access mode and the minimum spacing between two request starts. The host narrows every
/// value further by its own limits; the profile never widens the declared hosts.
/// </summary>
public sealed record BooruSourceProfile(IReadOnlyList<BooruEndpoint> Endpoints, BooruQueryRules Query,
    IReadOnlyList<BooruTagCategory> TagCategories, IReadOnlyList<BooruRating> Ratings,
    int MaximumPage, int MaximumLimit, BooruAuthentication Authentication = BooruAuthentication.None,
    int MinimumRequestSpacingMilliseconds = 1000);

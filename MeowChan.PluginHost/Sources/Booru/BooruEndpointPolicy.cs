using System.Globalization;
using System.Text;
using MeowChan.Booru;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Sources.Booru;

/// <summary>
/// The effective post-search profile of a source and the host's request checks derived from it. The profile comes
/// from the plugin's source declaration (<see cref="SourceDescription.BooruProfile"/>), except for Danbooru, whose
/// profile the host owns and applies unchanged (a declared Danbooru profile is ignored). A post-search source
/// without a valid profile has no endpoint at all: every request of it is refused before the transport.
/// </summary>
internal static class BooruEndpointPolicy
{
    /// <summary>Host ceilings: a profile can only lower them.</summary>
    internal const int HostMaximumPage = DanbooruTagQuery.MaximumSearchPages;
    internal const int HostMaximumLimit = DanbooruTagQuery.MaximumSearchPageSize;
    internal const int MaximumQueryUtf8Bytes = 1024;
    internal const int MaximumTermUtf8Bytes = 256;
    /// <summary>Danbooru post ids fit a signed 64-bit integer: at most 18 canonical decimal digits are accepted.</summary>
    internal const int MaximumPostIdDigits = SdkLimits.MaximumBooruPostIdDigits;

    /// <summary>The host-owned Danbooru profile: exactly the endpoints and parameters accepted before profiles existed.</summary>
    internal static readonly BooruSourceProfile Danbooru = new(
        [
            new(BooruEndpointKind.Search, "/posts.json",
            [
                new("tags", BooruParameterKind.TagQuery),
                new("page", BooruParameterKind.PageOrBeforeCursor),
                new("limit", BooruParameterKind.Limit)
            ]),
            new(BooruEndpointKind.Post, "/posts/{post}.json", [])
        ],
        new BooruQueryRules(DanbooruTagQuery.MaximumTagTerms, ["g", "general", "s", "sensitive", "q", "questionable", "e", "explicit"]),
        [BooruTagCategory.General, BooruTagCategory.Artist, BooruTagCategory.Copyright, BooruTagCategory.Character, BooruTagCategory.Meta],
        [BooruRating.General, BooruRating.Sensitive, BooruRating.Questionable, BooruRating.Explicit],
        DanbooruTagQuery.MaximumSearchPages, DanbooruTagQuery.MaximumSearchPageSize);

    internal static bool IsDanbooru(string? sourceId) => string.Equals(sourceId, DanbooruPostReference.ProviderId, StringComparison.Ordinal);

    /// <summary>The profile the host enforces for this source, or null when it has none (fail closed).</summary>
    internal static BooruSourceProfile? Resolve(SourceDescription source)
    {
        if (source is null || !source.Capabilities.HasFlag(SourceCapabilities.PostSearch)) return null;
        if (IsDanbooru(source.SourceId)) return Danbooru;
        if (source.BooruProfile is null || source.BooruProfile.Authentication != BooruAuthentication.None) return null;
        return SdkValidation.ValidateBooruProfile(source).Count == 0 ? source.BooruProfile : null;
    }

    internal static int MaximumPage(BooruSourceProfile profile) => Math.Min(profile.MaximumPage, HostMaximumPage);
    internal static int MaximumLimit(BooruSourceProfile profile) => Math.Min(profile.MaximumLimit, HostMaximumLimit);

    /// <summary>The spacing between two request starts of the source: the profile's, never below the host floor.</summary>
    internal static TimeSpan Spacing(BooruSourceProfile? profile, TimeSpan floor)
    {
        TimeSpan declared = TimeSpan.FromMilliseconds(profile?.MinimumRequestSpacingMilliseconds ?? 0);
        return declared > floor ? declared : floor;
    }

    internal static bool IsCanonicalPostId(string? id) =>
        id is { Length: > 0 and <= MaximumPostIdDigits } && id[0] != '0' && id.All(char.IsAsciiDigit);

    /// <summary>
    /// Validates the user's explicit provider query for a search and returns the separator-normalized form the
    /// plugin receives. Danbooru keeps its own restricted syntax; every other source uses its declared rules.
    /// A cursor term is never accepted from the user: only the plugin adds it for a host-issued cursor.
    /// </summary>
    internal static bool TryNormalizeQuery(string sourceId, BooruSourceProfile profile, string? userQuery, out string providerQuery)
    {
        providerQuery = string.Empty;
        if (userQuery is null) return false;
        if (IsDanbooru(sourceId))
        {
            if (!DanbooruTagQuery.TryParse(userQuery, out DanbooruTagQuery? parsed, out _) || parsed is null) return false;
            providerQuery = parsed.ProviderQuery;
            return true;
        }
        if (!TryTerms(userQuery, profile.Query, allowCursor: false, out List<string>? terms)) return false;
        providerQuery = string.Join(" ", terms!);
        return true;
    }

    internal static string? SourceOf(object? operation) => operation switch
    {
        BooruSearchRequest search => search.SourceId,
        BooruPostRequest post => post.Post.SourceId,
        _ => null
    };

    // The worker's URL must describe the same operation. Send only the URI constructed
    // from the host's request and source profile, even if the worker orders the fields differently.
    internal static HostHttpRequest? Bind(HostHttpRequest request, SourceDescription source, object operation)
    {
        if (request.Method != HostHttpMethod.Get || request.SourceId != SourceOf(operation)
            || request.SourceId != source.SourceId || Resolve(source) is not BooruSourceProfile profile
            || !IsAllowed(request.Url, source, profile))
            return null;

        BooruSearchRequest? search = operation as BooruSearchRequest;
        BooruPostRequest? post = operation as BooruPostRequest;
        string? before = search?.BeforePostId;
        BooruEndpoint endpoint = profile.Endpoints.Single(value =>
            value.Kind == (search is not null ? BooruEndpointKind.Search : BooruEndpointKind.Post));
        if (search is not null && (!TryNormalizeQuery(source.SourceId, profile, search.Query, out string normalized)
                || normalized != search.Query
                || search.Page > 1 && !endpoint.Parameters.Any(value => IsPageParameter(value.Kind))
                || before is not null && (source.PostOrder != PostOrderKind.DecimalPostId
                    || !IsCanonicalPostId(before) || profile.Query.BeforeCursorPrefix is null
                        && !endpoint.Parameters.Any(value => value.Kind == BooruParameterKind.PageOrBeforeCursor))))
            return null;
        if (post is not null && !IsCanonicalPostId(post.Post.PostId)) return null;

        string path = post is null ? endpoint.PathTemplate
            : endpoint.PathTemplate.Replace("{post}", post.Post.PostId, StringComparison.Ordinal);
        List<string> parameters = [];
        foreach (BooruQueryParameter parameter in endpoint.Parameters)
        {
            string value = parameter.Kind switch
            {
                BooruParameterKind.Fixed => parameter.FixedValue!,
                BooruParameterKind.TagQuery => before is not null && profile.Query.BeforeCursorPrefix is string prefix
                    ? search!.Query + " " + prefix + before : search!.Query,
                BooruParameterKind.PageNumber => search!.Page.ToString(CultureInfo.InvariantCulture),
                BooruParameterKind.PageIndex => (search!.Page - 1).ToString(CultureInfo.InvariantCulture),
                BooruParameterKind.PageOrBeforeCursor => before is null ? search!.Page.ToString(CultureInfo.InvariantCulture) : "b" + before,
                BooruParameterKind.Limit => search!.Limit.ToString(CultureInfo.InvariantCulture),
                BooruParameterKind.PostId => post!.Post.PostId,
                _ => throw new InvalidOperationException("An undeclared booru parameter was selected.")
            };
            parameters.Add(parameter.Name + "=" + Uri.EscapeDataString(value));
        }

        // BaseUri selects the metadata origin. A source with a separate website may
        // use its sole metadata host as the canonical API origin.
        string[] metadataHosts = source.HttpHosts.Where(host => host.Purpose.HasFlag(HttpHostPurpose.Metadata))
            .Select(host => host.Host).ToArray();
        string? host = metadataHosts.FirstOrDefault(value => value.Equals(source.BaseUri.IdnHost, StringComparison.OrdinalIgnoreCase))
            ?? (metadataHosts.Length == 1 ? metadataHosts[0] : null);
        if (host is null || !Uri.TryCreate("https://" + host + path
                + (parameters.Count == 0 ? "" : "?" + string.Join("&", parameters)), UriKind.Absolute, out Uri? canonical)
            || !IsAllowed(canonical, source, profile)
            || !request.Url.IdnHost.Equals(canonical.IdnHost, StringComparison.OrdinalIgnoreCase)
            || request.Url.AbsolutePath != canonical.AbsolutePath
            || !EquivalentQuery(request.Url.Query, canonical.Query))
            return null;
        return request with { Url = canonical };
    }

    internal static HostHttpFailure OperationDenied() =>
        new(new FailureDetail("http-request", "The HTTP request does not match the active booru operation."));

    private static bool IsPageParameter(BooruParameterKind kind) => kind is BooruParameterKind.PageNumber
        or BooruParameterKind.PageIndex or BooruParameterKind.PageOrBeforeCursor;

    private static bool EquivalentQuery(string submitted, string expected) =>
        submitted.Split('&').Select(Decode).Order(StringComparer.Ordinal)
            .SequenceEqual(expected.Split('&').Select(Decode).Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static string Decode(string parameter) => Uri.UnescapeDataString(parameter.TrimStart('?'));

    /// <summary>Whether <paramref name="uri"/> is one of the profile's endpoints with only declared, valid parameters.</summary>
    internal static bool IsAllowed(Uri uri, SourceDescription source, BooruSourceProfile? profile)
    {
        if (profile is null || uri is null) return false;
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0
            || uri.Fragment.Length != 0 || !uri.IsDefaultPort) return false;
        string path = uri.AbsolutePath;
        foreach (BooruEndpoint endpoint in profile.Endpoints)
            if (MatchesPath(endpoint.PathTemplate, path) && IsValidQuery(uri.Query, endpoint, source.SourceId, profile))
                return true;
        return false;
    }

    private static bool MatchesPath(string template, string path)
    {
        int placeholder = template.IndexOf("{post}", StringComparison.Ordinal);
        if (placeholder < 0) return string.Equals(template, path, StringComparison.Ordinal);
        string prefix = template[..placeholder], suffix = template[(placeholder + 6)..];
        return path.Length > prefix.Length + suffix.Length && path.StartsWith(prefix, StringComparison.Ordinal)
            && path.EndsWith(suffix, StringComparison.Ordinal) && IsCanonicalPostId(path[prefix.Length..^suffix.Length]);
    }

    private static bool IsValidQuery(string query, BooruEndpoint endpoint, string sourceId, BooruSourceProfile profile)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        if (query.Length > 0)
        {
            foreach (string part in query[1..].Split('&'))
            {
                int equals = part.IndexOf('=');
                if (equals <= 0) return false;
                string name, value, raw = part[(equals + 1)..];
                try
                {
                    name = Uri.UnescapeDataString(part[..equals]);
                    value = Uri.UnescapeDataString(raw);
                }
                catch (UriFormatException) { return false; }
                if (!names.Add(name)) return false;
                BooruQueryParameter? parameter = endpoint.Parameters.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal));
                if (parameter is null || !IsValidValue(parameter, raw, value, sourceId, profile)) return false;
            }
        }
        return endpoint.Parameters.All(parameter => (!parameter.Required && (IsDanbooru(sourceId)
            || parameter.Kind is not (BooruParameterKind.Fixed or BooruParameterKind.TagQuery or BooruParameterKind.Limit or BooruParameterKind.PostId)))
            || names.Contains(parameter.Name));
    }

    private static bool IsValidValue(BooruQueryParameter parameter, string raw, string value, string sourceId, BooruSourceProfile profile)
    {
        switch (parameter.Kind)
        {
            case BooruParameterKind.Fixed:
                return string.Equals(raw, parameter.FixedValue, StringComparison.Ordinal);
            case BooruParameterKind.TagQuery:
                return IsDanbooru(sourceId)
                    ? DanbooruTagQuery.TryParse(value, out _, out _)
                    : TryTerms(value, profile.Query, allowCursor: true, out _);
            case BooruParameterKind.PageNumber:
                return TryInteger(value, out int page) && page >= 1 && page <= MaximumPage(profile);
            case BooruParameterKind.PageIndex:
                return TryInteger(value, out int index) && index >= 0 && index < MaximumPage(profile);
            case BooruParameterKind.PageOrBeforeCursor:
                // Either a page number or the before-id cursor "b<post id>" (posts below that id), checked on the raw
                // text so no escaped form slips through.
                if (IsBeforeIdPage(raw)) return true;
                return TryInteger(value, out int numbered) && numbered >= 1 && numbered <= MaximumPage(profile);
            case BooruParameterKind.Limit:
                return TryInteger(value, out int limit) && limit >= 1 && limit <= MaximumLimit(profile);
            case BooruParameterKind.PostId:
                return IsCanonicalPostId(raw);
            default:
                return false;
        }
    }

    private static bool TryInteger(string value, out int number)
    {
        number = 0;
        return value.Length > 0 && (value.Length == 1 || value[0] != '0')
            && value.AsSpan().IndexOfAnyExceptInRange('0', '9') < 0
            && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    /// <summary>Exactly <c>b</c> followed by a canonical post id (1 to 18 digits, no leading zero).</summary>
    private static bool IsBeforeIdPage(string raw) =>
        raw.Length is >= 2 and <= MaximumPostIdDigits + 1 && raw[0] == 'b' && raw[1] is >= '1' and <= '9'
        && raw.AsSpan(2).IndexOfAnyExceptInRange('0', '9') < 0;

    /// <summary>
    /// The declared literal tag syntax. Only ASCII whitespace separates terms; controls, invisible formatting and
    /// other whitespace refuse the query instead of vanishing. Rejected text is never echoed.
    /// </summary>
    internal static bool TryTerms(string value, BooruQueryRules rules, bool allowCursor, out List<string>? terms)
    {
        terms = null;
        if (rules is null || value is null || value.Length > MaximumQueryUtf8Bytes) return false;
        int bytes = 0;
        for (int offset = 0; offset < value.Length;)
        {
            if (Rune.DecodeFromUtf16(value.AsSpan(offset), out Rune rune, out int consumed) != System.Buffers.OperationStatus.Done) return false;
            bool separator = rune.Value is ' ' or '\t' or '\r' or '\n';
            if (!separator && (Rune.IsWhiteSpace(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format)) return false;
            bytes += rune.Utf8SequenceLength;
            offset += consumed;
        }
        if (bytes > MaximumQueryUtf8Bytes) return false;

        List<string> result = new();
        int tags = 0;
        bool rating = false, cursor = false;
        foreach (string term in value.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (Encoding.UTF8.GetByteCount(term) > MaximumTermUtf8Bytes) return false;
            string? prefix = rules.BeforeCursorPrefix;
            if (allowCursor && prefix is not null && term.StartsWith(prefix, StringComparison.Ordinal))
            {
                if (cursor || !IsCanonicalPostId(term[prefix.Length..])) return false;
                cursor = true;
            }
            else
            {
                bool negative = term[0] == '-';
                string body = negative ? term[1..] : term;
                if (body.Length == 0 || body[0] == '-' || negative && !rules.AllowsExclusion) return false;
                if (body.Equals("or", StringComparison.OrdinalIgnoreCase) || body.Equals("and", StringComparison.OrdinalIgnoreCase)
                    || body.Equals("not", StringComparison.OrdinalIgnoreCase)) return false;
                int colon = body.IndexOf(':');
                if (colon >= 0)
                {
                    if (!body[..colon].Equals("rating", StringComparison.Ordinal) || rating
                        || !rules.RatingValues.Contains(body[(colon + 1)..], StringComparer.Ordinal)) return false;
                    rating = true;
                }
                else
                {
                    foreach (Rune rune in body.EnumerateRunes())
                        if (!Rune.IsLetterOrDigit(rune) && Rune.GetUnicodeCategory(rune) is not
                                (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                            && rune.Value is not ('_' or '-' or '.'))
                            return false;
                    if (++tags > Math.Min(rules.MaximumTagTerms, 8)) return false;
                }
            }
            result.Add(term);
        }
        if (result.Count == 0) return false;
        terms = result;
        return true;
    }
}

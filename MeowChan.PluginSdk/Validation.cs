using System.Net;

namespace MeowChan.PluginSdk;

public sealed record ValidationIssue(string Path, string Code, string Message);

/// <summary>
/// Pure validation with no registration, HTTP, file access or lifecycle effects. Validate
/// every worker response against its expected request before passing it to host services.
/// Cross-plugin conflicts are reported separately; ordering never selects a winner.
/// </summary>
public static class SdkValidation
{
    private const int MaximumAggregateSources = 256;
    private const int MaximumAggregateRoutes = 1024;

    public static IReadOnlyList<ValidationIssue> ValidateDescription(PluginDescription description)
    {
        Checks checks = new Checks();
        checks.Description(description, "plugin");
        return checks.Issues;
    }

    public static IReadOnlyList<ValidationIssue> ValidateDescriptions(IReadOnlyList<PluginDescription> descriptions)
    {
        Checks checks = new Checks();
        HashSet<string> plugins = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> sources = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, List<(string SourceId, RouteShape Shape)>> routes = new Dictionary<string, List<(string, RouteShape)>>(StringComparer.OrdinalIgnoreCase);
        int totalSources = 0;
        int totalRoutes = 0;
        bool budgetExceeded = false;
        checks.List(descriptions, "plugins", (description, path) =>
        {
            if (budgetExceeded) return;
            Checks descriptionChecks = new Checks();
            descriptionChecks.Description(description, path);
            if (descriptionChecks.Issues.Count != 0)
            {
                foreach (ValidationIssue issue in descriptionChecks.Issues) checks.Add(issue.Path, issue.Code, issue.Message);
                return;
            }
            totalSources += description.Sources.Count;
            totalRoutes += description.Sources.Sum(source => source.Routes.Count);
            if (totalSources > MaximumAggregateSources || totalRoutes > MaximumAggregateRoutes)
            {
                checks.Add(path, "too_large", "Combined descriptions exceed the source/route validation budget (256 sources, 1024 routes).");
                budgetExceeded = true;
                return;
            }
            if (!plugins.Add(description.PluginId)) checks.Add(path + ".pluginId", "duplicate_plugin", "Plugin identity is already declared.");
            foreach (SourceDescription source in description.Sources)
            {
                if (!sources.Add(source.SourceId)) checks.Add(path + ".sources", "duplicate_source", "Source identity is already declared.");
                foreach (UrlRoute route in source.Routes)
                {
                    if (!routes.TryGetValue(route.Host, out List<(string SourceId, RouteShape Shape)>? hostRoutes))
                    {
                        hostRoutes = new List<(string, RouteShape)>();
                        routes.Add(route.Host, hostRoutes);
                    }
                    RouteShape shape = new RouteShape(route);
                    if (hostRoutes.Any(existing => existing.SourceId != source.SourceId && RoutesOverlap(existing.Shape, shape)))
                        checks.Add(path + ".sources.routes", "route_conflict", "More than one source can own this URL route.");
                    hostRoutes.Add((source.SourceId, shape));
                }
            }
        }, 256);
        return checks.Issues;
    }

    public static IReadOnlyList<ValidationIssue> ValidateSettings(SettingsSchema schema, SettingsSnapshot settings,
        SettingScope scope)
    {
        Checks checks = new Checks();
        checks.Schema(schema, "schema", scope);
        checks.Settings(schema, settings, "settings");
        return checks.Issues;
    }

    public static IReadOnlyList<ValidationIssue> ValidateConfiguration(PluginDescription description,
        PluginConfiguration configuration)
    {
        Checks checks = new Checks();
        if (description is null || configuration is null)
        {
            checks.Add("configuration", "missing", "Description and active configuration are required.");
            return checks.Issues;
        }
        checks.Identity(configuration.Revision, "configuration.revision");
        checks.Settings(description.Settings, configuration.PluginSettings, "configuration.pluginSettings");
        if (configuration.SourceSettings is null)
            checks.Add("configuration.sourceSettings", "missing", "A source-settings map is required.");
        else
        {
            if (configuration.SourceSettings.Count > 64)
                checks.Add("configuration.sourceSettings", "too_large", "Too many source-settings entries.");
            else foreach ((string sourceId, SettingsSnapshot settings) in configuration.SourceSettings)
            {
                SourceDescription? source = description.Sources?.FirstOrDefault(value => value is not null && value.SourceId == sourceId);
                if (source is null) checks.Add("configuration.sourceSettings." + sourceId, "unknown_source", "Settings reference an undeclared source.");
                else checks.Settings(source.Settings, settings, "configuration.sourceSettings." + sourceId);
            }
            foreach (SourceDescription source in description.Sources ?? Array.Empty<SourceDescription>())
                if (source is not null && !configuration.SourceSettings.ContainsKey(source.SourceId))
                    checks.Add("configuration.sourceSettings." + source.SourceId, "missing", "Each declared source requires an explicit settings snapshot.");
        }
        return checks.Issues;
    }

    public static IReadOnlyList<ValidationIssue> ValidateThreadResult(ThreadReadRequest request,
        ThreadReadResult result, SourceDescription source)
    {
        Checks checks = new Checks();
        if (request is null || source is null)
        {
            checks.Add("request", "missing", "Request and source description are required.");
            return checks.Issues;
        }
        checks.ThreadId(request.Thread, "request.thread");
        checks.SameSource(request.Thread?.SourceId, source.SourceId, "request.thread.sourceId");
        checks.RouteIdentity(source, SourceResourceKind.Thread, request.Thread?.BoardId, request.Thread?.ThreadId, "request.thread");
        checks.Validators(request.Validators, "request.validators");
        switch (result)
        {
            case ThreadSnapshot snapshot:
                checks.ThreadSnapshot(snapshot.Snapshot, source, "result.snapshot");
                if (snapshot.Snapshot?.Thread != request.Thread) checks.Add("result.snapshot.thread", "wrong_resource", "Snapshot does not identify the requested thread.");
                break;
            case ThreadUnchanged unchanged:
                checks.ExpectedThread(unchanged.Thread, request.Thread, "result.thread");
                checks.Validators(unchanged.Validators, "result.validators", required: true);
                if (!request.HasSnapshot) checks.Add("result", "missing_baseline", "Unchanged requires a previously accepted snapshot.");
                break;
            case ThreadNotFound notFound:
                checks.ExpectedThread(notFound.Thread, request.Thread, "result.thread");
                if (notFound.HttpStatusCode is not null and not 404 and not 410)
                    checks.Add("result.httpStatusCode", "not_terminal", "Only a verified 404/410 may be transported as HTTP thread absence.");
                break;
            case ThreadDeferred deferred:
                checks.ExpectedThread(deferred.Thread, request.Thread, "result.thread");
                checks.Deferred(deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode, "result");
                break;
            case ThreadFailure failure:
                checks.ExpectedThread(failure.Thread, request.Thread, "result.thread");
                checks.Failure(failure.Failure, "result.failure");
                break;
            default:
                checks.Add("result", "wrong_result_kind", "A known thread result is required.");
                break;
        }
        return checks.Issues;
    }

    public static IReadOnlyList<ValidationIssue> ValidateCatalogResult(CatalogReadRequest request,
        CatalogReadResult result, SourceDescription source)
    {
        Checks checks = new Checks();
        if (request is null || source is null)
        {
            checks.Add("request", "missing", "Request and source description are required.");
            return checks.Issues;
        }
        checks.BoardId(request.Board, "request.board");
        checks.SameSource(request.Board?.SourceId, source.SourceId, "request.board.sourceId");
        checks.RouteIdentity(source, request.IsArchive ? SourceResourceKind.BoardArchive : SourceResourceKind.Board,
            request.Board?.BoardId, null, "request.board");
        checks.Page(request.Cursor, request.Page, "request");
        if (request.IsArchive && source.Routes?.Any(route => route is { Kind: SourceResourceKind.BoardArchive, IsCanonical: true }) != true)
            checks.Add("request.isArchive", "unsupported_archive", "The source did not declare a board archive.");
        checks.Validators(request.Validators, "request.validators");
        switch (result)
        {
            case CatalogSnapshot snapshot:
                checks.CatalogSnapshot(snapshot.Snapshot, source, "result.snapshot");
                if (snapshot.Snapshot?.Board != request.Board) checks.Add("result.snapshot.board", "wrong_resource", "Snapshot does not identify the requested board.");
                break;
            case CatalogUnchanged unchanged:
                checks.ExpectedBoard(unchanged.Board, request.Board, "result.board");
                checks.Validators(unchanged.Validators, "result.validators", required: true);
                if (!request.HasSnapshot) checks.Add("result", "missing_baseline", "Unchanged requires a previously accepted snapshot.");
                break;
            case CatalogDeferred deferred:
                checks.ExpectedBoard(deferred.Board, request.Board, "result.board");
                checks.Deferred(deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode, "result");
                break;
            case CatalogFailure failure:
                checks.ExpectedBoard(failure.Board, request.Board, "result.board");
                checks.Failure(failure.Failure, "result.failure");
                break;
            default:
                checks.Add("result", "wrong_result_kind", "A known catalog result is required.");
                break;
        }
        return checks.Issues;
    }

    /// <summary>
    /// Validates a search request against the source description: post-search capability, query bound, page
    /// 1..1000, limit 1..100 and the cursor rules (only for a <see cref="PostOrderKind.DecimalPostId"/> source,
    /// a canonical decimal post id, page 1). Issues never echo query or cursor text.
    /// </summary>
    public static IReadOnlyList<ValidationIssue> ValidateBooruSearchRequest(BooruSearchRequest request,
        SourceDescription source)
    {
        Checks checks = new Checks();
        checks.BooruSearchRequest(request, source);
        return checks.Issues;
    }

    /// <summary>
    /// Validates the request and its result: the page echoes source, query and page, holds at most Limit posts
    /// with unique ids of the source and consistent presence flags, URLs on declared hosts; for a decimal-ordered
    /// source also strictly descending canonical ids, ids below the request cursor and the next cursor rule.
    /// </summary>
    public static IReadOnlyList<ValidationIssue> ValidateBooruSearchResult(BooruSearchRequest request,
        BooruSearchResult result, SourceDescription source)
    {
        Checks checks = new Checks();
        checks.BooruSearchRequest(request, source);
        if (request is null || source is null) return checks.Issues;
        switch (result)
        {
            case BooruSearchSnapshot snapshot:
                checks.BooruSearchPage(snapshot.Page, request, source, "result.page");
                break;
            case BooruSearchDeferred deferred:
                checks.SameSource(deferred.SourceId, request.SourceId, "result.sourceId");
                checks.Deferred(deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode, "result");
                break;
            case BooruSearchFailure failure:
                checks.SameSource(failure.SourceId, request.SourceId, "result.sourceId");
                checks.Failure(failure.Failure, "result.failure");
                break;
            default:
                checks.Add("result", "wrong_result_kind", "A known booru search result is required.");
                break;
        }
        return checks.Issues;
    }

    /// <summary>Validates a single-post request: identity of the described post-search source (canonical decimal for a decimal-ordered source).</summary>
    public static IReadOnlyList<ValidationIssue> ValidateBooruPostRequest(BooruPostRequest request,
        SourceDescription source)
    {
        Checks checks = new Checks();
        checks.BooruPostRequest(request, source);
        return checks.Issues;
    }

    /// <summary>
    /// Validates the request and its result: a snapshot identifies the requested post and passes the post rules;
    /// not found, deferred and failure identify the requested post, and a not-found status is 404 or 410 when given.
    /// </summary>
    public static IReadOnlyList<ValidationIssue> ValidateBooruPostResult(BooruPostRequest request,
        BooruPostResult result, SourceDescription source)
    {
        Checks checks = new Checks();
        checks.BooruPostRequest(request, source);
        if (request is null || source is null) return checks.Issues;
        switch (result)
        {
            case BooruPostSnapshot snapshot:
                checks.BooruPost(snapshot.Post, source, "result.post");
                if (snapshot.Post?.Id != request.Post) checks.Add("result.post.id", "wrong_resource", "Snapshot does not identify the requested post.");
                break;
            case BooruPostNotFound notFound:
                checks.ExpectedBooruPost(notFound.Post, request.Post, "result.post");
                if (notFound.HttpStatusCode is not null and not 404 and not 410)
                    checks.Add("result.httpStatusCode", "not_terminal", "Only a verified 404/410 may be transported as HTTP post absence.");
                break;
            case BooruPostDeferred deferred:
                checks.ExpectedBooruPost(deferred.Post, request.Post, "result.post");
                checks.Deferred(deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode, "result");
                break;
            case BooruPostFailure failure:
                checks.ExpectedBooruPost(failure.Post, request.Post, "result.post");
                checks.Failure(failure.Failure, "result.failure");
                break;
            default:
                checks.Add("result", "wrong_result_kind", "A known booru post result is required.");
                break;
        }
        return checks.Issues;
    }

    public static IReadOnlyList<ValidationIssue> ValidateHttpRequest(HostHttpRequest request, SourceDescription source)
    {
        Checks checks = new Checks();
        checks.HttpRequest(request, source, "request");
        return checks.Issues;
    }

    public static IReadOnlyList<ValidationIssue> ValidateHttpResult(HostHttpRequest request,
        HostHttpResult result, SourceDescription source)
    {
        Checks checks = new Checks();
        checks.HttpRequest(request, source, "request");
        switch (result)
        {
            case HostHttpResponse response:
                checks.HttpStatus(response.StatusCode, "result.statusCode", required: true);
                checks.AllowedHttpUri(response.FinalUri, source, "result.finalUri");
                checks.Headers(response.Headers, "result.headers", requestHeaders: false);
                checks.Validators(response.Validators, "result.validators");
                if (response.Body is null) checks.Add("result.body", "missing", "Response body must be present, including an empty body.");
                else
                {
                    if (response.Body.Length > SdkLimits.MaximumMetadataBytes) checks.Add("result.body", "too_large", "Decompressed metadata body exceeds its byte limit.");
                    if ((request?.Method == HostHttpMethod.Head || response.StatusCode is 204 or 304) && response.Body.Length != 0)
                        checks.Add("result.body", "unexpected_body", "HEAD, 204 and 304 responses must have an empty body.");
                }
                break;
            case HostHttpDeferred deferred:
                checks.Deferred(deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode, "result");
                break;
            case HostHttpFailure failure:
                checks.Failure(failure.Failure, "result.failure");
                break;
            default:
                checks.Add("result", "wrong_result_kind", "A known host HTTP result is required.");
                break;
        }
        return checks.Issues;
    }

    public static IReadOnlyList<ValidationIssue> ValidateImageFilterRequest(ImageFilterRequest request,
        FilterDescription filter)
    {
        Checks checks = new Checks();
        if (request is null || filter is null)
        {
            checks.Add("request", "missing", "Filter description and request are required.");
            return checks.Issues;
        }
        checks.Identity(request.FilterKey, "request.filterKey");
        if (request.FilterKey != filter.Key) checks.Add("request.filterKey", "wrong_filter", "Request identifies another filter.");
        FilterCandidate? candidate = request.Candidate;
        if (candidate is null) checks.Add("request.candidate", "missing", "A candidate is required.");
        else
        {
            checks.MediaId(candidate.Id, "request.candidate.id");
            checks.Identity(candidate.PostId, "request.candidate.postId");
            if (candidate.Position < 0) checks.Add("request.candidate.position", "invalid_position", "Position must be nonnegative.");
            checks.WebUri(candidate.Url, "request.candidate.url");
            checks.Text(candidate.OriginalFileName, "request.candidate.originalFileName", 4096);
            checks.Text(candidate.MimeType, "request.candidate.mimeType", 256);
        }
        checks.Identity(request.InputRevision, "request.inputRevision");
        checks.Identity(request.MediaRevision, "request.mediaRevision");
        checks.Identity(request.AssetToken, "request.assetToken");
        checks.Identity(request.EffectiveRuleRevision, "request.effectiveRuleRevision");
        checks.Identity(request.PluginBuildRevision, "request.pluginBuildRevision");
        checks.Identity(request.ConfigurationRevision, "request.configurationRevision");
        checks.Identity(request.GrantRevision, "request.grantRevision");
        if (request.OriginalSize <= 0 || request.OriginalSize > SdkLimits.MaximumImageBytes)
            checks.Add("request.originalSize", "image_size_limit", "Original bytes must fit the image byte budget.");
        checks.Sha256(request.OriginalSha256, "request.originalSha256", required: true);
        checks.Settings(filter.Settings, request.RuleSettings, "request.ruleSettings");
        return checks.Issues;
    }

    public static IReadOnlyList<ValidationIssue> ValidateImageFilterResult(ImageFilterResult result)
    {
        Checks checks = new Checks();
        if (result is null) checks.Add("result", "missing", "A filter result is required.");
        else
        {
            checks.EnumValue(result.Outcome, "result.outcome");
            checks.Identity(result.ReasonCode, "result.reasonCode");
            checks.Text(result.Message, "result.message", SdkLimits.MaximumDiagnosticLength);
        }
        return checks.Issues;
    }

    public static IReadOnlyList<ValidationIssue> ValidateImageRead(string assetToken, long offset, int count, long assetSize)
    {
        Checks checks = new Checks();
        checks.Identity(assetToken, "assetToken");
        if (assetSize <= 0 || assetSize > SdkLimits.MaximumImageBytes) checks.Add("assetSize", "image_size_limit", "Invalid staged image size.");
        if (offset < 0 || offset > assetSize) checks.Add("offset", "invalid_offset", "Offset must be inside the image or exactly at its end.");
        if (count <= 0 || count > SdkLimits.MaximumImageChunkBytes) checks.Add("count", "chunk_limit", "Read count must be between 1 and 64 KiB.");
        return checks.Issues;
    }

    public static void ThrowIfInvalid(IReadOnlyList<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        if (issues.Count != 0)
            throw new InvalidDataException(string.Join("; ", issues.Select(issue => $"{issue.Path}: {issue.Code}: {issue.Message}")));
    }

    private static bool RoutesOverlap(RouteShape first, RouteShape second)
    {
        return first.Variants.Any(left => second.Variants.Any(right => RouteVariantsOverlap(left, right)));
    }

    private static bool RouteVariantsOverlap(RouteSegment[] first, RouteSegment[] second)
    {
        if (first.Length != second.Length) return false;
        for (int index = 0; index < first.Length; index++)
        {
            RouteSegment left = first[index];
            RouteSegment right = second[index];
            if (!left.Variable && !right.Variable && left.Literal != right.Literal) return false;
            if (left.Variable && !right.Variable && !MatchesSegment(left, right.Literal)) return false;
            if (!left.Variable && right.Variable && !MatchesSegment(right, left.Literal)) return false;
            if (left.Variable && right.Variable)
            {
                if (!left.Prefix.StartsWith(right.Prefix, StringComparison.Ordinal) && !right.Prefix.StartsWith(left.Prefix, StringComparison.Ordinal)) return false;
                if (!left.Suffix.EndsWith(right.Suffix, StringComparison.Ordinal) && !right.Suffix.EndsWith(left.Suffix, StringComparison.Ordinal)) return false;
                if (!VariableSegmentsOverlap(left, right)) return false;
            }
        }
        return true;
    }

    private static bool MatchesSegment(RouteSegment template, string literal)
        => literal.Length > template.Prefix.Length + template.Suffix.Length && literal.StartsWith(template.Prefix, StringComparison.Ordinal)
            && literal.EndsWith(template.Suffix, StringComparison.Ordinal)
            && MatchesIdentifier(Uri.UnescapeDataString(literal[template.Prefix.Length..(literal.Length - template.Suffix.Length)]), template.IdentifierKind);

    private static bool MatchesIdentifier(string value, RouteIdentifierKind kind)
        => value.Length > 0 && (kind == RouteIdentifierKind.PortableSegment || value.Length <= SdkLimits.MaximumIdentifierLength)
            && value.All(character => AllowsIdentifierCharacter(character, kind));

    private static bool AllowsIdentifierCharacter(char value, RouteIdentifierKind kind)
        => kind switch
        {
            RouteIdentifierKind.PortableSegment => true,
            RouteIdentifierKind.AsciiWord => char.IsAsciiLetterOrDigit(value) || value == '_',
            RouteIdentifierKind.Decimal => value is >= '0' and <= '9',
            _ => false
        };

    private static bool VariableSegmentsOverlap(RouteSegment first, RouteSegment second)
    {
        if (first.IdentifierKind == RouteIdentifierKind.PortableSegment && second.IdentifierKind == RouteIdentifierKind.PortableSegment) return true;
        string prefix = first.Prefix.Length >= second.Prefix.Length ? first.Prefix : second.Prefix;
        string suffix = first.Suffix.Length >= second.Suffix.Length ? first.Suffix : second.Suffix;
        int minimum = Math.Max(first.Prefix.Length + first.Suffix.Length + 1, second.Prefix.Length + second.Suffix.Length + 1);
        int maximum = Math.Max(minimum, prefix.Length + suffix.Length);
        // Restricted ASCII IDs use one URL character per identity character.
        // Portable IDs can expand when escaped; title aliases are also unbounded.
        if (first.IdentifierKind != RouteIdentifierKind.PortableSegment)
            maximum = Math.Min(maximum, first.Prefix.Length + first.Suffix.Length + SdkLimits.MaximumIdentifierLength);
        if (second.IdentifierKind != RouteIdentifierKind.PortableSegment)
            maximum = Math.Min(maximum, second.Prefix.Length + second.Suffix.Length + SdkLimits.MaximumIdentifierLength);
        if (minimum > maximum) return false;
        HashSet<int> overlaps = LiteralOverlapLengths(prefix, suffix);
        int[] firstPrefix = InvalidCharacterCounts(prefix, first.IdentifierKind);
        int[] firstSuffix = InvalidCharacterCounts(suffix, first.IdentifierKind);
        int[] secondPrefix = InvalidCharacterCounts(prefix, second.IdentifierKind);
        int[] secondSuffix = InvalidCharacterCounts(suffix, second.IdentifierKind);
        // Beyond the fixed prefix/suffix, both alphabets admit '0', so one such
        // length is enough. Shorter words can overlap the fixed affixes (1ab).
        for (int length = minimum; length <= maximum; length++)
        {
            int overlap = prefix.Length + suffix.Length - length;
            if (overlap > 0 && !overlaps.Contains(overlap)) continue;
            if (AllowsFixedCharacters(first, firstPrefix, firstSuffix, length, prefix.Length, suffix.Length)
                && AllowsFixedCharacters(second, secondPrefix, secondSuffix, length, prefix.Length, suffix.Length)) return true;
        }
        return false;
    }

    private static bool AllowsFixedCharacters(RouteSegment segment, int[] prefixCounts, int[] suffixCounts,
        int length, int prefixLength, int suffixLength)
    {
        int prefixEnd = Math.Min(prefixLength, length - segment.Suffix.Length);
        if (prefixEnd > segment.Prefix.Length && prefixCounts[prefixEnd] != prefixCounts[segment.Prefix.Length]) return false;
        int suffixStart = Math.Max(0, segment.Prefix.Length - (length - suffixLength));
        int suffixEnd = suffixLength - segment.Suffix.Length;
        return suffixEnd <= suffixStart || suffixCounts[suffixEnd] == suffixCounts[suffixStart];
    }

    private static int[] InvalidCharacterCounts(string value, RouteIdentifierKind kind)
    {
        int[] counts = new int[value.Length + 1];
        for (int index = 0; index < value.Length; index++)
            counts[index + 1] = counts[index] + (AllowsIdentifierCharacter(value[index], kind) ? 0 : 1);
        return counts;
    }

    private static HashSet<int> LiteralOverlapLengths(string prefix, string suffix)
    {
        // A prefix-function finds every suffix(prefix) == prefix(suffix) in
        // linear time; route validation must not expand bounded literals into
        // a quadratic set of candidate strings.
        string value = suffix + '\0' + prefix;
        int[] matches = new int[value.Length];
        for (int index = 1; index < value.Length; index++)
        {
            int count = matches[index - 1];
            while (count > 0 && value[index] != value[count]) count = matches[count - 1];
            if (value[index] == value[count]) count++;
            matches[index] = count;
        }
        HashSet<int> overlaps = new HashSet<int>();
        for (int count = matches[^1]; count > 0; count = matches[count - 1]) overlaps.Add(count);
        return overlaps;
    }

    private sealed class RouteShape
    {
        internal IReadOnlyList<RouteSegment[]> Variants { get; }

        internal RouteShape(UrlRoute route)
        {
            bool hasTrailingSlash = route.PathTemplate.EndsWith('/');
            RouteSegment[] segments = route.PathTemplate.TrimEnd('/').Split('/').Select(value => RouteSegment.Parse(value, route)).ToArray();
            List<RouteSegment[]> variants = new List<RouteSegment[]>();
            AddVariants(segments);
            if (route.AllowTitleSegment) AddVariants([.. segments, RouteSegment.Title]);
            Variants = variants;

            void AddVariants(RouteSegment[] path)
            {
                if (route.AllowTrailingSlash || !hasTrailingSlash) variants.Add(path);
                if (route.AllowTrailingSlash || hasTrailingSlash) variants.Add([.. path, RouteSegment.Empty]);
            }
        }
    }

    private sealed record RouteSegment(bool Variable, string Literal, string Prefix, string Suffix,
        RouteIdentifierKind IdentifierKind = RouteIdentifierKind.PortableSegment)
    {
        internal static readonly RouteSegment Empty = new RouteSegment(false, "", "", "");
        internal static readonly RouteSegment Title = new RouteSegment(true, "{title}", "", "");

        internal static RouteSegment Parse(string value, UrlRoute? route = null)
        {
            int start = value.IndexOf('{');
            int end = value.IndexOf('}');
            if (start < 0 || end <= start) return new RouteSegment(false, NormalizeLiteral(value), "", "");
            string prefix = NormalizeLiteral(value[..start]);
            string suffix = NormalizeLiteral(value[(end + 1)..]);
            RouteIdentifierKind kind = value[start..(end + 1)] == "{board}"
                ? route?.BoardIdentifierKind ?? RouteIdentifierKind.PortableSegment
                : route?.ThreadIdentifierKind ?? RouteIdentifierKind.PortableSegment;
            return new RouteSegment(true, prefix + value[start..(end + 1)] + suffix, prefix, suffix, kind);
        }

        // Sentinels keep a literal dot/space at either edge inside its segment while
        // applying the same escaping and unreserved-character normalization as Uri.
        private static string NormalizeLiteral(string value)
            => new Uri("https://route.invalid/_" + value + "_").AbsolutePath[2..^1];
    }

    private static bool IsDotSegment(string value) => Uri.UnescapeDataString(value) is "." or "..";

    private sealed class Checks
    {
        private readonly List<ValidationIssue> _issues = new List<ValidationIssue>();
        internal IReadOnlyList<ValidationIssue> Issues => _issues.AsReadOnly();

        internal void Add(string path, string code, string message)
        {
            if (_issues.Count < 128) _issues.Add(new ValidationIssue(path, code, message));
        }

        internal void Identity(string? value, string path)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > SdkLimits.MaximumIdentifierLength
                || value != value.Trim() || value.Any(char.IsControl))
                Add(path, "invalid_identity", "Identity must be nonempty, bounded and contain no surrounding whitespace or control characters.");
        }

        internal void Text(string? value, string path, int maximum = SdkLimits.MaximumTextLength, bool required = false)
        {
            if (required && string.IsNullOrWhiteSpace(value)) Add(path, "missing", "A nonempty value is required.");
            if (value?.Length > maximum) Add(path, "too_large", "Text exceeds its length limit.");
        }

        internal void EnumValue<T>(T value, string path) where T : struct, Enum
        {
            if (!Enum.IsDefined(value)) Add(path, "unknown_value", "Unknown enum value.");
        }

        internal void List<T>(IReadOnlyList<T>? values, string path, Action<T, string> validate,
            int maximum = SdkLimits.MaximumCollectionCount)
        {
            if (values is null) { Add(path, "missing", "A collection is required."); return; }
            if (values.Count > maximum) { Add(path, "too_large", "Collection exceeds its entry limit."); return; }
            for (int index = 0; index < values.Count; index++) validate(values[index], $"{path}[{index}]");
        }

        internal void WebUri(Uri? uri, string path)
        {
            if (uri is null || !uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https")
                || uri.UserInfo.Length != 0 || string.IsNullOrWhiteSpace(uri.Host))
                Add(path, "invalid_url", "An absolute HTTP(S) URL without credentials is required.");
        }

        private void Host(string? host, string path)
        {
            if (string.IsNullOrWhiteSpace(host) || host.Length > 253 || host != host.Trim()
                || host.Contains('*') || host.Contains(':') || host.Contains('/') || host.Contains('\\')
                || Uri.CheckHostName(host) == UriHostNameType.Unknown)
                Add(path, "invalid_host", "Declare one exact host without scheme, port, wildcard or path.");
        }

        internal void Description(PluginDescription? description, string path)
        {
            if (description is null) { Add(path, "missing", "Plugin description is required."); return; }
            Identity(description.PluginId, path + ".pluginId");
            Text(description.Name, path + ".name", 256, required: true);
            Identity(description.PluginVersion, path + ".pluginVersion");
            if (!SdkContract.IsSupported(description.SdkVersion)) Add(path + ".sdkVersion", "incompatible_sdk", $"This host supports SDK 1.0.0 and {SdkContract.Version}.");
            Text(description.Description, path + ".description", 16384, required: true);
            Text(description.Author, path + ".author", 256);
            if (description.AuthorUrl is not null) WebUri(description.AuthorUrl, path + ".authorUrl");
            if (description.DocumentationUrl is not null) WebUri(description.DocumentationUrl, path + ".documentationUrl");
            Schema(description.Settings, path + ".settings", SettingScope.Plugin);
            HashSet<string> sourceIds = new HashSet<string>(StringComparer.Ordinal);
            List(description.Sources, path + ".sources", (source, itemPath) =>
            {
                Source(source, itemPath);
                if (source is not null && !sourceIds.Add(source.SourceId)) Add(itemPath + ".sourceId", "duplicate_source", "Source identity is duplicated.");
            }, 64);
            HashSet<string> filterKeys = new HashSet<string>(StringComparer.Ordinal);
            List(description.Filters, path + ".filters", (filter, itemPath) =>
            {
                Filter(filter, itemPath);
                if (filter is not null && !filterKeys.Add(filter.Key)) Add(itemPath + ".key", "duplicate_filter", "Filter key is duplicated.");
            }, 128);
            if (SdkContract.IsSupported(description.SdkVersion) && description.SdkVersion != SdkContract.Version
                && description.Sources?.Any(source => source is not null && source.Capabilities.HasFlag(SourceCapabilities.PostSearch)) == true)
                Add(path + ".sdkVersion", "incompatible_sdk", $"Post search requires SDK {SdkContract.Version}.");
            bool hasCapability = description.Sources?.Count > 0 || description.Filters?.Count > 0;
            if (!hasCapability && description.DeclarationStatus != PluginDeclarationStatus.Blocked)
                Add(path, "missing_capability", "A plugin must declare a source or filter capability unless it is explicitly blocked.");
            if (description.DeclarationStatus == PluginDeclarationStatus.Blocked && hasCapability)
                Add(path + ".declarationStatus", "blocked_capability", "A blocked plugin declaration must not expose executable source or filter capabilities.");
            HashSet<string> informationKeys = new HashSet<string>(StringComparer.Ordinal);
            List(description.Information, path + ".information", (entry, itemPath) =>
            {
                if (entry is null) { Add(itemPath, "missing", "Information entry is required."); return; }
                Identity(entry.Key, itemPath + ".key");
                if (!informationKeys.Add(entry.Key)) Add(itemPath + ".key", "duplicate_key", "Information key is duplicated.");
                Text(entry.Label, itemPath + ".label", 256, required: true);
                EnumValue(entry.Kind, itemPath + ".kind");
                Text(entry.Value, itemPath + ".value", 16384);
                if (entry.Kind == InformationKind.List)
                {
                    if (entry.Value is not null) Add(itemPath + ".value", "wrong_payload", "List information uses Items only.");
                    List(entry.Items, itemPath + ".items", (value, valuePath) => Text(value, valuePath, 4096, required: true), 256);
                }
                else if (entry.Items is not null) Add(itemPath + ".items", "wrong_payload", "Text/value information does not contain Items.");
                if (entry.SourceId is not null && !sourceIds.Contains(entry.SourceId)) Add(itemPath + ".sourceId", "unknown_source", "Information references an undeclared source.");
            }, 256);
        }

        private void Source(SourceDescription? source, string path)
        {
            if (source is null) { Add(path, "missing", "Source description is required."); return; }
            Identity(source.SourceId, path + ".sourceId");
            Text(source.Name, path + ".name", 256, required: true);
            WebUri(source.BaseUri, path + ".baseUri");
            const SourceCapabilities known = SourceCapabilities.ThreadRead | SourceCapabilities.BoardCatalog
                | SourceCapabilities.PostCapture | SourceCapabilities.OriginalMedia | SourceCapabilities.Previews
                | SourceCapabilities.PostSearch;
            if (source.Capabilities == SourceCapabilities.None || (source.Capabilities & ~known) != 0)
                Add(path + ".capabilities", "unknown_capability", "Declare supported source capabilities.");
            bool postSearch = source.Capabilities.HasFlag(SourceCapabilities.PostSearch);
            if (postSearch)
            {
                const SourceCapabilities boardWorld = SourceCapabilities.ThreadRead | SourceCapabilities.BoardCatalog | SourceCapabilities.PostCapture;
                if ((source.Capabilities & boardWorld) != 0)
                    Add(path + ".capabilities", "mixed_capability", "Post search is never combined with thread, catalog or capture capabilities in one source.");
                if (source.Routes?.Count > 0)
                    Add(path + ".routes", "unexpected_route", "A post-search source has no board or thread routes.");
                if (!(source.HttpHosts?.Any(host => host is not null && host.Purpose.HasFlag(HttpHostPurpose.Metadata)) ?? false))
                    Add(path + ".httpHosts", "missing_metadata_host", "Post search requires at least one metadata host.");
            }
            EnumValue(source.PostOrder, path + ".postOrder");
            EnumValue(source.Maturity, path + ".maturity");
            Schema(source.Settings, path + ".settings", SettingScope.Source);
            if (source.Routes?.Count > 64)
            { Add(path + ".routes", "too_large", "Collection exceeds its entry limit."); return; }
            HashSet<string> routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List(source.Routes, path + ".routes", (route, itemPath) =>
            {
                Route(route, itemPath);
                if (route is not null && !routes.Add(route.Host + "\n" + route.PathTemplate)) Add(itemPath, "duplicate_route", "Route is duplicated.");
            }, 64);
            foreach (SourceResourceKind kind in Enum.GetValues<SourceResourceKind>())
            {
                bool supported = kind switch
                {
                    SourceResourceKind.Thread => source.Capabilities.HasFlag(SourceCapabilities.ThreadRead),
                    SourceResourceKind.Board => source.Capabilities.HasFlag(SourceCapabilities.BoardCatalog),
                    _ => source.Routes?.Any(route => route?.Kind == kind) == true
                };
                int canonical = source.Routes?.Count(route => route is not null && route.Kind == kind && route.IsCanonical) ?? 0;
                if (supported && canonical != 1) Add(path + ".routes", "canonical_route", "Every supported resource kind requires exactly one canonical route.");
                if (kind == SourceResourceKind.BoardArchive && supported
                    && !source.Capabilities.HasFlag(SourceCapabilities.BoardCatalog | SourceCapabilities.ThreadRead))
                    Add(path + ".routes", "archive_requires_catalog", "A board archive requires catalog and thread reads.");
            }
            HashSet<string> hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List(source.HttpHosts, path + ".httpHosts", (host, itemPath) =>
            {
                if (host is null) { Add(itemPath, "missing", "HTTP host declaration is required."); return; }
                Host(host.Host, itemPath + ".host");
                if (!hosts.Add(host.Host)) Add(itemPath + ".host", "duplicate_host", "Merge purposes for the same host.");
                const HttpHostPurpose all = HttpHostPurpose.Metadata | HttpHostPurpose.OriginalMedia | HttpHostPurpose.PreviewMedia;
                if (host.Purpose == HttpHostPurpose.None || (host.Purpose & ~all) != 0) Add(itemPath + ".purpose", "unknown_purpose", "Declare a known host purpose.");
            }, 64);
        }

        private void Route(UrlRoute? route, string path)
        {
            if (route is null) { Add(path, "missing", "Route is required."); return; }
            EnumValue(route.Kind, path + ".kind");
            EnumValue(route.BoardIdentifierKind, path + ".boardIdentifierKind");
            EnumValue(route.ThreadIdentifierKind, path + ".threadIdentifierKind");
            Host(route.Host, path + ".host");
            string? template = route.PathTemplate;
            if (string.IsNullOrWhiteSpace(template) || !template.StartsWith('/') || template.Length > 2048
                || template.IndexOfAny(['?', '#', '\\', '*']) >= 0)
            { Add(path + ".pathTemplate", "invalid_template", "Use a bounded absolute path template without query, fragment, wildcard or backslash."); return; }
            int boardCount = Count(template, "{board}");
            int threadCount = Count(template, "{thread}");
            string remainder = template.Replace("{board}", "", StringComparison.Ordinal).Replace("{thread}", "", StringComparison.Ordinal);
            if (boardCount != 1 || threadCount != (route.Kind == SourceResourceKind.Thread ? 1 : 0)
                || remainder.Contains('{') || remainder.Contains('}') || template.Contains("//", StringComparison.Ordinal))
                Add(path + ".pathTemplate", "invalid_template", "Use exactly one board placeholder and, for threads, exactly one thread placeholder.");
            foreach (string segment in template.Split('/'))
                if (Count(segment, "{") > 1 || IsDotSegment(segment)) Add(path + ".pathTemplate", "invalid_template", "One placeholder per segment; dot segments are forbidden.");
            if (route.AllowTitleSegment && route.Kind != SourceResourceKind.Thread) Add(path + ".allowTitleSegment", "invalid_template", "Only thread aliases may have an inert title segment.");
        }

        private static int Count(string value, string token) => (value.Length - value.Replace(token, "", StringComparison.Ordinal).Length) / token.Length;

        private void Filter(FilterDescription? filter, string path)
        {
            if (filter is null) { Add(path, "missing", "Filter description is required."); return; }
            Identity(filter.Key, path + ".key");
            Text(filter.Name, path + ".name", 256, required: true);
            Text(filter.Description, path + ".description", 16384, required: true);
            const FilterDataAccess all = FilterDataAccess.CandidateMetadata | FilterDataAccess.OriginalImage;
            if ((filter.RequiredAccess & ~all) != 0) Add(path + ".requiredAccess", "unknown_access", "Unknown filter data access.");
            Schema(filter.Settings, path + ".settings", SettingScope.FilterRule);
            List(filter.SupportedMimeTypes, path + ".supportedMimeTypes", (mime, itemPath) => Text(mime, itemPath, 256, required: true), 64);
        }

        internal void Schema(SettingsSchema? schema, string path, SettingScope scope)
        {
            EnumValue(scope, path + ".scope");
            if (schema is null) { Add(path, "missing", "Settings schema is required, including an empty schema."); return; }
            Identity(schema.Version, path + ".version");
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            List(schema.Fields, path + ".fields", (field, itemPath) =>
            {
                if (field is null) { Add(itemPath, "missing", "Setting definition is required."); return; }
                Identity(field.Key, itemPath + ".key");
                if (!keys.Add(field.Key)) Add(itemPath + ".key", "duplicate_key", "Setting key is duplicated.");
                EnumValue(field.Kind, itemPath + ".kind");
                if (field.Scope != scope) Add(itemPath + ".scope", "wrong_scope", "Setting belongs to another scope.");
                Text(field.Label, itemPath + ".label", 256, required: true);
                Text(field.Help, itemPath + ".help", 4096);
                if (field.Minimum > field.Maximum) Add(itemPath, "invalid_bounds", "Minimum exceeds maximum.");
                if (field.MaximumLength is < 1 or > SdkLimits.MaximumTextLength) Add(itemPath + ".maximumLength", "invalid_bounds", "Invalid text length bound.");
                if (field.Kind != SettingKind.Integer && (field.Minimum is not null || field.Maximum is not null)) Add(itemPath, "wrong_bounds", "Only Integer settings have numeric bounds.");
                if (field.Kind is SettingKind.Integer or SettingKind.Boolean && field.MaximumLength is not null) Add(itemPath, "wrong_bounds", "Integer/Boolean settings do not have text bounds.");
                if (field.Kind == SettingKind.Enum)
                {
                    HashSet<string> options = new HashSet<string>(StringComparer.Ordinal);
                    List(field.Options, itemPath + ".options", (option, optionPath) =>
                    {
                        if (option is null) { Add(optionPath, "missing", "Enum option is required."); return; }
                        Identity(option.Value, optionPath + ".value");
                        Text(option.Label, optionPath + ".label", 256, required: true);
                        if (!options.Add(option.Value)) Add(optionPath + ".value", "duplicate_option", "Enum option is duplicated.");
                    }, 256);
                    if (field.Options?.Count == 0) Add(itemPath + ".options", "missing_options", "Enum settings require options.");
                }
                else if (field.Options is not null) Add(itemPath + ".options", "wrong_payload", "Only Enum settings have options.");
                if (field.Default is not null) Setting(field, field.Default, itemPath + ".default");
            }, 256);
        }

        internal void Settings(SettingsSchema? schema, SettingsSnapshot? settings, string path)
        {
            if (schema is null || settings is null) { Add(path, "missing", "Schema and settings snapshot are required."); return; }
            if (schema.Fields?.Count > 256) { Add(path, "too_large", "Settings schema exceeds its field limit."); return; }
            if (settings.SchemaVersion != schema.Version) Add(path + ".schemaVersion", "schema_mismatch", "Settings require explicit migration to this schema version.");
            Dictionary<string, SettingDefinition> fields = new Dictionary<string, SettingDefinition>(StringComparer.Ordinal);
            List(schema.Fields, path + ".schema.fields", (field, _) =>
            {
                if (field is not null && field.Key is not null) fields.TryAdd(field.Key, field);
            }, 256);
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            List(settings.Values, path + ".values", (value, itemPath) =>
            {
                if (value is null) { Add(itemPath, "missing", "Setting value is required."); return; }
                Identity(value.Key, itemPath + ".key");
                if (value.Key is null) return;
                if (!seen.Add(value.Key)) Add(itemPath + ".key", "duplicate_key", "Setting key is duplicated.");
                if (!fields.TryGetValue(value.Key, out SettingDefinition? field)) Add(itemPath + ".key", "unknown_setting", "Setting is not defined in this schema; preserve it separately for migration.");
                else Setting(field, value, itemPath);
            }, 256);
            foreach (SettingDefinition field in fields.Values)
                if (field.IsRequired && field.Default is null && !seen.Contains(field.Key)) Add(path + "." + field.Key, "required_setting", "Required setting is unset.");
        }

        private void Setting(SettingDefinition field, SettingValue value, string path)
        {
            if (value.Key != field.Key || value.Kind != field.Kind) Add(path, "setting_type", "Setting key and type must match its schema.");
            bool text = value.Kind is SettingKind.Text or SettingKind.Enum or SettingKind.Uri;
            if ((text ? value.Text is null : value.Text is not null)
                || (value.Kind == SettingKind.Integer ? value.Integer is null : value.Integer is not null)
                || (value.Kind == SettingKind.Boolean ? value.Boolean is null : value.Boolean is not null))
                Add(path, "wrong_payload", "Set exactly the payload matching the setting kind.");
            Text(value.Text, path + ".text", field.MaximumLength ?? SdkLimits.MaximumTextLength, required: field.IsRequired && text);
            if (value.Integer < field.Minimum || value.Integer > field.Maximum) Add(path + ".integer", "out_of_range", "Integer is outside its allowed range.");
            if (value.Kind == SettingKind.Enum)
            {
                if (field.Options?.Count > 256) Add(path, "too_large", "Enum setting exceeds its option limit.");
                else if (!(field.Options?.Any(option => option is not null && option.Value == value.Text) ?? false)) Add(path + ".text", "unknown_option", "Enum value is not an allowed option.");
            }
            if (value.Kind == SettingKind.Uri && !Uri.TryCreate(value.Text, UriKind.Absolute, out _)) Add(path + ".text", "invalid_uri", "Setting requires an absolute URI.");
        }

        internal void BoardId(SourceBoardId? id, string path)
        {
            if (id is null) { Add(path, "missing", "Board identity is required."); return; }
            Identity(id.SourceId, path + ".sourceId"); Identity(id.BoardId, path + ".boardId");
        }

        internal void ThreadId(SourceThreadId? id, string path)
        {
            if (id is null) { Add(path, "missing", "Thread identity is required."); return; }
            Identity(id.SourceId, path + ".sourceId"); Identity(id.BoardId, path + ".boardId"); Identity(id.ThreadId, path + ".threadId");
        }

        internal void MediaId(SourceMediaId? id, string path)
        {
            if (id is null) { Add(path, "missing", "Media identity is required."); return; }
            ThreadId(id.Thread, path + ".thread"); Identity(id.MediaId, path + ".mediaId"); Identity(id.Variant, path + ".variant");
        }

        internal void SameSource(string? actual, string expected, string path)
        {
            if (actual != expected) Add(path, "wrong_source", "Resource identifies another source.");
        }

        internal void RouteIdentity(SourceDescription source, SourceResourceKind kind, string? board, string? thread, string path)
        {
            UrlRoute? route = source.Routes?.FirstOrDefault(value => value is not null && value.Kind == kind && value.IsCanonical);
            if (route is null) return;
            if (board is not null && !MatchesIdentifier(board, route.BoardIdentifierKind))
                Add(path + ".boardId", "invalid_route_identity", "Board identity does not match the canonical route's declared alphabet.");
            if (kind == SourceResourceKind.Thread && thread is not null && !MatchesIdentifier(thread, route.ThreadIdentifierKind))
                Add(path + ".threadId", "invalid_route_identity", "Thread identity does not match the canonical route's declared alphabet.");
        }

        internal void ExpectedThread(SourceThreadId? actual, SourceThreadId? expected, string path)
        {
            ThreadId(actual, path);
            if (actual != expected) Add(path, "wrong_resource", "Result identifies another thread.");
        }

        internal void ExpectedBoard(SourceBoardId? actual, SourceBoardId? expected, string path)
        {
            BoardId(actual, path);
            if (actual != expected) Add(path, "wrong_resource", "Result identifies another board.");
        }

        internal void ThreadSnapshot(SourceThreadSnapshot? snapshot, SourceDescription source, string path)
        {
            if (snapshot is null) { Add(path, "missing", "Thread snapshot is required."); return; }
            ThreadId(snapshot.Thread, path + ".thread");
            SameSource(snapshot.Thread?.SourceId, source.SourceId, path + ".thread.sourceId");
            SourceUri(snapshot.Url, source, snapshot.Thread, path + ".url");
            Text(snapshot.Title, path + ".title", 16384);
            EnumValue(snapshot.SourceState, path + ".sourceState");
            Validators(snapshot.Validators, path + ".validators");
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            List(snapshot.Posts, path + ".posts", (post, itemPath) =>
            {
                if (post is null) { Add(itemPath, "missing", "Post is required."); return; }
                Identity(post.PostId, itemPath + ".postId");
                if (!ids.Add(post.PostId)) Add(itemPath + ".postId", "duplicate_post", "Post identity is duplicated.");
                if (post.Ordinal < 0) Add(itemPath + ".ordinal", "invalid_order", "Post order must be nonnegative.");
                if (source.PostOrder == PostOrderKind.DecimalPostId && (string.IsNullOrEmpty(post.PostId) || post.PostId.Any(character => character is < '0' or > '9')))
                    Add(itemPath + ".postId", "invalid_order", "This source declares decimal post ordering.");
                if ((post.Fields & ~SourceFieldPresence.All) != 0) Add(itemPath + ".fields", "unknown_presence", "Unknown field-presence flag.");
                EnumValue(post.TextFormat, itemPath + ".textFormat");
                Text(post.Subject, itemPath + ".subject", 16384);
                Text(post.Author, itemPath + ".author", 4096);
                Text(post.Text, itemPath + ".text");
                if ((!post.Fields.HasFlag(SourceFieldPresence.PostedAt) && post.PostedAt is not null)
                    || (!post.Fields.HasFlag(SourceFieldPresence.Subject) && post.Subject is not null)
                    || (!post.Fields.HasFlag(SourceFieldPresence.Author) && post.Author is not null)
                    || (!post.Fields.HasFlag(SourceFieldPresence.Text) && post.Text is not null)
                    || (!post.Fields.HasFlag(SourceFieldPresence.IsDeleted) && post.IsDeleted)
                    || (!post.Fields.HasFlag(SourceFieldPresence.Attachments) && post.Attachments is not null))
                    Add(itemPath + ".fields", "presence_mismatch", "A supplied value requires its presence flag.");
                if (post.Fields.HasFlag(SourceFieldPresence.Attachments))
                {
                    HashSet<int> positions = new HashSet<int>();
                    List(post.Attachments, itemPath + ".attachments", (attachment, attachmentPath) =>
                    {
                        Attachment(attachment, source, attachmentPath);
                        if (attachment is not null && !positions.Add(attachment.Position)) Add(attachmentPath + ".position", "duplicate_position", "Attachment position is duplicated within the post.");
                    }, 1024);
                }
            });
        }

        private void Attachment(SourceAttachment? attachment, SourceDescription source, string path)
        {
            if (attachment is null) { Add(path, "missing", "Attachment is required."); return; }
            if (attachment.Position < 0) Add(path + ".position", "invalid_position", "Attachment position must be nonnegative.");
            if (attachment.MediaId is null)
            {
                if (!attachment.IsDeleted) Add(path + ".mediaId", "missing_media", "Only a deletion marker may omit the media identity.");
            }
            else Identity(attachment.MediaId, path + ".mediaId");
            Identity(attachment.Variant, path + ".variant");
            if (attachment.Url is not null)
            {
                WebUri(attachment.Url, path + ".url");
                if (attachment.Url.IsAbsoluteUri && !(source.HttpHosts?.Any(host => host is not null && host.Purpose.HasFlag(HttpHostPurpose.OriginalMedia)
                    && string.Equals(host.Host, attachment.Url.IdnHost, StringComparison.OrdinalIgnoreCase)) ?? false))
                    Add(path + ".url", "host_not_allowed", "Attachment URL must use a declared original-media host.");
            }
            else if (!attachment.IsDeleted) Add(path + ".url", "missing_url", "A live attachment requires its original URL.");
            Text(attachment.OriginalFileName, path + ".originalFileName", 4096);
            Text(attachment.MimeType, path + ".mimeType", 256);
            if (attachment.ExpectedSize < 0) Add(path + ".expectedSize", "invalid_size", "Expected size must be nonnegative.");
            if (attachment.Width is <= 0) Add(path + ".width", "invalid_dimension", "Width must be positive when supplied.");
            if (attachment.Height is <= 0) Add(path + ".height", "invalid_dimension", "Height must be positive when supplied.");
            Extension(attachment.Extension, path + ".extension");
            Md5(attachment.ExpectedMd5, path + ".expectedMd5");
            Sha256(attachment.ExpectedSha256, path + ".expectedSha256");
            if (attachment.ExpectedSha1 is not null)
            {
                Span<byte> bytes = stackalloc byte[20];
                if (!Convert.TryFromBase64String(attachment.ExpectedSha1, bytes, out int written) || written != 20)
                    Add(path + ".expectedSha1", "invalid_hash", "SHA-1 must be base64 of 20 bytes.");
            }
            if (attachment.ProviderSizeHint is SourceSizeHint hint)
            {
                if (hint.RawValue is not { Length: > 0 and <= 64 }
                    || !decimal.TryParse(hint.RawValue, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out decimal raw) || raw < 0)
                    Add(path + ".providerSizeHint.rawValue", "invalid_size_hint", "Size hint must be bounded nonnegative invariant numeric text.");
                if (hint.UnitLabel is not { Length: > 0 and <= 32 })
                    Add(path + ".providerSizeHint.unitLabel", "invalid_size_hint", "Size hint unit label must contain 1–32 characters.");
                EnumValue(hint.Precision, path + ".providerSizeHint.precision");
            }
        }

        private void Extension(string? extension, string path)
        {
            if (extension is null) return;
            if (extension.Length is < 2 or > 33 || extension[0] != '.' || extension.Skip(1).Any(character => !char.IsAsciiLetterOrDigit(character)))
                Add(path, "invalid_extension", "Extension is one dot followed by 1–32 ASCII letters/digits, never a path.");
        }

        private void Md5(string? value, string path)
        {
            if (value is null) return;
            Span<byte> bytes = stackalloc byte[16];
            if (!Convert.TryFromBase64String(value, bytes, out int written) || written != 16)
                Add(path, "invalid_hash", "MD5 must be base64 of 16 bytes.");
        }

        internal void BooruSearchRequest(BooruSearchRequest? request, SourceDescription? source)
        {
            if (request is null || source is null) { Add("request", "missing", "Request and source description are required."); return; }
            Identity(request.SourceId, "request.sourceId");
            SameSource(request.SourceId, source.SourceId, "request.sourceId");
            RequirePostSearch(source, "request.sourceId");
            if (request.Query is null) Add("request.query", "missing", "A query is required, including an empty query.");
            else if (System.Text.Encoding.UTF8.GetByteCount(request.Query) > 1024 || request.Query.Any(char.IsControl))
                Add("request.query", "invalid_query", "Query must be at most 1024 UTF-8 bytes and contain no control characters.");
            if (request.Page is < 1 or > 1000) Add("request.page", "invalid_page", "Page must be between 1 and 1000.");
            if (request.Limit is < 1 or > 100) Add("request.limit", "invalid_limit", "Limit must be between 1 and 100.");
            if (request.BeforePostId is not null)
            {
                if (source.PostOrder != PostOrderKind.DecimalPostId)
                    Add("request.beforePostId", "cursor_unsupported", "Only a source with decimal post order accepts a search cursor.");
                BooruCursor(request.BeforePostId, "request.beforePostId");
                if (request.Page != 1) Add("request.page", "ambiguous_page", "A cursor request uses page 1; the cursor alone selects the position.");
            }
        }

        // A cursor is a post id the host received from the same source; it is never echoed in an issue.
        private void BooruCursor(string cursor, string path)
        {
            if (!CanonicalDecimal(cursor) || cursor.Length > SdkLimits.MaximumIdentifierLength)
                Add(path, "invalid_cursor", "A cursor is a positive decimal post id without leading zeros.");
        }

        // Positive decimal without leading zeros; such ids compare by length, then ordinally.
        private static bool CanonicalDecimal([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value)
            => !string.IsNullOrEmpty(value) && value[0] is >= '1' and <= '9' && value.All(char.IsAsciiDigit);

        private static int CompareDecimal(string left, string right)
            => left.Length != right.Length ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right);

        private void DecimalBooruPostId(BooruPostId? id, SourceDescription source, string path)
        {
            if (source.PostOrder == PostOrderKind.DecimalPostId && id?.PostId is not null && !CanonicalDecimal(id.PostId))
                Add(path + ".postId", "invalid_order", "This source declares decimal post ids: a positive decimal without leading zeros.");
        }

        internal void BooruPostRequest(BooruPostRequest? request, SourceDescription? source)
        {
            if (request is null || source is null) { Add("request", "missing", "Request and source description are required."); return; }
            BooruPostIdentity(request.Post, "request.post");
            SameSource(request.Post?.SourceId, source.SourceId, "request.post.sourceId");
            RequirePostSearch(source, "request.post.sourceId");
            DecimalBooruPostId(request.Post, source, "request.post");
        }

        private void RequirePostSearch(SourceDescription source, string path)
        {
            if (!source.Capabilities.HasFlag(SourceCapabilities.PostSearch))
                Add(path, "missing_capability", "The source does not declare post search.");
        }

        internal void BooruPostIdentity(BooruPostId? id, string path)
        {
            if (id is null) { Add(path, "missing", "Post identity is required."); return; }
            Identity(id.SourceId, path + ".sourceId");
            if (string.IsNullOrEmpty(id.PostId) || id.PostId.Length > SdkLimits.MaximumIdentifierLength
                || id.PostId.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
                Add(path + ".postId", "invalid_identity", "Post identity must be nonempty, bounded and contain no whitespace or control characters.");
        }

        internal void ExpectedBooruPost(BooruPostId? actual, BooruPostId? expected, string path)
        {
            BooruPostIdentity(actual, path);
            if (actual != expected) Add(path, "wrong_resource", "Result identifies another post.");
        }

        internal void BooruSearchPage(BooruSearchPage? page, BooruSearchRequest request, SourceDescription source, string path)
        {
            if (page is null) { Add(path, "missing", "Search page is required."); return; }
            if (page.SourceId != request.SourceId) Add(path + ".sourceId", "wrong_resource", "Page identifies another source.");
            if (page.Query != request.Query) Add(path + ".query", "wrong_resource", "Page does not echo the requested query.");
            if (page.Page != request.Page) Add(path + ".page", "wrong_resource", "Page does not echo the requested page number.");
            bool decimalOrder = source.PostOrder == PostOrderKind.DecimalPostId;
            bool cursorRequest = request.BeforePostId is not null && CanonicalDecimal(request.BeforePostId);
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            string? previous = null;
            List(page.Posts, path + ".posts", (post, itemPath) =>
            {
                BooruPost(post, source, itemPath);
                if (post?.Id is null) return;
                SameSource(post.Id.SourceId, request.SourceId, itemPath + ".id.sourceId");
                if (post.Id.PostId is not null && !ids.Add(post.Id.PostId)) Add(itemPath + ".id.postId", "duplicate_post", "Post identity is duplicated within the page.");
                if (!decimalOrder || !CanonicalDecimal(post.Id.PostId)) return;
                if (previous is not null && CompareDecimal(post.Id.PostId, previous) >= 0)
                    Add(itemPath + ".id.postId", "invalid_order", "A decimal-ordered source lists strictly descending post ids.");
                if (cursorRequest && CompareDecimal(post.Id.PostId, request.BeforePostId!) >= 0)
                    Add(itemPath + ".id.postId", "cursor_not_advanced", "A cursor page lists only posts below the cursor.");
                previous = post.Id.PostId;
            }, Math.Clamp(request.Limit, 0, 100));

            string? lowest = page.Posts is { Count: > 0 } posts ? posts[^1]?.Id?.PostId : null;
            if (page.NextBeforePostId is not null)
            {
                if (!decimalOrder)
                    Add(path + ".nextBeforePostId", "cursor_unsupported", "Only a source with decimal post order offers a search cursor.");
                BooruCursor(page.NextBeforePostId, path + ".nextBeforePostId");
                if (page.IsEnd)
                    Add(path + ".nextBeforePostId", "end_with_cursor", "A final page cannot offer a next cursor.");
                else if (lowest is null || page.NextBeforePostId != lowest)
                    Add(path + ".nextBeforePostId", "invalid_cursor", "The next cursor is the lowest post id of the page.");
            }
            else if (decimalOrder && !page.IsEnd && lowest is not null)
                Add(path + ".nextBeforePostId", "missing_cursor", "A decimal-ordered page that is not final names its next cursor.");
        }

        internal void BooruPost(BooruPost? post, SourceDescription source, string path)
        {
            if (post is null) { Add(path, "missing", "Post is required."); return; }
            BooruPostIdentity(post.Id, path + ".id");
            DecimalBooruPostId(post.Id, source, path + ".id");
            if ((post.Fields & ~BooruFieldPresence.All) != 0) Add(path + ".fields", "unknown_presence", "Unknown field-presence flag.");
            if ((!post.Fields.HasFlag(BooruFieldPresence.CreatedAt) && post.CreatedAt is not null)
                || (!post.Fields.HasFlag(BooruFieldPresence.Rating) && (post.Rating != BooruRating.Unknown || post.RawRating is not null))
                || (!post.Fields.HasFlag(BooruFieldPresence.Score) && post.Score is not null)
                || (!post.Fields.HasFlag(BooruFieldPresence.FavoriteCount) && post.FavoriteCount is not null)
                || (!post.Fields.HasFlag(BooruFieldPresence.Tags) && post.Tags is not null)
                || (!post.Fields.HasFlag(BooruFieldPresence.Media) && post.Media is not null)
                || (!post.Fields.HasFlag(BooruFieldPresence.SourceClaim) && post.SourceClaim is not null)
                || (!post.Fields.HasFlag(BooruFieldPresence.IsDeleted) && post.IsDeleted)
                || (!post.Fields.HasFlag(BooruFieldPresence.IsBanned) && post.IsBanned))
                Add(path + ".fields", "presence_mismatch", "A supplied value requires its presence flag.");
            if (post.PostUrl is not null)
            {
                HttpsUri(post.PostUrl, path + ".postUrl");
                if (post.PostUrl.IsAbsoluteUri && !DeclaresHost(source, post.PostUrl, HttpHostPurpose.Metadata))
                    Add(path + ".postUrl", "host_not_allowed", "Post URL must use a declared metadata host.");
            }
            EnumValue(post.Rating, path + ".rating");
            Text(post.RawRating, path + ".rawRating", 16);
            Text(post.SourceClaim, path + ".sourceClaim", 4096);
            if (post.FavoriteCount < 0) Add(path + ".favoriteCount", "invalid_count", "Favorite count must be nonnegative.");
            if (post.Fields.HasFlag(BooruFieldPresence.Tags))
                List(post.Tags, path + ".tags", (tag, tagPath) =>
                {
                    if (tag is null) { Add(tagPath, "missing", "Tag is required."); return; }
                    if (string.IsNullOrEmpty(tag.Name) || System.Text.Encoding.UTF8.GetByteCount(tag.Name) > 256
                        || tag.Name.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
                        Add(tagPath + ".name", "invalid_tag", "Tag name must be nonempty, at most 256 UTF-8 bytes and contain no whitespace or control characters.");
                    EnumValue(tag.Category, tagPath + ".category");
                }, 4096);
            if (post.Fields.HasFlag(BooruFieldPresence.Media))
            {
                HashSet<BooruMediaRole> roles = new HashSet<BooruMediaRole>();
                List(post.Media, path + ".media", (variant, variantPath) =>
                {
                    if (variant is null) { Add(variantPath, "missing", "Media variant is required."); return; }
                    EnumValue(variant.Role, variantPath + ".role");
                    if (!roles.Add(variant.Role)) Add(variantPath + ".role", "duplicate_role", "Each media role may appear at most once.");
                    if (variant.Url is not null)
                    {
                        HttpsUri(variant.Url, variantPath + ".url");
                        HttpHostPurpose purpose = variant.Role == BooruMediaRole.Original ? HttpHostPurpose.OriginalMedia : HttpHostPurpose.PreviewMedia;
                        if (variant.Url.IsAbsoluteUri && !DeclaresHost(source, variant.Url, purpose))
                            Add(variantPath + ".url", "host_not_allowed", "Media URL must use a host declared for its role's media purpose.");
                    }
                    Extension(variant.Extension, variantPath + ".extension");
                    if (variant.ExpectedSize < 0) Add(variantPath + ".expectedSize", "invalid_size", "Expected size must be nonnegative.");
                    if (variant.Width <= 0) Add(variantPath + ".width", "invalid_dimension", "Width must be positive.");
                    if (variant.Height <= 0) Add(variantPath + ".height", "invalid_dimension", "Height must be positive.");
                    Md5(variant.ExpectedMd5, variantPath + ".expectedMd5");
                }, 3);
            }
        }

        private void HttpsUri(Uri? uri, string path)
        {
            WebUri(uri, path);
            if (uri is not null && uri.IsAbsoluteUri && uri.Scheme != "https")
                Add(path, "invalid_url", "An absolute HTTPS URL without credentials is required.");
        }

        private static bool DeclaresHost(SourceDescription source, Uri uri, HttpHostPurpose purpose)
            => source.HttpHosts?.Any(host => host is not null && host.Purpose.HasFlag(purpose)
                && string.Equals(host.Host, uri.IdnHost, StringComparison.OrdinalIgnoreCase)) ?? false;

        internal void CatalogSnapshot(SourceCatalogSnapshot? snapshot, SourceDescription source, string path)
        {
            if (snapshot is null) { Add(path, "missing", "Catalog snapshot is required."); return; }
            BoardId(snapshot.Board, path + ".board");
            SameSource(snapshot.Board?.SourceId, source.SourceId, path + ".board.sourceId");
            Page(snapshot.NextCursor, snapshot.NextPage, path);
            if (snapshot.IsComplete && (snapshot.NextCursor is not null || snapshot.NextPage is not null)) Add(path + ".isComplete", "incomplete_catalog", "A complete observation cannot claim a next page.");
            Validators(snapshot.Validators, path + ".validators");
            HashSet<SourceThreadId> ids = new HashSet<SourceThreadId>();
            List(snapshot.Threads, path + ".threads", (thread, itemPath) =>
            {
                if (thread is null) { Add(itemPath, "missing", "Thread reference is required."); return; }
                ThreadId(thread.Id, itemPath + ".id");
                if (thread.Id?.SourceId != snapshot.Board?.SourceId || thread.Id?.BoardId != snapshot.Board?.BoardId) Add(itemPath + ".id", "wrong_resource", "Catalog thread belongs to another source or board.");
                if (thread.Id is not null && !ids.Add(thread.Id)) Add(itemPath + ".id", "duplicate_thread", "Catalog thread is duplicated.");
                SourceUri(thread.Url, source, thread.Id, itemPath + ".url");
                Text(thread.Title, itemPath + ".title", 16384);
                Text(thread.OpeningText, itemPath + ".openingText");
                EnumValue(thread.OpeningTextFormat, itemPath + ".openingTextFormat");
                if (thread.ReplyCount < 0 || thread.AttachmentCount < 0) Add(itemPath, "invalid_count", "Catalog counts must be nonnegative.");
            });
        }

        private void SourceUri(Uri uri, SourceDescription source, SourceThreadId? thread, string path)
        {
            WebUri(uri, path);
            if (uri is null || !uri.IsAbsoluteUri || thread is null || thread.BoardId is null || thread.ThreadId is null) return;
            UrlRoute? canonical = source.Routes?.FirstOrDefault(route => route is not null
                && route.Kind == SourceResourceKind.Thread && route.IsCanonical);
            if (canonical?.PathTemplate is null || source.BaseUri is null || !source.BaseUri.IsAbsoluteUri)
            { Add(path, "missing_canonical_route", "Source requires a canonical thread route."); return; }
            string boardId = canonical.LowercaseBoardId ? thread.BoardId.ToLowerInvariant() : thread.BoardId;
            string template = string.Join('/', canonical.PathTemplate.Split('/').Select(segment => RouteSegment.Parse(segment).Literal));
            string expectedPath = template.Replace("{board}", Uri.EscapeDataString(boardId), StringComparison.Ordinal)
                .Replace("{thread}", Uri.EscapeDataString(thread.ThreadId), StringComparison.Ordinal);
            string authority = source.BaseUri.Scheme + "://" + canonical.Host
                + (source.BaseUri.IsDefaultPort ? "" : ":" + source.BaseUri.Port);
            if (!MatchesIdentifier(boardId, canonical.BoardIdentifierKind) || !MatchesIdentifier(thread.ThreadId, canonical.ThreadIdentifierKind)
                || expectedPath.Split('/').Any(IsDotSegment)
                || !Uri.TryCreate(authority + expectedPath, UriKind.Absolute, out Uri? expected)
                || !string.Equals(uri.IdnHost, expected.IdnHost, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme != expected.Scheme || uri.Port != expected.Port || uri.AbsolutePath != expected.AbsolutePath
                || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                Add(path, "noncanonical_url", "Source URL must be the canonical route for the declared board and thread identity.");
        }

        internal void Validators(SourceValidators? validators, string path, bool required = false)
        {
            if (validators is null) { if (required) Add(path, "missing_validator", "Unchanged requires validator/revision identity."); return; }
            Text(validators.EntityTag, path + ".entityTag", 4096);
            Text(validators.Revision, path + ".revision", SdkLimits.MaximumIdentifierLength);
            if (required && validators.EntityTag is null && validators.LastModified is null && validators.Revision is null)
                Add(path, "missing_validator", "Unchanged requires validator/revision identity.");
        }

        internal void Page(string? cursor, int? page, string path)
        {
            Text(cursor, path + ".cursor", 4096);
            if (page < 0) Add(path + ".page", "invalid_page", "Page must be nonnegative.");
            if (cursor is not null && page is not null) Add(path, "ambiguous_page", "Use either page or cursor, not both.");
        }

        internal void HttpStatus(int? status, string path, bool required = false)
        {
            if ((required && status is null) || status is < 100 or > 599) Add(path, "invalid_http_status", "HTTP status must be between 100 and 599.");
        }

        internal void Failure(FailureDetail? failure, string path)
        {
            if (failure is null) { Add(path, "missing", "Failure detail is required."); return; }
            Identity(failure.Code, path + ".code");
            Text(failure.Message, path + ".message", SdkLimits.MaximumDiagnosticLength, required: true);
            HttpStatus(failure.HttpStatusCode, path + ".httpStatusCode");
        }

        internal void Deferred(DateTimeOffset retryAt, string reason, int? status, string path)
        {
            if (retryAt == default) Add(path + ".retryAt", "missing_due_time", "Deferred must identify an actual due time.");
            Identity(reason, path + ".reasonCode");
            HttpStatus(status, path + ".httpStatusCode");
        }

        internal void HttpRequest(HostHttpRequest? request, SourceDescription? source, string path)
        {
            if (request is null || source is null) { Add(path, "missing", "HTTP request and source description are required."); return; }
            Identity(request.SourceId, path + ".sourceId");
            SameSource(request.SourceId, source.SourceId, path + ".sourceId");
            EnumValue(request.Method, path + ".method");
            AllowedHttpUri(request.Url, source, path + ".url");
            Headers(request.Headers, path + ".headers", requestHeaders: true);
            Validators(request.Validators, path + ".validators");
        }

        internal void AllowedHttpUri(Uri? uri, SourceDescription? source, string path)
        {
            WebUri(uri, path);
            if (uri is null || !uri.IsAbsoluteUri) return;
            if (uri.Fragment.Length != 0) Add(path, "invalid_url", "HTTP request destinations do not contain fragments.");
            if (!(source?.HttpHosts?.Any(host => host is not null && host.Purpose.HasFlag(HttpHostPurpose.Metadata)
                && string.Equals(host.Host, uri.IdnHost, StringComparison.OrdinalIgnoreCase)) ?? false))
                Add(path, "host_not_allowed", "HTTP metadata host is not declared for this source.");
        }

        internal void Headers(IReadOnlyDictionary<string, string>? headers, string path, bool requestHeaders)
        {
            if (headers is null) return;
            if (headers.Count > 64) { Add(path, "too_large", "Too many headers."); return; }
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string name, string value) in headers)
            {
                if (!names.Add(name)) Add(path, "duplicate_header", "HTTP header names are case-insensitive.");
                if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')) Add(path, "invalid_header", "Invalid HTTP header name.");
                if (value is null || value.Length > 8192 || value.Contains('\r') || value.Contains('\n')) Add(path + "." + name, "invalid_header", "Invalid or oversized HTTP header value.");
                if (requestHeaders && !name.Equals("Accept", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("If-None-Match", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("If-Modified-Since", StringComparison.OrdinalIgnoreCase))
                    Add(path + "." + name, "header_not_allowed", "Only Accept and conditional-request headers are allowed; the host owns credentials, Host and User-Agent.");
            }
        }

        internal void Sha256(string? value, string path, bool required = false)
        {
            if (value is null) { if (required) Add(path, "missing_hash", "SHA-256 is required."); return; }
            if (value.Length != 64 || value.Any(character => !char.IsAsciiHexDigit(character))) Add(path, "invalid_hash", "SHA-256 requires exactly 64 hexadecimal characters.");
        }
    }
}

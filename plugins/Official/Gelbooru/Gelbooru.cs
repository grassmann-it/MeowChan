// This is a complete editable drop-in source: only framework + MeowChan.PluginSdk references.
// Gelbooru is a booru post source family (SDK 1.1.0) for the Gelbooru engine's "dapi" JSON API: Gelbooru
// and Safebooru, two sources of one plugin. Typed post search and single-post reads. It makes metadata
// requests only through IHostHttp, never retries, never downloads media and only uses media addresses on
// the declared hosts of the source. Every returned value is a provider claim; the host owns pacing,
// retries and the media queue.
//
// Order and paging: the engine lists a search by descending post id. A numbered request is sent as
// "pid=<page - 1>" (the engine counts pages from 0); a cursor request (BeforePostId) adds the engine's
// "id:<cursor>" meta tag to the query, which selects the posts below that id, again by descending id.
// NextBeforePostId is the lowest id of a page. A page that is not strictly descending (or not below its
// cursor) is reported as a failure instead of breaking that promise.
//
// Credentials: optional "user_id" and "api_key" source settings. Only when both are set they are sent as
// the engine's "user_id" and "api_key" query parameters (the engine accepts no header form). They are
// never logged, never echoed in a failure message and never part of a returned address.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.PluginSdk;

namespace MeowChan.OfficialSources.Gelbooru;

public sealed class GelbooruPlugin : IBooruSourcePlugin, IPluginSettingsValidator
{
    private static readonly SettingsSchema EmptySettings = new("1", Array.Empty<SettingDefinition>());
    private static readonly PluginDescription Description = new("meowchan.official.gelbooru", "Gelbooru family", "1.0.0", SdkContract.Version,
        "Official Gelbooru-engine sources (Gelbooru and Safebooru): typed post search and single-post metadata with original, sample and preview media claims.",
        GelbooruParser.Profiles.Select(profile => DescribeSource(profile)).ToArray(), Array.Empty<FilterDescription>(), EmptySettings,
        new InformationEntry[] { new("access", "Access and tags", InformationKind.Text,
            "Public read-only access. An API key and user id are optional source settings; when both are set they are sent to the provider as query parameters, " +
            "because the engine accepts no other form, and are stored by the host as ordinary settings. Tags are the provider's tags (source tags), never private tags, " +
            "and carry no category because the post list does not provide one. Ratings, scores, sizes and hashes are provider claims. " +
            "Original files are acquired by MeowChan's media queue, not by this plugin.") },
        Author: "MeowChan", DocumentationUrl: new Uri("https://github.com/grassmann-it/MeowChan"));

    private static SourceDescription DescribeSource(GelbooruParser.Profile profile)
    {
        List<HttpHostDeclaration> hosts = new() { new(profile.MetadataHost, HttpHostPurpose.Metadata | (profile.MediaHosts.Contains(profile.MetadataHost, StringComparer.Ordinal)
            ? HttpHostPurpose.OriginalMedia | HttpHostPurpose.PreviewMedia : HttpHostPurpose.None)) };
        foreach (string host in profile.MediaHosts)
            if (!string.Equals(host, profile.MetadataHost, StringComparison.Ordinal))
                hosts.Add(new(host, HttpHostPurpose.OriginalMedia | HttpHostPurpose.PreviewMedia));
        return new SourceDescription(profile.SourceId, profile.Name, new Uri($"https://{profile.MetadataHost}/"),
            SourceCapabilities.PostSearch | SourceCapabilities.OriginalMedia | SourceCapabilities.Previews,
            Array.Empty<UrlRoute>(), hosts, GelbooruParser.SourceSettings(), PostOrderKind.DecimalPostId)
        { Maturity = SourceMaturity.Experimental };
    }

    public PluginDescription Describe() => Description;

    public Task<IReadOnlyList<FieldValidationError>> ValidateSettingsAsync(SettingsValidationRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<FieldValidationError> errors = new();
        if (request?.Settings?.Values is null) return Task.FromResult<IReadOnlyList<FieldValidationError>>(errors);
        string? userId = GelbooruParser.TextSetting(request.Settings, GelbooruParser.UserIdKey);
        string? apiKey = GelbooruParser.TextSetting(request.Settings, GelbooruParser.ApiKeyKey);
        if (userId is not null && !userId.All(char.IsAsciiDigit))
            errors.Add(new FieldValidationError(GelbooruParser.UserIdKey, "invalid-user-id", "The user id is a decimal number."));
        if (apiKey is not null && !apiKey.All(char.IsAsciiLetterOrDigit))
            errors.Add(new FieldValidationError(GelbooruParser.ApiKeyKey, "invalid-api-key", "The API key has only letters and digits."));
        if ((userId is null) != (apiKey is null))
            errors.Add(new FieldValidationError(userId is null ? GelbooruParser.UserIdKey : GelbooruParser.ApiKeyKey, "credentials-incomplete",
                "The user id and the API key are used together; set both or neither."));
        return Task.FromResult<IReadOnlyList<FieldValidationError>>(errors);
    }

    public async Task<BooruSearchResult> SearchPostsAsync(BooruSearchRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        GelbooruParser.Profile? profile = request is null ? null : GelbooruParser.ProfileOf(request.SourceId);
        if (request is null || profile is null || SdkValidation.ValidateBooruSearchRequest(request, Description.Sources.First(s => s.SourceId == profile.SourceId)).Count != 0)
            return new BooruSearchFailure(request?.SourceId ?? GelbooruParser.Profiles[0].SourceId, new FailureDetail("invalid-request", "The search request is not valid for this source."));
        // Post ids fit a 64-bit integer; a longer cursor is not one of the engine's ids. Never echoed.
        if (request.BeforePostId is not null && !GelbooruParser.ValidPostId(request.BeforePostId))
            return new BooruSearchFailure(request.SourceId, new FailureDetail("invalid-cursor", "The cursor is not a post id of this source."));
        GelbooruParser.Settings settings = GelbooruParser.ReadSettings(context, profile);
        if (settings.CredentialsIncomplete)
            return new BooruSearchFailure(request.SourceId, new FailureDetail("credentials-incomplete", "Set both the user id and the API key, or neither."));
        string tags = GelbooruParser.SearchTags(request.Query, request.BeforePostId);
        string pid = request.BeforePostId is null ? (request.Page - 1).ToString(CultureInfo.InvariantCulture) : "0";
        HostHttpResult result = await context.Http.SendAsync(Get(profile, new Uri(
            $"https://{profile.MetadataHost}/index.php?page=dapi&s=post&q=index&json=1&tags={Uri.EscapeDataString(tags)}&pid={pid}&limit={request.Limit.ToString(CultureInfo.InvariantCulture)}"
            + settings.CredentialQuery)), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result is HostHttpDeferred deferred) return new BooruSearchDeferred(request.SourceId, deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode);
        if (result is HostHttpFailure failure) return new BooruSearchFailure(request.SourceId, failure.Failure);
        HostHttpResponse response = (HostHttpResponse)result;
        GelbooruParser.Outcome outcome = GelbooruParser.Classify(profile, response, settings.MaximumResponseBytes, notFoundIsAbsence: false);
        if (outcome.Deferred is not null) return new BooruSearchDeferred(request.SourceId, outcome.Deferred.RetryAt, outcome.Deferred.ReasonCode, outcome.Deferred.HttpStatusCode);
        if (outcome.Failure is not null) return new BooruSearchFailure(request.SourceId, outcome.Failure);
        try
        {
            GelbooruParser.Listing listing = GelbooruParser.ParseListing(profile, response.Body, request.Limit, settings, cancellationToken);
            if (!GelbooruParser.StrictlyDescending(listing.Posts, request.BeforePostId))
                return new BooruSearchFailure(request.SourceId, new FailureDetail("unexpected-order", "The provider did not list the posts by descending id."));
            // IsEnd is a heuristic: a page with fewer posts than the page size in force is taken as the
            // end. The provider may apply a smaller page size than requested, which it reports as "limit";
            // that value is honoured so that a capped full page is not mistaken for the end. Hosts treat
            // IsEnd as "stop this pass", never as proof that nothing else matches.
            int effective = listing.ProviderLimit is int providerLimit && providerLimit > 0 ? Math.Min(request.Limit, providerLimit) : request.Limit;
            bool isEnd = listing.Posts.Count < effective;
            string? next = isEnd || listing.Posts.Count == 0 ? null : listing.Posts[^1].Id.PostId;
            return new BooruSearchSnapshot(new BooruSearchPage(request.SourceId, request.Query, request.Page, listing.Posts, isEnd, next));
        }
        catch (GelbooruParser.ResponseException exception) { return new BooruSearchFailure(request.SourceId, exception.Detail); }
    }

    public async Task<BooruPostResult> ReadPostAsync(BooruPostRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        GelbooruParser.Profile? profile = request?.Post is null ? null : GelbooruParser.ProfileOf(request.Post.SourceId);
        if (request?.Post is null || profile is null)
            return new BooruPostFailure(request?.Post ?? new BooruPostId(GelbooruParser.Profiles[0].SourceId, string.Empty), new FailureDetail("invalid-request", "The post request is not valid for this source."));
        if (!GelbooruParser.ValidPostId(request.Post.PostId))
            return new BooruPostFailure(request.Post, new FailureDetail("post-id", "A post id is a positive decimal number without leading zeros."));
        GelbooruParser.Settings settings = GelbooruParser.ReadSettings(context, profile);
        if (settings.CredentialsIncomplete)
            return new BooruPostFailure(request.Post, new FailureDetail("credentials-incomplete", "Set both the user id and the API key, or neither."));
        HostHttpResult result = await context.Http.SendAsync(Get(profile, new Uri(
            $"https://{profile.MetadataHost}/index.php?page=dapi&s=post&q=index&json=1&id={request.Post.PostId}" + settings.CredentialQuery)),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result is HostHttpDeferred deferred) return new BooruPostDeferred(request.Post, deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode);
        if (result is HostHttpFailure failure) return new BooruPostFailure(request.Post, failure.Failure);
        HostHttpResponse response = (HostHttpResponse)result;
        GelbooruParser.Outcome outcome = GelbooruParser.Classify(profile, response, settings.MaximumResponseBytes, notFoundIsAbsence: true);
        if (outcome.Deferred is not null) return new BooruPostDeferred(request.Post, outcome.Deferred.RetryAt, outcome.Deferred.ReasonCode, outcome.Deferred.HttpStatusCode);
        if (outcome.NotFound) return new BooruPostNotFound(request.Post, 404);
        if (outcome.Failure is not null) return new BooruPostFailure(request.Post, outcome.Failure);
        try
        {
            // The engine answers an unknown id with HTTP 200 and an empty list: that is the verified absence.
            GelbooruParser.Listing listing = GelbooruParser.ParseListing(profile, response.Body, 1, settings, cancellationToken);
            if (listing.Posts.Count == 0) return new BooruPostNotFound(request.Post);
            BooruPost post = listing.Posts[0];
            return post.Id == request.Post ? new BooruPostSnapshot(post)
                : new BooruPostFailure(request.Post, new FailureDetail("wrong-post", "The response describes another post."));
        }
        catch (GelbooruParser.ResponseException exception) { return new BooruPostFailure(request.Post, exception.Detail); }
    }

    private static HostHttpRequest Get(GelbooruParser.Profile profile, Uri url) => new(profile.SourceId, HostHttpMethod.Get, url,
        new Dictionary<string, string> { ["Accept"] = "application/json" });
}

// Passive framework-only mapping of the engine's public JSON field names onto SDK booru records.
// A malformed post makes the whole response malformed: nothing is dropped, truncated or repaired silently.
internal static class GelbooruParser
{
    internal const string UserIdKey = "user_id";
    internal const string ApiKeyKey = "api_key";
    internal const string MaximumTagsKey = "maximum_tags_per_post";
    internal const string MaximumResponseKey = "maximum_response_kib";
    internal const int DefaultMaximumTags = 4096, MinimumMaximumTags = 64, MaximumMaximumTags = 16384;
    internal const int DefaultMaximumResponseKib = 8192, MinimumMaximumResponseKib = 64;
    internal const int MaximumResponseKibCeiling = SdkLimits.MaximumMetadataBytes / 1024;
    private const int DefaultThrottleSeconds = 60;
    private const int DefaultUnavailableSeconds = 5;
    private static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromHours(168);

    // MediaHosts are the exact hosts on which a returned media address must lie. LegacyPaths: the engine's
    // list does not always give addresses (Safebooru), then they are assembled from directory/image/hash.
    internal sealed record Profile(string SourceId, string Name, string MetadataHost, IReadOnlyList<string> MediaHosts, bool LegacyPaths);

    internal static readonly Profile[] Profiles =
    {
        new("gelbooru", "Gelbooru", "gelbooru.com",
            new[] { "img3.gelbooru.com", "img2.gelbooru.com", "img4.gelbooru.com", "video-cdn3.gelbooru.com", "video-cdn4.gelbooru.com" }, false),
        new("safebooru", "Safebooru", "safebooru.org", new[] { "safebooru.org" }, true)
    };

    internal static Profile? ProfileOf(string? sourceId) => Profiles.FirstOrDefault(profile => string.Equals(profile.SourceId, sourceId, StringComparison.Ordinal));

    internal static SettingsSchema SourceSettings() => new("1", new SettingDefinition[]
    {
        new(UserIdKey, SettingScope.Source, SettingKind.Text, "User id",
            "Optional provider account id. Used only together with the API key.", MaximumLength: 20),
        new(ApiKeyKey, SettingScope.Source, SettingKind.Text, "API key",
            "Optional provider API key. Used only together with the user id and sent as a query parameter.", MaximumLength: 128),
        new(MaximumTagsKey, SettingScope.Source, SettingKind.Integer, "Maximum tags per post",
            "A post with more tags is reported as unusable instead of being truncated.",
            Default: new SettingValue(MaximumTagsKey, SettingKind.Integer, Integer: DefaultMaximumTags),
            Minimum: MinimumMaximumTags, Maximum: MaximumMaximumTags),
        new(MaximumResponseKey, SettingScope.Source, SettingKind.Integer, "Maximum response size (KiB)",
            "A metadata response larger than this is reported as unusable. The plugin SDK ceiling is 8192 KiB (8 MiB).",
            Default: new SettingValue(MaximumResponseKey, SettingKind.Integer, Integer: DefaultMaximumResponseKib),
            Minimum: MinimumMaximumResponseKib, Maximum: MaximumResponseKibCeiling)
    });

    internal sealed record Settings(int MaximumTags, int MaximumResponseBytes, string CredentialQuery, bool CredentialsIncomplete);

    internal sealed record Listing(IReadOnlyList<BooruPost> Posts, int? ProviderLimit);

    internal static string? TextSetting(SettingsSnapshot? snapshot, string key)
    {
        SettingValue? value = snapshot?.Values?.FirstOrDefault(entry => entry is not null && entry.Key == key && entry.Kind == SettingKind.Text);
        string? text = value?.Text;
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static int IntSetting(SettingsSnapshot? snapshot, string key, int fallback, int minimum, int maximum)
    {
        SettingValue? value = snapshot?.Values?.FirstOrDefault(entry => entry is not null && entry.Key == key && entry.Kind == SettingKind.Integer);
        return value?.Integer is long number && number >= minimum && number <= maximum ? (int)number : fallback;
    }

    internal static Settings ReadSettings(PluginCallContext context, Profile profile)
    {
        SettingsSnapshot? snapshot = null;
        context.Configuration?.SourceSettings?.TryGetValue(profile.SourceId, out snapshot);
        string? userId = TextSetting(snapshot, UserIdKey);
        string? apiKey = TextSetting(snapshot, ApiKeyKey);
        bool incomplete = (userId is null) != (apiKey is null) || userId?.All(char.IsAsciiDigit) == false || apiKey?.All(char.IsAsciiLetterOrDigit) == false;
        string query = incomplete || userId is null || apiKey is null ? string.Empty
            : $"&api_key={Uri.EscapeDataString(apiKey)}&user_id={Uri.EscapeDataString(userId)}";
        int kib = IntSetting(snapshot, MaximumResponseKey, DefaultMaximumResponseKib, MinimumMaximumResponseKib, MaximumResponseKibCeiling);
        return new Settings(IntSetting(snapshot, MaximumTagsKey, DefaultMaximumTags, MinimumMaximumTags, MaximumMaximumTags), kib * 1024, query, incomplete);
    }

    internal sealed class ResponseException(FailureDetail detail) : Exception("The provider response is unusable.")
    {
        internal FailureDetail Detail { get; } = detail;
    }

    internal sealed record Outcome(HostHttpDeferred? Deferred, FailureDetail? Failure, bool NotFound);

    // Positive decimal without leading zeros, small enough to fit a 64-bit integer.
    internal static bool ValidPostId(string? id) => id is { Length: > 0 and <= 18 } && id[0] != '0' && id.All(char.IsAsciiDigit);

    // The engine's "id:<n>" meta tag selects the posts with a lower id.
    internal static string SearchTags(string query, string? before)
    {
        string trimmed = query.Trim();
        if (before is null) return trimmed;
        return trimmed.Length == 0 ? "id:<" + before : trimmed + " id:<" + before;
    }

    // Parsed ids are positive 64-bit integers, so they compare as such; a cursor is a valid post id.
    internal static bool StrictlyDescending(IReadOnlyList<BooruPost> posts, string? before)
    {
        long? previous = before is null ? null : long.Parse(before, NumberStyles.None, CultureInfo.InvariantCulture);
        foreach (BooruPost post in posts)
        {
            long id = long.Parse(post.Id.PostId, NumberStyles.None, CultureInfo.InvariantCulture);
            if (id >= previous) return false;
            previous = id;
        }
        return true;
    }

    private static DateTimeOffset Now() => TimeProvider.System.GetUtcNow();

    private static FailureDetail Failed(string code, string message, int? status = null) => new(code, message, status);
    private static ResponseException Malformed() => new(Failed("malformed", "The provider response is not the expected post data."));

    // Maps the HTTP status and content type. A null Failure/Deferred/NotFound means: parse the JSON body.
    internal static Outcome Classify(Profile profile, HostHttpResponse response, int maximumBytes, bool notFoundIsAbsence)
    {
        int status = response.StatusCode;
        if (status == 200)
        {
            if (response.Body is null || response.Body.Length > Math.Min(maximumBytes, SdkLimits.MaximumMetadataBytes))
                return new Outcome(null, Failed("too-large", "The response exceeds the metadata size limit.", status), false);
            // An empty body is the engine's way to say "no posts" on some deployments; it needs no content type.
            if (response.Body.Length > 0 && !LooksJson(response))
                return new Outcome(null, Failed("not-json", "The response is not JSON.", status), false);
            return new Outcome(null, null, false);
        }
        switch (status)
        {
            case 404:
                return notFoundIsAbsence ? new Outcome(null, null, true) : new Outcome(null, Failed("not-found", "The provider did not find the requested resource.", status), false);
            case 410:
                return notFoundIsAbsence ? new Outcome(null, Failed("http-410", "The provider returned HTTP 410.", status), false)
                    : new Outcome(null, Failed("page-limit", "The provider does not serve this page.", status), false);
            case 429:
                return new Outcome(new HostHttpDeferred(RetryAt(response.Headers, DefaultThrottleSeconds), "provider-throttled", status), null, false);
            case 502:
            case 503:
            case 504:
                return new Outcome(new HostHttpDeferred(RetryAt(response.Headers, DefaultUnavailableSeconds), "provider-unavailable", status), null, false);
            case 401:
            case 403:
                return new Outcome(null, Failed("access-denied", "The provider denied access; it may require a user id and API key in the source settings.", status), false);
            case >= 300 and < 400:
                return new Outcome(null, Failed("redirect", "The provider answered with a redirect, which is not followed.", status), false);
            default:
                return new Outcome(null, Failed("http-" + status.ToString(CultureInfo.InvariantCulture), "The provider returned an unexpected HTTP status.", status), false);
        }
    }

    private static string? Header(IReadOnlyDictionary<string, string>? headers, string name)
    {
        if (headers is null) return null;
        foreach ((string key, string value) in headers)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return value;
        return null;
    }

    // The engine sometimes labels its JSON text/html. A body counts as JSON when it is labelled so, or when
    // the label is text/html and the body starts like a JSON document (an HTML challenge page does not).
    private static bool LooksJson(HostHttpResponse response)
    {
        string? contentType = Header(response.Headers, "Content-Type");
        if (contentType is null) return false;
        int semicolon = contentType.IndexOf(';');
        string mediaType = (semicolon < 0 ? contentType : contentType[..semicolon]).Trim();
        if (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase) && !string.Equals(mediaType, "text/json", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (byte value in response.Body)
        {
            if (value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') continue;
            return value is (byte)'{' or (byte)'[';
        }
        return false;
    }

    private static DateTimeOffset RetryAt(IReadOnlyDictionary<string, string>? headers, int defaultSeconds)
    {
        DateTimeOffset now = Now();
        string? value = Header(headers, "Retry-After")?.Trim();
        if (!string.IsNullOrEmpty(value))
        {
            if (value.All(char.IsAsciiDigit))
            {
                bool parsed = long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds);
                return now + (!parsed || seconds >= MaximumRetryAfter.TotalSeconds ? MaximumRetryAfter : TimeSpan.FromSeconds(seconds));
            }
            else if (DateTimeOffset.TryParseExact(value, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset date))
            {
                DateTimeOffset due = date.ToUniversalTime();
                return due <= now ? now : due - now > MaximumRetryAfter ? now + MaximumRetryAfter : due;
            }
        }
        return now.AddSeconds(defaultSeconds);
    }

    private static JsonDocument Parse(byte[] body)
    {
        try { return JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException) { throw Malformed(); }
    }

    // Accepted shapes: {"@attributes":{...},"post":[...]} (also with one post object), the same without
    // "post" (no posts), a bare array (Safebooru) and an empty body (no posts on some deployments).
    internal static Listing ParseListing(Profile profile, byte[] body, int limit, Settings settings, CancellationToken cancellationToken)
    {
        if (body.All(value => value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) return new Listing(Array.Empty<BooruPost>(), null);
        using JsonDocument document = Parse(body);
        JsonElement root = document.RootElement;
        int? providerLimit = null;
        JsonElement posts;
        try
        {
            if (root.ValueKind == JsonValueKind.Array) posts = root;
            else if (root.ValueKind == JsonValueKind.Object)
            {
                bool hasAttributes = root.TryGetProperty("@attributes", out JsonElement attributes);
                if (hasAttributes)
                {
                    if (attributes.ValueKind != JsonValueKind.Object) throw Malformed();
                    if (attributes.TryGetProperty("limit", out JsonElement limitElement)) providerLimit = IntOrNull(limitElement);
                }
                if (root.TryGetProperty("post", out posts)) { if (posts.ValueKind == JsonValueKind.Null) return new Listing(Array.Empty<BooruPost>(), providerLimit); }
                else if (hasAttributes) return new Listing(Array.Empty<BooruPost>(), providerLimit);
                else throw Malformed();
            }
            else throw Malformed();
            List<JsonElement> elements = posts.ValueKind == JsonValueKind.Array ? posts.EnumerateArray().ToList()
                : posts.ValueKind == JsonValueKind.Object ? new List<JsonElement> { posts } : throw Malformed();
            if (elements.Count > limit) throw new ResponseException(Failed("too-many-posts", "The response holds more posts than the requested limit."));
            List<BooruPost> result = new();
            HashSet<string> ids = new(StringComparer.Ordinal);
            foreach (JsonElement element in elements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BooruPost post = ParsePost(profile, element, settings);
                if (!ids.Add(post.Id.PostId)) throw Malformed();
                result.Add(post);
            }
            return new Listing(result, providerLimit);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException or InvalidDataException)
        { throw Malformed(); }
    }

    private static BooruPost ParsePost(Profile profile, JsonElement element, Settings settings)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Malformed();
        if (!element.TryGetProperty("id", out JsonElement idElement) || LongOrNull(idElement) is not long id || id <= 0) throw Malformed();
        string postId = id.ToString(CultureInfo.InvariantCulture);
        BooruFieldPresence fields = BooruFieldPresence.None;

        DateTimeOffset? createdAt = null;
        if (element.TryGetProperty("created_at", out JsonElement created))
        {
            fields |= BooruFieldPresence.CreatedAt;
            string? text = StringOrNull(created);
            if (!string.IsNullOrEmpty(text)) createdAt = ParseTimestamp(text);
        }

        BooruRating rating = BooruRating.Unknown;
        string? rawRating = null;
        if (element.TryGetProperty("rating", out JsonElement ratingElement))
        {
            fields |= BooruFieldPresence.Rating;
            string? text = StringOrNull(ratingElement);
            if (text is not null)
            {
                rating = text.ToLowerInvariant() switch
                {
                    "g" or "general" or "safe" => BooruRating.General,
                    "s" or "sensitive" => BooruRating.Sensitive,
                    "q" or "questionable" => BooruRating.Questionable,
                    "e" or "explicit" => BooruRating.Explicit,
                    _ => BooruRating.Unknown
                };
                if (rating == BooruRating.Unknown)
                {
                    if (text.Length > 16) throw Malformed();
                    rawRating = text;
                }
            }
        }

        int? score = null;
        if (element.TryGetProperty("score", out JsonElement scoreElement)) { fields |= BooruFieldPresence.Score; score = IntOrNull(scoreElement); }

        IReadOnlyList<BooruTag>? tags = null;
        if (element.TryGetProperty("tags", out JsonElement tagsElement))
        {
            fields |= BooruFieldPresence.Tags;
            tags = ParseTags(StringOrNull(tagsElement) ?? string.Empty, settings.MaximumTags);
        }

        string? sourceClaim = null;
        if (element.TryGetProperty("source", out JsonElement sourceElement))
        {
            fields |= BooruFieldPresence.SourceClaim;
            sourceClaim = StringOrNull(sourceElement);
            if (sourceClaim?.Length > 4096) throw Malformed();
        }

        bool isDeleted = false;
        if (element.TryGetProperty("status", out JsonElement statusElement) && StringOrNull(statusElement) is string status)
        { fields |= BooruFieldPresence.IsDeleted; isDeleted = string.Equals(status, "deleted", StringComparison.OrdinalIgnoreCase); }

        IReadOnlyList<BooruMediaVariant>? media = ParseMedia(profile, element, ref fields);

        return new BooruPost(new BooruPostId(profile.SourceId, postId), fields, new Uri(PostPage(profile, postId)),
            createdAt, rating, rawRating, score, null, tags, media, sourceClaim, isDeleted, false);
    }

    private static string PostPage(Profile profile, string postId) => $"https://{profile.MetadataHost}/index.php?page=post&s=view&id={postId}";

    // The list carries one space-separated tag string without categories. Names arrive HTML-escaped
    // ("&#039;", "&amp;") and are stored decoded; the first occurrence of a name wins.
    private static IReadOnlyList<BooruTag> ParseTags(string text, int maximum)
    {
        List<BooruTag> tags = new();
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (string raw in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string name = WebUtility.HtmlDecode(raw);
            if (name.Length == 0 || Encoding.UTF8.GetByteCount(name) > 256 || name.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))) throw Malformed();
            if (!names.Add(name)) continue;
            if (tags.Count >= maximum) throw new ResponseException(Failed("too-many-tags", "A post holds more tags than the configured maximum."));
            tags.Add(new BooruTag(name, BooruTagCategory.Unknown));
        }
        return tags;
    }

    private static readonly string[] MediaProperties =
    {
        "file_url", "sample_url", "preview_url", "width", "height", "md5", "hash", "image", "directory"
    };

    private static IReadOnlyList<BooruMediaVariant>? ParseMedia(Profile profile, JsonElement element, ref BooruFieldPresence fields)
    {
        if (!MediaProperties.Any(property => element.TryGetProperty(property, out _))) return null;
        fields |= BooruFieldPresence.Media;
        List<BooruMediaVariant> variants = new();

        string? fileUrl = Text(element, "file_url");
        string? sampleUrl = Text(element, "sample_url");
        string? previewUrl = Text(element, "preview_url");
        string? md5 = Text(element, "md5") ?? Text(element, "hash");
        int? width = element.TryGetProperty("width", out JsonElement widthElement) ? IntOrNull(widthElement) : null;
        int? height = element.TryGetProperty("height", out JsonElement heightElement) ? IntOrNull(heightElement) : null;
        // Deleted or broken posts may carry a zero size: that is "unknown", not a malformed response.
        if (width <= 0) width = null;
        if (height <= 0) height = null;
        string? base64Md5 = string.IsNullOrEmpty(md5) ? null : Md5(md5);

        if (profile.LegacyPaths && string.IsNullOrEmpty(fileUrl))
        {
            // Safebooru's list names directory, file name and hash instead of addresses; assemble them
            // only from strictly validated parts on the declared host.
            string? directory = Text(element, "directory");
            string? image = Text(element, "image");
            if (SafePart(directory, 32, directoryPart: true) && SafePart(image, 128, directoryPart: false))
            {
                fileUrl = $"https://{profile.MetadataHost}/images/{directory}/{image}";
                if (base64Md5 is not null)
                {
                    string hex = md5!.ToLowerInvariant();
                    if (IsTrue(element, "sample")) sampleUrl ??= $"https://{profile.MetadataHost}/samples/{directory}/sample_{hex}.jpg";
                    previewUrl ??= $"https://{profile.MetadataHost}/thumbnails/{directory}/thumbnail_{hex}.jpg";
                }
            }
        }

        Uri? original = MediaUri(profile, fileUrl);
        string? extension = original is null ? null : Extension(original);
        if (original is not null || extension is not null || width is not null || height is not null || base64Md5 is not null)
            variants.Add(new BooruMediaVariant(BooruMediaRole.Original, original, extension, null, width, height, base64Md5));
        // The engine repeats the original address when a post has no sample: that is not a sample.
        if (!string.IsNullOrEmpty(sampleUrl) && !string.Equals(sampleUrl, fileUrl, StringComparison.Ordinal))
            variants.Add(new BooruMediaVariant(BooruMediaRole.Sample, MediaUri(profile, sampleUrl)));
        if (!string.IsNullOrEmpty(previewUrl))
            variants.Add(new BooruMediaVariant(BooruMediaRole.Preview, MediaUri(profile, previewUrl)));
        return variants;
    }

    private static bool SafePart(string? value, int maximum, bool directoryPart)
        => value is { Length: > 0 } && value.Length <= maximum && value != "." && value != ".."
           && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' || (!directoryPart && character == '.'));

    private static string? Extension(Uri uri)
    {
        string name = uri.AbsolutePath;
        int dot = name.LastIndexOf('.');
        if (dot < 0 || name.LastIndexOf('/') > dot) return null;
        string extension = name[(dot + 1)..];
        return extension is { Length: > 0 and <= 32 } && extension.All(char.IsAsciiLetterOrDigit) ? "." + extension.ToLowerInvariant() : null;
    }

    // Only an absolute default-port https address on a declared media host is used; anything else is dropped.
    private static Uri? MediaUri(Profile profile, string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 2048 || !Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)) return null;
        return uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.IsDefaultPort && uri.Fragment.Length == 0
            && profile.MediaHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase) ? uri : null;
    }

    private static string Md5(string hex)
    {
        if (hex.Length != 32 || !hex.All(char.IsAsciiHexDigit)) throw Malformed();
        return Convert.ToBase64String(Convert.FromHexString(hex));
    }

    private static readonly string[] Months = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

    // The engine's timestamp: "Thu Nov 20 12:00:00 -0600 2025" (weekday, month, day, time, offset, year).
    private static DateTimeOffset ParseTimestamp(string text)
    {
        string[] part = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (part.Length != 6) throw Malformed();
        int month = Array.IndexOf(Months, part[1]) + 1;
        string offsetText = part[4];
        if (month == 0 || offsetText.Length != 5 || offsetText[0] is not ('+' or '-')
            || !int.TryParse(offsetText.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int offsetHours)
            || !int.TryParse(offsetText.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int offsetMinutes)
            || offsetHours > 14 || offsetMinutes > 59
            || !int.TryParse(part[2], NumberStyles.None, CultureInfo.InvariantCulture, out int day)
            || !int.TryParse(part[5], NumberStyles.None, CultureInfo.InvariantCulture, out int year)
            || !TimeSpan.TryParseExact(part[3], "hh\\:mm\\:ss", CultureInfo.InvariantCulture, out TimeSpan time))
            throw Malformed();
        TimeSpan offset = new(offsetHours, offsetMinutes, 0);
        if (offsetText[0] == '-') offset = -offset;
        try { return new DateTimeOffset(new DateTime(year, month, day, time.Hours, time.Minutes, time.Seconds), offset); }
        catch (ArgumentException) { throw Malformed(); }
    }

    private static string? Text(JsonElement element, string property)
        => element.TryGetProperty(property, out JsonElement value) ? StringOrNull(value) : null;

    // "sample" is 0/1, a boolean or a "0"/"1"/"true" string depending on the deployment.
    private static bool IsTrue(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value)) return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => value.TryGetInt32(out int number) && number == 1,
            JsonValueKind.String => value.GetString() is "1" or "true",
            _ => false
        };
    }

    private static string? StringOrNull(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        _ => throw Malformed()
    };

    // Numbers are accepted as JSON numbers or as decimal strings: deployments differ.
    private static int? IntOrNull(JsonElement value)
    {
        long? number = LongOrNull(value);
        if (number is null) return null;
        if (number < int.MinValue || number > int.MaxValue) throw Malformed();
        return (int)number.Value;
    }

    private static long? LongOrNull(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)) return number;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long parsed)) return parsed;
        throw Malformed();
    }
}

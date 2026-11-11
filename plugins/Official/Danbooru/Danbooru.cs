// This is a complete editable drop-in source: only framework + MeowChan.PluginSdk references.
// Danbooru is a booru post source (SDK 1.1.0): typed post search and single-post reads. It makes
// metadata requests only through IHostHttp, never retries, never downloads media and never invents
// a CDN address. Every returned value is a provider claim; the host owns pacing, retries and the
// media queue.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.PluginSdk;

namespace MeowChan.OfficialSources.Danbooru;

public sealed class DanbooruPlugin : IBooruSourcePlugin
{
    private static readonly SettingsSchema EmptySettings = new("1", Array.Empty<SettingDefinition>());
    private static readonly PluginDescription Description = new("meowchan.official.danbooru", "Danbooru", "1.0.0", SdkContract.Version,
        "Official Danbooru source: typed post search and single-post metadata with original, sample and preview media claims.",
        new SourceDescription[]
        {
            new(DanbooruParser.SourceId, "Danbooru", new Uri("https://danbooru.donmai.us/"),
                SourceCapabilities.PostSearch | SourceCapabilities.OriginalMedia | SourceCapabilities.Previews,
                Array.Empty<UrlRoute>(),
                new HttpHostDeclaration[]
                {
                    new(DanbooruParser.MetadataHost, HttpHostPurpose.Metadata),
                    new(DanbooruParser.MediaHost, HttpHostPurpose.OriginalMedia | HttpHostPurpose.PreviewMedia)
                }, EmptySettings) { Maturity = SourceMaturity.Experimental }
        }, Array.Empty<FilterDescription>(), EmptySettings,
        new InformationEntry[] { new("access", "Access and tags", InformationKind.Text,
            "Public read-only access: no account or API key is used. Tags are Danbooru's provider tags (source tags), never private tags. " +
            "Ratings, scores, sizes and hashes are provider claims. Original files are acquired by MeowChan's media queue, not by this plugin.") },
        Author: "MeowChan", DocumentationUrl: new Uri("https://github.com/grassmann-it/MeowChan"));

    public PluginDescription Describe() => Description;

    public async Task<BooruSearchResult> SearchPostsAsync(BooruSearchRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        if (request is null || SdkValidation.ValidateBooruSearchRequest(request, Description.Sources[0]).Count != 0)
            return new BooruSearchFailure(request?.SourceId ?? DanbooruParser.SourceId, new FailureDetail("invalid-request", "The search request is not valid for this source."));
        HostHttpResult result = await context.Http.SendAsync(Get(new Uri(
            $"https://{DanbooruParser.MetadataHost}/posts.json?tags={Uri.EscapeDataString(request.Query)}&page={request.Page.ToString(CultureInfo.InvariantCulture)}&limit={request.Limit.ToString(CultureInfo.InvariantCulture)}")),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result is HostHttpDeferred deferred) return new BooruSearchDeferred(request.SourceId, deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode);
        if (result is HostHttpFailure failure) return new BooruSearchFailure(request.SourceId, failure.Failure);
        HostHttpResponse response = (HostHttpResponse)result;
        DanbooruParser.Outcome outcome = DanbooruParser.Classify(response, notFoundIsAbsence: false);
        if (outcome.Deferred is not null) return new BooruSearchDeferred(request.SourceId, outcome.Deferred.RetryAt, outcome.Deferred.ReasonCode, outcome.Deferred.HttpStatusCode);
        if (outcome.Failure is not null) return new BooruSearchFailure(request.SourceId, outcome.Failure);
        try
        {
            IReadOnlyList<BooruPost> posts = DanbooruParser.ParseSearch(response.Body, request.Limit, cancellationToken);
            return new BooruSearchSnapshot(new BooruSearchPage(request.SourceId, request.Query, request.Page, posts, posts.Count < request.Limit));
        }
        catch (DanbooruParser.ResponseException exception) { return new BooruSearchFailure(request.SourceId, exception.Detail); }
    }

    public async Task<BooruPostResult> ReadPostAsync(BooruPostRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        if (request?.Post is null || request.Post.SourceId != DanbooruParser.SourceId)
            return new BooruPostFailure(request?.Post ?? new SourcePostId(DanbooruParser.SourceId, string.Empty), new FailureDetail("invalid-request", "The post request is not valid for this source."));
        if (!DanbooruParser.ValidPostId(request.Post.PostId))
            return new BooruPostFailure(request.Post, new FailureDetail("post-id", "A post id is a positive decimal number without leading zeros."));
        HostHttpResult result = await context.Http.SendAsync(Get(new Uri(
            $"https://{DanbooruParser.MetadataHost}/posts/{request.Post.PostId}.json")), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result is HostHttpDeferred deferred) return new BooruPostDeferred(request.Post, deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode);
        if (result is HostHttpFailure failure) return new BooruPostFailure(request.Post, failure.Failure);
        HostHttpResponse response = (HostHttpResponse)result;
        DanbooruParser.Outcome outcome = DanbooruParser.Classify(response, notFoundIsAbsence: true);
        if (outcome.Deferred is not null) return new BooruPostDeferred(request.Post, outcome.Deferred.RetryAt, outcome.Deferred.ReasonCode, outcome.Deferred.HttpStatusCode);
        if (outcome.NotFound) return new BooruPostNotFound(request.Post, 404);
        if (outcome.Failure is not null) return new BooruPostFailure(request.Post, outcome.Failure);
        try
        {
            BooruPost post = DanbooruParser.ParseSinglePost(response.Body, cancellationToken);
            return post.Id == request.Post ? new BooruPostSnapshot(post)
                : new BooruPostFailure(request.Post, new FailureDetail("wrong-post", "The response describes another post."));
        }
        catch (DanbooruParser.ResponseException exception) { return new BooruPostFailure(request.Post, exception.Detail); }
    }

    private static HostHttpRequest Get(Uri url) => new(DanbooruParser.SourceId, HostHttpMethod.Get, url,
        new Dictionary<string, string> { ["Accept"] = "application/json" });
}

// Passive framework-only mapping of Danbooru's public JSON field names onto SDK booru records.
// Tag order inside a post: artist, copyright, character, general, meta (then the provider's own order
// inside each field); the first occurrence of a name wins. A malformed post makes the whole response
// malformed: nothing is dropped, truncated or repaired silently.
internal static class DanbooruParser
{
    internal const string SourceId = "danbooru";
    internal const string MetadataHost = "danbooru.donmai.us";
    internal const string MediaHost = "cdn.donmai.us";
    private const int MaximumTags = 4096;
    private const int DefaultThrottleSeconds = 60;
    private const int DefaultUnavailableSeconds = 5;
    private static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromHours(24);

    internal sealed class ResponseException(FailureDetail detail) : Exception("The Danbooru response is unusable.")
    {
        internal FailureDetail Detail { get; } = detail;
    }

    internal sealed record Outcome(HostHttpDeferred? Deferred, FailureDetail? Failure, bool NotFound);

    private static readonly (string Property, BooruTagCategory Category)[] TagFields =
    {
        ("tag_string_artist", BooruTagCategory.Artist), ("tag_string_copyright", BooruTagCategory.Copyright),
        ("tag_string_character", BooruTagCategory.Character), ("tag_string_general", BooruTagCategory.General),
        ("tag_string_meta", BooruTagCategory.Meta)
    };

    private static readonly string[] MediaProperties =
    {
        "file_url", "large_file_url", "preview_file_url", "file_ext", "file_size", "image_width", "image_height", "md5"
    };

    // Positive decimal without leading zeros, small enough to fit a 64-bit integer.
    internal static bool ValidPostId(string? id) => id is { Length: > 0 and <= 18 } && id[0] != '0' && id.All(char.IsAsciiDigit);

    private static DateTimeOffset Now() => TimeProvider.System.GetUtcNow();

    private static FailureDetail Failed(string code, string message, int? status = null) => new(code, message, status);
    private static ResponseException Malformed() => new(Failed("malformed", "The Danbooru response is not the expected post data."));

    // Maps the HTTP status and content type. A null Failure/Deferred/NotFound means: parse the JSON body.
    internal static Outcome Classify(HostHttpResponse response, bool notFoundIsAbsence)
    {
        int status = response.StatusCode;
        if (status == 200)
        {
            if (response.Body is null || response.Body.Length > SdkLimits.MaximumMetadataBytes)
                return new Outcome(null, Failed("too-large", "The response exceeds the metadata size limit.", status), false);
            if (!IsJson(response.Headers))
                return new Outcome(null, Failed("not-json", "The response is not JSON.", status), false);
            return new Outcome(null, null, false);
        }
        switch (status)
        {
            case 404:
                return notFoundIsAbsence ? new Outcome(null, null, true) : new Outcome(null, Failed("not-found", "Danbooru did not find the requested resource.", status), false);
            case 410:
                return notFoundIsAbsence ? new Outcome(null, Failed("http-410", "Danbooru returned HTTP 410.", status), false)
                    : new Outcome(null, Failed("page-limit", "Danbooru does not serve this page.", status), false);
            case 429:
                return new Outcome(new HostHttpDeferred(RetryAt(response.Headers, DefaultThrottleSeconds), "provider-throttled", status), null, false);
            case 502:
            case 503:
                return new Outcome(new HostHttpDeferred(RetryAt(response.Headers, DefaultUnavailableSeconds), "provider-unavailable", status), null, false);
            case 401:
            case 403:
                return new Outcome(null, Failed("access-denied", "Danbooru denied access to this public request.", status), false);
            case >= 300 and < 400:
                return new Outcome(null, Failed("redirect", "Danbooru answered with a redirect, which is not followed.", status), false);
            default:
                return new Outcome(null, Failed("http-" + status.ToString(CultureInfo.InvariantCulture), "Danbooru returned an unexpected HTTP status.", status), false);
        }
    }

    private static string? Header(IReadOnlyDictionary<string, string>? headers, string name)
    {
        if (headers is null) return null;
        foreach ((string key, string value) in headers)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return value;
        return null;
    }

    private static bool IsJson(IReadOnlyDictionary<string, string>? headers)
    {
        string? contentType = Header(headers, "Content-Type");
        if (contentType is null) return false;
        int semicolon = contentType.IndexOf(';');
        string mediaType = (semicolon < 0 ? contentType : contentType[..semicolon]).Trim();
        return string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTimeOffset RetryAt(IReadOnlyDictionary<string, string>? headers, int defaultSeconds)
    {
        DateTimeOffset now = Now();
        string? value = Header(headers, "Retry-After")?.Trim();
        if (!string.IsNullOrEmpty(value))
        {
            if (value.All(char.IsAsciiDigit))
            {
                // Too many digits for a 64-bit integer is simply "very long": the 24 hour cap applies.
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

    internal static IReadOnlyList<BooruPost> ParseSearch(byte[] body, int limit, CancellationToken cancellationToken)
    {
        using JsonDocument document = Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw Malformed();
        if (document.RootElement.GetArrayLength() > limit)
            throw new ResponseException(Failed("too-many-posts", "The response holds more posts than the requested limit."));
        List<BooruPost> posts = new();
        HashSet<string> ids = new(StringComparer.Ordinal);
        try
        {
            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                BooruPost post = ParsePost(element);
                if (!ids.Add(post.Id.PostId)) throw Malformed();
                posts.Add(post);
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException or InvalidDataException)
        { throw Malformed(); }
        return posts;
    }

    internal static BooruPost ParseSinglePost(byte[] body, CancellationToken cancellationToken)
    {
        using JsonDocument document = Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw Malformed();
        cancellationToken.ThrowIfCancellationRequested();
        try { return ParsePost(document.RootElement); }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException or InvalidDataException)
        { throw Malformed(); }
    }

    private static BooruPost ParsePost(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Malformed();
        if (!element.TryGetProperty("id", out JsonElement idElement) || idElement.ValueKind != JsonValueKind.Number
            || !idElement.TryGetInt64(out long id) || id <= 0) throw Malformed();
        string postId = id.ToString(CultureInfo.InvariantCulture);
        BooruFieldPresence fields = BooruFieldPresence.None;

        DateTimeOffset? createdAt = null;
        if (element.TryGetProperty("created_at", out JsonElement created))
        {
            fields |= BooruFieldPresence.CreatedAt;
            string? text = StringOrNull(created);
            if (text is not null) createdAt = ParseTimestamp(text);
        }

        BooruRating rating = BooruRating.Unknown;
        string? rawRating = null;
        if (element.TryGetProperty("rating", out JsonElement ratingElement))
        {
            fields |= BooruFieldPresence.Rating;
            string? text = StringOrNull(ratingElement);
            if (text is not null)
            {
                rating = text switch { "g" => BooruRating.General, "s" => BooruRating.Sensitive, "q" => BooruRating.Questionable, "e" => BooruRating.Explicit, _ => BooruRating.Unknown };
                if (rating == BooruRating.Unknown)
                {
                    if (text.Length > 16) throw Malformed();
                    rawRating = text;
                }
            }
        }

        int? score = null;
        if (element.TryGetProperty("score", out JsonElement scoreElement)) { fields |= BooruFieldPresence.Score; score = IntOrNull(scoreElement); }
        int? favorites = null;
        if (element.TryGetProperty("fav_count", out JsonElement favoriteElement))
        {
            fields |= BooruFieldPresence.FavoriteCount;
            favorites = IntOrNull(favoriteElement);
            if (favorites < 0) throw Malformed();
        }

        IReadOnlyList<BooruTag>? tags = ParseTags(element, ref fields);

        string? sourceClaim = null;
        if (element.TryGetProperty("source", out JsonElement sourceElement))
        {
            fields |= BooruFieldPresence.SourceClaim;
            sourceClaim = StringOrNull(sourceElement);
            if (sourceClaim?.Length > 4096) throw Malformed();
        }

        bool isDeleted = false;
        if (element.TryGetProperty("is_deleted", out JsonElement deletedElement) && BoolOrNull(deletedElement) is bool deleted)
        { fields |= BooruFieldPresence.IsDeleted; isDeleted = deleted; }
        bool isBanned = false;
        if (element.TryGetProperty("is_banned", out JsonElement bannedElement) && BoolOrNull(bannedElement) is bool banned)
        { fields |= BooruFieldPresence.IsBanned; isBanned = banned; }

        IReadOnlyList<BooruMediaVariant>? media = ParseMedia(element, ref fields);

        return new BooruPost(new SourcePostId(SourceId, postId), fields, new Uri($"https://{MetadataHost}/posts/{postId}"),
            createdAt, rating, rawRating, score, favorites, tags, media, sourceClaim, isDeleted, isBanned);
    }

    private static IReadOnlyList<BooruTag>? ParseTags(JsonElement element, ref BooruFieldPresence fields)
    {
        List<(string Text, BooruTagCategory Category)> sources = new();
        foreach ((string property, BooruTagCategory category) in TagFields)
            if (element.TryGetProperty(property, out JsonElement value)) sources.Add((StringOrNull(value) ?? string.Empty, category));
        if (sources.Count == 0 && element.TryGetProperty("tag_string", out JsonElement all))
            sources.Add((StringOrNull(all) ?? string.Empty, BooruTagCategory.Unknown));
        if (sources.Count == 0) return null;
        fields |= BooruFieldPresence.Tags;
        List<BooruTag> tags = new();
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach ((string text, BooruTagCategory category) in sources)
            foreach (string name in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (Encoding.UTF8.GetByteCount(name) > 256 || name.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))) throw Malformed();
                if (!names.Add(name)) continue;
                if (tags.Count >= MaximumTags) throw Malformed();
                tags.Add(new BooruTag(name, category));
            }
        return tags;
    }

    private static IReadOnlyList<BooruMediaVariant>? ParseMedia(JsonElement element, ref BooruFieldPresence fields)
    {
        if (!MediaProperties.Any(property => element.TryGetProperty(property, out _))) return null;
        fields |= BooruFieldPresence.Media;
        List<BooruMediaVariant> variants = new();

        string? fileUrl = Text(element, "file_url");
        string? largeUrl = Text(element, "large_file_url");
        string? previewUrl = Text(element, "preview_file_url");
        string? extension = Text(element, "file_ext");
        string? md5 = Text(element, "md5");
        long? size = element.TryGetProperty("file_size", out JsonElement sizeElement) ? LongOrNull(sizeElement) : null;
        int? width = element.TryGetProperty("image_width", out JsonElement widthElement) ? IntOrNull(widthElement) : null;
        int? height = element.TryGetProperty("image_height", out JsonElement heightElement) ? IntOrNull(heightElement) : null;
        if (size < 0 || width <= 0 || height <= 0) throw Malformed();
        string? base64Md5 = md5 is null ? null : Md5(md5);
        string? dotted = extension is { Length: > 0 and <= 32 } && extension.All(char.IsAsciiLetterOrDigit) ? "." + extension : null;

        if (!string.IsNullOrEmpty(fileUrl) || dotted is not null || size is not null || width is not null || height is not null || base64Md5 is not null)
            variants.Add(new BooruMediaVariant(BooruMediaRole.Original, CdnUri(fileUrl), dotted, size, width, height, base64Md5));
        // Danbooru repeats the original address when a post has no sample: that is not a sample.
        if (!string.IsNullOrEmpty(largeUrl) && !string.Equals(largeUrl, fileUrl, StringComparison.Ordinal))
            variants.Add(new BooruMediaVariant(BooruMediaRole.Sample, CdnUri(largeUrl)));
        if (!string.IsNullOrEmpty(previewUrl))
            variants.Add(new BooruMediaVariant(BooruMediaRole.Preview, CdnUri(previewUrl)));
        return variants;
    }

    // Only an absolute default-port https address on the declared media host is used; anything else is dropped.
    private static Uri? CdnUri(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 2048 || !Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)) return null;
        return uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.IsDefaultPort && uri.Fragment.Length == 0
            && string.Equals(uri.IdnHost, MediaHost, StringComparison.OrdinalIgnoreCase) ? uri : null;
    }

    private static string Md5(string hex)
    {
        if (hex.Length != 32 || !hex.All(char.IsAsciiHexDigit)) throw Malformed();
        return Convert.ToBase64String(Convert.FromHexString(hex));
    }

    private static readonly string[] TimestampFormats =
    {
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK"
    };

    private static DateTimeOffset ParseTimestamp(string text)
        => DateTimeOffset.TryParseExact(text, TimestampFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset value)
            ? value : throw Malformed();

    private static string? Text(JsonElement element, string property)
        => element.TryGetProperty(property, out JsonElement value) ? StringOrNull(value) : null;

    private static string? StringOrNull(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        _ => throw Malformed()
    };

    private static int? IntOrNull(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)) return number;
        throw Malformed();
    }

    private static long? LongOrNull(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)) return number;
        throw Malformed();
    }

    private static bool? BoolOrNull(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw Malformed()
    };
}

// This is a complete editable drop-in source: only framework + MeowChan.PluginSdk references.
// e621 and its safe-for-work mirror e926 are booru post sources (SDK 1.1.0): typed post search and
// single-post reads. One plugin declares two sources, "e621" and "e926" (e926 shows only rating s posts;
// the provider enforces that, this plugin never relabels a post). The plugin makes metadata requests only
// through IHostHttp, never retries, never downloads media and never invents a CDN address. The host owns
// the User-Agent, credentials and pacing (e621 asks for at most 2 requests per second; the host spaces
// booru requests by one second), so this plugin has no account, API key or setting of its own and reads
// public data only. Every returned value is a provider claim.
//
// Order and paging: e621 lists a search by descending post id unless the query reorders it (the accepted
// query syntax has no order term) and its "page=b<id>" form returns the posts below <id>, again by
// descending id. The sources therefore declare DecimalPostId: a cursor request (BeforePostId) is sent as
// "page=b<id>", a numbered request keeps "page=<n>", and NextBeforePostId is the lowest id of a page. A
// page that is not strictly descending (or not below its cursor) is reported as a failure.
//
// SDK 1.1.0 has no species or lore tag category. Those tags are mapped to General (their text stays the
// provider's own tag); the category is a display hint, never a filter.
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

namespace MeowChan.OfficialSources.E621;

public sealed class E621Plugin : IBooruSourcePlugin
{
    private static readonly SettingsSchema EmptySettings = new("1", Array.Empty<SettingDefinition>());
    private static readonly PluginDescription Description = new("meowchan.official.e621", "e621 and e926", "1.0.0", SdkContract.Version,
        "Official e621 and e926 source: typed post search and single-post metadata with original, sample and preview media claims.",
        new SourceDescription[] { Describe(E621Parser.E621), Describe(E621Parser.E926) },
        Array.Empty<FilterDescription>(), EmptySettings,
        new InformationEntry[] { new("access", "Access and tags", InformationKind.Text,
            "Public read-only access: no account or API key is used. Tags are e621's provider tags (source tags), never private tags. " +
            "Species and lore tags are shown as general tags. Ratings, scores, sizes and hashes are provider claims. " +
            "e926 is the provider's safe-for-work view; it lists only rating s posts. " +
            "Original files are acquired by MeowChan's media queue, not by this plugin.") },
        Author: "MeowChan", DocumentationUrl: new Uri("https://github.com/grassmann-it/MeowChan"));

    private static SourceDescription Describe(E621Parser.Profile profile) =>
        new(profile.SourceId, profile.Name, new Uri($"https://{profile.MetadataHost}/"),
            SourceCapabilities.PostSearch | SourceCapabilities.OriginalMedia | SourceCapabilities.Previews,
            Array.Empty<UrlRoute>(),
            new HttpHostDeclaration[]
            {
                new(profile.MetadataHost, HttpHostPurpose.Metadata),
                new(E621Parser.MediaHost, HttpHostPurpose.OriginalMedia | HttpHostPurpose.PreviewMedia)
            }, EmptySettings, PostOrderKind.DecimalPostId) { Maturity = SourceMaturity.Experimental };

    public PluginDescription Describe() => Description;

    public async Task<BooruSearchResult> SearchPostsAsync(BooruSearchRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        E621Parser.Profile? profile = request is null ? null : E621Parser.Find(request.SourceId);
        if (request is null || profile is null || SdkValidation.ValidateBooruSearchRequest(request, Description.Sources.Single(source => source.SourceId == profile.SourceId)).Count != 0)
            return new BooruSearchFailure(request?.SourceId ?? E621Parser.E621.SourceId, new FailureDetail("invalid-request", "The search request is not valid for this source."));
        // e621 post ids fit a 64-bit integer; a longer cursor is not one of its ids. Never echoed.
        if (request.BeforePostId is not null && !E621Parser.ValidPostId(request.BeforePostId))
            return new BooruSearchFailure(request.SourceId, new FailureDetail("invalid-cursor", "The cursor is not an e621 post id."));
        string page = request.BeforePostId is null
            ? request.Page.ToString(CultureInfo.InvariantCulture)
            : E621Parser.BeforeCursor(request.BeforePostId);
        HostHttpResult result = await context.Http.SendAsync(Get(profile, new Uri(
            $"https://{profile.MetadataHost}/posts.json?tags={Uri.EscapeDataString(request.Query)}&page={page}&limit={request.Limit.ToString(CultureInfo.InvariantCulture)}")),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result is HostHttpDeferred deferred) return new BooruSearchDeferred(request.SourceId, deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode);
        if (result is HostHttpFailure failure) return new BooruSearchFailure(request.SourceId, failure.Failure);
        HostHttpResponse response = (HostHttpResponse)result;
        E621Parser.Outcome outcome = E621Parser.Classify(response, notFoundIsAbsence: false);
        if (outcome.Deferred is not null) return new BooruSearchDeferred(request.SourceId, outcome.Deferred.RetryAt, outcome.Deferred.ReasonCode, outcome.Deferred.HttpStatusCode);
        if (outcome.Failure is not null) return new BooruSearchFailure(request.SourceId, outcome.Failure);
        try
        {
            IReadOnlyList<BooruPost> posts = E621Parser.ParseSearch(profile, response.Body, request.Limit, cancellationToken);
            if (!E621Parser.StrictlyDescending(posts, request.BeforePostId))
                return new BooruSearchFailure(request.SourceId, new FailureDetail("unexpected-order", "e621 did not list the posts by descending id."));
            // IsEnd is a heuristic: the response carries no "last page" marker, so a page with fewer posts
            // than requested is taken as the end. A full page is never the end and continues below its
            // lowest id. Hosts treat IsEnd as "stop this pass", never as proof that nothing else matches.
            bool isEnd = posts.Count < request.Limit;
            string? next = isEnd ? null : posts[^1].Id.PostId;
            return new BooruSearchSnapshot(new BooruSearchPage(request.SourceId, request.Query, request.Page, posts, isEnd, next));
        }
        catch (E621Parser.ResponseException exception) { return new BooruSearchFailure(request.SourceId, exception.Detail); }
    }

    public async Task<BooruPostResult> ReadPostAsync(BooruPostRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        E621Parser.Profile? profile = request?.Post is null ? null : E621Parser.Find(request.Post.SourceId);
        if (request?.Post is null || profile is null)
            return new BooruPostFailure(request?.Post ?? new BooruPostId(E621Parser.E621.SourceId, string.Empty), new FailureDetail("invalid-request", "The post request is not valid for this source."));
        if (!E621Parser.ValidPostId(request.Post.PostId))
            return new BooruPostFailure(request.Post, new FailureDetail("post-id", "A post id is a positive decimal number without leading zeros."));
        HostHttpResult result = await context.Http.SendAsync(Get(profile, new Uri(
            $"https://{profile.MetadataHost}/posts/{request.Post.PostId}.json")), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result is HostHttpDeferred deferred) return new BooruPostDeferred(request.Post, deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode);
        if (result is HostHttpFailure failure) return new BooruPostFailure(request.Post, failure.Failure);
        HostHttpResponse response = (HostHttpResponse)result;
        E621Parser.Outcome outcome = E621Parser.Classify(response, notFoundIsAbsence: true);
        if (outcome.Deferred is not null) return new BooruPostDeferred(request.Post, outcome.Deferred.RetryAt, outcome.Deferred.ReasonCode, outcome.Deferred.HttpStatusCode);
        if (outcome.NotFound) return new BooruPostNotFound(request.Post, 404);
        if (outcome.Failure is not null) return new BooruPostFailure(request.Post, outcome.Failure);
        try
        {
            BooruPost post = E621Parser.ParseSinglePost(profile, response.Body, cancellationToken);
            return post.Id == request.Post ? new BooruPostSnapshot(post)
                : new BooruPostFailure(request.Post, new FailureDetail("wrong-post", "The response describes another post."));
        }
        catch (E621Parser.ResponseException exception) { return new BooruPostFailure(request.Post, exception.Detail); }
    }

    private static HostHttpRequest Get(E621Parser.Profile profile, Uri url) => new(profile.SourceId, HostHttpMethod.Get, url,
        new Dictionary<string, string> { ["Accept"] = "application/json" });
}

// Passive framework-only mapping of e621's public JSON field names onto SDK booru records.
// A search answers {"posts":[...]}, a single post {"post":{...}}. Tag order inside a post: artist,
// copyright, character, species, general, meta, lore (then the provider's own order inside each list);
// the first occurrence of a name wins. A malformed post makes the whole response malformed: nothing is
// dropped, truncated or repaired silently.
internal static class E621Parser
{
    internal const string MediaHost = "static1.e621.net";
    private const int MaximumTags = 4096;
    private const int DefaultThrottleSeconds = 60;
    private const int DefaultUnavailableSeconds = 5;
    private static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromHours(168);

    internal sealed record Profile(string SourceId, string Name, string MetadataHost);

    internal static readonly Profile E621 = new("e621", "e621", "e621.net");
    internal static readonly Profile E926 = new("e926", "e926", "e926.net");

    internal static Profile? Find(string? sourceId) => sourceId switch { "e621" => E621, "e926" => E926, _ => null };

    internal sealed class ResponseException(FailureDetail detail) : Exception("The e621 response is unusable.")
    {
        internal FailureDetail Detail { get; } = detail;
    }

    internal sealed record Outcome(HostHttpDeferred? Deferred, FailureDetail? Failure, bool NotFound);

    private static readonly (string Property, BooruTagCategory Category)[] TagFields =
    {
        ("artist", BooruTagCategory.Artist), ("copyright", BooruTagCategory.Copyright),
        ("character", BooruTagCategory.Character), ("species", BooruTagCategory.General),
        ("general", BooruTagCategory.General), ("meta", BooruTagCategory.Meta), ("lore", BooruTagCategory.General)
    };

    // Positive decimal without leading zeros, small enough to fit a 64-bit integer.
    internal static bool ValidPostId(string? id) => id is { Length: > 0 and <= 18 } && id[0] != '0' && id.All(char.IsAsciiDigit);

    // e621's "before id" page value: the posts with a lower id, by descending id.
    internal static string BeforeCursor(string postId) => "b" + postId;

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
    private static ResponseException Malformed() => new(Failed("malformed", "The e621 response is not the expected post data."));

    // Maps the HTTP status and content type. A null Failure/Deferred/NotFound means: parse the JSON body.
    // 421 and 429 are both e621's throttle answers.
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
                return notFoundIsAbsence ? new Outcome(null, null, true) : new Outcome(null, Failed("not-found", "e621 did not find the requested resource.", status), false);
            case 410:
                return notFoundIsAbsence ? new Outcome(null, Failed("http-410", "e621 returned HTTP 410.", status), false)
                    : new Outcome(null, Failed("page-limit", "e621 does not serve this page.", status), false);
            case 421:
            case 429:
                return new Outcome(new HostHttpDeferred(RetryAt(response.Headers, DefaultThrottleSeconds), "provider-throttled", status), null, false);
            case 502:
            case 503:
            case 504:
                return new Outcome(new HostHttpDeferred(RetryAt(response.Headers, DefaultUnavailableSeconds), "provider-unavailable", status), null, false);
            case 401:
            case 403:
                return new Outcome(null, Failed("access-denied", "e621 denied access to this public request.", status), false);
            case >= 300 and < 400:
                return new Outcome(null, Failed("redirect", "e621 answered with a redirect, which is not followed.", status), false);
            default:
                return new Outcome(null, Failed("http-" + status.ToString(CultureInfo.InvariantCulture), "e621 returned an unexpected HTTP status.", status), false);
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
                // Too many digits for a 64-bit integer is simply "very long": the host-aligned 168 hour cap applies.
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

    internal static IReadOnlyList<BooruPost> ParseSearch(Profile profile, byte[] body, int limit, CancellationToken cancellationToken)
    {
        using JsonDocument document = Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("posts", out JsonElement array) || array.ValueKind != JsonValueKind.Array) throw Malformed();
        if (array.GetArrayLength() > limit)
            throw new ResponseException(Failed("too-many-posts", "The response holds more posts than the requested limit."));
        List<BooruPost> posts = new();
        HashSet<string> ids = new(StringComparer.Ordinal);
        try
        {
            foreach (JsonElement element in array.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                BooruPost post = ParsePost(profile, element);
                if (!ids.Add(post.Id.PostId)) throw Malformed();
                posts.Add(post);
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException or InvalidDataException)
        { throw Malformed(); }
        return posts;
    }

    internal static BooruPost ParseSinglePost(Profile profile, byte[] body, CancellationToken cancellationToken)
    {
        using JsonDocument document = Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("post", out JsonElement element)) throw Malformed();
        cancellationToken.ThrowIfCancellationRequested();
        try { return ParsePost(profile, element); }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException or InvalidDataException)
        { throw Malformed(); }
    }

    private static BooruPost ParsePost(Profile profile, JsonElement element)
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

        // e621 ratings: s (safe), q (questionable), e (explicit). "s" is the provider's safe rating, which
        // is the SDK's General, not Sensitive.
        BooruRating rating = BooruRating.Unknown;
        string? rawRating = null;
        if (element.TryGetProperty("rating", out JsonElement ratingElement))
        {
            fields |= BooruFieldPresence.Rating;
            string? text = StringOrNull(ratingElement);
            if (text is not null)
            {
                rating = text switch { "s" => BooruRating.General, "q" => BooruRating.Questionable, "e" => BooruRating.Explicit, _ => BooruRating.Unknown };
                if (rating == BooruRating.Unknown)
                {
                    if (text.Length > 16) throw Malformed();
                    rawRating = text;
                }
            }
        }

        // "score" is an object {up, down, total}; only "total" is read.
        int? score = null;
        if (element.TryGetProperty("score", out JsonElement scoreElement))
        {
            fields |= BooruFieldPresence.Score;
            if (scoreElement.ValueKind == JsonValueKind.Object)
                score = scoreElement.TryGetProperty("total", out JsonElement total) ? IntOrNull(total) : null;
            else if (scoreElement.ValueKind != JsonValueKind.Null) throw Malformed();
        }
        int? favorites = null;
        if (element.TryGetProperty("fav_count", out JsonElement favoriteElement))
        {
            fields |= BooruFieldPresence.FavoriteCount;
            favorites = IntOrNull(favoriteElement);
            if (favorites < 0) throw Malformed();
        }

        IReadOnlyList<BooruTag>? tags = ParseTags(element, ref fields);

        // "sources" is a list of free-text claims; the first one is the claim. Inert text, never fetched.
        string? sourceClaim = null;
        if (element.TryGetProperty("sources", out JsonElement sourcesElement))
        {
            fields |= BooruFieldPresence.SourceClaim;
            if (sourcesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in sourcesElement.EnumerateArray())
                {
                    string? text = StringOrNull(entry);
                    if (sourceClaim is null && !string.IsNullOrEmpty(text)) sourceClaim = text;
                }
            }
            else if (sourcesElement.ValueKind != JsonValueKind.Null) throw Malformed();
            if (sourceClaim?.Length > 4096) throw Malformed();
        }

        bool isDeleted = false;
        if (element.TryGetProperty("flags", out JsonElement flags) && flags.ValueKind == JsonValueKind.Object
            && flags.TryGetProperty("deleted", out JsonElement deletedElement) && BoolOrNull(deletedElement) is bool deleted)
        { fields |= BooruFieldPresence.IsDeleted; isDeleted = deleted; }

        IReadOnlyList<BooruMediaVariant>? media = ParseMedia(element, ref fields);

        return new BooruPost(new BooruPostId(profile.SourceId, postId), fields, new Uri($"https://{profile.MetadataHost}/posts/{postId}"),
            createdAt, rating, rawRating, score, favorites, tags, media, sourceClaim, isDeleted, false);
    }

    private static IReadOnlyList<BooruTag>? ParseTags(JsonElement element, ref BooruFieldPresence fields)
    {
        if (!element.TryGetProperty("tags", out JsonElement tagObject)) return null;
        if (tagObject.ValueKind == JsonValueKind.Null) return null;
        if (tagObject.ValueKind != JsonValueKind.Object) throw Malformed();
        fields |= BooruFieldPresence.Tags;
        List<BooruTag> tags = new();
        HashSet<string> names = new(StringComparer.Ordinal);
        // The "invalid" list holds retired tags and is deliberately not read.
        foreach ((string property, BooruTagCategory category) in TagFields)
        {
            if (!tagObject.TryGetProperty(property, out JsonElement list) || list.ValueKind == JsonValueKind.Null) continue;
            if (list.ValueKind != JsonValueKind.Array) throw Malformed();
            foreach (JsonElement item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) throw Malformed();
                string name = item.GetString() ?? throw Malformed();
                if (name.Length == 0 || Encoding.UTF8.GetByteCount(name) > 256 || name.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))) throw Malformed();
                if (!names.Add(name)) continue;
                if (tags.Count >= MaximumTags) throw Malformed();
                tags.Add(new BooruTag(name, category));
            }
        }
        return tags;
    }

    private static IReadOnlyList<BooruMediaVariant>? ParseMedia(JsonElement element, ref BooruFieldPresence fields)
    {
        JsonElement file = default, sample = default, preview = default;
        bool hasFile = Section(element, "file", ref file), hasSample = Section(element, "sample", ref sample), hasPreview = Section(element, "preview", ref preview);
        if (!hasFile && !hasSample && !hasPreview) return null;
        fields |= BooruFieldPresence.Media;
        List<BooruMediaVariant> variants = new();

        string? fileUrl = null;
        if (hasFile)
        {
            fileUrl = Text(file, "url");
            string? extension = Text(file, "ext");
            string? md5 = Text(file, "md5");
            long? size = file.TryGetProperty("size", out JsonElement sizeElement) ? LongOrNull(sizeElement) : null;
            int? width = file.TryGetProperty("width", out JsonElement widthElement) ? IntOrNull(widthElement) : null;
            int? height = file.TryGetProperty("height", out JsonElement heightElement) ? IntOrNull(heightElement) : null;
            if (size < 0 || width <= 0 || height <= 0) throw Malformed();
            string? base64Md5 = md5 is null ? null : Md5(md5);
            string? dotted = extension is { Length: > 0 and <= 32 } && extension.All(char.IsAsciiLetterOrDigit) ? "." + extension : null;
            if (!string.IsNullOrEmpty(fileUrl) || dotted is not null || size is not null || width is not null || height is not null || base64Md5 is not null)
                variants.Add(new BooruMediaVariant(BooruMediaRole.Original, CdnUri(fileUrl), dotted, size, width, height, base64Md5));
        }
        // A post without a sample answers "has": false and a null url; the original address is not a sample.
        if (hasSample && !(sample.TryGetProperty("has", out JsonElement has) && BoolOrNull(has) == false))
        {
            string? sampleUrl = Text(sample, "url");
            if (!string.IsNullOrEmpty(sampleUrl) && !string.Equals(sampleUrl, fileUrl, StringComparison.Ordinal))
                variants.Add(new BooruMediaVariant(BooruMediaRole.Sample, CdnUri(sampleUrl)));
        }
        if (hasPreview)
        {
            string? previewUrl = Text(preview, "url");
            if (!string.IsNullOrEmpty(previewUrl))
                variants.Add(new BooruMediaVariant(BooruMediaRole.Preview, CdnUri(previewUrl)));
        }
        return variants;
    }

    // A media section is an object; null counts as absent, anything else is malformed.
    private static bool Section(JsonElement element, string name, ref JsonElement section)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return false;
        if (value.ValueKind != JsonValueKind.Object) throw Malformed();
        section = value;
        return true;
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

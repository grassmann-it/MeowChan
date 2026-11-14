// Editable single-file example. References only the .NET framework and MeowChan.PluginSdk.
// One plugin serves several jschan sites: the site table below is the only per-site code.
// Evidence and limitations are documented in the adjacent README.md and the Sites directory.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.PluginSdk;

namespace MeowChan.Plugins.Examples.JschanFamily;

public sealed class JschanFamilyPlugin : ISourcePlugin
{
    public const string PluginId = "meowchan.source.jschan";
    private static readonly SettingsSchema EmptySchema = new SettingsSchema("1", Array.Empty<SettingDefinition>());

    // Adding a site: one entry here, plus evidence under Sites/<Site>/ and fixture tests.
    // Source IDs are persistent identities (watches, archives, rules): never rename one.
    // jschan answers JSON on every route, so a site needs no per-site parsing switches.
    private sealed record Site(string SourceId, string Name, string Host, SourceMaturity Maturity)
    {
        internal Uri Origin { get; } = new Uri("https://" + Host + "/");
    }

    private static readonly Site[] Sites =
    [
        new Site("zzzchan", "ZZZchan", "zzzchan.xyz", SourceMaturity.Experimental)
    ];

    private static readonly IReadOnlyDictionary<string, (Site Site, SourceDescription Description)> BySource =
        Sites.ToDictionary(site => site.SourceId, site => (site, Describe(site)), StringComparer.Ordinal);

    public PluginDescription Describe() => new PluginDescription(PluginId, "jschan family",
        "1.0.0", SdkContract.Version,
        "Reads threads and board catalogs of jschan imageboards, including post text and original-file links.",
        Sites.Select(site => BySource[site.SourceId].Description).ToArray(), Array.Empty<FilterDescription>(), EmptySchema,
        new[]
        {
            new InformationEntry("support", "Accepted links", InformationKind.Text,
                "Accepts board and thread links from " + string.Join(", ", Sites.Select(site => site.Host)) + "."),
            new InformationEntry("limitations", "Limitations", InformationKind.Text,
                "No automatic board list or previews. Catalogs may be incomplete. Archived threads are not detected.")
        }.Concat(Sites.Select(site => new InformationEntry("site." + site.SourceId, site.Name, InformationKind.Text,
            "Accepts board and thread links from " + site.Host + ".", SourceId: site.SourceId))).ToArray(),
        Author: "MeowChan contributors", DocumentationUrl: new Uri("https://fatchan.gitgud.site/jschan-docs/"));

    private static SourceDescription Describe(Site site) => new SourceDescription(site.SourceId, site.Name, site.Origin,
        SourceCapabilities.ThreadRead | SourceCapabilities.BoardCatalog | SourceCapabilities.PostCapture | SourceCapabilities.OriginalMedia,
        [new UrlRoute(SourceResourceKind.Board, site.Host, "/{board}/", IsCanonical: true),
         new UrlRoute(SourceResourceKind.Board, site.Host, "/{board}/index.html", AllowTrailingSlash: false),
         new UrlRoute(SourceResourceKind.Thread, site.Host, "/{board}/thread/{thread}.html", IsCanonical: true, AllowTrailingSlash: false) { ThreadIdentifierKind = RouteIdentifierKind.Decimal }],
        [new HttpHostDeclaration(site.Host, HttpHostPurpose.Metadata | HttpHostPurpose.OriginalMedia)],
        EmptySchema, PostOrderKind.DecimalPostId) { Maturity = site.Maturity };

    public async Task<CatalogReadResult> ReadCatalogAsync(CatalogReadRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!BySource.TryGetValue(request.Board.SourceId, out var known) || !BoardValid(request.Board.BoardId))
            return new CatalogFailure(request.Board, new FailureDetail("invalid_resource", "Source or board identity is invalid."));
        (Site site, SourceDescription description) = known;
        if (request.Cursor is not null || request.Page is not null)
            return new CatalogFailure(request.Board, new FailureDetail("unsupported_page", "This endpoint returns its catalog in one response; no cursor or page is supported."));
        Uri endpoint = Endpoint(site, request.Board.BoardId, "catalog.json");
        HostHttpResult result = await context.Http.SendAsync(new HostHttpRequest(site.SourceId, HostHttpMethod.Get,
            endpoint, Headers: new Dictionary<string, string> { ["Accept"] = "application/json" },
            Validators: request.HasSnapshot ? request.Validators : null), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result is HostHttpDeferred deferred) return new CatalogDeferred(request.Board, deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode);
        if (result is HostHttpFailure failure) return new CatalogFailure(request.Board, failure.Failure);
        if (result is not HostHttpResponse response) return new CatalogFailure(request.Board, new FailureDetail("invalid_http_result", "Unknown host HTTP result."));
        if (!SameEndpoint(endpoint, response.FinalUri)) return new CatalogFailure(request.Board, new FailureDetail("unexpected_endpoint", "The response is not from the requested metadata endpoint.", response.StatusCode));
        if (response.StatusCode == 304)
            return request.HasSnapshot && HasHttpValidator(request.Validators)
                ? new CatalogUnchanged(request.Board, request.Validators!)
                : new CatalogFailure(request.Board, new FailureDetail("missing_baseline", "304 requires an accepted catalog and its HTTP validator.", 304));
        if (response.StatusCode != 200) return new CatalogFailure(request.Board, HttpFailure(response.StatusCode));
        if (response.Body.Length > SdkLimits.MaximumMetadataBytes) return new CatalogFailure(request.Board, TooLarge());
        try
        {
            using JsonDocument document = JsonDocument.Parse(response.Body);
            CatalogSnapshot snapshot = ParseCatalog(site, request.Board, document.RootElement, response.Validators);
            if (SdkValidation.ValidateCatalogResult(request, snapshot, description).Count != 0)
                return new CatalogFailure(request.Board, InvalidPayload());
            return snapshot;
        }
        catch (Exception error) when (PayloadError(error)) { return new CatalogFailure(request.Board, InvalidPayload()); }
    }

    public async Task<ThreadReadResult> ReadThreadAsync(ThreadReadRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!BySource.TryGetValue(request.Thread.SourceId, out var known) || !BoardValid(request.Thread.BoardId) || !DecimalIdValid(request.Thread.ThreadId))
            return new ThreadFailure(request.Thread, new FailureDetail("invalid_resource", "Source, board or thread identity is invalid."));
        (Site site, SourceDescription description) = known;
        Uri endpoint = Endpoint(site, request.Thread.BoardId, "thread/" + Uri.EscapeDataString(request.Thread.ThreadId) + ".json");
        HostHttpResult result = await context.Http.SendAsync(new HostHttpRequest(site.SourceId, HostHttpMethod.Get,
            endpoint, Headers: new Dictionary<string, string> { ["Accept"] = "application/json" },
            Validators: request.HasSnapshot ? request.Validators : null), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result is HostHttpDeferred deferred) return new ThreadDeferred(request.Thread, deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode);
        if (result is HostHttpFailure failure) return new ThreadFailure(request.Thread, failure.Failure);
        if (result is not HostHttpResponse response) return new ThreadFailure(request.Thread, new FailureDetail("invalid_http_result", "Unknown host HTTP result."));
        if (!SameEndpoint(endpoint, response.FinalUri)) return new ThreadFailure(request.Thread, new FailureDetail("unexpected_endpoint", "The response is not from the requested thread endpoint.", response.StatusCode));
        // Only this exact thread endpoint's 404 is observed. 410 is deliberately not inferred.
        if (response.StatusCode == 404) return new ThreadNotFound(request.Thread, 404);
        if (response.StatusCode == 304)
            return request.HasSnapshot && HasHttpValidator(request.Validators)
                ? new ThreadUnchanged(request.Thread, request.Validators!)
                : new ThreadFailure(request.Thread, new FailureDetail("missing_baseline", "304 requires an accepted thread and its HTTP validator.", 304));
        if (response.StatusCode != 200) return new ThreadFailure(request.Thread, HttpFailure(response.StatusCode));
        if (response.Body.Length > SdkLimits.MaximumMetadataBytes) return new ThreadFailure(request.Thread, TooLarge());
        try
        {
            using JsonDocument document = JsonDocument.Parse(response.Body);
            ThreadSnapshot snapshot = ParseThread(site, request.Thread, document.RootElement, response.Validators);
            if (SdkValidation.ValidateThreadResult(request, snapshot, description).Count != 0)
                return new ThreadFailure(request.Thread, InvalidPayload());
            return snapshot;
        }
        catch (Exception error) when (PayloadError(error)) { return new ThreadFailure(request.Thread, InvalidPayload()); }
    }

    private static CatalogSnapshot ParseCatalog(Site site, SourceBoardId board, JsonElement root, SourceValidators? validators)
    {
        List<SourceThreadReference> threads = new List<SourceThreadReference>();
        ArrayBudget(root, SdkLimits.MaximumCollectionCount);
        foreach (JsonElement entry in root.EnumerateArray())
        {
            CheckBoard(entry, board.BoardId);
            if (entry.TryGetProperty("thread", out JsonElement parent) && parent.ValueKind != JsonValueKind.Null)
                throw new JsonException("A catalog entry must be a thread opener.");
            SourceThreadId thread = new SourceThreadId(site.SourceId, board.BoardId, Id(entry, "postId"));
            (string? text, SourceTextFormat format) = Body(entry);
            threads.Add(new SourceThreadReference(thread, ThreadUri(site, thread), Text(entry, "subject"), text,
                format, PostedAt(entry), Count(entry, "replyposts"), Count(entry, "replyfiles")));
        }
        // An omitted thread is not a terminal observation or proof of exhaustive coverage.
        return new CatalogSnapshot(new SourceCatalogSnapshot(board, threads.AsReadOnly(), IsComplete: false, Validators: validators));
    }

    private static ThreadSnapshot ParseThread(Site site, SourceThreadId thread, JsonElement root, SourceValidators? validators)
    {
        if (Id(root, "postId") != thread.ThreadId) throw new JsonException("The opener belongs to another thread.");
        List<SourcePost> posts = new List<SourcePost> { Post(site, thread, root, true) };
        if (root.TryGetProperty("replies", out JsonElement replies))
        {
            ArrayBudget(replies, SdkLimits.MaximumCollectionCount - 1);
            foreach (JsonElement reply in replies.EnumerateArray()) posts.Add(Post(site, thread, reply, false));
        }
        return new ThreadSnapshot(new SourceThreadSnapshot(thread, ThreadUri(site, thread), Text(root, "subject"),
            SourceThreadState.Active, posts.AsReadOnly(), validators));
    }

    private static SourcePost Post(Site site, SourceThreadId thread, JsonElement entry, bool opener)
    {
        CheckBoard(entry, thread.BoardId);
        string postId = Id(entry, "postId");
        string? parent = entry.TryGetProperty("thread", out JsonElement value) && value.ValueKind != JsonValueKind.Null
            ? Id(entry, "thread") : null;
        if (opener ? parent != null : parent != null && parent != thread.ThreadId)
            throw new JsonException("Post belongs to another thread.");
        SourceFieldPresence fields = Present(entry, "date", SourceFieldPresence.PostedAt)
            | Present(entry, "subject", SourceFieldPresence.Subject) | Present(entry, "name", SourceFieldPresence.Author);
        if (entry.TryGetProperty("message", out _) || entry.TryGetProperty("nomarkup", out _)) fields |= SourceFieldPresence.Text;
        (string? text, SourceTextFormat format) = Body(entry);
        List<SourceAttachment>? attachments = null;
        if (entry.TryGetProperty("files", out JsonElement files))
        {
            fields |= SourceFieldPresence.Attachments;
            ArrayBudget(files, 1024);
            attachments = new List<SourceAttachment>();
            int position = 0;
            foreach (JsonElement file in files.EnumerateArray()) attachments.Add(Attachment(site, thread, file, postId, position++));
        }
        // Public decimal postId is authoritative. _id, array order and file hashes are not post identities.
        return new SourcePost(postId, 0, fields, PostedAt(entry), Text(entry, "subject"), Text(entry, "name"),
            text, format, Attachments: attachments?.AsReadOnly());
    }

    private static SourceAttachment Attachment(Site site, SourceThreadId thread, JsonElement file, string postId, int position)
    {
        string token = file.GetProperty("filename").GetString() ?? throw new JsonException("Missing provider filename.");
        if (token.Length == 0 || token.Length > 256 || token is "." or ".."
            || token.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
            throw new JsonException("Invalid provider file token.");
        string extension = file.GetProperty("extension").GetString() ?? throw new JsonException("Missing extension.");
        string? hash = Text(file, "hash");
        // phash is perceptual and must never substitute for the SHA-256 integrity hash.
        return new SourceAttachment(position, postId + "-" + position.ToString(CultureInfo.InvariantCulture),
            Url: new Uri(site.Origin, "file/" + Uri.EscapeDataString(token)),
            OriginalFileName: Text(file, "originalFilename"), Extension: extension,
            ExpectedSize: Integer(file, "size"), ExpectedSha256: hash, MimeType: Text(file, "mimetype"));
    }

    private static void CheckBoard(JsonElement entry, string board)
    {
        if (entry.TryGetProperty("board", out JsonElement value) && value.GetString() != board)
            throw new JsonException("Post belongs to another board.");
    }
    private static (string? Text, SourceTextFormat Format) Body(JsonElement entry) => entry.TryGetProperty("message", out _)
        ? (Text(entry, "message"), SourceTextFormat.Html) : (Text(entry, "nomarkup"), SourceTextFormat.PlainText);
    private static DateTimeOffset? PostedAt(JsonElement item)
    {
        string? value = Text(item, "date");
        if (value == null) return null;
        string[] formats = { "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:ss'Z'",
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mm:sszzz" };
        if (!DateTimeOffset.TryParseExact(value, formats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset date))
            throw new JsonException("Invalid zoned ISO post date.");
        return date;
    }

    private static Uri Endpoint(Site site, string board, string suffix) => new Uri(site.Origin, Uri.EscapeDataString(board) + "/" + suffix);
    private static Uri ThreadUri(Site site, SourceThreadId thread) => Endpoint(site, thread.BoardId, "thread/" + Uri.EscapeDataString(thread.ThreadId) + ".html");
    private static bool SameEndpoint(Uri expected, Uri actual) => actual.IsAbsoluteUri && actual.Scheme == expected.Scheme
        && actual.IdnHost == expected.IdnHost && actual.Port == expected.Port && actual.AbsolutePath == expected.AbsolutePath
        && actual.Query.Length == 0 && actual.Fragment.Length == 0 && actual.UserInfo.Length == 0;
    private static bool BoardValid(string board) => !string.IsNullOrWhiteSpace(board) && board.Length <= 128
        && board == board.Trim() && board is not "." and not ".." && !board.Any(char.IsControl)
        && board.IndexOfAny(['/', '\\', '?', '#']) < 0;
    private static bool DecimalIdValid(string id) => !string.IsNullOrEmpty(id) && id.Length <= SdkLimits.MaximumIdentifierLength
        && id.All(character => character is >= '0' and <= '9') && (id.Length == 1 || id[0] != '0');
    private static bool HasHttpValidator(SourceValidators? validators) => !string.IsNullOrWhiteSpace(validators?.EntityTag) || validators?.LastModified is not null;
    private static FailureDetail InvalidPayload() => new FailureDetail("invalid_payload", "The metadata response has an invalid JSON shape or violates the source contract.", 200);
    private static FailureDetail TooLarge() => new FailureDetail("metadata_too_large", "The metadata response exceeds the SDK body limit.", 200);
    private static FailureDetail HttpFailure(int status) => new FailureDetail("http_failure", "Metadata requires HTTP 200; this status is not a verified thread-end signal.", status);
    private static bool PayloadError(Exception error) => error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentException;
    private static void ArrayBudget(JsonElement array, int maximum)
    {
        if (array.GetArrayLength() > maximum) throw new JsonException("Metadata collection exceeds the SDK limit.");
    }
    private static string Id(JsonElement item, string name)
    {
        JsonElement value = item.GetProperty(name);
        string? id = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        if (id is null || !DecimalIdValid(id)) throw new JsonException("Invalid decimal identity.");
        return id;
    }
    private static string? Text(JsonElement item, string name) => item.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
    private static long? Integer(JsonElement item, string name) => item.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null ? value.GetInt64() : null;
    private static int? Count(JsonElement item, string name) => Integer(item, name) is long value ? checked((int)value) : null;
    private static SourceFieldPresence Present(JsonElement item, string name, SourceFieldPresence field) => item.TryGetProperty(name, out _) ? field : SourceFieldPresence.None;
}

// Editable single-file example. References only the .NET framework and MeowChan.PluginSdk.
// One plugin serves several vichan-based sites: the site table below is the only per-site code.
// Evidence and limitations are documented in the adjacent README.md and the Sites directory.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.PluginSdk;

namespace MeowChan.Plugins.Examples.VichanFamily;

public sealed class VichanFamilyPlugin : ISourcePlugin
{
    public const string PluginId = "meowchan.source.vichan";
    private static readonly SettingsSchema EmptySchema = new SettingsSchema("1", Array.Empty<SettingDefinition>());

    // Per-site deviations from the shared vichan JSON reader. Every flag is backed by
    // that site's evidence (Sites/<Site>/README.md); none is inferred for other sites.
    [Flags]
    private enum Quirks
    {
        None = 0,
        // Board index page /{board}/index.html is an additional board route.
        BoardIndexRoute = 1,
        // Thread routes accept only decimal thread identifiers.
        DecimalThreadRoute = 2,
        // files[] with a stable provider id, file_path and mime instead of tim/ext/extra_files.
        FilesArray = 4,
        // Posts and catalog entries carry "board"; a different board is rejected.
        BoardField = 8,
        // The catalog "images" count is not published as an attachment count.
        NoCatalogImageCount = 16,
        // filedeleted:1 marks the primary file of a post as deleted.
        DeletedFileFlag = 32,
        // ext:"deleted" marks a deleted file in its slot.
        DeletedExtensionMarker = 64,
        // The "md5" field carries either an MD5 (16 bytes) or a SHA-1 (20 bytes) digest.
        Md5OrSha1Digest = 128,
        // An empty original filename is treated as absent.
        EmptyFileNameIsAbsent = 256,
        // Decimal identities may have leading zeros (they are kept verbatim).
        LeadingZeroIds = 512
    }

    // Adding a site: one entry here, plus evidence under Sites/<Site>/ and fixture tests.
    // Source IDs are persistent identities (watches, archives, rules): never rename one.
    private sealed record Site(string SourceId, string Name, string Host, SourceMaturity Maturity, Quirks Quirks)
    {
        internal Uri Origin { get; } = new Uri("https://" + Host + "/");
    }

    private static readonly Site[] Sites =
    [
        new Site("lainchan", "Lainchan", "lainchan.org", SourceMaturity.Unspecified, Quirks.None),
        new Site("leftypol", "Leftypol", "leftypol.org", SourceMaturity.Experimental,
            Quirks.DecimalThreadRoute | Quirks.FilesArray | Quirks.BoardField | Quirks.NoCatalogImageCount),
        new Site("sushichan", "SushiChan", "sushigirl.cafe", SourceMaturity.Experimental,
            Quirks.DecimalThreadRoute | Quirks.DeletedFileFlag),
        new Site("uboachan", "Uboachan", "uboachan.net", SourceMaturity.Experimental,
            Quirks.DecimalThreadRoute | Quirks.DeletedExtensionMarker | Quirks.Md5OrSha1Digest
            | Quirks.EmptyFileNameIsAbsent | Quirks.LeadingZeroIds),
        new Site("wizchan", "Wizchan", "wizchan.org", SourceMaturity.Experimental,
            Quirks.BoardIndexRoute | Quirks.DecimalThreadRoute)
    ];

    private static readonly IReadOnlyDictionary<string, (Site Site, SourceDescription Description)> BySource =
        Sites.ToDictionary(site => site.SourceId, site => (site, Describe(site)), StringComparer.Ordinal);

    public PluginDescription Describe() => new PluginDescription(PluginId, "vichan family",
        "1.0.0", SdkContract.Version,
        "Reads threads and board catalogs of vichan-based imageboards, including post text and original-file links.",
        Sites.Select(site => BySource[site.SourceId].Description).ToArray(), Array.Empty<FilterDescription>(), EmptySchema,
        new[]
        {
            new InformationEntry("support", "Accepted links", InformationKind.Text,
                "Accepts board and thread links from " + string.Join(", ", Sites.Select(site => site.Host)) + "."),
            new InformationEntry("limitations", "Limitations", InformationKind.Text,
                "No automatic board list or previews. Catalogs may be incomplete. Archived threads are not detected.")
        }.Concat(Sites.Select(site => new InformationEntry("site." + site.SourceId, site.Name, InformationKind.Text,
            "Accepts board and thread links from " + site.Host + ".", SourceId: site.SourceId))).ToArray(),
        Author: "MeowChan contributors", DocumentationUrl: new Uri("https://github.com/vichan-devel/vichan"));

    private static SourceDescription Describe(Site site)
    {
        List<UrlRoute> routes = new List<UrlRoute> { new UrlRoute(SourceResourceKind.Board, site.Host, "/{board}/", IsCanonical: true) };
        if (site.Quirks.HasFlag(Quirks.BoardIndexRoute))
            routes.Add(new UrlRoute(SourceResourceKind.Board, site.Host, "/{board}/index.html", AllowTrailingSlash: false));
        UrlRoute thread = new UrlRoute(SourceResourceKind.Thread, site.Host, "/{board}/res/{thread}.html", IsCanonical: true, AllowTrailingSlash: false);
        routes.Add(site.Quirks.HasFlag(Quirks.DecimalThreadRoute) ? thread with { ThreadIdentifierKind = RouteIdentifierKind.Decimal } : thread);
        return new SourceDescription(site.SourceId, site.Name, site.Origin,
            SourceCapabilities.ThreadRead | SourceCapabilities.BoardCatalog | SourceCapabilities.PostCapture | SourceCapabilities.OriginalMedia,
            routes.ToArray(), [new HttpHostDeclaration(site.Host, HttpHostPurpose.Metadata | HttpHostPurpose.OriginalMedia)],
            EmptySchema, PostOrderKind.DecimalPostId) { Maturity = site.Maturity };
    }

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
        if (!BySource.TryGetValue(request.Thread.SourceId, out var known) || !BoardValid(request.Thread.BoardId)
            || !DecimalIdValid(known.Site, request.Thread.ThreadId))
            return new ThreadFailure(request.Thread, new FailureDetail("invalid_resource", "Source, board or thread identity is invalid."));
        (Site site, SourceDescription description) = known;
        Uri endpoint = Endpoint(site, request.Thread.BoardId, "res/" + Uri.EscapeDataString(request.Thread.ThreadId) + ".json");
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
        HashSet<long> pages = new HashSet<long>();
        ArrayBudget(root, SdkLimits.MaximumCollectionCount);
        foreach (JsonElement page in root.EnumerateArray())
        {
            long pageNumber = page.GetProperty("page").GetInt64();
            if (pageNumber < 0 || !pages.Add(pageNumber)) throw new JsonException("Invalid catalog page.");
            JsonElement pageThreads = page.GetProperty("threads");
            ArrayBudget(pageThreads, SdkLimits.MaximumCollectionCount - threads.Count);
            foreach (JsonElement entry in pageThreads.EnumerateArray())
            {
                if (site.Quirks.HasFlag(Quirks.BoardField) && Text(entry, "board") is string observedBoard && observedBoard != board.BoardId)
                    throw new JsonException("Catalog thread belongs to a different board.");
                SourceThreadId thread = new SourceThreadId(site.SourceId, board.BoardId, Id(site, entry, "no"));
                threads.Add(new SourceThreadReference(thread, ThreadUri(site, thread), Text(entry, "sub"), Text(entry, "com"),
                    SourceTextFormat.Html, PostedAt(entry), Count(entry, "replies"),
                    site.Quirks.HasFlag(Quirks.NoCatalogImageCount) ? null : Count(entry, "images")));
            }
        }
        // One body contains the returned pages; the provider does not promise exhaustive board coverage.
        return new CatalogSnapshot(new SourceCatalogSnapshot(board, threads.AsReadOnly(), IsComplete: false, Validators: validators));
    }

    private static ThreadSnapshot ParseThread(Site site, SourceThreadId thread, JsonElement root, SourceValidators? validators)
    {
        List<SourcePost> posts = new List<SourcePost>();
        string? title = null;
        JsonElement postArray = root.GetProperty("posts");
        ArrayBudget(postArray, SdkLimits.MaximumCollectionCount);
        foreach (JsonElement entry in postArray.EnumerateArray())
        {
            string postId = Id(site, entry, "no");
            if (site.Quirks.HasFlag(Quirks.BoardField) && Text(entry, "board") is string observedBoard && observedBoard != thread.BoardId)
                throw new JsonException("Post belongs to a different board.");
            string? parent = entry.TryGetProperty("resto", out JsonElement parentValue) && parentValue.ValueKind != JsonValueKind.Null ? Id(site, entry, "resto") : null;
            if (parent is not null && (postId == thread.ThreadId ? parent != "0" : parent != thread.ThreadId))
                throw new JsonException("Post belongs to a different thread.");
            if (postId == thread.ThreadId) title = Text(entry, "sub");
            SourceFieldPresence fields = Present(entry, "time", SourceFieldPresence.PostedAt)
                | Present(entry, "sub", SourceFieldPresence.Subject) | Present(entry, "name", SourceFieldPresence.Author)
                | Present(entry, "com", SourceFieldPresence.Text);
            List<SourceAttachment>? attachments = site.Quirks.HasFlag(Quirks.FilesArray)
                ? FilesArrayAttachments(site, thread, entry) : ClassicAttachments(site, thread, entry, postId);
            if (attachments is not null) fields |= SourceFieldPresence.Attachments;
            // Decimal ID ordering survives partial updates; no response-local array index becomes identity.
            posts.Add(new SourcePost(postId, 0, fields, PostedAt(entry), Text(entry, "sub"), Text(entry, "name"),
                Text(entry, "com"), SourceTextFormat.Html, Attachments: attachments?.AsReadOnly()));
        }
        // Neither locked nor cyclical is a verified end signal. No vichan archive flag is assumed.
        return new ThreadSnapshot(new SourceThreadSnapshot(thread, ThreadUri(site, thread), title, SourceThreadState.Active,
            posts.AsReadOnly(), validators));
    }

    // tim/ext primary file at position 0, extra_files from position 1 (kept even without a primary file).
    private static List<SourceAttachment>? ClassicAttachments(Site site, SourceThreadId thread, JsonElement entry, string postId)
    {
        bool primary = entry.TryGetProperty("tim", out _) || entry.TryGetProperty("ext", out _);
        bool deletedPrimary = site.Quirks.HasFlag(Quirks.DeletedFileFlag) && DeletedFile(entry);
        bool extras = entry.TryGetProperty("extra_files", out JsonElement extraFiles);
        if (!primary && !deletedPrimary && !extras) return null;
        List<SourceAttachment> attachments = new List<SourceAttachment>();
        if (deletedPrimary) attachments.Add(new SourceAttachment(0, null, IsDeleted: true));
        else if (primary) attachments.Add(ClassicAttachment(site, thread, entry, postId, 0));
        if (extras)
        {
            ArrayBudget(extraFiles, primary ? 1023 : 1024);
            int position = 1;
            foreach (JsonElement file in extraFiles.EnumerateArray()) attachments.Add(ClassicAttachment(site, thread, file, postId, position++));
        }
        return attachments;
    }

    private static SourceAttachment ClassicAttachment(Site site, SourceThreadId thread, JsonElement file, string postId, int position)
    {
        // A removed file keeps its metadata but is marked ext:"deleted" with an empty token: it has no media to
        // check or download, so it is a deletion marker in its slot, never a live attachment or a parse failure.
        if (site.Quirks.HasFlag(Quirks.DeletedExtensionMarker) && file.TryGetProperty("ext", out JsonElement marker)
            && marker.ValueKind == JsonValueKind.String && marker.GetString() == "deleted")
            return new SourceAttachment(position, null, IsDeleted: true);
        JsonElement timestamp = file.GetProperty("tim");
        if (timestamp.ValueKind is not JsonValueKind.String and not JsonValueKind.Number)
            throw new JsonException("Provider file token must be a string or number.");
        string? token = timestamp.ValueKind == JsonValueKind.String ? timestamp.GetString() : timestamp.GetRawText();
        if (string.IsNullOrEmpty(token) || token.Length > 128 || token.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-' && character != '_'))
            throw new JsonException("Invalid provider file token.");
        string extension = file.GetProperty("ext").GetString() ?? throw new JsonException("Missing extension.");
        string? originalName = Text(file, "filename");
        if (site.Quirks.HasFlag(Quirks.EmptyFileNameIsAbsent) && originalName?.Length == 0) originalName = null;
        (string? md5, string? sha1) = site.Quirks.HasFlag(Quirks.Md5OrSha1Digest) ? Md5OrSha1(file) : (Text(file, "md5"), null);
        return new SourceAttachment(position, postId + "-" + position.ToString(CultureInfo.InvariantCulture),
            Url: Endpoint(site, thread.BoardId, "src/" + Uri.EscapeDataString(token + extension)),
            OriginalFileName: originalName is null ? null : originalName + extension, Extension: extension,
            ExpectedSize: Integer(file, "fsize"), ExpectedMd5: md5) { ExpectedSha1 = sha1 };
    }

    private static bool DeletedFile(JsonElement entry)
    {
        if (!entry.TryGetProperty("filedeleted", out JsonElement flag)) return false;
        return flag.ValueKind switch
        {
            JsonValueKind.True => true, JsonValueKind.False or JsonValueKind.Null => false,
            JsonValueKind.Number when flag.TryGetInt32(out int value) && value is 0 or 1 => value == 1,
            _ => throw new JsonException("Invalid file deletion marker.")
        };
    }

    private static (string? Md5, string? Sha1) Md5OrSha1(JsonElement file)
    {
        string? raw = Text(file, "md5");
        if (raw is null) return (null, null);
        byte[] digest = Convert.FromBase64String(raw);
        // Each attachment is interpreted independently and retains its original value.
        if (digest.Length == 16) return (raw, null);
        if (digest.Length == 20) return (null, raw);
        throw new JsonException("Unsupported provider digest length.");
    }

    // files[] schema: id is a stable opaque provider token; tim can contain suffixes,
    // and array position is not file identity.
    private static List<SourceAttachment>? FilesArrayAttachments(Site site, SourceThreadId thread, JsonElement entry)
    {
        if (!entry.TryGetProperty("files", out JsonElement files)) return null;
        ArrayBudget(files, 1024);
        List<SourceAttachment> attachments = new List<SourceAttachment>();
        HashSet<string> fileIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement file in files.EnumerateArray())
        {
            string id = FileId(file.GetProperty("id"));
            string path = file.GetProperty("file_path").GetString() ?? throw new JsonException("Missing original path.");
            Uri url = MediaUri(site, thread.BoardId, path);
            string? extension = Text(file, "ext");
            string? originalName = Text(file, "filename");
            if (!fileIds.Add(id)) throw new JsonException("Duplicate provider file identity.");
            attachments.Add(new SourceAttachment(attachments.Count, id, Url: url,
                OriginalFileName: originalName is null ? null : originalName + extension, Extension: extension,
                ExpectedSize: Integer(file, "fsize"), ExpectedMd5: Text(file, "md5"), MimeType: Text(file, "mime")));
        }
        return attachments;
    }

    private static string FileId(JsonElement value)
    {
        if (value.ValueKind is not JsonValueKind.String and not JsonValueKind.Number)
            throw new JsonException("Provider file identity must be a string or number.");
        string? id = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        if (string.IsNullOrEmpty(id) || id.Length > SdkLimits.MaximumIdentifierLength
            || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new JsonException("Invalid provider file identity.");
        return id;
    }

    private static Uri MediaUri(Site site, string board, string path)
    {
        // Use the supplied original path, never thumb_path or a guessed tim URL.
        // Only the observed same-board /src/ layout is declared; a foreign URL,
        // query, encoded separator or traversal is not a new media grant.
        string prefix = "/" + Uri.EscapeDataString(board) + "/src/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) throw new JsonException("Unsupported original path.");
        string name = path[prefix.Length..];
        if (name.Length == 0 || name is "." or ".."
            || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
            throw new JsonException("Invalid original filename token.");
        Uri url = new Uri(site.Origin, path);
        if (url.Scheme != site.Origin.Scheme || url.IdnHost != site.Host || url.Port != site.Origin.Port
            || url.Query.Length != 0 || url.Fragment.Length != 0 || url.UserInfo.Length != 0
            || url.AbsolutePath != path) throw new JsonException("Unsupported original origin.");
        return url;
    }

    private static DateTimeOffset? PostedAt(JsonElement item) => Integer(item, "time") is long seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    private static Uri Endpoint(Site site, string board, string suffix) => new Uri(site.Origin, Uri.EscapeDataString(board) + "/" + suffix);
    private static Uri ThreadUri(Site site, SourceThreadId thread) => Endpoint(site, thread.BoardId, "res/" + Uri.EscapeDataString(thread.ThreadId) + ".html");
    private static bool SameEndpoint(Uri expected, Uri actual) => actual.IsAbsoluteUri && actual.Scheme == expected.Scheme
        && actual.IdnHost == expected.IdnHost && actual.Port == expected.Port && actual.AbsolutePath == expected.AbsolutePath
        && actual.Query.Length == 0 && actual.Fragment.Length == 0 && actual.UserInfo.Length == 0;
    private static bool BoardValid(string board) => !string.IsNullOrWhiteSpace(board) && board.Length <= 128
        && board == board.Trim() && board is not "." and not ".." && !board.Any(char.IsControl)
        && board.IndexOfAny(['/', '\\', '?', '#']) < 0;
    private static bool DecimalIdValid(Site site, string id) => !string.IsNullOrEmpty(id) && id.Length <= SdkLimits.MaximumIdentifierLength
        && id.All(character => character is >= '0' and <= '9')
        && (id.Length == 1 || id[0] != '0' || site.Quirks.HasFlag(Quirks.LeadingZeroIds));
    private static bool HasHttpValidator(SourceValidators? validators) => !string.IsNullOrWhiteSpace(validators?.EntityTag) || validators?.LastModified is not null;
    private static FailureDetail InvalidPayload() => new FailureDetail("invalid_payload", "The metadata response has an invalid JSON shape or violates the source contract.", 200);
    private static FailureDetail TooLarge() => new FailureDetail("metadata_too_large", "The metadata response exceeds the SDK body limit.", 200);
    private static FailureDetail HttpFailure(int status) => new FailureDetail("http_failure", "Metadata requires HTTP 200; this status is not a verified thread-end signal.", status);
    private static bool PayloadError(Exception error) => error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentException;
    private static void ArrayBudget(JsonElement array, int maximum)
    {
        if (array.GetArrayLength() > maximum) throw new JsonException("Metadata collection exceeds the SDK limit.");
    }
    private static string Id(Site site, JsonElement item, string name)
    {
        JsonElement value = item.GetProperty(name);
        string? id = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        if (id is null || !DecimalIdValid(site, id)) throw new JsonException("Invalid decimal identity.");
        return id;
    }
    private static string? Text(JsonElement item, string name) => item.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
    private static long? Integer(JsonElement item, string name) => item.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null ? value.GetInt64() : null;
    private static int? Count(JsonElement item, string name) => Integer(item, name) is long value ? checked((int)value) : null;
    private static SourceFieldPresence Present(JsonElement item, string name, SourceFieldPresence field) => item.TryGetProperty(name, out _) ? field : SourceFieldPresence.None;
}

// Editable single-file example. References only the .NET framework and MeowChan.PluginSdk.
// Evidence and limitations are documented in the adjacent README.md and Evidence directory.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.PluginSdk;

namespace MeowChan.Plugins.Examples.Leftypol;

public sealed class LeftypolPlugin : ISourcePlugin
{
    public const string SourceId = "leftypol";
    private const string Host = "leftypol.org";
    private static readonly Uri Origin = new Uri("https://" + Host + "/");
    private static readonly SettingsSchema EmptySchema = new SettingsSchema("1", Array.Empty<SettingDefinition>());

    public PluginDescription Describe() => new PluginDescription("meowchan.source.leftypol", "Leftypol",
        "1.0.0", SdkContract.Version,
        "Reads Leftypol threads and board catalogs, including post text and original-file links.",
        [Description()], Array.Empty<FilterDescription>(), EmptySchema,
        [new InformationEntry("support", "Accepted links", InformationKind.Text,
            "Accepts board and thread links from leftypol.org."),
         new InformationEntry("limitations", "Limitations", InformationKind.Text, "No automatic board list or previews. Catalogs may be incomplete. Archived threads are not detected.")],
        Author: "MeowChan contributors", DocumentationUrl: new Uri("https://leftypol.org/"));

    private static SourceDescription Description() => new SourceDescription(SourceId, "Leftypol", Origin,
        SourceCapabilities.ThreadRead | SourceCapabilities.BoardCatalog | SourceCapabilities.PostCapture | SourceCapabilities.OriginalMedia,
        [new UrlRoute(SourceResourceKind.Board, Host, "/{board}/", IsCanonical: true),
         new UrlRoute(SourceResourceKind.Thread, Host, "/{board}/res/{thread}.html", IsCanonical: true, AllowTrailingSlash: false) { ThreadIdentifierKind = RouteIdentifierKind.Decimal }],
        [new HttpHostDeclaration(Host, HttpHostPurpose.Metadata | HttpHostPurpose.OriginalMedia)],
        EmptySchema, PostOrderKind.DecimalPostId) { Maturity = SourceMaturity.Experimental };

    public async Task<CatalogReadResult> ReadCatalogAsync(CatalogReadRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Board.SourceId != SourceId || !BoardValid(request.Board.BoardId))
            return new CatalogFailure(request.Board, new FailureDetail("invalid_resource", "Source or board identity is invalid."));
        if (request.Cursor is not null || request.Page is not null)
            return new CatalogFailure(request.Board, new FailureDetail("unsupported_page", "This endpoint returns its catalog in one response; no cursor or page is supported."));
        Uri endpoint = Endpoint(request.Board.BoardId, "catalog.json");
        HostHttpResult result = await context.Http.SendAsync(new HostHttpRequest(SourceId, HostHttpMethod.Get,
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
            CatalogSnapshot snapshot = ParseCatalog(request.Board, document.RootElement, response.Validators);
            if (SdkValidation.ValidateCatalogResult(request, snapshot, Description()).Count != 0)
                return new CatalogFailure(request.Board, InvalidPayload());
            return snapshot;
        }
        catch (Exception error) when (PayloadError(error)) { return new CatalogFailure(request.Board, InvalidPayload()); }
    }

    public async Task<ThreadReadResult> ReadThreadAsync(ThreadReadRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Thread.SourceId != SourceId || !BoardValid(request.Thread.BoardId) || !DecimalIdValid(request.Thread.ThreadId))
            return new ThreadFailure(request.Thread, new FailureDetail("invalid_resource", "Source, board or thread identity is invalid."));
        Uri endpoint = Endpoint(request.Thread.BoardId, "res/" + Uri.EscapeDataString(request.Thread.ThreadId) + ".json");
        HostHttpResult result = await context.Http.SendAsync(new HostHttpRequest(SourceId, HostHttpMethod.Get,
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
            ThreadSnapshot snapshot = ParseThread(request.Thread, document.RootElement, response.Validators);
            if (SdkValidation.ValidateThreadResult(request, snapshot, Description()).Count != 0)
                return new ThreadFailure(request.Thread, InvalidPayload());
            return snapshot;
        }
        catch (Exception error) when (PayloadError(error)) { return new ThreadFailure(request.Thread, InvalidPayload()); }
    }

    private static CatalogSnapshot ParseCatalog(SourceBoardId board, JsonElement root, SourceValidators? validators)
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
                if (Text(entry, "board") is string observedBoard && observedBoard != board.BoardId)
                    throw new JsonException("Catalog thread belongs to a different board.");
                SourceThreadId thread = new SourceThreadId(SourceId, board.BoardId, Id(entry, "no"));
                threads.Add(new SourceThreadReference(thread, ThreadUri(thread), Text(entry, "sub"), Text(entry, "com"),
                    SourceTextFormat.Html, PostedAt(entry), Count(entry, "replies")));
            }
        }
        // One body contains the returned pages; the provider does not promise exhaustive board coverage.
        return new CatalogSnapshot(new SourceCatalogSnapshot(board, threads.AsReadOnly(), IsComplete: false, Validators: validators));
    }

    private static ThreadSnapshot ParseThread(SourceThreadId thread, JsonElement root, SourceValidators? validators)
    {
        List<SourcePost> posts = new List<SourcePost>();
        string? title = null;
        JsonElement postArray = root.GetProperty("posts");
        ArrayBudget(postArray, SdkLimits.MaximumCollectionCount);
        foreach (JsonElement entry in postArray.EnumerateArray())
        {
            string postId = Id(entry, "no");
            if (Text(entry, "board") is string observedBoard && observedBoard != thread.BoardId)
                throw new JsonException("Post belongs to a different board.");
            string? parent = entry.TryGetProperty("resto", out JsonElement parentValue) && parentValue.ValueKind != JsonValueKind.Null ? Id(entry, "resto") : null;
            if (parent is not null && (postId == thread.ThreadId ? parent != "0" : parent != thread.ThreadId))
                throw new JsonException("Post belongs to a different thread.");
            if (postId == thread.ThreadId) title = Text(entry, "sub");
            SourceFieldPresence fields = Present(entry, "time", SourceFieldPresence.PostedAt)
                | Present(entry, "sub", SourceFieldPresence.Subject) | Present(entry, "name", SourceFieldPresence.Author)
                | Present(entry, "com", SourceFieldPresence.Text);
            List<SourceAttachment>? attachments = null;
            if (entry.TryGetProperty("files", out JsonElement files))
            {
                fields |= SourceFieldPresence.Attachments;
                ArrayBudget(files, 1024);
                attachments = new List<SourceAttachment>();
                HashSet<string> fileIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonElement file in files.EnumerateArray())
                {
                    SourceAttachment attachment = Attachment(thread, file, attachments.Count);
                    if (!fileIds.Add(attachment.MediaId!)) throw new JsonException("Duplicate provider file identity.");
                    attachments.Add(attachment);
                }
            }
            // Decimal ID ordering survives partial updates; no response-local array index becomes identity.
            posts.Add(new SourcePost(postId, 0, fields, PostedAt(entry), Text(entry, "sub"), Text(entry, "name"),
                Text(entry, "com"), SourceTextFormat.Html, Attachments: attachments?.AsReadOnly()));
        }
        // Neither locked nor cyclical is a verified end signal. No Leftypol archive flag is assumed.
        return new ThreadSnapshot(new SourceThreadSnapshot(thread, ThreadUri(thread), title, SourceThreadState.Active,
            posts.AsReadOnly(), validators));
    }

    private static SourceAttachment Attachment(SourceThreadId thread, JsonElement file, int position)
    {
        // Leftypol has a files[] schema. id is a stable opaque provider token;
        // tim can contain suffixes, and array position is not file identity.
        string id = FileId(file.GetProperty("id"));
        string path = file.GetProperty("file_path").GetString() ?? throw new JsonException("Missing original path.");
        Uri url = MediaUri(thread.BoardId, path);
        string? extension = Text(file, "ext");
        string? originalName = Text(file, "filename");
        return new SourceAttachment(position, id, Url: url,
            OriginalFileName: originalName is null ? null : originalName + extension, Extension: extension,
            ExpectedSize: Integer(file, "fsize"), ExpectedMd5: Text(file, "md5"), MimeType: Text(file, "mime"));
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

    private static Uri MediaUri(string board, string path)
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
        Uri url = new Uri(Origin, path);
        if (url.Scheme != Origin.Scheme || url.IdnHost != Host || url.Port != Origin.Port
            || url.Query.Length != 0 || url.Fragment.Length != 0 || url.UserInfo.Length != 0
            || url.AbsolutePath != path) throw new JsonException("Unsupported original origin.");
        return url;
    }

    private static DateTimeOffset? PostedAt(JsonElement item) => Integer(item, "time") is long seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    private static Uri Endpoint(string board, string suffix) => new Uri(Origin, Uri.EscapeDataString(board) + "/" + suffix);
    private static Uri ThreadUri(SourceThreadId thread) => Endpoint(thread.BoardId, "res/" + Uri.EscapeDataString(thread.ThreadId) + ".html");
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

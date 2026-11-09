// Bounded SDK-only LynxChan adapter. It deliberately reads only the documented
// catalog/page/thread JSON resources; the host owns transport, pacing and media I/O.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.PluginSdk;

namespace MeowChan.Plugins.Examples.CapybaraChan;

public sealed class CapybaraChanPlugin : ISourcePlugin
{
    public const string SourceId = "capybarachan";
    private const string Host = "capybarachan.org";
    private static readonly Uri Origin = new Uri("https://" + Host + "/");
    private static readonly SettingsSchema EmptySchema = new SettingsSchema("1", Array.Empty<SettingDefinition>());

    public PluginDescription Describe() => new PluginDescription("meowchan.source.capybarachan", "CapybaraChan",
        "1.0.0", SdkContract.Version,
        "Reads the documented CapybaraChan LynxChan catalog and thread JSON resources.",
        [Description()], Array.Empty<FilterDescription>(), EmptySchema,
        [new InformationEntry("support", "Accepted links", InformationKind.Text,
            "Accepts documented board and thread links from capybarachan.org."),
         new InformationEntry("maturity", "Status", InformationKind.Text,
            "MeowChan · experimental; the board menu is not treated as an automatic webring."),
         new InformationEntry("limitations", "Limitations", InformationKind.Text,
            "No posting, login, webring, overboard or preview support. Catalog omissions are not deletion evidence.")],
        Author: "MeowChan contributors", DocumentationUrl: new Uri("https://capybarachan.org/"));

    private static SourceDescription Description() => new SourceDescription(SourceId, "CapybaraChan", Origin,
        SourceCapabilities.ThreadRead | SourceCapabilities.BoardCatalog | SourceCapabilities.PostCapture | SourceCapabilities.OriginalMedia,
        [new UrlRoute(SourceResourceKind.Board, Host, "/{board}/", IsCanonical: true) { BoardIdentifierKind = RouteIdentifierKind.AsciiWord },
         new UrlRoute(SourceResourceKind.Thread, Host, "/{board}/res/{thread}.html", IsCanonical: true, AllowTrailingSlash: false)
         { BoardIdentifierKind = RouteIdentifierKind.AsciiWord, ThreadIdentifierKind = RouteIdentifierKind.Decimal }],
        [new HttpHostDeclaration(Host, HttpHostPurpose.Metadata | HttpHostPurpose.OriginalMedia)],
        EmptySchema, PostOrderKind.DecimalPostId) { Maturity = SourceMaturity.Experimental };

    public async Task<CatalogReadResult> ReadCatalogAsync(CatalogReadRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Board.SourceId != SourceId || !BoardValid(request.Board.BoardId))
            return new CatalogFailure(request.Board, InvalidResource());
        if (request.Cursor is not null)
            return new CatalogFailure(request.Board, new FailureDetail("unsupported_cursor", "This LynxChan resource has no cursor contract."));
        if (request.Page is < 0)
            return new CatalogFailure(request.Board, new FailureDetail("invalid_page", "Page must be nonnegative."));
        Uri endpoint = request.Page is int page
            ? Endpoint(request.Board.BoardId, (page < 0 ? "" : (page == 0 ? "1" : page.ToString(CultureInfo.InvariantCulture))) + ".json")
            : Endpoint(request.Board.BoardId, "catalog.json");
        return await ReadCatalogResponseAsync(request, context, endpoint, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ThreadReadResult> ReadThreadAsync(ThreadReadRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Thread.SourceId != SourceId || !BoardValid(request.Thread.BoardId) || !DecimalIdValid(request.Thread.ThreadId))
            return new ThreadFailure(request.Thread, InvalidResource());
        Uri endpoint = Endpoint(request.Thread.BoardId, "res/" + Uri.EscapeDataString(request.Thread.ThreadId) + ".json");
        HostHttpResult result = await context.Http.SendAsync(new HostHttpRequest(SourceId, HostHttpMethod.Get, endpoint,
            Headers: new Dictionary<string, string> { ["Accept"] = "application/json" },
            Validators: request.HasSnapshot ? request.Validators : null), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result is HostHttpDeferred deferred) return new ThreadDeferred(request.Thread, deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode);
        if (result is HostHttpFailure failure) return new ThreadFailure(request.Thread, failure.Failure);
        if (result is not HostHttpResponse response) return new ThreadFailure(request.Thread, InvalidHttpResult());
        if (!SameEndpoint(endpoint, response.FinalUri)) return new ThreadFailure(request.Thread, UnexpectedEndpoint(response.StatusCode));
        if (response.StatusCode == 404) return new ThreadNotFound(request.Thread, 404);
        if (response.StatusCode == 304)
            return request.HasSnapshot && HasHttpValidator(request.Validators)
                ? new ThreadUnchanged(request.Thread, request.Validators!)
                : new ThreadFailure(request.Thread, MissingBaseline(304));
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

    private static async Task<CatalogReadResult> ReadCatalogResponseAsync(CatalogReadRequest request,
        PluginCallContext context, Uri endpoint, CancellationToken cancellationToken)
    {
        HostHttpResult result = await context.Http.SendAsync(new HostHttpRequest(SourceId, HostHttpMethod.Get, endpoint,
            Headers: new Dictionary<string, string> { ["Accept"] = "application/json" },
            Validators: request.HasSnapshot ? request.Validators : null), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result is HostHttpDeferred deferred) return new CatalogDeferred(request.Board, deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode);
        if (result is HostHttpFailure failure) return new CatalogFailure(request.Board, failure.Failure);
        if (result is not HostHttpResponse response) return new CatalogFailure(request.Board, InvalidHttpResult());
        if (!SameEndpoint(endpoint, response.FinalUri)) return new CatalogFailure(request.Board, UnexpectedEndpoint(response.StatusCode));
        if (response.StatusCode == 304)
            return request.HasSnapshot && HasHttpValidator(request.Validators)
                ? new CatalogUnchanged(request.Board, request.Validators!)
                : new CatalogFailure(request.Board, MissingBaseline(304));
        if (response.StatusCode == 404) return new CatalogFailure(request.Board, new FailureDetail("board_unavailable", "The documented board resource was not found.", 404));
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

    private static CatalogSnapshot ParseCatalog(SourceBoardId board, JsonElement root, SourceValidators? validators)
    {
        JsonElement entries = root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("threads");
        ArrayBudget(entries, SdkLimits.MaximumCollectionCount);
        List<SourceThreadReference> threads = new List<SourceThreadReference>();
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            string id = Id(entry, "threadId");
            DateTimeOffset? created = Time(entry, "creation");
            string? text = entry.TryGetProperty("message", out _) ? Text(entry, "message") : Text(entry, "markdown");
            SourceTextFormat format = entry.TryGetProperty("message", out _) ? SourceTextFormat.Html : SourceTextFormat.PlainText;
            threads.Add(new SourceThreadReference(new SourceThreadId(SourceId, board.BoardId, id),
                ThreadUri(board.BoardId, id), Text(entry, "subject"), text, format, created,
                Count(entry, "postCount"), Count(entry, "fileCount")));
        }
        return new CatalogSnapshot(new SourceCatalogSnapshot(board, threads.AsReadOnly(), IsComplete: false, Validators: validators));
    }

    private static ThreadSnapshot ParseThread(SourceThreadId thread, JsonElement root, SourceValidators? validators)
    {
        if (Id(root, "threadId") != thread.ThreadId) throw new JsonException("Thread identity mismatch.");
        if (Text(root, "boardUri") is string board && board != thread.BoardId) throw new JsonException("Board identity mismatch.");
        List<SourcePost> posts = new List<SourcePost>();
        string openerId = root.TryGetProperty("postId", out _) ? Id(root, "postId") : thread.ThreadId;
        posts.Add(Post(thread, root, openerId, isOpener: true));
        if (root.TryGetProperty("posts", out JsonElement replies))
        {
            ArrayBudget(replies, SdkLimits.MaximumCollectionCount - 1);
            foreach (JsonElement reply in replies.EnumerateArray())
            {
                string id = Id(reply, "postId");
                if (id == openerId) continue;
                if (Text(reply, "boardUri") is string replyBoard && replyBoard != thread.BoardId) throw new JsonException("Reply board mismatch.");
                posts.Add(Post(thread, reply, id, isOpener: false));
            }
        }
        return new ThreadSnapshot(new SourceThreadSnapshot(thread, ThreadUri(thread.BoardId, thread.ThreadId),
            Text(root, "subject"), SourceThreadState.Active, posts.AsReadOnly(), validators));
    }

    private static SourcePost Post(SourceThreadId thread, JsonElement entry, string postId, bool isOpener)
    {
        SourceFieldPresence fields = Present(entry, "creation", SourceFieldPresence.PostedAt)
            | Present(entry, "subject", SourceFieldPresence.Subject) | Present(entry, "name", SourceFieldPresence.Author);
        bool hasHtml = entry.TryGetProperty("message", out _);
        bool hasMarkdown = entry.TryGetProperty("markdown", out _);
        if (hasHtml || hasMarkdown) fields |= SourceFieldPresence.Text;
        (string? text, SourceTextFormat format) = hasHtml ? (Text(entry, "message"), SourceTextFormat.Html)
            : (Text(entry, "markdown"), SourceTextFormat.PlainText);
        List<SourceAttachment>? attachments = null;
        if (entry.TryGetProperty("files", out JsonElement files))
        {
            if (files.ValueKind != JsonValueKind.Array) throw new JsonException("Files must be an array.");
            fields |= SourceFieldPresence.Attachments;
            ArrayBudget(files, 1024);
            attachments = new List<SourceAttachment>();
            int position = 0;
            foreach (JsonElement file in files.EnumerateArray()) attachments.Add(Attachment(thread, file, postId, position++));
        }
        bool deleted = entry.TryGetProperty("deleted", out JsonElement deletedValue) && deletedValue.ValueKind == JsonValueKind.True;
        if (deleted) fields |= SourceFieldPresence.IsDeleted;
        return new SourcePost(postId, 0, fields, Time(entry, "creation"), Text(entry, "subject"), Text(entry, "name"),
            text, format, deleted, attachments?.AsReadOnly());
    }

    private static SourceAttachment Attachment(SourceThreadId thread, JsonElement file, string postId, int position)
    {
        bool deleted = file.TryGetProperty("deleted", out JsonElement deletedValue) && deletedValue.ValueKind == JsonValueKind.True;
        string? path = Text(file, "path");
        string mediaId = postId + "-" + position.ToString(CultureInfo.InvariantCulture);
        if (deleted) return new SourceAttachment(position, null, IsDeleted: true);
        if (path is null || !TryMediaUri(path, out Uri? url) || url is null) throw new JsonException("Missing or foreign original media path.");
        string? extension = Text(file, "extension");
        if (extension is null && PathExtension(url.AbsolutePath) is string inferred) extension = inferred;
        return new SourceAttachment(position, mediaId, Url: url, OriginalFileName: Text(file, "originalName"),
            Extension: extension, ExpectedSize: Integer(file, "size"), MimeType: Text(file, "mime"));
    }

    private static bool TryMediaUri(string value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(Origin, value, out Uri? candidate) || candidate is null || candidate.IdnHost != Host
            || candidate.Scheme != Origin.Scheme || candidate.Query.Length != 0 || candidate.Fragment.Length != 0
            || !candidate.AbsolutePath.StartsWith("/.media/", StringComparison.Ordinal)
            || candidate.AbsolutePath.Contains("..", StringComparison.Ordinal)) return false;
        uri = candidate;
        return true;
    }

    private static string? PathExtension(string path)
    {
        int slash = path.LastIndexOf('/'); int dot = path.LastIndexOf('.');
        return dot > slash && dot < path.Length - 1 ? path[dot..] : null;
    }

    private static Uri Endpoint(string board, string suffix) => new Uri(Origin, Uri.EscapeDataString(board) + "/" + suffix);
    private static Uri ThreadUri(string board, string thread) => Endpoint(board, "res/" + Uri.EscapeDataString(thread) + ".html");
    private static bool SameEndpoint(Uri expected, Uri actual) => actual.IsAbsoluteUri && actual.Scheme == expected.Scheme
        && actual.IdnHost == expected.IdnHost && actual.Port == expected.Port && actual.AbsolutePath == expected.AbsolutePath
        && actual.Query == expected.Query && actual.Fragment.Length == 0 && actual.UserInfo.Length == 0;
    private static bool BoardValid(string board) => !string.IsNullOrWhiteSpace(board) && board.Length <= 64 && board.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
    private static bool DecimalIdValid(string id) => !string.IsNullOrEmpty(id) && id.Length <= SdkLimits.MaximumIdentifierLength && id.All(c => c is >= '0' and <= '9') && (id.Length == 1 || id[0] != '0');
    private static bool HasHttpValidator(SourceValidators? v) => !string.IsNullOrWhiteSpace(v?.EntityTag) || v?.LastModified is not null || !string.IsNullOrWhiteSpace(v?.Revision);
    private static FailureDetail InvalidResource() => new FailureDetail("invalid_resource", "Source, board or thread identity is invalid.");
    private static FailureDetail InvalidHttpResult() => new FailureDetail("invalid_http_result", "Unknown host HTTP result.");
    private static FailureDetail UnexpectedEndpoint(int? status) => new FailureDetail("unexpected_endpoint", "The response is not from the requested metadata endpoint.", status);
    private static FailureDetail MissingBaseline(int status) => new FailureDetail("missing_baseline", "304 requires an accepted resource and its HTTP validator.", status);
    private static FailureDetail HttpFailure(int status) => new FailureDetail("http_failure", "Metadata requires HTTP 200; this status is not a verified resource end signal.", status);
    private static FailureDetail InvalidPayload() => new FailureDetail("invalid_payload", "The metadata response has an invalid JSON shape or violates the source contract.", 200);
    private static FailureDetail TooLarge() => new FailureDetail("metadata_too_large", "The metadata response exceeds the SDK body limit.", 200);
    private static bool PayloadError(Exception error) => error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentException;
    private static void ArrayBudget(JsonElement array, int maximum) { if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > maximum) throw new JsonException("Metadata collection exceeds the SDK limit."); }
    private static string Id(JsonElement item, string name) { JsonElement value = item.GetProperty(name); string? id = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long n) ? n.ToString(CultureInfo.InvariantCulture) : null; if (id is null || !DecimalIdValid(id)) throw new JsonException("Invalid decimal identity."); return id; }
    private static string? Text(JsonElement item, string name) => item.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null ? value.ValueKind == JsonValueKind.String ? value.GetString() : throw new JsonException("Expected text.") : null;
    private static long? Integer(JsonElement item, string name) => item.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null ? value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long n) ? n : throw new JsonException("Expected integer.") : null;
    private static int? Count(JsonElement item, string name) => Integer(item, name) is long value ? checked((int)value) : null;
    private static DateTimeOffset? Time(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long seconds)) return DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed)) return parsed;
        throw new JsonException("Invalid timestamp.");
    }
    private static SourceFieldPresence Present(JsonElement item, string name, SourceFieldPresence field) => item.TryGetProperty(name, out _) ? field : SourceFieldPresence.None;
}

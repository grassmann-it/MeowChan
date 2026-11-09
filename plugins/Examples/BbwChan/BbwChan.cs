// Bounded SDK-only LynxChan adapter. It reads only the documented BBW-Chan
// catalog/page/thread JSON resources; the host owns transport, pacing and media I/O.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.PluginSdk;

namespace MeowChan.Plugins.Examples.BbwChan;

public sealed class BbwChanPlugin : ISourcePlugin
{
    public const string SourceId = "bbwchan";
    private const string Host = "bbw-chan.link";
    private static readonly Uri Origin = new Uri("https://" + Host + "/");
    private static readonly SettingsSchema EmptySchema = new SettingsSchema("1", Array.Empty<SettingDefinition>());

    public PluginDescription Describe() => new PluginDescription("meowchan.source.bbwchan", "BBW-Chan",
        "1.0.0", SdkContract.Version,
        "Reads the documented BBW-Chan LynxChan catalog, board-page and thread JSON resources.",
        [Description()], Array.Empty<FilterDescription>(), EmptySchema,
        [new InformationEntry("support", "Accepted links", InformationKind.Text,
            "Accepts documented board and thread links from bbw-chan.link."),
         new InformationEntry("maturity", "Status", InformationKind.Text,
            "MeowChan · experimental; the .link host is the only configured host."),
         new InformationEntry("limitations", "Limitations", InformationKind.Text,
            "No legacy .nl fallback, .xyz alias, overboard, posting, login, webring or preview support. Catalog file lists are not treated as complete thread files.")],
        Author: "MeowChan contributors", DocumentationUrl: new Uri("https://bbw-chan.link/"));

    private static SourceDescription Description() => new SourceDescription(SourceId, "BBW-Chan", Origin,
        SourceCapabilities.ThreadRead | SourceCapabilities.BoardCatalog | SourceCapabilities.PostCapture | SourceCapabilities.OriginalMedia,
        [new UrlRoute(SourceResourceKind.Board, Host, "/{board}/", IsCanonical: true)
            { BoardIdentifierKind = RouteIdentifierKind.AsciiWord },
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
            return new CatalogFailure(request.Board, new FailureDetail("unsupported_cursor",
                "BBW-Chan documents numbered board pages, not cursor pagination."));
        if (request.Page is < 0)
            return new CatalogFailure(request.Board, new FailureDetail("invalid_page", "Page must be nonnegative."));

        int? requestedPage = request.Page;
        Uri endpoint = requestedPage is int page
            ? BoardPage(request.Board.BoardId, page)
            : new Uri(Origin, Uri.EscapeDataString(request.Board.BoardId) + "/catalog.json");
        HostHttpResult result = await context.Http.SendAsync(new HostHttpRequest(SourceId, HostHttpMethod.Get, endpoint,
            Headers: new Dictionary<string, string> { ["Accept"] = "application/json" },
            Validators: request.HasSnapshot ? request.Validators : null), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result is HostHttpDeferred deferred)
            return new CatalogDeferred(request.Board, deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode);
        if (result is HostHttpFailure failure) return new CatalogFailure(request.Board, failure.Failure);
        if (result is not HostHttpResponse response) return new CatalogFailure(request.Board, InvalidHttpResult());
        if (!SameEndpoint(endpoint, response.FinalUri))
            return new CatalogFailure(request.Board, UnexpectedEndpoint(response.StatusCode));
        if (response.StatusCode == 304)
            return request.HasSnapshot && HasHttpValidator(request.Validators)
                ? new CatalogUnchanged(request.Board, request.Validators!)
                : new CatalogFailure(request.Board, MissingBaseline(304));
        if (response.StatusCode == 404)
            return new CatalogFailure(request.Board, new FailureDetail("board_unavailable",
                "The documented BBW-Chan board resource was not found.", 404));
        if (response.StatusCode != 200) return new CatalogFailure(request.Board, HttpFailure(response.StatusCode));
        if (response.Body.Length > SdkLimits.MaximumMetadataBytes)
            return new CatalogFailure(request.Board, TooLarge());
        try
        {
            using JsonDocument document = JsonDocument.Parse(response.Body);
            CatalogSnapshot snapshot = ParseCatalog(request.Board, document.RootElement, response.Validators, requestedPage);
            return SdkValidation.ValidateCatalogResult(request, snapshot, Description()).Count == 0
                ? snapshot
                : new CatalogFailure(request.Board, InvalidPayload());
        }
        catch (Exception error) when (PayloadError(error))
        {
            return new CatalogFailure(request.Board, InvalidPayload());
        }
    }

    public async Task<ThreadReadResult> ReadThreadAsync(ThreadReadRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Thread.SourceId != SourceId || !BoardValid(request.Thread.BoardId)
            || !DecimalIdValid(request.Thread.ThreadId))
            return new ThreadFailure(request.Thread, InvalidResource());

        Uri endpoint = new Uri(Origin, Uri.EscapeDataString(request.Thread.BoardId) + "/res/"
            + Uri.EscapeDataString(request.Thread.ThreadId) + ".json");
        HostHttpResult result = await context.Http.SendAsync(new HostHttpRequest(SourceId, HostHttpMethod.Get, endpoint,
            Headers: new Dictionary<string, string> { ["Accept"] = "application/json" },
            Validators: request.HasSnapshot ? request.Validators : null), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result is HostHttpDeferred deferred)
            return new ThreadDeferred(request.Thread, deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode);
        if (result is HostHttpFailure failure) return new ThreadFailure(request.Thread, failure.Failure);
        if (result is not HostHttpResponse response) return new ThreadFailure(request.Thread, InvalidHttpResult());
        if (!SameEndpoint(endpoint, response.FinalUri))
            return new ThreadFailure(request.Thread, UnexpectedEndpoint(response.StatusCode));
        if (response.StatusCode == 404) return new ThreadNotFound(request.Thread, 404);
        if (response.StatusCode == 304)
            return request.HasSnapshot && HasHttpValidator(request.Validators)
                ? new ThreadUnchanged(request.Thread, request.Validators!)
                : new ThreadFailure(request.Thread, MissingBaseline(304));
        if (response.StatusCode != 200) return new ThreadFailure(request.Thread, HttpFailure(response.StatusCode));
        if (response.Body.Length > SdkLimits.MaximumMetadataBytes)
            return new ThreadFailure(request.Thread, TooLarge());
        try
        {
            using JsonDocument document = JsonDocument.Parse(response.Body);
            ThreadSnapshot snapshot = ParseThread(request.Thread, document.RootElement, response.Validators);
            return SdkValidation.ValidateThreadResult(request, snapshot, Description()).Count == 0
                ? snapshot
                : new ThreadFailure(request.Thread, InvalidPayload());
        }
        catch (Exception error) when (PayloadError(error))
        {
            return new ThreadFailure(request.Thread, InvalidPayload());
        }
    }

    private static CatalogSnapshot ParseCatalog(SourceBoardId board, JsonElement root,
        SourceValidators? validators, int? requestedPage)
    {
        JsonElement entries;
        int? pageCount = null;
        if (root.ValueKind == JsonValueKind.Array)
            entries = root;
        else
        {
            entries = root.GetProperty("threads");
            pageCount = OptionalInt(root, "pageCount");
        }
        ArrayBudget(entries, SdkLimits.MaximumCollectionCount);
        List<SourceThreadReference> threads = new List<SourceThreadReference>();
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            string id = Id(entry, "threadId");
            if (!seen.Add(id)) throw new JsonException("Duplicate thread identity.");
            string? message = entry.TryGetProperty("message", out _) ? Text(entry, "message") : Text(entry, "markdown");
            SourceTextFormat format = entry.TryGetProperty("message", out _) ? SourceTextFormat.Html : SourceTextFormat.PlainText;
            threads.Add(new SourceThreadReference(new SourceThreadId(SourceId, board.BoardId, id),
                ThreadUri(board.BoardId, id), Text(entry, "subject"), message, format,
                Time(entry, "creation"), OptionalInt(entry, "postCount"), OptionalInt(entry, "fileCount")));
        }
        int? nextPage = null;
        if (requestedPage is int page && pageCount is int count)
        {
            int currentWirePage = page == 0 ? 1 : page;
            if (currentWirePage < count) nextPage = currentWirePage + 1;
        }
        return new CatalogSnapshot(new SourceCatalogSnapshot(board, threads.AsReadOnly(), IsComplete: false,
            NextPage: nextPage, Validators: validators));
    }

    private static ThreadSnapshot ParseThread(SourceThreadId thread, JsonElement root, SourceValidators? validators)
    {
        if (Id(root, "threadId") != thread.ThreadId) throw new JsonException("Thread identity mismatch.");
        if (BoardValue(root, "boardUri") is string board && !BoardMatches(board, thread.BoardId))
            throw new JsonException("Board identity mismatch.");
        string openerId = root.TryGetProperty("postId", out _) ? Id(root, "postId") : thread.ThreadId;
        List<SourcePost> posts = new List<SourcePost> { ParsePost(thread, root, openerId, 0) };
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal) { openerId };
        if (root.TryGetProperty("posts", out JsonElement replies))
        {
            ArrayBudget(replies, SdkLimits.MaximumCollectionCount - 1);
            foreach (JsonElement reply in replies.EnumerateArray())
            {
                string id = Id(reply, "postId");
                if (!seen.Add(id)) throw new JsonException("Duplicate post identity.");
                if (BoardValue(reply, "boardUri") is string replyBoard && !BoardMatches(replyBoard, thread.BoardId))
                    throw new JsonException("Reply board identity mismatch.");
                posts.Add(ParsePost(thread, reply, id, posts.Count));
            }
        }
        return new ThreadSnapshot(new SourceThreadSnapshot(thread, ThreadUri(thread.BoardId, thread.ThreadId),
            Text(root, "subject"), SourceThreadState.Active, posts.AsReadOnly(), validators));
    }

    private static SourcePost ParsePost(SourceThreadId thread, JsonElement entry, string postId, long ordinal)
    {
        SourceFieldPresence fields = Present(entry, "creation", SourceFieldPresence.PostedAt)
            | Present(entry, "subject", SourceFieldPresence.Subject)
            | Present(entry, "name", SourceFieldPresence.Author);
        bool hasHtml = entry.TryGetProperty("message", out _);
        bool hasMarkdown = entry.TryGetProperty("markdown", out _);
        if (hasHtml || hasMarkdown) fields |= SourceFieldPresence.Text;
        (string? text, SourceTextFormat format) = hasHtml
            ? (Text(entry, "message"), SourceTextFormat.Html)
            : (Text(entry, "markdown"), SourceTextFormat.PlainText);
        List<SourceAttachment>? attachments = null;
        if (entry.TryGetProperty("files", out JsonElement files))
        {
            ArrayBudget(files, 1024);
            fields |= SourceFieldPresence.Attachments;
            attachments = new List<SourceAttachment>();
            int position = 0;
            foreach (JsonElement file in files.EnumerateArray())
                attachments.Add(ParseAttachment(file, postId, position++));
        }
        bool deleted = entry.TryGetProperty("deleted", out JsonElement deletedValue)
            && deletedValue.ValueKind == JsonValueKind.True;
        if (deleted) fields |= SourceFieldPresence.IsDeleted;
        return new SourcePost(postId, ordinal, fields, Time(entry, "creation"), Text(entry, "subject"),
            Text(entry, "name"), text, format, deleted, attachments?.AsReadOnly());
    }

    private static SourceAttachment ParseAttachment(JsonElement file, string postId, int position)
    {
        bool deleted = file.TryGetProperty("deleted", out JsonElement deletedValue)
            && deletedValue.ValueKind == JsonValueKind.True;
        if (deleted) return new SourceAttachment(position, null, IsDeleted: true);
        string? path = Text(file, "path");
        if (path is null || !TryMediaUri(path, out Uri? url) || url is null)
            throw new JsonException("Missing or foreign original media path.");
        string? extension = Text(file, "extension") ?? PathExtension(url.AbsolutePath);
        return new SourceAttachment(position,
            postId + "-" + position.ToString(CultureInfo.InvariantCulture), Url: url,
            OriginalFileName: Text(file, "originalName"), Extension: extension,
            ExpectedSize: OptionalLong(file, "size"), MimeType: Text(file, "mime"));
    }

    private static bool TryMediaUri(string value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(Origin, value, out Uri? candidate) || candidate is null
            || candidate.Scheme != Origin.Scheme || candidate.IdnHost != Host
            || candidate.Query.Length != 0 || candidate.Fragment.Length != 0 || candidate.UserInfo.Length != 0
            || !candidate.AbsolutePath.StartsWith("/.media/", StringComparison.Ordinal)
            || candidate.AbsolutePath.Count(c => c == '/') != 2
            || candidate.AbsolutePath.Contains("..", StringComparison.Ordinal)) return false;
        uri = candidate;
        return true;
    }

    private static Uri BoardPage(string board, int page)
        => new Uri(Origin, Uri.EscapeDataString(board) + "/"
            + (page == 0 ? "1" : page.ToString(CultureInfo.InvariantCulture)) + ".json");
    private static Uri ThreadUri(string board, string thread)
        => new Uri(Origin, Uri.EscapeDataString(board) + "/res/"
            + Uri.EscapeDataString(thread) + ".html");
    private static bool SameEndpoint(Uri expected, Uri actual)
        => actual.IsAbsoluteUri && actual.Scheme == expected.Scheme && actual.IdnHost == expected.IdnHost
            && actual.Port == expected.Port && actual.AbsolutePath == expected.AbsolutePath
            && actual.Query == expected.Query && actual.Fragment.Length == 0 && actual.UserInfo.Length == 0;
    private static bool BoardValid(string board)
        => !string.IsNullOrWhiteSpace(board) && board.Length <= 64
            && board.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
    private static bool DecimalIdValid(string id)
        => !string.IsNullOrEmpty(id) && id.Length <= SdkLimits.MaximumIdentifierLength
            && id.All(c => c is >= '0' and <= '9') && (id.Length == 1 || id[0] != '0');
    private static bool BoardMatches(string value, string expected)
    {
        string normalized = value.Trim().Trim('/');
        int slash = normalized.LastIndexOf('/');
        if (slash >= 0) normalized = normalized[(slash + 1)..];
        return normalized == expected;
    }
    private static string? BoardValue(JsonElement entry, string name)
        => entry.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
    private static string? PathExtension(string path)
    {
        int slash = path.LastIndexOf('/'); int dot = path.LastIndexOf('.');
        return dot > slash && dot < path.Length - 1 ? path[dot..] : null;
    }
    private static bool HasHttpValidator(SourceValidators? validators)
        => !string.IsNullOrWhiteSpace(validators?.EntityTag) || validators?.LastModified is not null
            || !string.IsNullOrWhiteSpace(validators?.Revision);
    private static void ArrayBudget(JsonElement value, int maximum)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximum)
            throw new JsonException("Metadata collection exceeds the SDK limit.");
    }
    private static SourceFieldPresence Present(JsonElement item, string name, SourceFieldPresence field)
        => item.TryGetProperty(name, out _) ? field : SourceFieldPresence.None;
    private static string Id(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out JsonElement value)) throw new JsonException("Missing identity.");
        string? id = value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)
                ? number.ToString(CultureInfo.InvariantCulture) : null;
        if (id is null || !DecimalIdValid(id)) throw new JsonException("Invalid decimal identity.");
        return id;
    }
    private static string? Text(JsonElement item, string name)
        => item.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : throw new JsonException("Expected text.")
            : null;
    private static long? OptionalLong(JsonElement item, string name)
        => item.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null
            ? value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)
                ? number : throw new JsonException("Expected integer.")
            : null;
    private static int? OptionalInt(JsonElement item, string name)
        => OptionalLong(item, name) is long value ? checked((int)value) : null;
    private static DateTimeOffset? Time(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long seconds))
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(),
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset parsed)) return parsed;
        throw new JsonException("Invalid timestamp.");
    }
    private static FailureDetail InvalidResource() => new FailureDetail("invalid_resource", "Source, board or thread identity is invalid.");
    private static FailureDetail InvalidHttpResult() => new FailureDetail("invalid_http_result", "Unknown host HTTP result.");
    private static FailureDetail UnexpectedEndpoint(int? status) => new FailureDetail("unexpected_endpoint", "The response is not from the requested BBW-Chan metadata endpoint.", status);
    private static FailureDetail MissingBaseline(int status) => new FailureDetail("missing_baseline", "304 requires an accepted resource and its HTTP validator.", status);
    private static FailureDetail HttpFailure(int status) => new FailureDetail("http_failure", "The BBW-Chan JSON response was not a verified resource result.", status);
    private static FailureDetail InvalidPayload() => new FailureDetail("invalid_payload", "The JSON response has no supported bounded BBW-Chan shape.", 200);
    private static FailureDetail TooLarge() => new FailureDetail("metadata_too_large", "The metadata response exceeds the SDK body limit.", 200);
    private static bool PayloadError(Exception error) => error is JsonException or KeyNotFoundException
        or InvalidDataException or InvalidOperationException or FormatException or OverflowException or ArgumentException;
}

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

namespace MeowChan.Plugins.Examples.Kohlchan;

public sealed class KohlchanPlugin : ISourcePlugin
{
    public const string SourceId = "kohlchan";
    private const string Host = "kohlchan.net";
    private static readonly Uri Origin = new Uri("https://" + Host + "/");
    private static readonly SettingsSchema EmptySchema = new SettingsSchema("1", Array.Empty<SettingDefinition>());

    public PluginDescription Describe() => new PluginDescription("meowchan.source.kohlchan", "Kohlchan — example source",
        "1.0.0", SdkContract.Version,
        "Editable example source. Limited public API observations and synthetic fixtures; full product acceptance remains separate.",
        [Description()], Array.Empty<FilterDescription>(), EmptySchema,
        [new InformationEntry("support", "Support status", InformationKind.Text,
            "Example source; bounded metadata access observed on 2026-10-22. See README for remaining acceptance checks."),
         new InformationEntry("limitations", "Limitations", InformationKind.Text, "No global board list or previews. Catalog coverage is conservative; bounded live catalogs exceeded the evidence limit.")],
        Author: "MeowChan contributors", DocumentationUrl: new Uri("https://kohlchan.net/info.html"));

    private static SourceDescription Description() => new SourceDescription(SourceId, "Kohlchan — example source", Origin,
        SourceCapabilities.ThreadRead | SourceCapabilities.BoardCatalog | SourceCapabilities.PostCapture | SourceCapabilities.OriginalMedia,
        [new UrlRoute(SourceResourceKind.Board, Host, "/{board}/", IsCanonical: true),
         new UrlRoute(SourceResourceKind.Thread, Host, "/{board}/res/{thread}.html", IsCanonical: true, AllowTrailingSlash: false)],
        [new HttpHostDeclaration(Host, HttpHostPurpose.Metadata | HttpHostPurpose.OriginalMedia)],
        EmptySchema, PostOrderKind.DecimalPostId);

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
        ArrayBudget(root, SdkLimits.MaximumCollectionCount);
        foreach (JsonElement entry in root.EnumerateArray())
        {
            SourceThreadId thread = new SourceThreadId(SourceId, board.BoardId, Id(entry, "threadId"));
            (string? text, SourceTextFormat format) = Body(entry);
            threads.Add(new SourceThreadReference(thread, ThreadUri(thread), Text(entry, "subject"), text, format,
                ReplyCount: Count(entry, "postCount")));
            // fileCount counts replies only; it is not silently presented as total attachments.
        }
        return new CatalogSnapshot(new SourceCatalogSnapshot(board, threads.AsReadOnly(), IsComplete: false, Validators: validators));
    }

    private static ThreadSnapshot ParseThread(SourceThreadId thread, JsonElement root, SourceValidators? validators)
    {
        if (Id(root, "threadId") != thread.ThreadId || Text(root, "boardUri") != thread.BoardId)
            throw new JsonException("Thread resource mismatch.");
        List<SourcePost> posts = new List<SourcePost> { Post(root, thread.ThreadId) };
        JsonElement postArray = root.GetProperty("posts");
        ArrayBudget(postArray, SdkLimits.MaximumCollectionCount - 1);
        foreach (JsonElement entry in postArray.EnumerateArray()) posts.Add(Post(entry, Id(entry, "postId")));
        // LynxChan documents archived separately from locked/autoSage/cyclic/trash.
        bool archived = root.TryGetProperty("archived", out JsonElement value) && value.GetBoolean();
        return new ThreadSnapshot(new SourceThreadSnapshot(thread, ThreadUri(thread), Text(root, "subject"),
            archived ? SourceThreadState.Archived : SourceThreadState.Active, posts.AsReadOnly(), validators));
    }

    private static SourcePost Post(JsonElement entry, string postId)
    {
        SourceFieldPresence fields = Present(entry, "creation", SourceFieldPresence.PostedAt)
            | Present(entry, "subject", SourceFieldPresence.Subject) | Present(entry, "name", SourceFieldPresence.Author);
        (string? text, SourceTextFormat format) = Body(entry);
        if (entry.TryGetProperty("markdown", out _) || entry.TryGetProperty("message", out _)) fields |= SourceFieldPresence.Text;
        DateTimeOffset? created = Text(entry, "creation") is string time
            ? DateTimeOffset.Parse(time, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal) : null;
        List<SourceAttachment>? attachments = null;
        if (entry.TryGetProperty("files", out JsonElement files))
        {
            fields |= SourceFieldPresence.Attachments;
            ArrayBudget(files, 1024);
            attachments = new List<SourceAttachment>();
            foreach (JsonElement file in files.EnumerateArray())
            {
                string path = file.GetProperty("path").GetString() ?? throw new JsonException("Missing original path.");
                // Resolve only absolute same-origin media paths actually described by LynxChan.
                if (!path.StartsWith("/.media/", StringComparison.Ordinal) || path.Length <= 8
                    || path[8..].Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
                    throw new JsonException("Unsupported media path.");
                Uri url = new Uri(Origin, path);
                if (url.Scheme != Origin.Scheme || url.IdnHost != Host || url.Port != Origin.Port || !url.AbsolutePath.StartsWith("/.media/", StringComparison.Ordinal))
                    throw new JsonException("Unsupported media origin.");
                string name = url.AbsolutePath[(url.AbsolutePath.LastIndexOf('/') + 1)..];
                int dot = name.LastIndexOf('.');
                string? extension = dot >= 0 ? name[dot..] : null;
                int position = attachments.Count;
                attachments.Add(new SourceAttachment(position, postId + "-" + position.ToString(CultureInfo.InvariantCulture), Url: url,
                    OriginalFileName: Text(file, "originalName"), Extension: extension,
                    ExpectedSize: Integer(file, "size"), MimeType: Text(file, "mime")));
            }
        }
        return new SourcePost(postId, 0, fields, created, Text(entry, "subject"), Text(entry, "name"), text, format,
            Attachments: attachments?.AsReadOnly());
    }

    private static (string? Text, SourceTextFormat Format) Body(JsonElement item) => item.TryGetProperty("markdown", out _)
        ? (Text(item, "markdown"), SourceTextFormat.Html) : (Text(item, "message"), SourceTextFormat.PlainText);

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

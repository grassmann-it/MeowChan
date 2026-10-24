// This is a complete editable drop-in source: only framework + MeowChan.PluginSdk references.
// libmeowchan links this exact file with MEOWCHAN_CORE, sharing the parser without an SDK dependency.
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
#if !MEOWCHAN_CORE
using MeowChan.PluginSdk;
#endif

namespace MeowChan.OfficialSources.EightKun;

#if !MEOWCHAN_CORE
public sealed class EightKunPlugin : ISourcePlugin
{
    private static readonly SettingsSchema EmptySettings = new("1", Array.Empty<SettingDefinition>());
    public PluginDescription Describe() => new("meowchan.official.8kun", "8kun", "1.0.0", SdkContract.Version,
        "Official 8kun source: catalog, captured posts and original media metadata.",
        new SourceDescription[]
        {
            new("8kun", "8kun", new Uri("https://8kun.top/"),
                SourceCapabilities.ThreadRead | SourceCapabilities.BoardCatalog | SourceCapabilities.PostCapture | SourceCapabilities.OriginalMedia,
                new UrlRoute[] { new(SourceResourceKind.Board, "8kun.top", "/{board}/", IsCanonical: true, LowercaseBoardId: true) { BoardIdentifierKind = RouteIdentifierKind.AsciiWord },
                new(SourceResourceKind.Board, "8kun.top", "/{board}/catalog", LowercaseBoardId: true) { BoardIdentifierKind = RouteIdentifierKind.AsciiWord },
                new(SourceResourceKind.Board, "8kun.top", "/{board}/catalog.html", LowercaseBoardId: true) { BoardIdentifierKind = RouteIdentifierKind.AsciiWord },
                new(SourceResourceKind.Board, "8kun.top", "/{board}/index.html", LowercaseBoardId: true) { BoardIdentifierKind = RouteIdentifierKind.AsciiWord },
                new(SourceResourceKind.Thread, "8kun.top", "/{board}/res/{thread}.html", IsCanonical: true, LowercaseBoardId: true) { BoardIdentifierKind = RouteIdentifierKind.AsciiWord, ThreadIdentifierKind = RouteIdentifierKind.Decimal } },
                new HttpHostDeclaration[] { new("8kun.top", HttpHostPurpose.Metadata), new("nerv.8kun.top", HttpHostPurpose.OriginalMedia) }, EmptySettings, PostOrderKind.DecimalPostId)
        }, Array.Empty<FilterDescription>(), EmptySettings,
        new InformationEntry[] { new("capture", "Captured content", InformationKind.Text,
            "Post text is HTML. Missing fields preserve earlier captures. Media acquisition and watch state belong to MeowChan.") },
        Author: "MeowChan", DocumentationUrl: new Uri("https://github.com/grassmann-it/MeowChan"));

    public async Task<CatalogReadResult> ReadCatalogAsync(CatalogReadRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        if (!EightKunParser.ValidBoard(request.Board.SourceId, request.Board.BoardId) || request.Cursor is not null || request.Page is not null)
            return new CatalogFailure(request.Board, new FailureDetail("invalid_request", "This source reads one complete board catalog without a cursor or page."));
        HostHttpResult result = await context.Http.SendAsync(new HostHttpRequest("8kun", HostHttpMethod.Get,
            new Uri($"https://8kun.top/{request.Board.BoardId}/catalog.json"), Validators: request.HasSnapshot ? request.Validators : null), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (result is HostHttpDeferred deferred) return new CatalogDeferred(request.Board, deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode);
        if (result is HostHttpFailure failure) return new CatalogFailure(request.Board, failure.Failure);
        HostHttpResponse response = (HostHttpResponse)result;
        if (response.StatusCode == 304)
            return request.HasSnapshot && HasValidator(response.Validators ?? request.Validators) ? new CatalogUnchanged(request.Board, (response.Validators ?? request.Validators)!)
                : new CatalogFailure(request.Board, new FailureDetail("unexpected_304", "A 304 requires an accepted snapshot.", 304));
        if (response.StatusCode != 200) return new CatalogFailure(request.Board, HttpFailure(response));
        if (response.Body.Length > SdkLimits.MaximumMetadataBytes) return new CatalogFailure(request.Board, new FailureDetail("metadata_too_large", "Metadata exceeds the SDK body limit."));
        try
        {
            IReadOnlyList<EightKunParser.CatalogEntry> entries = EightKunParser.ParseCatalog(Encoding.UTF8.GetString(response.Body), cancellationToken);
            return new CatalogSnapshot(new SourceCatalogSnapshot(request.Board, entries.Select(entry => new SourceThreadReference(
                new SourceThreadId("8kun", request.Board.BoardId, entry.Id), EightKunParser.ThreadUri(request.Board.BoardId, entry.Id),
                entry.Title, entry.Text, SourceTextFormat.Html)).ToArray(), true, Validators: response.Validators));
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException or OverflowException or FormatException or InvalidOperationException)
        { return new CatalogFailure(request.Board, new FailureDetail("invalid_metadata", "The catalog response is invalid.")); }
    }

    public async Task<ThreadReadResult> ReadThreadAsync(ThreadReadRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        if (!EightKunParser.ValidBoard(request.Thread.SourceId, request.Thread.BoardId) || !EightKunParser.ValidId(request.Thread.ThreadId))
            return new ThreadFailure(request.Thread, new FailureDetail("invalid_request", "Invalid source, board or thread identity."));
        HostHttpResult result = await context.Http.SendAsync(new HostHttpRequest("8kun", HostHttpMethod.Get,
            EightKunParser.ThreadEndpoint(request.Thread.BoardId, request.Thread.ThreadId), Validators: request.HasSnapshot ? request.Validators : null), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (result is HostHttpDeferred deferred) return new ThreadDeferred(request.Thread, deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode);
        if (result is HostHttpFailure failure) return new ThreadFailure(request.Thread, failure.Failure);
        HostHttpResponse response = (HostHttpResponse)result;
        if (response.StatusCode == 304)
            return request.HasSnapshot && HasValidator(response.Validators ?? request.Validators) ? new ThreadUnchanged(request.Thread, (response.Validators ?? request.Validators)!)
                : new ThreadFailure(request.Thread, new FailureDetail("unexpected_304", "A 304 requires an accepted snapshot.", 304));
        if (response.StatusCode == 404) return new ThreadNotFound(request.Thread, 404);
        if (response.StatusCode != 200) return new ThreadFailure(request.Thread, HttpFailure(response));
        if (response.Body.Length > SdkLimits.MaximumMetadataBytes) return new ThreadFailure(request.Thread, new FailureDetail("metadata_too_large", "Metadata exceeds the SDK body limit."));
        try
        {
            EightKunParser.ThreadData parsed = EightKunParser.ParseThread(Encoding.UTF8.GetString(response.Body), request.Thread.BoardId, request.Thread.ThreadId, cancellationToken);
            return new ThreadSnapshot(new SourceThreadSnapshot(request.Thread, EightKunParser.ThreadUri(request.Thread.BoardId, request.Thread.ThreadId),
                parsed.Title, parsed.Archived ? SourceThreadState.Archived : SourceThreadState.Active,
                parsed.Posts.Select(post => new SourcePost(post.Id, post.Ordinal, (SourceFieldPresence)post.Fields,
                    post.PostedAt, post.Subject, post.Author, post.Text, SourceTextFormat.Html, post.Deleted,
                    (post.Fields & 32) != 0 ? post.Attachments.Select(file => new SourceAttachment(file.Position, file.Id,
                        Url: file.Url, OriginalFileName: file.FileName, Extension: file.Extension,
                        ExpectedSize: file.Size, ExpectedMd5: file.Md5, IsDeleted: file.Deleted)).ToArray() : null)).ToArray(), response.Validators));
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException or OverflowException or FormatException or InvalidOperationException)
        { return new ThreadFailure(request.Thread, new FailureDetail("invalid_metadata", "The thread response is invalid.")); }
    }

    private static bool HasValidator(SourceValidators? validators) => validators is not null
        && (validators.EntityTag is not null || validators.LastModified is not null || validators.Revision is not null);

    private static FailureDetail HttpFailure(HostHttpResponse response) => new("http_status", "The source did not return a complete HTTP 200 response.", response.StatusCode);
}
#endif

// Passive framework-only records: never local paths, downloader or watch decisions.
internal static class EightKunParser
{
    internal sealed record Attachment(int Position, string? Id, Uri? Url = null, string? FileName = null,
        string? Extension = null, long? Size = null, string? Md5 = null, bool Deleted = false);
    internal sealed record Post(string Id, int Ordinal, int Fields, DateTimeOffset? PostedAt, string? Subject,
        string? Author, string? Text, bool Deleted, IReadOnlyList<Attachment> Attachments);
    internal sealed record ThreadData(string? Title, bool Archived, IReadOnlyList<Post> Posts);
    internal sealed record CatalogEntry(string Id, string? Title, string? Text);

    internal static bool ValidBoard(string source, string board) => source == "8kun" && board.Length is > 0 and <= 256
        && board.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    internal static bool ValidId(string id) => id.Length is > 0 and <= 256 && id.All(char.IsAsciiDigit);
    internal static Uri ThreadUri(string board, string id) => new($"https://8kun.top/{board}/res/{id}.html");
    internal static Uri ThreadEndpoint(string board, string id) => new($"https://8kun.top/{board}/res/{id}.json");

    internal static IReadOnlyList<CatalogEntry> ParseCatalog(string json, CancellationToken cancellationToken = default)
    {
        using JsonDocument document = JsonDocument.Parse(json.Length > 0 && json[0] == '\uFEFF' ? json[1..] : json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Catalog must be an array.");
        List<CatalogEntry> entries = new();
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (JsonElement page in document.RootElement.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!page.TryGetProperty("threads", out JsonElement threads) || threads.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Catalog page does not contain threads.");
            foreach (JsonElement entry in threads.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                long? number = Number(entry, "no");
                if (number is null or <= 0) throw new InvalidDataException("Invalid catalog thread ID.");
                string id = number.Value.ToString(CultureInfo.InvariantCulture);
                if (!ids.Add(id)) throw new InvalidDataException("Duplicate catalog thread ID.");
                entries.Add(new CatalogEntry(id, WebUtility.HtmlDecode(Text(entry, "sub")), Text(entry, "com")));
            }
        }
        return entries;
    }

    internal static ThreadData ParseThread(string json, string board, string threadId, CancellationToken cancellationToken = default)
    {
        using JsonDocument document = JsonDocument.Parse(json.Length > 0 && json[0] == '\uFEFF' ? json[1..] : json);
        if (!document.RootElement.TryGetProperty("posts", out JsonElement posts) || posts.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Thread data does not contain posts.");
        List<Post> captured = new();
        HashSet<string> ids = new(StringComparer.Ordinal);
        string? title = null;
        bool archived = false;
        int ordinal = 0;
        foreach (JsonElement post in posts.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ordinal == 0)
            {
                title = WebUtility.HtmlDecode(Text(post, "sub"));
                // 8kun locked/archived flags are not established end signals; only a thread 404 ends acquisition.
            }
            List<Attachment> attachments = new();
            CaptureAttachment(post, 0, board, attachments);
            if (post.TryGetProperty("extra_files", out JsonElement extraFiles) && extraFiles.ValueKind != JsonValueKind.Null)
            {
                if (extraFiles.ValueKind != JsonValueKind.Array) throw new InvalidDataException("extra_files must be an array.");
                int position = 1;
                foreach (JsonElement file in extraFiles.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CaptureAttachment(file, position++, board, attachments);
                }
            }
            string id = Text(post, "no") ?? (ordinal == 0 ? threadId
                : throw new InvalidDataException("A reply does not contain a post ID."));
            if (!ValidId(id) || !ids.Add(id)) throw new InvalidDataException("Invalid or duplicate post ID.");
            int fields = (Has(post, "time") ? 1 : 0) | (Has(post, "sub") ? 2 : 0) | (Has(post, "name") ? 4 : 0)
                | (Has(post, "com") ? 8 : 0) | (Has(post, "deleted") ? 16 : 0)
                | (attachments.Count > 0 || Has(post, "extra_files") ? 32 : 0);
            long? seconds = Number(post, "time");
            captured.Add(new Post(id, ordinal, fields, seconds.HasValue ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value) : null,
                WebUtility.HtmlDecode(Text(post, "sub")), WebUtility.HtmlDecode(Text(post, "name")), Text(post, "com"),
                Number(post, "deleted") == 1, attachments));
            ordinal++;
        }
        return new ThreadData(title, archived, captured);
    }

    private static void CaptureAttachment(JsonElement file, int position, string board, List<Attachment> attachments)
    {
        if (Number(file, "filedeleted") == 1) { attachments.Add(new Attachment(position, null, Deleted: true)); return; }
        string? id = Text(file, "tim");
        string? extension = Text(file, "ext");
        string? filename = Text(file, "filename");

        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(extension)) return;
        if (id.Length > 256 || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_' && character != '-'))
            throw new InvalidDataException("Invalid media identity.");
        if (extension.Length is < 2 or > 33 || extension[0] != '.' || !extension.AsSpan(1).ToArray().All(char.IsAsciiLetterOrDigit))
            throw new InvalidDataException("Invalid media extension.");
        attachments.Add(new Attachment(position, id, new Uri($"https://nerv.8kun.top/file_store/{id}{extension}"),
            (string.IsNullOrWhiteSpace(filename) ? id : filename) + extension, extension, Number(file, "fsize"), Text(file, "md5")));
    }

    private static bool Has(JsonElement element, string name) => element.TryGetProperty(name, out _);
    private static string? Text(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind switch { JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetRawText(),
            _ => throw new InvalidDataException("Invalid metadata field: " + name) };
    }
    private static long? Number(JsonElement element, string name)
    {
        string? text = Text(element, name);
        return text is null ? null : long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }
}

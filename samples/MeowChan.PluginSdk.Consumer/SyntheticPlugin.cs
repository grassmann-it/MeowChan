using System.Text.Json;
using MeowChan.PluginSdk;

namespace MeowChan.PluginSdk.Consumer;

/// <summary>A complete synthetic source; it needs no Core/GUI objects or special host registration.</summary>
public sealed class SyntheticPlugin : ISourcePlugin, IPluginSettingsValidator
{
    public const string SourceId = "sample.synthetic";
    private static readonly SettingsSchema EmptySchema = new SettingsSchema("1", Array.Empty<SettingDefinition>());

    public PluginDescription Describe() => new PluginDescription("sample.synthetic.plugin", "Synthetic SDK example",
        "1.0.0", SdkContract.Version, "Synthetic thread/catalog content with two attachments and configurable labels.",
        [new SourceDescription(SourceId, "Synthetic source", new Uri("https://synthetic.example.test/"),
            SourceCapabilities.ThreadRead | SourceCapabilities.BoardCatalog | SourceCapabilities.PostCapture | SourceCapabilities.OriginalMedia,
            [new UrlRoute(SourceResourceKind.Board, "synthetic.example.test", "/{board}/", IsCanonical: true),
                new UrlRoute(SourceResourceKind.Thread, "synthetic.example.test", "/{board}/thread/{thread}", IsCanonical: true)],
            [new HttpHostDeclaration("synthetic.example.test", HttpHostPurpose.Metadata | HttpHostPurpose.OriginalMedia)],
            EmptySchema)], Array.Empty<FilterDescription>(),
        new SettingsSchema("1", [new SettingDefinition("label", SettingScope.Plugin, SettingKind.Text, "Thread label",
            Help: "A single line label for synthetic threads.", IsRequired: true,
            Default: new SettingValue("label", SettingKind.Text, Text: "SDK example"), MaximumLength: 100)]),
        [new InformationEntry("fixture", "Data source", InformationKind.Text, "All content is generated locally.")]);

    public async Task<CatalogReadResult> ReadCatalogAsync(CatalogReadRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        if (request.Board.SourceId != SourceId)
            return new CatalogFailure(request.Board, new FailureDetail("unknown_source", "This plugin does not own the requested source."));
        HostHttpResult response = await context.Http.SendAsync(new HostHttpRequest(SourceId, HostHttpMethod.Get,
            ApiUri(request.Board.BoardId, "catalog"), Validators: request.Validators), cancellationToken).ConfigureAwait(false);
        return response switch
        {
            HostHttpDeferred deferred => new CatalogDeferred(request.Board, deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode),
            HostHttpFailure failure => new CatalogFailure(request.Board, failure.Failure),
            HostHttpResponse body => ReadCatalogResponse(request, body),
            _ => new CatalogFailure(request.Board, new FailureDetail("invalid_http_result", "Host returned an unknown HTTP result."))
        };
    }

    public async Task<ThreadReadResult> ReadThreadAsync(ThreadReadRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        if (request.Thread.SourceId != SourceId)
            return new ThreadFailure(request.Thread, new FailureDetail("unknown_source", "This plugin does not own the requested source."));
        HostHttpResult response = await context.Http.SendAsync(new HostHttpRequest(SourceId, HostHttpMethod.Get,
            ApiUri(request.Thread.BoardId, request.Thread.ThreadId), Validators: request.Validators), cancellationToken).ConfigureAwait(false);
        return response switch
        {
            HostHttpDeferred deferred => new ThreadDeferred(request.Thread, deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode),
            HostHttpFailure failure => new ThreadFailure(request.Thread, failure.Failure),
            HostHttpResponse body => ReadThreadResponse(request, context.Configuration.PluginSettings, body),
            _ => new ThreadFailure(request.Thread, new FailureDetail("invalid_http_result", "Host returned an unknown HTTP result."))
        };
    }

    public Task<IReadOnlyList<FieldValidationError>> ValidateSettingsAsync(SettingsValidationRequest request,
        PluginCallContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? label = request.Settings.Values.FirstOrDefault(value => value.Key == "label")?.Text;
        IReadOnlyList<FieldValidationError> errors = label?.IndexOfAny(['\r', '\n']) >= 0
            ? [new FieldValidationError("label", "single_line", "The thread label must be a single line.")]
            : Array.Empty<FieldValidationError>();
        return Task.FromResult(errors);
    }

    private static CatalogReadResult ReadCatalogResponse(CatalogReadRequest request, HostHttpResponse response)
    {
        if (response.StatusCode == 304)
            return request.HasSnapshot && request.Validators is not null
                ? new CatalogUnchanged(request.Board, response.Validators ?? request.Validators)
                : new CatalogFailure(request.Board, new FailureDetail("missing_baseline", "304 arrived without an accepted catalog."));
        if (response.StatusCode != 200)
            return new CatalogFailure(request.Board, new FailureDetail("http_failure", "Catalog requires HTTP 200.", response.StatusCode));
        try
        {
            using JsonDocument document = JsonDocument.Parse(response.Body);
            List<SourceThreadReference> threads = new List<SourceThreadReference>();
            foreach (JsonElement entry in document.RootElement.EnumerateArray())
            {
                string id = entry.GetProperty("id").GetString() ?? throw new JsonException("Thread ID is missing.");
                SourceThreadId thread = new SourceThreadId(SourceId, request.Board.BoardId, id);
                threads.Add(new SourceThreadReference(thread, ThreadUri(thread), entry.GetProperty("title").GetString()));
            }
            return new CatalogSnapshot(new SourceCatalogSnapshot(request.Board, threads.AsReadOnly(), IsComplete: true,
                Validators: response.Validators));
        }
        catch (JsonException exception)
        {
            return new CatalogFailure(request.Board, new FailureDetail("invalid_json", exception.Message));
        }
    }

    private static ThreadReadResult ReadThreadResponse(ThreadReadRequest request, SettingsSnapshot settings, HostHttpResponse response)
    {
        if (response.StatusCode == 404) return new ThreadNotFound(request.Thread, 404);
        if (response.StatusCode == 304)
            return request.HasSnapshot && request.Validators is not null
                ? new ThreadUnchanged(request.Thread, response.Validators ?? request.Validators)
                : new ThreadFailure(request.Thread, new FailureDetail("missing_baseline", "304 arrived without an accepted thread."));
        if (response.StatusCode != 200)
            return new ThreadFailure(request.Thread, new FailureDetail("http_failure", "Thread requires HTTP 200.", response.StatusCode));
        try
        {
            using JsonDocument document = JsonDocument.Parse(response.Body);
            List<SourcePost> posts = new List<SourcePost>();
            foreach (JsonElement post in document.RootElement.GetProperty("posts").EnumerateArray())
            {
                string postId = post.GetProperty("id").GetString() ?? throw new JsonException("Post ID is missing.");
                List<SourceAttachment> attachments = new List<SourceAttachment>();
                if (post.TryGetProperty("files", out JsonElement files))
                    foreach (JsonElement file in files.EnumerateArray())
                    {
                        string mediaId = file.GetString() ?? throw new JsonException("Media ID is missing.");
                        attachments.Add(new SourceAttachment(attachments.Count, mediaId,
                            Url: new Uri("https://synthetic.example.test/media/" + Uri.EscapeDataString(mediaId) + ".png"),
                            OriginalFileName: mediaId + ".png", Extension: ".png", MimeType: "image/png") {
                                ExpectedSha1 = Convert.ToBase64String(new byte[20]),
                                ProviderSizeHint = new SourceSizeHint("2.4e1", "KiB", SourceSizePrecision.Unspecified) });
                    }
                SourceFieldPresence fields = SourceFieldPresence.None;
                string? text = null;
                if (post.TryGetProperty("text", out JsonElement body)) { fields |= SourceFieldPresence.Text; text = body.GetString(); }
                if (post.TryGetProperty("files", out _)) fields |= SourceFieldPresence.Attachments;
                posts.Add(new SourcePost(postId, post.GetProperty("order").GetInt64(), fields, Text: text,
                    TextFormat: SourceTextFormat.PlainText, Attachments: fields.HasFlag(SourceFieldPresence.Attachments) ? attachments.AsReadOnly() : null));
            }
            string title = settings.Values.FirstOrDefault(value => value.Key == "label")?.Text ?? "SDK example";
            bool archived = document.RootElement.TryGetProperty("archived", out JsonElement archivedValue) && archivedValue.GetBoolean();
            return new ThreadSnapshot(new SourceThreadSnapshot(request.Thread, ThreadUri(request.Thread), title,
                archived ? SourceThreadState.Archived : SourceThreadState.Active, posts.AsReadOnly(), response.Validators));
        }
        catch (JsonException exception)
        {
            return new ThreadFailure(request.Thread, new FailureDetail("invalid_json", exception.Message));
        }
    }

    private static Uri ApiUri(string board, string resource)
        => new Uri("https://synthetic.example.test/api/" + Uri.EscapeDataString(board) + "/" + Uri.EscapeDataString(resource) + ".json");

    private static Uri ThreadUri(SourceThreadId thread)
        => new Uri("https://synthetic.example.test/" + Uri.EscapeDataString(thread.BoardId) + "/thread/" + Uri.EscapeDataString(thread.ThreadId));
}

using MeowChan.PluginSdk;

namespace MeowChan.PluginSdk.Legacy102Probe;

/// <summary>
/// A deterministic source compiled against the published 1.0.2 SDK assembly.
/// This type intentionally implements only the old ISourcePlugin contract.
/// </summary>
public sealed class LegacySourcePlugin : ISourcePlugin
{
    private const string SourceId = "legacy102";
    private const string Host = "legacy102.invalid";
    private const string BaseUrl = "https://legacy102.invalid";
    private static readonly Uri BaseUri = new Uri(BaseUrl, UriKind.Absolute);
    private static readonly SettingsSchema EmptySettings = new SettingsSchema("legacy102-settings-1", Array.Empty<SettingDefinition>());

    public PluginDescription Describe()
    {
        SourceDescription source = new SourceDescription(
            SourceId,
            "Legacy 1.0.2 source",
            BaseUri,
            SourceCapabilities.ThreadRead | SourceCapabilities.BoardCatalog | SourceCapabilities.PostCapture,
            new[]
            {
                new UrlRoute(SourceResourceKind.Board, Host, "/boards/{board}", IsCanonical: true),
                new UrlRoute(SourceResourceKind.Thread, Host, "/boards/{board}/threads/{thread}", IsCanonical: true)
            },
            new[] { new HttpHostDeclaration(Host, HttpHostPurpose.Metadata | HttpHostPurpose.OriginalMedia) },
            EmptySettings);

        return new PluginDescription(
            "legacy102.probe",
            "Legacy 1.0.2 SDK probe",
            "1.0.2",
            SdkContract.Version,
            "Deterministic offline source used to exercise the published SDK binary.",
            new[] { source },
            Array.Empty<FilterDescription>(),
            EmptySettings,
            new[] { new InformationEntry("fixture", "Fixture", InformationKind.Value, "legacy-102") });
    }

    public Task<CatalogReadResult> ReadCatalogAsync(CatalogReadRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        SourceThreadId thread = new SourceThreadId(request.Board.SourceId, request.Board.BoardId, "thread-001");
        SourceThreadReference reference = new SourceThreadReference(
            thread,
            new Uri($"{BaseUrl}/boards/{request.Board.BoardId}/threads/{thread.ThreadId}", UriKind.Absolute),
            "Synthetic legacy thread",
            "Deterministic catalog fixture",
            SourceTextFormat.PlainText,
            new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero),
            ReplyCount: 1,
            AttachmentCount: 1);
        SourceCatalogSnapshot snapshot = new SourceCatalogSnapshot(
            request.Board,
            new[] { reference },
            IsComplete: true,
            Validators: new SourceValidators(Revision: "legacy102-catalog-1"));
        return Task.FromResult<CatalogReadResult>(new CatalogSnapshot(snapshot));
    }

    public Task<ThreadReadResult> ReadThreadAsync(ThreadReadRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        SourceThreadId thread = request.Thread;
        Uri threadUri = new Uri($"{BaseUrl}/boards/{thread.BoardId}/threads/{thread.ThreadId}", UriKind.Absolute);
        SourceAttachment attachment = new SourceAttachment(
            Position: 0,
            MediaId: "media-001",
            Variant: "original",
            Url: new Uri($"{BaseUrl}/media/{thread.ThreadId}-001.bin", UriKind.Absolute),
            OriginalFileName: "legacy-001.bin",
            Extension: ".bin",
            ExpectedSize: 16,
            MimeType: "application/octet-stream");
        SourcePost post = new SourcePost(
            "post-001",
            Ordinal: 1,
            SourceFieldPresence.PostedAt | SourceFieldPresence.Subject | SourceFieldPresence.Author
                | SourceFieldPresence.Text | SourceFieldPresence.Attachments,
            PostedAt: new DateTimeOffset(2026, 11, 14, 0, 0, 1, TimeSpan.Zero),
            Subject: "Legacy fixture",
            Author: "probe",
            Text: "Deterministic SDK 1.0.2 payload",
            TextFormat: SourceTextFormat.PlainText,
            Attachments: new[] { attachment });
        SourceThreadSnapshot snapshot = new SourceThreadSnapshot(
            thread,
            threadUri,
            "Synthetic legacy thread",
            SourceThreadState.Active,
            new[] { post },
            new SourceValidators(Revision: "legacy102-thread-1"));
        return Task.FromResult<ThreadReadResult>(new ThreadSnapshot(snapshot));
    }
}

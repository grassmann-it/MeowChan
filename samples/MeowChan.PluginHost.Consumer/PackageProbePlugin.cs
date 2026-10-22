// One synthetic SDK-only file, compiled both by the package consumer and by the optional delivered compiler probe.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.PluginSdk;
using SdkThreadSnapshot = MeowChan.PluginSdk.ThreadSnapshot;

namespace MeowChan.PluginHost.Consumer;

public sealed class PackageProbePlugin : ISourcePlugin, IImageFilterPlugin, IPluginSettingsValidator
{
    public const string PluginId = "consumer.package-probe";
    public const string SourceId = "consumer.synthetic";
    public const string FilterKey = "bytes";
    private static readonly SettingsSchema Empty = new SettingsSchema("1", Array.Empty<SettingDefinition>());

    public PluginDescription Describe() => new PluginDescription(PluginId, "Synthetic package probe", "1.0.0",
        SdkContract.Version, "Local package contract and optional child-process probe. No public HTTP.",
        [new SourceDescription(SourceId, "Synthetic", new Uri("https://package-fixture.invalid/"),
            SourceCapabilities.ThreadRead | SourceCapabilities.BoardCatalog | SourceCapabilities.PostCapture | SourceCapabilities.OriginalMedia,
            [new UrlRoute(SourceResourceKind.Board, "package-fixture.invalid", "/{board}/", IsCanonical: true),
             new UrlRoute(SourceResourceKind.Thread, "package-fixture.invalid", "/{board}/thread/{thread}", IsCanonical: true)],
            [new HttpHostDeclaration("package-fixture.invalid", HttpHostPurpose.Metadata | HttpHostPurpose.OriginalMedia)], Empty)],
        [new FilterDescription(FilterKey, "Exact synthetic bytes", "Reads four invocation-bound bytes.",
            FilterDataAccess.CandidateMetadata | FilterDataAccess.OriginalImage, Empty, ["image/png"])],
        new SettingsSchema("1", [new SettingDefinition("label", SettingScope.Plugin, SettingKind.Text, "Label",
            Default: new SettingValue("label", SettingKind.Text, Text: "default"), MaximumLength: 80)]),
        [new InformationEntry("fixture", "Data", InformationKind.Text, "All values and bytes are synthetic.")]);

    public async Task<ThreadReadResult> ReadThreadAsync(ThreadReadRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        HostHttpResult result = await context.Http.SendAsync(new HostHttpRequest(SourceId, HostHttpMethod.Get,
            new Uri("https://package-fixture.invalid/metadata.json")), cancellationToken).ConfigureAwait(false);
        if (result is not HostHttpResponse { StatusCode: 200 } response)
            return new ThreadFailure(request.Thread, new FailureDetail("fixture_http", "Expected synthetic metadata response."));
        using JsonDocument body = JsonDocument.Parse(response.Body);
        string? text = body.RootElement.GetProperty("text").GetString();
        return new SdkThreadSnapshot(new SourceThreadSnapshot(request.Thread, ThreadUri(request.Thread), "Package fixture",
            SourceThreadState.Active,
            [new SourcePost("1", 0, SourceFieldPresence.Text | SourceFieldPresence.Attachments,
                Text: text, TextFormat: SourceTextFormat.PlainText,
                Attachments: [new SourceAttachment(0, "1-0", Url: new Uri("https://package-fixture.invalid/image.png"), Extension: ".png", ExpectedSize: 4)])],
            new SourceValidators(EntityTag: "\"fixture\"")));
    }

    public Task<CatalogReadResult> ReadCatalogAsync(CatalogReadRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SourceThreadId thread = new SourceThreadId(SourceId, request.Board.BoardId, "1");
        return Task.FromResult<CatalogReadResult>(new CatalogSnapshot(new SourceCatalogSnapshot(request.Board,
            [new SourceThreadReference(thread, ThreadUri(thread), "Package fixture")], IsComplete: true)));
    }

    public async Task<ImageFilterResult> EvaluateAsync(ImageFilterRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        ImageAssetChunk chunk = await context.Images.ReadImageAsync(request.AssetToken, 0, 4, cancellationToken).ConfigureAwait(false);
        bool matches = chunk.Offset == 0 && chunk.IsEnd && chunk.Bytes.SequenceEqual(new byte[] { 1, 2, 3, 4 });
        return new ImageFilterResult(matches ? ImageFilterOutcome.Match : ImageFilterOutcome.NoMatch, "synthetic_bytes");
    }

    public Task<IReadOnlyList<FieldValidationError>> ValidateSettingsAsync(SettingsValidationRequest request,
        PluginCallContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<FieldValidationError>>(Array.Empty<FieldValidationError>());
    }

    private static Uri ThreadUri(SourceThreadId thread) => new Uri("https://package-fixture.invalid/"
        + Uri.EscapeDataString(thread.BoardId) + "/thread/" + Uri.EscapeDataString(thread.ThreadId));
}

using MeowChan.PluginSdk;
namespace MeowChan.SourceAdapter.Fixture;
public sealed class VariantSource : ISourcePlugin
{
    public PluginDescription Describe() => new("variant-fixture", "Variant fixture", "1.0.0", SdkContract.Version, "Offline process fixture",
        new[] { new SourceDescription("variants", "Variants", new Uri("https://variants.test/"),
            SourceCapabilities.ThreadRead | SourceCapabilities.PostCapture | SourceCapabilities.OriginalMedia | SourceCapabilities.Previews,
            new[] { new UrlRoute(SourceResourceKind.Thread, "variants.test", "/{board}/thread/{thread}", true) },
            new[] { new HttpHostDeclaration("variants.test", HttpHostPurpose.Metadata | HttpHostPurpose.OriginalMedia | HttpHostPurpose.PreviewMedia) },
            new SettingsSchema("1", Array.Empty<SettingDefinition>())) }, Array.Empty<FilterDescription>(),
        new SettingsSchema("1", Array.Empty<SettingDefinition>()), Array.Empty<InformationEntry>());
    public Task<ThreadReadResult> ReadThreadAsync(ThreadReadRequest request, PluginCallContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<ThreadReadResult>(new ThreadSnapshot(new SourceThreadSnapshot(request.Thread,
            new Uri("https://variants.test/b/thread/1"), "variant identity fixture", SourceThreadState.Archived,
            new[] { new SourcePost("1", 0, SourceFieldPresence.Text | SourceFieldPresence.Attachments, Text: "two independent variants",
                Attachments: new[] { new SourceAttachment(0, "x--preview", "original", new Uri("https://variants.test/original.png"), "original.png", ".png", 4),
                    new SourceAttachment(1, "x", "preview", new Uri("https://variants.test/preview.png"), "preview.png", ".png", 4) }) })));
    public Task<CatalogReadResult> ReadCatalogAsync(CatalogReadRequest request, PluginCallContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

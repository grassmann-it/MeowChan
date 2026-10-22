using MeowChan.PluginSdk;

namespace MeowChan.PluginSdk.Tests;

internal static class TestContracts
{
    internal static readonly SettingsSchema EmptySchema = new SettingsSchema("1", Array.Empty<SettingDefinition>());
    internal static readonly SourceThreadId Thread = new SourceThreadId("synthetic", "b", "123");
    internal static readonly SourceBoardId Board = new SourceBoardId("synthetic", "b");
    internal static readonly Uri ThreadUrl = new Uri("https://source.test/b/thread/123");
    internal static readonly SourceValidators Validators = new SourceValidators(EntityTag: "\"revision-1\"");

    internal static SourceDescription Source(string sourceId = "synthetic", string host = "source.test")
        => new SourceDescription(sourceId, "Synthetic source", new Uri("https://" + host + "/"),
            SourceCapabilities.ThreadRead | SourceCapabilities.BoardCatalog | SourceCapabilities.PostCapture | SourceCapabilities.OriginalMedia,
            [new UrlRoute(SourceResourceKind.Board, host, "/{board}/", IsCanonical: true),
                new UrlRoute(SourceResourceKind.Thread, host, "/{board}/thread/{thread}", IsCanonical: true)],
            [new HttpHostDeclaration(host, HttpHostPurpose.Metadata | HttpHostPurpose.OriginalMedia)], EmptySchema);

    internal static PluginDescription Plugin(SourceDescription? source = null)
        => new PluginDescription("test.plugin", "Synthetic plugin", "1.0", SdkContract.Version,
            "Synthetic content for contract tests.", [source ?? Source()], Array.Empty<FilterDescription>(),
            EmptySchema, Array.Empty<InformationEntry>());

    internal static SourceThreadSnapshot Snapshot(params SourcePost[] posts)
        => new SourceThreadSnapshot(Thread, ThreadUrl, "Synthetic thread", SourceThreadState.Active, posts, Validators);

    internal static FilterDescription Filter()
        => new FilterDescription("dimensions", "Image dimensions", "Checks dimensions from the exact original bytes.",
            FilterDataAccess.CandidateMetadata | FilterDataAccess.OriginalImage, EmptySchema, ["image/png", "image/jpeg"]);

    internal static ImageFilterRequest FilterRequest()
        => new ImageFilterRequest("dimensions", new FilterCandidate(new SourceMediaId(Thread, "media-1"), "123", 0,
            new Uri("https://source.test/media/1.png"), "one.png", "image/png"),
            "input-1", "media-1", "lease-token", 100, new string('a', 64), "rules-1",
            new SettingsSnapshot("1", Array.Empty<SettingValue>()), "build-1", "configuration-1", "grant-1");
}

using MeowChan.PluginSdk;

namespace PluginReauthorizationProbe;

// The probe executable doubles as a loadable SDK-only entrypoint. A/B/C are synthetic,
// byte-distinct PE images (a trailing marker), not a compiler/package acceptance test.
// Only this public type is exported: the real worker needs no host implementation types.
public sealed class ProbePlugin : ISourcePlugin, IImageFilterPlugin
{
    private static char Marker => (char)File.ReadAllBytes(typeof(ProbePlugin).Assembly.Location)[^1];
    public PluginDescription Describe() => Description(Marker);
    internal static PluginDescription Description(char marker)
    {
        bool other = marker == 'x';
        string source = other ? "other" : "shared";
        return new(other ? "process.other" : "process.plugin",
            char.IsUpper(marker) ? "Synthetic " + marker : "Synthetic", "1.0.0", SdkContract.Version,
            "Offline process fixture", [new(source, "Synthetic source", new("https://fixture.invalid/"), SourceCapabilities.ThreadRead,
                [new(SourceResourceKind.Thread, "fixture.invalid", "/{board}/thread/{thread}", IsCanonical: true)],
                [new("fixture.invalid", HttpHostPurpose.Metadata)], new("1", []))],
            [new("bytes", "Bytes", "Reads only the synthetic host buffer", FilterDataAccess.OriginalImage,
                new("1", []), ["image/png"])],
            new("1", [new("count", SettingScope.Plugin, SettingKind.Integer, "Count",
                Default: new("count", SettingKind.Integer, Integer: 1))]), []);
    }
    public Task<ThreadReadResult> ReadThreadAsync(ThreadReadRequest request, PluginCallContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<ThreadReadResult>(new ThreadSnapshot(new(request.Thread,
            new("https://fixture.invalid/b/thread/1"), "Synthetic", SourceThreadState.Active,
            [new("1", 0, SourceFieldPresence.Text, Text: $"{Marker}:{context.Configuration.PluginSettings.Values.Single().Integer}",
                TextFormat: SourceTextFormat.PlainText)])));
    public Task<CatalogReadResult> ReadCatalogAsync(CatalogReadRequest request, PluginCallContext context, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public async Task<ImageFilterResult> EvaluateAsync(ImageFilterRequest request, PluginCallContext context, CancellationToken cancellationToken = default)
    {
        var chunk = await context.Images.ReadImageAsync(request.AssetToken, 0, 4, cancellationToken);
        return new(chunk.Bytes.SequenceEqual(new byte[] { 1, 2, 3, 4 }) ? ImageFilterOutcome.Match : ImageFilterOutcome.NoMatch, "synthetic");
    }
}

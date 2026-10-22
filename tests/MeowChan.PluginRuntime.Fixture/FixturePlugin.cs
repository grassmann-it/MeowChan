using MeowChan.PluginSdk;

namespace MeowChan.PluginRuntime.Fixture;

public sealed class FixturePlugin : ISourcePlugin, IImageFilterPlugin
{
    public PluginDescription Describe() => new("fixture", "Fixture", "1.0.0", SdkContract.Version, "Controlled runtime fixture",
        [new("fixture", "Fixture", new("https://fixture.test/"), SourceCapabilities.ThreadRead | SourceCapabilities.BoardCatalog,
            [new(SourceResourceKind.Thread, "fixture.test", "/{board}/thread/{thread}", IsCanonical: true), new(SourceResourceKind.Board, "fixture.test", "/{board}", IsCanonical: true)],
            [new("fixture.test", HttpHostPurpose.Metadata)], new("1", []))],
        [new("bytes", "Bytes", "Reads actual staged bytes", FilterDataAccess.OriginalImage | FilterDataAccess.CandidateMetadata, new("1", []), ["image/png"])],
        new("1", []), []);
    public Task<CatalogReadResult> ReadCatalogAsync(CatalogReadRequest request, PluginCallContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<CatalogReadResult>(new CatalogSnapshot(new(request.Board, [new(new("fixture", request.Board.BoardId, "1"), new("https://fixture.test/b/thread/1"))], true)));
    public async Task<ThreadReadResult> ReadThreadAsync(ThreadReadRequest request, PluginCallContext context, CancellationToken cancellationToken = default)
    {
        switch (request.Thread.ThreadId)
        {
            case "spawn":
                System.Diagnostics.ProcessStartInfo child = new(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                child.ArgumentList.Add(typeof(FixturePlugin).Assembly.Location); child.ArgumentList.Add("child");
                return Snapshot(request, System.Diagnostics.Process.Start(child)!.Id.ToString());
            case "memory": GC.KeepAlive(new byte[1024 * 1024 * 1024]); break;
            case "crash": Environment.Exit(71); break;
            case "hang": await Task.Delay(Timeout.InfiniteTimeSpan); break;
            case "cancel": await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); break;
            case "stdout": Console.WriteLine("plugin wrote stdout"); break;
            case "logs": for (int i = 0; i < 2000; i++) context.Log.Write(PluginLogLevel.Information, new string('x', 2048)); break;
            case "wrong": return new ThreadNotFound(new("fixture", "b", "other"), 404);
            case "large": return Snapshot(request, new string('x', 17 * 1024 * 1024));
            case "http":
                HostHttpResult result = await context.Http.SendAsync(new("fixture", HostHttpMethod.Get, new("https://fixture.test/api")), cancellationToken);
                return result switch
                {
                    HostHttpDeferred deferred => new ThreadDeferred(request.Thread, deferred.RetryAt, deferred.ReasonCode),
                    HostHttpResponse response => Snapshot(request, System.Text.Encoding.UTF8.GetString(response.Body)),
                    _ => new ThreadFailure(request.Thread, new("http", "Host HTTP failed."))
                };
            case "resource": return Snapshot(request, File.ReadAllText(context.Resources.ResolvePath("fixture-data")!));
            case "temp": File.WriteAllBytes(Path.Combine(Environment.CurrentDirectory, "overflow.bin"), new byte[2 * 1024 * 1024]); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); break;
        }
        return Snapshot(request, "post bytes");
    }
    private static ThreadSnapshot Snapshot(ThreadReadRequest request, string text) => new(new(request.Thread,
        new("https://fixture.test/b/thread/" + request.Thread.ThreadId), "Title", SourceThreadState.Active,
        [new("1", 0, SourceFieldPresence.Text, Text: text, TextFormat: SourceTextFormat.PlainText)]));
    public async Task<ImageFilterResult> EvaluateAsync(ImageFilterRequest request, PluginCallContext context, CancellationToken cancellationToken = default)
    {
        ImageAssetChunk chunk = await context.Images.ReadImageAsync(request.AssetToken, 0, (int)request.OriginalSize, cancellationToken);
        return new(chunk.Bytes.SequenceEqual(new byte[] { 1, 2, 3, 4 }) ? ImageFilterOutcome.Match : ImageFilterOutcome.NoMatch, "bytes");
    }
}

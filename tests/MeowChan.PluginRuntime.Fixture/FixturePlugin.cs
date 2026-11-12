using MeowChan.PluginSdk;

namespace MeowChan.PluginRuntime.Fixture;

public sealed class FixturePlugin : ISourcePlugin, IBooruSourcePlugin, IImageFilterPlugin, IPluginSettingsValidator
{
    public Task<IReadOnlyList<FieldValidationError>> ValidateSettingsAsync(SettingsValidationRequest request, PluginCallContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<FieldValidationError>>(request.SourceId == "malformed" ? [new(null!, "bad", "Bad result")] : []);
    public PluginDescription Describe() => new("fixture", "Fixture", "1.0.0", SdkContract.Version, "Controlled runtime fixture",
        [new("fixture", "Fixture", new("https://fixture.test/"), SourceCapabilities.ThreadRead | SourceCapabilities.BoardCatalog,
            [new(SourceResourceKind.Thread, "fixture.test", "/{board}/thread/{thread}", IsCanonical: true), new(SourceResourceKind.Board, "fixture.test", "/{board}", IsCanonical: true)],
            [new("fixture.test", HttpHostPurpose.Metadata)], new("1", [])),
         new("fixture-booru", "Fixture booru", new("https://booru.fixture.test/"), SourceCapabilities.PostSearch | SourceCapabilities.OriginalMedia | SourceCapabilities.Previews, [],
            [new("booru.fixture.test", HttpHostPurpose.Metadata), new("media.fixture.test", HttpHostPurpose.OriginalMedia | HttpHostPurpose.PreviewMedia)], new("1", []))],
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
            case "background":
            case "background-crash":
                _ = context.Http.SendAsync(new("fixture", HostHttpMethod.Get, new("https://fixture.test/background")), cancellationToken);
                await Task.Delay(100, cancellationToken);
                if (request.Thread.ThreadId == "background-crash") Environment.Exit(71);
                return Snapshot(request, "finished");
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
    public Task<BooruSearchResult> SearchPostsAsync(BooruSearchRequest request, PluginCallContext context, CancellationToken cancellationToken = default)
    {
        const string burst = "http-burst:";
        if (request.Query.StartsWith(burst, StringComparison.Ordinal)
            && int.TryParse(request.Query[burst.Length..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int count))
            return HttpBurstAsync(request, count, context, cancellationToken);
        switch (request.Query)
        {
            case "foreign": return Task.FromResult<BooruSearchResult>(new BooruSearchSnapshot(new(request.SourceId, request.Query, request.Page, [BooruPost(new("other", "1"))], true)));
            case "empty": return Task.FromResult<BooruSearchResult>(new BooruSearchSnapshot(new(request.SourceId, request.Query, request.Page, [], true)));
            case "deferred": return Task.FromResult<BooruSearchResult>(new BooruSearchDeferred(request.SourceId, DateTimeOffset.UtcNow.AddMinutes(1), "provider_cooldown", 429));
        }
        BooruPost[] posts = Enumerable.Range(1, 2).Select(index => BooruPost(new(request.SourceId, (request.Page * 100 + index).ToString()))).Take(request.Limit).ToArray();
        return Task.FromResult<BooruSearchResult>(new BooruSearchSnapshot(new(request.SourceId, request.Query, request.Page, posts, request.Page >= 2)));
    }
    // Sends `count` sequential host HTTP requests inside one typed booru operation. The
    // first non-200 result ends the burst and reports how many requests completed.
    private static async Task<BooruSearchResult> HttpBurstAsync(BooruSearchRequest request, int count, PluginCallContext context, CancellationToken cancellationToken)
    {
        for (int index = 0; index < count; index++)
        {
            HostHttpResult result = await context.Http.SendAsync(new(request.SourceId, HostHttpMethod.Get,
                new("https://booru.fixture.test/posts.json?i=" + index.ToString(System.Globalization.CultureInfo.InvariantCulture))), cancellationToken);
            string? code = result switch
            {
                HostHttpResponse { StatusCode: 200 } => null,
                HostHttpFailure failure => failure.Failure.Code,
                HostHttpDeferred deferred => deferred.ReasonCode,
                _ => "http-status"
            };
            if (code != null) return new BooruSearchFailure(request.SourceId, new(code, "completed " + index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        BooruPost[] posts = Enumerable.Range(1, Math.Min(count, request.Limit)).Select(index => BooruPost(new(request.SourceId, index.ToString(System.Globalization.CultureInfo.InvariantCulture)))).ToArray();
        return new BooruSearchSnapshot(new(request.SourceId, request.Query, request.Page, posts, true));
    }
    public Task<BooruPostResult> ReadPostAsync(BooruPostRequest request, PluginCallContext context, CancellationToken cancellationToken = default) => Task.FromResult<BooruPostResult>(request.Post.PostId switch
    {
        "missing" => new BooruPostNotFound(request.Post, 404),
        "foreign" => new BooruPostSnapshot(BooruPost(new("other", request.Post.PostId))),
        _ => new BooruPostSnapshot(BooruPost(request.Post))
    });
    private static BooruPost BooruPost(SourcePostId id) => new(id, BooruFieldPresence.CreatedAt | BooruFieldPresence.Rating | BooruFieldPresence.Tags | BooruFieldPresence.Media,
        new("https://booru.fixture.test/posts/" + id.PostId), new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero), BooruRating.General, "g",
        Tags: [new("fixture_tag", BooruTagCategory.General)],
        Media: [new(BooruMediaRole.Original, new("https://media.fixture.test/o/" + id.PostId + ".png"), ".png", 4, 2, 2), new(BooruMediaRole.Preview, new("https://media.fixture.test/p/" + id.PostId + ".jpg"))]);
    private static ThreadSnapshot Snapshot(ThreadReadRequest request, string text) => new(new(request.Thread,
        new("https://fixture.test/b/thread/" + request.Thread.ThreadId), "Title", SourceThreadState.Active,
        [new("1", 0, SourceFieldPresence.Text, Text: text, TextFormat: SourceTextFormat.PlainText)]));
    public async Task<ImageFilterResult> EvaluateAsync(ImageFilterRequest request, PluginCallContext context, CancellationToken cancellationToken = default)
    {
        ImageAssetChunk chunk = await context.Images.ReadImageAsync(request.AssetToken, 0, (int)request.OriginalSize, cancellationToken);
        return new(chunk.Bytes.SequenceEqual(new byte[] { 1, 2, 3, 4 }) ? ImageFilterOutcome.Match : ImageFilterOutcome.NoMatch, "bytes");
    }
}

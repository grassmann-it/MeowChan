using System.Text;
using MeowChan.PluginSdk;

namespace MeowChan.ExamplePlugins.Tests;

/// <summary>Controlled metadata-only host; original downloads and runtime acceptance are separate.</summary>
internal sealed class MakabaUboachanHarness
{
    internal ISourcePlugin Plugin { get; }
    internal SourceDescription Source => Plugin.Describe().Sources.Single();
    internal SourceValidators Validators { get; } = new(EntityTag: "W/\"fixture\"");
    internal ThreadReadRequest ThreadRequest { get; }
    internal CatalogReadRequest CatalogRequest { get; }
    internal StubHttp Http { get; }
    internal PluginCallContext Context { get; }
    internal MakabaUboachanHarness(ISourcePlugin plugin, string body, int status = 200)
    {
        Plugin = plugin;
        ThreadRequest = new(new SourceThreadId(Source.SourceId, "demo", "123"));
        CatalogRequest = new(new SourceBoardId(Source.SourceId, "demo"));
        Http = new(Encoding.UTF8.GetBytes(body), status, Validators);
        SettingsSnapshot empty = new("1", []);
        NoOtherServices none = new();
        Context = new(new PluginConfiguration("fixture", empty, new Dictionary<string, SettingsSnapshot> { [Source.SourceId] = empty }), Http, none, none, none);
    }
    internal Task<ThreadReadResult> ReadThread(CancellationToken cancellationToken = default) => Plugin.ReadThreadAsync(ThreadRequest, Context, cancellationToken);
    internal Task<CatalogReadResult> ReadCatalog(CancellationToken cancellationToken = default) => Plugin.ReadCatalogAsync(CatalogRequest, Context, cancellationToken);
    internal sealed class StubHttp(byte[] body, int status, SourceValidators validators) : IHostHttp
    {
        internal List<HostHttpRequest> Requests { get; } = [];
        internal byte[] Body { get; set; } = body;
        internal Uri? FinalUri { get; set; }
        internal HostHttpResult? Result { get; set; }
        internal Action? OnSend { get; set; }
        public Task<HostHttpResult> SendAsync(HostHttpRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request); OnSend?.Invoke();
            return Task.FromResult(Result ?? new HostHttpResponse(status, FinalUri ?? request.Url, new Dictionary<string, string>(), Body, validators));
        }
    }
    private sealed class NoOtherServices : IImageAssetReader, IPluginResources, IPluginLog
    {
        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Source plugins must not read images.");
        public string? ResolvePath(string logicalName) => throw new InvalidOperationException("Single-file source has no resources.");
        public void Write(PluginLogLevel level, string message) => throw new InvalidOperationException("Source content must not be logged.");
    }
}

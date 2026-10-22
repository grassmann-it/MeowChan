using System.Net.Http;
using MeowChan.Boards.Base;
using MeowChan.Interfaces;
using MeowChan.Downloader;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginSdk;
using Serilog;

namespace MeowChan.PluginHost.Sources;

/// <summary>An optional local provider adapter. Only asynchronous checks acquire a plugin worker.</summary>
public sealed class PluginSourceImageBoard : BaseImageBoard, IProviderDescriptorSource, IDisposable
{
    private readonly object _lifecycle = new();
    private readonly Func<CancellationToken, Task<IPluginWorkerClient>> _worker;
    private CancellationTokenSource _enabledLifetime = new();
    private bool _enabled = true;
    private bool _disposed;
    public SourceDescription Description { get; }
    public PluginSourceRouter Router { get; }
    public override string Name => Description.SourceId;
    public override string NiceName => Description.Name;
    public override Uri BaseUri => Description.BaseUri;
    internal override bool UsesOpaqueStorageIdentifiers => true;
    public ProviderCapabilities Capabilities => (ProviderCapabilities)(int)Description.Capabilities;
    public IReadOnlyList<string> SupportedHosts { get; }
    public string? AdapterVersion { get; }
    public bool Enabled { get { lock (_lifecycle) return _enabled && !_disposed; } }
    internal DownloadQueue? MetadataQueue { get; }

    public PluginSourceImageBoard(PluginDescription plugin, string sourceId,
        Func<CancellationToken, Task<IPluginWorkerClient>> worker, HttpClient? mediaClient = null, ILogger? logger = null,
        DownloadQueue? metadataQueue = null)
        : base(mediaClient, logger: logger)
    {
        Validate(SdkValidation.ValidateDescription(plugin));
        Description = plugin.Sources.Single(source => source.SourceId == sourceId);
        Router = new PluginSourceRouter(Description);
        _worker = worker;
        MetadataQueue = metadataQueue;
        SupportedHosts = Array.AsReadOnly(Description.Routes.Select(route => route.Host).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        AdapterVersion = plugin.PluginVersion;
    }

    public override bool CanHandle(Uri uri) => Router.TryResolve(uri, out _, out _, out _, out _);
    public override bool IsBoardUri(Uri uri) => Router.TryResolve(uri, out SourceResourceKind kind, out _, out _, out _) && kind == SourceResourceKind.Board;
    public override bool IsThreadUri(Uri uri) => Router.TryResolve(uri, out SourceResourceKind kind, out _, out _, out _) && kind == SourceResourceKind.Thread;
    public override IBoard GetBoard(Uri boardUri)
    {
        if (!Router.TryResolve(boardUri, out SourceResourceKind kind, out string board, out _, out Uri? canonical) || kind != SourceResourceKind.Board)
            throw new ArgumentException("Unsupported board URI.", nameof(boardUri));
        return new PluginSourceBoard(this, board, canonical!);
    }
    public override IThread GetThread(Uri threadUri)
    {
        if (!Router.TryResolve(threadUri, out SourceResourceKind kind, out string board, out string? thread, out Uri? canonical) || kind != SourceResourceKind.Thread)
            throw new ArgumentException("Unsupported thread URI.", nameof(threadUri));
        return new PluginSourceThread(new PluginSourceBoard(this, board, Description.Routes.Any(route => route.IsCanonical && route.Kind == SourceResourceKind.Board)
            ? Router.Canonical(SourceResourceKind.Board, board) : BaseUri), thread!, canonical!);
    }

    public void SetEnabled(bool enabled)
    {
        CancellationTokenSource? previous = null;
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_enabled == enabled) return;
            _enabled = enabled;
            if (enabled) _enabledLifetime = new CancellationTokenSource();
            else previous = _enabledLifetime;
        }
        previous?.Cancel();
        previous?.Dispose();
    }

    internal async Task<T> InvokeAsync<T>(Func<IPluginWorkerClient, CancellationToken, Task<T>> call, CancellationToken token)
    {
        using CancellationTokenSource linked = LinkLifetime(token);
        IPluginWorkerClient worker = await _worker(linked.Token).ConfigureAwait(false);
        T result = await call(worker, linked.Token).ConfigureAwait(false);
        // Even a broken provider/test transport that ignores cancellation cannot publish old observations.
        linked.Token.ThrowIfCancellationRequested();
        return result;
    }

    internal CancellationTokenSource LinkLifetime(CancellationToken token)
    {
        lock (_lifecycle)
        {
            if (!_enabled || _disposed) throw new PluginWorkerException("source_unavailable", "The source plugin is disabled or unavailable.");
            return CancellationTokenSource.CreateLinkedTokenSource(token, _enabledLifetime.Token);
        }
    }

    internal static void Validate(IReadOnlyList<ValidationIssue> issues)
    {
        if (issues.Count != 0) throw new PluginWorkerException("invalid_source_result", string.Join("; ", issues.Select(issue => issue.Path + ": " + issue.Message)));
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
            _enabledLifetime.Cancel();
            _enabledLifetime.Dispose();
        }
    }
}

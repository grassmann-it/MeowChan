using System.Net.Http;
using MeowChan.Boards.Base;
using MeowChan.Interfaces;
using MeowChan.Downloader;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginSdk;
using MeowChan.Storage;
using Serilog;

namespace MeowChan.PluginHost.Sources;

/// <summary>Why a source adapter refuses work. The worker error code stays <c>source_unavailable</c> for every reason.</summary>
public enum PluginSourceUnavailableReason
{
    /// <summary>The adapter is available.</summary>
    None,
    /// <summary>No reason was given (the generic message).</summary>
    Unavailable,
    /// <summary>The user turned the owning plugin off.</summary>
    PluginDisabled,
    /// <summary>The user turned this site of an engine-family plugin off.</summary>
    SiteDisabled,
    /// <summary>The owning bundled plugin's build changed and waits for an explicit repair review; a restart does not help.</summary>
    NeedsRepairReview,
    /// <summary>The owning plugin is not running for another startup problem.</summary>
    NotActive
}

/// <summary>An optional local provider adapter. Only asynchronous checks acquire a plugin worker.</summary>
public sealed class PluginSourceImageBoard : BaseImageBoard, IProviderDescriptorSource, ISourceMediaIntegrityPolicyProvider, IDisposable
{
    private readonly object _lifecycle = new();
    private readonly Func<CancellationToken, Task<IPluginWorkerClient>> _worker;
    private CancellationTokenSource? _enabledLifetime = new();
    private bool _enabled = true;
    private bool _disposed;
    private PluginSourceUnavailableReason _reason = PluginSourceUnavailableReason.None;
    private string? _reasonDetail;
    public SourceDescription Description { get; }
    /// <summary>Fresh host-owned source/profile policy; source metadata never grants permission.</summary>
    public ISourceMediaIntegrityPolicy? MediaIntegrityPolicy { get; init; }
    public PluginSourceRouter Router { get; }
    public override string Name => Description.SourceId;
    public override string NiceName => Description.Name;
    public override Uri BaseUri => Description.BaseUri;
    internal override bool UsesOpaqueStorageIdentifiers => true;
    public ProviderCapabilities Capabilities => MapCapabilities(Description.Capabilities);

    /// <summary>
    /// Explicit SDK-to-descriptor mapping. Only the flags listed here are carried over; flags without a descriptor
    /// counterpart (PostSearch, and any future SDK flag) are dropped instead of leaking as undefined enum values.
    /// </summary>
    private static readonly (SourceCapabilities Sdk, ProviderCapabilities Provider)[] CapabilityMap =
    {
        (SourceCapabilities.ThreadRead, ProviderCapabilities.ThreadWatch),
        (SourceCapabilities.BoardCatalog, ProviderCapabilities.BoardCatalog),
        (SourceCapabilities.PostCapture, ProviderCapabilities.PostCapture),
        (SourceCapabilities.OriginalMedia, ProviderCapabilities.OriginalMedia),
        (SourceCapabilities.Previews, ProviderCapabilities.Previews)
    };

    public static ProviderCapabilities MapCapabilities(SourceCapabilities capabilities)
    {
        ProviderCapabilities mapped = ProviderCapabilities.None;
        foreach ((SourceCapabilities sdk, ProviderCapabilities provider) in CapabilityMap)
            if ((capabilities & sdk) != 0) mapped |= provider;
        return mapped;
    }
    public IReadOnlyList<string> SupportedHosts { get; }
    public string? AdapterVersion { get; }
    public bool Enabled { get { lock (_lifecycle) return _enabled && !_disposed; } }
    /// <summary>Why the adapter refuses work right now; <see cref="PluginSourceUnavailableReason.None"/> while it is enabled.</summary>
    public PluginSourceUnavailableReason UnavailableReason
    {
        get { lock (_lifecycle) return _disposed ? PluginSourceUnavailableReason.Unavailable : _enabled ? PluginSourceUnavailableReason.None : _reason; }
    }
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

    public void SetEnabled(bool enabled) => SetEnabled(enabled, PluginSourceUnavailableReason.Unavailable, null);

    /// <summary>Turns the adapter on or off; a disabled adapter names <paramref name="reason"/> (and the optional
    /// <paramref name="detail"/>, such as the plugin's startup problem) in every refused call.</summary>
    public void SetEnabled(bool enabled, PluginSourceUnavailableReason reason, string? detail = null)
    {
        CancellationTokenSource? previous = null;
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!enabled)
            {
                _reason = reason == PluginSourceUnavailableReason.None ? PluginSourceUnavailableReason.Unavailable : reason;
                _reasonDetail = detail;
            }
            if (_enabled == enabled) return;
            _enabled = enabled;
            if (enabled) _enabledLifetime = new CancellationTokenSource();
            else { previous = _enabledLifetime; _enabledLifetime = null; }
        }
        CancelAndDispose(previous);
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
            if (!_enabled || _disposed) throw new PluginWorkerException("source_unavailable", UnavailableMessage(_disposed ? PluginSourceUnavailableReason.Unavailable : _reason, _reasonDetail));
            return CancellationTokenSource.CreateLinkedTokenSource(token, _enabledLifetime!.Token);
        }
    }

    internal static string UnavailableMessage(PluginSourceUnavailableReason reason, string? detail) => reason switch
    {
        PluginSourceUnavailableReason.PluginDisabled => "The source plugin is turned off.",
        PluginSourceUnavailableReason.SiteDisabled => "This site of the source plugin is turned off.",
        PluginSourceUnavailableReason.NeedsRepairReview => "The source plugin needs a repair review before it runs again (Sources & Plugins, Review bundled repairs)"
            + (string.IsNullOrEmpty(detail) ? "." : ": " + detail),
        PluginSourceUnavailableReason.NotActive => "The source plugin is not running" + (string.IsNullOrEmpty(detail) ? "." : ": " + detail),
        _ => "The source plugin is disabled or unavailable."
    };

    internal static void Validate(IReadOnlyList<ValidationIssue> issues)
    {
        if (issues.Count != 0) throw new PluginWorkerException("invalid_source_result", string.Join("; ", issues.Select(issue => issue.Path + ": " + issue.Message)));
    }

    public void Dispose()
    {
        CancellationTokenSource? previous;
        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
            previous = _enabledLifetime;
            _enabledLifetime = null;
        }
        CancelAndDispose(previous);
    }

    private static void CancelAndDispose(CancellationTokenSource? lifetime)
    {
        // Detaching under _lifecycle gives one caller ownership of this generation.
        // Invoke callbacks outside the lock; a concurrent disable/dispose cannot
        // cancel or dispose the same source while its callbacks are still running.
        try { lifetime?.Cancel(); }
        finally { lifetime?.Dispose(); }
    }
}

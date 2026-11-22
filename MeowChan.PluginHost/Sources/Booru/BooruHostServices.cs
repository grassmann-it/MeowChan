using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Http;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Sources.Booru;

/// <summary>
/// Routes only declared post-search metadata through the bounded booru transport.
/// Ordinary chan HTTP and image access remain owned by the existing host services.
/// </summary>
public sealed class BooruHostServices : IPluginHostServices
{
    private readonly IPluginHostServices _inner;
    private readonly BooruHostHttp _booruHttp;
    private readonly IReadOnlyDictionary<string, SourceDescription> _sources;
    private readonly BooruJobRegistry _jobs;

    public BooruHostServices(IPluginHostServices inner, BooruHostHttp booruHttp)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _booruHttp = booruHttp ?? throw new ArgumentNullException(nameof(booruHttp));
        _sources = booruHttp.Sources;
        _jobs = new BooruJobRegistry(GetClock(booruHttp));
    }

    internal IPluginHostServices Inner => _inner;
    internal IReadOnlyDictionary<string, SourceDescription> Sources => _sources;

    public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count,
        PluginOperationContext operation, CancellationToken cancellationToken)
        => _inner.ReadImageAsync(assetToken, offset, count, operation, cancellationToken);

    public Task<HostHttpResult> SendHttpAsync(HostHttpRequest request, PluginOperationContext operation,
        CancellationToken cancellationToken)
    {
        if (!_sources.TryGetValue(request.SourceId, out SourceDescription? source))
            return Task.FromResult<HostHttpResult>(Failure("booru-source", "The booru source is not declared by this host."));

        if (!source.Capabilities.HasFlag(SourceCapabilities.PostSearch))
            return operation.ResourceId.StartsWith(BooruJob.ResourcePrefix, StringComparison.Ordinal)
                ? Task.FromResult<HostHttpResult>(BooruEndpointPolicy.OperationDenied())
                : _inner.SendHttpAsync(request, operation, cancellationToken);

        if (!_jobs.TryGet(operation.ResourceId, out BooruJob? job) || job is null)
            return Task.FromResult<HostHttpResult>(Failure("booru-job", "The booru job is not live."));
        if (operation.BooruCall is not BooruCallBinding active)
            return _booruHttp.SendAsync(request, source, job, cancellationToken);
        if (active.Lifetime.IsCancellationRequested || BooruEndpointPolicy.Bind(request, source, active.Request) is not HostHttpRequest bound)
            return Task.FromResult<HostHttpResult>(BooruEndpointPolicy.OperationDenied());
        return SendBoundAsync(bound, source, job, active, cancellationToken);
    }

    private async Task<HostHttpResult> SendBoundAsync(HostHttpRequest request, SourceDescription source, BooruJob job,
        BooruCallBinding active, CancellationToken cancellationToken)
    {
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(active.Lifetime, cancellationToken);
        return await _booruHttp.SendAsync(request, source, job, lifetime.Token).ConfigureAwait(false);
    }

    /// <summary>The enforced post-search profile of a declared source; null when it has none.</summary>
    internal BooruSourceProfile? ProfileOf(string sourceId) => _booruHttp.ProfileOf(sourceId);

    internal BooruJob OpenJob() => _jobs.Open();

    internal void CloseJob(BooruJob job) => _jobs.Close(job);

    internal bool TryGetCooldown(string sourceId, out DateTimeOffset retryAt) =>
        _booruHttp.TryGetCooldown(sourceId, out retryAt);

    /// <summary>Begins a host-owned cooldown reset transaction; it never starts a request.</summary>
    internal BooruHostHttp.CooldownReset? BeginCooldownReset(string sourceId) => _booruHttp.BeginCooldownReset(sourceId);

    /// <summary>Clears only the host-owned cooldown fence for one declared source; it never starts a request.</summary>
    internal bool ResetCooldown(string sourceId) => _booruHttp.ResetCooldown(sourceId);

    internal TimeSpan GetMaximumCooldown() => _booruHttp.GetMaximumCooldown();

    private static TimeProvider GetClock(BooruHostHttp http)
    {
        // The transport's policy is intentionally private; jobs use the same clock
        // through the host's public construction contract by this stable helper.
        return http.TimeProvider;
    }

    private static HostHttpFailure Failure(string code, string message) =>
        new(new FailureDetail(code, message));
}

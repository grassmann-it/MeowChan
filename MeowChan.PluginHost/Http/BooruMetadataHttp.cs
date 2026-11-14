using MeowChan.Http;
using MeowChan.PluginHost.Sources.Booru;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Http;

/// <summary>
/// Compatibility adapter for the existing <see cref="PluginHostHttp"/> entry point.
/// The bounded Booru transport itself lives in <see cref="BooruHostHttp"/>; this class
/// only supplies the host-owned job of the metadata operation and keeps the borrowed
/// transport lifetime with <see cref="PluginHostHttp"/>.
/// </summary>
internal sealed class BooruMetadataHttp : IDisposable
{
    private readonly BooruHostHttp _host;

    internal BooruMetadataHttp(HttpClient transport, RequestPolicyState policy,
        IReadOnlyList<SourceDescription> sources, Func<TimeSpan>? maximumCooldownProvider = null)
    {
        _host = new BooruHostHttp(transport, policy, sources, maximumCooldownProvider, disposeTransport: false);
    }

    internal TimeProvider TimeProvider => _host.TimeProvider;

    /// <param name="request">The validated booru metadata request.</param>
    /// <param name="source">The declared post-search source.</param>
    /// <param name="cancellationToken">The caller's cancellation.</param>
    /// <param name="operationJob">
    /// The job bound to the typed plugin operation. Worker service calls always pass it
    /// (see <see cref="PluginHostHttp.SendAsync(HostHttpRequest, Contracts.PluginOperationContext, CancellationToken)"/>).
    /// Only the context-free <see cref="IHostHttp"/> entry point passes null: it resumes the
    /// ambient operation history, and a direct caller without one still gets one bounded,
    /// non-shareable job rather than an unbounded request lifetime.
    /// </param>
    internal async Task<HostHttpResult> SendAsync(HostHttpRequest request, SourceDescription source,
        BooruJob? operationJob, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        BooruJob? job = operationJob ?? PluginHostHttp.MetadataHistory.Current?.GetBooruJob(_host.TimeProvider);
        BooruJob? ownedJob = null;
        if (job is null)
        {
            ownedJob = new BooruJob(_host.TimeProvider, cancellationToken);
            job = ownedJob;
        }

        try
        {
            return await _host.SendAsync(request, source, job, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ownedJob?.Close();
        }
    }

    public void Dispose() => _host.Dispose();
}

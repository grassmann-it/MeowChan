using MeowChan.Http;
using MeowChan.PluginHost.Sources.Booru;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Http;

/// <summary>
/// Compatibility adapter for the existing <see cref="PluginHostHttp"/> entry point.
/// The bounded Booru transport itself lives in <see cref="BooruHostHttp"/>; this class
/// only supplies a host-owned job for the ambient metadata operation and keeps the
/// borrowed transport lifetime with <see cref="PluginHostHttp"/>.
/// </summary>
internal sealed class BooruMetadataHttp : IDisposable
{
    private readonly BooruHostHttp _host;

    internal BooruMetadataHttp(HttpClient transport, RequestPolicyState policy,
        IReadOnlyList<SourceDescription> sources)
    {
        _host = new BooruHostHttp(transport, policy, sources, disposeTransport: false);
    }

    internal async Task<HostHttpResult> SendAsync(HostHttpRequest request, SourceDescription source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Worker service continuations resume the MetadataHistory bound to the
        // operation. Direct callers (including host-level tests) still get one
        // bounded, non-shareable job rather than an unbounded request lifetime.
        BooruJob? job = PluginHostHttp.MetadataHistory.Current?.GetBooruJob(_host.TimeProvider);
        BooruJob? ownedJob = null;
        if (job is null)
        {
            ownedJob = new BooruJob(_host.TimeProvider);
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

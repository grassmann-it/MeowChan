using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Taggers;

/// <summary>One batch's HTTP service. The underlying transport is bound by the host.</summary>
internal sealed class TaggerHttp(TaggerRegistration registration, TaggerGrant grant, int maximumRequests, CancellationToken batchToken)
    : IHostHttp
{
    private readonly SourceDescription _source = new(registration.PluginId, registration.PluginId,
        new Uri("https://localhost/"), SourceCapabilities.None, [], registration.NetworkHosts, new("1", []));
    private int _remaining = maximumRequests;

    public async Task<HostHttpResult> SendAsync(HostHttpRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (registration.Binding.ConsentedHttp is not IHostHttp transport || !IsLive()
            || request.Url is not { IsAbsoluteUri: true, Scheme: "https", IsDefaultPort: true, UserInfo.Length: 0 }
            || SdkValidation.ValidateHttpRequest(request, _source).Count != 0 || !TryReserve())
            return Denied();

        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, batchToken, grant.Token);
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            Task<HostHttpResult> send = transport.SendAsync(request, lifetime.Token);
            HostHttpResult result;
            try
            {
                result = await send.WaitAsync(lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TaggerJobRunner.ObserveLateFailure(send);
                throw;
            }
            if (!IsLive() || result is HostHttpResponse response && response.FinalUri != request.Url
                || SdkValidation.ValidateHttpResult(request, result, _source).Count != 0)
                return Denied();
            lifetime.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (!IsLive())
        {
            return Denied();
        }
    }

    private bool IsLive() => !batchToken.IsCancellationRequested && grant.Permits(registration);

    private bool TryReserve()
    {
        int remaining;
        do
        {
            remaining = Volatile.Read(ref _remaining);
            if (remaining <= 0) return false;
        }
        while (Interlocked.CompareExchange(ref _remaining, remaining - 1, remaining) != remaining);
        return true;
    }

    private static HostHttpFailure Denied() => new(new FailureDetail("tagger_network_denied",
        "This tagger has no network access for the request."));
}

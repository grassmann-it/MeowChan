using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Booru;

/// <summary>Carries the post's live authorization through redirects and transport retries.</summary>
internal sealed class BooruOriginalRequestGuard(HttpClient transport, Action? authorize = null) : HttpMessageHandler
{
    private static readonly HttpRequestOptionsKey<Action> Authorization = new("MeowChan.Booru.OriginalAuthorization");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (authorize is not null) request.Options.Set(Authorization, authorize);
        if (request.Options.TryGetValue(Authorization, out Action? check)) check();
        using HttpRequestMessage forward = new(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy
        };
        foreach (var header in request.Headers) forward.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var option in request.Options) forward.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        HttpResponseMessage response = await transport.SendAsync(forward, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.RequestMessage == forward) response.RequestMessage = request;
        return response;
    }
}

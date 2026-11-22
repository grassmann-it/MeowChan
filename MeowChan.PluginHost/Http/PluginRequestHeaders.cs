using System.Globalization;
using System.Net.Http.Headers;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Http;

internal static class PluginRequestHeaders
{
    // Call only after SDK validation of the request and the current destination.
    internal static void Apply(HttpRequestMessage message, HostHttpRequest request)
    {
        Uri uri = message.RequestUri!;
        string host = uri.HostNameType == UriHostNameType.IPv6 ? "[" + uri.IdnHost + "]" : uri.IdnHost;
        message.Headers.Host = uri.IsDefaultPort ? host : host + ":" + uri.Port.ToString(CultureInfo.InvariantCulture);

        if (request.Headers is not null)
            foreach ((string name, string value) in request.Headers)
            {
                if (name.Equals("If-None-Match", StringComparison.OrdinalIgnoreCase))
                    message.Headers.IfNoneMatch.ParseAdd(value);
                else
                    message.Headers.TryAddWithoutValidation(name, value);
            }
        if (request.Validators?.EntityTag is string tag)
            message.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(tag));
        if (request.Validators?.LastModified is DateTimeOffset modified)
            message.Headers.IfModifiedSince = modified;
    }

    internal static bool IsConditional(HostHttpRequest request) =>
        request.Validators?.EntityTag is not null || request.Validators?.LastModified is not null
        || request.Headers?.Keys.Any(name => name.Equals("If-None-Match", StringComparison.OrdinalIgnoreCase)
            || name.Equals("If-Modified-Since", StringComparison.OrdinalIgnoreCase)) == true;
}
